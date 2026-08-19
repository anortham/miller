namespace Miller.Testing;

/// <summary>
/// Jittered poll backoff while the index is degraded. No enqueue while degraded.
/// </summary>
public sealed class CtDegradationBackoff
{
    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromSeconds(5);

    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<double> _jitter;
    private readonly TimeSpan _baseDelay;
    private DateTimeOffset? _nextPollAt;
    private bool _degraded;

    public CtDegradationBackoff(
        Func<DateTimeOffset>? clock = null,
        Func<double>? jitter = null,
        TimeSpan? baseDelay = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _jitter = jitter ?? (() => Random.Shared.NextDouble());
        _baseDelay = baseDelay is { } delay && delay > TimeSpan.Zero ? delay : DefaultBaseDelay;
    }

    public bool CanPoll => !_degraded || _clock() >= (_nextPollAt ?? DateTimeOffset.MinValue);

    public bool CanEnqueue => !_degraded;

    public void RecordDegraded()
    {
        _degraded = true;
        double factor = 0.5 + _jitter();
        _nextPollAt = _clock() + TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * factor);
    }

    public void RecordHealthy()
    {
        _degraded = false;
        _nextPollAt = null;
    }
}
