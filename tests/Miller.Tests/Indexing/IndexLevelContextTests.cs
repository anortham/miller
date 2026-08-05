using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class IndexLevelContextTests : IDisposable
{
    private readonly string _dir;
    private readonly string _registryDbPath;

    public IndexLevelContextTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-index-level-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registryDbPath = Path.Combine(_dir, "workspaces.db");
    }

    public void Dispose()
    {
        foreach (string artifact in Directory.EnumerateFiles(_dir, "symbols.db", SearchOption.AllDirectories))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = artifact,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            SqliteConnection.ClearPool(connection);
        }

        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Resolve_RegisteredSymbolsLevelArtifact_CarriesTheArtifactLevel()
    {
        WorkspaceReadContext context = RegisteredRead(SymbolsLevelArtifact.Create);

        Assert.Equal(IndexLevels.SymbolsMetadataValue, context.IndexLevel);
        Assert.True(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    [Fact]
    public void ResolveSymbolRead_RegisteredSymbolsLevelArtifact_CarriesTheLevelThoughNoRepositoryIndexServesTheRead()
    {
        WorkspaceSymbolReadContext context = RegisteredSymbolRead(SymbolsLevelArtifact.Create);

        Assert.IsNotType<MillerRepositoryIndex>(context.Index);
        Assert.Equal(IndexLevels.SymbolsMetadataValue, context.IndexLevel);
        Assert.True(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    [Fact]
    public void Resolve_RegisteredFullLevelArtifact_CarriesFullAndNothingConverges()
    {
        WorkspaceReadContext context = RegisteredRead(SymbolsLevelArtifact.CreateFull);

        Assert.Equal(IndexLevels.FullMetadataValue, context.IndexLevel);
        Assert.False(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    [Fact]
    public void ResolveSymbolRead_RegisteredFullLevelArtifact_CarriesFullAndNothingConverges()
    {
        WorkspaceSymbolReadContext context = RegisteredSymbolRead(SymbolsLevelArtifact.CreateFull);

        Assert.Equal(IndexLevels.FullMetadataValue, context.IndexLevel);
        Assert.False(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    [Fact]
    public void Resolve_CurrentWorkspaceSymbolsLevelArtifact_CarriesTheArtifactLevel()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        WorkspaceIndexProvider provider = CurrentWorkspaceProvider(SymbolsLevelArtifact.Create, registry);

        WorkspaceReadContext context = provider.Resolve(workspaceId: null, ensureFresh: false);

        Assert.Equal(IndexLevels.SymbolsMetadataValue, context.IndexLevel);
        Assert.True(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    [Fact]
    public void ResolveSymbolRead_CurrentWorkspaceSymbolsLevelArtifact_CarriesTheArtifactLevel()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        WorkspaceIndexProvider provider = CurrentWorkspaceProvider(SymbolsLevelArtifact.Create, registry);

        WorkspaceSymbolReadContext context = provider.ResolveSymbolRead(workspaceId: null, ensureFresh: false);

        Assert.Equal(IndexLevels.SymbolsMetadataValue, context.IndexLevel);
        Assert.True(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    [Fact]
    public void Resolve_CurrentWorkspaceFullLevelArtifact_CarriesFullThroughBothContexts()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        WorkspaceIndexProvider provider = CurrentWorkspaceProvider(SymbolsLevelArtifact.CreateFull, registry);

        WorkspaceReadContext read = provider.Resolve(workspaceId: null, ensureFresh: false);
        WorkspaceSymbolReadContext symbolRead = provider.ResolveSymbolRead(workspaceId: null, ensureFresh: false);

        Assert.Equal(IndexLevels.FullMetadataValue, read.IndexLevel);
        Assert.Equal(IndexLevels.FullMetadataValue, symbolRead.IndexLevel);
        Assert.False(IndexLevelGuard.ReferenceLayerConverging(read.IndexLevel));
        Assert.False(IndexLevelGuard.ReferenceLayerConverging(symbolRead.IndexLevel));
    }

    [Fact]
    public void ReferenceLayerConverging_RepositoryIndexOverload_AgreesWithTheCarriedLevelOverload()
    {
        MillerRepositoryIndex symbols = RepositoryIndexLoader.Load(SymbolsLevelArtifact.Create(NewDir("symbols-index")));
        MillerRepositoryIndex full = RepositoryIndexLoader.Load(SymbolsLevelArtifact.CreateFull(NewDir("full-index")));

        Assert.True(IndexLevelGuard.ReferenceLayerConverging(symbols));
        Assert.True(IndexLevelGuard.ReferenceLayerConverging(symbols.IndexLevel));
        Assert.False(IndexLevelGuard.ReferenceLayerConverging(full));
        Assert.False(IndexLevelGuard.ReferenceLayerConverging(full.IndexLevel));
    }

    [Fact]
    public void Create_LeavesTheReferenceLayerEmptyWhileTheSymbolsLayerIsPopulated()
    {
        string dbPath = SymbolsLevelArtifact.Create(NewDir("shape-symbols"));

        Assert.Equal(IndexLevels.SymbolsMetadataValue, ExtractIndexLevelReader.Read(dbPath));
        AssertPopulated(dbPath, "symbols", "files", "relationships", "reference_sites", "complexity_metrics", "type_facts");
        AssertEmpty(dbPath, "identifiers", "identifier_resolutions", "source_regions", "structural_facts");
    }

    [Fact]
    public void CreateFull_PopulatesTheReferenceLayerToo()
    {
        string dbPath = SymbolsLevelArtifact.CreateFull(NewDir("shape-full"));

        Assert.Equal(IndexLevels.FullMetadataValue, ExtractIndexLevelReader.Read(dbPath));
        AssertPopulated(
            dbPath,
            "symbols", "files", "relationships", "reference_sites", "complexity_metrics", "type_facts",
            "identifiers", "identifier_resolutions", "source_regions", "structural_facts");
    }

    [Fact]
    public void ResolveSymbolSearch_RegisteredSymbolsLevelArtifact_CarriesTheArtifactLevel()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        WorkspaceIndexProvider provider = RegisteredProvider(SymbolsLevelArtifact.Create, registry);

        WorkspaceSymbolSearchContext context = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);

        Assert.Equal(IndexLevels.SymbolsMetadataValue, context.IndexLevel);
        Assert.True(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    [Fact]
    public void ResolveSymbolSearch_RegisteredFullLevelArtifact_CarriesFullAndNothingConverges()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        WorkspaceIndexProvider provider = RegisteredProvider(SymbolsLevelArtifact.CreateFull, registry);

        WorkspaceSymbolSearchContext context = provider.ResolveSymbolSearch("target-ws", ensureFresh: false);

        Assert.Equal(IndexLevels.FullMetadataValue, context.IndexLevel);
        Assert.False(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    [Fact]
    public void ResolveArtifact_RegisteredSymbolsLevelArtifact_CarriesTheArtifactLevel()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        WorkspaceIndexProvider provider = RegisteredProvider(SymbolsLevelArtifact.Create, registry);

        WorkspaceArtifactContext context = provider.ResolveArtifact("target-ws", ensureFresh: false);

        Assert.Equal(IndexLevels.SymbolsMetadataValue, context.IndexLevel);
        Assert.True(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    [Fact]
    public void ResolveArtifact_RegisteredFullLevelArtifact_CarriesFullAndNothingConverges()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        WorkspaceIndexProvider provider = RegisteredProvider(SymbolsLevelArtifact.CreateFull, registry);

        WorkspaceArtifactContext context = provider.ResolveArtifact("target-ws", ensureFresh: false);

        Assert.Equal(IndexLevels.FullMetadataValue, context.IndexLevel);
        Assert.False(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    [Fact]
    public void ResolveRegionSearch_RegisteredSymbolsLevelArtifact_CarriesTheArtifactLevel()
    {
        WorkspaceRegionSearchContext context = RegisteredRegionSearch(SymbolsLevelArtifact.Create);

        Assert.Equal(IndexLevels.SymbolsMetadataValue, context.IndexLevel);
        Assert.True(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    [Fact]
    public void ResolveRegionSearch_RegisteredFullLevelArtifact_CarriesFullAndNothingConverges()
    {
        WorkspaceRegionSearchContext context = RegisteredRegionSearch(SymbolsLevelArtifact.CreateFull);

        Assert.Equal(IndexLevels.FullMetadataValue, context.IndexLevel);
        Assert.False(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    [Fact]
    public void ResolveCurrentWorkspace_SymbolsLevelArtifact_EverySearchAndArtifactContextCarriesTheLevel()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        WorkspaceIndexProvider provider = CurrentSidecarProvider(SymbolsLevelArtifact.Create, registry);

        Assert.Equal(
            IndexLevels.SymbolsMetadataValue,
            provider.ResolveSymbolSearch(workspaceId: null, ensureFresh: false).IndexLevel);
        Assert.Equal(
            IndexLevels.SymbolsMetadataValue,
            provider.ResolveArtifact(workspaceId: null, ensureFresh: false).IndexLevel);
        Assert.Equal(
            IndexLevels.SymbolsMetadataValue,
            provider.ResolveRegionSearch(workspaceId: null, ensureFresh: false).IndexLevel);
    }

    [Fact]
    public void ResolveCurrentWorkspace_FullLevelArtifact_EverySearchAndArtifactContextCarriesFull()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        WorkspaceIndexProvider provider = CurrentSidecarProvider(SymbolsLevelArtifact.CreateFull, registry);

        Assert.Equal(
            IndexLevels.FullMetadataValue,
            provider.ResolveSymbolSearch(workspaceId: null, ensureFresh: false).IndexLevel);
        Assert.Equal(
            IndexLevels.FullMetadataValue,
            provider.ResolveArtifact(workspaceId: null, ensureFresh: false).IndexLevel);
        Assert.Equal(
            IndexLevels.FullMetadataValue,
            provider.ResolveRegionSearch(workspaceId: null, ensureFresh: false).IndexLevel);
    }

    [Fact]
    public void ResolveCurrentWorkspace_RepairPromotedSymbolsArtifactUnderAFullSnapshot_CarriesTheEvidenceFileLevel()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        (WorkspaceIndexProvider provider, string dbPath, MillerRepositoryIndex snapshot) =
            CurrentWorkspaceProviderWithPath(SymbolsLevelArtifact.CreateFull, registry);

        PromoteOver(dbPath, SymbolsLevelArtifact.Create(NewDir("repaired")));

        Assert.Equal(IndexLevels.FullMetadataValue, snapshot.IndexLevel);
        Assert.Equal(IndexLevels.SymbolsMetadataValue, ExtractIndexLevelReader.Read(dbPath));
        Assert.Equal(
            IndexLevels.SymbolsMetadataValue,
            provider.Resolve(workspaceId: null, ensureFresh: false).IndexLevel);
        Assert.Equal(
            IndexLevels.SymbolsMetadataValue,
            provider.ResolveSymbolSearch(workspaceId: null, ensureFresh: false).IndexLevel);
        Assert.Equal(
            IndexLevels.SymbolsMetadataValue,
            provider.ResolveSymbolRead(workspaceId: null, ensureFresh: false).IndexLevel);
    }

    [Fact]
    public void ResolveCurrentWorkspace_RepairPromotedSymbolsArtifactUnderAFullSnapshot_ArmsTheGuards()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        (WorkspaceIndexProvider provider, string dbPath, _) =
            CurrentWorkspaceProviderWithPath(SymbolsLevelArtifact.CreateFull, registry);

        PromoteOver(dbPath, SymbolsLevelArtifact.Create(NewDir("repaired")));

        Assert.True(IndexLevelGuard.ReferenceLayerConverging(
            provider.Resolve(workspaceId: null, ensureFresh: false).IndexLevel));
        Assert.True(IndexLevelGuard.ReferenceLayerConverging(
            provider.ResolveSymbolRead(workspaceId: null, ensureFresh: false).IndexLevel));
    }

    [Fact]
    public void McpInspect_RepairPromotedSymbolsArtifactUnderAFullSnapshot_MarksUsageEvidenceUnavailable()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        (WorkspaceIndexProvider provider, string dbPath, _) =
            CurrentWorkspaceProviderWithPath(SymbolsLevelArtifact.CreateFull, registry);

        PromoteOver(dbPath, SymbolsLevelArtifact.Create(NewDir("repaired")));

        string compact = new InspectTool(provider).Inspect("Alpha", depth: "overview");

        Assert.Contains("usage_evidence=unavailable", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic_code=", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void McpContext_RepairPromotedSymbolsArtifactUnderAFullSnapshot_StillReportsReferenceLayerConverging()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        (WorkspaceIndexProvider provider, string dbPath, _) =
            CurrentWorkspaceProviderWithPath(SymbolsLevelArtifact.CreateFull, registry);

        PromoteOver(dbPath, SymbolsLevelArtifact.Create(NewDir("repaired")));

        string compact = new ContextTool(provider).Context("Alpha");

        Assert.Contains("diagnostic_code=reference_layer_converging", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveArtifact_CurrentWorkspaceFullArtifactPromotedOverThePath_CarriesThePromotedFileLevel()
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        (WorkspaceIndexProvider provider, string dbPath, _) =
            CurrentWorkspaceProviderWithPath(SymbolsLevelArtifact.Create, registry);

        PromoteOver(dbPath, SymbolsLevelArtifact.CreateFull(NewDir("promoted")));

        WorkspaceArtifactContext context = provider.ResolveArtifact(workspaceId: null, ensureFresh: false);

        Assert.Equal(IndexLevels.FullMetadataValue, context.IndexLevel);
        Assert.False(IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel));
    }

    private (WorkspaceIndexProvider Provider, string DbPath, MillerRepositoryIndex Snapshot)
        CurrentWorkspaceProviderWithPath(Func<string, string> createArtifact, WorkspaceRegistry registry)
    {
        string root = NewDir("current");
        string dbPath = createArtifact(root);
        MillerRepositoryIndex snapshot = RepositoryIndexLoader.Load(dbPath);
        WorkspaceIndexProvider provider = NewProvider(
            new IndexHolder(snapshot, builtRevision: 1),
            CurrentWorkspaceAt(root, dbPath),
            registry);
        return (provider, dbPath, snapshot);
    }

    private static void PromoteOver(string dbPath, string promotedDbPath)
    {
        File.Delete(dbPath + "-wal");
        File.Delete(dbPath + "-shm");
        File.Move(promotedDbPath, dbPath, overwrite: true);
    }

    private WorkspaceReadContext RegisteredRead(Func<string, string> createArtifact)
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        WorkspaceIndexProvider provider = RegisteredProvider(createArtifact, registry);
        return provider.Resolve("target-ws", ensureFresh: false);
    }

    private WorkspaceRegionSearchContext RegisteredRegionSearch(Func<string, string> createArtifact)
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        string currentRoot = NewDir("current");
        string currentDbPath = SymbolsLevelArtifact.CreateFull(currentRoot);
        string targetRoot = NewDir("target");
        string targetDbPath = createArtifact(targetRoot);
        WriteRegionSearchDb(targetDbPath, targetRoot);

        registry.UpsertSeen("target-ws", "target-111111111111", targetRoot, targetDbPath);
        registry.MarkScanned("target-ws", revision: 1);

        WorkspaceIndexProvider provider = NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(currentDbPath), builtRevision: 1),
            CurrentWorkspaceAt(currentRoot, currentDbPath),
            registry,
            new SymbolSearchSidecar(enabled: true, RegionIndexOptions.EnabledDefault));

        return provider.ResolveRegionSearch("target-ws", ensureFresh: false);
    }

    private WorkspaceIndexProvider CurrentSidecarProvider(
        Func<string, string> createArtifact, WorkspaceRegistry registry)
    {
        string root = NewDir("current");
        string dbPath = createArtifact(root);
        WriteRegionSearchDb(dbPath, root);
        return NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(dbPath), builtRevision: 1),
            CurrentWorkspaceAt(root, dbPath),
            registry,
            new SymbolSearchSidecar(enabled: true, RegionIndexOptions.EnabledDefault));
    }

    private static void WriteRegionSearchDb(string dbPath, string workspaceRoot) =>
        SearchIndexWriter.Write(
            SymbolSearchSidecar.SearchDbPathFor(dbPath),
            SqliteSymbolReader.Read(dbPath),
            revision: 1,
            dbPath,
            workspaceRoot,
            RegionIndexOptions.EnabledDefault);

    private WorkspaceSymbolReadContext RegisteredSymbolRead(Func<string, string> createArtifact)
    {
        using var registry = WorkspaceRegistry.Open(_registryDbPath);
        WorkspaceIndexProvider provider = RegisteredProvider(createArtifact, registry);
        return provider.ResolveSymbolRead("target-ws", ensureFresh: false);
    }

    private WorkspaceIndexProvider RegisteredProvider(Func<string, string> createArtifact, WorkspaceRegistry registry)
    {
        string currentRoot = NewDir("current");
        string currentDbPath = SymbolsLevelArtifact.CreateFull(currentRoot);
        string targetRoot = NewDir("target");
        string targetDbPath = createArtifact(targetRoot);

        registry.UpsertSeen("target-ws", "target-111111111111", targetRoot, targetDbPath);
        registry.MarkScanned("target-ws", revision: 1);

        return NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(currentDbPath), builtRevision: 1),
            CurrentWorkspaceAt(currentRoot, currentDbPath),
            registry);
    }

    private WorkspaceIndexProvider CurrentWorkspaceProvider(
        Func<string, string> createArtifact, WorkspaceRegistry registry)
    {
        string root = NewDir("current");
        string dbPath = createArtifact(root);
        return NewProvider(
            new IndexHolder(RepositoryIndexLoader.Load(dbPath), builtRevision: 1),
            CurrentWorkspaceAt(root, dbPath),
            registry);
    }

    private static WorkspaceIndexProvider NewProvider(
        IndexHolder holder,
        WorkspaceContext workspace,
        WorkspaceRegistry registry,
        SymbolSearchSidecar? sidecar = null) =>
        new(
            holder,
            workspace,
            registry,
            _ => throw new InvalidOperationException("refresh was not expected"),
            path => RepositoryIndexLoader.Load(path),
            path => SymbolSearchProjectionLoader.Load(path),
            (dbPath, root) => ContentSearchProjectionLoader.Load(dbPath, root),
            (dbPath, revision) => ContentCorpusSidecar.OpenGenerationChecked(
                ContentCorpusSidecar.ContentDbPathFor(dbPath), dbPath, revision),
            (dbPath, revision) => FtsRegionSearchIndex.Open(
                SymbolSearchSidecar.SearchDbPathFor(dbPath), revision, SymbolsArtifactIdentity.TryRead(dbPath)),
            _ => true,
            sidecar ?? SymbolSearchSidecar.Disabled);

    private WorkspaceContext CurrentWorkspaceAt(string root, string dbPath) =>
        WorkspaceContext.Create(root, AppContext.BaseDirectory, _dir) with
        {
            ExtractDbPath = dbPath,
            CanonicalRoot = root,
            CanonicalExtractDbPath = dbPath,
            WorkspaceId = "current-ws",
        };

    private string NewDir(string name)
    {
        string dir = Path.Combine(_dir, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void AssertPopulated(string dbPath, params string[] tables)
    {
        foreach (string table in tables)
            Assert.True(RowCount(dbPath, table) > 0, table);
    }

    private static void AssertEmpty(string dbPath, params string[] tables)
    {
        foreach (string table in tables)
            Assert.Equal(0, RowCount(dbPath, table));
    }

    private static long RowCount(string dbPath, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(command.ExecuteScalar() ?? 0L);
    }
}
