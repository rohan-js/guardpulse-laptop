package com.guardpulse.parentcontrol.shared

object FirebasePaths {
    fun userDevices(parentUid: String) = "users/$parentUid/devices"
    fun userDevice(parentUid: String, deviceId: String) = "users/$parentUid/devices/$deviceId"

    fun deviceRoot(deviceId: String) = "devices/$deviceId"
    fun deviceMeta(deviceId: String) = "devices/$deviceId/meta"
    fun deviceApps(deviceId: String) = "devices/$deviceId/apps"
    fun deviceApp(deviceId: String, packageName: String) =
        "devices/$deviceId/apps/${PackageKeys.encode(packageName)}"
    fun devicePolicyApps(deviceId: String) = "devices/$deviceId/policy/apps"
    fun devicePolicyApp(deviceId: String, packageName: String) =
        "devices/$deviceId/policy/apps/${PackageKeys.encode(packageName)}"
    fun devicePolicyModes(deviceId: String) = "devices/$deviceId/policy/modes"
    fun devicePolicyMode(deviceId: String, modeId: String) =
        "devices/$deviceId/policy/modes/$modeId"
    fun devicePolicyModeApp(deviceId: String, modeId: String, packageName: String) =
        "devices/$deviceId/policy/modes/$modeId/apps/${PackageKeys.encode(packageName)}"
    fun devicePolicyActiveMode(deviceId: String) = "devices/$deviceId/policy/activeMode"
    fun deviceControlV2(deviceId: String) = "devices/$deviceId/control/v2"
    fun deviceControlV2Apps(deviceId: String) = "devices/$deviceId/control/v2/apps"
    fun deviceControlV2App(deviceId: String, packageName: String) =
        "devices/$deviceId/control/v2/apps/${PackageKeys.encode(packageName)}"
    fun deviceControlV2Modes(deviceId: String) = "devices/$deviceId/control/v2/modes"
    fun deviceControlV2Mode(deviceId: String, modeId: String) =
        "devices/$deviceId/control/v2/modes/$modeId"
    fun deviceControlV2ModeApp(deviceId: String, modeId: String, packageName: String) =
        "devices/$deviceId/control/v2/modes/$modeId/apps/${PackageKeys.encode(packageName)}"
    fun deviceControlV2ActiveMode(deviceId: String) = "devices/$deviceId/control/v2/activeMode"
    fun deviceControlV2SafeMode(deviceId: String) = "devices/$deviceId/control/v2/safeMode"
    fun deviceControlV2Pin(deviceId: String) = "devices/$deviceId/control/v2/pin"
    fun deviceControlV2Schedule(deviceId: String) = "devices/$deviceId/control/v2/schedule"
    fun deviceControlV2Budget(deviceId: String) = "devices/$deviceId/control/v2/budget"
    fun deviceControlV2ContentFilter(deviceId: String) = "devices/$deviceId/control/v2/contentFilter"
    fun deviceControlV2Allowlist(deviceId: String) = "devices/$deviceId/control/v2/allowlist"
    fun deviceControlV2CustomBlockedDomains(deviceId: String) = "devices/$deviceId/control/v2/customBlockedDomains"
    fun deviceSync(deviceId: String) = "devices/$deviceId/sync"
    fun deviceSyncDesired(deviceId: String) = "devices/$deviceId/sync/desired"
    fun deviceSyncApplied(deviceId: String) = "devices/$deviceId/sync/applied"
    fun deviceSyncRuntime(deviceId: String) = "devices/$deviceId/sync/runtime"
    fun deviceStateApps(deviceId: String) = "devices/$deviceId/state/apps"
    fun deviceStateApp(deviceId: String, packageName: String) =
        "devices/$deviceId/state/apps/${PackageKeys.encode(packageName)}"
    fun deviceHeartbeat(deviceId: String) = "devices/$deviceId/heartbeat"
    fun deviceCommands(deviceId: String) = "devices/$deviceId/commands"
    fun deviceSecurity(deviceId: String) = "devices/$deviceId/security"
    fun deviceSecurityPin(deviceId: String) = "devices/$deviceId/security/pin"
    fun deviceSecurityRuntime(deviceId: String) = "devices/$deviceId/security/runtime"
    fun deviceSecuritySafeMode(deviceId: String) = "devices/$deviceId/security/safeMode"
    fun deviceTamperEvents(deviceId: String) = "devices/$deviceId/tamperEvents"
    fun deviceUnlockRequests(deviceId: String) = "devices/$deviceId/unlockRequests"
    fun deviceUnlockRequest(deviceId: String, requestId: String) =
        "devices/$deviceId/unlockRequests/$requestId"
    fun deviceActivity(deviceId: String) = "devices/$deviceId/activity"
    fun deviceActivityCurrent(deviceId: String) = "devices/$deviceId/activity/current"
    fun deviceActivityHistory(deviceId: String) = "devices/$deviceId/activity/history"
    fun deviceActivityHistoryItem(deviceId: String, sessionId: String) =
        "devices/$deviceId/activity/history/$sessionId"
    fun pairRequests(deviceId: String) = "pairRequests/$deviceId"
    fun pairRequest(deviceId: String, requestId: String) = "pairRequests/$deviceId/$requestId"
}
