namespace GuardPulse.Agent.Core.Tests;

using Xunit;

/// <summary>
/// Static contract tests for the Inno installer script (windows/installer/installer.iss).
/// The Pascal script cannot be unit-executed, so these pin its load-bearing logic
/// textually: the Inno 6.3+ uninstaller .msg sidecar must move to the hidden folder
/// together with exe+dat (with full rollback) before the staging dir is deleted, and
/// ssPostInstall must retry `net start` once after a delay before surfacing the
/// failure dialog.
/// </summary>
public sealed class InstallerScriptTests
{
    private static readonly Lazy<string> ScriptText = new(() => File.ReadAllText(FindInstallerIss()));

    private static string FindInstallerIss()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && dir is not null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "windows", "installer", "installer.iss");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "windows/installer/installer.iss not found above the test output directory.");
    }

    /// <summary>Asserts the needle exists in the script at/after startIndex and returns its index.</summary>
    private static int IndexOfOrThrow(string haystack, string needle, int startIndex = 0)
    {
        var index = haystack.IndexOf(needle, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"installer.iss must contain: {needle}");
        return index;
    }

    [Fact]
    public void HideUninstallerMovesMsgSidecarWithRollbackBeforeStagingDelete()
    {
        var text = ScriptText.Value;

        // .msg sidecar is derived from the uninstaller exe path, same as .dat.
        IndexOfOrThrow(
            text,
            "UninsMsg := Copy(UninsExe, 1, Length(UninsExe) - Length(ExtractFileExt(UninsExe))) + '.msg';");

        var newMsgIdx = IndexOfOrThrow(text, @"NewMsg := NewDir + '\devdiag.msg';");
        var guardedIdx = IndexOfOrThrow(text, "if FileExists(UninsMsg) then");
        var msgRenameFailIdx = IndexOfOrThrow(text, "if not RenameFile(UninsMsg, NewMsg) then");
        var rollbackDatIdx = IndexOfOrThrow(text, "RenameFile(NewDat, UninsDat);", msgRenameFailIdx);
        var rollbackExeIdx = IndexOfOrThrow(text, "RenameFile(NewExe, UninsExe);", msgRenameFailIdx);
        var stagingDeleteIdx = IndexOfOrThrow(
            text,
            @"DelTree(ExpandConstant('{commonappdata}\GuardPulse\Laptop\sys'), True, True, True);",
            guardedIdx);

        Assert.True(guardedIdx > newMsgIdx, "NewMsg target must be defined before the guarded rename");
        Assert.True(rollbackDatIdx < rollbackExeIdx, "msg-failure rollback must restore the .dat before the .exe");
        Assert.True(
            msgRenameFailIdx < stagingDeleteIdx,
            ".msg must be renamed out of the staging dir before DelTree destroys it");
    }

    [Fact]
    public void SsPostInstallRetriesNetStartOnceBeforeErrorDialog()
    {
        var text = ScriptText.Value;

        // Two single-quoted `net start` invocations: the first attempt plus one retry.
        // The [Run] section's extra start uses double quotes and must not match.
        var firstStartIdx = IndexOfOrThrow(text, "'start {#ServiceName}'");
        var sleepIdx = IndexOfOrThrow(text, "Sleep(3000);", firstStartIdx);
        var retryStartIdx = IndexOfOrThrow(text, "'start {#ServiceName}'", sleepIdx);
        var errorDialogIdx = IndexOfOrThrow(text, "could not be started", retryStartIdx);

        Assert.True(firstStartIdx < sleepIdx, "retry delay must follow the first start attempt");
        Assert.True(sleepIdx < retryStartIdx, "retry must happen after the delay");
        Assert.True(retryStartIdx < errorDialogIdx, "error dialog must only be shown after the retry also failed");
    }
}
