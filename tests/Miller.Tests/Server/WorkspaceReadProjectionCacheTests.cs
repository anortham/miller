using Miller.Core.Graph;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Reads;
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
    public void EstimatedBudget_EvictsLeastRecentlyUsedProjection()
    {
        using var cache = new WorkspaceReadProjectionCache(maxEstimatedBytes: 1500);
        var first = new WorkspaceReadProjectionKey("first", "g", "v", 1, "symbol");
        var second = first with { WorkspaceId = "second" };
        cache.GetOrAddSymbolIndex(first, () => new DummySymbolIndex("first"));
        cache.GetOrAddSymbolIndex(second, () => new DummySymbolIndex("second"));
        Assert.Equal(1, cache.Count);
        Assert.InRange(cache.EstimatedRetainedBytes, 1, 1500);
        bool reloaded = false;
        cache.GetOrAddSymbolIndex(first, () => { reloaded = true; return new DummySymbolIndex("again"); });
        Assert.True(reloaded);
    }

    [Fact]
    public void IdleTimer_ReleasesProjectionWithoutAnotherRead()
    {
        var clock = new ProjectionClock();
        using var cache = new WorkspaceReadProjectionCache(clock);
        var key = new WorkspaceReadProjectionKey("ws", "g", "v", 1, "symbol");
        cache.GetOrAddSymbolIndex(key, () => new DummySymbolIndex("value"));
        Assert.Equal(1, cache.Count);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.EstimatedRetainedBytes);
    }

    private sealed class ProjectionClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private Action? _tick;
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _tick = () => callback(state);
            return new ProjectionTimer();
        }
        public void Advance(TimeSpan elapsed) { _now += elapsed; _tick?.Invoke(); }
        private sealed class ProjectionTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("family")]
    [InlineData("manifest")]
    [InlineData("level")]
    public void ProjectionKey_ChangesWithEverySnapshotIdentityComponent(string change)
    {
        var snapshot = new WorkspaceReadSnapshot("/workspace", "ws", "family-a", "view",
            new WorkspaceFreshnessToken("family-a", 7, ManifestHash: "hash-a"),
            "full", WorkspaceReadMode.FamilyStore, GenerationName: "gen-001");
        var changed = change switch
        {
            "family" => snapshot with { ArtifactOrStoreId = "family-b" },
            "manifest" => snapshot with { Freshness = snapshot.Freshness with { ManifestHash = "hash-b" } },
            _ => snapshot with { IndexLevel = "symbols" }
        };
        Assert.NotEqual(WorkspaceReadProjectionKey.ForSymbol("ws", snapshot), WorkspaceReadProjectionKey.ForSymbol("ws", changed));
        Assert.NotEqual(WorkspaceReadProjectionKey.ForBridge("ws", snapshot), WorkspaceReadProjectionKey.ForBridge("ws", changed));
    }

    [Fact]
    public async Task SameRevisionConcurrentReaders_ShareOneImmutableConstruction()
    {
        using var cache = new WorkspaceReadProjectionCache();
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
        using var cache = new WorkspaceReadProjectionCache();
        var keyRev1 = new WorkspaceReadProjectionKey("ws1", "gen1", "view1", 1, "symbol");
        var keyRev2 = new WorkspaceReadProjectionKey("ws1", "gen1", "view1", 2, "symbol");

        var index1 = cache.GetOrAddSymbolIndex(keyRev1, () => new DummySymbolIndex("rev1"));
        Assert.Equal(1, cache.Count);
        Assert.Equal("rev1", ((DummySymbolIndex)index1).Name);

        var index2 = cache.GetOrAddSymbolIndex(keyRev2, () => new DummySymbolIndex("rev2"));
        Assert.Equal(1, cache.Count);
        Assert.Equal("rev2", ((DummySymbolIndex)index2).Name);
        Assert.NotSame(index1, index2);
    }

    [Fact]
    public void GenerationChange_RebuildsAndEvictsSuperseded()
    {
        using var cache = new WorkspaceReadProjectionCache();
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
        using var cache = new WorkspaceReadProjectionCache();
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

        Assert.Equal(0, cache.Count);

        shouldFail = false;
        var index = cache.GetOrAddSymbolIndex(key, () => new DummySymbolIndex("success"));
        Assert.Equal(1, cache.Count);
        Assert.Equal("success", ((DummySymbolIndex)index).Name);
    }

    [Fact]
    public void EvictWorkspace_RemovesAllEntriesForThatWorkspace()
    {
        using var cache = new WorkspaceReadProjectionCache();
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
