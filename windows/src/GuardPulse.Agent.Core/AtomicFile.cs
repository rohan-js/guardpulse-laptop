namespace GuardPulse.Agent.Core;

using System.IO;
using System.Text;

/// <summary>
/// Crash-safe file writes: content is written to a unique temp file in the same
/// directory, flushed to disk, then atomically moved over the destination. A crash
/// mid-write leaves the destination untouched (at worst an orphaned temp file).
/// </summary>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> atomically (UTF-8, no BOM).</summary>
    public static void WriteAllText(string path, string content)
    {
        WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static void WriteAllText(string path, string content, Encoding encoding)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = encoding.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
                // best-effort cleanup of the orphaned temp file
            }

            throw;
        }
    }
}
