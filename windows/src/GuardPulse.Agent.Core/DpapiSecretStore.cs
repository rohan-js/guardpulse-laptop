namespace GuardPulse.Agent.Core;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Small durable key/value secret store. Values live in one JSON file under
/// %ProgramData%\GuardPulse\Laptop whose whole content is DPAPI-encrypted with
/// DataProtectionScope.CurrentUser (the service account), so secrets cannot be
/// read by other local users.
/// </summary>
public interface ISecretStore
{
    string? Get(string key);

    void Set(string key, string value);

    void Delete(string key);
}

/// <summary>
/// DPAPI-backed <see cref="ISecretStore"/> persisted at
/// %ProgramData%\GuardPulse\Laptop\{fileName}. Uses crypt32 directly because the
/// Agent.Core project (plain net8.0) must not take the ProtectedData package
/// reference.
/// Durability: every save also maintains a second, machine-scope encrypted
/// mirror ({fileName}.mirror). The primary blob is CurrentUser-scoped, so a
/// corrupted primary file (dirty shutdown losing a torn write) or a changed
/// service principal would previously SILENTLY wipe the deviceId/refresh token
/// and orphan the pairing. Load() now falls back to the mirror and restores the
/// primary, so the identity survives either failure. Only when BOTH blobs are
/// unreadable does the store start empty (intended for a deliberate data wipe).
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private const int CryptProtectLocalMachine = 0x4;

    private readonly string _path;
    private readonly string _mirrorPath;
    private readonly object _gate = new();
    private readonly Action<string>? _diagnostics;
    private Dictionary<string, string> _values;

    public DpapiSecretStore(string fileName, Action<string>? diagnostics = null, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name must be provided.", nameof(fileName));
        }

        _diagnostics = diagnostics;
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GuardPulse",
            "Laptop");
        _path = Path.Combine(root, fileName);
        _mirrorPath = _path + ".mirror";
        _values = Load();
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            _values[key] = value;
            Save();
        }
    }

    public void Delete(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_values.Remove(key))
            {
                Save();
            }
        }
    }

    private Dictionary<string, string> Load()
    {
        // Primary: CurrentUser-scope blob written by this service principal.
        try
        {
            var cipher = File.ReadAllBytes(_path);
            var plain = Unprotect(cipher);
            var primary = JsonSerializer.Deserialize<Dictionary<string, string>>(plain)
                ?? new Dictionary<string, string>();
            if (primary.Count > 0)
            {
                return primary;
            }

            // A decryptable-but-empty primary usually means a torn write took both
            // blobs down at once (0.2.11 window) - still worth consulting the mirror
            // before declaring the identity gone.
            _diagnostics?.Invoke("DpapiSecretStore primary decrypts to empty; trying mirror.");
        }
        catch (Exception ex)
        {
            // Missing, corrupted, or encrypted for a different principal (e.g. after
            // service account changes): fall back to the machine-scope mirror before
            // starting empty - an empty store here means a NEW deviceId and anonymous
            // uid, which orphans the pairing on the parent's phone.
            if (File.Exists(_path))
            {
                _diagnostics?.Invoke("DpapiSecretStore load failed (file exists): " + ex.Message);
            }
        }

        // Mirror: LocalMachine-scope blob, decryptable by the service account
        // regardless of what invalidated the primary.
        try
        {
            var cipher = File.ReadAllBytes(_mirrorPath);
            var plain = Unprotect(cipher, machineScope: true);
            var restored = JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
            if (restored is not null && restored.Count > 0)
            {
                _diagnostics?.Invoke("DpapiSecretStore primary unreadable - identity restored from mirror (" + restored.Count + " keys)");
                WritePrimary(restored);
                return restored;
            }
        }
        catch (Exception ex)
        {
            _diagnostics?.Invoke("DpapiSecretStore mirror restore failed: " + ex.Message);
        }

        return new Dictionary<string, string>();
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        WritePrimary(_values);
        WriteMirror(_values);
    }

    private void WritePrimary(Dictionary<string, string> values)
    {
        var plain = JsonSerializer.Serialize(values);
        var cipher = Protect(Encoding.UTF8.GetBytes(plain));
        var tempPath = _path + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(cipher, 0, cipher.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, _path, overwrite: true);
    }

    private void WriteMirror(Dictionary<string, string> values)
    {
        var plain = JsonSerializer.Serialize(values);
        var cipher = Protect(Encoding.UTF8.GetBytes(plain), machineScope: true);
        var tempPath = _mirrorPath + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(cipher, 0, cipher.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, _mirrorPath, overwrite: true);
    }

    private static byte[] Protect(byte[] data, bool machineScope = false)
    {
        var input = ToBlob(data);
        try
        {
            var flags = CryptProtectUiForbidden | (machineScope ? CryptProtectLocalMachine : 0);
            if (!CryptProtectData(ref input, "GuardPulse.Agent.Core", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, out var output))
            {
                throw new CryptographicException($"CryptProtectData failed with error {Marshal.GetLastWin32Error()}.");
            }

            try
            {
                return FromBlob(output);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            FreeBlob(ref input);
        }
    }

    private static byte[] Unprotect(byte[] data, bool machineScope = false)
    {
        // DPAPI scopes are encoded in the blob itself; the flag only matters when
        // protecting. Passing it on unprotect is harmless and keeps intent explicit.
        var input = ToBlob(data);
        try
        {
            var flags = CryptProtectUiForbidden | (machineScope ? CryptProtectLocalMachine : 0);
            if (!CryptUnprotectData(ref input, out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, out var output))
            {
                throw new CryptographicException($"CryptUnprotectData failed with error {Marshal.GetLastWin32Error()}.");
            }

            try
            {
                return FromBlob(output);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            FreeBlob(ref input);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;

        public IntPtr Data;
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { Size = data.Length, Data = pointer };
    }

    private static void FreeBlob(ref DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.Data);
        }

        blob = default;
    }

    private static byte[] FromBlob(DataBlob blob)
    {
        var result = new byte[blob.Size];
        if (blob.Size > 0)
        {
            Marshal.Copy(blob.Data, result, 0, blob.Size);
        }

        return result;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
