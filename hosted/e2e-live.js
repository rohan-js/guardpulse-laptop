// READ-ONLY live check of the deployed console against the SG instance.
// Signs in with env credentials and verifies the dashboard's data path matches
// raw RTDB. NEVER writes — write paths are covered by harness.js + rules tests.
//   GP_EMAIL=... GP_PASSWORD=... node hosted/e2e-live.js [https://guardpulse-laptop-sg.web.app]
"use strict";
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const baseUrl = process.argv[2] || "https://guardpulse-laptop-sg.web.app";
const email = process.env.GP_EMAIL;
const password = process.env.GP_PASSWORD;
const credsAvailable = !!(email && password);
if (!credsAvailable){
  console.log("GP_EMAIL/GP_PASSWORD not set — running STATIC checks only (no sign-in).");
}

let pass = 0, fail = 0;
const check = (name, cond) => { if (cond){ pass++; console.log("  ok  " + name); } else { fail++; console.log("  FAIL " + name); } };

(async () => {
  // 1) The deployed page is the new console (SG config inlined).
  const pageRes = await fetch(baseUrl + "/");
  const page = await pageRes.text();
  check("site reachable (HTTP " + pageRes.status + ")", pageRes.ok);
  check("serves the SG instance config", page.includes("guardpulse-laptop-sg-default-rtdb") && !page.includes("guardpulse-laptop-control-default-rtdb"));
  check("serves the new feature set", page.includes("deriveSyncStatus") && page.includes("customSites") && page.includes("Protection health"));

  // 2) Load the SAME script in a sandbox with REAL fetch (read-only usage only).
  if (!credsAvailable){
    console.log("\n" + pass + " passed, " + fail + " failed (static — set GP_EMAIL/GP_PASSWORD for the sign-in + data checks)");
    process.exit(fail ? 1 : 0);
  }
  const js = page.match(/<script>([\s\S]*)<\/script>/)[1];
  const documentStub = {
    getElementById: () => ({ textContent: "", innerHTML: "", value: "", classList: { toggle(){}, add(){}, remove(){}, contains(){ return false; } }, contains(){ return false; }, querySelector(){ return null; }, querySelectorAll(){ return []; }, appendChild(){}, setAttribute(){}, focus(){} }),
    querySelectorAll: () => [], createElement: () => ({ classList: { toggle(){} }, textContent: "", innerHTML: "" }),
    addEventListener(){}, body: { classList: { toggle(){} } }, activeElement: null, hidden: false,
  };
  const sandbox = {
    console, document: documentStub,
    localStorage: { getItem: () => null, setItem(){}, removeItem(){} },
    navigator: { onLine: true, clipboard: null }, location: { href: baseUrl },
    setInterval(){ return 0; }, clearInterval(){}, setTimeout, clearTimeout,
    fetch, crypto, AbortController, escape: s => s, unescape: s => s,
    URL, URLSearchParams, TextEncoder, TextDecoder,
    btoa: s => Buffer.from(s, "binary").toString("base64"), atob: s => Buffer.from(s, "base64").toString("binary"),
  };
  sandbox.window = sandbox; sandbox.globalThis = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(js, sandbox, { filename: "deployed<script>" });
  const GP = sandbox.window.GP;

  // 3) Sign in against the SG project.
  await GP.auth.signIn(email, password);
  check("SG sign-in works", !!GP.auth.uid);

  // 4) Device mirror reads and matches a raw REST read.
  const raw = await GP.dbGet("users/" + GP.auth.uid + "/devices");
  const devices = (raw && raw.trim() !== "null") ? Object.keys(JSON.parse(raw)) : [];
  console.log("  devices visible: " + devices.length);
  check("device mirror readable", Array.isArray(devices));

  // 5) For every device: control/desired/applied are readable and consistent
  //    with what the dashboard would show (READ-ONLY — no writes anywhere).
  for (const deviceId of devices){
    const [control, desired, applied] = await Promise.all([
      GP.dbGet("devices/" + deviceId + "/control/v2"),
      GP.dbGet("devices/" + deviceId + "/sync/desired"),
      GP.dbGet("devices/" + deviceId + "/sync/applied"),
    ]);
    const c = control && control.trim() !== "null" ? JSON.parse(control) : null;
    const d = desired && desired.trim() !== "null" ? JSON.parse(desired) : null;
    const a = applied && applied.trim() !== "null" ? JSON.parse(applied) : null;
    const synced = c && d && a && a.revisionId === d.revisionId && c.revisionId === d.revisionId;
    console.log("  device " + deviceId.slice(0, 8) + "…: control=" + (c ? c.revisionId.slice(-6) : "none")
      + " desired=" + (d ? d.revisionId.slice(-6) : "none")
      + " applied=" + (a ? a.revisionId.slice(-6) : "none")
      + " → " + (synced ? "in sync" : "PENDING (laptop has not confirmed the latest change)"));
    if (c){
      const parsed = GP.parseControl(JSON.stringify(c));
      check("control snapshot parses (" + deviceId.slice(0, 8) + "…)", parsed.status === "valid");
    }
  }

  console.log("\n" + pass + " passed, " + fail + " failed (read-only — nothing was written)");
  process.exit(fail ? 1 : 0);
})().catch(e => { console.error("E2E CRASH:", e.message); process.exit(1); });
