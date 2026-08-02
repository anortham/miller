using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// The typed result of <see cref="IndexerService.TryScanAsLeader(Miller.Core.Freshness.ScanIntent, bool)"/>
/// (M7 decision-3) — the leader-gated scan trigger behind <c>workspace refresh/full</c>. The outcome is reported
/// honestly so the tool can render what actually happened without faking success: this instance scanned (carrying
/// julie's <see cref="ExtractReport"/>), the requested rebuild was DOWNGRADED to a delta and is still owed, it is
/// not the writer-lock leader (so it MUST NOT scan — the M3 single-writer corruption guard), the scan was
/// attempted but failed, or machine-wide admission was busy so the scan is queued behind another workspace's.
/// Best-effort: an extract failure is captured here as <see cref="Kind.Failed"/>, never thrown into the caller.
/// </summary>
/// <param name="Result">Which branch the trigger took.</param>
/// <param name="Report">
/// julie's extract report on <see cref="Kind.Scanned"/> and <see cref="Kind.Downgraded"/> (the delta that
/// actually ran); <c>null</c> otherwise.
/// </param>
/// <param name="HolderDescription">
/// Who holds machine-wide scan admission, on <see cref="Kind.Queued"/>; <c>null</c> otherwise.
/// </param>
/// <param name="DowngradeReason">
/// Why the rebuild ran as a delta and what is still owed, on <see cref="Kind.Downgraded"/>; <c>null</c> otherwise.
/// </param>
public sealed record ScanOutcome(
    ScanOutcome.Kind Result,
    ExtractReport? Report,
    string? HolderDescription = null,
    string? DowngradeReason = null)
{
    /// <summary>The five honest outcomes of a leader-gated scan trigger.</summary>
    public enum Kind
    {
        /// <summary>This instance is the leader; the <c>extract scan</c> ran (see <see cref="Report"/>).</summary>
        Scanned,

        /// <summary>
        /// This instance is the leader and a scan ran, but WEAKER than the one requested: a from-scratch rebuild
        /// was downgraded to a delta reconcile against the still-servable prior artifact after repeated failures.
        /// Neither a success nor a failure — the prior artifact is served with degraded freshness and the rebuild
        /// is STILL OWED, so a caller must re-arm it rather than report the rebuild as done.
        /// </summary>
        Downgraded,

        /// <summary>
        /// This instance does not hold the writer lock, so it did NOT scan (another miller owns the writes —
        /// the M3 corruption guard). The caller relies on the leader's watcher + the freshness poll instead.
        /// </summary>
        NotLeader,

        /// <summary>This instance is the leader but the <c>extract scan</c> failed (logged; never thrown).</summary>
        Failed,

        /// <summary>
        /// This instance is the leader, but another scan on this machine holds admission, so nothing ran YET.
        /// The rescan latch was re-armed with the caller's intent, so the leader runs it once admission frees
        /// up. Not a failure: no error was logged and there is no extract error to look for.
        /// </summary>
        Queued,
    }

    /// <summary>This instance is not the writer-lock leader, so no scan was performed.</summary>
    public static ScanOutcome NotLeader { get; } = new(Kind.NotLeader, Report: null);

    /// <summary>The leader's scan failed (the extract subprocess threw); the prior index is kept.</summary>
    public static ScanOutcome Failed { get; } = new(Kind.Failed, Report: null);

    /// <summary>
    /// Machine-wide scan admission was busy; the scan is latched and will run.
    /// <paramref name="holderDescription"/> names the observed holder.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="holderDescription"/> is null.</exception>
    public static ScanOutcome Queued(string holderDescription)
    {
        ArgumentNullException.ThrowIfNull(holderDescription);
        return new ScanOutcome(Kind.Queued, Report: null, holderDescription);
    }

    /// <summary>The leader scanned successfully, carrying julie's <paramref name="report"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    public static ScanOutcome Scanned(ExtractReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new ScanOutcome(Kind.Scanned, report);
    }

    /// <summary>
    /// The leader ran a DELTA where a from-scratch rebuild was requested, carrying that delta's
    /// <paramref name="report"/> and the <paramref name="reason"/> a caller renders. The rebuild remains owed.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> or <paramref name="reason"/> is null.</exception>
    public static ScanOutcome Downgraded(ExtractReport report, string reason)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(reason);
        return new ScanOutcome(Kind.Downgraded, report, HolderDescription: null, reason);
    }
}
