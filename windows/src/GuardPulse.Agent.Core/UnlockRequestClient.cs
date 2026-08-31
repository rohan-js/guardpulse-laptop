namespace GuardPulse.Agent.Core;

using System.Text.Json.Nodes;
using GuardPulse.Protocol;

/// <summary>
/// Ask-a-parent unlock flow: pushes a pending request under
/// devices/{id}/unlockRequests/{requestId} and streams the node for the parent's
/// decision. Fires UnlockApproved/UnlockDenied once per request (replays and
/// already-applied requests are skipped) and marks tvApplyStatus when the host
/// consumed the approval.
/// </summary>
public sealed class UnlockRequestClient
{
    private readonly IFirebaseClient _firebase;
    private readonly string _deviceId;
    private readonly Func<long> _serverNowMs;
    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private IDisposable? _listener;

    public UnlockRequestClient(IFirebaseClient firebase, string deviceId, Func<long> serverNowMs)
    {
        _firebase = firebase ?? throw new ArgumentNullException(nameof(firebase));
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device id must be provided.", nameof(deviceId));
        }

        _deviceId = deviceId;
        _serverNowMs = serverNowMs ?? throw new ArgumentNullException(nameof(serverNowMs));
    }

    /// <summary>(appKey, approvalType "oneVisit"|"timed", approvalDurationMs; null for oneVisit).</summary>
    public event Action<string, string, long?>? UnlockApproved;

    /// <summary>(appKey).</summary>
    public event Action<string>? UnlockDenied;

    /// <summary>
    /// Pushes {requestId, packageName, reason, status:"pending", createdAt (server),
    /// expiresAt = server now + 10 minutes} and returns the requestId.
    /// </summary>
    public async Task<string> CreatePendingAsync(string appKey, string? reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appKey))
        {
            throw new ArgumentException("App key must be provided.", nameof(appKey));
        }

        var requestId = Guid.NewGuid().ToString("D");
        var payload = new JsonObject
        {
            ["requestId"] = requestId,
            ["packageName"] = appKey,
            ["reason"] = reason,
            ["status"] = PolicyConstants.UNLOCK_PENDING,
            ["createdAt"] = ServerTimestamp(),
            ["expiresAt"] = _serverNowMs() + PolicyConstants.TEMP_UNLOCK_MS,
        };

        await _firebase.PutAsync(
            FirebasePaths.DeviceUnlockRequest(_deviceId, requestId),
            payload.ToJsonString(),
            ct).ConfigureAwait(false);
        return requestId;
    }

    /// <summary>Streams devices/{id}/unlockRequests for parent decisions; dispose to stop.</summary>
    public Task<IDisposable> ListenAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_listener != null)
            {
                throw new InvalidOperationException("Unlock request listener is already active.");
            }

            _listener = _firebase.StreamAsync(
                FirebasePaths.DeviceUnlockRequests(_deviceId),
                HandleNode,
                error => { /* the client reconnects internally */ },
                ct);
            return Task.FromResult(_listener);
        }
    }

    /// <summary>Records that the approval was consumed locally (tvApplyStatus/tvAppliedAt).</summary>
    public Task MarkAppliedAsync(string requestId, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["tvApplyStatus"] = "applied",
            ["tvAppliedAt"] = ServerTimestamp(),
        };
        return _firebase.PatchAsync(
            FirebasePaths.DeviceUnlockRequest(_deviceId, requestId),
            payload.ToJsonString(),
            ct);
    }

    private void HandleNode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "null")
        {
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(raw);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (root is not JsonObject map)
        {
            return;
        }

        var looksSingle = map.ContainsKey("requestId") || map.ContainsKey("packageName") || map.ContainsKey("status");
        if (looksSingle)
        {
            HandleRequest(ReadString(map, "requestId") ?? "", map);
            return;
        }

        foreach (var property in map)
        {
            if (property.Value is JsonObject request)
            {
                HandleRequest(property.Key, request);
            }
        }
    }

    private void HandleRequest(string requestId, JsonObject request)
    {
        if (requestId.Length == 0)
        {
            return;
        }

        var status = ReadString(request, "status");
        if (status == null)
        {
            return;
        }

        var packageName = ReadString(request, "packageName");
        if (packageName == null)
        {
            return;
        }

        lock (_gate)
        {
            if (_seen.Contains(requestId))
            {
                return;
            }
        }

        switch (status)
        {
            case PolicyConstants.UNLOCK_APPROVED:
                if (ReadString(request, "tvApplyStatus") == "applied")
                {
                    return; // consumed by a previous run/session
                }

                lock (_gate)
                {
                    _seen.Add(requestId);
                }

                var approvalType = ReadString(request, "approvalType") ?? PolicyConstants.UNLOCK_APPROVAL_ONE_VISIT;
                long? durationMs = ReadLong(request, "approvalDurationMs");
                UnlockApproved?.Invoke(packageName, approvalType, durationMs);
                break;

            case PolicyConstants.UNLOCK_DENIED:
                lock (_gate)
                {
                    _seen.Add(requestId);
                }

                UnlockDenied?.Invoke(packageName);
                break;

            case PolicyConstants.UNLOCK_PENDING:
                var expiresAt = ReadLong(request, "expiresAt") ?? 0L;
                if (expiresAt > 0 && _serverNowMs() > expiresAt)
                {
                    lock (_gate)
                    {
                        _seen.Add(requestId);
                    }

                    _ = ExpireAsync(requestId);
                }

                break;
        }
    }

    private async Task ExpireAsync(string requestId)
    {
        try
        {
            var payload = new JsonObject { ["status"] = PolicyConstants.UNLOCK_EXPIRED };
            await _firebase.PatchAsync(
                FirebasePaths.DeviceUnlockRequest(_deviceId, requestId),
                payload.ToJsonString(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // expiry bookkeeping is best-effort
        }
    }

    private static string? ReadString(JsonObject node, string name)
    {
        var value = node[name];
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }

    private static long? ReadLong(JsonObject node, string name)
    {
        var value = node[name];
        switch (value)
        {
            case JsonValue number when number.TryGetValue<long>(out var longValue):
                return longValue;
            case JsonValue numberDouble when numberDouble.TryGetValue<double>(out var doubleValue):
                return (long)doubleValue;
            case JsonValue text when text.TryGetValue<string>(out var raw)
                && long.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            default:
                return null;
        }
    }

    private static JsonObject ServerTimestamp()
    {
        return new JsonObject { [".sv"] = "timestamp" };
    }
}
