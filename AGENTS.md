# AGENTS.md — Coding Agent Guide for GuardPulse Laptop

> Companion to `PROJECT_CONTEXT.md`. Read that first for full context; read this for behavioral rules, command cheat-sheets, and definition-of-done.

---

## 1. IDENTITY & SCOPE

* **Product:** GuardPulse Laptop — Windows parental control (SYSTEM service + WPF session agent + Android parent app).
* **Repo root:** `D:\UVM\PROJECTS\somthing1\somthgn1\guardpulse-laptop`
* **Remote origin:** `https://github.com/rohan-js/guardpulse-laptop.git` (public, branch `main`)

### Hard rules

1. Never target `ZCode.exe` or `brave.exe` in any lock/enforcement test — use `C:\Temp\guardpulse-dummy\guardpulse-dummy-app.exe`.
3. Never push device-wide workbook locks (schedule/budget/allowlist) to the paired device without explicit consent.
4. Never commit: `firebase.local.properties`, `signing.local.properties`, `local.properties`, `e2e-session.json`, `.zcode/`, any `bin/` or `obj/` artifacts.
5. Never deploy laptop Firebase rules to a TV project or vice versa.

---

## 2. TOOLCHAIN CHEAT-SHEET

| Task | Command |
|---|---|
| dotnet build | `"C:\Users\rohan\AppData\Local\Microsoft\dotnet\dotnet.exe" build windows/GuardPulse.Laptop.sln` |
| dotnet test | `"C:\Users\rohan\AppData\Local\Microsoft\dotnet\dotnet.exe" test windows/GuardPulse.Laptop.sln` |
| dotnet publish (Service) | `dotnet publish windows/src/GuardPulse.Agent.Service -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o windows/publish/service` |
| dotnet publish (Session) | same with `-o windows/publish/session` |
| Android build | `cd <repo-root> && ./gradlew.bat :parent:assembleDebug` |
| Android tests | `./gradlew.bat :parent:test :shared:test` |
| Rules tests | `npm --prefix firebase run test:rules` (uses pinned firebase-tools@12, JDK 17 compatible) |
| Service restart | elevated PS: `Restart-Service GuardPulseDeviceService -Force` |
| Service query | elevated PS: `sc.exe query GuardPulseDeviceService` |
| Read service log | elevated PS: `Get-Content C:\ProgramData\GuardPulse\Laptop\logs\service-*.log -Tail N` |

**Path notes**

* `dotnet` is NOT on PATH. Always use absolute path `C:\Users\rohan\AppData\Local\Microsoft\dotnet\dotnet.exe`.
* JDK 17 at `C:\Program Files\Eclipse Adoptium\jdk-17.0.19.10-hotspot\`. No JDK 21 installed → never invoke bare `firebase emulators:exec`; use `npx -y firebase-tools@12 emulators:exec ...`.
* Android SDK via `local.properties`: `sdk.dir=C:/Users/rohan/AppData/Local/Android/Sdk`.

---

## 3. ELEVATION PATTERN (UAC)

The shell here runs non-elevated. For privileged ops:

1. Write a `.ps1` script to disk under `windows/installer/` or `%TEMP%`.
2. Launch it via `Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',<script>`.
3. Have the script write results to a temp file you can read afterwards.

Inline `-Command '...'` with nested quotes breaks silently. Script-file pattern is reliable. UAC policy on this machine was restored to default (`ConsentPromptBehaviorAdmin=5`) after an earlier temporary silent-elevate experiment (`=0`) — do not change it back without asking.

---

## 4. FILE EDITING RULES

* Preserve LF line endings in all source files. Several past subagent runs flipped files to CRLF causing thousands of phantom diff lines — always verify with `git diff --stat` vs `git diff -w --stat` after batch edits.
* When editing Kotlin/C# via Python heredocs, use raw strings (`r"""..."""`) or escape carefully — regex backslashes get eaten.
* After subagent edits, run `git status --porcelain` and inspect every changed file before committing.

---

## 5. ARCHITECTURE QUICK REFERENCE

Full details in `PROJECT_CONTEXT.md` §2-§3. One-paragraph version:

Windows SYSTEM service (`GuardPulseDeviceService`, display "Device Service") syncs an atomic V2 snapshot from Firebase RTDB `devices/{id}/control/v2`, persists it DPAPI-encrypted, enforces locally (NtSuspendProcess + WPF fullscreen PIN wall in the per-user session agent), writes `sync/applied` acknowledgement, and survives offline/tamper through DPAPI persistence + monotonic clocks + dead-man lockdown + fail-closed `policy-cache.json`. A single `devices/{id}` SSE fans out to `control/v2` / `sync/desired` / `commands`; `pairRequests` stays separate while unpaired. Workbook controls ride the same revision pipeline as validated optional fields.

Enforcement precedence (EnforcementEngine.Decide): self-exempt → SafeMode → schedule → budget → unlock → allowlist → bypass-default → manualBlocked → dailyLimit → none. Reasons `schedule` and `budget` trigger whole-device lockdown suspend; others are per-app.

---

## 6. CURRENT STATE (as of 2026-09-06 — 0.2.32)

* Head: **f146dfb** — Phone APK 0.2.31 (US instance). Prior **4b56257** (0.2.31) —
  site blocking made genuinely real-time (sync content-filter apply before ack,
  all-window UIA tab enforcement incl. background windows, tab-rules rebroadcast
  on session restart). Prior **db8faa6** (0.2.30) — pairing made permanent
  (one-deep rotation grace, mismatch=expired-retryable, owner re-pair rule);
  UIA tab blocking; extension machinery removed; all shipped defaults on the
  live US database `guardpulse-laptop-control`
  (`https://guardpulse-laptop-control-default-rtdb.firebaseio.com`).
  Pipeline speedups shipped (ControlDebounce 250ms to 20ms, RAM-first fast path,
  warm TCP keep-alive, optimistic parent UI). Sticky session lock + real-time
  parent messages shipped in 0.2.25. This round (uncommitted): rules ownership-transfer
  gating + legacy policy strictness parity (41/41 green, NOT deployed — deploy
  left to main agent), parent client hardening (pending-diff, unlock/safe-mode
  validation, multi-pairing, message UX), installer polling + version single-source.
* Rules deploy state: owner re-pair + messages node live per **db8faa6**/**f4acb4e**
  commit messages ("Deployed live"). The new transfer-gating rule in this working
  tree is NOT yet deployed.
* Paired device: `129b2e39670b44ebadb305a7bd91b6b9` (this machine, DESKTOP-4ILVI11). Legacy ids `52053ba0…`/`4f2dde5e…` are orphans.
* Installer output: `windows/installer/Output/DeviceServiceSetup-0.2.32.exe`
  (version single-sourced from `gradle.properties` via ISCC `/DAppVersion`).
* Phone APK: `release-apk/LAPTOP-PARENT-0.2.31-release.apk`.
* Windows tests green: 133 Protocol + 93 Core (226 total).
* Firebase rules deployed live to `guardpulse-laptop-control` (unchanged by 0.2.14 — no new keys).

---

## 6b. RECENT SHIPPED

- `4079413 Softer lock (taskbar free) + custom website block`: `customBlockedDomains` workbook, GuardCard, hosts custom bucket, rules deployed.
- `ac7c047 Revert to hostile fullscreen lock with sane exits + instant parent removal`: `Maximized+Topmost` revert, mixed Reassert/Hide, Alt+F4 CloseBlockedApp, Win handling, direct `users/{uid}/devices/{id}` delete.
- `a0be1ac Lock: minimize blocked app when wall hides (native feel)`: session-side minimize/restore via user32.
- `553030d Fix Win+D on lock screen (minimize + hide)`: SystemKey==D with Win held now minimizes+hide.
- **Multi-User & Per-User App Inventory Discovery (`InventoryScanner.cs`):** Enumerates all user profile directories from `HKLM\...\ProfileList` and `C:\Users\*`. Scans per-user Start Menus (`AppData\Roaming\...\Start Menu\Programs`), per-user Desktops, and `HKEY_USERS\<SID>\Software\Microsoft\Windows\CurrentVersion\Uninstall` so SYSTEM service captures Roblox Player/Studio (`AppData\Local\Roblox\Versions\*\*.exe`), Discord, Spotify, and per-user app installs.
- **Enterprise Browser URL-Path Blocking (`BrowserPolicyManager.cs`):** Native Windows Administrative Group Policy registry keys (`HKLM\SOFTWARE\Policies\Google\Chrome\URLBlocklist`, `Microsoft\Edge\URLBlocklist`, `BraveSoftware\Brave\URLBlocklist`, `Mozilla\Firefox\WebsiteFilter\Block`) blocking specific URL paths (e.g. `youtube.com/shorts`, `instagram.com/reels`) while keeping main domains accessible.
- **5 & 10-Minute Curfew Warning Heads-Up Toasts (`ToastWindow.xaml` / `ToastWindow.xaml.cs`):** Sleek floating dark-translucent toast at bottom-right corner (`ShowActivated = false` so it never steals focus from games or homework). `AgentHostedService.cs` 15s boundary ticker monitors remaining app/budget time and broadcasts `warningToast` pipe events when $\le 10\text{m}$ and $\le 5\text{m}$ remain.
- **Parent App UI Cleanup & Auto-Commit (`ParentSecurityFeature.kt`):** Removed redundant Allowed Hours and Content Filtering cards. Auto-commits typed URL on Save click without requiring separate `+ Add` tap.
- **Firebase Database Rules Live Deployment:** Upgraded `customBlockedDomains` regex in `database.rules.json` to allow URL path segments (`/shorts`) and deployed live to `guardpulse-laptop-control`.
- **Sub-50ms Ultra-Low Latency Real-Time Sync:**
  - `SyncEngine.cs`: Reduced `ControlDebounceMs` from 250ms $\rightarrow$ 20ms and `DesiredSettleRetryMs` from 500ms $\rightarrow$ 25ms, eliminating $>700\text{ ms}$ of artificial waiting loops.
  - `AgentHostedService.cs`: "RAM-First, Disk-Later" fast-path executing in-memory `EvaluateCurrentForeground()` and process suspension before offloading disk cache, ICACLS, hosts, and RTDB acknowledgement to non-blocking background threads.
  - `RtdbFirebaseClient.cs`: Configured .NET 8 `SocketsHttpHandler` with 15s TCP keep-alive pings and `TCP_NODELAY` to prevent router/carrier sleep latency.
  - `ParentAppsFeature.kt`: Optimistic UI switch toggle without freeze or delay.

## 7. IN-FLIGHT WORK

**Inno Setup .exe installer** — user explicitly requested ("like any other installer"). Plan approved:

* Tool: Inno Setup 6 **installed** at `C:\Program Files (x86)\Inno Setup 6\ISCC.exe` (6.3.3, verified 2026-08-24). `winget install -e --id JR.InnoSetup` was the install path.
* Authored `windows/installer/installer.iss` + `windows/installer/build-installer.ps1` + `docs/assets/guardpulse-logo.ico` (currently **untracked** - `git status` shows those three). Fixes since draft: `AppId` GUID, `SetupIconFile`, `[UninstallRun]` flag, `FirebasePage` type, `Format()` calls, `[Files]` template bundle, ACL LockLedgerPattern loop, session copy -Recurse. See PROJECT_CONTEXT.md for full dump.\n* `installer.iss` replicates `install.ps1` exactly:
  * `[Setup]` AppName=Device Service (stealth naming preserved), AppVersion=0.2.0, DefaultDirName={autopf}\Device Service, PrivilegesRequired=admin (+OverridesAllowed=dialog), OutputBaseFilename=GuardPulseLaptopSetup-0.2.0, SetupIconFile=<ico>.
  * `[Files]` bundle merged `windows/installer/publish/*` (297 files) → `{app}`, recursesubdirs.
  * `[Dirs]` `{commonappdata}\GuardPulse\Laptop\logs`.
  * `[Registry]` SafeBoot Minimal+Network keys `(default)="Service"` flags uninsdeletekey; HKLM Run key `DeviceServiceAgent="{app}\GuardPulse.Agent.Session.exe"` flags uninsdeletevalue.
  * `[Code]` wizard pages collecting ApiKey*/ProjectId*/DatabaseUrl; `CurStepChanged(ssInstall)` pre-copy stop/delete service + taskkill agents; post-copy GenerateAgentConfig (template token replace), icacls sequence (ONE FILE PER CALL — multi-file fails err87), sc create/description/failure/sdset, StartService. Abort on nonzero.
  * `[UninstallRun]` sc stop/delete, delete Run key + SafeBoot keys; optional RemoveData prompt deletes `{commonappdata}\GuardPulse`.
* `windows/installer/build-installer.ps1`: wires absolute dotnet (NOT on PATH), publishes both exes ReadyToRun into `windows/publish/{service,session}`, merges into `windows/installer/publish/` (+ `content-blocklists/`), generates ICO via System.Drawing BinaryWriter, then invokes `ISCC.exe` to produce `windows/installer/Output/GuardPulseLaptopSetup-0.2.0.exe`. Invoke as `powershell -ExecutionPolicy Bypass -File windows/installer/build-installer.ps1` from repo root.
* Output: `windows/installer/Output/GuardPulseLaptopSetup-0.2.0.exe` (~150 MB expected).
* Shipped: `2fb5f4c` compiled ISCC 6.3.3 (49s) -> `windows/installer/Output/GuardPulseLaptopSetup-0.2.0.exe` (50 MB, 298 payload files), `PrivilegesRequired=admin` forces UAC, wizard collects ApiKey*/ProjectId/DatabaseUrl, `GenerateAgentConfig` uses `AnsiString` bridge for `LoadStringFromFile`, `LockLedgerPattern` loops `usage-*/offsets-*/blocks-*.json`, `dotnet 190/190` + Kotlin green. Remaining: smoke UAC/wizard/ACLs/SafeBoot on a clean VM + uninstall round-trip (manual). `git push origin main` done.
* Add LICENSE.md before public release.

**Deferred destructive tests** (need explicit per-test consent): agent-kill lockdown depth while lock held, service-kill cache-only lock post-re-pair, simulated clock rollback → clockTampered, 1-min budget lock, hosts filter insert/remove round-trip, allowlist notApproved on dummy exe, virtual-desktop reassert visual, tray hide-on-pair visual. Use sacrificial dummy exe only; never ZCode/Brave.

---

## 8. TESTING CHECKLIST BEFORE ANY COMMIT

* [ ] `dotnet build windows/GuardPulse.Laptop.sln` — 0 errors
* [ ] `dotnet test windows/GuardPulse.Laptop.sln` — 261+ passing (140 Protocol + 121+ Core)
* [ ] `./gradlew.bat :parent:assembleDebug` — BUILD SUCCESSFUL
* [ ] `./gradlew.bat :parent:test :shared:test` — green
* [ ] `git diff --stat` ≈ `git diff -w --stat` (no CRLF explosion)
* [ ] `git status --porcelain` contains no `bin/` or `obj/` entries
* [ ] If service-affecting: restart service and confirm `Get-Service GuardPulseDeviceService` = Running + heartbeat `online:true` within 60s
* [ ] If rules changed: `npm --prefix firebase run test:rules` green before `firebase deploy --only database`

---

## 9. COMMIT MESSAGE STYLE

Short imperative subject line; body bullets describing what changed and why; mention verification done. Examples from history:

```
Harden Windows agent: pipe verification, dead-man failsafes, tamper telemetry, state ACLs
Single stream + Security workbook + notifications and digest
Branding: distinct laptop + TV logos from Stitch brand identity
Docs: laptop README mirrors guardpulse-laptop layout
```

Tag releases as `v0.2.0` etc. matching parent `versionName`.

---

## 10. KNOWN PITFALLS

See `PROJECT_CONTEXT.md` §5 for full list. Top three that bite repeatedly:

1. Multi-file `icacls` invocations fail with error 87 — always loop per file.
2. Subagent file edits can flip LF↔CRLF producing massive phantom diffs — always re-check with `git diff -w --stat`.
3. `dotnet` is not on system PATH — absolute path required in every invocation.

---

## 11. CURRENT STATE (2026-09-11 — supersedes §6)

* Head **`6fcb9c6`** (0.2.39: custom-sites persistent block toggle + remove), pushed. Installer: `windows/installer/Output/DeviceServiceSetup-0.2.38.exe` (folds in 0.2.37's uninstaller .msg fix + net start retry — 0.2.39 changed phone/rules only, no agent). Phone APK: `release-apk/LAPTOP-PARENT-0.2.39-release.apk`. Rules: `customSites` registry node deployed live to SG.
* **Both laptops are live-stalled** (confirmed in SG DB 09-11): heartbeats/state/messages flowing, `sync/applied` frozen hours behind `sync/desired` — SSE keep-alives mask a dead event-delivery. The 0.2.38 reconcile loop fixes it; **reinstall on both machines pending** (kid laptop `0d3fc12f…` runs 0.2.36; dev machine `232a64da…` runs 0.2.35 and its service is RUNNING again — user re-enabled it 09-11 for testing).
* LIVE Firebase = **Singapore** instance (runbook in §12). US instance is legacy.
* The two-tier phone dark-alarm ships in this repo's parent app; the parent's phone APK still needs a manual update to 0.2.39 (honest sync status + truthful chips + custom-sites toggles).
* Tests green at ship: dotnet 278 (140 Protocol + 138 Core incl. InstallerScriptTests + SyncEngineReconcileTests), rules 47 (`npm --prefix firebase run test:rules`).

## 12. FIREBASE RUNBOOK — SG LIVE (2026-09-10)

* Project `guardpulse-laptop-sg`, instance `guardpulse-laptop-sg-default-rtdb`. US `guardpulse-laptop-control` is legacy — do not read/write it for current state.
* firebase-tools **15.15** on PATH at `C:\Users\rohan\AppData\Roaming\npm\firebase`; auth via `~/.config/configstore/firebase-tools.json`.
* Read: `MSYS_NO_PATHCONV=1 firebase database:get "/devices/<id>/heartbeat" --project guardpulse-laptop-sg --instance guardpulse-laptop-sg-default-rtdb`
* **`database:patch` was removed in v15.** Admin writes: REST PATCH against `https://guardpulse-laptop-sg-default-rtdb.asia-southeast1.firebasedatabase.app/<path>.json?access_token=<token from firebase auth:print-access-token>`.
* App keys in `control/v2/apps` and `state/apps` are **base64url(full exe path)** — decode before comparing with inventory.
* Remote policy edit recipe (atomic, one PATCH at `/devices/{id}`): bump `control/v2/revisionId` (fresh push-style key) + `updatedAt` + `updatedBy`, set `control/v2/apps/<key>/manualBlocked`, and rewrite `sync/desired` with the same revisionId/kind/target/requestedAt/requestedBy. Agent applies in ~2 s and acks. Never edit without the parent's explicit ask.
* Devices: `0d3fc12f…` kid laptop (live), `232a64da…` dev machine (paired, offline, service disabled), `f3834d1d…` old unpaired.

## 13. 0.2.37/0.2.38 BACKLOG (ordered)

1. ~~P0 — HideUninstaller `.msg` fix~~ **DONE in `f2e675f`** (shipped in the 0.2.38 installer).
2. ~~`ssPostInstall` `net start` retry~~ **DONE in `f2e675f`** (shipped in the 0.2.38 installer).
3. ~~Phone UX: dead-path app rows~~ **DONE in `63c2808`** (stale .lnk targets no longer create rows) together with the real Chrome-row bug: apps whose desired rule the laptop never applied now read "Waiting for laptop", not "App allowed".
3b. ~~Custom-sites per-site toggle + remove~~ **DONE in `6fcb9c6`** (0.2.39): card now renders one row per site (block Switch + x-remove, immediate commits, Clear-all confirm-gated). Enforcement shape frozen — new phone-owned `devices/{id}/customSites` registry node rides the same atomic controlUpdate; rules deployed live to SG; **needs only the new phone APK**, no laptop changes.
4. **P0 — Reinstall 0.2.38 on BOTH laptops** (kid `0d3fc12f…` + dev `232a64da…`): both are live-stalled (SSE event delivery dead behind keep-alives; `sync/applied` frozen hours behind `sync/desired`). After install, the work-first reconcile loop applies the pending Chrome/Brave blocks within seconds and keeps healing any future stall; verify `sync/applied.revisionId == desired` + `meta.appVersion=1.0.0+63c2808`.
5. Phone APK update: parent's phone needs `LAPTOP-PARENT-0.2.38-release.apk` for honest sync status + truthful chips.
6. Ops (user action): switch the brother's Windows account to Standard — `childAccountIsAdmin` still firing daily; he killed the agent twice on 09-09/09-10.

## 14. MORE PITFALLS (2026-09-10)

1. firebase-tools v15 removed `database:patch`; `database:get` needs `--instance` for the regional SG instance.
2. Git Bash strips Windows-tool flags (`/F`, `/v`, `/tn`) — prefix `MSYS_NO_PATHCONV=1`; `taskkill //F` double-slash form still fails, use the fallback.
3. Inno 6.3.3 uninstaller = exe + dat + **msg** sidecar; renaming an uninstaller must move all three or it dies at startup.
4. An offline session agent can full-screen-wall the logged-on user from a stale policy-cache (fail-closed by design). On a dev box, disable the service instead of leaving a half-alive install.
5. `sc query` on the hardened service DACL returns Access denied from a non-elevated shell — read `HKLM\SYSTEM\CurrentControlSet\Services\GuardPulseDeviceService` registry values instead (ImagePath/Start are world-readable).
