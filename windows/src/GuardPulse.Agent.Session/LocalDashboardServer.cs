using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GuardPulse.Agent.Session;

/// <summary>
/// Loopback-only web server that exposes the local control dashboard. It never
/// talks to Firebase directly: every mutation is forwarded to the Service over the
/// existing named pipe (PipeClient), which writes control/v2 under the device's
/// PIN-gated authority. The server binds 127.0.0.1 only, so it is not reachable
/// from the network. A short-lived session token (set after PIN entry) keeps
/// other local pages from calling the mutation endpoints.
/// </summary>
public sealed class LocalDashboardServer : IDisposable
{
    /// <summary>The loopback base URL the page is served from.</summary>
    public const string Host = "http://127.0.0.1:37841/";

    public static string DashboardUrl => Host;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    // Control writes can take up to 3 conflict-retry rounds (GET + PUT + verify GET
    // each) when another parent is writing concurrently; 8s was too tight.
    private static readonly TimeSpan WriteRequestTimeout = TimeSpan.FromSeconds(20);

    // Session tokens (PIN + owner), keyed by token with the epoch-ms they were issued,
    // so they can be pruned when their Max-Age elapses and bounded in size.
    private const long TokenTtlMs = 24 * 60 * 60_000L;
    private const int MaxTokens = 512;
    private const long MaxRequestBodyBytes = 1_000_000;
    // SSE heartbeat: how often the event loop re-checks state even without a pulse.
    private static readonly TimeSpan EventHeartbeat = TimeSpan.FromSeconds(10);

    private readonly PipeClient? _pipe;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, long> _tokens = new();
    private readonly ConcurrentDictionary<string, long> _ownerTokens = new();
    // SSE waiters: scope key -> waiter id -> completion source. PulseDeviceChanged
    // completes the waiters so open consoles refresh immediately on a dataChanged.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, TaskCompletionSource<bool>>> _eventWaiters = new();
    private int _eventWaiterSeq;
    private Task? _loop;
    private bool _started;

    public LocalDashboardServer(PipeClient? pipe)
    {
        _pipe = pipe;
    }

    /// <summary>Starts serving. Never throws; on any bind failure the server silently disables itself.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            _listener.Prefixes.Add(Host);
            _listener.Start();
            _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            // Most common cause: the URL ACL reservation (installer) has not been applied.
            // The agent keeps running; the dashboard just won't be reachable until an admin
            // (re)installs or the reservation is added manually.
            System.Diagnostics.Debug.WriteLine("Dashboard server could not bind " + Host + ": " + ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // already stopped / never started
        }

        try
        {
            _listener.Close();
        }
        catch (Exception)
        {
            // best effort
        }

        _cts.Dispose();
    }

    /// <summary>Opens the dashboard in the user's default browser. Never throws.</summary>
    public static void OpenInBrowser()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = DashboardUrl,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception)
        {
            // no browser / shell failure: the user can type the URL manually
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // listener stopped or transient failure: back off briefly and retry
                try
                {
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            _ = Task.Run(() => HandleAsync(context, ct));
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleApiAsync(context, path, ct).ConfigureAwait(false);
            }
            else
            {
                await ServePageAsync(context).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            try
            {
                WriteJson(context, 500, JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOpts));
            }
            catch (Exception)
            {
                // response already committed
            }
        }
    }

    /// <summary>Hardening headers applied to every response (page, JSON, SSE).</summary>
    private static void ApplySecurityHeaders(HttpListenerResponse response)
    {
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
        // The dashboard is a single-file inline script/style app; this blocks external
        // content, framing and plugin surfaces without breaking it.
        response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    }

    /// <summary>Mutating routes must be POST: a cross-site top-level GET navigation carries
    /// SameSite=Lax cookies, so an unguarded GET would let a webpage log the parent out
    /// or fire control writes with a navigation.</summary>
    private static bool RequirePost(HttpListenerContext context)
    {
        if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        context.Response.Headers["Allow"] = "POST";
        WriteJson(context, 405, JsonSerializer.Serialize(new { ok = false, error = "method not allowed" }, JsonOpts));
        return false;
    }

    private async Task ServePageAsync(HttpListenerContext context)
    {
        var htmlPath = Path.Combine(AppContext.BaseDirectory, "Dashboard", "dashboard.html");
        string html;
        if (File.Exists(htmlPath))
        {
            html = await File.ReadAllTextAsync(htmlPath).ConfigureAwait(false);
        }
        else
        {
            html = FallbackHtml();
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        ApplySecurityHeaders(context.Response);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken: _cts.Token).ConfigureAwait(false);
        context.Response.Close();
    }

    private async Task HandleApiAsync(HttpListenerContext context, string path, CancellationToken ct)
    {
        // Parent console (owner) endpoints.
        if (path.Equals("/api/owner/login", StringComparison.OrdinalIgnoreCase))
        {
            if (RequirePost(context)) await HandleOwnerLoginAsync(context).ConfigureAwait(false);
            return;
        }

        if (path.Equals("/api/owner/logout", StringComparison.OrdinalIgnoreCase))
        {
            if (RequirePost(context)) HandleOwnerLogoutAsync(context);
            return;
        }

        if (path.TrimEnd('/').Equals("/api/devices", StringComparison.OrdinalIgnoreCase))
        {
            await HandleDevicesAsync(context, ct).ConfigureAwait(false);
            return;
        }

        // Live updates: pushes a state snapshot whenever the service pulses a change
        // (plus a 10s heartbeat). Local scope needs the PIN cookie; device scope the owner cookie.
        if (path.Equals("/api/events", StringComparison.OrdinalIgnoreCase))
        {
            await HandleEventsAsync(context, ct).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/api/device/", StringComparison.OrdinalIgnoreCase))
        {
            // /api/device/{id}/state | control | unlock | command | pin | unlock-respond
            var rest = path.Substring("/api/device/".Length).Trim('/');
            var parts = rest.Split('/');
            if (parts.Length != 2 || parts[0].Length == 0)
            {
                WriteJson(context, 400, JsonSerializer.Serialize(new { ok = false, error = "malformed device path" }, JsonOpts));
                return;
            }

            var deviceId = Uri.UnescapeDataString(parts[0]);
            var action = parts[1].ToLowerInvariant();
            switch (action)
            {
                case "state":
                    await HandleDeviceStateAsync(context, deviceId, ct).ConfigureAwait(false);
                    break;
                case "control":
                    if (RequirePost(context)) await HandleDeviceControlAsync(context, deviceId, ct).ConfigureAwait(false);
                    break;
                case "unlock":
                    if (RequirePost(context)) await HandleDeviceUnlockAsync(context, deviceId, ct).ConfigureAwait(false);
                    break;
                case "command":
                    if (RequirePost(context)) await HandleDeviceCommandAsync(context, deviceId, ct).ConfigureAwait(false);
                    break;
                case "pin":
                    if (RequirePost(context)) await HandleDevicePinAsync(context, deviceId, ct).ConfigureAwait(false);
                    break;
                case "unlock-respond":
                    if (RequirePost(context)) await HandleDeviceUnlockRespondAsync(context, deviceId, ct).ConfigureAwait(false);
                    break;
                default:
                    WriteJson(context, 404, JsonSerializer.Serialize(new { ok = false, error = "not found" }, JsonOpts));
                    break;
            }

            return;
        }

        // Fixed local routes: compare case-insensitively (the device-path branches above
        // already are; the old ordinal switch 404'd /API/STATE with no method check).
        switch (path.ToLowerInvariant())
        {
            case "/api/state":
                await HandleStateAsync(context, ct).ConfigureAwait(false);
                return;
            case "/api/login":
                if (RequirePost(context)) await HandleLoginAsync(context).ConfigureAwait(false);
                return;
            case "/api/logout":
                if (RequirePost(context)) HandleLocalLogoutAsync(context);
                return;
            case "/api/control":
                if (RequirePost(context)) await HandleControlAsync(context, ct).ConfigureAwait(false);
                return;
            case "/api/unlock":
                if (RequirePost(context)) await HandleUnlockAsync(context, ct).ConfigureAwait(false);
                return;
            default:
                WriteJson(context, 404, JsonSerializer.Serialize(new { ok = false, error = "not found" }, JsonOpts));
                return;
        }
    }

    private bool Authenticated(HttpListenerRequest request)
    {
        PruneTokens();
        var cookie = request.Cookies["gp_dash_token"]?.Value;
        return !string.IsNullOrEmpty(cookie) && _tokens.ContainsKey(cookie!);
    }

    private async Task HandleStateAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!IsPipeReady())
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "service unavailable" }, JsonOpts));
            return;
        }

        var reply = await _pipe!.SendRequestAsync("controlGet", new { }, RequestTimeout).ConfigureAwait(false);
        if (!reply.HasValue)
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "no service" }, JsonOpts));
            return;
        }

        var element = reply.Value;
        if (!element.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.True)
        {
            var error = element.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString()
                : "unknown error";
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error }, JsonOpts));
            return;
        }

        if (element.TryGetProperty("state", out var stateEl))
        {
            WriteRawJson(context, 200, stateEl.GetRawText());
        }
        else
        {
            WriteJson(context, 200, "{}");
        }
    }

    private async Task HandleLoginAsync(HttpListenerContext context)
    {
        if (!IsPipeReady())
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "service unavailable" }, JsonOpts));
            return;
        }

        var pin = "";
        try
        {
            var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("pin", out var pinEl) && pinEl.ValueKind == JsonValueKind.String)
            {
                pin = pinEl.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // empty body treated as pin-less login attempt
        }

        var reply = await _pipe!.SendRequestAsync("controlLogin", new { pin }, RequestTimeout).ConfigureAwait(false);
        string? error = null;
        if (!reply.HasValue || !ReplyOk(reply.Value, out _, out error))
        {
            WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error = error ?? "rejected" }, JsonOpts));
            return;
        }

        var token = MakeToken();
        _tokens[token] = NowMs();
        PruneTokens();
        context.Response.Headers["Set-Cookie"] =
            "gp_dash_token=" + token + "; Path=/; HttpOnly; SameSite=Lax; Max-Age=86400";
        WriteJson(context, 200, JsonSerializer.Serialize(new { ok = true }, JsonOpts));
    }

    private async Task HandleControlAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!Authenticated(context.Request))
        {
            WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error = "not authorized" }, JsonOpts));
            return;
        }

        if (!IsPipeReady())
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "service unavailable" }, JsonOpts));
            return;
        }

        JsonNode? patchNode = null;
        try
        {
            var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("patch", out var patchEl) && patchEl.ValueKind == JsonValueKind.Object)
            {
                patchNode = JsonNode.Parse(patchEl.GetRawText());
            }
        }
        catch (JsonException ex)
        {
            WriteJson(context, 400, JsonSerializer.Serialize(new { ok = false, error = "invalid json: " + ex.Message }, JsonOpts));
            return;
        }

        var payload = new JsonObject { ["patch"] = patchNode };
        var reply = await _pipe!.SendRequestAsync("controlWrite", payload, WriteRequestTimeout).ConfigureAwait(false);
        if (!reply.HasValue)
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "no service" }, JsonOpts));
            return;
        }

        // Forward the full envelope (incl. revisionId) so the page can show
        // "waiting for device" until the device acks this exact revision.
        ForwardRawOrError(context, reply.Value);
    }

    private async Task HandleUnlockAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!Authenticated(context.Request))
        {
            WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error = "not authorized" }, JsonOpts));
            return;
        }

        if (!IsPipeReady())
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "service unavailable" }, JsonOpts));
            return;
        }

        string appKey = "";
        string type = "oneVisit";
        long? durationMs = null;
        try
        {
            var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("appKey", out var k) && k.ValueKind == JsonValueKind.String)
            {
                appKey = k.GetString() ?? "";
            }

            if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
            {
                type = t.GetString() ?? "oneVisit";
            }

            if (root.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var dur))
            {
                durationMs = dur;
            }
        }
        catch (JsonException ex)
        {
            WriteJson(context, 400, JsonSerializer.Serialize(new { ok = false, error = "invalid json: " + ex.Message }, JsonOpts));
            return;
        }

        var payload = new JsonObject
        {
            ["appKey"] = appKey,
            ["type"] = type,
            ["durationMs"] = durationMs.HasValue ? JsonValue.Create(durationMs.Value) : null
        };
        var reply = await _pipe!.SendRequestAsync("controlUnlock", payload, RequestTimeout).ConfigureAwait(false);
        if (!reply.HasValue)
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "no service" }, JsonOpts));
            return;
        }

        ForwardRawOrError(context, reply.Value);
    }

    // ----------------------------------------------------------- parent console (owner)

    private bool OwnerAuthenticated(HttpListenerRequest request)
    {
        PruneTokens();
        var cookie = request.Cookies["gp_owner_token"]?.Value;
        return !string.IsNullOrEmpty(cookie) && _ownerTokens.ContainsKey(cookie!);
    }

    private async Task HandleOwnerLoginAsync(HttpListenerContext context)
    {
        if (!IsPipeReady())
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "service unavailable" }, JsonOpts));
            return;
        }

        string email = "";
        string password = "";
        try
        {
            var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String)
            {
                email = e.GetString() ?? "";
            }

            if (root.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String)
            {
                password = p.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // empty body treated as a blank login attempt
        }

        var payload = new JsonObject { ["email"] = email, ["password"] = password };
        var reply = await _pipe!.SendRequestAsync("ownerLogin", payload, RequestTimeout).ConfigureAwait(false);
        if (!reply.HasValue)
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "no service" }, JsonOpts));
            return;
        }

        var element = reply.Value;
        var ok = element.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        if (!ok)
        {
            var error = element.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString() : "sign-in failed";
            WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error }, JsonOpts));
            return;
        }

        var token = MakeToken();
        _ownerTokens[token] = NowMs();
        PruneTokens();
        context.Response.Headers["Set-Cookie"] =
            "gp_owner_token=" + token + "; Path=/; HttpOnly; SameSite=Lax; Max-Age=86400";

        // Return the full ownerLoginResult (includes the device list) so the page renders immediately.
        WriteRawJson(context, 200, element.GetRawText());
    }

    private Task HandleOwnerLogoutAsync(HttpListenerContext context)
    {
        var cookie = context.Request.Cookies["gp_owner_token"]?.Value;
        if (!string.IsNullOrEmpty(cookie))
        {
            _ownerTokens.TryRemove(cookie!, out _);
        }

        context.Response.Headers["Set-Cookie"] =
            "gp_owner_token=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0";
        WriteJson(context, 200, JsonSerializer.Serialize(new { ok = true }, JsonOpts));
        return Task.CompletedTask;
    }

    /// <summary>Revokes the local PIN session server-side (the sign-out/"Lock console" button);
    /// until now a gp_dash_token could not be revoked before its 24h TTL.</summary>
    private Task HandleLocalLogoutAsync(HttpListenerContext context)
    {
        var cookie = context.Request.Cookies["gp_dash_token"]?.Value;
        if (!string.IsNullOrEmpty(cookie))
        {
            _tokens.TryRemove(cookie!, out _);
        }

        context.Response.Headers["Set-Cookie"] =
            "gp_dash_token=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0";
        WriteJson(context, 200, JsonSerializer.Serialize(new { ok = true }, JsonOpts));
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- SSE events
    /// <summary>
    /// GET /api/events?device=local|{deviceId} — server-sent events carrying the state
    /// DTO. A snapshot is pushed immediately, then whenever <see cref="PulseDeviceChanged"/>
    /// fires for the scope (wired to the service's dataChanged broadcasts) and on a 10s
    /// heartbeat. Only scopes with an open console are serviced; the service's own state
    /// cache bounds the Firebase reads behind this endpoint.
    /// </summary>
    private async Task HandleEventsAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers["Allow"] = "GET";
            WriteJson(context, 405, JsonSerializer.Serialize(new { ok = false, error = "method not allowed" }, JsonOpts));
            return;
        }

        var device = context.Request.QueryString["device"];
        if (string.IsNullOrWhiteSpace(device))
        {
            device = "local";
        }

        string scopeKey;
        if (string.Equals(device, "local", StringComparison.OrdinalIgnoreCase))
        {
            if (!Authenticated(context.Request))
            {
                WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error = "not authorized" }, JsonOpts));
                return;
            }

            scopeKey = "local";
        }
        else
        {
            if (!OwnerAuthenticated(context.Request))
            {
                WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error = "not authorized" }, JsonOpts));
                return;
            }

            scopeKey = Uri.UnescapeDataString(device);
        }

        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-store";
        response.SendChunked = true;
        try
        {
            ApplySecurityHeaders(response);
            var lastJson = "";
            while (!ct.IsCancellationRequested)
            {
                var json = await FetchStateRawAsync(scopeKey, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(json) && !string.Equals(json, lastJson, StringComparison.Ordinal))
                {
                    lastJson = json!;
                    await WriteSseEventAsync(response, "state", json!, ct).ConfigureAwait(false);
                }

                await WaitForPulseAsync(scopeKey, EventHeartbeat, ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // client disconnected, pipe down or shutting down; the finally closes cleanly
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // already gone
            }
        }
    }

    /// <summary>Fetches the raw state DTO for a scope over the pipe (null when unavailable).</summary>
    private async Task<string?> FetchStateRawAsync(string scopeKey, CancellationToken ct)
    {
        if (_pipe is null || !_pipe.IsConnected)
        {
            return null;
        }

        var (type, payload) = scopeKey == "local"
            ? ("controlGet", (object)new { })
            : ("deviceState", (object)new { deviceId = scopeKey });

        var reply = await _pipe.SendRequestAsync(type, payload, RequestTimeout).ConfigureAwait(false);
        if (!reply.HasValue)
        {
            return null;
        }

        var element = reply.Value;
        if (!element.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        return element.TryGetProperty("state", out var stateEl) ? stateEl.GetRawText() : null;
    }

    private static async Task WriteSseEventAsync(HttpListenerResponse response, string eventName, string data, CancellationToken ct)
    {
        // The state DTO is compact JSON (no raw newlines), but strip defensively — a raw
        // newline inside an SSE data block would split the event.
        var payload = "event: " + eventName + "\ndata: " + data.Replace("\n", " ") + "\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Waits until the scope is pulsed, the timeout elapses, or cancellation fires.</summary>
    private async Task WaitForPulseAsync(string scopeKey, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref _eventWaiterSeq);
        var waiters = _eventWaiters.GetOrAdd(scopeKey, _ => new ConcurrentDictionary<int, TaskCompletionSource<bool>>());
        waiters[id] = tcs;
        try
        {
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            waiters.TryRemove(id, out _);
            if (waiters.IsEmpty)
            {
                _eventWaiters.TryRemove(KeyValuePair.Create(scopeKey, waiters));
            }
        }
    }

    /// <summary>
    /// Wakes consoles waiting on the event stream. Called by the session host when the
    /// service broadcasts dataChanged: the local scope always pulses (a dataChanged for
    /// this device's own node is relevant to it), plus the named device scope.
    /// </summary>
    public void PulseDeviceChanged(string? deviceId)
    {
        PulseScope("local");
        if (!string.IsNullOrWhiteSpace(deviceId) && !string.Equals(deviceId, "local", StringComparison.OrdinalIgnoreCase))
        {
            PulseScope(deviceId);
        }
    }

    private void PulseScope(string scopeKey)
    {
        if (_eventWaiters.TryGetValue(scopeKey, out var waiters))
        {
            foreach (var kv in waiters)
            {
                kv.Value.TrySetResult(true);
            }
        }
    }

    private async Task HandleDevicesAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!OwnerAuthenticated(context.Request))
        {
            WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error = "not authorized" }, JsonOpts));
            return;
        }

        if (!IsPipeReady())
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "service unavailable" }, JsonOpts));
            return;
        }

        var reply = await _pipe!.SendRequestAsync("listDevices", new { }, RequestTimeout).ConfigureAwait(false);
        if (!reply.HasValue)
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "no service" }, JsonOpts));
            return;
        }

        ForwardRawOrError(context, reply.Value);
    }

    private async Task HandleDeviceStateAsync(HttpListenerContext context, string deviceId, CancellationToken ct)
    {
        if (!OwnerAuthenticated(context.Request))
        {
            WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error = "not authorized" }, JsonOpts));
            return;
        }

        if (!IsPipeReady())
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "service unavailable" }, JsonOpts));
            return;
        }

        var payload = new JsonObject { ["deviceId"] = deviceId };
        var reply = await _pipe!.SendRequestAsync("deviceState", payload, RequestTimeout).ConfigureAwait(false);
        if (!reply.HasValue)
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "no service" }, JsonOpts));
            return;
        }

        ForwardRawOrError(context, reply.Value);
    }

    private async Task HandleDeviceControlAsync(HttpListenerContext context, string deviceId, CancellationToken ct)
    {
        if (!OwnerAuthenticated(context.Request))
        {
            WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error = "not authorized" }, JsonOpts));
            return;
        }

        if (!IsPipeReady())
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "service unavailable" }, JsonOpts));
            return;
        }

        JsonNode? patchNode = null;
        try
        {
            var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("patch", out var patchEl) && patchEl.ValueKind == JsonValueKind.Object)
            {
                patchNode = JsonNode.Parse(patchEl.GetRawText());
            }
        }
        catch (JsonException ex)
        {
            WriteJson(context, 400, JsonSerializer.Serialize(new { ok = false, error = "invalid json: " + ex.Message }, JsonOpts));
            return;
        }

        var payload = new JsonObject { ["deviceId"] = deviceId, ["patch"] = patchNode };
        var reply = await _pipe!.SendRequestAsync("deviceWrite", payload, WriteRequestTimeout).ConfigureAwait(false);
        if (!reply.HasValue)
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "no service" }, JsonOpts));
            return;
        }

        ForwardRawOrError(context, reply.Value);
    }

    private async Task HandleDeviceUnlockAsync(HttpListenerContext context, string deviceId, CancellationToken ct)
    {
        if (!OwnerAuthenticated(context.Request))
        {
            WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error = "not authorized" }, JsonOpts));
            return;
        }

        if (!IsPipeReady())
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "service unavailable" }, JsonOpts));
            return;
        }

        string appKey = "";
        string type = "oneVisit";
        long? durationMs = null;
        try
        {
            var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("appKey", out var k) && k.ValueKind == JsonValueKind.String)
            {
                appKey = k.GetString() ?? "";
            }

            if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
            {
                type = t.GetString() ?? "oneVisit";
            }

            if (root.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var dur))
            {
                durationMs = dur;
            }
        }
        catch (JsonException ex)
        {
            WriteJson(context, 400, JsonSerializer.Serialize(new { ok = false, error = "invalid json: " + ex.Message }, JsonOpts));
            return;
        }

        var payload = new JsonObject
        {
            ["deviceId"] = deviceId,
            ["appKey"] = appKey,
            ["type"] = type,
            ["durationMs"] = durationMs.HasValue ? JsonValue.Create(durationMs.Value) : null
        };
        var reply = await _pipe!.SendRequestAsync("deviceUnlock", payload, WriteRequestTimeout).ConfigureAwait(false);
        if (!reply.HasValue)
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "no service" }, JsonOpts));
            return;
        }

        ForwardRawOrError(context, reply.Value);
    }

    private async Task HandleDeviceCommandAsync(HttpListenerContext context, string deviceId, CancellationToken ct)
    {
        if (!OwnerAuthenticated(context.Request))
        {
            WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error = "not authorized" }, JsonOpts));
            return;
        }

        if (!IsPipeReady())
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "service unavailable" }, JsonOpts));
            return;
        }

        string type = "";
        string? packageName = null;
        try
        {
            var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
            {
                type = t.GetString() ?? "";
            }

            if (root.TryGetProperty("packageName", out var p) && p.ValueKind == JsonValueKind.String)
            {
                packageName = p.GetString();
            }
        }
        catch (JsonException) { }

        var payload = new JsonObject { ["deviceId"] = deviceId, ["type"] = type, ["packageName"] = packageName };
        var reply = await _pipe!.SendRequestAsync("deviceCommand", payload, WriteRequestTimeout).ConfigureAwait(false);
        if (!reply.HasValue)
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "no service" }, JsonOpts));
            return;
        }

        ForwardRawOrError(context, reply.Value);
    }

    private async Task HandleDevicePinAsync(HttpListenerContext context, string deviceId, CancellationToken ct)
    {
        if (!OwnerAuthenticated(context.Request))
        {
            WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error = "not authorized" }, JsonOpts));
            return;
        }

        if (!IsPipeReady())
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "service unavailable" }, JsonOpts));
            return;
        }

        string pin = "";
        try
        {
            var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("pin", out var p) && p.ValueKind == JsonValueKind.String)
            {
                pin = p.GetString() ?? "";
            }
        }
        catch (JsonException) { }

        var payload = new JsonObject { ["deviceId"] = deviceId, ["pin"] = pin };
        var reply = await _pipe!.SendRequestAsync("devicePin", payload, WriteRequestTimeout).ConfigureAwait(false);
        if (!reply.HasValue)
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "no service" }, JsonOpts));
            return;
        }

        ForwardRawOrError(context, reply.Value);
    }

    private async Task HandleDeviceUnlockRespondAsync(HttpListenerContext context, string deviceId, CancellationToken ct)
    {
        if (!OwnerAuthenticated(context.Request))
        {
            WriteJson(context, 401, JsonSerializer.Serialize(new { ok = false, error = "not authorized" }, JsonOpts));
            return;
        }

        if (!IsPipeReady())
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "service unavailable" }, JsonOpts));
            return;
        }

        string requestId = "";
        string action = "";
        try
        {
            var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("requestId", out var r) && r.ValueKind == JsonValueKind.String)
            {
                requestId = r.GetString() ?? "";
            }

            if (root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String)
            {
                action = a.GetString() ?? "";
            }
        }
        catch (JsonException) { }

        var payload = new JsonObject { ["deviceId"] = deviceId, ["requestId"] = requestId, ["action"] = action };
        var reply = await _pipe!.SendRequestAsync("deviceUnlockRespond", payload, WriteRequestTimeout).ConfigureAwait(false);
        if (!reply.HasValue)
        {
            WriteJson(context, 503, JsonSerializer.Serialize(new { ok = false, error = "no service" }, JsonOpts));
            return;
        }

        ForwardRawOrError(context, reply.Value);
    }

    private void ForwardRawOrError(HttpListenerContext context, JsonElement reply)
    {
        if (reply.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True)
        {
            WriteRawJson(context, 200, reply.GetRawText());
        }
        else
        {
            var error = reply.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString() : "rejected";
            WriteJson(context, 400, JsonSerializer.Serialize(new { ok = false, error }, JsonOpts));
        }
    }

    private static bool ReplyOk(JsonElement element, out JsonElement state, out string? error)
    {
        state = default;
        error = null;
        var ok = element.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        if (element.TryGetProperty("state", out var s))
        {
            state = s;
        }

        if (element.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
        {
            error = errEl.GetString();
        }

        return ok;
    }

    private bool IsPipeReady() => _pipe is not null && _pipe.IsConnected;

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        // Cap request bodies: nothing here legitimately needs more than a control patch.
        if (request.ContentLength64 > MaxRequestBodyBytes)
        {
            throw new InvalidOperationException("Request body too large.");
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (text.Length > MaxRequestBodyBytes)
        {
            throw new InvalidOperationException("Request body too large.");
        }

        return text;
    }

    private static string MakeToken()
    {
        var bytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void WriteJson(HttpListenerContext context, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ApplySecurityHeaders(context.Response);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private static void WriteRawJson(HttpListenerContext context, int status, string rawJson)
    {
        var bytes = Encoding.UTF8.GetBytes(rawJson);
        ApplySecurityHeaders(context.Response);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    /// <summary>Removes expired tokens and evicts oldest when the dictionary exceeds MaxTokens.</summary>
    private void PruneTokens()
    {
        var now = NowMs();
        PruneOne(_tokens, now);
        PruneOne(_ownerTokens, now);
    }

    private static void PruneOne(ConcurrentDictionary<string, long> tokens, long now)
    {
        // Sweep expired entries.
        foreach (var kv in tokens)
        {
            if (now - kv.Value > TokenTtlMs)
            {
                tokens.TryRemove(kv.Key, out _);
            }
        }

        // If still above the cap, evict the oldest entries.
        if (tokens.Count > MaxTokens)
        {
            var excess = tokens.Count - MaxTokens;
            foreach (var kv in tokens.OrderBy(k => k.Value).Take(excess))
            {
                tokens.TryRemove(kv.Key, out _);
            }
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string FallbackHtml() =>
        "<!doctype html><html><head><meta charset='utf-8'><title>GuardPulse</title></head>" +
        "<body style='font-family:Segoe UI,sans-serif;background:#0f1115;color:#e6e8ec;padding:40px'>" +
        "<h1>GuardPulse Dashboard</h1><p>The dashboard page (dashboard.html) was not found next to the agent. " +
        "Reinstall the agent to restore it.</p></body></html>";
}
