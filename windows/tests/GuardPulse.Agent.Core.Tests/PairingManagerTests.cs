namespace GuardPulse.Agent.Core.Tests;

using System.IO;
using GuardPulse.Agent.Core;
using Xunit;

public class PairingManagerTests : IDisposable
{
    private readonly string filePath;

    public PairingManagerTests()
    {
        this.filePath = Path.Combine(CreateTempDir(), "secrets.json");
    }

    [Fact]
    public void Validate_CurrentSecret_Matches()
    {
        var store = new MemorySecretStore();
        var pairing = new PairingManager(store);
        var (deviceId, secret, code) = pairing.GetOrCreate();
        var now = NowMs();

        Assert.True(pairing.Validate(secret, null, now - 60_000, now));
        Assert.True(pairing.Validate(null, code, now - 60_000, now));
        Assert.False(pairing.Validate("wrong", null, now - 60_000, now));
        Assert.False(pairing.Validate(null, "000000", now - 60_000, now));
    }

    [Fact]
    public void Validate_StaleRequestAge_Rejected()
    {
        var store = new MemorySecretStore();
        var pairing = new PairingManager(store);
        var (_, secret, _) = pairing.GetOrCreate();
        var now = NowMs();

        Assert.False(pairing.Validate(secret, null, now - (11 * 60_000), now));
    }

    [Fact]
    public void Validate_PreviousGeneration_StillMatches_WithinTtl()
    {
        var store = new MemorySecretStore();
        var pairing = new PairingManager(store);
        var (_, oldSecret, oldCode) = pairing.GetOrCreate();
        var scannedAt = NowMs();

        // Simulate a rotation boundary (2s tick refresh auto-rotated meanwhile).
        pairing.Rotate();

        // Old credentials: still valid within TTL (the rotation-boundary grace).
        Assert.True(pairing.Validate(oldSecret, null, scannedAt, scannedAt + 60_000));
        Assert.True(pairing.Validate(null, oldCode, scannedAt, scannedAt + 60_000));
    }

    [Fact]
    public void Validate_RequestCreatedBeforeRotation_ArrivingAfterRotation_Matches()
    {
        // The production shape of the rotation-boundary grace: the previous
        // generation becomes prev exactly at prevCreatedAt + TTL (the auto-rotation
        // trigger), so the request that scanned it seconds before the boundary is
        // validated when prev is already OLDER than one TTL. The grace window must
        // therefore be 2x TTL — with 1x it was zero-width and every boundary scan
        // was answered "expired", looping the user back to re-scan forever.
        var store = new MemorySecretStore();
        var pairing = new PairingManager(store);
        var (_, oldSecret, oldCode) = pairing.GetOrCreate();
        var prevMintedAt = NowMs();

        var requestCreatedAt = prevMintedAt + 9 * 60_000; // phone fires the request while the old QR is still current
        pairing.Rotate(); // ...and the 2s deviceInfo tick auto-rotates before the agent polls
        var now = prevMintedAt + 13 * 60_000; // agent validates 3 minutes after the rotation

        Assert.True(pairing.Validate(oldSecret, null, requestCreatedAt, now));
        Assert.True(pairing.Validate(null, oldCode, requestCreatedAt, now));
    }

    [Fact]
    public void Validate_GraceSlot_Expires_AfterDoubleTtl()
    {
        var store = new MemorySecretStore();
        var pairing = new PairingManager(store);
        var (_, oldSecret, _) = pairing.GetOrCreate();
        var prevMintedAt = NowMs();
        pairing.Rotate();

        // Request age is fine (9 min), but the prev generation is past 2x TTL:
        // the grace slot itself has expired.
        var requestCreatedAt = prevMintedAt + 12 * 60_000;
        var now = prevMintedAt + 21 * 60_000;
        Assert.False(pairing.Validate(oldSecret, null, requestCreatedAt, now));
    }

    [Fact]
    public void Validate_TwoGenerationsOld_Rejected()
    {
        var store = new MemorySecretStore();
        var pairing = new PairingManager(store);
        var (_, ancientSecret, _) = pairing.GetOrCreate();

        pairing.Rotate();
        pairing.Rotate(); // pushes the ancient one out of the grace slot

        Assert.False(pairing.Validate(ancientSecret, null, NowMs() - 60_000, NowMs()));
    }

    [Fact]
    public void Validate_GraceSlot_Expires_WithTtl()
    {
        var store = new MemorySecretStore();
        var pairing = new PairingManager(store);
        var (_, oldSecret, _) = pairing.GetOrCreate();
        var now = NowMs();
        pairing.Rotate();

        // The grace credential is still TTL-bound: an 11-minute-old presentation fails.
        Assert.False(pairing.Validate(oldSecret, null, now - (11 * 60_000), now));
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gp-pair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new();

        public string? Get(string key) => this.values.TryGetValue(key, out var value) ? value : null;

        public void Set(string key, string value) => this.values[key] = value;

        public void Delete(string key) => this.values.Remove(key);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(this.filePath)!, recursive: true); } catch { }
    }
}
