namespace GuardPulse.Agent.Core.Tests;

using System.Text.Json.Nodes;
using GuardPulse.Agent.Core;
using GuardPulse.Protocol;
using Xunit;

/// <summary>
/// The 15s reconcile loop is the only defense against a silently-stalled SSE fan-out:
/// keep-alives keep the transport "connected" (2s poll off, idle timer fed) while
/// put/patch events are dropped — field-observed on two laptops where heartbeats and
/// message polls flowed for hours while NO control revision was applied. These tests
/// pin the loop's contract: unconditional GETs of both tiny nodes, replay no-ops for
/// unchanged content, and the direct re-ack of an applied-but-unacknowledged revision
/// (heals the burnt 3-attempt ack budget without waiting for the next parent write).
/// </summary>
public sealed class SyncEngineReconcileTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly string DeviceId = "test-device";
    private static readonly string ControlPath = FirebasePaths.DeviceControlV2(DeviceId);
    private static readonly string DesiredPath = FirebasePaths.DeviceSyncDesired(DeviceId);
    private static readonly string AppliedPath = FirebasePaths.DeviceSyncApplied(DeviceId);
    private const string SnapshotSecretKey = "snapshot.v2";

    private const string ControlRev2 = """
        { "schemaVersion": 2, "revisionId": "rev-200", "updatedAt": 1710000000000, "updatedBy": "parent-1",
          "apps": {}, "modes": {},
          "safeMode": { "enabled": false, "until": 0, "startedAt": null, "startedBy": null },
          "pin": { "salt": "MDEyMzQ1Njc4OWFiY2RlZg", "hash": "9UI-qC10YGAs3EmhUfE4S5cFc2P_Psp3Kwc3_nbS1gc", "version": 2, "algorithm": "PBKDF2WithHmacSHA256", "iterations": 210000, "updatedAt": 1710000000500 } }
        """;

    private const string DesiredRev2 = """
        { "revisionId": "rev-200", "kind": "appPolicy", "requestedAt": 1710000000000, "requestedBy": "parent-1", "target": "app.exe" }
        """;

    [Fact]
    public async Task ReconcileLoopGetsBothNodesUnconditionally()
    {
        var (engine, firebase, _, _) = Harness();
        firebase.DesiredResponse = DesiredRev2;
        firebase.ControlResponse = ControlRev2;

        // Work-first loop: the first reconcile cycle completes inline (completed-task
        // fakes) before the loop parks on its interval delay.
        using var cts = new CancellationTokenSource();
        var loop = engine.ReconcileLoopAsync(cts.Token);
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(DesiredPath, firebase.Gets);
        Assert.Contains(ControlPath, firebase.Gets);
    }

    [Fact]
    public async Task ReconcileLoopReacksAppliedButUnacknowledgedRevision()
    {
        var (engine, firebase, _, _) = Harness();
        // rev-200 arrived (as the SSE stream would deliver it) and is enforced, its
        // ack never landed; the reconcile GETs replay the same nodes.
        engine.HandleControlData(ControlRev2);
        engine.HandleDesiredData(DesiredRev2);
        firebase.DesiredResponse = DesiredRev2;
        firebase.ControlResponse = ControlRev2;

        using var cts = new CancellationTokenSource();
        var loop = engine.ReconcileLoopAsync(cts.Token);
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        var ack = Assert.Single(firebase.Patches);
        Assert.Equal(AppliedPath, ack.Path);
        Assert.Contains("rev-200", ack.Json);
        Assert.Contains("\"applied\"", ack.Json);
    }

    [Fact]
    public async Task NotifyAppliedWithExtras_RidesSingleRootPatch()
    {
        var (engine, firebase, _, secrets) = Harness();
        engine.HandleControlData(ControlRev2);
        engine.HandleDesiredData(DesiredRev2);

        // Batched apply: ack + state diff + telemetry in ONE multi-path PATCH.
        var extras = new JsonObject
        {
            ["state/apps"] = new JsonObject { ["c2FtcGxlLmV4ZQ"] = new JsonObject { ["lockBlocked"] = true } },
            ["sync/runtime"] = new JsonObject { ["pipelineLatencyMs"] = 187, ["lastPolicyAppliedAt"] = 1710000000000 }
        };
        await engine.NotifyEnforcementAppliedAsync("rev-200", extras);

        var patch = Assert.Single(firebase.Patches);
        Assert.Equal("devices/test-device", patch.Path); // root PATCH, not the applied node
        var body = JsonNode.Parse(patch.Json)!.AsObject();
        Assert.Equal("rev-200", body["sync/applied"]!["revisionId"]!.GetValue<string>());
        Assert.True(body["state/apps"]!["c2FtcGxlLmV4ZQ"]!["lockBlocked"]!.GetValue<bool>());
        Assert.True(body["sync/runtime"]!["pipelineLatencyMs"]!.GetValue<long>() == 187);
    }

    [Fact]
    public async Task NotifyAppliedWithoutExtras_KeepsAppliedNodePatch()
    {
        var (engine, firebase, _, _) = Harness();
        engine.HandleControlData(ControlRev2);

        await engine.NotifyEnforcementAppliedAsync("rev-200");

        var patch = Assert.Single(firebase.Patches);
        Assert.Equal(AppliedPath, patch.Path); // unchanged legacy shape
        Assert.DoesNotContain("state/apps", patch.Json);
    }

    [Fact]
    public void HandleControlDataUnchangedRawReplayDoesNotChurn()
    {
        var (engine, _, _, secrets) = Harness();

        engine.HandleControlData(ControlRev2);
        engine.HandleControlData(ControlRev2);

        Assert.Equal(1, secrets.Sets.GetValueOrDefault(SnapshotSecretKey));
    }

    [Fact]
    public void HandleControlDataChangedRawStillProcesses()
    {
        var (engine, _, _, secrets) = Harness();

        engine.HandleControlData(ControlRev2);
        engine.HandleControlData(ControlRev2.Replace("rev-200", "rev-201"));

        Assert.Equal(2, secrets.Sets.GetValueOrDefault(SnapshotSecretKey));
    }

    private static (SyncEngine, RecordingFirebase, FakeTimeProvider, CountingSecretStore) Harness()
    {
        var time = new FakeTimeProvider(Start);
        var firebase = new RecordingFirebase();
        var secrets = new CountingSecretStore();
        var engine = new SyncEngine(firebase, secrets, DeviceId, time);
        return (engine, firebase, time, secrets);
    }

    private sealed class CountingSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new();

        public Dictionary<string, int> Sets { get; } = new();

        public string? Get(string key) => this.values.TryGetValue(key, out var value) ? value : null;

        public void Set(string key, string value)
        {
            this.Sets[key] = this.Sets.GetValueOrDefault(key) + 1;
            this.values[key] = value;
        }

        public void Delete(string key) => this.values.Remove(key);
    }

    /// <summary>Records GET/PATCH traffic and serves canned control/desired bodies.</summary>
    private sealed class RecordingFirebase : IFirebaseClient
    {
        public List<string> Gets { get; } = new();
        public List<(string Path, string Json)> Patches { get; } = new();
        public string? DesiredResponse { get; set; }
        public string? ControlResponse { get; set; }

        public string? Uid => null;

        public Task SignInAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetAsync(string path, CancellationToken ct)
        {
            this.Gets.Add(path);
            if (path == DesiredPath)
            {
                return Task.FromResult(this.DesiredResponse ?? "null");
            }

            if (path == ControlPath)
            {
                return Task.FromResult(this.ControlResponse ?? "null");
            }

            return Task.FromResult("null");
        }

        public Task PutAsync(string path, string json, CancellationToken ct) => Task.CompletedTask;

        public Task PatchAsync(string path, string json, CancellationToken ct)
        {
            this.Patches.Add((path, json));
            return Task.CompletedTask;
        }

        public Task<IDisposable> StreamAsync(string path, Action<string?> onData, Action<Exception> onError, CancellationToken ct, Action? onActivity = null)
            => Task.FromResult<IDisposable>(new NoopDisposable());

        public Task<long> FetchServerTimeOffsetMsAsync(CancellationToken ct) => Task.FromResult(0L);

        public void Dispose()
        {
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
