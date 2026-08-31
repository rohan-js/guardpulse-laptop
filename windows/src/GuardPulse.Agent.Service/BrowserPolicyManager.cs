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

        // 2. Apply to Mozilla Firefox
        try
        {
            using var ffBase = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Mozilla\Firefox");
            if (ffBase != null)
            {
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
    /// Bare domains (no path) are returned as-is and are not wildcarded.
    /// </summary>
    public static IReadOnlyList<string> ToBrowserPatterns(string raw)
    {
        var s = ToBrowserPattern(raw);
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        // Only path entries need the extra wildcard: youtube.com/shorts -> {youtube.com/shorts, youtube.com/shorts/*}
        if (!s.Contains('/')) return new[] { s };
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
}
