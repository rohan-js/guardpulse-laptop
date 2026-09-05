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
    // One-deep grace: when credentials rotate, the outgoing generation stays valid
    // for another full TTL. The setup window re-renders every 2s, so a QR scanned
    // one minute before a rotation boundary would otherwise validate FALSE one
    // minute after it — a permanent-looking "pairing rejected" loop from the user's
    // side. Only ONE old generation is kept (two at most are ever live).
    private const string PrevSecretKey = "pairing.prevSecret";
    private const string PrevCodeKey = "pairing.prevCode";
    private const string PrevCreatedAtKey = "pairing.prevCreatedAt";

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
    /// True when the presented secret OR manual code matches the current OR the
    /// immediately-previous generation AND the pair request was created within the
    /// pairing TTL (10 minutes). Mirrors TV isValid, plus the rotation-boundary
    /// grace described on the prev* keys.
    /// </summary>
    public bool Validate(string? secret, string? code, long createdAtMs, long nowMs)
    {
        if (createdAtMs <= 0 || nowMs - createdAtMs > PolicyConstants.PAIRING_TTL_MS)
        {
            return false;
        }

        var current = Current; // rotates stale credentials, so an old secret stops matching
        var currentMatch =
            (!string.IsNullOrWhiteSpace(secret) && secret == current.Secret)
            || (!string.IsNullOrWhiteSpace(code) && code == current.ManualCode);
        if (currentMatch)
        {
            return true;
        }

        // Rotation-boundary grace: the request may carry the generation that was
        // current when the user scanned the QR a minute before a rotation.
        string? prevSecret;
        string? prevCode;
        long? prevCreatedAt;
        lock (_gate)
        {
            prevSecret = _secrets.Get(PrevSecretKey);
            prevCode = _secrets.Get(PrevCodeKey);
            prevCreatedAt = ParseLong(_secrets.Get(PrevCreatedAtKey));
        }

        if (prevCreatedAt is null || nowMs - prevCreatedAt.Value > PolicyConstants.PAIRING_TTL_MS)
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(secret) && secret == prevSecret)
            || (!string.IsNullOrWhiteSpace(code) && code == prevCode);
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
        // Preserve the outgoing generation as the one-deep grace BEFORE overwriting.
        var oldSecret = _secrets.Get(SecretKey);
        var oldCode = _secrets.Get(CodeKey);
        var oldCreatedAt = _secrets.Get(CreatedAtKey);
        if (!string.IsNullOrWhiteSpace(oldSecret) && !string.IsNullOrWhiteSpace(oldCode))
        {
            _secrets.Set(PrevSecretKey, oldSecret);
            _secrets.Set(PrevCodeKey, oldCode);
            _secrets.Set(PrevCreatedAtKey, oldCreatedAt ?? "0");
        }

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
