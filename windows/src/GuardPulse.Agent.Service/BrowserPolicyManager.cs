namespace GuardPulse.Agent.Service;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;

/// <summary>
/// Manages enterprise URLBlocklist policies for modern browsers (Chrome, Edge, Brave, Firefox)
/// in HKLM\SOFTWARE\Policies to block granular URL paths (like youtube.com/shorts) natively.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrowserPolicyManager
{
    private static readonly string[] ChromiumPolicyPaths =
    {
        @"SOFTWARE\Policies\Google\Chrome",
        @"SOFTWARE\Policies\Microsoft\Edge",
        @"SOFTWARE\Policies\BraveSoftware\Brave",
    };

    public static void ApplyUrlBlocklist(IEnumerable<string> rawUrls)
    {
        var patterns = rawUrls
            .SelectMany(ToBrowserPatterns)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 1. Apply to Chromium browsers (Chrome, Edge, Brave)
        foreach (var basePath in ChromiumPolicyPaths)
        {
            try
            {
                using var baseKey = Registry.LocalMachine.CreateSubKey(basePath);
                if (baseKey == null) continue;

                // Force the system resolver: with Secure DNS (DoH) enabled the browser
                // resolves names over HTTPS and the hosts-file block is bypassed.
                baseKey.SetValue("DnsOverHttpsMode", "off", RegistryValueKind.String);

                if (patterns.Count == 0)
                {
                    baseKey.DeleteSubKeyTree("URLBlocklist", throwOnMissingSubKey: false);
                    continue;
                }

                using var blockKey = baseKey.CreateSubKey("URLBlocklist");
                if (blockKey == null) continue;

                // Clear old values to avoid stale entries
                foreach (var valName in blockKey.GetValueNames())
                {
                    blockKey.DeleteValue(valName, false);
                }

                // Write entries: 1, 2, 3...
                for (var i = 0; i < patterns.Count; i++)
                {
                    blockKey.SetValue((i + 1).ToString(), patterns[i], RegistryValueKind.String);
                }
            }
            catch { }
        }

        // 2. Apply to Mozilla Firefox (WebsiteFilter + DoH off)
        try
        {
            using var ffBase = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Mozilla\Firefox");
            if (ffBase != null)
            {
                ffBase.SetValue("DNSOverHTTPS", "false", RegistryValueKind.String);
                if (patterns.Count == 0)
                {
                    ffBase.DeleteSubKeyTree("WebsiteFilter", throwOnMissingSubKey: false);
                }
                else
                {
                    using var filterKey = ffBase.CreateSubKey("WebsiteFilter");
                    filterKey?.SetValue("Block", patterns.ToArray(), RegistryValueKind.MultiString);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Chromium URLBlocklist has no implicit wildcards: "youtube.com/shorts" matches only that exact path,
    /// but the user intends the whole subtree, so we emit both the exact entry and "path/*".
    /// Bare domains get "[*.]domain" too so subdomains (m./music.) are covered at navigation start.
    /// </summary>
    public static IReadOnlyList<string> ToBrowserPatterns(string raw)
    {
        var s = ToBrowserPattern(raw);
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        // Only path entries need the extra wildcard: youtube.com/shorts -> {youtube.com/shorts, youtube.com/shorts/*}
        if (!s.Contains('/'))
        {
            return new[] { s, "[*.]" + s, "." + s };
        }
        return s.EndsWith("/*", StringComparison.Ordinal) ? new[] { s } : new[] { s, s + "/*" };
    }

    public static string ToBrowserPattern(string raw)
    {
        var s = raw.Trim();
        if (string.IsNullOrWhiteSpace(s)) return "";

        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7);
        else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8);

        s = s.TrimStart('*', '.', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(s)) return "";

        return s;
    }

    /// <summary>
    /// Builds the per-tab rule set for the session agent's real-time UIA tab-close
    /// enforcement: whole-domain rules (subdomains implied by the agent's suffix walk)
    /// + host/prefix path rules, from the same inputs the hosts file uses.
    /// </summary>
    public static (IReadOnlyList<string> Domains, IReadOnlyList<string> Paths) BuildTabRules(
        IEnumerable<string> customEntries, IEnumerable<string> categoryDomains)
    {
        var domains = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in customEntries ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var entry = raw.Trim();
            if (entry.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) entry = entry[7..];
            else if (entry.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) entry = entry[8..];
            var slash = entry.IndexOf('/');
            var host = (slash >= 0 ? entry[..slash] : entry).TrimEnd('.').TrimStart('.').ToLowerInvariant();
            var prefix = slash >= 0 ? entry[slash..] : "";
            var q = prefix.IndexOf('?');
            if (q >= 0) prefix = prefix[..q];
            if (prefix.Length <= 1) prefix = "";
            if (host.Length == 0 || host.Length > 253 || host.Contains("..", StringComparison.Ordinal)) continue;
            if (!HostCharsValid(host)) continue;
            domains.Add(host);
            if (prefix.Length > 0) paths.Add(host + prefix);
        }

        foreach (var raw in categoryDomains ?? Enumerable.Empty<string>())
        {
            var host = raw.Trim().TrimEnd('.').ToLowerInvariant();
            if (host.Length == 0 || host.Length > 253) continue;
            if (!HostCharsValid(host)) continue;
            domains.Add(host);
        }

        return (domains.ToList(), paths.ToList());
    }

    private static bool HostCharsValid(string host)
    {
        foreach (var ch in host)
        {
            if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '.')) return false;
        }

        return true;
    }
}
