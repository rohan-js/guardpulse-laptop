namespace GuardPulse.Agent.Core;

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Locally granted app unlocks (approved unlockRequests). A one-visit grant
/// (<see cref="Grant"/> without a duration) stays unlocked until the host clears it on the next
/// foreground switch; a timed grant expires automatically <paramref name="duration"/> after it was
/// granted. Entries persist to unlocks.json in the state directory and survive restarts.
/// Thread-safe: all public members synchronize on a single lock object.
/// </summary>
public sealed class OneVisitUnlocks
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string stateFilePath;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<string, long?> entries = new();

    public OneVisitUnlocks(string stateDirectory, TimeProvider time)
    {
        this.stateFilePath = Path.Combine(stateDirectory, "unlocks.json");
        this.time = time;
        Directory.CreateDirectory(stateDirectory);
        Load();
    }

    /// <summary>
    /// Convenience constructor using the production state directory
    /// (%ProgramData%\GuardPulse\Laptop) and wall-clock time.
    /// </summary>
    public OneVisitUnlocks()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GuardPulse",
                "Laptop"),
            TimeProvider.System)
    {
    }

    /// <summary>Grants an unlock. Without a duration it holds until cleared; with one, until it expires.
    /// The deadline is stored as epoch ms, so callers tracking the server clock grant with
    /// <see cref="GrantAt"/> (or serverNowMs) to keep expiry consistent with RTDB-written timestamps.</summary>
    public void Grant(string appKey, TimeSpan? duration = null)
    {
        lock (this.gate)
        {
            this.entries[appKey] = duration == null
                ? null
                : NowMs() + (long)duration.Value.TotalMilliseconds;
            Persist();
        }
    }

    /// <summary>
    /// Grants an unlock like <see cref="Grant"/>, but the timed deadline is measured from
    /// <paramref name="nowMs"/> (the server clock) instead of the local one.
    /// </summary>
    public void GrantAt(string appKey, long nowMs, TimeSpan? duration = null)
    {
        lock (this.gate)
        {
            this.entries[appKey] = duration == null
                ? null
                : nowMs + (long)duration.Value.TotalMilliseconds;
            Persist();
        }
    }

    /// <summary>
    /// True while a grant for <paramref name="appKey"/> is live. <paramref name="nowMs"/> is
    /// the server-clock epoch ms used for timed-expiry checks; null falls back to the local clock.
    /// </summary>
    public bool IsUnlocked(string appKey, long? nowMs = null)
    {
        lock (this.gate)
        {
            PruneExpired(nowMs);
            return this.entries.ContainsKey(appKey);
        }
    }

    public void Clear(string appKey)
    {
        lock (this.gate)
        {
            if (this.entries.Remove(appKey))
            {
                Persist();
            }
        }
    }

    public void ClearAll()
    {
        lock (this.gate)
        {
            if (this.entries.Count > 0)
            {
                this.entries.Clear();
                Persist();
            }
        }
    }

    private void PruneExpired(long? nowMs = null)
    {
        var now = nowMs ?? NowMs();
        var expired = new List<string>();
        foreach (var (appKey, expiresAtMs) in this.entries)
        {
            if (expiresAtMs is long deadline && now >= deadline)
            {
                expired.Add(appKey);
            }
        }

        if (expired.Count > 0)
        {
            foreach (var appKey in expired)
            {
                this.entries.Remove(appKey);
            }

            Persist();
        }
    }

    private long NowMs() => this.time.GetUtcNow().ToUnixTimeMilliseconds();

    private void Load()
    {
        try
        {
            if (!File.Exists(this.stateFilePath))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(this.stateFilePath), JsonOptions);
            if (payload?.Unlocks != null)
            {
                foreach (var entry in payload.Unlocks)
                {
                    if (!string.IsNullOrEmpty(entry.AppKey))
                    {
                        this.entries[entry.AppKey] = entry.ExpiresAtMs;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            this.entries.Clear();
        }
    }

    private void Persist()
    {
        var payload = new Payload
        {
            Unlocks = this.entries
                .Select(kv => new Entry { AppKey = kv.Key, ExpiresAtMs = kv.Value })
                .ToList(),
        };
        AtomicFile.WriteAllText(this.stateFilePath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private sealed class Payload
    {
        public List<Entry> Unlocks { get; set; } = new();
    }

    private sealed class Entry
    {
        public string AppKey { get; set; } = string.Empty;

        /// <summary>Epoch-ms deadline of a timed unlock; null/omitted for one-visit unlocks.</summary>
        public long? ExpiresAtMs { get; set; }
    }
}
