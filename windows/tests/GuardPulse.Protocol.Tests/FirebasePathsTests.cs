namespace GuardPulse.Protocol.Tests;

using GuardPulse.Protocol;
using Xunit;

public class FirebasePathsTests
{
    private const string PackageName = "com.youtube.tv"; // base64url key: Y29tLnlvdXR1YmUudHY
    private const string PackageKey = "Y29tLnlvdXR1YmUudHY";
    private const string ModeId = "homework";
    private const string RequestId = "req-1";
    private const string SessionId = "sess-9";

    [Theory]
    [InlineData("device-abc-123")]
    [InlineData("dev777")]
    public void UserPaths(string parentUid)
    {
        var deviceId = "device-xyz";

        Assert.Equal($"users/{parentUid}/devices", FirebasePaths.UserDevices(parentUid));
        Assert.Equal($"users/{parentUid}/devices/{deviceId}", FirebasePaths.UserDevice(parentUid, deviceId));
    }

    [Theory]
    [InlineData("device-abc-123")]
    [InlineData("dev777")]
    public void DevicePaths(string id)
    {
        Assert.Equal($"devices/{id}", FirebasePaths.DeviceRoot(id));
        Assert.Equal($"devices/{id}/meta", FirebasePaths.DeviceMeta(id));
        Assert.Equal($"devices/{id}/apps", FirebasePaths.DeviceApps(id));
        Assert.Equal($"devices/{id}/apps/{PackageKey}", FirebasePaths.DeviceApp(id, PackageName));
        Assert.Equal($"devices/{id}/policy/apps", FirebasePaths.DevicePolicyApps(id));
        Assert.Equal($"devices/{id}/policy/apps/{PackageKey}", FirebasePaths.DevicePolicyApp(id, PackageName));
        Assert.Equal($"devices/{id}/policy/modes", FirebasePaths.DevicePolicyModes(id));
        Assert.Equal($"devices/{id}/policy/modes/{ModeId}", FirebasePaths.DevicePolicyMode(id, ModeId));
        Assert.Equal($"devices/{id}/policy/modes/{ModeId}/apps/{PackageKey}", FirebasePaths.DevicePolicyModeApp(id, ModeId, PackageName));
        Assert.Equal($"devices/{id}/policy/activeMode", FirebasePaths.DevicePolicyActiveMode(id));
    }

    [Theory]
    [InlineData("device-abc-123")]
    [InlineData("dev777")]
    public void ControlV2Paths(string id)
    {
        Assert.Equal($"devices/{id}/control/v2", FirebasePaths.DeviceControlV2(id));
        Assert.Equal($"devices/{id}/control/v2/apps", FirebasePaths.DeviceControlV2Apps(id));
        Assert.Equal($"devices/{id}/control/v2/apps/{PackageKey}", FirebasePaths.DeviceControlV2App(id, PackageName));
        Assert.Equal($"devices/{id}/control/v2/modes", FirebasePaths.DeviceControlV2Modes(id));
        Assert.Equal($"devices/{id}/control/v2/modes/{ModeId}", FirebasePaths.DeviceControlV2Mode(id, ModeId));
        Assert.Equal($"devices/{id}/control/v2/modes/{ModeId}/apps/{PackageKey}", FirebasePaths.DeviceControlV2ModeApp(id, ModeId, PackageName));
        Assert.Equal($"devices/{id}/control/v2/activeMode", FirebasePaths.DeviceControlV2ActiveMode(id));
        Assert.Equal($"devices/{id}/control/v2/safeMode", FirebasePaths.DeviceControlV2SafeMode(id));
        Assert.Equal($"devices/{id}/control/v2/pin", FirebasePaths.DeviceControlV2Pin(id));
    }

    [Theory]
    [InlineData("device-abc-123")]
    [InlineData("dev777")]
    public void SyncStateAndRuntimePaths(string id)
    {
        Assert.Equal($"devices/{id}/sync", FirebasePaths.DeviceSync(id));
        Assert.Equal($"devices/{id}/sync/desired", FirebasePaths.DeviceSyncDesired(id));
        Assert.Equal($"devices/{id}/sync/applied", FirebasePaths.DeviceSyncApplied(id));
        Assert.Equal($"devices/{id}/sync/runtime", FirebasePaths.DeviceSyncRuntime(id));
        Assert.Equal($"devices/{id}/state/apps", FirebasePaths.DeviceStateApps(id));
        Assert.Equal($"devices/{id}/state/apps/{PackageKey}", FirebasePaths.DeviceStateApp(id, PackageName));
        Assert.Equal($"devices/{id}/heartbeat", FirebasePaths.DeviceHeartbeat(id));
        Assert.Equal($"devices/{id}/commands", FirebasePaths.DeviceCommands(id));
    }

    [Theory]
    [InlineData("device-abc-123")]
    [InlineData("dev777")]
    public void SecurityTamperUnlockActivityAndPairingPaths(string id)
    {
        Assert.Equal($"devices/{id}/security", FirebasePaths.DeviceSecurity(id));
        Assert.Equal($"devices/{id}/security/pin", FirebasePaths.DeviceSecurityPin(id));
        Assert.Equal($"devices/{id}/security/runtime", FirebasePaths.DeviceSecurityRuntime(id));
        Assert.Equal($"devices/{id}/security/safeMode", FirebasePaths.DeviceSecuritySafeMode(id));
        Assert.Equal($"devices/{id}/tamperEvents", FirebasePaths.DeviceTamperEvents(id));
        Assert.Equal($"devices/{id}/unlockRequests", FirebasePaths.DeviceUnlockRequests(id));
        Assert.Equal($"devices/{id}/unlockRequests/{RequestId}", FirebasePaths.DeviceUnlockRequest(id, RequestId));
        Assert.Equal($"devices/{id}/activity", FirebasePaths.DeviceActivity(id));
        Assert.Equal($"devices/{id}/activity/current", FirebasePaths.DeviceActivityCurrent(id));
        Assert.Equal($"devices/{id}/activity/history", FirebasePaths.DeviceActivityHistory(id));
        Assert.Equal($"devices/{id}/activity/history/{SessionId}", FirebasePaths.DeviceActivityHistoryItem(id, SessionId));
        Assert.Equal($"pairRequests/{id}", FirebasePaths.PairRequests(id));
        Assert.Equal($"pairRequests/{id}/{RequestId}", FirebasePaths.PairRequest(id, RequestId));
    }

    [Fact]
    public void PackagePathSegmentsUseBase64UrlKeys()
    {
        // Exact strings for one sample device id, spelling out the encoded keys literally.
        Assert.Equal("devices/dev777/apps/Y29tLnlvdXR1YmUudHY", FirebasePaths.DeviceApp("dev777", "com.youtube.tv"));
        Assert.Equal("devices/dev777/control/v2/apps/Y29tLnlvdXR1YmUudHY", FirebasePaths.DeviceControlV2App("dev777", "com.youtube.tv"));
        Assert.Equal("devices/dev777/control/v2/modes/homework/apps/Y29tLnlvdXR1YmUudHY", FirebasePaths.DeviceControlV2ModeApp("dev777", "homework", "com.youtube.tv"));
        Assert.Equal("devices/dev777/state/apps/Y29tLnlvdXR1YmUudHY", FirebasePaths.DeviceStateApp("dev777", "com.youtube.tv"));
        Assert.Equal("users/parent-1/devices/dev777", FirebasePaths.UserDevice("parent-1", "dev777"));
    }
}
