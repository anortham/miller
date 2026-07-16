using System.Text.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miller.Dashboard.Components;
using Miller.Dashboard;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
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
        // Healthy read: the degrade channel stays clean so no-data never looks like corruption.
        Assert.Null(summary.Error);
    }

    [Fact]
    public void ReadTelemetrySummary_IncludesLastCallLastErrorAndRecentErrors()
    {
        InsertTelemetryRow("ws-a", "search", "ok", "2026-05-31T10:00:00.000Z", durationMs: 12);
        string searchErrorId = InsertTelemetryRow(
            "ws-a",
            "search",
            "error",
            "2026-05-31T10:02:00.000Z",
            durationMs: 20,
            errorKind: "InvalidOperationException",
            errorMessage: "bad argument",
            errorDetail: "System.InvalidOperationException: bad argument\n   at Miller.Tests.Known()");
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
        Assert.Equal(searchErrorId, summary.RecentErrors[1].Id);
        Assert.Equal("ws-a", summary.RecentErrors[1].WorkspaceId);
        Assert.Equal("bad argument", summary.RecentErrors[1].ErrorMessage);
        Assert.Contains("Miller.Tests.Known", summary.RecentErrors[1].ErrorDetail);
    }

    [Fact]
    public void ReadTelemetrySummary_OldTelemetrySchemaMarksDiagnosticsUnavailable()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _telemetryDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = """
                CREATE TABLE tool_telemetry (
                    id TEXT PRIMARY KEY,
                    ts TEXT NOT NULL,
                    tool TEXT NOT NULL,
                    op TEXT,
                    workspace_id TEXT,
                    duration_ms INTEGER NOT NULL,
                    outcome TEXT NOT NULL,
                    error_kind TEXT,
                    result_count INTEGER,
                    est_tokens INTEGER
                ) STRICT;
                """;
            ddl.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO tool_telemetry
                    (id, ts, tool, workspace_id, duration_ms, outcome, error_kind)
                VALUES
                    ('old-error-row', '2026-05-31T10:01:00.000Z', 'search', 'ws-a', 24, 'error', 'ArgumentException');
                """;
            insert.ExecuteNonQuery();
        }

        DashboardTelemetrySummary summary = DashboardData.ReadTelemetrySummary(_telemetryDb, "ws-a");
        DashboardDiagnostics diagnostics = DashboardData.ReadDiagnostics(new DashboardRuntimeInfo(
            RegistryDbPath: _registryDb,
            TelemetryDbPath: _telemetryDb,
            ToolsRoot: Path.Combine(_dir, ".tools"),
            WebRoot: Path.Combine(_dir, "wwwroot"),
            Url: "http://127.0.0.1:4977",
            PreferredWorkspaceRoot: "/repo/a",
            ProcessId: 1234,
            Version: "test-version",
            ExecutablePath: "/tmp/Miller.Dashboard",
            StdoutLogPath: Path.Combine(_dir, "dashboard.out.log"),
            StderrLogPath: Path.Combine(_dir, "dashboard.err.log")));

        DashboardRecentError error = Assert.Single(summary.RecentErrors);
        Assert.Equal("old-error-row", error.Id);
        Assert.Null(error.ErrorMessage);
        Assert.Null(error.ErrorDetail);
        Assert.False(diagnostics.TelemetryErrorDetailsAvailable);
        Assert.Contains(
            diagnostics.Warnings,
            warning => warning.Contains("older telemetry schema", StringComparison.Ordinal));
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
    public void ReadSnapshot_IncludesWorkspaceIndexFactsAndContextSavings()
    {
        const string runnerText = "class Runner {}\n";
        const string helperText = "static class Helper {}\n";
        const string readmeText = "# Miller\n";
        const string yamlText = "name: miller\n";
        using JulieDbFixture fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows:
            [
                new JulieDbFixture.SymbolRow(
                    "s1",
                    "Runner",
                    "class",
                    "csharp",
                    "src/Runner.cs",
                    "class Runner",
                    1,
                    null),
                new JulieDbFixture.SymbolRow(
                    "s2",
                    "Run",
                    "method",
                    "csharp",
                    "src/Runner.cs",
                    "void Run()",
                    2,
                    "s1"),
                new JulieDbFixture.SymbolRow(
                    "s3",
                    "Helper",
                    "class",
                    "csharp",
                    "src/Helper.cs",
                    "static class Helper",
                    1,
                    null),
            ],
            fileContent: new Dictionary<string, string>
            {
                ["src/Runner.cs"] = runnerText,
                ["src/Helper.cs"] = helperText,
            },
            revisions:
            [
                new JulieDbFixture.RevisionRow(7),
            ],
            extraFiles:
            [
                new JulieDbFixture.FileSpec("docs/README.md")
                {
                    Language = "markdown",
                    DiskText = readmeText,
                },
                new JulieDbFixture.FileSpec("config/miller.yml")
                {
                    Language = "yaml",
                    DiskText = yamlText,
                },
            ]);
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-a",
                "alpha-abcd1234",
                fixture.WorkspaceRoot,
                fixture.DbPath,
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
            registry.MarkScanned("ws-a", 7, DateTimeOffset.Parse("2026-05-31T10:01:00Z"));
        }
        InsertTelemetryRow(
            "ws-a",
            "context",
            "ok",
            "2026-05-31T10:02:00.000Z",
            durationMs: 20,
            estTokens: 500,
            bytesReturned: 2_000,
            sourceBytes: 10_000);
        InsertTelemetryRow(
            "ws-a",
            "inspect",
            "ok",
            "2026-05-31T10:03:00.000Z",
            durationMs: 12,
            estTokens: 350,
            bytesReturned: 1_500,
            sourceBytes: 8_000);
        InsertTelemetryRow(
            "ws-a",
            "search",
            "ok",
            "2026-05-31T10:04:00.000Z",
            durationMs: 5,
            estTokens: 10,
            bytesReturned: 100,
            sourceBytes: 0);

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(_registryDb, _telemetryDb, workspaceId: "ws-a");

        DashboardWorkspaceFacts facts = Assert.Single(snapshot.WorkspaceFacts);
        Assert.Same(facts, snapshot.SelectedWorkspaceFacts);
        Assert.Equal("ws-a", facts.WorkspaceId);
        Assert.Equal("ready", facts.Status);
        Assert.Null(facts.Message);
        Assert.Equal(4, facts.FileCount);
        Assert.Equal(3, facts.SymbolCount);
        Assert.Equal(3, facts.LanguageCount);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(runnerText) +
            Encoding.UTF8.GetByteCount(helperText) +
            Encoding.UTF8.GetByteCount(readmeText) +
            Encoding.UTF8.GetByteCount(yamlText),
            facts.ContentBytes);
        Assert.Equal(7, facts.LastRevision);
        Assert.Equal(snapshot.Workspaces[0].LastScanAt, facts.LastScanAt);
        Assert.Equal("missing", facts.SearchSidecarStatus);
        Assert.Equal("missing", facts.ContentSidecarStatus);

        DashboardLanguageStat csharp = Assert.Single(facts.Languages, language => language.Language == "csharp");
        Assert.Equal(2, csharp.FileCount);
        Assert.Equal(3, csharp.SymbolCount);
        Assert.True(csharp.ContentBytes > 0);
        DashboardLanguageStat markdown = Assert.Single(facts.Languages, language => language.Language == "markdown");
        Assert.Equal(1, markdown.FileCount);
        Assert.Equal(0, markdown.SymbolCount);

        DashboardSymbolKindStat classKind = Assert.Single(facts.SymbolKinds, kind => kind.Kind == "class");
        Assert.Equal(2, classKind.Count);
        DashboardSymbolKindStat methodKind = Assert.Single(facts.SymbolKinds, kind => kind.Kind == "method");
        Assert.Equal(1, methodKind.Count);

        Assert.Equal("tracked", snapshot.ContextSavings.Status);
        Assert.Equal(2, snapshot.ContextSavings.TrackedCalls);
        Assert.Equal(18_000, snapshot.ContextSavings.SourceBytes);
        Assert.Equal(3_500, snapshot.ContextSavings.BytesReturned);
        Assert.Equal(14_500, snapshot.ContextSavings.SavedBytes);
        Assert.Equal(850, snapshot.ContextSavings.EstimatedReturnedTokens);
        Assert.Equal(14500d / 18000d, snapshot.ContextSavings.SavingsRatio);
        Assert.Equal(2, snapshot.ContextSavings.Tools.Count);
        DashboardContextSavingsTool contextTool =
            Assert.Single(snapshot.ContextSavings.Tools, tool => tool.Tool == "context");
        Assert.Equal(10_000, contextTool.SourceBytes);
        Assert.Equal(8_000, contextTool.SavedBytes);

        Assert.NotNull(snapshot.Health);
        Assert.Equal("ws-a", snapshot.Health.WorkspaceId);
        Assert.Equal("usable_with_warnings", snapshot.Health.State);
        Assert.Equal("missing", snapshot.Health.SearchSidecarStatus);
        Assert.Equal("missing", snapshot.Health.ContentSidecarStatus);
        Assert.NotNull(snapshot.Onboarding);
        Assert.Equal("ws-a", snapshot.Onboarding.WorkspaceId);
        Assert.Equal(3, snapshot.Onboarding.TotalCalls);
        Assert.Contains(snapshot.Onboarding.StartHere, item => item.Contains("search", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadSnapshot_IncludesSelectedWorkspaceLocalMetrics()
    {
        using JulieDbFixture fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows:
            [
                new JulieDbFixture.SymbolRow(
                    "aa11223344556677889900aabbccdd11",
                    "HotPath",
                    "method",
                    "csharp",
                    "src/HotPath.cs",
                    "void HotPath()",
                    5,
                    null)
                {
                    EndLine = 9,
                    StartByte = 50,
                    EndByte = 90,
                },
                new JulieDbFixture.SymbolRow(
                    "aa11223344556677889900aabbccdd12",
                    "CopyA",
                    "method",
                    "csharp",
                    "src/A.cs",
                    "void CopyA()",
                    3,
                    null),
                new JulieDbFixture.SymbolRow(
                    "aa11223344556677889900aabbccdd13",
                    "CopyB",
                    "method",
                    "csharp",
                    "src/B.cs",
                    "void CopyB()",
                    7,
                    null),
            ],
            revisions:
            [
                new JulieDbFixture.RevisionRow(7),
            ]);
        Exec(fixture.DbPath, """
            UPDATE symbols SET body_hash = 'clone-hash' WHERE symbol_id IN
                ('aa11223344556677889900aabbccdd12', 'aa11223344556677889900aabbccdd13');
            INSERT INTO complexity_metrics
                (complexity_metric_id, file_id, path, language, scope, symbol_id, algorithm_id, covered_lines,
                 covered_bytes, decision_count, loop_count, max_nesting_depth, parameter_count, start_line,
                 start_column, end_line, end_column, start_byte, end_byte)
            VALUES
                ('metric-hot', 'file:src/HotPath.cs', 'src/HotPath.cs', 'csharp', 'symbol',
                 'aa11223344556677889900aabbccdd11', 'julie-ast-complexity-v1',
                 12, 120, 18, 2, 2, 0, 5, 0, 9, 0, 50, 90);
            """);
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-a",
                "alpha-abcd1234",
                fixture.WorkspaceRoot,
                fixture.DbPath,
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
            registry.MarkScanned("ws-a", 7, DateTimeOffset.Parse("2026-05-31T10:01:00Z"));
        }

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(_registryDb, _telemetryDb, "ws-a");

        Assert.NotNull(snapshot.LocalMetrics);
        Assert.Equal("ready", snapshot.LocalMetrics!.State);
        Assert.Equal("HotPath", snapshot.LocalMetrics.ComplexityHotspots[0].SymbolName);
        Assert.Equal("high", snapshot.LocalMetrics.ComplexityHotspots[0].Severity);
        Assert.Equal("clone-hash", snapshot.LocalMetrics.CloneGroups[0].BodyHash);
        Assert.Equal(2, snapshot.LocalMetrics.CloneGroups[0].Count);
    }

    [Fact]
    public void ReadSnapshot_IncludesPatternInventoryFamilies()
    {
        using JulieDbFixture fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows:
            [
                new JulieDbFixture.SymbolRow(
                    "sym-orders",
                    "OrdersView",
                    "view",
                    "razor",
                    "Views/Orders.cshtml",
                    null,
                    1,
                    null),
            ],
            fileContent: new Dictionary<string, string>
            {
                ["Views/Orders.cshtml"] = "<button hx-get=\"/orders\"></button>\n",
            });
        Exec(fixture.DbPath, """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('fact-hx-get', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 1, 9, 1, 25, 8, 24, 1.0, '{"name":"hx-get"}'),
                ('fact-hx-trigger', 'file:Views/Orders.cshtml', 'Views/Orders.cshtml', 'razor',
                 'htmx.attribute.v1', 'attribute', 'attribute', 'sym-orders',
                 1, 26, 1, 45, 25, 44, 1.0, '{"name":"hx-trigger"}');
            """);
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-patterns",
                "patterns-abcd1234",
                fixture.WorkspaceRoot,
                fixture.DbPath,
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
            registry.MarkScanned("ws-patterns", 1, DateTimeOffset.Parse("2026-05-31T10:01:00Z"));
        }

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(_registryDb, _telemetryDb, "ws-patterns");

        Assert.NotNull(snapshot.PatternInventory);
        Assert.Equal("ready", snapshot.PatternInventory!.State);
        DashboardPatternFamily family = Assert.Single(snapshot.PatternInventory.Families);
        Assert.Equal("htmx.attribute", family.Family);
        Assert.Equal(2, family.FactCount);
        Assert.Equal(1, family.PatternCount);
        Assert.Equal("razor", Assert.Single(family.Languages));
    }

    [Fact]
    public void DashboardIndexFactsCache_ReusesFactsWithinTtl()
    {
        using JulieDbFixture fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows:
            [
                new JulieDbFixture.SymbolRow(
                    "s1",
                    "Cached",
                    "class",
                    "csharp",
                    "src/Cached.cs",
                    "class Cached",
                    1,
                    null),
            ]);
        var workspace = new DashboardWorkspaceRow(
            "ws-cache",
            "cache-abcd1234",
            fixture.WorkspaceRoot,
            fixture.DbPath,
            "2026-05-31T10:00:00Z",
            "2026-05-31T10:01:00Z",
            3,
            "ready",
            null);

        DashboardIndexFactsCache.Clear();
        DashboardWorkspaceFacts first = DashboardIndexFactsCache.Read(workspace);
        DashboardWorkspaceFacts second = DashboardIndexFactsCache.Read(workspace);

        Assert.Same(first, second);
        DashboardIndexFactsCache.Clear();
        DashboardWorkspaceFacts third = DashboardIndexFactsCache.Read(workspace);
        Assert.NotSame(first, third);
    }

    [Fact]
    public void ReadSnapshot_UnreadableWorkspaceDbReturnsFactsErrorNotCrash()
    {
        string corruptDb = Path.Combine(_dir, "corrupt-symbols.db");
        File.WriteAllText(corruptDb, "not a sqlite database");
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-corrupt",
                "corrupt-abcd1234",
                Path.Combine(_dir, "corrupt"),
                corruptDb,
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
        }

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(_registryDb, _telemetryDb, workspaceId: "ws-corrupt");

        DashboardWorkspaceFacts facts = Assert.Single(snapshot.WorkspaceFacts);
        Assert.Same(facts, snapshot.SelectedWorkspaceFacts);
        Assert.Equal("ws-corrupt", facts.WorkspaceId);
        Assert.Equal("unreadable", facts.Status);
        Assert.Equal(0, facts.FileCount);
        Assert.Equal(0, facts.SymbolCount);
        Assert.False(string.IsNullOrWhiteSpace(facts.Message));
    }

    [Fact]
    public void ReadSnapshot_IncompatibleSchemaArtifactReturnsHealthUnavailableNotCrash()
    {
        using JulieDbFixture fixture = JulieDbFixture.Create(
            schemaVersion: 3,
            contractValue: "3",
            rows: Array.Empty<JulieDbFixture.SymbolRow>());
        Exec(fixture.DbPath, """
            UPDATE artifact_metadata SET value = '2.8.1' WHERE key = 'binary_version';
            """);
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-schema3",
                "schema3-abcd1234",
                fixture.WorkspaceRoot,
                fixture.DbPath,
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
            registry.MarkScanned("ws-schema3", 1, DateTimeOffset.Parse("2026-05-31T10:01:00Z"));
        }

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(_registryDb, _telemetryDb, workspaceId: "ws-schema3");

        Assert.NotNull(snapshot.Health);
        Assert.Equal("ws-schema3", snapshot.Health!.WorkspaceId);
        Assert.Equal("unavailable", snapshot.Health.State);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Health.Error));
        Assert.Contains("workspace full", snapshot.Health.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3", snapshot.Health.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSnapshot_MissingIndexDbRendersDegradedHealthPanelNotBlank()
    {
        // Regression guard: only IncompatibleExtractException may blank the whole health panel. A plain
        // missing symbols.db (never indexed / .miller deleted) must degrade the extraction SECTIONS while
        // the panel keeps rendering leader/sidecar facts — that is what ReadExtractionHealthOrUnavailable
        // is for, and bypassing it lost exactly this behavior once already.
        string root = Path.Combine(_dir, "never-indexed-root");
        Directory.CreateDirectory(root);
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-noindex",
                "noindex-abcd1234",
                root,
                Path.Combine(root, ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
        }

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(_registryDb, _telemetryDb, workspaceId: "ws-noindex");

        Assert.NotNull(snapshot.Health);
        Assert.Equal("ws-noindex", snapshot.Health!.WorkspaceId);
        // A COMPUTED verdict (possibly "unavailable" — that is the honest health state for a missing index)
        // with real leader/sidecar facts and no Error — NOT the catch's canned blank shape, whose signature
        // is Error != null + summary "workspace health is unavailable" + all statuses "unknown".
        Assert.Null(snapshot.Health.Error);
        Assert.NotEqual("workspace health is unavailable", snapshot.Health.Summary);
        Assert.NotEqual("unknown", snapshot.Health.SearchSidecarStatus);
        Assert.NotEqual("unknown", snapshot.Health.ContentSidecarStatus);
    }

    [Fact]
    public void ReadSnapshot_ReadsIndexFactsOnlyForSelectedWorkspace()
    {
        using JulieDbFixture selectedFixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows:
            [
                new JulieDbFixture.SymbolRow(
                    "s1",
                    "Selected",
                    "class",
                    "csharp",
                    "src/Selected.cs",
                    "class Selected",
                    1,
                    null),
            ]);
        string unselectedCorruptDb = Path.Combine(_dir, "unselected-corrupt-symbols.db");
        File.WriteAllText(unselectedCorruptDb, "not a sqlite database");
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-selected",
                "selected-abcd1234",
                selectedFixture.WorkspaceRoot,
                selectedFixture.DbPath,
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
            registry.UpsertSeen(
                "ws-unselected",
                "unselected-abcd1234",
                Path.Combine(_dir, "unselected"),
                unselectedCorruptDb,
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:01:00Z"));
        }

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(
            _registryDb,
            _telemetryDb,
            workspaceId: "ws-selected");

        DashboardWorkspaceFacts facts = Assert.Single(snapshot.WorkspaceFacts);
        Assert.Equal("ws-selected", facts.WorkspaceId);
        Assert.Same(facts, snapshot.SelectedWorkspaceFacts);
        Assert.Equal("ready", facts.Status);
        Assert.DoesNotContain(snapshot.WorkspaceFacts, row => row.WorkspaceId == "ws-unselected");
    }

    [Fact]
    public void ReadSnapshot_CountsAllLanguagesWhileDisplayingTopBreakdown()
    {
        JulieDbFixture.FileSpec[] languageFiles = Enumerable.Range(1, 13)
            .Select(i => new JulieDbFixture.FileSpec($"src/lang-{i}.txt")
            {
                Language = $"lang{i:00}",
                DiskText = $"language {i}",
            })
            .ToArray();
        using JulieDbFixture fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows: Array.Empty<JulieDbFixture.SymbolRow>(),
            extraFiles: languageFiles);
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-languages",
                "languages-abcd1234",
                fixture.WorkspaceRoot,
                fixture.DbPath,
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
        }

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(_registryDb, _telemetryDb, workspaceId: "ws-languages");

        DashboardWorkspaceFacts facts = Assert.Single(snapshot.WorkspaceFacts);
        Assert.Equal(13, facts.LanguageCount);
        Assert.Equal(12, facts.Languages.Count);
    }

    [Fact]
    public async Task WorkspaceShell_RendersVisibleTelemetryAndHtmxTargets()
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
                        Id: "0197a000-0000-7000-8000-000000000001",
                        Ts: "2026-05-31T10:01:00Z",
                        Tool: "search",
                        Op: "auto",
                        ErrorKind: "InvalidOperationException",
                        DurationMs: 24,
                        WorkspaceId: "ws-a",
                        WorkspaceDisplayId: "alpha-abcd1234",
                        ErrorMessage: "bad argument",
                        ErrorDetail: "System.InvalidOperationException: bad argument\n   at Miller.Tests.Known()")
                ]),
            SelectedWorkspaceId: "ws-a");

        string html = await RenderComponentAsync<WorkspaceShell>(new Dictionary<string, object?>
        {
            ["Snapshot"] = snapshot,
        });

        Assert.Contains("alpha-abcd1234", html);
        Assert.Contains("All workspaces", html);
        Assert.Contains("href=\"/\"", html);
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
        Assert.Contains("bad argument", html);
        Assert.Contains("Miller.Tests.Known", html);
        Assert.Contains("data-issue-details", html);
        Assert.Contains("data-issue-id=\"0197a000-0000-7000-8000-000000000001\"", html);
        Assert.Contains("<summary>view issue details</summary>", html);
        Assert.Contains("class=\"copy-issue-button\"", html);
        Assert.Contains("data-copy-target=\"issue-copy-0197a000-0000-7000-8000-000000000001\"", html);
        Assert.Contains(">Copy</button>", html);
        Assert.DoesNotContain("onclick=", html);
        Assert.Contains("id=\"issue-copy-0197a000-0000-7000-8000-000000000001\"", html);
        Assert.DoesNotContain("copy issue details", html);
        Assert.Contains("cid", html);
        Assert.Contains("/js/dashboard-site.js", html);
        Assert.Contains("activity.json?workspace_id=ws-a", html);
    }

    [Fact]
    public async Task WorkspaceShell_RendersWorkspaceFactsContextSavingsAndSnapshotLink()
    {
        var facts = new DashboardWorkspaceFacts(
            "ws-a",
            "alpha-abcd1234",
            "/repo/a",
            "/repo/a/.miller/symbols.db",
            "ready",
            null,
            FileCount: 4,
            SymbolCount: 3,
            LanguageCount: 2,
            ContentBytes: 12_800,
            LastRevision: 42,
            LastScanAt: "2026-05-31T10:01:00Z",
            SearchSidecarStatus: "missing",
            Languages:
            [
                new DashboardLanguageStat("csharp", FileCount: 3, SymbolCount: 3, ContentBytes: 11_200),
                new DashboardLanguageStat("markdown", FileCount: 1, SymbolCount: 0, ContentBytes: 1_600),
            ],
            SymbolKinds:
            [
                new DashboardSymbolKindStat("class", Count: 2),
                new DashboardSymbolKindStat("method", Count: 1),
            ]);
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
                    null),
            ],
            Telemetry: new DashboardTelemetrySummary("ws-a", [], 0, null, null, []),
            SelectedWorkspaceId: "ws-a",
            WorkspaceFacts: [facts],
            SelectedWorkspaceFacts: facts,
            ContextSavings: new DashboardContextSavingsSummary(
                "ws-a",
                "tracked",
                TrackedCalls: 2,
                SourceBytes: 18_000,
                BytesReturned: 3_500,
                SavedBytes: 14_500,
                EstimatedReturnedTokens: 850,
                Tools:
                [
                    new DashboardContextSavingsTool(
                        "context",
                        TrackedCalls: 1,
                        SourceBytes: 10_000,
                        BytesReturned: 2_000,
                        SavedBytes: 8_000,
                        EstimatedReturnedTokens: 500),
                ]),
            Health: new DashboardWorkspaceHealthPanel(
                "ws-a",
                "usable_with_warnings",
                "index readable with warnings",
                Warnings:
                [
                    new DashboardHealthWarning(
                        "search_sidecar",
                        "usable_with_warnings",
                        "search_sidecar is missing"),
                ],
                RecommendedActions: ["run workspace refresh"],
                Leader: "unknown",
                SearchSidecarStatus: "missing",
                ContentSidecarStatus: "current",
                ParseDiagnosticCount: 2,
                CapabilityGapCount: 1),
            Onboarding: new DashboardWorkspaceOnboardingPanel(
                "ws-a",
                "ready",
                TotalCalls: 4,
                StartHere:
                [
                    "run workspace health first when taking over this repo",
                    "use search to find candidate symbols, then inspect the selected result before editing",
                ],
                HotTargets:
                [
                    new DashboardOnboardingTarget(
                        "symbol",
                        "ReadReferencesAsync",
                        "method",
                        "src/Miller.Indexing/ReferenceReader.cs",
                        Line: 42,
                        Calls: 3),
                ],
                CommonMisses:
                [
                    new DashboardOnboardingMiss("search", null, "empty", Calls: 2),
                ],
                Notes: ["search has recent empty results"]),
            LocalMetrics: new DashboardLocalMetricsPanel(
                "ws-a",
                "ready",
                ComplexityHotspots:
                [
                    new DashboardMetricComplexityHotspot(
                        "high",
                        "HotPath",
                        "method",
                        "src/HotPath.cs",
                        Line: 5,
                        DecisionCount: 18,
                        MaxNestingDepth: 2),
                ],
                CloneGroups:
                [
                    new DashboardMetricCloneGroup(
                        "clone-hash",
                        Count: 2,
                        Symbols:
                        [
                            new DashboardMetricCloneSymbol("CopyA", "method", "src/A.cs", Line: 3),
                        ]),
                ]));

        string html = await RenderComponentAsync<WorkspaceShell>(new Dictionary<string, object?>
        {
            ["Snapshot"] = snapshot,
        });

        Assert.Contains("snapshot.json?workspace_id=ws-a", html);
        Assert.Contains("activity.json?workspace_id=ws-a", html);
        Assert.Contains("diagnostics.json", html);
        Assert.Contains("Index transparency", html);
        Assert.Contains("4 files", html);
        Assert.Contains("3 symbols", html);
        Assert.Contains("2 languages", html);
        Assert.Contains("search.db missing", html);
        Assert.Contains("csharp", html);
        Assert.Contains("markdown", html);
        Assert.Contains("class", html);
        Assert.Contains("Context saved", html);
        Assert.Contains("hx-target=\"#workspace-detail-stack\"", html);
        Assert.Contains("14.5 KB", html);
        Assert.Contains("850 tokens", html);
        Assert.Contains("context", html);
        Assert.Contains("Workspace health", html);
        Assert.Contains("usable_with_warnings", html);
        Assert.Contains("parse diagnostics", html);
        Assert.Contains("capability gaps", html);
        Assert.Contains("run workspace refresh", html);
        Assert.Contains("Workspace onboarding", html);
        Assert.Contains("ReadReferencesAsync", html);
        Assert.Contains("search has recent empty results", html);
        Assert.Contains("Local metrics", html);
        Assert.Contains("HotPath", html);
        Assert.Contains("clone-hash", html);
    }

    [Fact]
    public async Task WorkspaceShell_RendersDetailStylingHooks()
    {
        var facts = new DashboardWorkspaceFacts(
            "ws-a",
            "alpha-abcd1234",
            "/repo/a",
            "/repo/a/.miller/symbols.db",
            "ready",
            null,
            FileCount: 4,
            SymbolCount: 3,
            LanguageCount: 2,
            ContentBytes: 12_800,
            LastRevision: 42,
            LastScanAt: "2026-05-31T10:01:00Z",
            SearchSidecarStatus: "fresh",
            Languages:
            [
                new DashboardLanguageStat("csharp", FileCount: 3, SymbolCount: 3, ContentBytes: 11_200),
                new DashboardLanguageStat("markdown", FileCount: 1, SymbolCount: 0, ContentBytes: 1_600),
            ],
            SymbolKinds:
            [
                new DashboardSymbolKindStat("class", Count: 2),
            ]);
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
                    null),
                new DashboardWorkspaceRow(
                    "ws-b",
                    "beta-efgh5678",
                    "/repo/b",
                    "/repo/b/.miller/symbols.db",
                    "2026-05-31T10:00:00Z",
                    null,
                    null,
                    "missing",
                    "missing index"),
            ],
            Telemetry: new DashboardTelemetrySummary("ws-a", [], 0, null, null, []),
            SelectedWorkspaceId: "ws-a",
            WorkspaceFacts: [facts],
            SelectedWorkspaceFacts: facts,
            ContextSavings: DashboardContextSavingsSummary.NotTracked("ws-a"));

        string html = await RenderComponentAsync<WorkspaceShell>(new Dictionary<string, object?>
        {
            ["Snapshot"] = snapshot,
        });

        Assert.Contains("class=\"dashboard-hero\"", html);
        Assert.Contains("class=\"back-link\"", html);
        Assert.Contains("metric-band", html);
        Assert.Contains("class=\"language-pill\"", html);
        Assert.Contains("detail-grid", html);
        Assert.Contains("id=\"telemetry-panel\"", html);
    }

    [Fact]
    public async Task WorkspacesShell_RendersIndexListHooksAndLinks()
    {
        var index = new DashboardWorkspaceIndex(
            Entries:
            [
                new DashboardWorkspaceIndexEntry(
                    new DashboardWorkspaceRow(
                        "ws-a",
                        "alpha-abcd1234",
                        "/repo/a",
                        "/repo/a/.miller/symbols.db",
                        "2026-05-31T10:00:00Z",
                        "2026-05-31T10:01:00Z",
                        42,
                        "ready",
                        null),
                    new DashboardWorkspaceFacts(
                        "ws-a",
                        "alpha-abcd1234",
                        "/repo/a",
                        "/repo/a/.miller/symbols.db",
                        "ready",
                        null,
                        FileCount: 4,
                        SymbolCount: 3,
                        LanguageCount: 2,
                        ContentBytes: 12_800,
                        LastRevision: 42,
                        LastScanAt: "2026-05-31T10:01:00Z",
                        SearchSidecarStatus: "fresh",
                        Languages:
                        [
                            new DashboardLanguageStat("csharp", FileCount: 3, SymbolCount: 3, ContentBytes: 11_200),
                            new DashboardLanguageStat("markdown", FileCount: 1, SymbolCount: 0, ContentBytes: 1_600),
                        ],
                        SymbolKinds: []),
                    RootExists: true),
                new DashboardWorkspaceIndexEntry(
                    new DashboardWorkspaceRow(
                        "ws-b",
                        "beta-efgh5678",
                        "/repo/b-gone",
                        "/repo/b-gone/.miller/symbols.db",
                        "2026-05-31T10:00:00Z",
                        null,
                        null,
                        "missing",
                        "missing index"),
                    DashboardIndexFactsReader.Read(new DashboardWorkspaceRow(
                        "ws-b",
                        "beta-efgh5678",
                        "/repo/b-gone",
                        "/repo/b-gone/.miller/symbols.db",
                        "2026-05-31T10:00:00Z",
                        null,
                        null,
                        "missing",
                        "missing index")),
                    RootExists: false),
            ],
            WorkspaceCount: 2,
            TotalFiles: 4,
            TotalSymbols: 3,
            LanguageCount: 2,
            LiveCount: 1,
            MissingRootCount: 1,
            ErrorCount: 0);

        string html = await RenderComponentAsync<WorkspacesShell>(new Dictionary<string, object?>
        {
            ["Index"] = index,
        });

        Assert.Contains("class=\"dashboard-hero\"", html);
        Assert.Contains("id=\"workspace-index\"", html);
        Assert.Contains("class=\"ws-index-row\"", html);
        Assert.Contains("href=\"/workspace?workspace_id=ws-a\"", html);
        Assert.Contains("class=\"workspace-status-rail ok\"", html);
        Assert.Contains("class=\"workspace-row-main\"", html);
        Assert.Contains("alpha-abcd1234", html);
        Assert.Contains("csharp", html);
        Assert.Contains("id=\"workspace-filter\"", html);
        Assert.Contains("x-data=\"workspaceIndexFilter\"", html);
        Assert.Contains("ws-stale-collapse", html);
        Assert.Contains("miller workspace prune", html);
        Assert.Contains("root missing", html);
        Assert.DoesNotContain("Index.Entries", html);
    }

    private static DashboardWorkspaceIndex SampleWorkspaceIndex(string? error = null)
    {
        var liveRow = new DashboardWorkspaceRow(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db",
            "2026-05-31T10:00:00Z", "2026-05-31T10:01:00Z", 42, "ready", null);
        var liveFacts = new DashboardWorkspaceFacts(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db", "ready", null,
            FileCount: 4, SymbolCount: 3, LanguageCount: 2, ContentBytes: 12_800,
            LastRevision: 42, LastScanAt: "2026-05-31T10:01:00Z", SearchSidecarStatus: "fresh",
            Languages: [new DashboardLanguageStat("csharp", 3, 3, 11_200)], SymbolKinds: []);
        var staleRow = new DashboardWorkspaceRow(
            "ws-b", "beta-efgh5678", "/repo/b-gone", "/repo/b-gone/.miller/symbols.db",
            "2026-05-31T10:00:00Z", null, null, "missing", "missing index");

        return new DashboardWorkspaceIndex(
            Entries:
            [
                new DashboardWorkspaceIndexEntry(liveRow, liveFacts, RootExists: true),
                new DashboardWorkspaceIndexEntry(staleRow, DashboardIndexFactsReader.Read(staleRow), RootExists: false),
            ],
            WorkspaceCount: 2, TotalFiles: 4, TotalSymbols: 3, LanguageCount: 2,
            LiveCount: 1, MissingRootCount: 1, ErrorCount: 0, Error: error);
    }

    [Fact]
    public async Task WorkspaceIndex_TableRolesAreWellFormed()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(),
        });

        Assert.Contains("<div class=\"ws-index\" role=\"table\"", html);
        Assert.Contains("<div class=\"ws-index-head\" role=\"row\">", html);
        Assert.Contains("<div class=\"ws-index-row\"", html);
        Assert.Contains("role=\"row\"", html);
        Assert.Contains("role=\"columnheader\"", html);
        Assert.Contains("role=\"cell\"", html);
        Assert.Contains("<a class=\"workspace-name\"", html);
        Assert.DoesNotContain("<a class=\"ws-index-row\"", html);
    }

    [Fact]
    public async Task WorkspaceIndex_EveryRowHasSameCellCountAsHeaderColumns()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(),
        });

        int headerRows = CountOccurrences(html, "<div class=\"ws-index-head\" role=\"row\">");
        int columnHeaders = CountOccurrences(html, "role=\"columnheader\"");
        int dataRows = CountOccurrences(html, "<div class=\"ws-index-row\"");
        int cells = CountOccurrences(html, "role=\"cell\"");

        Assert.Equal(2, headerRows);
        Assert.Equal(2, dataRows);

        int columnsPerTable = columnHeaders / headerRows;
        Assert.Equal(8, columnsPerTable);
        Assert.Equal(columnsPerTable * dataRows, cells);
    }

    [Fact]
    public async Task WorkspaceIndex_AriaSortLivesOnColumnHeadersNotButtons()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(),
        });

        Assert.Contains("role=\"columnheader\" aria-sort=\"none\"", html);
        Assert.DoesNotContain("aria-sort=\"none\" x-on:click", html);
        Assert.DoesNotContain("class=\"col-sort\" data-sort-col=\"workspace\" aria-sort", html);
    }

    [Fact]
    public async Task WorkspaceIndex_StaleTableCarriesItsOwnHeaderRow()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(),
        });

        int staleAt = html.IndexOf("ws-index-stale", StringComparison.Ordinal);
        Assert.True(staleAt >= 0);
        string staleSection = html[staleAt..];

        Assert.Contains("ws-index-head", staleSection);
        Assert.Contains("Languages", staleSection);
        Assert.DoesNotContain("col-sort", staleSection);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    [Fact]
    public async Task WorkspaceIndex_SortableHeadersAreButtonsWithAriaSort()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(),
        });

        // Each numeric/name column header is a button carrying aria-sort state for client-side sorting.
        Assert.Contains("data-sort-col=\"workspace\"", html);
        Assert.Contains("data-sort-col=\"files\"", html);
        Assert.Contains("data-sort-col=\"symbols\"", html);
        Assert.Contains("data-sort-col=\"rev\"", html);
        Assert.Contains("data-sort-col=\"activity\"", html);
        Assert.Contains("aria-sort=\"none\"", html);
        Assert.Contains("class=\"col-sort", html);
        // Rows expose clean numeric sort keys so JS sorts values, not formatted strings.
        Assert.Contains("data-sort-files=\"4\"", html);
        Assert.Contains("data-sort-symbols=\"3\"", html);
        Assert.Contains("data-sort-rev=\"42\"", html);
        Assert.Contains("data-sort-name=\"alpha-abcd1234\"", html);
    }

    [Fact]
    public async Task WorkspaceIndex_PollsWorkspacesFragmentVisibilityGated()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(),
        });

        // A6: the section refreshes itself from the orphaned fragment route, visibility-gated,
        // and patches in place via idiomorph rather than tearing the subtree down on every poll.
        Assert.Contains("hx-get=\"/fragments/workspaces\"", html);
        Assert.Contains("data-poll-trigger=\"every 30s\"", html);
        Assert.Contains("hx-trigger=\"every 30s\"", html);
        Assert.Contains("hx-ext=\"morph\"", html);
        Assert.Contains("hx-swap=\"morph:outerHTML\"", html);
    }

    [Fact]
    public async Task WorkspaceIndex_RendersRegistryErrorNotice()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(error: "registry read degraded: database is locked"),
        });

        // Task 1's Index.Error surfaces as a visible notice using the existing error styling.
        Assert.Contains("class=\"notice error-notice\"", html);
        Assert.Contains("registry read degraded: database is locked", html);
    }

    [Fact]
    public async Task WorkspaceIndex_OmitsErrorNoticeWhenErrorIsNull()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(error: null),
        });

        Assert.DoesNotContain("error-notice", html);
    }

    [Fact]
    public async Task WorkspaceIndex_RendersRemoveConfirmFormPerRow()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(),
        });

        // Every entry — live and stale — carries an expandable confirm form posting to the remove endpoint.
        Assert.Contains("action=\"/workspace/remove\"", html);
        Assert.Contains("name=\"workspace_id\" value=\"ws-a\"", html);
        Assert.Contains("name=\"workspace_id\" value=\"ws-b\"", html);
        // The confirm copy states the consequence and the recovery path.
        Assert.Contains("workspace open", html);
        Assert.Contains("Confirm remove", html);
        Assert.Contains("Cancel", html);
        // Antiforgery token on the mutation form, so an arbitrary page cannot POST to the local port.
        Assert.Contains("name=\"__RequestVerificationToken\"", html);
    }

    [Fact]
    public async Task WorkspaceDetailPanel_RendersRemoveConfirmForm()
    {
        var facts = new DashboardWorkspaceFacts(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db",
            "ready", null, 1, 1, 1, 100, 42, "2026-06-12T09:00:00Z", "fresh",
            Array.Empty<DashboardLanguageStat>(), Array.Empty<DashboardSymbolKindStat>());

        string html = await RenderComponentAsync<WorkspaceDetailPanel>(new Dictionary<string, object?>
        {
            ["Facts"] = facts,
        });

        // The detail page carries the same expandable confirm form as the all-workspaces rows.
        Assert.Contains("action=\"/workspace/remove\"", html);
        Assert.Contains("name=\"workspace_id\" value=\"ws-a\"", html);
        Assert.Contains("Confirm remove", html);
        Assert.Contains("data-close-details", html);
        Assert.DoesNotContain(">Cancel</a>", html);
        Assert.Contains("name=\"__RequestVerificationToken\"", html);
    }

    [Fact]
    public async Task WorkspaceIndex_RendersPruneButtonForMissingRoots()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(),
        });

        // The stale section offers one-click prune for missing-root registrations (sample has exactly one).
        Assert.Contains("action=\"/workspaces/prune\"", html);
        Assert.Contains("Prune 1 stale", html);
    }

    [Fact]
    public async Task WorkspaceIndex_RendersRemoveOutcomeNotices()
    {
        string pruned = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(),
            ["Notice"] = "pruned",
            ["NoticeDetail"] = "2",
        });
        Assert.Contains("Pruned 2 stale registration", pruned);
        Assert.DoesNotContain("error-notice", pruned);

        string refused = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(),
            ["Notice"] = "remove-refused-in-use",
            ["NoticeDetail"] = "/repo/a",
        });
        Assert.Contains("error-notice", refused);
        Assert.Contains("/repo/a", refused);
        Assert.Contains("in use", refused);

        // An unrecognised code renders nothing (the notice rides a query param anyone can craft).
        string unknown = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(),
            ["Notice"] = "bogus-code",
            ["NoticeDetail"] = "x",
        });
        Assert.DoesNotContain("bogus-code", unknown);
    }

    [Fact]
    public async Task WorkspacesShell_ForwardsOutcomeNoticeToIndex()
    {
        string html = await RenderComponentAsync<WorkspacesShell>(new Dictionary<string, object?>
        {
            ["Index"] = SampleWorkspaceIndex(),
            ["Notice"] = "removed",
            ["NoticeDetail"] = "/repo/a",
        });

        Assert.Contains("Removed /repo/a", html);
    }

    [Fact]
    public void ReadIndex_AnnotatesRootExistsAndLiveMissingErrorCounts()
    {
        string liveRoot = Path.Combine(_dir, "live-root");
        Directory.CreateDirectory(liveRoot);
        string missingRoot = Path.Combine(_dir, "missing-root");
        // missingRoot intentionally not created

        using JulieDbFixture liveFixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows:
            [
                new JulieDbFixture.SymbolRow(
                    "s1", "Live", "class", "csharp", "src/Live.cs", "class Live", 1, null),
            ]);

        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-live",
                "live-abcd1234",
                liveRoot,
                liveFixture.DbPath,
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
            registry.UpsertSeen(
                "ws-gone",
                "gone-abcd1234",
                missingRoot,
                Path.Combine(missingRoot, ".miller", "symbols.db"),
                WorkspaceRegistryState.Stale,
                DateTimeOffset.Parse("2026-05-31T09:00:00Z"));
            registry.UpsertSeen(
                "ws-err",
                "err-abcd1234",
                liveRoot,
                Path.Combine(liveRoot, "no-such-symbols.db"),
                WorkspaceRegistryState.Error,
                DateTimeOffset.Parse("2026-05-31T08:00:00Z"));
            registry.MarkError("ws-err", "synthetic error", DateTimeOffset.Parse("2026-05-31T08:01:00Z"));
            // Both missing-root AND errored: must count as missing-root ONLY so the counts stay a partition.
            registry.UpsertSeen(
                "ws-gone-err",
                "goneerr-abcd1234",
                Path.Combine(_dir, "missing-err-root"),
                Path.Combine(_dir, "missing-err-root", ".miller", "symbols.db"),
                WorkspaceRegistryState.Error,
                DateTimeOffset.Parse("2026-05-31T07:00:00Z"));
            registry.MarkError("ws-gone-err", "synthetic error", DateTimeOffset.Parse("2026-05-31T07:01:00Z"));
        }

        DashboardWorkspaceIndex index = DashboardData.ReadIndex(_registryDb);

        Assert.Equal(4, index.WorkspaceCount);
        Assert.Equal(1, index.LiveCount);
        Assert.Equal(2, index.MissingRootCount);
        Assert.Equal(1, index.ErrorCount);
        Assert.Equal(index.WorkspaceCount, index.LiveCount + index.MissingRootCount + index.ErrorCount);

        DashboardWorkspaceIndexEntry live = Assert.Single(index.Entries, e => e.Workspace.WorkspaceId == "ws-live");
        Assert.True(live.RootExists);
        Assert.False(live.IsStale);

        DashboardWorkspaceIndexEntry gone = Assert.Single(index.Entries, e => e.Workspace.WorkspaceId == "ws-gone");
        Assert.False(gone.RootExists);
        Assert.True(gone.IsStale);

        DashboardWorkspaceIndexEntry err = Assert.Single(index.Entries, e => e.Workspace.WorkspaceId == "ws-err");
        Assert.True(err.RootExists);
        Assert.True(err.IsStale);
        Assert.Equal("error", err.Workspace.State);
    }

    [Fact]
    public void DashboardHost_PreservesFragmentCompatibilityRoutes()
    {
        string endpoints = File.ReadAllText(Path.Combine(
            Miller.Tests.ScaleTestSupport.RepoRoot(),
            "src",
            "Miller.Dashboard",
            "Endpoints",
            "DashboardEndpoints.cs"));
        // The endpoint composition lives in DashboardHostPipeline (extracted from Program.cs for TestServer).
        string pipeline = File.ReadAllText(Path.Combine(
            Miller.Tests.ScaleTestSupport.RepoRoot(),
            "src",
            "Miller.Dashboard",
            "DashboardHostPipeline.cs"));

        Assert.Contains("MapGet(\"/fragments/dashboard\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/fragments/workspaces\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("MapMethods(\"/favicon.ico\"", pipeline, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/diagnostics.json\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("MapDashboardEndpoints", pipeline, StringComparison.Ordinal);
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
        string errorId = InsertTelemetryRow(
            "ws-json",
            "search",
            "error",
            "2026-05-31T12:02:00.000Z",
            durationMs: 25,
            errorKind: "ArgumentException",
            errorMessage: "missing query",
            errorDetail: "System.ArgumentException: missing query");

        string json = DashboardData.RenderTelemetryJson(_telemetryDb, "ws-json");

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("ws-json", root.GetProperty("workspace_id").GetString());
        Assert.Equal(1, root.GetProperty("total_calls").GetInt64());
        JsonElement tool = root.GetProperty("tools")[0];
        Assert.Equal("search", tool.GetProperty("tool").GetString());
        Assert.Equal(1, tool.GetProperty("calls").GetInt64());
        Assert.Equal(25, tool.GetProperty("p95_ms").GetInt64());
        Assert.Equal(0, tool.GetProperty("sum_est_tokens").GetInt64());
        Assert.True(tool.TryGetProperty("last_call_ts", out _));
        JsonElement error = root.GetProperty("recent_errors")[0];
        Assert.Equal(errorId, error.GetProperty("id").GetString());
        Assert.Equal("missing query", error.GetProperty("error_message").GetString());
        Assert.Equal("System.ArgumentException: missing query", error.GetProperty("error_detail").GetString());
    }

    [Fact]
    public void RenderDiagnosticsJson_ReportsResolvedPathsAndTelemetrySchema()
    {
        using (TelemetryLedger.Open(_telemetryDb, "ws-json", "/repo/json"))
        {
        }

        string json = DashboardData.RenderDiagnosticsJson(new DashboardRuntimeInfo(
            RegistryDbPath: _registryDb,
            TelemetryDbPath: _telemetryDb,
            ToolsRoot: Path.Combine(_dir, ".tools"),
            WebRoot: Path.Combine(_dir, "wwwroot"),
            Url: "http://127.0.0.1:4977",
            PreferredWorkspaceRoot: "/repo/json",
            ProcessId: 1234,
            Version: "test-version",
            ExecutablePath: "/tmp/Miller.Dashboard",
            StdoutLogPath: Path.Combine(_dir, "dashboard.out.log"),
            StderrLogPath: Path.Combine(_dir, "dashboard.err.log")));

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal(_registryDb, root.GetProperty("registry_db_path").GetString());
        Assert.Equal(_telemetryDb, root.GetProperty("telemetry_db_path").GetString());
        Assert.Equal("test-version", root.GetProperty("version").GetString());
        Assert.True(root.GetProperty("telemetry_error_details_available").GetBoolean());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public void RenderSnapshotJson_UsesStableSnakeCaseContract()
    {
        using JulieDbFixture fixture = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows:
            [
                new JulieDbFixture.SymbolRow(
                    "s1",
                    "Runner",
                    "class",
                    "csharp",
                    "src/Runner.cs",
                    "class Runner",
                    1,
                    null),
            ],
            fileContent: new Dictionary<string, string>
            {
                ["src/Runner.cs"] = "class Runner {}\n",
            });
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-json",
                "json-abc12345",
                fixture.WorkspaceRoot,
                fixture.DbPath,
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-05-31T12:00:00Z"));
            registry.MarkScanned("ws-json", 9, DateTimeOffset.Parse("2026-05-31T12:01:00Z"));
        }
        InsertTelemetryRow(
            "ws-json",
            "context",
            "ok",
            "2026-05-31T12:02:00.000Z",
            durationMs: 8,
            estTokens: 30,
            bytesReturned: 120,
            sourceBytes: 1_200);

        string json = DashboardData.RenderSnapshotJson(_registryDb, _telemetryDb, "ws-json");

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("ws-json", root.GetProperty("selected_workspace_id").GetString());
        Assert.True(root.TryGetProperty("workspaces", out _));
        JsonElement selectedFacts = root.GetProperty("selected_workspace_facts");
        Assert.Equal("ws-json", selectedFacts.GetProperty("workspace_id").GetString());
        Assert.Equal(1, selectedFacts.GetProperty("file_count").GetInt64());
        Assert.Equal(1, selectedFacts.GetProperty("symbol_count").GetInt64());
        Assert.Equal("csharp", selectedFacts.GetProperty("languages")[0].GetProperty("language").GetString());
        Assert.Equal("class", selectedFacts.GetProperty("symbol_kinds")[0].GetProperty("kind").GetString());
        JsonElement savings = root.GetProperty("context_savings");
        Assert.Equal("tracked", savings.GetProperty("status").GetString());
        Assert.Equal(1_200, savings.GetProperty("source_bytes").GetInt64());
        Assert.Equal(1_080, savings.GetProperty("saved_bytes").GetInt64());
    }

    [Fact]
    public void MissingDashboardDatabases_RenderAsEmptyReadOnlyViews()
    {
        Assert.Empty(DashboardData.ReadWorkspaces(_registryDb));
        Assert.Equal(0, DashboardData.ReadTelemetrySummary(_telemetryDb, "missing").TotalCalls);
    }

    [Fact]
    public void ReadIndex_JoinsNewestTelemetryTimestampPerWorkspace()
    {
        string busyRoot = Path.Combine(_dir, "root-busy");
        string quietRoot = Path.Combine(_dir, "root-quiet");
        Directory.CreateDirectory(busyRoot);
        Directory.CreateDirectory(quietRoot);

        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-busy", "busy-abcd1234", busyRoot, Path.Combine(busyRoot, ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready, DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
            registry.UpsertSeen(
                "ws-quiet", "quiet-abcd1234", quietRoot, Path.Combine(quietRoot, ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready, DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
        }

        InsertTelemetryRow("ws-busy", "search", "ok", "2026-06-12T10:00:00.000Z", durationMs: 12);
        InsertTelemetryRow("ws-busy", "inspect", "ok", "2026-06-12T12:30:00.000Z", durationMs: 8);
        InsertTelemetryRow("ws-busy", "trace", "ok", "2026-06-12T09:00:00.000Z", durationMs: 5);

        DashboardWorkspaceIndex index = DashboardData.ReadIndex(_registryDb, _telemetryDb);

        DashboardWorkspaceIndexEntry busy = Assert.Single(index.Entries, e => e.Workspace.WorkspaceId == "ws-busy");
        Assert.Equal("2026-06-12T12:30:00.000Z", busy.LastActivityTs);

        DashboardWorkspaceIndexEntry quiet = Assert.Single(index.Entries, e => e.Workspace.WorkspaceId == "ws-quiet");
        Assert.Null(quiet.LastActivityTs);
    }

    [Fact]
    public void ReadIndex_MissingTelemetryDbDegradesToNullLastActivity()
    {
        string root = Path.Combine(_dir, "root-b");
        Directory.CreateDirectory(root);
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-a", "alpha-abcd1234", root, Path.Combine(root, ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready, DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
        }

        DashboardWorkspaceIndex index = DashboardData.ReadIndex(
            _registryDb,
            Path.Combine(_dir, "no-such-telemetry.db"));

        DashboardWorkspaceIndexEntry entry = Assert.Single(index.Entries);
        Assert.Null(entry.LastActivityTs);
        Assert.Equal(1, index.WorkspaceCount);
    }

    [Fact]
    public void ReadIndex_CorruptTelemetryDbDegradesToNullLastActivity()
    {
        string root = Path.Combine(_dir, "root-c");
        Directory.CreateDirectory(root);
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-a", "alpha-abcd1234", root, Path.Combine(root, ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready, DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
        }

        File.WriteAllText(_telemetryDb, "not a sqlite database");

        DashboardWorkspaceIndex index = DashboardData.ReadIndex(_registryDb, _telemetryDb);

        DashboardWorkspaceIndexEntry entry = Assert.Single(index.Entries);
        Assert.Null(entry.LastActivityTs);
        Assert.True(string.IsNullOrEmpty(index.Error));
    }

    [Fact]
    public void ReadIndex_TelemetryDbWithoutToolTelemetryTableDegradesToNullLastActivity()
    {
        string root = Path.Combine(_dir, "root-d");
        Directory.CreateDirectory(root);
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-a", "alpha-abcd1234", root, Path.Combine(root, ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready, DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
        }

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _telemetryDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var ddl = connection.CreateCommand();
            ddl.CommandText = "CREATE TABLE unrelated (id TEXT PRIMARY KEY) STRICT;";
            ddl.ExecuteNonQuery();
        }

        DashboardWorkspaceIndex index = DashboardData.ReadIndex(_registryDb, _telemetryDb);

        Assert.Null(Assert.Single(index.Entries).LastActivityTs);
    }

    [Fact]
    public void ReadIndex_CorruptRegistryDbReturnsEmptyIndexWithError()
    {
        File.WriteAllText(_registryDb, "not a sqlite database");

        DashboardWorkspaceIndex index = DashboardData.ReadIndex(_registryDb);

        Assert.Empty(index.Entries);
        Assert.Equal(0, index.WorkspaceCount);
        Assert.False(string.IsNullOrWhiteSpace(index.Error));
    }

    [Fact]
    public void ReadWorkspaces_CorruptRegistryDbDegradesToEmptyNotCrash()
    {
        File.WriteAllText(_registryDb, "not a sqlite database");

        IReadOnlyList<DashboardWorkspaceRow> rows = DashboardData.ReadWorkspaces(_registryDb);

        Assert.Empty(rows);
    }

    [Fact]
    public void ReadSnapshot_CorruptRegistryDbReturnsSnapshotNotCrash()
    {
        File.WriteAllText(_registryDb, "not a sqlite database");

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(_registryDb, _telemetryDb, workspaceId: null);

        Assert.Empty(snapshot.Workspaces);
        Assert.Empty(snapshot.WorkspaceFacts);
        Assert.Equal("not_tracked", snapshot.ContextSavings.Status);
    }

    [Fact]
    public void ReadTelemetrySummary_CorruptTelemetryDbDegradesToEmpty()
    {
        File.WriteAllText(_telemetryDb, "not a sqlite database");

        DashboardTelemetrySummary summary = DashboardData.ReadTelemetrySummary(_telemetryDb, "ws-a");

        Assert.Equal("ws-a", summary.WorkspaceId);
        Assert.Equal(0, summary.TotalCalls);
        Assert.Empty(summary.Tools);
        Assert.Empty(summary.RecentErrors);
        // A corrupt DB must be distinguishable from a healthy empty one: the caught message rides Error.
        Assert.False(string.IsNullOrWhiteSpace(summary.Error));
        Assert.StartsWith("telemetry read degraded:", summary.Error);
    }

    [Fact]
    public void ReadRecentActivity_CorruptTelemetryDbDegradesToEmpty()
    {
        File.WriteAllText(_telemetryDb, "not a sqlite database");

        DashboardActivityFeed feed = DashboardData.ReadRecentActivity(_telemetryDb, _registryDb, workspaceId: "ws-a");

        Assert.Equal("ws-a", feed.WorkspaceId);
        Assert.Empty(feed.Entries);
        // A corrupt DB must be distinguishable from a healthy empty feed: the caught message rides Error.
        Assert.False(string.IsNullOrWhiteSpace(feed.Error));
        Assert.StartsWith("telemetry read degraded:", feed.Error);
    }

    [Fact]
    public void ReadSnapshot_CorruptTelemetryDbDegradesTelemetryPanelsNotCrash()
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
        }
        File.WriteAllText(_telemetryDb, "not a sqlite database");

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(_registryDb, _telemetryDb, workspaceId: null);

        // SelectTelemetryWorkspace degrades to null on a corrupt telemetry DB → falls back to workspaces[0].
        Assert.Equal("ws-a", snapshot.SelectedWorkspaceId);
        Assert.Equal(0, snapshot.Telemetry.TotalCalls);
        Assert.Empty(snapshot.Telemetry.Tools);
        Assert.Equal("not_tracked", snapshot.ContextSavings.Status);
    }

    [Fact]
    public void TryRefreshWorkspace_UnregisteredIdReturnsFailedJsonNotThrow()
    {
        // Empty-but-valid registry: an unregistered id must degrade to a Failed result, never throw a 500.
        using (WorkspaceRegistry.Open(_registryDb))
        {
        }
        string toolsRoot = Path.Combine(_dir, ".tools");

        WorkspaceRefreshResult result = DashboardData.TryRefreshWorkspace(_registryDb, toolsRoot, "does-not-exist");

        Assert.Equal(WorkspaceRefreshStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));

        using var doc = JsonDocument.Parse(DashboardData.RenderRefreshJson(result));
        Assert.Equal("does-not-exist", doc.RootElement.GetProperty("WorkspaceId").GetString());
        Assert.Equal("failed", doc.RootElement.GetProperty("StatusText").GetString());
    }

    [Fact]
    public void JsonRefreshEndpoint_UsesNonThrowingRefreshPath()
    {
        // A4 guard: the JSON refresh route must ride TryRefreshWorkspace (like the htmx /fragments/refresh route),
        // not the throwing RefreshWorkspace, so an unregistered id renders a failed body instead of a 500.
        string endpoints = File.ReadAllText(Path.Combine(
            Miller.Tests.ScaleTestSupport.RepoRoot(),
            "src",
            "Miller.Dashboard",
            "Endpoints",
            "DashboardEndpoints.cs"));

        int route = endpoints.IndexOf("/workspaces/{workspace_id}/refresh", StringComparison.Ordinal);
        Assert.True(route >= 0, "JSON refresh route not found");
        // Window spans this route's handler only: the sole other TryRefreshWorkspace call precedes the
        // route, so scanning forward cannot pass on a different route's use of it.
        string block = endpoints[route..Math.Min(endpoints.Length, route + 700)];
        Assert.Contains("TryRefreshWorkspace", block, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderSnapshotJson_PreferredRootMatchesWorkspacePageSelection()
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
        // Telemetry favors ws-a; the preferred root must still win, matching the /workspace page.
        InsertTelemetryRow("ws-a", "search", "ok", "2026-05-31T10:02:00.000Z", durationMs: 5);

        string json = DashboardData.RenderSnapshotJson(
            _registryDb,
            _telemetryDb,
            workspaceId: null,
            preferredWorkspaceRoot: "/repo/b");

        using var doc = JsonDocument.Parse(json);
        string? jsonSelected = doc.RootElement.GetProperty("selected_workspace_id").GetString();

        string? pageSelected = DashboardData
            .ReadSnapshot(_registryDb, _telemetryDb, workspaceId: null, preferredWorkspaceRoot: "/repo/b")
            .SelectedWorkspaceId;

        Assert.Equal("ws-b", jsonSelected);
        Assert.Equal(pageSelected, jsonSelected);
    }

    private static async Task<string> RenderComponentAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The remove/prune forms embed <AntiforgeryToken/>; outside a real HTTP request the provider is this
        // fixed-token stub so the hidden input still renders (the value is never validated here).
        services.AddSingleton<Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider>(
            new FixedAntiforgeryStateProvider());
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

    private sealed class FixedAntiforgeryStateProvider :
        Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider
    {
        public override Microsoft.AspNetCore.Components.Forms.AntiforgeryRequestToken? GetAntiforgeryToken() =>
            new("render-test-token", "__RequestVerificationToken");
    }

    private static void Exec(string dbPath, string sql)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        using var connection = new SqliteConnection(csb.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string InsertTelemetryRow(
        string workspaceId,
        string tool,
        string outcome,
        string ts,
        long durationMs,
        string? errorKind = null,
        string? op = null,
        long? estTokens = null,
        long bytesReturned = 0,
        long sourceBytes = 0,
        string? errorMessage = null,
        string? errorDetail = null,
        string? id = null)
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
        string rowId = id ?? Guid.CreateVersion7().ToString();
        cmd.CommandText = """
            INSERT INTO tool_telemetry
                (id, ts, tool, op, workspace_id, workspace_root, duration_ms, outcome, error_kind,
                 error_message, error_detail, bytes_returned, source_bytes, est_tokens)
            VALUES
                ($id, $ts, $tool, $op, $ws, $root, $duration, $outcome, $error,
                 $message, $detail, $bytesReturned, $sourceBytes, $tokens);
            """;
        cmd.Parameters.AddWithValue("$id", rowId);
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$op", (object?)op ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$root", "/repo/test");
        cmd.Parameters.AddWithValue("$duration", durationMs);
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$error", (object?)errorKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$message", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$detail", (object?)errorDetail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bytesReturned", bytesReturned);
        cmd.Parameters.AddWithValue("$sourceBytes", sourceBytes);
        cmd.Parameters.AddWithValue("$tokens", (object?)estTokens ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return rowId;
    }

    // ---- metric trends (history.db sidecar) ----

    private DashboardWorkspaceRow WorkspaceRowWithMiller(out string historyDbPath)
    {
        string millerDir = Path.Combine(_dir, "repo", ".miller");
        Directory.CreateDirectory(millerDir);
        string indexDbPath = Path.Combine(millerDir, "symbols.db");
        historyDbPath = MetricSnapshotAggregates.HistoryDbPathFor(indexDbPath);
        return new DashboardWorkspaceRow(
            WorkspaceId: "ws-a",
            DisplayId: "alpha-abcd1234",
            CanonicalRoot: Path.Combine(_dir, "repo"),
            IndexDbPath: indexDbPath,
            LastSeenAt: "2026-07-07T10:00:00Z",
            LastScanAt: "2026-07-07T10:01:00Z",
            LastRevision: 3,
            State: "ready",
            LastError: null);
    }

    private static void RecordSnapshot(
        string historyDbPath, long revision, params (string Metric, double Value)[] metrics) =>
        RecordSnapshotAt(historyDbPath, revision, null, metrics);

    private static void RecordSnapshotAt(
        string historyDbPath, long revision, DateTime? recordedAtUtc, params (string Metric, double Value)[] metrics)
    {
        var snapshot = new MetricHistorySnapshot(
            WorkspaceId: "ws-a",
            ArtifactId: "art-1",
            Revision: revision,
            ExtractorVersion: "2.11.0",
            MillerVersion: "0.9.9",
            Source: "converge",
            Metrics: metrics.Select(m => new MetricHistoryPoint(m.Metric, m.Value, null)).ToList());
        MetricHistoryWriteResult result = MetricHistoryStore.RecordConverge(historyDbPath, snapshot, recordedAtUtc);
        Assert.Equal(MetricHistoryWriteResult.Recorded, result);
    }

    [Fact]
    public void ReadTrends_BuildsSparklineSeriesForAvailableMetricsInCanonicalOrder()
    {
        DashboardWorkspaceRow workspace = WorkspaceRowWithMiller(out string historyDbPath);
        // symbol_count + complexity_p90 get >=2 points (sparkline); marker_total gets 1 point (<2 => hint);
        // clone_group_count + dead_code_candidate_count stay absent (no row).
        RecordSnapshot(historyDbPath, 1, ("symbol_count", 100), ("complexity_p90", 8));
        RecordSnapshot(historyDbPath, 2, ("symbol_count", 110), ("complexity_p90", 9));
        RecordSnapshot(historyDbPath, 3, ("symbol_count", 120), ("complexity_p90", 9), ("marker_total", 4));

        DashboardWorkspaceTrendsPanel panel = DashboardIndexFactsReader.ReadTrends(workspace);

        Assert.True(panel.HasData);
        Assert.Equal("ws-a", panel.WorkspaceId);
        // Canonical order: symbol_count, complexity_p90, marker_total (clone/dead-code absent).
        Assert.Equal(
            new[] { "symbol_count", "complexity_p90", "marker_total" },
            panel.Series.Select(s => s.Metric).ToArray());

        DashboardTrendSeries symbols = panel.Series[0];
        Assert.True(symbols.HasTrend);
        Assert.Equal(new[] { 100d, 110d, 120d }, symbols.Points.ToArray());
        Assert.Equal(100d, symbols.First);
        Assert.Equal(120d, symbols.Latest);

        DashboardTrendSeries markers = panel.Series[2];
        Assert.False(markers.HasTrend); // single point => empty-state hint row, still present
        Assert.Single(markers.Points);
    }

    [Fact]
    public void ReadTrends_CarriesRecordedWindowBoundsForEachSeries()
    {
        DashboardWorkspaceRow workspace = WorkspaceRowWithMiller(out string historyDbPath);
        RecordSnapshotAt(historyDbPath, 1, new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc), ("symbol_count", 100));
        RecordSnapshotAt(historyDbPath, 2, new DateTime(2026, 7, 1, 12, 30, 0, DateTimeKind.Utc), ("symbol_count", 110));
        RecordSnapshotAt(historyDbPath, 3, new DateTime(2026, 7, 16, 16, 0, 0, DateTimeKind.Utc), ("symbol_count", 120));

        DashboardWorkspaceTrendsPanel panel = DashboardIndexFactsReader.ReadTrends(workspace);

        DashboardTrendSeries symbols = Assert.Single(panel.Series);
        Assert.Equal("2026-06-12T10:00:00.0000000Z", symbols.FirstRecordedAtUtc);
        Assert.Equal("2026-07-16T16:00:00.0000000Z", symbols.LatestRecordedAtUtc);
    }

    [Fact]
    public void ReadTrends_BoundsFollowSnapshotOrderNotRecordedAtOrder()
    {
        DashboardWorkspaceRow workspace = WorkspaceRowWithMiller(out string historyDbPath);
        RecordSnapshotAt(historyDbPath, 1, new DateTime(2026, 7, 16, 16, 0, 0, DateTimeKind.Utc), ("symbol_count", 100));
        RecordSnapshotAt(historyDbPath, 2, new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc), ("symbol_count", 120));

        DashboardWorkspaceTrendsPanel panel = DashboardIndexFactsReader.ReadTrends(workspace);

        DashboardTrendSeries symbols = Assert.Single(panel.Series);
        Assert.Equal("2026-07-16T16:00:00.0000000Z", symbols.FirstRecordedAtUtc);
        Assert.Equal("2026-06-12T10:00:00.0000000Z", symbols.LatestRecordedAtUtc);
    }

    [Fact]
    public void ReadTrends_BoundsMatchPlottedEndpointsWhenDownsampled()
    {
        DashboardWorkspaceRow workspace = WorkspaceRowWithMiller(out string historyDbPath);
        var origin = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 51; i++)
            RecordSnapshotAt(historyDbPath, i + 1, origin.AddHours(i), ("symbol_count", 100 + i));

        DashboardWorkspaceTrendsPanel panel = DashboardIndexFactsReader.ReadTrends(workspace);

        DashboardTrendSeries symbols = Assert.Single(panel.Series);
        Assert.Equal(50, symbols.Points.Count);
        Assert.Equal(100d, symbols.First);
        Assert.Equal(150d, symbols.Latest);
        Assert.Equal("2026-05-01T00:00:00.0000000Z", symbols.FirstRecordedAtUtc);
        Assert.Equal("2026-05-03T02:00:00.0000000Z", symbols.LatestRecordedAtUtc);
    }

    [Fact]
    public void ReadTrends_MissingHistoryDb_ReturnsEmptyPanelWithoutError()
    {
        DashboardWorkspaceRow workspace = WorkspaceRowWithMiller(out _);
        // No history.db written.

        DashboardWorkspaceTrendsPanel panel = DashboardIndexFactsReader.ReadTrends(workspace);

        Assert.False(panel.HasData);
        Assert.False(panel.Unreadable); // absent ⟹ fresh-workspace empty state, not the error state.
        Assert.Empty(panel.Series);
        Assert.Equal("ws-a", panel.WorkspaceId);
    }

    [Fact]
    public void ReadTrends_PresentButUnreadableHistoryDb_ReturnsErrorFlaggedEmptyPanel()
    {
        DashboardWorkspaceRow workspace = WorkspaceRowWithMiller(out string historyDbPath);
        File.WriteAllText(historyDbPath, "this is not a sqlite database, just garbage");

        DashboardWorkspaceTrendsPanel panel = DashboardIndexFactsReader.ReadTrends(workspace);

        // Downgraded to an empty panel BUT flagged so the panel renders "history unreadable", not "no trend data yet".
        Assert.False(panel.HasData);
        Assert.True(panel.Unreadable);
        Assert.Empty(panel.Series);
        Assert.Equal("ws-a", panel.WorkspaceId);
    }

    [Fact]
    public void ReadSnapshot_PopulatesTrendsForSelectedWorkspace()
    {
        string millerDir = Path.Combine(_dir, "repo", ".miller");
        Directory.CreateDirectory(millerDir);
        string indexDbPath = Path.Combine(millerDir, "symbols.db");
        string historyDbPath = MetricSnapshotAggregates.HistoryDbPathFor(indexDbPath);
        RecordSnapshot(historyDbPath, 1, ("symbol_count", 50));
        RecordSnapshot(historyDbPath, 2, ("symbol_count", 60));

        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-a",
                "alpha-abcd1234",
                Path.Combine(_dir, "repo"),
                indexDbPath,
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-07-07T10:00:00Z"));
        }

        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(_registryDb, _telemetryDb, "ws-a");

        Assert.NotNull(snapshot.Trends);
        Assert.True(snapshot.Trends!.HasData);
        DashboardTrendSeries series = Assert.Single(snapshot.Trends.Series);
        Assert.Equal("symbol_count", series.Metric);
        Assert.Equal(new[] { 50d, 60d }, series.Points.ToArray());
    }

    [Fact]
    public void Sparkline_Points_EmptyForFewerThanTwoValues()
    {
        Assert.Equal(string.Empty, DashboardSparkline.Points(Array.Empty<double>()));
        Assert.Equal(string.Empty, DashboardSparkline.Points(new[] { 5d }));
    }

    [Fact]
    public void Sparkline_Points_SpansWidthAndInvertsMaxToTop()
    {
        string points = DashboardSparkline.Points(new[] { 0d, 10d });
        string[] pairs = points.Split(' ');
        Assert.Equal(2, pairs.Length);

        // First x == 0, last x == full width; higher value (10) => smaller y than lower value (0).
        (double x0, double y0) = ParsePair(pairs[0]);
        (double x1, double y1) = ParsePair(pairs[1]);
        Assert.Equal(0d, x0);
        Assert.Equal(DashboardSparkline.ViewWidth, x1);
        Assert.True(y1 < y0);
    }

    [Fact]
    public void Sparkline_Points_FlatSeriesDrawsCenteredLine()
    {
        string points = DashboardSparkline.Points(new[] { 7d, 7d, 7d });
        string[] pairs = points.Split(' ');
        double mid = DashboardSparkline.ViewHeight / 2d;
        foreach (string pair in pairs)
        {
            (double _, double y) = ParsePair(pair);
            Assert.Equal(mid, y, precision: 2);
        }
    }

    private static (double X, double Y) ParsePair(string pair)
    {
        string[] parts = pair.Split(',');
        return (
            double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }
}
