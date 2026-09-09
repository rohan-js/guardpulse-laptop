namespace GuardPulse.Agent.Core;

using System.IO;
using GuardPulse.Protocol;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Uninstall gate: makes removing GuardPulse require the parent PIN. The service
/// mirrors the CURRENT snapshot PIN into uninstall-pin.json (SYSTEM/Admins-only);
/// the elevated uninstaller verifies a candidate PIN against it and, on success,
/// writes a short-lived permit (uninstall-permit.json) that the installer consumes
/// before tearing anything down. Wrong attempts are recorded for tamper events and
/// rate-limited with the same escalating pattern as the wall's PIN gate. Devices
/// that were never paired have no pin file: they stay freely uninstallable.
/// Thread-safe; all state lives on disk (survives restarts like PinRetryGate).
/// </summary>
public sealed class UninstallGate
{
    /// <summary>How long a granted permit stays valid for the installer to consume.</summary>
    public static readonly TimeSpan PermitTtl = TimeSpan.FromMinutes(15);

    private const int MaxAttemptsPerWindow = 5;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BaseRejectionMs = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRejectionMs = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string stateDirectory;
    private readonly TimeProvider time;
    private readonly object gate = new();

    public UninstallGate(string stateDirectory, TimeProvider time)
    {
        this.stateDirectory = stateDirectory;
        this.time = time;
        Directory.CreateDirectory(stateDirectory);
    }

    private string PinFilePath => Path.Combine(stateDirectory, "uninstall-pin.json");
    private string PermitFilePath => Path.Combine(stateDirectory, "uninstall-permit.json");
    private string AttemptsFilePath => Path.Combine(stateDirectory, "uninstall-attempts.json");

    /// <summary>Mirrors the snapshot's PIN (salt/hash/iterations/version). Call whenever
    /// control applies a pin. Nothing is written when the snapshot has no pin AND no
    /// pin file exists (never-paired device: gate stays open).</summary>
    public void SetPin(string salt, string expectedHash, int iterations, int version)
    {
        lock (this.gate)
        {
            AtomicFile.WriteAllText(this.PinFilePath, JsonSerializer.Serialize(
                new PinFile
                {
                    Salt = salt,
                    Hash = expectedHash,
                    Iterations = iterations,
                    Version = version,
                    UpdatedAtMs = this.time.GetUtcNow().ToUnixTimeMilliseconds(),
                },
                JsonOptions));
        }
    }

    /// <summary>Removes the pin mirror (parent cleared the PIN): uninstall becomes free.</summary>
    public void ClearPin()
    {
        lock (this.gate)
        {
            TryDelete(this.PinFilePath);
        }
    }

    /// <summary>True when a PIN mirror exists — i.e. removal requires the PIN.</summary>
    public bool IsPinGated()
    {
        lock (this.gate)
        {
            return File.Exists(this.PinFilePath);
        }
    }

    /// <summary>
    /// Verifies a candidate PIN from the uninstaller. Returns a decision: granted
    /// (permit written), rejected (wrong PIN; failure recorded), or locked (wrong PIN
    /// inside an active rejection window; nothing recorded). Caller surfaces only
    /// granted/locked to avoid confirming guesses.
    /// </summary>
    public UninstallPinDecision VerifyPin(string candidate)
    {
        lock (this.gate)
        {
            var now = this.time.GetUtcNow().ToUnixTimeMilliseconds();
            var attempts = ReadAttempts();
            attempts.Attempts.RemoveAll(a => now - a > (long)AttemptWindow.TotalMilliseconds);

            if (attempts.RejectedUntilMs > 0 && attempts.RejectedUntilMs <= now)
            {
                // Lockout served: stale failures clear (RejectionCount stays, so a
                // new lockout escalates like PinRetryGate does).
                attempts.Attempts.Clear();
            }

            if (attempts.RejectedUntilMs > now)
            {
                // Active lockout: refuse without evaluating (and without extending
                // the window — only NEW failed attempts grow it, or the jail would
                // never expire for someone who keeps poking).
                return new UninstallPinDecision(UninstallPinResult.Locked, attempts.RejectedUntilMs);
            }

            PinFile? pin = null;
            try
            {
                pin = JsonSerializer.Deserialize<PinFile>(File.ReadAllText(this.PinFilePath), JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                pin = null;
            }

            var ok = pin is not null
                && !string.IsNullOrWhiteSpace(candidate)
                && PinHasher.Verify(candidate, pin.Salt, pin.Hash, pin.Version, null, pin.Iterations);

            if (ok)
            {
                attempts.Attempts.Clear();
                attempts.RejectedUntilMs = 0;
                WriteAttempts(attempts);
                GrantPermit(now);
                return new UninstallPinDecision(UninstallPinResult.Granted, 0);
            }

            attempts.Attempts.Add(now);
            if (attempts.Attempts.Count >= MaxAttemptsPerWindow)
            {
                ExtendRejection(attempts, now);
            }

            WriteAttempts(attempts);
            return new UninstallPinDecision(UninstallPinResult.Rejected, attempts.RejectedUntilMs);
        }
    }

    /// <summary>True when a fresh, unconsumed permit exists. Call before ANY teardown step.</summary>
    public bool HasFreshPermit()
    {
        lock (this.gate)
        {
            try
            {
                var permit = JsonSerializer.Deserialize<PermitFile>(File.ReadAllText(this.PermitFilePath), JsonOptions);
                return permit is { } p
                    && this.time.GetUtcNow().ToUnixTimeMilliseconds() - p.IssuedAtMs <= (long)PermitTtl.TotalMilliseconds;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Consumes the permit (delete). Called once destruction is authorized.</summary>
    public void ConsumePermit()
    {
        lock (this.gate)
        {
            TryDelete(this.PermitFilePath);
        }
    }

    private void GrantPermit(long nowMs)
    {
        AtomicFile.WriteAllText(this.PermitFilePath, JsonSerializer.Serialize(
            new PermitFile
            {
                IssuedAtMs = nowMs,
                Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
            },
            JsonOptions));
    }

    private void ExtendRejection(AttemptsFile attempts, long nowMs)
    {
        // Each locked window doubles the rejection, capped at the max.
        var consecutive = Math.Max(1, attempts.RejectionCount + 1);
        attempts.RejectionCount = consecutive;
        var ms = (long)BaseRejectionMs.TotalMilliseconds << Math.Min(consecutive - 1, 4);
        attempts.RejectedUntilMs = Math.Max(nowMs, attempts.RejectedUntilMs) + Math.Min(ms, (long)MaxRejectionMs.TotalMilliseconds);
    }

    private AttemptsFile ReadAttempts()
    {
        try
        {
            return JsonSerializer.Deserialize<AttemptsFile>(File.ReadAllText(this.AttemptsFilePath), JsonOptions) ?? new AttemptsFile();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AttemptsFile();
        }
    }

    private void WriteAttempts(AttemptsFile attempts)
    {
        AtomicFile.WriteAllText(this.AttemptsFilePath, JsonSerializer.Serialize(attempts, JsonOptions));
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class PinFile
    {
        public string Salt { get; set; } = string.Empty;

        public string Hash { get; set; } = string.Empty;

        public int Iterations { get; set; }

        public int Version { get; set; } = 1;

        public long UpdatedAtMs { get; set; }
    }

    private sealed class PermitFile
    {
        public long IssuedAtMs { get; set; }

        public string Nonce { get; set; } = string.Empty;
    }

    private sealed class AttemptsFile
    {
        public List<long> Attempts { get; set; } = new();

        public long RejectedUntilMs { get; set; }

        public int RejectionCount { get; set; }
    }
}

/// <summary>Outcome of <see cref="UninstallGate.VerifyPin"/>.</summary>
public enum UninstallPinResult
{
    Granted,
    Rejected,
    Locked,
}

public sealed record UninstallPinDecision(UninstallPinResult Result, long RejectedUntilMs);
