namespace GuardPulse.Agent.Core;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using GuardPulse.Protocol;
using Microsoft.Win32;

/// <summary>One row of the device app inventory (uploaded to devices/{id}/apps).</summary>
/// <param name="AppKey">Stable process key (lowercased full exe path) or a virtual bypass id.</param>
/// <param name="Label">Human-readable display name.</param>
/// <param name="Blockable">Whether the parent can block it.</param>
/// <param name="ProtectedReason">Reason string when not blockable; null otherwise.</param>
/// <param name="BypassRow">True for the five virtual Windows bypass rows (task manager, etc.).</param>
public sealed record InventoryApp(
    string AppKey,
    string Label,
    bool Blockable,
    string? ProtectedReason = null,
    bool BypassRow = false);

/// <summary>
/// Builds the app inventory from Start Menu shortcuts (all users + current user) and registry
/// uninstall entries, plus the five virtual Windows bypass rows. Pure heuristic, best-effort:
/// every source is scanned defensively and failures of individual files/keys are skipped.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InventoryScanner
{
    private static readonly char[] SeparatorChars = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    private static readonly (string Id, string Label)[] BypassRows =
    {
        (PolicyConstants.WINDOWS_TASK_MANAGER_PACKAGE, "Task Manager"),
        (PolicyConstants.WINDOWS_COMMAND_LINE_PACKAGE, "Command Prompt & PowerShell"),
        (PolicyConstants.WINDOWS_REGISTRY_EDITOR_PACKAGE, "Registry Editor"),
        (PolicyConstants.WINDOWS_SETTINGS_PACKAGE, "Windows Settings"),
        (PolicyConstants.WINDOWS_INSTALLERS_PACKAGE, "Installers"),
    };

    /// <summary>
    /// Scans Start Menu .lnk files (recursive) and registry uninstall keys, dedupes by app key
    /// and appends the five bypass virtual rows (BypassRow = true). On non-Windows hosts only
    /// the bypass rows are returned.
    /// </summary>
    public static IReadOnlyList<InventoryApp> Scan()
    {
        var byAppKey = new Dictionary<string, InventoryApp>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            TryAddStartMenuApps(byAppKey);
            TryAddRegistryApps(byAppKey);
            TryAddPerUserAppLocations(byAppKey);
        }

        var result = byAppKey.Values
            .OrderBy(app => app.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.AppKey, StringComparer.Ordinal)
            .Select(app => new InventoryApp(app.AppKey, app.Label, app.Blockable, app.ProtectedReason, app.BypassRow))
            .ToList();

        foreach (var (id, label) in BypassRows)
        {
            result.Add(new InventoryApp(id, label, Blockable: true, BypassRow: true));
        }

        return result;
    }

    /// <summary>Stable process key: lowercased invariant full exe path.</summary>
    public static string AppKeyForProcess(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new ArgumentException("Exe path must not be empty.", nameof(exePath));
        }

        var normalized = exePath.Trim().Trim('"');
        return Path.GetFullPath(normalized).ToLowerInvariant();
    }

    /// <summary>
    /// Maps a process to one of the five virtual bypass ids, or null when the process is not a
    /// bypass target. Installers match msiexec.exe, anything under \Windows\Installer, and
    /// msiexec-spawned setup windows (title contains "Setup"/"Install" — the latter is already
    /// covered by the unconditional msiexec.exe match and kept for documentation).
    /// </summary>
    public static string? MatchBypassRow(string exePath, string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        var normalized = exePath.Trim().Trim('"');
        var fileName = Path.GetFileName(normalized).ToLowerInvariant();
        switch (fileName)
        {
            case "taskmgr.exe":
                return PolicyConstants.WINDOWS_TASK_MANAGER_PACKAGE;
            case "cmd.exe":
            case "powershell.exe":
            case "pwsh.exe":
            case "wt.exe":
            case "windowsterminal.exe":
                return PolicyConstants.WINDOWS_COMMAND_LINE_PACKAGE;
            case "regedit.exe":
            case "regedt32.exe":
                return PolicyConstants.WINDOWS_REGISTRY_EDITOR_PACKAGE;
            case "systemsettings.exe":
            case "control.exe":
                return PolicyConstants.WINDOWS_SETTINGS_PACKAGE;
            case "msiexec.exe":
                // Window-title sniffing ("Setup"/"Install") is subsumed by the msiexec match.
                return PolicyConstants.WINDOWS_INSTALLERS_PACKAGE;
        }

        if (normalized.ToLowerInvariant().Contains("\\windows\\installer\\", StringComparison.Ordinal))
        {
            return PolicyConstants.WINDOWS_INSTALLERS_PACKAGE;
        }

        // Uninstallers: unins000.exe-style (Inno Setup) and the temp copy Inno runs
        // from when the original is locked ("_unins.tmp" under %TEMP%). Without this,
        // an "Installers locked" policy never trips on exactly the exes that remove
        // apps — including this agent's own uninstaller.
        if (fileName.StartsWith("unins", StringComparison.Ordinal) ||
            fileName.StartsWith("_unins", StringComparison.Ordinal))
        {
            return PolicyConstants.WINDOWS_INSTALLERS_PACKAGE;
        }

        return null;
    }

    // ------------------------------------------------------------------ sources

    private static IEnumerable<string> GetUserProfileDirectories()
    {
        var profileDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Read from Registry ProfileList
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (key != null)
            {
                foreach (var subName in key.GetSubKeyNames())
                {
                    if (subName.StartsWith("S-1-5-18", StringComparison.OrdinalIgnoreCase) ||
                        subName.StartsWith("S-1-5-19", StringComparison.OrdinalIgnoreCase) ||
                        subName.StartsWith("S-1-5-20", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var subKey = key.OpenSubKey(subName);
                    if (subKey?.GetValue("ProfileImagePath") is string profilePath && !string.IsNullOrWhiteSpace(profilePath))
                    {
                        var expanded = Environment.ExpandEnvironmentVariables(profilePath);
                        if (Directory.Exists(expanded))
                        {
                            profileDirs.Add(expanded);
                        }
                    }
                }
            }
        }
        catch { }

        // 2. Fallback: Enumerate C:\Users\*
        try
        {
            var usersRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users";
            if (Directory.Exists(usersRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(usersRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (string.Equals(name, "Public", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "Default User", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "All Users", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    profileDirs.Add(dir);
                }
            }
        }
        catch { }

        return profileDirs;
    }

    private static void TryAddStartMenuApps(Dictionary<string, InventoryApp> byAppKey)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var commonStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        if (!string.IsNullOrEmpty(commonStartMenu)) roots.Add(Path.Combine(commonStartMenu, "Programs"));
        var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrEmpty(commonDesktop)) roots.Add(commonDesktop);

        var currentStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        if (!string.IsNullOrEmpty(currentStartMenu)) roots.Add(Path.Combine(currentStartMenu, "Programs"));
        var currentDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(currentDesktop)) roots.Add(currentDesktop);

        foreach (var userDir in GetUserProfileDirectories())
        {
            roots.Add(Path.Combine(userDir, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"));
            roots.Add(Path.Combine(userDir, @"Desktop"));
        }

        foreach (var root in roots)
        {
            List<string> links;
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var link in links)
            {
                var label = Path.GetFileNameWithoutExtension(link);
                var target = ReadLnkTargetPath(link);
                if (!string.IsNullOrEmpty(target))
                {
                    TryAddApp(byAppKey, target, label);
                }
            }
        }
    }

    private static void TryAddRegistryApps(Dictionary<string, InventoryApp> byAppKey)
    {
        var roots = new List<(RegistryKey Hive, string SubKeyPath)>
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        try
        {
            foreach (var sid in Registry.Users.GetSubKeyNames())
            {
                if (sid.StartsWith(".", StringComparison.Ordinal) ||
                    sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase) ||
                    sid.StartsWith("S-1-5-18", StringComparison.OrdinalIgnoreCase) ||
                    sid.StartsWith("S-1-5-19", StringComparison.OrdinalIgnoreCase) ||
                    sid.StartsWith("S-1-5-20", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                roots.Add((Registry.Users, $@"{sid}\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"));
                roots.Add((Registry.Users, $@"{sid}\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"));
            }
        }
        catch { }

        foreach (var (hive, subKeyPath) in roots)
        {
            using var rootKey = hive.OpenSubKey(subKeyPath);
            if (rootKey == null)
            {
                continue;
            }

            string[] subKeyNames;
            try
            {
                subKeyNames = rootKey.GetSubKeyNames();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subKeyName in subKeyNames)
            {
                try
                {
                    using var subKey = rootKey.OpenSubKey(subKeyName);
                    if (subKey == null)
                    {
                        continue;
                    }

                    var displayName = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    var installLocation = subKey.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        var exePath = FindPrimaryExe(installLocation, displayName);
                        if (exePath != null)
                        {
                            TryAddApp(byAppKey, exePath, displayName.Trim());
                            continue;
                        }
                    }

                    var displayIcon = subKey.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrWhiteSpace(displayIcon))
                    {
                        var iconExe = displayIcon.Trim().Trim('"').Split(',')[0].Trim();
                        if (iconExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconExe))
                        {
                            TryAddApp(byAppKey, iconExe, displayName.Trim());
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // skip unreadable entries
                }
            }
        }
    }

    private static void TryAddPerUserAppLocations(Dictionary<string, InventoryApp> byAppKey)
    {
        foreach (var userDir in GetUserProfileDirectories())
        {
            // 1. Roblox: AppData\Local\Roblox\Versions\version-*\RobloxPlayerBeta.exe, RobloxStudioBeta.exe
            try
            {
                var robloxVersions = Path.Combine(userDir, @"AppData\Local\Roblox\Versions");
                if (Directory.Exists(robloxVersions))
                {
                    foreach (var verDir in Directory.EnumerateDirectories(robloxVersions, "version-*"))
                    {
                        var playerExe = Path.Combine(verDir, "RobloxPlayerBeta.exe");
                        if (File.Exists(playerExe))
                        {
                            TryAddApp(byAppKey, playerExe, "Roblox Player");
                        }

                        var studioExe = Path.Combine(verDir, "RobloxStudioBeta.exe");
                        if (File.Exists(studioExe))
                        {
                            TryAddApp(byAppKey, studioExe, "Roblox Studio");
                        }
                    }
                }
            }
            catch { }

            // 2. Per-user Programs directory: AppData\Local\Programs\*
            try
            {
                var localPrograms = Path.Combine(userDir, @"AppData\Local\Programs");
                if (Directory.Exists(localPrograms))
                {
                    foreach (var progDir in Directory.EnumerateDirectories(localPrograms))
                    {
                        var folderName = Path.GetFileName(progDir);
                        var exe = FindPrimaryExe(progDir, folderName);
                        if (exe != null)
                        {
                            TryAddApp(byAppKey, exe, folderName);
                        }
                    }
                }
            }
            catch { }

            // 3. Discord: AppData\Local\Discord\app-*\Discord.exe
            try
            {
                var discordRoot = Path.Combine(userDir, @"AppData\Local\Discord");
                if (Directory.Exists(discordRoot))
                {
                    foreach (var appDir in Directory.EnumerateDirectories(discordRoot, "app-*"))
                    {
                        var discordExe = Path.Combine(appDir, "Discord.exe");
                        if (File.Exists(discordExe))
                        {
                            TryAddApp(byAppKey, discordExe, "Discord");
                        }
                    }
                }
            }
            catch { }

            // 4. Spotify: AppData\Roaming\Spotify\Spotify.exe
            try
            {
                var spotifyExe = Path.Combine(userDir, @"AppData\Roaming\Spotify\Spotify.exe");
                if (File.Exists(spotifyExe))
                {
                    TryAddApp(byAppKey, spotifyExe, "Spotify");
                }
            }
            catch { }
        }
    }

    private static void TryAddApp(Dictionary<string, InventoryApp> byAppKey, string exePath, string label)
    {
        string appKey;
        try
        {
            appKey = AppKeyForProcess(exePath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return;
        }

        if (byAppKey.ContainsKey(appKey) || IsExcludedProcess(appKey))
        {
            return;
        }

        var trimmedLabel = string.IsNullOrWhiteSpace(label) ? Path.GetFileName(appKey) : label.Trim();
        byAppKey[appKey] = new InventoryApp(appKey, trimmedLabel, Blockable: true);
    }

    /// <summary>
    /// Excludes exes inside the Windows directory (System32 cmd.exe/powershell.exe/pwsh.exe are
    /// represented by the "Command Prompt &amp; PowerShell" bypass row instead) and the agent's
    /// own binaries.
    /// </summary>
    private static bool IsExcludedProcess(string lowerAppKey)
    {
        var fileName = Path.GetFileName(lowerAppKey);
        if (fileName.StartsWith("guardpulse.agent.", StringComparison.Ordinal))
        {
            return true;
        }

        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windowsDir))
        {
            var prefix = windowsDir.ToLowerInvariant().TrimEnd(SeparatorChars) + Path.DirectorySeparatorChar;
            if (lowerAppKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>Picks the most plausible main executable of an installed app (top level of its folder).</summary>
    private static string? FindPrimaryExe(string installLocation, string displayName)
    {
        try
        {
            if (!Directory.Exists(installLocation))
            {
                return null;
            }

            var candidates = Directory
                .EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var folderName = Path.GetFileName(installLocation.TrimEnd(SeparatorChars));
            string? match = candidates.FirstOrDefault(exe =>
                    string.Equals(Path.GetFileNameWithoutExtension(exe), folderName, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(exe =>
                    displayName.Contains(Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                return match;
            }

            // Fall back to the largest exe: uninstallers/installers are small, real apps are not.
            return candidates
                .OrderByDescending(exe => new FileInfo(exe).Length)
                .First();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pragmatic LNK parse: no shell-link struct decoding, just a byte-scan for ASCII and UTF-16LE
    /// strings that look like absolute paths to an .exe (LinkInfo's local base path and the
    /// StringData entries are plain strings inside the file). Prefers the first candidate that
    /// exists on disk.
    /// </summary>
    private static string? ReadLnkTargetPath(string lnkPath)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(lnkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Shell link header signature 00 00 00 4C ("L" + size dword).
        if (bytes.Length < 0x4C || bytes[0] != 0x4C || bytes[1] != 0x00 || bytes[2] != 0x00 || bytes[3] != 0x00)
        {
            return null;
        }

        var candidates = ExtractAsciiStrings(bytes, 5);
        // LNK strings are word-aligned in practice; scan both parities to be safe.
        candidates.AddRange(ExtractUtf16Strings(bytes, 5, offset: 0));
        candidates.AddRange(ExtractUtf16Strings(bytes, 5, offset: 1));

        string? firstExe = null;
        foreach (var candidate in candidates)
        {
            if (!LooksLikeExecutablePath(candidate))
            {
                continue;
            }

            firstExe ??= candidate;
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // probing failure: try the next candidate
            }
        }

        return firstExe;
    }

    private static bool LooksLikeExecutablePath(string value)
    {
        if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rooted = value.Length >= 3 &&
            ((value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z') && value[1] == ':' &&
             (value[2] == '\\' || value[2] == '/'))
            || value.StartsWith(@"\\", StringComparison.Ordinal);
        if (!rooted)
        {
            return false;
        }

        return value.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }

    private static List<string> ExtractAsciiStrings(byte[] bytes, int minLength)
    {
        var result = new List<string>();
        var start = -1;
        for (var i = 0; i <= bytes.Length; i++)
        {
            var printable = i < bytes.Length && bytes[i] is >= 0x20 and <= 0x7E;
            if (printable)
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else
            {
                if (start >= 0 && i - start >= minLength)
                {
                    result.Add(Encoding.ASCII.GetString(bytes, start, i - start));
                }

                start = -1;
            }
        }

        return result;
    }

    private static List<string> ExtractUtf16Strings(byte[] bytes, int minLength, int offset)
    {
        var result = new List<string>();
        var start = -1;
        for (var i = offset; i < bytes.Length; i += 2)
        {
            if (i + 1 >= bytes.Length)
            {
                // Trailing half pair: flush any run ending at the last whole character.
                if (start >= 0 && i - start >= minLength * 2)
                {
                    result.Add(Encoding.Unicode.GetString(bytes, start, i - start));
                }

                break;
            }

            var printable = (bytes[i] is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF) && bytes[i + 1] == 0x00;
            if (printable)
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else
            {
                if (start >= 0 && i - start >= minLength * 2)
                {
                    result.Add(Encoding.Unicode.GetString(bytes, start, i - start));
                }

                start = -1;
            }
        }

        return result;
    }
}
