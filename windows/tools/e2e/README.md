# GuardPulse Laptop — E2E Harness (fake parent)

A single-file Node CLI that acts as a **fake parent** against the live Firebase
project `guardpulse-laptop-control` so the Windows agent can be validated
end-to-end **without the phone app**.

Pure REST — no Firebase SDK, **zero dependencies, no `npm install`**. Node 18+
(uses global `fetch` and `node:crypto` built-ins).

```
windows/tools/e2e/
  guardpulse-e2e.js    the CLI
  package.json         commonjs, no deps
  e2e-session.json     (generated) persisted parent idToken/refreshToken
```

## Configuration (env)

Credentials are REQUIRED via the environment — they are intentionally not
committed (this repo is public). The harness exits with a clear error if any
of `GP_API_KEY`, `GP_EMAIL` or `GP_PASSWORD` is missing.

| Variable          | Purpose                                          |
| ----------------- | ------------------------------------------------ |
| `GP_API_KEY`      | (required) Identity Toolkit API key              |
| `GP_DB_URL`       | (optional) RTDB base URL                         |
| `GP_EMAIL`        | (required) parent account email                  |
| `GP_PASSWORD`     | (required) parent account password               |
| `GP_SESSION_FILE` | (optional) where the session is kept             |

Auth is email/password over the Identity Toolkit REST API. The idToken is
reused from the session file, refreshed via `securetoken.googleapis.com` when
it expires (or on any RTDB 401 that is not a rules "Permission denied"), and
re-signed-in as a last resort. Every failed HTTP call prints **status + body**.

---

## Commands

All examples: `node guardpulse-e2e.js <command>` (run from `windows/tools/e2e`).

### `ensure-account`

Creates the fake parent account via `accounts:signUp` (tolerates
`EMAIL_EXISTS`), signs in via `signInWithPassword`, prints the `idToken`, and
persists token + refreshToken to `e2e-session.json`.

```bash
node guardpulse-e2e.js ensure-account
```

### `seed-control <deviceId> [--pin 123456] [--block <appKey>]... [--limit <appKey>=<minutes>]...`

Writes a **rules-valid** `control/v2` snapshot to `devices/{id}/control/v2`:

- `schemaVersion: 2`, fresh unique push-like `revisionId` (must differ from the
  stored one — enforced by the DB rules; the tool reads the current revision
  first and guarantees a new one),
- `updatedAt` / app `updatedAt` / pin `updatedAt` as `{".sv":"timestamp"}`,
- `apps`: each map key is `base64url(packageName)` **without padding** and the
  row carries `packageKey` (same value), `packageName`, `manualBlocked`
  (`--block` => true, `--limit` => false unless also blocked),
  `dailyLimitMinutes` (1..1440) when limited,
- `modes: {}`, and **`activeMode` omitted entirely** (never written as null),
- `safeMode: {enabled:false, until:0}` (rules require safeMode to exist with
  these invariants when disabled),
- `pin`: PBKDF2-HMAC-SHA256 v2 record identical to Kotlin `PinHasher.create` —
  16 random bytes salt as 22-char base64url, 210000 iterations of
  `"pin"` with the raw salt bytes, 32-byte key as 43-char base64url,
  `version:2, algorithm:"PBKDF2WithHmacSHA256"`.

It also **dual-writes** the same pin record to the legacy path
`devices/{id}/security/pin`, and points `devices/{id}/sync/desired` at the new
revision: `{revisionId, kind:"appPolicy", requestedAt:{".sv":"timestamp"},
requestedBy:<uid>}`.

App keys on Windows are stable lowercase exe paths (e.g.
`c:\program files\...\app.exe`) or the virtual bypass ids
(`guardpulse.windows.taskmgr`, `guardpulse.windows.commandline`,
`guardpulse.windows.registry`, `guardpulse.windows.settings`,
`guardpulse.windows.installers`). Quote Windows paths in your shell.

Requires the device to be **already paired** with this account
(`meta.ownerUid == <uid>`), otherwise the rules reject the write.

```bash
node guardpulse-e2e.js seed-control LAPTOP-DEVICE-ID \
  --pin 123456 \
  --block 'c:\program files\steam\steam.exe' \
  --block guardpulse.windows.taskmgr \
  --limit 'c:\program files\discord\discord.exe'=60
```

### `pair-accept <deviceId> [--secret <secret>] [--code <6-digit>] [--code-only]`

Creates the **pair request** that the parent phone would send, using the
secret / manual code shown on the laptop's setup screen:

```json
pairRequests/{deviceId}/{pushId}:
  { parentUid: <uid>, secret: "...", code: "123456",
    createdAt: {".sv":"timestamp"}, status: "pending" }
```

- Pass `--secret <value>` and/or `--code <value>` **from the laptop screen**
  (at least one is required; the agent validates them against its persisted
  pairing secret/code).
- `--code-only` omits the `secret` field entirely (code-only pairing).
- The tool first tries to read `devices/{id}/meta` to show pairing state; a
  permission error here is **normal before pairing** (rules only let the agent
  read an unowned device) and is tolerated.
- Rules only allow creating the request while `meta.ownerUid` does not exist —
  if the device is already paired you get 401 Permission denied.

The real agent listens on `pairRequests/{deviceId}`, validates secret/code +
TTL (10 min), then writes `meta.ownerUid = <parentUid>` — after which the
parent can read the whole device node.

```bash
node guardpulse-e2e.js pair-accept LAPTOP-DEVICE-ID --secret AbC123... --code 424242
```

### `watch <deviceId>`

Polls every 2 s (Ctrl+C to stop; press twice to force). Prints one block per
poll and marks changed sections with `*`:

- `sync/applied` — revisionId, status, sessionId, appliedAt, error
- `sync/runtime` — connected, sessionId, protocolVersion, policy receive/apply times, lastError
- `heartbeat` — online, protectionHealthy, enforcementMode, safeModeActive, lastSeen
- `state/apps` — tracked row count + the `lockBlocked` rows (with lockReason)
- `activity` — current appLabel, overlayState, appKey, updatedAt
- `tamper` — latest tamper events (uses the `createdAt` index, falls back to a full read)

```bash
node guardpulse-e2e.js watch LAPTOP-DEVICE-ID
```

### `unlock-approve <deviceId> <requestId> [--timed 15|30]`

Approves a **pending** unlock request the agent created when the child tapped
"ask parent":

```json
PATCH devices/{id}/unlockRequests/{requestId}:
  { status: "approved",
    approvalType: "oneVisit" | "timed",
    approvalDurationMs?: 900000 | 1800000,   // only with --timed
    updatedAt: {".sv":"timestamp"}, updatedBy: <uid> }
```

Default is a one-visit approval; `--timed 15` / `--timed 30` adds a 15/30
minute timed window. The rules only allow approving while status is
`pending` (the tool pre-reads the request and warns otherwise).

```bash
node guardpulse-e2e.js unlock-approve LAPTOP-DEVICE-ID -PjXyZ... --timed 15
```

### `send-command <deviceId> <rescanApps|resetToday|unpair|openSetup> [--app <appKey>]`

Posts a command for the agent to claim:

```json
POST devices/{id}/commands:
  { type, requestedBy: <uid>, createdAt: {".sv":"timestamp"}, ttlMs,
    packageName?: <appKey> }        // packageName only with --app
```

`ttlMs` mirrors `PolicyConstants.commandTtlMs` (openSetup 60 s, unpair 600 s,
others 300 s). Use `--app` with `resetToday` to reset a single app's usage
today (omit it to reset everything).

```bash
node guardpulse-e2e.js send-command LAPTOP-DEVICE-ID rescanApps
node guardpulse-e2e.js send-command LAPTOP-DEVICE-ID resetToday --app 'c:\program files\steam\steam.exe'
```

### `status <deviceId>`

One-shot summary: `meta`, `heartbeat`, `sync/desired`, `sync/applied`,
`sync/runtime`, `activity/current`, `state/apps` (count + locked rows) and the
latest tamper events.

---

## Full E2E walkthrough (agent install → policy → unlock → commands)

Prereq: Node 18+, and the Windows agent installed on the test laptop
(service running, setup screen visible). Run everything from
`windows/tools/e2e`.

```bash
# 1. Create/sign-in the fake parent (once; session persists to e2e-session.json)
node guardpulse-e2e.js ensure-account

# 2. Install + start the agent on the laptop (per windows/installer docs).
#    The agent registers devices/{id}/meta {deviceId, tvUid, platform:"windows"}
#    and its setup screen shows a PAIRING SECRET and a 6-DIGIT CODE.
#    Note the deviceId (shown on the setup screen) and both values.

# 3. Start watching (leave running in a second terminal)
node guardpulse-e2e.js watch LAPTOP-DEVICE-ID

# 4. Pair: send the pair request using the laptop's shown secret/code.
#    The agent validates it and writes meta.ownerUid within ~10 s; watch
#    confirms via subsequent reads succeeding.
node guardpulse-e2e.js pair-accept LAPTOP-DEVICE-ID --secret <shown-secret> --code <shown-code>

# 5. Seed a policy: set PIN 123456, block a test app (pick an installed app's
#    key from the agent's inventory under devices/{id}/apps), limit another.
node guardpulse-e2e.js seed-control LAPTOP-DEVICE-ID \
  --pin 123456 \
  --block 'c:\program files\<vendor>\<app>.exe' \
  --limit 'c:\program files\<other>\<app>.exe'=30

# 6. Observe in `watch` (usually within a couple of seconds):
#      sync/applied* : revisionId=<new> status=applied sessionId=<agent session>
#      state/apps    : ... lockBlocked=1 [c:\program files\<vendor>\<app>.exe (manual)]
#      activity      : appLabel="<App>" overlayState=locked   (when the app is focused)
#    On the laptop: launching the blocked app shows the lock overlay; entering
#    PIN 123456 unlocks it for one visit.

# 7. Child taps "Ask parent" on the lock overlay -> agent creates a pending
#    unlockRequest. Approve it (find the requestId in watch/tamper output or
#    devices/{id}/unlockRequests):
node guardpulse-e2e.js unlock-approve LAPTOP-DEVICE-ID <requestId>          # one visit
node guardpulse-e2e.js unlock-approve LAPTOP-DEVICE-ID <requestId> --timed 15

# 8. Exercise commands:
node guardpulse-e2e.js send-command LAPTOP-DEVICE-ID rescanApps
node guardpulse-e2e.js send-command LAPTOP-DEVICE-ID resetToday --app 'c:\program files\<vendor>\<app>.exe'
node guardpulse-e2e.js send-command LAPTOP-DEVICE-ID openSetup
node guardpulse-e2e.js send-command LAPTOP-DEVICE-ID unpair    # removes ownerUid: device unpaired

# 9. One-shot sanity check anytime:
node guardpulse-e2e.js status LAPTOP-DEVICE-ID
```

## Rule-constraint notes (why the tool writes what it writes)

- `control/v2` validates: `schemaVersion==2`, non-blank `revisionId` that
  **changed** vs the previous value, `safeMode` present (`enabled:false` +
  `until:0` when disabled), pin salt/hash shapes (22/43-char base64url) with
  PBKDF2 bounds for v2, and every app map key == `base64url(packageName)`
  with `packageKey` equal to the key — hence the exact snapshot shape above.
- `sync/applied` may only be written by the agent, and only when its
  `revisionId` equals both `sync/desired.revisionId` and
  `control/v2.revisionId`, and its `sessionId` equals `sync/runtime.sessionId`.
  The harness never writes `applied`; it seeds `desired` to match the snapshot
  so the agent's ack is rule-legal.
- `pairRequests/{id}` can only be created while `meta.ownerUid` is absent, and
  only with `status:"pending"`; the agent flips it to `accepted` and sets
  `meta.ownerUid`.
- `unlockRequests` approval is only legal from `pending`, and
  `approvalDurationMs` may only be exactly 900000 or 1800000.
- `commands` may only be created by the owner without a status (or
  `pending`); only the agent moves them to running/done/failed/expired.

## Troubleshooting

- `HTTP 401 {"error":"Permission denied"}` on writes: the device is not
  paired with this account (`meta.ownerUid` mismatch) — run `pair-accept`
  first; on reads before pairing it is expected.
- `INVALID_LOGIN_CREDENTIALS` on commands: run `ensure-account` once (or set
  `GP_EMAIL`/`GP_PASSWORD` to the account you created).
- Stale session: delete `e2e-session.json` and re-run `ensure-account`.
- Set `GP_DEBUG=1` for stack traces on unexpected errors.
