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
    public void ToBrowserPatterns_DomainOnly_IsExactPlusWww_NoSubdomainVariants()
    {
        var patterns = BrowserPolicyManager.ToBrowserPatterns("YouTube.COM");
        // The subdomain opt-ins ([*.] and leading dot) must be gone: blocking
        // youtube.com must not take down music.youtube.com at navigation start.
        Assert.Equal(new[] { "youtube.com", "www.youtube.com" }, patterns);
        Assert.DoesNotContain(patterns, p => p.StartsWith("[*.]"));
        Assert.DoesNotContain(patterns, p => p.StartsWith("."));
    }

    [Fact]
    public void ToBrowserPatterns_AlreadyWww_DoesNotDuplicate()
    {
        Assert.Equal(new[] { "www.youtube.com" }, BrowserPolicyManager.ToBrowserPatterns("www.youtube.com"));
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
