# GuardPulse Laptop — Windows Parental Control

<p align="center">
  <img src="docs/assets/guardpulse-logo.png" alt="GuardPulse Logo" width="400" />
</p>

<p align="center">
  <b>GuardPulse Laptop</b> is a Firebase-backed parental-control system for Windows laptops, with a parent phone dashboard, discreet SYSTEM service enforcement, foreground PIN wall, daily limits, and workbook controls.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-0078D6?logo=windows&logoColor=white" alt="Windows" />
  <img src="https://img.shields.io/badge/.NET%208-512BD4?logo=dotnet&logoColor=white" alt=".NET 8" />
  <img src="https://img.shields.io/badge/Kotlin-7F52FF?logo=kotlin&logoColor=white" alt="Kotlin" />
  <img src="https://img.shields.io/badge/Firebase-FFCA28?logo=firebase&logoColor=black" alt="Firebase" />
  <img src="https://img.shields.io/badge/Realtime%20Database-FFCA28?logo=firebase&logoColor=black" alt="Firebase Realtime Database" />
  <img src="https://img.shields.io/badge/Gradle-02303A?logo=gradle&logoColor=white" alt="Gradle" />
  <img src="https://img.shields.io/badge/Public%20Template-Safe%20Config-2B7A77" alt="Public template with safe config" />
</p>

---

## Quick Navigation

- [Introduction](#introduction)
- [Key Features](#key-features)
- [How It Works](#how-it-works)
- [Architecture](#architecture)
- [Reliability Model](#reliability-model)
- [Modules](#modules)
- [Firebase Setup](#firebase-setup)
- [Build Guide](#build-guide)
- [Private Release / Publish](#private-release-publish)
- [Parent App Setup](#parent-app-setup)
- [Windows Agent Install](#windows-agent-install)
- [Hardening: Safe Boot + SYSTEM ACLs](#hardening-safe-boot-system-acls)
- [Remote Unlock + PIN Wall](#remote-unlock-pin-wall)
- [Firebase Paths](#firebase-paths)
- [Security Limits](#security-limits)
- [Project Info](#project-info)

---

## Introduction

**GuardPulse Laptop** is designed for Windows environments where ordinary app blocking is not enough. Instead of relying on network blocking, the laptop agent watches the foreground app through a session hook and immediately covers blocked apps with a full-screen parent PIN wall.

**GuardPulse Laptop** is a standalone, fully separated parental-control system for a Windows laptop: a parent phone app (Android) controls a discreet Windows agent that blocks apps behind a full-screen parent-PIN wall, enforces per-app daily time limits, reports activity history, and alerts on tamper attempts — synchronized through a dedicated Firebase Realtime Database project.

This product is independent from the GuardPulse Android TV system (separate repository clone at `guardpulse-laptop`, separate Firebase project `guardpulse-laptop-control`, separate parent app `com.guardpulse.laptop.parent`). Nothing is shared except versioned contracts in `shared/`.

> This public repository intentionally uses placeholder Firebase values. Do not commit live Firebase project IDs, API keys, device IDs, APKs, local backups, or operational notes.

---

## Key Features

| Feature | Description |
| :--- | :--- |
| **Foreground PIN Wall** | Blocked Windows apps are suspended via `NtSuspendProcess` and immediately covered by a WPF full-screen PIN screen (`LockWindow` + `Reassert` on foreign foreground). |
| **Acknowledged Parent Dashboard** | Controls remain pending until the laptop validates, stores (DPAPI), enforces, and acknowledges the matching V2 revision. Parent tabs: Devices, Activity, Apps, Security, Events. |
| **One-Visit Unlocks** | Correct PIN or parent approval unlocks the current app visit only; leaving the app clears the unlock. |
| **Timed Parent Approvals** | Parent approvals can grant one visit, 15 minutes, or 30 minutes. |
| **Dangerous Tools Default-Locked** | Task Manager, cmd/PowerShell/Windows Terminal, Registry Editor, Windows Settings, and installers appear as always-present rows, default-locked, parent-toggleable. |
| **Daily Limits** | Usage Access tracks app time with millisecond precision and turns reached limits into PIN-wall locks. `Reset today` is available per app. |
| **Workbook Controls** | Schedule (allowed-hours window `0..1439` with midnight-wrap, outside → `schedule` lock + lockdown suspend), whole-device Daily Budget (`1..1440`), Allowlist (`notApproved` — only inventoried + `Windows\` system apps run), and Hosts-file Content Filtering (`# BEGIN/END GUARDPULSE` block for `social`, `gambling`, `adult`, `gaming` from `content-blocklists/*.txt`). Configured from the Security tab. |
| **One-Tap Modes and Safe Mode** | Named policy sets and a confirmed, time-bounded emergency pause (`Enabled` + `Until`) are synchronized through the same control revision. |
| **Tamper Alerts** | Firebase tamper feed reports `childAccountIsAdmin` (admin elevation), `agentMissing` (10s dead-man), `clockTampered` (>60s rollback), and protection health. |
| **Hidden Tray** | Laptop tray icon hides when paired (`pairedState`) and returns on unpair; setup remains reachable via parent `openSetup` command. |
| **Safe Boot Resilience** | Service registers under `SafeBoot\Minimal` + `Network` so protection survives Safe Mode reboots. |
| **Weekly Digest** | Activity tab shows a Monday-to-now "This week" card with total time, top 3 apps, and tamper count computed from `activity/history`. |

---

## How It Works

GuardPulse Laptop uses Firebase as the coordination layer between the parent phone and the Windows agent.

1. **Pair laptop** from the parent app using a QR payload (`deviceId` + 6-digit code) or manual device ID/code shown in the agent's setup window (`%ProgramData%\GuardPulse\Laptop\device.json`).
2. **Parent app writes one atomic V2 control snapshot** (`devices/{id}/control/v2`) with a unique revision and mirrors legacy paths for compatibility.
3. **Windows service validates and encrypts the snapshot locally** (DPAPI `snapshot.v2` via `DpapiSecretStore`), then enforces it against the latest fresh foreground observation. A single `devices/{id}` SSE fans out to `control/v2`, `sync/desired`, and `commands`.
4. **Session agent reports the foreground app** via WinEvent hook + `GetForegroundWindow` fallback; the service decides via `EnforcementEngine` precedence (Safe Mode → schedule → budget → one-visit unlock → allowlist → bypass/manual → daily limit).
5. **Service writes an applied acknowledgement** (`devices/{id}/sync/applied` with `revisionId` + `sessionId` + `appliedAt`) only after `policy-cache.json` and durable per-app state have been committed. Until then, the parent keeps showing the last confirmed value.
6. **PIN or parent approval grants one visit or a bounded timed unlock** (`15`/`30` min), then returns to the underlying target app. Leaving the app clears one-visit grants.

### Flow Summary

```text
Parent App (Security workbook)
   |
   | control/v2 revision, commands, unlock approvals
   v
Firebase Realtime Database (guardpulse-laptop-control)
   |
   | applied acknowledgement, state, usage, health, inventory
   v
Windows Agent Service (SYSTEM)
   |
   | Named pipe guardpulse-laptop-agent (verified)
   v
Windows Session Agent (WPF tray "Device Service")
   |
   | Foreground hook + NtSuspendProcess
   v
PIN Wall (LockWindow) / Lockdown suspend / Hosts-file block
```

---

## Architecture

| Component | Role |
| :--- | :--- |
| **Parent App** | Signs in parents, pairs laptops, controls app locks, sets daily limits, configures workbook controls (schedule/budget/content filter/allowlist), manages One-Tap Modes and Safe Mode, approves unlock requests, and shows tamper and activity. |
| **Windows Agent Service** | Runs as `GuardPulseDeviceService` (`Device Service`) under `LocalSystem` — Firebase V2 sync (`SyncEngine`), enforcement (`EnforcementEngine` + `ProcessSuspender`), watchdog, and `HostsFileRewriter`. |
| **Windows Session Agent** | Runs as WPF tray `GuardPulse.Agent.Session` ("Device Service") — lock wall UI (`LockWindow`), setup QR, foreground hook (`ForegroundHook`), and pipe client. |
| **Core Logic** | `windows/src/GuardPulse.Agent.Core` — Firebase REST/SSE client (`RtdbFirebaseClient`), sync engine, usage ledger (`UsageLedger` monotonic `TickCount64`), activity log, inventory scanner, pairing manager, clock tamper guard. |
| **Protocol** | `windows/src/GuardPulse.Protocol` (`netstandard2.0`) — V2 control snapshot contracts (`ControlProtocol`), PIN hashing (`PinHasher` PBKDF2WithHmacSHA256, 210k iterations, 16-byte salt, 32-byte hash, base64url `22`/`43`), package keys (`PackageKeys` base64url), paths (`FirebasePaths`), constants (`PolicyConstants`). |
| **Shared Module** | `shared/` — Mirror of the protocol contracts in Kotlin for the parent app. |
| **Firebase Auth** | Email/password login for parents and anonymous auth for laptop devices. |
| **Realtime Database** | Stores desired control, acknowledgements, runtime state, inventory, commands, unlock requests, and tamper events under `guardpulse-laptop-control`. |

### Reliability Model

- `/control/v2` is the desired policy authority after migration.
- `/sync/desired` identifies the parent revision the laptop must apply.
- `/sync/applied` confirms the exact revision and laptop session that enforced it.
- Parent controls show `Sending`, `Waiting for laptop`, `Applied`, `Delayed`, `Offline - pending`, `Failed`, or `TV update required` (mapped to laptop).
- The laptop keeps its last valid encrypted snapshot and PIN while Firebase is unavailable or a new snapshot is malformed.
- Laptop callbacks, retries, commands, foreground events, and writes are serialized through one actor so old asynchronous completions cannot replace newer state.
- Usage combines the persistent foreground-session ledger with monotonic `TickCount64` and per-day millisecond reset offsets.
- Single `devices/{id}` SSE replaces the old 5-stream polling; a 60s boundary ticker survives for schedule/budget/allowlist window transitions, heartbeat `30s→60s`, flush `10s→20s`, hook `1s→3s`.

See [Reliability Architecture](docs/reliability-architecture.md) for invariants, recovery behavior, pairing security, retention, and rollout details. Laptop hardening is in `## Laptop Agent Hardening And Optimization (0.2.0)`.

---

## Modules

| Module | Description |
| :--- | :--- |
| `:parent` | Android phone controller app built with Kotlin/Compose UI (`com.guardpulse.laptop.parent`). |
| `windows/src/GuardPulse.Agent.Service` | Windows Service (`net8.0`) — `AgentHostedService` integrator, watchdog, heartbeat. |
| `windows/src/GuardPulse.Agent.Session` | WPF session agent (`net8.0-windows`) — tray, lock wall, blur, setup window. |
| `windows/src/GuardPulse.Agent.Core` | Core logic (`net8.0`) — sync, ledger, activity, inventory, hosts rewriter. |
| `windows/src/GuardPulse.Protocol` | Protocol contracts (`netstandard2.0`) — snapshot, PIN, keys, paths, constants. |
| `shared` | Shared Kotlin contracts for the parent app. |
| `firebase/` | Realtime Database rules (`database.rules.json`) and emulator tests (`database.rules.test.js`). |
| `windows/installer/` | Installer (`install.ps1`, `uninstall.ps1`, `agent-config.template.json`, `content-blocklists/`, `publish/`). |
| `scripts/` | PowerShell helpers for Android builds. |

---

## Firebase Setup

Use the Firebase Spark plan and enable:

- **Authentication**
  - Email/password for parent users
  - Anonymous auth for laptop devices
- **Realtime Database**

Public debug builds use explicit placeholder `BuildConfig` values. Real values are read only by release builds from an ignored `firebase.local.properties` file in the repository root:

```properties
firebase.apiKey=YOUR_FIREBASE_API_KEY
firebase.projectId=guardpulse-laptop-control
firebase.databaseUrl=https://guardpulse-laptop-control-default-rtdb.firebaseio.com
parent.appId=YOUR_PARENT_FIREBASE_APP_ID
```

Also replace the placeholder project in `.firebaserc` locally before deploying rules. Release builds fail rather than silently producing an APK when required Firebase values are absent. The laptop also uses `windows/installer/agent-config.template.json` (`__API_KEY__` / `__PROJECT_ID__` / `__DATABASE_URL__`) rendered to `C:\Program Files\Device Service\agent-config.json`.

Deploy database rules:

```powershell
firebase use guardpulse-laptop-control
firebase deploy --only database
```

Run rules tests:

```powershell
npm --prefix firebase install
firebase emulators:exec --only database "npm --prefix firebase test"
# or via the pinned wrapper used in CI:
npm --prefix firebase run test:rules
```

> Keep live Firebase config local. Do not commit real project IDs, API keys, service account files, device IDs, or app backups to a public repository.

---

## Build Guide

### Recommended Build

```powershell
.\scripts\build.ps1
```

### Direct Gradle Build

```powershell
.\gradlew.bat --no-daemon --console=plain :parent:assembleDebug
```

### Windows Agent (.NET 8)

```powershell
cd windows
dotnet build GuardPulse.Laptop.sln
dotnet test
dotnet publish src/GuardPulse.Agent.Service -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o publish/service
dotnet publish src/GuardPulse.Agent.Session -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o publish/session
# Merge both into windows/installer/publish for the installer (see windows/installer/README.md)
```

### Unit Tests

```powershell
.\gradlew.bat --no-daemon --console=plain test
dotnet test windows/GuardPulse.Laptop.sln
```

Generated outputs:

| App | Output |
| :--- | :--- |
| Parent phone app | `parent/build/outputs/apk/debug/parent-debug.apk` |
| Windows service | `windows/publish/service/GuardPulse.Agent.Service.exe` |
| Windows session agent | `windows/publish/session/GuardPulse.Agent.Session.exe` |
| Merged installer payload | `windows/installer/publish/` (297-file ReadyToRun, both exes + `content-blocklists/`) |

CI runs unit tests, lint, public debug assemblies, and Firebase Realtime Database emulator tests under JDK 21 (parent needs JDK 17 locally via `Eclipse Adoptium\jdk-17.0.19.10-hotspot`). It never receives live Firebase or signing credentials.

## Private Release / Publish

GuardPulse Laptop `0.2.0` uses version code `2`, R8, resource shrinking, and a private signing certificate. Create an ignored `signing.local.properties`:

```properties
storeFile=C:/absolute/path/to/private.keystore
storePassword=LOCAL_ONLY
keyAlias=LOCAL_ONLY
keyPassword=LOCAL_ONLY
storeType=JKS
expectedSha256=EXPECTED_CERTIFICATE_SHA256
```

Build the matched release:

```powershell
.\gradlew.bat --no-daemon --console=plain :parent:assembleRelease
dotnet publish windows/src/GuardPulse.Agent.Service -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o windows/installer/publish
dotnet publish windows/src/GuardPulse.Agent.Session -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o windows/installer/publish
```

The build verifies the signing certificate SHA-256 before compilation. Publishing is ReadyToRun folders (no single-file bundling) — merge both outputs into `windows/installer/publish` (installer expects `GuardPulse.Agent.Service.exe` + `GuardPulse.Agent.Session.exe` in one folder). Never publish either local properties file, the keystore, APKs, operational context, or device identifiers.

---

## Parent App Setup

1. Build the parent APK.
2. Install it on the parent Android phone.
3. Sign in with Firebase email/password authentication.
4. Pair a laptop using the QR payload or manual pairing details shown in the laptop setup window (`%ProgramData%\GuardPulse\Laptop\device.json`).
5. Set a 6-digit parent PIN in the Security tab.
6. Control app locks, daily limits, workbook controls (Allowed Hours, Daily Budget, Content Filtering, Allowlist Mode), One-Tap Modes, Safe Mode, and unlock approvals from the parent phone app.

Install helper:

```powershell
.\scripts\install-parent.ps1
```

---

## Windows Agent Install

Requires an **elevated PowerShell** (`#requires -RunAsAdministrator`). The installer is `windows/installer/install.ps1`:

```powershell
cd windows\installer
.\install.ps1 -ApiKey <web-api-key> -ProjectId guardpulse-laptop-control -DatabaseUrl https://guardpulse-laptop-control-default-rtdb.firebaseio.com
# Optional: -InstallDir "C:\Program Files\Device Service" -SourceDir "C:\path\to\publish"
```

What it does:

1. Validates `publish/GuardPulse.Agent.Service.exe` + `GuardPulse.Agent.Session.exe` exist (both must be in one folder).
2. Stops/deletes any existing `GuardPulseDeviceService` and kills running `GuardPulse.Agent.*` (file-lock handling).
3. Copies `publish/*` to `C:\Program Files\Device Service` (ReadyToRun folders, fast start, no temp extraction).
4. Renders `C:\Program Files\Device Service\agent-config.json` from `agent-config.template.json` (`__API_KEY__`/`__PROJECT_ID__`/`__DATABASE_URL__`, file log `logLevel` defaults to `warning`).
5. Creates `C:\ProgramData\GuardPulse\Laptop` + `logs`; ACLs: `Users:(RX)` on the dir only, `Users:R` on `device.json`/`policy-cache.json`, `SYSTEM`+`Administrators:(F)` on `secrets.bin`/`enforcement-state.json`/`usage-*.json`/`offsets-*.json`/`blocks-*.json`, and `logs:(OI)(CI)(F)` (locale-independent SIDs `*S-1-5-32-545` etc.).
6. Creates `sc.exe create GuardPulseDeviceService binPath= Service.exe start= auto` with `sc.exe failure reset=86400 restart/5000/restart/5000/restart/30000`, then `sc.exe sdset "D:(A;;GA;;;SY)(A;;GA;;;BA)"` (SYSTEM/Admin only).
7. Registers `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal` + `Network` `Service` keys so the service starts even in Safe Mode.
8. Starts the service and adds `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` `DeviceServiceAgent="Agent.exe"` per-user fallback at logon. Reminder: log off/reboot once so the per-user agent starts.

Uninstall (elevated):

```powershell
.\windows\installer\uninstall.ps1            # keep %ProgramData%\GuardPulse
.\windows\installer\uninstall.ps1 -RemoveData # also delete state
```

### Fallback Enforcement

| Surface | Enforcement |
| :--- | :--- |
| Normal apps | Foreground PIN wall (`LockWindow` suspend + `NtSuspendProcess`) |
| Daily limit reached | Foreground PIN wall (`dailyLimit`) |
| Schedule outside window | Whole-device PIN wall (`schedule` + lockdown suspend of non-essential processes) |
| Whole-device budget exceeded | Whole-device PIN wall (`budget` + lockdown suspend) |
| Content-filtered site | Hosts-file block (`# BEGIN/END GUARDPULSE`, `0.0.0.0` per domain from `content-blocklists/*.txt`, DNS flush) |
| Allowlist unknown app | PIN wall (`notApproved` — only inventoried + `Windows\` system apps run) |
| TV setup screen | Hidden setup opened from parent command (`openSetup`) and gated by PIN |

---

## Hardening: Safe Boot + SYSTEM ACLs

This replaces Device Owner provisioning (no Device Owner on Windows). The installer plus `AgentHostedService` harden the laptop:

| Hardening | How |
| :--- | :--- |
| **Safe Boot** | `install.ps1` writes `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\GuardPulseDeviceService` `(default)=Service` and the same under `Network`; protection survives Safe Mode reboots. |
| **Service ACL** | `sc.exe sdset "D:(A;;GA;;;SY)(A;;GA;;;BA)"` — only `SYSTEM` and `Administrators` can stop/reconfigure the service. |
| **State directory** | `%ProgramData%\GuardPulse\Laptop` is `Users:(RX)` only (no `OI`/`CI`); files created by `SYSTEM` start locked down. |
| **Per-file ACLs** | `secrets.bin`, `enforcement-state.json`, `usage-*.json`/`offsets-*.json`/`blocks-*.json` are `SYSTEM:(F)` + `Administrators:(F)` with inheritance `r`; `device.json` + `policy-cache.json` stay `Users:R` so the session agent can read them. Applied via absolute-path `C:\Windows\System32\icacls.exe` (session-0-safe) in both `SetupStateDirectory` and `install.ps1`. |
| **Pipe verification** | `AgentPipeHost` accepts only `GuardPulse.Agent.Session.exe` (`GetNamedPipeClientProcessId` + `QueryFullProcessImageName`); fake `foreground`/`pin` messages are dropped before parsing. |
| **Dead-man lockdown** | Service checks every `2s`; if no verified agent for `>10s` while a lock is active, it `SuspendAllForLockdown()` (Windows-dir + OS essentials + own exes stay alive) and emits `agentMissing`; resumes on reconnect. |
| **Agent-side cache** | `policy-cache.json` (`blockedApps` + `SafeMode`, never the PIN) is refreshed on every applied snapshot and state upload; the session agent enforces it when the pipe is dead. |
| **Clock guard** | `UsageLedger` maxes wall-clock vs monotonic `TickCount64` deltas; a `>60s` backward jump emits `clockTampered` and ledger time never runs backwards. |
| **Admin check** | Session startup reports elevation type (`Full`/`Limited`); the service warns once per day with `childAccountIsAdmin`. |
| **Reassert** | `LockWindow.Reassert()` restores `Topmost` on foreign foreground reports. |

---

## Remote Unlock + PIN Wall

The lock screen supports two unlock paths:

| Unlock Path | Result |
| :--- | :--- |
| **Correct PIN** | Grants a one-visit unlock (`OneVisitUnlocks`) and returns to the target app; leaving the app clears the unlock. |
| **Ask Parent to Unlock** | Creates an immutable request `devices/{id}/unlockRequests/{requestId}` (`status: pending`, `expiresAt = now + 10 min`); parent can approve one visit, 15 minutes, 30 minutes, or deny. Stream-driven via `unlockRequests` SSE with 60s boundary backstop. |
| **Parent unblocks app** | The parent remains pending until the laptop applies the V2 revision and writes `sync/applied` (`status: applied` + `sessionId`); only then does the visible wall dismiss. |
| **Daily limit reset** | `resetToday` command clears the day's ledger offset; the wall dismisses after the laptop processes and confirms the `resetToday` command. |
| **Content-filter unblock** | Disabling all `contentFilter` categories removes the hosts-file block and flushes DNS. |

One-visit app unlocks clear when the user leaves the unlocked app. The laptop's daily budget and per-app daily limits are separate: budget is whole-device (`budgetMinutesToday`), per-app limits are `dailyLimitMinutes` on the rule.

---

## Firebase Paths

| Path | Purpose |
| :--- | :--- |
| `/users/{uid}/devices` | Parent-visible paired laptop list. |
| `/users/{uid}/devices/{deviceId}` | Per-device parent mirror (`lastSeen`, `online`, `enforcementMode`). |
| `/devices/{deviceId}/meta` | Device identity (`ownerUid`, `tvUid`, `pairedAt`, `label`, `platform: windows`). |
| `/devices/{deviceId}/apps` | Laptop-uploaded app inventory (`packageName` + `label` + `blockable`). |
| `/devices/{deviceId}/control/v2` | Atomic desired control snapshot (`schemaVersion==2`, `revisionId` + `updatedAt`/`updatedBy`, `apps`, `modes`, `activeMode`, `safeMode`, `pin`, `schedule`, `budget`, `contentFilter`, `allowlist`). |
| `/devices/{deviceId}/sync/desired` | Latest parent-requested revision (`revisionId` + `kind` + `requestedAt`/`requestedBy` + `target`). `kind` includes `schedule`/`budget`/`contentFilter`/`allowlist`. |
| `/devices/{deviceId}/sync/applied` | Laptop acknowledgement for an exact revision and session (`revisionId` + `status: applied`/`failed` + `appliedAt` + `sessionId`). |
| `/devices/{deviceId}/sync/runtime` | Connection, protocol, channel timestamps, and last failure diagnostics. |
| `/devices/{deviceId}/heartbeat` | `online` + `lastSeen` + `enforcementMode` + `protectionHealthy` + `scheduleActive`/`budgetMinutesToday`/`contentFilterActive`/`allowlistActive`. |
| `/devices/{deviceId}/state/apps/{encodedKey}` | Runtime lock state and precise usage uploaded by laptop (`fallbackLocked`, `lockReason`, `usageMinutesToday`, `controlRevisionId`). |
| `/devices/{deviceId}/security/pin` | Salted PIN hash metadata dual-written by parent app (legacy path). |
| `/devices/{deviceId}/security/runtime` | Laptop protection health, enforcement mode, and foreground status (`scheduleActive` etc.). |
| `/devices/{deviceId}/unlockRequests/{requestId}` | Laptop-created unlock requests approved/denied by parent (stream + 60s backstop). |
| `/devices/{deviceId}/tamperEvents/{eventId}` | Protection and risky-settings tamper events (`clockTampered`, `agentMissing`, `childAccountIsAdmin`). Retained 30 days, capped at 200. |
| `/devices/{deviceId}/commands/{commandId}` | Parent-issued commands `rescanApps`/`resetToday`/`unpair`/`openSetup` (single `devices/{id}` stream, `pairRequests` separate while unpaired). |
| `/pairRequests/{deviceId}/{requestId}` | Pairing handshake (`parentUid` + `secret`/`code`, 10-min TTL, `pairing.deviceId` DPAPI). |
| `/devices/{deviceId}/activity/current` | Current foreground app pushed by the laptop. |
| `/devices/{deviceId}/activity/history/{id}` | Completed foreground sessions (30-day `ActivityRetention`). |

Package names (Windows exe paths) are stored as Firebase-safe base64url-encoded keys. Each app record also stores its original `packageName`. Commands, unlock requests, and pair requests are retained for seven days after reaching a terminal state.

---

## Security Limits

GuardPulse Laptop is designed for practical parental control on consumer Windows. It is not the same as a fully managed enterprise device.

| Risk | Notes |
| :--- | :--- |
| Service stopped by admin | `sc sdset` denies standard users; admin stop triggers watchdog + SCM restart-on-failure (`reset=86400 restart/5000`); last-gasp tamper is emitted where possible. |
| Tray killed | Watchdog (`WTSQueryUserToken` + `CreateProcessAsUserW`) respawns the session agent; dead-man lockdown suspends non-essential processes after 10s without a verified agent. |
| Clock rollback | Monotonic anchor preserves ledger time; `clockTampered` tamper is emitted and `NowMs()` never runs backwards. |
| Pipe spoof | Only `GuardPulse.Agent.Session.exe` image is accepted; `foreground`/`pin`/`askParent` from other processes are dropped before parsing. |
| Hosts-file bypass | `hosts` is `SYSTEM`-writable; child standard users cannot edit it. Empty `contentFilter` removes the block. |
| Recovery/factory reset | Cannot be prevented by a normal service. |
| Root/firmware flashing | Out of scope for app-level protection. |
| Physical access attacks | Out of scope for software-only controls. |
| Offline or malformed policy | Laptop continues enforcing the last valid encrypted local V2 snapshot (`snapshot.v2` DPAPI); the parent does not present an unacknowledged value as confirmed. |
| PIN database exposure | New PINs use PBKDF2-HMAC-SHA256 with 210,000 iterations, a random 16-byte salt, and a 32-byte hash. Legacy hashes remain verification-only until reset. |

For the strongest protection, keep the child on a standard (non-admin) Windows account. For normal home laptops, the current hardening provides a practical PIN-wall and tamper-alert layer.

---

## Project Info

| Item | Value |
| :--- | :--- |
| Project | GuardPulse Laptop — Windows Parental Control |
| Primary language | Kotlin (parent `com.guardpulse.laptop.parent`) + C# .NET 8 (agent `Device Service`) |
| Platform | Android phone (API 26+) + Windows 10/11 x64 |
| Backend | Firebase Auth + Realtime Database (`guardpulse-laptop-control`, Spark plan: anonymous device + email/password parent) |
| Current release | `0.2.0` (`versionCode 2`, tag `v0.2.0` on `rohan-js/guardpulse-laptop`) |
| Repository mode | Public template with placeholder Firebase config (`firebase.local.properties` + `windows/installer/agent-config.template.json` gitignored; production project `guardpulse-laptop-control`) |

Built for Windows parental-control workflows where app access needs to be managed from a parent phone and enforced directly on the laptop screen.

