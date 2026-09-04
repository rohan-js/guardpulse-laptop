namespace GuardPulse.Agent.Service.Tests;

using GuardPulse.Agent.Service;
using Xunit;

public class BrowserUrlBlockerTests
{
    private static readonly string[] CustomWithPath = { "youtube.com/shorts" };
    private static readonly string[] CustomDomain = { "example.com" };
    private static readonly string[] CategoryDomains = { "gamblingsite.net", "www.adultsite.org" };

    [Theory]
    [InlineData("https://www.youtube.com/shorts/abc123", true)]
    [InlineData("https://youtube.com/shorts", true)]
    [InlineData("http://www.youtube.com/shorts?feature=x", true)]
    [InlineData("https://www.youtube.com/watch?v=1", false)]
    [InlineData("https://www.youtube.com/", false)]
    [InlineData("https://www.youtube.com/shortsomething", false)] // segment boundary
    [InlineData("https://m.youtube.com/shorts/abc", false)] // different host, not implied
    public void PathRule_MatchesHostAndPathOnly(string url, bool expected)
    {
        var blocker = BrowserUrlBlocker.Build(CustomWithPath, Array.Empty<string>());
        Assert.Equal(expected, blocker.IsBlocked(url).Blocked);
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("https://example.com/anything/deep", true)]
    [InlineData("http://www.example.com/", true)]
    [InlineData("https://notexample.com", false)]
    [InlineData("https://example.com.evil.io", false)]
    public void DomainRule_MatchesAnyPathOnHost(string url, bool expected)
    {
        var blocker = BrowserUrlBlocker.Build(CustomDomain, Array.Empty<string>());
        Assert.Equal(expected, blocker.IsBlocked(url).Blocked);
    }

    [Theory]
    [InlineData("https://gamblingsite.net/poker", true)]
    [InlineData("https://adultsite.org/", true)]
    [InlineData("https://www.gamblingsite.net", true)]
    public void CategoryDomains_BlockWholeSite(string url, bool expected)
    {
        var blocker = BrowserUrlBlocker.Build(Array.Empty<string>(), CategoryDomains);
        Assert.Equal(expected, blocker.IsBlocked(url).Blocked);
    }

    [Fact]
    public void SchemeAndCaseInsensitive()
    {
        var blocker = BrowserUrlBlocker.Build(new[] { "HTTPS://YouTube.com/Shorts" }, Array.Empty<string>());
        Assert.True(blocker.IsBlocked("https://WWW.YOUTUBE.COM/SHORTS/watch").Blocked);
    }

    [Fact]
    public void MatchedRule_ReturnsHostPathForm()
    {
        var blocker = BrowserUrlBlocker.Build(CustomWithPath, Array.Empty<string>());
        var (blocked, rule) = blocker.IsBlocked("https://www.youtube.com/shorts/xyz");
        Assert.True(blocked);
        Assert.Contains("shorts", rule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url at all")]
    [InlineData("::::")]
    public void GarbageInput_NeverBlocks(string? url)
    {
        var blocker = BrowserUrlBlocker.Build(CustomDomain, CategoryDomains);
        Assert.False(blocker.IsBlocked(url).Blocked);
    }

    [Fact]
    public void UserinfoAndPort_Stripped()
    {
        var blocker = BrowserUrlBlocker.Build(CustomDomain, Array.Empty<string>());
        Assert.True(blocker.IsBlocked("https://user:pass@example.com:8080/page").Blocked);
    }

    [Fact]
    public void Empty_Build_NeverBlocks()
    {
        Assert.False(BrowserUrlBlocker.Empty.IsBlocked("https://youtube.com/shorts").Blocked);
        Assert.False(BrowserUrlBlocker.Build(Array.Empty<string>(), Array.Empty<string>())
            .IsBlocked("https://example.com").Blocked);
    }

    [Fact]
    public void OversizedUrl_NeverBlocks()
    {
        var blocker = BrowserUrlBlocker.Build(CustomDomain, Array.Empty<string>());
        Assert.False(blocker.IsBlocked("https://example.com/" + new string('a', 3000)).Blocked);
    }
}
