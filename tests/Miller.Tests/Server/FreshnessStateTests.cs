using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the coarse <c>index_fresh</c> boolean (decision-8): the in-memory index is fresh iff its built
/// revision equals the latest persisted revision AND the indexer's coalescing queue is empty. Both conditions
/// are required — a matching revision with pending un-drained events is NOT fresh (an external edit has been
/// observed but not yet applied), and an empty queue against a newer revision is NOT fresh (the writer moved
/// ahead and this instance has not rebuilt yet). Pure truth-table logic, no I/O.
/// </summary>
public sealed class FreshnessStateTests
{
    [Theory]
    // builtRevision, latestRevision, queueEmpty -> fresh
    [InlineData(5, 5, true, true)]    // equal revision + empty queue => fresh
    [InlineData(5, 6, true, false)]   // writer ahead, not rebuilt yet => stale
    [InlineData(5, 5, false, false)]  // equal revision but pending events => stale
    [InlineData(5, 6, false, false)]  // behind AND pending => stale
    [InlineData(0, 0, true, true)]    // no revision yet, nothing queued => fresh (vacuously)
    [InlineData(7, 4, true, false)]   // built ahead of latest (defensive) => not equal => stale
    public void Compute_MatchesTruthTable(long built, long latest, bool queueEmpty, bool expectedFresh)
    {
        Assert.Equal(expectedFresh, FreshnessState.Compute(built, latest, queueEmpty));
    }
}
