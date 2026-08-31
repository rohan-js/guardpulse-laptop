namespace GuardPulse.Agent.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

/// <summary>
/// Builds and rewrites the GuardPulse block inside the Windows hosts file. Pure string
/// logic so the marked-block replacement is unit-testable; the service applies the
/// result to C:\Windows\System32\drivers\etc\hosts (writable by SYSTEM only).
/// </summary>
public static class HostsFileRewriter
{
    public const string BeginMarker = "# BEGIN GUARDPULSE CONTENT FILTER";
    public const string EndMarker = "# END GUARDPULSE CONTENT FILTER";

    /// <summary>One domain per line; comments and blanks ignored. Missing files contribute nothing.</summary>
    public static IReadOnlyList<string> LoadDomains(string blocklistDirectory, string category)
    {
        var path = Path.Combine(blocklistDirectory, category + ".txt");
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            return File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The full marked block (markers included) redirecting every domain to 0.0.0.0.</summary>
    public static string BuildBlock(IReadOnlyDictionary<string, IEnumerable<string>> categoriesToDomains)
    {
        var builder = new StringBuilder();
        builder.AppendLine(BeginMarker);
        foreach (var (category, domains) in categoriesToDomains)
        {
            var materialized = domains.ToList();
            if (materialized.Count == 0)
            {
                continue;
            }

            builder.AppendLine("# " + category);
            foreach (var domain in materialized)
            {
                builder.AppendLine("0.0.0.0 " + domain);
            }
        }

        builder.Append(EndMarker);
        return builder.ToString();
    }

    /// <summary>
    /// Replaces (or inserts/removes) the marked block in a hosts file's content. Idempotent:
    /// everything outside the markers is preserved byte-for-byte. Marker handling is
    /// corruption-robust: a stray BEGIN truncates to EOF, and a stray END also removes any
    /// orphaned block lines that precede it, so a half-written block can always be cleaned up.
    /// </summary>
    public static string ApplyBlock(string hostsContent, string? block)
    {
        var content = hostsContent.Replace("\r\n", "\n");
        var begin = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = content.IndexOf(EndMarker, StringComparison.Ordinal);
        if (begin >= 0 && end >= begin)
        {
            var after = content[(end + EndMarker.Length)..].TrimStart('\n');
            content = content[..begin].TrimEnd('\n') + (after.Length > 0 ? "\n" + after : "");
        }
        else if (end >= 0)
        {
            // Stray END without a BEGIN: remove the END marker plus any orphaned
            // 0.0.0.0 block lines (and blank separators) that precede it — the
            // original content sits above and is preserved.
            var start = end;
            while (start > 0)
            {
                // Skip line terminators immediately before the current boundary.
                var pos = start;
                while (pos > 0 && (content[pos - 1] == '\n' || content[pos - 1] == '\r'))
                {
                    pos--;
                }

                if (pos == 0)
                {
                    start = 0;
                    break;
                }

                var lineStart = content.LastIndexOf('\n', pos - 1);
                var line = content[(lineStart + 1)..pos].Trim();
                if (line.Length == 0)
                {
                    // A blank separator that is part of the orphaned block.
                    start = lineStart + 1;
                    continue;
                }

                if (!line.StartsWith("0.0.0.0 ", StringComparison.Ordinal)
                    && !line.StartsWith("# ", StringComparison.Ordinal))
                {
                    break; // reached original content
                }

                start = lineStart + 1;
                if (lineStart < 0)
                {
                    start = 0;
                    break;
                }
            }

            content = content[..start].TrimEnd('\n');
        }
        else if (begin >= 0)
        {
            // Corrupted half-marker: drop everything from the stray BEGIN to the end.
            content = content[..begin].TrimEnd('\n');
        }

        content = content.TrimEnd('\n');
        if (string.IsNullOrEmpty(block))
        {
            return content.Length == 0 ? "" : content + "\n";
        }

        return (content.Length == 0 ? "" : content + "\n\n") + block + "\n";
    }
}
