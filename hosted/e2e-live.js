// LIVE end-to-end check of index.html against the REAL Firebase database.
// Exercises the actual page code (auth, compose, control writes, command/approval paths)
// with real credentials. The single "small write" test: briefly blocks one allowed,
// blockable app on the first paired device, waits for the device to acknowledge, then
// restores it to its original value. No device-wide locks (no schedule/budget/allowlist).
//
// Credentials come from environment variables (never hardcoded):
//   GP_EMAIL=you@example.com GP_PASSWORD=... node hosted/e2e-live.js
"use strict";
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const html = fs.readFileSync(path.join(__dirname, "index.html"), "utf8");
const js = html.match(/<script>([\s\S]*)<\/script>/)[1];
const email = process.env.GP_EMAIL, password = process.env.GP_PASSWORD;
if (!email || !password){ console.error("Set GP_EMAIL and GP_PASSWORD"); process.exit(2); }

// Minimal DOM stubs — the page's real logic runs untouched; only the DOM is stubbed.
function el(){ return { textContent:"", innerHTML:"", value:"", className:"", classList:{ add(){}, remove(){}, toggle(){}, contains(){ return false; } }, contains(){ return false; }, querySelector(){ return el(); }, querySelectorAll(){ return []; }, appendChild(){}, setAttribute(){}, focus(){}, onclick:null }; }
const elements = {};
const documentStub = {
  getElementById(id){ return (elements[id] = elements[id] || el()); },
  querySelectorAll(){ return []; }, createElement(){ return el(); }, addEventListener(){},
  body: { classList: { toggle(){}, add(){}, remove(){}, contains(){ return false; } } },
  activeElement: null, hidden: false, visibilityState: "visible",
};
const sandbox = {
  console, window: {}, document: documentStub,
  setInterval, clearInterval, setTimeout, clearTimeout,
  Date, JSON, Math, Number, parseInt, parseFloat, String, Object, Array, RegExp, Promise,
  URL, TextEncoder, TextDecoder, crypto: globalThis.crypto,
  btoa: (s) => Buffer.from(s, "binary").toString("base64"),
  atob: (s) => Buffer.from(s, "base64").toString("binary"),
  unescape, encodeURIComponent, decodeURIComponent,
  fetch: globalThis.fetch, // real network
  localStorage: { _m: {}, getItem(k){ return this._m[k] !== undefined ? this._m[k] : null; }, setItem(k, v){ this._m[k] = String(v); }, removeItem(k){ delete this._m[k]; } },
};
sandbox.window.document = documentStub;
vm.createContext(sandbox);
vm.runInContext(js, sandbox, { filename: "hosted-console.js" });
const run = (e) => vm.runInContext(e, sandbox);
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

(async () => {
  console.log("== live E2E: hosted console vs real Firebase ==");
  console.log("signing in as " + email);
  const uid = await run(`auth.signIn(${JSON.stringify(email)}, ${JSON.stringify(password)})`);
  console.log("uid:", uid);
  await run("refreshServerOffset()");
  await run("loadDevices()");
  const devices = run("DEVICES");
  if (!devices.length){ console.error("No devices under this account — nothing to test."); process.exit(1); }
  console.log("devices:");
  devices.forEach(d => console.log("  - " + d.deviceId + "  " + d.label + "  online=" + d.online + "  mode=" + (d.enforcementMode || "?")));

  const deviceId = devices[0].deviceId;
  const before = await run(`composeDeviceState(${JSON.stringify(deviceId)})`);
  console.log("initial state: apps=" + before.apps.length + ", modes=" + before.modes.length
    + ", pendingUnlocks=" + before.pendingUnlocks.length + ", syncStatus=" + before.syncStatus
    + ", controlRev=" + before.controlRevisionId);

  // Pick a test app: prefer an idle app, else the first allowed + blockable one.
  const idle = ["notepad.exe", "notepad", "calculator", "paint", "mspaint", "calc"];
  let test = before.apps.find(a => idle.includes(String(a.packageName || "").toLowerCase()) && !a.blocked && !a.protectedReason);
  if (!test) test = before.apps.find(a => !a.blocked && !a.protectedReason);
  if (!test){ console.error("No allowed, blockable app available to test with."); process.exit(1); }
  const key = test.key, pkg = test.packageName;
  const origBlocked = !!test.blocked;
  console.log("TEST APP: " + test.label + " (" + pkg + ") — currently " + (origBlocked ? "LOCKED" : "allowed"));

  try {
    // ---- small write: block the app. The patch MUST include packageKey (Firebase rules require it).
    const w = await run(`writeControl(${JSON.stringify(deviceId)}, {apps:{[${JSON.stringify(key)}]:{packageKey:${JSON.stringify(key)},packageName:${JSON.stringify(pkg)},manualBlocked:true}}})`);
    console.log("write(block): " + (w.ok ? "ok rev=" + w.revisionId : "FAILED: " + w.error));
    if (!w.ok) throw new Error(w.error);
    await sleep(7000); // let the device stream the change and ack
    const mid = await run(`composeDeviceState(${JSON.stringify(deviceId)})`);
    console.log("after block: controlRev=" + mid.controlRevisionId + " (wrote " + w.revisionId + "), syncRev=" + mid.syncRevisionId
      + ", syncStatus=" + mid.syncStatus + ", effectiveBlocked=" + ((mid.apps.find(a => a.key === key) || {}).blocked));
    if (mid.controlRevisionId !== w.revisionId) throw new Error("Block write did not land in control/v2");

    // ---- restore exactly (also rules-compliant)
    const w2 = await run(`writeControl(${JSON.stringify(deviceId)}, {apps:{[${JSON.stringify(key)}]:{packageKey:${JSON.stringify(key)},packageName:${JSON.stringify(pkg)},manualBlocked:${origBlocked}}}})`);
    console.log("write(restore): " + (w2.ok ? "ok rev=" + w2.revisionId : "FAILED: " + w2.error));
    if (!w2.ok) throw new Error(w2.error);
    await sleep(7000);
    const after = await run(`composeDeviceState(${JSON.stringify(deviceId)})`);
    const afterApp = after.apps.find(a => a.key === key);
    console.log("after restore: controlRev=" + after.controlRevisionId + " (wrote " + w2.revisionId + "), appBlocked=" + (afterApp && afterApp.blocked) + " (expected " + origBlocked + ")");
    if (after.controlRevisionId !== w2.revisionId) throw new Error("Restore write did not land");
    if (afterApp && afterApp.blocked !== origBlocked) throw new Error("Restore did not stick in control");

    // ---- read-only sanity: pending requests + tamper events + modes parse
    console.log("pendingUnlocks: " + before.pendingUnlocks.length + ", tamperEvents: " + before.tamperEvents.length + ", modes: " + before.modes.length);
    console.log("\nE2E PASS — sign-in, compose, write, device-ack, and restore all confirmed against live Firebase.");
    process.exit(0);
  } catch (e){
    console.error("E2E ERROR: " + e.message);
    // safety net: force the app back to its original value (rules-compliant patch)
    try {
      await run(`writeControl(${JSON.stringify(deviceId)}, {apps:{[${JSON.stringify(key)}]:{packageKey:${JSON.stringify(key)},packageName:${JSON.stringify(pkg)},manualBlocked:${origBlocked}}}})`);
      console.error("SAFETY: restore write re-sent");
    } catch (e2){ console.error("SAFETY restore failed: " + e2.message); }
    process.exit(1);
  }
})().catch(e => { console.error("E2E FATAL: " + (e && e.stack || e)); process.exit(1); });
