namespace Miller.Testing;

/// <summary>
/// Counts how many polls in a row answered <c>unavailable_delta</c>, and says when that run of
/// answers has to be treated as a degradation instead of as a healthy poll.
///
/// <para>Why it exists: <c>unavailable_delta</c> is not the string <c>degraded</c>, so the daemon loop
/// recorded it as a HEALTHY poll — no backoff applied, and the poll ran again 250 ms later. The
/// condition is sticky by design: the poller must not absorb an interval whose impact it could not
/// read, so the base stays put, the same unreadable interval is re-read on every tick, and the
/// Unknown impact answer enqueues nothing. One edit to a widely-used type therefore turned the daemon
/// into a 4 Hz loop that looked healthy while automatic runs had silently stopped.</para>
///
/// <para>Consecutive is the same as "on the same base". The poller advances its base only on a poll
/// that LANDED — a rebuild, a status-only pass, or a complete delta — and every one of those answers
/// clears this counter. So a streak here is always a run of unreadable answers over ONE unabsorbed
/// interval.</para>
///
/// <para>This type decides nothing about selection or watermarks. It only reports; the caller keeps
/// the fail-safe that an unavailable delta enqueues nothing and absorbs nothing.</para>
/// </summary>
public sealed class CtUnavailableDeltaTracker
{
    /// <summary>
    /// Consecutive unavailable answers tolerated before the caller backs off. Eight polls is two
    /// seconds at the default 250 ms poll interval: long enough that ONE transient answer (a reader
    /// that lost a race with a promote, a single bridge error) costs nothing, short enough that a
    /// stuck base is reported while the person whose edit caused it is still watching.
    /// </summary>
    public const int DefaultLimit = 8;

    private readonly int _limit;
    private int _streak;
    private string? _reason;

    public CtUnavailableDeltaTracker(int limit = DefaultLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        _limit = limit;
    }

    /// <summary>How many unavailable answers have arrived in a row.</summary>
    public int Streak => _streak;

    /// <summary>
    /// Why automatic runs are not happening, in plain English, or null while the poll path is
    /// healthy. The caller publishes this in the daemon status record, so <c>tests status</c> can
    /// answer the question a silent daemon cannot.
    /// </summary>
    public string? StuckReason => _reason;

    /// <summary>
    /// Records one <c>unavailable_delta</c> answer. Returns <c>true</c> once the run of them has
    /// reached the limit, which is the caller's signal to back the poll off and report the reason.
    /// </summary>
    public bool RecordUnavailable(string? deltaReason)
    {
        if (_streak < int.MaxValue)
            _streak++;
        if (_streak < _limit)
            return false;

        _reason = Describe(deltaReason);
        return true;
    }

    /// <summary>
    /// Records any other poll answer. The streak and the reason clear, because the answer either
    /// advanced the base or failed for a cause that has its own report.
    /// </summary>
    public void RecordOther()
    {
        _streak = 0;
        _reason = null;
    }

    /// <summary>
    /// The published wording. The delta reason (for example <c>impact_truncated</c>) is named when
    /// the poller supplied one, because it is the whole difference between "the index is busy" and
    /// "this edit is too widely used for the impact bound".
    /// </summary>
    public static string Describe(string? deltaReason) =>
        string.IsNullOrWhiteSpace(deltaReason)
            ? "auto-runs paused: impact unavailable"
            : $"auto-runs paused: impact unavailable ({deltaReason.Trim()})";
}
