namespace WpfMarkdownViewer.Streaming;

/// <summary>
/// Pure, side-effect-free policy that maps recent token throughput to a flush interval, plus idle
/// detection. Discrete three-tier design (see grilling decision "自适应离散三档"): kept pure so the
/// tier logic is unit-testable in isolation from the DispatcherTimer that drives flushing.
/// </summary>
public sealed class AdaptiveThrottlePolicy
{
    /// <summary>Upper bound (tokens/sec) of the "slow" tier — sparse stream, flush near-immediately.</summary>
    public double SlowMaxRate { get; init; } = 15;

    /// <summary>Upper bound (tokens/sec) of the "mid" tier; above this is the "fast" tier.</summary>
    public double MidMaxRate { get; init; } = 60;

    public TimeSpan SlowInterval { get; init; } = TimeSpan.FromMilliseconds(16);
    public TimeSpan MidInterval { get; init; } = TimeSpan.FromMilliseconds(33);
    public TimeSpan FastInterval { get; init; } = TimeSpan.FromMilliseconds(75);

    /// <summary>If no input arrives for longer than this, the pump flushes immediately and may refine the Active Block.</summary>
    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>The flush interval to use given the most recent token rate.</summary>
    public TimeSpan NextInterval(double tokensPerSecond)
    {
        if (tokensPerSecond <= SlowMaxRate)
            return SlowInterval;
        if (tokensPerSecond <= MidMaxRate)
            return MidInterval;
        return FastInterval;
    }

    /// <summary>Whether the stream is considered idle given how long since the last input arrived.</summary>
    public bool IsIdle(TimeSpan sinceLastInput) => sinceLastInput >= IdleThreshold;
}
