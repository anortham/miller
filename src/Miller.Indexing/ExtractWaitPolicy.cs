using System.Globalization;

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
    /// scan finishes, small enough to bound a pathological extractor that writes forever.
    ///
    /// <para>At the default 10-minute stall window this is 4 hours. It used to be 6× — one hour — and that was
    /// measured to be too small to be a backstop at all: a HEALTHY force scan of a 74k-file repo (the size in
    /// the 2026-08-01 fleet field report) took 61.3 minutes and would have been killed 77 seconds before it
    /// finished, on every attempt, forever
    /// (`docs/findings/2026-08-02-w10-scale-repro-wal-measurement.md`). A cap a real workload cannot beat does
    /// not bound a runaway extractor; it converts the largest repos into permanently unindexable ones.</para>
    /// </summary>
    public const int HardCapMultiplier = 24;

    /// <summary>Operator override for the absolute cap: a positive number of seconds, or a TimeSpan.</summary>
    public const string HardCapEnvironmentVariable = "MILLER_EXTRACT_HARD_CAP";

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
    /// The absolute cap for the DEFAULT production stall window, honoring
    /// <see cref="HardCapEnvironmentVariable"/>. An override at or below <paramref name="stallTimeout"/> is
    /// ignored rather than clamped: it would make the cap fire before the stall window could ever be reached,
    /// which is a typo far more often than an intent, and the constructor rejects it outright.
    ///
    /// <para>This is deliberately NOT applied to a caller-supplied stall window. The explicit-timeout
    /// constructor is the kill-path test seam, and an operator's machine-wide override must not reach in and
    /// change what a caller asked for — the same rule <c>ExtractJobsPolicy</c> follows.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="readEnvironmentVariable"/> is null.</exception>
    public static TimeSpan HardTimeoutForEnvironment(
        TimeSpan stallTimeout, Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        return ParseDuration(readEnvironmentVariable(HardCapEnvironmentVariable)) is { } configured
               && configured > stallTimeout
            ? configured
            : HardTimeoutFor(stallTimeout);
    }

    public static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            && seconds > 0 && !double.IsNaN(seconds) && !double.IsInfinity(seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed) && parsed > TimeSpan.Zero
            ? parsed
            : null;
    }

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
