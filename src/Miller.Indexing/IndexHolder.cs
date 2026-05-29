namespace Miller.Indexing;

/// <summary>
/// The single seam the read tools (search/inspect/resolver) depend on so the in-memory index can be replaced
/// behind live readers (decision-5). It holds a <see cref="MillerRepositoryIndex"/> — a frozen, immutable index
/// — paired with the julie <c>canonical_revisions</c> revision it was built from. The freshness service rebuilds
/// a new index from a fresh read and publishes it with <see cref="Swap"/>; tools read <see cref="Current"/>
/// per call.
///
/// <para><b>Why lock-free is correct here.</b> The index is immutable once built, so publishing a new one is a
/// single reference swap — there is no torn intermediate state, and an in-flight read that captured the old
/// reference keeps a fully consistent old snapshot. This structurally satisfies the symbol-ID-churn rule: the
/// whole resolved index is replaced atomically, so no stale link keyed on a churned id can survive a swap.</para>
///
/// <para>The index and its revision are stored together in one immutable <see cref="Snapshot"/> behind a single
/// <c>volatile</c> reference, so the pair never tears: a reader can never observe a new index with the old
/// revision (or vice versa). Safe for any number of concurrent readers with a single publisher.</para>
/// </summary>
public sealed class IndexHolder
{
    /// <summary>An immutable (index, revision) pair — the unit that is published atomically.</summary>
    private sealed record IndexState(MillerRepositoryIndex Index, long Revision);

    // The single volatile reference. A swap replaces it wholesale; a read takes it once for a consistent pair.
    private volatile IndexState _snapshot;

    /// <summary>
    /// Seed the holder with the initial index and the revision it was built from (the bootstrap scan's revision).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is null.</exception>
    public IndexHolder(MillerRepositoryIndex index, long builtRevision)
    {
        ArgumentNullException.ThrowIfNull(index);
        _snapshot = new IndexState(index, builtRevision);
    }

    /// <summary>The current frozen index. Read per tool call; never null.</summary>
    public MillerRepositoryIndex Current => _snapshot.Index;

    /// <summary>The revision the <see cref="Current"/> index was built from (its <c>canonical_revisions</c> cursor).</summary>
    public long BuiltRevision => _snapshot.Revision;

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
    /// Atomically publish a freshly built index and the revision it was built from. In-flight reads keep their
    /// prior snapshot; subsequent reads see the new one. Last swap wins.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is null.</exception>
    public void Swap(MillerRepositoryIndex next, long revision)
    {
        ArgumentNullException.ThrowIfNull(next);
        _snapshot = new IndexState(next, revision);
    }
}
