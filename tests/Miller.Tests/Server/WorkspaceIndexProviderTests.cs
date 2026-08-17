using Microsoft.Data.Sqlite;
using System.Text.Json;
using Miller.Core.Graph;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Server;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
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
        // Keep this prefix short: PathFor adds sidecars/search-{64-hex}.db, and Windows
        // SQLITE_CANTOPEN (14) when the -journal sibling exceeds MAX_PATH.
        _dir = Path.Combine(Path.GetTempPath(), "m-wip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registryDbPath = Path.Combine(_dir, "workspaces.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ServerJson_ImmutableStrings_UsesJsonEscaping()
    {
        string json = ServerJson.Strings(["alpha", "quote\"", "line\n"]);

        Assert.Equal("[\"alpha\",\"quote\\u0022\",\"line\\n\"]", json);
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
    public void Resolve_CurrentFamilyStoreDoesNotMaterializeTheHolderRepository()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("current-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("current-store-lazy-holder");
        int holderLoads = 0;
        var holder = new IndexHolder(
            () =>
            {
                holderLoads++;
                throw new InvalidOperationException("holder repository was not expected");
            },
            builtRevision: 12,
            documentCount: 1,
            knownExtensionsCount: 1);
        var provider = NewProvider(
            holder,
            CurrentWorkspaceAt(root, current.DbPath, "current-ws"),
            registry,
            openReadSession: (_, _, _) => new WorkspaceReadHandle(
                new FixtureReadSession(StoreSnapshot(root, "manifest-a"), target.DbPath)),
            loadSessionSymbolSearch: session => SymbolSearchProjectionLoader.LoadSession(session));

        using WorkspaceReadContext context = provider.Resolve(workspaceId: null, ensureFresh: false);
        using WorkspaceSymbolSearchContext search = provider.ResolveSymbolSearch(workspaceId: null, ensureFresh: false);
        using WorkspaceSymbolReadContext inspect = provider.ResolveSymbolRead(workspaceId: null, ensureFresh: false);
        using WorkspaceArtifactContext artifact = provider.ResolveArtifact(workspaceId: null, ensureFresh: false);

        Assert.Single(context.Index.FindByName("TargetType"));
        Assert.Single(search.Index.FindByName("TargetType"));
        Assert.Single(inspect.Index.FindByName("TargetType"));
        Assert.Equal(WorkspaceReadMode.FamilyStore, artifact.Snapshot.Mode);
        Assert.Equal(0, holderLoads);
    }

    [Fact]
    public void Resolve_CurrentFamilyStoreEmitsFixedReadPhaseTelemetry()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("current-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("current-store-telemetry");
        var holder = new IndexHolder(
            () => throw new InvalidOperationException("holder repository was not expected"),
            builtRevision: 12,
            documentCount: 1,
            knownExtensionsCount: 1);
        var graphStatements = new List<GraphStatementObservation>();
        var provider = NewProvider(
            holder,
            CurrentWorkspaceAt(root, current.DbPath, "current-ws"),
            registry,
            openReadSession: (_, _, _) => new WorkspaceReadHandle(
                new FixtureReadSession(StoreSnapshot(root, "manifest-a"), target.DbPath)),
            loadSessionSymbolSearch: session => SymbolSearchProjectionLoader.LoadSession(session),
            graphStatementObserver: graphStatements.Add);
        using var ledger = TelemetryLedger.Open(Path.Combine(_dir, "telemetry.db"), "current-ws", root);
        using TelemetryScope scope = ledger.Measure("context", op: null);

        using WorkspaceReadContext context = provider.Resolve(workspaceId: null, ensureFresh: false);
        IndexedSymbol targetSymbol = Assert.Single(context.Index.FindByName("TargetType"));
        Assert.Empty(context.Graph.Reach([targetSymbol.SymbolId], 1, 10, Direction.Forward));
        ReadToolWorkspaceRouting.ApplyTelemetry(scope, context);

        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        JsonElement rootElement = metadata.RootElement;
        Assert.True(rootElement.GetProperty("read_resolve_ms").GetInt64() >= 0);
        Assert.True(rootElement.GetProperty("read_lookup_count").GetInt64() > 0);
        Assert.True(rootElement.GetProperty("read_lookup_ms").GetInt64() >= 0);
        Assert.True(rootElement.GetProperty("read_graph_count").GetInt64() > 0);
        Assert.True(rootElement.GetProperty("read_graph_ms").GetInt64() >= 0);
        Assert.Equal(1, rootElement.GetProperty("read_provider_cache_entries").GetInt32());
        Assert.Equal(
            [
                GraphStatementPhase.RelationshipForward,
                GraphStatementPhase.UnresolvedNameForward,
                GraphStatementPhase.FamilyResolution,
                GraphStatementPhase.Supplemental,
                GraphStatementPhase.Completion,
            ],
            graphStatements.Select(static observation => observation.Phase));
    }

    [Fact]
    public void Resolve_CurrentFamilyStoreLoadsSupplementalEdgesOncePerSnapshotOnFirstGraphUse()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("current-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("current-store-supplemental-cache");
        var holder = new IndexHolder(
            () => throw new InvalidOperationException("holder repository was not expected"),
            builtRevision: 12,
            documentCount: 1,
            knownExtensionsCount: 1);
        WorkspaceReadSnapshot snapshot = StoreSnapshot(root, "manifest-a");
        int loadCount = 0;
        var provider = NewProvider(
            holder,
            CurrentWorkspaceAt(root, current.DbPath, "current-ws"),
            registry,
            openReadSession: (_, _, _) => new WorkspaceReadHandle(
                new FixtureReadSession(snapshot, target.DbPath)),
            loadSessionSymbolSearch: session => SymbolSearchProjectionLoader.LoadSession(session),
            loadSupplementalEdges: _ =>
            {
                loadCount++;
                return [];
            });

        using (WorkspaceReadContext unusedGraph = provider.Resolve(workspaceId: null, ensureFresh: false))
            Assert.Equal(0, loadCount);

        using (WorkspaceReadContext first = provider.Resolve(workspaceId: null, ensureFresh: false))
        {
            IndexedSymbol targetSymbol = Assert.Single(first.Index.FindByName("TargetType"));
            Assert.Empty(first.Graph.Reach([targetSymbol.SymbolId], 1, 10, Direction.Forward));
        }

        Assert.Equal(1, loadCount);

        using (WorkspaceReadContext second = provider.Resolve(workspaceId: null, ensureFresh: false))
        {
            IndexedSymbol targetSymbol = Assert.Single(second.Index.FindByName("TargetType"));
            Assert.Empty(second.Graph.Reach([targetSymbol.SymbolId], 1, 10, Direction.Forward));
        }

        Assert.Equal(1, loadCount);

        snapshot = StoreSnapshot(root, "manifest-b");
        using (WorkspaceReadContext afterManifestChange = provider.Resolve(workspaceId: null, ensureFresh: false))
        {
            IndexedSymbol targetSymbol = Assert.Single(afterManifestChange.Index.FindByName("TargetType"));
            Assert.Empty(afterManifestChange.Graph.Reach([targetSymbol.SymbolId], 1, 10, Direction.Forward));
        }

        Assert.Equal(2, loadCount);
    }

    [Fact]
    public void Resolve_CurrentFamilyStoreSharesSupplementalEdgesAcrossProviderInstances()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("current-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("current-store-supplemental-share");
        var holder = new IndexHolder(
            () => throw new InvalidOperationException("holder repository was not expected"),
            builtRevision: 12,
            documentCount: 1,
            knownExtensionsCount: 1);
        WorkspaceReadSnapshot snapshot = StoreSnapshot(root, "manifest-a");
        int loadCount = 0;
        var cache = new SupplementalEdgeCache();
        WorkspaceIndexProvider CreateProvider() => NewProvider(
            holder,
            CurrentWorkspaceAt(root, current.DbPath, "current-ws"),
            registry,
            openReadSession: (_, _, _) => new WorkspaceReadHandle(
                new FixtureReadSession(snapshot, target.DbPath)),
            loadSessionSymbolSearch: session => SymbolSearchProjectionLoader.LoadSession(session),
            loadSupplementalEdges: _ =>
            {
                loadCount++;
                return [];
            },
            supplementalEdgesCache: cache);

        using (WorkspaceReadContext first = CreateProvider().Resolve(workspaceId: null, ensureFresh: false))
        {
            IndexedSymbol targetSymbol = Assert.Single(first.Index.FindByName("TargetType"));
            Assert.Empty(first.Graph.Reach([targetSymbol.SymbolId], 1, 10, Direction.Forward));
        }

        using (WorkspaceReadContext second = CreateProvider().Resolve(workspaceId: null, ensureFresh: false))
        {
            IndexedSymbol targetSymbol = Assert.Single(second.Index.FindByName("TargetType"));
            Assert.Empty(second.Graph.Reach([targetSymbol.SymbolId], 1, 10, Direction.Forward));
        }

        Assert.Equal(1, loadCount);
    }

    [Fact]
    public void Resolve_CurrentFamilyStoreDefaultSupplementalLoadReusesOneCacheEntry()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("current-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("current-store-supplemental-default");
        var holder = new IndexHolder(
            () => throw new InvalidOperationException("holder repository was not expected"),
            builtRevision: 12,
            documentCount: 1,
            knownExtensionsCount: 1);
        var provider = NewProvider(
            holder,
            CurrentWorkspaceAt(root, current.DbPath, "current-ws"),
            registry,
            openReadSession: (_, _, _) => new WorkspaceReadHandle(
                new FixtureReadSession(StoreSnapshot(root, "manifest-a"), target.DbPath)),
            loadSessionSymbolSearch: session => SymbolSearchProjectionLoader.LoadSession(session));

        using (WorkspaceReadContext first = provider.Resolve(workspaceId: null, ensureFresh: false))
        {
            IndexedSymbol targetSymbol = Assert.Single(first.Index.FindByName("TargetType"));
            Assert.Empty(first.Graph.Reach([targetSymbol.SymbolId], 1, 10, Direction.Forward));
        }

        using (WorkspaceReadContext second = provider.Resolve(workspaceId: null, ensureFresh: false))
        {
            IndexedSymbol targetSymbol = Assert.Single(second.Index.FindByName("TargetType"));
            Assert.Empty(second.Graph.Reach([targetSymbol.SymbolId], 1, 10, Direction.Forward));
        }

        WorkspaceIndexProviderCacheSnapshot cache = provider.CacheSnapshot();
        Assert.Equal(1, cache.SupplementalEdgeEntries);
        Assert.Equal(2, cache.TotalEntries);
    }

    [Fact]
    public void LookupPhaseTelemetryUsesFixedFamiliesAndIndependentBaselines()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        ISymbolLookupIndex inner = SymbolSearchProjectionLoader.Load(fixture.DbPath);
        var measured = new MeasuredSymbolLookupIndex(inner);
        _ = measured.FindByName("TargetType");
        var telemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        IndexedSymbol symbol = Assert.Single(measured.FindByName("TargetType"));

        _ = measured.DocumentCount;
        _ = measured.KnownExtensions;
        _ = measured.Search("TargetType");
        _ = measured.Resolve(symbol.DocId);
        _ = measured.FindBySymbolId(symbol.SymbolId);
        _ = measured.FindChildren(symbol.SymbolId);
        _ = measured.FindByFilePath(symbol.FilePath);
        _ = measured.FindByFilePathFragment(symbol.FilePath, 10);
        _ = measured.FindFilePathsByFragment(symbol.FilePath, 10);
        _ = measured.IsIndexedFilePath(symbol.FilePath);
        _ = measured.ResolveIndexedFilePath(symbol.FilePath);

        ContextLookupPhaseObservation first = telemetry.CompleteLookupPhase(ContextLookupPhase.QueryRetrieval);
        _ = measured.FindBySymbolId(symbol.SymbolId);
        _ = measured.FindBySymbolId(symbol.SymbolId);
        ContextLookupPhaseObservation second = telemetry.CompleteLookupPhase(ContextLookupPhase.TermRetrieval);

        Assert.Equal(12, first.Delta.TotalCallCount);
        Assert.Equal(12, first.Total.TotalCallCount);
        Assert.Equal(1, first.Delta.DocumentCount.CallCount);
        Assert.Equal(1, first.Delta.KnownExtensions.CallCount);
        Assert.Equal(1, first.Delta.Search.CallCount);
        Assert.Equal(1, first.Delta.ResolveDoc.CallCount);
        Assert.Equal(1, first.Delta.FindByName.CallCount);
        Assert.Equal(1, first.Delta.FindBySymbolId.CallCount);
        Assert.Equal(1, first.Delta.FindChildren.CallCount);
        Assert.Equal(1, first.Delta.FindByFilePath.CallCount);
        Assert.Equal(1, first.Delta.FindByFilePathFragment.CallCount);
        Assert.Equal(1, first.Delta.FindFilePathsByFragment.CallCount);
        Assert.Equal(1, first.Delta.IsIndexedFilePath.CallCount);
        Assert.Equal(1, first.Delta.ResolveIndexedFilePath.CallCount);
        Assert.Equal(2, second.Delta.TotalCallCount);
        Assert.Equal(2, second.Delta.FindBySymbolId.CallCount);
        Assert.Equal(0, second.Delta.FindByName.CallCount);
        Assert.Equal(14, second.Total.TotalCallCount);
        Assert.Equal(3, second.Total.FindBySymbolId.CallCount);
        Assert.Equal(1, second.Total.FindByName.CallCount);
        Assert.All(
            new[]
            {
                first.Delta.DocumentCount,
                first.Delta.KnownExtensions,
                first.Delta.Search,
                first.Delta.ResolveDoc,
                first.Delta.FindByName,
                first.Delta.FindBySymbolId,
                first.Delta.FindChildren,
                first.Delta.FindByFilePath,
                first.Delta.FindByFilePathFragment,
                first.Delta.FindFilePathsByFragment,
                first.Delta.IsIndexedFilePath,
                first.Delta.ResolveIndexedFilePath,
            },
            static family => Assert.True(family.ElapsedMilliseconds >= 0));
    }

    [Fact]
    public void LookupPhaseTelemetryClassifiesSearchRequestsWithoutExposingQueries()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        ISymbolLookupIndex inner = SymbolSearchProjectionLoader.Load(fixture.DbPath);
        var measured = new MeasuredSymbolLookupIndex(inner);
        using var ledger = TelemetryLedger.Open(Path.Combine(_dir, "search-shape.db"), "current-ws", _dir);
        using TelemetryScope scope = ledger.Measure("context", op: null);
        _ = measured.Search("before baseline", 10, SearchMode.Or);
        var telemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using IDisposable searchTelemetry = telemetry.ActivateSearchTelemetry();

        _ = measured.Search("TargetType", 10, SearchMode.Or);
        _ = measured.Search("TargetType", 10, SearchMode.Or);
        _ = measured.Search("TargetType", 20, SearchMode.Or);
        _ = measured.Search("TargetType", 20, SearchMode.And);
        _ = measured.Search("Qzxwplmn", 10, SearchMode.Or);
        ContextLookupPhaseObservation first = telemetry.CompleteLookupPhase(ContextLookupPhase.QueryRetrieval);

        _ = measured.Search("TargetType", 10, SearchMode.Or);
        _ = measured.Search("Vbnmqwer", 10, SearchMode.And);
        ContextLookupPhaseObservation second = telemetry.CompleteLookupPhase(ContextLookupPhase.TermRetrieval);

        Assert.Equal(2, first.SearchDelta.FirstQuery.CallCount);
        Assert.Equal(1, first.SearchDelta.ModeVariant.CallCount);
        Assert.Equal(1, first.SearchDelta.WindowVariant.CallCount);
        Assert.Equal(1, first.SearchDelta.ExactRepeat.CallCount);
        Assert.Equal(1, first.SearchDelta.FirstQuery.ReturnedRowCount);
        Assert.Equal(1, first.SearchDelta.ModeVariant.ReturnedRowCount);
        Assert.Equal(1, first.SearchDelta.WindowVariant.ReturnedRowCount);
        Assert.Equal(1, first.SearchDelta.ExactRepeat.ReturnedRowCount);
        Assert.Equal(1, first.SearchDelta.And.CallCount);
        Assert.Equal(4, first.SearchDelta.Or.CallCount);
        Assert.Equal(1, first.SearchDelta.And.ReturnedRowCount);
        Assert.Equal(3, first.SearchDelta.Or.ReturnedRowCount);
        Assert.Equal(0, first.SearchDelta.DroppedCallCount);
        Assert.Equal(1, second.SearchDelta.FirstQuery.CallCount);
        Assert.Equal(0, second.SearchDelta.ModeVariant.CallCount);
        Assert.Equal(0, second.SearchDelta.WindowVariant.CallCount);
        Assert.Equal(1, second.SearchDelta.ExactRepeat.CallCount);
        Assert.Equal(3, second.SearchTotal.FirstQuery.CallCount);
        Assert.Equal(1, second.SearchTotal.ModeVariant.CallCount);
        Assert.Equal(1, second.SearchTotal.WindowVariant.CallCount);
        Assert.Equal(2, second.SearchTotal.ExactRepeat.CallCount);
        Assert.Equal(7, second.SearchTotal.TotalCallCount);
        Assert.All(
            new[]
            {
                first.SearchDelta.FirstQuery,
                first.SearchDelta.ModeVariant,
                first.SearchDelta.WindowVariant,
                first.SearchDelta.ExactRepeat,
                first.SearchDelta.And,
                first.SearchDelta.Or,
            },
            static family => Assert.True(family.ElapsedMilliseconds >= 0));
    }

    [Fact]
    public void LookupPhaseTelemetryBoundsUnclassifiedSearchRequestState()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        ISymbolLookupIndex inner = SymbolSearchProjectionLoader.Load(fixture.DbPath);
        var measured = new MeasuredSymbolLookupIndex(inner);
        using var ledger = TelemetryLedger.Open(Path.Combine(_dir, "search-overflow.db"), "current-ws", _dir);
        using TelemetryScope scope = ledger.Measure("context", op: null);
        var telemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using IDisposable searchTelemetry = telemetry.ActivateSearchTelemetry();

        for (int index = 0; index < 80; index++)
            _ = measured.Search($"MissingType{index}", 10, SearchMode.Or);

        ContextLookupPhaseObservation observation =
            telemetry.CompleteLookupPhase(ContextLookupPhase.QueryRetrieval);

        Assert.Equal(64, observation.SearchDelta.FirstQuery.CallCount);
        Assert.Equal(16, observation.SearchDelta.DroppedCallCount);
        Assert.Equal(80, observation.SearchDelta.TotalCallCount);
    }

    [Fact]
    public void LookupPhaseTelemetryDoesNotCrossCorrelateInterleavedContexts()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        ISymbolLookupIndex inner = SymbolSearchProjectionLoader.Load(fixture.DbPath);
        var measured = new MeasuredSymbolLookupIndex(inner);
        using var ledger = TelemetryLedger.Open(Path.Combine(_dir, "search-interleaved.db"), "current-ws", _dir);
        using TelemetryScope firstScope = ledger.Measure("context", op: null);
        var firstTelemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using IDisposable firstSearchTelemetry = firstTelemetry.ActivateSearchTelemetry();
        _ = measured.Search("TargetType", 10, SearchMode.Or);

        ContextLookupPhaseObservation second;
        using (TelemetryScope secondScope = ledger.Measure("context", op: null))
        {
            var secondTelemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
            using IDisposable secondSearchTelemetry = secondTelemetry.ActivateSearchTelemetry();
            _ = measured.Search("Qzxwplmn", 20, SearchMode.And);
            second = secondTelemetry.CompleteLookupPhase(ContextLookupPhase.QueryRetrieval);
        }

        _ = measured.Search("TargetType", 10, SearchMode.Or);
        ContextLookupPhaseObservation first =
            firstTelemetry.CompleteLookupPhase(ContextLookupPhase.QueryRetrieval);

        Assert.Equal(2, first.SearchDelta.ExactRepeat.CallCount + first.SearchDelta.FirstQuery.CallCount);
        Assert.Equal(2, first.SearchDelta.Or.CallCount);
        Assert.Equal(0, first.SearchDelta.And.CallCount);
        Assert.Equal(1, second.SearchDelta.FirstQuery.CallCount);
        Assert.Equal(1, second.SearchDelta.And.CallCount);
        Assert.Equal(0, second.SearchDelta.Or.CallCount);
    }

    [Fact]
    public void LookupPhaseTelemetryCorrelatesCachedFtsStagesAcrossInterleavedContexts()
    {
        string searchDbPath = Path.Combine(_dir, "phase-search.db");
        SearchIndexWriter.Write(
            searchDbPath,
            [
                new IndexedSymbol(
                    0,
                    "alpha",
                    "AlphaBeta",
                    "class AlphaBeta",
                    "class",
                    "csharp",
                    "src/A.cs",
                    1,
                    2,
                    null,
                    false),
            ],
            revision: 1);
        var measured = new MeasuredSymbolLookupIndex(FtsSymbolSearchIndex.Open(searchDbPath));
        var firstTelemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using IDisposable firstActivation = firstTelemetry.ActivateSearchTelemetry();
        _ = measured.Search("alpha", 10, SearchMode.Or);

        ContextLookupPhaseObservation second;
        var secondTelemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using (secondTelemetry.ActivateSearchTelemetry())
        {
            _ = measured.Search("missing", 10, SearchMode.Or);
            second = secondTelemetry.CompleteLookupPhase(ContextLookupPhase.QueryRetrieval);
        }

        _ = measured.Search("alpha", 10, SearchMode.Or);
        ContextLookupPhaseObservation first =
            firstTelemetry.CompleteLookupPhase(ContextLookupPhase.QueryRetrieval);
        ContextLookupPhaseObservation firstZeroDelta =
            firstTelemetry.CompleteLookupPhase(ContextLookupPhase.TermRetrieval);

        Assert.Equal(2, first.FtsSearchDelta.ConnectionOpen.CallCount);
        Assert.Equal(2, first.FtsSearchDelta.WordCandidates.CallCount);
        Assert.Equal(2, first.FtsSearchDelta.WordScoring.CallCount);
        Assert.Equal(2, first.FtsSearchDelta.WordHydration.CallCount);
        Assert.Equal(2, first.FtsSearchDelta.TrigramCandidates.CallCount);
        Assert.Equal(2, first.FtsSearchDelta.TrigramScoring.CallCount);
        Assert.Equal(2, first.FtsSearchDelta.FinalOrdering.CallCount);
        Assert.Equal(1, second.FtsSearchDelta.ConnectionOpen.CallCount);
        Assert.Equal(1, second.FtsSearchDelta.WordCandidates.CallCount);
        Assert.Equal(1, second.FtsSearchDelta.WordScoring.CallCount);
        Assert.Equal(1, second.FtsSearchDelta.WordHydration.CallCount);
        Assert.Equal(1, second.FtsSearchDelta.TrigramCandidates.CallCount);
        Assert.Equal(1, second.FtsSearchDelta.TrigramScoring.CallCount);
        Assert.Equal(1, second.FtsSearchDelta.FinalOrdering.CallCount);
        Assert.Equal(0, firstZeroDelta.FtsSearchDelta.TotalCallCount);
        Assert.Equal(2, firstZeroDelta.FtsSearchTotal.ConnectionOpen.CallCount);
    }

    [Fact]
    public void LookupPhaseTelemetryPreservesFtsStageTimeSpanUnits()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        ISymbolLookupIndex inner = SymbolSearchProjectionLoader.Load(fixture.DbPath);
        var measured = new MeasuredSymbolLookupIndex(inner);
        var telemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using IDisposable activation = telemetry.ActivateSearchTelemetry();

        FtsSearchQueryTelemetryCollector.Current!.Record(new FtsSearchQueryObservation(
            FtsSearchQueryFamily.WordCandidates,
            Rows: 12,
            Elapsed: TimeSpan.FromMilliseconds(250)));
        ContextLookupPhaseObservation observation =
            telemetry.CompleteLookupPhase(ContextLookupPhase.QueryRetrieval);

        Assert.Equal(1, observation.FtsSearchDelta.WordCandidates.CallCount);
        Assert.Equal(250, observation.FtsSearchDelta.WordCandidates.ElapsedMilliseconds);
        Assert.Equal(12, observation.FtsSearchDelta.WordCandidates.ReturnedRowCount);
    }

    [Fact]
    public void LookupPhaseTelemetryCorrelatesContentFtsStagesAcrossInterleavedContextsAndPreservesUnits()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        var measured = new MeasuredSymbolLookupIndex(SymbolSearchProjectionLoader.Load(fixture.DbPath));
        var firstTelemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using IDisposable firstActivation = firstTelemetry.ActivateSearchTelemetry();
        FtsTextSearchQueryTelemetryCollector.Current!.Record(new FtsTextSearchQueryObservation(
            FtsTextSearchQueryFamily.OpenMetadata,
            Rows: 1,
            Elapsed: TimeSpan.FromMilliseconds(10)));

        ContextLookupPhaseObservation second;
        var secondTelemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using (secondTelemetry.ActivateSearchTelemetry())
        {
            FtsTextSearchQueryTelemetryCollector.Current!.Record(new FtsTextSearchQueryObservation(
                FtsTextSearchQueryFamily.SymbolSpanHydration,
                Rows: 12,
                Elapsed: TimeSpan.FromMilliseconds(250)));
            second = secondTelemetry.CompleteLookupPhase(ContextLookupPhase.SourceRescue);
        }

        FtsTextSearchQueryTelemetryCollector.Current!.Record(new FtsTextSearchQueryObservation(
            FtsTextSearchQueryFamily.FinalOrdering,
            Rows: 3,
            Elapsed: TimeSpan.FromMilliseconds(40)));
        ContextLookupPhaseObservation first =
            firstTelemetry.CompleteLookupPhase(ContextLookupPhase.SourceRescue);
        ContextLookupPhaseObservation zero =
            firstTelemetry.CompleteLookupPhase(ContextLookupPhase.QueryRetrieval);

        Assert.Equal(1, first.FtsTextSearchDelta.OpenMetadata.CallCount);
        Assert.Equal(10, first.FtsTextSearchDelta.OpenMetadata.ElapsedMilliseconds);
        Assert.Equal(1, first.FtsTextSearchDelta.OpenMetadata.ReturnedRowCount);
        Assert.Equal(1, first.FtsTextSearchDelta.FinalOrdering.CallCount);
        Assert.Equal(0, first.FtsTextSearchDelta.SymbolSpanHydration.CallCount);
        Assert.Equal(1, second.FtsTextSearchDelta.SymbolSpanHydration.CallCount);
        Assert.Equal(250, second.FtsTextSearchDelta.SymbolSpanHydration.ElapsedMilliseconds);
        Assert.Equal(12, second.FtsTextSearchDelta.SymbolSpanHydration.ReturnedRowCount);
        Assert.Equal(0, second.FtsTextSearchDelta.OpenMetadata.CallCount);
        Assert.Equal(0, zero.FtsTextSearchDelta.TotalCallCount);
    }

    [Fact]
    public void LookupPhaseTelemetryCorrelatesContentIndexResolveStagesAcrossInterleavedContexts()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        var measured = new MeasuredSymbolLookupIndex(SymbolSearchProjectionLoader.Load(fixture.DbPath));
        var firstTelemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using IDisposable firstActivation = firstTelemetry.ActivateSearchTelemetry();
        TextContentIndexResolveTelemetryCollector.Current!.Record(new TextContentIndexResolveObservation(
            TextContentIndexResolveFamily.ReadSessionOpen,
            TimeSpan.FromMilliseconds(10)));

        ContextLookupPhaseObservation second;
        var secondTelemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using (secondTelemetry.ActivateSearchTelemetry())
        {
            TextContentIndexResolveTelemetryCollector.Current!.Record(new TextContentIndexResolveObservation(
                TextContentIndexResolveFamily.CacheMiss,
                TimeSpan.FromMilliseconds(20)));
            second = secondTelemetry.CompleteLookupPhase(ContextLookupPhase.SourceRescue);
        }

        TextContentIndexResolveTelemetryCollector.Current!.Record(new TextContentIndexResolveObservation(
            TextContentIndexResolveFamily.CacheHit,
            TimeSpan.FromMilliseconds(30)));
        ContextLookupPhaseObservation first =
            firstTelemetry.CompleteLookupPhase(ContextLookupPhase.SourceRescue);
        ContextLookupPhaseObservation zero =
            firstTelemetry.CompleteLookupPhase(ContextLookupPhase.QueryRetrieval);

        Assert.Equal(1, first.TextContentIndexResolveDelta.ReadSessionOpen.CallCount);
        Assert.Equal(10, first.TextContentIndexResolveDelta.ReadSessionOpen.ElapsedMilliseconds);
        Assert.Equal(1, first.TextContentIndexResolveDelta.CacheHit.CallCount);
        Assert.Equal(30, first.TextContentIndexResolveDelta.CacheHit.ElapsedMilliseconds);
        Assert.Equal(0, first.TextContentIndexResolveDelta.CacheMiss.CallCount);
        Assert.Equal(1, second.TextContentIndexResolveDelta.CacheMiss.CallCount);
        Assert.Equal(20, second.TextContentIndexResolveDelta.CacheMiss.ElapsedMilliseconds);
        Assert.Equal(0, second.TextContentIndexResolveDelta.CacheHit.CallCount);
        Assert.Equal(0, zero.TextContentIndexResolveDelta.TotalCallCount);
    }

    [Fact]
    public void MeasuredSearchPreservesNullForgivenInnerBehavior()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        ISymbolLookupIndex inner = SymbolSearchProjectionLoader.Load(fixture.DbPath);
        var measured = new MeasuredSymbolLookupIndex(inner);

        Assert.Empty(measured.Search(null!));
    }

    [Fact]
    public void ContextSearchCacheReusesExactRequestsAndMissesEveryKeyVariant()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        ISymbolLookupIndex inner = SymbolSearchProjectionLoader.Load(fixture.DbPath);
        var measured = new MeasuredSymbolLookupIndex(inner);
        var cache = new ContextSearchCacheLookupIndex(measured);

        IReadOnlyList<SearchHit> first = cache.Search("TargetType", 10, SearchMode.Or);
        IReadOnlyList<SearchHit> repeated = cache.Search("TargetType", 10, SearchMode.Or);
        IReadOnlyList<SearchHit> differentLimit = cache.Search("TargetType", 20, SearchMode.Or);
        IReadOnlyList<SearchHit> differentMode = cache.Search("TargetType", 10, SearchMode.And);
        IReadOnlyList<SearchHit> differentQuery = cache.Search("Qzxwplmn", 10, SearchMode.Or);

        Assert.Same(first, repeated);
        Assert.Equal(first, repeated);
        Assert.Equal(["TargetType"], repeated.Select(static hit => hit.Document.Name));
        Assert.Single(differentLimit);
        Assert.Single(differentMode);
        Assert.Empty(differentQuery);
        Assert.Equal(
            4,
            measured.SnapshotByFamily()[(int)SymbolLookupMethodFamily.Search].CallCount);
    }

    [Fact]
    public void ContextSearchCacheStopsCachingNewRequestsAtItsFixedCapacity()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        ISymbolLookupIndex inner = SymbolSearchProjectionLoader.Load(fixture.DbPath);
        var measured = new MeasuredSymbolLookupIndex(inner);
        var cache = new ContextSearchCacheLookupIndex(measured);

        for (int index = 0; index < 65; index++)
            _ = cache.Search($"Qzxwplmn{index}", 10, SearchMode.Or);
        _ = cache.Search("Qzxwplmn64", 10, SearchMode.Or);
        _ = cache.Search("Qzxwplmn0", 10, SearchMode.Or);

        Assert.Equal(
            66,
            measured.SnapshotByFamily()[(int)SymbolLookupMethodFamily.Search].CallCount);
    }

    [Fact]
    public void ContextSearchCacheDoesNotRetainOneResultBeyondItsRowBudget()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        ISymbolLookupIndex inner = SymbolSearchProjectionLoader.Load(fixture.DbPath);
        SearchHit hit = Assert.Single(inner.Search("TargetType"));
        var backend = new FixedSearchResultLookupIndex(inner, Enumerable.Repeat(hit, 10_001).ToArray());
        var cache = new ContextSearchCacheLookupIndex(backend);

        _ = cache.Search("TargetType", 10_001, SearchMode.Or);
        _ = cache.Search("TargetType", 10_001, SearchMode.Or);

        Assert.Equal(2, backend.SearchCallCount);
    }

    [Fact]
    public void ContextSearchCacheDoesNotRetainNewResultAfterCumulativeRowBudgetIsExhausted()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        ISymbolLookupIndex inner = SymbolSearchProjectionLoader.Load(fixture.DbPath);
        SearchHit hit = Assert.Single(inner.Search("TargetType"));
        var backend = new FixedSearchResultLookupIndex(inner, Enumerable.Repeat(hit, 6_000).ToArray());
        var cache = new ContextSearchCacheLookupIndex(backend);

        _ = cache.Search("First", 6_000, SearchMode.Or);
        _ = cache.Search("Second", 6_000, SearchMode.Or);
        _ = cache.Search("Second", 6_000, SearchMode.Or);
        _ = cache.Search("First", 6_000, SearchMode.Or);

        Assert.Equal(3, backend.SearchCallCount);
    }

    [Fact]
    public void ContextSearchCacheReportsAvoidedWorkWithoutInventingBackendElapsedTime()
    {
        using var fixture = DbWithSymbol("current-ws", revision: 1, "TargetType");
        ISymbolLookupIndex inner = SymbolSearchProjectionLoader.Load(fixture.DbPath);
        var measured = new MeasuredSymbolLookupIndex(inner);
        var telemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using IDisposable searchTelemetry = telemetry.ActivateSearchTelemetry();
        var cache = new ContextSearchCacheLookupIndex(measured);

        _ = cache.Search("TargetType", 10, SearchMode.Or);
        IReadOnlyList<SearchHit> repeated = cache.Search("TargetType", 10, SearchMode.Or);
        ContextLookupPhaseObservation observation =
            telemetry.CompleteLookupPhase(ContextLookupPhase.AnchorResolution);

        Assert.Equal(1, observation.Delta.Search.CallCount);
        Assert.Equal(1, observation.SearchDelta.FirstQuery.CallCount);
        Assert.Equal(1, observation.SearchDelta.CacheHit.CallCount);
        Assert.Equal(0, observation.SearchDelta.CacheHit.ElapsedMilliseconds);
        Assert.Equal(repeated.Count, observation.SearchDelta.CacheHit.ReturnedRowCount);
        Assert.Equal(1, observation.SearchDelta.Or.CallCount);
    }

    [Fact]
    public void RepeatedCurrentFamilyReadsKeepCachedIdentityAndReportOnlyTheirOwnLookupDelta()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("current-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("current-store-telemetry-delta");
        var provider = NewProvider(
            new IndexHolder(
                () => throw new InvalidOperationException("holder repository was not expected"),
                builtRevision: 12,
                documentCount: 1,
                knownExtensionsCount: 1),
            CurrentWorkspaceAt(root, current.DbPath, "current-ws"),
            registry,
            openReadSession: (_, _, _) => new WorkspaceReadHandle(
                new FixtureReadSession(StoreSnapshot(root, "manifest-a"), target.DbPath)),
            loadSessionSymbolSearch: session => SymbolSearchProjectionLoader.LoadSession(session));
        using var ledger = TelemetryLedger.Open(Path.Combine(_dir, "telemetry-delta.db"), "current-ws", root);
        ISymbolLookupIndex firstIndex;

        using (TelemetryScope firstScope = ledger.Measure("context", op: null))
        using (WorkspaceReadContext first = provider.Resolve(workspaceId: null, ensureFresh: false))
        {
            firstIndex = first.Index;
            Assert.Single(first.Index.FindByName("TargetType"));
            ReadToolWorkspaceRouting.ApplyTelemetry(firstScope, first);
            using JsonDocument metadata = JsonDocument.Parse(firstScope.MetadataJson);
            Assert.Equal(1, metadata.RootElement.GetProperty("read_lookup_count").GetInt64());
        }

        using (TelemetryScope secondScope = ledger.Measure("context", op: null))
        using (WorkspaceReadContext second = provider.Resolve(workspaceId: null, ensureFresh: false))
        {
            Assert.Same(firstIndex, second.Index);
            Assert.Single(second.Index.FindByName("TargetType"));
            ReadToolWorkspaceRouting.ApplyTelemetry(secondScope, second);
            using JsonDocument metadata = JsonDocument.Parse(secondScope.MetadataJson);
            Assert.Equal(1, metadata.RootElement.GetProperty("read_lookup_count").GetInt64());
        }
    }

    [Fact]
    public void RepeatedCurrentFamilyReadRoutesKeepOneBoundedLookupAndNoRepositoryOrGraphGeneration()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("current-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("current-store-repeated-reads");
        int holderLoads = 0;
        int lookupLoads = 0;
        var holder = new IndexHolder(
            () =>
            {
                holderLoads++;
                throw new InvalidOperationException("holder repository was not expected");
            },
            builtRevision: 12,
            documentCount: 1,
            knownExtensionsCount: 1);
        var provider = NewProvider(
            holder,
            CurrentWorkspaceAt(root, current.DbPath, "current-ws"),
            registry,
            sidecar: new SymbolSearchSidecar(enabled: true),
            openReadSession: (_, _, _) => new WorkspaceReadHandle(
                new FixtureReadSession(StoreSnapshot(root, "manifest-a"), target.DbPath)),
            loadSessionSymbolSearch: _ =>
            {
                lookupLoads++;
                return SymbolSearchProjectionLoader.Load(target.DbPath);
            },
            openStoreSymbolSearch: _ =>
            {
                lookupLoads++;
                return SymbolSearchProjectionLoader.Load(target.DbPath);
            });
        ISymbolGraphReachability? priorGraph = null;

        for (int iteration = 0; iteration < 32; iteration++)
        {
            for (int graphRoute = 0; graphRoute < 3; graphRoute++)
            {
                using WorkspaceReadContext context = provider.Resolve(workspaceId: null, ensureFresh: false);
                Assert.IsType<MeasuredSymbolGraphReachability>(context.Graph);
                Assert.False(context.BridgeGraph.IsValueCreated);
                if (priorGraph is not null)
                    Assert.NotSame(priorGraph, context.Graph);
                priorGraph = context.Graph;
            }

            using WorkspaceSymbolReadContext inspect =
                provider.ResolveSymbolRead(workspaceId: null, ensureFresh: false);
            Assert.Single(inspect.Index.FindByName("TargetType"));
        }

        WorkspaceIndexProviderCacheSnapshot cache = provider.CacheSnapshot();
        Assert.Equal(0, holderLoads);
        Assert.Equal(1, lookupLoads);
        Assert.Equal(0, cache.RepositoryEntries);
        Assert.Equal(1, cache.SymbolSearchEntries + cache.SymbolReadEntries);
        Assert.Equal(1, cache.TotalEntries);
    }

    [Fact]
    public void Resolve_CurrentLegacySwapDuringSessionOpenUsesOneAtomicHolderGeneration()
    {
        using var before = DbWithSymbol("current-ws", revision: 1, "BeforeType");
        using var after = DbWithSymbol("current-ws", revision: 2, "AfterType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        var holder = new IndexHolder(RepositoryIndexLoader.Load(before.DbPath), builtRevision: 1);
        var provider = NewProvider(
            holder,
            CurrentWorkspace(before.DbPath, "current-ws"),
            registry,
            openReadSession: (databasePath, root, workspaceId) =>
            {
                holder.Swap(RepositoryIndexLoader.Load(after.DbPath), revision: 2);
                return new WorkspaceReadHandle(LegacyArtifactReadSession.Open(databasePath, root, workspaceId));
            });

        using WorkspaceReadContext context = provider.Resolve(workspaceId: null, ensureFresh: false);

        Assert.Equal(2, context.Revision);
        Assert.Single(context.Index.FindByName("AfterType"));
        Assert.Empty(context.Index.FindByName("BeforeType"));
    }

    [Fact]
    public void Resolve_CurrentWorkspace_ReconcilesMalformedStorePointerBeforeRetryingTheRead()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("current-malformed-pointer");
        int openCount = 0;
        int refreshCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspaceAt(root, current.DbPath, "current-ws"),
            registry,
            refresh: workspaceId =>
            {
                refreshCount++;
                return new WorkspaceRefreshResult(
                    WorkspaceRefreshStatus.Refreshed,
                    workspaceId,
                    root,
                    current.DbPath,
                    Revision: 1,
                    Scanned: true);
            },
            openReadSession: (databasePath, workspaceRoot, workspaceId) =>
            {
                if (++openCount == 1)
                    throw new StorePointerFormatException("malformed");
                return new WorkspaceReadHandle(LegacyArtifactReadSession.Open(
                    databasePath,
                    workspaceRoot,
                    workspaceId));
            });

        using WorkspaceReadContext context = provider.Resolve(workspaceId: null, ensureFresh: false);

        Assert.Equal(2, openCount);
        Assert.Equal(1, refreshCount);
        Assert.Equal(WorkspaceReadMode.LegacyArtifact, context.Snapshot.Mode);
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
    public void Resolve_RegisteredFamilyStoreCachesBoundedLookupByManifestAndNeverLoadsFullIndex()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-store-cache");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        WorkspaceReadSnapshot snapshot = StoreSnapshot(root, "manifest-a");
        int loadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadIndex: _ => throw new InvalidOperationException("legacy artifact load was not expected"),
            openReadSession: (_, _, _) => new WorkspaceReadHandle(new StubReadSession(snapshot)),
            loadSessionIndex: _ => throw new InvalidOperationException("full session index load was not expected"),
            loadSessionSymbolSearch: _ =>
            {
                loadCount++;
                return SymbolSearchProjectionLoader.Load(target.DbPath);
            });

        using WorkspaceReadContext first = provider.Resolve("target-ws", ensureFresh: false);
        using WorkspaceReadContext second = provider.Resolve("target-ws", ensureFresh: false);
        Assert.Equal(12, first.Revision);
        snapshot = StoreSnapshot(root, "manifest-b");
        using WorkspaceReadContext afterManifestChange = provider.Resolve("target-ws", ensureFresh: false);
        snapshot = StoreSnapshot(root, "manifest-b", storeLogSequence: 13);
        using WorkspaceReadContext afterStoreLogChange = provider.Resolve("target-ws", ensureFresh: false);
        snapshot = StoreSnapshot(root, "manifest-b", IndexLevels.SymbolsMetadataValue);
        using WorkspaceReadContext afterLevelChange = provider.Resolve("target-ws", ensureFresh: false);

        Assert.Equal(WorkspaceReadMode.FamilyStore, first.Snapshot.Mode);
        Assert.Equal("manifest-a", first.Snapshot.Freshness.ManifestHash);
        Assert.Same(first.Index, second.Index);
        Assert.NotSame(first.Index, afterManifestChange.Index);
        Assert.NotSame(afterManifestChange.Index, afterStoreLogChange.Index);
        Assert.NotSame(afterManifestChange.Index, afterLevelChange.Index);
        Assert.Equal("manifest-b", afterManifestChange.Snapshot.Freshness.ManifestHash);
        Assert.Equal(13, afterStoreLogChange.Revision);
        Assert.Equal(IndexLevels.SymbolsMetadataValue, afterLevelChange.Snapshot.IndexLevel);
        Assert.Equal(4, loadCount);
    }

    [Fact]
    public void Resolve_RegisteredFamilyStoreUsesBoundedLookupAndNeverLoadsFullIndex()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-store-bounded-read");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        WorkspaceReadSnapshot snapshot = StoreSnapshot(root, "manifest-a");
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadIndex: _ => throw new InvalidOperationException("legacy artifact load was not expected"),
            openReadSession: (_, _, _) => new WorkspaceReadHandle(
                new FixtureReadSession(snapshot, target.DbPath)),
            loadSessionIndex: _ => throw new InvalidOperationException("full session index load was not expected"));

        using WorkspaceReadContext context = provider.Resolve("target-ws", ensureFresh: false);

        Assert.Single(context.Index.FindByName("TargetType"));
        Assert.IsType<TargetResolution.Symbol>(context.Resolver.Resolve("TargetType"));
    }

    [Fact]
    public void Resolve_RegisteredFamilyStoreDefersBridgeLoadAndCachesItWithinContext()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-store-lazy-bridge");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        WorkspaceReadSnapshot snapshot = StoreSnapshot(root, "manifest-a");
        int bridgeLoadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            openReadSession: (_, _, _) => new WorkspaceReadHandle(
                new FixtureReadSession(snapshot, target.DbPath)),
            loadSessionBridgeGraph: _ =>
            {
                bridgeLoadCount++;
                return BridgeGraph.Build([], new Dictionary<string, BridgeNode>(StringComparer.Ordinal));
            });

        using WorkspaceReadContext context = provider.Resolve("target-ws", ensureFresh: false);

        Assert.Equal(0, bridgeLoadCount);
        Assert.Same(context.BridgeGraph.Value, context.BridgeGraph.Value);
        Assert.Equal(1, bridgeLoadCount);
    }

    [Fact]
    public void ResolveSymbolRead_RegisteredFamilyStoreUsesGenerationCheckedSidecarRoute()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-store-symbol-read");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);
        WorkspaceReadSnapshot snapshot = StoreSnapshot(root, "manifest-a");
        int lookupLoads = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            sidecar: new SymbolSearchSidecar(enabled: true),
            openReadSession: (_, _, _) => new WorkspaceReadHandle(new StubReadSession(snapshot)),
            loadSessionSymbolSearch: _ =>
            {
                lookupLoads++;
                return SymbolSearchProjectionLoader.Load(target.DbPath);
            },
            openStoreSymbolSearch: _ =>
            {
                lookupLoads++;
                return SymbolSearchProjectionLoader.Load(target.DbPath);
            });

        using WorkspaceSymbolReadContext context = provider.ResolveSymbolRead("target-ws", ensureFresh: false);
        using WorkspaceSymbolReadContext currentContext = provider.ResolveSymbolRead(null, ensureFresh: false);

        Assert.Single(context.Index.FindByName("TargetType"));
        Assert.Single(currentContext.Index.FindByName("TargetType"));
        Assert.Equal(2, lookupLoads);
    }

    [Fact]
    public void ResolveSymbolRead_FamilyStoreLastGood_UsesGenerationProjection()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("inspect-lg");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);
        WorkspaceReadSnapshot lastGood = StoreSnapshot(root, "manifest-a", storeLogSequence: 17);
        WorkspaceReadSnapshot live = StoreSnapshot(root, "manifest-a", storeLogSequence: 21) with
        {
            ResolutionState = "exact",
        };
        string searchPath = StoreSidecarCatalog.PathFor(root, StoreSidecarKind.Search, lastGood.ViewId);
        WriteEmptySqlite(searchPath);
        StoreSidecarCatalog.Stamp(
            searchPath,
            StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, lastGood));

        int storeOpens = 0;
        int generationLoads = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            sidecar: new SymbolSearchSidecar(enabled: true),
            openReadSession: (_, _, _) => new WorkspaceReadHandle(new StubReadSession(live)),
            loadSessionSymbolSearch: _ =>
            {
                generationLoads++;
                return SymbolSearchProjectionLoader.Load(target.DbPath);
            },
            openStoreSymbolSearch: _ =>
            {
                storeOpens++;
                throw new InvalidOperationException("named inspect must not open last-good search");
            });

        using WorkspaceSymbolReadContext context = provider.ResolveSymbolRead("target-ws", ensureFresh: false);

        Assert.Single(context.Index.FindByName("TargetType"));
        Assert.Equal(0, storeOpens);
        Assert.Equal(1, generationLoads);
    }

    [Fact]
    public void ResolveSymbolSearch_FamilyStoreLastGood_ReopensWhenSidecarStampCatchesUp()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("search-cu");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);
        WorkspaceReadSnapshot lastGood = StoreSnapshot(root, "manifest-a", storeLogSequence: 17);
        WorkspaceReadSnapshot live = StoreSnapshot(root, "manifest-a", storeLogSequence: 21) with
        {
            ResolutionState = "exact",
        };
        string searchPath = StoreSidecarCatalog.PathFor(root, StoreSidecarKind.Search, lastGood.ViewId);
        Directory.CreateDirectory(Path.GetDirectoryName(searchPath)!);
        SearchIndexWriter.Write(searchPath, SqliteSymbolReader.Read(target.DbPath), revision: 17);
        StoreSidecarCatalog.Stamp(
            searchPath,
            StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, lastGood));

        var sidecar = new SymbolSearchSidecar(enabled: true, RegionIndexOptions.Disabled);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            sidecar: sidecar,
            openReadSession: (_, _, _) => new WorkspaceReadHandle(new StubReadSession(live)),
            openStoreSymbolSearch: session => sidecar.OpenStoreRequired(root, session.Snapshot));

        using WorkspaceSymbolSearchContext first = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);
        Assert.Single(first.Index.FindByName("TargetType"));
        Assert.Empty(first.Index.FindByName("FreshType"));

        using var fresh = DbWithSymbol("target-ws", revision: 1, "FreshType");
        SearchIndexWriter.Write(searchPath, SqliteSymbolReader.Read(fresh.DbPath), revision: 21);
        StoreSidecarCatalog.Stamp(
            searchPath,
            StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, live));

        using WorkspaceSymbolSearchContext second = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);

        Assert.NotSame(first.Index, second.Index);
        Assert.Empty(second.Index.FindByName("TargetType"));
        Assert.Single(second.Index.FindByName("FreshType"));
    }

    [Fact]
    public void Resolve_RegisteredWorkspace_ReloadsAfterARefreshedScanThatDidNotAdvanceTheRevision()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-rebuild");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int loadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            refresh: workspaceId => new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.Refreshed,
                workspaceId,
                root,
                target.DbPath,
                Revision: 1,
                Scanned: true),
            loadIndex: path =>
            {
                loadCount++;
                return RepositoryIndexLoader.Load(path);
            });

        WorkspaceReadContext first = provider.Resolve("target-ws", ensureFresh: false);
        // A force rebuild recreates the DB from scratch, so its revision counter can land on the SAME
        // number the cache key already holds — the entry must be evicted, not trusted to age out by key.
        WorkspaceReadContext second = provider.Resolve("target-ws", ensureFresh: true);

        Assert.Equal(1, second.Revision);
        Assert.NotSame(first.Index, second.Index);
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public void Resolve_RegisteredWorkspace_ReloadsAfterAnExternalRebuildAtTheSameRevision()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var rebuilt = DbWithSymbol("target-ws", revision: 1, "RebuiltTargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-external-rebuild");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int loadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            refresh: workspaceId => new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.Unchanged,
                workspaceId,
                root,
                target.DbPath,
                Revision: 1,
                Scanned: true),
            loadIndex: path =>
            {
                loadCount++;
                return RepositoryIndexLoader.Load(path);
            });

        WorkspaceReadContext first = provider.Resolve("target-ws", ensureFresh: false);

        // ANOTHER process force-rebuilds the workspace: the DB file is deleted and recreated, and the fresh
        // artifact's restarted revision counter lands on the number this process already cached. This
        // process's own next scan legitimately reports no_change (the sources did not change after the
        // rebuild), so eviction-on-Refreshed never fires — only the file identity baked into the cache key
        // can catch the rewrite.
        File.Copy(rebuilt.DbPath, target.DbPath, overwrite: true);
        File.SetLastWriteTimeUtc(target.DbPath, File.GetLastWriteTimeUtc(target.DbPath).AddSeconds(7));

        WorkspaceReadContext second = provider.Resolve("target-ws", ensureFresh: true);

        Assert.Equal(1, second.Revision);
        Assert.NotSame(first.Index, second.Index);
        Assert.NotEmpty(second.Index.FindByName("RebuiltTargetType"));
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
        Assert.False(context.IsCurrent);
        Assert.Equal(1, searchLoadCount);
        Assert.Equal(0, fullLoadCount);
        var hit = Assert.Single(context.Index.Search("TargetType", limit: 10));
        Assert.Equal("TargetType", context.Index.Resolve(hit.Document.DocId).Name);
    }

    [Fact]
    public void ResolveArtifact_RegisteredWorkspace_ReturnsDbFactsWithoutFullOrProjectionLoads()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 4, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-artifact");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 4);

        int fullLoadCount = 0;
        int searchLoadCount = 0;
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
                searchLoadCount++;
                throw new InvalidOperationException("symbol loader was not expected");
            },
            loadContentSearch: (_, _) =>
            {
                contentLoadCount++;
                throw new InvalidOperationException("content loader was not expected");
            });

        WorkspaceArtifactContext context = provider.ResolveArtifact("target-ws", ensureFresh: false);

        Assert.Equal("target-ws", context.WorkspaceId);
        Assert.Equal("target-111111111111", context.DisplayId);
        Assert.Equal(target.DbPath, context.ReadSession.LegacyArtifactPath);
        Assert.Equal(root, context.WorkspaceRoot);
        Assert.Equal(4, context.Revision);
        Assert.Equal(0, fullLoadCount);
        Assert.Equal(0, searchLoadCount);
        Assert.Equal(0, contentLoadCount);
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
    public void ResolveSymbolSearch_CurrentDisplayIdPrefixRoutesToServedWorkspaceWithoutRefresh()
    {
        const string currentId = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
        using var current = DbWithSymbol(currentId, revision: 1, "CurrentType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = Path.Combine(_dir, "current-display-prefix");
        Directory.CreateDirectory(root);
        string displayId = WorkspaceId.Display(root, currentId);
        registry.UpsertSeen(currentId, displayId, root, current.DbPath);
        registry.MarkScanned(currentId, revision: 1);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspaceAt(root, current.DbPath, currentId),
            registry,
            refresh: _ => throw new InvalidOperationException("current display prefix should not refresh through registry"));

        WorkspaceSymbolSearchContext context = provider.ResolveSymbolSearch("current-display-prefix-abcdef", ensureFresh: true);

        Assert.Equal(currentId, context.WorkspaceId);
        Assert.Equal(displayId, context.DisplayId);
        Assert.Equal("current", context.FreshnessStatus);
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
    public void ResolveSymbolSearch_Registered_SidecarEnabledAndFresh_RoutesToDiskIndexAndCachesIt()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        WriteSearchDbFor(target, revision: 1);
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-sidecar-fresh");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadSymbolSearch: _ => throw new InvalidOperationException("in-memory fallback must not run when the sidecar serves"),
            sidecar: new SymbolSearchSidecar(enabled: true, RegionIndexOptions.Disabled));

        WorkspaceSymbolSearchContext first = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);
        WorkspaceSymbolSearchContext second = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);

        Assert.IsType<FtsSymbolSearchIndex>(first.Index);
        Assert.Same(first.Index, second.Index);   // opened once, cached by (workspace, dbPath, revision)
        var hit = Assert.Single(first.Index.Search("TargetType", limit: 10));
        Assert.Equal("TargetType", first.Index.Resolve(hit.Document.DocId).Name);
    }

    [Fact]
    public void ResolveSymbolSearch_Registered_SidecarEnabled_RevisionBumpEvictsAndReRoutesToFreshArtifact()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        WriteSearchDbFor(target, revision: 1);
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-sidecar-revbump");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadSymbolSearch: _ => throw new InvalidOperationException("fallback must not run while a fresh sidecar exists"),
            sidecar: new SymbolSearchSidecar(enabled: true, RegionIndexOptions.Disabled));

        WorkspaceSymbolSearchContext first = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);
        Assert.IsType<FtsSymbolSearchIndex>(first.Index);
        Assert.Equal(1, first.Revision);

        // A new extract revision lands and the leader rebuilds search.db at revision 2: the revision-keyed cache
        // must evict the stale revision-1 reader and re-route to the fresh artifact, not serve the cached one.
        registry.MarkScanned("target-ws", revision: 2);
        WriteSearchDbFor(target, revision: 2);
        WorkspaceSymbolSearchContext second = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);

        Assert.IsType<FtsSymbolSearchIndex>(second.Index);
        Assert.Equal(2, second.Revision);
        Assert.NotSame(first.Index, second.Index);
    }

    [Fact]
    public void ResolveSymbolSearch_Registered_SidecarEnabledButArtifactMissing_FailsVisibly()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        // No search.db written next to target.DbPath — the sidecar must fail visibly, not allocate a projection.
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-sidecar-missing");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadSymbolSearch: _ => throw new InvalidOperationException("in-memory fallback must not run"),
            sidecar: new SymbolSearchSidecar(enabled: true));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            provider.ResolveSymbolSearch("target-ws", ensureFresh: false));

        Assert.Contains("Search sidecar is enabled but missing", ex.Message);
        Assert.Contains("workspace refresh", ex.Message);
    }

    [Fact]
    public void ResolveSymbolSearch_Registered_SidecarEnabled_OpensArtifactCreatedAfterMissingFailure()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-sidecar-repair");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadSymbolSearch: _ => throw new InvalidOperationException("in-memory fallback must not run"),
            sidecar: new SymbolSearchSidecar(enabled: true));

        Assert.Throws<InvalidOperationException>(() =>
            provider.ResolveSymbolSearch("target-ws", ensureFresh: false));
        WriteSearchDbFor(target, revision: 1);
        WorkspaceSymbolSearchContext second = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);

        Assert.IsType<FtsSymbolSearchIndex>(second.Index);
        var hit = Assert.Single(second.Index.Search("TargetType", limit: 10));
        Assert.Equal("TargetType", second.Index.Resolve(hit.Document.DocId).Name);
    }

    [Fact]
    public void ResolveSymbolSearch_Registered_SidecarEnabledButArtifactStale_FailsVisibly()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 2, "TargetType");
        WriteSearchDbFor(target, revision: 1);   // artifact one revision behind the registry's view (2)
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-sidecar-stale");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 2);

        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadSymbolSearch: _ => throw new InvalidOperationException("in-memory fallback must not run"),
            sidecar: new SymbolSearchSidecar(enabled: true));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            provider.ResolveSymbolSearch("target-ws", ensureFresh: false));

        Assert.Contains("Search sidecar", ex.Message);
        Assert.Contains("stale", ex.Message);
        Assert.Contains("expected 2", ex.Message);
    }

    [Fact]
    public void ResolveSymbolSearch_Current_SidecarEnabledAndFresh_RoutesToDiskIndex()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        WriteSearchDbFor(current, revision: 1);
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspaceAt(current.Directory, current.DbPath, "current-ws"),
            registry,
            sidecar: new SymbolSearchSidecar(enabled: true));

        WorkspaceSymbolSearchContext context = provider.ResolveSymbolSearch(workspaceId: null, ensureFresh: false);

        Assert.IsType<FtsSymbolSearchIndex>(context.Index);
        var hit = Assert.Single(context.Index.Search("CurrentType", limit: 10));
        Assert.Equal("CurrentType", context.Index.Resolve(hit.Document.DocId).Name);
    }

    [Fact]
    public void ResolveSymbolSearch_Current_SidecarDisabled_UsesHolderIndexEvenWhenArtifactPresent()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        WriteSearchDbFor(current, revision: 1);   // a fresh artifact exists, but the flag is off by default
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        var holder = new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1);
        var provider = NewProvider(
            holder,
            CurrentWorkspaceAt(current.Directory, current.DbPath, "current-ws"),
            registry);   // default sidecar = Disabled

        WorkspaceSymbolSearchContext context = provider.ResolveSymbolSearch(workspaceId: null, ensureFresh: false);

        Assert.IsType<MillerRepositoryIndex>(context.Index);
        Assert.Same(holder.Current, context.Index);
    }

    [Fact]
    public void ResolveSymbolSearch_Current_SidecarEnabledButArtifactMissing_FailsVisibly()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        // No search.db — the current path must fail visibly instead of silently using the holder index.
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        var holder = new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1);
        var provider = NewProvider(
            holder,
            CurrentWorkspaceAt(current.Directory, current.DbPath, "current-ws"),
            registry,
            sidecar: new SymbolSearchSidecar(enabled: true));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            provider.ResolveSymbolSearch(workspaceId: null, ensureFresh: false));

        Assert.Contains("Search sidecar is enabled but missing", ex.Message);
    }

    [Fact]
    public void ResolveSymbolSearch_Current_SidecarEnabled_OpensArtifactCreatedAfterMissingFailure()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        var holder = new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1);
        var provider = NewProvider(
            holder,
            CurrentWorkspaceAt(current.Directory, current.DbPath, "current-ws"),
            registry,
            sidecar: new SymbolSearchSidecar(enabled: true));

        Assert.Throws<InvalidOperationException>(() =>
            provider.ResolveSymbolSearch(workspaceId: null, ensureFresh: false));
        WriteSearchDbFor(current, revision: 1);
        WorkspaceSymbolSearchContext second = provider.ResolveSymbolSearch(workspaceId: null, ensureFresh: false);

        Assert.IsType<FtsSymbolSearchIndex>(second.Index);
        var hit = Assert.Single(second.Index.Search("CurrentType", limit: 10));
        Assert.Equal("CurrentType", second.Index.Resolve(hit.Document.DocId).Name);
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

        using var ledger = TelemetryLedger.Open(Path.Combine(_dir, "content-telemetry.db"), "current-ws");
        using var scope = ledger.Measure("content", op: null);

        WorkspaceContentSearchContext context = provider.ResolveContentSearch("target-ws", ensureFresh: false);

        Assert.Equal("target-ws", context.WorkspaceId);
        Assert.Equal(1, context.Revision);
        Assert.Equal(1, contentLoadCount);
        Assert.Equal(0, fullLoadCount);
        Assert.Equal(0, symbolLoadCount);
        var hit = Assert.Single(context.Index.Search("freshness", limit: 10));
        Assert.Equal("docs/guide.md", hit.Path);
        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.Equal("index_load", metadata.RootElement.GetProperty("wait_reason").GetString());
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
    public void ResolveTextContentSearch_RegisteredWorkspace_UsesFreshContentDbAndCachesIt()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSource("target-ws", revision: 1, "KnownSourceError");
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(target.DbPath),
            target.DbPath,
            target.WorkspaceRoot,
            workspaceId: "target-ws",
            revision: 1);
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", target.WorkspaceRoot, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int textLoadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadTextContentSearch: (dbPath, revision) =>
            {
                textLoadCount++;
                return FtsTextContentSearchIndex.Open(ContentCorpusSidecar.ContentDbPathFor(dbPath), revision);
            });

        WorkspaceTextContentSearchContext first = provider.ResolveTextContentSearch("target-ws", ensureFresh: false);
        WorkspaceTextContentSearchContext second = provider.ResolveTextContentSearch("target-ws", ensureFresh: false);

        Assert.Equal("target-ws", first.WorkspaceId);
        Assert.Equal(1, first.Revision);
        Assert.False(first.IsCurrent);
        Assert.Same(first.Index, second.Index);
        Assert.Equal(1, textLoadCount);
        TextContentSearchHit hit = Assert.Single(first.Index.Search(
            "KnownSourceError",
            TextContentKind.WorkspaceSource,
            limit: 10));
        Assert.Equal("src/Source.cs", hit.Path);
    }

    [Fact]
    public void ResolveTextContentSearch_CurrentWorkspace_UsesHolderRevisionAndCachesIt()
    {
        using var fx = DbWithSource("current-ws", revision: 7, "KnownSourceError");
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(fx.DbPath),
            fx.DbPath,
            fx.WorkspaceRoot,
            workspaceId: "current-ws",
            revision: 7);
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        var holder = new IndexHolder(RepositoryIndexLoader.Load(fx.DbPath), builtRevision: 7);

        int textLoadCount = 0;
        var provider = NewProvider(
            holder,
            CurrentWorkspaceAt(fx.WorkspaceRoot, fx.DbPath, "current-ws"),
            registry,
            loadTextContentSearch: (dbPath, revision) =>
            {
                textLoadCount++;
                return FtsTextContentSearchIndex.Open(ContentCorpusSidecar.ContentDbPathFor(dbPath), revision);
            });
        var measured = new MeasuredSymbolLookupIndex(SymbolSearchProjectionLoader.Load(fx.DbPath));
        var telemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using IDisposable activation = telemetry.ActivateSearchTelemetry();

        WorkspaceTextContentSearchContext byNull = provider.ResolveTextContentSearch(workspaceId: null, ensureFresh: false);
        WorkspaceTextContentSearchContext byId = provider.ResolveTextContentSearch("current-ws", ensureFresh: false);
        ContextLookupPhaseObservation observation =
            telemetry.CompleteLookupPhase(ContextLookupPhase.SourceRescue);

        Assert.Equal(7, byNull.Revision);
        Assert.Equal("current", byNull.FreshnessStatus);
        Assert.True(byNull.IsCurrent);
        Assert.True(byId.IsCurrent);
        Assert.Same(byNull.Index, byId.Index);
        Assert.Equal(1, textLoadCount);
        Assert.Single(byNull.Index.Search("KnownSourceError", TextContentKind.WorkspaceSource, 10));
        Assert.Equal(2, observation.TextContentIndexResolveDelta.Resolve.CallCount);
        Assert.Equal(2, observation.TextContentIndexResolveDelta.ReadSessionOpen.CallCount);
        Assert.Equal(1, observation.TextContentIndexResolveDelta.CacheHit.CallCount);
        Assert.Equal(1, observation.TextContentIndexResolveDelta.CacheMiss.CallCount);
        Assert.Equal(1, observation.TextContentIndexResolveDelta.IndexLoad.CallCount);
    }

    [Fact]
    public void ResolveTextContentSearch_CurrentFamilyStoreCachesBySnapshotAndReportsFixedResolveStages()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSource("current-ws", revision: 1, "KnownSourceError");
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(target.DbPath),
            target.DbPath,
            target.WorkspaceRoot,
            workspaceId: "current-ws",
            revision: 1);
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("current-store-content-cache");
        WorkspaceReadSnapshot snapshot = StoreSnapshot(root, "manifest-a");
        int loadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 12),
            CurrentWorkspaceAt(root, current.DbPath, "current-ws"),
            registry,
            openReadSession: (_, _, _) => new WorkspaceReadHandle(new StubReadSession(snapshot)),
            openStoreTextContentSearch: _ =>
            {
                loadCount++;
                return FtsTextContentSearchIndex.Open(
                    ContentCorpusSidecar.ContentDbPathFor(target.DbPath),
                    1);
            });
        var measured = new MeasuredSymbolLookupIndex(SymbolSearchProjectionLoader.Load(current.DbPath));
        var telemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using IDisposable activation = telemetry.ActivateSearchTelemetry();

        WorkspaceTextContentSearchContext first =
            provider.ResolveTextContentSearch(workspaceId: null, ensureFresh: false);
        WorkspaceTextContentSearchContext cached =
            provider.ResolveTextContentSearch("current-ws", ensureFresh: false);
        snapshot = StoreSnapshot(root, "manifest-b");
        WorkspaceTextContentSearchContext changed =
            provider.ResolveTextContentSearch(workspaceId: null, ensureFresh: false);
        ContextLookupPhaseObservation observation =
            telemetry.CompleteLookupPhase(ContextLookupPhase.SourceRescue);

        Assert.Same(first.Index, cached.Index);
        Assert.NotSame(first.Index, changed.Index);
        Assert.Equal(2, loadCount);
        Assert.Equal(3, observation.TextContentIndexResolveDelta.Resolve.CallCount);
        Assert.Equal(3, observation.TextContentIndexResolveDelta.ReadSessionOpen.CallCount);
        Assert.Equal(1, observation.TextContentIndexResolveDelta.CacheHit.CallCount);
        Assert.Equal(2, observation.TextContentIndexResolveDelta.CacheMiss.CallCount);
        Assert.Equal(2, observation.TextContentIndexResolveDelta.IndexLoad.CallCount);
    }

    [Fact]
    public async Task ResolveTextContentSearch_ConcurrentFamilyStoreResolvesLoadOneReader()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSource("current-ws", revision: 1, "KnownSourceError");
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(target.DbPath),
            target.DbPath,
            target.WorkspaceRoot,
            workspaceId: "current-ws",
            revision: 1);
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("current-store-content-concurrent");
        WorkspaceReadSnapshot snapshot = StoreSnapshot(root, "manifest-a");
        using var loadEntered = new ManualResetEventSlim();
        using var releaseLoad = new ManualResetEventSlim();
        int loadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 12),
            CurrentWorkspaceAt(root, current.DbPath, "current-ws"),
            registry,
            openReadSession: (_, _, _) => new WorkspaceReadHandle(new StubReadSession(snapshot)),
            openStoreTextContentSearch: _ =>
            {
                Interlocked.Increment(ref loadCount);
                loadEntered.Set();
                Assert.True(releaseLoad.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                return FtsTextContentSearchIndex.Open(
                    ContentCorpusSidecar.ContentDbPathFor(target.DbPath),
                    1);
            });
        var measured = new MeasuredSymbolLookupIndex(SymbolSearchProjectionLoader.Load(current.DbPath));
        var telemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using IDisposable activation = telemetry.ActivateSearchTelemetry();
        TextContentIndexResolveTelemetryCollector collector =
            TextContentIndexResolveTelemetryCollector.Current!;

        Task<WorkspaceTextContentSearchContext> first = RunBlockingResolve(
            () => provider.ResolveTextContentSearch(workspaceId: null, ensureFresh: false));
        Assert.True(loadEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Task<WorkspaceTextContentSearchContext> second = RunBlockingResolve(
            () => provider.ResolveTextContentSearch("current-ws", ensureFresh: false));
        Assert.True(SpinWait.SpinUntil(
            () => collector.Snapshot().CacheHit.CallCount == 1,
            TimeSpan.FromSeconds(5)));
        releaseLoad.Set();

        WorkspaceTextContentSearchContext[] contexts = await Task.WhenAll(first, second);
        ContextLookupPhaseObservation observation =
            telemetry.CompleteLookupPhase(ContextLookupPhase.SourceRescue);

        Assert.Same(contexts[0].Index, contexts[1].Index);
        Assert.Equal(1, loadCount);
        Assert.Equal(2, observation.TextContentIndexResolveDelta.Resolve.CallCount);
        Assert.Equal(2, observation.TextContentIndexResolveDelta.ReadSessionOpen.CallCount);
        Assert.Equal(1, observation.TextContentIndexResolveDelta.CacheHit.CallCount);
        Assert.Equal(1, observation.TextContentIndexResolveDelta.CacheMiss.CallCount);
        Assert.Equal(1, observation.TextContentIndexResolveDelta.IndexLoad.CallCount);
    }

    [Fact]
    public void ResolveTextContentSearch_FailedLoadReportsOnlyCompletedStagesAndDoesNotPoisonCache()
    {
        using var fx = DbWithSource("current-ws", revision: 7, "KnownSourceError");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(fx.DbPath), builtRevision: 7),
            CurrentWorkspaceAt(fx.WorkspaceRoot, fx.DbPath, "current-ws"),
            registry,
            loadTextContentSearch: (_, _) => throw new OperationCanceledException("stop during load"));
        var measured = new MeasuredSymbolLookupIndex(SymbolSearchProjectionLoader.Load(fx.DbPath));
        var telemetry = new ReadPhaseTelemetry(measured, graph: null, providerCacheEntries: 0);
        using IDisposable activation = telemetry.ActivateSearchTelemetry();

        Assert.Throws<OperationCanceledException>(() =>
            provider.ResolveTextContentSearch(workspaceId: null, ensureFresh: false));
        Assert.Throws<OperationCanceledException>(() =>
            provider.ResolveTextContentSearch(workspaceId: null, ensureFresh: false));
        ContextLookupPhaseObservation observation =
            telemetry.CompleteLookupPhase(ContextLookupPhase.SourceRescue);

        Assert.Equal(0, observation.TextContentIndexResolveDelta.Resolve.CallCount);
        Assert.Equal(2, observation.TextContentIndexResolveDelta.ReadSessionOpen.CallCount);
        Assert.Equal(0, observation.TextContentIndexResolveDelta.CacheHit.CallCount);
        Assert.Equal(2, observation.TextContentIndexResolveDelta.CacheMiss.CallCount);
        Assert.Equal(0, observation.TextContentIndexResolveDelta.IndexLoad.CallCount);
    }

    [Fact]
    public void ResolveRegionSearch_RegionIndexDisabled_FailsClosed()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspaceAt(current.Directory, current.DbPath, "current-ws"),
            registry,
            sidecar: new SymbolSearchSidecar(enabled: true, RegionIndexOptions.Disabled));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            provider.ResolveRegionSearch(workspaceId: null, ensureFresh: false));

        Assert.Contains("MILLER_REGION_INDEX=0", ex.Message);
    }

    [Fact]
    public void ResolveRegionSearch_RegisteredWorkspace_UsesFreshDiskRegionIndexAndCachesIt()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithRegion("target-ws", revision: 1, "TargetType", "src/Target.cs", "// TODO target\nclass TargetType {}\n");
        WriteRegionSearchDbFor(target, revision: 1);
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", target.WorkspaceRoot, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        int regionLoadCount = 0;
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadRegionSearch: (dbPath, revision) =>
            {
                regionLoadCount++;
                return FtsRegionSearchIndex.Open(
                    SymbolSearchSidecar.SearchDbPathFor(dbPath),
                    revision,
                    SymbolsArtifactIdentity.TryRead(dbPath));
            },
            sidecar: new SymbolSearchSidecar(enabled: true, RegionIndexOptions.EnabledDefault));

        WorkspaceRegionSearchContext first = provider.ResolveRegionSearch("target-ws", ensureFresh: false);
        WorkspaceRegionSearchContext second = provider.ResolveRegionSearch("target-ws", ensureFresh: false);

        Assert.IsType<FtsRegionSearchIndex>(first.Index);
        Assert.Same(first.Index, second.Index);
        Assert.Equal(1, regionLoadCount);
        RegionSearchHit hit = Assert.Single(first.Index.Search("TODO", new HashSet<string> { "comment" }, limit: 10));
        Assert.Equal("src/Target.cs", hit.Path);
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
        using var releaseLoad = new ManualResetEventSlim(initialState: false);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadIndex: path =>
            {
                Interlocked.Increment(ref loadCount);
                loadStarted.TrySetResult();
                releaseLoad.Wait(TimeSpan.FromSeconds(5));
                return RepositoryIndexLoader.Load(path);
            });

        Task<WorkspaceReadContext> first = RunBlockingResolve(() => provider.Resolve("target-ws", ensureFresh: false));
        Task timeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(loadStarted.Task, await Task.WhenAny(loadStarted.Task, timeout));
        Task<WorkspaceReadContext> second = RunBlockingResolve(() => provider.Resolve("target-ws", ensureFresh: false));
        releaseLoad.Set();

        WorkspaceReadContext[] contexts = await Task.WhenAll(first, second);

        Assert.Equal(1, loadCount);
        Assert.Same(contexts[0].Index, contexts[1].Index);
        Assert.Same(contexts[0].Resolver, contexts[1].Resolver);
    }

    [Fact]
    public async Task Resolve_RegisteredWorkspace_ConcurrentWaitersBothMarkIndexLoad()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-telemetry-single-flight");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseLoad = new ManualResetEventSlim(initialState: false);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadIndex: path =>
            {
                loadStarted.TrySetResult();
                releaseLoad.Wait(TimeSpan.FromSeconds(5));
                return RepositoryIndexLoader.Load(path);
            });
        using var ledger = TelemetryLedger.Open(Path.Combine(_dir, "concurrent-load-telemetry.db"), "current-ws");

        Task<string> first = RunBlockingResolve(() =>
        {
            using TelemetryScope scope = ledger.Measure("inspect", op: null);
            provider.Resolve("target-ws", ensureFresh: false);
            return scope.MetadataJson;
        });
        Task timeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(loadStarted.Task, await Task.WhenAny(loadStarted.Task, timeout));

        var secondScopePublished = new TaskCompletionSource<TelemetryScope>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> second = RunBlockingResolve(() =>
        {
            using TelemetryScope scope = ledger.Measure("inspect", op: null);
            secondScopePublished.TrySetResult(scope);
            provider.Resolve("target-ws", ensureFresh: false);
            return scope.MetadataJson;
        });
        TelemetryScope secondScope = await secondScopePublished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        bool secondMarkedWhileBlocked = SpinWait.SpinUntil(
            () => secondScope.MetadataJson.Contains("\"wait_reason\":\"index_load\"", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));

        releaseLoad.Set();
        string[] metadataRows = await Task.WhenAll(first, second);

        Assert.True(secondMarkedWhileBlocked, "the caller waiting on the shared lazy was not marked as index_load");
        foreach (string metadataJson in metadataRows)
        {
            using JsonDocument metadata = JsonDocument.Parse(metadataJson);
            Assert.Equal("index_load", metadata.RootElement.GetProperty("wait_reason").GetString());
        }
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
        using var releaseLoad = new ManualResetEventSlim(initialState: false);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadContentSearch: (dbPath, root) =>
            {
                Interlocked.Increment(ref loadCount);
                loadStarted.TrySetResult();
                releaseLoad.Wait(TimeSpan.FromSeconds(5));
                return ContentSearchProjectionLoader.Load(dbPath, root);
            });

        Task<WorkspaceContentSearchContext> first =
            RunBlockingResolve(() => provider.ResolveContentSearch("target-ws", ensureFresh: false));
        Task timeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(loadStarted.Task, await Task.WhenAny(loadStarted.Task, timeout));
        Task<WorkspaceContentSearchContext> second =
            RunBlockingResolve(() => provider.ResolveContentSearch("target-ws", ensureFresh: false));
        releaseLoad.Set();

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

        Task<WorkspaceReadContext> first = RunBlockingResolve(() => provider.Resolve("target-ws", ensureFresh: false));
        Task timeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(startedA.Task, await Task.WhenAny(startedA.Task, timeout));

        registry.UpsertSeen("target-ws", "target-111111111111", root, targetB.DbPath);
        registry.MarkScanned("target-ws", revision: 2);

        Task<WorkspaceReadContext> second = RunBlockingResolve(() => provider.Resolve("target-ws", ensureFresh: false));
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
            RunBlockingResolve(() => provider.ResolveSymbolSearch("target-ws", ensureFresh: false));
        Task timeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(startedA.Task, await Task.WhenAny(startedA.Task, timeout));

        registry.UpsertSeen("target-ws", "target-111111111111", root, targetB.DbPath);
        registry.MarkScanned("target-ws", revision: 2);

        Task<WorkspaceSymbolSearchContext> second =
            RunBlockingResolve(() => provider.ResolveSymbolSearch("target-ws", ensureFresh: false));
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
            RunBlockingResolve(() => provider.ResolveContentSearch("target-ws", ensureFresh: false));
        Task firstTimeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(startedA.Task, await Task.WhenAny(startedA.Task, firstTimeout));

        registry.UpsertSeen("target-ws", "target-111111111111", targetB.WorkspaceRoot, targetB.DbPath);
        registry.MarkScanned("target-ws", revision: 2);

        Task<WorkspaceContentSearchContext> second =
            RunBlockingResolve(() => provider.ResolveContentSearch("target-ws", ensureFresh: false));
        Task secondTimeout = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(startedB.Task, await Task.WhenAny(startedB.Task, secondTimeout));

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

        using var ledger = TelemetryLedger.Open(Path.Combine(_dir, "refresh-telemetry.db"), "current-ws");
        using var scope = ledger.Measure("inspect", op: null);

        WorkspaceReadContext context = provider.Resolve("target-ws", ensureFresh: true);

        Assert.True(refreshed);
        Assert.Equal(2, context.Revision);
        Assert.Equal("refreshed", context.FreshnessStatus);
        Assert.Null(context.WarningText);
        using JsonDocument metadata = JsonDocument.Parse(scope.MetadataJson);
        Assert.Equal("workspace_refresh", metadata.RootElement.GetProperty("wait_reason").GetString());
    }

    [Fact]
    public void Resolve_RegisteredWorkspace_MarksOnlyTheFirstLazyIndexLoad()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string root = NewRoot("target-load-reason");
        registry.UpsertSeen("target-ws", "target-111111111111", root, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);
        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry);
        using var ledger = TelemetryLedger.Open(Path.Combine(_dir, "load-telemetry.db"), "current-ws");

        using (TelemetryScope firstScope = ledger.Measure("inspect", op: null))
        {
            provider.Resolve("target-ws", ensureFresh: false);

            using JsonDocument metadata = JsonDocument.Parse(firstScope.MetadataJson);
            Assert.Equal("index_load", metadata.RootElement.GetProperty("wait_reason").GetString());
        }

        using (TelemetryScope cachedScope = ledger.Measure("inspect", op: null))
        {
            provider.Resolve("target-ws", ensureFresh: false);

            using JsonDocument metadata = JsonDocument.Parse(cachedScope.MetadataJson);
            Assert.False(metadata.RootElement.TryGetProperty("wait_reason", out _));
        }
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
        Func<string, long, ITextContentSearchIndex>? loadTextContentSearch = null,
        Func<string, long, IRegionSearchIndex>? loadRegionSearch = null,
        Func<long, bool?>? currentIndexFresh = null,
        SymbolSearchSidecar? sidecar = null,
        Func<string, string, string?, WorkspaceReadHandle>? openReadSession = null,
        Func<IWorkspaceReadSession, MillerRepositoryIndex>? loadSessionIndex = null,
        Func<IWorkspaceReadSession, SymbolSearchProjection>? loadSessionSymbolSearch = null,
        Func<WorkspaceReadHandle, ISymbolLookupIndex>? openStoreSymbolSearch = null,
        Func<IWorkspaceReadSession, BridgeGraph>? loadSessionBridgeGraph = null,
        Action<GraphStatementObservation>? graphStatementObserver = null,
        Func<WorkspaceReadHandle, ITextContentSearchIndex>? openStoreTextContentSearch = null,
        Func<WorkspaceReadHandle, IReadOnlyList<GraphEdge>>? loadSupplementalEdges = null,
        SupplementalEdgeCache? supplementalEdgesCache = null) =>
        new(
            holder,
            workspace,
            registry,
            refresh ?? (_ => throw new InvalidOperationException("refresh was not expected")),
            loadIndex ?? (path => RepositoryIndexLoader.Load(path)),
            loadSymbolSearch ?? (path => SymbolSearchProjectionLoader.Load(path)),
            loadContentSearch ?? ((dbPath, root) => ContentSearchProjectionLoader.Load(dbPath, root)),
            loadTextContentSearch ?? ((dbPath, revision) => ContentCorpusSidecar.OpenGenerationChecked(
                ContentCorpusSidecar.ContentDbPathFor(dbPath), dbPath, revision)),
            loadRegionSearch ?? ((dbPath, revision) => FtsRegionSearchIndex.Open(
                SymbolSearchSidecar.SearchDbPathFor(dbPath), revision, SymbolsArtifactIdentity.TryRead(dbPath))),
            currentIndexFresh ?? (_ => true),
            sidecar ?? SymbolSearchSidecar.Disabled,
            openReadSession,
            loadSessionIndex,
            loadSessionSymbolSearch,
            openStoreSymbolSearch,
            loadSessionBridgeGraph,
            graphStatementObserver,
            openStoreTextContentSearch,
            loadSupplementalEdges,
            supplementalEdgesCache);

    private static WorkspaceReadSnapshot StoreSnapshot(
        string root,
        string manifestHash,
        string indexLevel = IndexLevels.FullMetadataValue,
        long storeLogSequence = 12) =>
        new(
            root,
            "target-ws",
            "family-a",
            "view-a",
            new WorkspaceFreshnessToken(
                "family-a",
                7,
                manifestHash,
                storeLogSequence,
                "resolution-a",
                StoreInstanceId: "family-a:gen-001",
                ViewId: "view-a",
                GenerationName: "gen-001",
                ManifestGeneration: 7,
                IndexLevel: indexLevel,
                LevelStampL1: "l1-a",
                LevelStampL2: "l2-a",
                LevelStampL3: indexLevel == IndexLevels.FullMetadataValue ? "l3-a" : null),
            indexLevel,
            WorkspaceReadMode.FamilyStore,
            GenerationName: "gen-001",
            ManifestGeneration: 7);

    private sealed class StubReadSession(WorkspaceReadSnapshot snapshot) : IWorkspaceReadSession
    {
        public WorkspaceReadSnapshot Snapshot { get; } = snapshot;

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) =>
            throw new InvalidOperationException("direct session reads were not expected");

        public void Dispose()
        {
        }
    }

    private sealed class FixtureReadSession(WorkspaceReadSnapshot snapshot, string dbPath) : IWorkspaceReadSession
    {
        public WorkspaceReadSnapshot Snapshot { get; } = snapshot;

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            return query(connection);
        }

        public void Dispose()
        {
        }
    }

    private static Task<T> RunBlockingResolve<T>(Func<T> resolve) =>
        Task.Factory.StartNew(
            resolve,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    // Build the on-disk search.db sidecar next to a fixture's symbols.db (the path the router derives), stamped
    // with the given extract revision — the Phase-3 freshness key.
    [Fact]
    public void ResolveSymbolSearch_PromoteRestartsRevisionAtTheSameNumber_RefusesThePreRebuildSidecar()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSymbol("target-ws", revision: 1, "TargetType");
        WriteSearchDbFor(target, revision: 1);
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen(
            "target-ws", "target-111111111111", NewRoot("target-promote-collision"), target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry,
            loadSymbolSearch: _ => throw new InvalidOperationException("fallback must not run while a fresh sidecar exists"),
            sidecar: new SymbolSearchSidecar(enabled: true, RegionIndexOptions.Disabled));

        Assert.IsType<FtsSymbolSearchIndex>(provider.ResolveSymbolSearch("target-ws", ensureFresh: false).Index);

        // A full rebuild promotes a NEW artifact whose revision counter restarted at 1. Revision comparison alone
        // reads that as fresh, so the sidecar built from the superseded artifact would keep serving pre-rebuild
        // results forever. Only the artifact id separates the two generations.
        ReplaceArtifactId(target.DbPath, "artifact-after-full-rebuild");

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => provider.ResolveSymbolSearch("target-ws", ensureFresh: false));
        Assert.Contains("different index generation", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveTextContentSearch_PromoteRestartsRevisionAtTheSameNumber_RefusesThePreRebuildCorpus()
    {
        using var current = DbWithSymbol("current-ws", revision: 1, "CurrentType");
        using var target = DbWithSource("target-ws", revision: 1, "KnownSourceError");
        ContentCorpusWriter.Write(
            ContentCorpusSidecar.ContentDbPathFor(target.DbPath),
            target.DbPath,
            target.WorkspaceRoot,
            workspaceId: "target-ws",
            revision: 1);
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        registry.UpsertSeen("target-ws", "target-111111111111", target.WorkspaceRoot, target.DbPath);
        registry.MarkScanned("target-ws", revision: 1);

        var provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
            CurrentWorkspace(current.DbPath, "current-ws"),
            registry);

        Assert.Equal(1, provider.ResolveTextContentSearch("target-ws", ensureFresh: false).Revision);

        ReplaceArtifactId(target.DbPath, "artifact-after-full-rebuild");

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => provider.ResolveTextContentSearch("target-ws", ensureFresh: false));
        Assert.Contains("different index generation", failure.Message, StringComparison.Ordinal);
    }

    private static void ReplaceArtifactId(string symbolsDbPath, string artifactId)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = symbolsDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE artifact_metadata SET value = $id WHERE key = 'artifact_id';";
        command.Parameters.AddWithValue("$id", artifactId);
        command.ExecuteNonQuery();
    }

    // Passing symbolsDbPath is what makes this faithful: the production writer stamps the artifact id from it,
    // and the read gates reject a sidecar that carries no stamp when the live artifact has one.
    private static void WriteEmptySqlite(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE payload(value TEXT); INSERT INTO payload VALUES ('ready');";
        command.ExecuteNonQuery();
    }

    private static void WriteSearchDbFor(JulieDbFixture fixture, long revision) =>
        SearchIndexWriter.Write(
            SymbolSearchSidecar.SearchDbPathFor(fixture.DbPath),
            SqliteSymbolReader.Read(fixture.DbPath),
            revision,
            fixture.DbPath,
            workspaceRoot: null,
            RegionIndexOptions.Disabled);

    private static void WriteRegionSearchDbFor(JulieDbFixture fixture, long revision) =>
        SearchIndexWriter.Write(
            SymbolSearchSidecar.SearchDbPathFor(fixture.DbPath),
            SqliteSymbolReader.Read(fixture.DbPath),
            revision,
            fixture.DbPath,
            fixture.WorkspaceRoot,
            RegionIndexOptions.EnabledDefault);

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

    private static JulieDbFixture DbWithRegion(
        string workspaceId,
        long revision,
        string symbolName,
        string path,
        string text)
    {
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        int endByte = newline < 0 ? System.Text.Encoding.UTF8.GetByteCount(text) : newline;
        string symbolId = Guid.NewGuid().ToString("N");
        return JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(symbolId, symbolName, "class", "csharp",
                    path, $"public class {symbolName}", 2, ParentId: null),
            },
            workspaceId: workspaceId,
            fileContent: new Dictionary<string, string> { [path] = text },
            sourceRegions: new[]
            {
                new JulieDbFixture.SourceRegionRow(
                    "region-" + symbolName, "file:" + path, path, "csharp", "comment", symbolId,
                    1, 1, 1, endByte, 0, endByte, null),
            },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(revision, "fresh"),
            });
    }

    private static JulieDbFixture DbWithSource(string workspaceId, long revision, string marker)
    {
        const string path = "src/Source.cs";
        string text = $$"""
            public class Source
            {
                public void Handle()
                {
                    throw new InvalidOperationException("{{marker}}");
                }
            }
            """;
        return JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("sym-source", "Source", "class", "csharp",
                    path, "public class Source", 1, ParentId: null)
                {
                    EndLine = 7,
                },
                new JulieDbFixture.SymbolRow("sym-handle", "Handle", "method", "csharp",
                    path, "public void Handle()", 3, ParentId: "sym-source")
                {
                    EndLine = 6,
                },
            },
            workspaceId: workspaceId,
            fileContent: new Dictionary<string, string> { [path] = text },
            revisions: new[]
            {
                new JulieDbFixture.RevisionRow(revision, "fresh"),
            });
    }

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

    private sealed class FixedSearchResultLookupIndex(
        ISymbolLookupIndex inner,
        IReadOnlyList<SearchHit> searchResult) : ISymbolLookupIndex
    {
        public int SearchCallCount { get; private set; }

        public int DocumentCount => inner.DocumentCount;

        public IReadOnlySet<string> KnownExtensions => inner.KnownExtensions;

        public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or)
        {
            SearchCallCount++;
            return searchResult;
        }

        public IndexedSymbol Resolve(int docId) => inner.Resolve(docId);

        public IReadOnlyList<IndexedSymbol> FindByName(string name) => inner.FindByName(name);

        public IndexedSymbol? FindBySymbolId(string symbolId) => inner.FindBySymbolId(symbolId);

        public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) => inner.FindChildren(parentId);

        public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) => inner.FindByFilePath(filePath);

        public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
            inner.FindByFilePathFragment(query, limit);

        public IReadOnlyList<string> FindFilePathsByFragment(string query, int limit) =>
            inner.FindFilePathsByFragment(query, limit);

        public bool IsIndexedFilePath(string path) => inner.IsIndexedFilePath(path);

        public string? ResolveIndexedFilePath(string target) => inner.ResolveIndexedFilePath(target);
    }
}
