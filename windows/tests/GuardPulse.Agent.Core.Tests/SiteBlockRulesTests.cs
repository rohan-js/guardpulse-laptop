using GuardPulse.Agent.Service;
using Xunit;

namespace GuardPulse.Agent.Service.Tests;

public class SiteBlockRulesTests
{
    [Fact]
    public void Build_SplitsPathsFromDomains()
    {
        var doc = SiteBlockRules.Build(
            new[] { "youtube.com/shorts", "example.com", "https://foo.bar/baz" },
            Array.Empty<string>());

        Assert.Contains("youtube.com", doc.Domains);
        Assert.Contains("example.com", doc.Domains);
        Assert.Contains("foo.bar", doc.Domains);
        Assert.Contains(doc.Paths, p => p.Host == "youtube.com" && p.Prefix == "/shorts");
        Assert.Contains(doc.Paths, p => p.Host == "foo.bar" && p.Prefix == "/baz");
        Assert.DoesNotContain(doc.Paths, p => p.Host == "example.com");
    }

    [Fact]
    public void Build_CategoryDomainsBecomeWholeDomainRules()
    {
        var doc = SiteBlockRules.Build(
            Array.Empty<string>(),
            new[] { "GamblingSite.Net", "www.adultsite.org" });

        Assert.Contains("gamblingsite.net", doc.Domains);
        Assert.Contains("www.adultsite.org", doc.Domains);
        Assert.Empty(doc.Paths);
    }

    [Theory]
    [InlineData("not a domain")]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_SkipsGarbage(string entry)
    {
        var doc = SiteBlockRules.Build(new[] { entry }, Array.Empty<string>());
        Assert.Empty(doc.Domains);
        Assert.Empty(doc.Paths);
    }

    [Fact]
    public void ToJson_ProducesFetchableDocument()
    {
        var doc = SiteBlockRules.Build(new[] { "youtube.com/shorts" }, new[] { "example.com" });
        var json = SiteBlockRules.ToJson(doc, "chrome-extension://abcdefghijklmnopabcdefghijklmnop");

        Assert.Contains("\"domains\"", json);
        Assert.Contains("example.com", json);
        Assert.Contains("chrome-extension://abcdefghijklmnopabcdefghijklmnop/blocked.html", json);
        // Must round-trip (the extension JSON.parses it).
        var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.TryGetProperty("blockPageUrl", out _));
    }

    [Fact]
    public void FromCrx_RejectsGarbage()
    {
        Assert.Null(SiteBlockCrx.FromCrx(Array.Empty<byte>()));
        Assert.Null(SiteBlockCrx.FromCrx(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }));
    }

    [Fact]
    public void ToExtensionId_MapsHexToAlpha()
    {
        // 16 zero bytes -> all 'a's; deterministic length 32.
        var id = SiteBlockCrx.ToExtensionId(new byte[16], 0, 16);
        Assert.Equal(32, id.Length);
        Assert.All(id.ToCharArray(), c => Assert.InRange(c, 'a', 'p'));
    }
}
