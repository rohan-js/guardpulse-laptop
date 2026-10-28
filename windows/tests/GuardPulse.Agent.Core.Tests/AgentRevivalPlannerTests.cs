namespace GuardPulse.Agent.Core.Tests;

using GuardPulse.Agent.Core;
using Xunit;

/// <summary>
/// Kill-vs-logoff semantics of the session-agent watchdog: a lost agent in a
/// still-interactive session is a kill (relaunch + tamper), a logged-off session is
/// forgotten without tamper, and live agents only refresh their tracked pid.
/// </summary>
public sealed class AgentRevivalPlannerTests
{
    [Fact]
    public void NewInteractiveSessionWithoutAgent_LaunchesWithoutTamper()
    {
        var plan = AgentRevivalPlanner.Plan(
            interactiveSessions: new[] { 1 },
            liveAgentPidBySession: new Dictionary<int, int>(),
            trackedPidBySession: new Dictionary<int, int>());

        Assert.Equal(new[] { 1 }, plan.LaunchSessions);
        Assert.Empty(plan.KilledSessions);
        Assert.Empty(plan.ForgetSessions);
        Assert.Empty(plan.RefreshSessions);
    }

    [Fact]
    public void KilledAgentInInteractiveSession_RelaunchesAndReportsTamper()
    {
        var plan = AgentRevivalPlanner.Plan(
            interactiveSessions: new[] { 1 },
            liveAgentPidBySession: new Dictionary<int, int>(),
            trackedPidBySession: new Dictionary<int, int> { [1] = 4242 });

        Assert.Equal(new[] { 1 }, plan.LaunchSessions);
        Assert.Equal(new[] { (1, 4242) }, plan.KilledSessions);
        Assert.Empty(plan.ForgetSessions);
        Assert.Empty(plan.RefreshSessions);
    }

    [Fact]
    public void LoggedOffSession_IsForgottenWithoutTamper()
    {
        var plan = AgentRevivalPlanner.Plan(
            interactiveSessions: Array.Empty<int>(),
            liveAgentPidBySession: new Dictionary<int, int>(),
            trackedPidBySession: new Dictionary<int, int> { [1] = 4242 });

        Assert.Empty(plan.LaunchSessions);
        Assert.Empty(plan.KilledSessions);
        Assert.Equal(new[] { 1 }, plan.ForgetSessions);
        Assert.Empty(plan.RefreshSessions);
    }

    [Fact]
    public void LiveAgent_RefreshesTrackedPid_NoLaunchNoTamper()
    {
        var plan = AgentRevivalPlanner.Plan(
            interactiveSessions: new[] { 1 },
            liveAgentPidBySession: new Dictionary<int, int> { [1] = 5050 },
            trackedPidBySession: new Dictionary<int, int> { [1] = 4242 });

        Assert.Empty(plan.LaunchSessions);
        Assert.Empty(plan.KilledSessions);
        Assert.Empty(plan.ForgetSessions);
        Assert.Equal(new[] { (1, 5050) }, plan.RefreshSessions);
    }

    [Fact]
    public void RunKeyLaunchedAgent_FirstObservation_RefreshesWithoutTamper()
    {
        var plan = AgentRevivalPlanner.Plan(
            interactiveSessions: new[] { 2 },
            liveAgentPidBySession: new Dictionary<int, int> { [2] = 7000 },
            trackedPidBySession: new Dictionary<int, int>());

        Assert.Equal(new[] { (2, 7000) }, plan.RefreshSessions);
        Assert.Empty(plan.LaunchSessions);
        Assert.Empty(plan.KilledSessions);
    }

    [Fact]
    public void MixedSessions_PlanCoversEachClass()
    {
        var plan = AgentRevivalPlanner.Plan(
            interactiveSessions: new[] { 1, 2, 3 },
            liveAgentPidBySession: new Dictionary<int, int> { [2] = 1111 },
            trackedPidBySession: new Dictionary<int, int> { [1] = 4242, [9] = 5555 });

        Assert.Equal(new[] { 1, 3 }, plan.LaunchSessions);
        Assert.Equal(new[] { (1, 4242) }, plan.KilledSessions);
        Assert.Equal(new[] { 9 }, plan.ForgetSessions);
        Assert.Equal(new[] { (2, 1111) }, plan.RefreshSessions);
    }
}
