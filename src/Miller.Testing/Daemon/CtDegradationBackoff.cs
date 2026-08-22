namespace Miller.Testing;

/// <summary>
/// Jittered poll backoff while the index is degraded. No enqueue while degraded.
///
/// <para>There are two degradations, and they differ in what they stop. A DEGRADED INDEX stops both
/// arms: nothing readable came back, so neither a poll nor an enqueue can be trusted. A STUCK POLL
/// (<see cref="RecordPollDegraded"/>) stops only the poll: the answers are unreadable, but work
/// already accepted at an earlier, readable base is unaffected and must still be free to run.</para>
/// </summary>
public sealed class CtDegradationBackoff
{
    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromSeconds(5);

    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<double> _jitter;
    private readonly TimeSpan _baseDelay;
    private DateTimeOffset? _nextPollAt;
    private bool _degraded;
    private bool _pollDegraded;

    public CtDegradationBackoff(
        Func<DateTimeOffset>? clock = null,
        Func<double>? jitter = null,
        TimeSpan? baseDelay = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _jitter = jitter ?? (() => Random.Shared.NextDouble());
        _baseDelay = baseDelay is { } delay && delay > TimeSpan.Zero ? delay : DefaultBaseDelay;
    }

    public bool CanPoll =>
        (!_degraded && !_pollDegraded) || _clock() >= (_nextPollAt ?? DateTimeOffset.MinValue);

    public bool CanEnqueue => !_degraded;

    public void RecordDegraded()
    {
        _degraded = true;
        Schedule();
    }

    /// <summary>
    /// The poll itself keeps failing to produce a usable answer, but the index reads fine. Slows the
    /// poll to the same jittered cadence a degraded index gets, and leaves the enqueue arm armed.
    /// </summary>
    public void RecordPollDegraded()
    {
        _pollDegraded = true;
        Schedule();
    }

    public void RecordHealthy()
    {
        _degraded = false;
        _pollDegraded = false;
        _nextPollAt = null;
    }

    private void Schedule()
    {
        double factor = 0.5 + _jitter();
        _nextPollAt = _clock() + TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * factor);
    }
}
