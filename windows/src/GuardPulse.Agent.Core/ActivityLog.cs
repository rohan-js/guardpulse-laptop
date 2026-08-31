namespace GuardPulse.Agent.Core;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Foreground activity log for the Firebase activity schema (devices/{id}/activity/current and
/// /history). Sessions shorter than <see cref="MinSessionMs"/> are dropped from history (they are
/// still counted by <see cref="UsageLedger"/>); closed sessions queue locally and are marked
/// uploaded one by one (queue-then-ack). State persists to activity-log.json in
/// <paramref name="stateDirectory"/> (UTF-8, System.Text.Json) and survives restarts.
/// Thread-safe: all public members synchronize on a single lock object.
/// </summary>
public sealed class ActivityLog
{
    /// <summary>App sessions shorter than this are not queued into history.</summary>
    public const long MinSessionMs = 2_000L;

    /// <summary>Browser tab sessions shorter than this are not queued (tab switches are noisy).</summary>
    public const long MinTabSessionMs = 10_000L;

    /// <summary>Default overlay state when an app session starts.</summary>
    public const string OverlayStateNone = "none";

    /// <summary>Overlay state while the GuardPulse lock window is shown.</summary>
    public const string OverlayStateLocked = "locked";

    private const string CaptureSourceAgent = "agent";
    private const string SessionTypeApp = "app";
    private const string SessionTypeTab = "tab";

    /// <summary>History entries older than this are dropped even if never uploaded (bounds disk/RAM).</summary>
    private const long RetentionMs = 30L * 24 * 60 * 60_000L;

    /// <summary>Hard cap on stored history rows (the parent UI reads at most the latest 500).</summary>
    private const int HistoryCap = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string stateDirectory;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly string filePath;
    private readonly List<PersistedEntry> history = new();
    private bool dirty;
    private CurrentSnapshot? current;
    private CurrentTabSnapshot? currentTab;

    public ActivityLog(string stateDirectory, TimeProvider time)
    {
        this.stateDirectory = stateDirectory;
        this.time = time;
        Directory.CreateDirectory(stateDirectory);
        this.filePath = Path.Combine(stateDirectory, "activity-log.json");
        Load();
    }

    /// <summary>Finishes the currently open app session (if any) and starts a new one.
    /// Also closes the open tab session — leaving the app ends the tab session too.</summary>
    public void StartApp(string appKey, string label, long startedAtMs)
    {
        lock (this.gate)
        {
            this.CloseTab(startedAtMs);
            CloseAt(startedAtMs);
            this.current = new CurrentSnapshot(
                runtimeApp: appKey,
                appKey: appKey,
                appLabel: label,
                appStartedAt: startedAtMs,
                overlayState: OverlayStateNone,
                updatedAt: startedAtMs);
            this.dirty = true; // flushed by the service's periodic activity loop
        }
    }

    /// <summary>Closes the current app session at <paramref name="endedAtMs"/> and queues it for upload.</summary>
    /// <param name="overlayState">Optional final overlay state recorded on the snapshot before closing.</param>
    public void CloseCurrent(long endedAtMs, string? overlayState = null)
    {
        lock (this.gate)
        {
            if (this.current == null)
            {
                return;
            }

            if (overlayState != null)
            {
                this.current = this.current with { overlayState = overlayState };
            }

            CloseAt(endedAtMs);
            this.dirty = true;
        }
    }

    /// <summary>Finishes the currently open tab session (if any) and starts a new one.</summary>
    public void StartTab(string browserAppKey, string tabLabel, long startedAtMs, string? url = null)
    {
        lock (this.gate)
        {
            this.CloseTab(startedAtMs);
            this.currentTab = new CurrentTabSnapshot(
                AppKey: browserAppKey,
                Label: tabLabel,
                StartedAt: startedAtMs,
                Url: url);
            this.dirty = true; // flushed by the service's periodic activity loop
        }
    }

    /// <summary>Closes the current tab session at <paramref name="endedAtMs"/> and queues it for upload.</summary>
    public void CloseTab(long endedAtMs)
    {
        lock (this.gate)
        {
            if (this.currentTab == null)
            {
                return;
            }

            var durationMs = endedAtMs - this.currentTab.StartedAt;
            if (durationMs >= MinTabSessionMs)
            {
                this.history.Add(new PersistedEntry(
                    id: NewSessionId(),
                    appKey: this.currentTab.AppKey,
                    appLabel: this.currentTab.Label,
                    startedAt: this.currentTab.StartedAt,
                    endedAt: endedAtMs,
                    uploaded: false,
                    entryType: SessionTypeTab,
                    url: this.currentTab.Url));
            }

            this.currentTab = null;
            this.dirty = true;
        }
    }

    /// <summary>Updates the overlay state ("none"|"locked") of the current snapshot.</summary>
    public void SetOverlayState(string overlayState)
    {
        lock (this.gate)
        {
            if (this.current == null)
            {
                return;
            }

            // The service re-evaluates every 15s and re-asserts the same overlay; marking
            // dirty on a no-op would rewrite the whole log file up to 3x/min while idle.
            if (string.Equals(this.current.overlayState, overlayState, StringComparison.Ordinal))
            {
                return;
            }

            this.current = this.current with { overlayState = overlayState, updatedAt = NowMs() };
            this.dirty = true;
        }
    }

    /// <summary>Writes the log if unsaved changes exist (called from the service's periodic activity loop and on shutdown).</summary>
    public void Flush()
    {
        lock (this.gate)
        {
            if (this.dirty)
            {
                Persist();
            }
        }
    }

    public CurrentSnapshot? Current()
    {
        lock (this.gate)
        {
            return this.current;
        }
    }

    /// <summary>Closed sessions that have not been acknowledged with <see cref="MarkUploaded"/> yet.</summary>
    public IReadOnlyList<HistoryEntry> Pending()
    {
        lock (this.gate)
        {
            return this.history
                .Where(e => !e.Uploaded)
                .Select(e => e.ToHistoryEntry())
                .ToList();
        }
    }

    public void MarkUploaded(string id)
    {
        lock (this.gate)
        {
            var entry = this.history.FirstOrDefault(e => e.Id == id);
            if (entry == null || entry.Uploaded)
            {
                return;
            }

            entry.Uploaded = true;
            Persist();
        }
    }

    /// <summary>Retention: drops history entries that ended before <paramref name="cutoffMs"/> (uploaded or not).</summary>
    public void PruneBefore(long cutoffMs)
    {
        lock (this.gate)
        {
            if (this.history.RemoveAll(e => e.EndedAt < cutoffMs) > 0)
            {
                Persist();
            }
        }
    }

    private void CloseAt(long endedAtMs)
    {
        if (this.current is not { } snapshot)
        {
            return;
        }

        var durationMs = endedAtMs - snapshot.appStartedAt;
        if (durationMs >= MinSessionMs)
        {
            this.history.Add(new PersistedEntry(
                id: NewSessionId(),
                appKey: snapshot.appKey,
                appLabel: snapshot.appLabel,
                startedAt: snapshot.appStartedAt,
                endedAt: endedAtMs));
        }

        this.current = null;
    }

    private long NowMs() => this.time.GetUtcNow().ToUnixTimeMilliseconds();

    private static string NewSessionId() => Guid.NewGuid().ToString("N");

    private void Load()
    {
        try
        {
            if (!File.Exists(this.filePath))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(this.filePath), JsonOptions);
            if (payload?.History != null)
            {
                this.history.AddRange(payload.History);
            }

            this.current = payload?.Current;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            this.history.Clear();
            this.current = null;
        }
    }

    private void Persist()
    {
        // Bounded history: drop entries outside the retention window even if Firebase
        // acknowledgements stall, so the file can never grow without bound.
        var cutoff = NowMs() - RetentionMs;
        if (cutoff > 0)
        {
            this.history.RemoveAll(e => e.EndedAt < cutoff);
        }

        if (this.history.Count > HistoryCap)
        {
            this.history.RemoveRange(0, this.history.Count - HistoryCap);
        }

        var payload = new Payload
        {
            Current = this.current,
            History = this.history.Select(e => new PersistedEntry(e.Id, e.AppKey, e.AppLabel, e.StartedAt, e.EndedAt, e.Uploaded, e.EntryType, e.Url)).ToList(),
        };
        AtomicFile.WriteAllText(this.filePath, JsonSerializer.Serialize(payload, JsonOptions));
        this.dirty = false;
    }

    /// <summary>devices/{id}/activity/current payload. Property names match the Firebase schema exactly.</summary>
    public sealed record CurrentSnapshot(
        string runtimeApp,
        string appKey,
        string appLabel,
        long appStartedAt,
        string overlayState,
        bool mediaAvailable = false,
        string playbackState = "unknown",
        double playbackSpeed = 0,
        string captureSource = "agent",
        long updatedAt = 0)
    {
        public string ToFirebaseJson() => JsonSerializer.Serialize(this, JsonOptions);
    }

    /// <summary>One entry of devices/{id}/activity/history/{id}. Property names match the Firebase schema exactly.</summary>
    public sealed record HistoryEntry(
        string id,
        string type,
        string appKey,
        string appLabel,
        long startedAt,
        long endedAt,
        long updatedAt,
        string captureSource,
        string? url = null)
    {
        public string ToFirebaseJson() => JsonSerializer.Serialize(this, JsonOptions);
    }

    private sealed class Payload
    {
        public CurrentSnapshot? Current { get; set; }

        public List<PersistedEntry>? History { get; set; }
    }

    private sealed class PersistedEntry
    {
        public PersistedEntry()
        {
        }

        public PersistedEntry(string id, string appKey, string appLabel, long startedAt, long endedAt, bool uploaded = false, string entryType = SessionTypeApp, string? url = null)
        {
            this.Id = id;
            this.AppKey = appKey;
            this.AppLabel = appLabel;
            this.StartedAt = startedAt;
            this.EndedAt = endedAt;
            this.Uploaded = uploaded;
            this.EntryType = entryType;
            this.Url = url;
        }

        public string Id { get; set; } = string.Empty;

        public string AppKey { get; set; } = string.Empty;

        public string AppLabel { get; set; } = string.Empty;

        public long StartedAt { get; set; }

        public long EndedAt { get; set; }

        public bool Uploaded { get; set; }

        public string EntryType { get; set; } = SessionTypeApp;

        public string? Url { get; set; }

        public HistoryEntry ToHistoryEntry() => new(
            id: this.Id,
            type: this.EntryType,
            appKey: this.AppKey,
            appLabel: this.AppLabel,
            startedAt: this.StartedAt,
            endedAt: this.EndedAt,
            updatedAt: this.EndedAt,
            captureSource: CaptureSourceAgent,
            url: this.Url);
    }

    /// <summary>Open browser tab session; kept separate from app sessions so tab churn
    /// can never disturb foreground tracking.</summary>
    internal sealed record CurrentTabSnapshot(string AppKey, string Label, long StartedAt, string? Url = null);
}
