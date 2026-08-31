using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace GuardPulse.Agent.Session;

/// <summary>
/// Reports the foreground process to the service: instant WinEvent hook
/// notifications plus a 3s GetForegroundWindow polling fallback. Bypass-tool
/// executables are mapped to their virtual app keys so the service never
/// needs process details to decide on them. ForegroundChanged is always raised
/// on the WPF dispatcher (the polling timer runs on a ThreadPool thread), so
/// subscribers can touch UI state safely.
/// </summary>
public sealed class ForegroundHook : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private readonly PipeClient _pipe;
    // The WinEvent hook is the instant, primary foreground signal; this poll is only
    // a safety net for missed hook events, so it can idle slowly.
    private readonly System.Timers.Timer _poll = new(TimeSpan.FromSeconds(10));
    private readonly nint _hook;
    private readonly WinEventProc _proc; // keep delegate alive
    private readonly Dispatcher _dispatcher;
    private readonly object _stateGate = new();
    private string? _lastAppKey;

    /// <summary>App key of the most recent foreground report ("__agent__" for our own UI); used by the service-offline fallback lock.</summary>
    public string? LastReportedAppKey { get { lock (_stateGate) return _lastAppKey; } }

    /// <summary>Raised on the WPF dispatcher for every accepted foreground change (after dedupe), with the app key ("__agent__" for our own UI).</summary>
    public event Action<string>? ForegroundChanged;

    public ForegroundHook(PipeClient pipe)
    {
        _pipe = pipe;
        _dispatcher = Dispatcher.CurrentDispatcher; // constructed on the UI thread
        _proc = OnWinEvent;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            nint.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _poll.Elapsed += (_, _) => PollOnce();
        _poll.Start();
        // After a reconnect the service no longer knows the current foreground
        // app (its dedupe would swallow the repeat), so force one fresh report.
        _pipe.Connected += ResetDedupe;
        PollOnce();
    }

    private void ResetDedupe()
    {
        lock (_stateGate) _lastAppKey = null;
    }

    private void OnWinEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        Report(hwnd);
    }

    private void PollOnce()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd != nint.Zero) Report(hwnd);
    }

    private void Report(nint hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId)
            {
                ReportOwnWindow();
                return;
            }

            // A dead process's window can linger as "foreground" and would pin
            // tracking forever; skip it — the next live foreground reports.
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return; // pid already recycled away
            }

            var exePath = GetProcessImagePath(pid);
            if (string.IsNullOrEmpty(exePath))
            {
                ReportOwnWindow();
                return;
            }

            var exeName = Path.GetFileName(exePath).ToLowerInvariant();
            if (exeName is "guardpulse.agent.session.exe" or "guardpulse.agent.service.exe")
            {
                ReportOwnWindow();
                return;
            }

            var windowTitle = GetWindowTitle(hwnd);
            var bypass = MapBypassRow(exeName, windowTitle);
            var appKey = bypass ?? exePath.ToLowerInvariant();
            lock (_stateGate)
            {
                if (appKey == _lastAppKey) return;
                _lastAppKey = appKey;
            }

            _pipe.SendForeground(appKey, exePath, windowTitle);
            // Subscribers touch WPF state; the polling timer fires on a ThreadPool
            // thread, so always deliver through the UI dispatcher.
            _dispatcher.BeginInvoke(() => ForegroundChanged?.Invoke(appKey));
        }
        catch (Exception)
        {
            // transient failures are fine; poll will retry
        }
    }

    private void ReportOwnWindow()
    {
        const string own = "__agent__";
        lock (_stateGate)
        {
            if (_lastAppKey == own) return;
            _lastAppKey = own;
        }

        _pipe.SendForeground(own, Environment.ProcessPath ?? "", null);
    }

    internal static string? MapBypassRow(string exeName, string? windowTitle) => exeName switch
    {
        "taskmgr.exe" => "guardpulse.windows.taskmgr",
        "cmd.exe" or "powershell.exe" or "pwsh.exe" or "wt.exe" or "windowsterminal.exe" or "conhost.exe"
            when exeName != "conhost.exe" || IsConsoleShell(windowTitle)
            => "guardpulse.windows.commandline",
        "regedit.exe" or "regedt32.exe" => "guardpulse.windows.registry",
        "systemsettings.exe" or "control.exe" => "guardpulse.windows.settings",
        "msiexec.exe" => "guardpulse.windows.installers",
        _ => null
    };

    // A conhost window belongs to a console shell only when its title actually names
    // one — a bare "cmd" substring would lock any console app whose title happens
    // to contain it (e.g. "mycmd-tool").
    private static bool IsConsoleShell(string? title) =>
        title is not null && (
            title.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("command prompt", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("terminal", StringComparison.OrdinalIgnoreCase));

    internal static string? GetProcessImagePath(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInfo, false, pid);
        if (handle == nint.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    internal static string? GetWindowTitle(nint hwnd)
    {
        var buffer = new StringBuilder(512);
        var length = GetWindowText(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : null;
    }

    public void Dispose()
    {
        _poll.Stop();
        _poll.Dispose();
        if (_hook != nint.Zero) UnhookWinEvent(_hook);
    }

    private const uint ProcessQueryLimitedInfo = 0x1000;

    private delegate void WinEventProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint mod, WinEventProc proc,
        uint pid, uint tid, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint process, int flags,
        StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
