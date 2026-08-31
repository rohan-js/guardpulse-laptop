namespace GuardPulse.Protocol;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>A created PIN hash: the base64url salt and the base64url derived hash.</summary>
public sealed record PinHash(string Salt, string Hash);

/// <summary>
/// PIN hashing helpers. Ported from shared/src/main/java/com/guardpulse/parentcontrol/shared/PinHasher.kt.
/// v2: PBKDF2-HMAC-SHA256, 600000 iterations, 16-byte random salt, 32-byte derived key.
/// v1 (legacy): SHA-256 of "salt:pin". All hashes are base64url without padding.
/// The stored hash carries its own iteration count, so existing v2 hashes (210k) keep
/// verifying; new hashes use the stronger 600k (rules allow 210000..1000000).
/// </summary>
public static class PinHasher
{
    public const int LEGACY_VERSION = 1;
    public const int CURRENT_VERSION = 2;
    public const string ALGORITHM = "PBKDF2WithHmacSHA256";
    public const int ITERATIONS = 600_000;

    /// <summary>Oldest accepted v2 iteration count. Verification and parsing keep this at the
    /// legacy 210k floor so hashes created by older builds/Android devices still verify; only
    /// NEW hashes use <see cref="ITERATIONS"/>.</summary>
    public const int MIN_V2_ITERATIONS = 210_000;

    private const int SaltLengthBytes = 16;
    private const int KeyLengthBytes = 32; // KEY_LENGTH_BITS / 8
    private const int MaxV2Iterations = 1_000_000;

    // [0-9] rather than \d: .NET's \d matches Unicode digits, Java's is ASCII-only. Java parity wanted.
    private static readonly Regex SixDigitPin = new Regex("^[0-9]{6}$", RegexOptions.Compiled);

    /// <summary>Creates a v2 PBKDF2 hash for a six-digit PIN with a fresh random 16-byte salt.</summary>
    public static PinHash Create(string pin)
    {
        RequireSixDigitPin(pin);
        var saltBytes = new byte[SaltLengthBytes];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(saltBytes);
        }

        var salt = PackageKeys.ToBase64Url(saltBytes);
        return new PinHash(salt, Pbkdf2(pin, salt, ITERATIONS));
    }

    /// <summary>
    /// Verifies a PIN against a stored salt/hash. Default version is the legacy v1 scheme
    /// (per the Windows contracts); callers verifying v2 hashes must pass CURRENT_VERSION.
    /// The comparison runs in constant time.
    /// </summary>
    public static bool Verify(string pin, string salt, string expectedHash, int version = 1, string? algorithm = null, int? iterations = null)
    {
        if (pin == null || salt == null || expectedHash == null)
        {
            return false;
        }

        if (!IsSixDigitPin(pin) || salt.Trim().Length == 0 || expectedHash.Trim().Length == 0)
        {
            return false;
        }

        string actual;
        if (version == LEGACY_VERSION)
        {
            actual = LegacyHash(pin, salt);
        }
        else if (version == CURRENT_VERSION)
        {
            if (algorithm != null && algorithm != ALGORITHM)
            {
                return false;
            }

            var rounds = iterations ?? ITERATIONS;
            if (rounds < MIN_V2_ITERATIONS || rounds > MaxV2Iterations)
            {
                return false;
            }

            actual = Pbkdf2(pin, salt, rounds);
        }
        else
        {
            return false;
        }

        return FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual),
            Encoding.UTF8.GetBytes(expectedHash));
    }

    private static void RequireSixDigitPin(string pin)
    {
        if (!IsSixDigitPin(pin))
        {
            throw new ArgumentException("PIN must be exactly six digits", nameof(pin));
        }
    }

    private static bool IsSixDigitPin(string pin)
    {
        return pin != null && pin.Length == 6 && SixDigitPin.IsMatch(pin);
    }

    private static string Pbkdf2(string pin, string salt, int iterations)
    {
        var saltBytes = PackageKeys.FromBase64Url(salt);
        using (var deriveBytes = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes(pin),
            saltBytes,
            iterations,
            HashAlgorithmName.SHA256))
        {
            var key = deriveBytes.GetBytes(KeyLengthBytes);
            return PackageKeys.ToBase64Url(key);
        }
    }

    private static string LegacyHash(string pin, string salt)
    {
        using (var sha256 = SHA256.Create())
        {
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(salt + ":" + pin));
            return PackageKeys.ToBase64Url(bytes);
        }
    }

    /// <summary>
    /// Constant-time byte comparison (netstandard2.0 has no CryptographicOperations.FixedTimeEquals).
    /// Always scans the longer of the two inputs so runtime does not leak length-derived early exits
    /// beyond the lengths themselves.
    /// </summary>
    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        var length = left.Length >= right.Length ? left.Length : right.Length;
        var diff = left.Length ^ right.Length;
        for (var i = 0; i < length; i++)
        {
            var l = i < left.Length ? left[i] : (byte)0;
            var r = i < right.Length ? right[i] : (byte)0;
            diff |= l ^ r;
        }

        return diff == 0;
    }
}
