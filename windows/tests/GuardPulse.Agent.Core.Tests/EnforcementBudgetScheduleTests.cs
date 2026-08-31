using GuardPulse.Agent.Core;
using GuardPulse.Agent.Core.Tests;
using GuardPulse.Protocol;
using Xunit;

public class EnforcementBudgetScheduleTests : IDisposable
{
    private readonly string stateDir = UsageLedgerTestsHelper.CreateTempDir();

    [Fact]
    public void ScheduleInsideWindowDoesNotLockOutsideDoes()
    {
        var midday = new DateTimeOffset(2026, 3, 15, 13, 0, 0, TimeSpan.Zero);
        var early = new DateTimeOffset(2026, 3, 15, 7, 0, 0, TimeSpan.Zero);
        var snapshot = new ControlSnapshotV2("rev-window")
        {
            Schedule = new ControlSchedule(Enabled: true, StartMinute: 9 * 60, EndMinute: 17 * 60)
        };
        Assert.False(new EnforcementEngine(new FakeTimeProvider(midday)).OutsideAllowedHours(snapshot));
        Assert.True(new EnforcementEngine(new FakeTimeProvider(early)).OutsideAllowedHours(snapshot));
    }

    [Fact]
    public void ScheduleWrapsPastMidnight()
    {
        var midnight = new DateTimeOffset(2026, 3, 15, 23, 30, 0, TimeSpan.Zero);
        var midday = new DateTimeOffset(2026, 3, 15, 13, 0, 0, TimeSpan.Zero);
        var snapshot = new ControlSnapshotV2("rev-wrap") { Schedule = new ControlSchedule(true, 21 * 60, 7 * 60) };
        Assert.False(new EnforcementEngine(new FakeTimeProvider(midnight)).OutsideAllowedHours(snapshot));
        Assert.True(new EnforcementEngine(new FakeTimeProvider(midday)).OutsideAllowedHours(snapshot));
    }

    [Fact]
    public void AllowlistStaticHelperBlocksUnknownNonWindowsPaths()
    {
        var snapshot = new ControlSnapshotV2("rev-allow") { Allowlist = new ControlAllowlist(true) };
        Assert.True(EnforcementEngine.IsBlockedByAllowlist(snapshot, "c:\\x\\y.exe"));
    }

    [Fact]
    public void BudgetTripsWhenDeviceTotalReachesLimit()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero));
        var ledger = new UsageLedger(stateDir, clock) { MonotonicTicks = static () => 0L };
        ledger.OnForegroundChanged("c:\\a.exe", clock.GetUtcNow().ToUnixTimeMilliseconds());
        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(10).ToUnixTimeMilliseconds());
        ledger.OnForegroundChanged("c:\\b.exe", clock.GetUtcNow().ToUnixTimeMilliseconds());
        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(6).ToUnixTimeMilliseconds());
        var snapshot = new ControlSnapshotV2("rev-budget") { Budget = new ControlBudget(15) };
        Assert.True(new EnforcementEngine(clock).BudgetExceeded(snapshot, ledger));
    }

    public void Dispose()
    {
        try { Directory.Delete(stateDir, recursive: true); } catch { /* ignore */ }
    }
}

internal static class UsageLedgerTestsHelper
{
    public static string CreateTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), "guardpulse-enforcement-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }
}
