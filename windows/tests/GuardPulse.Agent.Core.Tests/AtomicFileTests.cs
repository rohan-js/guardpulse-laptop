namespace GuardPulse.Agent.Core.Tests;

using System.IO;
using Xunit;

public class AtomicFileTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "gp-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(this.dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.dir, true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }

    [Fact]
    public void WriteAllText_CreatesAndReplacesContent()
    {
        var path = Path.Combine(this.dir, "state.json");
        AtomicFile.WriteAllText(path, "{\"a\":1}");
        Assert.Equal("{\"a\":1}", File.ReadAllText(path));

        AtomicFile.WriteAllText(path, "{\"b\":2}");
        Assert.Equal("{\"b\":2}", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_CreatesParentDirectories()
    {
        var path = Path.Combine(this.dir, "nested", "deeper", "state.json");
        AtomicFile.WriteAllText(path, "hello");
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_LeavesNoTempFilesBehind()
    {
        var path = Path.Combine(this.dir, "state.json");
        AtomicFile.WriteAllText(path, "hello");
        Assert.Empty(Directory.GetFiles(this.dir, "*.tmp", SearchOption.AllDirectories));
    }
}
