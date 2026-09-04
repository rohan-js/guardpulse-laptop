/**
 * GuardPulse Site Guard — force-installed companion of the GuardPulse laptop agent.
 *
 * Pulls the current block rules from the agent's loopback endpoint and enforces them
 * in real time: blocked navigations are redirected to the local blocked page, SPA
 * history.pushState jumps (which never trigger network rules) are caught by the tab
 * watcher, and when the parent REMOVES a site every affected tab is restored
 * automatically (block-page tabs navigate back; dead/error tabs reload in place).
 * No keystroke injection, no page scripts — only tab-level navigation calls.
 */

const RULES_URL = "http://127.0.0.1:37846/rules";
const FETCH_ALARM = "gp-fetch";
const REFRESH_ALARM_MS = 15;

let rules = { domains: [], paths: [], blockPageUrl: "" };
let hostSet = new Set();
let pathRules = []; // [{host, prefix}]
let lastSignature = "";
let dnrCounter = 1;

chrome.alarms.create(FETCH_ALARM, { periodInMinutes: REFRESH_ALARM_MS / 60 });
fetchRules();
chrome.alarms.onAlarm.addListener(a => { if (a.name === FETCH_ALARM) fetchRules(); });
chrome.runtime.onStartup.addListener(fetchRules);
chrome.tabs.onUpdated.addListener((tabId, info, tab) => {
  // Also treat any navigation as a rules-refresh trigger: phone-initiated changes
  // land within a browser event instead of waiting for the alarm.
  if (info.status === "committed" || info.url !== undefined) {
    if (info.url && info.url.startsWith("chrome-extension://")) enforceTab(tabId, tab);
    else fetchRules();
    enforceTab(tabId, tab);
  }
});

async function fetchRules() {
  try {
    const res = await fetch(RULES_URL, { cache: "no-store" });
    if (!res.ok) return;
    const next = await res.json();
    const previous = { domains: rules.domains.slice(), paths: rules.paths.slice() };
    rules = {
      domains: Array.isArray(next.domains) ? next.domains : [],
      paths: Array.isArray(next.paths) ? next.paths : [],
      blockPageUrl: typeof next.blockPageUrl === "string" ? next.blockPageUrl : "",
    };
    rebuildIndexes();

    const signature = JSON.stringify({ d: rules.domains, p: rules.paths, b: rules.blockPageUrl });
    const changed = signature !== lastSignature;

    await applyNetworkRules(signature);

    if (changed) {
      // A rule was added or removed: sweep every tab. Blocked -> redirect to the
      // block page; sitting on our block page for an unblocked site -> go back;
      // dead on a now-unblocked site (native error page / offline SPA) -> reload.
      const removed = removedRules(previous);
      await sweepAllTabs(removed);
    }
  } catch (e) {
    /* agent down: keep last rules */
  }
}

function removedRules(previous) {
  const nowDomains = new Set(rules.domains.map(d => d.toLowerCase()));
  const nowPaths = new Set(rules.paths.map(p => (p.host + p.prefix).toLowerCase()));
  return {
    domains: (previous.domains || []).filter(d => !nowDomains.has(String(d).toLowerCase())),
    paths: (previous.paths || []).filter(p => !nowPaths.has((p.host + p.prefix).toLowerCase())),
  };
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

function wasBlockedByRemoved(url, removed) {
  if (!url || !/^https?:/i.test(url)) return false;
  let host, path;
  try {
    const u = new URL(url);
    host = u.hostname.toLowerCase();
    path = u.pathname || "/";
  } catch {
    return false;
  }

  const removedDomains = new Set((removed.domains || []).map(d => String(d).toLowerCase()));
  const removedPaths = (removed.paths || []).map(p => ({
    host: String(p.host || "").toLowerCase(),
    prefix: String(p.prefix || "/"),
  }));

  let cur = host;
  while (cur) {
    if (removedDomains.has(cur)) return true;
    const dot = cur.indexOf(".");
    if (dot < 0 || dot === cur.length - 1) break;
    cur = cur.slice(dot + 1);
  }

  for (const rule of removedPaths) {
    let cur2 = host;
    while (cur2) {
      if (cur2 === rule.host && pathStartsWith(path, rule.prefix)) return true;
      const dot = cur2.indexOf(".");
      if (dot < 0 || dot === cur2.length - 1) break;
      cur2 = cur2.slice(dot + 1);
    }
  }
  return false;
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
    let from = null;
    try {
      from = new URLSearchParams(new URL(url).search).get("from");
    } catch (e) { /* ignore */ }
    if (from && !isBlockedUrl(from)) {
      blockPageTabs.delete(tabId);
      chrome.tabs.update(tabId, { url: from }).catch(() => {});
    }
    return;
  }

  const match = isBlockedUrl(url);
  if (!match) return;

  blockPageTabs.set(tabId, url);
  chrome.tabs.update(tabId, { url: blockPageFor(url) }).catch(() => {});
}

async function sweepAllTabs(removed) {
  try {
    const tabs = await chrome.tabs.query({});
    for (const tab of tabs) {
      if (tab.id === undefined || !tab.url) continue;
      const lower = tab.url.toLowerCase();
      if (lower.startsWith(rules.blockPageUrl.toLowerCase())) {
        await enforceTab(tab.id, tab);
        continue;
      }
      if (isBlockedUrl(tab.url)) {
        await enforceTab(tab.id, tab);
      } else if (removed && wasBlockedByRemoved(tab.url, removed)) {
        // Unblocked: reload the dead/error page so the site comes back instantly.
        chrome.tabs.reload(tab.id, { bypassCache: true }).catch(() => {});
      }
    }
  } catch (e) {
    /* window closed mid-sweep */
  }
}

/** Network-layer rules: redirect any http(s) request to a blocked host/prefix. */
async function applyNetworkRules(signature) {
  if (signature === lastApplied) return;
  lastApplied = signature;

  const existing = await chrome.declarativeNetRequest.getDynamicRules();
  const removeIds = existing.map(r => r.id);

  const addRules = [];
  const hostPattern = d => d.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const blockPagePath = "/blocked.html";

  // One rule per blocked domain: match host + subdomains, keep the full URL in $0,
  // redirect to the packed block page (transform keeps it literal — no loop, the
  // block page is chrome-extension:// and never matches ^https?://).
  let id = 1;
  for (const domain of rules.domains) {
    const d = hostPattern(domain);
    addRules.push({
      id: id++,
      priority: 1,
      action: {
        type: "redirect",
        redirect: {
          transform: { scheme: "chrome-extension", host: extensionHost(), path: blockPagePath, query: "from=\\0" },
        },
      },
      condition: {
        regexFilter: `^https?://([a-z0-9-]+\\.)*${d}(/.*)?$`,
        resourceTypes: ["main_frame"],
      },
    });
  }

  for (const p of rules.paths || []) {
    const host = hostPattern(String(p.host || ""));
    const prefix = String(p.prefix || "/");
    const esc = prefix.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    addRules.push({
      id: id++,
      priority: 1,
      action: {
        type: "redirect",
        redirect: {
          transform: { scheme: "chrome-extension", host: extensionHost(), path: blockPagePath, query: "from=\\0" },
        },
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
