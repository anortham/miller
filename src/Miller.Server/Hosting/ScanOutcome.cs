using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// The typed result of <see cref="IndexerService.TryScanAsLeader(bool)"/> (M7 decision-3) — the leader-gated
/// scan trigger behind <c>workspace refresh/full</c>. The outcome is reported honestly so the tool can render
/// what actually happened without faking success: this instance scanned (carrying julie's
/// <see cref="ExtractReport"/>), it is not the writer-lock leader (so it MUST NOT scan — the M3 single-writer
/// corruption guard), or the scan was attempted but failed. Best-effort: an extract failure is captured here as
/// <see cref="Kind.Failed"/>, never thrown into the caller.
/// </summary>
/// <param name="Result">Which branch the trigger took.</param>
/// <param name="Report">julie's extract report on <see cref="Kind.Scanned"/>; <c>null</c> otherwise.</param>
public sealed record ScanOutcome(ScanOutcome.Kind Result, ExtractReport? Report)
{
    /// <summary>The three honest outcomes of a leader-gated scan trigger.</summary>
    public enum Kind
    {
        /// <summary>This instance is the leader; the <c>extract scan</c> ran (see <see cref="Report"/>).</summary>
        Scanned,

        /// <summary>
        /// This instance does not hold the writer lock, so it did NOT scan (another miller owns the writes —
        /// the M3 corruption guard). The caller relies on the leader's watcher + the freshness poll instead.
        /// </summary>
        NotLeader,

        /// <summary>This instance is the leader but the <c>extract scan</c> failed (logged; never thrown).</summary>
        Failed,
    }

    /// <summary>This instance is not the writer-lock leader, so no scan was performed.</summary>
    public static ScanOutcome NotLeader { get; } = new(Kind.NotLeader, Report: null);

    /// <summary>The leader's scan failed (the extract subprocess threw); the prior index is kept.</summary>
    public static ScanOutcome Failed { get; } = new(Kind.Failed, Report: null);

    /// <summary>The leader scanned successfully, carrying julie's <paramref name="report"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    public static ScanOutcome Scanned(ExtractReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new ScanOutcome(Kind.Scanned, report);
    }
}
