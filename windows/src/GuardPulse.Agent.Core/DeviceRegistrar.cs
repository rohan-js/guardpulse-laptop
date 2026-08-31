namespace GuardPulse.Agent.Core;

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuardPulse.Protocol;

/// <summary>
/// Writes devices/{id}/meta on startup. The PATCH is a partial update so an
/// existing ownerUid (set during pairing) always survives; this class never
/// writes ownerUid itself. Registration is skipped when the meta node already
/// belongs to a different tvUid (a foreign device must not be hijacked).
/// </summary>
public sealed class DeviceRegistrar
{
    private readonly IFirebaseClient _firebase;
    private readonly string _deviceId;
    private readonly string? _appVersion;

    public DeviceRegistrar(IFirebaseClient firebase, string deviceId, string? appVersion = null)
    {
        _firebase = firebase ?? throw new ArgumentNullException(nameof(firebase));
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device id must be provided.", nameof(deviceId));
        }

        _deviceId = deviceId;
        _appVersion = appVersion ?? ResolveAppVersion();
    }

    /// <summary>Owner parent uid discovered on the meta node (kept for unpair); null while unowned.</summary>
    public string? OwnerUid { get; private set; }

    public async Task RegisterAsync(CancellationToken ct)
    {
        var uid = _firebase.Uid ?? throw new InvalidOperationException("RegisterAsync requires a signed-in firebase client.");

        string? metaJson;
        try
        {
            metaJson = await _firebase.GetAsync(FirebasePaths.DeviceMeta(_deviceId), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsPermissionOrMissing(ex))
        {
            // Before first registration the rules deny reading the node (tvUid not
            // written yet); that is the fresh-device case, not an error.
            metaJson = null;
        }

        // A transient network/HTTP failure is NOT the fresh-device case: rethrow so
        // the caller retries instead of overwriting a live meta node with a stale read.
        var existingTvUid = ReadMetaString(metaJson, "tvUid");
        OwnerUid = ReadMetaString(metaJson, "ownerUid");

        if (existingTvUid != null && existingTvUid != uid)
        {
            // Device id collides with another tv's registration; never claim it.
            return;
        }

        var payload = new JsonObject
        {
            ["deviceId"] = _deviceId,
            ["tvUid"] = uid,
            ["platform"] = PolicyConstants.PLATFORM_WINDOWS,
            ["label"] = Environment.MachineName + " (laptop)",
            ["manufacturer"] = "Microsoft",
            ["model"] = Environment.OSVersion.VersionString,
            ["appVersion"] = _appVersion,
            ["lastRegisteredAt"] = ServerTimestamp(),
        };

        await _firebase.PatchAsync(FirebasePaths.DeviceMeta(_deviceId), payload.ToJsonString(), ct).ConfigureAwait(false);
    }

    private static string? ReadMetaString(string metaJson, string field)
    {
        if (string.IsNullOrWhiteSpace(metaJson) || metaJson.Trim() == "null")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metaJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
        }
        catch (JsonException)
        {
            // unreadable meta: treat as unregistered (PATCH will not touch ownerUid)
        }

        return null;
    }

    private static JsonObject ServerTimestamp()
    {
        return new JsonObject { [".sv"] = "timestamp" };
    }

    /// <summary>401/403 (rules deny read before registration) and 404 (node missing) are the
    /// fresh-device cases; anything else (timeout, 5xx, network) is not — a transient
    /// failure should not be treated as "no meta exists" (avoids spurious unpaired state).
    /// Firebase RTDB returns 401 (not 403) for this rules denial, so 401 must be included:
    /// without it a fresh device can never register and every later call 401s forever.</summary>
    private static bool IsPermissionOrMissing(HttpRequestException ex)
    {
        return ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
    }

    private static string? ResolveAppVersion()
    {
        return typeof(DeviceRegistrar).Assembly.GetName().Version?.ToString(3);
    }
}
