namespace GuardPulse.Agent.Core;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Persistent per-day usage ledger with millisecond precision, ported in spirit from the TV
/// agent's UsageTracker/FallbackStateStore (committed sessions + a live open session, with a
/// "Reset Today" offset and a per-day daily-limit block set).
///
/// File layout inside <paramref name="stateDirectory"/> (all UTF-8, System.Text.Json):
///   usage-{yyyy-MM-dd}.json   { "sessions": [ { "k": appKey, "s": startMs, "e": endMs } ],
///                              "open": { "k": appKey, "s": startMs } }        (open omitted when none)
///   offsets-{yyyy-MM-dd}.json { "appKey": offsetMs }
///   blocks-{yyyy-MM-dd}.json  { "appKey": true }
///
/// Day keys use LOCAL midnight (TimeZoneInfo of the provided TimeProvider). A session that
/// starts before midnight and ends after it is charged entirely to the day it started on.
/// Thread-safe: all public members synchronize on a single lock object.
/// </summary>
public sealed class UsageLedger
{
    /// <summary>Monotonic ms source (injectable for deterministic tests; real clock anchors wall-clock rollback).</summary>
    internal Func<long> MonotonicTicks { get; set; } = static () => Environment.TickCount64;

    private const long MergeGapMs = 2_000L;

    /// <summary>Days of per-app usage retained on disk and in memory (bounds growth on long-lived agents).</summary>
    private const int MaxRetainedDays = 62;

    /// <summary>Per-day session cap: beyond this, oldest sessions collapse into exact per-app totals.</summary>
    private const int SessionCap = 2000;
    private const int SessionKeep = 1500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string stateDirectory;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<string, DayState> days = new();
    private readonly Dictionary<string, Dictionary<string, long>> offsets = new();
    private readonly Dictionary<string, HashSet<string>> dailyBlocks = new();
    private readonly HashSet<string> dirtyDays = new();
    private readonly List<(string DayKey, Session Session)> markerPending = new();
    private OpenSession? open;

    public UsageLedger(string stateDirectory, TimeProvider time)
    {
        this.stateDirectory = stateDirectory;
        this.time = time;
        Directory.CreateDirectory(stateDirectory);
        LoadAll();
    }

    /// <summary>Raised (throttled per rollback episode) when the wall clock jumps backwards by more than <see cref="MaxBackwardsJumpMs"/>. Payload is the observed jump size in ms.</summary>
    public event Action<long>? ClockTampered;

    private const long MaxBackwardsJumpMs = 60_000L;
    private long lastWallMs;
    private bool tamperLatched;

    /// <summary>Closes the previously open session (if any) and opens a new one for <paramref name="appKey"/>.</summary>
    /// <remarks>
    /// A repeat event for the app that is already open is a no-op (the session keeps running).
    /// When the incoming app matches the most recent closed session of that app in the same day
    /// and the gap since that session's end is under 2 seconds, the two sessions merge: the
    /// closed session is reopened with its original start, spanning the blip.
    /// </remarks>
    public void OnForegroundChanged(string appKey, long timestampMs)
    {
        lock (this.gate)
        {
            var dayKey = DayKeyOf(timestampMs);

            if (this.open != null && this.open.AppKey == appKey && this.open.DayKey == dayKey)
            {
                // Same app already in the foreground: the open session simply continues.
                return;
            }

            if (this.open is { } previousOpen)
            {
                var end = Math.Max(LiveElapsedMs(previousOpen) + previousOpen.StartMs, timestampMs);
                var openDay = GetDay(previousOpen.DayKey);
                openDay.Sessions.Add(new Session(previousOpen.AppKey, previousOpen.StartMs, end));
                this.open = null;
                // Carried in the marker file so a crash before the next flush cannot
                // lose this closed session (full crash durability, tiny write).
                this.markerPending.Add((previousOpen.DayKey, openDay.Sessions[^1]));
                this.dirtyDays.Add(previousOpen.DayKey);
            }

            // Merge across a sub-2-second blip: reopen the previous run of this app.
            var day = GetDay(dayKey);
            for (var i = day.Sessions.Count - 1; i >= 0; i--)
            {
                var previous = day.Sessions[i];
                if (previous.AppKey != appKey)
                {
                    continue;
                }

                if (timestampMs - previous.EndMs < MergeGapMs && previous.EndMs <= timestampMs)
                {
                    day.Sessions.RemoveAt(i);
                    this.open = new OpenSession(dayKey, appKey, previous.StartMs,
                        TicksAtWall(previous.StartMs));
                    this.dirtyDays.Add(dayKey);
                    PersistOpenMarker();
                    return;
                }

                break; // most recent run of this app was too long ago: no merge
            }

            this.open = new OpenSession(dayKey, appKey, timestampMs, MonotonicTicks());
            this.dirtyDays.Add(dayKey);
            // The tiny open-session marker is written immediately so a crash right after a
            // switch resumes the NEW app's session (the bulky session list flushes lazily).
            PersistOpenMarker();
        }
    }

    /// <summary>Raw + live usage for today (ms), minus the app's Reset-Today offset, clamped to >= 0.</summary>
    public long EffectiveUsageMsToday(string appKey)
    {
        lock (this.gate)
        {
            var dayKey = TodayKey();
            var raw = RawUsageMs(dayKey, appKey);
            var dayOffset = GetOffsets(dayKey);
            dayOffset.TryGetValue(appKey, out var offset);
            return Math.Max(0L, raw - offset);
        }
    }

    /// <summary>Raw per-app usage for today in ms (no reset offsets), including the live open session.</summary>
    public IReadOnlyDictionary<string, long> UsageMsToday()
    {
        lock (this.gate)
        {
            var dayKey = TodayKey();
            var state = GetDay(dayKey);
            var result = new Dictionary<string, long>();
            foreach (var session in state.Sessions)
            {
                result.TryGetValue(session.AppKey, out var current);
                result[session.AppKey] = current + Math.Max(0L, session.EndMs - session.StartMs);
            }

            // Compacted (collapsed) sessions keep exact totals without storing each row.
            foreach (var (appKey, aggregatedMs) in state.Aggregated)
            {
                result.TryGetValue(appKey, out var current);
                result[appKey] = current + aggregatedMs;
            }

            if (this.open is { } live && live.DayKey == dayKey)
            {
                result.TryGetValue(live.AppKey, out var current);
                result[live.AppKey] = current + LiveElapsedMs(live);
            }

            return result;
        }
    }

    /// <summary>
    /// Reset-Today for one app: records an offset equal to the app's current raw usage so its
    /// effective usage becomes zero (idempotent; the raw ledger keeps growing monotonically).
    /// </summary>
    public void SetResetOffset(string appKey)
    {
        lock (this.gate)
        {
            var dayKey = TodayKey();
            var dayOffset = GetOffsets(dayKey);
            dayOffset[appKey] = RawUsageMs(dayKey, appKey);
            PersistOffsets(dayKey);
        }
    }

    public void ClearDayBlocks()
    {
        lock (this.gate)
        {
            var dayKey = TodayKey();
            if (this.dailyBlocks.Remove(dayKey))
            {
                TryDeleteFile(BlocksFileName(dayKey));
            }
        }
    }

    public void MarkDailyBlocked(string appKey)
    {
        lock (this.gate)
        {
            var dayKey = TodayKey();
            GetBlocks(dayKey).Add(appKey);
            PersistBlocks(dayKey);
        }
    }

    public bool IsDailyBlocked(string appKey)
    {
        lock (this.gate)
        {
            return GetBlocks(TodayKey()).Contains(appKey);
        }
    }

    private long RawUsageMs(string dayKey, string appKey)
    {
        var state = GetDay(dayKey);
        long raw = state.Sessions
            .Where(s => s.AppKey == appKey)
            .Sum(s => Math.Max(0L, s.EndMs - s.StartMs));
        state.Aggregated.TryGetValue(appKey, out var aggregated);
        raw += aggregated;
        if (this.open is { } live && live.DayKey == dayKey && live.AppKey == appKey)
        {
            raw += LiveElapsedMs(live);
        }

        return raw;
    }

    private long LiveElapsedMs(OpenSession live)
    {
        // The wall delta can be shrunk or frozen by a clock rollback; the
        // monotonic anchor (ms since boot) keeps accruing, so take the larger.
        var wallDelta = Math.Max(0L, NowMs() - live.StartMs);
        var monoDelta = Math.Max(0L, MonotonicTicks() - live.StartTicks);
        return Math.Max(wallDelta, monoDelta);
    }

    private long NowMs()
    {
        var wall = this.time.GetUtcNow().ToUnixTimeMilliseconds();

        if (this.lastWallMs != 0)
        {
            if (!this.tamperLatched && wall < this.lastWallMs - MaxBackwardsJumpMs)
            {
                this.tamperLatched = true;
                ClockTampered?.Invoke(this.lastWallMs - wall);
            }
            else if (wall >= this.lastWallMs)
            {
                this.tamperLatched = false;
            }

            if (wall < this.lastWallMs)
            {
                wall = this.lastWallMs; // ledger time never runs backwards
            }
        }

        this.lastWallMs = wall;
        return wall;
    }

    /// <summary>Monotonic tick value corresponding to a past wall timestamp (best effort; used to re-anchor merged sessions).</summary>
    private long TicksAtWall(long wallMs)
    {
        var elapsed = Math.Max(0L, NowMs() - wallMs);
        return Math.Max(0L, MonotonicTicks() - elapsed);
    }

    private string TodayKey() => DayKeyOf(NowMs());

    private string DayKeyOf(long epochMs)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(epochMs), this.time.LocalTimeZone);
        return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private DayState GetDay(string dayKey)
    {
        if (!this.days.TryGetValue(dayKey, out var state))
        {
            state = new DayState(dayKey);
            this.days[dayKey] = state;
        }

        return state;
    }

    private Dictionary<string, long> GetOffsets(string dayKey)
    {
        if (!this.offsets.TryGetValue(dayKey, out var map))
        {
            map = new Dictionary<string, long>();
            this.offsets[dayKey] = map;
        }

        return map;
    }

    private HashSet<string> GetBlocks(string dayKey)
    {
        if (!this.dailyBlocks.TryGetValue(dayKey, out var set))
        {
            set = new HashSet<string>();
            this.dailyBlocks[dayKey] = set;
        }

        return set;
    }

    private void LoadAll()
    {
        foreach (var file in Directory.EnumerateFiles(stateDirectory, "usage-*.json"))
        {
            var dayKey = Path.GetFileName(file)["usage-".Length..]
                .Replace(".json", string.Empty, StringComparison.Ordinal);
            var payload = ReadJson<DayFile>(file);
            if (payload == null)
            {
                continue;
            }

            var state = new DayState(dayKey);
            foreach (var entry in payload.Sessions)
            {
                state.Sessions.Add(new Session(entry.K, entry.S, entry.E));
            }

            if (payload.Agg != null)
            {
                foreach (var (appKey, ms) in payload.Agg)
                {
                    state.Aggregated[appKey] = ms;
                }
            }

            this.days[dayKey] = state;
        }

        foreach (var file in Directory.EnumerateFiles(stateDirectory, "offsets-*.json"))
        {
            var dayKey = Path.GetFileName(file)["offsets-".Length..]
                .Replace(".json", string.Empty, StringComparison.Ordinal);
            var map = ReadJson<Dictionary<string, long>>(file);
            if (map != null)
            {
                this.offsets[dayKey] = new Dictionary<string, long>(map);
            }
        }

        foreach (var file in Directory.EnumerateFiles(stateDirectory, "blocks-*.json"))
        {
            var dayKey = Path.GetFileName(file)["blocks-".Length..]
                .Replace(".json", string.Empty, StringComparison.Ordinal);
            var set = ReadJson<Dictionary<string, bool>>(file);
            if (set != null)
            {
                this.dailyBlocks[dayKey] = new HashSet<string>(set.Keys);
            }
        }

        PruneOldDays();
        RestoreOpenSession();
    }

    /// <summary>
    /// Restores the live open session and any unflushed closed sessions after a restart.
    /// The tiny usage-open.json marker is written on every app switch and carries both,
    /// so a crash loses nothing; the legacy Open field inside a day file is only
    /// consulted when no marker exists.
    /// </summary>
    private void RestoreOpenSession()
    {
        OpenCandidate? marker = null;
        var markerRaw = ReadJson<OpenFile>(Path.Combine(stateDirectory, OpenFileName));
        if (markerRaw is { } m && !string.IsNullOrWhiteSpace(m.DayKey) && !string.IsNullOrWhiteSpace(m.K))
        {
            marker = new OpenCandidate(m.DayKey, m.K, m.S);
            if (m.Pending is { } pending)
            {
                foreach (var entry in pending)
                {
                    if (string.IsNullOrWhiteSpace(entry.K))
                    {
                        continue;
                    }

                    var state = GetDay(entry.D);
                    state.Sessions.Add(new Session(entry.K, entry.S, entry.E));
                    this.dirtyDays.Add(entry.D); // next flush folds them into the day file
                }
            }
        }

        OpenCandidate? legacy = null;
        if (marker == null)
        {
            foreach (var state in this.days.Values)
            {
                var dayFile = ReadJson<DayFile>(Path.Combine(stateDirectory, UsageFileName(state.DayKey)));
                if (dayFile?.Open is { } persistedOpen)
                {
                    var candidate = new OpenCandidate(state.DayKey, persistedOpen.K, persistedOpen.S);
                    if (legacy == null || candidate.StartMs > legacy.StartMs)
                    {
                        legacy = candidate;
                    }
                }
            }
        }

        var best = marker ?? legacy;
        if (best != null)
        {
            // Ticks-since-boot cannot survive a reboot, so a restored open session
            // starts with a fresh anchor: monotonic contributes 0 and the wall clock
            // alone resumes accounting.
            this.open = new OpenSession(best.DayKey, best.AppKey, best.StartMs, MonotonicTicks());
        }
    }

    private sealed record OpenCandidate(string DayKey, string AppKey, long StartMs);

    /// <summary>
    /// Drops day state older than <see cref="MaxRetainedDays"/> (day keys sort lexicographically,
    /// so a string comparison of "yyyy-MM-dd" keys is an exact date comparison). Deletes the
    /// on-disk files too. Callers hold the lock.
    /// </summary>
    private void PruneOldDays()
    {
        if (this.days.Count <= MaxRetainedDays)
        {
            return;
        }

        var cutoff = DayKeyOf(NowMs() - MaxRetainedDays * 86_400_000L);
        PruneCollection(this.days, cutoff);
        PruneCollection(this.offsets, cutoff);
        PruneCollection(this.dailyBlocks, cutoff);

        foreach (var file in Directory.EnumerateFiles(stateDirectory, "usage-*.json"))
        {
            var dayKey = Path.GetFileName(file)["usage-".Length..]
                .Replace(".json", string.Empty, StringComparison.Ordinal);
            if (string.CompareOrdinal(dayKey, cutoff) < 0)
            {
                TryDeleteFile(file);
            }
        }

        foreach (var file in Directory.EnumerateFiles(stateDirectory, "offsets-*.json"))
        {
            var dayKey = Path.GetFileName(file)["offsets-".Length..]
                .Replace(".json", string.Empty, StringComparison.Ordinal);
            if (string.CompareOrdinal(dayKey, cutoff) < 0)
            {
                TryDeleteFile(file);
            }
        }

        foreach (var file in Directory.EnumerateFiles(stateDirectory, "blocks-*.json"))
        {
            var dayKey = Path.GetFileName(file)["blocks-".Length..]
                .Replace(".json", string.Empty, StringComparison.Ordinal);
            if (string.CompareOrdinal(dayKey, cutoff) < 0)
            {
                TryDeleteFile(file);
            }
        }
    }

    private static void PruneCollection<TValue>(Dictionary<string, TValue> map, string cutoff)
    {
        foreach (var dayKey in map.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
        {
            map.Remove(dayKey);
        }
    }

    private void PersistDay(string dayKey)
    {
        var state = GetDay(dayKey);
        var payload = new DayFile
        {
            Sessions = state.Sessions.Select(s => new SessionEntry { K = s.AppKey, S = s.StartMs, E = s.EndMs }).ToList(),
            Agg = state.Aggregated.Count > 0 ? state.Aggregated : null,
            // The open session is tracked separately in usage-open.json (written on every
            // switch); embedding it here would force a full-list rewrite per switch.
            Open = null,
        };
        WriteJson(Path.Combine(stateDirectory, UsageFileName(dayKey)), payload);
        PruneOldDays();
    }

    /// <summary>
    /// Writes the tiny open-session marker immediately on every app switch: the open
    /// session plus any closed sessions not yet flushed. Crash semantics match the
    /// previous always-persist behavior — nothing is lost, and the bulky session list
    /// still flushes lazily.
    /// </summary>
    private void PersistOpenMarker()
    {
        if (this.open is not { } live)
        {
            return;
        }

        var payload = new OpenFile
        {
            DayKey = live.DayKey,
            K = live.AppKey,
            S = live.StartMs,
        };
        if (this.markerPending.Count > 0)
        {
            payload.Pending = this.markerPending
                .Select(p => new PendingEntry { D = p.DayKey, K = p.Session.AppKey, S = p.Session.StartMs, E = p.Session.EndMs })
                .ToList();
        }

        WriteJson(Path.Combine(stateDirectory, OpenFileName), payload);
    }

    /// <summary>Writes dirty day files (called from the service's periodic loops and on shutdown).</summary>
    public void FlushDirty()
    {
        lock (this.gate)
        {
            if (this.dirtyDays.Count > 0)
            {
                foreach (var dayKey in this.dirtyDays)
                {
                    if (this.days.ContainsKey(dayKey))
                    {
                        CompactDay(dayKey);
                        PersistDay(dayKey);
                    }
                }

                this.dirtyDays.Clear();
            }

            if (this.markerPending.Count > 0)
            {
                // The closed sessions are now inside their day files; the marker no
                // longer needs to carry them.
                this.markerPending.Clear();
                PersistOpenMarker();
            }
        }
    }

    /// <summary>Collapses the oldest sessions into exact per-app totals once a day's list exceeds the cap.</summary>
    private void CompactDay(string dayKey)
    {
        var state = GetDay(dayKey);
        if (state.Sessions.Count <= SessionCap)
        {
            return;
        }

        var collapse = state.Sessions.Count - SessionKeep;
        foreach (var session in state.Sessions.Take(collapse))
        {
            state.Aggregated.TryGetValue(session.AppKey, out var current);
            state.Aggregated[session.AppKey] = current + Math.Max(0L, session.EndMs - session.StartMs);
        }

        state.Sessions.RemoveRange(0, collapse);
    }

    /// <summary>Current number of stored sessions for a day (test accessor).</summary>
    internal int SessionCountForDay(string dayKey)
    {
        lock (this.gate)
        {
            return this.days.TryGetValue(dayKey, out var state) ? state.Sessions.Count : 0;
        }
    }

    private void PersistOffsets(string dayKey)
    {
        WriteJson(Path.Combine(stateDirectory, OffsetsFileName(dayKey)), GetOffsets(dayKey));
    }

    private void PersistBlocks(string dayKey)
    {
        var set = GetBlocks(dayKey);
        var payload = set.ToDictionary(appKey => appKey, _ => true);
        WriteJson(Path.Combine(stateDirectory, BlocksFileName(dayKey)), payload);
    }

    private static string UsageFileName(string dayKey) => $"usage-{dayKey}.json";

    private const string OpenFileName = "usage-open.json";

    private static string OffsetsFileName(string dayKey) => $"offsets-{dayKey}.json";

    private static string BlocksFileName(string dayKey) => $"blocks-{dayKey}.json";

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort: a stale block file is re-loaded as an empty set only if absent
        }
    }

    private static T? ReadJson<T>(string path)
        where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteJson<T>(string path, T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        AtomicFile.WriteAllText(path, json);
    }

    private sealed record OpenSession(string DayKey, string AppKey, long StartMs, long StartTicks);

    private sealed record Session(string AppKey, long StartMs, long EndMs);

    private sealed class DayState
    {
        public DayState(string dayKey)
        {
            this.DayKey = dayKey;
        }

        public string DayKey { get; }

        public List<Session> Sessions { get; } = new();

        /// <summary>Exact per-app totals of sessions collapsed by compaction (keeps usage math bounded).</summary>
        public Dictionary<string, long> Aggregated { get; } = new();
    }

    private sealed class DayFile
    {
        public List<SessionEntry> Sessions { get; set; } = new();

        /// <summary>Legacy open-session marker (read for back-compat; no longer written — see usage-open.json).</summary>
        public SessionEntry? Open { get; set; }

        public Dictionary<string, long>? Agg { get; set; }
    }

    private sealed class OpenFile
    {
        public string DayKey { get; set; } = string.Empty;

        public string K { get; set; } = string.Empty;

        public long S { get; set; }

        /// <summary>Closed sessions not yet folded into their day file (crash-durable via this marker).</summary>
        public List<PendingEntry>? Pending { get; set; }
    }

    private sealed class PendingEntry
    {
        public string D { get; set; } = string.Empty;

        public string K { get; set; } = string.Empty;

        public long S { get; set; }

        public long E { get; set; }
    }

    private sealed class SessionEntry
    {
        public string K { get; set; } = string.Empty;

        public long S { get; set; }

        public long E { get; set; }
    }
}
