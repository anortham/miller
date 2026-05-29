namespace Miller.Server.Hosting;

/// <summary>
/// The pure computation behind the coarse <c>index_fresh</c> telemetry signal (m3-design decision-8). The
/// in-memory index served to a tool call is "fresh" iff:
/// <list type="number">
/// <item>its built revision equals the latest persisted <c>canonical_revisions</c> revision (this instance has
///   rebuilt up to the writer's most recent commit), AND</item>
/// <item>the indexer's coalescing queue is empty (no observed-but-not-yet-applied file event is pending).</item>
/// </list>
/// Both are required: a matching revision with pending events means an edit was seen but not yet written; an
/// empty queue against a newer revision means the writer advanced and this reader has not rebuilt. Cheap — no
/// hot-path I/O; the caller supplies the two longs and the boolean it already has.
/// </summary>
public static class FreshnessState
{
    /// <summary>
    /// Compute the coarse freshness boolean. See the type summary for the rule.
    /// </summary>
    /// <param name="builtRevision">The revision the currently-held index was built from.</param>
    /// <param name="latestRevision">The latest persisted revision for the workspace (0 = none yet).</param>
    /// <param name="queueEmpty">Whether the indexer's event queue currently holds no pending events.</param>
    public static bool Compute(long builtRevision, long latestRevision, bool queueEmpty) =>
        builtRevision == latestRevision && queueEmpty;
}
