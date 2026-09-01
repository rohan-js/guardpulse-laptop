namespace GuardPulse.Agent.Core.Tests;

using System.Text;
using Xunit;

/// <summary>
/// Durability tests for the DPAPI secret store identity mirror: a corrupted or
/// missing primary blob must be recovered from the machine-scope mirror instead
/// of silently starting empty (which orphans the pairing).
/// </summary>
public class DpapiSecretStoreTests
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "gp-dpapi-tests-" + Guid.NewGuid().ToString("N"));

    private DpapiSecretStore Create() => new("secrets.bin", directory: dir);

    private string PrimaryPath => Path.Combine(dir, "secrets.bin");

    private string MirrorPath => Path.Combine(dir, "secrets.bin.mirror");

    [Fact]
    public void SetGet_PersistsAcrossInstances()
    {
        try
        {
            var store = Create();
            store.Set("pairing.deviceId", "device-a");
            store.Set("auth.refreshToken", "token-1");

            var again = Create();
            Assert.Equal("device-a", again.Get("pairing.deviceId"));
            Assert.Equal("token-1", again.Get("auth.refreshToken"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Mirror_IsWrittenOnFirstSave()
    {
        try
        {
            var store = Create();
            store.Set("pairing.deviceId", "device-a");
            Assert.True(File.Exists(PrimaryPath), "primary blob should exist");
            Assert.True(File.Exists(MirrorPath), "mirror blob should exist");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void CorruptedPrimary_IsRestoredFromMirror()
    {
        try
        {
            var store = Create();
            store.Set("pairing.deviceId", "device-a");
            store.Set("auth.refreshToken", "token-1");

            // Simulate a dirty shutdown tearing the primary write: garbage bytes.
            File.WriteAllBytes(PrimaryPath, Encoding.UTF8.GetBytes("corrupted-not-a-dpapi-blob"));

            var recovered = Create();
            Assert.Equal("device-a", recovered.Get("pairing.deviceId"));
            Assert.Equal("token-1", recovered.Get("auth.refreshToken"));

            // The primary must also have been rewritten so later loads are clean.
            var again = Create();
            Assert.Equal("device-a", again.Get("pairing.deviceId"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void DeletedPrimary_IsRestoredFromMirror()
    {
        try
        {
            var store = Create();
            store.Set("pairing.deviceId", "device-b");
            store.Set("pairing.code", "123456");

            File.Delete(PrimaryPath);

            var recovered = Create();
            Assert.Equal("device-b", recovered.Get("pairing.deviceId"));
            Assert.Equal("123456", recovered.Get("pairing.code"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void FullWipe_PrimaryAndMirror_StillStartsEmpty()
    {
        try
        {
            var store = Create();
            store.Set("pairing.deviceId", "device-c");

            // Deliberate data wipe (uninstaller) removes both blobs.
            File.Delete(PrimaryPath);
            File.Delete(MirrorPath);

            var fresh = Create();
            Assert.Null(fresh.Get("pairing.deviceId"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Delete_RemovesKeyFromPrimaryAndMirror()
    {
        try
        {
            var store = Create();
            store.Set("k1", "v1");
            store.Set("k2", "v2");
            store.Delete("k1");

            var again = Create();
            Assert.Null(again.Get("k1"));
            Assert.Equal("v2", again.Get("k2"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
