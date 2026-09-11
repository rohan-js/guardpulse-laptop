// Headless functional check of index.html's inline JS against a mock
// Firebase (auth REST + RTDB REST, incl. multi-path PATCH semantics).
// Run:  node hosted/harness.js
"use strict";
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const html = fs.readFileSync(path.join(__dirname, "index.html"), "utf8");
const js = html.match(/<script>([\s\S]*)<\/script>/)[1];

// ---------------------------------------------------------------- DOM stub
function el(id) {
  return {
    id, textContent: "", innerHTML: "", value: "", className: "",
    classList: {
      _s: new Set(),
      add(c) { this._s.add(c); },
      remove(c) { this._s.delete(c); },
      toggle(c, on) { on === undefined ? (this._s.has(c) ? this._s.delete(c) : this._s.add(c)) : on ? this._s.add(c) : this._s.delete(c); },
      contains(c) { return this._s.has(c); },
    },
    contains() { return false; },
    querySelector() { return el(id + "-child"); },
    querySelectorAll() { return []; },
    appendChild() {},
    setAttribute() {},
    onclick: null,
    focus() {},
  };
}
const elements = {};
const documentStub = {
  getElementById(id) { return (elements[id] = elements[id] || el(id)); },
  querySelectorAll() { return []; },
  createElement() { return el("created"); },
  addEventListener() {},
  body: { classList: { toggle() {}, add() {}, remove() {}, contains() { return false; } } },
  activeElement: null,
  hidden: false,
};

// ---------------------------------------------------------------- RTDB mock
const store = {}; // path -> value
function splitPath(p){ return String(p).replace(/^\/+|\/+$/g, "").split("/").filter(Boolean); }
function getNode(path){
  const segs = splitPath(path);
  let node = store;
  for (const seg of segs){
    if (node == null || typeof node !== "object") return null;
    node = node[seg];
  }
  return node === undefined ? null : node;
}
function setIn(root, segs, value){
  let node = root;
  for (let i = 0; i < segs.length - 1; i++){
    if (typeof node[segs[i]] !== "object" || node[segs[i]] === null) node[segs[i]] = {};
    node = node[segs[i]];
  }
  if (value === null || value === undefined) delete node[segs[segs.length - 1]];
  else node[segs[segs.length - 1]] = value;
}
function applyPatch(body){
  // RTDB REST PATCH: slash keys in the body are paths; null deletes.
  for (const [key, value] of Object.entries(body)) setIn(store, splitPath(key), value);
}
const authTokens = { "tok-1": "uid-parent" };
let tokenSeq = 1;

async function mockFetch(url, opts){
  const u = new URL(url);
  if (u.hostname === "identitytoolkit.googleapis.com"){
    const body = JSON.parse((opts && opts.body) || "{}");
    if (u.pathname.endsWith("accounts:signUp") || u.pathname.endsWith("accounts:signInWithPassword")){
      if (body.email === "parent@test.dev" && body.password === "secret1"){
        return mkRes(200, JSON.stringify({ idToken: "tok-" + tokenSeq, refreshToken: "rt-" + tokenSeq, localId: "uid-parent", expiresIn: "3600" }));
      }
      return mkRes(400, JSON.stringify({ error: { message: "INVALID_LOGIN_CREDENTIALS" } }));
    }
    return mkRes(400, JSON.stringify({ error: { message: "UNKNOWN" } }));
  }
  if (u.hostname !== "guardpulse-laptop-sg-default-rtdb.asia-southeast1.firebasedatabase.app"){
    return mkRes(404, "{}");
  }
  const token = u.searchParams.get("auth") || "";
  if (!authTokens[token]) return mkRes(401, "null");
  const path = decodeURIComponent(u.pathname.replace(/\.json$/, ""));
  const method = ((opts && opts.method) || "GET").toUpperCase();
  if (method === "GET"){
    const node = getNode(path);
    return mkRes(200, JSON.stringify(node === null ? null : node));
  }
  if (method === "PUT"){
    const body = JSON.parse((opts && opts.body) || "null");
    setIn(store, splitPath(path), body);
    return mkRes(200, JSON.stringify(body));
  }
  if (method === "PATCH"){
    // Slash-keys in the BODY are paths (what the console's atomic writes rely on).
    const body = JSON.parse((opts && opts.body) || "{}");
    const isRoot = splitPath(path).length === 0;
    if (isRoot) applyPatch(body);
    else {
      const merged = JSON.parse(JSON.stringify(getNode(path) || {}));
      for (const [k, v] of Object.entries(body)) setIn(merged, splitPath(k), v);
      setIn(store, splitPath(path), merged);
    }
    return mkRes(200, "{}");
  }
  if (method === "DELETE"){
    setIn(store, splitPath(path), null);
    return mkRes(200, "null");
  }
  return mkRes(400, "{}");
  function mkRes(status, text){ return { ok: status < 400, status, text: async () => text, json: async () => JSON.parse(text) }; }
}

// ---------------------------------------------------------------- VM sandbox
const sandbox = {
  console,
  document: documentStub,
  localStorage: (() => { const m = {}; return { getItem: k => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); }, removeItem: k => { delete m[k]; } }; })(),
  navigator: { onLine: true, clipboard: null },
  location: { href: "http://localhost/" },
  setInterval() { return 0; },
  clearInterval() {},
  setTimeout, clearTimeout,
  fetch: mockFetch,
  crypto,
  AbortController,
  escape: s => String(s).replace(/[^\w@-]/g, c => "%" + c.charCodeAt(0).toString(16).toUpperCase()),
  unescape: s => decodeURIComponent(String(s)),
  URL, URLSearchParams, TextEncoder, TextDecoder,
  btoa: s => Buffer.from(s, "binary").toString("base64"),
  atob: s => Buffer.from(s, "base64").toString("binary"),
};
sandbox.window = sandbox;
sandbox.globalThis = sandbox;
vm.createContext(sandbox);
vm.runInContext(js, sandbox, { filename: "index.html<script>" });
const GP = sandbox.window.GP;
if (!GP) { console.error("FAIL: window.GP not exposed"); process.exit(1); }

// ---------------------------------------------------------------- tests
let pass = 0, fail = 0;
function check(name, cond){
  if (cond){ pass++; console.log("  ok  " + name); }
  else { fail++; console.log("  FAIL " + name); }
}

(async () => {
  console.log("auth:");
  await GP.auth.signIn("parent@test.dev", "secret1");
  check("sign-in returns the parent uid", GP.auth.uid === "uid-parent");

  console.log("pure functions:");
  const snap = {
    schemaVersion: 2,
    revisionId: "rev-1", updatedAt: 1, updatedBy: "uid-parent",
    apps: {}, modes: {}, activeMode: null,
    safeMode: { enabled: false, until: 0 },
    pin: { salt: "MDEyMzQ1Njc4OWFiY2RlZg", hash: "9UI-qC10YGAs3EmhUfE4S5cFc2P_Psp3Kwc3_nbS1gc", version: 2, algorithm: "PBKDF2WithHmacSHA256", iterations: 210000 },
  };
  const parsed = GP.parseControl(JSON.stringify(snap));
  check("parseControl valid snapshot", parsed.status === "valid" && parsed.snapshot.pin.iterations === 210000);
  check("parseControl rejects bad schema", GP.parseControl(JSON.stringify(Object.assign({}, snap, { schemaVersion: 3 }))).status === "invalid");

  const rKeep = GP.appRuleFor("k", "pkg", true);
  const rClear = GP.appRuleFor("k", "pkg", true, null);
  const rSet = GP.appRuleFor("k", "pkg", true, 30, 45);
  check("appRuleFor undefined limit omitted", !("dailyLimitMinutes" in rKeep) && !("sessionLimitMinutes" in rKeep));
  check("appRuleFor explicit null clears", rClear.dailyLimitMinutes === null);
  check("appRuleFor sets both limits", rSet.dailyLimitMinutes === 30 && rSet.sessionLimitMinutes === 45);

  check("freshness thresholds", GP.freshness(Date.now() - 10000, Date.now()) === "live"
    && GP.freshness(Date.now() - 60000, Date.now()) === "delayed"
    && GP.freshness(Date.now() - 120000, Date.now()) === "offline"
    && GP.freshness(0, Date.now()) === "offline");

  const appliedOld = { revisionId: "rev-old", status: "applied" };
  const desiredNew = { revisionId: "rev-new", kind: "appPolicy" };
  check("deriveSyncStatus WAITING", GP.deriveSyncStatus(true, "valid", 2, desiredNew, appliedOld, "live") === "WAITING");
  check("deriveSyncStatus OFFLINE_PENDING", GP.deriveSyncStatus(true, "valid", 2, desiredNew, appliedOld, "offline") === "OFFLINE_PENDING");
  check("deriveSyncStatus APPLIED", GP.deriveSyncStatus(true, "valid", 2, desiredNew, { revisionId: "rev-new", status: "applied" }, "live") === "APPLIED");
  check("deriveSyncStatus FAILED wins on matching failed ack", GP.deriveSyncStatus(true, "valid", 2, desiredNew, { revisionId: "rev-new", status: "failed" }, "live") === "FAILED");
  check("deriveSyncStatus INVALID blocks", GP.deriveSyncStatus(true, "invalid", 2, null, null, "live") === "FAILED");
  check("deriveSyncStatus TV_UPDATE_REQUIRED", GP.deriveSyncStatus(true, "valid", 1, null, null, "live") === "TV_UPDATE_REQUIRED");

  const wednesday = new Date("2026-09-16T15:00:00");
  const ws = GP.weekStartMs(wednesday.getTime());
  check("weekStartMs lands on Monday 00:00", new Date(ws).getDay() === 1 && new Date(ws).getHours() === 0);

  check("normalizeDomainUi lowercases", GP.normalizeDomainUi("YouTube.com") === "youtube.com");
  check("normalizeDomainUi strips scheme + slash", GP.normalizeDomainUi("https://youtube.com/") === "youtube.com");
  check("normalizeDomainUi keeps path", GP.normalizeDomainUi("youtube.com/shorts") === "youtube.com/shorts");
  check("normalizeDomainUi rejects non-http scheme", GP.normalizeDomainUi("ftp://x.com") === null);
  check("normalizeDomainUi rejects single label", GP.normalizeDomainUi("localhost") === null);
  check("normalizeDomainUi rejects empty label", GP.normalizeDomainUi("a..b.com") === null);

  check("parsePairPayload", (() => {
    const p = GP.parsePairPayload("guardpulse://pair?deviceId=dev123&secret=s3cr3t");
    return !!p && p.deviceId === "dev123" && p.secret === "s3cr3t";
  })());

  check("legacyMirrors appPolicy", (() => {
    const root = { apps: { key1: { packageKey: "key1", packageName: "p", manualBlocked: true } } };
    const m = GP.legacyMirrors("appPolicy", { apps: { key1: {} } }, root);
    return !!(m["policy/apps/key1"] && m["policy/apps/key1"].manualBlocked === true);
  })());
  check("legacyMirrors safeMode/pin", (() => {
    const root = { safeMode: { enabled: false, until: 0 }, pin: { salt: "s", hash: "h" } };
    const m1 = GP.legacyMirrors("safeMode", {}, root);
    const m2 = GP.legacyMirrors("pin", {}, root);
    return !!m1["security/safeMode"] && !!m2["security/pin"];
  })());
  check("legacyMirrors activeMode delete", GP.legacyMirrors("activeMode", { activeMode: null }, { activeMode: { modeId: "m1" } })["policy/activeMode"] === null);

  console.log("write pipeline (mock RTDB):");
  store["devices"] = { dev1: {} };
  const w1 = await GP.writeControl("dev1", { apps: { c2FtcGxlLmV4ZQ: { packageKey: "c2FtcGxlLmV4ZQ", packageName: "sample.exe", manualBlocked: true, dailyLimitMinutes: 30 } } }, "appPolicy", "sample.exe");
  check("writeControl ok", w1.ok === true);
  const ctrl = getNode("devices/dev1/control/v2");
  const desired = getNode("devices/dev1/sync/desired");
  const mirror = getNode("devices/dev1/policy/apps/c2FtcGxlLmV4ZQ");
  check("control/v2 stamped", !!(ctrl && ctrl.revisionId === w1.revisionId && ctrl.schemaVersion === 2 && ctrl.updatedBy === "uid-parent"));
  check("sync/desired written with SAME revisionId + kind", !!(desired && desired.revisionId === w1.revisionId && desired.kind === "appPolicy" && desired.target === "sample.exe" && desired.requestedBy === "uid-parent"));
  check("legacy apps mirror written", !!(mirror && mirror.manualBlocked === true && mirror.packageKey === "c2FtcGxlLmV4ZQ"));

  const w2 = await GP.writeControl("dev1", { safeMode: { enabled: true, until: Date.now() + 900000, startedAt: { ".sv": "timestamp" }, startedBy: "uid-parent" } }, "safeMode", "safeMode");
  const ctrl2 = getNode("devices/dev1/control/v2");
  check("safeMode enable carries startedAt + until", !!(w2.ok && ctrl2.safeMode.enabled === true && "startedAt" in ctrl2.safeMode && ctrl2.safeMode.until > 0));
  check("safeMode mirror written", !!getNode("devices/dev1/security/safeMode"));

  const w3 = await GP.writeControl("dev1", { budget: { dailyLimitMinutes: 120 } }, "budget", "budget");
  check("budget write ok", !!(w3.ok && getNode("devices/dev1/control/v2").budget && getNode("devices/dev1/control/v2").budget.dailyLimitMinutes === 120));

  // Clearing an app limit must actually clear (the audit's silent-failure bug).
  const w3b = await GP.writeControl("dev1", { apps: { c2FtcGxlLmV4ZQ: { packageKey: "c2FtcGxlLmV4ZQ", packageName: "sample.exe", manualBlocked: true, dailyLimitMinutes: null } } }, "appPolicy", "sample.exe");
  const cleared = getNode("devices/dev1/control/v2/apps/c2FtcGxlLmV4ZQ");
  check("app limit clear is explicit null (deletes field)", !!(w3b.ok && cleared && cleared.dailyLimitMinutes === undefined));

  // Custom-sites atomic write: blocked list + registry in ONE multi-path PATCH.
  const w4 = await GP.writeControl(
    "dev1",
    { customBlockedDomains: ["youtube.com", "example.com/shorts"] },
    "customBlockedDomains", "customBlockedDomains",
    { customSites: ["youtube.com", "example.com/shorts"] }
  );
  console.log("W4:", JSON.stringify(w4), "node:", JSON.stringify(getNode("devices/dev1/customSites")));
  check("customSites registry written atomically", !!(w4.ok
    && JSON.stringify(getNode("devices/dev1/customSites")) === JSON.stringify(["youtube.com", "example.com/shorts"])
    && getNode("devices/dev1/control/v2/customBlockedDomains").length === 2));

  await GP.sendCommand("dev1", "openSetup");
  const cmds = getNode("devices/dev1/commands");
  const cmd = cmds && Object.values(cmds)[0];
  check("openSetup command shape", !!(cmd && cmd.type === "openSetup" && cmd.status === "pending" && cmd.ttlMs === 60000 && cmd.requestedBy === "uid-parent"));

  (store.devices.dev1 = store.devices.dev1 || {}).unlockRequests = { req1: { status: "pending", packageName: "sample.exe", createdAt: 1, expiresAt: Date.now() + 600000 } };
  await GP.respondUnlock("dev1", "req1", "approve30");
  const req = getNode("devices/dev1/unlockRequests/req1");
  check("unlock approve 30min payload", !!(req && req.status === "approved" && req.approvalType === "timed" && req.approvalDurationMs === 1800000));

  const wp = await GP.setPin("dev1", "123456");
  const pinNode = getNode("devices/dev1/control/v2/pin");
  check("setPin v2 record at 210000 iters", !!(wp.ok && pinNode.version === 2 && pinNode.algorithm === "PBKDF2WithHmacSHA256" && pinNode.iterations === 210000));
  check("pin hash 43-char b64url, salt 22-char", /^[A-Za-z0-9_-]{43}$/.test(pinNode.hash) && /^[A-Za-z0-9_-]{22}$/.test(pinNode.salt));
  check("security/pin mirror written", !!getNode("devices/dev1/security/pin"));

  console.log("compose (mock RTDB):");
  const now = Date.now();
  (store.devices.dev1 = store.devices.dev1 || {}).sync = {
    runtime: { connected: true, protocolVersion: 2, sessionId: "s1" },
    applied: { revisionId: w4.revisionId, status: "applied", appliedAt: now },
  };
  store.users = store.users || {};
  store.users["uid-parent"] = { devices: { dev1: { deviceId: "dev1", label: "Kid Laptop", online: true, lastSeen: now - 10000, enforcementMode: "fallback", protectionHealthy: true } } };
  delete GP.devCache.dev1; // refreshState clears the per-device cache before every compose
  const composed = await GP.composeDeviceState("dev1");
  check("compose basic fields", composed.label === "Kid Laptop" && composed.online === true);
  console.log("COMPOSE1:", composed.syncState, JSON.stringify(composed.customSites), composed.syncDesiredRevisionId, composed.availability);
  check("compose sync state APPLIED when desired==applied", composed.syncState === "APPLIED" || composed.syncState === "IDLE");
  check("compose customSites carried", JSON.stringify(composed.customSites) === JSON.stringify(["youtube.com", "example.com/shorts"]));
  check("compose apps include default-locked windows bypass", composed.apps.some(a => a.packageName === "guardpulse.windows.taskmgr"));

  setIn(store, splitPath("devices/dev1/sync/desired"), { revisionId: "rev-console-9", kind: "appPolicy", target: "x", requestedAt: now, requestedBy: "uid-parent" });
  delete GP.devCache.dev1;
  const composed2 = await GP.composeDeviceState("dev1");
  console.log("COMPOSE2:", composed2.syncState, composed2.syncDesiredRevisionId);
  check("compose sync state WAITING when desired ahead + fresh", composed2.syncState === "WAITING");

  console.log("\n" + pass + " passed, " + fail + " failed");
  process.exit(fail ? 1 : 0);
})().catch(e => { console.error("HARNESS CRASH:", e); process.exit(1); });
