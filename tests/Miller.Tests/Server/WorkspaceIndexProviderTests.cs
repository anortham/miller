using Miller.Indexing;
using Miller.Server;
using Miller.Server.Resolution;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class WorkspaceIndexProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _registryDbPath;

    public WorkspaceIndexProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-workspace-index-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registryDbPath = Path.Combine(_dir, "workspaces.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Resolve_CurrentWorkspace_ReturnsAResolverFixedToTheCapturedSnapshot()
    {
        using var before = DbWithSymbol("current-ws", revision: 1, "ExistingType");
        using var after = DbWithSymbol("current-ws", revision: 2, "FreshType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        var holder = new IndexHolder(RepositoryIndexLoader.Load(before.DbPath), builtRevision: 1);
        MillerRepositoryIndex afterIndex = RepositoryIndexLoader.Load(after.DbPath);
        var workspace = CurrentWorkspace(before.DbPath, "current-ws");
        var provider = NewProvider(
            holder,
            workspace,
            registry,
            currentIndexFresh: revision =>
            {
                holder.Swap(afterIndex, revision: 2);
                return revision == 1;
            });

        WorkspaceReadContext context = provider.Resolve(workspaceId: null, ensureFresh: false);

        Assert.Equal(1, context.Revision);
        Assert.True(context.IndexFresh);
        Assert.Empty(context.Index.FindByName("FreshType"));
        Assert.IsType<TargetResolution.NotFound>(context.Resolver.Resolve("FreshType"));
    }

    [Fact]
    public void Resolve_RegisteredWorkspace_CachesByWorkspacePathAndRevision()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int loadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadIndex: path =>
            {
                loadCount++;
                return RepositoryIndexLoader.Load(path);
            });

        WorkspaceReadContext first = provider.Resolve("target-ws", ensureFresh: false);
        WorkspaceReadContext second = provider.Resolve("target-ws", ensureFresh: false);
        registry.MarkScanned("target-ws", revision: 2);
        WorkspaceReadContext afterRevisionChange = provider.Resolve("target-ws", ensureFresh: false);

        Assert.Equal("target-ws", first.WorkspaceId);
        Assert.Equal(1, first.Revision);
        Assert.Same(first.Index, second.Index);
        Assert.Same(first.Resolver, second.Resolver);
        Assert.Equal(2, afterRevisionChange.Revision);
        Assert.NotSame(first.Index, afterRevisionChange.Index);
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public void ResolveSymbolSearch_RegisteredWorkspace_UsesSymbolProjectionLoaderWithoutFullLoad()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-symbol-search");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int fullLoadCount = 0;
        int searchLoadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadIndex: _ =>
            {
                fullLoadCount++;
                throw new InvalidOperationException("full loader was not expected");
            },
            loadSymbolSearch: path =>
            {
                searchLoadCount++;
                return SymbolSearchProjectionLoader.Load(path);
            });

        WorkspaceSymbolSearchContext context = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);

        Assert.Equal("target-ws", context.WorkspaceId);
        Assert.Equal(1, context.Revision);
        Assert.Equal(1, searchLoadCount);
        Assert.Equal(0, fullLoadCount);
        var hit = Assert.Single(context.Index.Search("TargetType", limit: 10));
        Assert.Equal("TargetType", context.Index.Resolve(hit.Document.DocId).Name);
    }

    [Theory]
    [InlineData("target-111111111111")]
    [InlineData("target-1111")]
    [InlineData("target-ws")]
    public void ResolveSymbolSearch_RegisteredWorkspace_AcceptsDisplayIdAndUniquePrefix(string selector)
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-symbol-alias");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry);

        WorkspaceSymbolSearchContext context = provider.ResolveSymbolSearch(selector, ensureFresh: false);

        Assert.Equal("target-ws", context.WorkspaceId);
        Assert.Equal("target-111111111111", context.DisplayId);
        var hit = Assert.Single(context.Index.Search("TargetType", limit: 10));
        Assert.Equal("TargetType", context.Index.Resolve(hit.Document.DocId).Name);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("primary")]
    public void ResolveSymbolSearch_CurrentAliasesRouteToServedWorkspace(string selector)
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry);

        WorkspaceSymbolSearchContext context = provider.ResolveSymbolSearch(selector, ensureFresh: false);

        Assert.Equal("current-ws", context.WorkspaceId);
        var hit = Assert.Single(context.Index.Search("CurrentType", limit: 10));
        Assert.Equal("CurrentType", context.Index.Resolve(hit.Document.DocId).Name);
    }

    [Fact]
    public void ResolveSymbolSearch_AmbiguousWorkspacePrefix_ReturnsClearError()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var targetA = DbWithSymbol("target-a", revision: 1, "TargetA");
        using var targetB = DbWithSymbol("target-b", revision: 1, "TargetB");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string rootA = NewRoot("target-ambiguous-a");
        string rootB = NewRoot("target-ambiguous-b");
        registry.UpsertSeen("target-a", "target-111111111111", rootA, targetA.DbPath);
        registry.MarkScanned("target-a", revision: 1);
        registry.UpsertSeen("target-b", "target-222222222222", rootB, targetB.DbPath);
        registry.MarkScanned("target-b", revision: 1);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry);

        var ex = Assert.Throws<KeyNotFoundException>(() =>
            provider.ResolveSymbolSearch("target", ensureFresh: false));

        Assert.Contains("ambiguous workspace selector 'target'", ex.Message);
        Assert.Contains("target-111111111111", ex.Message);
        Assert.Contains("target-222222222222", ex.Message);
    }

    [Fact]
    public void ResolveSymbolSearch_RegisteredWorkspace_CachesByWorkspacePathAndRevision()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-symbol-cache");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int searchLoadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadSymbolSearch: path =>
            {
                searchLoadCount++;
                return SymbolSearchProjectionLoader.Load(path);
            });

        WorkspaceSymbolSearchContext first = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);
        WorkspaceSymbolSearchContext second = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);

        Assert.Same(first.Index, second.Index);
        Assert.Equal(1, searchLoadCount);

        registry.MarkScanned("target-ws", revision: 2);
        WorkspaceSymbolSearchContext afterRevisionChange =
            provider.ResolveSymbolSearch("target-ws", ensureFresh: false);

        Assert.Equal(2, afterRevisionChange.Revision);
        Assert.NotSame(first.Index, afterRevisionChange.Index);
        Assert.Equal(2, searchLoadCount);
    }

    [Fact]
    public void ResolveSymbolSearch_RegisteredWorkspace_ReloadsWhenDbPathChangesEvenAtTheSameRevision()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var targetA = DbWithSymbol("target-ws", revision: 3, "Quokkanaut");
        using var targetB = DbWithSymbol("target-ws", revision: 3, "Zigglethorpe");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-symbol-path-change");
        registry.UpsertSeen("target-ws", "target-111111111111", root, targetA.DbPath);
        registry.MarkScanned("target-ws", revision: 3);

        int searchLoadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadSymbolSearch: path =>
            {
                searchLoadCount++;
                return SymbolSearchProjectionLoader.Load(path);
            });

        WorkspaceSymbolSearchContext first = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);
        registry.UpsertSeen("target-ws", "target-111111111111", root, targetB.DbPath);
        WorkspaceSymbolSearchContext second = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);

        Assert.Empty(first.Index.Search("Zigglethorpe", limit: 10));
        Assert.NotEmpty(second.Index.Search("Zigglethorpe", limit: 10));
        Assert.NotSame(first.Index, second.Index);
        Assert.Equal(2, searchLoadCount);
    }

    [Fact]
    public void ResolveContentSearch_RegisteredWorkspace_UsesContentLoaderWithoutFullOrSymbolLoad()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithDoc("docs/guide.md", "# Guide\nThe freshness gate verifies blake3 before reading.\n");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", target.WorkspaceRoot, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int fullLoadCount = 0;
        int symbolLoadCount = 0;
        int contentLoadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadIndex: _ =>
            {
                fullLoadCount++;
                throw new InvalidOperationException("full loader was not expected");
            },
            loadSymbolSearch: _ =>
            {
                symbolLoadCount++;
                throw new InvalidOperationException("symbol loader was not expected");
            },
            loadContentSearch: (dbPath, root) =>
            {
                contentLoadCount++;
                return ContentSearchProjectionLoader.Load(dbPath, root);
            });

        WorkspaceContentSearchContext context = provider.ResolveContentSearch("target-ws", ensureFresh: false);

        Assert.Equal("target-ws", context.WorkspaceId);
        Assert.Equal(1, context.Revision);
        Assert.Equal(1, contentLoadCount);
        Assert.Equal(0, fullLoadCount);
        Assert.Equal(0, symbolLoadCount);
        var hit = Assert.Single(context.Index.Search("freshness", limit: 10));
        Assert.Equal("docs/guide.md", hit.Path);
    }

    [Fact]
    public void ResolveContentSearch_RegisteredWorkspace_CachesByWorkspacePathAndRevision()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithDoc("docs/guide.md", "alpha freshness documentation");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", target.WorkspaceRoot, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int contentLoadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadContentSearch: (dbPath, root) =>
            {
                contentLoadCount++;
                return ContentSearchProjectionLoader.Load(dbPath, root);
            });

        WorkspaceContentSearchContext first = provider.ResolveContentSearch("target-ws", ensureFresh: false);
        WorkspaceContentSearchContext second = provider.ResolveContentSearch("target-ws", ensureFresh: false);

        Assert.Same(first.Index, second.Index);
        Assert.Equal(1, contentLoadCount);

        registry.MarkScanned("target-ws", revision: 2);
        WorkspaceContentSearchContext afterRevisionChange =
            provider.ResolveContentSearch("target-ws", ensureFresh: false);

        Assert.Equal(2, afterRevisionChange.Revision);
        Assert.NotSame(first.Index, afterRevisionChange.Index);
        Assert.Equal(2, contentLoadCount);
    }

    [Fact]
    public void ResolveContentSearch_RegisteredWorkspace_ReloadsWhenDbPathChangesEvenAtTheSameRevision()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var targetA = DbWithDoc("docs/guide.md", "alpha apple documentation");
        using var targetB = DbWithDoc("docs/guide.md", "beta zigglethorpe documentation");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", targetA.WorkspaceRoot, targetA.DbPath);
        registry.MarkScanned("target-ws", revision: 3);

        int contentLoadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadContentSearch: (dbPath, root) =>
            {
                contentLoadCount++;
                return ContentSearchProjectionLoader.Load(dbPath, root);
            });

        WorkspaceContentSearchContext first = provider.ResolveContentSearch("target-ws", ensureFresh: false);
        registry.UpsertSeen("target-ws", "target-111111111111", targetB.WorkspaceRoot, targetB.DbPath);
        WorkspaceContentSearchContext second = provider.ResolveContentSearch("target-ws", ensureFresh: false);

        Assert.Empty(first.Index.Search("zigglethorpe", limit: 10));
        Assert.NotEmpty(second.Index.Search("zigglethorpe", limit: 10));
        Assert.NotSame(first.Index, second.Index);
        Assert.Equal(2, contentLoadCount);
    }

    [Fact]
    public void ResolveContentSearch_CurrentWorkspace_BuildsAndCachesFromDiskLazily()
    {
        using var fx = DbWithDoc("docs/guide.md", "# Guide\nThe freshness gate verifies blake3 hashes.\n");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        var holder = new IndexHolder(RepositoryIndexLoader.Load(fx.DbPath), builtRevision: 7);

        int contentLoadCount = 0;
        var provider = NewProvider(
            holder,
            CurrentWorkspaceAt(fx.WorkspaceRoot, fx.DbPath, "current-ws"),
            registry,
            loadContentSearch: (dbPath, root) =>
            {
                contentLoadCount++;
                return ContentSearchProjectionLoader.Load(dbPath, root);
            });

        WorkspaceContentSearchContext byNull = provider.ResolveContentSearch(workspaceId: null, ensureFresh: false);
        WorkspaceContentSearchContext byId = provider.ResolveContentSearch("current-ws", ensureFresh: false);

        Assert.Equal(7, byNull.Revision);
        Assert.Equal("current", byNull.FreshnessStatus);
        var hit = Assert.Single(byNull.Index.Search("freshness", limit: 10));
        Assert.Equal("docs/guide.md", hit.Path);
        Assert.Same(byNull.Index, byId.Index); // null and the explicit current id resolve to one cached build
        Assert.Equal(1, contentLoadCount);
    }

    [Fact]
    public void ResolveContentSearch_CurrentWorkspace_RebuildsWhenHolderRevisionChanges()
    {
        using var fx = DbWithDoc("docs/guide.md", "freshness corpustoken");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        var holder = new IndexHolder(RepositoryIndexLoader.Load(fx.DbPath), builtRevision: 1);

        int contentLoadCount = 0;
        var provider = NewProvider(
            holder,
            CurrentWorkspaceAt(fx.WorkspaceRoot, fx.DbPath, "current-ws"),
            registry,
            loadContentSearch: (dbPath, root) =>
            {
                contentLoadCount++;
                return ContentSearchProjectionLoader.Load(dbPath, root);
            });

        WorkspaceContentSearchContext first = provider.ResolveContentSearch(workspaceId: null, ensureFresh: false);
        WorkspaceContentSearchContext cached = provider.ResolveContentSearch(workspaceId: null, ensureFresh: false);
        holder.Swap(RepositoryIndexLoader.Load(fx.DbPath), revision: 2);
        WorkspaceContentSearchContext afterSwap = provider.ResolveContentSearch(workspaceId: null, ensureFresh: false);

        Assert.Equal(1, first.Revision);
        Assert.Same(first.Index, cached.Index);       // same holder revision → cached, one build
        Assert.Equal(2, afterSwap.Revision);
        Assert.NotSame(first.Index, afterSwap.Index); // a freshness Swap bumps the revision → rebuild
        Assert.Equal(2, contentLoadCount);
    }

    [Fact]
    public async Task Resolve_RegisteredWorkspace_ConcurrentCacheMissesShareOneLoad()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-single-flight");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int loadCount = 0;
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadIndex: path =>
            {
                Interlocked.Increment(ref loadCount);
                loadStarted.TrySetResult();
                Thread.Sleep(200);
                return RepositoryIndexLoader.Load(path);
            });

        Task<WorkspaceReadContext> first = Task.Run(() => provider.Resolve("target-ws", ensureFresh: false));
        Task timeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(loadStarted.Task, await Task.WhenAny(loadStarted.Task, timeout));
        Task<WorkspaceReadContext> second = Task.Run(() => provider.Resolve("target-ws", ensureFresh: false));

        WorkspaceReadContext[] contexts = await Task.WhenAll(first, second);

        Assert.Equal(1, loadCount);
        Assert.Same(contexts[0].Index, contexts[1].Index);
        Assert.Same(contexts[0].Resolver, contexts[1].Resolver);
    }

    [Fact]
    public async Task ResolveContentSearch_RegisteredWorkspace_ConcurrentCacheMissesShareOneLoad()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithDoc("docs/guide.md", "freshness documentation");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", target.WorkspaceRoot, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int loadCount = 0;
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadContentSearch: (dbPath, root) =>
            {
                Interlocked.Increment(ref loadCount);
                loadStarted.TrySetResult();
                Thread.Sleep(200);
                return ContentSearchProjectionLoader.Load(dbPath, root);
            });

        Task<WorkspaceContentSearchContext> first =
            Task.Run(() => provider.ResolveContentSearch("target-ws", ensureFresh: false));
        Task timeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(loadStarted.Task, await Task.WhenAny(loadStarted.Task, timeout));
        Task<WorkspaceContentSearchContext> second =
            Task.Run(() => provider.ResolveContentSearch("target-ws", ensureFresh: false));

        WorkspaceContentSearchContext[] contexts = await Task.WhenAll(first, second);

        Assert.Equal(1, loadCount);
        Assert.Same(contexts[0].Index, contexts[1].Index);
    }

    [Fact]
    public void Resolve_RegisteredWorkspace_ReloadsWhenDbPathChangesEvenAtTheSameRevision()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var targetA = DbWithSymbol("target-ws", revision: 3, "TargetA");
        using var targetB = DbWithSymbol("target-ws", revision: 3, "TargetB");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-path-change");
        registry.UpsertSeen("target-ws", "target-111111111111", root, targetA.DbPath);
        registry.MarkScanned("target-ws", revision: 3);

        int loadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadIndex: path =>
            {
                loadCount++;
                return RepositoryIndexLoader.Load(path);
            });

        WorkspaceReadContext first = provider.Resolve("target-ws", ensureFresh: false);
        registry.UpsertSeen("target-ws", "target-111111111111", root, targetB.DbPath);
        WorkspaceReadContext second = provider.Resolve("target-ws", ensureFresh: false);

        Assert.NotSame(first.Index, second.Index);
        Assert.IsType<TargetResolution.NotFound>(first.Resolver.Resolve("TargetB"));
        Assert.IsType<TargetResolution.Symbol>(second.Resolver.Resolve("TargetB"));
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public void Resolve_RegisteredWorkspace_EvictsOlderCacheEntryAfterRevisionChange()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var targetA = DbWithSymbol("target-ws", revision: 1, "TargetA");
        using var targetB = DbWithSymbol("target-ws", revision: 2, "TargetB");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-evict");
        registry.UpsertSeen("target-ws", "target-111111111111", root, targetA.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        string activeDbPath = targetA.DbPath;
        int loadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadIndex: _ =>
            {
                loadCount++;
                return RepositoryIndexLoader.Load(activeDbPath);
            });

        WorkspaceReadContext first = provider.Resolve("target-ws", ensureFresh: false);
        registry.UpsertSeen("target-ws", "target-111111111111", root, targetB.DbPath);
        registry.MarkScanned("target-ws", revision: 2);
        activeDbPath = targetB.DbPath;
        WorkspaceReadContext second = provider.Resolve("target-ws", ensureFresh: false);
        registry.UpsertSeen("target-ws", "target-111111111111", root, targetA.DbPath);
        registry.MarkScanned("target-ws", revision: 1);
        activeDbPath = targetA.DbPath;
        WorkspaceReadContext third = provider.Resolve("target-ws", ensureFresh: false);

        Assert.IsType<TargetResolution.Symbol>(first.Resolver.Resolve("TargetA"));
        Assert.IsType<TargetResolution.Symbol>(second.Resolver.Resolve("TargetB"));
        Assert.IsType<TargetResolution.Symbol>(third.Resolver.Resolve("TargetA"));
        Assert.Equal(3, loadCount);
    }

    [Fact]
    public async Task Resolve_RegisteredWorkspace_InFlightStaleLoadCannotEvictNewerCacheEntry()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var targetA = DbWithSymbol("target-ws", revision: 1, "TargetA");
        using var targetB = DbWithSymbol("target-ws", revision: 2, "TargetB");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-inflight-evict");
        registry.UpsertSeen("target-ws", "target-111111111111", root, targetA.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int loadCount = 0;
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseA = new ManualResetEventSlim();
        using var releaseB = new ManualResetEventSlim();
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadIndex: path =>
            {
                Interlocked.Increment(ref loadCount);
                if (path == targetA.DbPath)
                {
                    startedA.TrySetResult();
                    releaseA.Wait(TestContext.Current.CancellationToken);
                }
                else if (path == targetB.DbPath)
                {
                    startedB.TrySetResult();
                    releaseB.Wait(TestContext.Current.CancellationToken);
                }
                return RepositoryIndexLoader.Load(path);
            });

        Task<WorkspaceReadContext> first = Task.Run(() => provider.Resolve("target-ws", ensureFresh: false));
        Task timeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(startedA.Task, await Task.WhenAny(startedA.Task, timeout));

        registry.UpsertSeen("target-ws", "target-111111111111", root, targetB.DbPath);
        registry.MarkScanned("target-ws", revision: 2);

        Task<WorkspaceReadContext> second = Task.Run(() => provider.Resolve("target-ws", ensureFresh: false));
        Assert.Same(startedB.Task, await Task.WhenAny(startedB.Task, timeout));

        releaseA.Set();
        WorkspaceReadContext stale = await first;
        releaseB.Set();
        WorkspaceReadContext fresh = await second;
        WorkspaceReadContext freshAgain = provider.Resolve("target-ws", ensureFresh: false);

        Assert.IsType<TargetResolution.Symbol>(stale.Resolver.Resolve("TargetA"));
        Assert.IsType<TargetResolution.Symbol>(fresh.Resolver.Resolve("TargetB"));
        Assert.Same(fresh.Index, freshAgain.Index);
        Assert.Same(fresh.Resolver, freshAgain.Resolver);
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public async Task ResolveSymbolSearch_RegisteredWorkspace_InFlightStaleLoadCannotEvictNewerCacheEntry()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var targetA = DbWithSymbol("target-ws", revision: 1, "TargetA");
        using var targetB = DbWithSymbol("target-ws", revision: 2, "TargetB");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-symbol-inflight-evict");
        registry.UpsertSeen("target-ws", "target-111111111111", root, targetA.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int loadCount = 0;
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseA = new ManualResetEventSlim();
        using var releaseB = new ManualResetEventSlim();
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadSymbolSearch: path =>
            {
                Interlocked.Increment(ref loadCount);
                if (path == targetA.DbPath)
                {
                    startedA.TrySetResult();
                    releaseA.Wait(TestContext.Current.CancellationToken);
                }
                else if (path == targetB.DbPath)
                {
                    startedB.TrySetResult();
                    releaseB.Wait(TestContext.Current.CancellationToken);
                }
                return SymbolSearchProjectionLoader.Load(path);
            });

        Task<WorkspaceSymbolSearchContext> first =
            Task.Run(() => provider.ResolveSymbolSearch("target-ws", ensureFresh: false));
        Task timeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(startedA.Task, await Task.WhenAny(startedA.Task, timeout));

        registry.UpsertSeen("target-ws", "target-111111111111", root, targetB.DbPath);
        registry.MarkScanned("target-ws", revision: 2);

        Task<WorkspaceSymbolSearchContext> second =
            Task.Run(() => provider.ResolveSymbolSearch("target-ws", ensureFresh: false));
        Assert.Same(startedB.Task, await Task.WhenAny(startedB.Task, timeout));

        releaseA.Set();
        WorkspaceSymbolSearchContext stale = await first;
        releaseB.Set();
        WorkspaceSymbolSearchContext fresh = await second;
        WorkspaceSymbolSearchContext freshAgain = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);

        Assert.NotEmpty(stale.Index.Search("TargetA", limit: 10));
        Assert.NotEmpty(fresh.Index.Search("TargetB", limit: 10));
        Assert.Same(fresh.Index, freshAgain.Index);
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public async Task ResolveContentSearch_RegisteredWorkspace_InFlightStaleLoadCannotEvictNewerCacheEntry()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var targetA = DbWithDoc("docs/guide.md", "alpha older documentation");
        using var targetB = DbWithDoc("docs/guide.md", "beta newer documentation");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", targetA.WorkspaceRoot, targetA.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int loadCount = 0;
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseA = new ManualResetEventSlim();
        using var releaseB = new ManualResetEventSlim();
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadContentSearch: (dbPath, root) =>
            {
                Interlocked.Increment(ref loadCount);
                if (dbPath == targetA.DbPath)
                {
                    startedA.TrySetResult();
                    releaseA.Wait(TestContext.Current.CancellationToken);
                }
                else if (dbPath == targetB.DbPath)
                {
                    startedB.TrySetResult();
                    releaseB.Wait(TestContext.Current.CancellationToken);
                }
                return ContentSearchProjectionLoader.Load(dbPath, root);
            });

        Task<WorkspaceContentSearchContext> first =
            Task.Run(() => provider.ResolveContentSearch("target-ws", ensureFresh: false));
        Task timeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(startedA.Task, await Task.WhenAny(startedA.Task, timeout));

        registry.UpsertSeen("target-ws", "target-111111111111", targetB.WorkspaceRoot, targetB.DbPath);
        registry.MarkScanned("target-ws", revision: 2);

        Task<WorkspaceContentSearchContext> second =
            Task.Run(() => provider.ResolveContentSearch("target-ws", ensureFresh: false));
        Assert.Same(startedB.Task, await Task.WhenAny(startedB.Task, timeout));

        releaseA.Set();
        WorkspaceContentSearchContext stale = await first;
        releaseB.Set();
        WorkspaceContentSearchContext fresh = await second;
        WorkspaceContentSearchContext freshAgain = provider.ResolveContentSearch("target-ws", ensureFresh: false);

        Assert.NotEmpty(stale.Index.Search("alpha", limit: 10));
        Assert.NotEmpty(fresh.Index.Search("beta", limit: 10));
        Assert.Same(fresh.Index, freshAgain.Index);
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public void Resolve_RegisteredWorkspace_RefreshesBeforeLoadingWhenRequested()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-refresh");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        bool refreshed = false;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            refresh: workspaceId =>
            {
                refreshed = true;
                registry.MarkScanned(workspaceId, revision: 2);
                return new WorkspaceRefreshResult(
                    WorkspaceRefreshStatus.Refreshed,
                    workspaceId,
                    root,
                    target.DbPath,
                    Revision: 2,
                    Scanned: true);
            });

        WorkspaceReadContext context = provider.Resolve("target-ws", ensureFresh: true);

        Assert.True(refreshed);
        Assert.Equal(2, context.Revision);
        Assert.Equal("refreshed", context.FreshnessStatus);
        Assert.Null(context.WarningText);
    }

    [Fact]
    public void Resolve_RegisteredWorkspace_UnchangedRefreshLoadsReadableDbAndReportsFresh()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 4, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-unchanged");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 4);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            refresh: workspaceId => new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.Unchanged,
                workspaceId,
                root,
                target.DbPath,
                Revision: 4,
                Scanned: true));

        WorkspaceReadContext context = provider.Resolve("target-ws", ensureFresh: true);

        Assert.Equal("unchanged", context.FreshnessStatus);
        Assert.True(context.IndexFresh);
        Assert.IsType<TargetResolution.Symbol>(context.Resolver.Resolve("TargetType"));
    }

    [Fact]
    public void Resolve_RegisteredWorkspace_LockBusyWithReadableDbReportsUnconfirmedContext()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 4, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-lock-busy");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 4);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            refresh: workspaceId => new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.LockBusy,
                workspaceId,
                root,
                target.DbPath,
                Revision: 4,
                WarningText: "busy but readable"));

        WorkspaceReadContext context = provider.Resolve("target-ws", ensureFresh: true);

        Assert.Equal("unconfirmed_lock_busy", context.FreshnessStatus);
        Assert.False(context.IndexFresh);
        Assert.Equal("busy but readable", context.WarningText);
        Assert.IsType<TargetResolution.Symbol>(context.Resolver.Resolve("TargetType"));
    }

    [Fact]
    public void Resolve_RegisteredLoadedExistingWithoutRefreshLoadsReadableDbButReportsUnconfirmedFreshness()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 4, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-loaded-existing");
        registry.UpsertSeen(
            "target-ws",
            "target-111111111111",
            root,
            target.DbPath,
            WorkspaceRegistryState.LoadedExisting);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry);

        WorkspaceReadContext context = provider.Resolve("target-ws", ensureFresh: false);

        Assert.Equal("loaded_existing", context.FreshnessStatus);
        Assert.False(context.IndexFresh);
        Assert.IsType<TargetResolution.Symbol>(context.Resolver.Resolve("TargetType"));
    }

    [Fact]
    public void Resolve_RegisteredWorkspace_MissingIndexRefreshDoesNotLoadOrPresentFreshContext()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-missing-index");
        string missingDb = Path.Combine(root, ".miller", "symbols.db");
        registry.UpsertSeen("target-ws", "target-111111111111", root, missingDb);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            refresh: workspaceId => new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.MissingIndex,
                workspaceId,
                root,
                missingDb,
                Error: "index db missing"),
            loadIndex: _ => throw new InvalidOperationException("provider should not load a missing index"));

        var ex = Assert.Throws<FileNotFoundException>(() =>
            provider.Resolve("target-ws", ensureFresh: true));

        Assert.Contains("index db missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RegisteredWorkspace_FailedRefreshDoesNotLoadOrPresentFreshContext()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 4, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-failed");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 4);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            refresh: workspaceId => new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.Failed,
                workspaceId,
                root,
                target.DbPath,
                Error: "scan failed"),
            loadIndex: _ => throw new InvalidOperationException("provider should not load after failed refresh"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            provider.Resolve("target-ws", ensureFresh: true));

        Assert.Contains("scan failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RegisteredWorkspace_MissingRootMarksTheRegistryRowMissing()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string missingRoot = Path.Combine(_dir, "does-not-exist");
        registry.UpsertSeen("target-ws", "target-111111111111", missingRoot, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry);

        var ex = Assert.Throws<DirectoryNotFoundException>(() =>
            provider.Resolve("target-ws", ensureFresh: false));

        Assert.Contains(missingRoot, ex.Message, StringComparison.Ordinal);
        Assert.Equal(WorkspaceRegistryState.Missing, registry.Get("target-ws")?.State);
    }

    private WorkspaceIndexProvider NewProvider(
        IndexHolder holder,
        WorkspaceContext workspace,
        WorkspaceRegistry registry,
        Func<string, WorkspaceRefreshResult>? refresh = null,
        Func<string, MillerRepositoryIndex>? loadIndex = null,
        Func<string, SymbolSearchProjection>? loadSymbolSearch = null,
        Func<string, string, ContentSearchProjection>? loadContentSearch = null,
        Func<long, bool?>? currentIndexFresh = null) =>
        new(
            holder,
            workspace,
            registry,
            refresh ?? (_ => throw new InvalidOperationException("refresh was not expected")),
            loadIndex ?? (path => RepositoryIndexLoader.Load(path)),
            loadSymbolSearch ?? (path => SymbolSearchProjectionLoader.Load(path)),
            loadContentSearch ?? ((dbPath, root) => ContentSearchProjectionLoader.Load(dbPath, root)),
            currentIndexFresh ?? (_ => true));

    private WorkspaceContext CurrentWorkspace(string dbPath, string workspaceId) =>
        CurrentWorkspaceAt(NewRoot("current"), dbPath, workspaceId);

    private WorkspaceContext CurrentWorkspaceAt(string root, string dbPath, string workspaceId) =>
        WorkspaceContext.Create(root, AppContext.BaseDirectory, _dir) with
        {
            ExtractDbPath = dbPath,
            CanonicalRoot = root,
            CanonicalExtractDbPath = dbPath,
            WorkspaceId = workspaceId,
        };

    private string NewRoot(string name)
    {
        string root = Path.Combine(_dir, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static JulieDbFixture DbWithSymbol(string workspaceId, long revision, string symbolName) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(
                    Guid.NewGuid().ToString("N"),
                    symbolName,
                    "class",
                    "csharp",
                    $"src/{symbolName}.cs",
                    $"public class {symbolName}",
                    1,
                    ParentId: null),
            },
            workspaceId: workspaceId,
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(revision, "fresh"),
            });

    // A symbol-free fixture whose only `files` row is a docs-like file materialized on disk under
    // WorkspaceRoot — the corpus the content-search loader re-sources and BLAKE3-verifies. Register the
    // workspace with root == fixture.WorkspaceRoot so the loader finds the doc under the registered root.
    private static JulieDbFixture DbWithDoc(string docPath, string docText) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>(),
            extraFiles: new[]
            {
                new JulieDbFixture.FileSpec(docPath) { Language = "markdown", DiskText = docText },
            });
}
