using System.IO;
using System.Text.Json;

namespace GuardPulse.Agent.Session;

/// <summary>
/// Read-only view of the policy cache the service writes next to device.json
/// (C:\ProgramData\GuardPulse\Laptop\policy-cache.json). Consulted only while
/// the pipe to the service is dead: the agent keeps blocking rule-blocked apps
/// on its own — fail closed until the service returns. Contains no PIN, so
/// offline unlocks are impossible by design.
/// </summary>
internal sealed class PolicyCache
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GuardPulse", "Laptop", "policy-cache.json");

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

    public bool SafeMode { get; private init; }

    public HashSet<string> BlockedApps { get; private init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> DailyBlockedApps { get; private init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> SessionBlockedApps { get; private init; } = new(StringComparer.OrdinalIgnoreCase);

    public static PolicyCache Load()
    {
        return Load(DefaultPath);
    }

    public static PolicyCache Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                _cached = null;
                return new PolicyCache();
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
            // a missing cache (service never wrote one) is still genuinely empty.
            _cached = null;
            return _lastGood ?? new PolicyCache();
        }
    }

    private static PolicyCache Parse(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var cache = new PolicyCache
        {
            SafeMode = root.TryGetProperty("safeMode", out var safeMode) && safeMode.GetBoolean()
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

        return null;
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
