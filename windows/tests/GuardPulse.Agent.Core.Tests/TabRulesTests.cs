namespace GuardPulse.Agent.Core.Tests;

using GuardPulse.Agent.Service;
using Xunit;

public class TabRulesTests
{
    private static readonly string[] PathEntry = { "youtube.com/shorts" };
    private static readonly string[] DomainEntry = { "example.com" };

    [Fact]
    public void Build_SplitsPathsFromDomains()
    {
        var (domains, paths) = BrowserPolicyManager.BuildTabRules(
            new[] { "youtube.com/shorts", "https://foo.bar/baz", "example.com" },
            Array.Empty<string>());

        Assert.Contains("youtube.com", domains);
        Assert.Contains("example.com", domains);
        Assert.Contains("foo.bar", domains);
        Assert.Contains("youtube.com/shorts", paths);
        Assert.Contains("foo.bar/baz", paths);
    }

    [Fact]
    public void Build_CategoryDomainsBecomeWholeDomainRules()
    {
        var (domains, paths) = BrowserPolicyManager.BuildTabRules(
            Array.Empty<string>(), new[] { "GamblingSite.Net", "www.adultsite.org" });

        Assert.Contains("gamblingsite.net", domains);
        Assert.Contains("www.adultsite.org", domains);
        Assert.Empty(paths);
    }

    [Fact]
    public void Build_StripsQueryStringsAndTrailingSlashes()
    {
        var (_, paths) = BrowserPolicyManager.BuildTabRules(
            new[] { "youtube.com/shorts?feature=x", "foo.com/" }, Array.Empty<string>());
        Assert.Contains("youtube.com/shorts", paths);
        // "foo.com/" collapses to a whole-domain rule, not a path rule.
        Assert.DoesNotContain(paths, p => p.StartsWith("foo.com/"));
    }

    [Theory]
    [InlineData("not a domain")]
    [InlineData("")]
    [InlineData("a..b.com")]
    public void Build_SkipsGarbage(string entry)
    {
        var (domains, paths) = BrowserPolicyManager.BuildTabRules(new[] { entry }, Array.Empty<string>());
        Assert.DoesNotContain(entry, domains);
        Assert.Empty(paths);
    }
}
