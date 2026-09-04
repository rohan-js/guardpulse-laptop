namespace GuardPulse.Agent.Core.Tests;

using System.IO;
using GuardPulse.Agent.Core;
using Xunit;

public class SessionUsageTrackerTests : IDisposable
{
    // 2026-01-15T12:00:00Z; day keys are UTC "yyyyMMdd", so the UTC day here is 20260115
    // and the next rollover is 2026-01-16T00:00:00Z.
    private static readonly DateTimeOffset Base = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly long BaseMs = Base.ToUnixTimeMilliseconds();
    private static readonly long NextUtcMidnightMs =
        new DateTimeOffset(2026, 1, 16, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private const string AppA = "c:\\apps\\game.exe";
    private const string AppB = "c:\\apps\\writer.exe";

    private readonly string filePath;

    public SessionUsageTrackerTests()
    {
        this.filePath = Path.Combine(CreateTempDir(), "sessions.json");
    }

    // ------------------------------------------------------- accrual & pause

    [Fact]
    public void ConsecutiveTicks_WhileForeground_Accrue()
    {
        var time = new FakeTimeProvider(BaseMs);
        var tracker = NewTracker(time);

        Tick(tracker, time, AppA, BaseMs);                       // anchor: accrues nothing
        Tick(tracker, time, AppA, BaseMs + 5_000);
        Tick(tracker, time, AppA, BaseMs + 10_000);

        Assert.Equal(10_000, Effective(tracker, time, AppA));
    }

    [Fact]
    public void SwitchingToAnotherApp_PausesThePreviousAppsTimer()
    {
        var time = new FakeTimeProvider(BaseMs);
        var tracker = NewTracker(time);

        Tick(tracker, time, AppA, BaseMs);
        Tick(tracker, time, AppA, BaseMs + 10_000);              // A: 10s
        Tick(tracker, time, AppB, BaseMs + 15_000);              // switch: B anchors
        Tick(tracker, time, AppB, BaseMs + 25_000);              // B: 10s
        Tick(tracker, time, AppA, BaseMs + 30_000);              // return within 2min: resume, no accrual

        Assert.Equal(10_000, Effective(tracker, time, AppA));    // away time not charged
        Assert.Equal(10_000, Effective(tracker, time, AppB));

        Tick(tracker, time, AppA, BaseMs + 40_000);              // open again: accrues
        Assert.Equal(20_000, Effective(tracker, time, AppA));
    }

    [Fact]
    public void NullForeground_PausesAccrual_WithoutResetting()
    {
        var time = new FakeTimeProvider(BaseMs);
        var tracker = NewTracker(time);

        Tick(tracker, time, AppA, BaseMs);
        Tick(tracker, time, AppA, BaseMs + 10_000);              // A: 10s
        Tick(tracker, time, null, BaseMs + 15_000);              // unidentified foreground: paused

        Assert.Equal(10_000, Effective(tracker, time, AppA));

        Tick(tracker, time, AppA, BaseMs + 20_000);              // back within 2min: re-anchor, unknown gap not charged
        Tick(tracker, time, AppA, BaseMs + 30_000);              // open again: accrues

        Assert.Equal(20_000, Effective(tracker, time, AppA));
    }

    // ---------------------------------------------------------- reset on gap

    [Fact]
    public void SameApp_GapOverTwoMinutes_ResetsSession()
    {
        var time = new FakeTimeProvider(BaseMs);
        var tracker = NewTracker(time);

        Tick(tracker, time, AppA, BaseMs);
        Tick(tracker, time, AppA, BaseMs + 60_000);              // A: 60s
        Tick(tracker, time, AppA, BaseMs + 60_000 + SessionUsageTracker.ResetGapMs + 1_000);

        Assert.Equal(0, Effective(tracker, time, AppA));

        Tick(tracker, time, AppA, BaseMs + 60_000 + SessionUsageTracker.ResetGapMs + 6_000);

        Assert.Equal(5_000, Effective(tracker, time, AppA));     // fresh session from the return
    }

    [Fact]
    public void OtherApp_GapOverTwoMinutes_ResetsOnReturn()
    {
        var time = new FakeTimeProvider(BaseMs);
        var tracker = NewTracker(time);

        Tick(tracker, time, AppA, BaseMs);
        Tick(tracker, time, AppA, BaseMs + 60_000);              // A: 60s
        Tick(tracker, time, AppB, BaseMs + 65_000);              // B takes over
        Tick(tracker, time, AppB, BaseMs + 185_000);             // B keeps the screen for 2min+
        Tick(tracker, time, AppA, BaseMs + 190_000);             // A returns 130s after its last tick

        Assert.Equal(0, Effective(tracker, time, AppA));         // away >= 2min: session reset

        Tick(tracker, time, AppA, BaseMs + 195_000);

        Assert.Equal(5_000, Effective(tracker, time, AppA));
    }

    [Fact]
    public void OtherApp_ShortGap_ResumesWithoutReset()
    {
        var time = new FakeTimeProvider(BaseMs);
        var tracker = NewTracker(time);

        Tick(tracker, time, AppA, BaseMs);
        Tick(tracker, time, AppA, BaseMs + 60_000);              // A: 60s
        Tick(tracker, time, AppB, BaseMs + 65_000);              // brief detour
        Tick(tracker, time, AppA, BaseMs + 120_000);             // back 60s later: pause, not reset
        Tick(tracker, time, AppA, BaseMs + 130_000);

        Assert.Equal(70_000, Effective(tracker, time, AppA));
    }

    // ------------------------------------------------------------ day rollover

    [Fact]
    public void UtcDayRollover_ResetsEverySession()
    {
        var time = new FakeTimeProvider(BaseMs);
        var tracker = NewTracker(time);

        Tick(tracker, time, AppA, BaseMs);
        Tick(tracker, time, AppA, BaseMs + 60_000);              // A: 60s
        Tick(tracker, time, AppB, BaseMs + 65_000);
        Tick(tracker, time, AppB, BaseMs + 125_000);             // B: 60s

        var afterMidnight = NextUtcMidnightMs + 5_000;
        Tick(tracker, time, AppA, afterMidnight);

        Assert.Equal(0, Effective(tracker, time, AppA));
        Assert.Equal(0, Effective(tracker, time, AppB));

        Tick(tracker, time, AppA, afterMidnight + 5_000);

        Assert.Equal(5_000, Effective(tracker, time, AppA));     // accrual restarts on the new day
    }

    // ----------------------------------------------------------------- reset()

    [Fact]
    public void Reset_ZeroesSession_AndPersists()
    {
        var time = new FakeTimeProvider(BaseMs);
        var tracker = NewTracker(time);

        Tick(tracker, time, AppA, BaseMs);
        Tick(tracker, time, AppA, BaseMs + 120_000);             // A: 120s (at the gap boundary: still accrues)

        tracker.Reset(AppA);

        Assert.Equal(0, Effective(tracker, time, AppA));
        Assert.Equal(0, NewTracker(time).EffectiveSessionMs(AppA, time.NowMs())); // hit the disk

        Tick(tracker, time, AppA, BaseMs + 125_000);             // re-anchor, no accrual of the gap
        Tick(tracker, time, AppA, BaseMs + 130_000);

        Assert.Equal(5_000, Effective(tracker, time, AppA));
    }

    // ------------------------------------------------------------- persistence

    [Fact]
    public void AccruedSession_SurvivesNewTrackerInstance()
    {
        var time = new FakeTimeProvider(BaseMs);
        var tracker = NewTracker(time);

        Tick(tracker, time, AppA, BaseMs);                       // first flush (debounce gate open)
        Tick(tracker, time, AppA, BaseMs + 10_000);              // 10s: still inside the 60s debounce
        Tick(tracker, time, AppA, BaseMs + 61_000);              // 61s of accrual; debounce elapsed: flushed

        var reloaded = NewTracker(time);

        Assert.Equal(61_000, reloaded.EffectiveSessionMs(AppA, time.NowMs()));
    }

    [Fact]
    public void CorruptStateFile_StartsWithAnEmptyTracker()
    {
        File.WriteAllText(this.filePath, "{ not json");
        var time = new FakeTimeProvider(BaseMs);
        var tracker = NewTracker(time);

        Assert.Equal(0, Effective(tracker, time, AppA));

        Tick(tracker, time, AppA, BaseMs);
        Tick(tracker, time, AppA, BaseMs + 5_000);

        Assert.Equal(5_000, Effective(tracker, time, AppA));     // functional after the bad read
    }

    [Fact]
    public void StaleDayKeyOnDisk_IsDroppedOnLoad()
    {
        var yesterday = new DateTimeOffset(2026, 1, 14, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        File.WriteAllText(this.filePath,
            "{\"Apps\":{\"" + AppA.Replace("\\", "\\\\") + "\":{\"MsToday\":600000,\"DayKey\":\"20260114\",\"LastTickMs\":" + yesterday + "}}}");

        var time = new FakeTimeProvider(BaseMs);
        var tracker = NewTracker(time);

        Assert.Equal(0, tracker.EffectiveSessionMs(AppA, BaseMs)); // foreign day: fresh session
    }

    // ---------------------------------------------------------------- helpers

    private SessionUsageTracker NewTracker(FakeTimeProvider time) => new(this.filePath, time);

    /// <summary>Keeps the fake clock in sync with the tick timestamp (the tracker reads the clock for day keys and persist debouncing).</summary>
    private static void Tick(SessionUsageTracker tracker, FakeTimeProvider time, string? appKey, long nowMs)
    {
        time.SetUtcNow(nowMs);
        tracker.Tick(appKey, nowMs);
    }

    private static long Effective(SessionUsageTracker tracker, FakeTimeProvider time, string appKey)
    {
        return tracker.EffectiveSessionMs(appKey, time.NowMs());
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "guardpulse-session-tracker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            var directory = Path.GetDirectoryName(this.filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }
}
