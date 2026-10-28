namespace GuardPulse.Agent.Core;

/// <summary>
/// Pure decision core for the session-agent watchdog: given the interactive WTS
/// sessions, which of them have a live agent process and the watchdog's tracked
/// session-to-pid state, decide which sessions need a launch, which lost a
/// previously seen agent (kill - tamper), which should just refresh their tracked
/// pid, and which logged off (forget - not tamper). Free of process/token APIs so
/// the kill-vs-logoff semantics are unit tested.
/// </summary>
public static class AgentRevivalPlanner
{
    /// <param name="LaunchSessions">Interactive sessions with no live agent: relaunch.</param>
    /// <param name="KilledSessions">Sessions whose previously tracked agent is gone (tamper signal).</param>
    /// <param name="ForgetSessions">Tracked sessions that are no longer interactive (logoff, not tamper).</param>
    /// <param name="RefreshSessions">Live agents: refresh the tracked pid (covers Run-key launches and restarts).</param>
    public readonly record struct RevivalPlan(
        IReadOnlyList<int> LaunchSessions,
        IReadOnlyList<(int SessionId, int PreviousPid)> KilledSessions,
        IReadOnlyList<int> ForgetSessions,
        IReadOnlyList<(int SessionId, int Pid)> RefreshSessions);

    public static RevivalPlan Plan(
        IReadOnlyCollection<int> interactiveSessions,
        IReadOnlyDictionary<int, int> liveAgentPidBySession,
        IReadOnlyDictionary<int, int> trackedPidBySession)
    {
        var launch = new List<int>();
        var killed = new List<(int, int)>();
        var refresh = new List<(int, int)>();

        foreach (var session in interactiveSessions)
        {
            if (liveAgentPidBySession.TryGetValue(session, out var livePid))
            {
                refresh.Add((session, livePid));
                continue;
            }

            if (trackedPidBySession.TryGetValue(session, out var previousPid))
            {
                killed.Add((session, previousPid));
            }

            launch.Add(session);
        }

        var forget = trackedPidBySession.Keys
            .Where(session => !interactiveSessions.Contains(session))
            .ToList();

        return new RevivalPlan(launch, killed, forget, refresh);
    }
}
