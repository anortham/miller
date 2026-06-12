namespace Miller.Indexing;

/// <summary>
/// The single seam the read tools (search/inspect/resolver) depend on so the in-memory index can be replaced
/// behind live readers (decision-5). It holds a <see cref="MillerRepositoryIndex"/> — a frozen, immutable index
/// — paired with the julie <c>extraction_revisions</c> revision it was built from and the artifact's
/// <c>artifact_metadata.artifact_id</c> identity. The freshness service rebuilds a new index from a fresh read
/// and publishes it with <see cref="Swap"/>; tools read <see cref="Current"/> per call.
///
/// <para><b>Why lock-free is correct here.</b> The index is immutable once built, so publishing a new one is a
/// single reference swap — there is no torn intermediate state, and an in-flight read that captured the old
/// reference keeps a fully consistent old snapshot. This structurally satisfies the symbol-ID-churn rule: the
/// whole resolved index is replaced atomically, so no stale link keyed on a churned id can survive a swap.</para>
///
/// <para>The index, its revision, and its artifact identity are stored together in one immutable snapshot
/// behind a single <c>volatile</c> reference, so the triple never tears: a reader can never observe a new index
/// with the old revision (or vice versa). Safe for any number of concurrent readers with a single publisher.</para>
///
/// <para><b>Why the artifact id matters.</b> A full (force) rebuild promotes a FRESH file over
/// <c>symbols.db</c> (see <see cref="FullRebuildPromotion"/>), restarting julie's revision counter — the
/// rebuilt artifact's latest revision can land at or below the held one, so a revision-only comparison would
/// keep serving the pre-rebuild index forever. The artifact id changes with every fresh artifact and breaks
/// that tie (the 2026-06-11 Eros fleet finding's cross-process file-stamp fix, applied to the in-process
/// holder). Null means "unknown" (a synthetic/static extract); unknown never forces a swap.</para>
/// </summary>
public sealed class IndexHolder
{
    /// <summary>An immutable (index, revision, artifact id) triple — the unit that is published atomically.</summary>
    private sealed record IndexState(MillerRepositoryIndex Index, long Revision, string? ArtifactId);

    // The single volatile reference. A swap replaces it wholesale; a read takes it once for a consistent triple.
    private volatile IndexState _snapshot;

    /// <summary>
    /// Seed the holder with the initial index, the revision it was built from (the bootstrap scan's revision),
    /// and the artifact identity of the DB it was loaded from (null when unknown).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is null.</exception>
    public IndexHolder(MillerRepositoryIndex index, long builtRevision, string? builtArtifactId = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        _snapshot = new IndexState(index, builtRevision, builtArtifactId);
    }

    /// <summary>The current frozen index. Read per tool call; never null.</summary>
    public MillerRepositoryIndex Current => _snapshot.Index;

    /// <summary>The revision the <see cref="Current"/> index was built from (its <c>extraction_revisions</c> cursor).</summary>
    public long BuiltRevision => _snapshot.Revision;

    /// <summary>The <c>artifact_metadata.artifact_id</c> of the DB the <see cref="Current"/> index was built
    /// from, or null when unknown. A polled id that differs from this one means the artifact file was REPLACED
    /// (a full rebuild), regardless of where its restarted revision counter landed.</summary>
    public string? BuiltArtifactId => _snapshot.ArtifactId;

    /// <summary>
    /// Read the index and its built revision as one consistent pair (a single volatile read). Use this anywhere
    /// both values are needed together (e.g. computing <c>index_fresh</c>) so a concurrent <see cref="Swap"/>
    /// can never be observed as a half-applied pair.
    /// </summary>
    public (MillerRepositoryIndex Index, long Revision) Snapshot()
    {
        var snapshot = _snapshot;
        return (snapshot.Index, snapshot.Revision);
    }

    /// <summary>
    /// Atomically publish a freshly built index, the revision it was built from, and the artifact identity of
    /// the DB it was loaded from (null when unknown). In-flight reads keep their prior snapshot; subsequent
    /// reads see the new one. Last swap wins.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is null.</exception>
    public void Swap(MillerRepositoryIndex next, long revision, string? artifactId = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        _snapshot = new IndexState(next, revision, artifactId);
    }
}
