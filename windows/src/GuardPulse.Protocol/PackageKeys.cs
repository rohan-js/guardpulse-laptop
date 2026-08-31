namespace GuardPulse.Protocol;

using System;
using System.Text;

/// <summary>
/// Base64url (RFC 4648 section 5, without padding) encoding of package names used as
/// Firebase keys. Ported from shared/src/main/java/com/guardpulse/parentcontrol/shared/PackageKeys.kt.
/// </summary>
public static class PackageKeys
{
    /// <summary>Encodes a package name to a Firebase-safe base64url key without padding.</summary>
    public static string Encode(string packageName)
    {
        return ToBase64Url(Encoding.UTF8.GetBytes(packageName));
    }

    /// <summary>Decodes a base64url key (padded or unpadded) back to the package name.</summary>
    public static string Decode(string key)
    {
        return Encoding.UTF8.GetString(FromBase64Url(key));
    }

    /// <summary>Encodes raw bytes as unpadded base64url (shared helper, also used by PinHasher).</summary>
    internal static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decodes an unpadded base64url string to raw bytes (shared helper, also used by PinHasher).</summary>
    internal static byte[] FromBase64Url(string key)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        // Tolerate already-padded input (java.util.Base64.getUrlDecoder() accepts it too),
        // then re-add exactly the padding Convert.FromBase64String expects.
        var unpadded = key.TrimEnd('=');

        var builder = new StringBuilder(unpadded.Length + 4);
        for (var i = 0; i < unpadded.Length; i++)
        {
            var c = unpadded[i];
            switch (c)
            {
                case '-':
                    builder.Append('+');
                    break;
                case '_':
                    builder.Append('/');
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        // Restore the '=' padding so Convert.FromBase64String accepts the input.
        var remainder = builder.Length % 4;
        if (remainder == 2)
        {
            builder.Append("==");
        }
        else if (remainder == 3)
        {
            builder.Append('=');
        }
        else if (remainder == 1)
        {
            throw new FormatException("Invalid base64url input length.");
        }

        return Convert.FromBase64String(builder.ToString());
    }
}
