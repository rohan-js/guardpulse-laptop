namespace GuardPulse.Agent.Core;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Firebase Realtime Database access: anonymous Identity Toolkit auth, REST
/// read/write, and SSE streaming. Streaming emulates Firebase ValueEventListener
/// semantics: every put/patch event is merged into the streamed node and the FULL
/// merged node is delivered as raw JSON (null when the node is deleted), which is
/// what the V2 sync rules expect.
/// </summary>
public interface IFirebaseClient : IDisposable
{
    /// <summary>Anonymous uid once signed in; null before the first successful sign-in.</summary>
    string? Uid { get; }

    /// <summary>signUp or token refresh; persists the refresh token via ISecretStore ("auth.refreshToken") so the uid is stable across restarts.</summary>
    Task SignInAsync(CancellationToken ct);

    /// <summary>Returns the raw JSON at the path; "null" when absent.</summary>
    Task<string> GetAsync(string path, CancellationToken ct);

    /// <summary>PUT; a json of "null" deletes the node.</summary>
    Task PutAsync(string path, string json, CancellationToken ct);

    /// <summary>PATCH (updateChildren semantics; missing keys untouched).</summary>
    Task PatchAsync(string path, string json, CancellationToken ct);

    /// <summary>
    /// SSE value stream. onData receives the raw JSON of the node (null after the
    /// node was deleted). Reconnects with exponential backoff 5s..5min and
    /// resubscribes until the disposable is disposed or the token cancels.
    /// </summary>
    Task<IDisposable> StreamAsync(string path, Action<string?> onData, Action<Exception> onError, CancellationToken ct);

    /// <summary>GET .info/serverTimeOffset (milliseconds).</summary>
    Task<long> FetchServerTimeOffsetMsAsync(CancellationToken ct);
}

public sealed class RtdbFirebaseClient : IFirebaseClient
{
    internal const string RefreshTokenSecretKey = "auth.refreshToken";
    public const string OwnerRefreshTokenSecretKey = "auth.ownerRefreshToken";

    private const int TokenExpiryMarginSeconds = 60;
    // Fast reconnect: a dropped SSE stream must recover in seconds, not minutes -
    // every stream (control, commands, unlock requests, pairing) is blind during
    // the backoff, so a long cap silently degrades the whole channel to polling.
    private const int InitialRetryMs = 1_000;
    private const int MaxRetryMs = 15_000;
    private static readonly TimeSpan RestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StreamReadIdleTimeout = TimeSpan.FromMinutes(10);
    // How long an SSE connect may spend waiting for HTTP response headers. Without
    // this a half-open connection (server accepted TCP but never replied) would hang
    // Http.SendAsync forever, silently killing the stream and any control updates.
    private static readonly TimeSpan StreamConnectTimeout = TimeSpan.FromSeconds(45);

    // Shared across all client instances (rule: one static HttpClient). Timeout is
    // infinite because SSE responses stay open; REST requests get their own cts.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        EnableMultipleHttp2Connections = true,
        KeepAlivePingDelay = TimeSpan.FromSeconds(15),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly AgentConfig _config;
    private readonly ISecretStore _secrets;
    private readonly bool _isOwner;
    private readonly string _refreshTokenSecretKey;
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly object _tokenGate = new();

    private string? _uid;
    private string? _idToken;
    private string? _refreshToken;
    private DateTime _idTokenExpiresUtc = DateTime.MinValue;

    /// <summary>
    /// Constructs a Firebase client. The default instance authenticates anonymously
    /// as the device (tvUid). Pass <paramref name="isOwner"/> true (with a distinct
    /// <paramref name="refreshTokenSecretKey"/>) to create an owner-scoped instance
    /// that signs in with an email + password and acts as the parent for every device
    /// under that account.
    /// </summary>
    public RtdbFirebaseClient(AgentConfig config, ISecretStore secrets, bool isOwner = false, string? refreshTokenSecretKey = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _isOwner = isOwner;
        _refreshTokenSecretKey = refreshTokenSecretKey ?? RefreshTokenSecretKey;
        _refreshToken = secrets.Get(_refreshTokenSecretKey) ?? (isOwner ? null : config.RefreshToken);
    }

    /// <summary>True once an owner instance has successfully signed in.</summary>
    public bool IsOwnerSignedIn
    {
        get
        {
            lock (_tokenGate)
            {
                return _uid != null;
            }
        }
    }

    public string? Uid
    {
        get
        {
            lock (_tokenGate)
            {
                return _uid;
            }
        }
    }

    public async Task SignInAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(forceRefresh: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs in as the parent/owner using an email + password (the same credentials
    /// as the phone parent app). Only valid on an owner-scoped instance. On success
    /// the owner refresh token is persisted under <see cref="OwnerRefreshTokenSecretKey"/>.
    /// </summary>
    public async Task<(bool Ok, string? Error, string? Uid)> SignInWithEmailPasswordAsync(string email, string password, CancellationToken ct)
    {
        if (!_isOwner)
        {
            return (false, "This client is not configured for owner sign-in.", null);
        }

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
            ["returnSecureToken"] = "true",
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={Uri.EscapeDataString(_config.ApiKey)}")
        {
            Content = content,
        };

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (false, ExtractAuthError(body) ?? $"Sign-in failed ({(int)response.StatusCode}).", null);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var idToken = GetString(root, "idToken");
        var newRefreshToken = GetString(root, "refreshToken");
        var expiresIn = GetDouble(root, "expiresIn");
        var localId = GetString(root, "localId");
        if (idToken == null || newRefreshToken == null || localId == null)
        {
            return (false, "Sign-in response lacked tokens.", null);
        }

        StoreToken(localId, idToken, newRefreshToken, expiresIn);
        return (true, null, localId);
    }

    private static string? ExtractAuthError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
            // fall through to default message
        }

        return null;
    }

    public Task<string> GetAsync(string path, CancellationToken ct)
    {
        return SendForBodyAsync(HttpMethod.Get, path, json: null, ct);
    }

    public Task PutAsync(string path, string json, CancellationToken ct)
    {
        return SendForBodyAsync(HttpMethod.Put, path, json, ct);
    }

    public Task PatchAsync(string path, string json, CancellationToken ct)
    {
        return SendForBodyAsync(HttpMethod.Patch, path, json, ct);
    }

    public async Task<long> FetchServerTimeOffsetMsAsync(CancellationToken ct)
    {
        var json = await GetAsync(".info/serverTimeOffset", ct).ConfigureAwait(false);
        var trimmed = json.Trim();
        if (trimmed == "null" || trimmed.Length == 0)
        {
            return 0;
        }

        if (long.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out var exact))
        {
            return exact;
        }

        if (double.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out var fractional))
        {
            return (long)fractional;
        }

        return 0;
    }

    public Task<IDisposable> StreamAsync(string path, Action<string?> onData, Action<Exception> onError, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onData);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(
            () => RunStreamLoopAsync(path, onData, onError, cts.Token),
            CancellationToken.None);
        return Task.FromResult<IDisposable>(new StreamHandle(cts));
    }

    public void Dispose()
    {
        // The HttpClient is intentionally static/shared; individual streams are
        // disposed by their own handles/tokens. The auth gate is left untouched —
        // in-flight sign-ins may still be observing it.
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ auth

    private async Task EnsureTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && HasFreshToken())
        {
            return;
        }

        await _authGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && HasFreshToken())
            {
                return;
            }

            string? refreshToken;
            lock (_tokenGate)
            {
                refreshToken = _refreshToken ?? _secrets.Get(_refreshTokenSecretKey) ?? (_isOwner ? null : _config.RefreshToken);
            }

            if (refreshToken != null && await TryRefreshAsync(refreshToken, ct).ConfigureAwait(false))
            {
                return;
            }

            if (_isOwner)
            {
                // An owner instance must authenticate explicitly via
                // SignInWithEmailPasswordAsync; it must never mint an anonymous token.
                throw new InvalidOperationException("Owner client is not signed in. Call SignInWithEmailPasswordAsync first.");
            }

            await SignUpAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _authGate.Release();
        }
    }

    private bool HasFreshToken()
    {
        lock (_tokenGate)
        {
            return _idToken != null && _idTokenExpiresUtc - DateTime.UtcNow > TimeSpan.FromSeconds(TokenExpiryMarginSeconds);
        }
    }

    private async Task<bool> TryRefreshAsync(string refreshToken, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["returnSecureToken"] = "true",
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(_config.ApiKey)}")
        {
            Content = content,
        };

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false; // invalid/expired refresh token (or transient failure -> signUp will also fail)
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var idToken = GetString(root, "id_token");
        var newRefreshToken = GetString(root, "refresh_token");
        var expiresIn = GetDouble(root, "expires_in");
        var userId = GetString(root, "user_id");
        if (idToken == null || newRefreshToken == null)
        {
            return false;
        }

        StoreToken(userId, idToken, newRefreshToken, expiresIn);
        return true;
    }

    private async Task SignUpAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={Uri.EscapeDataString(_config.ApiKey)}")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Anonymous sign-up failed ({(int)response.StatusCode}): {Truncate(body, 300)}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var idToken = GetString(root, "idToken");
        var newRefreshToken = GetString(root, "refreshToken");
        var expiresIn = GetDouble(root, "expiresIn");
        var localId = GetString(root, "localId");
        if (idToken == null || newRefreshToken == null)
        {
            throw new HttpRequestException("Anonymous sign-up response lacked tokens.");
        }

        StoreToken(localId, idToken, newRefreshToken, expiresIn);
    }

    private void StoreToken(string? uid, string idToken, string refreshToken, double? expiresInSeconds)
    {
        lock (_tokenGate)
        {
            if (!string.IsNullOrEmpty(uid))
            {
                _uid = uid;
            }

            _idToken = idToken;
            _refreshToken = refreshToken;
            _idTokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresInSeconds ?? 3600);
        }

        _secrets.Set(_refreshTokenSecretKey, refreshToken);
    }

    private void InvalidateToken()
    {
        lock (_tokenGate)
        {
            _idToken = null;
            _idTokenExpiresUtc = DateTime.MinValue;
        }
    }

    // ------------------------------------------------------------------ rest

    private async Task<string> SendForBodyAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await EnsureTokenAsync(forceRefresh: false, ct).ConfigureAwait(false);

            string url;
            string token;
            lock (_tokenGate)
            {
                token = _idToken!;
            }

            url = BuildDatabaseUrl(path, token);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RestTimeout);
            using var request = new HttpRequestMessage(method, url);
            if (json != null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var response = await Http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return body;
            }

            if ((int)response.StatusCode == 401 && attempt == 0)
            {
                InvalidateToken(); // forces token refresh, then retry once
                continue;
            }

            throw new HttpRequestException(
                $"Firebase {method} {path} failed ({(int)response.StatusCode}): {Truncate(body, 300)}",
                null,
                response.StatusCode);
        }
    }

    private string BuildDatabaseUrl(string path, string idToken)
    {
        var builder = new StringBuilder(_config.DatabaseUrl.TrimEnd('/'));
        var anySegment = false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            builder.Append('/').Append(Uri.EscapeDataString(segment));
            anySegment = true;
        }

        // Empty path means a root multi-path update; keep the "/" separator so the
        // ".json" suffix lands on the path, not on the hostname.
        if (!anySegment)
        {
            builder.Append('/');
        }

        builder.Append(".json?auth=").Append(Uri.EscapeDataString(idToken));
        return builder.ToString();
    }

    // ------------------------------------------------------------------ sse

    private async Task RunStreamLoopAsync(string path, Action<string?> onData, Action<Exception> onError, CancellationToken ct)
    {
        var retryDelayMs = InitialRetryMs;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureTokenAsync(forceRefresh: false, ct).ConfigureAwait(false);
                string token;
                lock (_tokenGate)
                {
                    token = _idToken!;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, BuildDatabaseUrl(path, token));
                request.Headers.Accept.ParseAdd("text/event-stream");
                HttpResponseMessage response;
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(StreamConnectTimeout);
                    try
                    {
                        response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Half-open connection or server that never replied — treat as a
                        // transient error so the stream loop reconnects with backoff.
                        throw new IOException($"SSE stream {path} connect timed out after {StreamConnectTimeout.TotalSeconds}s.");
                    }
                }
                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"SSE stream {path} failed ({(int)response.StatusCode}).");
                    }

                    retryDelayMs = InitialRetryMs;
                    using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    await ReadSseFramesAsync(reader, path, onData, ct).ConfigureAwait(false);
                }

                // Server closed the stream (Firebase does this periodically); reconnect.
                onError(new IOException($"SSE stream {path} closed by server."));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                onError(ex);
            }

            try
            {
                await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            retryDelayMs = Math.Min(retryDelayMs * 2, MaxRetryMs);
        }
    }

    private async Task ReadSseFramesAsync(StreamReader reader, string path, Action<string?> onData, CancellationToken ct)
    {
        JsonNode? root = null;
        string? eventName = null;
        var dataLines = new List<string>();

        while (true)
        {
            string? line;
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readCts.CancelAfter(StreamReadIdleTimeout);
                try
                {
                    line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // No frame within the idle window: the connection is silently
                    // dead (Firebase sends keep-alives far more often than this).
                    throw new IOException($"SSE stream {path} idle for {StreamReadIdleTimeout.TotalMinutes} minutes.");
                }
            }

            if (line == null)
            {
                return; // stream ended; caller reconnects
            }

            if (line.Length == 0)
            {
                var name = eventName ?? "message";
                var buffered = dataLines;
                eventName = null;
                dataLines = new List<string>();
                if (buffered.Count == 0)
                {
                    continue;
                }

                var data = string.Join('\n', buffered);
                if (string.Equals(name, "auth-revoked", StringComparison.Ordinal))
                {
                    InvalidateToken(); // forces refresh (not reuse) on the next connect
                    throw new IOException($"SSE stream {path} auth-revoked: {Truncate(data, 100)}");
                }

                if (ApplyFrame(name, data, ref root, path))
                {
                    onData(root == null ? null : root.ToJsonString());
                }
            }
            else if (line.StartsWith(':'))
            {
                // comment / keep-alive
            }
            else if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var payload = line["data:".Length..];
                dataLines.Add(payload.StartsWith(' ') ? payload[1..] : payload);
            }

            // "id:"/"retry:" fields are not used by the RTDB event stream.
        }
    }

    /// <summary>Returns false for frames that should not produce a delivery (keep-alive).</summary>
    private static bool ApplyFrame(string eventName, string data, ref JsonNode? root, string path)
    {
        switch (eventName)
        {
            case "keep-alive":
                return false;
            case "cancel":
                throw new IOException($"SSE stream {path} cancelled by server (permission denied).");
            default:
                MergePayload(isPatch: eventName == "patch", data, ref root);
                return true;
        }
    }

    /// <summary>Merges a {"path":"...","data":...} put/patch payload into the streamed node (value-listener semantics).</summary>
    private static void MergePayload(bool isPatch, string data, ref JsonNode? root)
    {
        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(data) as JsonObject;
        }
        catch (JsonException)
        {
            return; // ignore malformed frames; reconnect replay restores truth
        }

        if (payload == null)
        {
            return;
        }

        var eventPath = payload["path"]?.GetValue<string>() ?? "/";
        var dataNode = payload["data"];
        if (eventPath == "/" || eventPath.Length == 0)
        {
            if (isPatch)
            {
                // A root patch carries only the changed keys; merge them into the
                // current value instead of replacing the node (RTDB patch semantics).
                if (dataNode is JsonObject patchObject)
                {
                    root ??= new JsonObject();
                    var target = (JsonObject)root;
                    foreach (var entry in patchObject)
                    {
                        if (entry.Key.Contains('/'))
                        {
                            // Multi-path update keys arrive as literal "a/b/c" strings
                            // (updateChildren with compound map keys). RTDB keys cannot
                            // contain '/', so these are always paths, never names.
                            SetAtPath(ref root, entry.Key, entry.Value?.DeepClone());
                        }
                        else
                        {
                            target[entry.Key] = entry.Value?.DeepClone();
                        }
                    }
                }

                return;
            }

            root = dataNode?.DeepClone();
            return;
        }

        SetAtPath(ref root, eventPath, dataNode?.DeepClone());
    }

    /// <summary>Sets (or deletes, when value is null) the node at a slash-separated path.</summary>
    private static void SetAtPath(ref JsonNode? root, string path, JsonNode? value)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            root = value;
            return;
        }

        if (value == null)
        {
            JsonNode? current = root;
            for (var i = 0; i < segments.Length - 1 && current != null; i++)
            {
                current = current[segments[i]];
            }

            (current as JsonObject)?.Remove(segments[^1]);
            return;
        }

        root ??= new JsonObject();
        JsonNode? node = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var next = node![segments[i]];
            if (next is not JsonObject)
            {
                next = new JsonObject();
                node[segments[i]] = next;
            }

            node = next;
        }

        node![segments[^1]] = value;
    }

    private static string? GetString(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static double? GetDouble(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed class StreamHandle(CancellationTokenSource cts) : IDisposable
    {
        private CancellationTokenSource? _cts = cts;

        public void Dispose()
        {
            Interlocked.Exchange(ref _cts, null)?.Cancel();
        }
    }
}
