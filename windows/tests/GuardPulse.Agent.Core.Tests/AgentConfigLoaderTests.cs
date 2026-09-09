namespace GuardPulse.Agent.Core.Tests;

using System.IO;
using Xunit;

/// <summary>
/// Config coherence: the US-apiKey + Singapore-databaseUrl combo from the
/// 2026-09-09 cutover incident parsed fine, signed in fine, and then failed
/// EVERY cloud write silently for an hour. The loader must reject it upfront.
/// </summary>
public sealed class AgentConfigLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gp-cfg").FullName;

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "agent-config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Load_Rejects_DatabaseUrl_FromAnotherProject()
    {
        var path = WriteConfig("""
            {"apiKey":"AIzaSyUS-key","projectId":"guardpulse-laptop-control",
             "databaseUrl":"https://guardpulse-laptop-sg-default-rtdb.asia-southeast1.firebasedatabase.app"}
            """);
        var ex = Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path));
        Assert.Contains("does not belong to project", ex.Message);
    }

    [Fact]
    public void Load_Accepts_CoherentConfig_AndTrimsUrl()
    {
        var path = WriteConfig("""
            {"apiKey":"AIzaSySG-key","projectId":"guardpulse-laptop-sg",
             "databaseUrl":"https://guardpulse-laptop-sg-default-rtdb.asia-southeast1.firebasedatabase.app/"}
            """);
        var config = AgentConfigLoader.Load(path);
        Assert.Equal("guardpulse-laptop-sg", config.ProjectId);
        Assert.False(config.DatabaseUrl.EndsWith("/"));
    }

    [Fact]
    public void Load_Rejects_MissingFields()
    {
        var path = WriteConfig("""{"apiKey":"k","projectId":"p"}""");
        Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
