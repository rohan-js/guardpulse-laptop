namespace GuardPulse.Protocol;

/// <summary>
/// Firebase Realtime Database path builders. Ported from
/// shared/src/main/java/com/guardpulse/parentcontrol/shared/FirebasePaths.kt — same segments, same order.
/// </summary>
public static class FirebasePaths
{
    public static string UserDevices(string parentUid) => $"users/{parentUid}/devices";

    public static string UserDevice(string parentUid, string deviceId) => $"users/{parentUid}/devices/{deviceId}";

    public static string DeviceRoot(string deviceId) => $"devices/{deviceId}";

    public static string DeviceMeta(string deviceId) => $"devices/{deviceId}/meta";

    public static string DeviceApps(string deviceId) => $"devices/{deviceId}/apps";

    public static string DeviceApp(string deviceId, string packageName) =>
        $"devices/{deviceId}/apps/{PackageKeys.Encode(packageName)}";

    public static string DevicePolicyApps(string deviceId) => $"devices/{deviceId}/policy/apps";

    public static string DevicePolicyApp(string deviceId, string packageName) =>
        $"devices/{deviceId}/policy/apps/{PackageKeys.Encode(packageName)}";

    public static string DevicePolicyModes(string deviceId) => $"devices/{deviceId}/policy/modes";

    public static string DevicePolicyMode(string deviceId, string modeId) =>
        $"devices/{deviceId}/policy/modes/{modeId}";

    public static string DevicePolicyModeApp(string deviceId, string modeId, string packageName) =>
        $"devices/{deviceId}/policy/modes/{modeId}/apps/{PackageKeys.Encode(packageName)}";

    public static string DevicePolicyActiveMode(string deviceId) => $"devices/{deviceId}/policy/activeMode";

    public static string DeviceControlV2(string deviceId) => $"devices/{deviceId}/control/v2";

    public static string DeviceControlV2Apps(string deviceId) => $"devices/{deviceId}/control/v2/apps";

    public static string DeviceControlV2App(string deviceId, string packageName) =>
        $"devices/{deviceId}/control/v2/apps/{PackageKeys.Encode(packageName)}";

    public static string DeviceControlV2Modes(string deviceId) => $"devices/{deviceId}/control/v2/modes";

    public static string DeviceControlV2Mode(string deviceId, string modeId) =>
        $"devices/{deviceId}/control/v2/modes/{modeId}";

    public static string DeviceControlV2ModeApp(string deviceId, string modeId, string packageName) =>
        $"devices/{deviceId}/control/v2/modes/{modeId}/apps/{PackageKeys.Encode(packageName)}";

    public static string DeviceControlV2ActiveMode(string deviceId) => $"devices/{deviceId}/control/v2/activeMode";

    public static string DeviceControlV2SafeMode(string deviceId) => $"devices/{deviceId}/control/v2/safeMode";

    public static string DeviceControlV2Pin(string deviceId) => $"devices/{deviceId}/control/v2/pin";

    public static string DeviceSync(string deviceId) => $"devices/{deviceId}/sync";

    public static string DeviceSyncDesired(string deviceId) => $"devices/{deviceId}/sync/desired";

    public static string DeviceSyncApplied(string deviceId) => $"devices/{deviceId}/sync/applied";

    public static string DeviceSyncRuntime(string deviceId) => $"devices/{deviceId}/sync/runtime";

    public static string DeviceStateApps(string deviceId) => $"devices/{deviceId}/state/apps";

    public static string DeviceStateApp(string deviceId, string packageName) =>
        $"devices/{deviceId}/state/apps/{PackageKeys.Encode(packageName)}";

    public static string DeviceStateBrowser(string deviceId) => $"devices/{deviceId}/state/browser";

    public static string DeviceHeartbeat(string deviceId) => $"devices/{deviceId}/heartbeat";

    public static string DeviceCommands(string deviceId) => $"devices/{deviceId}/commands";

    public static string DeviceSecurity(string deviceId) => $"devices/{deviceId}/security";

    public static string DeviceSecurityPin(string deviceId) => $"devices/{deviceId}/security/pin";

    public static string DeviceSecurityRuntime(string deviceId) => $"devices/{deviceId}/security/runtime";

    public static string DeviceSecuritySafeMode(string deviceId) => $"devices/{deviceId}/security/safeMode";

    public static string DeviceTamperEvents(string deviceId) => $"devices/{deviceId}/tamperEvents";

    public static string DeviceUnlockRequests(string deviceId) => $"devices/{deviceId}/unlockRequests";

    public static string DeviceUnlockRequest(string deviceId, string requestId) =>
        $"devices/{deviceId}/unlockRequests/{requestId}";

    public static string DeviceActivity(string deviceId) => $"devices/{deviceId}/activity";

    public static string DeviceActivityCurrent(string deviceId) => $"devices/{deviceId}/activity/current";

    public static string DeviceActivityHistory(string deviceId) => $"devices/{deviceId}/activity/history";

    public static string DeviceActivityHistoryItem(string deviceId, string sessionId) =>
        $"devices/{deviceId}/activity/history/{sessionId}";

    public static string PairRequests(string deviceId) => $"pairRequests/{deviceId}";

    public static string PairRequest(string deviceId, string requestId) =>
        $"pairRequests/{deviceId}/{requestId}";
}
