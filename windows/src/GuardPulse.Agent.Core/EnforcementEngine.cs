namespace GuardPulse.Agent.Core;

using GuardPulse.Protocol;

/// <summary>Lock decision for a foreground app. Reason is "manual" or "dailyLimit" when locked.</summary>
public sealed record BlockDecision(bool Locked, string Reason, string AppKey);

/// <summary>
/// Pure decision logic for whether the current foreground app must be locked, ported from the TV
/// agent's FallbackProtection.shouldLock with Windows-specific bypass handling. Precedence:
///   1. the agent's own exe is never locked (self-exemption),
///   2. active Safe Mode disables all locking,
///   3. outside the allowed-hours schedule => "schedule" (whole-device),
///   4. whole-device daily budget exhausted => "budget",
///   5. a one-visit/timed unlock for the app wins,
///   6. allowlist mode: apps without a rule and outside the Windows dir => "notApproved",
///   7. a Windows bypass row with no rule is blocked ("manual"),
///   8. rule.ManualBlocked => "manual",
///   9. dailyLimitMinutes reached => "dailyLimit" (also marks the day blocked in the ledger),
///  10. otherwise not locked.
/// </summary>
public sealed class EnforcementEngine
{
    private const long MsPerMinute = 60_000L;

    private readonly TimeProvider time;

    public EnforcementEngine(TimeProvider time)
    {
        this.time = time;
    }

    /// <summary>
    /// Safe Mode is armed and its Until deadline is still in the future.
    /// <paramref name="serverNowMs"/> is the RTDB/server-clock epoch ms (Safe Mode's Until
    /// is written against server time); null falls back to the local clock.
    /// </summary>
    public bool SafeModeActive(ControlSnapshotV2 snapshot, long? serverNowMs = null)
    {
        return snapshot.SafeMode is { Enabled: true } && snapshot.SafeMode.Until > (serverNowMs ?? NowMs());
    }

    /// <summary>True when an enabled allowed-hours schedule excludes the current device-local minute.</summary>
    public bool OutsideAllowedHours(ControlSnapshotV2 snapshot)
    {
        if (snapshot.Schedule is not { Enabled: true } schedule)
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone);
        var minute = local.Hour * 60 + local.Minute;
        var start = schedule.StartMinute;
        var end = schedule.EndMinute;
        if (start == end)
        {
            return true; // zero-length window: never allowed
        }

        var within = start < end
            ? minute >= start && minute < end       // same-day window
            : minute >= start || minute < end;      // wraps past midnight
        return !within;
    }

    /// <summary>True when the sum of today's per-app usage reaches the whole-device budget.</summary>
    public bool BudgetExceeded(ControlSnapshotV2 snapshot, UsageLedger ledger)
    {
        if (snapshot.Budget is not { } budget)
        {
            return false;
        }

        long totalMs = 0;
        foreach (var usage in ledger.UsageMsToday().Values)
        {
            totalMs += usage;
        }

        return totalMs >= (long)budget.DailyLimitMinutes * MsPerMinute;
    }

    /// <summary>Allowlist mode: only apps with an explicit rule (or Windows-dir system binaries) may run.</summary>
    public static bool IsBlockedByAllowlist(ControlSnapshotV2 snapshot, string appKey)
    {
        if (snapshot.Allowlist is not { Enabled: true })
        {
            return false;
        }

        if (snapshot.EffectiveApps().ContainsKey(appKey))
        {
            return false;
        }

        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !appKey.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="serverNowMs">RTDB/server-clock epoch ms for Safe Mode and unlock
    /// deadline checks; null falls back to the local clock.</param>
    public BlockDecision Decide(
        ControlSnapshotV2 snapshot,
        string appKey,
        UsageLedger ledger,
        OneVisitUnlocks unlocks,
        string agentAppKey,
        long? serverNowMs = null)
    {
        if (string.Equals(appKey, agentAppKey, StringComparison.OrdinalIgnoreCase))
        {
            return NotLocked(appKey);
        }

        if (SafeModeActive(snapshot, serverNowMs))
        {
            return NotLocked(appKey);
        }

        if (OutsideAllowedHours(snapshot))
        {
            return Locked(appKey, PolicyConstants.BLOCK_REASON_SCHEDULE);
        }

        if (BudgetExceeded(snapshot, ledger))
        {
            return Locked(appKey, PolicyConstants.BLOCK_REASON_BUDGET);
        }

        var apps = snapshot.EffectiveApps();

        if (unlocks.IsUnlocked(appKey, serverNowMs))
        {
            return NotLocked(appKey);
        }

        if (IsBlockedByAllowlist(snapshot, appKey))
        {
            return Locked(appKey, PolicyConstants.BLOCK_REASON_NOT_APPROVED);
        }

        if (!apps.TryGetValue(appKey, out var rule))
        {
            // Bypass rows are default-locked; a missing rule for them means blocked.
            if (PolicyConstants.IsWindowsBypassPackage(appKey))
            {
                return Locked(appKey, PolicyConstants.BLOCK_REASON_MANUAL);
            }

            return NotLocked(appKey);
        }

        if (rule.ManualBlocked)
        {
            return Locked(appKey, PolicyConstants.BLOCK_REASON_MANUAL);
        }

        if (rule.DailyLimitMinutes is int minutes &&
            ledger.EffectiveUsageMsToday(appKey) >= (long)minutes * MsPerMinute)
        {
            ledger.MarkDailyBlocked(appKey);
            return Locked(appKey, PolicyConstants.BLOCK_REASON_DAILY_LIMIT);
        }

        return NotLocked(appKey);
    }

    private long NowMs() => this.time.GetUtcNow().ToUnixTimeMilliseconds();

    private static BlockDecision Locked(string appKey, string reason) => new(true, reason, appKey);

    private static BlockDecision NotLocked(string appKey) => new(false, string.Empty, appKey);
}
