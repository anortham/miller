using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using Miller.Indexing.Store;
using Miller.Indexing.Semantic;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

[Collection(StoreEnvironmentCollection.Name)]
public sealed class StoreReaderConsumerTests : IDisposable
{
    private readonly string? _storeMode = Environment.GetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable);

    public StoreReaderConsumerTests() =>
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, null);

    [Theory]
    [InlineData(WorkspaceRefreshMode.Background)]
    [InlineData(WorkspaceRefreshMode.None)]
    public void ReaderAdmissionBusy_DoesNotRefreshOrMarkTheRegisteredIndexMissing(WorkspaceRefreshMode mode)
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding,
            _ => throw new StoreReaderRegistrationException(ReaderFailure.Busy));
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(fixture.Root, "registry.db"));
        WorkspaceRegistryRow row = registry.UpsertSeen("workspace-a", "example", fixture.Binding.WorkspaceRoot,
            Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
        int refreshCalls = 0;
        var provider = CreateStoreEnabledProvider(registry, reader.Client, () => refreshCalls++);

        FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(
            () => provider.ResolveSymbolRead("workspace-a", mode));

        Assert.Equal(ReaderFailure.Busy, Assert.IsType<StoreReaderRegistrationException>(error.InnerException).Failure);
        Assert.Equal(0, refreshCalls);
        Assert.Equal(row, registry.Get("workspace-a"));
        Assert.DoesNotContain("open", reader.Events);
        Assert.Equal(0, reader.Owed);
    }

    [Theory]
    [InlineData("McpHealth")]
    [InlineData("McpStatus")]
    public void ReaderAdmissionBusy_HealthPreservesRegistryAndReportsTemporaryUnavailable(string profileName)
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding,
            _ => throw new StoreReaderRegistrationException(ReaderFailure.Busy));
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(fixture.Root, "registry.db"));
        WorkspaceRegistryRow row = registry.UpsertSeen("workspace-a", "example", fixture.Binding.WorkspaceRoot,
            Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "symbols.db"), WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(registry, row, Enum.Parse<WorkspaceRegisteredFactsProfile>(profileName),
            SymbolSearchSidecar.Disabled, new ContentCorpusSidecar(), storeEnabled: true,
            readerClient: () => reader.Client);

        Assert.Equal(row, registry.Get(row.WorkspaceId));
        Assert.Equal("reader_admission_busy", facts.FreshnessStatus);
        Assert.Null(facts.IndexFresh);
        Assert.Contains("retry", facts.WarningText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndexLevelReadWithDisabledStoreModeRefusesAnActivePointer()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        WorkspaceContext workspace = WorkspaceContext.Create(fixture.Binding.WorkspaceRoot, fixture.Root, fixture.Root)
            with { ReaderClient = reader.Client };

        FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() => IndexerService.ReadIndexLevel(workspace));

        Assert.Equal(FamilyStoreReadFailure.BindingNotReady, error.Failure);
        Assert.Empty(reader.Events);
        Assert.Equal(0, reader.Owed);
    }

    [Fact]
    public void FreshnessConstructorStaysLazyAndPollingUsesBoundWorkspaceProducer()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        using var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = fixture.Root;
        using var service = new FreshnessService(bootstrap, NullLogger<FreshnessService>.Instance,
            storeEnabled: () => true);
        Assert.Empty(reader.Events);
        WorkspaceContext workspace = WorkspaceContext.Create(fixture.Binding.WorkspaceRoot, fixture.Root, fixture.Root)
            with { ReaderClient = reader.Client, WorkspaceId = "workspace-a" };
        var holder = new IndexHolder(() => throw new InvalidOperationException("Status must not hydrate the index"),
            builtRevision: 1, documentCount: 0, knownExtensionsCount: 0, builtArtifactId: "old");
        bootstrap.SeedForTest(workspace, holder);

        PollResult result = service.PollNow();

        Assert.True(result.Swapped);
        Assert.Equal(2, holder.MetadataSnapshot().Revision);
        Assert.Equal(1, holder.MetadataSnapshot().DocumentCount);
        Assert.Equal("acquire", reader.Events[0]);
        Assert.Equal("release", reader.Events[^1]);
        Assert.Equal(3, reader.Events.Count(value => value == "acquire"));
        Assert.Equal(0, reader.Owed);
    }

    [Fact]
    public void VectorFamilyDiscoverySkipsRetainedStoreUnderPinnedStandaloneMode()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        WorkspaceContext workspace = WorkspaceContext.Create(fixture.Binding.WorkspaceRoot, fixture.Root, fixture.Root)
            with { ReaderClient = reader.Client };

        Assert.Null(VectorConvergeService.FamilyStoreRootFor(workspace));

        Assert.Empty(reader.Events);
        Assert.Equal(0, reader.Owed);
        Assert.False(File.Exists(Path.Combine(fixture.Binding.StoreRoot, "vectors.db")));
    }

    [Fact]
    public void UnboundWorkspaceProviderServesAndProbesWithItsConfiguredProducer()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(fixture.Root, "registry.db"));
        WorkspaceRegistryRow row = registry.UpsertSeen("workspace-a", "example", fixture.Binding.WorkspaceRoot,
            Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
        var provider = CreateStoreEnabledProvider(registry, reader.Client, static () => { });
        Assert.Empty(reader.Events);

        using (WorkspaceSymbolReadContext context = provider.ResolveSymbolRead("workspace-a", WorkspaceRefreshMode.None))
        {
            Assert.Equal("Visible", Assert.Single(SqliteSymbolReader.ReadForPaths(context.ReadSession, ["same.cs"])).Name);
            Assert.Equal("acquire", reader.Events[0]);
            Assert.DoesNotContain("release", reader.Events);
        }
        Assert.Equal(new[] { "acquire", "open", "close", "release" }, reader.Events);
        reader.Events.Clear();
        Assert.True(WorkspaceIndexProvider.HasReadableIndex(row, storeEnabled: true, readerClient: reader.Client));
        Assert.Equal(new[] { "acquire", "open", "close", "release" }, reader.Events);
        Assert.Equal(0, reader.Owed);
    }

    [Fact]
    public void HostRegisteredProviderDoesNotResolveUnusedReaderToolsWithoutAPrimary()
    {
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, "off");
        using JulieDbFixture fixture = JulieDbFixture.CreateForInspect();
        MillerHostPaths paths = MillerHostPaths.Create(fixture.WorkspaceRoot, fixture.WorkspaceRoot) with { ToolsRoot = "" };
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(paths.RegistryDbPath);
        registry.UpsertSeen("legacy", "example", fixture.WorkspaceRoot, fixture.DbPath, WorkspaceRegistryState.Ready);
        string unusedProducer = Path.Combine(fixture.WorkspaceRoot, "unused-producer");
        File.WriteAllText(unusedProducer, "A no-refresh read must never execute this file.");
        var refresh = new CrossWorkspaceRefreshService(registry, new JulieExtractRunner(unusedProducer),
            SymbolSearchSidecar.Disabled, ScanGovernor.FromEnvironment(paths.MillerDirectory));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMillerServices(semanticMode: SemanticMode.Off, startIndexer: false);
        services.AddSingleton(paths);
        // Existing refresh tooling is independent of read-only admission and is never called here.
        services.AddSingleton(refresh);
        using ServiceProvider host = services.BuildServiceProvider();
        IndexBootstrapService bootstrap = host.GetRequiredService<IndexBootstrapService>();

        WorkspaceIndexProvider provider = host.GetRequiredService<WorkspaceIndexProvider>();
        using WorkspaceSymbolReadContext context = provider.ResolveSymbolRead("legacy", WorkspaceRefreshMode.None);

        Assert.Equal("legacy", context.Snapshot.ViewId);
        Assert.True(context.ReadSession.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM symbols";
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }));
        Assert.False(bootstrap.IsBound);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, _storeMode);

    private static WorkspaceIndexProvider CreateStoreEnabledProvider(
        WorkspaceRegistry registry,
        IJulieStoreClient reader,
        Action onUnexpectedRefresh)
    {
        WorkspaceRefreshResult UnexpectedRefresh(string _)
        {
            onUnexpectedRefresh();
            throw new InvalidOperationException("The test must not refresh this workspace.");
        }

        return new WorkspaceIndexProvider(
            holder: null,
            currentWorkspace: null,
            registry: registry,
            refresh: UnexpectedRefresh,
            loadIndex: _ => throw new InvalidOperationException("The family store read must not load a legacy index."),
            loadSymbolSearch: _ => throw new InvalidOperationException("The family store read must not load a legacy symbol projection."),
            loadContentSearch: (_, _) => throw new InvalidOperationException("The family store read must not load a legacy content projection."),
            loadTextContentSearch: (_, _) => throw new InvalidOperationException("The family store read must not load a legacy text index."),
            loadRegionSearch: (_, _) => throw new InvalidOperationException("The family store read must not load a legacy region index."),
            currentIndexFresh: _ => true,
            sidecar: SymbolSearchSidecar.Disabled,
            openReadSession: (databasePath, root, workspaceId) => WorkspaceReadSessionFactory.Open(
                databasePath,
                root,
                workspaceId,
                storeEnabled: true,
                factCacheStore: null,
                readerClientFactory: () => reader),
            scheduleBackgroundRefresh: _ => { },
            hasReadableIndex: row => WorkspaceIndexProvider.HasReadableIndex(
                row,
                storeEnabled: true,
                readerClient: reader),
            backgroundRefreshGate: new BackgroundRefreshGate());
    }
}
