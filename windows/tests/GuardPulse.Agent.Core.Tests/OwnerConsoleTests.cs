namespace GuardPulse.Agent.Core.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using GuardPulse.Agent.Core;
using GuardPulse.Protocol;
using Xunit;

public class OwnerConsoleTests
{
    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);
        public string? Get(string key) => _data.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _data[key] = value;
        public void Delete(string key) => _data.Remove(key);
    }

    private class FakeFirebase : IFirebaseClient
    {
        public string? Uid { get; set; }
        public Dictionary<string, string> Store { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Puts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Patches { get; } = new(StringComparer.Ordinal);
        public List<string> Streams { get; } = new();

        public Task SignInAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetAsync(string path, CancellationToken ct)
            => Task.FromResult(Store.TryGetValue(path, out var v) ? v : "null");
        public virtual Task PutAsync(string path, string json, CancellationToken ct)
        {
            Puts[path] = json;
            Store[path] = json;
            return Task.CompletedTask;
        }
        public virtual Task PatchAsync(string path, string json, CancellationToken ct)
        {
            Patches[path] = json;
            // Merge into Store so subsequent reads see the update (PATCH = updateChildren).
            if (Store.TryGetValue(path, out var existing))
            {
                var target = (JsonNode.Parse(existing) as JsonObject) ?? new JsonObject();
                if (JsonNode.Parse(json) is JsonObject patch)
                {
                    foreach (var pair in patch)
                    {
                        if (pair.Value is null || pair.Value.GetValueKind() == JsonValueKind.Null)
                        {
                            target.Remove(pair.Key);
                        }
                        else
                        {
                            target[pair.Key] = pair.Value?.DeepClone();
                        }
                    }
                }

                Store[path] = target.ToJsonString();
            }
            else
            {
                Store[path] = json;
            }

            return Task.CompletedTask;
        }
        public Task<IDisposable> StreamAsync(string path, Action<string?> onData, Action<Exception> onError, CancellationToken ct)
        {
            Streams.Add(path);
            return Task.FromResult<IDisposable>(new FakeDisposable());
        }
        public Task<long> FetchServerTimeOffsetMsAsync(CancellationToken ct) => Task.FromResult(0L);
        public void Dispose() { }

        private sealed class FakeDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    private static string ValidSeedControl(string deviceId)
    {
        var key = PackageKeys.Encode("example.com");
        var seed = new JsonObject
        {
            ["schemaVersion"] = PolicyConstants.SYNC_PROTOCOL_VERSION,
            ["revisionId"] = "seed",
            ["updatedAt"] = new JsonObject { [".sv"] = "timestamp" },
            ["updatedBy"] = "seedowner",
            ["apps"] = new JsonObject
            {
                [key] = new JsonObject
                {
                    ["packageName"] = "example.com",
                    ["packageKey"] = key,
                    ["manualBlocked"] = false
                }
            },
            ["modes"] = new JsonObject(),
            ["safeMode"] = new JsonObject { ["enabled"] = false, ["until"] = 0 }
        };
        return seed.ToJsonString();
    }

    private static SyncEngine NewEngine(FakeFirebase owner)
    {
        var device = new FakeFirebase { Uid = "device1" };
        var engine = new SyncEngine(device, new FakeSecretStore(), "device1", TimeProvider.System);
        engine.SetOwnerClient(owner);
        return engine;
    }

    [Fact]
    public async Task WriteControlV2ForDeviceAsync_MergesAndStampsAsOwner()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        var key = PackageKeys.Encode("example.com");
        owner.Store[FirebasePaths.DeviceControlV2("dev2")] = ValidSeedControl("dev2");

        var patch = "{\"apps\":{\"" + key + "\":{\"packageName\":\"example.com\",\"manualBlocked\":true}}}";
        var (ok, error, _) = await engine.WriteControlV2ForDeviceAsync("dev2", patch, CancellationToken.None);

        Assert.True(ok, error);
        var putJson = owner.Puts[FirebasePaths.DeviceControlV2("dev2")];
        var parsed = ControlProtocol.Parse(putJson);
        Assert.Equal(ControlParseStatus.Valid, parsed.Status);
        Assert.NotNull(parsed.Snapshot);
        var (pkg, rule) = parsed.Snapshot!.EffectiveApps().First();
        Assert.Equal("example.com", pkg);
        Assert.True(rule.ManualBlocked);
        Assert.Equal("owner1", parsed.Snapshot.UpdatedBy);
    }

    [Fact]
    public async Task WriteControlV2ForDeviceAsync_RejectsInvalidMergedSnapshot()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        owner.Store[FirebasePaths.DeviceControlV2("dev3")] = ValidSeedControl("dev3");

        // budget out of range makes the whole merged snapshot invalid.
        var (ok, error, _) = await engine.WriteControlV2ForDeviceAsync("dev3",
            "{\"budget\":{\"dailyLimitMinutes\":99999}}", CancellationToken.None);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.False(owner.Puts.ContainsKey(FirebasePaths.DeviceControlV2("dev3")));
    }

    [Fact]
    public async Task ReadControlV2Async_ReturnsSeededSnapshot()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        owner.Store[FirebasePaths.DeviceControlV2("dev2")] = ValidSeedControl("dev2");

        var (snapshot, raw) = await engine.ReadControlV2Async("dev2", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.False(string.IsNullOrEmpty(raw));
        Assert.True(snapshot!.EffectiveApps().Any(a => a.Key == "example.com"));
    }

    [Fact]
    public async Task RequestUnlockForDeviceAsync_AcceptsRawPackageName()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        const string reqId = "req123";
        owner.Store[FirebasePaths.DeviceUnlockRequests("dev2")] =
            "{\"" + reqId + "\":" + PendingUnlockRequest(reqId) + "}";

        // Pass the raw package name (not the encoded key); the engine decodes-or-uses-as-is.
        var (ok, error) = await engine.RequestUnlockForDeviceAsync("dev2", "example.com",
            PolicyConstants.UNLOCK_APPROVAL_TIMED, 15 * 60_000, CancellationToken.None);

        Assert.True(ok, error);
        Assert.Empty(owner.Puts);
        var patchPath = FirebasePaths.DeviceUnlockRequest("dev2", reqId);
        Assert.Contains(patchPath, owner.Patches.Keys);
        using var doc = JsonDocument.Parse(owner.Patches[patchPath]);
        Assert.Equal("approved", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReadDeviceListAsync_ReturnsRawDeviceList()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        var listJson = "{\"devA\":{\"label\":\"Laptop A\",\"online\":true},\"devB\":{\"label\":\"Laptop B\",\"online\":false}}";
        owner.Store[FirebasePaths.UserDevices("owner1")] = listJson;

        var raw = await engine.ReadDeviceListAsync("owner1", CancellationToken.None);

        Assert.Equal(listJson, raw);
    }

    // ----------------------------------------------------------------- deletes via null patch

    [Fact]
    public async Task WriteControlV2ForDeviceAsync_DeletesAppViaNullPatch()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        var key = PackageKeys.Encode("example.com");
        owner.Store[FirebasePaths.DeviceControlV2("dev2")] = ValidSeedControl("dev2");

        var (ok, error, _) = await engine.WriteControlV2ForDeviceAsync("dev2",
            "{\"apps\":{\"" + key + "\":null}}", CancellationToken.None);

        Assert.True(ok, error);
        var putJson = owner.Puts[FirebasePaths.DeviceControlV2("dev2")];
        var parsed = ControlProtocol.Parse(putJson);
        Assert.Equal(ControlParseStatus.Valid, parsed.Status);
        Assert.NotNull(parsed.Snapshot);
        Assert.DoesNotContain(key, putJson);
        Assert.Empty(parsed.Snapshot!.Apps);
    }

    [Fact]
    public async Task WriteControlV2ForDeviceAsync_NullPatchRemovesBudget()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        var seed = JsonNode.Parse(ValidSeedControl("dev3"))!.AsObject();
        seed["budget"] = new JsonObject { ["dailyLimitMinutes"] = 120 };
        owner.Store[FirebasePaths.DeviceControlV2("dev3")] = seed.ToJsonString();

        var (ok, error, _) = await engine.WriteControlV2ForDeviceAsync("dev3",
            "{\"budget\":null}", CancellationToken.None);

        Assert.True(ok, error);
        var parsed = ControlProtocol.Parse(owner.Puts[FirebasePaths.DeviceControlV2("dev3")]);
        Assert.Equal(ControlParseStatus.Valid, parsed.Status);
        Assert.Null(parsed.Snapshot!.Budget);
    }

    // ---------------------------------------------------------------- remote unlock approvals

    private static string PendingUnlockRequest(string requestId)
    {
        return new JsonObject
        {
            ["requestId"] = requestId,
            ["packageName"] = "example.com",
            ["reason"] = "askParent",
            ["status"] = "pending",
            ["createdAt"] = 1_000_000,
            ["expiresAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000,
        }.ToJsonString();
    }

    [Fact]
    public async Task RequestUnlockForDeviceAsync_ApprovesExistingPending()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        var key = PackageKeys.Encode("example.com");
        const string reqId = "req123";
        owner.Store[FirebasePaths.DeviceUnlockRequests("dev2")] =
            "{\"" + reqId + "\":" + PendingUnlockRequest(reqId) + "}";

        var (ok, error) = await engine.RequestUnlockForDeviceAsync("dev2", "example.com", "timed", 900_000, CancellationToken.None);

        Assert.True(ok, error);
        // The existing request is PATCHed in place; nothing new is created.
        Assert.Empty(owner.Puts);
        var patchPath = FirebasePaths.DeviceUnlockRequest("dev2", reqId);
        Assert.Contains(patchPath, owner.Patches.Keys);
        using var doc = JsonDocument.Parse(owner.Patches[patchPath]);
        var root = doc.RootElement;
        Assert.Equal("approved", root.GetProperty("status").GetString());
        Assert.Equal("timed", root.GetProperty("approvalType").GetString());
        Assert.Equal(900_000, root.GetProperty("approvalDurationMs").GetInt64());
        // Immutable request fields are not rewritten by an approval.
        Assert.False(root.TryGetProperty("requestId", out _));
    }

    [Fact]
    public async Task RequestUnlockForDeviceAsync_NoPendingReturnsError()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        var key = PackageKeys.Encode("example.com");

        var (ok, error) = await engine.RequestUnlockForDeviceAsync("dev2", "example.com", "oneVisit", null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("pending", error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(owner.Puts);
        Assert.Empty(owner.Patches);
    }

    [Fact]
    public async Task RequestUnlockForDeviceAsync_RejectsInvalidTimedDuration()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        var key = PackageKeys.Encode("example.com");
        const string reqId = "req1";
        owner.Store[FirebasePaths.DeviceUnlockRequests("dev2")] =
            "{\"" + reqId + "\":" + PendingUnlockRequest(reqId) + "}";

        var (ok, error) = await engine.RequestUnlockForDeviceAsync("dev2", "example.com", "timed", 600_000, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("15 or 30", error);
        Assert.Empty(owner.Puts);
        Assert.Empty(owner.Patches);
    }

    [Fact]
    public async Task RequestUnlockForDeviceAsync_OneVisitOmitsDuration()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        var key = PackageKeys.Encode("example.com");
        const string reqId = "req1";
        owner.Store[FirebasePaths.DeviceUnlockRequests("dev2")] =
            "{\"" + reqId + "\":" + PendingUnlockRequest(reqId) + "}";

        var (ok, error) = await engine.RequestUnlockForDeviceAsync("dev2", "example.com", "oneVisit", null, CancellationToken.None);

        Assert.True(ok, error);
        using var doc = JsonDocument.Parse(owner.Patches[FirebasePaths.DeviceUnlockRequest("dev2", reqId)]);
        var root = doc.RootElement;
        Assert.Equal("approved", root.GetProperty("status").GetString());
        Assert.Equal("oneVisit", root.GetProperty("approvalType").GetString());
        Assert.False(root.TryGetProperty("approvalDurationMs", out _));
    }

    // ------------------------------------------------------------ conflict retry

    private sealed class ConflictFakeFirebase : FakeFirebase
    {
        private int _puts;

        public override Task PutAsync(string path, string json, CancellationToken ct)
        {
            var first = _puts++;
            // On the first PUT for control/v2, simulate a concurrent writer winning between
            // our read and our PUT: their snapshot is based on the ORIGINAL seed (so our
            // patch is absent) plus their own change, and it replaces ours.
            if (first == 0 && path.Contains("control/v2"))
            {
                var concurrent = JsonNode.Parse(Store[path])!.AsObject();
                concurrent["revisionId"] = "concurrent-rev";
                concurrent["updatedBy"] = "concurrent-owner";
                concurrent["budget"] = new JsonObject { ["dailyLimitMinutes"] = 90 };
                Store[path] = concurrent.ToJsonString();
                return Task.CompletedTask;
            }

            return base.PutAsync(path, json, ct);
        }
    }

    [Fact]
    public async Task WriteControlV2ForDeviceAsync_RetriesWhenConcurrentWriterWins()
    {
        var owner = new ConflictFakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        var key = PackageKeys.Encode("example.com");
        owner.Store[FirebasePaths.DeviceControlV2("dev2")] = ValidSeedControl("dev2");

        var (ok, error, _) = await engine.WriteControlV2ForDeviceAsync("dev2",
            "{\"apps\":{\"" + key + "\":{\"packageName\":\"example.com\",\"manualBlocked\":true}}}", CancellationToken.None);

        Assert.True(ok, error);
        // The final snapshot merged BOTH writers: our patch (manualBlocked) AND the
        // concurrent writer's budget. If no retry happened, manualBlocked would still be
        // false (the concurrent snapshot was built from the untouched seed).
        var stored = owner.Store[FirebasePaths.DeviceControlV2("dev2")];
        var parsed = ControlProtocol.Parse(stored);
        Assert.Equal(ControlParseStatus.Valid, parsed.Status);
        Assert.True(parsed.Snapshot!.EffectiveApps()["example.com"].ManualBlocked);
        Assert.Equal(90, parsed.Snapshot.Budget?.DailyLimitMinutes);
        Assert.NotEqual("concurrent-rev", parsed.Snapshot.RevisionId); // our revision won
    }

    // ------------------------------------------------------------ SSE stream wiring

    [Fact]
    public async Task StartAsync_SubscribesToTargetedChildStreams_NeverTheRoot()
    {
        var device = new FakeFirebase { Uid = "device1" };
        var engine = new SyncEngine(device, new FakeSecretStore(), "device1", TimeProvider.System);
        engine.SetOwnerClient(new FakeFirebase { Uid = "owner1" });

        await engine.StartAsync(CancellationToken.None);

        // The device-root stream would echo the whole node on every self-write;
        // targeted child streams deliver only what the agent consumes.
        Assert.Contains(FirebasePaths.DeviceControlV2("device1"), device.Streams);
        Assert.Contains(FirebasePaths.DeviceSyncDesired("device1"), device.Streams);
        Assert.Contains(FirebasePaths.DeviceCommands("device1"), device.Streams);
        Assert.Contains(FirebasePaths.PairRequests("device1"), device.Streams);
        Assert.Contains(".info/connected", device.Streams);
        Assert.DoesNotContain(FirebasePaths.DeviceRoot("device1"), device.Streams);
    }

    // ------------------------------------------------- write result revisionId + silent drops

    [Fact]
    public async Task WriteControlV2ForDeviceAsync_ReturnsFreshRevisionId()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        var key = PackageKeys.Encode("example.com");
        owner.Store[FirebasePaths.DeviceControlV2("dev2")] = ValidSeedControl("dev2");

        var (ok, error, revisionId) = await engine.WriteControlV2ForDeviceAsync("dev2",
            "{\"apps\":{\"" + key + "\":{\"packageName\":\"example.com\",\"manualBlocked\":true}}}", CancellationToken.None);

        Assert.True(ok, error);
        Assert.False(string.IsNullOrEmpty(revisionId));
        Assert.NotEqual("seed", revisionId);
        // The stored node's revisionId is the one we reported to the caller — the console
        // waits for sync/applied.revisionId to reach this exact value.
        var stored = ControlProtocol.Parse(owner.Store[FirebasePaths.DeviceControlV2("dev2")]);
        Assert.Equal(revisionId, stored.Snapshot!.RevisionId);
    }

    [Fact]
    public async Task WriteControlV2ForDeviceAsync_UnknownActiveModeReturnsErrorNotOk()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        owner.Store[FirebasePaths.DeviceControlV2("dev2")] = ValidSeedControl("dev2");

        // The lenient parser drops an activeMode pointing at a nonexistent mode; the write
        // must surface that instead of PUTting a snapshot without the change.
        var (ok, error, _) = await engine.WriteControlV2ForDeviceAsync("dev2",
            "{\"activeMode\":{\"modeId\":\"missing-mode\",\"modeName\":\"Nope\"}}", CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("missing-mode", error);
        Assert.False(owner.Puts.ContainsKey(FirebasePaths.DeviceControlV2("dev2")));
    }

    [Fact]
    public async Task WriteControlV2ForDeviceAsync_BlankNameModeReturnsErrorNotOk()
    {
        var owner = new FakeFirebase { Uid = "owner1" };
        var engine = NewEngine(owner);
        owner.Store[FirebasePaths.DeviceControlV2("dev2")] = ValidSeedControl("dev2");

        var (ok, error, _) = await engine.WriteControlV2ForDeviceAsync("dev2",
            "{\"modes\":{\"m1\":{\"modeId\":\"m1\",\"name\":\"   \",\"apps\":{}}}}", CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("m1", error);
        Assert.False(owner.Puts.ContainsKey(FirebasePaths.DeviceControlV2("dev2")));
    }

    // ------------------------------------------------- desired settle decision

    [Fact]
    public void ShouldWaitForDesired_StaleOrMatchingDesiredNeverWaits()
    {
        var desired = new SyncDesiredRevision("rev-phone", "appPolicy");
        // Desired predates the pending control (dashboard/agent writes never touch
        // sync/desired): waiting would burn ~1s on every such write.
        Assert.False(SyncEngine.ShouldWaitForDesired(desired, "rev-new", desiredGeneration: 5, controlGeneration: 9));
        // Desired matches the snapshot already: nothing to wait for.
        Assert.False(SyncEngine.ShouldWaitForDesired(
            new SyncDesiredRevision("rev-new", "appPolicy"), "rev-new", desiredGeneration: 10, controlGeneration: 9));
        // No desired at all (never written on this project's owner path).
        Assert.False(SyncEngine.ShouldWaitForDesired(null, "rev-new", 0, 9));
        // Desired arrived after the control frame and mismatches: a parent write may
        // still be landing — wait (bounded by the settle window).
        Assert.True(SyncEngine.ShouldWaitForDesired(desired, "rev-new", desiredGeneration: 11, controlGeneration: 9));
    }
}
