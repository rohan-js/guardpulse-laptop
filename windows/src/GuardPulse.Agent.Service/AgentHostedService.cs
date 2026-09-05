// AgentHostedService: the integrator. Loads config, prepares the state directory and
// device identity, wires all Core components (firebase client, sync engine, ledger,
// activity log, enforcement, unlocks, inventory, pairing) plus the pipe host, watchdog
// and process suspender, and runs all periodic loops:
//   - enforcement of the current foreground app (suspend + lock overlay broadcast),
//     re-evaluated on every 15s boundary tick so time-based decisions apply promptly
//   - activity upload (throttled to 20s) and history ack
//   - heartbeat (30s) + sync/runtime + security/runtime
//   - per-app state upload (60s + on decision change)
//   - inventory upload (start + rescanApps)
//   - commands (rescanApps / resetToday / openSetup / unpair)
//   - pair request acceptance (device registration) and unlock request approvals
//   - PIN verification with retry gating, tamper event push
//
// NOTE: CommandsLoop / DeviceRegistrar / UnlockRequestClient / PinRetryPolicy are NOT
// part of CONTRACTS.md; the equivalent behavior is implemented locally in this file on
// top of the contracted primitives (IFirebaseClient + FirebasePaths + PolicyConstants)
// so this project compiles against the authoritative contract exactly.

using System.Text.Json;
using System.Text.Json.Nodes;
using GuardPulse.Agent.Core;
using GuardPulse.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GuardPulse.Agent.Service;

public sealed partial class AgentHostedService(
    ILogger<AgentHostedService> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private static readonly TimeSpan CommandPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnlockPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PairPollInterval = TimeSpan.FromSeconds(5);
    // Telemetry cadence = how stale the phone's view of this laptop can get while
    // idle. These bound the laptop->phone direction (push-based on both ends once
    // the write lands, so the interval IS the latency).
    private static readonly TimeSpan ActivityFlushInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StateUploadInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DeadManCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeadManGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ActivityRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan StandardCommandTtl = TimeSpan.FromMinutes(5);
    // Retention window for devices/{id}/unlockRequests (deleted during handling + sweep).
    private static readonly TimeSpan UnlockRequestRetention = TimeSpan.FromDays(7);
    // RTDB retention: tamperEvents 30 days, activity history 7 days (sweep at startup + 24h).
    private static readonly TimeSpan TamperEventRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan ActivityHistoryRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan MessageRetention = TimeSpan.FromDays(1);
    // Field caps matching the RTDB rules; oversized values are silently rejected.
    private const int ActivityLabelMax = 160;
    private const int BrowserNameMax = 100;
    private const int BrowserTabMax = 300;
    private const int BrowserUrlMax = 2048;

    private static readonly Dictionary<string, string> BypassLabels = new(StringComparer.Ordinal)
    {
        [PolicyConstants.WINDOWS_TASK_MANAGER_PACKAGE] = "Task Manager",
        [PolicyConstants.WINDOWS_COMMAND_LINE_PACKAGE] = "Command Prompt & PowerShell",
        [PolicyConstants.WINDOWS_REGISTRY_EDITOR_PACKAGE] = "Registry Editor",
        [PolicyConstants.WINDOWS_SETTINGS_PACKAGE] = "Windows Settings",
        [PolicyConstants.WINDOWS_INSTALLERS_PACKAGE] = "Installers"
    };

    private readonly ILogger<AgentHostedService> _logger = logger;
    private readonly IHostApplicationLifetime _lifetime = lifetime;

    private readonly object _gate = new(); // guards foreground/overlay state
    private readonly object _labelLock = new();
    private readonly Dictionary<string, string> _labelByAppKey = new(StringComparer.OrdinalIgnoreCase);
    // Guards the SSE-vs-poll dedupe collections (_processedCommands,
    // _handledUnlockRequests, _lastUploadedAppStates): both the stream callbacks and
    // the 5s boundary polls mutate them concurrently.
    private readonly object _dedupeGate = new();
    // Most recently processed command ids, oldest first; trimmed to the newest
    // ProcessedCommandsKeep (ordered trim keeps restart-hydration replays deduped
    // where a Clear() would re-run the evicted commands).
    private readonly List<string> _processedCommands = new();
    private const int ProcessedCommandsMax = 1000;
    private const int ProcessedCommandsKeep = 500;
    private readonly HashSet<string> _handledUnlockRequests = new(StringComparer.Ordinal);
    private const int HandledUnlockRequestsMax = 1000;
    private IDisposable? _messageStream;
    private readonly HashSet<string> _handledMessages = new(StringComparer.Ordinal);
    private const int HandledMessagesMax = 200;

    private CancellationToken _ct;
    private TimeProvider _time = TimeProvider.System;
    private string _stateDir = StatePaths.Root;
    private AgentConfig _config = null!;
    private ISecretStore _secrets = null!;
    private IFirebaseClient _firebase = null!;
    private SyncEngine _syncEngine = null!;
    private UsageLedger _ledger = null!;
    private SessionUsageTracker _sessions = null!;
    private ActivityLog _activity = null!;
    private EnforcementEngine _enforcement = null!;
    private OneVisitUnlocks _unlocks = null!;
    private PairingManager _pairing = null!;
    private AgentPipeHost _pipeHost = null!;
    private ProcessSuspender _suspender = null!;
    private Watchdog _watchdog = null!;
    private PinRetryGate _pinRetry = null!;
    private string _deviceId = "";
    private string _agentAppKey = "";
    private string? _ownerUid;
    private bool _syncStarted;
    private bool _aclApplied;
    private bool _lockdownActive;
    private long _lastAgentSeenMs;
    private string? _adminTamperDayKey;
    private bool _policyCacheAclApplied;

    private string? _currentAppKey;
    private string _currentLabel = "";
    private string _currentOverlay = "none";
    private long _currentStartedAtMs;

    private DateTime _lastActivityUploadUtc = DateTime.MinValue;
    private DateTime _lastStateUploadUtc = DateTime.MinValue;

    // Last uploaded per-app state JSON (key -> serialized entry) for diff-based uploads.
    private readonly Dictionary<string, string> _lastUploadedAppStates = new(StringComparer.Ordinal);

    // Live browser tab state from the Session's BrowserWatcher + the per-domain time
    // ledger for today (b64url(domain) -> ms; RTDB keys cannot contain '.').
    private readonly object _browserGate = new();
    private PipeBrowserState? _currentBrowser;
    private readonly Dictionary<string, long> _browserDomains = new(StringComparer.Ordinal);
    private string? _browserDomainDayKey;
    private long _lastDomainAccrualAtMs;
    private long _lastBrowserUploadAtMs;
    private long _lastBrowserStateAtMs;
    private string? _lastUploadedBrowserJson;
    private bool _browserUploadPending;

    // ------------------------------------------------------------------ startup
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Device service orchestrator starting (pid {Pid}, dir {Dir})",
            Environment.ProcessId, AppContext.BaseDirectory);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _ct = linked.Token;

            SetupStateDirectory();

            var config = TryLoadConfig();
            if (config is null)
            {
                _lifetime.StopApplication();
                return;
            }

            _config = config;
            WireComponents();
            SubscribeEvents();

            // Force a clean rewrite of the hosts block on startup: an older build may have
            // left an over-broad whole-domain entry (e.g. "0.0.0.0 youtube.com") that blocks
            // the entire domain even though only a PATH-based rule (e.g. "youtube.com/shorts")
            // should be browser-blocked. The SyncEngine restores LastValidSnapshot from the
            // encrypted local store in its constructor, so re-applying it now strips any stale
            // entries. Safe to call before listeners attach; a null snapshot (fresh device) is
            // a no-op.
            if (_syncEngine.LastValidSnapshot != null)
            {
                ApplyContentFilterHosts(_syncEngine.LastValidSnapshot);
            }


            _pipeHost.Start(_ct);
            _watchdog.Start(_ct);
            _lastAgentSeenMs = NowMs(); // boot grace: give the watchdog time to spawn the agent

            // Bootstrap devices/{id}/meta (tvUid claim) before any loop touches the
            // device node — every other write is rules-gated on that claim existing.
            await RunSafeAsync("register", RegisterDeviceAsync);

            _ = RunSafeAsync("sync-start", () => EnsureSyncStartedAsync(_ct));
            _ = RunSafeAsync("inventory-initial", UploadInventoryAsync);
            _ = RunSafeAsync("owner-recover", EnsureOwnerUidAsync);

            // Start unlock/pair streams now that _firebase/_deviceId are wired.
            SubscribeToRealtimeStreams();

            // RTDB retention sweep at startup; the interval loop below repeats it daily.
            _ = RunSafeAsync("rtdb-prune", PruneRtdbNodesAsync);

            await Task.WhenAll(
                IntervalLoopAsync("heartbeat", TimeSpan.FromMilliseconds(PolicyConstants.HEARTBEAT_INTERVAL_MS), HeartbeatAsync),
                IntervalLoopAsync("state-upload", StateUploadInterval, () => UploadStatesAsync(false)),
                IntervalLoopAsync("activity-flush", ActivityFlushInterval, () => FlushActivityAsync(false)),
                // 5s ticker for daily-limit/budget/schedule boundaries + 5/10m warnings
                // + pairing/unlock/command fallback polls: schedule transitions and
                // SSE-fallback delivery land within seconds instead of quarter-minutes.
                IntervalLoopAsync("boundary", TimeSpan.FromSeconds(5), BoundaryTickAsync),
                // 10s browser roll-up: accrue active-domain time and refresh state/browser.
                IntervalLoopAsync("browser-rollup", TimeSpan.FromSeconds(10), BrowserRollupTickAsync),
                IntervalLoopAsync("dead-man", DeadManCheckInterval, () => { DeadManCheck(); return Task.CompletedTask; }),
                IntervalLoopAsync("rtdb-prune", TimeSpan.FromDays(1), PruneRtdbNodesAsync));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful stop
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Unhandled orchestrator failure; stopping host");
            _lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Device service orchestrator stopping");
        try
        {
            // Persist debounced state (sessions/activity) before anything else so a
            // normal stop loses nothing; crash-window semantics are unchanged.
            _ledger?.FlushDirty();
            _activity?.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "State flush during stop failed");
        }

        try
        {
            _suspender?.ResumeAll();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ResumeAll during stop failed");
        }

        try
        {
            _watchdog?.Dispose();
        }
        catch
        {
            // ignore shutdown races
        }

        try
        {
            _pipeHost?.Stop();
        }
        catch
        {
            // ignore shutdown races
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _firebase?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------ wiring / setup
    // SIDs (not localized group names) so the grants work on any Windows language.
    private const string SidUsers = "*S-1-5-32-545";
    private const string SidSystem = "*S-1-5-18";
    private const string SidAdmins = "*S-1-5-32-544";

    private void SetupStateDirectory()
    {
        _stateDir = StatePaths.Root;
        Directory.CreateDirectory(_stateDir);
        Directory.CreateDirectory(StatePaths.LogsDirectory);
        if (_aclApplied)
        {
            return;
        }

        try
        {
            // Users get read/traverse on the directory itself only — the grant does
            // NOT propagate to children, so files created by SYSTEM start locked
            // down. The session agent (running as the child user) reaches exactly
            // the files it is granted explicitly below.
            RunIcacls($"\"{_stateDir}\" /remove:g {SidUsers}", "state-dir-reset");
            RunIcacls($"\"{_stateDir}\" /grant \"{SidUsers}:(RX)\"", "state-dir-users");
            RunIcacls($"\"{Path.Combine(_stateDir, "device.json")}\" /grant \"{SidUsers}:R\"", "device-json-users");

            // Sensitive state is SYSTEM/Administrators only: the ledger files must
            // stay child-proof (editing usage/offsets would defeat daily limits).
            // One file per icacls call: the multi-file form fails with error 87.
            var targets = new List<string>
            {
                Path.Combine(_stateDir, "secrets.bin"),
                Path.Combine(_stateDir, "enforcement-state.json")
            };
            foreach (var pattern in new[] { "usage-*.json", "offsets-*.json", "blocks-*.json" })
            {
                targets.AddRange(Directory.EnumerateFiles(_stateDir, pattern));
            }

            foreach (var target in targets.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                RunIcacls($"\"{target}\" /inheritance:r /grant:r \"{SidSystem}:(F)\" \"{SidAdmins}:(F)\"", "state-files-lock");
            }

            RunIcacls(
                $"\"{StatePaths.LogsDirectory}\" /inheritance:r /grant:r \"{SidSystem}:(OI)(CI)(F)\" \"{SidAdmins}:(OI)(CI)(F)\"",
                "logs-lock");

            _aclApplied = true;
            _logger.LogInformation("State directory ready at {Dir} (Users: directory read only; state files locked)", _stateDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply state ACLs in {Dir}", _stateDir);
        }
    }

    /// <summary>icacls keeps the agent free of the legacy AccessControl package, whose facade assemblies break LibraryImport compilation on net8.0.</summary>
    private void RunIcacls(string arguments, string step)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                // Absolute path: bare-name resolution from a session-0 service is unreliable.
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "icacls.exe"),
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var icacls = System.Diagnostics.Process.Start(psi);
            if (icacls is null)
            {
                _logger.LogWarning("icacls step {Step}: process did not start", step);
                return;
            }

            var stdout = icacls.StandardOutput.ReadToEnd();
            var stderr = icacls.StandardError.ReadToEnd();
            if (!icacls.WaitForExit(10_000))
            {
                _logger.LogWarning("icacls step {Step}: timed out", step);
            }

            if (icacls.ExitCode != 0)
            {
                _logger.LogWarning("icacls step {Step} exited {Code} out='{Stdout}' err='{Stderr}'",
                    step, icacls.ExitCode, stdout, stderr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "icacls step {Step} failed to launch", step);
        }
    }

    private AgentConfig? TryLoadConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "agent-config.json");
        try
        {
            var config = AgentConfigLoader.Load(path);
            if (IsMissingOrPlaceholder(config.ApiKey) || IsMissingOrPlaceholder(config.ProjectId)
                || IsMissingOrPlaceholder(config.DatabaseUrl))
            {
                _logger.LogCritical(
                    "agent-config.json at {Path} is missing required values (apiKey/projectId/databaseUrl) or still contains placeholders. Place a valid config next to the service exe. Service will stop.",
                    path);
                return null;
            }

            return config;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "Could not load agent-config.json at {Path}. Place a valid config next to the service exe. Service will stop.",
                path);
            return null;
        }
    }

    private static bool IsMissingOrPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
               || value.Contains("__", StringComparison.Ordinal)
               || value.StartsWith("your-", StringComparison.OrdinalIgnoreCase);
    }

    private void WireComponents()
    {
        _time = TimeProvider.System;
        _secrets = new DpapiSecretStore("secrets.bin", msg => _logger.LogWarning("Secret store: {Msg}", msg));
        _pairing = new PairingManager(_secrets);
        WriteDeviceJson();

        _firebase = new RtdbFirebaseClient(_config, _secrets);
        _syncEngine = new SyncEngine(_firebase, _secrets, _deviceId, _time);
        _ledger = new UsageLedger(_stateDir, _time);
        _sessions = new SessionUsageTracker(Path.Combine(_stateDir, "session-usage.json"), _time);
        _activity = new ActivityLog(_stateDir, _time);
        _enforcement = new EnforcementEngine(_time);
        _unlocks = new OneVisitUnlocks(_stateDir, _time);
        _pipeHost = new AgentPipeHost(_logger);
        _suspender = new ProcessSuspender(_logger);

        var agentExePath = Path.Combine(AppContext.BaseDirectory, "GuardPulse.Agent.Session.exe");
        _watchdog = new Watchdog(_logger, agentExePath);
        // Only agents the watchdog knows about may talk on the pipe; a spoofed client
        // must not be able to keep ConnectedAgents > 0 and defeat the dead-man lockdown.
        _pipeHost.SetKnownAgentPids(() => _watchdog.KnownAgentPids());
        try
        {
            _agentAppKey = InventoryScanner.AppKeyForProcess(agentExePath);
        }
        catch
        {
            _agentAppKey = agentExePath.ToLowerInvariant();
        }

        _pinRetry = new PinRetryGate(_time, Path.Combine(_stateDir, "pin-retry.json"));
        TrimWorkingSet(); // release JIT/startup pages; the loops allocate little
        _logger.LogInformation("Components wired for device {DeviceId}", _deviceId);
    }

    /// <summary>Long-running system service hygiene: keep the resident footprint small.</summary>
    private static void TrimWorkingSet()
    {
        try
        {
            _ = SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, unchecked((nuint)(-1)), unchecked((nuint)(-1)));
        }
        catch (Exception)
        {
            // non-fatal: trimming is best effort
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(nint process, nuint min, nuint max);

    private void WriteDeviceJson()
    {
        var (deviceId, secret, code) = _pairing.GetOrCreate();
        _deviceId = deviceId;
        // device.json is granted to Users (the child account can read it), so it must
        // NEVER hold the pairing secret — only the stable deviceId and the short-lived
        // 6-digit manual code. The QR secret reaches the setup window over the pipe
        // ("deviceInfo" request) straight from the secret store.
        var json = JsonSerializer.Serialize(new DeviceIdentityJson(deviceId, code), JsonOpts);
        var path = Path.Combine(_stateDir, "device.json");
        var created = !File.Exists(path);
        File.WriteAllText(path, json);
        if (created)
        {
            // Fresh-install ordering: SetupStateDirectory's device.json icacls grant
            // runs before the file exists; re-grant right after we just created it.
            RunIcacls($"\"{path}\" /grant \"{SidUsers}:R\"", "device-json-users");
        }

        _lastPairingSecret = secret;
        _lastPairingCode = code;
    }

    // Pairing secret/code as of the last device.json write. Rotation is lazy
    // (inside PairingManager.Current, on 10-min TTL expiry) and does NOT touch
    // device.json by itself - this tracking lets the boundary tick notice a
    // rotation and refresh the file, keeping the tray QR in sync.
    private string? _lastPairingSecret;
    private string? _lastPairingCode;

    private void RefreshPairingArtifactsIfNeeded()
    {
        try
        {
            var cur = _pairing.Current; // lazily rotates stale credentials
            if (cur.Secret != _lastPairingSecret || cur.ManualCode != _lastPairingCode)
            {
                WriteDeviceJson();
                _logger.LogInformation("Pairing credentials rotated; device.json refreshed for the tray QR");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pairing artifact refresh failed");
        }
    }

    private void SubscribeEvents()
    {
        _syncEngine.ControlApplied += snapshot => _ = HandleControlAppliedAsync(snapshot);
        _syncEngine.ControlRejected += reason => _logger.LogWarning("Control revision rejected: {Reason}", reason);
        _syncEngine.CommandReceived += raw => _ = HandleCommandsStreamAsync(raw);
        // The SSE client reconnects on its own; surface the errors so stream outages
        // are diagnosable from the service log instead of being swallowed.
        _syncEngine.StreamError += (path, error) =>
            _logger.LogWarning(error, "Sync stream {Path} error (client will reconnect)", path);

        _pipeHost.ForegroundReceived += (appKey, exePath, windowTitle) =>
            RunSafe("foreground", () => OnForegroundReceived(appKey, exePath, windowTitle));
        _pipeHost.TabClosedReceived += url =>
        {
            try
            {
                File.AppendAllText(Path.Combine(_stateDir, "block-actions.log"),
                    $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} tab closed url={url}\r\n");
            }
            catch
            {
                // diagnostics only
            }
        };
        _pipeHost.BrowserReceived += snapshot =>
            _ = RunSafeAsync("browser", () => OnBrowserReceivedAsync(snapshot));
        _pipeHost.PinReceived += digits => RunSafe("pin", () => OnPinReceived(digits));
        _pipeHost.AskParentReceived += appKey => _ = RunSafeAsync("ask-parent", () => CreateUnlockRequestAsync(appKey));
        _pipeHost.SetupClosedReceived += () => _logger.LogInformation("Session agent setup window closed");
        _pipeHost.HelloReceived += (session, pid) =>
        {
            _logger.LogDebug("Agent hello session={Session} pid={Pid}", session, pid);
            RunSafe("hello-reevaluate", () =>
            {
                EvaluateCurrentForeground();
                var (helloLocked, helloReason) = DecideFor(_currentAppKey ?? "");
                if (!helloLocked)
                {
                    _suspender.ResumeAll();
                    _pipeHost.BroadcastUnlock();
                }
                else if (!string.IsNullOrEmpty(_currentAppKey))
                {
                    _pipeHost.BroadcastLock(_currentAppKey, LabelFor(_currentAppKey), helloReason);
                }
            });
            // The first hello can race owner-uid recovery (both start at service boot);
            // if the uid is still unknown, retry in the background - its success path
            // re-broadcasts, so the tray reflects reality without a pipe reconnect.
            _ = RunSafeAsync("owner-recover-hello", EnsureOwnerUidAsync);
            _pipeHost.BroadcastPairedState(IsPaired);
        };
        _pipeHost.AdminStateReceived += (session, isAdmin) => RunSafe("admin-state", () => OnAdminStateReceived(session, isAdmin));
        // Setup window pairing credentials: served from the secret store (PairingManager),
        // NOT device.json — device.json is child-readable and must not carry the secret.
        _pipeHost.DeviceInfoRequest += req =>
        {
            try
            {
                var (deviceId, secret, code) = _pairing.GetOrCreate();
                return JsonSerializer.Serialize(
                    new { t = "deviceInfo", req, deviceId, secret, code }, JsonOpts);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "deviceInfo request failed");
                return null;
            }
        };


        _watchdog.TamperDetected += (type, message) => _ = RunSafeAsync("tamper-push", () => PushTamperEventAsync(type, message));
        _ledger.ClockTampered += jumpMs => _ = RunSafeAsync("clock-tamper-push", () =>
            PushTamperEventAsync("clockTampered",
                $"System clock jumped backwards by {jumpMs / 1000}s while usage was being tracked; usage preserved via monotonic clock."));
    }


    /// <summary>Builds the controlState DTO shared by the local (this-laptop) and remote (parent
    /// console) views. <paramref name="thisDevice"/> controls whether the local app inventory and
    /// per-app ledger usage are included (only meaningful for this laptop).</summary>

    /// <summary>Serializes just the state DTO (no envelope) — the device-state cache stores
    /// this shape so a cached hit can be re-wrapped with the caller's fresh req id.</summary>

    /// <summary>
    /// One inventoried app for the console merge. Key is the base64url package key;
    /// Blockable/ProtectedReason come from the device's own inventory upload.
    /// </summary>

    private List<object?> BuildModesList(ControlSnapshotV2? snapshot, out string? activeId, out string? activeName)
    {
        activeId = snapshot?.ActiveMode?.ModeId;
        activeName = null;
        var modes = new List<object?>();
        if (snapshot?.Modes != null)
        {
            var byId = new Dictionary<string, ControlMode>(StringComparer.OrdinalIgnoreCase);
            foreach (var (modeId, mode) in snapshot.Modes)
            {
                byId[modeId] = mode;
                // Full per-mode app rules: the console's mode editor needs the same rows
                // the phone's Modes card edits (lock switch + per-mode daily limit).
                var modeApps = new List<object?>();
                if (mode.Apps != null)
                {
                    foreach (var (packageName, rule) in mode.Apps)
                    {
                        modeApps.Add(new
                        {
                            key = PackageKeys.Encode(packageName),
                            packageName,
                            label = LabelFor(packageName),
                            blocked = rule.ManualBlocked,
                            dailyLimitMinutes = rule.DailyLimitMinutes,
                        });
                    }
                }

                modes.Add(new
                {
                    modeId,
                    name = mode.Name,
                    createdAt = mode.CreatedAt,
                    updatedAt = mode.UpdatedAt,
                    appCount = mode.Apps?.Count ?? 0,
                    apps = modeApps,
                });
            }

            if (activeId != null && byId.TryGetValue(activeId, out var activeMode))
            {
                activeName = activeMode.Name;
            }
        }

        return modes;
    }

    private List<object?> BuildUsageList(ControlSnapshotV2? snapshot)
    {
        var usage = new List<object?>();
        if (snapshot != null)
        {
            foreach (var (packageName, rule) in snapshot.EffectiveApps())
            {
                var usageMs = _ledger.EffectiveUsageMsToday(packageName);
                if (usageMs <= 0)
                {
                    continue;
                }

                usage.Add(new
                {
                    key = PackageKeys.Encode(packageName),
                    label = LabelFor(packageName),
                    minutes = usageMs / 60_000L,
                    ms = usageMs,
                    lockBlocked = rule.DailyLimitMinutes is int limit && usageMs >= (long)limit * 60_000L,
                });
            }
        }

        return usage;
    }

    /// <summary>Parses a device-list node (users/{ownerUid}/devices) into cards for the console.</summary>
    private List<object?> ParseDeviceList(string? json)
    {
        var list = new List<object?>();
        if (string.IsNullOrWhiteSpace(json) || (json = json!.Trim()) == "null")
        {
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return list;
            }

            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var id = p.Name;
                var label = id;
                var online = false;
                long lastSeen = 0;
                string? platform = null;
                string? enforcementMode = null;
                bool? protectionHealthy = null;
                if (p.Value.ValueKind == JsonValueKind.Object)
                {
                    var v = p.Value;
                    if (v.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String)
                    {
                        label = l.GetString() ?? id;
                    }

                    online = v.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.True;
                    if (v.TryGetProperty("lastSeen", out var ls) && ls.ValueKind == JsonValueKind.Number)
                    {
                        lastSeen = ls.GetInt64();
                    }

                    if (v.TryGetProperty("platform", out var pl) && pl.ValueKind == JsonValueKind.String)
                    {
                        platform = pl.GetString();
                    }

                    if (v.TryGetProperty("enforcementMode", out var em) && em.ValueKind == JsonValueKind.String)
                    {
                        enforcementMode = em.GetString();
                    }

                    if (v.TryGetProperty("protectionHealthy", out var ph)
                        && (ph.ValueKind == JsonValueKind.True || ph.ValueKind == JsonValueKind.False))
                    {
                        protectionHealthy = ph.GetBoolean();
                    }
                }

                list.Add(new { deviceId = id, label, online, lastSeen, platform, enforcementMode, protectionHealthy });
            }
        }
        catch (JsonException)
        {
            // best effort: return whatever we managed to parse
        }

        return list;
    }

    /// <summary>Parses devices/{id}/state/apps into a usage list (best-effort) for the console.
    /// labelByPkg supplies the device's own inventory labels; falls back to local labels.</summary>
    private List<object?> ParseAppsState(string? json, Dictionary<string, string>? labelByPkg = null)
    {
        var list = new List<object?>();
        if (string.IsNullOrWhiteSpace(json) || (json = json!.Trim()) == "null")
        {
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return list;
            }

            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var ms = 0L;
                if (p.Value.TryGetProperty("usageMs", out var um) && um.ValueKind == JsonValueKind.Number)
                {
                    ms = um.GetInt64();
                }
                else if (p.Value.TryGetProperty("minutes", out var mn) && mn.ValueKind == JsonValueKind.Number)
                {
                    ms = mn.GetInt64() * 60_000L;
                }

var minutes = ms / 60_000L;
	                // Keep every entry the TV reported: lockBlocked is the TV's ACTUAL
	                // enforcement state, and the phone uses it instead of the desired rule.
	                // Label from the entry's own packageName (devices publish it);
                    // fall back to decoding the packageKey — never render the raw
                    // base64url key as the label.
                    var packageName = p.Value.TryGetProperty("packageName", out var pn) && pn.ValueKind == JsonValueKind.String
                        ? pn.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(packageName))
                    {
                        try
                        {
                            packageName = PackageKeys.Decode(p.Name);
                        }
                        catch (Exception ex) when (ex is FormatException or ArgumentException)
                        {
                            packageName = p.Name;
                        }
                    }

                    // Prefer the device's own inventory label for its app.
                    var label = packageName != null && labelByPkg != null && labelByPkg.TryGetValue(packageName, out var invLabel)
                        ? invLabel
                        : LabelFor(packageName);

                    list.Add(new
                    {
                        key = p.Name,
                        label,
                        minutes,
                        ms,
                        // The device writes this when its daily-limit lock trips; the
                        // console highlights the usage chip red, like the phone.
                        lockBlocked = p.Value.TryGetProperty("lockBlocked", out var lb) && lb.ValueKind == JsonValueKind.True,
                    });
                }
            }
            catch (JsonException)
        {
            // best effort
        }

        return list;
    }

    /// <summary>Parses devices/{id}/unlockRequests into the console's pending-requests list (newest first, capped at 50).</summary>
    private List<object?> ParsePendingUnlocks(string? json)
    {
        var items = new List<(long SortKey, object? Item)>();
        if (string.IsNullOrWhiteSpace(json) || (json = json!.Trim()) == "null")
        {
            return [];
        }

        var now = _syncEngine.ServerNowMs();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var r = prop.Value;
                if (r.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var status = r.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (!string.Equals(status, PolicyConstants.UNLOCK_PENDING, StringComparison.Ordinal))
                {
                    continue;
                }

                var expiresAt = r.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var ex) ? ex : (long?)null;
                if (expiresAt != null && expiresAt <= now)
                {
                    continue; // expired pending requests are not actionable
                }

                var packageName = r.TryGetProperty("packageName", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : "";
                var reason = r.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() : null;
                var createdAt = r.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.Number && ca.TryGetInt64(out var c) ? c : (long?)null;
                items.Add((createdAt ?? 0, new
                {
                    requestId = prop.Name,
                    packageName,
                    label = LabelFor(packageName ?? ""),
                    reason,
                    createdAt,
                    expiresAt,
                }));
            }
        }
        catch (JsonException)
        {
            // best effort
        }

        return items.OrderByDescending(i => i.SortKey).Take(50).Select(i => i.Item).ToList();
    }

    /// <summary>Parses devices/{id}/tamperEvents into the console's event list (newest first, capped at 50).</summary>
    private List<object?> ParseTamperEvents(string? json)
    {
        var items = new List<(long SortKey, object? Item)>();
        if (string.IsNullOrWhiteSpace(json) || (json = json!.Trim()) == "null")
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var r = prop.Value;
                if (r.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = r.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "";
                var message = r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                var createdAt = r.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.Number && ca.TryGetInt64(out var c) ? c : (long?)null;
                items.Add((createdAt ?? 0, new { type, message, createdAt }));
            }
        }
        catch (JsonException)
        {
            // best effort
        }

        return items.OrderByDescending(i => i.SortKey).Take(50).Select(i => i.Item).ToList();
    }

    /// <summary>Parses a device's uploaded inventory (devices/{id}/apps) for the console merge.</summary>

    // ----------------------------------------------------------------- control
    private async Task HandleControlAppliedAsync(ControlSnapshotV2 snapshot)
    {
        try
        {
            _logger.LogInformation("Applying control revision {RevisionId}", snapshot.RevisionId);

            // ENFORCEMENT FIRST: the suspend/wall lands before ANY disk write. A slow
            // disk or AV scan on policy-cache.json used to gate every lock here.
            EvaluateCurrentForeground();
            _ = Task.Run(() => WritePolicyCache(snapshot));

            _ = Task.Run(async () =>
            {
                // Ack FIRST (before the hosts rewrite): the parent's Sync card reflects
                // enforcement without waiting on file I/O.
                try
                {
                    await _syncEngine.NotifyEnforcementAppliedAsync(snapshot.RevisionId);

                    TryWriteText(Path.Combine(_stateDir, "enforcement-state.json"),
                        new JsonObject { ["revisionId"] = snapshot.RevisionId, ["appliedAtMs"] = NowMs() }.ToJsonString(JsonOpts));

                    ApplyContentFilterHosts(snapshot);
                    LogPipelineLatency(snapshot);
                }
                catch (Exception ackEx)
                {
                    _logger.LogWarning(ackEx, "Failed to acknowledge revision {RevisionId}", snapshot.RevisionId);
                }

                try
                {
                    await UploadStatesAsync(true);
                }
                catch (Exception upEx)
                {
                    _logger.LogWarning(upEx, "State upload after apply failed for {RevisionId}", snapshot.RevisionId);
                }

            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply control revision {RevisionId}", snapshot.RevisionId);
            try
            {
                await _syncEngine.NotifyEnforcementFailedAsync(snapshot.RevisionId, ex.Message);
            }
            catch (Exception ackEx)
            {
                _logger.LogWarning(ackEx, "Failed to report enforcement failure for {RevisionId}", snapshot.RevisionId);
            }
        }
    }

    /// <summary>Logs the measured phone→laptop pipeline latency for the applied revision
    /// (server-corrected now − the phone's server-stamped requestedAt).</summary>
    private void LogPipelineLatency(ControlSnapshotV2 snapshot)
    {
        try
        {
            var latency = _syncEngine.PipelineLatencyMs;
            if (latency is not null)
            {
                _logger.LogInformation(
                    "Control revision {RevisionId} applied in {LatencyMs} ms (phone→laptop)",
                    snapshot.RevisionId, latency);
            }
        }
        catch
        {
            // diagnostics only
        }
    }

    /// <summary>
    /// Writes the fail-closed policy cache the session agent consults while the pipe is
    /// dead: the resolved blocked-app set (manual blocks, default-locked bypass rows and
    /// apps already over their daily limit) plus the Safe Mode flag. Never contains the
    /// PIN; a null snapshot (unpaired) writes an empty, nothing-blocked cache.
    /// </summary>
    private void WritePolicyCache(ControlSnapshotV2? snapshot)
    {
        try
        {
            var blocked = new JsonArray();
            var dailyBlocked = new JsonArray();
            var sessionBlocked = new JsonArray();
            var safeMode = false;

            if (snapshot is not null)
            {
                safeMode = _enforcement.SafeModeActive(snapshot, _syncEngine.ServerNowMs());
                var apps = snapshot.EffectiveApps();
                foreach (var (appKey, rule) in apps)
                {
                    if (rule.ManualBlocked)
                    {
                        blocked.Add(appKey);
                    }
                    else if (rule.DailyLimitMinutes is int minutes
                        && _ledger.EffectiveUsageMsToday(appKey) >= (long)minutes * 60_000L)
                    {
                        dailyBlocked.Add(appKey);
                    }
                    else if (rule.SessionLimitMinutes is int sessionMinutes
                        && _sessions.EffectiveSessionMs(appKey, NowMs()) >= (long)sessionMinutes * 60_000L)
                    {
                        sessionBlocked.Add(appKey);
                    }
                }

                // Bypass tools without any rule row are default-locked.
                foreach (var bypass in PolicyConstants.WindowsBypassPackages)
                {
                    if (!apps.ContainsKey(bypass))
                    {
                        blocked.Add(bypass);
                    }
                }
            }

            var payload = new JsonObject
            {
                ["writtenAtMs"] = NowMs(),
                ["safeMode"] = safeMode,
                ["blockedApps"] = blocked,
                ["dailyBlockedApps"] = dailyBlocked,
                ["sessionBlockedApps"] = sessionBlocked
            };
            TryWriteText(Path.Combine(_stateDir, "policy-cache.json"), payload.ToJsonString(JsonOpts));

            // The cache must stay readable by the session agent even where ProgramData
            // inheritance is stricter than default; grant once per service run.
            if (!_policyCacheAclApplied)
            {
                RunIcacls($"\"{Path.Combine(_stateDir, "policy-cache.json")}\" /grant \"{SidUsers}:R\"", "policy-cache-users");
                _policyCacheAclApplied = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Policy cache write failed");
        }
    }

    /// <summary>
    /// Rewrites the GuardPulse block in the hosts file for the snapshot's content-filter
    /// categories plus custom domains (SYSTEM can write hosts; the child account cannot).
    /// No categories and no custom domains removes the block. The DNS cache is flushed after a change.
    /// </summary>
    private void ApplyContentFilterHosts(ControlSnapshotV2 snapshot)
    {
        try
        {
            var hostsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers", "etc", "hosts");
            var blocklistDir = Path.Combine(AppContext.BaseDirectory, "content-blocklists");
            var filter = snapshot.ContentFilter;
            var categories = new Dictionary<string, IEnumerable<string>>();
            if (filter is { Social: true }) categories["social"] = HostsFileRewriter.LoadDomains(blocklistDir, "social");
            if (filter is { Gambling: true }) categories["gambling"] = HostsFileRewriter.LoadDomains(blocklistDir, "gambling");
            if (filter is { Adult: true }) categories["adult"] = HostsFileRewriter.LoadDomains(blocklistDir, "adult");
            if (filter is { Gaming: true }) categories["gaming"] = HostsFileRewriter.LoadDomains(blocklistDir, "gaming");

            var customUrls = snapshot.CustomBlockedDomains?.Domains ?? Array.Empty<string>();
            var pureDomains = customUrls.Where(u => !HasUrlPath(u)).ToList();
            var custom = NormalizeCustomBlockedDomains(pureDomains);
            if (custom.Count > 0) categories["custom"] = custom;

            var block = categories.Count > 0 ? HostsFileRewriter.BuildBlock(categories) : null;
            var current = File.Exists(hostsPath) ? File.ReadAllText(hostsPath) : "";
            var rewritten = HostsFileRewriter.ApplyBlock(current, block);
            if (rewritten != current)
            {
                AtomicFile.WriteAllText(hostsPath, rewritten);
                _ = FlushDnsAsync();
                _logger.LogInformation("Content filter hosts block updated ({Count} categories)", categories.Count);
            }

            BrowserPolicyManager.ApplyUrlBlocklist(customUrls);

            // Real-time layer: push per-tab rules to the session agent (UIA tab close).
            var (tabDomains, tabPaths) = BrowserPolicyManager.BuildTabRules(customUrls, categories.Values.SelectMany(d => d));
            _pipeHost.BroadcastTabRules(tabDomains, tabPaths);
            TryWriteText(Path.Combine(_stateDir, "block-rules.json"),
                System.Text.Json.JsonSerializer.Serialize(
                    new { domains = tabDomains, paths = tabPaths }, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Content filter hosts/browser update failed");
        }
    }

    private static bool HasUrlPath(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7);
        else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8);
        var slash = s.IndexOf('/');
        return slash >= 0 && slash < s.Length - 1;
    }

    private static IReadOnlyList<string> NormalizeCustomBlockedDomains(IReadOnlyList<string>? domains)
    {
        if (domains == null || domains.Count == 0) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in domains)
        {
            var normalized = NormalizeCustomDomain(raw);
            if (normalized == null || !seen.Add(normalized)) continue;
            result.Add(normalized);
            var www = "www." + normalized;
            if (seen.Add(www)) result.Add(www);
        }

        return result;
    }

    private static string? NormalizeCustomDomain(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        if (s.StartsWith("http://", StringComparison.Ordinal)) s = s.Substring(7);
        else if (s.StartsWith("https://", StringComparison.Ordinal)) s = s.Substring(8);
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s.Substring(0, slash);
        s = s.TrimEnd('.');
        if (s.Length == 0 || s.Length > 253 || s.Contains("..") || s.StartsWith("-") || s.StartsWith(".")) return null;
        var labels = s.Split('.');
        if (labels.Length < 2) return null;
        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Length > 63 || label.StartsWith("-") || label.EndsWith("-")) return null;
            foreach (var ch in label) if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-')) return null;
        }

        var tld = labels[labels.Length - 1];
        if (tld.Length < 2) return null;
        foreach (var ch in tld) if (ch < 'a' || ch > 'z') return null;
        return s;
    }

    /// <summary>Flush the DNS cache so removed blocks take effect immediately.</summary>
    private async Task FlushDnsAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe"),
                Arguments = "/flushdns",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var process = System.Diagnostics.Process.Start(psi);
            await Task.Run(() => process?.WaitForExit(5000));
        }
        catch (Exception)
        {
            // best effort: stale DNS entries expire on their own
        }
    }

    private async Task EnsureSyncStartedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_syncStarted)
        {
            try
            {
                await _syncEngine.StartAsync(ct);
                _syncStarted = true;
                _logger.LogInformation("Sync engine started (session {SessionId})", _syncEngine.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sync engine start failed; retrying in 60s (local protection stays active)");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    // ------------------------------------------------------------- enforcement
    private void EvaluateCurrentForeground()
    {
        string? appKey;
        lock (_gate)
        {
            appKey = _currentAppKey;
        }

        if (string.IsNullOrEmpty(appKey))
        {
            return;
        }

        var (locked, reason) = DecideFor(appKey);
        if (locked && string.Equals(reason, "dailyLimit", StringComparison.OrdinalIgnoreCase))
        {
            _ledger.MarkDailyBlocked(appKey);
        }

        ApplyDecision(appKey, locked, reason);
    }

    // --------------------------------------------------------------- dead-man lock
    /// <summary>
    /// If the session agent vanishes while a lock decision is active, the overlay is gone
    /// and only process suspension remains — so we escalate to a full lockdown: every
    /// non-essential interactive process is suspended until an agent reconnects.
    /// </summary>
    private void DeadManCheck()
    {
        bool lockWanted;
        lock (_gate)
        {
            lockWanted = _currentOverlay == "locked";
        }

        if (!lockWanted)
        {
            if (_lockdownActive)
            {
                ExitLockdownState("lock no longer active");
            }

            return;
        }

        if (_pipeHost.ConnectedAgents > 0)
        {
            _lastAgentSeenMs = NowMs();
            if (_lockdownActive)
            {
                // Re-apply the current decision first so still-locked apps get
                // retagged; ExitLockdown then resumes only the rest.
                EvaluateCurrentForeground();
                ExitLockdownState("agent reconnected");
            }

            return;
        }

        if (_lockdownActive)
        {
            // Sweep again: processes launched while the overlay is gone get caught too.
            _suspender.SuspendAllForLockdown();
        }
        else if (NowMs() - _lastAgentSeenMs > DeadManGrace.TotalMilliseconds)
        {
            _lockdownActive = true;
            var count = _suspender.SuspendAllForLockdown();
            _logger.LogWarning(
                "DEAD-MAN LOCKDOWN: no session agent for over {Grace}s while a lock is active; suspended {Count} processes",
                DeadManGrace.TotalSeconds, count);
            _ = RunSafeAsync("tamper-push", () => PushTamperEventAsync("agentMissing",
                $"The protection agent stopped responding for over {DeadManGrace.TotalSeconds:N0}s while an app was locked. " +
                "The device was locked down as a precaution until the agent returned."));
        }
    }

    private void ExitLockdownState(string reason)
    {
        _lockdownActive = false;
        _suspender.ExitLockdown();
        _logger.LogWarning("Dead-man lockdown lifted: {Reason}", reason);
    }

    /// <summary>
    /// The agent reports whether its logon account holds administrator rights; an admin
    /// child can defeat local protection, so the parent is warned (at most once a day).
    /// </summary>
    private void OnAdminStateReceived(int session, bool isAdmin)
    {
        if (!isAdmin)
        {
            return;
        }

        var dayKey = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone)
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (string.Equals(_adminTamperDayKey, dayKey, StringComparison.Ordinal))
        {
            return;
        }

        _adminTamperDayKey = dayKey;
        _logger.LogWarning("Session {Session} runs with administrator rights", session);
        _ = RunSafeAsync("tamper-push", () => PushTamperEventAsync("childAccountIsAdmin",
            "The child's Windows account has administrator rights — protection cannot be guaranteed. " +
            "Use a standard (non-admin) account for the child."));
    }

    private (bool Locked, string Reason) DecideFor(string appKey)
    {
        var snapshot = _syncEngine.LastValidSnapshot;
        if (snapshot is null)
        {
            // No valid snapshot ever persisted (fresh install / never paired). Lock
            // NOTHING — including bypass packages — because there is no owner policy
            // to enforce and no unlock path (no PIN, no parent app). Locking the
            // user's own terminal here would trap them with no way out. Once the
            // device is paired and receives control, normal enforcement resumes.
            return (false, "none");
        }

        // Server clock: safeMode.Until and unlock deadlines are written against the
        // RTDB/server time, so a skewed local clock must not mis-arm Safe Mode or
        // extend/truncate timed unlocks.
        var serverNowMs = _syncEngine.ServerNowMs();
        var decision = _enforcement.Decide(snapshot, appKey, _ledger, _unlocks, _agentAppKey, serverNowMs, _sessions, NowMs());
        return (decision.Locked, decision.Reason);
    }

    private void ApplyDecision(string appKey, bool locked, string reason)
    {
        var wasLocked = false;
        lock (_gate)
        {
            wasLocked = _currentOverlay == "locked";
        }

        if (locked)
        {
            if (reason is PolicyConstants.BLOCK_REASON_SCHEDULE or PolicyConstants.BLOCK_REASON_BUDGET)
            {
                // Whole-device lock: the overlay covers the desktop and the lockdown
                // whitelist suspends everything non-essential (same rules as the
                // dead-man lockdown; OS essentials and our own exes stay alive).
                var lockedDown = _suspender.SuspendAllForLockdown();
                _logger.LogInformation("Locked {AppKey} (reason={Reason}, lockdown suspended={Suspended})", appKey, reason, lockedDown);
            }
            else
            {
                var suspended = _suspender.SuspendProcessesForApp(appKey);
                if (suspended == 0 && _suspender.HasMatchingProcesses(appKey))
                {
                    _suspender.TerminateFallback(appKey);
                }

                _logger.LogInformation("Locked {AppKey} (reason={Reason}, suspended={Suspended})", appKey, reason, suspended);
            }

            // Record the lock state BEFORE broadcasting: if a broadcast throws, the
            // dead-man still sees "locked" and must not resume the suspended apps.
            lock (_gate)
            {
                _currentOverlay = "locked";
            }

            _pipeHost.BroadcastLock(appKey, LabelFor(appKey), reason);
            _pipeHost.BroadcastPinState(_syncEngine.LastValidSnapshot?.Pin is not null, _pinRetry.BlockedUntilMs());
            _activity.SetOverlayState("locked");
        }
        else
        {
            lock (_gate)
            {
                _currentOverlay = "none";
            }

            if (wasLocked)
            {
                _suspender.ResumeAll();
                _pipeHost.BroadcastUnlock();
            }

            _activity.SetOverlayState("none");
        }

        _ = RunSafeAsync("activity-flush", () => FlushActivityAsync(true));
        _ = RunSafeAsync("state-upload", () => UploadStatesAsync(true));
        _pipeHost.BroadcastActivity(LabelFor(appKey), locked ? "locked" : "none");
    }

    private void OnForegroundReceived(string rawAppKey, string? exePath, string? windowTitle)
    {
        // The agent's own UI (lock overlay, setup window) being foreground is not a
        // user app switch: switching to it would revoke the locked app's unlock
        // grant and broadcast unlock, tearing the lock down the moment it shows.
        if (string.Equals(rawAppKey, "__agent__", StringComparison.Ordinal))
        {
            return;
        }

        var appKey = ResolveAppKey(rawAppKey, exePath, windowTitle);
        if (string.IsNullOrEmpty(appKey))
        {
            return;
        }

        bool sameApp;
        lock (_gate)
        {
            sameApp = string.Equals(_currentAppKey, appKey, StringComparison.OrdinalIgnoreCase);
        }

        if (sameApp)
        {
            // Same app — but it may have just relaunched while a lock decision was
            // active; returning here would let the fresh process free-run until the
            // next periodic evaluation. Re-verify immediately. When the decision is
            // "unlocked" this is a no-op, so ordinary window switches inside one app
            // don't trigger repeated flushes/uploads.
            var (locked, reason) = DecideFor(appKey);
            if (locked)
            {
                if (string.Equals(reason, "dailyLimit", StringComparison.OrdinalIgnoreCase))
                {
                    _ledger.MarkDailyBlocked(appKey);
                }

                ApplyDecision(appKey, locked, reason);
            }

            return;
        }

        var now = NowMs();
        string? previous;
        var label = LabelFor(appKey);
        lock (_gate)
        {
            previous = _currentAppKey;
            _currentAppKey = appKey;
            _currentLabel = label;
            _currentStartedAtMs = now;
        }

        _activity.CloseCurrent(now);
        if (previous is not null)
        {
            _unlocks.Clear(previous);
        }

        _ledger.OnForegroundChanged(appKey, now);
        _activity.StartApp(appKey, label, now);
        _logger.LogDebug("Foreground -> {AppKey}", appKey);

        EvaluateCurrentForeground();
    }

    private static string ResolveAppKey(string rawAppKey, string? exePath, string? windowTitle)
    {
        // The agent's self-marker must never be resolved to a file path.
        if (string.Equals(rawAppKey, "__agent__", StringComparison.Ordinal))
        {
            return rawAppKey;
        }

        var path = string.IsNullOrWhiteSpace(exePath) ? rawAppKey : exePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            var bypass = InventoryScanner.MatchBypassRow(path, windowTitle);
            if (!string.IsNullOrEmpty(bypass))
            {
                return bypass;
            }
        }
        catch
        {
            // scanner hiccups fall through to path-based keying
        }

        try
        {
            return InventoryScanner.AppKeyForProcess(path);
        }
        catch
        {
            return path.ToLowerInvariant();
        }
    }

    private string LabelFor(string appKey)
    {
        lock (_labelLock)
        {
            if (_labelByAppKey.TryGetValue(appKey, out var label) && !string.IsNullOrEmpty(label))
            {
                return label;
            }
        }

        if (BypassLabels.TryGetValue(appKey, out var bypassLabel))
        {
            return bypassLabel;
        }

        // Settings-section virtual packages come seeded from the phone; give them the
        // phone's friendly names instead of the raw package id.
        var sectionPolicy = PolicyConstants.SettingsSectionPolicyFor(appKey);
        if (sectionPolicy != null)
        {
            return sectionPolicy.Label;
        }

        return Path.GetFileName(appKey);
    }

    // --------------------------------------------------------------------- PIN
    private void OnPinReceived(string digits)
    {
        var pin = _syncEngine.LastValidSnapshot?.Pin;
        if (_pinRetry.IsBlocked())
        {
            _pipeHost.BroadcastPinState(pin is not null, _pinRetry.BlockedUntilMs());
            return;
        }

        var ok = false;
        if (pin is not null && !string.IsNullOrEmpty(pin.Salt) && !string.IsNullOrEmpty(pin.Hash))
        {
            try
            {
                ok = PinHasher.Verify(digits, pin.Salt, pin.Hash, pin.Version, pin.Algorithm, pin.Iterations);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PIN verify threw");
            }
        }

        if (ok)
        {
            _pinRetry.RecordSuccess();
            string? appKey;
            lock (_gate)
            {
                appKey = _currentAppKey;
            }

            if (!string.IsNullOrEmpty(appKey))
            {
                // Server clock: timed-expiry bookkeeping stays consistent with RTDB
                // timestamps even when the laptop clock is skewed (one-visit grants
                // carry no deadline, so this is a no-op for them).
                _unlocks.GrantAt(appKey, _syncEngine.ServerNowMs());
                // The approved visit starts a FRESH session: without this the session
                // accumulator (still >= limit) would re-lock the app immediately.
                _sessions.Reset(appKey);
            }

            _suspender.ResumeAll();
            _pipeHost.BroadcastUnlock();
            _activity.SetOverlayState("none");
            lock (_gate)
            {
                _currentOverlay = "none";
            }

            _logger.LogInformation("PIN accepted; one-visit unlock granted for {AppKey}", appKey);
            // Same foreground re-evaluation the unlock-approval path uses: the grant can
            // flip the decision for the focused app (and refresh any still-locked ones).
            EvaluateCurrentForeground();
            _ = RunSafeAsync("state-upload", () => UploadStatesAsync(true));
        }
        else
        {
            _pinRetry.RecordFailure();
            _pipeHost.BroadcastPinState(pin is not null, _pinRetry.BlockedUntilMs());
            _logger.LogInformation("PIN rejected");
        }
    }

    // ---------------------------------------------------------- unlock requests
    private async Task CreateUnlockRequestAsync(string appKey)
    {
        if (string.IsNullOrEmpty(appKey))
        {
            return;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var payload = new JsonObject
        {
            ["requestId"] = requestId,
            ["packageName"] = appKey,
            ["reason"] = "askParent",
            ["status"] = PolicyConstants.UNLOCK_PENDING,
            ["createdAt"] = Sv(),
            // Server clock, not the local one: the phone app filters pending
            // requests with ServerNowMs, so a skewed local clock could hide or
            // resurrect requests.
            ["expiresAt"] = _syncEngine.ServerNowMs() + 10 * 60_000L
        };
        await _firebase.PutAsync(FirebasePaths.DeviceUnlockRequest(_deviceId, requestId), payload.ToJsonString(JsonOpts), _ct);
        _logger.LogInformation("Created parent unlock request {RequestId} for {AppKey}", requestId, appKey);
    }

    private async Task PollUnlockRequestsAsync()
    {
        var json = await _firebase.GetAsync(FirebasePaths.DeviceUnlockRequests(_deviceId), _ct);
        await HandleUnlockJsonAsync(json);
    }

    private async Task HandleUnlockJsonAsync(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            return;
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var requestId = property.Name;
            lock (_dedupeGate)
            {
                if (_handledUnlockRequests.Contains(requestId))
                {
                    continue;
                }
            }

            // Retention: terminal requests the TV already applied, or anything older
            // than 7 days, is deleted so the unlockRequests node does not grow forever.
            var createdAt = GetLong(property.Value, "createdAt");
            var serverNow = _syncEngine.ServerNowMs();
            var tvApplyStatus = GetString(property.Value, "tvApplyStatus");
            var status = GetString(property.Value, "status");
            var pastRetention = createdAt > 0 && serverNow - createdAt > (long)UnlockRequestRetention.TotalMilliseconds;
            if (pastRetention
                || (status is PolicyConstants.UNLOCK_DENIED or PolicyConstants.UNLOCK_EXPIRED
                    && string.Equals(tvApplyStatus, PolicyConstants.SYNC_STATUS_APPLIED, StringComparison.Ordinal)))
            {
                lock (_dedupeGate)
                {
                    _handledUnlockRequests.Add(requestId);
                }

                await _firebase.PutAsync(FirebasePaths.DeviceUnlockRequest(_deviceId, requestId), "null", _ct);
                continue;
            }

            // Approvals past their TTL must never re-grant after a restart. Server-time
            // based: expiresAt is written from the server clock (owner side filters the
            // same way), so a skewed local clock can't resurrect expired approvals.
            var expiresAt = GetLong(property.Value, "expiresAt");
            if (expiresAt > 0 && serverNow > expiresAt)
            {
                lock (_dedupeGate)
                {
                    _handledUnlockRequests.Add(requestId);
                }

                continue;
            }

            // The TV apply marker survives restarts: without this check a replayed
            // still-approved request would be re-granted on every service start.
            if (string.Equals(tvApplyStatus, PolicyConstants.SYNC_STATUS_APPLIED, StringComparison.Ordinal))
            {
                lock (_dedupeGate)
                {
                    _handledUnlockRequests.Add(requestId);
                }

                continue;
            }

            if (status != PolicyConstants.UNLOCK_APPROVED)
            {
                if (status is PolicyConstants.UNLOCK_DENIED or PolicyConstants.UNLOCK_EXPIRED)
                {
                    lock (_dedupeGate)
                    {
                        _handledUnlockRequests.Add(requestId);
                    }
                }

                continue;
            }

            lock (_dedupeGate)
            {
                _handledUnlockRequests.Add(requestId);
                if (_handledUnlockRequests.Count > HandledUnlockRequestsMax)
                {
                    // Bound the dedupe set the same way _processedCommands is bounded.
                    foreach (var stale in _handledUnlockRequests.Take(_handledUnlockRequests.Count - HandledUnlockRequestsMax))
                    {
                        _handledUnlockRequests.Remove(stale);
                    }
                }
            }

            await ApplyApprovedUnlockAsync(requestId, property.Value);
        }
    }

    private async Task ApplyApprovedUnlockAsync(string requestId, JsonElement value)
    {
        var appKey = GetString(value, "packageName");
        var approvalType = GetString(value, "approvalType");
        long? durationMs = GetNullableLong(value, "approvalDurationMs");

        TimeSpan? duration = approvalType == PolicyConstants.UNLOCK_APPROVAL_TIMED
            ? TimeSpan.FromMilliseconds(durationMs ?? 15 * 60_000L)
            : null;

        if (!string.IsNullOrEmpty(appKey))
        {
            // Server clock: approvalDurationMs is measured from the owner's (server)
            // time, so a skewed local clock must not shorten or extend the window.
            _unlocks.GrantAt(appKey, _syncEngine.ServerNowMs(), duration);
            // Same as the PIN path: the approved window opens a fresh session.
            _sessions.Reset(appKey);
        }

        _suspender.ResumeAll();
        _pipeHost.BroadcastUnlock();
        _activity.SetOverlayState("none");
        lock (_gate)
        {
            _currentOverlay = "none";
        }

        var patch = new JsonObject
        {
            ["tvApplyStatus"] = PolicyConstants.SYNC_STATUS_APPLIED,
            ["tvAppliedAt"] = Sv()
        };
        await _firebase.PatchAsync(FirebasePaths.DeviceUnlockRequest(_deviceId, requestId), patch.ToJsonString(JsonOpts), _ct);
        _logger.LogInformation("Applied parent unlock for {AppKey} (type={Type}, durationMs={Duration})",
            appKey, approvalType, durationMs);

        EvaluateCurrentForeground();
    }

    // ------------------------------------------------------------------ commands
    private async Task PollCommandsAsync()
    {
        var json = await _firebase.GetAsync(FirebasePaths.DeviceCommands(_deviceId), _ct);
        await HandleCommandsJsonAsync(json);
    }

    private async Task HandleCommandsJsonAsync(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            return;
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var commandId = property.Name;
            lock (_dedupeGate)
            {
                if (_processedCommands.Contains(commandId))
                {
                    continue;
                }
            }

            var value = property.Value;
            var status = GetString(value, "status");
            if (!string.IsNullOrEmpty(status) && status != PolicyConstants.COMMAND_PENDING)
            {
                lock (_dedupeGate)
                {
                    _processedCommands.Add(commandId);
                    TrimProcessedCommandsLocked();
                }

                continue;
            }

            var type = GetString(value, "type");
            if (string.IsNullOrEmpty(type))
            {
                continue;
            }

            // TTL against the SERVER clock: createdAt is written by the phone app
            // with server time, so a skewed laptop clock must not expire fresh commands
            // (or keep stale ones alive).
            var createdAt = GetLong(value, "createdAt");
            var ttl = CommandTtl(type);
            if (createdAt <= 0 || _syncEngine.ServerNowMs() > createdAt + (long)ttl.TotalMilliseconds)
            {
                // UNPAIR is the exception: an expired unpair still runs. Skipping it
                // deadlocks re-pairing forever — meta.ownerUid stays attached while the
                // phone already deleted the device, and the pair-request rule refuses
                // new requests for owned devices (permission denied on every retry).
                if (type == PolicyConstants.COMMAND_UNPAIR)
                {
                    _logger.LogWarning(
                        "Processing EXPIRED unpair (created {AgeMin:N0} min ago): a skipped one deadlocks re-pairing",
                        (_syncEngine.ServerNowMs() - createdAt) / 60_000);
                    await ExecuteCommandAsync(type, packageName: null);
                    await DeleteCommandAsync(commandId);
                    continue;
                }

                lock (_dedupeGate)
                {
                    _processedCommands.Add(commandId);
                    TrimProcessedCommandsLocked();
                }

                await FinishCommandAsync(commandId, PolicyConstants.COMMAND_EXPIRED, null);
                continue;
            }

            // Claim the command so a second service instance cannot double-run it.
            // Re-read first and stand down when another writer already flipped the
            // status away from pending (same pattern as CommandsLoop's claim). SSE
            // and the 5s poll can deliver the same pending command concurrently.
            var currentJson = await _firebase.GetAsync(
                FirebasePaths.DeviceCommands(_deviceId) + "/" + commandId, _ct);
            if (ReadCommandStatus(currentJson) is { } currentStatus
                && currentStatus != PolicyConstants.COMMAND_PENDING)
            {
                lock (_dedupeGate)
                {
                    _processedCommands.Add(commandId);
                    TrimProcessedCommandsLocked();
                }

                continue;
            }

            // "claimedAt" is the rules-whitelisted field (not the TV's startedAt).
            var claim = new JsonObject { ["status"] = PolicyConstants.COMMAND_RUNNING, ["claimedAt"] = Sv() };
            await _firebase.PatchAsync(
                FirebasePaths.DeviceCommands(_deviceId) + "/" + commandId, claim.ToJsonString(JsonOpts), _ct);

            lock (_dedupeGate)
            {
                _processedCommands.Add(commandId);
                TrimProcessedCommandsLocked();
            }

            try
            {
                await ExecuteCommandAsync(type, GetString(value, "packageName"));
                await FinishCommandAsync(commandId, PolicyConstants.COMMAND_DONE, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Command {Type} ({Id}) failed", type, commandId);
                await FinishCommandAsync(commandId, PolicyConstants.COMMAND_FAILED, ex.Message);
            }

            // Terminal commands are acked above; delete them so the node does not
            // grow forever and the SSE replay stays small.
            await DeleteCommandAsync(commandId);
        }
    }

    /// <summary>Oldest-first trim of the dedupe list, keeping the newest ProcessedCommandsKeep ids.</summary>
    private void TrimProcessedCommandsLocked()
    {
        if (_processedCommands.Count <= ProcessedCommandsMax)
        {
            return;
        }

        _processedCommands.RemoveRange(0, _processedCommands.Count - ProcessedCommandsKeep);
    }

    private static string? ReadCommandStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
            // treat malformed nodes as unclaimed
        }

        return null;
    }

    private async Task DeleteCommandAsync(string commandId)
    {
        try
        {
            await _firebase.PutAsync(
                FirebasePaths.DeviceCommands(_deviceId) + "/" + commandId, "null", _ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Command {Id} cleanup failed (retained until next sweep)", commandId);
        }
    }

    private static TimeSpan CommandTtl(string type) => type switch
    {
        PolicyConstants.COMMAND_OPEN_SETUP => TimeSpan.FromSeconds(60),
        PolicyConstants.COMMAND_UNPAIR => TimeSpan.FromMinutes(10),
        _ => StandardCommandTtl
    };

    private async Task ExecuteCommandAsync(string type, string? packageName)
    {
        switch (type)
        {
            case PolicyConstants.COMMAND_RESCAN_APPS:
                await UploadInventoryAsync();
                return;

            case PolicyConstants.COMMAND_RESET_TODAY:
                if (string.IsNullOrEmpty(packageName))
                {
                    foreach (var appKey in _ledger.UsageMsToday().Keys)
                    {
                        _ledger.SetResetOffset(appKey);
                    }

                    _sessions.ResetAll();
                }
                else
                {
                    _ledger.SetResetOffset(packageName);
                    _sessions.Reset(packageName);
                }

                _ledger.ClearDayBlocks();
                await UploadStatesAsync(true);
                _logger.LogInformation("resetToday applied (app={App})", packageName ?? "<all>");
                return;

            case PolicyConstants.COMMAND_OPEN_SETUP:
                _pipeHost.BroadcastOpenSetup();
                _watchdog.LaunchAgent("--setup");
                return;

            case PolicyConstants.COMMAND_UNPAIR:
                await UnpairAsync();
                return;

            default:
                throw new ArgumentException($"Unknown command type: {type}");
        }
    }

    private async Task FinishCommandAsync(string commandId, string status, string? error)
    {
        var payload = new JsonObject
        {
            ["status"] = status,
            ["completedAt"] = Sv(),
            ["error"] = error
        };
        await _firebase.PatchAsync(
            FirebasePaths.DeviceCommands(_deviceId) + "/" + commandId, payload.ToJsonString(JsonOpts), _ct);
    }

    private async Task UnpairAsync()
    {
        var ownerUid = _ownerUid;
        // One atomic multi-path update (mirrors the TV): clearing meta.ownerUid in a
        // separate write first would deny the users-entry delete, whose rule needs
        // ownerUid to still match the owner at evaluation time.
        var updates = new JsonObject
        {
            [FirebasePaths.DeviceMeta(_deviceId) + "/ownerUid"] = null,
            [FirebasePaths.DeviceMeta(_deviceId) + "/pairedAt"] = null
        };
        if (!string.IsNullOrEmpty(ownerUid))
        {
            updates[FirebasePaths.UserDevice(ownerUid, _deviceId)] = null;
        }

        await _firebase.PatchAsync("", updates.ToJsonString(JsonOpts), _ct);

        // Rotate the pairing secret/code and refresh device.json for the next pairing.
        _pairing.Rotate();
        WriteDeviceJson();
        _ownerUid = null;
        lock (_dedupeGate)
        {
            _handledUnlockRequests.Clear();
        }

        _pipeHost.BroadcastPairedState(IsPaired); // tray returns: a new pairing is needed

        // Local protection (snapshot, ledger, enforcement) intentionally stays in place.
        _logger.LogInformation("Unpaired; local protection retained");
    }

    /// <summary>Paired = an owner is recorded for this device.</summary>
    private bool IsPaired => !string.IsNullOrEmpty(_ownerUid);

    // ------------------------------------------------------------------ pairing
    private async Task RegisterDeviceAsync()
    {
        await _firebase.SignInAsync(_ct);
        var registrar = new DeviceRegistrar(_firebase, _deviceId);
        await registrar.RegisterAsync(_ct);
        if (!string.IsNullOrEmpty(registrar.OwnerUid))
        {
            _ownerUid = registrar.OwnerUid;
        }

        _logger.LogInformation("Device meta registered for {DeviceId}", _deviceId);
    }

    private async Task RecoverOwnerUidAsync()
    {
        try
        {
            var json = await _firebase.GetAsync(FirebasePaths.DeviceMeta(_deviceId), _ct);
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
            {
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var ownerUid = GetString(doc.RootElement, "ownerUid");
            if (!string.IsNullOrEmpty(ownerUid))
            {
                _ownerUid = ownerUid;
                _logger.LogInformation("Recovered paired owner from device metadata");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not recover owner uid");
        }
    }

    // Throttled self-heal for a lost owner uid: startup recovery is best-effort and
    // runs concurrently with the first agent hello, so the tray (pairedState) and the
    // users/{uid} presence mirror can both go stale if it fails once. Retries at most
    // every 5 minutes and re-broadcasts the paired state on success so the tray hides
    // without waiting for a pipe reconnect.
    private const long OwnerRecoverThrottleMs = 5 * 60_000;
    private long _lastOwnerRecoverAttemptMs;
    private int _ownerRecoverInFlight;

    private async Task EnsureOwnerUidAsync()
    {
        if (!string.IsNullOrEmpty(_ownerUid))
        {
            return;
        }

        var now = NowMs();
        if (Volatile.Read(ref _ownerRecoverInFlight) == 1
            || Interlocked.Read(ref _lastOwnerRecoverAttemptMs) > now - OwnerRecoverThrottleMs)
        {
            return;
        }

        Volatile.Write(ref _ownerRecoverInFlight, 1);
        try
        {
            Interlocked.Exchange(ref _lastOwnerRecoverAttemptMs, now);
            await RecoverOwnerUidAsync();
            if (!string.IsNullOrEmpty(_ownerUid))
            {
                _logger.LogInformation("Owner uid recovered; broadcasting paired state");
                _pipeHost.BroadcastPairedState(IsPaired);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Owner uid recovery attempt failed");
        }
        finally
        {
            Volatile.Write(ref _ownerRecoverInFlight, 0);
        }
    }

    // Single-flight guard: the pair-request SSE stream and the 5s boundary poll can
    // both fire for the same pending request; only one may process it at a time.
    private int _pairRequestInFlight;

    private async Task PollPairRequestsAsync()
    {
        if (!string.IsNullOrEmpty(_ownerUid))
        {
            return; // already paired
        }

        if (Interlocked.CompareExchange(ref _pairRequestInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await PollPairRequestsCoreAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _pairRequestInFlight, 0);
        }
    }

    private async Task PollPairRequestsCoreAsync()
    {
        var json = await _firebase.GetAsync(FirebasePaths.PairRequests(_deviceId), _ct);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            return;
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        JsonProperty? oldest = null;
        long oldestCreatedAt = long.MaxValue;
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (GetString(property.Value, "status") != "pending")
            {
                continue;
            }

            var requestCreatedAt = GetLong(property.Value, "createdAt");
            if (requestCreatedAt > 0 && requestCreatedAt < oldestCreatedAt)
            {
                oldestCreatedAt = requestCreatedAt;
                oldest = property;
            }
        }

        if (oldest is null)
        {
            return;
        }

        var request = oldest.Value;
        var requestId = request.Name;
        var parentUid = GetString(request.Value, "parentUid");
        var secret = GetString(request.Value, "secret");
        var code = GetString(request.Value, "code");
        var createdAt = GetLong(request.Value, "createdAt");

        // Server clock: pair requests carry a server-side createdAt, and the pairing
        // secret rotates on a TTL measured against that same clock.
        var now = _syncEngine.ServerNowMs();
        if (createdAt <= 0 || now > createdAt + PolicyConstants.PAIRING_TTL_MS)
        {
            await RespondToPairRequestAsync(requestId, "expired");
            return;
        }

        if (string.IsNullOrEmpty(parentUid) || !_pairing.Validate(
                string.IsNullOrEmpty(secret) ? null : secret,
                string.IsNullOrEmpty(code) ? null : code,
                createdAt,
                now))
        {
            await RespondToPairRequestAsync(requestId, "rejected");
            return;
        }

        // Claim ownership first; another parent must not be able to hijack a paired device.
        var metaJson = await _firebase.GetAsync(FirebasePaths.DeviceMeta(_deviceId), _ct);
        string? existingOwner = null;
        if (!string.IsNullOrWhiteSpace(metaJson) && metaJson.Trim() != "null")
        {
            using var metaDoc = JsonDocument.Parse(metaJson);
            existingOwner = GetString(metaDoc.RootElement, "ownerUid");
        }

        if (!string.IsNullOrEmpty(existingOwner))
        {
            _ownerUid = existingOwner;
            await RespondToPairRequestAsync(requestId, "rejected");
            return;
        }

        var label = Environment.MachineName;
        var meta = new JsonObject
        {
            ["ownerUid"] = parentUid,
            ["pairedAt"] = Sv(),
            ["label"] = label,
            ["platform"] = PolicyConstants.PLATFORM_WINDOWS
        };
        await _firebase.PatchAsync(FirebasePaths.DeviceMeta(_deviceId), meta.ToJsonString(JsonOpts), _ct);

        var userDevice = new JsonObject
        {
            ["deviceId"] = _deviceId,
            ["label"] = label,
            ["pairedAt"] = Sv(),
            ["lastSeen"] = Sv(),
            ["online"] = true,
            ["enforcementMode"] = PolicyConstants.ENFORCEMENT_FALLBACK,
            ["protectionHealthy"] = false
        };
        await _firebase.PutAsync(FirebasePaths.UserDevice(parentUid, _deviceId), userDevice.ToJsonString(JsonOpts), _ct);

        await RespondToPairRequestAsync(requestId, "accepted");

        _ownerUid = parentUid;
        _pairing.Rotate();
        WriteDeviceJson();
        _pipeHost.BroadcastPairedState(IsPaired); // tray hides once paired
        _logger.LogInformation("Pairing completed with parent {ParentUid}", parentUid);
    }

    private async Task RespondToPairRequestAsync(string requestId, string status)
    {
        var payload = new JsonObject { ["status"] = status, ["respondedAt"] = Sv() };
        await _firebase.PatchAsync(FirebasePaths.PairRequest(_deviceId, requestId), payload.ToJsonString(JsonOpts), _ct);
    }

    // ---------------------------------------------------------------- heartbeat
    private async Task HeartbeatAsync()
    {
        var healthy = _pipeHost.ConnectedAgents > 0;
        var pinConfigured = _syncEngine.LastValidSnapshot?.Pin is not null;

        // Each write is isolated: one failing node must not skip the others (a
        // sync/runtime failure used to strand the users/{uid} presence mirror too).
        var heartbeat = new JsonObject
        {
            ["online"] = true,
            ["lastSeen"] = Sv(),
            ["enforcementMode"] = PolicyConstants.ENFORCEMENT_FALLBACK,
            ["protectionHealthy"] = healthy
        };
        try
        {
            await _firebase.PatchAsync(FirebasePaths.DeviceHeartbeat(_deviceId), heartbeat.ToJsonString(JsonOpts), _ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat write failed");
        }

        var runtime = new JsonObject
        {
            ["lastHeartbeatWriteAt"] = Sv(),
            // Real SSE connectivity from the sync engine, not a hardcoded true: the
            // phone's sync-health card must show a dead stream as disconnected.
            ["connected"] = _syncEngine.IsStreamConnected,
            ["sessionId"] = _syncEngine.SessionId,
            ["protocolVersion"] = 2
        };
        try
        {
            await _firebase.PatchAsync(FirebasePaths.DeviceSyncRuntime(_deviceId), runtime.ToJsonString(JsonOpts), _ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sync/runtime heartbeat write failed");
        }

        var security = new JsonObject
        {
            ["enforcementMode"] = PolicyConstants.ENFORCEMENT_FALLBACK,
            ["protectionHealthy"] = healthy,
            ["pinConfigured"] = pinConfigured,
            ["platform"] = PolicyConstants.PLATFORM_WINDOWS,
            ["updatedAt"] = Sv()
        };
        try
        {
            await _firebase.PatchAsync(FirebasePaths.DeviceSecurityRuntime(_deviceId), security.ToJsonString(JsonOpts), _ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "security/runtime write failed");
        }

        // Mirror online status onto the parent's device list (same as the TV agent).
        // Empty owner uid means startup recovery failed or pairing landed after it:
        // retry (throttled) so the phone's device entry does not go permanently stale.
        await EnsureOwnerUidAsync();
        var ownerUid = _ownerUid;
        if (!string.IsNullOrEmpty(ownerUid))
        {
            var userDevice = new JsonObject
            {
                ["deviceId"] = _deviceId,
                ["lastSeen"] = Sv(),
                ["online"] = true,
                ["enforcementMode"] = PolicyConstants.ENFORCEMENT_FALLBACK,
                ["protectionHealthy"] = healthy
            };
            try
            {
                await _firebase.PatchAsync(FirebasePaths.UserDevice(ownerUid, _deviceId), userDevice.ToJsonString(JsonOpts), _ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "users/{uid}/devices presence mirror write failed");
            }
        }
    }

    // ---------------------------------------------------------------- inventory
    private async Task UploadInventoryAsync()
    {
        IReadOnlyList<InventoryApp> apps;
        try
        {
            apps = InventoryScanner.Scan();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inventory scan failed");
            return;
        }

        var map = new JsonObject();
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            map[PackageKeys.Encode(app.AppKey)] = new JsonObject
            {
                ["packageName"] = app.AppKey,
                ["label"] = app.Label,
                ["blockable"] = app.Blockable,
                ["protectedReason"] = app.ProtectedReason,
                ["systemApp"] = false
            };
            labels[app.AppKey] = app.Label;
        }

        lock (_labelLock)
        {
            _labelByAppKey.Clear();
            foreach (var (key, value) in labels)
            {
                _labelByAppKey[key] = value;
            }
        }

        await _firebase.PutAsync(FirebasePaths.DeviceApps(_deviceId), map.ToJsonString(JsonOpts), _ct);

        var runtime = new JsonObject
        {
            ["inventoryRevision"] = Guid.NewGuid().ToString(),
            ["lastInventoryWriteAt"] = Sv()
        };
        await _firebase.PatchAsync(FirebasePaths.DeviceSyncRuntime(_deviceId), runtime.ToJsonString(JsonOpts), _ct);
        _logger.LogInformation("Inventory uploaded ({Count} apps)", map.Count);
    }

    // ------------------------------------------------------------- state upload
    private string? _lastUploadedDayKey;

    /// <summary>Local-midnight day key, matching the ledger's day boundary semantics.</summary>
    private string LocalDayKey() =>
        TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone)
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task UploadStatesAsync(bool force)
    {
        if (!force && DateTime.UtcNow - _lastStateUploadUtc < StateUploadInterval)
        {
            return;
        }

        _lastStateUploadUtc = DateTime.UtcNow;

        // Day rollover: yesterday's per-app states keep their old usageMsToday in
        // Firebase forever because the diff skips unchanged entries and most apps
        // have no sessions today. Force one full re-upload per local day so usage
        // resets propagate to the console/phone.
        var dayKey = LocalDayKey();
        if (!string.Equals(_lastUploadedDayKey, dayKey, StringComparison.Ordinal))
        {
            _lastUploadedDayKey = dayKey;
            lock (_dedupeGate)
            {
                _lastUploadedAppStates.Clear();
            }
        }

        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_currentAppKey))
            {
                tracked.Add(_currentAppKey);
            }
        }

        foreach (var appKey in _ledger.UsageMsToday().Keys)
        {
            tracked.Add(appKey);
        }

        tracked.UnionWith(PolicyConstants.WindowsBypassPackages);

        var snapshot = _syncEngine.LastValidSnapshot;
        var revisionId = snapshot?.RevisionId;

        // Write per-app states in a single PATCH instead of one REST call per
        // app (one SSE event instead of N on the phone). Only entries whose JSON
        // changed since the last upload are included — an idle device's state is
        // static, so the steady-state upload becomes a no-op. The data shape is
        // identical; entries not in the patch keep their previously written values.
        var states = new JsonObject();
        foreach (var appKey in tracked)
        {
            var (locked, reason) = DecideFor(appKey);
            var usageMs = _ledger.EffectiveUsageMsToday(appKey);
            states[PackageKeys.Encode(appKey)] = new JsonObject
            {
                ["packageName"] = appKey,
                ["requestedSuspended"] = false,
                ["suspended"] = _suspender.IsSuspended(appKey),
                ["enforcementMode"] = PolicyConstants.ENFORCEMENT_FALLBACK,
                ["fallbackLocked"] = locked,
                ["usageMinutesToday"] = usageMs / 60_000L,
                ["usageMsToday"] = usageMs,
                ["usageCapturedAt"] = Sv(),
                ["lockBlocked"] = locked,
                ["lockReason"] = locked ? reason : null,
                ["controlRevisionId"] = revisionId,
                ["updatedAt"] = Sv(),
                ["blockable"] = true
            };
        }

        var changedStates = new JsonObject();
        foreach (var (encodedKey, entry) in states)
        {
            var json = entry.ToJsonString(JsonOpts);
            lock (_dedupeGate)
            {
                if (!_lastUploadedAppStates.TryGetValue(encodedKey, out var previous) || previous != json)
                {
                    changedStates[encodedKey] = entry.DeepClone();
                    _lastUploadedAppStates[encodedKey] = json;
                }
            }
        }

        if (changedStates.Count > 0)
        {
            await _firebase.PatchAsync(FirebasePaths.DeviceStateApps(_deviceId), changedStates.ToJsonString(JsonOpts), _ct);
            // (usage/status upload; nothing further needed locally)
        }

        // Always kept fresh: the phone's Sync Health card reads this timestamp even
        // when the per-app diff produced no writes.
        var runtime = new JsonObject { ["lastStateWriteAt"] = Sv() };
        await _firebase.PatchAsync(FirebasePaths.DeviceSyncRuntime(_deviceId), runtime.ToJsonString(JsonOpts), _ct);

        // Daily-limit state evolves without snapshot changes; keep the cache fresh.
        WritePolicyCache(snapshot);
    }

    // ------------------------------------------------------------ browser tab state

    private async Task OnBrowserReceivedAsync(PipeBrowserState snapshot)
    {
        PipeBrowserState? previous;
        lock (_browserGate)
        {
            previous = _currentBrowser;
            _currentBrowser = snapshot;
            _lastBrowserStateAtMs = NowMs();
        }

        // Tab timeline: one history entry per tab session >=10s (ActivityLog caps the
        // churn); appLabel carries the tab title for the phone's Activity tab.
        var tabTitle = snapshot.ActiveTab;
        if (!string.IsNullOrWhiteSpace(tabTitle)
            && (previous is null || !string.Equals(previous.ActiveTab, tabTitle, StringComparison.Ordinal)))
        {
            _activity.StartTab(snapshot.AppKey, Truncate(tabTitle, ActivityLabelMax)!, NowMs(), snapshot.ActiveUrl);
        }

        var now = Environment.TickCount64;
        if (now - _lastBrowserUploadAtMs >= 2_000)
        {
            _lastBrowserUploadAtMs = now;
            _browserUploadPending = false;
            await UploadBrowserStateAsync(force: true);
        }
        else
        {
            _browserUploadPending = true; // the roll-up tick flushes within 15s
        }

        // Blocked-site reaction: a URL change (including SPA pushState jumps, which the
        // watcher reports via the omnibox) is acted on immediately — the offending TAB
        // gets navigated to the block page; the browser itself is never locked.
    }

    private async Task BrowserRollupTickAsync()
    {
        PipeBrowserState? snapshot;
        lock (_browserGate)
        {
            snapshot = _currentBrowser;
            if (snapshot != null)
            {
                var dayKey = LocalDayKey();
                if (!string.Equals(_browserDomainDayKey, dayKey, StringComparison.Ordinal))
                {
                    _browserDomainDayKey = dayKey;
                    _browserDomains.Clear();
                }

                // Accrue active-domain time only while this browser is the foreground
                // app; cap each tick so stalls can't credit phantom time.
                var now = NowMs();
                var elapsed = _lastDomainAccrualAtMs == 0 ? 0 : now - _lastDomainAccrualAtMs;
                _lastDomainAccrualAtMs = now;
                if (elapsed is > 1_000 and <= 20_000
                    && string.Equals(_currentAppKey, snapshot.AppKey, StringComparison.OrdinalIgnoreCase)
                    && RegistrableDomain(snapshot.ActiveUrl) is { } domain)
                {
                    _browserDomains[domain] = Math.Min(_browserDomains.GetValueOrDefault(domain) + elapsed, 86_400_000L);
                }
            }
            else
            {
                _lastDomainAccrualAtMs = 0;
            }
        }

        if (snapshot != null && (_browserUploadPending || _browserDomains.Count > 0))
        {
            await UploadBrowserStateAsync(force: false);
        }
    }

    private async Task UploadBrowserStateAsync(bool force)
    {
        PipeBrowserState? snapshot;
        Dictionary<string, long> domains;
        lock (_browserGate)
        {
            snapshot = _currentBrowser;
            if (snapshot is null)
            {
                return;
            }

            domains = new Dictionary<string, long>(_browserDomains, StringComparer.Ordinal);
        }

        var browserJson = new JsonObject
        {
            ["browser"] = Truncate(snapshot.AppKey, BrowserNameMax),
            ["label"] = Truncate(snapshot.Label, BrowserNameMax),
            ["activeTab"] = Truncate(snapshot.ActiveTab, BrowserTabMax),
            ["activeUrl"] = Truncate(snapshot.ActiveUrl, BrowserUrlMax),
            ["tabCount"] = snapshot.TabCount,
            ["tabs"] = BuildTabsJson(snapshot.Tabs),
            ["domainsToday"] = BuildDomainsJson(domains),
            ["updatedAt"] = Sv(),
        };
        var json = browserJson.ToJsonString(JsonOpts);
        if (!force && json == _lastUploadedBrowserJson)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (!force && now - _lastBrowserUploadAtMs < 1_500)
        {
            _browserUploadPending = true;
            return;
        }

        _lastBrowserUploadAtMs = now;
        _lastUploadedBrowserJson = json;
        _browserUploadPending = false;

        await _firebase.PatchAsync(FirebasePaths.DeviceStateBrowser(_deviceId), json, _ct);
        var runtime = new JsonObject { ["lastBrowserWriteAt"] = Sv() };
        await _firebase.PatchAsync(FirebasePaths.DeviceSyncRuntime(_deviceId), runtime.ToJsonString(JsonOpts), _ct);
    }

    private static JsonArray BuildTabsJson(IReadOnlyList<PipeBrowserTab> tabs)
    {
        var array = new JsonArray();
        foreach (var tab in tabs)
        {
            var entry = new JsonObject { ["title"] = Truncate(tab.Title, BrowserTabMax) };
            if (!string.IsNullOrEmpty(tab.Url))
            {
                entry["url"] = Truncate(tab.Url, BrowserUrlMax);
            }

            array.Add(entry);
        }

        return array;
    }

    private static JsonObject BuildDomainsJson(Dictionary<string, long> domains)
    {
        var json = new JsonObject();
        foreach (var (domain, ms) in domains.OrderByDescending(kv => kv.Value).Take(50))
        {
            // RTDB keys cannot contain '.', so domains ride base64url like package keys.
            json[PackageKeys.Encode(domain)] = ms;
        }

        return json;
    }

    /// <summary>github.com/watch?v=x → "github.com" (host minus a leading www.).</summary>
    private static string? RegistrableDomain(string? url) => BrowserDomains.Extract(url);

    /// <summary>The browser DTO for the console state; null when nothing was captured.
    /// updatedAt is the last tab-event time (stable between events so diff-render works).</summary>
    private JsonObject? BuildBrowserStateDto()
    {
        lock (_browserGate)
        {
            if (_currentBrowser is null)
            {
                return null;
            }

            var domains = new Dictionary<string, long>(_browserDomains, StringComparer.Ordinal);
            return new JsonObject
            {
                ["browser"] = Truncate(_currentBrowser.AppKey, BrowserNameMax),
                ["label"] = Truncate(_currentBrowser.Label, BrowserNameMax),
                ["activeTab"] = Truncate(_currentBrowser.ActiveTab, BrowserTabMax),
                ["activeUrl"] = Truncate(_currentBrowser.ActiveUrl, BrowserUrlMax),
                ["tabCount"] = _currentBrowser.TabCount,
                ["tabs"] = BuildTabsJson(_currentBrowser.Tabs),
                ["domainsToday"] = BuildDomainsJson(domains),
                ["updatedAt"] = _lastBrowserStateAtMs,
            };
        }
    }

    // ------------------------------------------------------------ activity upload
    private async Task FlushActivityAsync(bool force)
    {
        if (!force && DateTime.UtcNow - _lastActivityUploadUtc < ActivityFlushInterval)
        {
            return;
        }

        _lastActivityUploadUtc = DateTime.UtcNow;

        // Persist the debounced ledger/activity state alongside the upload cycle.
        _ledger.FlushDirty();
        _activity.Flush();

        string? appKey = null, label = null, overlay = null;
        long startedAtMs = 0;
        lock (_gate)
        {
            appKey = _currentAppKey;
            label = _currentLabel;
            overlay = _currentOverlay;
            startedAtMs = _currentStartedAtMs;
        }

        if (!string.IsNullOrEmpty(appKey))
        {
            var current = new JsonObject
            {
                ["runtimeApp"] = appKey,
                ["appKey"] = appKey,
                ["appLabel"] = label,
                ["appStartedAt"] = startedAtMs,
                ["overlayState"] = string.IsNullOrEmpty(overlay) ? "none" : overlay,
                ["mediaAvailable"] = false,
                ["playbackState"] = "unknown",
                ["playbackSpeed"] = 0,
                ["captureSource"] = "agent",
                ["updatedAt"] = Sv()
            };
            await _firebase.PutAsync(
                FirebasePaths.DeviceActivityCurrent(_deviceId), current.ToJsonString(JsonOpts), _ct);
        }

        foreach (var entry in _activity.Pending())
        {
            var node = SerializeRecord(entry);
            if (node is null)
            {
                continue;
            }

            var id = PickString(node, "Id", "id", "SessionId", "sessionId");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var startedAt = PickLong(node, "StartedAt", "startedAt", "StartedAtMs", "startedAtMs");
            var endedAt = PickLong(node, "EndedAt", "endedAt", "EndedAtMs", "endedAtMs");
            // Tab sessions arrive with type "tab" (ActivityLog queue); default to app.
            var entryType = PickString(node, "Type", "type", "EntryType", "entryType");
            var historyAppKey = PickString(node, "AppKey", "appKey", "PackageName", "packageName");
            var historyAppLabel = PickString(node, "Label", "label", "AppLabel", "appLabel");
            if (string.IsNullOrEmpty(historyAppLabel))
            {
                // RTDB rules reject missing labels; fall back to the package name and
                // clamp lengths so oversized titles/labels never fail the write.
                historyAppLabel = historyAppKey;
            }

            var history = new JsonObject
            {
                ["id"] = id,
                ["type"] = string.IsNullOrEmpty(entryType) ? "app" : entryType,
                ["appKey"] = historyAppKey,
                ["appLabel"] = Truncate(historyAppLabel, ActivityLabelMax),
                ["title"] = Truncate(PickString(node, "Title", "title"), ActivityLabelMax),
                ["url"] = PickString(node, "Url", "url"),
                ["subtitle"] = null,
                ["startedAt"] = startedAt,
                ["endedAt"] = endedAt,
                ["lastPositionMs"] = null,
                ["durationMs"] = null,
                ["playbackState"] = null,
                ["confidence"] = null,
                ["captureSource"] = "agent",
                ["updatedAt"] = endedAt
            };

            await _firebase.PutAsync(
                FirebasePaths.DeviceActivityHistoryItem(_deviceId, id), history.ToJsonString(JsonOpts), _ct);
            _activity.MarkUploaded(id);
        }

        _activity.PruneBefore(NowMs() - (long)ActivityRetention.TotalMilliseconds);
    }

    // ------------------------------------------------------------------- tamper
    private async Task PushTamperEventAsync(string type, string message)
    {
        var eventId = Guid.NewGuid().ToString("N");
        var payload = new JsonObject
        {
            ["type"] = type,
            ["message"] = message,
            ["createdAt"] = Sv()
        };
        var path = FirebasePaths.DeviceTamperEvents(_deviceId) + "/" + eventId;
        await _firebase.PutAsync(path, payload.ToJsonString(JsonOpts), _ct);
    }

    // ------------------------------------------------------------ rtdb retention
    private const int RtdbPruneBatchSize = 100;

    /// <summary>
    /// Retention sweep for append-only RTDB nodes the agent otherwise grows forever:
    /// tamperEvents older than 30 days and activity/history records older than 7 days.
    /// Child keys are listed via GET, stale ids collected, then deleted with one
    /// multi-path null PATCH per batch. Best effort: any failure is logged at Warning
    /// and retried on the next sweep (startup + every 24h).
    /// </summary>
    private async Task PruneRtdbNodesAsync()
    {
        try
        {
            var now = _syncEngine.ServerNowMs();
            var stalePaths = new List<string>();
            await CollectStaleChildPathsAsync(
                FirebasePaths.DeviceTamperEvents(_deviceId), TamperEventRetention, now, ["createdAt"], stalePaths);
            await CollectStaleChildPathsAsync(
                FirebasePaths.DeviceActivityHistory(_deviceId), ActivityHistoryRetention, now, ["endedAt", "startedAt"], stalePaths);
            await CollectStaleChildPathsAsync(
                FirebasePaths.DeviceMessages(_deviceId), MessageRetention, now, ["createdAt"], stalePaths);

            for (var offset = 0; offset < stalePaths.Count; offset += RtdbPruneBatchSize)
            {
                var batch = new JsonObject();
                foreach (var stalePath in stalePaths.Skip(offset).Take(RtdbPruneBatchSize))
                {
                    batch[stalePath] = null;
                }

                await _firebase.PatchAsync("", batch.ToJsonString(JsonOpts), _ct);
            }

            if (stalePaths.Count > 0)
            {
                _logger.LogInformation("RTDB retention sweep deleted {Count} stale nodes", stalePaths.Count);
            }
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RTDB retention sweep failed");
        }
    }

    /// <summary>Child-lists a node via GET and appends the full paths of children whose
    /// newest timestamp field is older than the retention window.</summary>
    private async Task CollectStaleChildPathsAsync(
        string nodePath, TimeSpan retention, long nowMs, string[] timeFields, List<string> stalePaths)
    {
        try
        {
            var json = await _firebase.GetAsync(nodePath, _ct);
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
            {
                return;
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var cutoff = nowMs - (long)retention.TotalMilliseconds;
            foreach (var child in doc.RootElement.EnumerateObject())
            {
                var stamp = 0L;
                foreach (var field in timeFields)
                {
                    if (child.Value.ValueKind == JsonValueKind.Object
                        && child.Value.TryGetProperty(field, out var value)
                        && value.ValueKind == JsonValueKind.Number
                        && value.TryGetInt64(out var parsed))
                    {
                        stamp = Math.Max(stamp, parsed);
                    }
                }

                // Missing/unparsable timestamps are never pruned by the sweep.
                if (stamp > 0 && stamp < cutoff)
                {
                    stalePaths.Add(nodePath + "/" + child.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retention child-list failed for {NodePath}", nodePath);
        }
    }

    // ------------------------------------------------------------------- helpers
    private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Clamps a value to the RTDB rules' max length; null/empty passes through.</summary>
    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= max ? value : value.Substring(0, max);
    }

    private async Task RunSafeAsync(string name, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} failed", name);
        }
    }

    private void RunSafe(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name} failed", name);
        }
    }


    private IDisposable? _unlockStream;
    private IDisposable? _pairStream;

    private void SubscribeToRealtimeStreams()
    {
        TryStartUnlockStream();
        TryStartMessageStream();
        TryStartPairStreamIfNeeded();
    }

    private void TryStartUnlockStream()
    {
        if (_unlockStream != null) return;
        try
        {
            _unlockStream = _firebase.StreamAsync(
                FirebasePaths.DeviceUnlockRequests(_deviceId),
                raw => _ = HandleUnlockStreamAsync(raw),
                _ => { },
                _ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unlock stream start failed; boundary ticker will poll");
        }
    }

    private void TryStartMessageStream()
    {
        if (_messageStream != null) return;
        try
        {
            _messageStream = _firebase.StreamAsync(
                FirebasePaths.DeviceMessages(_deviceId),
                raw => _ = HandleMessagesJsonAsync(raw),
                _ => { },
                _ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "messages stream subscribe failed (poll fallback covers it)");
        }
    }

    /// <summary>Real-time parent messages: each unseen node shows as a toast on the
    /// laptop and is deleted after display. Old backlog (>10 min) is deleted silently.</summary>
    private async Task HandleMessagesJsonAsync(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "null") return;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            foreach (var child in doc.RootElement.EnumerateObject())
            {
                var messageId = child.Name;
                if (_handledMessages.Contains(messageId)) continue;
                if (_handledMessages.Count > HandledMessagesMax)
                {
                    _handledMessages.Clear();
                }

                _handledMessages.Add(messageId);
                if (child.Value.ValueKind != JsonValueKind.Object) continue;

                var text = GetString(child.Value, "text");
                var createdAt = GetLong(child.Value, "createdAt");
                var age = createdAt > 0 ? NowMs() - createdAt : long.MaxValue;

                // Delete after display (owner may also delete from the phone).
                try
                {
                    await _firebase.PutAsync(FirebasePaths.DeviceMessage(_deviceId, messageId), "null", _ct);
                }
                catch (Exception delEx)
                {
                    _logger.LogWarning(delEx, "message delete failed for {MessageId}", messageId);
                }

                if (string.IsNullOrWhiteSpace(text)) continue;
                if (age > 10 * 60_000L)
                {
                    _logger.LogInformation("Skipped stale parent message {MessageId} ({AgeMin} min old)",
                        messageId, age / 60_000);
                    continue;
                }

                _pipeHost.BroadcastShowMessage(text);
                _logger.LogInformation("Displayed parent message {MessageId}", messageId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "messages stream handling failed");
        }
    }

    private async Task PollMessagesAsync()
    {
        try
        {
            var json = await _firebase.GetAsync(FirebasePaths.DeviceMessages(_deviceId), _ct);
            await HandleMessagesJsonAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "messages poll failed");
        }
    }

    private void TryStartPairStreamIfNeeded()
    {
        if (!string.IsNullOrEmpty(_ownerUid)) return;
        if (_pairStream != null) return;
        try
        {
            _pairStream = _firebase.StreamAsync(
                FirebasePaths.PairRequests(_deviceId),
                raw => _ = HandlePairStreamAsync(raw),
                _ => { },
                _ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pair stream start failed; boundary ticker will poll");
        }
    }

    private void StopPairStreamIfPaired()
    {
        if (string.IsNullOrEmpty(_ownerUid)) return;
        try { _pairStream?.Dispose(); } catch { }
        _pairStream = null;
    }

    private readonly HashSet<string> _sentWarningToastsToday = new(StringComparer.OrdinalIgnoreCase);

    private Task BoundaryTickAsync()
    {
        EvaluateCurrentForeground();
        lock (_gate)
        {
            var foreground = _currentAppKey;
            _sessions.Tick(foreground, NowMs());
        }
        _ledger.FlushDirty();
        CheckScreenTimeWarnings();
        if (string.IsNullOrEmpty(_ownerUid))
        {
            // Keep device.json in sync with lazy secret rotation so the tray QR
            // never shows a stale secret (a stale QR makes every pair attempt
            // get rejected with no recovery until a service restart).
            RefreshPairingArtifactsIfNeeded();
            TryStartPairStreamIfNeeded();
            _ = RunSafeAsync("pairing-boundary", PollPairRequestsAsync);
        }
        else
        {
            StopPairStreamIfPaired();
        }
        _ = RunSafeAsync("unlock-boundary", PollUnlockRequestsAsync);
        _ = RunSafeAsync("message-boundary", PollMessagesAsync);
        // Commands have no other poll fallback: if the commands SSE stream is down,
        // owner-sent rescanApps/resetToday/unpair/openSetup would never arrive.
        _ = RunSafeAsync("commands-boundary", PollCommandsAsync);
        return Task.CompletedTask;
    }

    private void CheckScreenTimeWarnings()
    {
        var snapshot = _syncEngine.LastValidSnapshot;
        if (snapshot is null) return;

        // Local day key (same pattern as LocalDayKey/UploadStatesAsync): usage resets at
        // local midnight, so a UTC key would reset the warning toasts hours late/early
        // and could re-toast or skip a day entirely.
        var today = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone)
            .ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

        // 1. App-specific daily limit warning
        string? activeKey;
        lock (_gate)
        {
            activeKey = _currentAppKey;
        }

        if (!string.IsNullOrEmpty(activeKey) && activeKey != "__agent__")
        {
            var apps = snapshot.EffectiveApps();
            if (apps.TryGetValue(activeKey, out var rule) && rule.DailyLimitMinutes is int limitMinutes)
            {
                var usedMs = _ledger.EffectiveUsageMsToday(activeKey);
                var limitMs = (long)limitMinutes * 60_000L;
                var remainingMs = limitMs - usedMs;
                var label = LabelFor(activeKey);

                if (remainingMs > 0 && remainingMs <= 10 * 60_000L && remainingMs > 5 * 60_000L)
                {
                    var key10 = $"app_10m_{activeKey}_{today}";
                    if (_sentWarningToastsToday.Add(key10))
                    {
                        _pipeHost.BroadcastWarningToast("GuardPulse Screen Time", $"You have 10 minutes remaining for {label}.");
                    }
                }
                else if (remainingMs > 0 && remainingMs <= 5 * 60_000L)
                {
                    var key5 = $"app_5m_{activeKey}_{today}";
                    if (_sentWarningToastsToday.Add(key5))
                    {
                        _pipeHost.BroadcastWarningToast("GuardPulse Screen Time", $"You have 5 minutes remaining for {label}. Please save your work.");
                    }
                }
            }
        }

        // 2. App-specific session limit warning (continuous open use; re-arms each session)
        if (!string.IsNullOrEmpty(activeKey) && activeKey != "__agent__")
        {
            var apps = snapshot.EffectiveApps();
            if (apps.TryGetValue(activeKey, out var sessionRule) && sessionRule.SessionLimitMinutes is int sessionMinutes)
            {
                var usedSessionMs = _sessions.EffectiveSessionMs(activeKey, NowMs());
                var sessionLimitMs = (long)sessionMinutes * 60_000L;
                var sessionRemainingMs = sessionLimitMs - usedSessionMs;
                var label = LabelFor(activeKey);
                // Session-scoped dedup bucket: a fresh session (after the 2-min reset
                // or an approval) gets a new start timestamp, so the 5m/1m toasts
                // re-arm without waiting for the next day.
                var sessionBucket = _sessions.SessionStartedAtMs(activeKey) / 60_000L;

                if (sessionRemainingMs > 0 && sessionRemainingMs <= 5 * 60_000L && sessionRemainingMs > 60_000L)
                {
                    var keyS5 = $"session_5m_{activeKey}_{sessionBucket}";
                    if (_sentWarningToastsToday.Add(keyS5))
                    {
                        _pipeHost.BroadcastWarningToast("GuardPulse Screen Time", $"You have 5 minutes left for {label} this session.");
                    }
                }
                else if (sessionRemainingMs > 0 && sessionRemainingMs <= 60_000L)
                {
                    var keyS1 = $"session_1m_{activeKey}_{sessionBucket}";
                    if (_sentWarningToastsToday.Add(keyS1))
                    {
                        _pipeHost.BroadcastWarningToast("GuardPulse Screen Time", $"You have 1 minute left for {label} this session.");
                    }
                }
            }
        }

        // 3. Whole-device daily budget warning
        if (snapshot.Budget is { DailyLimitMinutes: > 0 } budget)
        {
            long totalUsedMs = 0;
            foreach (var key in _ledger.UsageMsToday().Keys)
            {
                totalUsedMs += _ledger.EffectiveUsageMsToday(key);
            }

            var budgetMs = (long)budget.DailyLimitMinutes * 60_000L;
            var remainingBudgetMs = budgetMs - totalUsedMs;

            if (remainingBudgetMs > 0 && remainingBudgetMs <= 10 * 60_000L && remainingBudgetMs > 5 * 60_000L)
            {
                var keyBudget10 = $"budget_10m_{today}";
                if (_sentWarningToastsToday.Add(keyBudget10))
                {
                    _pipeHost.BroadcastWarningToast("GuardPulse Screen Time", "You have 10 minutes of daily screen time remaining.");
                }
            }
            else if (remainingBudgetMs > 0 && remainingBudgetMs <= 5 * 60_000L)
            {
                var keyBudget5 = $"budget_5m_{today}";
                if (_sentWarningToastsToday.Add(keyBudget5))
                {
                    _pipeHost.BroadcastWarningToast("GuardPulse Screen Time", "You have 5 minutes of daily screen time remaining. Please save your work.");
                }
            }
        }
    }

    private Task HandleUnlockStreamAsync(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "null") return Task.CompletedTask;
        return HandleUnlockJsonAsync(raw);
    }

    private Task HandlePairStreamAsync(string? raw)
    {
        if (string.IsNullOrEmpty(_ownerUid) && !string.IsNullOrWhiteSpace(raw) && raw.Trim() != "null")
        {
            _ = RunSafeAsync("pairing-stream", PollPairRequestsAsync);
        }
        return Task.CompletedTask;
    }

    private Task HandleCommandsStreamAsync(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "null") return Task.CompletedTask;
        return HandleCommandsJsonAsync(raw);
    }

    private async Task IntervalLoopAsync(string name, TimeSpan interval, Func<Task> body)
    {
        while (!_ct.IsCancellationRequested)
        {
            try
            {
                await body();
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name} iteration failed", name);
            }

            try
            {
                await Task.Delay(interval, _ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void TryWriteText(string path, string contents)
    {
        try
        {
            File.WriteAllText(path, contents);
        }
        catch
        {
            // marker only; not load-bearing
        }
    }

    /// <summary>{".sv":"timestamp"} - lets Firebase fill server time.</summary>
    private static JsonNode Sv() => new JsonObject { [".sv"] = "timestamp" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static JsonObject? SerializeRecord<T>(T record)
    {
        try
        {
            return JsonSerializer.SerializeToNode(record) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string PickString(JsonObject node, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var (key, value) in node)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)
                    && value is not null
                    && value.GetValueKind() == JsonValueKind.String)
                {
                    return value.GetValue<string>() ?? "";
                }
            }
        }

        return "";
    }

    private static long PickLong(JsonObject node, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var (key, value) in node)
            {
                if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase) || value is null)
                {
                    continue;
                }

                try
                {
                    if (value.GetValueKind() == JsonValueKind.Number)
                    {
                        return value.GetValue<long>();
                    }

                    if (value.GetValueKind() == JsonValueKind.String
                        && long.TryParse(value.GetValue<string>(), out var parsed))
                    {
                        return parsed;
                    }
                }
                catch
                {
                    // try next
                }
            }
        }

        return 0;
    }

    private static string GetString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString() ?? "";
                }
            }
        }

        return "";
    }

    private static long GetLong(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var parsed))
            {
                return parsed;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt64(out var parsed))
                {
                    return parsed;
                }
            }
        }

        return 0;
    }

    private static long? GetNullableLong(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed))
                {
                    return parsed;
                }

                if (value.ValueKind == JsonValueKind.Null)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private sealed record DeviceIdentityJson(string DeviceId, string Code);

    // Local PIN retry policy: 5 failures inside 5 minutes block further attempts for
    // 60s, doubling per consecutive lock (capped at 15 minutes); success resets state.
    // (PinRetryPolicy is not part of CONTRACTS.md, so it lives here.)
    /// <summary>
    /// PIN brute-force gate with reboot-proof strikes: the escalating strike count and
    /// block deadline persist to the state dir, so restarting the machine (a standard
    /// user can do that freely) does not hand back 5 fresh guesses per boot.
    /// </summary>
    private sealed class PinRetryGate(TimeProvider time, string? persistencePath = null)
    {
        private const int MaxFailures = 5;
        private const long WindowMs = 5 * 60_000L;
        private const long BaseBlockMs = 60_000L;
        private const long MaxBlockMs = 15 * 60_000L;

        private readonly object _gate = new();
        private readonly Queue<long> _failures = new();
        private long _blockedUntilMs;
        private int _strikeCount;

        public bool IsBlocked()
        {
            lock (_gate)
            {
                return Now() < _blockedUntilMs;
            }
        }

        /// <summary>Epoch ms when the current block ends (0 when not blocked).</summary>
        public long BlockedUntilMs()
        {
            lock (_gate)
            {
                return _blockedUntilMs > Now() ? _blockedUntilMs : 0;
            }
        }

        public void RecordFailure()
        {
            lock (_gate)
            {
                var now = Now();
                while (_failures.Count > 0 && now - _failures.Peek() > WindowMs)
                {
                    _failures.Dequeue();
                }

                _failures.Enqueue(now);
                if (_failures.Count >= MaxFailures)
                {
                    _strikeCount++;
                    var blockMs = Math.Min(BaseBlockMs << Math.Min(_strikeCount - 1, 8), MaxBlockMs);
                    _blockedUntilMs = now + blockMs;
                    _failures.Clear();
                    PersistLocked();
                }
            }
        }

        public void RecordSuccess()
        {
            lock (_gate)
            {
                _failures.Clear();
                _blockedUntilMs = 0;
                _strikeCount = 0;
                PersistLocked();
            }
        }

        private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

        private void PersistLocked()
        {
            if (persistencePath == null)
            {
                return;
            }

            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(
                    new { strikeCount = _strikeCount, blockedUntilMs = _blockedUntilMs });
                GuardPulse.Agent.Core.AtomicFile.WriteAllText(persistencePath, json);
            }
            catch
            {
                // Persistence is best-effort; the in-memory gate still applies.
            }
        }
    }
}
