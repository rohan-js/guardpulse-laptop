namespace GuardPulse.Agent.Core.Tests;

using GuardPulse.Agent.Core;
using GuardPulse.Protocol;
using Xunit;

/// <summary>
/// IsStreamConnected must derive from real stream traffic: RTDB over REST has no
/// .info/connected events (the attach value is null and rules deny the path), so a
/// hardcoded/handler-driven flag would either lie or stay false forever — the
/// 0.2.13 regression that showed the paired laptop as Offline on the phone.
/// </summary>
public sealed class SyncEngineStreamHealthTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsStreamConnected_FalseBeforeAnyActivity()
    {
        var (engine, _, time) = Harness();
        Assert.False(engine.IsStreamConnected);
    }

    [Fact]
    public void IsStreamConnected_TrueAfterActivity_WithinWindow()
    {
        var (engine, _, time) = Harness();
        engine.NoteStreamActivity();
        time.Advance(TimeSpan.FromSeconds(30)); // one missed keep-alive period
        Assert.True(engine.IsStreamConnected);
    }

    [Fact]
    public void IsStreamConnected_FalseAfterWindowExpires()
    {
        var (engine, _, time) = Harness();
        engine.NoteStreamActivity();
        time.Advance(TimeSpan.FromSeconds(76)); // beyond the 75s alive window
        Assert.False(engine.IsStreamConnected);
    }

    [Fact]
    public void IsStreamConnected_RecoversOnNextActivity()
    {
        var (engine, _, time) = Harness();
        engine.NoteStreamActivity();
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(engine.IsStreamConnected);
        engine.NoteStreamActivity();
        Assert.True(engine.IsStreamConnected);
    }

    [Fact]
    public void NoteStreamActivity_ConcurrentCalls_KeepLatestTimestamp()
    {
        var (engine, _, time) = Harness();
        engine.NoteStreamActivity();
        time.Advance(TimeSpan.FromSeconds(5));
        engine.NoteStreamActivity();
        time.Advance(TimeSpan.FromSeconds(60)); // only 60s past the LAST activity
        Assert.True(engine.IsStreamConnected);
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.False(engine.IsStreamConnected);
    }

    private static (SyncEngine Engine, FakeFirebaseClient Firebase, FakeTimeProvider Time) Harness()
    {
        var time = new FakeTimeProvider(Start);
        var firebase = new FakeFirebaseClient();
        var secrets = new MemorySecretStore();
        var engine = new SyncEngine(firebase, secrets, "test-device", time);
        return (engine, firebase, time);
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new();

        public string? Get(string key) => this.values.TryGetValue(key, out var value) ? value : null;

        public void Set(string key, string value) => this.values[key] = value;

        public void Delete(string key) => this.values.Remove(key);
    }

    /// <summary>No-op client: the stream-health tests never start the engine's streams.</summary>
    private sealed class FakeFirebaseClient : IFirebaseClient
    {
        public string? Uid => null;

        public Task SignInAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetAsync(string path, CancellationToken ct) => Task.FromResult("null");

        public Task PutAsync(string path, string json, CancellationToken ct) => Task.CompletedTask;

        public Task PatchAsync(string path, string json, CancellationToken ct) => Task.CompletedTask;

        public Task<IDisposable> StreamAsync(string path, Action<string?> onData, Action<Exception> onError, CancellationToken ct, Action? onActivity = null)
            => Task.FromResult<IDisposable>(new NoopDisposable());

        public Task<long> FetchServerTimeOffsetMsAsync(CancellationToken ct) => Task.FromResult(0L);

        public void Dispose()
        {
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
