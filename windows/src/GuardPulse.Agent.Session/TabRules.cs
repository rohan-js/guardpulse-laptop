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
    public IReadOnlyList<string> Domains { get; init; } = new List<string>();
    public IReadOnlyList<string> Paths { get; init; } = new List<string>();

    public static TabRules Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var domains = new List<string>();
            var paths = new List<string>();
            if (root.TryGetProperty("domains", out var d) && d.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in d.EnumerateArray())
                {
                    if (item.GetString() is { Length: > 0 } host) domains.Add(host);
                }
            }

            if (root.TryGetProperty("paths", out var p) && p.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in p.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } path) paths.Add(path);
                }
            }

            return new TabRules { Domains = domains, Paths = paths };
        }
        catch (JsonException)
        {
            return new TabRules();
        }
    }

    public bool IsEmpty => Domains.Count == 0 && Paths.Count == 0;

    /// <summary>Matches an omnibox URL against the rules. Whole-domain rules cover
    /// subdomains (walking parent suffixes); path rules match host + segment-boundary
    /// prefix. Scheme- and case-insensitive. Returns the matched rule or null.</summary>
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

        // Walk host and its parent suffixes: a youtube.com rule covers m./music. too.
        var current = host;
        while (current is not null)
        {
            if (Domains.Contains(current)) return $"host:{current}";
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
                if (!host.Equals(ruleHost, StringComparison.OrdinalIgnoreCase)) continue;
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
