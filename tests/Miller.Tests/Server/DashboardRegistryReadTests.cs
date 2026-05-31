using System.Text.Json;
using Miller.Dashboard;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Xunit;

namespace Miller.Tests.Server;

public sealed class DashboardRegistryReadTests : IDisposable
{
    private readonly string _dir;
    private readonly string _registryDb;
    private readonly string _telemetryDb;

    public DashboardRegistryReadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registryDb = Path.Combine(_dir, "workspaces.db");
        _telemetryDb = Path.Combine(_dir, "telemetry.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ReadWorkspaces_ReadsRegistryRowsWithoutScanningFilesystem()
    {
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-a",
                "alpha-abcd1234",
                Path.Combine(_dir, "alpha"),
                Path.Combine(_dir, "alpha", ".miller", "symbols.db"),
                WorkspaceRegistryState.Current,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
            registry.MarkScanned("ws-a", 42, DateTimeOffset.Parse("2026-05-31T10:01:00Z"));
            registry.UpsertSeen(
                "ws-b",
                "beta-efgh5678",
                Path.Combine(_dir, "beta"),
                Path.Combine(_dir, "beta", ".miller", "symbols.db"),
                WorkspaceRegistryState.LoadedExisting,
                DateTimeOffset.Parse("2026-05-31T11:00:00Z"));
        }

        IReadOnlyList<DashboardWorkspaceRow> rows = DashboardData.ReadWorkspaces(_registryDb);

        Assert.Equal(2, rows.Count);
        Assert.Equal("ws-a", rows[0].WorkspaceId);
        Assert.Equal("ready", rows[0].State);
        Assert.Equal(42, rows[0].LastRevision);
        Assert.Equal("ws-b", rows[1].WorkspaceId);
        Assert.Equal("loaded_existing", rows[1].State);
    }

    [Fact]
    public void ReadTelemetrySummary_ScopesRowsToRequestedWorkspace()
    {
        using (var ledger = TelemetryLedger.Open(_telemetryDb, "ws-a", "/repo/a"))
        {
            ledger.Record(new TelemetryRecord(
                Tool: "search",
                Op: "auto",
                WorkspaceId: "ws-a",
                WorkspaceRoot: "/repo/a",
                DurationMs: 12,
                Outcome: "ok",
                ErrorKind: null,
                ResultCount: 2,
                BytesExamined: 0,
                BytesReturned: 0,
                SourceBytes: 0,
                EstTokens: 20,
                IndexFresh: true,
                TargetHash: "hash-a",
                MetadataJson: "{}"));
            ledger.Record(new TelemetryRecord(
                Tool: "inspect",
                Op: null,
                WorkspaceId: "ws-b",
                WorkspaceRoot: "/repo/b",
                DurationMs: 900,
                Outcome: "error",
                ErrorKind: "Boom",
                ResultCount: null,
                BytesExamined: 0,
                BytesReturned: 0,
                SourceBytes: 0,
                EstTokens: 999,
                IndexFresh: false,
                TargetHash: "hash-b",
                MetadataJson: "{}"));
        }

        DashboardTelemetrySummary summary = DashboardData.ReadTelemetrySummary(_telemetryDb, "ws-a");

        Assert.Equal("ws-a", summary.WorkspaceId);
        Assert.Equal(1, summary.TotalCalls);
        var search = Assert.Single(summary.Tools);
        Assert.Equal("search", search.Tool);
        Assert.Equal(12, search.MaxMs);
        Assert.Equal(20, search.SumEstTokens);
    }

    [Fact]
    public void RenderWorkspacesJson_UsesStableSnakeCaseContract()
    {
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-json",
                "json-abc12345",
                "/repo/json",
                "/repo/json/.miller/symbols.db",
                WorkspaceRegistryState.Missing,
                DateTimeOffset.Parse("2026-05-31T12:00:00Z"));
            registry.MarkMissing("ws-json", "root missing", DateTimeOffset.Parse("2026-05-31T12:01:00Z"));
        }

        string json = DashboardData.RenderWorkspacesJson(_registryDb);

        using var doc = JsonDocument.Parse(json);
        JsonElement row = doc.RootElement[0];
        Assert.Equal("ws-json", row.GetProperty("workspace_id").GetString());
        Assert.Equal("json-abc12345", row.GetProperty("display_id").GetString());
        Assert.Equal("missing", row.GetProperty("state").GetString());
        Assert.Equal("root missing", row.GetProperty("last_error").GetString());
    }

    [Fact]
    public void RenderTelemetryJson_UsesStableSnakeCaseContract()
    {
        using (var ledger = TelemetryLedger.Open(_telemetryDb, "ws-json", "/repo/json"))
        {
            ledger.Record(new TelemetryRecord(
                Tool: "search",
                Op: null,
                WorkspaceId: "ws-json",
                WorkspaceRoot: "/repo/json",
                DurationMs: 25,
                Outcome: "empty",
                ErrorKind: null,
                ResultCount: 0,
                BytesExamined: 0,
                BytesReturned: 0,
                SourceBytes: 0,
                EstTokens: 7,
                IndexFresh: true,
                TargetHash: "hash-json",
                MetadataJson: "{}"));
        }

        string json = DashboardData.RenderTelemetryJson(_telemetryDb, "ws-json");

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("ws-json", root.GetProperty("workspace_id").GetString());
        Assert.Equal(1, root.GetProperty("total_calls").GetInt64());
        JsonElement tool = root.GetProperty("tools")[0];
        Assert.Equal("search", tool.GetProperty("tool").GetString());
        Assert.Equal(1, tool.GetProperty("calls").GetInt64());
        Assert.Equal(25, tool.GetProperty("p95_ms").GetInt64());
        Assert.Equal(7, tool.GetProperty("sum_est_tokens").GetInt64());
    }

    [Fact]
    public void MissingDashboardDatabases_RenderAsEmptyReadOnlyViews()
    {
        Assert.Empty(DashboardData.ReadWorkspaces(_registryDb));
        Assert.Equal(0, DashboardData.ReadTelemetrySummary(_telemetryDb, "missing").TotalCalls);
    }
}
