namespace GuardPulse.Agent.Core.Tests;

using GuardPulse.Agent.Service;
using Xunit;

public class TabRulesTests
{
    private static readonly string[] PathEntry = { "youtube.com/shorts" };
    private static readonly string[] DomainEntry = { "example.com" };

    [Fact]
    public void Build_SplitsCustomDomainsExact_PathsAndCategorySuffix()
    {
        var (exact, domains, paths) = BrowserPolicyManager.BuildTabRules(
            new[] { "youtube.com/shorts", "https://foo.bar/baz", "youtube.com" },
            Array.Empty<string>());

        // Parent-typed whole domains are EXACT rules now.
        Assert.Contains("youtube.com", exact);
        Assert.DoesNotContain("youtube.com", domains);
        // Path entries keep prefix matching.
        Assert.Contains("youtube.com/shorts", paths);
        Assert.Contains("foo.bar/baz", paths);
        Assert.DoesNotContain("foo.bar", exact);
    }

    [Fact]
    public void Build_CategoryDomainsStayWholeDomainRules()
    {
        var (exact, domains, paths) = BrowserPolicyManager.BuildTabRules(
            Array.Empty<string>(), new[] { "GamblingSite.Net", "www.adultsite.org" });

        Assert.Empty(exact);
        Assert.Contains("gamblingsite.net", domains);
        Assert.Contains("www.adultsite.org", domains);
        Assert.Empty(paths);
    }

    [Fact]
    public void Build_StripsQueryStringsAndTrailingSlashes()
    {
        var (_, _, paths) = BrowserPolicyManager.BuildTabRules(
            new[] { "youtube.com/shorts?feature=x", "foo.com/" }, Array.Empty<string>());
        Assert.Contains("youtube.com/shorts", paths);
        // "foo.com/" collapses to an exact whole-domain rule, not a path rule.
        Assert.DoesNotContain(paths, p => p.StartsWith("foo.com/"));
        Assert.Contains("foo.com", BrowserPolicyManager.BuildTabRules(new[] { "foo.com/" }, Array.Empty<string>()).ExactDomains);
    }

    [Theory]
    [InlineData("not a domain")]
    [InlineData("")]
    [InlineData("a..b.com")]
    public void Build_SkipsGarbage(string entry)
    {
        var (exact, domains, paths) = BrowserPolicyManager.BuildTabRules(new[] { entry }, Array.Empty<string>());
        Assert.DoesNotContain(entry, exact);
        Assert.Empty(domains);
        Assert.Empty(paths);
    }

    [Fact]
    public void ToBrowserPatterns_DomainOnly_IsDotPrefixedExact_NoBareHost()
    {
        var patterns = BrowserPolicyManager.ToBrowserPatterns("YouTube.COM");
        // Chromium grammar (url_matcher/url_util.cc): BARE host = host + all subdomains
        // (the music.youtube.com leak); LEADING DOT = match_subdomains=false → exact.
        Assert.Equal(new[] { ".youtube.com", ".www.youtube.com" }, patterns);
        Assert.All(patterns, p => Assert.StartsWith(".", p));
    }

    [Fact]
    public void ToBrowserPatterns_AlreadyWww_DoesNotDuplicate()
    {
        Assert.Equal(new[] { ".www.youtube.com" }, BrowserPolicyManager.ToBrowserPatterns("www.youtube.com"));
    }

    [Fact]
    public void ToBrowserPatterns_PathEntry_KeepsSubtreeWildcard()
    {
        Assert.Equal(new[] { "youtube.com/shorts", "youtube.com/shorts/*" },
            BrowserPolicyManager.ToBrowserPatterns("youtube.com/shorts"));
        Assert.Equal(new[] { "youtube.com/shorts/*" },
            BrowserPolicyManager.ToBrowserPatterns("youtube.com/shorts/*"));
    }
}
