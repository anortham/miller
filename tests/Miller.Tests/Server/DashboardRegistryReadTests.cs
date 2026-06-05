using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miller.Dashboard.Components;
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
    public void ReadTelemetrySummary_IncludesLastCallLastErrorAndRecentErrors()
    {
        InsertTelemetryRow("ws-a", "search", "ok", "2026-05-31T10:00:00.000Z", durationMs: 12);
        InsertTelemetryRow("ws-a", "search", "error", "2026-05-31T10:02:00.000Z",
            durationMs: 20, errorKind: "InvalidOperationException");
        InsertTelemetryRow("ws-a", "inspect", "error", "2026-05-31T10:04:00.000Z",
            durationMs: 7, errorKind: "KeyNotFoundException");
        InsertTelemetryRow("ws-a", "search", "ok", "2026-05-31T10:05:00.000Z", durationMs: 5);
        InsertTelemetryRow("ws-b", "search", "error", "2026-05-31T10:06:00.000Z",
            durationMs: 99, errorKind: "OtherWorkspaceException");

        DashboardTelemetrySummary summary = DashboardData.ReadTelemetrySummary(_telemetryDb, "ws-a");

        DashboardToolStat search = Assert.Single(summary.Tools, t => t.Tool == "search");
        Assert.Equal("2026-05-31T10:05:00.000Z", search.LastCallTs);
        Assert.Equal("ok", search.LastOutcome);
        Assert.Equal("2026-05-31T10:02:00.000Z", search.LastErrorTs);
        Assert.Equal("InvalidOperationException", search.LastErrorKind);

        Assert.Equal(2, summary.RecentErrors.Count);
        Assert.Equal("inspect", summary.RecentErrors[0].Tool);
        Assert.Equal("KeyNotFoundException", summary.RecentErrors[0].ErrorKind);
        Assert.Equal("search", summary.RecentErrors[1].Tool);
        Assert.Equal("InvalidOperationException", summary.RecentErrors[1].ErrorKind);
    }

    [Fact]
    public void ReadSnapshot_DefaultsSelectionToWorkspaceWithMostTelemetry()
    {
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-a",
                "alpha-abcd1234",
                "/repo/a",
                "/repo/a/.miller/symbols.db",
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
            registry.UpsertSeen(
                "ws-b",
                "beta-efgh5678",
                "/repo/b",
                "/repo/b/.miller/symbols.db",
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T11:00:00Z"));
        }
        using (var ledger = TelemetryLedger.Open(_telemetryDb, "ws-a", "/repo/a"))
        {
            ledger.Record(new TelemetryRecord(
                Tool: "search",
                Op: "auto",
                WorkspaceId: "ws-a",
                WorkspaceRoot: "/repo/a",
                DurationMs: 42,
                Outcome: "ok",
                ErrorKind: null,
                ResultCount: 1,
                BytesExamined: 0,
                BytesReturned: 0,
                SourceBytes: 0,
                EstTokens: 11,
                IndexFresh: true,
                TargetHash: "hash-a",
                MetadataJson: "{}"));
        }
        using (var ledger = TelemetryLedger.Open(_telemetryDb, "ws-b", "/repo/b"))
        {
            ledger.Record(new TelemetryRecord(
                Tool: "search",
                Op: "auto",
                WorkspaceId: "ws-b",
                WorkspaceRoot: "/repo/b",
                DurationMs: 12,
                Outcome: "ok",
                ErrorKind: null,
                ResultCount: 1,
                BytesExamined: 0,
                BytesReturned: 0,
                SourceBytes: 0,
                EstTokens: 11,
                IndexFresh: true,
                TargetHash: "hash-b",
                MetadataJson: "{}"));
            ledger.Record(new TelemetryRecord(
                Tool: "inspect",
                Op: null,
                WorkspaceId: "ws-b",
                WorkspaceRoot: "/repo/b",
                DurationMs: 18,
                Outcome: "ok",
                ErrorKind: null,
                ResultCount: 1,
                BytesExamined: 0,
                BytesReturned: 0,
                SourceBytes: 0,
                EstTokens: 22,
                IndexFresh: true,
                TargetHash: "hash-b",
                MetadataJson: "{}"));
        }

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(_registryDb, _telemetryDb, workspaceId: null);

        Assert.Equal("ws-b", snapshot.SelectedWorkspaceId);
        Assert.Equal(2, snapshot.Workspaces.Count);
        Assert.Equal("ws-b", snapshot.Telemetry.WorkspaceId);
        Assert.Equal(2, snapshot.Telemetry.TotalCalls);
    }

    [Fact]
    public void ReadSnapshot_DefaultsSelectionToPreferredRootBeforeTelemetryCount()
    {
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-a",
                "alpha-abcd1234",
                "/repo/a",
                "/repo/a/.miller/symbols.db",
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
            registry.UpsertSeen(
                "ws-b",
                "beta-efgh5678",
                "/repo/b",
                "/repo/b/.miller/symbols.db",
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T11:00:00Z"));
        }
        using (var ledger = TelemetryLedger.Open(_telemetryDb, "ws-a", "/repo/a"))
        {
            ledger.Record(new TelemetryRecord(
                Tool: "search",
                Op: "auto",
                WorkspaceId: "ws-a",
                WorkspaceRoot: "/repo/a",
                DurationMs: 42,
                Outcome: "ok",
                ErrorKind: null,
                ResultCount: 1,
                BytesExamined: 0,
                BytesReturned: 0,
                SourceBytes: 0,
                EstTokens: 11,
                IndexFresh: true,
                TargetHash: "hash-a",
                MetadataJson: "{}"));
        }

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(
            _registryDb,
            _telemetryDb,
            workspaceId: null,
            preferredWorkspaceRoot: "/repo/b");

        Assert.Equal("ws-b", snapshot.SelectedWorkspaceId);
        Assert.Equal("ws-b", snapshot.Telemetry.WorkspaceId);
        Assert.Equal(0, snapshot.Telemetry.TotalCalls);
    }

    [Fact]
    public async Task DashboardShell_RendersVisibleTelemetryAndHtmxTargets()
    {
        var snapshot = new DashboardSnapshot(
            Workspaces:
            [
                new DashboardWorkspaceRow(
                    "ws-a",
                    "alpha-abcd1234",
                    "/repo/a",
                    "/repo/a/.miller/symbols.db",
                    "2026-05-31T10:00:00Z",
                    "2026-05-31T10:01:00Z",
                    42,
                    "ready",
                    null)
            ],
            Telemetry: new DashboardTelemetrySummary(
                "ws-a",
                [
                    new DashboardToolStat(
                        "search",
                        Calls: 3,
                        AvgMs: 12.5,
                        P95Ms: 20,
                        MaxMs: 24,
                        ErrorCount: 1,
                        SumEstTokens: 90,
                        LastCallTs: "2026-05-31T10:02:00Z",
                        LastOutcome: "ok",
                        LastErrorTs: "2026-05-31T10:01:00Z",
                        LastErrorKind: "InvalidOperationException")
                ],
                TotalCalls: 3,
                WindowStartTs: "2026-05-31T10:00:00Z",
                WindowEndTs: "2026-05-31T10:02:00Z",
                RecentErrors:
                [
                    new DashboardRecentError(
                        "2026-05-31T10:01:00Z",
                        "search",
                        "auto",
                        "InvalidOperationException",
                        DurationMs: 24)
                ]),
            SelectedWorkspaceId: "ws-a");

        string html = await RenderComponentAsync<DashboardShell>(new Dictionary<string, object?>
        {
            ["Snapshot"] = snapshot,
        });

        Assert.Contains("Miller Dashboard", html);
        Assert.Contains("id=\"dashboard-content\"", html);
        Assert.Contains("hx-get=\"/fragments/dashboard?workspace_id=ws-a\"", html);
        Assert.Contains("hx-target=\"#dashboard-content\"", html);
        Assert.Contains("hx-get=\"/fragments/telemetry", html);
        Assert.Contains("workspace_id=ws-a", html);
        Assert.DoesNotContain("Snapshot.SelectedWorkspaceId", html);
        Assert.Contains("id=\"telemetry-panel\"", html);
        Assert.Contains("search", html);
        Assert.Contains("12.5", html);
        Assert.Contains("p95", html);
        Assert.Contains("Last error", html);
        Assert.Contains("Recent errors", html);
        Assert.Contains("InvalidOperationException", html);
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
        Assert.True(tool.TryGetProperty("last_call_ts", out _));
        Assert.True(root.TryGetProperty("recent_errors", out _));
    }

    [Fact]
    public void MissingDashboardDatabases_RenderAsEmptyReadOnlyViews()
    {
        Assert.Empty(DashboardData.ReadWorkspaces(_registryDb));
        Assert.Equal(0, DashboardData.ReadTelemetrySummary(_telemetryDb, "missing").TotalCalls);
    }

    private static async Task<string> RenderComponentAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        IServiceProvider provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            provider,
            provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }

    private void InsertTelemetryRow(
        string workspaceId,
        string tool,
        string outcome,
        string ts,
        long durationMs,
        string? errorKind = null,
        string? op = null,
        long? estTokens = null)
    {
        using (TelemetryLedger.Open(_telemetryDb, workspaceId, "/repo/test"))
        {
            // Ensures the telemetry schema exists before inserting deterministic timestamps.
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _telemetryDb,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tool_telemetry
                (id, ts, tool, op, workspace_id, workspace_root, duration_ms, outcome, error_kind, est_tokens)
            VALUES
                ($id, $ts, $tool, $op, $ws, $root, $duration, $outcome, $error, $tokens);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$op", (object?)op ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$root", "/repo/test");
        cmd.Parameters.AddWithValue("$duration", durationMs);
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$error", (object?)errorKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tokens", (object?)estTokens ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
