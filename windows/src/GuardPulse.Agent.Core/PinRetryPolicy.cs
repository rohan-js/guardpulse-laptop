namespace GuardPulse.Agent.Core;

using System.IO;
using System.Text.Json;

/// <summary>
/// Escalating back-off for wrong PIN entries. Consecutive failures 1-3 block for 1s,
/// 4 for 5s, 5 for 15s, 6 for 30s, then 30s doubled per extra failure, capped at 5 minutes.
/// A success resets the ladder. State persists to pins.json in the state directory.
/// Thread-safe: all public members synchronize on a single lock object.
/// </summary>
public sealed class PinRetryPolicy
{
    private const long Step1To3Ms = 1_000L;
    private const long Step4Ms = 5_000L;
    private const long Step5Ms = 15_000L;
    private const long Step6Ms = 30_000L;
    private const long MaxBlockMs = 5 * 60_000L;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string stateFilePath;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private int consecutiveFailures;
    private long blockedUntilMs;

    public PinRetryPolicy(string stateDirectory, TimeProvider time)
    {
        this.stateFilePath = Path.Combine(stateDirectory, "pins.json");
        this.time = time;
        Directory.CreateDirectory(stateDirectory);
        Load();
    }

    public void RecordFailure()
    {
        lock (this.gate)
        {
            this.consecutiveFailures++;
            this.blockedUntilMs = NowMs() + BlockMsFor(this.consecutiveFailures);
            Persist();
        }
    }

    public void RecordSuccess()
    {
        lock (this.gate)
        {
            if (this.consecutiveFailures == 0 && this.blockedUntilMs == 0)
            {
                return;
            }

            this.consecutiveFailures = 0;
            this.blockedUntilMs = 0;
            Persist();
        }
    }

    /// <summary>Epoch-ms when the current block ends; 0 when not blocked.</summary>
    public long BlockedUntilMs()
    {
        lock (this.gate)
        {
            return this.blockedUntilMs > NowMs() ? this.blockedUntilMs : 0L;
        }
    }

    public bool IsBlocked()
    {
        lock (this.gate)
        {
            return NowMs() < this.blockedUntilMs;
        }
    }

    private static long BlockMsFor(int failures)
    {
        return failures switch
        {
            <= 0 => 0L,
            <= 3 => Step1To3Ms,
            4 => Step4Ms,
            5 => Step5Ms,
            6 => Step6Ms,
            _ => Math.Min(Step6Ms << Math.Min(failures - 6, 16), MaxBlockMs),
        };
    }

    private long NowMs() => this.time.GetUtcNow().ToUnixTimeMilliseconds();

    private void Load()
    {
        try
        {
            if (!File.Exists(this.stateFilePath))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(this.stateFilePath), JsonOptions);
            if (payload != null)
            {
                this.consecutiveFailures = Math.Max(0, payload.ConsecutiveFailures);
                this.blockedUntilMs = payload.BlockedUntilMs;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            this.consecutiveFailures = 0;
            this.blockedUntilMs = 0;
        }
    }

    private void Persist()
    {
        var payload = new Payload
        {
            ConsecutiveFailures = this.consecutiveFailures,
            BlockedUntilMs = this.blockedUntilMs,
        };
        File.WriteAllText(this.stateFilePath, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private sealed class Payload
    {
        public int ConsecutiveFailures { get; set; }

        public long BlockedUntilMs { get; set; }
    }
}
