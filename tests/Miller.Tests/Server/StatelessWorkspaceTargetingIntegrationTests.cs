using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Miller.Tests.Server;

public sealed class StatelessWorkspaceTargetingIntegrationTests : IDisposable
{
    private readonly List<string> _directories = [];
    private readonly List<JulieDbFixture> _fixtures = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (JulieDbFixture fixture in _fixtures)
            fixture.Dispose();
        foreach (string directory in _directories)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task ExplicitWorkspaceCallsStayUnboundAndServeTwoRegisteredIds()
    {
        string home = NewDirectory("home");
        string rootA = NewDirectory("workspace-a");
        string rootB = NewDirectory("workspace-b");
        JulieDbFixture fixtureA = CreateFixture(rootA);
        JulieDbFixture fixtureB = CreateFixture(rootB);
        const string workspaceA = "stateless-workspace-a";
        const string workspaceB = "stateless-workspace-b";

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddConsole());
        services.AddMillerServices(semanticMode: SemanticMode.Off, startIndexer: false);
        StatelessMcpToolRegistration.Add(services);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        services
            .AddMcpServer(options => { options.ServerInfo = new() { Name = "stateless-targeting", Version = "0" }; })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(WorkspaceBindingCallToolFilter.Create());
            });

        await using var provider = services.BuildServiceProvider();
        IndexBootstrapService bootstrap = provider.GetRequiredService<IndexBootstrapService>();
        bootstrap.TestHomeDirectoryOverride = home;
        WorkspaceRegistry registry = provider.GetRequiredService<WorkspaceRegistry>();
        registry.UpsertSeen(workspaceA, workspaceA, rootA, fixtureA.DbPath, WorkspaceRegistryState.Ready);
        registry.MarkScanned(workspaceA, revision: 7);
        registry.UpsertSeen(workspaceB, workspaceB, rootB, fixtureB.DbPath, WorkspaceRegistryState.Ready);
        registry.MarkScanned(workspaceB, revision: 11);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            await bootstrap.StartAsync(cancellationToken);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        Assert.False(bootstrap.IsBound);
        Assert.True(bootstrap.IsDeferred);
        string directOutput = provider.GetRequiredService<WorkspaceTool>()
            .Workspace(operation: "status", workspace_id: workspaceA, format: "json");
        Assert.Equal(workspaceA, WorkspaceIdFromStatus(directOutput));
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(cancellationToken);
        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken);

        string outputA = await CallStatusAsync(client, workspaceA, cancellationToken);
        string outputB = await CallStatusAsync(client, workspaceB, cancellationToken);

        Assert.Equal(workspaceA, WorkspaceIdFromStatus(outputA));
        Assert.Equal(rootA, WorkspaceRootFromStatus(outputA));
        Assert.Equal(workspaceB, WorkspaceIdFromStatus(outputB));
        Assert.Equal(rootB, WorkspaceRootFromStatus(outputB));
        Assert.False(bootstrap.IsBound);

        await client.DisposeAsync();
        await clientToServer.Writer.CompleteAsync();
        await serverToClient.Writer.CompleteAsync();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); } catch (Exception) { }
    }

    [Fact]
    public async Task ExplicitWorkspaceOutputIsStableWhenPrimaryBindsToMatchingOrDifferentRoot()
    {
        string home = NewDirectory("primary-home");
        string rootA = NewDirectory("primary-a");
        string rootB = NewDirectory("primary-b");
        JulieDbFixture fixtureA = CreateFixture(rootA);
        JulieDbFixture fixtureB = CreateFixture(rootB);
        const string workspaceA = "primary-workspace-a";
        const string workspaceB = "primary-workspace-b";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMillerServices(semanticMode: SemanticMode.Off, startIndexer: false);
        using var provider = services.BuildServiceProvider();
        IndexBootstrapService bootstrap = provider.GetRequiredService<IndexBootstrapService>();
        bootstrap.TestHomeDirectoryOverride = home;
        WorkspaceRegistry registry = provider.GetRequiredService<WorkspaceRegistry>();
        registry.UpsertSeen(workspaceA, workspaceA, rootA, fixtureA.DbPath, WorkspaceRegistryState.Ready);
        registry.MarkScanned(workspaceA, revision: 7);
        registry.UpsertSeen(workspaceB, workspaceB, rootB, fixtureB.DbPath, WorkspaceRegistryState.Ready);
        registry.MarkScanned(workspaceB, revision: 11);

        WorkspaceTool tool = provider.GetRequiredService<WorkspaceTool>();
        string beforeBinding = tool.Workspace(operation: "status", workspace_id: workspaceB, format: "json");

        bootstrap.SeedForTest(
            WorkspaceContext.Create(rootA, AppContext.BaseDirectory, home) with
            {
                WorkspaceId = workspaceA,
                CanonicalRoot = rootA,
                ExtractDbPath = fixtureA.DbPath,
                CanonicalExtractDbPath = fixtureA.DbPath,
            },
            new IndexHolder(
                MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fixtureA.DbPath)),
                builtRevision: 7));
        string differentPrimary = provider.GetRequiredService<WorkspaceTool>()
            .Workspace(operation: "status", workspace_id: workspaceB, format: "json");

        bootstrap.SeedForTest(
            WorkspaceContext.Create(rootB, AppContext.BaseDirectory, home) with
            {
                WorkspaceId = workspaceB,
                CanonicalRoot = rootB,
                ExtractDbPath = fixtureB.DbPath,
                CanonicalExtractDbPath = fixtureB.DbPath,
            },
            new IndexHolder(
                MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fixtureB.DbPath)),
                builtRevision: 11));
        string matchingPrimary = provider.GetRequiredService<WorkspaceTool>()
            .Workspace(operation: "status", workspace_id: workspaceB, format: "json");

        Assert.Equal(WorkspaceIdFromStatus(beforeBinding), WorkspaceIdFromStatus(differentPrimary));
        Assert.Equal(WorkspaceRootFromStatus(beforeBinding), WorkspaceRootFromStatus(differentPrimary));
        Assert.Equal(WorkspaceIdFromStatus(beforeBinding), WorkspaceIdFromStatus(matchingPrimary));
        Assert.Equal(WorkspaceRootFromStatus(beforeBinding), WorkspaceRootFromStatus(matchingPrimary));
    }

    private JulieDbFixture CreateFixture(string root)
    {
        JulieDbFixture fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            JulieDbFixture.DefaultRows,
            workspaceId: null);
        _fixtures.Add(fixture);
        return fixture;
    }

    private string NewDirectory(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), $"miller-stateless-{name}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return PathCanonicalizer.CanonicalizeRoot(path);
    }

    private static async Task<string> CallStatusAsync(
        McpClient client,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        CallToolResult result = await client.CallToolAsync(
            "workspace",
            new Dictionary<string, object?>
            {
                ["operation"] = "status",
                ["workspace_id"] = workspaceId,
                ["format"] = "json",
            },
            cancellationToken: cancellationToken);
        Assert.True(result.IsError != true, ResultText(result));
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    private static string ResultText(CallToolResult result) =>
        result.Content is [TextContentBlock text] ? text.Text : "<non-text result>";

    private static string WorkspaceIdFromStatus(string output)
    {
        using JsonDocument document = JsonDocument.Parse(output);
        return document.RootElement.GetProperty("workspace").GetProperty("workspace_id").GetString()!;
    }

    private static string WorkspaceRootFromStatus(string output)
    {
        using JsonDocument document = JsonDocument.Parse(output);
        return document.RootElement.GetProperty("workspace").GetProperty("root").GetString()!;
    }

}
