// Watchdog: keeps one session agent alive per interactive session.
// Every 2s - or immediately when the pipe host reports an agent disconnect (KickNow) -
// it enumerates active WTS sessions, checks for a live agent in each and, when missing,
// relaunches it into that session via WTSQueryUserToken + CreateProcessAsUser.
// (This is the correct approach for launching UI from session 0; the installer's HKLM Run
// key is only a logon-time fallback.) Detects agent kills and raises throttled tamper events.

using System.Diagnostics;
using GuardPulse.Agent.Core;
using GuardPulse.Protocol;
using Microsoft.Extensions.Logging;

namespace GuardPulse.Agent.Service;

internal sealed class Watchdog : IDisposable
{
    // 2s: an End Task on the agent must cost the child at most ~2s of monitoring gap on
    // the periodic path alone; the pipe-disconnect kick below cuts the typical case to <1s.
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KickDebounce = TimeSpan.FromMilliseconds(500);

    private readonly ILogger _logger;
    private readonly string _agentExePath;
    private readonly string _agentProcessName;
    private readonly object _gate = new();
    private readonly object _kickGate = new();
    private readonly Dictionary<int, int> _agentPidBySession = new();
    private long _lastTamperMs;
    private long _lastLaunchFailLogMs;
    private DateTime _lastKickAtUtc = DateTime.MinValue;
    private TaskCompletionSource<bool> _kick = NewKickSource();
    private bool _disposed;
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

    /// <summary>
    /// Requests an immediate revival scan instead of waiting for the next periodic tick.
    /// Called when a pipe client drops - an End Task on the agent surfaces as an instant
    /// transport drop - so the kill is undone in well under the scan interval. Debounced
    /// to at most one extra scan per 500 ms.
    /// </summary>
    public void KickNow()
    {
        TaskCompletionSource<bool>? kick;
        lock (_kickGate)
        {
            if (_disposed)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastKickAtUtc < KickDebounce)
            {
                return;
            }

            _lastKickAtUtc = now;
            kick = _kick;
            _kick = NewKickSource();
        }

        kick.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewKickSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        // Plain Task.Delay rather than PeriodicTimer: a kick must be able to supersede the
        // pending wait, and PeriodicTimer forbids overlapping WaitForNextTickAsync calls.
        while (!ct.IsCancellationRequested)
        {
            var scanDelay = Task.Delay(ScanInterval, CancellationToken.None);
            Task kickTask;
            lock (_kickGate)
            {
                kickTask = _kick.Task;
            }

            await Task.WhenAny(scanDelay, kickTask);
            if (ct.IsCancellationRequested)
            {
                break;
            }

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

    private void Tick()
    {
        var sessions = NativeMethods.GetInteractiveSessionIds();
        var agents = Process.GetProcessesByName(_agentProcessName);
        try
        {
            var livePidBySession = new Dictionary<int, int>();
            foreach (var agent in agents)
            {
                try
                {
                    livePidBySession.TryAdd(agent.SessionId, agent.Id);
                }
                catch
                {
                    // process may exit between enumeration and read
                }
            }

            Dictionary<int, int> tracked;
            lock (_gate)
            {
                tracked = new Dictionary<int, int>(_agentPidBySession);
            }

            var plan = AgentRevivalPlanner.Plan(sessions, livePidBySession, tracked);

            foreach (var (sessionId, previousPid) in plan.KilledSessions)
            {
                OnTamper("agentKilled",
                    $"Session agent for session {sessionId} (pid {previousPid}) is no longer running; restarting.");
            }

            foreach (var sessionId in plan.LaunchSessions)
            {
                var pid = LaunchInSession(sessionId, "");
                if (pid != 0)
                {
                    lock (_gate)
                    {
                        _agentPidBySession[sessionId] = pid;
                    }
                }
            }

            foreach (var (sessionId, pid) in plan.RefreshSessions)
            {
                lock (_gate)
                {
                    _agentPidBySession[sessionId] = pid;
                }
            }

            // Forget sessions that logged off - that is not tamper.
            foreach (var sessionId in plan.ForgetSessions)
            {
                lock (_gate)
                {
                    _agentPidBySession.Remove(sessionId);
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
        lock (_kickGate)
        {
            _disposed = true;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
