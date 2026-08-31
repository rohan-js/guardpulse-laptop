namespace GuardPulse.Agent.Core.Tests;

using GuardPulse.Agent.Core;
using Xunit;

public class OneVisitUnlocksTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly long BaseMs = Base.ToUnixTimeMilliseconds();

    private readonly string stateDir = CreateTempDir();

    [Fact]
    public void GrantWithoutDuration_HoldsUntilCleared()
    {
        var time = new FakeTimeProvider(BaseMs);
        var unlocks = new OneVisitUnlocks(this.stateDir, time);

        Assert.False(unlocks.IsUnlocked("app.a"));

        unlocks.Grant("app.a");
        time.Advance(TimeSpan.FromHours(12)); // one-visit grants do not expire on their own
        Assert.True(unlocks.IsUnlocked("app.a"));

        unlocks.Clear("app.a");
        Assert.False(unlocks.IsUnlocked("app.a"));
    }

    [Fact]
    public void TimedGrant_ExpiresExactlyAtDeadline()
    {
        var time = new FakeTimeProvider(BaseMs);
        var unlocks = new OneVisitUnlocks(this.stateDir, time);

        unlocks.Grant("app.a", TimeSpan.FromMinutes(5));

        time.AdvanceMs(5 * 60_000 - 1);
        Assert.True(unlocks.IsUnlocked("app.a"));

        time.AdvanceMs(1);
        Assert.False(unlocks.IsUnlocked("app.a"));
    }

    [Fact]
    public void TimedGrant_RegrantExtendsDeadline()
    {
        var time = new FakeTimeProvider(BaseMs);
        var unlocks = new OneVisitUnlocks(this.stateDir, time);

        unlocks.Grant("app.a", TimeSpan.FromMinutes(1));
        time.AdvanceMs(30_000);
        unlocks.Grant("app.a", TimeSpan.FromMinutes(1)); // fresh 1-minute window

        time.AdvanceMs(50_000); // 80s after the first grant, 50s after the re-grant
        Assert.True(unlocks.IsUnlocked("app.a"));
    }

    [Fact]
    public void ClearAll_RemovesEveryGrant()
    {
        var time = new FakeTimeProvider(BaseMs);
        var unlocks = new OneVisitUnlocks(this.stateDir, time);

        unlocks.Grant("app.a");
        unlocks.Grant("app.b", TimeSpan.FromMinutes(5));
        unlocks.ClearAll();

        Assert.False(unlocks.IsUnlocked("app.a"));
        Assert.False(unlocks.IsUnlocked("app.b"));
    }

    [Fact]
    public void Grants_PersistAcrossRestart()
    {
        var time = new FakeTimeProvider(BaseMs);
        var first = new OneVisitUnlocks(this.stateDir, time);
        first.Grant("app.a");
        first.Grant("app.b", TimeSpan.FromMinutes(10));

        var reloaded = new OneVisitUnlocks(this.stateDir, time);
        Assert.True(reloaded.IsUnlocked("app.a"));
        Assert.True(reloaded.IsUnlocked("app.b"));

        time.AdvanceMs(11 * 60_000);
        Assert.True(reloaded.IsUnlocked("app.a"));
        Assert.False(reloaded.IsUnlocked("app.b"));
    }

    [Fact]
    public void ExpiredTimedGrant_DoesNotResurrectAfterReload()
    {
        var time = new FakeTimeProvider(BaseMs);
        var first = new OneVisitUnlocks(this.stateDir, time);
        first.Grant("app.a", TimeSpan.FromMinutes(5));

        time.AdvanceMs(6 * 60_000);
        var reloaded = new OneVisitUnlocks(this.stateDir, time);
        Assert.False(reloaded.IsUnlocked("app.a"));
    }

    [Fact]
    public void UnknownApp_IsNeverUnlocked()
    {
        var unlocks = new OneVisitUnlocks(this.stateDir, new FakeTimeProvider(BaseMs));

        unlocks.Grant("app.a");

        Assert.False(unlocks.IsUnlocked("app.b"));
        Assert.True(unlocks.IsUnlocked("app.a"));
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "guardpulse-unlocks-tests-" + Guid.NewGuid().ToString("N"));
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
