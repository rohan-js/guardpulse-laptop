namespace GuardPulse.Agent.Core.Tests;

using System.Text.Json;
using GuardPulse.Agent.Core;
using Xunit;

public class ActivityLogTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly long BaseMs = Base.ToUnixTimeMilliseconds();

    private readonly string stateDir = CreateTempDir();

    [Fact]
    public void StartApp_PopulatesCurrentSnapshotFields()
    {
        var time = new FakeTimeProvider(Base);
        var log = new ActivityLog(this.stateDir, time);

        log.StartApp("c:\\apps\\media.exe", "Media Center", BaseMs);

        var current = Assert.IsType<ActivityLog.CurrentSnapshot>(log.Current());
        Assert.Equal("c:\\apps\\media.exe", current.runtimeApp);
        Assert.Equal("c:\\apps\\media.exe", current.appKey);
        Assert.Equal("Media Center", current.appLabel);
        Assert.Equal(BaseMs, current.appStartedAt);
        Assert.Equal("none", current.overlayState);
        Assert.False(current.mediaAvailable);
        Assert.Equal("unknown", current.playbackState);
        Assert.Equal(0, current.playbackSpeed);
        Assert.Equal("agent", current.captureSource);
        Assert.Equal(BaseMs, current.updatedAt);
    }

    [Fact]
    public void SetOverlayState_UpdatesCurrent_OrIsIgnoredWithoutCurrent()
    {
        var time = new FakeTimeProvider(Base);
        var log = new ActivityLog(this.stateDir, time);

        log.SetOverlayState("locked");
        Assert.Null(log.Current());

        log.StartApp("app.a", "A", BaseMs);
        time.SetUtcNow(BaseMs + 1_000);
        log.SetOverlayState("locked");

        Assert.Equal("locked", log.Current()!.overlayState);
        Assert.Equal(BaseMs + 1_000, log.Current()!.updatedAt);
    }

    [Fact]
    public void SessionsShorterThan2Seconds_AreDroppedFromHistory()
    {
        var log = new ActivityLog(this.stateDir, new FakeTimeProvider(Base));

        log.StartApp("app.a", "A", BaseMs);
        log.CloseCurrent(BaseMs + 1_999);
        Assert.Empty(log.Pending());
        Assert.Null(log.Current());

        log.StartApp("app.a", "A", BaseMs + 2_000);
        log.CloseCurrent(BaseMs + 4_000);
        Assert.Single(log.Pending());
    }

    [Fact]
    public void CloseCurrent_QueuesEntry_QueueThenAck()
    {
        var log = new ActivityLog(this.stateDir, new FakeTimeProvider(Base));

        log.StartApp("app.a", "A", BaseMs);
        log.CloseCurrent(BaseMs + 5_000);
        log.StartApp("app.b", "B", BaseMs + 5_000);
        log.CloseCurrent(BaseMs + 50_000);

        var pending = log.Pending();
        Assert.Equal(2, pending.Count);
        Assert.All(pending, entry => Assert.Equal("app", entry.type));
        Assert.Equal("app.a", pending[0].appKey);
        Assert.Equal("app.b", pending[1].appKey);
        Assert.Equal(BaseMs, pending[0].startedAt);
        Assert.Equal(BaseMs + 5_000, pending[0].endedAt);
        Assert.Equal(BaseMs + 50_000, pending[1].endedAt);

        log.MarkUploaded(pending[0].id);
        var remaining = log.Pending();
        var entry = Assert.Single(remaining);
        Assert.Equal(pending[1].id, entry.id);

        log.MarkUploaded(pending[0].id); // idempotent
        Assert.Single(log.Pending());
        log.MarkUploaded(pending[1].id);
        Assert.Empty(log.Pending());
    }

    [Fact]
    public void PruneBefore_DropsEntriesEndedBeforeCutoff()
    {
        var log = new ActivityLog(this.stateDir, new FakeTimeProvider(Base));

        log.StartApp("app.a", "A", BaseMs);
        log.CloseCurrent(BaseMs + 5_000);
        log.StartApp("app.b", "B", BaseMs + 60_000);
        log.CloseCurrent(BaseMs + 120_000);

        log.PruneBefore(BaseMs + 10_000);

        var entry = Assert.Single(log.Pending());
        Assert.Equal("app.b", entry.appKey);
        Assert.Equal(BaseMs + 120_000, entry.endedAt);
    }

    [Fact]
    public void Current_ToFirebaseJson_HasExactlyTheSchemaFields()
    {
        var log = new ActivityLog(this.stateDir, new FakeTimeProvider(Base));
        log.StartApp("app.a", "A", BaseMs);
        log.SetOverlayState("locked");

        using var document = JsonDocument.Parse(log.Current()!.ToFirebaseJson());
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.Equal(
            new HashSet<string>
            {
                "runtimeApp", "appKey", "appLabel", "appStartedAt", "overlayState",
                "mediaAvailable", "playbackState", "playbackSpeed", "captureSource", "updatedAt",
            },
            keys);
        Assert.Equal("app.a", document.RootElement.GetProperty("appKey").GetString());
        Assert.Equal("locked", document.RootElement.GetProperty("overlayState").GetString());
        Assert.Equal("agent", document.RootElement.GetProperty("captureSource").GetString());
        Assert.Equal(BaseMs, document.RootElement.GetProperty("appStartedAt").GetInt64());
    }

    [Fact]
    public void History_ToFirebaseJson_HasExactlyTheSchemaFields()
    {
        var log = new ActivityLog(this.stateDir, new FakeTimeProvider(Base));
        log.StartApp("app.a", "A", BaseMs);
        log.CloseCurrent(BaseMs + 5_000);

        var entry = Assert.Single(log.Pending());
        using var document = JsonDocument.Parse(entry.ToFirebaseJson());
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.Equal(
            new HashSet<string>
            {
                "id", "type", "appKey", "appLabel", "startedAt", "endedAt", "updatedAt", "captureSource",
            },
            keys);
        Assert.Equal(entry.id, document.RootElement.GetProperty("id").GetString());
        Assert.Equal("app", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("agent", document.RootElement.GetProperty("captureSource").GetString());
        Assert.Equal(BaseMs + 5_000, document.RootElement.GetProperty("endedAt").GetInt64());
        Assert.Equal(BaseMs + 5_000, document.RootElement.GetProperty("updatedAt").GetInt64());
    }

    [Fact]
    public void State_SurvivesReload()
    {
        var time = new FakeTimeProvider(Base);
        var first = new ActivityLog(this.stateDir, time);
        first.StartApp("app.a", "A", BaseMs);
        first.CloseCurrent(BaseMs + 5_000);
        first.StartApp("app.b", "B", BaseMs + 5_000);
        Assert.Single(first.Pending());
        // Persistence is debounced (dirty until flushed by the service's periodic loop).
        first.Flush();

        var second = new ActivityLog(this.stateDir, time);
        Assert.Equal("app.b", second.Current()!.appKey);
        Assert.Single(second.Pending());
    }

    [Fact]
    public void Mutations_AreDebounced_UntilFlush()
    {
        var time = new FakeTimeProvider(Base);
        var log = new ActivityLog(this.stateDir, time);
        var logPath = System.IO.Path.Combine(this.stateDir, "activity-log.json");

        log.StartApp("app.a", "A", BaseMs);
        log.SetOverlayState("locked");
        log.CloseCurrent(BaseMs + 10_000);
        log.StartApp("app.b", "B", BaseMs + 10_000);

        // No disk writes happened until the flush (steady-state = zero I/O per switch).
        Assert.False(System.IO.File.Exists(logPath));

        log.Flush();
        Assert.True(System.IO.File.Exists(logPath));
        var reloaded = new ActivityLog(this.stateDir, time);
        Assert.Equal("app.b", reloaded.Current()!.appKey);
        Assert.Equal("none", reloaded.Current()!.overlayState);
        Assert.Single(reloaded.Pending()); // the closed app.a session was persisted too
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "guardpulse-activity-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.stateDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }
}
