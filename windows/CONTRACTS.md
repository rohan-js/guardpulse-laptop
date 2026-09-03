# GuardPulse Laptop Windows Agent — Shared Contracts

This file is the single source of truth for cross-project contracts.
All agents MUST code against exactly these signatures. Do not rename or move.

## Projects & namespaces

```
windows/src/GuardPulse.Protocol      -> namespace GuardPulse.Protocol        (netstandard2.1)
windows/src/GuardPulse.Agent.Core    -> namespace GuardPulse.Agent.Core      (net8.0, refs Protocol)
windows/src/GuardPulse.Agent.Service -> namespace GuardPulse.Agent.Service   (net8.0-windows, refs Core)
windows/src/GuardPulse.Agent.Session -> namespace GuardPulse.Agent.Session   (net8.0-windows WPF, refs Core)
windows/tests/GuardPulse.Protocol.Tests   (xunit)
windows/tests/GuardPulse.Agent.Core.Tests (xunit)
```

## GuardPulse.Protocol public surface (port EXACTLY from Kotlin sources)

Kotlin sources to port are in this repo at:
`shared/src/main/java/com/guardpulse/parentcontrol/shared/{PackageKeys,PinHasher,ControlProtocol,FirebasePaths,PolicyConstants}.kt`
and their tests under `shared/src/test/java/com/guardpulse/parentcontrol/shared/`.

C# surface (records/classes):

```csharp
public static class PackageKeys { public static string Encode(string packageName); public static string Decode(string key); }

public sealed record PinHash(string Salt, string Hash);
public static class PinHasher
{
    public const int LEGACY_VERSION = 1; public const int CURRENT_VERSION = 2;
    public const string ALGORITHM = "PBKDF2WithHmacSHA256"; public const int ITERATIONS = 600000;
    public static PinHash Create(string pin);                      // v2 PBKDF2, 16-byte salt, 32-byte key, base64url no padding
    public static bool Verify(string pin, string salt, string expectedHash, int version = 1, string? algorithm = null, int? iterations = null); // constant-time compare
}

public sealed record ControlAppRule(string PackageName, bool ManualBlocked, int? DailyLimitMinutes, long? UpdatedAt = null);
public sealed record ControlMode(string ModeId, string Name, IReadOnlyDictionary<string, ControlAppRule> Apps, long? CreatedAt = null, long? UpdatedAt = null);
public sealed record ControlActiveMode(string? ModeId, string? ModeName, long? ActivatedAt = null);
public sealed record ControlSafeMode(bool Enabled = false, long Until = 0, long? StartedAt = null, string? StartedBy = null);
public sealed record ControlPin(string Salt, string Hash, int Version = 1, string? Algorithm = null, int? Iterations = null, long? UpdatedAt = null);
public sealed record ControlSnapshotV2(
    string RevisionId, long? UpdatedAt, string? UpdatedBy,
    IReadOnlyDictionary<string, ControlAppRule> Apps,
    IReadOnlyDictionary<string, ControlMode> Modes,
    ControlActiveMode? ActiveMode, ControlSafeMode SafeMode, ControlPin? Pin)
{
    public IReadOnlyDictionary<string, ControlAppRule> EffectiveApps();  // active mode's apps + default-locked filled in
}

public enum ControlParseStatus { Missing, Valid, Invalid }
public static class ControlProtocol
{
    // input: raw JSON of devices/{id}/control/v2 (System.Text.Json JsonElement or string)
    public static ControlParseResult Parse(string json);
    // Validate exactly like Kotlin: schemaVersion==2, revisionId nonblank & CHANGED, mode key==modeId,
    // activeMode exists in modes, safeMode invariants, pin salt/hash shapes + version 1|2 + PBKDF2 bounds,
    // app key == base64url(packageName), manualBlocked present, dailyLimit 1..1440.
    public static SyncDesiredRevision? ParseDesired(string json);
}
public sealed record ControlParseResult(ControlParseStatus Status, ControlSnapshotV2? Snapshot, string? Error);

public sealed record SyncDesiredRevision(string RevisionId, string Kind, string? Target = null, long? RequestedAt = null, string? RequestedBy = null);
public sealed record SyncAppliedRevision(string? RevisionId, string? Status, long? AppliedAt, string? SessionId, string? Error);

public static class FirebasePaths  // same functions as Kotlin, string paths
{
    public static string UserDevices(string parentUid); public static string UserDevice(string parentUid, string deviceId);
    public static string DeviceRoot/meta/Apps/PolicyApps/ControlV2/SyncDesired/SyncApplied/SyncRuntime/StateApps/Heartbeat/Commands/SecurityPin/SecurityRuntime/TamperEvents/UnlockRequests/PairRequests(string deviceId);
    public static string DeviceUnlockRequest(string deviceId, string requestId);
    public static string DeviceActivityCurrent(string deviceId);           // devices/{id}/activity/current
    public static string DeviceActivityHistory(string deviceId);           // devices/{id}/activity/history
    public static string DeviceActivityHistoryItem(string deviceId, string sessionId);
    public static string PairRequest(string deviceId, string requestId);
}

public static class PolicyConstants
{
    public const string PLATFORM_WINDOWS = "windows";
    public const string ENFORCEMENT_FALLBACK = "fallback"; public const string ENFORCEMENT_UNPROTECTED = "unprotected";
    public const string COMMAND_RESCAN_APPS = "rescanApps"; COMMAND_RESET_TODAY = "resetToday"; COMMAND_UNPAIR = "unpair"; COMMAND_OPEN_SETUP = "openSetup";
    public const string UNLOCK_PENDING/APPROVED/DENIED/EXPIRED; UNLOCK_APPROVAL_ONE_VISIT = "oneVisit"; UNLOCK_APPROVAL_TIMED = "timed";
    public const string SYNC_STATUS_APPLIED = "applied"; SYNC_STATUS_FAILED = "failed";
    // Windows bypass virtual app ids (default-locked):
    public const string WINDOWS_TASK_MANAGER_PACKAGE = "guardpulse.windows.taskmgr";
    public const string WINDOWS_COMMAND_LINE_PACKAGE = "guardpulse.windows.commandline";
    public const string WINDOWS_REGISTRY_EDITOR_PACKAGE = "guardpulse.windows.registry";
    public const string WINDOWS_SETTINGS_PACKAGE = "guardpulse.windows.settings";
    public const string WINDOWS_INSTALLERS_PACKAGE = "guardpulse.windows.installers";
    public static IReadOnlySet<string> WindowsBypassPackages { get; }
    public const long PAIRING_TTL_MS = 600_000; public const long HEARTBEAT_INTERVAL_MS = 60_000;
    public const long TAMPER_EVENT_THROTTLE_MS = 900_000; public const int PIN_LENGTH = 6; public const int MAX_DAILY_LIMIT_MINUTES = 1440;
}
```

## GuardPulse.Agent.Core public surface

```csharp
public sealed record AgentConfig(string ApiKey, string ProjectId, string DatabaseUrl, string? DeviceId = null, string? RefreshToken = null, string? ExemptAccount = null);
public static class AgentConfigLoader { public static AgentConfig Load(string path); }

// Firebase: REST + SSE, anonymous Identity Toolkit auth
public interface IFirebaseClient : IDisposable
{
    string? Uid { get; }                       // anonymous uid once signed in
    Task SignInAsync(CancellationToken ct);    // signUp or token refresh; persists refresh token via ISecretStore
    Task<string> GetAsync(string path, CancellationToken ct);            // returns raw JSON ("null" when absent)
    Task PutAsync(string path, string json, CancellationToken ct);
    Task PatchAsync(string path, string json, CancellationToken ct);     // updateChildren semantics
    Task<IDisposable> StreamAsync(string path, Action<string?> onData, Action<Exception> onError, CancellationToken ct); // SSE; onData gets raw JSON of event data (null on delete)
    Task<long> FetchServerTimeOffsetMsAsync(CancellationToken ct);       // .info/serverTimeOffset
}

public interface ISecretStore   // DPAPI-backed (CurrentUser, DataProtectionScope.CurrentUser is fine for the service account)
{
    string? Get(string key); void Set(string key, string value); void Delete(string key);
}
public sealed class DpapiSecretStore : ISecretStore { public DpapiSecretStore(string fileName); } // %ProgramData%\GuardPulse\Laptop\{fileName}

// Sync engine — full V2 compliance (see docs/reliability-architecture.md in repo root)
public sealed class SyncEngine
{
    public SyncEngine(IFirebaseClient firebase, ISecretStore secrets, string deviceId, TimeProvider time);
    public string SessionId { get; }
    public event Action<ControlSnapshotV2>? ControlApplied;       // fired when a VALID snapshot should be enforced
    public event Action<string>? ControlRejected;                  // revisionId + reason
    public Task StartAsync(CancellationToken ct);                  // auth, listeners (connected, control/v2, sync/desired, commands, pairRequests)
    // Debounce 20ms; validate; persist last-valid snapshot (DPAPI, key "snapshot.v2"); ack ONLY after durable apply:
    public Task NotifyEnforcementAppliedAsync(string revisionId);  // PATCH sync/applied {revisionId,status:"applied",appliedAt:now,sessionId}
    public Task NotifyEnforcementFailedAsync(string revisionId, string error);
    public ControlSnapshotV2? LastValidSnapshot { get; }           // fail-closed offline protection
}

// Usage ledger — ms precision, local-midnight day keys "yyyy-MM-dd", resetToday offsets
public sealed class UsageLedger
{
    public UsageLedger(string stateDirectory, TimeProvider time);
    public void OnForegroundChanged(string appKey, long timestampMs);  // closes previous session, opens new
    public long EffectiveUsageMsToday(string appKey);                   // ledger + offsets clamp
    public IReadOnlyDictionary<string, long> UsageMsToday();
    public void SetResetOffset(string appKey);                          // offset = current effective usage
    public void ClearDayBlocks(); public void MarkDailyBlocked(string appKey); public bool IsDailyBlocked(string appKey);
}

// Activity log — sessions history, 30-day retention, queue-then-ack
public sealed class ActivityLog
{
    public ActivityLog(string stateDirectory, TimeProvider time);
    public void StartApp(string appKey, string label, long startedAtMs);
    public void CloseCurrent(long endedAtMs, string? overlayState = null);
    public void SetOverlayState(string overlayState);                  // "none"|"locked"
    public sealed record CurrentSnapshot(...); public sealed record HistoryEntry(...);
    public CurrentSnapshot? Current(); public IReadOnlyList<HistoryEntry> Pending(); public void MarkUploaded(string id);
    public void PruneBefore(long cutoffMs);
}

// Enforcement decisions
public sealed record BlockDecision(bool Locked, string Reason, string AppKey); // reason: manual|dailyLimit|bypass
public sealed class EnforcementEngine
{
    public EnforcementEngine(TimeProvider time);
    public BlockDecision Decide(ControlSnapshotV2 snapshot, string appKey, UsageLedger ledger, OneVisitUnlocks unlocks, string agentAppKey);
    public bool SafeModeActive(ControlSnapshotV2 snapshot);
}
public sealed class OneVisitUnlocks
{
    public void Grant(string appKey, TimeSpan? duration = null); public bool IsUnlocked(string appKey); public void Clear(string appKey); public void ClearAll();
}

// Inventory
public sealed record InventoryApp(string AppKey, string Label, bool Blockable, string? ProtectedReason = null, bool BypassRow = false);
public static class InventoryScanner
{
    public static IReadOnlyList<InventoryApp> Scan();   // Start Menu .lnk (all users + current), registry uninstall entries, running-dir dedupe by exe path; PLUS the 5 bypass virtual rows (BypassRow=true, labels: "Task Manager", "Command Prompt & PowerShell", "Registry Editor", "Windows Settings", "Installers")
    public static string AppKeyForProcess(string exePath);   // stable key: lowercase full exe path
    public static string? MatchBypassRow(string exePath, string? windowTitle); // maps taskmgr.exe/cmd.exe/powershell.exe/pwsh.exe/WT.exe/regedit.exe/regedt32.exe/SystemSettings.exe/installer detection to the virtual ids
}

// Pairing
public sealed class PairingManager
{
    public PairingManager(ISecretStore secrets);
    public (string DeviceId, string Secret, string ManualCode) GetOrCreate();  // secret 32B base64url, code 6 digits, persisted
    public bool Validate(string? secret, string? code, long createdAtMs, long nowMs);
    public void Rotate();
}
```

## Pipe protocol (service <-> session agent), JSON lines over named pipe `guardpulse-laptop-agent`

Messages service -> agent:
```json
{"t":"lock","appKey":"...","appLabel":"...","reason":"manual|dailyLimit|bypass"}
{"t":"unlock"}                                   // hide lock overlay
{"t":"pinState","configured":true,"blockedUntilMs":0}
{"t":"openSetup"}
{"t":"activity","appLabel":"...","overlayState":"none"}
```
Agent -> service:
```json
{"t":"foreground","appKey":"C:\\path\\app.exe","exePath":"...","windowTitle":"..."}
{"t":"browser","browser":"c:\\...\\brave.exe","label":"Brave","activeTab":"GitHub",
 "activeUrl":"https://github.com/","tabCount":9,
 "tabs":[{"title":"GitHub","url":"https://github.com/"},{"title":"YouTube"}],
 "urlSource":"uia|session|title"}     // live tab snapshot (BrowserWatcher); url fields best-effort
{"t":"pin","digits":"123456"}                    // agent collected PIN; service verifies
{"t":"askParent","appKey":"..."}                 // create unlockRequest in Firebase
{"t":"setupClosed"}
{"t":"hello","pid":1234,"session":1}
{"t":"adminState","isAdmin":true}                // child account holds administrator rights
```
Service -> agent broadcasts (in addition to lock/unlock/pinState/openSetup/activity):
```json
{"t":"pairedState","paired":true}                // paired/unpaired transitions (tray icon)
{"t":"warningToast","title":"...","message":"..."} // one-off warnings (e.g. admin child detected)
```
Agent -> service request/reply messages (each request carries `"req":"<guid>"`; the
service replies with the same `req` and the matching `t` in the envelope):
```json
{"t":"deviceInfo","req":"..."} -> {"t":"deviceInfo","req":"...","ok":true,"deviceId":"...","secret":"...","code":"..."}
```
`deviceInfo` serves the pairing credentials (deviceId + DPAPI-held secret + manual
code) to the session agent's setup window for the QR code. All remote control flows
through the parent phone app via Firebase RTDB — there is no local HTTP dashboard
(removed in 0.2.13) and no owner-console pipe handlers.

## Config file (installer copies next to binaries): `agent-config.json`
```json
{ "apiKey": "...", "projectId": "...", "databaseUrl": "https://guardpulse-laptop-control-default-rtdb.firebaseio.com" }
```
State directory: `%ProgramData%\GuardPulse\Laptop` (create, ACL: BUILTIN\Users modify for state files).
Service name: `GuardPulseDeviceService`. Display name: `Device Service`. Session agent exe: `GuardPulse.Agent.Session.exe` (product name "Device Service").
Agent's own exe must NEVER be locked by itself (self-exemption).

## Rules for all agents
- Write ONLY files inside your assigned paths. Never run dotnet build/test. Never git commit. Do not touch other files.
- C# 12, nullable enable, file-scoped namespaces, System.Text.Json everywhere (no Newtonsoft).
- No Windows Forms in Core/Protocol (WPF only in Session).
- Logging: Microsoft.Extensions.Logging.Abstractions ILogger where a host provides it; Console.WriteLine fallback is fine.
