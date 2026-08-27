namespace Miller.Indexing;

/// <summary>
/// Atomically publishes repository generations with eager metadata. A generation may defer repository
/// materialization until <see cref="Current"/> is explicitly requested.
/// </summary>
public sealed class IndexHolder
{
    private sealed record IndexState(
        Lazy<MillerRepositoryIndex> Index,
        Func<MillerRepositoryIndex> Factory,
        long Revision,
        string? ArtifactId,
        int DocumentCount,
        int KnownExtensionsCount);

    private volatile IndexState _snapshot;

    /// <summary>
    /// Seed the holder with the initial index, the revision it was built from (the bootstrap scan's revision),
    /// and the artifact identity of the DB it was loaded from (null when unknown).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is null.</exception>
    public IndexHolder(MillerRepositoryIndex index, long builtRevision, string? builtArtifactId = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        _snapshot = CreateEagerState(index, builtRevision, builtArtifactId);
    }

    /// <summary>Seed a generation whose metadata is available before its repository is materialized.</summary>
    public IndexHolder(
        Func<MillerRepositoryIndex> indexFactory,
        long builtRevision,
        int documentCount,
        int knownExtensionsCount,
        string? builtArtifactId = null)
    {
        _snapshot = CreateLazyState(
            indexFactory,
            builtRevision,
            builtArtifactId,
            documentCount,
            knownExtensionsCount);
    }

    /// <summary>The current frozen index. Read per tool call; never null. A lazy factory that throws is
    /// discarded rather than memoized, so the next read re-runs the factory instead of replaying one
    /// race's exception until the next swap.</summary>
    public MillerRepositoryIndex Current => Materialize(_snapshot);

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
        return (Materialize(snapshot), snapshot.Revision);
    }

    /// <summary>Read generation metadata without materializing the repository.</summary>
    public IndexHolderMetadata MetadataSnapshot()
    {
        var snapshot = _snapshot;
        return new IndexHolderMetadata(
            snapshot.Revision,
            snapshot.ArtifactId,
            snapshot.DocumentCount,
            snapshot.KnownExtensionsCount);
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
        _snapshot = CreateEagerState(next, revision, artifactId);
    }

    /// <summary>Atomically publish a generation without evaluating its repository factory.</summary>
    public void SwapLazy(
        Func<MillerRepositoryIndex> indexFactory,
        long revision,
        int documentCount,
        int knownExtensionsCount,
        string? artifactId = null)
    {
        _snapshot = CreateLazyState(
            indexFactory,
            revision,
            artifactId,
            documentCount,
            knownExtensionsCount);
    }

    private MillerRepositoryIndex Materialize(IndexState state)
    {
        try
        {
            return state.Index.Value;
        }
        catch
        {
            DiscardFaultedState(state);
            throw;
        }
    }

    // Lazy<T> under ExecutionAndPublication memoizes a factory exception, so one transient race would
    // replay it on every read until the next swap. Replacing the faulted state (only while it is still
    // the published one — a concurrent Swap/SwapLazy always wins) lets the next read re-run the factory.
    // CompareExchange, not lock-check-assign: Swap writes _snapshot without a lock, so a check-then-assign
    // window could clobber a swap that landed between the two.
    private void DiscardFaultedState(IndexState faulted)
    {
        IndexState replacement = faulted with { Index = NewLazy(faulted.Factory) };
#pragma warning disable CS0420 // Interlocked treats the volatile field correctly by definition.
        Interlocked.CompareExchange(ref _snapshot, replacement, faulted);
#pragma warning restore CS0420
    }

    private static Lazy<MillerRepositoryIndex> NewLazy(Func<MillerRepositoryIndex> factory) =>
        new(factory, LazyThreadSafetyMode.ExecutionAndPublication);

    private static IndexState CreateEagerState(
        MillerRepositoryIndex index,
        long revision,
        string? artifactId)
    {
        Func<MillerRepositoryIndex> factory = () => index;
        return new IndexState(
            NewLazy(factory),
            factory,
            revision,
            artifactId,
            index.DocumentCount,
            index.KnownExtensions.Count);
    }

    private static IndexState CreateLazyState(
        Func<MillerRepositoryIndex> indexFactory,
        long revision,
        string? artifactId,
        int documentCount,
        int knownExtensionsCount)
    {
        ArgumentNullException.ThrowIfNull(indexFactory);
        ArgumentOutOfRangeException.ThrowIfNegative(documentCount);
        ArgumentOutOfRangeException.ThrowIfNegative(knownExtensionsCount);
        return new IndexState(
            NewLazy(indexFactory),
            indexFactory,
            revision,
            artifactId,
            documentCount,
            knownExtensionsCount);
    }
}

public readonly record struct IndexHolderMetadata(
    long Revision,
    string? ArtifactId,
    int DocumentCount,
    int KnownExtensionsCount);
