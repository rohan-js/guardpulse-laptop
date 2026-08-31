const fs = require("fs");
const {
  assertFails,
  assertSucceeds,
  initializeTestEnvironment,
} = require("@firebase/rules-unit-testing");

let testEnv;

beforeAll(async () => {
  testEnv = await initializeTestEnvironment({
    projectId: "guardpulse-parental-control-test",
    database: {
      rules: fs.readFileSync("database.rules.json", "utf8"),
    },
  });
});

afterAll(async () => {
  await testEnv.cleanup();
});

beforeEach(async () => {
  await testEnv.clearDatabase();
  await testEnv.withSecurityRulesDisabled(async (context) => {
    const db = context.database();
    await db.ref("devices/tv1/meta").set({
      deviceId: "tv1",
      tvUid: "tvUid",
      ownerUid: "parentUid",
      label: "Mi TV",
      androidSdk: 28,
    });
    await db.ref("users/parentUid/devices/tv1").set({
      deviceId: "tv1",
      label: "Mi TV",
      pairedAt: 1,
    });
  });
});

function dbAs(uid) {
  return testEnv.authenticatedContext(uid).database();
}

test("paired parent can read only paired device", async () => {
  await assertSucceeds(dbAs("parentUid").ref("devices/tv1/apps").get());
  await assertFails(dbAs("otherParent").ref("devices/tv1/apps").get());
});

test("parent can write desired policy but not TV state", async () => {
  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/policy/apps/Y29tLnZpZGVv").set({
      packageName: "com.video",
      manualBlocked: true,
      dailyLimitMinutes: 30,
      updatedAt: 1,
    })
  );

  await assertFails(
    dbAs("parentUid").ref("devices/tv1/state/apps/Y29tLnZpZGVv").set({
      packageName: "com.video",
      requestedSuspended: true,
      enforcementMode: "fallback",
      fallbackLocked: true,
      usageMinutesToday: 0,
    })
  );
});

test("parent can manage modes active mode and safe mode", async () => {
  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/policy/modes/mode1").set({
      modeId: "mode1",
      name: "Study",
      createdAt: 1,
      updatedAt: 1,
      updatedBy: "parentUid",
      apps: {
        Y29tLnZpZGVv: {
          packageName: "com.video",
          manualBlocked: true,
          dailyLimitMinutes: 30,
          updatedAt: 1,
        },
      },
    })
  );

  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/policy/activeMode").set({
      modeId: "mode1",
      modeName: "Study",
      activatedAt: 2,
      updatedBy: "parentUid",
    })
  );

  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/security/safeMode").set({
      enabled: true,
      until: 1800003,
      startedAt: 3,
      startedBy: "parentUid",
    })
  );

  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/security/safeMode").set({
      enabled: false,
      until: 0,
      updatedAt: 4,
      updatedBy: "parentUid",
    })
  );
});

test("TV and unpaired parents cannot write parent-only controls", async () => {
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/policy/modes/mode1").set({
      modeId: "mode1",
      name: "Study",
    })
  );

  await assertFails(
    dbAs("otherParent").ref("devices/tv1/security/safeMode").set({
      enabled: true,
      until: 9999999999999,
    })
  );
});

test("TV can write runtime state but not desired policy", async () => {
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/security/runtime").update({
      enforcementMode: "fallback",
      accessibility: true,
      usageAccess: true,
      updatedAt: 1,
    })
  );

  await assertFails(
    dbAs("tvUid").ref("devices/tv1/policy/apps/Y29tLnZpZGVv").set({
      packageName: "com.video",
      manualBlocked: true,
    })
  );
});

test("parent creates command and TV updates command status", async () => {
  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/commands/cmd1").set({
      type: "rescanApps",
      requestedBy: "parentUid",
      createdAt: 1,
      ttlMs: 300000,
      status: "pending",
    })
  );

  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/commands/cmd1").update({
      status: "running",
      claimedAt: 2,
      sessionId: "session-1",
    })
  );

  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/commands/cmd1").update({
      status: "done",
      completedAt: 3,
    })
  );

  await assertFails(
    dbAs("tvUid").ref("devices/tv1/commands/cmd1").update({
      status: "running",
      claimedAt: 4,
    })
  );
});

test("parent can create open setup command and unknown commands are rejected", async () => {
  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/commands/cmdOpenSetup").set({
      type: "openSetup",
      requestedBy: "parentUid",
      createdAt: 1,
    })
  );

  await assertFails(
    dbAs("parentUid").ref("devices/tv1/commands/cmdUnknown").set({
      type: "openHiddenThing",
      requestedBy: "parentUid",
      createdAt: 1,
    })
  );
});

test("unpaired parent cannot approve unlock request", async () => {
  await testEnv.withSecurityRulesDisabled(async (context) => {
    const db = context.database();
    await db.ref("devices/tv1/unlockRequests/request1").set({
      requestId: "request1",
      packageName: "com.video",
      reason: "manual",
      status: "pending",
      createdAt: 1,
      expiresAt: 9999999999999,
    });
  });

  await assertFails(
    dbAs("otherParent").ref("devices/tv1/unlockRequests/request1").update({
      status: "approved",
      updatedAt: 2,
      updatedBy: "otherParent",
    })
  );
});

test("paired parent can choose unlock approval type and invalid duration is rejected", async () => {
  await testEnv.withSecurityRulesDisabled(async (context) => {
    const db = context.database();
    await db.ref("devices/tv1/unlockRequests/request1").set({
      requestId: "request1",
      packageName: "com.video",
      reason: "manual",
      status: "pending",
      createdAt: 1,
      expiresAt: 9999999999999,
    });
  });

  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/unlockRequests/request1").update({
      status: "approved",
      approvalType: "timed",
      approvalDurationMs: 900000,
      updatedAt: 2,
      updatedBy: "parentUid",
    })
  );

  await assertFails(
    dbAs("parentUid").ref("devices/tv1/unlockRequests/request1").update({
      status: "approved",
      approvalType: "timed",
      approvalDurationMs: 120000,
      updatedAt: 3,
      updatedBy: "parentUid",
    })
  );
});

test("parent writes coherent V2 control and desired revision while TV cannot", async () => {
  const control = {
    schemaVersion: 2,
    revisionId: "revision-1",
    updatedAt: 10,
    updatedBy: "parentUid",
    apps: {
      Y29tLnZpZGVv: {
        packageKey: "Y29tLnZpZGVv",
        packageName: "com.video",
        manualBlocked: true,
        dailyLimitMinutes: 30,
        updatedAt: 10,
      },
    },
    modes: {
      mode1: {
        modeId: "mode1",
        name: "Study",
        apps: {
          Y29tLnZpZGVv: {
            packageKey: "Y29tLnZpZGVv",
            packageName: "com.video",
            manualBlocked: true,
          },
        },
      },
    },
    activeMode: { modeId: "mode1", modeName: "Study", activatedAt: 10 },
    safeMode: { enabled: false, until: 0 },
  };

  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1").update({
      "control/v2": control,
      "sync/desired": {
        revisionId: "revision-1",
        kind: "migration",
        target: "control",
        requestedAt: 10,
        requestedBy: "parentUid",
      },
    })
  );

  await assertFails(
    dbAs("tvUid").ref("devices/tv1/control/v2").set({
      ...control,
      revisionId: "revision-tv",
      updatedBy: "tvUid",
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/sync/desired").set({
      revisionId: "revision-tv",
      kind: "migration",
      requestedAt: 11,
      requestedBy: "tvUid",
    })
  );
});

test("V2 rejects missing mode references and unsupported schemas", async () => {
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/control/v2").set({
      schemaVersion: 2,
      revisionId: "bad-mode",
      updatedBy: "parentUid",
      activeMode: { modeId: "missing" },
      safeMode: { enabled: false, until: 0 },
    })
  );

  await assertFails(
    dbAs("parentUid").ref("devices/tv1/control/v2").set({
      schemaVersion: 3,
      revisionId: "future-schema",
      updatedBy: "parentUid",
      safeMode: { enabled: false, until: 0 },
    })
  );
});

test("TV writes acknowledgements runtime diagnostics and precise usage only", async () => {
  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1").update({
      "control/v2": {
        schemaVersion: 2,
        revisionId: "revision-1",
        updatedAt: 10,
        updatedBy: "parentUid",
        safeMode: { enabled: false, until: 0 },
      },
      "sync/desired": {
        revisionId: "revision-1",
        kind: "migration",
        requestedAt: 10,
        requestedBy: "parentUid",
      },
    })
  );
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/sync/runtime").set({
      connected: true,
      sessionId: "session-1",
      protocolVersion: 2,
      lastPolicyAppliedAt: 20,
      lastUsageWriteAt: 21,
      lastSuccessAt: 21,
    })
  );
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/sync/applied").set({
      revisionId: "revision-1",
      status: "applied",
      appliedAt: 20,
      sessionId: "session-1",
    })
  );
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/state/apps/Y29tLnZpZGVv").set({
      packageName: "com.video",
      requestedSuspended: false,
      enforcementMode: "fallback",
      fallbackLocked: false,
      usageMinutesToday: 1,
      usageMsToday: 65000,
      rawUsageMsToday: 65000,
      usageCapturedAt: 30,
      foregroundActive: true,
      foregroundStartedAt: 25,
      controlRevisionId: "revision-1",
      updatedAt: 30,
    })
  );

  await assertFails(
    dbAs("parentUid").ref("devices/tv1/sync/applied").set({
      revisionId: "revision-1",
      status: "applied",
      appliedAt: 20,
      sessionId: "parent-session",
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/state/apps/Y29tLnZpZGVv").update({
      usageMsToday: -1,
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/state/apps/Y29tLnZpZGVv").update({
      controlRevisionId: 123,
    })
  );
});

test("TV acknowledges approved unlock without gaining approval authority", async () => {
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/unlockRequests/requestAck").set({
      requestId: "requestAck",
      packageName: "com.video",
      reason: "manual",
      status: "pending",
      createdAt: 1,
      expiresAt: 9999999999999,
      ttlMs: 600000,
    })
  );

  await assertFails(
    dbAs("tvUid").ref("devices/tv1/unlockRequests/requestAck").update({
      status: "approved",
    })
  );

  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/unlockRequests/requestAck").update({
      status: "approved",
      approvalType: "oneVisit",
      updatedAt: 2,
      updatedBy: "parentUid",
    })
  );

  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/unlockRequests/requestAck").update({
      tvApplyStatus: "applied",
      tvAppliedAt: 3,
    })
  );
});

test("pair request exposes lifecycle only to its parent and TV", async () => {
  await testEnv.withSecurityRulesDisabled(async (context) => {
    await context.database().ref("devices/tv1/meta/ownerUid").remove();
  });
  await assertSucceeds(
    dbAs("parentUid").ref("pairRequests/tv1/pair1").set({
      parentUid: "parentUid",
      code: "123456",
      createdAt: 1,
      expiresAt: 300001,
      status: "pending",
    })
  );
  await assertSucceeds(dbAs("parentUid").ref("pairRequests/tv1/pair1").get());
  await assertFails(dbAs("otherParent").ref("pairRequests/tv1/pair1").get());
  await assertSucceeds(
    dbAs("tvUid").ref("pairRequests/tv1/pair1").update({
      status: "accepted",
      respondedAt: 2,
    })
  );
  await assertFails(
    dbAs("parentUid").ref("pairRequests/tv1/pair1").update({ status: "rejected" })
  );
});

test("TV cannot replace an existing owner or write another parent's device entry", async () => {
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/meta/ownerUid").set("otherParent")
  );
  await assertFails(
    dbAs("tvUid").ref("users/otherParent/devices/tv1").set({
      deviceId: "tv1",
      label: "Mi TV",
    })
  );
  await assertSucceeds(
    dbAs("tvUid").ref().update({
      "devices/tv1/meta/ownerUid": null,
      "users/parentUid/devices/tv1": null,
    })
  );
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/meta/ownerUid").set("otherParent")
  );
});

test("new PIN records require valid PBKDF2 parameters while legacy hashes remain valid", async () => {
  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/security/pin").set({
      salt: "abcdefghijklmnopqrstuv",
      hash: "abcdefghijklmnopqrstuvwxyzABCDEFGH123456789",
      version: 2,
      algorithm: "PBKDF2WithHmacSHA256",
      iterations: 210000,
      updatedAt: 1,
      updatedBy: "parentUid",
    })
  );
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/security/pin").set({
      salt: "abcdefghijklmnopqrstuv",
      hash: "abcdefghijklmnopqrstuvwxyzABCDEFGH123456789",
      version: 2,
      algorithm: "SHA-256",
      iterations: 1,
      updatedAt: 2,
      updatedBy: "parentUid",
    })
  );
  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/security/pin").set({
      salt: "abcdefghijklmnopqrstuv",
      hash: "abcdefghijklmnopqrstuvwxyzABCDEFGH123456789",
      updatedAt: 3,
      updatedBy: "parentUid",
    })
  );
});

test("paired parent can delete terminal history but not pending work", async () => {
  await testEnv.withSecurityRulesDisabled(async (context) => {
    const db = context.database();
    await db.ref("devices/tv1/commands/done").set({
      type: "rescanApps",
      status: "done",
      createdAt: 1,
    });
    await db.ref("devices/tv1/commands/pending").set({
      type: "rescanApps",
      status: "pending",
      createdAt: 2,
    });
    await db.ref("devices/tv1/unlockRequests/expired").set({
      requestId: "expired",
      packageName: "com.video",
      reason: "manual",
      status: "expired",
      createdAt: 1,
    });
    await db.ref("devices/tv1/tamperEvents/event1").set({
      type: "pinRetryLocked",
      createdAt: 1,
    });
  });

  await assertSucceeds(dbAs("parentUid").ref("devices/tv1/commands/done").remove());
  await assertFails(dbAs("parentUid").ref("devices/tv1/commands/pending").remove());
  await assertSucceeds(dbAs("parentUid").ref("devices/tv1/unlockRequests/expired").remove());
  await assertSucceeds(dbAs("parentUid").ref("devices/tv1/tamperEvents/event1").remove());
});

test("request identity fields are immutable after creation", async () => {
  await testEnv.withSecurityRulesDisabled(async (context) => {
    await context.database().ref("devices/tv1/unlockRequests/requestImmutable").set({
      requestId: "requestImmutable",
      packageName: "com.video",
      reason: "manual",
      status: "pending",
      createdAt: 1,
      expiresAt: 600001,
    });
  });
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/unlockRequests/requestImmutable").update({
      packageName: "com.other",
      status: "approved",
      updatedAt: 2,
      updatedBy: "parentUid",
    })
  );
});

test("runtime and request contracts reject unknown fields", async () => {
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/state/apps/Y29tLnZpZGVv").set({
      packageName: "com.video",
      requestedSuspended: false,
      enforcementMode: "fallback",
      fallbackLocked: false,
      usageMinutesToday: 0,
      injected: true,
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/sync/runtime").set({
      connected: true,
      protocolVersion: 2,
      injected: true,
    })
  );
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/commands/unknownField").set({
      type: "rescanApps",
      status: "pending",
      createdAt: 1,
      requestedBy: "parentUid",
      injected: true,
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/unlockRequests/unknownField").set({
      requestId: "unknownField",
      packageName: "com.video",
      reason: "manual",
      status: "pending",
      createdAt: 1,
      injected: true,
    })
  );
});

test("device can write activity current and parent cannot", async () => {
  const current = {
    runtimeApp: "C:\\path\\app.exe",
    appKey: "c:\\path\\app.exe",
    appLabel: "App",
    appStartedAt: 1000,
    overlayState: "locked",
    mediaAvailable: false,
    playbackState: "unknown",
    playbackSpeed: 0,
    captureSource: "agent",
    updatedAt: 2000,
  };
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/activity/current").set(current)
  );
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/activity/current").set(current)
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/activity/current").set({
      ...current,
      overlayState: "hidden",
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/activity/current").set({
      ...current,
      injected: true,
    })
  );
});

test("device can write valid activity history entries only", async () => {
  const entry = {
    id: "session-1",
    type: "app",
    appKey: "c:\\path\\app.exe",
    appLabel: "App",
    startedAt: 1000,
    endedAt: 60000,
    captureSource: "agent",
    updatedAt: 60000,
  };
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/activity/history/session-1").set(entry)
  );
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/activity/history/session-2").set({
      ...entry,
      id: "session-2",
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/activity/history/session-3").set({
      ...entry,
      id: "session-3",
      endedAt: 999,
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/activity/history/session-4").set({
      ...entry,
      id: "mismatch",
    })
  );
});

test("device meta accepts platform field for windows agent", async () => {
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/meta").update({
      platform: "windows",
      appVersion: "0.2.0",
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/meta").update({
      platform: 123,
    })
  );
});

test("device can write windows-shaped app state with revision id", async () => {
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/state/apps/YzpccGF0aFwdygvvbi5leGU").set({
      packageName: "C:\\path\\app.exe",
      requestedSuspended: false,
      suspended: false,
      enforcementMode: "fallback",
      fallbackLocked: true,
      usageMinutesToday: 12,
      usageMsToday: 720000,
      usageCapturedAt: 5000,
      lockBlocked: true,
      lockReason: "manual",
      controlRevisionId: "rev-1",
      blockable: true,
      updatedAt: 5000,
    })
  );
});

const baseControl = (extra) => ({
  schemaVersion: 2,
  revisionId: "rev-laptop-1",
  updatedAt: 10,
  updatedBy: "parentUid",
  safeMode: { enabled: false, until: 0 },
  ...extra,
});

test("laptop controls: valid schedule budget contentFilter allowlist are accepted", async () => {
  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/control/v2").set(
      baseControl({
        revisionId: "rev-laptop-2",
        schedule: { enabled: true, startMinute: 930, endMinute: 1260 },
        budget: { dailyLimitMinutes: 120 },
        contentFilter: { social: true, gambling: false, adult: true },
        allowlist: { enabled: false },
      })
    )
  );
  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/sync/desired").set({
      revisionId: "rev-laptop-2",
      kind: "schedule",
      requestedAt: 11,
      requestedBy: "parentUid",
    })
  );
});

test("laptop controls: invalid values are rejected", async () => {
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/control/v2").set(
      baseControl({ revisionId: "rev-bad-schedule", schedule: { enabled: true, startMinute: 1440, endMinute: 60 } })
    )
  );
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/control/v2").set(
      baseControl({ revisionId: "rev-bad-budget", budget: { dailyLimitMinutes: 0 } })
    )
  );
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/control/v2").set(
      baseControl({ revisionId: "rev-bad-filter", contentFilter: { social: "yes" } })
    )
  );
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/control/v2").set(
      baseControl({ revisionId: "rev-bad-allowlist", allowlist: { enabled: "yes" } })
    )
  );
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/control/v2").set(
      baseControl({ revisionId: "rev-bad-extra", schedule: { enabled: true, startMinute: 0, endMinute: 60, bonus: 1 } })
    )
  );
});

test("laptop controls: schedule blocks can be removed and unknown kinds still rejected", async () => {
  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/control/v2").set(baseControl({ revisionId: "rev-laptop-3" }))
  );
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/sync/desired").set({
      revisionId: "rev-laptop-3",
      kind: "nonsense",
      requestedAt: 12,
      requestedBy: "parentUid",
    })
  );
});

test("device can report schedule budget filter and allowlist runtime flags", async () => {
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/heartbeat").update({
      online: true,
      lastSeen: 100,
      scheduleActive: true,
      budgetMinutesToday: 45,
      contentFilterActive: true,
      allowlistActive: false,
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/heartbeat").update({
      online: true,
      lastSeen: 101,
      budgetMinutesToday: "45",
    })
  );
});

test("parent writes custom blocked domains with valid domains and paths only", async () => {
  // Valid: plain domains, and path entries like youtube.com/shorts.
  await assertSucceeds(
    dbAs("parentUid").ref("devices/tv1/control/v2").update({
      customBlockedDomains: {
        "0": "example.com",
        "1": "youtube.com/shorts",
      },
    })
  );

  // Invalid: not a domain-shaped string, and over the 253-char limit.
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/control/v2").update({
      customBlockedDomains: {
        "0": "not a domain",
      },
    })
  );
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/control/v2").update({
      customBlockedDomains: {
        "0": "a".repeat(254) + ".com",
      },
    })
  );
});

// RTDB keys cannot contain "." (nor "$#[]/"), so domainsToday domain keys
// arrive encoded — same convention as the base64 app keys used above
// (Z2l0aHViLmNvbQ === base64("github.com"), eW91dHViZS5jb20 === base64("youtube.com")).
const browserState = {
  browser: "c:\\program files\\bravesoftware\\brave-browser\\application\\brave.exe",
  label: "Brave",
  activeTab: "GitHub",
  activeUrl: "https://github.com/",
  tabCount: 9,
  tabs: [
    { title: "GitHub", url: "https://github.com/" },
    { title: "YouTube" },
  ],
  domainsToday: { Z2l0aHViLmNvbQ: 123456, eW91dHViZS5jb20: 789 },
  updatedAt: 1699999999999,
};

test("device can write full browser state but parents cannot", async () => {
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/state/browser").set(browserState)
  );
  await assertFails(
    dbAs("parentUid").ref("devices/tv1/state/browser").set(browserState)
  );
  await assertFails(
    dbAs("otherParent").ref("devices/tv1/state/browser").update({ tabCount: 3 })
  );
});

test("device can patch browser state fields and subnodes", async () => {
  // A patch of live fields alone cannot create the node: required fields missing.
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/state/browser").update({ activeTab: "Docs" })
  );
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/state/browser").set(browserState)
  );
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/state/browser").update({
      activeTab: "YouTube",
      activeUrl: "https://youtube.com/",
      tabCount: 10,
      updatedAt: 1700000000000,
    })
  );
  await assertSucceeds(
    dbAs("tvUid")
      .ref("devices/tv1/state/browser")
      .update({ domainsToday: { Z2l0aHViLmNvbQ: 130000 } })
  );
  await assertSucceeds(
    dbAs("tvUid")
      .ref("devices/tv1/state/browser/domainsToday/eW91dHViZS5jb20")
      .set(800)
  );
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/state/browser/tabs/2").set({ title: "Docs" })
  );
  await assertSucceeds(
    dbAs("tvUid")
      .ref("devices/tv1/state/browser/tabs/1")
      .update({ url: "https://youtube.com/watch?v=abc" })
  );
});

test("browser state rejects invalid titles counts urls and unknown fields", async () => {
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/state/browser").set({
      ...browserState,
      tabs: [{ title: "a".repeat(5000) }],
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/state/browser/tabs/0").set({
      title: "b".repeat(301),
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/state/browser").set({
      ...browserState,
      tabs: [{ title: "Tab", url: "https://x.com/", evil: 1 }],
    })
  );
  await assertFails(
    dbAs("tvUid")
      .ref("devices/tv1/state/browser")
      .set({ ...browserState, tabCount: -1 })
  );
  await assertFails(
    dbAs("tvUid")
      .ref("devices/tv1/state/browser")
      .set({ ...browserState, tabCount: 1001 })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/state/browser").set({
      ...browserState,
      browser: "x".repeat(10000),
    })
  );
  await assertFails(
    dbAs("tvUid").ref("devices/tv1/state/browser").set({
      ...browserState,
      domainsToday: { Z2l0aHViLmNvbQ: 86400001 },
    })
  );
  await assertFails(
    dbAs("tvUid")
      .ref("devices/tv1/state/browser")
      .set({ ...browserState, injected: true })
  );
});

test("sync runtime accepts lastBrowserWriteAt and still rejects unknown fields", async () => {
  await assertSucceeds(
    dbAs("tvUid")
      .ref("devices/tv1/sync/runtime")
      .update({ lastBrowserWriteAt: 1700000000000 })
  );
  await assertFails(
    dbAs("tvUid")
      .ref("devices/tv1/sync/runtime")
      .update({ lastBrowserWriteAt: "not-a-number" })
  );
  await assertFails(
    dbAs("tvUid")
      .ref("devices/tv1/sync/runtime")
      .update({ lastBrowserWriteNanoseconds: 1 })
  );
});

test("device can append tab timeline entries to activity history", async () => {
  const tabEntry = {
    id: "tab-1",
    type: "tab",
    appKey: "c:\\path\\brave.exe",
    appLabel: "Brave",
    title: "GitHub",
    startedAt: 1000,
    endedAt: 60000,
    captureSource: "agent",
    updatedAt: 60000,
  };
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/activity/history/tab-1").set(tabEntry)
  );
  await assertFails(
    dbAs("tvUid")
      .ref("devices/tv1/activity/history/tab-2")
      .set({ ...tabEntry, id: "tab-2", type: "window" })
  );
});

test("tab timeline entries may carry a page url (browser tab sessions)", async () => {
  const tabEntry = {
    id: "tab-url-1",
    type: "tab",
    appKey: "c:\\path\\brave.exe",
    appLabel: "GitHub",
    title: "GitHub",
    url: "https://github.com/guardpulse",
    startedAt: 1000,
    endedAt: 60000,
    captureSource: "agent",
    updatedAt: 60000,
  };
  await assertSucceeds(
    dbAs("tvUid").ref("devices/tv1/activity/history/tab-url-1").set(tabEntry)
  );
  await assertFails(
    dbAs("tvUid")
      .ref("devices/tv1/activity/history/tab-url-2")
      .set({ ...tabEntry, id: "tab-url-2", url: "x".repeat(3000) })
  );
});
