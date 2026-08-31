namespace GuardPulse.Agent.Core.Tests;

using GuardPulse.Agent.Core;
using Xunit;

public class PinRetryPolicyTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly long BaseMs = Base.ToUnixTimeMilliseconds();

    private readonly string stateDir = CreateTempDir();

    [Fact]
    public void Ladder_FollowsBoundaryValues()
    {
        var expectedBlocksMs = new long[]
        {
            1_000, 1_000, 1_000,     // failures 1-3
            5_000,                    // failure 4
            15_000,                   // failure 5
            30_000,                   // failure 6
            60_000,                   // failure 7  (30s * 2^1)
            120_000,                  // failure 8  (30s * 2^2)
            240_000,                  // failure 9  (30s * 2^3)
            300_000,                  // failure 10 (480s capped at 5min)
            300_000,                  // failure 11 (still capped)
        };

        var time = new FakeTimeProvider(BaseMs);
        var policy = new PinRetryPolicy(this.stateDir, time);
        var now = BaseMs;

        for (var failure = 1; failure <= expectedBlocksMs.Length; failure++)
        {
            time.SetUtcNow(now);
            policy.RecordFailure();

            Assert.True(policy.IsBlocked(), $"failure {failure} must block");
            Assert.Equal(now + expectedBlocksMs[failure - 1], policy.BlockedUntilMs());

            now += expectedBlocksMs[failure - 1]; // exactly at the deadline the block has lapsed
            time.SetUtcNow(now);
            Assert.False(policy.IsBlocked(), $"failure {failure} block must end at the deadline");
            Assert.Equal(0, policy.BlockedUntilMs());
        }
    }

    [Fact]
    public void RecordSuccess_ClearsActiveBlockAndResetsLadder()
    {
        var time = new FakeTimeProvider(BaseMs);
        var policy = new PinRetryPolicy(this.stateDir, time);

        policy.RecordFailure(); // 1s block
        policy.RecordFailure(); // 1s block
        policy.RecordFailure(); // 1s block
        policy.RecordFailure(); // 5s block
        Assert.True(policy.IsBlocked());

        policy.RecordSuccess();
        Assert.False(policy.IsBlocked());
        Assert.Equal(0, policy.BlockedUntilMs());

        time.SetUtcNow(BaseMs + 60_000);
        policy.RecordFailure(); // back to the first rung, not the fifth
        Assert.Equal(time.NowMs() + 1_000, policy.BlockedUntilMs());
    }

    [Fact]
    public void RecordSuccess_WithoutFailures_IsHarmless()
    {
        var policy = new PinRetryPolicy(this.stateDir, new FakeTimeProvider(BaseMs));

        policy.RecordSuccess();

        Assert.False(policy.IsBlocked());
        Assert.Equal(0, policy.BlockedUntilMs());
    }

    [Fact]
    public void State_PersistsAcrossRestart()
    {
        var time = new FakeTimeProvider(BaseMs);
        var first = new PinRetryPolicy(this.stateDir, time);
        first.RecordFailure();
        first.RecordFailure();
        first.RecordFailure();
        first.RecordFailure(); // 5s block

        var reloaded = new PinRetryPolicy(this.stateDir, time);
        Assert.True(reloaded.IsBlocked());
        Assert.Equal(BaseMs + 5_000, reloaded.BlockedUntilMs());
    }

    [Fact]
    public void Ladder_ContinuesAcrossRestart()
    {
        var time = new FakeTimeProvider(BaseMs);
        var first = new PinRetryPolicy(this.stateDir, time);
        for (var i = 0; i < 4; i++)
        {
            first.RecordFailure();
            time.AdvanceMs(6_000); // past each block
        }

        var reloaded = new PinRetryPolicy(this.stateDir, time);
        reloaded.RecordFailure(); // fifth failure overall
        Assert.Equal(time.NowMs() + 15_000, reloaded.BlockedUntilMs());
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "guardpulse-pin-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.stateDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }
}
