# GuardPulse Reliability Architecture

This document describes the synchronization, enforcement, usage, security, and release invariants for GuardPulse `0.2.0`.

## Core Invariants

1. The parent never presents a requested control value as TV-confirmed until the TV acknowledges the exact revision.
2. The TV never weakens its last valid local protection because Firebase is offline, a listener is cancelled, or a newer control is malformed.
3. Accessibility is the only component that opens an app lock wall.
4. A lock is based only on a fresh foreground observation. Persisted foreground state cannot open a wall after Home, reboot, or process recovery.
5. Older asynchronous work cannot overwrite a newer control revision, acknowledgement, state, or diagnostic.
6. Pairing cannot replace an existing owner. Ownership must be removed before another parent can claim the TV.
7. Release APKs must contain live Firebase configuration, use the existing signing certificate, and be deployed with replace-only installation.

## Desired And Confirmed State

The parent writes a complete desired snapshot to:

```text
/devices/{deviceId}/control/v2
```

The snapshot includes:

- `schemaVersion`
- `revisionId`
- `updatedAt`
- `updatedBy`
- app policies and daily limits
- One-Tap Modes and active mode
- Safe Mode
- Live TV, whole Settings, and protected Settings-section policies
- versioned PIN metadata

The matching request is written to:

```text
/devices/{deviceId}/sync/desired
```

The TV validates the complete snapshot, saves it locally, applies it to the current fresh foreground package, writes resulting runtime state, and then acknowledges:

```text
/devices/{deviceId}/sync/applied
```

An acknowledgement is valid only when its `revisionId` matches both the current desired revision and the validated control revision. It also carries the TV `sessionId`, status, application time, and an optional bounded error.

The parent retains two distinct models:

- **Desired state:** the newest requested operation.
- **Confirmed state:** the last snapshot acknowledged by the TV.

Pending, offline, rejected, malformed, and timed-out changes never overwrite the confirmed UI model.

## Control Validation

V2 is classified as `Missing`, `Valid`, or `Invalid`.

- `Missing` allows the compatibility migration to seed V2 from complete legacy data.
- `Valid` can be submitted and acknowledged.
- `Invalid` disables parent mutations until the explicit repair action reconstructs a coherent snapshot.

Validation rejects:

- unsupported schema versions
- absent required booleans
- limits outside the accepted range
- package names that do not match their encoded keys
- malformed PIN records
- an active mode that does not exist
- unsupported lock, command, unlock, or acknowledgement values

After the TV accepts its first valid V2 snapshot, it does not fall back to potentially stale legacy policy.

## TV Synchronization Actor

`TvSyncService` owns Android service lifecycle and the foreground notification. `TvSyncEngine` owns ordered synchronization.

Firebase callbacks, connection changes, policy snapshots, foreground changes, usage updates, commands, inventory, health, heartbeat, acknowledgements, and retries enter one `Channel<TvSyncEvent>`. Work that produces Firebase writes is awaited before the actor advances.

The engine:

- coalesces state, usage, inventory, and health writes by channel
- retains only the newest complete control revision
- tracks revision generations so older completions become no-ops
- persists processed command IDs to prevent replay
- recreates cancelled listeners with capped exponential backoff
- generates a new session on reconnect
- flushes dirty channels immediately after reconnect
- records channel success/failure timestamps in `/sync/runtime`

The encrypted local store retains the last valid V2 snapshot, applied revision, PIN, usage ledger, processed commands, and required recovery metadata across process restarts.

## Parent Synchronization

`ParentSyncViewModel` owns authentication, selected-device restoration, listener lifecycle, reconnect, desired operations, and immutable UI state. Feature screens do not create Firebase listeners.

Typed control operations replace closure-based mutation queues. A queued operation is either:

- applied to a valid base snapshot
- retained while prerequisites are unavailable
- exposed as failed with a reason

It is never silently dropped.

The parent derives these statuses:

| Status | Meaning |
| :--- | :--- |
| `Sending` | The parent is committing the atomic desired update. |
| `Waiting for TV` | Firebase accepted it but the matching TV acknowledgement has not arrived. |
| `Applied` | The TV acknowledged the exact revision. |
| `Delayed` | TV heartbeat or acknowledgement is late but not yet offline. |
| `Offline - pending` | The desired operation is retained while the TV is offline. |
| `Failed` | Firebase, validation, or TV application reported a bounded failure. |
| `TV update required` | The TV does not report protocol V2. |

The one-second usage clock exists only inside visible usage components. It does not replace the complete parent UI state every second.

## Foreground Lock Enforcement

Accessibility supplies:

- observed package
- mapped policy package
- observation timestamp
- boot identity

Observations older than three seconds, from a previous boot, or representing launcher, Home, SystemUI, GuardPulse overlays, or ignored system surfaces cannot open a wall.

`TvSyncService` may recompute and publish state, but it cannot launch `LockActivity`. This prevents cached foreground data from reopening a previous app after Home or after a different locked app is opened.

The lock wall dismisses only when:

- a newly validated local V2 snapshot allows the target
- a valid one-visit or timed approval is consumed
- a correct PIN grants the applicable unlock
- Safe Mode suppresses the lock

It does not listen directly to legacy policy paths.

## Usage Accounting

GuardPulse maintains a persistent daily ledger in milliseconds.

A foreground session is finalized on:

- package change
- Home or launcher
- SystemUI
- GuardPulse setup or lock overlay
- Accessibility shutdown
- stale observation
- midnight

Effective usage is the monotonic maximum of:

1. `UsageStatsManager` usage,
2. committed observed ledger usage,
3. committed usage plus a fresh active session.

Reset Today applies a millisecond offset after combining these sources. Values are clamped to non-negative numbers. Daily-limit enforcement runs locally with millisecond precision and does not wait for Firebase.

The TV uploads the foreground app immediately on change, every ten seconds while active, and performs a full reconciliation every sixty seconds. Parent extrapolation stops after 20 seconds without a fresh TV update and freezes when the TV is delayed or offline.

## Pairing And Ownership

An owned TV rejects new pairing requests. Ownership transfer requires:

1. the current parent to unpair, or
2. the gated TV setup recovery flow to clear pairing after PIN confirmation and two explicit confirmations.

Claiming an unowned TV is transactional. The parent then creates its user-device entry and accepts the request. Each stage is retry-safe.

Pairing code and secret rotate after:

- successful pairing
- unpairing
- request expiry
- emergency recovery reset

Unpairing preserves the TV’s local control snapshot and PIN so losing cloud ownership does not remove local protection.

## PIN Security

New PIN records use:

- algorithm `PBKDF2-HMAC-SHA256`
- 210,000 iterations
- random 16-byte salt
- 32-byte hash
- explicit hash version and algorithm fields

Legacy SHA records remain verifiable for compatibility. The parent reports that a security upgrade is required until a new PIN is set.

Sensitive local PIN and pairing values use Android Keystore-backed AES-GCM. Failed-attempt state persists across process restarts. Delays progress from one second through five, 15, and 30 seconds, then double up to five minutes. A correct PIN clears the counter. Threshold events produce throttled tamper reports.

## Firebase Roles And Retention

Database rules enforce:

- parent-only desired control and approval writes
- TV-only state, acknowledgement, runtime, inventory, health, and tamper writes
- immutable command, unlock, pair-request, package, reason, and creation fields
- field whitelists for every mutable contract
- encoded package-key identity
- owner transition restrictions
- acknowledgement revision/session identity

Queries are bounded to the newest:

- 20 commands
- 30 unlock requests
- 50 tamper events

Terminal commands, unlocks, and pair requests are removed after seven days. Tamper events are removed after 30 days and capped at 200 records.

## Compatibility Rollout

This release dual-writes legacy control paths while V2 becomes authoritative.

1. Deploy backward-compatible rules.
2. Install the signed TV release with `adb install -r`.
3. Install the matching parent release.
4. Confirm protocol V2, a valid control snapshot, and a matching applied acknowledgement.
5. Exercise app locks, Settings locks, modes, Safe Mode, PIN, usage, commands, and unlock approval.
6. Deploy final strict ownership and validation rules.
7. Retain legacy dual writes for this compatibility release.

An older TV continues reading legacy paths. A new TV uses legacy data only until it accepts its first valid V2 snapshot.

## Private Release Inputs

Live values belong only in ignored root files.

`firebase.local.properties`:

```properties
firebase.apiKey=YOUR_FIREBASE_API_KEY
firebase.projectId=your-firebase-project-id
firebase.databaseUrl=https://your-firebase-project-id-default-rtdb.firebaseio.com
parent.appId=YOUR_PARENT_FIREBASE_APP_ID
tv.appId=YOUR_TV_FIREBASE_APP_ID
```

`signing.local.properties`:

```properties
storeFile=C:/absolute/path/to/private.keystore
storePassword=LOCAL_ONLY
keyAlias=LOCAL_ONLY
keyPassword=LOCAL_ONLY
storeType=JKS
expectedSha256=EXPECTED_CERTIFICATE_SHA256
```

Release tasks fail when inputs are absent or the certificate SHA-256 differs from the expected installed identity.

## Verification Matrix

Before deployment:

```powershell
.\gradlew.bat --no-daemon --console=plain test
.\gradlew.bat --no-daemon --console=plain :tv:assembleDebug :parent:assembleDebug
.\gradlew.bat --no-daemon --console=plain :tv:assembleRelease :parent:assembleRelease
.\gradlew.bat --no-daemon --console=plain lint
firebase emulators:exec --project guardpulse-parental-control-test --only database "npm --prefix firebase test"
```

After deployment, verify the release version and certificate, V2 protocol, applied revision, Accessibility, Usage Access, hidden launcher behavior, offline recovery, and replace-only data preservation.
## Laptop Agent Hardening And Optimization (0.2.0)

The Windows agent inherits the same desired/confirmed and fail-closed invariants as the TV.

Additional hardening:

- **Pipe verification:** the named-pipe host accepts only `GuardPulse.Agent.Session.exe` (verified via `GetNamedPipeClientProcessId` + `QueryFullProcessImageName`); fake foreground/PIN messages are dropped before parsing.
- **Dead-man lockdown:** if no verified session agent is connected for >10s while a lock is active, the service suspends all non-essential interactive processes and emits `agentMissing` (resumes on reconnect).
- **Agent-side cache:** `policy-cache.json` (blocked apps + Safe Mode, never the PIN) is refreshed on every applied snapshot and on state upload; the session agent enforces it when the service pipe is dead.
- **Clock guard:** `UsageLedger` maxes wall-clock vs monotonic deltas; a >60s backward jump emits `clockTampered` and ledger time never runs backwards.
- **Reassert:** `LockWindow.Reassert()` restores topmost on foreign foreground reports.
- **Admin check:** session startup reports elevation type (`Full`/`Limited`); the service warns once per day with `childAccountIsAdmin`.
- **State ACLs:** `%ProgramData%\GuardPulse\Laptop` leaves Users with directory read only; `secrets.bin`, `enforcement-state.json`, `usage-*.json` are SYSTEM/Admin-only.
- **Safe Boot:** the service is registered under `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal` + `Network`.

Optimization:

- One device-root SSE (`devices/{id}`) fans out to `control/v2`, `sync/desired`, and `commands`; `pairRequests` remains separate (only while unpaired). Polling fallbacks for `commands`/`unlockRequests`/`pairing` survive alongside the streams (used when a stream stalls); a 15s boundary tick re-evaluates time-based decisions (schedule/budget/safe-mode expiries) and drives those polls.
- Heartbeat 30s -> 60s, activity flush 10s -> 20s, `ForegroundHook` 1s -> 3s (WinEvent hook stays instant), working-set trim after wiring, file log `Warning` by default (`agent-config.json` `logLevel` overrides).
- Publish is ReadyToRun folders (no single-file extraction) with `PublishReadyToRun=true`; `installer/README.md` documents it.

Workbook controls (all added as optional validated fields under `control/v2`):

- `schedule {enabled, startMinute, endMinute}` (0..1439, wraps past midnight; outside -> `schedule` lock + lockdown suspend)
- `budget {dailyLimitMinutes}` (whole-device 1..1440)
- `contentFilter {social,gambling,adult,gaming}` -> marked hosts-file block `# BEGIN/END GUARDPULSE`
- `allowlist {enabled}` -> only inventoried/Windows-dir apps may run (`notApproved`)

Rules extend `control/v2`, `sync/desired` kinds, `heartbeat` + `security/runtime` with `scheduleActive`/`budgetMinutesToday`/`contentFilterActive`/`allowlistActive`; emulator tests cover each.
Parent repository adds `updateSchedule`/`updateBudget`/`updateContentFilter`/`updateAllowlist` via `controlUpdate`; Security tab adds 4 workbook cards; Activity tab gains a weekly digest; local notifications fire for unlock/tamper/offline (>5min) via `ParentNotifications.kt`.

