using System.Diagnostics;
using Miller.Core.Graph;
using Miller.Core.Search;
using Miller.Indexing;

namespace Miller.Server.Workspaces;

internal readonly record struct ReadMeasurementSnapshot(long CallCount, long ElapsedTicks);

internal sealed class ReadPhaseTelemetry
{
    private readonly MeasuredSymbolLookupIndex _lookup;
    private readonly ReadMeasurementSnapshot _lookupBaseline;
    private readonly MeasuredSymbolGraphReachability? _graph;
    private readonly ReadMeasurementSnapshot _graphBaseline;

    public ReadPhaseTelemetry(
        MeasuredSymbolLookupIndex lookup,
        MeasuredSymbolGraphReachability? graph,
        int providerCacheEntries)
    {
        _lookup = lookup;
        _lookupBaseline = lookup.Snapshot();
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

    private static ReadMeasurementSnapshot Delta(
        ReadMeasurementSnapshot current,
        ReadMeasurementSnapshot baseline) =>
        new(
            Math.Max(0, current.CallCount - baseline.CallCount),
            Math.Max(0, current.ElapsedTicks - baseline.ElapsedTicks));

    private static long ElapsedMilliseconds(long ticks) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds);
}

internal sealed class MeasuredSymbolLookupIndex(ISymbolLookupIndex inner) : ISymbolLookupIndex
{
    private long _elapsedTicks;
    private long _callCount;

    public int DocumentCount => Measure(() => inner.DocumentCount);

    public IReadOnlySet<string> KnownExtensions => Measure(() => inner.KnownExtensions);

    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
        Measure(() => inner.Search(query, limit, mode));

    public IndexedSymbol Resolve(int docId) => Measure(() => inner.Resolve(docId));

    public IReadOnlyList<IndexedSymbol> FindByName(string name) => Measure(() => inner.FindByName(name));

    public IndexedSymbol? FindBySymbolId(string symbolId) => Measure(() => inner.FindBySymbolId(symbolId));

    public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) => Measure(() => inner.FindChildren(parentId));

    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) => Measure(() => inner.FindByFilePath(filePath));

    public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
        Measure(() => inner.FindByFilePathFragment(query, limit));

    public IReadOnlyList<string> FindFilePathsByFragment(string query, int limit) =>
        Measure(() => inner.FindFilePathsByFragment(query, limit));

    public bool IsIndexedFilePath(string path) => Measure(() => inner.IsIndexedFilePath(path));

    public string? ResolveIndexedFilePath(string target) => Measure(() => inner.ResolveIndexedFilePath(target));

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
