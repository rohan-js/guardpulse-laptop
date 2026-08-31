namespace GuardPulse.Agent.Core.Tests;

using GuardPulse.Agent.Core;
using Xunit;

public class BrowserDomainsTests
{
    [Theory]
    [InlineData("https://github.com/guardpulse", "github.com")]
    [InlineData("https://www.youtube.com/watch?v=x", "youtube.com")]
    [InlineData("http://News.Site.example:8080/a", "news.site.example")]
    [InlineData("youtube.com/shorts/OOSzHH0QkgY", "youtube.com")]
    public void Extract_HostMinusWww(string url, string expected)
    {
        Assert.Equal(expected, BrowserDomains.Extract(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("file:///C:/temp/x.html")]
    [InlineData("ftp://files.example.com/a")]
    public void Extract_NonHttp_ReturnsNull(string? url)
    {
        Assert.Null(BrowserDomains.Extract(url));
    }
}

public class ActivityLogTabSessionTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly long BaseMs = Base.ToUnixTimeMilliseconds();

    private readonly string stateDir = CreateTempDir();

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

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gp-activity-tabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ShortTabSessions_AreDropped()
    {
        var time = new FakeTimeProvider(Base);
        var log = new ActivityLog(this.stateDir, time);

        log.StartTab("c:\\browsers\\brave.exe", "Quick tab", BaseMs);
        log.CloseTab(BaseMs + 3_000);

        Assert.Empty(log.Pending());
    }

    [Fact]
    public void TabSession_QueuesWithTypeTab_AndSurvivesReload()
    {
        var time = new FakeTimeProvider(Base);
        var log = new ActivityLog(this.stateDir, time);

        log.StartTab("c:\\browsers\\brave.exe", "Wikipedia", BaseMs, url: "https://example.com/page");
        log.CloseTab(BaseMs + 60_000);
        log.Flush();

        var pending = log.Pending();
        var entry = Assert.Single(pending);
        Assert.Equal("tab", entry.type);
        Assert.Equal("c:\\browsers\\brave.exe", entry.appKey);
        Assert.Equal("Wikipedia", entry.appLabel);
        Assert.Equal("https://example.com/page", entry.url);
        Assert.Equal(BaseMs, entry.startedAt);
        Assert.Equal(BaseMs + 60_000, entry.endedAt);

        // A new log over the same state dir keeps the queued entry (queue-then-ack).
        var reloaded = new ActivityLog(this.stateDir, time);
        Assert.Single(reloaded.Pending());
    }

    [Fact]
    public void AppSessions_KeepTypeApp()
    {
        var time = new FakeTimeProvider(Base);
        var log = new ActivityLog(this.stateDir, time);

        log.StartApp("c:\\apps\\notepad.exe", "Notepad", BaseMs);
        log.CloseCurrent(BaseMs + 30_000);

        var entry = Assert.Single(log.Pending());
        Assert.Equal("app", entry.type);
    }

    [Fact]
    public void TabAndAppSessions_TrackIndependently()
    {
        var time = new FakeTimeProvider(Base);
        var log = new ActivityLog(this.stateDir, time);

        log.StartApp("c:\\browsers\\brave.exe", "Brave", BaseMs);
        log.StartTab("c:\\browsers\\brave.exe", "Docs", BaseMs + 1_000);
        log.StartTab("c:\\browsers\\brave.exe", "Mail", BaseMs + 30_000);
        // Switching app closes the open tab session (Mail: 10s) and the app session.
        log.StartApp("c:\\apps\\game.exe", "Game", BaseMs + 40_000);
        log.CloseCurrent(BaseMs + 50_000);

        var entries = log.Pending();
        Assert.Equal(4, entries.Count);
        Assert.Equal(2, entries.Count(e => e.type == "tab"));
        Assert.Equal(2, entries.Count(e => e.type == "app"));
    }
}
