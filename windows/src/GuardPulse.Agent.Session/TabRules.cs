namespace GuardPulse.Agent.Session;

using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Real-time enforcement of the blocked-site list on ALREADY-OPEN browser tabs, using
/// UI Automation only: when the active tab's URL matches the rule set, the agent
/// invokes Chromium's own per-tab "Close tab" button in the tab strip. No keystrokes,
/// no address-bar manipulation, no focus change, no process kills — the offending tab
/// simply closes. Registry URLBlocklist + hosts remain the navigation-start layers;
/// this closes the gap for SPA jumps and already-open tabs.
/// </summary>
public sealed class TabRules
{
    private IReadOnlyList<string> _exactDomains = new List<string>();
    private IReadOnlyList<string> _domains = new List<string>();
    private IReadOnlyList<string> _paths = new List<string>();

    // Parent-typed custom sites: matched EXACTLY (www-canonical, no suffix walk) so a
    // youtube.com rule covers www.youtube.com but never music.youtube.com.
    // Normalized to lowercase on set so comparison is case-insensitive no matter
    // which ingestion path built the set (Parse, pipe push, hello).
    public IReadOnlyList<string> ExactDomains
    {
        get => _exactDomains;
        init => _exactDomains = NormalizeAll(value);
    }

    // Curated category lists (social/gambling/adult/gaming): whole-domain rules —
    // the suffix walk below covers their subdomains by design.
    public IReadOnlyList<string> Domains
    {
        get => _domains;
        init => _domains = NormalizeAll(value);
    }

    public IReadOnlyList<string> Paths
    {
        get => _paths;
        init => _paths = NormalizeAll(value);
    }

    private static IReadOnlyList<string> NormalizeAll(IReadOnlyList<string>? values)
    {
        if (values is null) return new List<string>();
        var normalized = new List<string>(values.Count);
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v)) normalized.Add(v.ToLowerInvariant());
        }

        return normalized;
    }

    public static TabRules Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var exactDomains = new List<string>();
            var domains = new List<string>();
            var paths = new List<string>();
            if (root.TryGetProperty("exactDomains", out var e) && e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray())
                {
                    if (item.GetString() is { Length: > 0 } host) exactDomains.Add(host.ToLowerInvariant());
                }
            }

            if (root.TryGetProperty("domains", out var d) && d.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in d.EnumerateArray())
                {
                    // Normalized to lowercase at parse so Domains comparison is
                    // case-insensitive (OrdinalIgnoreCase at match time too).
                    if (item.GetString() is { Length: > 0 } host) domains.Add(host.ToLowerInvariant());
                }
            }

            if (root.TryGetProperty("paths", out var p) && p.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in p.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } path) paths.Add(path.ToLowerInvariant());
                }
            }

            return new TabRules { ExactDomains = exactDomains, Domains = domains, Paths = paths };
        }
        catch (JsonException)
        {
            return new TabRules();
        }
    }

    public bool IsEmpty => ExactDomains.Count == 0 && Domains.Count == 0 && Paths.Count == 0;

    /// <summary>Matches an omnibox URL against the rules. ExactDomains (parent-typed
    /// sites) match the host exactly with www-canonicalization on both sides — no
    /// subdomain leakage. Whole-domain rules (curated categories) cover subdomains
    /// (walking parent suffixes); path rules match host + segment-boundary prefix.
    /// Scheme- and case-insensitive. Returns the matched rule or null.</summary>
    public string? Match(string? url)
    {
        if (url is null || !IsHttpUrl(url)) return null;

        var s = StripScheme(url.Trim());
        var slash = s.IndexOf('/');
        var hostPort = slash >= 0 ? s[..slash] : s;
        var path = slash >= 0 ? s[slash..] : "";

        hostPort = hostPort.TrimEnd('.');
        var at = hostPort.LastIndexOf('@');
        if (at >= 0) hostPort = hostPort[(at + 1)..];
        var colon = hostPort.IndexOf(':');
        if (colon >= 0) hostPort = hostPort[..colon];

        if (!NormalizeHost(hostPort, out var host)) return null;

        if (path.Length > 1)
        {
            var q = path.IndexOf('?');
            if (q >= 0) path = path[..q];
            if (path.Length > 1) path = path.TrimEnd('/');
        }

        // Parent-typed rules first, EXACTLY: strip the canonical www. prefix from
        // both sides, then compare. youtube.com blocks youtube.com + www.youtube.com;
        // music.youtube.com is a different host and stays open.
        var hostKey = StripWww(host);
        foreach (var rule in ExactDomains)
        {
            if (hostKey.Equals(StripWww(rule), StringComparison.Ordinal))
            {
                return $"exact:{rule}";
            }
        }

        // Walk host and its parent suffixes: a category rule like facebook.com covers
        // www./m./touch. variants by design.
        var current = host;
        while (current is not null)
        {
            // Rule sets are normalized to lowercase at parse/set, so the domain
            // comparison is case-insensitive (current is already lowercase).
            if (Domains.Contains(current, StringComparer.OrdinalIgnoreCase)) return $"host:{current}";
            current = NextSuffix(current);
        }

        current = host;
        while (current is not null)
        {
            foreach (var rule in Paths)
            {
                var slashIdx = rule.IndexOf('/');
                if (slashIdx <= 0) continue;
                var ruleHost = rule[..slashIdx];
                var rulePath = rule[slashIdx..];
                // Compare against the CURRENT walked suffix (not the original
                // host) so m.youtube.com/shorts matches a youtube.com/shorts rule.
                if (!current.Equals(ruleHost, StringComparison.OrdinalIgnoreCase)) continue;
                if (PathMatches(path, rulePath))
                {
                    return $"path:{ruleHost}{rulePath}";
                }
            }

            current = NextSuffix(current);
        }

        return null;
    }

    private static bool PathMatches(string path, string rulePath)
    {
        var p = rulePath.Trim().TrimEnd('/');
        if (p.Length == 0) return true;
        if (!path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == p.Length || path[p.Length] == '/';
    }

    private static string? NextSuffix(string host)
    {
        var dot = host.IndexOf('.');
        if (dot < 0 || dot == host.Length - 1) return null;
        return host[(dot + 1)..];
    }

    /// <summary>Strips one canonical "www." prefix — www.youtube.com and youtube.com
    /// are the same site; music.youtube.com is not touched.</summary>
    private static string StripWww(string host)
    {
        var s = host.Trim().ToLowerInvariant();
        return s.StartsWith("www.", StringComparison.Ordinal) ? s[4..] : s;
    }

    private static bool IsHttpUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        (url.Contains("://", StringComparison.Ordinal) is false && url.Contains('.', StringComparison.Ordinal));

    private static string StripScheme(string s)
    {
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return s[7..];
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return s[8..];
        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        return schemeEnd >= 0 ? s[(schemeEnd + 3)..] : s;
    }

    private static bool NormalizeHost(string raw, out string host)
    {
        var s = raw.Trim().ToLowerInvariant();
        if (s.Length == 0 || s.Length > 253 || s.Contains("..", StringComparison.Ordinal))
        {
            host = "";
            return false;
        }

        foreach (var ch in s)
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '.';
            if (!ok)
            {
                host = "";
                return false;
            }
        }

        host = s;
        return true;
    }
}
