using System.IO;
using System.Text.Json;

namespace GuardPulse.Agent.Session;

/// <summary>
/// Read-only view of the policy cache the service writes next to device.json
/// (C:\ProgramData\GuardPulse\Laptop\policy-cache.json). Consulted only while
/// the pipe to the service is dead: the agent keeps blocking rule-blocked apps
/// on its own — fail closed until the service returns. Contains no PIN, so
/// offline unlocks are impossible by design.
///
/// Service-written schema (all fields except the app lists are optional; old
/// files without them still parse):
/// {"writtenAtMs":N,"safeMode":bool,"blockedApps":[...],"dailyBlockedApps":[...],
///  "sessionBlockedApps":[...],"allowlistEnabled":bool,"allowlistApps":[...],
///  "schedule":{"enabled":bool,"startMinute":N,"endMinute":N},"deviceLocked":bool}
/// </summary>
internal sealed class PolicyCache
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GuardPulse", "Laptop", "policy-cache.json");

    // Bypass-tool virtual app ids, mirrored by value from
    // PolicyConstants.WindowsBypassPackages
    // (windows/src/GuardPulse.Protocol/PolicyConstants.cs). Copied, not
    // referenced, so this per-user agent never depends on the Service project.
    // These tools are default-locked: never fail open for them.
    private static readonly HashSet<string> BypassTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "guardpulse.windows.taskmgr",
        "guardpulse.windows.commandline",
        "guardpulse.windows.registry",
        "guardpulse.windows.settings",
        "guardpulse.windows.installers",
    };

    /// <summary>Bypass-tool app keys (fail-closed seed for unknown policy state).</summary>
    internal static IReadOnlyCollection<string> BypassAppKeys => BypassTools;

    // Last successfully parsed policy. A cache that exists but cannot be read
    // (corrupt mid-write, transient ACL denial) must NOT fail open to an empty
    // blocklist — the stale policy keeps offline locking alive until the service
    // returns and rewrites the file.
    private static PolicyCache? _lastGood;

    // Timestamp-gated parse cache: the file is re-read only when its mtime or size
    // changes, so per-foreground-event and per-second callers do zero disk I/O in
    // the steady state while seeing exactly the same freshness as a fresh read.
    private static PolicyCache? _cached;
    private static DateTime _cachedMtimeUtc;
    private static long _cachedSize;

    public long WrittenAtMs { get; private init; }

    public bool SafeMode { get; private init; }

    public bool DeviceLocked { get; private init; }

    public bool AllowlistEnabled { get; private init; }

    public HashSet<string> AllowlistApps { get; private init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ScheduleEnabled { get; private set; }

    public int ScheduleStartMinute { get; private set; }

    public int ScheduleEndMinute { get; private set; }

    public HashSet<string> BlockedApps { get; private init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> DailyBlockedApps { get; private init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> SessionBlockedApps { get; private init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when parsed from a real service-written file. Load() returns
    /// null instead of an instance when the file never existed — callers treat
    /// null as UNKNOWN (never synthesize an empty policy).</summary>
    public bool KnownGood { get; private init; }

    public static PolicyCache? Load()
    {
        return Load(DefaultPath);
    }

    public static PolicyCache? Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                // The service never wrote a cache: the policy state is UNKNOWN,
                // not empty. Return the last good policy when one exists (fail
                // closed), otherwise null so callers apply unknown-state rules.
                _cached = null;
                return _lastGood;
            }

            var mtime = info.LastWriteTimeUtc;
            var size = info.Length;
            if (_cached != null && mtime == _cachedMtimeUtc && size == _cachedSize)
            {
                return _cached;
            }

            var cache = Parse(path);
            _cached = cache;
            _cachedMtimeUtc = mtime;
            _cachedSize = size;
            return cache;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Keep the last good policy (fail closed) instead of unlocking everything;
            // with no last good policy the state is genuinely unknown (null).
            _cached = null;
            return _lastGood;
        }
    }

    private static PolicyCache Parse(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var cache = new PolicyCache
        {
            KnownGood = true,
            WrittenAtMs = root.TryGetProperty("writtenAtMs", out var writtenAt) && writtenAt.ValueKind == JsonValueKind.Number
                ? writtenAt.GetInt64()
                : 0,
            SafeMode = root.TryGetProperty("safeMode", out var safeMode) && safeMode.GetBoolean(),
            DeviceLocked = root.TryGetProperty("deviceLocked", out var deviceLocked) && deviceLocked.GetBoolean(),
            AllowlistEnabled = root.TryGetProperty("allowlistEnabled", out var allowlistEnabled) && allowlistEnabled.GetBoolean(),
        };
        if (root.TryGetProperty("blockedApps", out var blocked) && blocked.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in blocked.EnumerateArray())
            {
                if (item.GetString() is { } appKey) cache.BlockedApps.Add(appKey);
            }
        }

        if (root.TryGetProperty("dailyBlockedApps", out var daily) && daily.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in daily.EnumerateArray())
            {
                if (item.GetString() is { } appKey) cache.DailyBlockedApps.Add(appKey);
            }
        }

        if (root.TryGetProperty("sessionBlockedApps", out var session) && session.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in session.EnumerateArray())
            {
                if (item.GetString() is { } appKey) cache.SessionBlockedApps.Add(appKey);
            }
        }

        if (root.TryGetProperty("allowlistApps", out var allowlist) && allowlist.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in allowlist.EnumerateArray())
            {
                if (item.GetString() is { } appKey) cache.AllowlistApps.Add(appKey);
            }
        }

        if (root.TryGetProperty("schedule", out var schedule) && schedule.ValueKind == JsonValueKind.Object)
        {
            cache.ScheduleEnabled = schedule.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean();
            if (schedule.TryGetProperty("startMinute", out var start) && start.ValueKind == JsonValueKind.Number)
            {
                cache.ScheduleStartMinute = start.GetInt32();
            }

            if (schedule.TryGetProperty("endMinute", out var end) && end.ValueKind == JsonValueKind.Number)
            {
                cache.ScheduleEndMinute = end.GetInt32();
            }
        }

        _lastGood = cache;
        return cache;
    }

    /// <summary>The lock reason for the app, or null when it must stay usable.</summary>
    public string? BlockedReasonFor(string appKey)
    {
        if (SafeMode)
        {
            return null; // Safe Mode suspends all locking, offline included
        }

        // Schedule checked BEFORE the whole-device flag: deviceLocked covers both
        // budget and out-of-hours, and the offline wall must show the right reason.
        if (ScheduleEnabled && IsOutsideSchedule(DateTime.Now, ScheduleStartMinute, ScheduleEndMinute))
        {
            return "schedule";
        }

        if (DeviceLocked)
        {
            return "budget"; // whole-device lock (budget), enforced offline
        }

        if (AllowlistEnabled && !AllowlistApps.Contains(appKey))
        {
            return "notApproved";
        }

        if (BlockedApps.Contains(appKey))
        {
            return "manual";
        }

        if (DailyBlockedApps.Contains(appKey))
        {
            return "dailyLimit";
        }

        if (SessionBlockedApps.Contains(appKey))
        {
            return "sessionLimit";
        }

        // Never fail open for bypass tools, even if the service omitted them
        // from every list in this snapshot.
        if (BypassTools.Contains(appKey))
        {
            return "manual";
        }

        return null;
    }

    /// <summary>Lock reason while the policy state is UNKNOWN (Load() returned
    /// null): bypass tools stay locked ("manual", fail closed), everything else
    /// is left alone until the service speaks.</summary>
    public static string? UnknownReasonFor(string appKey)
    {
        return BypassTools.Contains(appKey) ? "manual" : null;
    }

    private static bool IsOutsideSchedule(DateTime localNow, int startMinute, int endMinute)
    {
        var now = localNow.Hour * 60 + localNow.Minute;
        // start==end means "never allowed" (locked all day) — mirrors the engine.
        if (startMinute == endMinute)
        {
            return true;
        }

        if (endMinute < startMinute)
        {
            // Overnight window (e.g. 22:00-06:00): outside is the daytime gap.
            return now >= endMinute && now < startMinute;
        }

        return now < startMinute || now >= endMinute;
    }

    /// <summary>Human label from an app key (lowercased exe path or virtual bypass id).</summary>
    public static string LabelFor(string appKey)
    {
        if (appKey.StartsWith("guardpulse.windows.", StringComparison.Ordinal))
        {
            return appKey["guardpulse.windows.".Length..] switch
            {
                "taskmgr" => "Task Manager",
                "commandline" => "Command Line",
                "registry" => "Registry Editor",
                "settings" => "Settings",
                "installers" => "Installers",
                _ => appKey
            };
        }

        var name = Path.GetFileName(appKey);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
