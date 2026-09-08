using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;

namespace GuardPulse.Agent.Session;

internal sealed record BrowserTab(string Title, string? Url);

/// <summary>
/// Live tab snapshot for the foreground browser. UrlSource records how much the
/// agent could read: "uia" (titles + active URL via UI Automation), "session"
/// (background URLs enriched from the profile's SNSS file), or "title" (window
/// title only — UIA unavailable).
/// </summary>
internal sealed record BrowserSnapshot(
    string AppKey,
    string Label,
    string ActiveTab,
    string? ActiveUrl,
    int TabCount,
    IReadOnlyList<BrowserTab> Tabs,
    string UrlSource);

/// <summary>
/// Watches the foreground browser and streams its live tab state over the agent
/// pipe ("browser" messages): active tab (parsed from the window title, refined by
/// the UIA-selected tab), the open-tab list + count (UI Automation tab strip), the
/// active tab's URL (UIA omnibox read), and best-effort background-tab URLs (copied
/// Chromium SNSS session files). Change-driven with a >=1.5s debounce plus a 60s
/// heartbeat while browsing; every failure mode degrades to titles-only or silence.
/// This watcher must never disturb the protection agent: no exceptions leave it and
/// UIA scans run off the UI thread, at most one at a time.
/// </summary>
public sealed class BrowserWatcher : IDisposable
{
    private static readonly HashSet<string> BrowserExes = new(StringComparer.Ordinal)
    {
        "chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "opera.exe", "vivaldi.exe", "chromium.exe",
    };

    private static readonly string[] BrowserSuffixes =
    {
        "Google Chrome", "Chrome", "Microsoft Edge", "Edge", "Brave", "Mozilla Firefox", "Firefox", "Opera", "Vivaldi",
    };

    private const int ScanIntervalMs = 1_000;
    private const int DebounceMs = 1_500;
    private const int HeartbeatMs = 60_000;
    private const int MaxTabs = 25;
    private const int GraceAfterFocusLossMs = 60_000;
    private const int MaxTabStrip = 40;

    private readonly PipeClient _pipe;
    private readonly ForegroundHook _hook;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _stateGate = new();
    private BrowserSnapshot? _lastSent;
    private long _lastSentAtMs;
    private nint _lastBrowserHwnd;
    private TabRules _tabRules = new();
    // Per-(window,url) close-attempt guards: a failed close clears its guard so the
    // next scan retries; a vanished window/URL clears it too.
    private readonly HashSet<(nint hwnd, string url)> _closeAttempted = new();
    // Guard-reset tracking: the single-URL _lastClosedUrl guard must reset when
    // the URL is absent from the scan or the tab set changes, otherwise a tab
    // that reopened (or a sibling window's identical URL) would never be
    // enforced again. Track tab-count + active URL per window (by hwnd); any
    // change clears the guard so the next scan re-evaluates from scratch.
    private readonly Dictionary<nint, (int TabCount, string? ActiveUrl)> _lastTabState = new();
    private readonly object _guardGate = new();
    public event Action<string>? BlockedTabClosed;
    private string? _lastBrowserExePath;
    private long _browserFocusLostAtMs;
    private bool _graceSnapshotSent;
    private bool _disposed;
    // Recently-seen browser top-level windows (most recent first). Enforcement walks
    // all of them each scan so blocked tabs close even in background windows.
    private readonly List<nint> _recentBrowserHwnds = new();
    private const int MaxTrackedBrowserWindows = 6;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);

    /// <summary>Enumerates visible top-level windows belonging to known browser
    /// processes (background windows included), newest-first by discovery order.</summary>
    private List<nint> EnumerateBrowserWindows()
    {
        var result = new List<nint>();
        try
        {
            var browserPids = new HashSet<int>();
            foreach (var name in BrowserExes)
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    browserPids.Add(proc.Id);
                    proc.Dispose();
                }
            }

            if (browserPids.Count == 0) return result;

            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == 0 || !browserPids.Contains((int)pid)) return true;
                    if (GetWindowTextLength(hwnd) == 0) return true;
                    result.Add(hwnd);
                }
                catch
                {
                    // single-window failure must not abort enumeration
                }

                return true;
            }, nint.Zero);
        }
        catch
        {
            // best-effort: foreground path still covers the active window
        }

        return result;
    }

    public BrowserWatcher(PipeClient pipe, ForegroundHook hook)
    {
        _pipe = pipe;
        _hook = hook;
        _dispatcher = Dispatcher.CurrentDispatcher; // constructed on the UI thread
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ScanIntervalMs),
        };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        // A foreground switch to a browser should produce a snapshot quickly rather
        // than waiting for the next tick; the debounce in MaybeSend still applies.
        hook.ForegroundChanged += OnForegroundChangedScan;
    }

    // Named handlers (not `_ =>` lambdas): a lambda parameter named `_` would turn
    // the `_ = Task` discard into an assignment to that parameter.
    /// <summary>Replaces the blocked-site rule set (service pushes on every policy apply).</summary>
    public void SetTabRules(TabRules rules)
    {
        _tabRules = rules ?? new TabRules();
    }

    private void OnTick() => _ = ScanAsync(immediate: false);

    private void OnForegroundChangedScan(string appKey) => _ = ScanAsync(immediate: true);

    private sealed record ScanResult(BrowserSnapshot? Snapshot, nint BrowserHwnd, string? ExePath);

    private async Task ScanAsync(bool immediate)
    {
        if (_disposed || !_scanGate.Wait(0)) return; // previous scan still running
        try
        {
            var scan = await Task.Run(CaptureForeground).ConfigureAwait(true);
            if (scan.Snapshot is { } snapshot)
            {
                lock (_stateGate)
                {
                    _lastBrowserHwnd = scan.BrowserHwnd;
                    _lastBrowserExePath = scan.ExePath;
                    _browserFocusLostAtMs = 0;
                    _graceSnapshotSent = false;
                    // Recency rotation with the foreground window pinned: move the
                    // current window to the front, then evict least-recently-seen
                    // entries (from the back) — never the just-seen foreground one.
                    _recentBrowserHwnds.Remove(scan.BrowserHwnd);
                    _recentBrowserHwnds.Insert(0, scan.BrowserHwnd);
                    while (_recentBrowserHwnds.Count > MaxTrackedBrowserWindows)
                    {
                        _recentBrowserHwnds.RemoveAt(_recentBrowserHwnds.Count - 1);
                    }
                }

                MaybeSend(snapshot);
            }
            else
            {
                // Focus left the browser: send one final enumeration of the last
                // browser window (UIA works on background windows) so the parent
                // sees the tabs that are still open, then go quiet.
                nint lastHwnd;
                string? lastExe;
                long lostAtMs;
                bool graceSent;
                lock (_stateGate)
                {
                    if (_browserFocusLostAtMs == 0 && _lastBrowserHwnd != nint.Zero)
                    {
                        _browserFocusLostAtMs = Environment.TickCount64;
                    }

                    lastHwnd = _lastBrowserHwnd;
                    lastExe = _lastBrowserExePath;
                    lostAtMs = _browserFocusLostAtMs;
                    graceSent = _graceSnapshotSent;
                }

                if (lastHwnd != nint.Zero && !graceSent
                    && Environment.TickCount64 - lostAtMs < GraceAfterFocusLossMs)
                {
                    var graceSnap = await Task.Run(() => CaptureBrowserWindow(lastHwnd, lastExe)).ConfigureAwait(true);
                    if (graceSnap is not null)
                    {
                        lock (_stateGate) _graceSnapshotSent = true;
                        MaybeSend(graceSnap);
                    }
                }
            }

            // Real-time enforcement must not depend on which app is foreground:
            // walk EVERY tracked browser window (background included) and close
            // any whose active URL matches the blocked-site rules.
            await EnforceTabRulesAcrossWindowsAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Never let a tab-scan failure escape: the next tick retries.
        }
        finally
        {
            try
            {
                _scanGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Dispose raced an in-flight scan; the scan result is discarded anyway
            }
        }
    }

    /// <summary>Real-time blocked-site enforcement across ALL tracked browser windows:
    /// a window whose active URL matches the blocked-site rules has THAT tab closed via
    /// UIA (no keystrokes, no focus change, background windows included). One close
    /// attempt per URL per scan; the guard resets when the URL leaves the scan or the
    /// tab set changes, so reopened/twin tabs are enforced again. Title-only fallback
    /// snapshots never enforce (ActiveUrl must be a real UIA URL or contain a dot);
    /// UIA capture walks run off the UI thread with a 3s timeout each and never block
    /// the dispatcher continuation.</summary>
    private async Task EnforceTabRulesAcrossWindowsAsync()
    {
        if (_tabRules.IsEmpty) return;

        List<nint> windows;
        nint foregroundHwnd = nint.Zero;
        lock (_stateGate)
        {
            windows = _recentBrowserHwnds.Where(IsWindowVisible).ToList();
            // Recency rotation: most recently seen first, foreground pinned at
            // the head so the 6-window cap never evicts the window in use.
            try
            {
                foregroundHwnd = GetForegroundWindow();
            }
            catch
            {
            }

            if (foregroundHwnd != nint.Zero && windows.Contains(foregroundHwnd))
            {
                windows.Remove(foregroundHwnd);
                windows.Insert(0, foregroundHwnd);
            }

            _recentBrowserHwnds.Clear();
            _recentBrowserHwnds.AddRange(windows.Take(MaxTrackedBrowserWindows));
            windows = _recentBrowserHwnds.ToList();
        }

        // Fall back to a fresh enumeration when nothing is tracked yet (service
        // restart, first launch after install) so enforcement starts immediately.
        if (windows.Count == 0 || _recentBrowserHwnds.Count < MaxTrackedBrowserWindows)
        {
            // Keep the tracked set fresh every scan: new browser windows must be
            // enforced without waiting for the child to focus them once.
            var discovered = EnumerateBrowserWindows();
            lock (_stateGate)
            {
                foreach (var hwnd in discovered)
                {
                    if (_recentBrowserHwnds.Contains(hwnd)) continue;
                    _recentBrowserHwnds.Insert(0, hwnd);
                    if (_recentBrowserHwnds.Count > MaxTrackedBrowserWindows)
                    {
                        _recentBrowserHwnds.RemoveAt(_recentBrowserHwnds.Count - 1);
                    }
                }
            }

            lock (_stateGate)
            {
                windows = _recentBrowserHwnds.ToList();
            }
        }

        if (windows.Count == 0) return;

        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hwnd in windows)
        {
            // OFF the UI thread with a 3s timeout each: the UIA capture walk
            // runs on a ThreadPool thread (awaited, never blocking the
            // dispatcher continuation), and a hung renderer only skips this
            // window for this scan.
            BrowserSnapshot? snapshot = null;
            try
            {
                var exePath = ForegroundHook.GetProcessImagePath(WindowProcessId(hwnd));
                if (exePath is null) continue;
                var capturedHwnd = hwnd;
                var capturedExe = exePath;
                snapshot = await Task.Run(() => CaptureBrowserWindow(capturedHwnd, capturedExe))
                    .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                continue; // hung UIA tree: skip this window this scan
            }
            catch (Exception)
            {
                continue;
            }

            // Gate title-only fallback snapshots OUT of enforcement: only act
            // when ActiveUrl is a real URL (UIA omnibox read, or at least a
            // dotted host rather than a bare page title).
            if (snapshot is null || !IsEnforceableUrl(snapshot)) continue;
            var url = snapshot.ActiveUrl!;
            seenUrls.Add(url);

            var match = _tabRules.Match(url);
            if (match is null) continue;

            // Tab-set change detection: track tab-count + active URL per window; any
            // change clears that window's close-attempt guard so enforcement re-fires.
            var tabCount = snapshot.TabCount;
            lock (_guardGate)
            {
                var changed = !_lastTabState.TryGetValue(hwnd, out var prev)
                    || prev.TabCount != tabCount
                    || !string.Equals(prev.ActiveUrl, url, StringComparison.Ordinal);
                _lastTabState[hwnd] = (tabCount, url);
                if (changed) _closeAttempted.Remove((hwnd, url));
            }

            lock (_guardGate)
            {
                if (_closeAttempted.Contains((hwnd, url)))
                {
                    continue; // close already dispatched for this window+URL
                }

                _closeAttempted.Add((hwnd, url));
            }

            var capturedUrl = url;
            _ = Task.Run(() =>
            {
                try
                {
                    if (!TabEnforcer.CloseSelectedTab(hwnd))
                    {
                        // Close failed (localized strip, no button found...): clear the
                        // guard so the next scan retries instead of stalling forever.
                        lock (_guardGate) _closeAttempted.Remove((hwnd, capturedUrl));
                        return;
                    }

                    _pipe.SendTabClosed(capturedUrl);
                    BlockedTabClosed?.Invoke(capturedUrl);
                }
                catch
                {
                    lock (_guardGate) _closeAttempted.Remove((hwnd, capturedUrl));
                }
            });
        }

        // Drop guards for windows that are gone entirely, and for URLs no longer
        // present anywhere (tab closed or navigated away) so the same URL
        // enforces again if it reopens.
        lock (_guardGate)
        {
            var gone = _lastTabState.Keys.Where(h => !windows.Contains(h)).ToList();
            foreach (var h in gone) _lastTabState.Remove(h);

            var liveUrls = seenUrls;
            _closeAttempted.RemoveWhere(k => !liveUrls.Contains(k.url));
            var liveWindows = windows.ToHashSet();
            _closeAttempted.RemoveWhere(k => !liveWindows.Contains(k.hwnd));
        }
    }

    /// <summary>True only when the snapshot carries a real URL: a UIA omnibox
    /// read, or at least a dotted host (never a bare title-only guess).</summary>
    private static bool IsEnforceableUrl(BrowserSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ActiveUrl)) return false;
        if (string.Equals(snapshot.UrlSource, "uia", StringComparison.OrdinalIgnoreCase)) return true;
        return snapshot.ActiveUrl.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>Best-effort URL from a window title ("Site - Browser" → "site").</summary>
    private static string? UrlFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? title[..dash].Trim() : title.Trim();
    }

    private static uint WindowProcessId(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private ScanResult CaptureForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero)
        {
            return new ScanResult(null, nint.Zero, null);
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        var exePath = pid == 0 ? null : ForegroundHook.GetProcessImagePath(pid);
        var exeName = Path.GetFileName(exePath ?? string.Empty).ToLowerInvariant();
        if (exePath is null || !BrowserExes.Contains(exeName))
        {
            return new ScanResult(null, nint.Zero, null);
        }

        var snapshot = CaptureBrowserWindow(hwnd, exePath);
        return new ScanResult(snapshot, hwnd, exePath);
    }

    /// <summary>Builds a snapshot for one browser window; titles-only when UIA is unavailable.</summary>
    private BrowserSnapshot? CaptureBrowserWindow(nint hwnd, string? exePath)
    {
        if (exePath is null)
        {
            return null;
        }

        var appKey = exePath.ToLowerInvariant();
        var label = BrowserLabel(exePath);
        var windowTitle = ForegroundHook.GetWindowTitle(hwnd) ?? string.Empty;

        var tabs = new List<BrowserTab>();
        string? selectedTab = null;
        string? activeUrl = null;
        var urlSource = "title";

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root != null)
            {
                var tabItems = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                if (tabItems != null)
                {
                    var n = Math.Min(tabItems.Count, MaxTabStrip);
                    for (var i = 0; i < n; i++)
                    {
                        try
                        {
                            var el = tabItems[i];
                            var name = el.Current.Name?.Trim();
                            if (string.IsNullOrWhiteSpace(name))
                            {
                                continue;
                            }

                            var isSelected = false;
                            try
                            {
                                if (el.GetCurrentPattern(SelectionItemPattern.Pattern) is SelectionItemPattern sel)
                                {
                                    isSelected = sel.Current.IsSelected;
                                }
                            }
                            catch (InvalidOperationException)
                            {
                                // pattern unsupported — fall back to the window title
                            }

                            if (isSelected && selectedTab is null)
                            {
                                selectedTab = name;
                            }

                            tabs.Add(new BrowserTab(name, null));
                        }
                        catch (ElementNotAvailableException)
                        {
                            // the browser rebuilt its tree mid-scan; skip the tab
                        }
                    }
                }

                activeUrl = ReadActiveUrl(root);
                if (activeUrl != null)
                {
                    urlSource = "uia";
                }
            }
        }
        catch (Exception)
        {
            // UIA unavailable or the tree changed mid-scan: titles-only snapshot.
        }

        // The selected tab's full name is more accurate than the window title strip,
        // which truncates long page titles.
        var parsedTitle = ParseActiveTab(windowTitle, label);
        var activeTab = selectedTab ?? parsedTitle;
        if (tabs.Count == 0 && !string.IsNullOrEmpty(activeTab))
        {
            tabs.Add(new BrowserTab(activeTab, null)); // degrade gracefully: at least the active tab
        }

        if (tabs.Count > MaxTabs)
        {
            tabs = tabs.Take(MaxTabs).ToList();
        }

        // Best-effort background URLs from the profile's session file (Chromium only,
        // at most every 30s, purely additive: unmatched tabs simply keep url=null).
        try
        {
            if (tabs.Count > 0)
            {
                var urlMap = BrowserSessionFileReader.GetTitleToUrlMap(exePath);
                if (urlMap.Count > 0)
                {
                    var enriched = new List<BrowserTab>(tabs.Count);
                    foreach (var tab in tabs)
                    {
                        var url = tab.Url
                            ?? (urlMap.TryGetValue(tab.Title.Trim(), out var u) ? u : null);
                        enriched.Add(new BrowserTab(tab.Title, url));
                    }

                    tabs = enriched;
                    urlSource = activeUrl != null ? "session" : urlSource;
                }
            }
        }
        catch (Exception)
        {
            // session-file enrichment is optional by contract
        }

        // Prefer the live URL's page when the strip name got truncated: if exactly
        // one listed tab matches the active URL's host, the selected name stands.
        return new BrowserSnapshot(appKey, label, activeTab, activeUrl, tabs.Count, tabs, urlSource);
    }

    private static string? ReadActiveUrl(AutomationElement root)
    {
        try
        {
            var edits = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (edits == null)
            {
                return null;
            }

            var n = Math.Min(edits.Count, 12);
            for (var i = 0; i < n; i++)
            {
                try
                {
                    var el = edits[i];
                    if (el.Current.IsPassword)
                    {
                        continue;
                    }

                    if (el.GetCurrentPattern(ValuePattern.Pattern) is not ValuePattern vp)
                    {
                        continue;
                    }

                    var value = vp.Current.Value?.Trim();
                    if (LooksLikeUrl(value))
                    {
                        return value;
                    }
                }
                catch (InvalidOperationException)
                {
                    // no value pattern on this edit; try the next one
                }
                catch (ElementNotAvailableException)
                {
                    // tree churn; try the next one
                }
            }
        }
        catch (Exception)
        {
            // omnibox read is optional
        }

        return null;
    }

    internal static bool LooksLikeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 5 || value.Length > 2048)
        {
            return false;
        }

        if (value.Contains(' ') || value.Contains('"') || value.Contains('<'))
        {
            return false;
        }

        if (value.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out _);
        }

        // scheme-less omnibox value like "github.com/guardpulse"
        var host = value.Split('/', 2)[0];
        return host.Contains('.') && !host.EndsWith(".", StringComparison.Ordinal);
    }

    /// <summary>"Page title - Brave" → "Page title". Handles the Chromium dash and the
    /// Firefox em-dash, and Edge's embedded special space ("Microsoft Edge").</summary>
    internal static string ParseActiveTab(string windowTitle, string browserLabel)
    {
        var title = windowTitle.Trim();
        if (title.Length == 0)
        {
            return browserLabel;
        }

        foreach (var sep in new[] { " — ", " - ", " – " })
        {
            var idx = title.LastIndexOf(sep, StringComparison.Ordinal);
            if (idx <= 0)
            {
                continue;
            }

            var suffix = title[(idx + sep.Length)..].Trim();
            if (BrowserSuffixMatches(suffix))
            {
                var head = title[..idx].Trim();
                // Multi-tab windows chain ("Page A - Page B - Brave"); keep only the
                // segment the browser actually shows first? No — Chromium puts the
                // ACTIVE page title alone; extra dashes belong to the page itself.
                return head.Length > 0 ? head : browserLabel;
            }
        }

        return title;
    }

    private static bool BrowserSuffixMatches(string suffix)
    {
        var normalized = suffix.Replace('\u2009', ' ').Replace('\u00a0', ' ');
        foreach (var b in BrowserSuffixes)
        {
            if (string.Equals(normalized, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BrowserLabel(string exePath)
    {
        var exe = Path.GetFileName(exePath).ToLowerInvariant();
        return exe switch
        {
            "chrome.exe" or "chromium.exe" => "Chrome",
            "msedge.exe" => "Edge",
            "brave.exe" => "Brave",
            "firefox.exe" => "Firefox",
            "opera.exe" => "Opera",
            "vivaldi.exe" => "Vivaldi",
            _ => Path.GetFileNameWithoutExtension(exePath),
        };
    }

    private void MaybeSend(BrowserSnapshot snapshot)
    {
        var now = Environment.TickCount64;
        lock (_stateGate)
        {
            var changed = _lastSent is null || !SameSnapshot(_lastSent, snapshot);
            var heartbeat = now - _lastSentAtMs >= HeartbeatMs;
            if (!changed)
            {
                if (!heartbeat || _lastSent is null)
                {
                    return;
                }
            }
            else if (_lastSent != null && now - _lastSentAtMs < DebounceMs)
            {
                return; // coalesce bursts of tab switches
            }

            _lastSent = snapshot;
            _lastSentAtMs = now;
        }

        try
        {
            _pipe.SendBrowser(snapshot);
        }
        catch (Exception)
        {
            // pipe hiccup; the next change or heartbeat re-sends
        }
    }

    private static bool SameSnapshot(BrowserSnapshot a, BrowserSnapshot b)
    {
        return a.AppKey == b.AppKey
            && a.ActiveTab == b.ActiveTab
            && a.ActiveUrl == b.ActiveUrl
            && a.TabCount == b.TabCount
            && a.Tabs.Count == b.Tabs.Count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _hook.ForegroundChanged -= OnForegroundChangedScan;
        _scanGate.Dispose();
    }

    private delegate void WinEventProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
