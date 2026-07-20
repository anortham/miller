using System.Text.Json;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

public sealed class WorkspaceFactsAssemblerTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "miller-workspace-facts-" + Guid.NewGuid());

    public WorkspaceFactsAssemblerTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void RegisteredStatusFacts_CliProfileDoesNotMarkMissingIndex()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        string missingDb = Path.Combine(_temp, "missing", "symbols.db");
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-cli",
            "cli",
            Path.Combine(_temp, "workspace-cli"),
            missingDb,
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.CliStatus,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar());

        Assert.Equal("ready", facts.FreshnessStatus);
        Assert.Null(facts.IndexFresh);
        Assert.Equal("index DB not found: " + missingDb, facts.WarningText);
        Assert.Equal(WorkspaceRegistryState.Ready, registry.Get("ws-cli")!.State);
    }

    [Fact]
    public void RegisteredStatusFacts_McpProfileMarksMissingIndexAndUsesTypedStatus()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        string missingDb = Path.Combine(_temp, "missing", "symbols.db");
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-mcp",
            "mcp",
            Path.Combine(_temp, "workspace-mcp"),
            missingDb,
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.McpStatus,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar());

        Assert.Equal("missing_index", facts.FreshnessStatus);
        Assert.False(facts.IndexFresh);
        Assert.Equal("Workspace index DB not found: " + missingDb, facts.WarningText);
        Assert.Equal(WorkspaceRegistryState.Missing, registry.Get("ws-mcp")!.State);
    }

    [Fact]
    public void RegisteredHealthFacts_CliProfileReportsMissingIndexWithoutMarkingRegistry()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        string missingDb = Path.Combine(_temp, "missing-health-cli", "symbols.db");
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-cli-health",
            "clih",
            Path.Combine(_temp, "workspace-cli-health"),
            missingDb,
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.CliHealth,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar());

        Assert.Equal("missing_index", facts.FreshnessStatus);
        Assert.False(facts.IndexFresh);
        Assert.Equal("index DB not found: " + missingDb, facts.WarningText);
        Assert.Equal(WorkspaceRegistryState.Ready, registry.Get("ws-cli-health")!.State);
    }

    [Fact]
    public void RegisteredHealthFacts_McpProfileReportsMissingIndexAndMarksRegistry()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        string missingDb = Path.Combine(_temp, "missing-health-mcp", "symbols.db");
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-mcp-health",
            "mcph",
            Path.Combine(_temp, "workspace-mcp-health"),
            missingDb,
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.McpHealth,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar());

        Assert.Equal("missing_index", facts.FreshnessStatus);
        Assert.False(facts.IndexFresh);
        Assert.Equal("Workspace index DB not found: " + missingDb, facts.WarningText);
        Assert.Equal(WorkspaceRegistryState.Missing, registry.Get("ws-mcp-health")!.State);
    }

    [Fact]
    public void RegisteredHealthReadError_McpProfileMarksRegistryError()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        string dbPath = Path.Combine(_temp, "unreadable", "symbols.db");
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-mcp-unreadable",
            "mcpu",
            Path.Combine(_temp, "workspace-mcp-unreadable"),
            dbPath,
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredHealthReadError(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.McpHealth,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar(),
            new InvalidOperationException("schema is incomplete"));

        Assert.Equal("unreadable_index", facts.FreshnessStatus);
        Assert.False(facts.IndexFresh);
        Assert.Equal(
            $"could not read workspace index DB '{dbPath}': schema is incomplete",
            facts.WarningText);
        Assert.Equal(WorkspaceRegistryState.Error, registry.Get("ws-mcp-unreadable")!.State);
    }

    [Fact]
    public void UnregisteredLocalFactsUseCliUnknownFreshness()
    {
        string dbPath = Path.Combine(_temp, "local", "symbols.db");
        var context = new WorkspaceContext(
            WorkspaceRoot: Path.Combine(_temp, "local"),
            ExtractDbPath: dbPath,
            TelemetryDbPath: Path.Combine(_temp, "telemetry.db"),
            RegistryDbPath: Path.Combine(_temp, "workspaces.db"),
            ToolsRoot: Path.Combine(_temp, "tools"),
            WorkspaceId: null);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromUnregisteredLocal(
            context,
            new WorkspaceIndexFacts(DocumentCount: 17, KnownExtensionsCount: 3),
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar());

        Assert.Equal(context.WorkspaceRoot, facts.Root);
        Assert.Null(facts.WorkspaceId);
        Assert.Equal(17, facts.DocumentCount);
        Assert.Equal(3, facts.KnownExtensionsCount);
        Assert.Null(facts.IndexFresh);
        Assert.Equal("unregistered", facts.FreshnessStatus);
        Assert.True(facts.QueueEmpty);
    }

    [Fact]
    public void ToListEntriesUsesCallerCurrentPredicate()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            new WorkspaceRegistryRow(
                "ws-a",
                "aaaa",
                "/workspace/a",
                "/workspace/a/.miller/symbols.db",
                now,
                now,
                12,
                WorkspaceRegistryState.Ready,
                LastError: null),
            new WorkspaceRegistryRow(
                "ws-b",
                "bbbb",
                "/workspace/b",
                "/workspace/b/.miller/symbols.db",
                now,
                LastScanAt: null,
                LastRevision: null,
                WorkspaceRegistryState.Missing,
                "gone"),
        };

        IReadOnlyList<WorkspaceListEntry> entries =
            WorkspaceFactsAssembler.ToListEntries(rows, row => row.WorkspaceId == "ws-b");

        Assert.Collection(
            entries,
            first =>
            {
                Assert.Equal("ws-a", first.WorkspaceId);
                Assert.False(first.Current);
                Assert.Equal("ready", first.State);
            },
            second =>
            {
                Assert.Equal("ws-b", second.WorkspaceId);
                Assert.True(second.Current);
                Assert.Equal("gone", second.LastError);
            });
    }

    [Fact]
    public void ToListEntriesCarriesLastSeenAtForRecencyOrdering()
    {
        DateTimeOffset seen = DateTimeOffset.UtcNow.AddMinutes(-42);
        var rows = new[]
        {
            new WorkspaceRegistryRow(
                "ws-a",
                "aaaa",
                "/workspace/a",
                "/workspace/a/.miller/symbols.db",
                seen,
                LastScanAt: null,
                LastRevision: 3,
                WorkspaceRegistryState.Ready,
                LastError: null),
        };

        IReadOnlyList<WorkspaceListEntry> entries =
            WorkspaceFactsAssembler.ToListEntries(rows, _ => false);

        Assert.Equal(seen, Assert.Single(entries).LastSeenAt);
    }

    [Fact]
    public void SemanticOff_FactsReportDisabledAndRenderNowhere()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-vec-off",
            "voff",
            Path.Combine(_temp, "workspace-vec-off"),
            Path.Combine(_temp, "workspace-vec-off", ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.McpStatus,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar(),
            VectorSidecar.Disabled);

        Assert.Equal("disabled", facts.Vectors!.State);
        Assert.DoesNotContain("vectors:", WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false), StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        Assert.False(doc.RootElement.GetProperty("index").TryGetProperty("vectors", out _));
    }

    [Fact]
    public void SemanticOn_WithoutArtifact_ReportsUnavailableWithReasonInCompactAndJson()
    {
        using WorkspaceRegistry registry = WorkspaceRegistry.Open(Path.Combine(_temp, "workspaces.db"));
        WorkspaceRegistryRow row = registry.UpsertSeen(
            "ws-vec-on",
            "von",
            Path.Combine(_temp, "workspace-vec-on"),
            Path.Combine(_temp, "workspace-vec-on", ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready);

        WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
            registry,
            row,
            WorkspaceRegisteredFactsProfile.McpStatus,
            new SymbolSearchSidecar(enabled: true),
            new ContentCorpusSidecar(),
            new VectorSidecar(SemanticMode.On));

        Assert.Equal("unavailable", facts.Vectors!.State);
        Assert.Contains("vectors: unavailable (", WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false), StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        JsonElement vectors = doc.RootElement.GetProperty("index").GetProperty("vectors");
        Assert.Equal("unavailable", vectors.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(vectors.GetProperty("reason").GetString()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }
}
