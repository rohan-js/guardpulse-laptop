// Headless functional check of dashboard.html's inline JS (no browser needed):
// executes the boot path and render() against a stubbed DOM + mock state.
// Run:  node windows/tools/dashboard-tests/dashboard-harness.js
"use strict";
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const html = fs.readFileSync(
  path.join(__dirname, "..", "..", "src", "GuardPulse.Agent.Session", "Dashboard", "dashboard.html"),
  "utf8");
const js = html.match(/<script>([\s\S]*)<\/script>/)[1];

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
  visibilityState: "visible",
};

const state = {
  deviceId: "devA", label: "LAPTOP-TEST", online: true, thisDevice: true, paired: true, pinConfigured: true,
  enforcementMode: "fallback", protectionHealthy: true, lastSeen: 1700000000000,
  apps: [
    { key: "Y2hyb21lLmV4ZQ", packageName: "chrome.exe", label: "Google Chrome", blocked: true, dailyLimitMinutes: null },
    { key: "YzpcZ2FtZXNccm9ibG94", packageName: "c:\\games\\roblox\\player.exe", label: "Roblox Player", blocked: false, dailyLimitMinutes: 45 },
    { key: "ZXhwbG9yZXI", packageName: "c:\\windows\\explorer.exe", label: "Windows Explorer", blocked: false,
      dailyLimitMinutes: null, blockable: false, protectedReason: "System component" },
  ],
  inventory: [{ key: "bm90ZXBhZC5leGU", label: "Notepad", packageName: "notepad.exe" }],
  modes: [
    { modeId: "m_1", name: "Jayaraj", createdAt: 1, updatedAt: 2, appCount: 1,
      apps: [{ key: "Y2hyb21lLmV4ZQ", packageName: "chrome.exe", label: "Google Chrome", blocked: true, dailyLimitMinutes: 30 }] },
  ],
  activeModeId: null, activeModeName: null,
  safeMode: { enabled: false, until: 0, startedAt: null },
  budgetMinutes: 120, allowlistEnabled: false, customDomains: ["youtube.com/shorts"],
  schedule: null, contentFilter: { social: true, gambling: false, adult: false, gaming: false },
  usage: [{ key: "Y2hyb21lLmV4ZQ", label: "Google Chrome", minutes: 166, ms: 9960000, lockBlocked: true }],
  serverNow: Date.now(), controlRevisionId: "rev-1",
  syncStatus: "applied", syncAppliedAt: Date.now() - 30000, syncRevisionId: "rev-1",
  pendingUnlocks: [{ requestId: "r1", packageName: "Roblox", label: "Roblox Player", reason: "askParent",
    createdAt: Date.now() - 120000, expiresAt: Date.now() + 480000 }],
  tamperEvents: [{ type: "accessibilityDisabled", message: "Accessibility service was disabled", createdAt: Date.now() - 3600000 }],
};

// Browser now card payload (the agent adds s.browser when it can see the active browser)
state.browser = {
  browser: "c:\\program files\\bravesoftware\\brave-browser\\application\\brave.exe",
  label: "Brave", activeTab: "Wikipedia", activeUrl: "https://wikipedia.org/", tabCount: 3,
  tabs: [{ title: "Wikipedia", url: "https://wikipedia.org/" }, { title: "GitHub" }, { title: "News" }],
  domainsToday: { "d2lraXBlZGlhLm9yZw": 65000, "Z2l0aHViLmNvbQ": 30000 }, // b64url keys: RTDB keys cannot contain '.'
  updatedAt: state.serverNow - 5000,
};

const sandbox = {
  console, document: documentStub, window: {},
  setInterval, clearInterval, setTimeout, clearTimeout,
  Date, JSON, Math, Number, parseInt, String, Object, Array, RegExp, Promise,
  btoa: (s) => Buffer.from(s, "binary").toString("base64"),
  atob: (s) => Buffer.from(s, "base64").toString("binary"),
  unescape, encodeURIComponent, decodeURIComponent,
  fetch: async () => ({ status: 200, json: async () => ({}) }),
  EventSource: class { addEventListener() {} close() {} },
};
sandbox.window.document = documentStub;

let failures = 0;
function check(name, cond, extra) {
  if (cond) console.log("PASS  " + name);
  else { failures++; console.log("FAIL  " + name + (extra ? " :: " + extra : "")); }
}

vm.createContext(sandbox);
vm.runInContext(js, sandbox, { filename: "dashboard.js" });

(async () => {
  check("boot ran", true);

  // --- static-file hygiene (design-port guarantees)
  check("no external URLs", !/https?:\/\//.test(html), (html.match(/https?:\/\/[^\s"']+/g) || []).slice(0, 3).join(","));
  check("logo embedded as data URI", html.includes('data:image/png;base64,') && !html.includes("__GP_LOGO_URI__"));
  check("no tailwind/font CDNs", !/cdn\.tailwindcss|fonts\.googleapis|googleusercontent/.test(html));
  check("hidden rule intact", /\.hidden\s*{\s*display:\s*none\s*!important/.test(html));
  check("busy gating intact", /body\.busy #app button/.test(html) && /button\[disabled\]/.test(html));

  sandbox.__state = state;
  const run = (expr) => vm.runInContext(expr, sandbox);

  // --- local scope render
  run("SCOPE = {kind:'local'}; STATE = __state; render();");
  const appHtml = documentStub.getElementById("app").innerHTML;
  check("render: apps table", appHtml.includes("Google Chrome") && appHtml.includes("Roblox Player"));
  check("render: usage chip h/m/s", appHtml.includes("2h 46m"));
  check("render: modes summary", appHtml.includes("Jayaraj") && appHtml.includes("1 locked"));
  check("render: pending requests", appHtml.includes("Pending requests") && appHtml.includes("expires in"));
  check("render: tamper events", appHtml.includes("accessibilityDisabled"));
  check("render: safe mode presets", appHtml.includes("15 minutes"));
  check("render: locked pill", appHtml.includes("Locked by parent"));
  // phone parity: the TV's runtime lockBlocked wins over the desired rule (like the phone)
  state.usage[0].lockBlocked = false; // TV is NOT enforcing the lock
  run("SCOPE = {kind:'local'}; STATE = __state; render();");
  const appHtmlParity = documentStub.getElementById("app").innerHTML;
  check("parity: desired lock + runtime unlocked -> Allowed", appHtmlParity.includes("Allowed") && !appHtmlParity.includes("Locked by parent"));
  state.usage[0].lockBlocked = true; // restore
  check("render: status card", appHtml.includes("Device status") && appHtml.includes("Enforcement"));
  check("render: console grid wrappers", appHtml.includes('class="console-grid"') && appHtml.includes('cg-rail') && appHtml.includes('cg-apps') && appHtml.includes('cg-modes') && appHtml.includes('cg-rules'));
  check("render: card-head pattern", appHtml.includes('class="card-head"'));
  check("render: pills carry dots", appHtml.includes('<span class="dot"></span>'));
  check("render: protected pill + disabled switch", appHtml.includes(">Protected</span>")
    && appHtml.includes(' disabled onchange="toggleBlock(') && appHtml.includes("Windows Explorer"));
  check("render: no inventory dropdown (apps merged)", !appHtml.includes("addSel"));
  check("render: no raw backslash in onclick", !appHtml.includes("onclick=\"toggleBlock('c:"));
  check("render: inline SVG icons used", appHtml.includes('<svg class="icon"'));

  // --- browser now card
  check("render: browser card", appHtml.includes("Browser now") && appHtml.includes("Brave"));
  check("render: browser rows", appHtml.includes("Wikipedia") && appHtml.includes("https://wikipedia.org/") && appHtml.includes("3 tabs open"));
  check("render: browser today by site", appHtml.includes("Today by site") && appHtml.includes("wikipedia.org") && appHtml.includes("1m 5s"));
  check("render: browser live pill", appHtml.includes('>live</span>') && !appHtml.includes('>stale</span>'));
  // staleness: updatedAt 10 min behind serverNow -> warn "stale" pill, card still rendered
  state.browser.updatedAt = state.serverNow - 600000;
  run("SCOPE = {kind:'local'}; STATE = __state; render();");
  const appHtmlStale = documentStub.getElementById("app").innerHTML;
  check("render: browser stale pill", appHtmlStale.includes('>stale</span>') && appHtmlStale.includes("Browser now") && appHtmlStale.includes("Wikipedia"));
  state.browser.updatedAt = state.serverNow - 5000; // restore

  // --- key-only resolution with Windows-path keys
  check("pkgOf backslash preserved", run("pkgOf('YzpcZ2FtZXNccm9ibG94')") === "c:\\games\\roblox\\player.exe");
  check("escapeAttr keeps backslashes", run("escapeAttr(\"c:\\\\a\\\\b'ex\\\"\")").includes("\\\\a"));
  check("b64urlEncode", run("b64urlEncode('notepad.exe')") === "bm90ZXBhZC5leGU");

  // --- diff-aware re-render
  const k1 = run("stateKey()");
  state.serverNow += 5000;
  check("stateKey ignores serverNow", k1 === run("stateKey()"));

  // --- mode editor expansion
  run("window.__modeOpen='m_1'; render();");
  const modeHtml = documentStub.getElementById("app").innerHTML;
  check("mode editor rows", modeHtml.includes("Mode limit (min)") && modeHtml.includes("Add app to mode"));
  check("mode editor scroll wrapper", modeHtml.includes('<div class="modebody"><div class="table-wrap">'));
  check("mode editor spans full row", modeHtml.includes('cg-modes span-full'));
  run("window.__modeOpen=''; render();");
  check("modes card normal width when closed", !documentStub.getElementById("app").innerHTML.includes('cg-modes span-full'));

  // --- waiting-for-device gating lifecycle (device scope)
  run("SCOPE = {kind:'device', deviceId:'devA'}; STATE.syncStatus=null; STATE.syncRevisionId=null;");
  run("noteWritten('rev-2')");
  check("pendingStatus waiting", run("pendingStatus()") === "waiting");
  run("STATE.syncStatus='applied'; STATE.syncRevisionId='rev-2';");
  const st2 = run("pendingStatus()");
  check("pendingStatus clears on ack", st2 === "idle" && run("pendingRevision") === null);

  // --- phone parity: remote (device) scope has NO per-app temporary Unlock control,
  // and the Allow/Lock control is a SWITCH (checked = allowed), like the phone.
  run("render();");
  const devHtml = documentStub.getElementById("app").innerHTML;
  check("device scope: no per-app unlock", !devHtml.includes('>Unlock</button>') && !devHtml.includes('id="ut_'));
  check("device scope: reset present", devHtml.includes('>Reset</button>'));
  check("device scope: allow switch", devHtml.includes('onchange="toggleBlock(') && devHtml.includes('class="track"')
    && !devHtml.includes('>Block</button>') && !devHtml.includes('>Allow</button>'));
  run("SCOPE = {kind:'local'}; render();");
  check("local scope: unlock kept", documentStub.getElementById("app").innerHTML.includes('>Unlock</button>'));

  // --- formatting
  check("fmtMs 2h46m", run("fmtMs(9960000)") === "2h 46m");
  check("fmtMs 45s", run("fmtMs(45000)") === "45s");

  console.log(failures === 0 ? "\nALL CHECKS PASSED" : "\n" + failures + " CHECKS FAILED");
  process.exit(failures === 0 ? 0 : 1);
})().catch(e => { console.error("HARNESS ERROR:", e); process.exit(2); });
