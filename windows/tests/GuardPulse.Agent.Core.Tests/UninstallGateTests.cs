namespace GuardPulse.Agent.Core.Tests;

using GuardPulse.Agent.Core;
using GuardPulse.Protocol;
using Xunit;

/// <summary>
/// The uninstall PIN gate: mirror lifecycle, verify/permit/consume, wrong-PIN
/// recording and the escalating rejection window (all clock-driven via FakeTimeProvider).
/// </summary>
public sealed class UninstallGateTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string stateDir = Directory.CreateTempSubdirectory("gp-ugate").FullName;
    private readonly FakeTimeProvider time = new(Base);
    private readonly UninstallGate gate;

    public UninstallGateTests()
    {
        gate = new UninstallGate(stateDir, time);
    }

    [Fact]
    public void NotGated_WhenNeverPaired()
    {
        Assert.False(gate.IsPinGated());
    }

    [Fact]
    public void SetPin_Gates_ThenVerifies_AndIssuesPermit()
    {
        var hash = PinHasher.Create("123456");
        gate.SetPin(hash.Salt, hash.Hash, PinHasher.ITERATIONS, PinHasher.CURRENT_VERSION);
        Assert.True(gate.IsPinGated());
        Assert.False(gate.HasFreshPermit());

        var decision = gate.VerifyPin("123456");
        Assert.Equal(UninstallPinResult.Granted, decision.Result);
        Assert.True(gate.HasFreshPermit());

        gate.ConsumePermit();
        Assert.False(gate.HasFreshPermit());
    }

    [Fact]
    public void WrongPin_Rejected_DoesNotIssuePermit()
    {
        var hash = PinHasher.Create("123456");
        gate.SetPin(hash.Salt, hash.Hash, PinHasher.ITERATIONS, PinHasher.CURRENT_VERSION);

        var decision = gate.VerifyPin("000000");
        Assert.Equal(UninstallPinResult.Rejected, decision.Result);
        Assert.False(gate.HasFreshPermit());
    }

    [Fact]
    public void FiveWrongPins_LockOut_FurtherAttempts()
    {
        var hash = PinHasher.Create("123456");
        gate.SetPin(hash.Salt, hash.Hash, PinHasher.ITERATIONS, PinHasher.CURRENT_VERSION);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(UninstallPinResult.Rejected, gate.VerifyPin("000000").Result);
        }

        // The 6th attempt is inside the rejection window: locked, not evaluated.
        Assert.Equal(UninstallPinResult.Locked, gate.VerifyPin("123456").Result);
        Assert.False(gate.HasFreshPermit());
    }

    [Fact]
    public void LockoutWindow_Expires_AttemptsWorkAgain()
    {
        var hash = PinHasher.Create("123456");
        gate.SetPin(hash.Salt, hash.Hash, PinHasher.ITERATIONS, PinHasher.CURRENT_VERSION);

        for (var i = 0; i < 5; i++) _ = gate.VerifyPin("000000");

        // First lockout = base 1 min. Inside the window even the CORRECT PIN is
        // refused (lockout is not a "wait and retry now" state).
        Assert.Equal(UninstallPinResult.Locked, gate.VerifyPin("123456").Result);

        // Past the window, attempts are evaluated again.
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(UninstallPinResult.Rejected, gate.VerifyPin("000000").Result);
        Assert.Equal(UninstallPinResult.Granted, gate.VerifyPin("123456").Result);
    }

    [Fact]
    public void ClearPin_OpensGate()
    {
        var hash = PinHasher.Create("123456");
        gate.SetPin(hash.Salt, hash.Hash, PinHasher.ITERATIONS, PinHasher.CURRENT_VERSION);
        gate.ClearPin();
        Assert.False(gate.IsPinGated());
    }

    [Fact]
    public void Permit_Expires_AfterTtl()
    {
        var hash = PinHasher.Create("123456");
        gate.SetPin(hash.Salt, hash.Hash, PinHasher.ITERATIONS, PinHasher.CURRENT_VERSION);
        _ = gate.VerifyPin("123456");
        Assert.True(gate.HasFreshPermit());

        time.Advance(TimeSpan.FromMinutes(16));
        Assert.False(gate.HasFreshPermit());
    }

    public void Dispose()
    {
        try { Directory.Delete(stateDir, recursive: true); } catch { }
    }
}
