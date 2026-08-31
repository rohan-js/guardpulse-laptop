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

public sealed class AgentHostedService(
    ILogger<AgentHostedService> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private static readonly TimeSpan CommandPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnlockPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PairPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActivityFlushInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StateUploadInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DeadManCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeadManGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ActivityRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan StandardCommandTtl = TimeSpan.FromMinutes(5);

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
    private readonly HashSet<string> _processedCommands = new(StringComparer.Ordinal);
    private readonly HashSet<string> _handledUnlockRequests = new(StringComparer.Ordinal);
    private const int HandledUnlockRequestsMax = 1000;

    private CancellationToken _ct;
    private TimeProvider _time = TimeProvider.System;
    private string _stateDir = StatePaths.Root;
    private AgentConfig _config = null!;
    private ISecretStore _secrets = null!;
    private IFirebaseClient _firebase = null!;
    private RtdbFirebaseClient? _ownerClient;
    private SyncEngine _syncEngine = null!;
    private UsageLedger _ledger = null!;
    private ActivityLog _activity = null!;
    private EnforcementEngine _enforcement = null!;
    private OneVisitUnlocks _unlocks = null!;
    private PairingManager _pairing = null!;
    private AgentPipeHost _pipeHost = null!;
    private ProcessSuspender _suspender = null!;
    private Watchdog _watchdog = null!;
    private PinRetryGate _pinRetry = null!;
    private PinRetryGate _dashboardLoginRetry = null!;
    private PinRetryGate _ownerLoginRetry = null!;
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
            _ = RunSafeAsync("owner-recover", RecoverOwnerUidAsync);

            // Start unlock/pair streams now that _firebase/_deviceId are wired.
            SubscribeToRealtimeStreams();

            await Task.WhenAll(
                IntervalLoopAsync("heartbeat", TimeSpan.FromMilliseconds(PolicyConstants.HEARTBEAT_INTERVAL_MS), HeartbeatAsync),
                IntervalLoopAsync("state-upload", StateUploadInterval, () => UploadStatesAsync(false)),
                IntervalLoopAsync("activity-flush", ActivityFlushInterval, () => FlushActivityAsync(false)),
                // 15s ticker for daily-limit/budget/schedule boundaries + 5/10m warnings + pairing while unpaired.
                IntervalLoopAsync("boundary", TimeSpan.FromSeconds(15), BoundaryTickAsync),
                // 15s browser roll-up: accrue active-domain time and refresh state/browser.
                IntervalLoopAsync("browser-rollup", TimeSpan.FromSeconds(15), BrowserRollupTickAsync),
                IntervalLoopAsync("dead-man", DeadManCheckInterval, () => { DeadManCheck(); return Task.CompletedTask; }));
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
        // Owner (parent) client: signs in with the parent's email+password and can act
        // on every device under that account. Its refresh token persists in the same
        // DPAPI secret store as the device token, so the console stays signed in.
        _ownerClient = new RtdbFirebaseClient(_config, _secrets, isOwner: true,
            refreshTokenSecretKey: RtdbFirebaseClient.OwnerRefreshTokenSecretKey);
        _syncEngine = new SyncEngine(_firebase, _secrets, _deviceId, _time);
        _syncEngine.SetOwnerClient(_ownerClient);
        _ledger = new UsageLedger(_stateDir, _time);
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

        _pinRetry = new PinRetryGate(_time);
        // Separate gate from the lock-overlay PIN path: HTTP brute-force attempts must
        // not lock the child out of the overlay (and vice versa), while both stay bounded.
        _dashboardLoginRetry = new PinRetryGate(_time);
        _ownerLoginRetry = new PinRetryGate(_time);
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
        var json = JsonSerializer.Serialize(new DeviceIdentityJson(deviceId, secret, code), JsonOpts);
        File.WriteAllText(Path.Combine(_stateDir, "device.json"), json);
    }

    private void SubscribeEvents()
    {
        _syncEngine.ControlApplied += snapshot => _ = HandleControlAppliedAsync(snapshot);
        _syncEngine.ControlRejected += reason => _logger.LogWarning("Control revision rejected: {Reason}", reason);
        _syncEngine.CommandReceived += raw => _ = HandleCommandsStreamAsync(raw);

        _pipeHost.ForegroundReceived += (appKey, exePath, windowTitle) =>
            RunSafe("foreground", () => OnForegroundReceived(appKey, exePath, windowTitle));
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
            _pipeHost.BroadcastPairedState(IsPaired);
        };
        _pipeHost.AdminStateReceived += (session, isAdmin) => RunSafe("admin-state", () => OnAdminStateReceived(session, isAdmin));

        // Local web dashboard (browser page on the laptop): the device is allowed to
        // write control/v2 (firebase rule) and the Session dashboard talks over the pipe.
        _pipeHost.ControlGetHandler = BuildDashboardState;
        _pipeHost.ControlLoginHandler = HandleDashboardLogin;
        _pipeHost.ControlWriteHandler = HandleDashboardWrite;
        _pipeHost.ControlUnlockHandler = HandleDashboardUnlock;

        // Parent console (owner sign-in, device list, per-device control): the owner
        // client acts as the parent for any device under the signed-in account.
        _pipeHost.OwnerLoginHandler = HandleOwnerLogin;
        _pipeHost.ListDevicesHandler = HandleListDevices;
        _pipeHost.DeviceStateHandler = HandleDeviceState;
        _pipeHost.DeviceWriteHandler = HandleDeviceWrite;
        _pipeHost.DeviceUnlockHandler = HandleDeviceUnlock;
        _pipeHost.DeviceCommandHandler = HandleDeviceCommand;
        _pipeHost.DevicePinHandler = HandleDevicePin;
        _pipeHost.DeviceUnlockRespondHandler = HandleDeviceUnlockRespond;

        _watchdog.TamperDetected += (type, message) => _ = RunSafeAsync("tamper-push", () => PushTamperEventAsync(type, message));
        _ledger.ClockTampered += jumpMs => _ = RunSafeAsync("clock-tamper-push", () =>
            PushTamperEventAsync("clockTampered",
                $"System clock jumped backwards by {jumpMs / 1000}s while usage was being tracked; usage preserved via monotonic clock."));
    }

    // ----------------------------------------------------------------- dashboard
    private const long DashboardInventoryMaxAgeMs = 5 * 60_000;
    private List<InventoryApp>? _dashboardInventoryCache;
    private long _dashboardInventoryAtMs;

    private string BuildDashboardState(string req)
    {
        try
        {
            var snapshot = _syncEngine.LastValidSnapshot;
            // The local console never sees pending unlock approvals (those belong to the
            // parent surfaces — phone / remote console); everything else mirrors the
            // remote DTO so the UI renders both scopes identically.
            var syncStatus = snapshot != null && string.Equals(_syncEngine.LastAppliedRevision, snapshot.RevisionId, StringComparison.Ordinal)
                ? PolicyConstants.SYNC_STATUS_APPLIED
                : null;
            return ComposeStateJson(req, snapshot, _deviceId, IsPaired, snapshot?.Pin is not null,
                thisDevice: true, syncStatus: syncStatus,
                enforcementMode: PolicyConstants.ENFORCEMENT_FALLBACK,
                protectionHealthy: _pipeHost.ConnectedAgents > 0,
                inventoryApps: DashboardInvApps());
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { t = "controlState", req, ok = false, error = ex.Message }, JsonOpts);
        }
    }

    /// <summary>Builds the controlState DTO shared by the local (this-laptop) and remote (parent
    /// console) views. <paramref name="thisDevice"/> controls whether the local app inventory and
    /// per-app ledger usage are included (only meaningful for this laptop).</summary>
    private string ComposeStateJson(string req, ControlSnapshotV2? snapshot, string deviceId,
        bool paired, bool pinConfigured, bool thisDevice, List<object?>? remoteUsage = null,
        string? label = null, bool online = true, long lastSeen = 0,
        string? syncStatus = null, long? syncAppliedAt = null, string? syncRevisionId = null,
        List<object?>? pendingUnlocks = null, List<object?>? tamperEvents = null,
        string? enforcementMode = null, bool? protectionHealthy = null, List<InvApp>? inventoryApps = null)
    {
        var state = ComposeStateDto(snapshot, deviceId, paired, pinConfigured, thisDevice, remoteUsage,
            label, online, lastSeen, syncStatus, syncAppliedAt, syncRevisionId, pendingUnlocks, tamperEvents,
            enforcementMode, protectionHealthy, inventoryApps);
        return JsonSerializer.Serialize(new { t = "controlState", req, ok = true, state }, JsonOpts);
    }

    /// <summary>Serializes just the state DTO (no envelope) — the device-state cache stores
    /// this shape so a cached hit can be re-wrapped with the caller's fresh req id.</summary>
    private string ComposeStateDtoRaw(string deviceId, ControlSnapshotV2? snapshot,
        bool paired, bool pinConfigured, bool thisDevice, List<object?>? remoteUsage,
        string? label, bool online, long lastSeen,
        string? syncStatus, long? syncAppliedAt, string? syncRevisionId,
        List<object?>? pendingUnlocks, List<object?>? tamperEvents,
        string? enforcementMode, bool? protectionHealthy, List<InvApp>? inventoryApps = null)
    {
        var state = ComposeStateDto(snapshot, deviceId, paired, pinConfigured, thisDevice, remoteUsage,
            label, online, lastSeen, syncStatus, syncAppliedAt, syncRevisionId, pendingUnlocks, tamperEvents,
            enforcementMode, protectionHealthy, inventoryApps);
        return JsonSerializer.Serialize(state, JsonOpts);
    }

    private Dictionary<string, object?> ComposeStateDto(ControlSnapshotV2? snapshot, string deviceId,
        bool paired, bool pinConfigured, bool thisDevice, List<object?>? remoteUsage,
        string? label, bool online, long lastSeen,
        string? syncStatus, long? syncAppliedAt, string? syncRevisionId,
        List<object?>? pendingUnlocks, List<object?>? tamperEvents,
        string? enforcementMode, bool? protectionHealthy, List<InvApp>? inventoryApps = null)
    {
        var apps = BuildAppsList(snapshot, inventoryApps);
        var modes = BuildModesList(snapshot, out var activeId, out var activeName);
        var usage = remoteUsage ?? BuildUsageList(snapshot);
        var safe = snapshot?.SafeMode;
        var state = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["deviceId"] = deviceId,
            ["label"] = label ?? deviceId,
            ["online"] = online,
            ["lastSeen"] = lastSeen,
            ["thisDevice"] = thisDevice,
            ["paired"] = paired,
            ["pinConfigured"] = pinConfigured,
            ["apps"] = apps,
            ["inventory"] = thisDevice ? DashboardInventory() : Array.Empty<object?>(),
            ["modes"] = modes,
            ["activeModeId"] = string.IsNullOrEmpty(activeId) ? null : activeId,
            ["activeModeName"] = activeId != null && activeName != null ? activeName : null,
            ["safeMode"] = new { enabled = safe?.Enabled ?? false, until = safe?.Until ?? 0, startedAt = safe?.StartedAt },
            ["budgetMinutes"] = snapshot?.Budget?.DailyLimitMinutes,
            ["allowlistEnabled"] = snapshot?.Allowlist?.Enabled ?? false,
            ["customDomains"] = snapshot?.CustomBlockedDomains?.Domains ?? Array.Empty<string>(),
            ["schedule"] = snapshot?.Schedule == null
                ? null
                : new { enabled = snapshot.Schedule.Enabled, startMinute = snapshot.Schedule.StartMinute, endMinute = snapshot.Schedule.EndMinute },
            ["contentFilter"] = snapshot?.ContentFilter == null
                ? null
                : new
                {
                    social = snapshot.ContentFilter.Social,
                    gambling = snapshot.ContentFilter.Gambling,
                    adult = snapshot.ContentFilter.Adult,
                    gaming = snapshot.ContentFilter.Gaming,
                },
            ["usage"] = usage,
            // Live browser tab snapshot from the Session's BrowserWatcher (null when
            // the agent never saw a browser this session — e.g. TV-like usage).
            ["browser"] = BuildBrowserStateDto(),
            // The console's clock cannot be trusted for deadlines (safe mode "until",
            // request expiry); serverNow is the agent's best estimate of server time.
            ["serverNow"] = _syncEngine.ServerNowMs(),
            // The revision the device is currently enforcing, so the console can tell
            // "waiting for device" (syncRevisionId != controlRevisionId) from synced.
            ["controlRevisionId"] = snapshot?.RevisionId,
            ["enforcementMode"] = enforcementMode,
            ["protectionHealthy"] = protectionHealthy,
            ["syncStatus"] = syncStatus,
            ["syncAppliedAt"] = syncAppliedAt,
            ["syncRevisionId"] = syncRevisionId,
            ["pendingUnlocks"] = pendingUnlocks ?? new List<object?>(),
            ["tamperEvents"] = tamperEvents ?? new List<object?>(),
        };
        return state;
    }

    /// <summary>
    /// One inventoried app for the console merge. Key is the base64url package key;
    /// Blockable/ProtectedReason come from the device's own inventory upload.
    /// </summary>
    private sealed record InvApp(string Key, string PackageName, string Label, bool? Blockable, string? ProtectedReason);

    /// <summary>
    /// Builds the console apps list the way the phone does: EVERY inventoried app is
    /// listed, with the control policy overlaid where a rule exists. Apps without any
    /// rule are allowed; inventory "protected" entries carry their reason so the UI can
    /// mark them non-blockable. Sorted by label.
    /// </summary>
    private List<object?> BuildAppsList(ControlSnapshotV2? snapshot, List<InvApp>? inventoryApps = null)
    {
        // (packageName, label, blockable, protectedReason, blocked, limit)
        var merged = new Dictionary<string, (string Pkg, string Label, bool? Blockable, string? Reason, bool? Blocked, int? Limit)>(StringComparer.Ordinal);

        foreach (var inv in inventoryApps ?? new List<InvApp>())
        {
            merged[inv.Key] = (inv.PackageName, inv.Label, inv.Blockable, inv.ProtectedReason, null, null);
        }

        if (snapshot != null)
        {
            foreach (var (packageName, rule) in snapshot.EffectiveApps())
            {
                var key = PackageKeys.Encode(packageName);
                var label = merged.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing.Label)
                    ? existing.Label
                    : LabelFor(packageName);
                merged[key] = (packageName, label,
                    merged.TryGetValue(key, out var e2) ? e2.Blockable : null,
                    merged.TryGetValue(key, out var e3) ? e3.Reason : null,
                    rule.ManualBlocked, rule.DailyLimitMinutes);
            }
        }

        return merged
            .Select(kv => new
            {
                key = kv.Key,
                packageName = kv.Value.Pkg,
                label = string.IsNullOrWhiteSpace(kv.Value.Label) ? kv.Value.Pkg : kv.Value.Label,
                blocked = kv.Value.Blocked ?? false,
                dailyLimitMinutes = kv.Value.Limit,
                blockable = kv.Value.Blockable ?? true,
                protectedReason = kv.Value.Reason,
            })
            .OrderBy(a => a.label, StringComparer.OrdinalIgnoreCase)
            .ToList<object?>();
    }

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

    private IReadOnlyList<object?> DashboardInventory()
    {
        var list = new List<object?>();
        foreach (var app in EnsureDashboardInventoryCache())
        {
            // key is the base64url-encoded form (safe for HTML attributes/ids);
            // packageName is the raw exe path/package name for the control/v2 write.
            list.Add(new { key = PackageKeys.Encode(app.AppKey), label = app.Label, packageName = app.AppKey });
        }

        return list;
    }

    /// <summary>Strongly-typed view of the local inventory for the apps-list merge.</summary>
    private List<InvApp> DashboardInvApps()
    {
        var list = new List<InvApp>();
        foreach (var app in EnsureDashboardInventoryCache())
        {
            list.Add(new InvApp(PackageKeys.Encode(app.AppKey), app.AppKey, app.Label, app.Blockable, app.ProtectedReason));
        }

        return list;
    }

    private IReadOnlyList<InventoryApp> EnsureDashboardInventoryCache()
    {
        lock (_labelLock)
        {
            var now = Environment.TickCount64;
            if (_dashboardInventoryCache == null || now - _dashboardInventoryAtMs > DashboardInventoryMaxAgeMs)
            {
                _dashboardInventoryCache = [.. InventoryScanner.Scan()];
                _dashboardInventoryAtMs = now;
            }

            return _dashboardInventoryCache;
        }
    }

    /// <summary>Parses a device's uploaded inventory (devices/{id}/apps) for the console merge.</summary>
    private List<InvApp> ParseInventoryApps(string? json)
    {
        var list = new List<InvApp>();
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
                var key = p.Name;
                var packageName = p.Value.ValueKind == JsonValueKind.Object
                    && p.Value.TryGetProperty("packageName", out var pn)
                    && pn.ValueKind == JsonValueKind.String
                        ? pn.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace(packageName))
                {
                    try
                    {
                        packageName = PackageKeys.Decode(key);
                    }
                    catch (Exception ex) when (ex is FormatException or ArgumentException)
                    {
                        packageName = key;
                    }
                }

                var label = p.Value.ValueKind == JsonValueKind.Object
                    && p.Value.TryGetProperty("label", out var l)
                    && l.ValueKind == JsonValueKind.String
                        ? l.GetString()
                        : null;
                var blockable = p.Value.ValueKind == JsonValueKind.Object
                    && p.Value.TryGetProperty("blockable", out var b)
                    && (b.ValueKind == JsonValueKind.True || b.ValueKind == JsonValueKind.False)
                        ? b.GetBoolean()
                        : (bool?)null;
                var reason = p.Value.ValueKind == JsonValueKind.Object
                    && p.Value.TryGetProperty("protectedReason", out var pr)
                    && pr.ValueKind == JsonValueKind.String
                        ? pr.GetString()
                        : null;

                list.Add(new InvApp(key, packageName!, label ?? LabelFor(packageName!), blockable, reason));
            }
        }
        catch (JsonException)
        {
            // best effort: an unreadable inventory degrades to policy-only rows
        }

        return list;
    }

    private string HandleDashboardLogin(string req, string pin)
    {
        try
        {
            var stored = _syncEngine.LastValidSnapshot?.Pin;
            if (stored is null)
            {
                // No PIN configured: the local console bootstrap accepts any PIN. The
                // moment a PIN exists, the retry gate below applies like the overlay.
                return JsonSerializer.Serialize(new { t = "controlLoginResult", req, ok = true }, JsonOpts);
            }

            // /api/login is reachable by any local process; without a gate a 6-digit PIN
            // could be brute-forced quickly over loopback (the overlay path is gated too).
            if (_dashboardLoginRetry.IsBlocked())
            {
                return JsonSerializer.Serialize(new
                {
                    t = "controlLoginResult",
                    req,
                    ok = false,
                    error = "Too many attempts. Try again in a moment.",
                    blockedUntilMs = _dashboardLoginRetry.BlockedUntilMs(),
                }, JsonOpts);
            }

            var ok = PinHasher.Verify(pin, stored.Salt, stored.Hash, stored.Version, stored.Algorithm, stored.Iterations);
            if (ok)
            {
                _dashboardLoginRetry.RecordSuccess();
            }
            else
            {
                _dashboardLoginRetry.RecordFailure();
            }

            return JsonSerializer.Serialize(new { t = "controlLoginResult", req, ok }, JsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { t = "controlLoginResult", req, ok = false, error = ex.Message }, JsonOpts);
        }
    }

    private async Task<string> HandleDashboardWrite(string req, string patch)
    {
        try
        {
            var (ok, error, revisionId) = await _syncEngine.WriteControlV2Async(patch, CancellationToken.None);
            return DashboardResult(req, ok, error, revisionId);
        }
        catch (Exception ex)
        {
            return DashboardResult(req, false, ex.Message);
        }
    }

    private string HandleDashboardUnlock(string req, string appKey, string type, long? durationMs)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(appKey))
            {
                return DashboardResult(req, false, "appKey is required");
            }

            if (string.Equals(type, "timed", StringComparison.OrdinalIgnoreCase))
            {
                _unlocks.Grant(appKey, TimeSpan.FromMilliseconds(durationMs ?? 15 * 60_000));
            }
            else
            {
                _unlocks.Grant(appKey);
            }

            return DashboardResult(req, true, null);
        }
        catch (Exception ex)
        {
            return DashboardResult(req, false, ex.Message);
        }
    }

    // --------------------------------------------------------- parent console (owner)

    private async Task<string> HandleOwnerLogin(string req, string email, string password)
    {
        try
        {
            if (_ownerClient is null)
            {
                return JsonSerializer.Serialize(new { t = "ownerLoginResult", req, ok = false, error = "Owner client is not available." }, JsonOpts);
            }

            if (_ownerLoginRetry.IsBlocked())
            {
                return JsonSerializer.Serialize(new { t = "ownerLoginResult", req, ok = false, error = "Too many sign-in attempts. Try again later." }, JsonOpts);
            }

            var (ok, error, uid) = await _ownerClient.SignInWithEmailPasswordAsync(email, password, CancellationToken.None);
            if (!ok || uid is null)
            {
                _ownerLoginRetry.RecordFailure();
                return JsonSerializer.Serialize(new { t = "ownerLoginResult", req, ok = false, error = error ?? "Sign-in failed." }, JsonOpts);
            }

            _ownerLoginRetry.RecordSuccess();
            var devicesJson = await _syncEngine.ReadDeviceListAsync(uid, CancellationToken.None);
            var devices = ParseDeviceList(devicesJson);
            return JsonSerializer.Serialize(new { t = "ownerLoginResult", req, ok = true, ownerUid = uid, devices }, JsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { t = "ownerLoginResult", req, ok = false, error = ex.Message }, JsonOpts);
        }
    }

    private async Task<string> HandleListDevices(string req)
    {
        try
        {
            if (_ownerClient is null || !_ownerClient.IsOwnerSignedIn || _ownerClient.Uid is null)
            {
                return JsonSerializer.Serialize(new { t = "deviceList", req, ok = false, error = "Owner not signed in." }, JsonOpts);
            }

            var devicesJson = await _syncEngine.ReadDeviceListAsync(_ownerClient.Uid, CancellationToken.None);
            var devices = ParseDeviceList(devicesJson);
            return JsonSerializer.Serialize(new { t = "deviceList", req, ok = true, devices }, JsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { t = "deviceList", req, ok = false, error = ex.Message }, JsonOpts);
        }
    }

    private async Task<string> HandleDeviceState(string req, string deviceId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return JsonSerializer.Serialize(new { t = "controlState", req, ok = false, error = "deviceId is required." }, JsonOpts);
            }

            if (_ownerClient is null || !_ownerClient.IsOwnerSignedIn || _ownerClient.Uid is null)
            {
                return JsonSerializer.Serialize(new { t = "controlState", req, ok = false, error = "Owner not signed in." }, JsonOpts);
            }

            // The SSE event loop re-fetches this state on a timer; a short cache keeps
            // an open console from multiplying Firebase reads (each compose is ~6 GETs).
            if (TryGetCachedDeviceState(deviceId, out var cachedRaw))
            {
                return WrapCachedDeviceState(req, cachedRaw);
            }

            var (snapshot, _) = await _syncEngine.ReadControlV2Async(deviceId, CancellationToken.None);

            var label = deviceId;
            var online = false;
            long lastSeen = 0;
            string? enforcementMode = null;
            bool? protectionHealthy = null;
            var listRaw = await _syncEngine.ReadDeviceListEntryAsync(_ownerClient.Uid, deviceId, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(listRaw) && listRaw!.Trim() != "null")
            {
                try
                {
                    using var doc = JsonDocument.Parse(listRaw);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var r = doc.RootElement;
                        if (r.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String)
                        {
                            label = l.GetString() ?? deviceId;
                        }

                        online = r.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.True;
                        if (r.TryGetProperty("lastSeen", out var ls) && ls.ValueKind == JsonValueKind.Number)
                        {
                            lastSeen = ls.GetInt64();
                        }

                        // Same tiles the phone's device card shows (Mode / Health).
                        if (r.TryGetProperty("enforcementMode", out var em) && em.ValueKind == JsonValueKind.String)
                        {
                            enforcementMode = em.GetString();
                        }

                        if (r.TryGetProperty("protectionHealthy", out var ph)
                            && (ph.ValueKind == JsonValueKind.True || ph.ValueKind == JsonValueKind.False))
                        {
                            protectionHealthy = ph.GetBoolean();
                        }
                    }
                }
                catch (JsonException)
                {
                    // best effort: fall back to deviceId
                }
            }

            var stateRaw = await _syncEngine.ReadDeviceAppsStateAsync(deviceId, CancellationToken.None);
            // Full installed-app inventory: the console lists every app (phone parity),
            // with the control policy overlaid where a rule exists.
            var inventoryRaw = await _syncEngine.ReadDeviceInventoryAsync(deviceId, CancellationToken.None);
            var inventoryApps = ParseInventoryApps(inventoryRaw);
            var labelByPkg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var inv in inventoryApps)
            {
                labelByPkg[inv.PackageName] = inv.Label;
            }

            var usage = ParseAppsState(stateRaw, labelByPkg);

            string? syncStatus = null;
            long? syncAppliedAt = null;
            string? syncRevisionId = null;
            var syncRaw = await _syncEngine.ReadDeviceSyncAppliedAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(syncRaw) && syncRaw!.Trim() != "null")
            {
                try
                {
                    using var doc = JsonDocument.Parse(syncRaw);
                    var r = doc.RootElement;
                    if (r.ValueKind == JsonValueKind.Object)
                    {
                        syncStatus = r.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                        syncRevisionId = r.TryGetProperty("revisionId", out var rid) && rid.ValueKind == JsonValueKind.String ? rid.GetString() : null;
                        if (r.TryGetProperty("appliedAt", out var aa) && aa.ValueKind == JsonValueKind.Number && aa.TryGetInt64(out var appliedAt))
                        {
                            syncAppliedAt = appliedAt;
                        }
                    }
                }
                catch (JsonException)
                {
                    // best effort: no sync status
                }
            }

            var pendingUnlocksRaw = await _syncEngine.ReadUnlockRequestsAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
            var pendingUnlocks = ParsePendingUnlocks(pendingUnlocksRaw);

            var tamperRaw = await _syncEngine.ReadTamperEventsAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
            var tamperEvents = ParseTamperEvents(tamperRaw);

            var stateJson = ComposeStateDtoRaw(deviceId, snapshot, paired: true, pinConfigured: snapshot?.Pin is not null,
                thisDevice: false, remoteUsage: usage, label: label, online: online, lastSeen: lastSeen,
                syncStatus: syncStatus, syncAppliedAt: syncAppliedAt, syncRevisionId: syncRevisionId,
                pendingUnlocks: pendingUnlocks, tamperEvents: tamperEvents,
                enforcementMode: enforcementMode, protectionHealthy: protectionHealthy,
                inventoryApps: inventoryApps);
            CacheDeviceState(deviceId, stateJson);
            return WrapCachedDeviceState(req, stateJson);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { t = "controlState", req, ok = false, error = ex.Message }, JsonOpts);
        }
    }

    // ------------------------------------------------- device state cache (SSE)
    private const long DeviceStateCacheTtlMs = 10_000;
    private readonly object _deviceStateCacheLock = new();
    private readonly Dictionary<string, (string Raw, long AtMs)> _deviceStateCache = new(StringComparer.Ordinal);

    private bool TryGetCachedDeviceState(string deviceId, out string raw)
    {
        lock (_deviceStateCacheLock)
        {
            if (_deviceStateCache.TryGetValue(deviceId, out var hit)
                && NowMs() - hit.AtMs < DeviceStateCacheTtlMs)
            {
                raw = hit.Raw;
                return true;
            }
        }

        raw = "";
        return false;
    }

    private void CacheDeviceState(string deviceId, string raw)
    {
        lock (_deviceStateCacheLock)
        {
            if (_deviceStateCache.Count > 32)
            {
                _deviceStateCache.Clear(); // bounded; entries are cheap to rebuild
            }

            _deviceStateCache[deviceId] = (raw, NowMs());
        }
    }

    private void InvalidateDeviceStateCache(string deviceId)
    {
        lock (_deviceStateCacheLock)
        {
            _deviceStateCache.Remove(deviceId);
        }
    }

    private static string WrapCachedDeviceState(string req, string stateRaw)
    {
        var payload = new JsonObject
        {
            ["t"] = "controlState",
            ["req"] = req,
            ["ok"] = true,
            ["state"] = JsonNode.Parse(stateRaw),
        };
        return payload.ToJsonString(JsonOpts);
    }

    /// <summary>Pulses the console's SSE event stream so open dashboards refresh immediately
    /// (control applied, usage/state written, unlock requests, tamper events).</summary>
    private void BroadcastDataChanged(string deviceId)
    {
        try
        {
            _pipeHost.BroadcastDataChanged(deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "dataChanged broadcast failed");
        }
    }

    private async Task<string> HandleDeviceWrite(string req, string deviceId, string patch)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return DashboardResult(req, false, "deviceId is required.");
            }

            if (_ownerClient is null || !_ownerClient.IsOwnerSignedIn)
            {
                return JsonSerializer.Serialize(new { t = "controlResult", req, ok = false, error = "Owner not signed in." }, JsonOpts);
            }

            var (ok, error, revisionId) = await _syncEngine.WriteControlV2ForDeviceAsync(deviceId, patch, CancellationToken.None);
            if (ok)
            {
                InvalidateDeviceStateCache(deviceId);
            }

            return DashboardResult(req, ok, error, revisionId);
        }
        catch (Exception ex)
        {
            return DashboardResult(req, false, ex.Message);
        }
    }

    private async Task<string> HandleDeviceUnlock(string req, string deviceId, string appKey, string type, long? durationMs)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return DashboardResult(req, false, "deviceId is required.");
            }

            if (_ownerClient is null || !_ownerClient.IsOwnerSignedIn)
            {
                return JsonSerializer.Serialize(new { t = "controlResult", req, ok = false, error = "Owner not signed in." }, JsonOpts);
            }

            var (ok, error) = await _syncEngine.RequestUnlockForDeviceAsync(deviceId, appKey, type, durationMs, CancellationToken.None);
            return DashboardResult(req, ok, error);
        }
        catch (Exception ex)
        {
            return DashboardResult(req, false, ex.Message);
        }
    }

    private async Task<string> HandleDeviceCommand(string req, string deviceId, string type, string? packageName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return DashboardResult(req, false, "deviceId is required.");
            }

            if (_ownerClient is null || !_ownerClient.IsOwnerSignedIn)
            {
                return JsonSerializer.Serialize(new { t = "controlResult", req, ok = false, error = "Owner not signed in." }, JsonOpts);
            }

            var (ok, error) = await _syncEngine.SendCommandForDeviceAsync(deviceId, type, packageName, CancellationToken.None);
            if (ok)
            {
                InvalidateDeviceStateCache(deviceId);
            }

            return DashboardResult(req, ok, error);
        }
        catch (Exception ex)
        {
            return DashboardResult(req, false, ex.Message);
        }
    }

    private async Task<string> HandleDevicePin(string req, string deviceId, string pin)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return DashboardResult(req, false, "deviceId is required.");
            }

            if (_ownerClient is null || !_ownerClient.IsOwnerSignedIn)
            {
                return JsonSerializer.Serialize(new { t = "controlResult", req, ok = false, error = "Owner not signed in." }, JsonOpts);
            }

            var (ok, error) = await _syncEngine.SetPinForDeviceAsync(deviceId, pin, CancellationToken.None);
            if (ok)
            {
                InvalidateDeviceStateCache(deviceId);
            }

            return DashboardResult(req, ok, error);
        }
        catch (Exception ex)
        {
            return DashboardResult(req, false, ex.Message);
        }
    }

    private async Task<string> HandleDeviceUnlockRespond(string req, string deviceId, string requestId, string action)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return DashboardResult(req, false, "deviceId is required.");
            }

            if (_ownerClient is null || !_ownerClient.IsOwnerSignedIn)
            {
                return JsonSerializer.Serialize(new { t = "controlResult", req, ok = false, error = "Owner not signed in." }, JsonOpts);
            }

            var (ok, error) = await _syncEngine.RespondUnlockRequestAsync(deviceId, requestId, action, CancellationToken.None);
            if (ok)
            {
                InvalidateDeviceStateCache(deviceId);
            }

            return DashboardResult(req, ok, error);
        }
        catch (Exception ex)
        {
            return DashboardResult(req, false, ex.Message);
        }
    }

    private static string DashboardResult(string req, bool ok, string? error, string? revisionId = null) =>
        JsonSerializer.Serialize(new { t = "controlResult", req, ok, error, revisionId }, JsonOpts);

    // ----------------------------------------------------------------- control
    private async Task HandleControlAppliedAsync(ControlSnapshotV2 snapshot)
    {
        try
        {
            _logger.LogInformation("Applying control revision {RevisionId}", snapshot.RevisionId);

            WritePolicyCache(snapshot);
            EvaluateCurrentForeground();

            _ = Task.Run(async () =>
            {
                // Ack FIRST: the parent's Sync card must reflect enforcement even when the
                // per-app state upload below fails (it used to gate the ack, leaving
                // sync/applied stale on upload errors).
                try
                {
                    TryWriteText(Path.Combine(_stateDir, "enforcement-state.json"),
                        new JsonObject { ["revisionId"] = snapshot.RevisionId, ["appliedAtMs"] = NowMs() }.ToJsonString(JsonOpts));

                    ApplyContentFilterHosts(snapshot);
                    await _syncEngine.NotifyEnforcementAppliedAsync(snapshot.RevisionId);
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

                BroadcastDataChanged(_deviceId);
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
            var safeMode = false;

            if (snapshot is not null)
            {
                safeMode = _enforcement.SafeModeActive(snapshot);
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
                ["dailyBlockedApps"] = dailyBlocked
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

        var decision = _enforcement.Decide(snapshot, appKey, _ledger, _unlocks, _agentAppKey);
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

        lock (_gate)
        {
            if (string.Equals(_currentAppKey, appKey, StringComparison.OrdinalIgnoreCase))
            {
                return; // same app; periodic re-evaluation covers time-based changes
            }
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
                _unlocks.Grant(appKey);
            }

            _suspender.ResumeAll();
            _pipeHost.BroadcastUnlock();
            _activity.SetOverlayState("none");
            lock (_gate)
            {
                _currentOverlay = "none";
            }

            _logger.LogInformation("PIN accepted; one-visit unlock granted for {AppKey}", appKey);
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
            // Server clock, not the local one: the owner console filters pending
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
            if (_handledUnlockRequests.Contains(requestId))
            {
                continue;
            }

            // Approvals past their TTL must never re-grant after a restart. Server-time
            // based: expiresAt is written from the server clock (owner side filters the
            // same way), so a skewed local clock can't resurrect expired approvals.
            var expiresAt = GetLong(property.Value, "expiresAt");
            if (expiresAt > 0 && _syncEngine.ServerNowMs() > expiresAt)
            {
                _handledUnlockRequests.Add(requestId);
                continue;
            }

            var status = GetString(property.Value, "status");
            if (status != PolicyConstants.UNLOCK_APPROVED)
            {
                if (status is PolicyConstants.UNLOCK_DENIED or PolicyConstants.UNLOCK_EXPIRED)
                {
                    _handledUnlockRequests.Add(requestId);
                }

                continue;
            }

            _handledUnlockRequests.Add(requestId);
            if (_handledUnlockRequests.Count > HandledUnlockRequestsMax)
            {
                // Bound the dedupe set the same way _processedCommands is bounded.
                foreach (var stale in _handledUnlockRequests.Take(_handledUnlockRequests.Count - HandledUnlockRequestsMax))
                {
                    _handledUnlockRequests.Remove(stale);
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
            _unlocks.Grant(appKey, duration);
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

        // The pending-requests list changed; wake any open console watching this device.
        BroadcastDataChanged(_deviceId);
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
            if (_processedCommands.Contains(commandId))
            {
                continue;
            }

            var value = property.Value;
            var status = GetString(value, "status");
            if (!string.IsNullOrEmpty(status) && status != PolicyConstants.COMMAND_PENDING)
            {
                _processedCommands.Add(commandId);
                continue;
            }

            var type = GetString(value, "type");
            if (string.IsNullOrEmpty(type))
            {
                continue;
            }

            var createdAt = GetLong(value, "createdAt");
            var ttl = CommandTtl(type);
            if (createdAt <= 0 || NowMs() > createdAt + (long)ttl.TotalMilliseconds)
            {
                _processedCommands.Add(commandId);
                await FinishCommandAsync(commandId, PolicyConstants.COMMAND_EXPIRED, null);
                continue;
            }

            // Claim the command so a second service instance cannot double-run it.
            // "claimedAt" is the rules-whitelisted field (not the TV's startedAt).
            var claim = new JsonObject { ["status"] = PolicyConstants.COMMAND_RUNNING, ["claimedAt"] = Sv() };
            await _firebase.PatchAsync(
                FirebasePaths.DeviceCommands(_deviceId) + "/" + commandId, claim.ToJsonString(JsonOpts), _ct);

            _processedCommands.Add(commandId);
            if (_processedCommands.Count > 1000)
            {
                _processedCommands.Clear();
                _processedCommands.Add(commandId);
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
                }
                else
                {
                    _ledger.SetResetOffset(packageName);
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
        _handledUnlockRequests.Clear();
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

    private async Task PollPairRequestsAsync()
    {
        if (!string.IsNullOrEmpty(_ownerUid))
        {
            return; // already paired
        }

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

        var now = NowMs();
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

        var heartbeat = new JsonObject
        {
            ["online"] = true,
            ["lastSeen"] = Sv(),
            ["enforcementMode"] = PolicyConstants.ENFORCEMENT_FALLBACK,
            ["protectionHealthy"] = healthy
        };
        await _firebase.PatchAsync(FirebasePaths.DeviceHeartbeat(_deviceId), heartbeat.ToJsonString(JsonOpts), _ct);

        var runtime = new JsonObject
        {
            ["lastHeartbeatWriteAt"] = Sv(),
            ["connected"] = true,
            ["sessionId"] = _syncEngine.SessionId,
            ["protocolVersion"] = 2
        };
        await _firebase.PatchAsync(FirebasePaths.DeviceSyncRuntime(_deviceId), runtime.ToJsonString(JsonOpts), _ct);

        var security = new JsonObject
        {
            ["enforcementMode"] = PolicyConstants.ENFORCEMENT_FALLBACK,
            ["protectionHealthy"] = healthy,
            ["pinConfigured"] = pinConfigured,
            ["platform"] = PolicyConstants.PLATFORM_WINDOWS,
            ["updatedAt"] = Sv()
        };
        await _firebase.PatchAsync(FirebasePaths.DeviceSecurityRuntime(_deviceId), security.ToJsonString(JsonOpts), _ct);

        // Mirror online status onto the parent's device list (same as the TV agent).
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
            await _firebase.PatchAsync(FirebasePaths.UserDevice(ownerUid, _deviceId), userDevice.ToJsonString(JsonOpts), _ct);
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
            _lastUploadedAppStates.Clear();
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
            if (!_lastUploadedAppStates.TryGetValue(encodedKey, out var previous) || previous != json)
            {
                changedStates[encodedKey] = entry.DeepClone();
                _lastUploadedAppStates[encodedKey] = json;
            }
        }

        if (changedStates.Count > 0)
        {
            await _firebase.PatchAsync(FirebasePaths.DeviceStateApps(_deviceId), changedStates.ToJsonString(JsonOpts), _ct);
            // Usage/status changed; wake any open console watching this device.
            BroadcastDataChanged(_deviceId);
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
            _activity.StartTab(snapshot.AppKey, tabTitle.Length > 160 ? tabTitle[..160] : tabTitle, NowMs(), snapshot.ActiveUrl);
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
            ["browser"] = snapshot.AppKey,
            ["label"] = snapshot.Label,
            ["activeTab"] = snapshot.ActiveTab,
            ["activeUrl"] = snapshot.ActiveUrl,
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
        BroadcastDataChanged(_deviceId);
    }

    private static JsonArray BuildTabsJson(IReadOnlyList<PipeBrowserTab> tabs)
    {
        var array = new JsonArray();
        foreach (var tab in tabs)
        {
            var entry = new JsonObject { ["title"] = tab.Title };
            if (!string.IsNullOrEmpty(tab.Url))
            {
                entry["url"] = tab.Url;
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
                ["browser"] = _currentBrowser.AppKey,
                ["label"] = _currentBrowser.Label,
                ["activeTab"] = _currentBrowser.ActiveTab,
                ["activeUrl"] = _currentBrowser.ActiveUrl,
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
            var history = new JsonObject
            {
                ["id"] = id,
                ["type"] = string.IsNullOrEmpty(entryType) ? "app" : entryType,
                ["appKey"] = PickString(node, "AppKey", "appKey", "PackageName", "packageName"),
                ["appLabel"] = PickString(node, "Label", "label", "AppLabel", "appLabel"),
                ["title"] = PickString(node, "Title", "title"),
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
        BroadcastDataChanged(_deviceId);
    }

    // ------------------------------------------------------------------- helpers
    private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

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
        _ledger.FlushDirty();
        CheckScreenTimeWarnings();
        if (string.IsNullOrEmpty(_ownerUid))
        {
            TryStartPairStreamIfNeeded();
            _ = RunSafeAsync("pairing-boundary", PollPairRequestsAsync);
        }
        else
        {
            StopPairStreamIfPaired();
        }
        _ = RunSafeAsync("unlock-boundary", PollUnlockRequestsAsync);
        // Commands have no other poll fallback: if the commands SSE stream is down,
        // owner-sent rescanApps/resetToday/unpair/openSetup would never arrive.
        _ = RunSafeAsync("commands-boundary", PollCommandsAsync);
        return Task.CompletedTask;
    }

    private void CheckScreenTimeWarnings()
    {
        var snapshot = _syncEngine.LastValidSnapshot;
        if (snapshot is null) return;

        var today = DateTime.UtcNow.ToString("yyyyMMdd");

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

        // 2. Whole-device daily budget warning
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

    private sealed record DeviceIdentityJson(string DeviceId, string Secret, string Code);

    // Local PIN retry policy: 5 failures inside 5 minutes block further attempts for
    // 60s, doubling per consecutive lock (capped at 15 minutes); success resets state.
    // (PinRetryPolicy is not part of CONTRACTS.md, so it lives here.)
    private sealed class PinRetryGate(TimeProvider time)
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
            }
        }

        private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();
    }
}
