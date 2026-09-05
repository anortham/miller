using Miller.Dashboard;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Tests.Support;
using Miller.Tests.Indexing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Miller.Tests.Server;

[Collection("DashboardIndexFactsCache")]
public sealed class DashboardReaderRetentionTests : IDisposable
{
    private readonly string? _previousStore = Environment.GetEnvironmentVariable("MILLER_INDEX_STORE");
    private readonly string? _previousTools = Environment.GetEnvironmentVariable("MILLER_TOOLS_ROOT");
    public DashboardReaderRetentionTests()
    {
        Environment.SetEnvironmentVariable("MILLER_INDEX_STORE", null);
        Environment.SetEnvironmentVariable("MILLER_TOOLS_ROOT", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-tools"));
    }
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MILLER_INDEX_STORE", _previousStore);
        Environment.SetEnvironmentVariable("MILLER_TOOLS_ROOT", _previousTools);
        DashboardIndexFactsCache.Clear();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregateReadsUseSelectedProducerAndCloseBeforeRelease(bool cached)
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        DashboardIndexFactsCache.Clear();

        DashboardWorkspaceFacts facts = cached
            ? DashboardIndexFactsCache.Read(Row(fixture), true, () => reader.Client)
            : DashboardIndexFactsReader.Read(Row(fixture), true, () => reader.Client);

        Assert.Equal(2, facts.IndexRevision);
        Assert.True(facts.SymbolCount > 0);
        Assert.Equal(cached
            ? new[] { "acquire", "open", "close", "release", "acquire", "open", "close", "release" }
            : new[] { "acquire", "open", "close", "release" }, reader.Events);
        Assert.Equal(0, reader.Owed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistryListAndDetailUseSelectedProducer(bool detail)
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        string registryPath = Register(fixture);
        DashboardIndexFactsCache.Clear();
        DashboardWorkspaceFacts facts = detail
            ? DashboardData.ReadSnapshot(registryPath, Path.Combine(fixture.Root, "telemetry.db"), "ws-reader", null,
                () => reader.Client).SelectedWorkspaceFacts!
            : Assert.Single(DashboardData.ReadIndex(registryPath, null, true, () => reader.Client).Entries).Facts;

        Assert.Equal(2, facts.IndexRevision);
        Assert.Equal(detail ? 1 : 2, reader.Events.Count(x => x == "acquire"));
        Assert.Equal("release", reader.Events.Last());
        Assert.Equal(0, reader.Owed);
    }

    [Fact]
    public void MatchingStampAndCachedFactsDoNoReaderWork()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        StoreFreshnessStamp.Write(new StoreFreshnessStampDocument(
            StoreFreshnessStamp.SchemaVersion, fixture.Binding.FamilyId, fixture.Binding.StoreRoot,
            fixture.Binding.ViewId, fixture.Binding.WorkspaceRoot, 2, 2, "manifest-current",
            "11111111-1111-4111-8111-111111111111:gen-001", "2.31.0"));
        DashboardIndexFactsCache.Clear();
        DashboardWorkspaceFacts first = DashboardIndexFactsCache.Read(Row(fixture), true, () => reader.Client);
        Assert.Equal(2, first.IndexRevision);
        Assert.Equal(new[] { "acquire", "open", "close", "release" }, reader.Events);
        reader.Events.Clear();

        Environment.SetEnvironmentVariable("MILLER_TOOLS_ROOT", "");
        DashboardWorkspaceFacts second = DashboardIndexFactsCache.Read(Row(fixture), true);

        Assert.Same(first, second);
        Assert.Empty(reader.Events);
        Assert.Equal(0, reader.Owed);
    }

    [Fact]
    public void FailedGenerationOpenReleasesSelectedProducerPin()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        reader.AfterAcquire = () => File.Move(
            Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db"),
            Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.saved.db"));

        DashboardWorkspaceFacts facts = DashboardIndexFactsReader.Read(Row(fixture), true, () => reader.Client);

        Assert.Equal("unreadable", facts.Status);
        Assert.Equal(new[] { "acquire", "release" }, reader.Events);
        Assert.Equal(0, reader.Owed);
    }

    [Fact]
    public void LegacyAggregateReadDoesNoReaderWork()
    {
        using StoreFixture store = StoreFixture.Create();
        using var reader = new StoreCallerReaderFixture(store.Binding, store.ReaderReply);
        using JulieDbFixture legacy = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows: []);
        DashboardWorkspaceRow row = Row(store) with
        {
            CanonicalRoot = legacy.WorkspaceRoot,
            IndexDbPath = legacy.DbPath
        };
        DashboardIndexFactsCache.Clear();

        DashboardWorkspaceFacts facts = DashboardIndexFactsCache.Read(row, false, () => reader.Client);

        Assert.Equal("ready", facts.Status);
        Assert.Null(facts.Store);
        Assert.Empty(reader.Events);
        Assert.Equal(0, reader.Owed);
    }

    [Fact]
    public void ToolsRootCloneRevalidatesCurrentRootWithoutStartingProducer()
    {
        var paths = new DashboardPaths("registry.db", "telemetry.db", "original-tools", "wwwroot", "http://127.0.0.1:0");
        DashboardPaths clone = paths with { ToolsRoot = "" };

        ArgumentException error = Assert.Throws<ArgumentException>(() => clone.ReaderClient);

        Assert.Equal("toolsRoot", error.ParamName);
        Assert.IsType<JulieStoreClient>(paths.ReaderClient);
    }

    [Fact]
    public void ToolsRootClonePreservesExplicitProducerOverride()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        var paths = new DashboardPaths("registry.db", "telemetry.db", "original-tools", "wwwroot", "http://127.0.0.1:0")
        {
            ReaderClientOverride = reader.Client
        };
        DashboardPaths clone = paths with { ToolsRoot = "" };

        DashboardWorkspaceFacts facts = DashboardIndexFactsReader.Read(Row(fixture), true, () => clone.ReaderClient);

        Assert.Equal(2, facts.IndexRevision);
        Assert.Equal(new[] { "acquire", "open", "close", "release" }, reader.Events);
        Assert.Equal(0, reader.Owed);
    }

    [Theory]
    [InlineData("/", 2)]
    [InlineData("/fragments/workspaces", 2)]
    [InlineData("/workspace?workspace_id=ws-reader", 1)]
    [InlineData("/fragments/dashboard?workspace_id=ws-reader", 1)]
    [InlineData("/fragments/detail-stack?workspace_id=ws-reader", 1)]
    [InlineData("/index.json", 2)]
    [InlineData("/snapshot.json?workspace_id=ws-reader", 1)]
    public async Task HttpReadsUseDashboardSelectedClient(string path, int acquisitions)
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        using var reader = new StoreCallerReaderFixture(fixture.Binding, fixture.ReaderReply);
        var paths = new DashboardPaths(Register(fixture), Path.Combine(fixture.Root, "telemetry.db"),
            Path.Combine(fixture.Root, "selected-tools"), Path.Combine(fixture.Root, "wwwroot"), "http://127.0.0.1:0")
        {
            ReaderClientOverride = reader.Client
        };
        DashboardIndexFactsCache.Clear();
        using IHost host = new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(DashboardHostPipeline.ConfigureServices)
                .Configure(app => DashboardHostPipeline.Configure(app, paths, fixture.Root)))
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = host.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains("reader-abcd1234", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(acquisitions, reader.Events.Count(x => x == "acquire"));
        Assert.Equal("release", reader.Events.Last());
        Assert.Equal(0, reader.Owed);
    }

    [Theory]
    [InlineData("/index.json", false)]
    [InlineData("/snapshot.json", false)]
    [InlineData("/index.json", true)]
    [InlineData("/snapshot.json?workspace_id=ws-legacy", true)]
    public async Task HttpReadsLeaveUnusedInvalidToolsRootUnresolved(string path, bool legacy)
    {
        using StoreFixture fixture = StoreFixture.Create();
        using JulieDbFixture artifact = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows: []);
        string registryPath = Path.Combine(fixture.Root, "registry.db");
        if (legacy)
        {
            Environment.SetEnvironmentVariable("MILLER_INDEX_STORE", "off");
            using var registry = WorkspaceRegistry.Open(registryPath);
            registry.UpsertSeen("ws-legacy", "legacy-abcd1234", artifact.WorkspaceRoot, artifact.DbPath,
                WorkspaceRegistryState.Ready, DateTimeOffset.UtcNow);
        }
        var paths = new DashboardPaths(registryPath, Path.Combine(fixture.Root, "telemetry.db"),
            "", Path.Combine(fixture.Root, "wwwroot"), "http://127.0.0.1:0");
        DashboardIndexFactsCache.Clear();
        using IHost host = new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(DashboardHostPipeline.ConfigureServices)
                .Configure(app => DashboardHostPipeline.Configure(app, paths, fixture.Root)))
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = host.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(legacy ? "legacy-abcd1234" : path == "/index.json" ? "\"workspace_count\":0" : "\"selected_workspace_id\":null", body);
        if (legacy)
            Assert.Contains("\"status\":\"ready\"", body);
    }

    private static string Register(StoreFixture fixture)
    {
        string path = Path.Combine(fixture.Root, "registry.db");
        using var registry = WorkspaceRegistry.Open(path);
        DashboardWorkspaceRow row = Row(fixture);
        registry.UpsertSeen(row.WorkspaceId, row.DisplayId, row.CanonicalRoot, row.IndexDbPath,
            WorkspaceRegistryState.Ready, DateTimeOffset.UtcNow);
        return path;
    }

    private static DashboardWorkspaceRow Row(StoreFixture fixture) => new(
        "ws-reader", "reader-abcd1234", fixture.Binding.WorkspaceRoot,
        Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "symbols.db"),
        "2026-09-04T00:00:00Z", "2026-09-04T00:00:00Z", 2, "ready", null);
}
