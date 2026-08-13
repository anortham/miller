namespace Miller.Indexing.Reads;

/// <summary>
/// The single definition of the store-log cursor (the value Miller reports as <c>revision</c> and gates
/// sidecar freshness on).
///
/// <para><b>Why this is shared and not duplicated (load-bearing).</b> Two readers need this cursor —
/// <see cref="FamilyStoreReadSession"/> for the served snapshot and <c>RevisionDeltaReader</c> for delta
/// convergence — and <c>StoreSidecarStamp</c> compares one against the other. When the two carried
/// byte-identical copies of the SQL they were free to drift, and a drift means a sidecar is judged stale
/// against a cursor its writer never saw. Keep exactly one definition here.</para>
///
/// <para><b>Why non-terminal progress chunks are excluded.</b> A whole-repo <c>store import</c> emits a
/// <c>store_import_l1_chunk</c>/<c>store_import_l3_chunk</c> row per commit batch, each with
/// <c>view_id</c> NULL, <c>version_id</c> NULL and <c>terminal</c> 0 — 1,971 such rows against 20 real
/// events on the Miller workspace itself (2026-08-12 triage). Counting them made the cursor advance
/// roughly twice a second <i>during</i> an import that changed nothing, which made every reader swap its
/// index view ~145 ms at a time for no benefit, and left the derived sidecars chasing a target that moved
/// again before they could stamp it — a treadmill they could not win
/// (<c>content.db</c> observed converging to the previous poll's "expected" value, forever).
/// A chunk is in-flight progress, not a durable state transition, so it must not move the cursor.</para>
///
/// <para><b>Do not simplify this to <c>terminal = 1</c>.</b> <c>store_import_l1_published</c> and
/// <c>store_update_l1_published</c> are <c>terminal = 0</c> and ARE meaningful — the L1 publish is the
/// manifest flip that lets bootstrap serve. Dropping them would hide a real advance. The
/// <c>terminal = 1</c> disjunct is kept ahead of the name test so a future terminal chunk kind still
/// counts.</para>
/// </summary>
internal static class StoreLogCursor
{
    /// <summary>
    /// Matches the store-log rows that are NOT view- or version-scoped but still represent a durable store
    /// transition. Written against the alias <c>log</c>.
    /// </summary>
    internal const string GlobalEventPredicate =
        "(log.view_id IS NULL AND log.version_id IS NULL "
        + "AND (log.terminal = 1 OR log.event_kind NOT LIKE '%\\_chunk' ESCAPE '\\'))";

    /// <summary>
    /// The cursor query. Requires <c>$view_id</c> and <c>$generation</c> parameters and returns 0 on an
    /// empty log.
    /// </summary>
    internal const string MaxSequenceSql =
        $"""
        SELECT COALESCE(MAX(log.sequence),0)
        FROM store_log AS log
        WHERE log.view_id=$view_id
           OR {GlobalEventPredicate}
           OR EXISTS (
                SELECT 1
                FROM manifest_entries AS entry
                WHERE entry.view_id=$view_id
                  AND entry.generation=$generation
                  AND entry.version_id=log.version_id)
        """;
}
