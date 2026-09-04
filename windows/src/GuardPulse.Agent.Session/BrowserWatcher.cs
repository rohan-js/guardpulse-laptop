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

    // 1s scan + no debounce on URL changes: the service's per-tab block action keys off
    // the active URL, so detection latency after an SPA jump stays ~1-2s (feels instant).
    private const int ScanIntervalMs = 1_000;
    private const int DebounceMs = 1_500;
    private const int HeartbeatMs = 60_000;
    private const int MaxTabs = 25;
    private const int GraceAfterFocusLossMs = 60_000;
    private const int MaxTabStrip = 40;

    private readonly PipeClient _pipe;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _stateGate = new();
    private BrowserSnapshot? _lastSent;
    private long _lastSentAtMs;
    private nint _lastBrowserHwnd;
    private string? _lastBrowserExePath;
    private long _browserFocusLostAtMs;
    private bool _graceSnapshotSent;
    private bool _disposed;

    public BrowserWatcher(PipeClient pipe, ForegroundHook hook)
    {
        _pipe = pipe;
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
        }
        catch (Exception)
        {
            // Never let a tab-scan failure escape: the next tick retries.
        }
        finally
        {
            _scanGate.Release();
        }
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
            var urlChanged = _lastSent != null
                && !string.Equals(_lastSent.ActiveUrl, snapshot.ActiveUrl, StringComparison.Ordinal);
            var changed = _lastSent is null || !SameSnapshot(_lastSent, snapshot);
            var heartbeat = now - _lastSentAtMs >= HeartbeatMs;
            if (!changed)
            {
                if (!heartbeat || _lastSent is null)
                {
                    return;
                }
            }
            else if (!urlChanged && _lastSent != null && now - _lastSentAtMs < DebounceMs)
            {
                return; // coalesce bursts of tab switches — but URL changes go out instantly
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
        _scanGate.Dispose();
    }

    private delegate void WinEventProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
