// Watchdog: keeps one session agent alive per interactive session.
// Every 5s it enumerates active WTS sessions, checks for a live agent in each and,
// when missing, relaunches it into that session via WTSQueryUserToken + CreateProcessAsUser.
// (This is the correct approach for launching UI from session 0; the installer's HKLM Run
// key is only a logon-time fallback.) Detects agent kills and raises throttled tamper events.

using System.Diagnostics;
using GuardPulse.Protocol;
using Microsoft.Extensions.Logging;

namespace GuardPulse.Agent.Service;

internal sealed class Watchdog : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _agentExePath;
    private readonly string _agentProcessName;
    private readonly object _gate = new();
    private readonly Dictionary<int, int> _agentPidBySession = new();
    private readonly HashSet<int> _sessionsWithAgentSeen = new();
    private long _lastTamperMs;
    private long _lastLaunchFailLogMs;
    private CancellationTokenSource? _cts;

    public Watchdog(ILogger logger, string agentExePath)
    {
        _logger = logger;
        _agentExePath = agentExePath;
        _agentProcessName = Path.GetFileNameWithoutExtension(agentExePath);
    }

    /// <summary>(type, message) - e.g. ("agentKilled", "..."). Throttled to one event per 15 minutes.</summary>
    public event Action<string, string>? TamperDetected;

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = WatchLoopAsync(_cts.Token);
        _logger.LogInformation("Watchdog started for {AgentExe}", Path.GetFileName(_agentExePath));
    }

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        // 10s: halves the wakeups; agent-kill detection latency stays acceptable and
        // service-side enforcement is unaffected by the session gap.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Watchdog tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private void Tick()
    {
        var sessions = NativeMethods.GetInteractiveSessionIds();
        var agents = Process.GetProcessesByName(_agentProcessName);
        try
        {
            foreach (var sessionId in sessions)
            {
                var existing = agents.FirstOrDefault(p =>
                {
                    try
                    {
                        return p.SessionId == sessionId;
                    }
                    catch
                    {
                        return false;
                    }
                });

                if (existing is not null)
                {
                    lock (_gate)
                    {
                        _agentPidBySession[sessionId] = existing.Id;
                        _sessionsWithAgentSeen.Add(sessionId);
                    }

                    continue;
                }

                // No agent in an active session: if we had seen one there before, it was killed -> tamper.
                int? previousPid = null;
                var seenBefore = false;
                lock (_gate)
                {
                    seenBefore = _sessionsWithAgentSeen.Contains(sessionId);
                    previousPid = _agentPidBySession.GetValueOrDefault(sessionId);
                }

                if (seenBefore)
                {
                    OnTamper("agentKilled",
                        $"Session agent for session {sessionId} (pid {previousPid ?? 0}) is no longer running; restarting.");
                }

                var pid = LaunchInSession(sessionId, "");
                if (pid != 0)
                {
                    lock (_gate)
                    {
                        _agentPidBySession[sessionId] = pid;
                        _sessionsWithAgentSeen.Add(sessionId);
                    }
                }
            }

            // Forget sessions that logged off - that is not tamper.
            lock (_gate)
            {
                foreach (var sessionId in _agentPidBySession.Keys.ToList())
                {
                    if (!sessions.Contains(sessionId))
                    {
                        _agentPidBySession.Remove(sessionId);
                        _sessionsWithAgentSeen.Remove(sessionId);
                    }
                }
            }
        }
        finally
        {
            foreach (var agent in agents)
            {
                agent.Dispose();
            }
        }
    }

    /// <summary>PIDs of the session agents this watchdog has launched or observed, so the pipe host can accept only genuine agents.</summary>
    public IReadOnlyCollection<int> KnownAgentPids()
    {
        lock (_gate)
        {
            return _agentPidBySession.Values.Distinct().ToList();
        }
    }

    public int LaunchAgent(string arguments)
    {
        var consoleSession = NativeMethods.WTSGetActiveConsoleSessionId();
        var target = consoleSession;
        if (target <= 0 || !NativeMethods.GetInteractiveSessionIds().Contains(target))
        {
            var active = NativeMethods.GetInteractiveSessionIds();
            target = active.Count > 0 ? active[0] : -1;
        }

        if (target < 0)
        {
            _logger.LogInformation("No interactive session available to launch the agent into");
            return 0;
        }

        return LaunchInSession(target, arguments);
    }

    private int LaunchInSession(int sessionId, string arguments)
    {
        try
        {
            if (!NativeMethods.WTSQueryUserToken(sessionId, out var token))
            {
                LogLaunchFailure($"WTSQueryUserToken failed for session {sessionId} (win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
                return 0;
            }

            try
            {
                NativeMethods.EnablePrivilege("SeAssignPrimaryTokenPrivilege");
                NativeMethods.EnablePrivilege("SeIncreaseQuotaPrivilege");

                var creationFlags = 0;
                var envBlock = IntPtr.Zero;
                if (NativeMethods.CreateEnvironmentBlock(out envBlock, token, false))
                {
                    creationFlags |= NativeMethods.CREATE_UNICODE_ENVIRONMENT;
                }
                else
                {
                    envBlock = IntPtr.Zero;
                }

                var si = new NativeMethods.STARTUPINFOW
                {
                    cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.STARTUPINFOW>(),
                    lpDesktop = "winsta0\\default",
                    dwFlags = NativeMethods.STARTF_USESHOWWINDOW,
                    wShowWindow = NativeMethods.SW_SHOWNORMAL
                };

                var commandLine = "\"" + _agentExePath + "\""
                    + (string.IsNullOrEmpty(arguments) ? "" : " " + arguments);

                if (NativeMethods.CreateProcessAsUserW(
                        token, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                        creationFlags, envBlock, Path.GetDirectoryName(_agentExePath), ref si, out var pi))
                {
                    if (envBlock != IntPtr.Zero)
                    {
                        NativeMethods.DestroyEnvironmentBlock(envBlock);
                    }

                    NativeMethods.CloseHandle(pi.hProcess);
                    NativeMethods.CloseHandle(pi.hThread);
                    _logger.LogInformation("Launched session agent in session {SessionId} (pid {Pid})",
                        sessionId, pi.dwProcessId);
                    return pi.dwProcessId;
                }

                if (envBlock != IntPtr.Zero)
                {
                    NativeMethods.DestroyEnvironmentBlock(envBlock);
                }

                LogLaunchFailure($"CreateProcessAsUser failed for session {sessionId} (win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
                return 0;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        catch (Exception ex)
        {
            LogLaunchFailure($"Agent launch in session {sessionId} threw: {ex.Message}");
            return 0;
        }
    }

    private void LogLaunchFailure(string message)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _lastLaunchFailLogMs < 300_000)
        {
            _logger.LogDebug("{Message}", message);
            return;
        }

        _lastLaunchFailLogMs = now;
        _logger.LogWarning("{Message}. The per-user Run key will retry at next logon.", message);
    }

    private void OnTamper(string type, string message)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _lastTamperMs < PolicyConstants.TAMPER_EVENT_THROTTLE_MS)
        {
            _logger.LogWarning("Tamper signal suppressed (throttled): {Message}", message);
            return;
        }

        _lastTamperMs = now;
        _logger.LogWarning("Tamper event {Type}: {Message}", type, message);
        TamperDetected?.Invoke(type, message);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
