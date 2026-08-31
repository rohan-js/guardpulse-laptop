// Headless functional check of index.html's inline JS against a mock
// Firebase (auth REST + RTDB REST). Verifies the browser port end-to-end.
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
  visibilityState: "visible",
};

// ---------------------------------------------------------------- mock Firebase
const b64e = (s) => Buffer.from(s, "binary").toString("base64").replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const CHROME = b64e("chrome.exe");
const EXAMPLE = b64e("C:\\Program Files\\Example\\app.exe");
const INV2 = b64e("notepad.exe");
const NOW = Date.now();

function seedControl(activeMode) {
  return {
    schemaVersion: 2, revisionId: "rev-1", updatedAt: 1, updatedBy: "owner1",
    apps: { [CHROME]: { packageName: "chrome.exe", packageKey: CHROME, manualBlocked: false } },
    modes: {
      m1: { modeId: "m1", name: "Study", apps: { [CHROME]: { packageName: "chrome.exe", packageKey: CHROME, manualBlocked: true } } },
    },
    activeMode, safeMode: { enabled: false, until: 0 },
  };
}

const tree = {
  users: {
    owner1: { devices: { devA: { deviceId: "devA", label: "LAPTOP-TEST", online: true, lastSeen: NOW - 30000, platform: "windows", enforcementMode: "fallback", protectionHealthy: true } } },
  },
  devices: {
    devA: {
      apps: {
        [CHROME]: { packageName: "chrome.exe", label: "Google Chrome", blockable: true },
        [EXAMPLE]: { packageName: "C:\\Program Files\\Example\\app.exe", label: "Example App", blockable: true },
        [INV2]: { packageName: "notepad.exe", label: "Notepad", blockable: false, protectedReason: "System component" },
      },
      state: {
        apps: { [CHROME]: { packageName: "chrome.exe", usageMs: 9960000, lockBlocked: false, controlRevisionId: "rev-1" } },
        browser: { browser: "c:\\program files\\bravesoftware\\brave-browser\\application\\brave.exe", label: "Brave", activeTab: "Wikipedia", activeUrl: "https://wikipedia.org/", tabCount: 3, tabs: [{ title: "Wikipedia", url: "https://wikipedia.org/" }, { title: "GitHub" }, { title: "News" }], domainsToday: { "d2lraXBlZGlhLm9yZw": 65000, "Z2l0aHViLmNvbQ": 30000 }, updatedAt: NOW - 5000 },
      },
      sync: { applied: { revisionId: "rev-1", status: "applied", appliedAt: NOW - 30000, sessionId: "s1" } },
      unlockRequests: { r1: { requestId: "r1", packageName: "chrome.exe", reason: "askParent", status: "pending", createdAt: NOW - 120000, expiresAt: NOW + 480000 } },
      tamperEvents: { t1: { type: "accessibilityDisabled", message: "Accessibility service was disabled", createdAt: NOW - 3600000 } },
      control: { v2: seedControl(null) },
      commands: {},
      security: {},
    },
  },
};

function getAt(segs) {
  let n = tree;
  for (const s of segs) { if (n && typeof n === "object" && s in n) n = n[s]; else return undefined; }
  return n;
}
function setPath(segs, value) {
  if (!segs.length) throw new Error("root set not supported in mock");
  let n = tree;
  for (let i = 0; i < segs.length - 1; i++) {
    const s = segs[i];
    if (!n[s] || typeof n[s] !== "object") n[s] = {};
    n = n[s];
  }
  if (value === null) delete n[segs[segs.length - 1]];
  else n[segs[segs.length - 1]] = value;
}
function jsonResp(obj) { return { status: 200, ok: true, headers: {}, json: async () => obj, text: async () => JSON.stringify(obj) }; }
function textResp(t) { return { status: 200, ok: true, headers: {}, json: async () => JSON.parse(t), text: async () => t }; }
function emptyStream() { return { getReader() { return { read() { return Promise.resolve({ done: true, value: undefined }); } }; } }; }

async function mockFetch(url, init) {
  init = init || {};
  if (url.includes("identitytoolkit")) return jsonResp({ idToken: "tok", refreshToken: "rt", expiresIn: "3600", localId: "owner1" });
  if (url.includes("securetoken")) return jsonResp({ id_token: "tok", refresh_token: "rt", expires_in: "3600", user_id: "owner1" });
  if (url.includes("firebaseio.com")) {
    const accept = init.headers ? (init.headers["Accept"] || "") : "";
    if (accept === "text/event-stream") return { status: 200, ok: true, headers: {}, body: emptyStream() };
    const u = new URL(url);
    const pathStr = u.pathname.replace(/^\//, "").replace(/\.json$/, "");
    const segs = pathStr.split("/").map(decodeURIComponent).filter(Boolean);
    if (segs[0] === ".info" && segs[1] === "serverTimeOffset") return textResp("0");
    const method = init.method || "GET";
    const body = init.body ? JSON.parse(init.body) : null;
    if (method === "GET") {
      const v = getAt(segs);
      return textResp(v === undefined ? "null" : JSON.stringify(v));
    }
    if (method === "PUT") { setPath(segs, body); return textResp(JSON.stringify(body)); }
    if (method === "PATCH") {
      const target = getAt(segs) || {};
      for (const [k, v] of Object.entries(body || {})) { if (v === null) delete target[k]; else target[k] = v; }
      setPath(segs, target);
      return textResp(JSON.stringify(body));
    }
    return textResp("{}");
  }
  throw new Error("unexpected url " + url);
}

// ---------------------------------------------------------------- sandbox
const sandbox = {
  console, document: documentStub, window: {},
  setInterval, clearInterval, setTimeout, clearTimeout,
  Date, JSON, Math, Number, parseInt, parseFloat, String, Object, Array, RegExp, Promise,
  URL, TextEncoder, TextDecoder, crypto: globalThis.crypto, AbortController,
  btoa: (s) => Buffer.from(s, "binary").toString("base64"),
  atob: (s) => Buffer.from(s, "base64").toString("binary"),
  unescape, encodeURIComponent, decodeURIComponent, escape,
  fetch: mockFetch,
  localStorage: {
    _m: {},
    getItem(k) { return this._m[k] !== undefined ? this._m[k] : null; },
    setItem(k, v) { this._m[k] = String(v); },
    removeItem(k) { delete this._m[k]; },
  },
};
sandbox.window.document = documentStub;

let failures = 0;
function check(name, cond, extra) {
  if (cond) console.log("PASS  " + name);
  else { failures++; console.log("FAIL  " + name + (extra ? " :: " + extra : "")); }
}

vm.createContext(sandbox);
vm.runInContext(js, sandbox, { filename: "hosted-console.js" });
const run = (expr) => vm.runInContext(expr, sandbox);

(async () => {
  check("boot ran", true);
  check("no external urls beyond Firebase", !/https?:\/\//.test(html.replace(/"https:\/\/identitytoolkit[^"]*"/g, "").replace(/"https:\/\/securetoken[^"]*"/g, "").replace(/"https:\/\/guardpulse-laptop-control-default-rtdb\.firebaseio\.com"/g, "")));
  check("logo embedded", html.includes("data:image/png;base64,") && !html.includes("__GP_LOGO_URI__"));

  // --- auth
  const uid = await run("auth.signIn('p@example.com','secret123')");
  check("sign-in returns uid", uid === "owner1", uid);
  check("refresh token persisted", sandbox.localStorage.getItem("gp.refresh") === "rt");

  // --- compose (base state)
  const st = await run("composeDeviceState('devA')");
  check("compose: labels", st.label === "LAPTOP-TEST");
  const names = st.apps.map(a => a.label);
  check("compose: inventory merged (Example App + Notepad)", names.includes("Example App") && names.includes("Notepad"));
  check("compose: policy row (Google Chrome)", names.includes("Google Chrome"));
  check("compose: bypass default-locked (Task Manager blocked)", (st.apps.find(a => a.label === "Task Manager") || {}).blocked === true);
  check("compose: protected row (Notepad)", (st.apps.find(a => a.label === "Notepad") || {}).protectedReason === "System component");
  check("compose: chrome allowed (base)", (st.apps.find(a => a.packageName === "chrome.exe") || {}).blocked === false);
  check("compose: modes", st.modes.length === 1 && st.modes[0].name === "Study");
  check("compose: usage hms", st.usage.length === 1 && st.usage[0].ms === 9960000);
  check("compose: pending unlocks", st.pendingUnlocks.length === 1 && st.pendingUnlocks[0].label === "Google Chrome");
  check("compose: tamper events", st.tamperEvents.length === 1);
  check("compose: sync applied", st.syncStatus === "applied" && st.syncRevisionId === "rev-1");
  check("compose: controlRevisionId", st.controlRevisionId === "rev-1");
  check("compose: serverNow", typeof st.serverNow === "number");

  // --- browser now card (devices/{id}/state/browser)
  const stBr = await run("composeDeviceState('devA')");
  check("compose: browser node", !!stBr.browser && stBr.browser.tabCount === 3 && stBr.browser.label === "Brave" && stBr.browser.activeTab === "Wikipedia");
  run("SCOPE={kind:'device',deviceId:'devA'}; STATE=" + JSON.stringify(stBr) + "; render();");
  const brHtml = documentStub.getElementById("app").innerHTML;
  check("render: Browser now card", brHtml.includes("Browser now") && brHtml.includes(">Brave</span>") && brHtml.includes(">live</span>"));
  check("render: browser active tab", brHtml.includes(">Wikipedia</span>") && brHtml.includes("https://wikipedia.org/"));
  check("render: today by site (b64url-decoded domains)", brHtml.includes("Today by site") && brHtml.includes(">wikipedia.org</span>") && brHtml.includes(">github.com</span>"));
  check("render: tab list summary + active chip", brHtml.includes("3 tabs open") && brHtml.includes('<span class="chip">active</span>'));
  tree.devices.devA.state.browser.updatedAt = NOW - 600000;
  delete sandbox.window.GP.devCache.devA;
  const stStale = await run("composeDeviceState('devA')");
  run("SCOPE={kind:'device',deviceId:'devA'}; STATE=" + JSON.stringify(stStale) + "; render();");
  const staleHtml = documentStub.getElementById("app").innerHTML;
  check("render: stale pill + dimmed body", staleHtml.includes(">stale</span>") && staleHtml.includes('class="card-body muted"') && !staleHtml.includes(">live</span>"));
  tree.devices.devA.state.browser.updatedAt = NOW - 5000;
  delete sandbox.window.GP.devCache.devA;

  // --- phone parity: the TV's runtime lockBlocked wins over the desired rule (like the phone)
  // desired locked, runtime unlocked -> must show Allowed (the user's Edge case)
  tree.devices.devA.control.v2.apps[CHROME].manualBlocked = true;
  delete sandbox.window.GP.devCache.devA;
  const stDiv = await run("composeDeviceState('devA')");
  run("SCOPE={kind:'device',deviceId:'devA'}; STATE=" + JSON.stringify(stDiv) + "; render();");
  const divHtml = documentStub.getElementById("app").innerHTML;
  const chromeRow = divHtml.indexOf(">Google Chrome</span></div></td><td><span class=\"pill");
  const chromeRowHtml = chromeRow >= 0 ? divHtml.slice(chromeRow, chromeRow + 300) : "";
  check("parity: desired lock + runtime unlocked -> Allowed", chromeRow >= 0 && chromeRowHtml.includes("Allowed") && !chromeRowHtml.includes("Locked by parent"));
  check("parity: usage kept for zero-ms state entries", (stDiv.usage || []).length === 1 && stDiv.usage[0].lockBlocked === false);
  tree.devices.devA.control.v2.apps[CHROME].manualBlocked = false;
  // desired unlocked, runtime locked (e.g. daily limit hit) -> must show Locked
  tree.devices.devA.state.apps[CHROME].lockBlocked = true;
  delete sandbox.window.GP.devCache.devA;
  const stDiv2 = await run("composeDeviceState('devA')");
  run("SCOPE={kind:'device',deviceId:'devA'}; STATE=" + JSON.stringify(stDiv2) + "; render();");
  const divHtml2 = documentStub.getElementById("app").innerHTML;
  check("parity: runtime lock wins -> Locked", divHtml2.includes("Locked"));
  tree.devices.devA.state.apps[CHROME].lockBlocked = false;
  delete sandbox.window.GP.devCache.devA;

  // --- active-mode override (phone parity: mode apps replace top-level)
  tree.devices.devA.control.v2.activeMode = { modeId: "m1", modeName: "Study" };
  delete sandbox.window.GP.devCache.devA;
  const st2 = await run("composeDeviceState('devA')");
  check("active mode: chrome becomes locked", (st2.apps.find(a => a.packageName === "chrome.exe") || {}).blocked === true);
  check("active mode: activeModeId", st2.activeModeId === "m1" && st2.activeModeName === "Study");
  tree.devices.devA.control.v2.activeMode = null;
  delete sandbox.window.GP.devCache.devA;

  // --- control write: block chrome (with packageKey, as the rules require)
  const w1 = await run(`writeControl('devA', {apps:{[${JSON.stringify(CHROME)}]:{packageKey:${JSON.stringify(CHROME)},packageName:'chrome.exe',manualBlocked:true}}})`);
  check("write ok", w1.ok === true, w1.error);
  check("write returns revision", !!w1.revisionId && w1.revisionId !== "rev-1");
  check("write stored + merged", tree.devices.devA.control.v2.revisionId === w1.revisionId && tree.devices.devA.control.v2.apps[CHROME].manualBlocked === true);
  check("write carries packageKey", tree.devices.devA.control.v2.apps[CHROME].packageKey === CHROME);
  check("appRule builds rules-compliant patch", JSON.stringify(run("appRuleFor('k1','pkg.exe',true)")) === JSON.stringify({ packageKey: "k1", packageName: "pkg.exe", manualBlocked: true }));
  check("write stamped owner", tree.devices.devA.control.v2.updatedBy === "owner1");

  // --- silent-drop detection
  const before = tree.devices.devA.control.v2.revisionId;
  const w2 = await run(`writeControl('devA', {activeMode:{modeId:'nope',modeName:'X'}})`);
  check("silent drop rejected", w2.ok === false && String(w2.error).includes("nope"), w2.error);
  check("silent drop wrote nothing", tree.devices.devA.control.v2.revisionId === before);

  // --- unlock approval
  await run("respondUnlock('devA','r1','approveOneVisit')");
  check("unlock approved", tree.devices.devA.unlockRequests.r1.status === "approved" && tree.devices.devA.unlockRequests.r1.approvalType === "oneVisit");

  // --- commands
  await run("sendCommand('devA','rescanApps',null)");
  const cmdId = Object.keys(tree.devices.devA.commands)[0];
  check("command created pending", !!cmdId && tree.devices.devA.commands[cmdId].type === "rescanApps" && tree.devices.devA.commands[cmdId].status === "pending" && tree.devices.devA.commands[cmdId].requestedBy === "owner1");

  // --- render + waiting gating (UI)
  run("SCOPE={kind:'device',deviceId:'devA'}; STATE=" + JSON.stringify(st) + "; render();");
  const appHtml = documentStub.getElementById("app").innerHTML;
  check("render: apps table", appHtml.includes("Google Chrome") && appHtml.includes("Example App"));
  check("render: usage chip", appHtml.includes("2h 46m"));
  check("render: pending requests", appHtml.includes("Pending requests") && appHtml.includes("expires in"));
  check("render: allow switch", appHtml.includes('onchange="toggleBlock(') && appHtml.includes('class="track"'));
  check("render: no per-app temp unlock", !appHtml.includes('id="ut_') && !appHtml.includes(">Unlock</button>"));
  run("noteWritten('rev-9')");
  check("pendingStatus waiting", run("pendingStatus()") === "waiting");
  run("STATE.syncStatus='applied'; STATE.syncRevisionId='rev-9';");
  check("pendingStatus clears on ack", run("pendingStatus()") === "idle");
  check("fmtMs", run("fmtMs(9960000)") === "2h 46m");

  console.log(failures === 0 ? "\nALL HARNESS CHECKS PASSED" : "\n" + failures + " CHECKS FAILED");
  process.exit(failures === 0 ? 0 : 1);
})().catch(e => { console.error("HARNESS ERROR:", e); process.exit(2); });
