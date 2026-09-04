namespace GuardPulse.Agent.Core.Tests;

using GuardPulse.Agent.Core;
using GuardPulse.Protocol;
using Xunit;

public class EnforcementEngineTests : IDisposable
{
    private const string AgentAppKey = "c:\\program files\\guardpulse\\guardpulse.agent.session.exe";
    private const string GameAppKey = "c:\\games\\blocky.exe";
    private const string WorkAppKey = "c:\\apps\\writer.exe";

    private static readonly DateTimeOffset Base = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly long BaseMs = Base.ToUnixTimeMilliseconds();

    private readonly string stateDir = CreateTempDir();

    // --------------------------------------------------------------- safe mode

    [Fact]
    public void SafeModeActive_RequiresEnabledAndFutureDeadline()
    {
        var engine = new EnforcementEngine(new FakeTimeProvider(BaseMs));

        Assert.False(engine.SafeModeActive(Snapshot(safeMode: new ControlSafeMode())));
        Assert.False(engine.SafeModeActive(Snapshot(safeMode: new ControlSafeMode(true, BaseMs))));
        Assert.True(engine.SafeModeActive(Snapshot(safeMode: new ControlSafeMode(true, BaseMs + 1))));
    }

    [Fact]
    public void SafeMode_OverridesManualBlock()
    {
        var engine = new EnforcementEngine(new FakeTimeProvider(BaseMs));
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: true)),
            safeMode: new ControlSafeMode(true, BaseMs + 60_000));

        var decision = engine.Decide(snapshot, GameAppKey, Ledger(), Unlocks(), AgentAppKey);

        Assert.False(decision.Locked);
    }

    [Fact]
    public void SafeMode_OverridesDailyLimit_AndUnlock()
    {
        var time = new FakeTimeProvider(BaseMs);
        var engine = new EnforcementEngine(time);
        var ledger = LedgerWithUsage(time, GameAppKey, 10 * 60_000);
        var unlocks = Unlocks();
        unlocks.Grant(GameAppKey);
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: false, DailyLimitMinutes: 5)),
            safeMode: new ControlSafeMode(true, BaseMs + 60_000));

        var decision = engine.Decide(snapshot, GameAppKey, ledger, unlocks, AgentAppKey);

        Assert.False(decision.Locked);
    }

    // -------------------------------------------------------------- agent self

    [Fact]
    public void AgentSelfExe_IsNeverLocked()
    {
        var engine = new EnforcementEngine(new FakeTimeProvider(BaseMs));
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(AgentAppKey, ManualBlocked: true)));

        var decision = engine.Decide(snapshot, AgentAppKey, Ledger(), Unlocks(), AgentAppKey);

        Assert.False(decision.Locked);
    }

    // ----------------------------------------------------------------- unlock

    [Fact]
    public void Unlock_OverridesManualBlock()
    {
        var engine = new EnforcementEngine(new FakeTimeProvider(BaseMs));
        var unlocks = Unlocks();
        unlocks.Grant(GameAppKey);
        var snapshot = Snapshot(apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: true)));

        var decision = engine.Decide(snapshot, GameAppKey, Ledger(), unlocks, AgentAppKey);

        Assert.False(decision.Locked);
    }

    [Fact]
    public void Unlock_OverridesDailyLimit()
    {
        var time = new FakeTimeProvider(BaseMs);
        var engine = new EnforcementEngine(time);
        var ledger = LedgerWithUsage(time, GameAppKey, 10 * 60_000);
        var unlocks = Unlocks();
        unlocks.Grant(GameAppKey);
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: false, DailyLimitMinutes: 5)));

        var decision = engine.Decide(snapshot, GameAppKey, ledger, unlocks, AgentAppKey);

        Assert.False(decision.Locked);
    }

    // ------------------------------------------------------------ bypass rows

    [Fact]
    public void BypassRow_WithoutRule_IsBlockedAsManual()
    {
        var engine = new EnforcementEngine(new FakeTimeProvider(BaseMs));
        var snapshot = Snapshot(); // no apps at all

        foreach (var (appKey, reason) in new[]
                 {
                     (PolicyConstants.WINDOWS_TASK_MANAGER_PACKAGE, "task manager"),
                     (PolicyConstants.WINDOWS_COMMAND_LINE_PACKAGE, "command line"),
                     (PolicyConstants.WINDOWS_REGISTRY_EDITOR_PACKAGE, "registry editor"),
                     (PolicyConstants.WINDOWS_SETTINGS_PACKAGE, "settings"),
                     (PolicyConstants.WINDOWS_INSTALLERS_PACKAGE, "installers"),
                 })
        {
            var decision = engine.Decide(snapshot, appKey, Ledger(), Unlocks(), AgentAppKey);
            Assert.True(decision.Locked, reason);
            Assert.Equal("manual", decision.Reason);
            Assert.Equal(appKey, decision.AppKey);
        }
    }

    [Fact]
    public void BypassRow_ExplicitlyAllowedByParent_IsNotLocked()
    {
        var engine = new EnforcementEngine(new FakeTimeProvider(BaseMs));
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(PolicyConstants.WINDOWS_TASK_MANAGER_PACKAGE, ManualBlocked: false)));

        var decision = engine.Decide(snapshot, PolicyConstants.WINDOWS_TASK_MANAGER_PACKAGE, Ledger(), Unlocks(), AgentAppKey);

        Assert.False(decision.Locked);
    }

    // ------------------------------------------------------- manual / unknown

    [Fact]
    public void ManualBlocked_LocksWithManualReason()
    {
        var engine = new EnforcementEngine(new FakeTimeProvider(BaseMs));
        var snapshot = Snapshot(apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: true)));

        var decision = engine.Decide(snapshot, GameAppKey, Ledger(), Unlocks(), AgentAppKey);

        Assert.True(decision.Locked);
        Assert.Equal("manual", decision.Reason);
        Assert.Equal(GameAppKey, decision.AppKey);
    }

    [Fact]
    public void UnknownApp_WithoutRule_IsNotLocked()
    {
        var engine = new EnforcementEngine(new FakeTimeProvider(BaseMs));

        var decision = engine.Decide(Snapshot(), WorkAppKey, Ledger(), Unlocks(), AgentAppKey);

        Assert.False(decision.Locked);
    }

    // ------------------------------------------------------------- daily limit

    [Fact]
    public void DailyLimitReached_LocksAndMarksDayBlocked()
    {
        var time = new FakeTimeProvider(BaseMs);
        var engine = new EnforcementEngine(time);
        var ledger = LedgerWithUsage(time, GameAppKey, 10 * 60_000);
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: false, DailyLimitMinutes: 10)));

        var decision = engine.Decide(snapshot, GameAppKey, ledger, Unlocks(), AgentAppKey);

        Assert.True(decision.Locked);
        Assert.Equal("dailyLimit", decision.Reason);
        Assert.True(ledger.IsDailyBlocked(GameAppKey));
    }

    [Fact]
    public void DailyLimit_BelowLimit_IsNotLocked()
    {
        var time = new FakeTimeProvider(BaseMs);
        var engine = new EnforcementEngine(time);
        var ledger = LedgerWithUsage(time, GameAppKey, 10 * 60_000 - 1);
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: false, DailyLimitMinutes: 10)));

        var decision = engine.Decide(snapshot, GameAppKey, ledger, Unlocks(), AgentAppKey);

        Assert.False(decision.Locked);
        Assert.False(ledger.IsDailyBlocked(GameAppKey));
    }

    [Fact]
    public void ManualBlock_WinsOverDailyLimit()
    {
        var time = new FakeTimeProvider(BaseMs);
        var engine = new EnforcementEngine(time);
        var ledger = LedgerWithUsage(time, GameAppKey, 10 * 60_000);
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: true, DailyLimitMinutes: 5)));

        var decision = engine.Decide(snapshot, GameAppKey, ledger, Unlocks(), AgentAppKey);

        Assert.True(decision.Locked);
        Assert.Equal("manual", decision.Reason);
    }

    [Fact]
    public void DailyLimit_WithoutLimitMinutes_NeverLocks()
    {
        var time = new FakeTimeProvider(BaseMs);
        var engine = new EnforcementEngine(time);
        var ledger = LedgerWithUsage(time, GameAppKey, 24 * 60 * 60_000);
        var snapshot = Snapshot(apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: false)));

        var decision = engine.Decide(snapshot, GameAppKey, ledger, Unlocks(), AgentAppKey);

        Assert.False(decision.Locked);
    }

    // ---------------------------------------------------------- session limit

    /// <summary>
    /// Drives the tracker the way the host loop does: a tick every 60s. Sixty 60s ticks
    /// accrue <paramref name="minutes"/> minutes of continuous open use.
    /// </summary>
    private SessionUsageTracker TrackerWithSession(FakeTimeProvider time, string appKey, int minutes)
    {
        var sessions = new SessionUsageTracker(
            Path.Combine(this.stateDir, "sessions.json"), time);
        var end = BaseMs + (long)minutes * 60_000;
        for (var tick = BaseMs; tick <= end; tick += 60_000)
        {
            time.SetUtcNow(tick);
            sessions.Tick(appKey, tick);
        }

        return sessions;
    }

    [Fact]
    public void SessionLimitReached_LocksWithSessionLimitReason()
    {
        var time = new FakeTimeProvider(BaseMs);
        var engine = new EnforcementEngine(time);
        var sessions = TrackerWithSession(time, GameAppKey, 20); // 20 continuous minutes
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: false, SessionLimitMinutes: 20)));

        var decision = engine.Decide(
            snapshot, GameAppKey, Ledger(), Unlocks(), AgentAppKey, sessions: sessions);

        Assert.True(decision.Locked);
        Assert.Equal("sessionLimit", decision.Reason);
        Assert.Equal(GameAppKey, decision.AppKey);
    }

    [Fact]
    public void SessionLimit_BelowLimit_IsNotLocked()
    {
        var time = new FakeTimeProvider(BaseMs);
        var engine = new EnforcementEngine(time);
        var sessions = TrackerWithSession(time, GameAppKey, 10); // 10 continuous minutes
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: false, SessionLimitMinutes: 20)));

        var decision = engine.Decide(
            snapshot, GameAppKey, Ledger(), Unlocks(), AgentAppKey, sessions: sessions);

        Assert.False(decision.Locked);
    }

    [Fact]
    public void SessionLimit_WithoutLimitMinutes_NeverLocks()
    {
        var time = new FakeTimeProvider(BaseMs);
        var engine = new EnforcementEngine(time);
        var sessions = TrackerWithSession(time, GameAppKey, 24 * 60);
        var snapshot = Snapshot(apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: false)));

        var decision = engine.Decide(
            snapshot, GameAppKey, Ledger(), Unlocks(), AgentAppKey, sessions: sessions);

        Assert.False(decision.Locked);
    }

    [Fact]
    public void SessionLimit_DoesNotChangeBehaviorWhenTrackerNotWired()
    {
        var time = new FakeTimeProvider(BaseMs);
        var engine = new EnforcementEngine(time);
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: false, SessionLimitMinutes: 20)));

        var decision = engine.Decide(snapshot, GameAppKey, Ledger(), Unlocks(), AgentAppKey);

        Assert.False(decision.Locked); // sessions=null: pre-session-limit behavior
    }

    [Fact]
    public void SessionLimit_ResetByTracker_Unlocks()
    {
        var time = new FakeTimeProvider(BaseMs);
        var engine = new EnforcementEngine(time);
        var sessions = TrackerWithSession(time, GameAppKey, 20); // limit reached
        sessions.Reset(GameAppKey);                              // unlock approval / resetToday
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: false, SessionLimitMinutes: 20)));

        var decision = engine.Decide(
            snapshot, GameAppKey, Ledger(), Unlocks(), AgentAppKey, sessions: sessions);

        Assert.False(decision.Locked);
    }

    [Fact]
    public void Unlock_OverridesSessionLimit()
    {
        var time = new FakeTimeProvider(BaseMs);
        var engine = new EnforcementEngine(time);
        var sessions = TrackerWithSession(time, GameAppKey, 20); // limit reached
        var unlocks = Unlocks();
        unlocks.Grant(GameAppKey);
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: false, SessionLimitMinutes: 20)));

        var decision = engine.Decide(
            snapshot, GameAppKey, Ledger(), unlocks, AgentAppKey, sessions: sessions);

        Assert.False(decision.Locked); // the unlock arm runs before the session-limit arm
    }

    // ----------------------------------------------------------- mode override

    [Fact]
    public void ActiveMode_OverridesTopLevelRules()
    {
        var engine = new EnforcementEngine(new FakeTimeProvider(BaseMs));
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: true)),
            modes: Modes(new ControlMode(
                "school",
                "School",
                Apps(new ControlAppRule(GameAppKey, ManualBlocked: false, DailyLimitMinutes: null)))),
            activeMode: new ControlActiveMode("school", "School"));

        var decision = engine.Decide(snapshot, GameAppKey, Ledger(), Unlocks(), AgentAppKey);

        Assert.False(decision.Locked);
    }

    [Fact]
    public void ActiveMode_CanBlockWhatTopLevelAllows()
    {
        var engine = new EnforcementEngine(new FakeTimeProvider(BaseMs));
        var snapshot = Snapshot(
            apps: Apps(new ControlAppRule(GameAppKey, ManualBlocked: false)),
            modes: Modes(new ControlMode(
                "school",
                "School",
                Apps(new ControlAppRule(GameAppKey, ManualBlocked: true)))),
            activeMode: new ControlActiveMode("school", "School"));

        var decision = engine.Decide(snapshot, GameAppKey, Ledger(), Unlocks(), AgentAppKey);

        Assert.True(decision.Locked);
        Assert.Equal("manual", decision.Reason);
    }

    [Fact]
    public void ActiveMode_StillFillsDefaultLockedBypassRows()
    {
        var engine = new EnforcementEngine(new FakeTimeProvider(BaseMs));
        var snapshot = Snapshot(
            modes: Modes(new ControlMode(
                "school",
                "School",
                Apps(new ControlAppRule(GameAppKey, ManualBlocked: false)))),
            activeMode: new ControlActiveMode("school", "School"));

        var decision = engine.Decide(
            snapshot, PolicyConstants.WINDOWS_REGISTRY_EDITOR_PACKAGE, Ledger(), Unlocks(), AgentAppKey);

        Assert.True(decision.Locked);
        Assert.Equal("manual", decision.Reason);
    }

    // ---------------------------------------------------------------- helpers

    private UsageLedger Ledger() => new(this.stateDir, new FakeTimeProvider(BaseMs));

    private OneVisitUnlocks Unlocks() => new(this.stateDir, new FakeTimeProvider(BaseMs));

    private UsageLedger LedgerWithUsage(FakeTimeProvider time, string appKey, long usageMs)
    {
        var ledger = new UsageLedger(this.stateDir, time);
        ledger.OnForegroundChanged(appKey, BaseMs);
        time.SetUtcNow(BaseMs + usageMs);
        ledger.OnForegroundChanged("other.app", BaseMs + usageMs);
        time.SetUtcNow(BaseMs + usageMs + 1_000);
        return ledger;
    }

    private static Dictionary<string, ControlAppRule> Apps(params ControlAppRule[] rules) =>
        rules.ToDictionary(rule => rule.PackageName);

    private static Dictionary<string, ControlMode> Modes(params ControlMode[] modes) =>
        modes.ToDictionary(mode => mode.ModeId);

    private static ControlSnapshotV2 Snapshot(
        IReadOnlyDictionary<string, ControlAppRule>? apps = null,
        IReadOnlyDictionary<string, ControlMode>? modes = null,
        ControlActiveMode? activeMode = null,
        ControlSafeMode? safeMode = null)
        => new(
            "rev-1",
            null,
            null,
            apps ?? new Dictionary<string, ControlAppRule>(),
            modes ?? new Dictionary<string, ControlMode>(),
            activeMode,
            safeMode ?? new ControlSafeMode(),
            null);

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "guardpulse-enforcement-tests-" + Guid.NewGuid().ToString("N"));
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
