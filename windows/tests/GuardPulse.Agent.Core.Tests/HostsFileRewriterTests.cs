using GuardPulse.Agent.Core;
using Xunit;

public class HostsFileRewriterTests
{
    [Fact]
    public void ApplyBlock_InsertsMarkedBlock()
    {
        var block = HostsFileRewriter.BuildBlock(new Dictionary<string, IEnumerable<string>>
        {
            ["social"] = new[] { "facebook.com" }
        });
        var result = HostsFileRewriter.ApplyBlock("127.0.0.1 localhost\n", block);
        Assert.Contains(HostsFileRewriter.BeginMarker, result);
        Assert.Contains("0.0.0.0 facebook.com", result);
        Assert.Contains("127.0.0.1 localhost", result);
    }

    [Fact]
    public void ApplyBlock_RemovesBlockWhenNull()
    {
        var block = HostsFileRewriter.BuildBlock(new Dictionary<string, IEnumerable<string>> { ["social"] = new[] { "x.com" } });
        var withBlock = HostsFileRewriter.ApplyBlock("", block);
        var cleaned = HostsFileRewriter.ApplyBlock(withBlock, null);
        Assert.DoesNotContain(HostsFileRewriter.BeginMarker, cleaned);
        Assert.DoesNotContain("x.com", cleaned);
    }

    [Fact]
    public void ApplyBlock_ReplacesExistingBlockIdempotently()
    {
        var first = HostsFileRewriter.BuildBlock(new Dictionary<string, IEnumerable<string>> { ["social"] = new[] { "a.com" } });
        var second = HostsFileRewriter.BuildBlock(new Dictionary<string, IEnumerable<string>> { ["gaming"] = new[] { "b.com" } });
        var hosts = HostsFileRewriter.ApplyBlock("127.0.0.1 localhost\n", first);
        var updated = HostsFileRewriter.ApplyBlock(hosts, second);
        Assert.Contains("b.com", updated);
        Assert.DoesNotContain("a.com", updated);
        Assert.Equal(1, updated.Split(HostsFileRewriter.BeginMarker).Length - 1);
    }

    [Fact]
    public void ApplyBlock_RemovesOrphansAfterStrayEndMarker()
    {
        // Corrupt half-write: block lines present but the BEGIN marker was lost.
        var corrupt = "127.0.0.1 localhost\n0.0.0.0 orphan.com\n" + HostsFileRewriter.EndMarker + "\n";
        var cleaned = HostsFileRewriter.ApplyBlock(corrupt, null);
        Assert.DoesNotContain("orphan.com", cleaned);
        Assert.DoesNotContain(HostsFileRewriter.EndMarker, cleaned);
        Assert.Contains("127.0.0.1 localhost", cleaned);
    }

    [Fact]
    public void ApplyBlock_TruncatesAtStrayBeginMarker()
    {
        // BEGIN present but END lost: everything from the marker to EOF is dropped.
        var corrupt = "127.0.0.1 localhost\n" + HostsFileRewriter.BeginMarker + "\n0.0.0.0 a.com\n0.0.0.0 b.com\n";
        var cleaned = HostsFileRewriter.ApplyBlock(corrupt, null);
        Assert.DoesNotContain(HostsFileRewriter.BeginMarker, cleaned);
        Assert.DoesNotContain("a.com", cleaned);
        Assert.Contains("127.0.0.1 localhost", cleaned);
    }

    [Fact]
    public void ApplyBlock_DisableRemovesReplacedBlock()
    {
        var block = HostsFileRewriter.BuildBlock(new Dictionary<string, IEnumerable<string>>
        {
            ["social"] = new[] { "facebook.com" },
            ["gaming"] = new[] { "fortnite.com" },
        });
        var hosts = HostsFileRewriter.ApplyBlock("127.0.0.1 localhost\n", block);
        var disabled = HostsFileRewriter.ApplyBlock(hosts, null);
        Assert.DoesNotContain("facebook.com", disabled);
        Assert.DoesNotContain("fortnite.com", disabled);
        Assert.DoesNotContain(HostsFileRewriter.BeginMarker, disabled);
        Assert.Equal("127.0.0.1 localhost\n", disabled);
    }
}
