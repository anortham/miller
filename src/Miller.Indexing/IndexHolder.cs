namespace Miller.Indexing;

/// <summary>
/// Atomically publishes repository generations with eager metadata. A generation may defer repository
/// materialization until <see cref="Current"/> is explicitly requested.
/// </summary>
public sealed class IndexHolder
{
    private sealed record IndexState(
        Lazy<MillerRepositoryIndex> Index,
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

    /// <summary>The current frozen index. Read per tool call; never null.</summary>
    public MillerRepositoryIndex Current => _snapshot.Index.Value;

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
        return (snapshot.Index.Value, snapshot.Revision);
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

    private static IndexState CreateEagerState(
        MillerRepositoryIndex index,
        long revision,
        string? artifactId) =>
        new(
            new Lazy<MillerRepositoryIndex>(() => index, LazyThreadSafetyMode.ExecutionAndPublication),
            revision,
            artifactId,
            index.DocumentCount,
            index.KnownExtensions.Count);

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
            new Lazy<MillerRepositoryIndex>(indexFactory, LazyThreadSafetyMode.ExecutionAndPublication),
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
