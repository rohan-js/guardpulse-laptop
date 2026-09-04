namespace GuardPulse.Agent.Service;

using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Builds the JSON rule document consumed by the force-installed Site Guard browser
/// extension: blocked whole domains (subdomains implied, www twins included), blocked
/// host+path prefixes, and the extension-relative block page. Inputs are exactly the
/// ones ApplyContentFilterHosts already computes for hosts/registry enforcement.
/// </summary>
public static class SiteBlockRules
{
    public sealed record PathRule(string Host, string Prefix);

    public sealed record RuleDocument(
        [property: JsonPropertyName("domains")] IReadOnlyList<string> Domains,
        [property: JsonPropertyName("paths")] IReadOnlyList<PathRule> Paths,
        [property: JsonPropertyName("blockPageUrl")] string BlockPageUrl);

    /// <summary>Builds the rule document. Pure; unit-tested.</summary>
    public static RuleDocument Build(IEnumerable<string> customEntries, IEnumerable<string> categoryDomains)
    {
        var domains = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new SortedSet<PathRule>(Comparer<PathRule>.Create((a, b) =>
        {
            var c = string.Compare(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Prefix, b.Prefix, StringComparison.OrdinalIgnoreCase);
        }));

        foreach (var raw in customEntries ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var (host, prefix) = SplitHostPrefix(raw);
            if (host is null) continue;
            domains.Add(host);
            if (prefix is not null)
            {
                paths.Add(new PathRule(host, prefix));
            }
        }

        foreach (var raw in categoryDomains ?? Enumerable.Empty<string>())
        {
            var host = NormalizeHost(raw);
            if (host is not null) domains.Add(host);
        }

        return new RuleDocument(domains.ToList(), paths.ToList(), "chrome-extension://SITE/blocked.html");
    }

    /// <summary>Convenience: build + serialize in one call (the host's common path).</summary>
    public static string BuildJson(IEnumerable<string> customEntries, IEnumerable<string> categoryDomains, string extensionOrigin)
    {
        return ToJson(Build(customEntries, categoryDomains), extensionOrigin);
    }

    /// <summary>Serializes to the wire format the extension fetches.</summary>
    public static string ToJson(RuleDocument document, string extensionOrigin)
    {
        var payload = new
        {
            domains = document.Domains,
            paths = document.Paths,
            blockPageUrl = extensionOrigin.TrimEnd('/') + "/blocked.html",
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Splits a scheme-stripped entry into lowercased host and "/path" prefix
    /// (null prefix = whole domain). Mirrors the old BrowserUrlBlocker parsing.</summary>
    public static (string? Host, string? Prefix) SplitHostPrefix(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s[8..];
        else if (s.Contains("://", StringComparison.Ordinal))
        {
            s = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        }

        var slash = s.IndexOf('/');
        var hostPort = slash >= 0 ? s[..slash] : s;
        var path = slash >= 0 ? s[slash..] : null;
        hostPort = hostPort.TrimEnd('.');
        var at = hostPort.LastIndexOf('@');
        if (at >= 0) hostPort = hostPort[(at + 1)..];
        var colon = hostPort.IndexOf(':');
        if (colon >= 0) hostPort = hostPort[..colon];

        var host = NormalizeHost(hostPort);
        if (host is null) return (null, null);

        if (path is null) return (host, null);
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        if (path.Length == 0 || path == "/") return (host, null);
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
