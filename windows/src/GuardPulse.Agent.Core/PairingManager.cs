namespace GuardPulse.Agent.Core;

using System.Security.Cryptography;
using GuardPulse.Protocol;

/// <summary>Current pairing credentials (deviceId is stable; secret/code rotate).</summary>
public sealed record PairingCredentials(string DeviceId, string Secret, string ManualCode, long CreatedAtMs);

/// <summary>
/// Device pairing credentials persisted via ISecretStore (pairing.deviceId /
/// pairing.secret / pairing.code / pairing.createdAt). Ported from the TV
/// PairingManager: the 32-byte base64url secret and the 6-digit manual code are
/// reused only while younger than the 10-minute pairing TTL, then rotated.
/// </summary>
public sealed class PairingManager
{
    private const string DeviceIdKey = "pairing.deviceId";
    private const string SecretKey = "pairing.secret";
    private const string CodeKey = "pairing.code";
    private const string CreatedAtKey = "pairing.createdAt";

    private readonly ISecretStore _secrets;
    private readonly object _gate = new();

    public PairingManager(ISecretStore secrets)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public PairingCredentials Current
    {
        get
        {
            lock (_gate)
            {
                var deviceId = _secrets.Get(DeviceIdKey);
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    deviceId = Guid.NewGuid().ToString("N");
                    _secrets.Set(DeviceIdKey, deviceId);
                }

                var secret = _secrets.Get(SecretKey);
                var code = _secrets.Get(CodeKey);
                var createdAt = ParseLong(_secrets.Get(CreatedAtKey));
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (string.IsNullOrWhiteSpace(secret)
                    || string.IsNullOrWhiteSpace(code)
                    || createdAt == null
                    || now - createdAt.Value >= PolicyConstants.PAIRING_TTL_MS)
                {
                    var rotated = RotateLocked();
                    return new PairingCredentials(deviceId, rotated.Secret, rotated.Code, rotated.CreatedAt);
                }

                return new PairingCredentials(deviceId, secret, code, createdAt.Value);
            }
        }
    }

    /// <summary>Returns the current credentials, creating (and persisting) them on first use.</summary>
    public (string DeviceId, string Secret, string ManualCode) GetOrCreate()
    {
        var current = Current;
        return (current.DeviceId, current.Secret, current.ManualCode);
    }

    /// <summary>
    /// True when the presented secret OR manual code matches the current credentials AND the
    /// pair request was created within the pairing TTL (10 minutes). Mirrors TV isValid.
    /// </summary>
    public bool Validate(string? secret, string? code, long createdAtMs, long nowMs)
    {
        if (createdAtMs <= 0 || nowMs - createdAtMs > PolicyConstants.PAIRING_TTL_MS)
        {
            return false;
        }

        var current = Current; // rotates stale credentials, so an old secret stops matching
        return (!string.IsNullOrWhiteSpace(secret) && secret == current.Secret)
            || (!string.IsNullOrWhiteSpace(code) && code == current.ManualCode);
    }

    /// <summary>Generates and persists a fresh secret + manual code (deviceId unchanged).</summary>
    public void Rotate()
    {
        lock (_gate)
        {
            RotateLocked();
        }
    }

    private (string Secret, string Code, long CreatedAt) RotateLocked()
    {
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = ToBase64Url(secretBytes);
        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _secrets.Set(SecretKey, secret);
        _secrets.Set(CodeKey, code);
        _secrets.Set(CreatedAtKey, createdAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (secret, code, createdAt);
    }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static long? ParseLong(string? value)
    {
        return long.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
