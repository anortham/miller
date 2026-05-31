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
        Func<long, bool?>? currentIndexFresh = null) =>
        new(
            holder,
            workspace,
            registry,
            refresh ?? (_ => throw new InvalidOperationException("refresh was not expected")),
            loadIndex ?? (path => RepositoryIndexLoader.Load(path)),
            currentIndexFresh ?? (_ => true));

    private WorkspaceContext CurrentWorkspace(string dbPath, string workspaceId)
    {
        string root = NewRoot("current");
        return WorkspaceContext.Create(root, AppContext.BaseDirectory, _dir) with
        {
            ExtractDbPath = dbPath,
            CanonicalRoot = root,
            CanonicalExtractDbPath = dbPath,
            WorkspaceId = workspaceId,
        };
    }

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
                new JulieDbFixture.RevisionRow(revision, workspaceId, "fresh"),
            });
}
