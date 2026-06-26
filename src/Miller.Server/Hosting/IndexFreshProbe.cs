using Miller.Indexing;

namespace Miller.Server.Hosting;

/// <summary>
/// The service the telemetry filter reads to populate the coarse <c>index_fresh</c> column (decision-8). It
/// combines the two halves of freshness via <see cref="FreshnessState"/>: the held index's built revision vs.
/// the latest persisted revision (the dominant term, from the freshness reader) AND the indexer's queue-empty
/// state (no observed-but-unapplied events). Both inputs are injected suppliers so the probe is unit-tested
/// without SQLite/timer, and so it works in every instance — a reader (no indexer) supplies a constant
/// queue-empty=true, the leader supplies its live queue count.
///
/// <para><b>Honesty contract.</b> A transient revision-read failure (a momentarily-locked WAL DB) yields
/// <c>null</c> — "not measured" — never a fabricated true/false. <c>index_fresh</c> may legitimately be unknown.</para>
/// </summary>
public sealed class IndexFreshProbe
{
    private readonly IndexHolder _holder;
    private readonly Func<long> _latestRevision;
    private readonly Func<bool> _queueEmpty;

    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public IndexFreshProbe(IndexHolder holder, Func<long> latestRevision, Func<bool> queueEmpty)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(latestRevision);
        ArgumentNullException.ThrowIfNull(queueEmpty);
        _holder = holder;
        _latestRevision = latestRevision;
        _queueEmpty = queueEmpty;
    }

    /// <summary>
    /// Compute the coarse <c>index_fresh</c> boolean, or null when the revision read fails (unknown). Cheap —
    /// one volatile holder read, one revision query, one queue-count read.
    /// </summary>
    public bool? Compute()
    {
        try
        {
            long latest = _latestRevision();
            return FreshnessState.Compute(_holder.BuiltRevision, latest, _queueEmpty());
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }
}
