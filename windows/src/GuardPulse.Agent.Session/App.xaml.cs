using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace GuardPulse.Agent.Session;

public partial class App : Application
{
    private const string MutexName = "GuardPulse.Agent.Session.SingleInstance";
    private const long ServiceDeadGraceMs = 5_000;

    private Mutex? _mutex;
    private PipeClient? _pipe;
    private ForegroundHook? _hook;
    private BrowserWatcher? _browserWatcher;
    private TrayHost? _tray;
    private LockWindow? _lockWindow;
    // Apps the SERVICE explicitly locked (only ever filled by a "lock" pipe
    // message; only ever cleared by an "unlock" message). While the pipe is
    // connected this set is the ONLY lock authority — never seed it from the
    // policy cache: a seeded entry would wall an app the service never locked,
    // and because the service only broadcasts unlock when IT was locked, that
    // wall could never be lifted (the "random lock screen" incident).
    private readonly HashSet<string> _serviceLockedApps = new(StringComparer.OrdinalIgnoreCase);
    // Offline fail-closed seed (cache blocked lists + bypass tools), consulted
    // ONLY while the pipe is dead past the grace period.
    private readonly HashSet<string> _fallbackLockedApps = new(StringComparer.OrdinalIgnoreCase);
    // Most recent app the SERVICE locked (survives HoldDesktop, where the wall's
    // display key is empty). Unlock restores/removes THIS key.
    private string? _lastLockedAppKey;
    private long _suppressLockUntilTick;

    // Pin state from the service; remembered so a lazily created LockWindow gets the
    // latest values even if the pinState broadcast arrived before its first lock.
    private bool _pinConfigured;
    private long _pinBlockedUntilMs;

    // Service-offline fallback: when the pipe stays dead past the grace period the
    // agent locks rule-blocked apps on its own from the service's policy cache
    // (fail closed; no PIN verification is possible offline).
    private readonly DispatcherTimer _serviceWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _pipeDeadSinceTicks;
    private bool _fallbackLockVisible;
    // Named Connected/pipe handlers so OnExit can unsubscribe them (Connected
    // handlers otherwise keep App alive via the pipe's event after teardown).
    private Action? _onPipeConnectedAdmin;
    private Action? _onPipeConnectedTabRules;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Offline-only seed: the fail-closed fallback (pipe dead >5s) locks these
        // on its own. The connected verdict path NEVER consults this set.
        var initCache = PolicyCache.Load();
        if (initCache is not null)
        {
            foreach (var b in initCache.BlockedApps) _fallbackLockedApps.Add(b);
            foreach (var d in initCache.DailyBlockedApps) _fallbackLockedApps.Add(d);
            foreach (var s in initCache.SessionBlockedApps) _fallbackLockedApps.Add(s);
        }
        foreach (var b in PolicyCache.BypassAppKeys) _fallbackLockedApps.Add(b);

        _pipe = new PipeClient();
        _pipe.MessageReceived += OnPipeMessage;
        _onPipeConnectedAdmin = ReportAdminState;
        _onPipeConnectedTabRules = () => _ = RequestTabRulesAsync();
        _pipe.Connected += _onPipeConnectedAdmin;
        // After every (re)connect, ask for the current blocked-site rules — without
        // this a restarted session agent runs with empty rules until the next
        // control apply on the service side.
        _pipe.Connected += _onPipeConnectedTabRules;
        // On (re)connect the service's hello handler immediately re-broadcasts the
        // current verdict (lock or unlock), so service-sourced state is rebuilt
        // from authority; clear any stale service-locked keys instead of keeping
        // them (a kept key would wall an app whose unlock message was missed).
        _pipe.Connected += () => Dispatcher.Invoke(_serviceLockedApps.Clear);
        _pipe.Start();

        _hook = new ForegroundHook(_pipe);
        // Best-effort browser tab visibility: never touches protection state, and a
        // UIA/pipe failure inside the watcher is swallowed there.
        _browserWatcher = new BrowserWatcher(_pipe, _hook);
        _hook.ForegroundChanged += appKey =>
        {
            if (appKey == "__agent__") return;
            // Desktop-suppression for re-assert loops: while the service holds a
            // lock verdict, shell minimize gestures momentarily bring the blocked
            // app forward; suppressing local re-locks briefly avoids wall flicker
            // while the service state (the sole unlock authority) settles.
            if (Environment.TickCount64 < _suppressLockUntilTick) return;

            // While the pipe is up the service owns lock verdicts: the wall
            // follows the service's lock/unlock messages via _serviceLockedApps
            // (the sole unlock authority). The offline policy cache drives the
            // wall only while the pipe is dead (fail closed).
            var serviceOwnsVerdict = _pipe?.IsConnected == true;
            string? blockedReason;
            if (serviceOwnsVerdict)
            {
                blockedReason = _serviceLockedApps.Contains(appKey)
                    || _serviceLockedApps.Contains(System.IO.Path.GetFileName(appKey))
                    ? "manual" : null;
            }
            else
            {
                var cache = PolicyCache.Load();
                // Null cache = UNKNOWN policy state (service never wrote one):
                // bypass tools stay locked (fail closed), everything else waits
                // for the service instead of guessing from an empty list.
                blockedReason = cache?.BlockedReasonFor(appKey) ?? PolicyCache.UnknownReasonFor(appKey);
            }

            var isBlocked = blockedReason != null;
            if (isBlocked)
            {
                // Echo the blocked key into the set the unlock path clears so a
                // service-placed lock and its unlock always round-trip, even when
                // the wall was re-shown by the offline fallback in between.
                _serviceLockedApps.Add(appKey);
            }
            else
            {
                _serviceLockedApps.Remove(appKey);
                _serviceLockedApps.Remove(System.IO.Path.GetFileName(appKey));
            }

            if (isBlocked)
            {
                if (_lockWindow is { IsVisible: true } && appKey == _lockWindow.CurrentAppKey)
                {
                    // Synchronous re-assert: this hook callback runs on the WinEvent
                    // thread, and an async BeginInvoke would lose the race against a
                    // process fighting for foreground — the wall must be forward
                    // before we return. Dispatcher.Invoke marshals to the WPF thread
                    // and blocks here until it is done.
                    try
                    {
                        Dispatcher.Invoke(() => _lockWindow?.Reassert());
                    }
                    catch (Exception)
                    {
                        // dispatcher shut down mid-switch; the next hook event retries
                    }
                }
                else
                {
                    var oldLockedKey = _lockWindow?.CurrentAppKey;
                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (!string.IsNullOrEmpty(oldLockedKey) && oldLockedKey != appKey && _lockWindow is { IsVisible: true })
                            {
                                _lockWindow?.MinimizeBlockedApp(oldLockedKey);
                            }

                            EnsureLockWindow().ShowFor(appKey, PolicyCache.LabelFor(appKey), blockedReason ?? "manual");
                        });
                    }
                    catch (Exception)
                    {
                        // dispatcher shut down mid-switch; the next hook event retries
                    }
                }
            }
            else if (_lockWindow is { IsVisible: true } && appKey != _lockWindow.CurrentAppKey)
            {
                // App switch while the wall is up: NEVER lift/hide/resume here.
                // The wall lifts ONLY on an explicit service "unlock" pipe
                // message. An allowed app in front just switches the wall to a
                // full-desktop hold (keeps CoverVirtualDesktop refreshed for the
                // whole virtual desktop); Minimizing the blocked app is still
                // fine because it does not touch the wall itself.
                var lockedKey = _lockWindow.CurrentAppKey;
                var lockedAppSuspended = _serviceLockedApps.Contains(lockedKey)
                    || (!string.IsNullOrEmpty(lockedKey) && _serviceLockedApps.Contains(System.IO.Path.GetFileName(lockedKey)));
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_lockWindow is not { IsVisible: true } || appKey == _lockWindow.CurrentAppKey)
                        {
                            return;
                        }

                        if (lockedAppSuspended)
                        {
                            _lockWindow.HoldDesktop();
                        }
                        else
                        {
                            // Previously-named app is no longer service-locked:
                            // stop naming it, but do NOT hide — only "unlock" hides.
                            _lockWindow.HoldDesktop();
                        }                    });
                }
                catch (Exception)
                {
                    // dispatcher shut down mid-switch
                }
            }
        };

        _tray = new TrayHost(ShowSetup, () =>
        {
            if (_lockWindow is { IsVisible: true }) return;
            _tray?.Dispose();
            Shutdown();
        });

        _serviceWatch.Tick += OnServiceWatchTick;
        _serviceWatch.Start();

        // Resident-footprint hygiene: trim once the WPF UI has settled.
        var trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        trimTimer.Tick += (_, _) =>
        {
            trimTimer.Stop();
            try
            {
                _ = SetProcessWorkingSetSize(
                    System.Diagnostics.Process.GetCurrentProcess().Handle, unchecked((nuint)(-1)), unchecked((nuint)(-1)));
            }
            catch (Exception)
            {
                // best effort only
            }
        };
        trimTimer.Start();

        if (e.Args.Contains("--setup", StringComparer.OrdinalIgnoreCase))
        {
            ShowSetup();
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(nint process, nuint min, nuint max);

    /// <summary>
    /// Creates the lock window on first use. WPF windows are expensive to keep resident,
    /// and an idle device may never lock — deferring creation trims startup memory.
    /// </summary>
    private LockWindow EnsureLockWindow()
    {
        if (_lockWindow == null)
        {
            _lockWindow = new LockWindow(_pipe!);
            _lockWindow.DesktopMinimized += OnWallDesktopMinimized;
            _lockWindow.UpdatePinState(_pinConfigured, _pinBlockedUntilMs);
        }

        return _lockWindow;
    }

    private void OnWallDesktopMinimized()
    {
        // 60s desktop-suppression for re-assert loops, based on service state:
        // only suppress while the service still holds this app locked (known
        // verdict); an unlocked app must re-lock immediately on next signal.
        var key = _lockWindow?.CurrentAppKey;
        if (!string.IsNullOrEmpty(key) && _serviceLockedApps.Contains(key))
        {
            _suppressLockUntilTick = Environment.TickCount64 + 60_000;
        }
        else
        {
            _suppressLockUntilTick = Environment.TickCount64 + 600;
        }
    }

    private void OnPipeMessage(System.Text.Json.JsonElement message)
    {
        var type = message.TryGetProperty("t", out var t) ? t.GetString() : null;
        Dispatcher.Invoke(() =>
        {
            switch (type)
            {
                case "lock":
                    var lockKey = message.TryGetProperty("appKey", out var lk) ? lk.GetString() ?? "" : "";
                    var label = message.TryGetProperty("appLabel", out var lb) ? lb.GetString() ?? "" : "";
                    var reason = message.TryGetProperty("reason", out var rs) ? rs.GetString() : null;
                    if (!string.IsNullOrEmpty(lockKey))
                    {
                        _serviceLockedApps.Add(lockKey);
                        _lastLockedAppKey = lockKey;
                        EnsureLockWindow().ShowFor(lockKey, label, reason);
                    }
                    break;
                case "unlock":
                    // HoldDesktop empties CurrentAppKey to "" after an app switch —
                    // ?? only catches null, so without the empty-check the unlock
                    // would drop NO key: the blocked app stays in _serviceLockedApps
                    // and the next focus re-walls it with no service lock behind it.
                    var unlockedKey = _lockWindow?.CurrentAppKey;
                    if (string.IsNullOrEmpty(unlockedKey)) unlockedKey = _lastLockedAppKey;
                    _lastLockedAppKey = null;
                    _lockWindow?.HideLock();
                    _lockWindow?.RestoreAllMinimized();
                    if (!string.IsNullOrEmpty(unlockedKey))
                    {
                        _serviceLockedApps.Remove(unlockedKey);
                        _serviceLockedApps.Remove(System.IO.Path.GetFileName(unlockedKey));
                        _lockWindow?.RestoreMinimized(unlockedKey);
                        _lockWindow?.ClearMinimized();
                    }

                    // A broadcast unlock with no tracked key still clears the wall
                    // (service restart edge: hello decided unlocked). Everything the
                    // service had locked is void now.
                    _serviceLockedApps.Clear();
                    break;
                case "pinState":
                    _pinConfigured = message.TryGetProperty("configured", out var configured) && configured.GetBoolean();
                    _pinBlockedUntilMs = message.TryGetProperty("blockedUntilMs", out var blocked) ? blocked.GetInt64() : 0;
                    _lockWindow?.UpdatePinState(_pinConfigured, _pinBlockedUntilMs);
                    break;
                case "openSetup":
                    ShowSetup();
                    break;
                case "pairedState":
                    // Paired: the tray icon disappears (the parent app drives everything);
                    // unpaired: it returns so the pairing code is reachable.
                    _tray?.SetPaired(message.TryGetProperty("paired", out var pairedEl) && pairedEl.GetBoolean());
                    break;
                case "warningToast":
                    var toastTitle = message.TryGetProperty("title", out var tt) ? tt.GetString() ?? "GuardPulse Screen Time" : "GuardPulse Screen Time";
                    var toastMsg = message.TryGetProperty("message", out var tm) ? tm.GetString() ?? "" : "";
                    ToastWindow.ShowToast(toastTitle, toastMsg);
                    break;
                case "tabRules":
                {
                    var domains = new List<string>();
                    var paths = new List<string>();
                    if (message.TryGetProperty("domains", out var td) && td.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in td.EnumerateArray())
                        {
                            if (item.GetString() is { Length: > 0 } dm) domains.Add(dm);
                        }
                    }

                    if (message.TryGetProperty("paths", out var tp) && tp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in tp.EnumerateArray())
                        {
                            if (item.GetString() is { Length: > 0 } pp) paths.Add(pp);
                        }
                    }

                    _browserWatcher?.SetTabRules(new TabRules { Domains = domains, Paths = paths });
                    break;
                }
                case "showMessage":
                    var parentText = message.TryGetProperty("text", out var mt) ? mt.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(parentText))
                    {
                        ToastWindow.ShowToast("Message from Parent", parentText, displaySeconds: 12);
                    }

                    break;
            }
        });
    }
    /// <summary>Asks the service for the current blocked-site rules (on pipe
    /// connect) and applies them; keeps tab enforcement warm across restarts.</summary>
    private async Task RequestTabRulesAsync()
    {
        try
        {
            var reply = await _pipe.SendRequestAsync("tabRulesGet", new { }, TimeSpan.FromSeconds(4));
            if (reply is not System.Text.Json.JsonElement wrapped
                || !wrapped.TryGetProperty("payload", out var payload)) return;
            var domains = new List<string>();
            var paths = new List<string>();
            if (payload.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (payload.TryGetProperty("domains", out var td) && td.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in td.EnumerateArray())
                    {
                        if (item.GetString() is { Length: > 0 } dm) domains.Add(dm);
                    }
                }

                if (payload.TryGetProperty("paths", out var tp) && tp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in tp.EnumerateArray())
                    {
                        if (item.GetString() is { Length: > 0 } pp) paths.Add(pp);
                    }
                }
            }

            Dispatcher.Invoke(() => _browserWatcher?.SetTabRules(new TabRules { Domains = domains, Paths = paths }));
        }
        catch
        {
            // service busy/unavailable; the next control apply re-broadcasts anyway
        }
    }


    /// <summary>
    /// Reports whether this logon account is an administrator; the service warns the parent
    /// (an admin child can defeat local protection). A UAC-filtered token hides the
    /// Administrators SID from WindowsIdentity.Groups, so the token's elevation type is the
    /// honest signal: Full or Limited both mean the account can elevate.
    /// </summary>
    private void ReportAdminState()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            _pipe?.SendAdminState(GetElevationType(identity.Token) is 2 or 3);
        }
        catch (Exception)
        {
            // identity query failed: skip this report
        }
    }

    private const int TokenElevationTypeClass = 18; // TOKEN_INFORMATION_CLASS

    private static int GetElevationType(nint token)
    {
        try
        {
            return GetTokenInformation(token, TokenElevationTypeClass, out var value, sizeof(int), out _)
                ? value
                : 1; // TokenElevationTypeDefault: assume a standard account
        }
        catch (Exception)
        {
            return 1;
        }
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    private void OnServiceWatchTick(object? sender, EventArgs e)
    {
        if (_pipe is null)
        {
            return;
        }

        if (_pipe.IsConnected)
        {
            _pipeDeadSinceTicks = 0;
            // The service owns the overlay while connected (its hello handler and
            // 10s re-evaluation re-broadcast the current decision); stop managing
            // the fallback lock but leave any visible window to the service.
            _fallbackLockVisible = false;
            return;
        }

        var now = Environment.TickCount64;
        if (_pipeDeadSinceTicks == 0)
        {
            _pipeDeadSinceTicks = now;
            return;
        }

        if (now - _pipeDeadSinceTicks < ServiceDeadGraceMs)
        {
            return;
        }

        var appKey = _hook?.LastReportedAppKey;
        if (string.IsNullOrEmpty(appKey) || appKey == "__agent__")
        {
            return;
        }

        var cache = PolicyCache.Load();
        var reason = cache?.BlockedReasonFor(appKey) ?? PolicyCache.UnknownReasonFor(appKey);
        if (reason is null)
        {
            // Foreground moved to an allowed app while offline: only ever lift a
            // lock this fallback placed — service-placed locks stay untouched.
            if (_fallbackLockVisible)
            {
                _fallbackLockVisible = false;
                _lockWindow.HideLock();
            }

            return;
        }

        _fallbackLockVisible = true;
        EnsureLockWindow().ShowFor(appKey, PolicyCache.LabelFor(appKey), reason);
    }

    private void ShowSetup()
    {
        var existing = Windows.OfType<SetupWindow>().FirstOrDefault();
        if (existing != null)
        {
            existing.Activate();
            return;
        }
        var setup = new SetupWindow(_pipe!);
        setup.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceWatch.Stop();
        if (_pipe is not null)
        {
            _pipe.MessageReceived -= OnPipeMessage;
            if (_onPipeConnectedAdmin is not null) _pipe.Connected -= _onPipeConnectedAdmin;
            if (_onPipeConnectedTabRules is not null) _pipe.Connected -= _onPipeConnectedTabRules;
        }

        _pipe?.SendSetupClosed();
        _lockWindow?.Teardown();
        _browserWatcher?.Dispose();
        _hook?.Dispose();
        _pipe?.Dispose();
        _tray?.Dispose();
        try
        {
            // The second-instance path exits without owning the mutex; releasing
            // an unowned mutex throws and would crash instead of exiting cleanly.
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // mutex not owned on this thread — nothing to release
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }
}
