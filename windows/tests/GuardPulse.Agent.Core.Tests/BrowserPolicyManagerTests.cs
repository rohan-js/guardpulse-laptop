using GuardPulse.Agent.Service;
using Xunit;

public class BrowserPolicyManagerTests
{
    [Fact]
    public void ToBrowserPattern_PreservesPath()
    {
        // Path-based rules must keep their path so only the sub-path is browser-blocked
        // (the whole domain must NOT be redirected into the hosts file).
        Assert.Equal("youtube.com/shorts", BrowserPolicyManager.ToBrowserPattern("youtube.com/shorts"));
    }

    [Fact]
    public void ToBrowserPattern_KeepsWholeDomain()
    {
        Assert.Equal("youtube.com", BrowserPolicyManager.ToBrowserPattern("youtube.com"));
    }
}
