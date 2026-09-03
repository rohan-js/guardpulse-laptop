// Named pipe host for service <-> session agent communication.
// Pipe name: guardpulse-laptop-agent (per CONTRACTS.md). Supports multiple concurrent
// agent connections (one per logon session); JSON lines in both directions; broadcasts
// go to every connected agent; agent->service messages raise events. Tracks the last
// hello per session. The pipe ACL grants Everyone read/write so per-user agents can connect.

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace GuardPulse.Agent.Service;

internal sealed class AgentPipeHost : IDisposable
{
    public const string PipeName = "guardpulse-laptop-agent";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ILogger _logger;
    private readonly List<ClientConnection> _clients = [];
    private readonly Dictionary<int, (int Pid, DateTime SeenAt)> _lastHello = new();
    private readonly object _gate = new();
    private Func<IReadOnlyCollection<int>>? _knownAgentPids;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public AgentPipeHost(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Provides the set of legitimate agent PIDs (from the watchdog) so the hello handler can reject spoofed connections.</summary>
    public void SetKnownAgentPids(Func<IReadOnlyCollection<int>>? provider)
    {
        _knownAgentPids = provider;
    }

    /// <summary>(appKey, exePath, windowTitle) from a foreground message.</summary>
    public event Action<string, string?, string?>? ForegroundReceived;

    /// <summary>Live browser tab snapshot from the agent's BrowserWatcher.</summary>
    public event Action<PipeBrowserState>? BrowserReceived;

    /// <summary>PIN digits collected by the agent; the service verifies.</summary>
    public event Action<string>? PinReceived;

    /// <summary>Child asked for a parent unlock for an app.</summary>
    public event Action<string>? AskParentReceived;

    /// <summary>The agent's setup window was closed.</summary>
    public event Action? SetupClosedReceived;

    /// <summary>(session, pid) from a hello message.</summary>
    public event Action<int, int>? HelloReceived;

    /// <summary>(session, isAdmin) from an adminState message: whether the child's account holds administrator rights.</summary>
    public event Action<int, bool>? AdminStateReceived;

    /// <summary>
    /// The setup window requested the pairing credentials ("deviceInfo" request).
    /// The handler receives the caller's req id and returns the complete reply JSON
    /// (embedding that req id), or null when the credentials are unavailable.
    /// </summary>
    public event Func<string, string?>? DeviceInfoRequest;

    public int ConnectedAgents
    {
        get { lock (_gate) return _clients.Count(c => c.IsConnected); }
    }

    public IReadOnlyDictionary<int, (int Pid, DateTime SeenAt)> LastHelloBySession
    {
        get { lock (_gate) return new Dictionary<int, (int Pid, DateTime SeenAt)>(_lastHello); }
    }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _logger.LogInformation("Pipe host listening on {PipeName}", PipeName);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // shutdown races are fine
        }

        List<ClientConnection> toClose;
        lock (_gate)
        {
            toClose = [.. _clients];
            _clients.Clear();
        }

        foreach (var client in toClose)
        {
            client.Dispose();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                var security = new PipeSecurity();
                // FullControl for the creator: creating additional instances of an
                // existing pipe is checked against the first instance's DACL, and
                // ReadWrite alone does not include the CreatePipeInstance right.
                security.AddAccessRule(new PipeAccessRule(
                    WindowsIdentity.GetCurrent().User!,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
                server = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.InOut, 32,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    inBufferSize: 0, outBufferSize: 0, pipeSecurity: security);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create pipe server; retrying in 5s");
                await DelaySafe(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                server.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pipe accept failed");
                server.Dispose();
                await DelaySafe(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            // Only our session agent may talk on this pipe: any local process can
            // connect (the DACL grants Everyone), so verify the client's image before
            // accepting a single message. Spoofed "foreground"/"pin" traffic is dropped.
            if (!IsSessionAgent(server.SafePipeHandle, out var clientPid, out var clientImage))
            {
                _logger.LogWarning(
                    "Dropping unauthorized pipe client {Pid} ({Image})", clientPid, clientImage);
                try
                {
                    server.Disconnect();
                }
                catch (Exception)
                {
                    // client may already be gone
                }

                server.Dispose();
                continue;
            }

            var client = new ClientConnection(server, _logger);
            lock (_gate)
            {
                _clients.Add(client);
            }

            _logger.LogInformation("Session agent connected (endpoint {Id}, {Count} connected)",
                client.Id, ConnectedAgents);
            _ = Task.Run(() => ClientLoopAsync(client, ct));
        }
    }

    private async Task ClientLoopAsync(ClientConnection client, CancellationToken ct)
    {
        var reader = new StreamReader(client.Stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        try
        {
            while (!ct.IsCancellationRequested && client.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                HandleLine(client, line);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (IOException)
        {
            // client went away
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pipe client loop error");
        }
        finally
        {
            RemoveClient(client);
        }
    }

    private void HandleLine(ClientConnection client, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!root.TryGetProperty("t", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                return;
            }

            switch (typeEl.GetString())
            {
                case "hello":
                {
                    var pid = GetInt(root, "pid");
                    var session = GetInt(root, "session");

                    // Accept only agents whose PID our watchdog has actually launched or
                    // observed. GetNamedPipeClientProcessId already verified the client
                    // image, but a child can rename any binary to match, so the PID must
                    // ALSO be one we know — otherwise a spoofed connection could keep
                    // ConnectedAgents > 0 and defeat the dead-man lockdown.
                    if (_knownAgentPids != null && !_knownAgentPids().Contains(pid))
                    {
                        _logger.LogWarning("Rejecting pipe client with unknown pid {Pid}", pid);
                        client.Dispose();
                        break;
                    }

                    client.Session = session;
                    lock (_gate)
                    {
                        _lastHello[session] = (pid, DateTime.UtcNow);
                    }

                    _logger.LogInformation("Agent hello: session {Session} pid {Pid}", session, pid);
                    HelloReceived?.Invoke(session, pid);
                    break;
                }

                case "foreground":
                {
                    var appKey = GetString(root, "appKey");
                    var exePath = GetString(root, "exePath");
                    var windowTitle = GetString(root, "windowTitle");
                    if (!string.IsNullOrEmpty(appKey) || !string.IsNullOrEmpty(exePath))
                    {
                        ForegroundReceived?.Invoke(appKey ?? "", string.IsNullOrEmpty(exePath) ? null : exePath,
                            string.IsNullOrEmpty(windowTitle) ? null : windowTitle);
                    }

                    break;
                }

                case "browser":
                {
                    var snapshot = ParseBrowserState(root);
                    if (snapshot != null)
                    {
                        BrowserReceived?.Invoke(snapshot);
                    }

                    break;
                }

                case "pin":
                {
                    var digits = GetString(root, "digits");
                    if (!string.IsNullOrEmpty(digits))
                    {
                        PinReceived?.Invoke(digits);
                    }

                    break;
                }

                case "askParent":
                {
                    var appKey = GetString(root, "appKey");
                    if (!string.IsNullOrEmpty(appKey))
                    {
                        AskParentReceived?.Invoke(appKey);
                    }

                    break;
                }

                case "setupClosed":
                    SetupClosedReceived?.Invoke();
                    break;

                case "adminState":
                {
                    var isAdmin = root.TryGetProperty("isAdmin", out var adminEl)
                        && adminEl.ValueKind == JsonValueKind.True;
                    AdminStateReceived?.Invoke(client.Session, isAdmin);
                    break;
                }

                case "deviceInfo":
                {
                    // Setup window asking for the pairing credentials (deviceId + QR
                    // secret + code). The service answers from its secure store over
                    // the same connection, correlated by the caller's "req" id — the
                    // secret never touches the world-readable device.json.
                    var req = root.TryGetProperty("req", out var reqEl)
                        && reqEl.ValueKind == JsonValueKind.String ? reqEl.GetString() : null;
                    if (string.IsNullOrEmpty(req))
                    {
                        break;
                    }

                    var handler = DeviceInfoRequest;
                    var reply = handler?.Invoke(req!);
                    if (reply is null)
                    {
                        client.Send(ErrorResponse(req!, "device info unavailable", "deviceInfo"));
                    }
                    else
                    {
                        client.Send(reply);
                    }

                    break;
                }
            }
        }
        catch (JsonException)
        {
            _logger.LogDebug("Invalid pipe message ignored: {Line}", line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pipe message dispatch failed");
        }
    }

    private static string ErrorResponse(string req, string error, string t = "controlResult") =>
        JsonSerializer.Serialize(new { t, req, ok = false, error }, JsonOpts);

    // ------------------------------------------------------------ verification
    private bool IsSessionAgent(SafePipeHandle pipe, out uint pid, out string image)
    {
        pid = 0;
        image = "";
        try
        {
            if (!GetNamedPipeClientProcessId(pipe, out pid))
            {
                return false;
            }

            image = QueryImagePath(pid) ?? "";
            return string.Equals(Path.GetFileName(image), "guardpulse.agent.session.exe",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pipe client verification failed for pid {Pid}", pid);
            return false;
        }
    }

    private static string? QueryImagePath(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(nint process, int flags,
        StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    // ------------------------------------------------------------- broadcasts
    public void BroadcastLock(string appKey, string appLabel, string reason) =>
        Broadcast(new { t = "lock", appKey, appLabel, reason });

    public void BroadcastUnlock() => Broadcast(new { t = "unlock" });

    public void BroadcastPinState(bool configured, long blockedUntilMs) =>
        Broadcast(new { t = "pinState", configured, blockedUntilMs });

    public void BroadcastOpenSetup() => Broadcast(new { t = "openSetup" });

    public void BroadcastPairedState(bool paired) => Broadcast(new { t = "pairedState", paired });

    public void BroadcastActivity(string appLabel, string overlayState) =>
        Broadcast(new { t = "activity", appLabel, overlayState });

    public void BroadcastWarningToast(string title, string message) =>
        Broadcast(new { t = "warningToast", title, message });

    private void Broadcast(object message)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(message, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pipe broadcast serialization failed");
            return;
        }

        List<ClientConnection> snapshot;
        lock (_gate)
        {
            snapshot = [.. _clients];
        }

        foreach (var client in snapshot)
        {
            client.Send(json);
        }
    }

    private void RemoveClient(ClientConnection client)
    {
        lock (_gate)
        {
            _clients.Remove(client);
        }

        _logger.LogInformation("Session agent disconnected (endpoint {Id}, {Count} connected)",
            client.Id, ConnectedAgents);
        client.Dispose();
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private static string GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString() ?? "";
            }
        }

        return "";
    }

    private static int GetInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (element.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i))
            {
                return i;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var i))
            {
                return i;
            }
        }

        return 0;
    }

    private static PipeBrowserState? ParseBrowserState(JsonElement root)
    {
        var appKey = GetString(root, "browser");
        if (string.IsNullOrEmpty(appKey))
        {
            return null;
        }

        var tabs = new List<PipeBrowserTab>();
        if (root.TryGetProperty("tabs", out var tabsEl) && tabsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabsEl.EnumerateArray())
            {
                if (tab.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = GetString(tab, "title");
                if (string.IsNullOrEmpty(title))
                {
                    continue;
                }

                var url = GetString(tab, "url");
                tabs.Add(new PipeBrowserTab(title, string.IsNullOrEmpty(url) ? null : url));
                if (tabs.Count >= 25)
                {
                    break; // matches the rules' intent; the agent already caps
                }
            }
        }

        var activeTab = GetString(root, "activeTab");
        var activeUrl = GetString(root, "activeUrl");
        var tabCount = GetInt(root, "tabCount");
        return new PipeBrowserState(
            AppKey: appKey,
            Label: string.IsNullOrWhiteSpace(GetString(root, "label")) ? null : GetString(root, "label"),
            ActiveTab: string.IsNullOrEmpty(activeTab) ? null : activeTab,
            ActiveUrl: string.IsNullOrEmpty(activeUrl) ? null : activeUrl,
            TabCount: tabCount > 0 ? tabCount : tabs.Count,
            Tabs: tabs,
            UrlSource: string.IsNullOrWhiteSpace(GetString(root, "urlSource")) ? null : GetString(root, "urlSource"));
    }

    public void Dispose() => Stop();

    // --------------------------------------------------------------- connection
    private sealed class ClientConnection : IDisposable
    {
        private readonly ILogger _logger;
        private readonly StreamWriter _writer;
        // Bounded with DropOldest: a wedged/non-reading client can never grow the
        // outbox without limit; stale broadcasts are dropped in favor of new ones.
        private readonly System.Threading.Channels.Channel<string> _outbox =
            System.Threading.Channels.Channel.CreateBounded<string>(
                new System.Threading.Channels.BoundedChannelOptions(4096)
                {
                    SingleReader = true,
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                });

        public ClientConnection(NamedPipeServerStream stream, ILogger logger)
        {
            Stream = stream;
            _logger = logger;
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            _ = WriterLoopAsync();
        }

        public NamedPipeServerStream Stream { get; }
        public int Id { get; } = Environment.TickCount;
        public int Session { get; set; }

        public bool IsConnected
        {
            get
            {
                try
                {
                    return Stream.IsConnected;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void Send(string json)
        {
            if (!IsConnected)
            {
                return;
            }

            _outbox.Writer.TryWrite(json);
        }

        private async Task WriterLoopAsync()
        {
            await foreach (var json in _outbox.Reader.ReadAllAsync())
            {
                try
                {
                    if (!IsConnected)
                    {
                        break;
                    }

                    await _writer.WriteLineAsync(json);
                    await _writer.FlushAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Pipe write failed for endpoint {Id}", Id);
                    break;
                }
            }
        }

        public void Dispose()
        {
            _outbox.Writer.TryComplete();
            try
            {
                _writer.Dispose();
            }
            catch
            {
                // already closed
            }

            try
            {
                Stream.Dispose();
            }
            catch
            {
                // already closed
            }
        }
    }
}

/// <summary>One open browser tab as reported by the agent's BrowserWatcher.</summary>
internal sealed record PipeBrowserTab(string Title, string? Url);

/// <summary>Live browser tab snapshot (pipe "browser" message; shape per CONTRACTS.md).</summary>
internal sealed record PipeBrowserState(
    string AppKey,
    string? Label,
    string? ActiveTab,
    string? ActiveUrl,
    int TabCount,
    IReadOnlyList<PipeBrowserTab> Tabs,
    string? UrlSource);
