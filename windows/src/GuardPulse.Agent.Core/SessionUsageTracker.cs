namespace GuardPulse.Agent.Core;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Per-app CONTINUOUS-session tracker backing the session-limit enforcement (N minutes of
/// unbroken open use locks the app). Unlike <see cref="UsageLedger"/> (which accrues raw
/// foreground time), this accrues only while an app is the foreground app: leaving the app
/// pauses its timer, staying away for at least <see cref="ResetGapMs"/> (2 minutes) resets
/// that app's session to zero, and a UTC-day rollover resets everything.
///
/// The host's periodic loop (~5s) calls <see cref="Tick"/> with the current foreground app
/// key (null when none could be identified — a null tick flushes pending state and pauses
/// accrual for the interval). State persists to a single JSON file (crash-safe via
/// <see cref="AtomicFile"/>) so a session in progress survives an agent restart. Day keys
/// are UTC "yyyyMMdd" derived from
/// the provided TimeProvider (the ledger's day keys are local; the session semantics here
/// intentionally mirror the TV agent's UTC-day reset). Thread-safe: all public members
/// synchronize on a single lock object; <see cref="Tick"/> is called from one loop only.
/// </summary>
public sealed class SessionUsageTracker
{
    /// <summary>Foreground gap after which an app's open-use chain is broken (2 minutes away = fresh session).</summary>
    internal const long ResetGapMs = 120_000L;

    /// <summary>Accrual persists are debounced to at most one write per minute (resets and rollovers always flush).</summary>
    private const long PersistDebounceMs = 60_000L;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string filePath;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<string, AppState> apps = new(StringComparer.Ordinal);

    /// <summary>Last non-null foreground app key seen by <see cref="Tick"/> (not persisted: after a restart the first tick re-anchors).</summary>
    private string? lastForegroundKey;
    private string currentDayKey;
    private long lastPersistMs;
    private bool dirty;

    public SessionUsageTracker(string filePath, TimeProvider time)
    {
        this.filePath = filePath;
        this.time = time;
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        this.currentDayKey = TodayKey();
        Load();
    }

    /// <summary>
    /// Advances the accounting for <paramref name="foregroundAppKey"/> at <paramref name="nowMs"/>.
    /// Consecutive ticks for the same app accrue the inter-tick delta; when the app is seen
    /// again after a gap longer than <see cref="ResetGapMs"/> (same app or after switching),
    /// its session resets to zero first. A null key means no identifiable foreground app:
    /// nothing accrues and pending changes are only flushed per the persist debounce.
    /// </summary>
    public void Tick(string? foregroundAppKey, long nowMs)
    {
        lock (this.gate)
        {
            var today = TodayKey();
            if (today != this.currentDayKey)
            {
                this.currentDayKey = today;
                foreach (var entry in this.apps.Values)
                {
                    ResetEntry(entry, today);
                }

                this.dirty = true;
                PersistNow(); // always on day rollover
            }

            if (foregroundAppKey is null)
            {
                // No identifiable foreground app: nothing accrues across this interval.
                // Clearing the last-key anchor makes the next non-null tick a re-anchor
                // (pause, no accrual) while the elapsed gap still counts toward the
                // reset threshold below.
                this.lastForegroundKey = null;
                PersistIfDue();
                return;
            }

            var state = GetOrCreate(foregroundAppKey, today);
            if (state.DayKey != today)
            {
                // Day key mismatch on tick (stale entry that dodged the sweep above):
                // reset that entry, then start accruing fresh from now.
                ResetEntry(state, today);
            }

            if (state.LastTickMs > 0 &&
                string.Equals(this.lastForegroundKey, foregroundAppKey, StringComparison.Ordinal))
            {
                // Same app as the previous tick: it was open continuously across the interval.
                var delta = nowMs - state.LastTickMs;
                if (delta > ResetGapMs)
                {
                    // The tick loop stalled past the reset gap: the session starts over.
                    state.MsToday = 0;
                    this.dirty = true;
                }
                else if (delta > 0)
                {
                    state.MsToday += delta;
                    this.dirty = true;
                }
            }
            else if (state.LastTickMs > 0 && nowMs - state.LastTickMs > ResetGapMs)
            {
                // The app is (re)becoming foreground after being away too long: fresh session.
                state.MsToday = 0;
                this.dirty = true;
            }

            state.LastTickMs = Math.Max(state.LastTickMs, nowMs); // ledger time never runs backwards
            this.lastForegroundKey = foregroundAppKey;
            PersistIfDue();
        }
    }

    /// <summary>Accrued continuous-session ms for <paramref name="appKey"/> today, applying the implicit day-rollover check first.</summary>
    public long EffectiveSessionMs(string appKey, long nowMs)
    {
        lock (this.gate)
        {
            var today = TodayKey();
            if (today != this.currentDayKey)
            {
                this.currentDayKey = today;
                foreach (var entry in this.apps.Values)
                {
                    ResetEntry(entry, today);
                }

                this.dirty = true;
                PersistNow(); // always on day rollover
                return 0;
            }

            if (!this.apps.TryGetValue(appKey, out var state))
            {
                return 0;
            }

            if (state.DayKey != today)
            {
                ResetEntry(state, today);
                this.dirty = true;
                PersistNow(); // a single stale entry's rollover is flushed immediately
                return 0;
            }

            return state.MsToday;
        }
    }

    /// <summary>
    /// Epoch ms (host clock) at which the app's CURRENT open-use session began — the
    /// accrual anchor of the live chain. Zero when no session chain is live (never
    /// accrued, paused by leaving the app, or reset). Used by warning dedup keys so
    /// each fresh session re-arms the 5m/1m toasts.
    /// </summary>
    public long SessionStartedAtMs(string appKey)
    {
        lock (this.gate)
        {
            return this.apps.TryGetValue(appKey, out var state) && state.LastTickMs != 0
                ? state.LastTickMs - state.MsToday
                : 0;
        }
    }

    /// <summary>
    /// Sticky lock state: true once the session limit has fired for the app (this
    /// session). Survives away-gaps, app switches and day rollover — only
    /// <see cref="Reset"/>/<see cref="ResetAll"/> (parent approval, correct PIN,
    /// Reset-Today) clears it, so the wall cannot be disarmed by waiting 2 minutes.
    /// </summary>
    public bool IsSessionLocked(string appKey)
    {
        lock (this.gate)
        {
            return this.apps.TryGetValue(appKey, out var state) && state.SessionLocked;
        }
    }

    /// <summary>Marks the app session-locked (called by the enforcement engine the
    /// moment the limit fires) and persists immediately — a crash must not disarm it.</summary>
    public void MarkSessionLocked(string appKey)
    {
        lock (this.gate)
        {
            var state = GetOrCreate(appKey, TodayKey());
            if (!state.SessionLocked)
            {
                state.SessionLocked = true;
                this.dirty = true;
                PersistNow();
            }
        }
    }

    /// <summary>
    /// Zeroes the app's session AND clears its sticky lock (unlock approval /
    /// Reset-Today), then persists immediately. A no-op for an app with no tracked state.
    /// </summary>
    public void Reset(string appKey)
    {
        lock (this.gate)
        {
            if (!this.apps.TryGetValue(appKey, out var state))
            {
                return;
            }

            ResetEntry(state, TodayKey());
            state.SessionLocked = false;
            this.dirty = true;
            PersistNow(); // always on Reset
        }
    }

    /// <summary>
    /// Zeroes EVERY app's session (device-wide Reset-Today) and persists immediately.
    /// </summary>
    public void ResetAll()
    {
        lock (this.gate)
        {
            var today = TodayKey();
            foreach (var state in this.apps.Values)
            {
                ResetEntry(state, today);
                state.SessionLocked = false;
            }

            this.dirty = true;
            PersistNow();
        }
    }

    private AppState GetOrCreate(string appKey, string today)
    {
        if (!this.apps.TryGetValue(appKey, out var state))
        {
            state = new AppState { DayKey = today };
            this.apps[appKey] = state;
            this.dirty = true;
        }

        return state;
    }

    private void ResetEntry(AppState state, string today)
    {
        state.MsToday = 0;
        state.DayKey = today;
        state.LastTickMs = 0;
    }

    private long NowMs() => this.time.GetUtcNow().ToUnixTimeMilliseconds();

    private string TodayKey() => this.time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private void Load()
    {
        try
        {
            if (!File.Exists(this.filePath))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<TrackerFile>(File.ReadAllText(this.filePath), JsonOptions);
            if (payload?.Apps == null)
            {
                return;
            }

            foreach (var (appKey, state) in payload.Apps)
            {
                if (string.IsNullOrWhiteSpace(appKey) || state is null || state.DayKey != this.currentDayKey)
                {
                    continue; // missing or foreign-day entry: dropped (fresh session)
                }

                this.apps[appKey] = new AppState
                {
                    MsToday = Math.Max(0L, state.MsToday),
                    DayKey = state.DayKey,
                    LastTickMs = state.LastTickMs,
                    SessionLocked = state.SessionLocked,
                };
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            this.apps.Clear(); // corrupt/unreadable state: start with an empty tracker
        }
    }

    private void PersistIfDue()
    {
        if (!this.dirty || (this.lastPersistMs != 0 && NowMs() - this.lastPersistMs < PersistDebounceMs))
        {
            return;
        }

        PersistNow();
    }

    private void PersistNow()
    {
        this.lastPersistMs = NowMs();
        this.dirty = false;
        var payload = new TrackerFile();
        foreach (var (appKey, state) in this.apps)
        {
            payload.Apps[appKey] = new AppState
            {
                MsToday = state.MsToday,
                DayKey = state.DayKey,
                LastTickMs = state.LastTickMs,
                SessionLocked = state.SessionLocked,
            };
        }

        AtomicFile.WriteAllText(this.filePath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private sealed class TrackerFile
    {
        public Dictionary<string, AppState> Apps { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class AppState
    {
        public long MsToday { get; set; }

        public string DayKey { get; set; } = string.Empty;

        public long LastTickMs { get; set; }

        // Sticky: set the moment the session limit fires; survives away-gaps,
        // app switches and day rollover. Cleared ONLY by Reset/ResetAll
        // (parent approval, correct PIN, Reset-Today).
        public bool SessionLocked { get; set; }
    }
}
