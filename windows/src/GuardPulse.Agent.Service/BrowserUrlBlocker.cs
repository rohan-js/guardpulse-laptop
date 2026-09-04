namespace GuardPulse.Agent.Service;

using System.Collections.Frozen;

/// <summary>
/// Real-time matcher deciding whether a live browser tab URL is blocked by the current
/// content policy. The registry URLBlocklist + hosts-file layers only act at navigation
/// start (and never on SPA history.pushState jumps), so this closes the gap: the agent
/// already receives the active tab URL via UIA within seconds; this turns it into an
/// enforcement signal.
///
/// Inputs mirror ApplyContentFilterHosts exactly:
///  - path rules from the parent's custom blocked sites ("youtube.com/shorts") match the
///    host+path exactly or as a "host/path/*" prefix;
///  - bare custom domains and every enabled built-in category domain match ANY path on
///    that host (belt-and-braces for DNS-over-HTTPS, which bypasses the hosts file).
///
/// Matching is scheme- and case-insensitive; "www." twins of custom entries are implied.
/// Instances are immutable and rebuilt whenever the control snapshot's filter inputs
/// change; <see cref="IsBlocked"/> is pure and called on every browser pipe event.
/// </summary>
public sealed class BrowserUrlBlocker
{
    private const int MaxUrlLength = 2048; // rules cap for browser URL fields

    private readonly FrozenDictionary<string, FrozenSet<string>> _pathRules; // host -> blocked path prefixes
    private readonly FrozenSet<string> _blockedHosts;

    private BrowserUrlBlocker(FrozenDictionary<string, FrozenSet<string>> pathRules, FrozenSet<string> blockedHosts)
    {
        _pathRules = pathRules;
        _blockedHosts = blockedHosts;
    }

    public static BrowserUrlBlocker Empty { get; } = new(
        FrozenDictionary<string, FrozenSet<string>>.Empty,
        FrozenSet<string>.Empty);

    /// <summary>
    /// Builds a blocker from the parent's custom blocked-site entries (with or without
    /// paths) and the enabled built-in category domains (plain hosts).
    /// </summary>
    public static BrowserUrlBlocker Build(IEnumerable<string> customEntries, IEnumerable<string> categoryDomains)
    {
        var pathRules = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in customEntries ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var (host, path) = SplitHostPath(raw);
            if (host is null) continue;
            if (path is null)
            {
                // Bare domain: block every path on the host (and its www twin).
                AddHostWithWww(hosts, host);
                continue;
            }

            // Path rule: ONLY host+path matches — the rest of the host stays open
            // (the whole point of path-level rules like youtube.com/shorts).
            if (!pathRules.TryGetValue(host, out var paths))
            {
                paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                pathRules[host] = paths;
            }

            paths.Add(path);
            // www twin shares the path rule.
            if (!pathRules.TryGetValue("www." + host, out var wwwPaths))
            {
                wwwPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                pathRules["www." + host] = wwwPaths;
            }

            wwwPaths.Add(path);
        }

        foreach (var raw in categoryDomains ?? Enumerable.Empty<string>())
        {
            var host = NormalizeHost(raw);
            if (host is null) continue;
            AddHostWithWww(hosts, host);
        }

        if (pathRules.Count == 0 && hosts.Count == 0)
        {
            return Empty;
        }

        return new BrowserUrlBlocker(
            pathRules.ToFrozenDictionary(
                kvp => kvp.Key,
                kvp => (FrozenSet<string>)kvp.Value.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
            hosts.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Decides whether <paramref name="url"/> (an omnibox URL, best-effort) matches any
    /// blocked host or host+path rule. Returns the matched rule for approval scoping.
    /// </summary>
    public (bool Blocked, string? MatchedRule) IsBlocked(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength)
        {
            return (false, null);
        }

        var (host, path) = SplitHostPath(url);
        if (host is null)
        {
            return (false, null);
        }

        if (_blockedHosts.Contains(host))
        {
            return (true, host);
        }

        // A path rule for "www.host" also covers the apex host and vice versa.
        foreach (var candidate in (string[])[host, StripWww(host)])
        {
            if (candidate is null) continue;
            if (_pathRules.TryGetValue(candidate, out var paths) && path is not null)
            {
                foreach (var rule in paths)
                {
                    if (PathMatches(path, rule))
                    {
                        return (true, candidate + "/" + rule.TrimStart('/'));
                    }
                }
            }
        }

        return (false, null);
    }

    /// <summary>Exact segment-boundary prefix match: "shorts" matches "/shorts" and
    /// "/shorts/abc" but never "/shortsomething". The empty rule matches the bare host path.</summary>
    private static bool PathMatches(string path, string rule)
    {
        var rulePath = rule.Trim().TrimEnd('/');
        if (rulePath.Length == 0)
        {
            return true;
        }

        if (!path.StartsWith(rulePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == rulePath.Length || path[rulePath.Length] == '/';
    }

    private static void AddHostWithWww(HashSet<string> hosts, string host)
    {
        hosts.Add(host);
        // Cover both twins regardless of which form the parent/category supplied.
        var apex = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && host.Length > 4
            ? host[4..]
            : null;
        if (apex is not null)
        {
            hosts.Add(apex);
        }
        else
        {
            hosts.Add("www." + host);
        }
    }

    private static string? StripWww(string host)
    {
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && host.Length > 4 ? host[4..] : null;
    }

    /// <summary>Scheme-stripped, lowercased host + leading-slash path (null when the input
    /// is not host-shaped enough to match). Tolerates a missing path (bare host).</summary>
    private static (string? Host, string? Path) SplitHostPath(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s[8..];
        // Omnibox values are usually scheme-stripped already; tolerate any other scheme.
        else if (s.Contains("://", StringComparison.Ordinal))
        {
            s = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        }

        var slash = s.IndexOf('/');
        var hostPort = slash >= 0 ? s[..slash] : s;
        var path = slash >= 0 ? s[slash..] : null;
        // Query strings never participate in path matching ("/shorts?x" is "/shorts").
        if (path is not null)
        {
            var q = path.IndexOf('?');
            if (q >= 0) path = path[..q];
            if (path.Length > 1) path = path.TrimEnd('/');
        }
        hostPort = hostPort.TrimEnd('.');
        // Drop userinfo/credentials if present; keep the last @-segment (IPv6 literals
        // with ports contain no '@').
        var at = hostPort.LastIndexOf('@');
        if (at >= 0) hostPort = hostPort[(at + 1)..];
        var colon = hostPort.IndexOf(':');
        if (colon >= 0) hostPort = hostPort[..colon];

        var host = NormalizeHost(hostPort);
        if (host is null)
        {
            return (null, null);
        }

        return (host, path);
    }

    private static string? NormalizeHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (s.Length == 0 || s.Length > 253 || s.Contains("..")) return null;
        foreach (var ch in s)
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '.';
            if (!ok) return null;
        }

        return s;
    }
}
