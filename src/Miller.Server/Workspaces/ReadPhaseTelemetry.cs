using System.Diagnostics;
using Miller.Core.Graph;
using Miller.Core.Search;
using Miller.Indexing;

namespace Miller.Server.Workspaces;

internal readonly record struct ReadMeasurementSnapshot(long CallCount, long ElapsedTicks);

internal enum SymbolLookupMethodFamily
{
    DocumentCount,
    KnownExtensions,
    Search,
    ResolveDoc,
    FindByName,
    FindBySymbolId,
    FindChildren,
    FindByFilePath,
    FindByFilePathFragment,
    FindFilePathsByFragment,
    IsIndexedFilePath,
    ResolveIndexedFilePath,
}

internal enum ContextLookupPhase
{
    QueryRetrieval,
    TermRetrieval,
    AnchorResolution,
    GraphReach,
    SymbolHydration,
    FileNeighbours,
    CandidateOrdering,
}

internal readonly record struct LookupMethodTelemetry(long CallCount, long ElapsedMilliseconds);

internal sealed record SymbolLookupTelemetrySnapshot(
    LookupMethodTelemetry DocumentCount,
    LookupMethodTelemetry KnownExtensions,
    LookupMethodTelemetry Search,
    LookupMethodTelemetry ResolveDoc,
    LookupMethodTelemetry FindByName,
    LookupMethodTelemetry FindBySymbolId,
    LookupMethodTelemetry FindChildren,
    LookupMethodTelemetry FindByFilePath,
    LookupMethodTelemetry FindByFilePathFragment,
    LookupMethodTelemetry FindFilePathsByFragment,
    LookupMethodTelemetry IsIndexedFilePath,
    LookupMethodTelemetry ResolveIndexedFilePath)
{
    internal long TotalCallCount =>
        DocumentCount.CallCount +
        KnownExtensions.CallCount +
        Search.CallCount +
        ResolveDoc.CallCount +
        FindByName.CallCount +
        FindBySymbolId.CallCount +
        FindChildren.CallCount +
        FindByFilePath.CallCount +
        FindByFilePathFragment.CallCount +
        FindFilePathsByFragment.CallCount +
        IsIndexedFilePath.CallCount +
        ResolveIndexedFilePath.CallCount;
}

internal sealed record ContextLookupPhaseObservation(
    ContextLookupPhase Phase,
    SymbolLookupTelemetrySnapshot Delta,
    SymbolLookupTelemetrySnapshot Total);

internal sealed class ReadPhaseTelemetry
{
    private readonly MeasuredSymbolLookupIndex _lookup;
    private readonly ReadMeasurementSnapshot _lookupBaseline;
    private readonly ReadMeasurementSnapshot[] _lookupFamilyBaseline;
    private ReadMeasurementSnapshot[] _lookupPhaseBaseline;
    private readonly MeasuredSymbolGraphReachability? _graph;
    private readonly ReadMeasurementSnapshot _graphBaseline;

    public ReadPhaseTelemetry(
        MeasuredSymbolLookupIndex lookup,
        MeasuredSymbolGraphReachability? graph,
        int providerCacheEntries)
    {
        _lookup = lookup;
        _lookupBaseline = lookup.Snapshot();
        _lookupFamilyBaseline = lookup.SnapshotByFamily();
        _lookupPhaseBaseline = _lookupFamilyBaseline;
        _graph = graph;
        _graphBaseline = graph?.Snapshot() ?? default;
        ProviderCacheEntries = providerCacheEntries;
    }

    public long ResolveElapsedMilliseconds { get; private set; }

    public int ProviderCacheEntries { get; }

    public long LookupCallCount => Delta(_lookup.Snapshot(), _lookupBaseline).CallCount;

    public long LookupElapsedMilliseconds =>
        ElapsedMilliseconds(Delta(_lookup.Snapshot(), _lookupBaseline).ElapsedTicks);

    public long GraphCallCount =>
        _graph is null ? 0 : Delta(_graph.Snapshot(), _graphBaseline).CallCount;

    public long GraphElapsedMilliseconds =>
        _graph is null ? 0 : ElapsedMilliseconds(Delta(_graph.Snapshot(), _graphBaseline).ElapsedTicks);

    public void CompleteResolve(long startedAt) =>
        ResolveElapsedMilliseconds = Math.Max(
            0,
            (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

    internal ContextLookupPhaseObservation CompleteLookupPhase(ContextLookupPhase phase)
    {
        ReadMeasurementSnapshot[] current = _lookup.SnapshotByFamily();
        var observation = new ContextLookupPhaseObservation(
            phase,
            LookupTelemetry(current, _lookupPhaseBaseline),
            LookupTelemetry(current, _lookupFamilyBaseline));
        _lookupPhaseBaseline = current;
        return observation;
    }

    private static ReadMeasurementSnapshot Delta(
        ReadMeasurementSnapshot current,
        ReadMeasurementSnapshot baseline) =>
        new(
            Math.Max(0, current.CallCount - baseline.CallCount),
            Math.Max(0, current.ElapsedTicks - baseline.ElapsedTicks));

    private static long ElapsedMilliseconds(long ticks) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds);

    private static SymbolLookupTelemetrySnapshot LookupTelemetry(
        IReadOnlyList<ReadMeasurementSnapshot> current,
        IReadOnlyList<ReadMeasurementSnapshot> baseline)
    {
        LookupMethodTelemetry Family(SymbolLookupMethodFamily family)
        {
            int index = (int)family;
            ReadMeasurementSnapshot delta = Delta(current[index], baseline[index]);
            return new LookupMethodTelemetry(delta.CallCount, ElapsedMilliseconds(delta.ElapsedTicks));
        }

        return new SymbolLookupTelemetrySnapshot(
            Family(SymbolLookupMethodFamily.DocumentCount),
            Family(SymbolLookupMethodFamily.KnownExtensions),
            Family(SymbolLookupMethodFamily.Search),
            Family(SymbolLookupMethodFamily.ResolveDoc),
            Family(SymbolLookupMethodFamily.FindByName),
            Family(SymbolLookupMethodFamily.FindBySymbolId),
            Family(SymbolLookupMethodFamily.FindChildren),
            Family(SymbolLookupMethodFamily.FindByFilePath),
            Family(SymbolLookupMethodFamily.FindByFilePathFragment),
            Family(SymbolLookupMethodFamily.FindFilePathsByFragment),
            Family(SymbolLookupMethodFamily.IsIndexedFilePath),
            Family(SymbolLookupMethodFamily.ResolveIndexedFilePath));
    }
}

internal sealed class MeasuredSymbolLookupIndex(ISymbolLookupIndex inner) : ISymbolLookupIndex
{
    private readonly long[] _elapsedTicks = new long[Enum.GetValues<SymbolLookupMethodFamily>().Length];
    private readonly long[] _callCounts = new long[Enum.GetValues<SymbolLookupMethodFamily>().Length];

    public int DocumentCount => Measure(SymbolLookupMethodFamily.DocumentCount, () => inner.DocumentCount);

    public IReadOnlySet<string> KnownExtensions =>
        Measure(SymbolLookupMethodFamily.KnownExtensions, () => inner.KnownExtensions);

    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
        Measure(SymbolLookupMethodFamily.Search, () => inner.Search(query, limit, mode));

    public IndexedSymbol Resolve(int docId) => Measure(SymbolLookupMethodFamily.ResolveDoc, () => inner.Resolve(docId));

    public IReadOnlyList<IndexedSymbol> FindByName(string name) =>
        Measure(SymbolLookupMethodFamily.FindByName, () => inner.FindByName(name));

    public IndexedSymbol? FindBySymbolId(string symbolId) =>
        Measure(SymbolLookupMethodFamily.FindBySymbolId, () => inner.FindBySymbolId(symbolId));

    public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) =>
        Measure(SymbolLookupMethodFamily.FindChildren, () => inner.FindChildren(parentId));

    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) =>
        Measure(SymbolLookupMethodFamily.FindByFilePath, () => inner.FindByFilePath(filePath));

    public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
        Measure(SymbolLookupMethodFamily.FindByFilePathFragment, () => inner.FindByFilePathFragment(query, limit));

    public IReadOnlyList<string> FindFilePathsByFragment(string query, int limit) =>
        Measure(SymbolLookupMethodFamily.FindFilePathsByFragment, () => inner.FindFilePathsByFragment(query, limit));

    public bool IsIndexedFilePath(string path) =>
        Measure(SymbolLookupMethodFamily.IsIndexedFilePath, () => inner.IsIndexedFilePath(path));

    public string? ResolveIndexedFilePath(string target) =>
        Measure(SymbolLookupMethodFamily.ResolveIndexedFilePath, () => inner.ResolveIndexedFilePath(target));

    public ReadMeasurementSnapshot Snapshot()
    {
        ReadMeasurementSnapshot[] families = SnapshotByFamily();
        return new(
            families.Sum(static family => family.CallCount),
            families.Sum(static family => family.ElapsedTicks));
    }

    internal ReadMeasurementSnapshot[] SnapshotByFamily()
    {
        var snapshot = new ReadMeasurementSnapshot[_callCounts.Length];
        for (int index = 0; index < snapshot.Length; index++)
        {
            snapshot[index] = new ReadMeasurementSnapshot(
                Interlocked.Read(ref _callCounts[index]),
                Interlocked.Read(ref _elapsedTicks[index]));
        }
        return snapshot;
    }

    private T Measure<T>(SymbolLookupMethodFamily family, Func<T> action)
    {
        int index = (int)family;
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            return action();
        }
        finally
        {
            Interlocked.Increment(ref _callCounts[index]);
            Interlocked.Add(ref _elapsedTicks[index], Stopwatch.GetTimestamp() - startedAt);
        }
    }
}

internal sealed class MeasuredSymbolGraphReachability : ISymbolGraphReachability
{
    private readonly ISymbolGraphReachability _inner;
    private long _elapsedTicks;
    private long _callCount;

    internal MeasuredSymbolGraphReachability(
        ISymbolGraphReachability inner,
        Action<GraphStatementObservation>? statementObserver = null)
    {
        _inner = inner;
        if (inner is SqliteSymbolGraphIndex sqlite)
            sqlite.StatementObserver = statementObserver;
    }

    public GraphReachResult ReachWithEvidence(
        IEnumerable<string> starts,
        int maxDepth,
        int limit,
        Direction dir) => Measure(() => _inner.ReachWithEvidence(starts, maxDepth, limit, dir));

    public IReadOnlyList<ReachedNode> Reach(
        IEnumerable<string> starts,
        int maxDepth,
        int limit,
        Direction dir) => Measure(() => _inner.Reach(starts, maxDepth, limit, dir));

    public IReadOnlyList<string>? ShortestPath(string from, string to, int maxDepth) =>
        Measure(() => _inner.ShortestPath(from, to, maxDepth));

    public GraphPath? ShortestPathWithEvidence(
        string from,
        string to,
        int maxDepth,
        Func<GraphNeighbour, bool> edgeFilter) =>
        Measure(() => _inner.ShortestPathWithEvidence(from, to, maxDepth, edgeFilter));

    public ReadMeasurementSnapshot Snapshot() =>
        new(Interlocked.Read(ref _callCount), Interlocked.Read(ref _elapsedTicks));

    private T Measure<T>(Func<T> action)
    {
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            return action();
        }
        finally
        {
            Interlocked.Increment(ref _callCount);
            Interlocked.Add(ref _elapsedTicks, Stopwatch.GetTimestamp() - startedAt);
        }
    }
}
