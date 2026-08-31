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
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private const int CryptProtectUiForbidden = 0x1;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Action<string>? _diagnostics;
    private Dictionary<string, string> _values;

    public DpapiSecretStore(string fileName, Action<string>? diagnostics = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name must be provided.", nameof(fileName));
        }

        _diagnostics = diagnostics;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GuardPulse",
            "Laptop");
        _path = Path.Combine(root, fileName);
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
        try
        {
            var cipher = File.ReadAllBytes(_path);
            var plain = Unprotect(cipher);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            // Missing, corrupted, or encrypted for a different principal (e.g. after
            // service account changes): start empty rather than failing the agent, but
            // surface the reason so a wiped store is observable instead of silent.
            if (File.Exists(_path))
            {
                _diagnostics?.Invoke("DpapiSecretStore load failed (file exists): " + ex.Message);
            }

            return new Dictionary<string, string>();
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var plain = JsonSerializer.Serialize(_values);
        var cipher = Protect(Encoding.UTF8.GetBytes(plain));
        var tempPath = _path + ".tmp";
        File.WriteAllBytes(tempPath, cipher);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static byte[] Protect(byte[] data)
    {
        var input = ToBlob(data);
        try
        {
            if (!CryptProtectData(ref input, "GuardPulse.Agent.Core", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
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

    private static byte[] Unprotect(byte[] data)
    {
        var input = ToBlob(data);
        try
        {
            if (!CryptUnprotectData(ref input, out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
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
