#!/usr/bin/env node
/**
 * GuardPulse Laptop Windows agent — E2E harness ("fake parent").
 *
 * Drives the live Firebase project over pure REST (RTDB REST API +
 * Identity Toolkit REST) so the Windows agent can be validated
 * end-to-end WITHOUT the phone app. Zero dependencies, Node 18+
 * (uses global fetch and node:crypto/node:fs/node:path built-ins).
 *
 * Commands:
 *   ensure-account                                 create/sign-in the fake parent, persist token
 *   seed-control <deviceId> [--pin 123456] [--block <appKey>]... [--limit <appKey>=<minutes>]...
 *   pair-accept <deviceId> [--secret X] [--code Y] [--code-only]
 *   watch <deviceId>                               poll agent state every 2s (Ctrl+C to stop)
 *   unlock-approve <deviceId> <requestId> [--timed 15|30]
 *   send-command <deviceId> <rescanApps|resetToday|unpair|openSetup> [--app <appKey>]
 *   status <deviceId>                              one-shot summary
 *
 * Config (env): GP_API_KEY, GP_DB_URL, GP_EMAIL, GP_PASSWORD, GP_SESSION_FILE.
 */
'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

// ---------------------------------------------------------------------------
// Config
// ---------------------------------------------------------------------------

// Config: credentials come ONLY from the environment so they are never committed.
const CONFIG = (() => {
  const apiKey = process.env.GP_API_KEY;
  const email = process.env.GP_EMAIL;
  const password = process.env.GP_PASSWORD;
  if (!apiKey || !email || !password) {
    console.error('Missing required env vars: GP_API_KEY, GP_EMAIL and GP_PASSWORD must be set to run the e2e harness.');
    process.exit(1);
  }

  return {
    apiKey,
    dbUrl: (process.env.GP_DB_URL || 'https://guardpulse-laptop-control-default-rtdb.firebaseio.com').replace(/\/+$/, ''),
    email,
    password,
  };
})();
const SESSION_FILE = process.env.GP_SESSION_FILE || path.join(__dirname, 'e2e-session.json');

const IDENTITY_BASE = 'https://identitytoolkit.googleapis.com/v1';
const SECURETOKEN_URL = 'https://securetoken.googleapis.com/v1/token';

const SV_TIMESTAMP = { '.sv': 'timestamp' };
const PBKDF2_ITERATIONS = 210000;

const COMMAND_TYPES = ['rescanApps', 'resetToday', 'unpair', 'openSetup'];
// Mirrors PolicyConstants.commandTtlMs from the shared Kotlin sources.
const COMMAND_TTL_MS = { rescanApps: 300000, resetToday: 300000, unpair: 600000, openSetup: 60000 };

// ---------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------

class CliError extends Error {}

function die(msg) {
  // Thrown instead of process.exit() so pending HTTP handles can close cleanly
  // (hard process.exit trips a libuv assertion on Windows with open sockets).
  throw new CliError(msg);
}

function info(msg) {
  console.log(msg);
}

function clock(ms = Date.now()) {
  const d = new Date(ms);
  return d.toTimeString().slice(0, 8);
}

function ago(ms) {
  if (typeof ms !== 'number' || Number.isNaN(ms)) return 'n/a';
  const s = Math.max(0, Math.round((Date.now() - ms) / 1000));
  if (s < 60) return s + 's ago';
  if (s < 3600) return Math.round(s / 60) + 'm ago';
  return Math.round(s / 3600) + 'h ago';
}

function j(obj) {
  return JSON.stringify(obj, null, 2);
}

/** Firebase-style push id (20 chars, time-ordered, unique). */
const PUSH_CHARS = '-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz';
function pushId(nowMs = Date.now()) {
  const chars = new Array(8);
  let remaining = nowMs;
  for (let i = 7; i >= 0; i--) {
    chars[i] = PUSH_CHARS[remaining % 64];
    remaining = Math.floor(remaining / 64);
  }
  let id = chars.join('');
  for (let i = 0; i < 12; i++) id += PUSH_CHARS[Math.floor(Math.random() * 64)];
  return id;
}

/** Same as Kotlin PackageKeys.encode: base64url(packageName), no padding. */
function encodePackageKey(packageName) {
  return Buffer.from(String(packageName), 'utf8').toString('base64url').replace(/=+$/, '');
}

class HttpError extends Error {
  constructor(label, status, body) {
    super(`${label} failed: HTTP ${status}: ${typeof body === 'string' ? body : JSON.stringify(body)}`);
    this.status = status;
    this.body = body;
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function fetchJson(url, { method = 'GET', body } = {}, timeoutMs = 20000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const res = await fetch(url, {
      method,
      headers: body !== undefined ? { 'content-type': 'application/json' } : undefined,
      body: body !== undefined ? JSON.stringify(body) : undefined,
      signal: controller.signal,
    });
    const text = await res.text();
    let json = null;
    try {
      json = text.length ? JSON.parse(text) : null;
    } catch {
      json = text;
    }
    return { status: res.status, ok: res.ok, json, text };
  } finally {
    clearTimeout(timer);
  }
}

// ---------------------------------------------------------------------------
// Identity Toolkit auth (email/password parent account)
// ---------------------------------------------------------------------------

function loadSession() {
  try {
    const raw = fs.readFileSync(SESSION_FILE, 'utf8');
    const s = JSON.parse(raw);
    return s && typeof s === 'object' ? s : null;
  } catch {
    return null;
  }
}

function saveSession(session) {
  fs.writeFileSync(SESSION_FILE, JSON.stringify(session, null, 2) + '\n', 'utf8');
}

async function identityPost(action, body) {
  const url = `${IDENTITY_BASE}/accounts:${action}?key=${encodeURIComponent(CONFIG.apiKey)}`;
  const res = await fetchJson(url, { method: 'POST', body });
  if (!res.ok) throw new HttpError(`identitytoolkit ${action}`, res.status, res.json ?? res.text);
  return res.json;
}

function sessionFromSignIn(resp) {
  const expiresInSec = parseInt(resp.expiresIn || '3600', 10);
  return {
    uid: resp.localId,
    email: resp.email,
    idToken: resp.idToken,
    refreshToken: resp.refreshToken,
    idTokenExpiresAt: Date.now() + Math.max(60, expiresInSec - 60) * 1000,
    savedAt: Date.now(),
  };
}

async function signInWithEmail() {
  let resp;
  try {
    resp = await identityPost('signInWithPassword', {
      email: CONFIG.email,
      password: CONFIG.password,
      returnSecureToken: true,
    });
  } catch (e) {
    const bodyText = typeof e.body === 'string' ? e.body : JSON.stringify(e.body || '');
    if (/EMAIL_NOT_FOUND|INVALID_PASSWORD|INVALID_LOGIN_CREDENTIALS/.test(bodyText)) {
      throw new Error('no usable session and sign-in failed — run `node guardpulse-e2e.js ensure-account` first. ' + e.message);
    }
    throw e;
  }
  const session = sessionFromSignIn(resp);
  saveSession(session);
  return session;
}

async function refreshFromToken(refreshToken) {
  const url = `${SECURETOKEN_URL}?key=${encodeURIComponent(CONFIG.apiKey)}`;
  const res = await fetchJson(url, {
    method: 'POST',
    body: { grant_type: 'refresh_token', refresh_token: refreshToken },
  });
  if (!res.ok) return null;
  const d = res.json;
  if (!d || !d.access_token) return null;
  const expiresInSec = parseInt(d.expires_in || '3600', 10);
  const session = {
    uid: d.user_id,
    email: CONFIG.email,
    idToken: d.access_token,
    refreshToken: d.refresh_token || refreshToken,
    idTokenExpiresAt: Date.now() + Math.max(60, expiresInSec - 60) * 1000,
    savedAt: Date.now(),
  };
  saveSession(session);
  return session;
}

let inflightRefresh = null;

/** Refresh (or sign in) exactly once even when many calls race; returns idToken. */
async function forceTokenRefresh() {
  if (inflightRefresh) return inflightRefresh;
  inflightRefresh = (async () => {
    const s = loadSession();
    if (s && s.refreshToken) {
      const refreshed = await refreshFromToken(s.refreshToken);
      if (refreshed) return refreshed.idToken;
    }
    const signedIn = await signInWithEmail();
    return signedIn.idToken;
  })().finally(() => {
    inflightRefresh = null;
  });
  return inflightRefresh;
}

/** Returns a fresh-enough idToken; refreshes or signs in when needed. */
async function ensureToken() {
  const s = loadSession();
  if (s && s.idToken && s.idTokenExpiresAt && s.idTokenExpiresAt > Date.now() + 60000) {
    return s.idToken;
  }
  return forceTokenRefresh();
}

async function requireUid() {
  await ensureToken();
  const s = loadSession();
  if (!s || !s.uid) die('signed in but uid unknown — re-run ensure-account');
  return s.uid;
}

// ---------------------------------------------------------------------------
// RTDB REST (PUT/PATCH/POST/GET with ?auth=; auto token refresh on 401)
// ---------------------------------------------------------------------------

function rtdbUrl(p, token, extraQuery) {
  const params = new URLSearchParams({ auth: token });
  if (extraQuery) for (const [k, v] of Object.entries(extraQuery)) params.set(k, v);
  return `${CONFIG.dbUrl}/${p}.json?${params.toString()}`;
}

async function rtdbRaw(method, p, token, body, extraQuery) {
  return fetchJson(rtdbUrl(p, token, extraQuery), {
    method,
    body: body !== undefined ? body : undefined,
  });
}

/**
 * Perform an authenticated RTDB REST call. On 401 that is NOT a rules
 * "Permission denied", refreshes the token once and retries.
 * Throws HttpError (prints status + body) on failure.
 */
async function rtdb(method, p, body, extraQuery) {
  let token = await ensureToken();
  let res = await rtdbRaw(method, p, token, body, extraQuery);

  if (res.status === 401) {
    const bodyText = typeof res.json === 'string' ? res.json : JSON.stringify(res.json ?? res.text ?? '');
    if (!/permission denied/i.test(bodyText)) {
      try {
        token = await forceTokenRefresh();
        res = await rtdbRaw(method, p, token, body, extraQuery);
      } catch (e) {
        throw new HttpError(`RTDB ${method} ${p} (token refresh failed: ${e.message})`, res.status, res.json ?? res.text);
      }
    }
  }

  if (!res.ok) {
    throw new HttpError(`RTDB ${method} ${p}`, res.status, res.json ?? res.text);
  }
  return res.json;
}

const rtdbGet = (p, q) => rtdb('GET', p, undefined, q);
const rtdbPut = (p, body) => rtdb('PUT', p, body);
const rtdbPatch = (p, body) => rtdb('PATCH', p, body);
const rtdbPost = (p, body) => rtdb('POST', p, body);

// ---------------------------------------------------------------------------
// PIN hashing (must match Kotlin PinHasher v2 exactly)
// ---------------------------------------------------------------------------

/** PBKDF2-HMAC-SHA256, 210000 iterations, 16-byte salt, 32-byte key, base64url no padding. */
function createPinRecord(pin) {
  if (!/^\d{6}$/.test(pin)) die('PIN must be exactly six digits (got "' + pin + '")');
  const saltBytes = crypto.randomBytes(16);
  const salt = saltBytes.toString('base64url').replace(/=+$/, '');
  const hash = crypto
    .pbkdf2Sync(pin, saltBytes, PBKDF2_ITERATIONS, 32, 'sha256')
    .toString('base64url')
    .replace(/=+$/, '');
  if (salt.length !== 22 || hash.length !== 43) {
    die(`internal error: bad salt/hash shapes (salt=${salt.length} chars, hash=${hash.length} chars)`);
  }
  return {
    salt,
    hash,
    version: 2,
    algorithm: 'PBKDF2WithHmacSHA256',
    iterations: PBKDF2_ITERATIONS,
    updatedAt: SV_TIMESTAMP,
  };
}

// ---------------------------------------------------------------------------
// Arg parsing
// ---------------------------------------------------------------------------

function parseArgs(argv, { flags = [], values = [], repeat = [] } = {}) {
  const pos = [];
  const opts = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--') {
      pos.push(...argv.slice(i + 1));
      break;
    }
    if (a.startsWith('--')) {
      const name = a.slice(2);
      const key = name.replace(/-([a-z])/g, (m, c) => c.toUpperCase()); // code-only -> codeOnly
      if (flags.includes(name)) {
        opts[key] = true;
        continue;
      }
      if (values.includes(name)) {
        const v = argv[++i];
        if (v === undefined || v.startsWith('--')) die(`--${name} requires a value`);
        opts[key] = v;
        continue;
      }
      if (repeat.includes(name)) {
        const v = argv[++i];
        if (v === undefined || v.startsWith('--')) die(`--${name} requires a value`);
        (opts[key] = opts[key] || []).push(v);
        continue;
      }
      die(`unknown option --${name}`);
    }
    pos.push(a);
  }
  return { pos, opts };
}

// ---------------------------------------------------------------------------
// Command: ensure-account
// ---------------------------------------------------------------------------

async function cmdEnsureAccount() {
  info(`GuardPulse E2E fake parent`);
  info(`  project db : ${CONFIG.dbUrl}`);
  info(`  email      : ${CONFIG.email}`);

  try {
    await identityPost('signUp', {
      email: CONFIG.email,
      password: CONFIG.password,
      returnSecureToken: true,
    });
    info('  account    : created');
  } catch (e) {
    const bodyText = typeof e.body === 'string' ? e.body : JSON.stringify(e.body || {});
    if (/EMAIL_EXISTS/.test(bodyText)) {
      info('  account    : already exists (EMAIL_EXISTS tolerated)');
    } else {
      throw e;
    }
  }

  const resp = await identityPost('signInWithPassword', {
    email: CONFIG.email,
    password: CONFIG.password,
    returnSecureToken: true,
  });
  const session = sessionFromSignIn(resp);
  saveSession(session);

  info('  uid        : ' + session.uid);
  info('  idToken    : ' + session.idToken);
  info(`  token good until ${new Date(session.idTokenExpiresAt).toISOString()} (auto-refreshed on 401)`);
  info(`  session persisted to ${SESSION_FILE}`);
}

// ---------------------------------------------------------------------------
// Command: seed-control
// ---------------------------------------------------------------------------

async function cmdSeedControl(deviceId, opts) {
  const uid = await requireUid();
  let pin = opts.pin || '123456';
  let pinRecord;
  if (opts['keep-pin']) {
    // Preserve the existing PIN record (snapshot + security/pin stay untouched).
    try {
      const current = await rtdbGet(`devices/${deviceId}/control/v2`);
      pinRecord = current && current.pin;
    } catch { /* unreadable: fall through to a fresh pin */ }
  }
  if (!pinRecord) pinRecord = createPinRecord(pin); else pin = '(kept)';

  // Merge --block / --limit into app rules keyed by base64url(packageName).
  const apps = {};
  const addRule = (appKey, patch) => {
    const key = encodePackageKey(appKey);
    apps[key] = Object.assign(
      {
        packageKey: key,
        packageName: appKey,
        manualBlocked: false,
        updatedAt: SV_TIMESTAMP,
      },
      apps[key] || {},
      patch
    );
  };
  for (const appKey of opts.block || []) {
    addRule(appKey, { manualBlocked: true });
  }
  for (const appKey of opts.allow || []) {
    addRule(appKey, { manualBlocked: false }); // explicit row: un-defaults bypass tools
  }
  for (const spec of opts.limit || []) {
    const eq = spec.indexOf('=');
    if (eq < 0) die(`--limit expects <appKey>=<minutes>, got "${spec}"`);
    const appKey = spec.slice(0, eq);
    const minutes = parseInt(spec.slice(eq + 1), 10);
    if (!Number.isInteger(minutes) || minutes < 1 || minutes > 1440) {
      die(`daily limit for "${appKey}" must be an integer in 1..1440 minutes (got "${spec.slice(eq + 1)}")`);
    }
    addRule(appKey, { dailyLimitMinutes: minutes });
  }

  // revisionId must differ from the currently stored one (rules enforce this).
  let previousRevision;
  try {
    const current = await rtdbGet(`devices/${deviceId}/control/v2`);
    previousRevision = current && current.revisionId ? current.revisionId : null;
  } catch {
    previousRevision = null; // unreadable (unpaired?) — rules will reject the write later anyway
  }
  let revisionId = pushId();
  while (revisionId === previousRevision) revisionId = pushId();


  const snapshot = {
    schemaVersion: 2,
    revisionId,
    updatedAt: SV_TIMESTAMP,
    updatedBy: uid,
    apps,
    modes: {},
    // activeMode intentionally OMITTED entirely (never write null)
    safeMode: { enabled: false, until: 0 },
    pin: pinRecord,
  };

  info(`Seeding devices/${deviceId}/control/v2`);
  info(`  revisionId : ${revisionId}${previousRevision ? ' (previous: ' + previousRevision + ')' : ' (no previous)'}`);
  info(`  pin        : ${pin} -> salt=${pinRecord.salt} hash=${pinRecord.hash}`);
  info(`  apps       : ${Object.keys(apps).length} rule(s)`);

  const writtenSnapshot = await rtdbPut(`devices/${deviceId}/control/v2`, snapshot);
  info(`  control/v2 written (updatedAt resolved to ${
    writtenSnapshot && typeof writtenSnapshot === 'object' && writtenSnapshot.updatedAt
      ? new Date(writtenSnapshot.updatedAt).toISOString()
      : 'server value'
  })`);

  if (!opts['keep-pin']) {
    // Dual-write the legacy pin path with the same record.
    await rtdbPut(`devices/${deviceId}/security/pin`, pinRecord);
    info(`  security/pin dual-written (same record)`);
  }

  // Nudge the agent: sync/desired points at the new revision.
  const desired = {
    revisionId,
    kind: 'appPolicy',
    requestedAt: SV_TIMESTAMP,
    requestedBy: uid,
  };
  const writtenDesired = await rtdbPut(`devices/${deviceId}/sync/desired`, desired);
  info(`  sync/desired written (requestedAt resolved to ${
    writtenDesired && typeof writtenDesired === 'object' && writtenDesired.requestedAt
      ? new Date(writtenDesired.requestedAt).toISOString()
      : 'server value'
  })`);
  info(`Done. Run \`node guardpulse-e2e.js watch ${deviceId}\` and wait for sync/applied {revisionId:"${revisionId}", status:"applied"}.`);
}

// ---------------------------------------------------------------------------
// Command: pair-accept
// ---------------------------------------------------------------------------

async function cmdPairAccept(deviceId, opts) {
  const uid = await requireUid();

  // Learn current device state if readable. Before pairing the parent usually
  // CANNOT read devices/{id}/* (rules require ownerUid/tvUid == auth.uid), so a
  // failure here is expected and harmless.
  try {
    const meta = await rtdbGet(`devices/${deviceId}/meta`);
    if (meta && typeof meta === 'object') {
      info(`Current meta for ${deviceId}:`);
      info(`  tvUid=${meta.tvUid} ownerUid=${meta.ownerUid || '(unset)'} platform=${meta.platform || '?'} pairedAt=${meta.pairedAt ? ago(meta.pairedAt) : 'never'}`);
      if (meta.ownerUid) {
        info(`  NOTE: ownerUid is already set — this device appears PAIRED. A new pair request will be rejected by the rules.`);
      }
    } else {
      info(`devices/${deviceId}/meta is empty — device not registered yet (agent writes meta on first run).`);
    }
  } catch (e) {
    info(`meta not readable by this account yet (${e.message}) — normal before pairing; continuing.`);
  }

  const body = { parentUid: uid, createdAt: SV_TIMESTAMP, status: 'pending' };
  if (opts.secret && !opts.codeOnly) body.secret = opts.secret;
  if (opts.code) body.code = opts.code;
  if (!body.secret && !body.code) {
    die('pair-accept needs the value shown on the laptop setup screen: pass --secret <secret> and/or --code <6-digit code> (use --code-only to send just the code)');
  }
  if (body.code && !/^\d{6}$/.test(body.code)) die('--code must be exactly six digits (got "' + body.code + '")');
  if (body.secret && !String(body.secret).trim()) die('--secret must not be blank');

  info(`Creating pair request at pairRequests/${deviceId}`);
  const result = await rtdbPost(`pairRequests/${deviceId}`, body);
  const requestKey = result && result.name ? result.name : '(unknown)';
  info(`  requestId  : ${requestKey}`);
  info(`  payload    : ${JSON.stringify({ ...body, createdAt: '(server timestamp)' })}`);
  info(`Done. The laptop agent should accept within its pairing TTL (10 min): it validates the secret/code`);
  info(`and then writes meta.ownerUid="${uid}". Watch with \`node guardpulse-e2e.js status ${deviceId}\`.`);
}

// ---------------------------------------------------------------------------
// Watch / status rendering helpers
// ---------------------------------------------------------------------------

function summarizeStateApps(obj) {
  if (!obj || typeof obj !== 'object') return { count: 0, locked: [] };
  const entries = Object.entries(obj);
  const locked = entries
    .filter(([, v]) => v && typeof v === 'object' && v.lockBlocked === true)
    .map(([k, v]) => `${v.packageName || k}${v.lockReason ? ' (' + v.lockReason + ')' : ''}`);
  return { count: entries.length, locked };
}

function summarizeTamper(obj) {
  if (!obj || typeof obj !== 'object') return [];
  return Object.entries(obj)
    .map(([id, v]) => ({ id, ...(v && typeof v === 'object' ? v : {}) }))
    .sort((a, b) => (b.createdAt || 0) - (a.createdAt || 0))
    .slice(0, 5);
}

async function getTamperEvents(deviceId) {
  try {
    return await rtdbGet(`devices/${deviceId}/tamperEvents`, { orderBy: '"createdAt"', limitToLast: 5 });
  } catch {
    // orderBy needs the deployed .indexOn; fall back to a plain read.
    return rtdbGet(`devices/${deviceId}/tamperEvents`);
  }
}

function line(label, text, changed) {
  return `  ${label.padEnd(13)}${changed ? '*' : ' '}: ${text}`;
}

async function fetchWatchSections(deviceId) {
  const [applied, runtime, heartbeat, stateApps, activity, tamper] = await Promise.allSettled([
    rtdbGet(`devices/${deviceId}/sync/applied`),
    rtdbGet(`devices/${deviceId}/sync/runtime`),
    rtdbGet(`devices/${deviceId}/heartbeat`),
    rtdbGet(`devices/${deviceId}/state/apps`),
    rtdbGet(`devices/${deviceId}/activity/current`),
    getTamperEvents(deviceId),
  ]);
  return { applied, runtime, heartbeat, stateApps, activity, tamper };
}

function renderWatchTick(deviceId, sections, poll, prevSigs) {
  const out = [];
  const sigs = {};
  const sig = (key, value) => {
    sigs[key] = JSON.stringify(value);
    return prevSigs ? prevSigs[key] !== sigs[key] : false;
  };

  const settled = (r, label, sigKey) => {
    if (r.status === 'rejected') {
      const e = r.reason;
      out.push(line(label, `ERROR ${e.message}`, sig(label + ':err', e.message)));
      return null;
    }
    if (r.value && typeof r.value === 'object') return r.value;
    out.push(line(label, '(absent)', sig(sigKey || label, null)));
    return null;
  };

  const applied = settled(sections.applied, 'sync/applied');
  if (applied) {
    out.push(line('sync/applied',
      `revisionId=${applied.revisionId || '-'} status=${applied.status || '-'} sessionId=${applied.sessionId || '-'} appliedAt=${ago(applied.appliedAt)} error=${applied.error || '-'}`,
      sig('applied', [applied.revisionId, applied.status, applied.sessionId, applied.error])));
  }

  const runtime = settled(sections.runtime, 'sync/runtime');
  if (runtime) {
    out.push(line('sync/runtime',
      `connected=${runtime.connected} sessionId=${runtime.sessionId || '-'} protocolVersion=${runtime.protocolVersion ?? '-'} policyRx=${ago(runtime.lastPolicyReceivedAt)} policyApplied=${ago(runtime.lastPolicyAppliedAt)} lastError=${runtime.lastError || '-'}`,
      sig('runtime', [runtime.connected, runtime.sessionId, runtime.protocolVersion, runtime.lastError])));
  }

  const heartbeat = settled(sections.heartbeat, 'heartbeat');
  if (heartbeat) {
    out.push(line('heartbeat',
      `online=${heartbeat.online} protectionHealthy=${heartbeat.protectionHealthy} enforcementMode=${heartbeat.enforcementMode || '-'} safeModeActive=${heartbeat.safeModeActive ?? '-'} lastSeen=${ago(heartbeat.lastSeen)}`,
      sig('heartbeat', [heartbeat.online, heartbeat.protectionHealthy, heartbeat.enforcementMode, heartbeat.safeModeActive])));
  }

  const stateAppsVal = sections.stateApps.status === 'fulfilled' ? sections.stateApps.value : null;
  if (sections.stateApps.status === 'rejected') {
    out.push(line('state/apps', `ERROR ${sections.stateApps.reason.message}`, sig('stateApps:err', sections.stateApps.reason.message)));
  } else {
    const sum = summarizeStateApps(stateAppsVal);
    out.push(line('state/apps',
      `${sum.count} tracked row(s); lockBlocked=${sum.locked.length}${sum.locked.length ? ' [' + sum.locked.join(', ') + ']' : ''}`,
      sig('stateApps', sum.locked)));
  }

  const activity = settled(sections.activity, 'activity');
  if (activity) {
    out.push(line('activity',
      `appLabel="${activity.appLabel ?? '-'}" overlayState=${activity.overlayState ?? '-'} appKey=${activity.appKey ?? '-'} updatedAt=${ago(activity.updatedAt)}`,
      sig('activity', [activity.appLabel, activity.overlayState, activity.appKey])));
  }

  const tamperVal = sections.tamper.status === 'fulfilled' ? summarizeTamper(sections.tamper.value) : null;
  if (sections.tamper.status === 'rejected') {
    out.push(line('tamper', `ERROR ${sections.tamper.reason.message}`, sig('tamper:err', sections.tamper.reason.message)));
  } else {
    const latest = tamperVal[0];
    out.push(line('tamper',
      `${tamperVal.length} event(s)${latest ? `; latest: ${latest.type || latest.id} ${ago(latest.createdAt)}${latest.message ? ' — ' + latest.message : ''}` : ''}`,
      sig('tamper', tamperVal.map((t) => [t.id, t.type]))));
  }

  info(`[${clock()}] poll #${poll} on devices/${deviceId} (changed sections marked *)`);
  for (const l of out) info(l);
  return sigs;
}

// ---------------------------------------------------------------------------
// Command: watch
// ---------------------------------------------------------------------------

async function cmdWatch(deviceId) {
  await requireUid();
  info(`Watching devices/${deviceId} every 2s — Ctrl+C to stop.`);
  info(`db: ${CONFIG.dbUrl}`);
  let stopped = false;
  process.on('SIGINT', () => {
    if (stopped) process.exit(130); // second Ctrl+C forces exit
    stopped = true;
    info('\n[watch] stopping (Ctrl+C again to force)...');
  });

  let prevSigs = null;
  let poll = 0;
  while (!stopped) {
    poll++;
    try {
      const sections = await fetchWatchSections(deviceId);
      prevSigs = renderWatchTick(deviceId, sections, poll, prevSigs);
    } catch (e) {
      info(`[${clock()}] poll #${poll} failed: ${e.message}`);
    }
    // Sleep in short slices so Ctrl+C is honored promptly.
    for (let waited = 0; waited < 2000 && !stopped; waited += 200) await sleep(200);
  }
}

// ---------------------------------------------------------------------------
// Command: status
// ---------------------------------------------------------------------------

async function cmdStatus(deviceId) {
  await requireUid();
  const sections = await fetchWatchSections(deviceId);
  const meta = await rtdbGet(`devices/${deviceId}/meta`).catch((e) => ({ __error: e.message }));

  info(`=== GuardPulse status: devices/${deviceId} @ ${new Date().toISOString()} ===`);
  info(`db: ${CONFIG.dbUrl}`);

  info('--- meta ---');
  info(meta && meta.__error ? `ERROR ${meta.__error}` : j(meta ?? null));

  const heartbeat = sections.heartbeat;
  info('--- heartbeat ---');
  if (heartbeat.status === 'rejected') info(`ERROR ${heartbeat.reason.message}`);
  else {
    const v = heartbeat.value && typeof heartbeat.value === 'object' ? heartbeat.value : null;
    info(v
      ? j({ online: v.online, lastSeen: v.lastSeen, enforcementMode: v.enforcementMode, protectionHealthy: v.protectionHealthy, safeModeActive: v.safeModeActive, activeModeName: v.activeModeName })
      : '(absent)');
  }

  const desired = await rtdbGet(`devices/${deviceId}/sync/desired`).catch((e) => ({ __error: e.message }));
  info('--- sync/desired ---');
  info(desired && desired.__error ? `ERROR ${desired.__error}` : j(desired ?? null));

  for (const [label, result] of [
    ['sync/applied', sections.applied],
    ['sync/runtime', sections.runtime],
    ['activity/current', sections.activity],
  ]) {
    info(`--- ${label} ---`);
    if (result.status === 'rejected') info(`ERROR ${result.reason.message}`);
    else info(j(result.value ?? null));
  }

  info('--- state/apps ---');
  if (sections.stateApps.status === 'rejected') {
    info(`ERROR ${sections.stateApps.reason.message}`);
  } else {
    const sum = summarizeStateApps(sections.stateApps.value);
    info(`${sum.count} tracked row(s); lockBlocked=${sum.locked.length}`);
    for (const l of sum.locked) info(`  LOCKED: ${l}`);
  }

  info('--- tamperEvents (latest 5) ---');
  if (sections.tamper.status === 'rejected') {
    info(`ERROR ${sections.tamper.reason.message}`);
  } else {
    for (const t of summarizeTamper(sections.tamper.value)) {
      info(`  ${new Date(t.createdAt || 0).toISOString()} ${t.type || t.id}${t.message ? ' — ' + t.message : ''}`);
    }
  }
}

// ---------------------------------------------------------------------------
// Command: unlock-approve
// ---------------------------------------------------------------------------

async function cmdUnlockApprove(deviceId, requestId, opts) {
  const uid = await requireUid();

  try {
    const req = await rtdbGet(`devices/${deviceId}/unlockRequests/${requestId}`);
    if (req && typeof req === 'object') {
      info(`Request ${requestId}: packageName=${req.packageName} reason=${req.reason} status=${req.status}`);
      if (req.status !== 'pending') {
        info(`  WARNING: status is "${req.status}" — rules only allow approving a "pending" request; the PATCH below may be rejected.`);
      }
    } else {
      info(`Request ${requestId} not found or empty — PATCH will attempt anyway.`);
    }
  } catch (e) {
    info(`Could not pre-read the request (${e.message}) — PATCH will attempt anyway.`);
  }

  const body = {
    status: 'approved',
    approvalType: opts.timed ? 'timed' : 'oneVisit',
    updatedAt: SV_TIMESTAMP,
    updatedBy: uid,
  };
  if (opts.timed) {
    const minutes = parseInt(opts.timed, 10);
    if (minutes === 15) body.approvalDurationMs = 900000;
    else if (minutes === 30) body.approvalDurationMs = 1800000;
    else die('--timed must be 15 or 30');
  }

  info(`PATCH devices/${deviceId}/unlockRequests/${requestId}`);
  info(`  payload: ${JSON.stringify({ ...body, updatedAt: '(server timestamp)' })}`);
  const result = await rtdbPatch(`devices/${deviceId}/unlockRequests/${requestId}`, body);
  info(`  result : ${j(result)}`);
  info('Done. The agent should observe the approval and unlock the app (one visit or timed window).');
}

// ---------------------------------------------------------------------------
// Command: send-command
// ---------------------------------------------------------------------------

async function cmdSendCommand(deviceId, type, opts) {
  const uid = await requireUid();
  if (!COMMAND_TYPES.includes(type)) {
    die(`unknown command type "${type}" — must be one of: ${COMMAND_TYPES.join(', ')}`);
  }

  const body = {
    type,
    requestedBy: uid,
    createdAt: SV_TIMESTAMP,
    ttlMs: COMMAND_TTL_MS[type],
  };
  if (opts.app) body.packageName = opts.app;

  info(`POST devices/${deviceId}/commands`);
  info(`  payload: ${JSON.stringify({ ...body, createdAt: '(server timestamp)' })}`);
  const result = await rtdbPost(`devices/${deviceId}/commands`, body);
  info(`  commandId: ${result && result.name ? result.name : '(unknown)'}`);
  info('Done. The agent claims the command and reports status back (watch it with the `watch` command).');
}

// ---------------------------------------------------------------------------
// Usage / main
// ---------------------------------------------------------------------------

const USAGE = `GuardPulse Laptop E2E harness — fake parent over Firebase REST (zero deps, Node 18+)

Usage:
  node guardpulse-e2e.js ensure-account
      Create the parent account (tolerates EMAIL_EXISTS) and sign in; prints idToken.
      Persists token+refresh to e2e-session.json.

  node guardpulse-e2e.js seed-control <deviceId> [--pin 123456]
        [--block <appKey>]... [--limit <appKey>=<minutes>]...
      Writes a rules-valid control/v2 snapshot (schemaVersion 2, new revisionId,
      safeMode disabled, PBKDF2 v2 pin), dual-writes security/pin, and points
      sync/desired at the new revision (kind=appPolicy).

  node guardpulse-e2e.js pair-accept <deviceId> [--secret <secret>] [--code <6-digit>]
        [--code-only]
      Creates the pair request a parent phone would send, using the secret/code
      shown on the laptop setup screen. --code-only omits the secret.

  node guardpulse-e2e.js watch <deviceId>
      Polls every 2s: sync/applied, sync/runtime, heartbeat, state/apps,
      activity/current, latest tamperEvents. Ctrl+C to stop.

  node guardpulse-e2e.js unlock-approve <deviceId> <requestId> [--timed 15|30]
      Approves a pending unlock request (oneVisit by default; timed 15/30 min).

  node guardpulse-e2e.js send-command <deviceId> <rescanApps|resetToday|unpair|openSetup>
        [--app <appKey>]
      Posts a command for the agent to claim.

  node guardpulse-e2e.js status <deviceId>
      One-shot summary: meta, heartbeat, sync/desired, sync/applied, sync/runtime,
      activity/current, state/apps, latest tamperEvents.

Environment:
  GP_API_KEY      Identity Toolkit / RTDB API key
  GP_DB_URL       RTDB base URL
  GP_EMAIL        parent email            (required; no default - never committed)
  GP_PASSWORD     parent password         (required; no default - never committed)
  GP_SESSION_FILE where to persist the session (default <tool dir>/e2e-session.json)`;

async function main() {
  const [cmd, ...rest] = process.argv.slice(2);
  try {
    switch (cmd) {
      case undefined:
      case 'help':
      case '--help':
      case '-h':
        info(USAGE);
        break;
      case 'ensure-account': {
        const { pos } = parseArgs(rest);
        if (pos.length) die(`ensure-account takes no positional arguments (got ${pos.join(' ')})`);
        await cmdEnsureAccount();
        break;
      }
      case 'seed-control': {
        const { pos, opts } = parseArgs(rest, { values: ['pin'], flags: ['keep-pin'], repeat: ['block', 'limit', 'allow'] });
        const deviceId = pos[0];
        if (!deviceId) die('usage: seed-control <deviceId> [--pin 123456] [--block <appKey>]... [--limit <appKey>=<minutes>]... [--allow <appKey>]...');
        await cmdSeedControl(deviceId, opts);
        break;
      }
      case 'pair-accept': {
        const { pos, opts } = parseArgs(rest, { values: ['secret', 'code'], flags: ['code-only'] });
        const deviceId = pos[0];
        if (!deviceId) die('usage: pair-accept <deviceId> [--secret X] [--code Y] [--code-only]');
        await cmdPairAccept(deviceId, opts);
        break;
      }
      case 'watch': {
        const { pos } = parseArgs(rest);
        const deviceId = pos[0];
        if (!deviceId) die('usage: watch <deviceId>');
        await cmdWatch(deviceId);
        break;
      }
      case 'unlock-approve': {
        const { pos, opts } = parseArgs(rest, { values: ['timed'] });
        const [deviceId, requestId] = pos;
        if (!deviceId || !requestId) die('usage: unlock-approve <deviceId> <requestId> [--timed 15|30]');
        await cmdUnlockApprove(deviceId, requestId, opts);
        break;
      }
      case 'send-command': {
        const { pos, opts } = parseArgs(rest, { values: ['app'] });
        const [deviceId, type] = pos;
        if (!deviceId || !type) die('usage: send-command <deviceId> <rescanApps|resetToday|unpair|openSetup> [--app <appKey>]');
        await cmdSendCommand(deviceId, type, opts);
        break;
      }
      case 'status': {
        const { pos } = parseArgs(rest);
        const deviceId = pos[0];
        if (!deviceId) die('usage: status <deviceId>');
        await cmdStatus(deviceId);
        break;
      }
      default:
        info(USAGE);
        die(`unknown command "${cmd}"`);
    }
  } catch (e) {
    if (e instanceof CliError) {
      console.error('ERROR: ' + e.message);
    } else {
      console.error('ERROR: ' + (e && e.message ? e.message : String(e)));
      if (process.env.GP_DEBUG && e && e.stack) console.error(e.stack);
    }
    process.exitCode = 1;
  }
}

main();
