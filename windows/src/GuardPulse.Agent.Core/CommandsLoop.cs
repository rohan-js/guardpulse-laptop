namespace GuardPulse.Agent.Core;

using System.Text.Json;
using System.Text.Json.Nodes;
using GuardPulse.Protocol;

/// <summary>
/// Consumes the raw JSON of the devices/{id}/commands node (SSE value events: a map
/// of {commandId: command}, or a single command object) and drives each command
/// through the TV lifecycle: expire stale, claim via GET-then-PATCH status="running"
/// (races tolerated), dispatch to the host, then mark done/failed with completedAt.
/// Processed command ids are persisted (bounded) to prevent replay after restart.
/// </summary>
public sealed class CommandsLoop
{
    private const string ProcessedSecretKey = "commands.processed";
    private const int MaxProcessedIds = 100;

    private static readonly HashSet<string> CommandFieldNames = new(StringComparer.Ordinal)
    {
        "commandId", "type", "status", "packageName", "packageKey", "createdAt",
        "ttlMs", "startedAt", "completedAt", "sessionId", "error", "requestedBy",
    };

    private readonly IFirebaseClient _firebase;
    private readonly ISecretStore _secrets;
    private readonly string _deviceId;
    private readonly Func<long> _serverNowMs;
    private readonly Func<string>? _sessionId;
    private readonly object _gate = new();
    private readonly List<string> _processedOrder = new();
    private HashSet<string> _processedIds = new(StringComparer.Ordinal);

    public CommandsLoop(
        IFirebaseClient firebase,
        ISecretStore secrets,
        string deviceId,
        Func<long> serverNowMs,
        Func<string>? sessionId = null)
    {
        _firebase = firebase ?? throw new ArgumentNullException(nameof(firebase));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device id must be provided.", nameof(deviceId));
        }

        _deviceId = deviceId;
        _serverNowMs = serverNowMs ?? throw new ArgumentNullException(nameof(serverNowMs));
        _sessionId = sessionId;
        LoadProcessed();
    }

    /// <summary>(commandId, packageName); fired after the command is claimed as running.</summary>
    public event Action<string, string?>? RescanApps;

    /// <summary>(commandId, packageName).</summary>
    public event Action<string, string?>? ResetToday;

    /// <summary>(commandId, packageName).</summary>
    public event Action<string, string?>? Unpair;

    /// <summary>(commandId, packageName).</summary>
    public event Action<string, string?>? OpenSetup;

    /// <summary>
    /// Handles one raw JSON delivery of the commands node. Unknown command types are
    /// marked failed; already-processed or terminal commands are skipped (replays are
    /// marked done, mirroring the TV).
    /// </summary>
    public async Task HandleAsync(string rawJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || rawJson.Trim() == "null")
        {
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawJson);
        }
        catch (JsonException)
        {
            return;
        }

        if (root is not JsonObject map)
        {
            return;
        }

        // A merged value event delivers {commandId: {...}} maps; a lone fragment with
        // well-known command fields is a single command (its id may be unavailable).
        var looksSingle = map.Any(property => CommandFieldNames.Contains(property.Key));
        if (looksSingle)
        {
            await ProcessAsync(ReadString(map, "commandId"), map, ct).ConfigureAwait(false);
            return;
        }

        foreach (var property in map)
        {
            if (property.Value is JsonObject command)
            {
                await ProcessAsync(property.Key, command, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Fetches the commands node once and runs it through HandleAsync.</summary>
    public async Task PollAsync(CancellationToken ct)
    {
        var json = await _firebase.GetAsync(FirebasePaths.DeviceCommands(_deviceId), ct).ConfigureAwait(false);
        await HandleAsync(json, ct).ConfigureAwait(false);
    }

    private async Task ProcessAsync(string? commandId, JsonObject command, CancellationToken ct)
    {
        var type = ReadString(command, "type");
        if (type == null)
        {
            return;
        }

        var status = ReadString(command, "status");
        if (status != null && status != PolicyConstants.COMMAND_PENDING)
        {
            return; // another instance (or an earlier run) already owns this command
        }

        var id = commandId ?? "";
        if (id.Length > 0 && IsProcessed(id))
        {
            await PatchAsync(id, DonePayload(), ct).ConfigureAwait(false);
            return;
        }

        var createdAt = ReadLong(command, "createdAt") ?? 0L;
        var ttlMs = ReadLong(command, "ttlMs") ?? PolicyConstants.CommandTtlMs(type);
        var now = _serverNowMs();
        if (createdAt <= 0L || now > createdAt + ttlMs)
        {
            if (id.Length > 0)
            {
                await PatchAsync(id, StatusPayload(PolicyConstants.COMMAND_EXPIRED), ct).ConfigureAwait(false);
            }

            return;
        }

        var packageName = ReadString(command, "packageName");

        // Claim: re-read then flip to running; if another writer won the race, its
        // status is no longer pending/absent and we stand down.
        if (id.Length > 0)
        {
            var currentJson = await _firebase.GetAsync(FirebasePaths.DeviceCommands(_deviceId) + "/" + id, ct).ConfigureAwait(false);
            if (ReadStatus(currentJson) is { } currentStatus && currentStatus != PolicyConstants.COMMAND_PENDING)
            {
                return;
            }

            var claim = new JsonObject
            {
                ["status"] = PolicyConstants.COMMAND_RUNNING,
                ["startedAt"] = now,
            };
            var sessionId = _sessionId?.Invoke();
            if (!string.IsNullOrEmpty(sessionId))
            {
                claim["sessionId"] = sessionId;
            }

            await _firebase.PatchAsync(FirebasePaths.DeviceCommands(_deviceId) + "/" + id, claim.ToJsonString(), ct).ConfigureAwait(false);
        }

        switch (type)
        {
            case PolicyConstants.COMMAND_RESCAN_APPS:
                RescanApps?.Invoke(id, packageName);
                break;
            case PolicyConstants.COMMAND_RESET_TODAY:
                ResetToday?.Invoke(id, packageName);
                break;
            case PolicyConstants.COMMAND_UNPAIR:
                Unpair?.Invoke(id, packageName);
                break;
            case PolicyConstants.COMMAND_OPEN_SETUP:
                OpenSetup?.Invoke(id, packageName);
                break;
            default:
                if (id.Length > 0)
                {
                    var failed = StatusPayload(PolicyConstants.COMMAND_FAILED);
                    failed["error"] = "Unknown command type: " + type;
                    await PatchAsync(id, failed, ct).ConfigureAwait(false);
                }

                return;
        }

        if (id.Length == 0)
        {
            return; // fragment without an id: dispatched, nothing to finalize
        }

        await PatchAsync(id, DonePayload(), ct).ConfigureAwait(false);
        MarkProcessed(id);
    }

    private async Task PatchAsync(string commandId, JsonObject payload, CancellationToken ct)
    {
        await _firebase.PatchAsync(
            FirebasePaths.DeviceCommands(_deviceId) + "/" + commandId,
            payload.ToJsonString(),
            ct).ConfigureAwait(false);
    }

    private static JsonObject StatusPayload(string status)
    {
        return new JsonObject
        {
            ["status"] = status,
            ["completedAt"] = new JsonObject { [".sv"] = "timestamp" },
        };
    }

    private static JsonObject DonePayload()
    {
        return StatusPayload(PolicyConstants.COMMAND_DONE);
    }

    private static string? ReadStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
            // treat malformed nodes as unclaimed
        }

        return null;
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

    // -------------------------------------------------------------- processed ids

    private bool IsProcessed(string commandId)
    {
        lock (_gate)
        {
            return _processedIds.Contains(commandId);
        }
    }

    private void MarkProcessed(string commandId)
    {
        lock (_gate)
        {
            if (_processedIds.Add(commandId))
            {
                _processedOrder.Add(commandId);
            }

            while (_processedOrder.Count > MaxProcessedIds)
            {
                var oldest = _processedOrder[0];
                _processedOrder.RemoveAt(0);
                _processedIds.Remove(oldest);
            }

            try
            {
                _secrets.Set(ProcessedSecretKey, JsonSerializer.Serialize(_processedOrder));
            }
            catch
            {
                // replay protection is best-effort across restarts
            }
        }
    }

    private void LoadProcessed()
    {
        try
        {
            var stored = _secrets.Get(ProcessedSecretKey);
            if (string.IsNullOrWhiteSpace(stored))
            {
                return;
            }

            var ids = JsonSerializer.Deserialize<List<string>>(stored);
            if (ids == null)
            {
                return;
            }

            _processedOrder.AddRange(ids);
            _processedIds = new HashSet<string>(ids, StringComparer.Ordinal);
        }
        catch
        {
            // corrupted memory: start empty (commands are idempotent on the host side)
        }
    }
}
