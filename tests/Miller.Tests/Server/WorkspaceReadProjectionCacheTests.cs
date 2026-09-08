using Miller.Core.Graph;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

public sealed class WorkspaceReadProjectionCacheTests
{
    private sealed class DummySymbolIndex : ISymbolLookupIndex
    {
        public string Name { get; }
        public DummySymbolIndex(string name) => Name = name;
        public int DocumentCount => 0;
        public IReadOnlySet<string> KnownExtensions => new HashSet<string>();
        public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) => [];
        public IndexedSymbol Resolve(int docId) => throw new NotImplementedException();
        public IReadOnlyList<IndexedSymbol> FindByName(string name) => [];
        public IndexedSymbol? FindBySymbolId(string symbolId) => null;
        public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) => [];
        public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) => [];
        public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) => [];
        public bool IsIndexedFilePath(string path) => true;
        public string? ResolveIndexedFilePath(string path) => path;
    }

    [Fact]
    public async Task SameRevisionConcurrentReaders_ShareOneImmutableConstruction()
    {
        var cache = new WorkspaceReadProjectionCache();
        var key = new WorkspaceReadProjectionKey("ws1", "gen1", "view1", 1, "symbol");
        int loadCount = 0;

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            return cache.GetOrAddSymbolIndex(key, () =>
            {
                Interlocked.Increment(ref loadCount);
                Thread.Sleep(20);
                return new DummySymbolIndex("shared");
            });
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, loadCount);
        foreach (var res in results)
        {
            Assert.Equal("shared", ((DummySymbolIndex)res).Name);
            Assert.Same(results[0], res);
        }
    }

    [Fact]
    public void RevisionAdvance_RebuildsAndEvictsSuperseded()
    {
        var cache = new WorkspaceReadProjectionCache();
        var keyRev1 = new WorkspaceReadProjectionKey("ws1", "gen1", "view1", 1, "symbol");
        var keyRev2 = new WorkspaceReadProjectionKey("ws1", "gen1", "view1", 2, "symbol");

        var index1 = cache.GetOrAddSymbolIndex(keyRev1, () => new DummySymbolIndex("rev1"));
        Assert.Equal(1, cache.Count);
        Assert.Equal("rev1", ((DummySymbolIndex)index1).Name);

        var index2 = cache.GetOrAddSymbolIndex(keyRev2, () => new DummySymbolIndex("rev2"));
        // Old revision for ws1 was superseded and evicted
        Assert.Equal(1, cache.Count);
        Assert.Equal("rev2", ((DummySymbolIndex)index2).Name);
        Assert.NotSame(index1, index2);
    }

    [Fact]
    public void GenerationChange_RebuildsAndEvictsSuperseded()
    {
        var cache = new WorkspaceReadProjectionCache();
        var keyGen1 = new WorkspaceReadProjectionKey("ws1", "gen1", "view1", 1, "symbol");
        var keyGen2 = new WorkspaceReadProjectionKey("ws1", "gen2", "view1", 1, "symbol");

        var index1 = cache.GetOrAddSymbolIndex(keyGen1, () => new DummySymbolIndex("gen1"));
        Assert.Equal(1, cache.Count);

        var index2 = cache.GetOrAddSymbolIndex(keyGen2, () => new DummySymbolIndex("gen2"));
        Assert.Equal(1, cache.Count);
        Assert.Equal("gen2", ((DummySymbolIndex)index2).Name);
        Assert.NotSame(index1, index2);
    }

    [Fact]
    public void FailedLoad_PurgesImmediatelyAndAllowsRetry()
    {
        var cache = new WorkspaceReadProjectionCache();
        var key = new WorkspaceReadProjectionKey("ws1", "gen1", "view1", 1, "symbol");
        bool shouldFail = true;

        Assert.Throws<InvalidOperationException>(() =>
        {
            cache.GetOrAddSymbolIndex(key, () =>
            {
                if (shouldFail)
                    throw new InvalidOperationException("load exploded");
                return new DummySymbolIndex("success");
            });
        });

        // Failed load must not remain cached
        Assert.Equal(0, cache.Count);

        // Next call should succeed and not re-throw the cached exception
        shouldFail = false;
        var index = cache.GetOrAddSymbolIndex(key, () => new DummySymbolIndex("success"));
        Assert.Equal(1, cache.Count);
        Assert.Equal("success", ((DummySymbolIndex)index).Name);
    }

    [Fact]
    public void EvictWorkspace_RemovesAllEntriesForThatWorkspace()
    {
        var cache = new WorkspaceReadProjectionCache();
        var keyWs1Sym = new WorkspaceReadProjectionKey("ws1", "gen1", "view1", 1, "symbol");
        var keyWs1Bridge = new WorkspaceReadProjectionKey("ws1", "gen1", "view1", 1, "bridge");
        var keyWs2Sym = new WorkspaceReadProjectionKey("ws2", "gen1", "view1", 1, "symbol");

        cache.GetOrAddSymbolIndex(keyWs1Sym, () => new DummySymbolIndex("ws1_sym"));
        cache.GetOrAddBridgeGraph(keyWs1Bridge, () => BridgeGraph.Build([], new Dictionary<string, BridgeNode>()));
        cache.GetOrAddSymbolIndex(keyWs2Sym, () => new DummySymbolIndex("ws2_sym"));

        Assert.Equal(3, cache.Count);

        cache.EvictWorkspace("ws1");

        Assert.Equal(1, cache.Count);
        var remaining = cache.GetOrAddSymbolIndex(keyWs2Sym, () => throw new Exception("should be cached"));
        Assert.Equal("ws2_sym", ((DummySymbolIndex)remaining).Name);
    }
}
