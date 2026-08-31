namespace GuardPulse.Agent.Core.Tests;

using GuardPulse.Agent.Core;
using Xunit;

public class UsageLedgerTests : IDisposable
{
    // 2026-01-15T12:00:00Z; the fake time provider treats UTC as local, so the local day key
    // of this instant is 2026-01-15 and local midnight is 2026-01-16T00:00:00Z.
    private static readonly DateTimeOffset Base = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly long BaseMs = Base.ToUnixTimeMilliseconds();
    private static readonly long NextLocalMidnightMs =
        new DateTimeOffset(2026, 1, 16, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private readonly string stateDir = CreateTempDir();

    private UsageLedger NewLedger(FakeTimeProvider time)
    {
        // Pin the monotonic anchor: real ticks would add machine-dependent drift
        // to exact-value assertions (the clock-rollback tests clamp via wall only).
        return new UsageLedger(this.stateDir, time) { MonotonicTicks = static () => 0L };
    }

    [Fact]
    public void OnForegroundChanged_ClosesPreviousSessionAndOpensNew()
    {
        var time = new FakeTimeProvider(Base);
        var ledger = NewLedger(time);

        ledger.OnForegroundChanged("app.a", BaseMs);
        time.SetUtcNow(BaseMs + 30_000);
        ledger.OnForegroundChanged("app.b", BaseMs + 30_000);

        Assert.Equal(30_000, ledger.EffectiveUsageMsToday("app.a"));
        Assert.Equal(0, ledger.EffectiveUsageMsToday("app.b"));
        Assert.Equal(30_000, ledger.UsageMsToday()["app.a"]);
    }

    [Fact]
    public void OnForegroundChanged_SameAppAgain_ContinuesSession()
    {
        var time = new FakeTimeProvider(Base);
        var ledger = NewLedger(time);

        ledger.OnForegroundChanged("app.a", BaseMs);
        time.SetUtcNow(BaseMs + 10_000);
        ledger.OnForegroundChanged("app.a", BaseMs + 10_000);
        time.SetUtcNow(BaseMs + 25_000);

        Assert.Equal(25_000, ledger.EffectiveUsageMsToday("app.a"));
    }

    [Fact]
    public void BlipShorterThan2Seconds_MergesSessionsAndChargesTheGap()
    {
        var time = new FakeTimeProvider(Base);
        var ledger = NewLedger(time);

        ledger.OnForegroundChanged("app.a", BaseMs);                          // 0s
        ledger.OnForegroundChanged("app.b", BaseMs + 10_000);                 // closes A at 10s
        ledger.OnForegroundChanged("app.a", BaseMs + 11_000);                 // 1s gap -> merge
        ledger.OnForegroundChanged("app.c", BaseMs + 30_000);                 // closes A at 30s

        // Merged back to the original start: the 1s blip is charged to A (30s, not 29s).
        Assert.Equal(30_000, ledger.EffectiveUsageMsToday("app.a"));
        Assert.Equal(1_000, ledger.EffectiveUsageMsToday("app.b"));
        Assert.Equal(0, ledger.EffectiveUsageMsToday("app.c"));
    }

    [Fact]
    public void GapOfTwoSecondsOrMore_DoesNotMerge()
    {
        var time = new FakeTimeProvider(Base);
        var ledger = NewLedger(time);

        ledger.OnForegroundChanged("app.a", BaseMs);
        ledger.OnForegroundChanged("app.b", BaseMs + 10_000);
        ledger.OnForegroundChanged("app.a", BaseMs + 12_000);                 // 2s gap -> no merge
        ledger.OnForegroundChanged("app.c", BaseMs + 20_000);

        Assert.Equal(10_000 + 8_000, ledger.EffectiveUsageMsToday("app.a"));
    }

    [Fact]
    public void OpenSession_AccruesLiveUsage()
    {
        var time = new FakeTimeProvider(Base);
        var ledger = NewLedger(time);

        ledger.OnForegroundChanged("app.a", BaseMs);
        time.SetUtcNow(BaseMs + 5_000);

        Assert.Equal(5_000, ledger.EffectiveUsageMsToday("app.a"));
    }

    [Fact]
    public void SetResetOffset_ZeroesEffectiveUsage_KeepsRawLedger()
    {
        var time = new FakeTimeProvider(Base);
        var ledger = NewLedger(time);

        ledger.OnForegroundChanged("app.a", BaseMs);
        ledger.OnForegroundChanged("app.b", BaseMs + 60_000);

        ledger.SetResetOffset("app.a");
        Assert.Equal(0, ledger.EffectiveUsageMsToday("app.a"));
        Assert.Equal(60_000, ledger.UsageMsToday()["app.a"]);

        // Only usage accrued after the reset counts again.
        ledger.OnForegroundChanged("app.a", BaseMs + 60_000);
        ledger.OnForegroundChanged("app.c", BaseMs + 90_000);
        Assert.Equal(30_000, ledger.EffectiveUsageMsToday("app.a"));
    }

    [Fact]
    public void DayRollover_ChargesSessionToStartDay_AndNewDayStartsEmpty()
    {
        var lateEvening = NextLocalMidnightMs - 60 * 60_000;                 // 23:00 on day 1
        var earlyMorning = NextLocalMidnightMs + 60 * 60_000;                // 01:00 on day 2
        var time = new FakeTimeProvider(lateEvening);
        var ledger = NewLedger(time);

        ledger.OnForegroundChanged("app.a", lateEvening);
        time.SetUtcNow(earlyMorning);
        ledger.OnForegroundChanged("app.b", earlyMorning);

        // The overnight session is charged entirely to the day it started on.
        Assert.Equal(0, ledger.EffectiveUsageMsToday("app.a"));
        Assert.Empty(ledger.UsageMsToday().Where(kv => kv.Key == "app.a").ToList());
        Assert.Equal(0, ledger.EffectiveUsageMsToday("app.b"));
    }

    [Fact]
    public void DailyBlockMarkers_ArePerDay()
    {
        var time = new FakeTimeProvider(Base);
        var ledger = NewLedger(time);

        Assert.False(ledger.IsDailyBlocked("app.a"));
        ledger.MarkDailyBlocked("app.a");
        Assert.True(ledger.IsDailyBlocked("app.a"));

        ledger.ClearDayBlocks();
        Assert.False(ledger.IsDailyBlocked("app.a"));

        ledger.MarkDailyBlocked("app.a");
        time.SetUtcNow(NextLocalMidnightMs + 1);
        Assert.False(ledger.IsDailyBlocked("app.a"));
    }

    [Fact]
    public void Constructor_ReloadsOpenSessionFromDisk()
    {
        var time = new FakeTimeProvider(Base);
        var first = NewLedger(time);
        first.OnForegroundChanged("app.a", BaseMs);

        time.SetUtcNow(BaseMs + 10_000);
        var second = NewLedger(time);
        Assert.Equal(10_000, second.EffectiveUsageMsToday("app.a"));
    }

    [Fact]
    public void Constructor_ReloadsDayBlocksFromDisk()
    {
        var time = new FakeTimeProvider(Base);
        NewLedger(time).MarkDailyBlocked("app.a");

        var reloaded = NewLedger(time);
        Assert.True(reloaded.IsDailyBlocked("app.a"));
    }

    [Fact]
    public void ClockRollback_Over60s_FiresTamperOnceAndKeepsUsage()
    {
        var time = new FakeTimeProvider(Base);
        var ledger = NewLedger(time);
        var tamperJumps = new List<long>();
        ledger.ClockTampered += jump => tamperJumps.Add(jump);

        ledger.OnForegroundChanged("app.a", BaseMs);
        time.SetUtcNow(BaseMs + 10_000);
        Assert.Equal(10_000, ledger.EffectiveUsageMsToday("app.a"));

        // Clock rolls back two minutes while the session is open.
        time.SetUtcNow(BaseMs - 120_000);
        var afterRollback = ledger.EffectiveUsageMsToday("app.a");
        ledger.EffectiveUsageMsToday("app.a"); // second read must not re-fire

        Assert.Single(tamperJumps);
        Assert.InRange(tamperJumps[0], 120_000, 140_000); // observed jump ~130s
        Assert.InRange(afterRollback, 10_000, 10_000 + 5_000); // preserved, at most monotonic drift
    }

    [Fact]
    public void ClockRollback_Under60s_DoesNotFireTamper()
    {
        var time = new FakeTimeProvider(Base);
        var ledger = NewLedger(time);
        var fired = 0;
        ledger.ClockTampered += _ => fired++;

        ledger.OnForegroundChanged("app.a", BaseMs);
        time.SetUtcNow(BaseMs + 10_000);
        ledger.EffectiveUsageMsToday("app.a");

        time.SetUtcNow(BaseMs + 5_000); // small backwards jitter: not tamper
        Assert.Equal(10_000, ledger.EffectiveUsageMsToday("app.a"));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void ClockRecovery_AfterTamper_RearmsDetection()
    {
        var time = new FakeTimeProvider(Base);
        var ledger = NewLedger(time);
        var fired = 0;
        ledger.ClockTampered += _ => fired++;

        ledger.OnForegroundChanged("app.a", BaseMs);
        time.SetUtcNow(BaseMs + 10_000);
        ledger.EffectiveUsageMsToday("app.a");

        time.SetUtcNow(BaseMs - 120_000);
        ledger.EffectiveUsageMsToday("app.a");
        Assert.Equal(1, fired);

        time.SetUtcNow(BaseMs + 60_000); // clock restored past the high-water mark
        ledger.EffectiveUsageMsToday("app.a");
        time.SetUtcNow(BaseMs - 180_000); // a second, larger rollback
        ledger.EffectiveUsageMsToday("app.a");

        Assert.Equal(2, fired);
    }

    [Fact]
    public void Load_PrunesDaysOlderThanRetention()
    {
        var time = new FakeTimeProvider(Base);
        // Seed 70 usage files spanning 70 days (well over the 62-day retention) so the
        // ledger's startup prune must drop the oldest files.
        var seedDir = CreateTempDir();
        try
        {
            for (var i = 0; i < 70; i++)
            {
                var dayKey = Base.AddDays(-i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(seedDir, "usage-" + dayKey + ".json"),
                    "{\"sessions\":[],\"open\":null}");
            }

            _ = new UsageLedger(seedDir, time);

            var remaining = System.IO.Directory.EnumerateFiles(seedDir, "usage-*.json").Count();
            Assert.InRange(remaining, 62, 63); // bounded, not unbounded
            // The oldest seeded day must be gone.
            var oldest = Base.AddDays(-69).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(System.IO.File.Exists(System.IO.Path.Combine(seedDir, "usage-" + oldest + ".json")));
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(seedDir, recursive: true);
            }
            catch (System.IO.IOException)
            {
                // best effort cleanup
            }
        }
    }

    [Fact]
    public void OnForegroundChanged_WritesOpenMarker_RestoredAfterRestart()
    {
        var time = new FakeTimeProvider(Base);
        var ledger = NewLedger(time);

        ledger.OnForegroundChanged("app.a", BaseMs);
        time.SetUtcNow(BaseMs + 10_000);
        ledger.OnForegroundChanged("app.b", BaseMs + 10_000);

        // The tiny open-session marker is durable immediately after the switch, so a
        // crash resumes the NEW app's session (not the previous one).
        var markerPath = System.IO.Path.Combine(this.stateDir, "usage-open.json");
        Assert.True(System.IO.File.Exists(markerPath));
        Assert.Contains("app.b", System.IO.File.ReadAllText(markerPath));

        // A restart (fresh instance) restores the new app's open session and accrues it.
        var restored = new UsageLedger(this.stateDir, time) { MonotonicTicks = static () => 0L };
        time.SetUtcNow(BaseMs + 40_000);
        Assert.Equal(30_000, restored.EffectiveUsageMsToday("app.b"));
        Assert.Equal(10_000, restored.EffectiveUsageMsToday("app.a")); // closed before the switch
    }

    [Fact]
    public void Compaction_CollapsesOldestSessions_KeepsTotalsExact()
    {
        var time = new FakeTimeProvider(Base);
        var ledger = NewLedger(time);
        var todayKey = Base.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // 2,100 alternating 3s sessions (spacing beyond the 2s blip-merge gap so each
        // switch is a distinct session): past the 2,000-session cap, oldest rows
        // collapse into per-app totals — usage math must stay exact.
        var t = BaseMs;
        for (var i = 0; i < 2100; i++)
        {
            time.SetUtcNow(t);
            ledger.OnForegroundChanged(i % 2 == 0 ? "app.a" : "app.b", t);
            t += 3000;
        }

        time.SetUtcNow(t + 5_000); // the final app.b session accrues 3s (loop tail) + 5s live
        ledger.FlushDirty();

        Assert.True(ledger.SessionCountForDay(todayKey) <= 2000);
        Assert.Equal(1050 * 3_000L, ledger.EffectiveUsageMsToday("app.a"));
        Assert.Equal(1049 * 3_000L + 8_000, ledger.EffectiveUsageMsToday("app.b"));
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "guardpulse-ledger-tests-" + Guid.NewGuid().ToString("N"));
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
