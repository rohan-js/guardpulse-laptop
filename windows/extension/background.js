/**
 * GuardPulse Site Guard — force-installed companion of the GuardPulse laptop agent.
 *
 * Pulls the current block rules from the agent's loopback endpoint and enforces them
 * in real time: blocked navigations are redirected to the local blocked page, SPA
 * history.pushState jumps (which never trigger network rules) are caught by the tab
 * watcher, and tabs sitting on the block page are sent BACK when the parent removes
 * the site. No keystroke injection, no page scripts — only tab-level navigation calls.
 */

const RULES_URL = "http://127.0.0.1:37846/rules";
const FETCH_ALARM = "gp-fetch";
const REFRESH_ALARM_MS = 15;
const TAB_SETTLE_MS = 400;

let rules = { domains: [], paths: [], blockPageUrl: "" };
let hostSet = new Set();
let pathRules = []; // [{host, prefix}]
let lastApplied = ""; // JSON hash of applied DNR rule ids
let dnrCounter = 1;

chrome.alarms.create(FETCH_ALARM, { periodInMinutes: REFRESH_ALARM_MS / 60 });
fetchRules();
chrome.alarms.onAlarm.addListener(a => { if (a.name === FETCH_ALARM) fetchRules(); });
chrome.runtime.onStartup.addListener(fetchRules);
chrome.tabs.onUpdated.addListener((tabId, info, tab) => {
  if (info.status === "committed" || info.url !== undefined) enforceTab(tabId, tab);
});

async function fetchRules() {
  try {
    const res = await fetch(RULES_URL, { cache: "no-store" });
    if (!res.ok) return;
    const next = await res.json();
    rules = {
      domains: Array.isArray(next.domains) ? next.domains : [],
      paths: Array.isArray(next.paths) ? next.paths : [],
      blockPageUrl: typeof next.blockPageUrl === "string" ? next.blockPageUrl : "",
    };
    rebuildIndexes();
    await applyNetworkRules();
    sweepAllTabs(); // a site may have been blocked or unblocked while we waited
  } catch (e) {
    /* agent down: keep last rules */
  }
}

function rebuildIndexes() {
  hostSet = new Set(rules.domains.map(d => d.toLowerCase()));
  pathRules = (rules.paths || []).map(p => ({
    host: String(p.host || "").toLowerCase(),
    prefix: String(p.prefix || "/"),
  }));
}

function isBlockedUrl(rawUrl) {
  if (!rawUrl || !/^https?:/i.test(rawUrl)) return null;
  let host, path;
  try {
    const u = new URL(rawUrl);
    host = u.hostname.toLowerCase();
    path = u.pathname || "/";
  } catch {
    return null;
  }

  const matched = host => `host:${host}`;
  // Walk host and its parent domains: a youtube.com rule covers music.youtube.com.
  let cur = host;
  while (cur) {
    if (hostSet.has(cur)) return matched(cur);
    const dot = cur.indexOf(".");
    if (dot < 0 || dot === cur.length - 1) break;
    cur = cur.slice(dot + 1);
  }

  for (const rule of pathRules) {
    let cur2 = host;
    while (cur2) {
      if (cur2 === rule.host && pathStartsWith(path, rule.prefix)) {
        return `path:${cur2}${rule.prefix}`;
      }
      const dot = cur2.indexOf(".");
      if (dot < 0 || dot === cur2.length - 1) break;
      cur2 = cur2.slice(dot + 1);
    }
  }
  return null;
}

function pathStartsWith(path, prefix) {
  const p = prefix.endsWith("/") && prefix.length > 1 ? prefix.slice(0, -1) : prefix;
  if (p === "" || p === "/") return true;
  return path === p || path.startsWith(p.endsWith("/") ? p : p + "/") || path.startsWith(p);
}

function blockPageFor(originalUrl) {
  return rules.blockPageUrl + "?from=" + encodeURIComponent(originalUrl);
}

/** Tabs currently showing our block page, with the original URL they came from. */
const blockPageTabs = new Map(); // tabId -> originalUrl

async function enforceTab(tabId, tab) {
  if (!tab || !tab.url || !rules.blockPageUrl) return;
  const url = tab.url;
  const lower = url.toLowerCase();

  // Tab on our block page: if the original site was unblocked, send it back.
  if (lower.startsWith(rules.blockPageUrl.toLowerCase())) {
    const from = new URLSearchParams(new URL(url).search).get("from");
    if (from) {
      blockPageTabs.set(tabId, from);
      if (!isBlockedUrl(from)) {
        if (blockPageTabs.get(tabId) !== undefined) {
          blockPageTabs.delete(tabId);
          chrome.tabs.update(tabId, { url: from }).catch(() => {});
        }
      }
    }
    return;
  }

  const match = isBlockedUrl(url);
  if (!match) return;

  blockPageTabs.set(tabId, url);
  chrome.tabs.update(tabId, { url: blockPageFor(url) }).catch(() => {});
}

async function sweepAllTabs() {
  try {
    const tabs = await chrome.tabs.query({});
    for (const tab of tabs) {
      if (tab.id !== undefined && tab.url) await enforceTab(tab.id, tab);
    }
  } catch (e) {
    /* window closed mid-sweep */
  }
}

/** Network-layer rules: redirect any http(s) request to a blocked host/prefix. */
async function applyNetworkRules() {
  const signature = JSON.stringify({ d: rules.domains, p: rules.paths, b: rules.blockPageUrl });
  if (signature === lastApplied) return;
  lastApplied = signature;

  const existing = await chrome.declarativeNetRequest.getDynamicRules();
  const removeIds = existing.map(r => r.id);

  const addRules = [];
  const makeRedirect = () => ({
    type: "redirect",
    redirect: { regexSubstitution: encodeRedirectTarget() },
  });

  // One rule per blocked domain: match host + subdomains, keep path in $1, redirect
  // to block page carrying the original URL (regexSubstitution cannot URL-encode,
  // so the block page decodes the raw form).
  let id = 1;
  for (const domain of rules.domains) {
    const d = domain.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    addRules.push({
      id: id++,
      priority: 1,
      action: {
        type: "redirect",
        redirect: { transform: { scheme: "chrome-extension", host: extensionHost(), path: "/blocked.html", query: "from=\\0" } },
      },
      condition: {
        regexFilter: `^https?://([a-z0-9-]+\\.)*${d}(/.*)?$`,
        resourceTypes: ["main_frame"],
      },
    });
  }

  for (const p of rules.paths || []) {
    const host = String(p.host || "").replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    const prefix = String(p.prefix || "/");
    const esc = prefix.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    addRules.push({
      id: id++,
      priority: 1,
      action: {
        type: "redirect",
        redirect: { transform: { scheme: "chrome-extension", host: extensionHost(), path: "/blocked.html", query: "from=\\0" } },
      },
      condition: {
        regexFilter: `^https?://([a-z0-9-]+\\.)*${host}${esc}(/.*)?$`,
        resourceTypes: ["main_frame"],
      },
    });
  }

  await chrome.declarativeNetRequest.updateDynamicRules({
    removeRuleIds: removeIds,
    addRules,
  });
}

let cachedHost = "";
function extensionHost() {
  if (cachedHost) return cachedHost;
  const u = new URL(chrome.runtime.getURL("/blocked.html"));
  cachedHost = u.host;
  return cachedHost;
}
