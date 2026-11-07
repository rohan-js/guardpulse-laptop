# AGENTS.md — Coding Agent Guide for GuardPulse Laptop

> Companion to `PROJECT_CONTEXT.md`. Read that first for full context; read this for behavioral rules, command cheat-sheets, and definition-of-done.

---

## 1. IDENTITY & SCOPE

* **Product:** GuardPulse Laptop — Windows parental control (SYSTEM service + WPF session agent + Android parent app).
* **Repo root:** `D:\UVM\PROJECTS\somthing1\guardpulse-laptop`
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

## 6. CURRENT STATE (2026-09-06, 0.2.32 — HISTORICAL; §11 supersedes everything here)

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
- **0.2.37 → 0.2.43 (2026-09-10 → 2026-11-01, all shipped and pushed):**
  - `f2e675f` **0.2.37**: hidden uninstaller `.msg` sidecar fix + `ssPostInstall` net-start retry.
  - `63c2808` **0.2.38**: sync-heal — work-first reconcile loop defeats SSE keep-alive stalls (`sync/applied` no longer freezes); honest freshness on the phone; dead-path inventory rows dropped.
  - `6fcb9c6` / `8e6c868` **0.2.39 / 0.2.40**: custom-sites registry (`devices/{id}/customSites`), per-site block toggle (OFF = blocked), confirm-gated removal; rules deployed live to SG.
  - `d3ed816` → `458957b` (5 commits): **hosted web console** rebuilt to full phone parity at `https://guardpulse-laptop-sg.web.app`, then write-failure hardening (deferred-render fix, 12s auth timeouts, named busy actions, persistent error surface, one-click Write test).
  - `0adafff` **0.2.41**: persisted paired owner (`owner.v2` in secrets.bin) — no fake pairing prompt after reboot.
  - `a61d425` **0.2.42**: latency round — network-change SSE kick, batched ack+state+telemetry write, 10s state cadence, optimistic phone toggles, `pipelineLatencyMs`.
  - `db7cb7b` **security**: PII scrub, de-usernamed paths, per-platform API keys (web referrer-restricted `…1SIk0` / desktop `…0F6VA` / Android SHA-1-pinned `…bZSM`).
  - `36e9c3a` **0.2.43**: event-driven agent revival — pipe-disconnect kick + 2s watchdog scan (`AgentRevivalPlanner`); End Task on the session agent is undone in ~1s.

## 7. IN-FLIGHT WORK (2026-11-01)

Nothing large in flight — the installer shipped long ago (`2fb5f4c`) and every round since is folded into the latest artifacts. Standing items:

* **USER ACTION — reinstall `windows/installer/Output/DeviceServiceSetup-0.2.43.exe` on the DEV machine** (`232a64da…` runs 0.2.35, still live-stalled). **DONE on the kid laptop `0d3fc12f…` 06 Nov** (verified 07 Nov: 2 s watchdog cadence in the log, `protectionHealthy:true`, `pipelineLatencyMs` 4.9 s post-repair). One install folds: 0.2.37 uninstaller `.msg` fix, 0.2.38 sync-heal (fixes the live SSE-stall freezes), 0.2.41 persisted owner, 0.2.42 latency round, 0.2.43 agent revival, desktop API key. After install verify `sync/applied == sync/desired`, and no tray/pairing prompt after reboot.
* **USER ACTION — install `release-apk/LAPTOP-PARENT-0.2.42-release.apk` on the parent phone** (optimistic toggles, honest sync status, custom-sites toggles, Android-restricted key).
* **DEFERRED (agent, only after both laptops run 0.2.43):** restrict/disable the legacy shared Browser key (`c684b4bc-…`, `AIzaSyAAWg…qGp0`) — the two installed agents still authenticate with it. `gcloud services api-keys update c684b4bc-… --allowed-referrers=…` or disable outright.
* **USER ACTION:** rotate the parent password (shared in chat twice); delete the stale Cloudflare Pages project `guardpulse-console`.
* **OPS (user):** switch the brother's Windows account to Standard — see §13 item 6.
* Add LICENSE.md before any public announcement (still absent).
* No git tag since `v0.2.0` — tag `v0.2.43`+ when shipping stabilizes.

**Deferred destructive tests** (need explicit per-test consent): agent-kill lockdown depth while lock held, service-kill cache-only lock post-re-pair, simulated clock rollback → clockTampered, 1-min budget lock, hosts filter insert/remove round-trip, allowlist notApproved on dummy exe, virtual-desktop reassert visual, tray hide-on-pair visual. Use sacrificial dummy exe only; never ZCode/Brave.

---

## 8. TESTING CHECKLIST BEFORE ANY COMMIT

* [ ] `dotnet build windows/GuardPulse.Laptop.sln` — 0 errors
* [ ] `dotnet test windows/GuardPulse.Laptop.sln` — 287 passing (140 Protocol + 147 Core; keep ≥ this)
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

## 11. CURRENT STATE (2026-11-01 — supersedes §6)

* Head **`0.2.43`** (tamper round: event-driven agent revival — pipe-disconnect kick + 2s watchdog scan, so End Task on the session agent is undone in ~1-2s instead of up to 10s+; `AgentRevivalPlanner` decision core unit-tested). Installer: `windows/installer/Output/DeviceServiceSetup-0.2.43.exe` (folds 0.2.37 uninstaller fix + 0.2.38 sync-heal + 0.2.41 persisted owner + 0.2.42 latency round + this). Phone APK: `release-apk/LAPTOP-PARENT-0.2.42-release.apk` (no phone changes in 0.2.43). Rules: `customSites` + `sync/runtime.pipelineLatencyMs` deployed live to SG. Web console: `https://guardpulse-laptop-sg.web.app` (§15).
* **Both laptops were live-stalled** (confirmed in SG DB 09-11): heartbeats/state/messages flowing, `sync/applied` frozen hours behind `sync/desired` — SSE keep-alives mask a dead event-delivery. The 0.2.38 reconcile loop fixes it. Kid laptop `0d3fc12f…` now runs **0.2.43** (installed 06 Nov ~20:05 IST — its fresh boot applied the 71 h-old Roblox block, which is exactly the "applied in 255365066 ms" row the phone showed; full sync healthy). Dev machine `232a64da…` still on 0.2.35, reinstall pending.
* **Tamper saga (2026-11-07 repair):** 242 `agentKilled` events since 07 Oct; the kid-laptop repair found the install-dir ACL broken — BUILTIN\Users read/execute denied, so every agent relaunch failed 0x80070005 silently. After the on-laptop agent restored Users RX, the agent reconnected (stable from 20:09 IST; protectionHealthy true; activity/browser writes live; next policy applied in 4.9 s). Brother had jayar's admin password until 01 Nov (password rotated since); `rithi_ygx8ljx` is Standard — the post-Nov-1 kills were Task Manager on his own session, now blocked by `DisableTaskMgr=1` in his hive (verify no system-wide HKLM DisableTaskMgr remains — it would lock the parent out too). Evasion kit in his Downloads: BlueStacks/BSTweaker, ProtonVPN, `delete_chrome_policies.bat` (impotent without admin) — block `HD-Player.exe` + `ProtonVPN.exe` via the Apps tab. `meta.appVersion` stays "1.0.0" (Windows builds don't stamp it — cosmetic).
* LIVE Firebase = **Singapore** instance (runbook in §12). US instance is legacy.
* The parent's phone APK still needs a manual update to **0.2.42** (optimistic toggles, honest sync status + truthful chips, custom-sites toggles: ON = allowed / OFF = blocked, x-removal asks first, Android-restricted API key).
* Tests green at ship: dotnet 287 (140 Protocol + 147 Core incl. AgentRevivalPlannerTests), rules 48 (`npm --prefix firebase run test:rules`).
* **Hosted web console LIVE at `https://guardpulse-laptop-sg.web.app`** (runbook in §15) — full phone-parity rebuild; the old Cloudflare `guardpulse-console.pages.dev` deployment is STALE (pre-0.2.13 code + legacy US database) and can be deleted in the Cloudflare dashboard.

## 12. FIREBASE RUNBOOK — SG LIVE (2026-09-10)

* Project `guardpulse-laptop-sg`, instance `guardpulse-laptop-sg-default-rtdb`. US `guardpulse-laptop-control` is legacy — do not read/write it for current state.
* firebase-tools **15.15** on PATH at `C:\Users\rohan\AppData\Roaming\npm\firebase`; auth via `~/.config/configstore/firebase-tools.json`.
* Read: `MSYS_NO_PATHCONV=1 firebase database:get "/devices/<id>/heartbeat" --project guardpulse-laptop-sg --instance guardpulse-laptop-sg-default-rtdb`
* **`database:patch` was removed in v15.** Admin writes: REST PATCH against `https://guardpulse-laptop-sg-default-rtdb.asia-southeast1.firebasedatabase.app/<path>.json?access_token=<token from firebase auth:print-access-token>`.
* App keys in `control/v2/apps` and `state/apps` are **base64url(full exe path)** — decode before comparing with inventory.
* Remote policy edit recipe (atomic, one PATCH at `/devices/{id}`): bump `control/v2/revisionId` (fresh push-style key) + `updatedAt` + `updatedBy`, set `control/v2/apps/<key>/manualBlocked`, and rewrite `sync/desired` with the same revisionId/kind/target/requestedAt/requestedBy. Agent applies in ~2 s and acks. Never edit without the parent's explicit ask.
* Devices: `0d3fc12f…` kid laptop (live, 0.2.36, stalled — needs 0.2.43), `232a64da…` dev machine (paired, 0.2.35, service re-enabled 09-11 for testing), `f3834d1d…` old unpaired.

## 13. 0.2.37/0.2.38 BACKLOG (ordered)

1. ~~P0 — HideUninstaller `.msg` fix~~ **DONE in `f2e675f`** (shipped in the 0.2.38 installer).
2. ~~`ssPostInstall` `net start` retry~~ **DONE in `f2e675f`** (shipped in the 0.2.38 installer).
3. ~~Phone UX: dead-path app rows~~ **DONE in `63c2808`** (stale .lnk targets no longer create rows) together with the real Chrome-row bug: apps whose desired rule the laptop never applied now read "Waiting for laptop", not "App allowed".
3b. ~~Custom-sites per-site toggle + remove~~ **DONE in `6fcb9c6`** (0.2.39): card now renders one row per site (block Switch + x-remove, immediate commits, Clear-all confirm-gated). Enforcement shape frozen — new phone-owned `devices/{id}/customSites` registry node rides the same atomic controlUpdate; rules deployed live to SG; **needs only the new phone APK**, no laptop changes.
4. **P0 — Reinstall 0.2.43 on the DEV machine** (`232a64da…`, runs 0.2.35, still live-stalled). **Kid laptop DONE 06 Nov**, verified 07 Nov (0.2.43 cadence in log, agent reconnected after ACL repair, 4.9 s apply). After the dev install, verify `sync/applied.revisionId == desired` and that a reboot shows no tray/pairing window.
5. Phone APK update: parent's phone needs `LAPTOP-PARENT-0.2.42-release.apk` (optimistic toggles + honest sync + custom-sites toggles + Android-restricted key). **The parent's phone already shows 0.2.42 features in the field** (pipelineLatencyMs row seen 07 Nov) — confirm which APK is installed; ship the release APK if it was a debug build.
6. ~~Ops: switch the brother's account to Standard~~ **DONE 2026-11-07**: `rithi_ygx8ljx` is Standard; jayar (admin, parent's) password rotated 01 Nov — the brother knew it before that date; `DisableTaskMgr=1` applied to rithi's hive 07 Nov. Residual: keep jayar's password private; blocking `HD-Player.exe` (BlueStacks) + `ProtonVPN.exe` via the Apps tab is open; watch for `agentKilled` re-appearance as the early-warning signal.

## 14. MORE PITFALLS (2026-09-10)

1. firebase-tools v15 removed `database:patch`; `database:get` needs `--instance` for the regional SG instance.
2. Git Bash strips Windows-tool flags (`/F`, `/v`, `/tn`) — prefix `MSYS_NO_PATHCONV=1`; `taskkill //F` double-slash form still fails, use the fallback.
3. Inno 6.3.3 uninstaller = exe + dat + **msg** sidecar; renaming an uninstaller must move all three or it dies at startup.
4. An offline session agent can full-screen-wall the logged-on user from a stale policy-cache (fail-closed by design). On a dev box, disable the service instead of leaving a half-alive install.
5. `sc query` on the hardened service DACL returns Access denied from a non-elevated shell — read `HKLM\SYSTEM\CurrentControlSet\Services\GuardPulseDeviceService` registry values instead (ImagePath/Start are world-readable).

## 15. WEB CONSOLE — HOSTED DASHBOARD (2026-09-11)

* **URL:** `https://guardpulse-laptop-sg.web.app` (Firebase Hosting, project `guardpulse-laptop-sg`). Sign in with the parent account (same credentials as the phone app; Create-account works too).
* **Source:** single-file `hosted/index.html` (zero deps, no build — repo convention), talking to Firebase directly: Identity-Toolkit REST auth + RTDB REST, POLLING every 8s (SSE deadlocks Chrome's 6-connection cap — the old console's lesson).
* **Deploy:** `MSYS_NO_PATHCONV=1 firebase deploy --only hosting --project guardpulse-laptop-sg` (`.firebaserc` default stays legacy-US for the rules guard — always pass `--project`).
* **Tests:** `node hosted/harness.js` (43 checks: full script runs headless against a mock Firebase incl. multi-path PATCH semantics; pins deriveSyncStatus, write-pipeline bodies, legacy mirrors, custom-sites atomic write, PIN record, safeMode startedAt). `node hosted/e2e-live.js` = READ-ONLY live check (add `GP_EMAIL`/`GP_PASSWORD` env for the sign-in + device checks). Never write policies from the e2e.
* **Write pipeline (critical):** every control write is ONE multi-path PATCH at `devices/{id}` carrying `control/v2` (full stamped snapshot) + `sync/desired` (same revisionId + kind) + the legacy mirror (`policy/apps` / `policy/modes` / `policy/activeMode` / `security/safeMode` / `security/pin`). Extra paths (customSites) are RELATIVE keys — absolute keys nest one level too deep and rules reject them.
* **Phone parity shipped:** Synchronization card (7-state deriveSyncStatus + reconnect + repair-control), Protection health (security/runtime rows), Activity (now-on-laptop freshness/PIN-wall badge/session timer, weekly digest, history Today/7d/30d), Apps (Allow switch ON=allowed, daily+session limits with explicit-null clear, Waiting-for-laptop stale-runtime labels, reset-today), Custom sites (registry union, toggle OFF=blocked, ×+Clear-all confirms), Modes CRUD+activate+per-mode limits, Safe Mode (startedAt/startedBy — rules REQUIRE them when enabling), Budget (+Clear), Allowlist, Parent PIN (PBKDF2 210k), Pending unlock requests (deny/one-visit/15/30), openSetup, messages, pairing (payload/manual+code), gone-dark banners, tamper events. Schedule + content-filter UI intentionally NOT included (phone removed them; DB values untouched).
* **API-key matrix (hardened 2026-09-13, gcloud project guardpulse-laptop-sg):**
  - **Web console key** `…1SIk0` — Identity-Toolkit+securetoken only, referrers `guardpulse-laptop-sg.web.app/*` — inline in hosted/index.html. Verified: sign-in 200 from the origin, 403 without/evil referrer.
  - **Desktop agent key** `…0F6VA` — Identity-Toolkit+securetoken only, no app restriction — installer prefill (firebase-local.iss + installer.iss fallback). Future installs carry it.
  - **Android key** `…bZSM` — restricted to `com.guardpulse.laptop.parent` + release SHA-1 `35415ac0…7f01` — firebase.local.properties (0.2.42 APK carries it).
  - **Browser key (auto)** `…qGp0…` — UNRESTRICTED, still used ONLY by the two installed laptops' agent-config.json. **DEFERRED: after BOTH laptops run the 0.2.43 installer, restrict it to web.app referrers or disable it** (`gcloud services api-keys update c684b4bc-… --allowed-referrers=…`). Do NOT restrict before the reinstall — the old agents would lose sign-in.
  - build-installer.ps1's key-equality guard is now a warning (installer=desktop vs phone=android divergence is by design); the cutover invariant (URL contains project) is still enforced.
* **Old dashboard history:** both dashboards were removed 0.2.13 (`43facc7`); the Cloudflare Pages project `guardpulse-console` kept serving the removed code pointed at the DEAD US database (sign-in breaks for SG accounts; any write would no-op silently). Audit verdicts: Safe-Mode-enable rejected by rules, app-limit clear silently ineffective, ack pipeline dead (no sync/desired), custom-sites registry absent. All fixed in this rebuild.
