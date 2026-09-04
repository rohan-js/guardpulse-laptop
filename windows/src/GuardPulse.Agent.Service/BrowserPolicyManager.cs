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
    /// Force-installs the Site Guard extension into Chrome/Edge/Brave via enterprise
    /// policy. The extension provides real-time per-tab blocking (SPA navigations and
    /// already-open tabs) that registry URLBlocklist alone cannot cover. updateUrl is
    /// served by the agent's loopback BlocklistServer.
    /// </summary>
    public static void ApplyExtensionForceInstall(string extensionId, string updateUrl)
    {
        if (string.IsNullOrWhiteSpace(extensionId) || string.IsNullOrWhiteSpace(updateUrl)) return;

        foreach (var basePath in ChromiumPolicyPaths)
        {
            try
            {
                using var baseKey = Registry.LocalMachine.CreateSubKey(basePath);
                if (baseKey == null) continue;
                using var listKey = baseKey.CreateSubKey("ExtensionInstallForcelist");
                listKey?.SetValue("1", $"{extensionId};{updateUrl}", RegistryValueKind.String);

                // The CRX download source must be explicitly allowed on some
                // Chromium versions/channels even under forcelist.
                var origin = updateUrl.Substring(0, updateUrl.LastIndexOf('/') + 1);
                using var sourcesKey = baseKey.CreateSubKey("ExtensionInstallSources");
                sourcesKey?.SetValue("1", origin + "*", RegistryValueKind.String);

                // Modern equivalent policy (Brave/Edge quirks): per-extension settings.
                using var settingsKey = baseKey.CreateSubKey("ExtensionSettings");
                using var extKey = settingsKey.CreateSubKey(extensionId);
                extKey?.SetValue("installation_mode", "force_installed", RegistryValueKind.String);
                extKey?.SetValue("update_url", updateUrl, RegistryValueKind.String);
            }
            catch { }
        }
    }
}
