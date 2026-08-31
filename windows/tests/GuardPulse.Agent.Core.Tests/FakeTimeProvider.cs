namespace GuardPulse.Agent.Core.Tests;

/// <summary>
/// Deterministic TimeProvider for tests: GetUtcNow returns an adjustable value and
/// LocalTimeZone defaults to UTC so local-midnight day keys are predictable.
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset utcNow;
    private TimeZoneInfo localTimeZone = TimeZoneInfo.Utc;

    public FakeTimeProvider(DateTimeOffset initialUtcNow)
    {
        this.utcNow = initialUtcNow;
    }

    public FakeTimeProvider(long initialEpochMs)
        : this(DateTimeOffset.FromUnixTimeMilliseconds(initialEpochMs))
    {
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (this.gate)
        {
            return this.utcNow;
        }
    }

    public override TimeZoneInfo LocalTimeZone
    {
        get
        {
            lock (this.gate)
            {
                return this.localTimeZone;
            }
        }
    }

    /// <summary>Current fake time in epoch milliseconds (identical to GetUtcNow converted).</summary>
    public long NowMs()
    {
        return GetUtcNow().ToUnixTimeMilliseconds();
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        lock (this.gate)
        {
            this.utcNow = value;
        }
    }

    public void SetUtcNow(long epochMs)
    {
        SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(epochMs));
    }

    public void Advance(TimeSpan by)
    {
        lock (this.gate)
        {
            this.utcNow = this.utcNow.Add(by);
        }
    }

    public void AdvanceMs(long milliseconds)
    {
        Advance(TimeSpan.FromMilliseconds(milliseconds));
    }

    public void SetLocalTimeZone(TimeZoneInfo zone)
    {
        lock (this.gate)
        {
            this.localTimeZone = zone;
        }
    }
}
