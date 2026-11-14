namespace GuardPulse.Agent.Core.Tests;

using GuardPulse.Agent.Session;
using Xunit;

/// <summary>
/// Pins the site-matching semantics of the session agent's real-time tab enforcement.
/// Regression root: parent blocked youtube.com and music.youtube.com came down with it —
/// the matcher used to suffix-walk EVERY rule; parent-typed rules are now exact
/// (www-canonical) while curated category lists keep whole-domain coverage.
/// </summary>
public sealed class TabRulesMatchTests
{
    private static TabRules Rules(string[] exact, string[] domains, string[] paths) => new()
    {
        ExactDomains = exact,
        Domains = domains,
        Paths = paths,
    };

    private const string RulesJson = """
        {"exactDomains":["youtube.com"],"domains":["facebook.com"],"paths":["youtube.com/shorts"]}
        """;

    [Fact]
    public void ExactRule_DoesNotLeakToSubdomains_TheReportedBug()
    {
        var rules = Rules(new[] { "youtube.com" }, Array.Empty<string>(), Array.Empty<string>());
        Assert.Null(rules.Match("https://music.youtube.com/watch?v=abc"));
        Assert.Null(rules.Match("https://www.music.youtube.com/watch?v=abc"));
        Assert.Null(rules.Match("https://m.youtube.com/watch?v=abc"));
    }

    [Fact]
    public void ExactRule_CoversCanonicalWwwHost()
    {
        var rules = Rules(new[] { "youtube.com" }, Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal("exact:youtube.com", rules.Match("https://www.youtube.com/watch"));
        Assert.Equal("exact:youtube.com", rules.Match("https://youtube.com/"));
        Assert.Equal("exact:youtube.com", rules.Match("http://YOUTUBE.COM/some/path?q=1"));
    }

    [Fact]
    public void ExactRule_WwwRule_CoversBareHostToo()
    {
        var rules = Rules(new[] { "www.youtube.com" }, Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal("exact:www.youtube.com", rules.Match("https://youtube.com/watch"));
        Assert.Equal("exact:www.youtube.com", rules.Match("https://www.youtube.com/watch"));
    }

    [Fact]
    public void ExactRule_SiblingHostStaysOpen()
    {
        var rules = Rules(new[] { "music.youtube.com" }, Array.Empty<string>(), Array.Empty<string>());
        Assert.Null(rules.Match("https://www.youtube.com/watch"));
        Assert.Equal("exact:music.youtube.com", rules.Match("https://music.youtube.com/"));
    }

    [Fact]
    public void CategoryRules_KeepWholeDomainSuffixCoverage()
    {
        var rules = Rules(Array.Empty<string>(), new[] { "facebook.com" }, Array.Empty<string>());
        Assert.Equal("host:facebook.com", rules.Match("https://www.facebook.com/profile"));
        Assert.Equal("host:facebook.com", rules.Match("https://m.facebook.com/home"));
        Assert.Equal("host:facebook.com", rules.Match("https://touch.facebook.com/"));
    }

    [Fact]
    public void PathRules_KeepSubtreeMatching()
    {
        var rules = Rules(Array.Empty<string>(), Array.Empty<string>(), new[] { "youtube.com/shorts" });
        Assert.Equal("path:youtube.com/shorts", rules.Match("https://m.youtube.com/shorts/x"));
        Assert.Equal("path:youtube.com/shorts", rules.Match("https://www.youtube.com/shorts/video1"));
        Assert.Null(rules.Match("https://www.youtube.com/watch?v=1"));
    }

    [Fact]
    public void Parse_ReadsAllThreeRuleGroups()
    {
        var rules = TabRules.Parse(RulesJson);
        Assert.Equal(new[] { "youtube.com" }, rules.ExactDomains);
        Assert.Equal(new[] { "facebook.com" }, rules.Domains);
        Assert.Equal(new[] { "youtube.com/shorts" }, rules.Paths);
        Assert.False(rules.IsEmpty);
    }

    [Fact]
    public void Parse_LegacyPayloadWithoutExactDomains_StillWorks()
    {
        var rules = TabRules.Parse("""{"domains":["facebook.com"],"paths":["youtube.com/shorts"]}""");
        Assert.Empty(rules.ExactDomains);
        // Old shape keeps the old (suffix) behavior for its domains.
        Assert.Equal("host:facebook.com", rules.Match("https://m.facebook.com/"));
    }

    [Fact]
    public void Parse_Garbage_ReturnsEmpty()
    {
        Assert.True(TabRules.Parse("not json").IsEmpty);
        Assert.True(TabRules.Parse("{}").IsEmpty);
    }

    [Fact]
    public void MixedRules_ExactMiss_DoesNotFallThroughToSuffix()
    {
        // The exact list is authoritative for parent-typed SITES: no other group may
        // re-introduce subdomain coverage for them. Path rules are a separate feature
        // (a subtree like /shorts keeps its subdomain coverage by design).
        var rules = Rules(new[] { "youtube.com" }, new[] { "facebook.com" }, new[] { "youtube.com/shorts" });
        Assert.Null(rules.Match("https://music.youtube.com/"));
        Assert.Equal("path:youtube.com/shorts", rules.Match("https://music.youtube.com/shorts/x"));
        Assert.Equal("exact:youtube.com", rules.Match("https://youtube.com/shorts/x"));
        Assert.Equal("host:facebook.com", rules.Match("https://m.facebook.com/"));
    }

    [Theory]
    [InlineData("ftp://youtube.com/x")]
    [InlineData("about:blank")]
    public void NonHttpUrls_NeverMatch(string url)
    {
        Assert.Null(Rules(new[] { "youtube.com" }, Array.Empty<string>(), Array.Empty<string>()).Match(url));
    }
}
