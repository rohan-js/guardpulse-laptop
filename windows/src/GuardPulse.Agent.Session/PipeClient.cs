using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace GuardPulse.Agent.Session;

/// <summary>
/// Newline-delimited JSON client for the "guardpulse-laptop-agent" named pipe
/// owned by the GuardPulse Agent Service. Auto-reconnects until disposed.
/// Sends go through an unbounded outbound channel drained by a dedicated writer
/// task, so callers (foreground poll, keypad) can never block on a stalled pipe.
/// Message shapes are fixed by windows/CONTRACTS.md.
/// </summary>
public sealed class PipeClient : IDisposable
{
    private const string PipeName = "guardpulse-laptop-agent";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cts = new();
    // Bounded with DropOldest: if the writer stalls, old messages are dropped in
    // favor of new ones instead of growing the queue without limit.
    private readonly Channel<string> _outbound = Channel.CreateBounded<string>(
        new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private Task? _receiveLoop;
    private Task? _writerLoop;
    private NamedPipeClientStream? _stream;
    private StreamWriter? _writer;
    private readonly object _sendLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pending = new();
    private long _lastForegroundSentAt;
    private string? _lastForegroundAppKey;

    public event Action<JsonElement>? MessageReceived;
    public event Action? Connected;
    public event Action? Disconnected;

    public bool IsConnected => _stream?.IsConnected == true;

    public void Start()
    {
        if (_receiveLoop != null) return;
        _writerLoop = Task.Run(() => RunWriterAsync(_cts.Token));
        _receiveLoop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunWriterAsync(CancellationToken ct)
    {
        var reader = _outbound.Reader;
        while (!ct.IsCancellationRequested)
        {
            string json;
            try
            {
                json = await reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_sendLock)
            {
                try
                {
                    _writer?.WriteLine(json);
                }
                catch (Exception)
                {
                    // write failed (pipe down / disposing); the reconnect drain in
                    // RunAsync clears this stale backlog, so it is not re-queued here
                }
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _stream = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _stream.ConnectAsync(15_000, ct);

                // Reconnect: do NOT replay the pre-restart backlog. Everything queued
                // while disconnected describes a service/world state that no longer
                // exists (stale foreground/browser/pin snapshots); drain it so the
                // fresh service session is not fed old state. The hello below plus a
                // forced fresh foreground report re-establish current state.
                lock (_sendLock)
                {
                    while (_outbound.Reader.TryRead(out _)) { }
                }

                _writer = new StreamWriter(_stream, new UTF8Encoding(false))
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                Send(new
                {
                    t = "hello",
                    pid = Environment.ProcessId,
                    session = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId
                });

                Connected?.Invoke();

                using var reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break; // server closed
                    if (line.Length == 0) continue;
                    JsonElement element;
                    try
                    {
                        element = JsonSerializer.Deserialize<JsonElement>(line);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    // Replies to SendRequestAsync are correlated by the "req" id
                    // the caller supplied; route them to the pending task, not
                    // MessageReceived. A reply whose caller already timed out is
                    // dropped instead of being broadcast as a push message.
                    if (element.ValueKind == JsonValueKind.Object &&
                        element.TryGetProperty("req", out var reqElement) &&
                        reqElement.ValueKind == JsonValueKind.String &&
                        reqElement.GetString() is { } req)
                    {
                        if (_pending.TryRemove(req, out var pending))
                        {
                            pending.TrySetResult(element);
                        }

                        continue;
                    }
                    DispatchOnUiThread(element);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // pipe not ready yet or connection dropped; retry shortly
            }
            finally
            {
                Disconnected?.Invoke();
                lock (_sendLock)
                {
                    _writer = null;
                }
                _stream?.Dispose();
                _stream = null;

                // A lost connection can never answer in-flight requests.
                foreach (var pending in _pending)
                {
                    pending.Value.TrySetResult(null);
                }
                _pending.Clear();
            }

            try
            {
                await Task.Delay(ReconnectDelay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void DispatchOnUiThread(JsonElement element)
    {
        // Async marshal: the receive loop must keep draining the pipe even when
        // the UI thread is busy, otherwise server broadcasts would back up.
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(() => MessageReceived?.Invoke(element));
        }
        else
        {
            MessageReceived?.Invoke(element);
        }
    }

    public void SendTabClosed(string url)
    {
        Send(new { t = "tabClosed", url });
    }

    public void SendForeground(string appKey, string exePath, string? windowTitle)
    {
        var now = Environment.TickCount64;
        // Debounce identical rapid repeats (window churn) to 150ms.
        if (appKey == _lastForegroundAppKey && now - _lastForegroundSentAt < 150) return;
        _lastForegroundAppKey = appKey;
        _lastForegroundSentAt = now;

        Send(new { t = "foreground", appKey, exePath, windowTitle });
    }

    /// <summary>Live browser tab state from <see cref="BrowserWatcher"/>; shape fixed by CONTRACTS.md.</summary>
    internal void SendBrowser(BrowserSnapshot snapshot)
    {
        Send(new
        {
            t = "browser",
            browser = snapshot.AppKey,
            label = snapshot.Label,
            activeTab = snapshot.ActiveTab,
            activeUrl = snapshot.ActiveUrl,
            tabCount = snapshot.TabCount,
            tabs = snapshot.Tabs.Select(t => t.Url is null
                ? (object)new { title = t.Title }
                : new { title = t.Title, url = t.Url }).ToArray(),
            urlSource = snapshot.UrlSource,
        });
    }

    public void SendPin(string digits) => Send(new { t = "pin", digits });
    public void SendAskParent(string appKey) => Send(new { t = "askParent", appKey });
    public void SendSetupClosed() => Send(new { t = "setupClosed" });
    public void SendAdminState(bool isAdmin) => Send(new { t = "adminState", isAdmin });

    /// <summary>
    /// Sends a request and waits for the correlated reply, or null on timeout
    /// or if the pipe disconnects before the reply arrives.
    /// </summary>
    public async Task<JsonElement?> SendRequestAsync(string t, object payload, TimeSpan timeout)
    {
        var req = Guid.NewGuid().ToString("N");

        var message = new JsonObject
        {
            ["t"] = t,
            ["req"] = req
        };
        if (JsonSerializer.SerializeToNode(payload) is JsonObject props)
        {
            foreach (var prop in props)
            {
                message[prop.Key] = prop.Value?.DeepClone();
            }
        }

        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[req] = tcs;

        SendRaw(message.ToJsonString());

        try
        {
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            return winner == tcs.Task ? await tcs.Task : null;
        }
        finally
        {
            // Drop the entry on timeout; on completion the receive loop already
            // removed it, so this is a no-op then.
            _pending.TryRemove(req, out _);
        }
    }

    private void SendRaw(string json)
    {
        // Never block callers on the pipe; the writer task drains the channel.
        _outbound.Writer.TryWrite(json);
    }

    private void Send(object message)
    {
        var json = JsonSerializer.Serialize(message);
        // Never block callers on the pipe; the writer task drains the channel.
        _outbound.Writer.TryWrite(json);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            // Join both loops before tearing down the stream/CTS: cancelling a pending
            // Delay surfaces as OperationCanceledException, but if we disposed the
            // stream/CTS first, a loop still inside Delay/read could throw an
            // unobserved ObjectDisposedException.
            Task.WaitAll(
                new[] { _receiveLoop, _writerLoop }.Where(t => t is not null).Cast<Task>().ToArray(),
                TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // AggregateException from loop faults or the join timeout: shutdown races are fine
        }

        _cts.Dispose();
        _stream?.Dispose();
    }
}
