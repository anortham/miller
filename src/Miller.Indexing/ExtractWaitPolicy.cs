namespace Miller.Indexing;

/// <summary>What the bounded wait should do with a still-running julie-extract process.</summary>
public enum ExtractWaitVerdict
{
    /// <summary>The process is making progress (or within its no-progress budget) — keep waiting.</summary>
    Continue,

    /// <summary>No observable progress for the stall window — treat as hung and kill.</summary>
    Stalled,

    /// <summary>Still progressing but past the absolute cap — kill to bound runaway extracts.</summary>
    HardCapExceeded,
}

/// <summary>
/// Pure decision core for <see cref="JulieExtractRunner"/>'s bounded wait. A fixed total timeout cannot tell a
/// hung extractor from a legitimately long scan: the same openclaw force-scan that finishes in ~90s idle takes
/// 12+ minutes under fleet load, so a 600s total cap killed healthy rebuilds (2026-06-11 Eros field report).
/// Instead, the runner samples a progress stamp (artifact db/-wal/-shm bytes plus stdout/stderr activity) and
/// this policy kills only when the stamp stops moving for the stall window, with a generous absolute cap as a
/// runaway backstop. Pure (no clock, no I/O) so the fast suite can cover every verdict.
/// </summary>
public sealed class ExtractWaitPolicy
{
    /// <summary>Absolute-cap multiple of the stall window: generous enough that any progressing real-world
    /// scan finishes, small enough to bound a pathological extractor that writes forever.</summary>
    public const int HardCapMultiplier = 6;

    private readonly TimeSpan _stallTimeout;
    private readonly TimeSpan _hardTimeout;
    private long? _lastStamp;
    private TimeSpan _lastProgress = TimeSpan.Zero;

    public ExtractWaitPolicy(TimeSpan stallTimeout, TimeSpan hardTimeout)
    {
        if (stallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stallTimeout), stallTimeout, "Stall timeout must be positive.");
        if (hardTimeout < stallTimeout)
            throw new ArgumentOutOfRangeException(nameof(hardTimeout), hardTimeout, "Hard cap must be >= the stall timeout.");
        _stallTimeout = stallTimeout;
        _hardTimeout = hardTimeout;
    }

    public static TimeSpan HardTimeoutFor(TimeSpan stallTimeout) => stallTimeout * HardCapMultiplier;

    /// <summary>
    /// Observe the process at <paramref name="elapsed"/> since start with the current progress stamp.
    /// A changed stamp counts as progress; the first stamp is only a baseline (a process that never
    /// produces anything stalls out measured from start).
    /// </summary>
    public ExtractWaitVerdict Observe(TimeSpan elapsed, long progressStamp)
    {
        if (_lastStamp is { } previous)
        {
            if (progressStamp != previous)
            {
                _lastStamp = progressStamp;
                _lastProgress = elapsed;
            }
        }
        else
        {
            _lastStamp = progressStamp;
        }

        if (elapsed >= _hardTimeout)
            return ExtractWaitVerdict.HardCapExceeded;
        if (elapsed - _lastProgress >= _stallTimeout)
            return ExtractWaitVerdict.Stalled;
        return ExtractWaitVerdict.Continue;
    }
}
