// Fallback enforcement for Windows: suspends processes that belong to a locked app
// via NtSuspendProcess/NtResumeProcess, tracking every pid we suspended so we can
// resume exactly those. Never touches our own processes, session-0 services, or
// critical system processes (explorer, csrss, svchost, ...).

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GuardPulse.Protocol;
using Microsoft.Extensions.Logging;

namespace GuardPulse.Agent.Service;

internal sealed class ProcessSuspender
{
    /// <summary>Process names that must never be suspended (would destabilize the OS/shell).</summary>
    private static readonly HashSet<string> NeverSuspendNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "registry",
        "memcompression",
        "smss.exe",
        "csrss.exe",
        "wininit.exe",
        "winlogon.exe",
        "services.exe",
        "lsass.exe",
        "lsm.exe",
        "svchost.exe",
        "dwm.exe",
        "explorer.exe",
        "conhost.exe",
        "sihost.exe",
        "taskhostw.exe",
        "fontdrvhost.exe",
        "runtimebroker.exe",
        "applicationframehost.exe",
        "shellexperiencehost.exe",
        "startmenuexperiencehost.exe",
        "dllhost.exe",
        "audiodg.exe",
        "spoolsv.exe",
        "searchindexer.exe",
        "ctfmon.exe"
    };

    /// <summary>
    /// Executable names matched for the Windows bypass virtual app ids
    /// (mirrors Core InventoryScanner bypass semantics per CONTRACTS.md).
    /// </summary>
    private static readonly Dictionary<string, string[]> BypassExeNames = new(StringComparer.Ordinal)
    {
        [PolicyConstants.WINDOWS_TASK_MANAGER_PACKAGE] = ["taskmgr.exe"],
        [PolicyConstants.WINDOWS_COMMAND_LINE_PACKAGE] = ["cmd.exe", "powershell.exe", "pwsh.exe", "wt.exe", "windowsterminal.exe"],
        [PolicyConstants.WINDOWS_REGISTRY_EDITOR_PACKAGE] = ["regedit.exe", "regedt32.exe"],
        [PolicyConstants.WINDOWS_SETTINGS_PACKAGE] = ["systemsettings.exe"],
        [PolicyConstants.WINDOWS_INSTALLERS_PACKAGE] = ["msiexec.exe"]
    };

    // Ownership: once a SuspendedProcess is stored in _suspendedByPid, BOTH the safe
    // handle and the Process object belong to the table — they are disposed only in
    // PurgeExited (exited) or after a successful resume (ResumeAll/ExitLockdown).
    // Disposing the Process earlier makes HasExited throw and strands the suspended
    // process frozen forever (resume is then impossible).
    private sealed record SuspendedProcess(string AppKey, Microsoft.Win32.SafeHandles.SafeProcessHandle Handle, Process Process);

    /// <summary>Tag used for processes suspended by the dead-man lockdown (not tied to one app).</summary>
    public const string LockdownTag = "__lockdown__";

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, SuspendedProcess> _suspendedByPid = new();

    public ProcessSuspender(ILogger logger)
    {
        _logger = logger;
    }

    public int SuspendedCount
    {
        get { lock (_gate) return _suspendedByPid.Count; }
    }

    public bool IsSuspended(string appKey)
    {
        lock (_gate)
        {
            return _suspendedByPid.Values.Any(s =>
                string.Equals(s.AppKey, appKey, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Suspends every interactive process belonging to the app. Returns the suspended count.</summary>
    public int SuspendProcessesForApp(string appKey)
    {
        if (string.IsNullOrEmpty(appKey))
        {
            return 0;
        }

        var suspended = 0;
        foreach (var process in Process.GetProcesses())
        {
            // TrySuspend TRANSFERS ownership of `process` to the suspend table when it
            // stores it; only unowned objects may be disposed here. Disposing a stored
            // entry's Process breaks its later resume (HasExited throws after dispose)
            // and strands the app suspended forever.
            var stored = false;
            try
            {
                if (!MatchesApp(process, appKey))
                {
                    continue;
                }

                lock (_gate)
                {
                    if (_suspendedByPid.TryGetValue(process.Id, out var existing))
                    {
                        // Already suspended (e.g. by the lockdown): retag as app-suspended
                        // so a later ExitLockdown does not resume it while still locked.
                        if (!string.Equals(existing.AppKey, appKey, StringComparison.OrdinalIgnoreCase))
                        {
                            _suspendedByPid[process.Id] = existing with { AppKey = appKey };
                        }

                        suspended++;
                        continue;
                    }
                }

                var count = TrySuspend(process, appKey);
                suspended += count;
                stored = count > 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Suspend match failed for pid {Pid}", TryGetId(process));
            }
            finally
            {
                if (!stored) process.Dispose();
            }
        }

        PurgeExited();
        return suspended;
    }

    /// <summary>
    /// Dead-man lockdown: suspends every interactive process that is not system-critical
    /// (shell/OS essentials, anything running from the Windows directory, our own exes).
    /// Used when the session agent disappears while a lock decision is active.
    /// </summary>
    public int SuspendAllForLockdown()
    {
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var suspended = 0;
        foreach (var process in Process.GetProcesses())
        {
            // Same ownership rule as SuspendProcessesForApp: stored entries belong to
            // the suspend table until they are resumed or found exited.
            var stored = false;
            try
            {
                if (process.SessionId == 0)
                {
                    continue; // never touch session-0 services
                }

                var exeName = process.ProcessName + ".exe";
                if (NeverSuspendNames.Contains(exeName)
                    || exeName.StartsWith("GuardPulse.Agent", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var modulePath = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(modulePath)
                        && modulePath.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // OS/shell infrastructure lives in the Windows dir
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // access denied or exited: fall through and suspend by handle
                }

                lock (_gate)
                {
                    if (_suspendedByPid.ContainsKey(process.Id))
                    {
                        continue; // already held (app lock or an earlier lockdown pass)
                    }
                }

                var count = TrySuspend(process, LockdownTag);
                suspended += count;
                stored = count > 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Lockdown suspend failed for pid {Pid}", TryGetId(process));
            }
            finally
            {
                if (!stored) process.Dispose();
            }
        }

        PurgeExited();
        return suspended;
    }

    /// <summary>Resumes only lockdown-tagged processes; app-locked processes stay suspended.</summary>
    public void ExitLockdown()
    {
        List<SuspendedProcess> snapshot;
        lock (_gate) snapshot = _suspendedByPid.Where(kv => kv.Value.AppKey == LockdownTag).Select(kv => kv.Value).ToList();
        var (ok, failed) = ResumeEntriesWithResult(snapshot);
        lock (_gate) foreach (var entry in ok) _suspendedByPid.Remove(entry.Process.Id);
        foreach (var entry in ok) { entry.Handle.Dispose(); entry.Process.Dispose(); }
        if (ok.Count > 0) _logger.LogInformation("Lockdown lifted: resumed {Count} processes", ok.Count);
        if (failed.Count > 0) _logger.LogWarning("ExitLockdown kept {Count} failed pids for retry", failed.Count);
    }

    private int TrySuspend(Process process, string tag)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SUSPEND_RESUME | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            process.Id);
        if (handle == IntPtr.Zero)
        {
            _logger.LogWarning("Could not open pid {Pid} for suspend (win32 error {Error})",
                process.Id, Marshal.GetLastWin32Error());
            return 0;
        }

        var safe = new Microsoft.Win32.SafeHandles.SafeProcessHandle(handle, true);
        var status = NativeMethods.NtSuspendProcess(safe.DangerousGetHandle());
        if (status != 0)
        {
            _logger.LogWarning("NtSuspendProcess failed for pid {Pid} (status 0x{Status:X8})", process.Id, status);
            safe.Dispose();
            return 0;
        }

        lock (_gate)
        {
            _suspendedByPid[process.Id] = new SuspendedProcess(tag, safe, process);
        }

        _logger.LogDebug("Suspended pid {Pid} for {Tag}", process.Id, tag);
        return 1;
    }

    /// <summary>True when at least one live interactive process belongs to the app.</summary>
    public bool HasMatchingProcesses(string appKey)
    {
        if (string.IsNullOrEmpty(appKey))
        {
            return false;
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (MatchesApp(process, appKey))
                    {
                        return true;
                    }
                }
                catch
                {
                    // ignore and continue
                }
            }
        }

        return false;
    }

    /// <summary>Used when suspension fails: terminates the app's processes instead.</summary>
    public void TerminateFallback(string appKey)
    {
        if (string.IsNullOrEmpty(appKey))
        {
            return;
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (!MatchesApp(process, appKey))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    _logger.LogWarning("Terminated pid {Pid} for {AppKey} (suspend fallback)", process.Id, appKey);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    _logger.LogDebug(ex, "Terminate fallback failed for pid {Pid}", TryGetId(process));
                }
            }
        }
    }

    /// <summary>Resumes every process this suspender suspended.</summary>
    public void ResumeAll()
    {
        List<SuspendedProcess> snapshot;
        lock (_gate) snapshot = [.. _suspendedByPid.Values];
        var (ok, failed) = ResumeEntriesWithResult(snapshot);
        lock (_gate)
        {
            foreach (var entry in ok) _suspendedByPid.Remove(entry.Process.Id);
        }
        foreach (var entry in ok) { entry.Handle.Dispose(); entry.Process.Dispose(); }
        if (ok.Count > 0) _logger.LogDebug("Resumed {Count} processes", ok.Count);
        if (failed.Count > 0) _logger.LogWarning("ResumeAll kept {Count} failed pids for retry", failed.Count);
    }

    private void ResumeEntries(List<SuspendedProcess> toResume)
    {
        var (ok, failed) = ResumeEntriesWithResult(toResume);
        lock (_gate) foreach (var entry in ok) _suspendedByPid.Remove(entry.Process.Id);
        foreach (var entry in ok) { entry.Handle.Dispose(); entry.Process.Dispose(); }
    }

    private (List<SuspendedProcess> ok, List<SuspendedProcess> failed) ResumeEntriesWithResult(List<SuspendedProcess> toResume)
    {
        var ok = new List<SuspendedProcess>();
        var failed = new List<SuspendedProcess>();
        foreach (var entry in toResume)
        {
            var resumed = false;
            try
            {
                if (entry.Process.HasExited)
                {
                    resumed = true;
                }
                else
                {
                    var status = NativeMethods.NtResumeProcess(entry.Handle.DangerousGetHandle());
                    if (status == 0) resumed = true;
                    else _logger.LogWarning("NtResumeProcess failed for pid {Pid} (status 0x{Status:X8})", entry.Process.Id, status);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Resume failed for pid {Pid}", entry.Process.Id);
            }
            if (resumed) ok.Add(entry);
            else failed.Add(entry);
        }
        return (ok, failed);
    }

    private static bool MatchesApp(Process process, string appKey)
    {
        try
        {
            if (process.SessionId == 0)
            {
                return false; // never touch session-0 services
            }

            var exeName = process.ProcessName + ".exe";
            if (NeverSuspendNames.Contains(exeName))
            {
                return false;
            }

            // Self-exemption: never lock our own agent/service.
            if (exeName.StartsWith("GuardPulse.Agent", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? modulePath = null;
            try
            {
                modulePath = process.MainModule?.FileName;
            }
            catch (Win32Exception)
            {
                // access denied (elevated/protected process) - only name-based bypass matching remains
            }
            catch (InvalidOperationException)
            {
                // process exited between enumeration and query
            }

            if (!string.IsNullOrEmpty(modulePath))
            {
                if (string.Equals(modulePath, appKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else
            {
                exeName = process.ProcessName.ToLowerInvariant() + ".exe";
            }

            if (BypassExeNames.TryGetValue(appKey, out var bypassExes))
            {
                var filePart = string.IsNullOrEmpty(modulePath)
                    ? exeName
                    : Path.GetFileName(modulePath);
                if (bypassExes.Contains(filePart, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private void PurgeExited()
    {
        List<int> dead = [];
        lock (_gate)
        {
            foreach (var (pid, entry) in _suspendedByPid)
            {
                try
                {
                    if (entry.Process.HasExited)
                    {
                        dead.Add(pid);
                    }
                }
                catch
                {
                    dead.Add(pid);
                }
            }

            foreach (var pid in dead)
            {
                if (_suspendedByPid.Remove(pid, out var entry))
                {
                    entry.Handle.Dispose();
                    entry.Process.Dispose();
                }
            }
        }
    }

    private static int TryGetId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return -1;
        }
    }
}
