using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace GuardPulse.Agent.Session;

/// <summary>
/// Best-effort background-tab URL enrichment: Chromium keeps its live session in a
/// SNSS file ("Current Tabs") under the profile's User Data directory. The file's
/// binary pickle layout varies between Chromium versions, so instead of a spec
/// parser we scan the copied bytes for ASCII http(s) URLs and nearby UTF-16 title
/// strings and only claim title→URL pairs that match exactly. Failure modes
/// (locked file, stale mtime, layout change) return an empty map — the live
/// titles/URL path never depends on this.
/// </summary>
internal static class BrowserSessionFileReader
{
    private const int MaxFileBytes = 12 * 1024 * 1024; // 12 MB is far beyond any real session
    private const int MaxAgeMs = 10 * 60_000;          // a stale session file says nothing about "now"
    private const int RefreshMs = 30_000;              // at most one copy+parse per browser per 30s
    private const int MaxUrls = 200;

    private static readonly ConcurrentDictionary<string, (DateTime At, Dictionary<string, string> Map)> Cache = new();

    /// <summary>title → url pairs for tabs currently recorded by this browser, or an empty map.</summary>
    public static IReadOnlyDictionary<string, string> GetTitleToUrlMap(string browserExePath)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (Cache.TryGetValue(browserExePath, out var cached) && (now - cached.At).TotalMilliseconds < RefreshMs)
            {
                return cached.Map;
            }

            var map = ParseLatestSessionFile(browserExePath);
            Cache[browserExePath] = (now, map);
            return map;
        }
        catch (Exception)
        {
            return new Dictionary<string, string>();
        }
    }

    private static Dictionary<string, string> ParseLatestSessionFile(string browserExePath)
    {
        var exe = Path.GetFileName(browserExePath).ToLowerInvariant();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = exe switch
        {
            "chrome.exe" => new[] { Path.Combine(localAppData, "Google", "Chrome", "User Data") },
            "msedge.exe" => new[] { Path.Combine(localAppData, "Microsoft", "Edge", "User Data") },
            "brave.exe" => new[] { Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data") },
            "vivaldi.exe" => new[] { Path.Combine(localAppData, "Vivaldi", "User Data") },
            "opera.exe" => new[]
            {
                Path.Combine(localAppData, "Opera Software"),
            },
            _ => Array.Empty<string>(),
        };

        if (candidates.Length == 0)
        {
            return new Dictionary<string, string>();
        }

        string? newest = null;
        var newestMtime = DateTime.MinValue;
        foreach (var root in candidates)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "Current Tabs", SearchOption.AllDirectories))
                {
                    try
                    {
                        var mtime = File.GetLastWriteTimeUtc(file);
                        if (mtime > newestMtime)
                        {
                            newestMtime = mtime;
                            newest = file;
                        }
                    }
                    catch (IOException)
                    {
                        // unreadable profile dir; try the next one
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // profile dir vanished mid-enumeration; try the next root
            }
        }

        if (newest is null || (DateTime.UtcNow - newestMtime).TotalMilliseconds > MaxAgeMs)
        {
            return new Dictionary<string, string>();
        }

        var temp = Path.Combine(Path.GetTempPath(), "gp-snss-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.Copy(newest, temp, overwrite: true);
            using var stream = File.OpenRead(temp);
            if (stream.Length == 0 || stream.Length > MaxFileBytes)
            {
                return new Dictionary<string, string>();
            }

            var bytes = new byte[stream.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            return ExtractTitleUrlPairs(bytes, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // locked or vanished mid-copy: skip this cycle
            return new Dictionary<string, string>();
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // temp cleanup is best-effort
            }
        }
    }

    /// <summary>Scans SNSS bytes for (URL, nearby UTF-16 title) pairs; exact-title map out.</summary>
    internal static Dictionary<string, string> ExtractTitleUrlPairs(byte[] bytes, int length)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var titles = CollectUtf16Titles(bytes, length);

        for (var i = 0; i < length - 9 && map.Count < MaxUrls; i++)
        {
            if (!MatchesScheme(bytes, i, "https://") && !MatchesScheme(bytes, i, "http://"))
            {
                continue;
            }

            var end = i;
            while (end < length && IsUrlByte(bytes[end]))
            {
                end++;
            }

            if (end - i < 5)
            {
                continue;
            }

            var url = Encoding.ASCII.GetString(bytes, i, end - i);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            // Chromium stores the page title just before or after the URL inside the
            // same pickle; take the closest title within 1 KB.
            string? best = null;
            var bestDistance = int.MaxValue;
            foreach (var (pos, title) in titles)
            {
                var distance = pos < i ? i - pos : pos - end;
                if (distance < bestDistance && distance <= 1024)
                {
                    bestDistance = distance;
                    best = title;
                }
            }

            if (best != null && !map.ContainsKey(best))
            {
                map[best] = url;
            }

            i = end - 1;
        }

        return map;
    }

    private static bool MatchesScheme(byte[] bytes, int i, string scheme)
    {
        for (var k = 0; k < scheme.Length; k++)
        {
            var c = bytes[i + k];
            if (c != scheme[k] && c != scheme[k] - 32) // case-insensitive ASCII
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUrlByte(byte b) =>
        b is >= 0x21 and <= 0x7E and not ((byte)'"') and not ((byte)'\'') and not ((byte)'<') and not ((byte)'>');

    /// <summary>UTF-16LE strings that look like page titles: a plausible pickle length
    /// prefix followed by printable UTF-16 characters.</summary>
    private static List<(int Pos, string Title)> CollectUtf16Titles(byte[] bytes, int length)
    {
        var titles = new List<(int, string)>();
        for (var i = 4; i + 1 < length && titles.Count < MaxUrls * 2; i += 2)
        {
            var charCount = bytes[i - 4] | (bytes[i - 3] << 8) | (bytes[i - 2] << 16) | (bytes[i - 1] << 24);
            if (charCount < 3 || charCount > 512)
            {
                continue;
            }

            var byteLen = charCount * 2;
            if (byteLen <= 0 || i + byteLen > length)
            {
                continue;
            }

            var ok = true;
            for (var k = 0; k < byteLen; k += 2)
            {
                var lo = bytes[i + k];
                var hi = bytes[i + k + 1];
                if (hi != 0)
                {
                    ok = false; // non-BMP/UTF-16BE — not a Chromium title string
                    break;
                }

                var c = (char)lo;
                if (!(char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsSeparator(c) || char.IsSymbol(c) || c == ' '))
                {
                    ok = false;
                    break;
                }
            }

            if (!ok)
            {
                continue;
            }

            var title = Encoding.Unicode.GetString(bytes, i, byteLen).Trim();
            if (title.Length >= 3 && !BrowserWatcher.LooksLikeUrl(title))
            {
                titles.Add((i, title));
            }

            i += byteLen - 2;
        }

        return titles;
    }
}
