using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GuardPulse.Agent.Session;

public partial class LockWindow : Window
{
    private const int PinLength = 6;
    private static readonly TimeSpan BlockedRefresh = TimeSpan.FromSeconds(1);
    private static Window? _hiddenOwner;

    private readonly PipeClient _pipe;
    private readonly DispatcherTimer _blockedTimer = new() { Interval = BlockedRefresh };
    private readonly Ellipse[] _dots = new Ellipse[PinLength];
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private string _pin = "";
    private string _appKey = "";
    private long _blockedUntilMs;

    public LockWindow(PipeClient pipe)
    {
        _pipe = pipe;
        InitializeComponent();

        if (_hiddenOwner == null)
        {
            _hiddenOwner = new Window
            {
                Width = 1,
                Height = 1,
                Left = -20000,
                Top = -20000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            _hiddenOwner.Show();
            _hiddenOwner.Hide();
        }
        Owner = _hiddenOwner;

        _dots[0] = PinDot0; _dots[1] = PinDot1; _dots[2] = PinDot2;
        _dots[3] = PinDot3; _dots[4] = PinDot4; _dots[5] = PinDot5;
        _blockedTimer.Tick += (_, _) => RefreshBlockedText();
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += OnWindowStateChanged;
        // Something moved the wall (z-order/activation tricks, shell placement): snap
        // it back over the whole virtual desktop.
        LocationChanged += (_, _) =>
        {
            if (IsVisible && !_coveringDesktop) CoverVirtualDesktop();
        };
        SourceInitialized += (_, _) =>
        {
            DwmBlur.Enable(this, System.Windows.Media.Color.FromRgb(0xFC, 0xF8, 0xFB), 0.75);
        };
        CoverVirtualDesktop();
    }

    private bool _coveringDesktop;

    /// <summary>
    /// Covers the FULL virtual desktop (every monitor): a single-monitor Maximized
    /// window leaves the other screens usable while the wall is up. Explicit bounds
    /// require a Manual (Normal, non-maximized) state.
    /// </summary>
    private void CoverVirtualDesktop()
    {
        _coveringDesktop = true;
        try
        {
            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
            }

            WindowStyle = WindowStyle.None;
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
        }
        finally
        {
            _coveringDesktop = false;
        }
    }

    public event Action? DesktopMinimized;
    private readonly Dictionary<int, string> _minimizedByPid = new();

    public string CurrentAppKey => _appKey;

    public void ShowFor(string appKey, string appLabel, string? reason)
    {
        _appKey = appKey; // needed so AskParent can name the locked app & hide-on-switch can match
        AppLabel.Text = appLabel;
        LoadAppIcon(appKey);
        ReasonText.Text = reason switch
        {
            "dailyLimit" => "Daily time limit reached",
            "sessionLimit" => "Session limit reached",
            "blockedSite" => "Blocked site",
            "manual" => "Locked by parent",
            "bypass" => "Restricted tool",
            "schedule" => "Outside allowed hours",
            "budget" => "Daily screen time is over",
            "notApproved" => "Not on the approved app list",
            _ => "Locked by parent"
        };
        ClearPin();
        WaitingPanel.Visibility = Visibility.Collapsed;
        AskParentButton.IsEnabled = true;
        RefreshBlockedText();
        CoverVirtualDesktop();

        if (!IsVisible)
        {
            Show();
        }

        Topmost = false;
        Topmost = true;
        Activate();
        Focus();
    }

    public void HideLock()
    {
        _appKey = "";
        _blockedTimer.Stop();
        ClearPin();
        if (IsVisible) Hide();
    }

    /// <summary>
    /// Full-desktop hold: keeps the wall visible across every monitor without naming a
    /// locked app (used while a suspended/blocked app is still running behind an allowed
    /// foreground window — the wall must not auto-hide just because focus moved).
    /// The wall still hides via its own PIN success / service unlock paths.
    /// </summary>
    public void HoldDesktop()
    {
        if (_appKey.Length > 0)
        {
            AppLabel.Text = "Protected apps are locked";
            ReasonText.Text = "Locked by parent";
            ClearPin();
        }

        _appKey = "";
        RefreshBlockedText();
        CoverVirtualDesktop();

        if (!IsVisible)
        {
            Show();
        }

        Topmost = false;
        Topmost = true;
        Activate();
        Focus();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            DesktopMinimized?.Invoke();
            var key = _appKey;
            if (!string.IsNullOrEmpty(key))
            {
                MinimizeBlockedApp(key);
            }
            Hide();
        }
        else
        {
            // Manual/Normal: re-snap over the virtual desktop instead of restoring
            // a single-monitor Maximized state.
            CoverVirtualDesktop();
            Topmost = false;
            Topmost = true;
            Activate();
            Focus();
        }
    }

    public void MinimizeBlockedApp(string appKey)
    {
        if (string.IsNullOrEmpty(appKey)) return;
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                if (!MatchesForMinimize(process, appKey)) continue;
                if (process.HasExited) continue;
                var pid = process.Id;
                _minimizedByPid[pid] = appKey;

                try
                {
                    var hwnd = process.MainWindowHandle;
                    if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
                    {
                        CloakWindow(hwnd, true);
                        ShowWindow(hwnd, SwMinimize);
                    }
                }
                catch { }

                EnumWindows((hwnd, _) =>
                {
                    try
                    {
                        GetWindowThreadProcessId(hwnd, out var winPid);
                        if (winPid == (uint)pid && IsWindowVisible(hwnd))
                        {
                            CloakWindow(hwnd, true);
                            ShowWindow(hwnd, SwMinimize);
                        }
                    }
                    catch { }

                    return true;
                }, IntPtr.Zero);
            }
        }
        catch { }
    }

    public void RestoreMinimized(string appKey)
    {
        if (string.IsNullOrEmpty(appKey) || _minimizedByPid.Count == 0) return;
        var pids = _minimizedByPid.Where(kv => string.Equals(kv.Value, appKey, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();
        foreach (var pid in pids)
        {
            try
            {
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    var hwnd = proc.MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                    {
                        CloakWindow(hwnd, false);
                        ShowWindow(hwnd, SwRestore);
                    }
                }
                catch { }

                EnumWindows((hwnd, _) =>
                {
                    try
                    {
                        GetWindowThreadProcessId(hwnd, out var winPid);
                        if (winPid == (uint)pid)
                        {
                            CloakWindow(hwnd, false);
                            ShowWindow(hwnd, SwRestore);
                        }
                    }
                    catch { }

                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            _minimizedByPid.Remove(pid);
        }
    }

    public void TriggerShowDesktop()
    {
        DesktopMinimized?.Invoke();
        var key = _appKey;
        if (!string.IsNullOrEmpty(key))
        {
            MinimizeBlockedApp(key);
        }
        WindowState = WindowState.Minimized;
        HideLock();
    }

    private static bool MatchesForMinimize(System.Diagnostics.Process process, string appKey)
    {
        try
        {
            var exePath = process.MainModule?.FileName ?? "";
            if (!string.IsNullOrEmpty(exePath) && string.Equals(exePath, appKey, StringComparison.OrdinalIgnoreCase)) return true;
            var fileName = System.IO.Path.GetFileName(exePath);
            if (appKey.StartsWith("guardpulse.windows.", StringComparison.OrdinalIgnoreCase))
            {
                var bypassNames = appKey switch
                {
                    "guardpulse.windows.taskmgr" => new[] { "taskmgr.exe" },
                    "guardpulse.windows.commandline" => new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wt.exe", "windowsterminal.exe", "conhost.exe" },
                    "guardpulse.windows.registry" => new[] { "regedit.exe", "regedt32.exe" },
                    "guardpulse.windows.settings" => new[] { "systemsettings.exe" },
                    "guardpulse.windows.installers" => new[] { "msiexec.exe" },
                    _ => Array.Empty<string>()
                };
                foreach (var n in bypassNames) if (string.Equals(fileName, n, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (!string.IsNullOrEmpty(fileName) && string.Equals(fileName, appKey, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Re-asserts topmost + activation for the visible overlay. Called when a foreign
    /// process takes foreground while the lock is up (z-order/activation tricks, input
    /// races); a no-op when the overlay is not showing.
    /// </summary>
    public void Reassert()
    {
        if (!IsVisible)
        {
            return;
        }

        CoverVirtualDesktop();
        Topmost = false;
        Topmost = true;
        Activate();
        Focus();
    }

    public void UpdatePinState(bool configured, long blockedUntilMs)
    {
        var wasBlocked = _blockedUntilMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _blockedUntilMs = blockedUntilMs;
        if (blockedUntilMs > 0)
        {
            if (!_blockedTimer.IsEnabled) _blockedTimer.Start();
        }
        else
        {
            _blockedTimer.Stop();
        }

        RefreshBlockedText();
        if (!wasBlocked && blockedUntilMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            ShowWrongPinFeedback();
        }

        if (!configured && IsVisible)
        {
            ReasonText.Text = "A parent PIN must be set from the parent app first";
        }
    }

    private void RefreshBlockedText()
    {
        var remaining = _blockedUntilMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remaining > 0)
        {
            BlockedText.Text = $"Too many attempts. Try again in {remaining / 1000 + 1}s";
            BlockedText.Visibility = Visibility.Visible;
            KeypadGrid.IsEnabled = false;
        }
        else
        {
            BlockedText.Text = "";
            BlockedText.Visibility = Visibility.Collapsed;
            KeypadGrid.IsEnabled = true;
        }
    }

    private void OnKeypad(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string key } && key.Length > 0 && char.IsDigit(key[0]))
        {
            AppendPinDigit(key[0]);
        }
    }

    private void OnKeyClear(object sender, RoutedEventArgs e) => ClearPin();

    private void OnKeyBack(object sender, RoutedEventArgs e) => RemoveLastPinDigit();

    private void AppendPinDigit(char digit)
    {
        if (_blockedUntilMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return;
        if (_pin.Length < PinLength) _pin += digit;
        RenderPin();
        if (_pin.Length == PinLength)
        {
            _pipe.SendPin(_pin);
            ClearPin();
        }
    }

    private void RemoveLastPinDigit()
    {
        if (_blockedUntilMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return;
        if (_pin.Length > 0) _pin = _pin[..^1];
        RenderPin();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        if ((Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
            && (e.Key == Key.F4 || e.SystemKey == Key.F4))
        {
            e.Handled = true;
            CloseBlockedApp(_appKey);
            return;
        }

        if (e.Key == Key.System || e.Key == Key.D)
        {
            var sysKey = e.SystemKey == Key.None ? e.Key : e.SystemKey;
            if (sysKey == Key.D && (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)))
            {
                e.Handled = true;
                TriggerShowDesktop();
                return;
            }
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            TriggerShowDesktop();
            return;
        }

        if (_blockedUntilMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            e.Handled = true;
            return;
        }

        if (e.Key >= Key.D0 && e.Key <= Key.D9 || e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
        {
            var digit = ((int)e.Key - (int)Key.D0) % 10;
            AppendPinDigit((char)('0' + digit));
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            RemoveLastPinDigit();
            e.Handled = true;
        }
    }

    private void OnAskParent(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_appKey)) _pipe.SendAskParent(_appKey);
        WaitingPanel.Visibility = Visibility.Visible;
        AskParentButton.IsEnabled = false;
    }

    private void ClearPin()
    {
        _pin = "";
        RenderPin();
    }

    private void RenderPin()
    {
        for (var i = 0; i < _dots.Length; i++)
        {
            _dots[i].Fill = i < _pin.Length
                ? (Brush)FindResource("Brush.Primary")
                : Brushes.Transparent;
        }
    }

    private void ShowWrongPinFeedback()
    {
        ErrorText.Opacity = 1;
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(0.4) };
        var offsets = new[] { -4, 4, -4, 4, -2, 2, 0 };
        for (var i = 0; i < offsets.Length; i++)
        {
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(offsets[i],
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.05 * i))));
        }

        PinRowShake.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    public void CloseBlockedApp(string appKey)
    {
        if (string.IsNullOrEmpty(appKey)) { HideLock(); return; }
        try
        {
            var matched = false;
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                bool isMatch = false;
                try
                {
                    var exePath = process.MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(exePath) && string.Equals(exePath, appKey, StringComparison.OrdinalIgnoreCase)) isMatch = true;
                    else
                    {
                        var fileName = System.IO.Path.GetFileName(exePath);
                        if (appKey.StartsWith("guardpulse.windows.", StringComparison.OrdinalIgnoreCase))
                        {
                            var bypassNames = appKey switch
                            {
                                "guardpulse.windows.taskmgr" => new[] { "taskmgr.exe" },
                                "guardpulse.windows.commandline" => new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wt.exe", "windowsterminal.exe", "conhost.exe" },
                                "guardpulse.windows.registry" => new[] { "regedit.exe", "regedt32.exe" },
                                "guardpulse.windows.settings" => new[] { "systemsettings.exe" },
                                "guardpulse.windows.installers" => new[] { "msiexec.exe" },
                                _ => Array.Empty<string>()
                            };
                            foreach (var n in bypassNames) if (string.Equals(fileName, n, StringComparison.OrdinalIgnoreCase)) { isMatch = true; break; }
                        }
                        else if (!string.IsNullOrEmpty(fileName) && string.Equals(fileName, appKey, StringComparison.OrdinalIgnoreCase)) isMatch = true;
                    }
                }
                catch { }

                if (!isMatch) continue;
                matched = true;
                try
                {
                    if (process.HasExited) continue;
                    if (process.CloseMainWindow())
                    {
                        if (process.WaitForExit(1000)) continue;
                    }
                    process.Kill(entireProcessTree: true);
                }
                catch { }
            }

            _minimizedByPid.Clear();
            HideLock();
            if (!matched) HideLock();
        }
        catch { HideLock(); }
    }

    private void LoadAppIcon(string appKey)
    {
        try
        {
            if (!_iconCache.TryGetValue(appKey, out var icon))
            {
                icon = null;
                if (appKey.Contains(".exe", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(appKey))
                {
                    using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(appKey);
                    if (extracted != null)
                    {
                        using var bitmap = extracted.ToBitmap();
                        using var stream = new MemoryStream();
                        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                        stream.Position = 0;
                        icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                            stream,
                            System.Windows.Media.Imaging.BitmapCreateOptions.None,
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    }
                }

                _iconCache[appKey] = icon;
            }

            if (icon != null)
            {
                AppIcon.Source = icon;
                AppIcon.Visibility = Visibility.Visible;
            }
            else
            {
                AppIcon.Source = null;
                AppIcon.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            AppIcon.Source = null;
            AppIcon.Visibility = Visibility.Collapsed;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (IsEnabledForClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        var key = _appKey;
        CloseBlockedApp(key);
    }

    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const int DwmwaCloak = 13;
    private const int DwmwaForceIconicRepresentation = 7;

    private static void CloakWindow(IntPtr hwnd, bool cloak)
    {
        try
        {
            int val = cloak ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaCloak, ref val, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaForceIconicRepresentation, ref val, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    private bool IsEnabledForClose { get; set; }

    public void ForceClose()
    {
        IsEnabledForClose = true;
        Close();
    }
}
