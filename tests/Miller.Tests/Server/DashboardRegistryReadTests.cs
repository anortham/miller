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
        Assert.Equal(2, snapshot.ContextSavings.Tools.Count);
        DashboardContextSavingsTool contextTool =
            Assert.Single(snapshot.ContextSavings.Tools, tool => tool.Tool == "context");
        Assert.Equal(10_000, contextTool.SourceBytes);
        Assert.Equal(8_000, contextTool.SavedBytes);
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
                        "2026-05-31T10:01:00Z",
                        "search",
                        "auto",
                        "InvalidOperationException",
                        DurationMs: 24)
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
                ]));

        string html = await RenderComponentAsync<WorkspaceShell>(new Dictionary<string, object?>
        {
            ["Snapshot"] = snapshot,
        });

        Assert.Contains("snapshot.json?workspace_id=ws-a", html);
        Assert.Contains("Index transparency", html);
        Assert.Contains("4 files", html);
        Assert.Contains("3 symbols", html);
        Assert.Contains("2 languages", html);
        Assert.Contains("search.db missing", html);
        Assert.Contains("csharp", html);
        Assert.Contains("markdown", html);
        Assert.Contains("class", html);
        Assert.Contains("Context saved", html);
        Assert.Contains("14.5 KB", html);
        Assert.Contains("850 tokens", html);
        Assert.Contains("context", html);
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
                        SymbolKinds: [])),
                new DashboardWorkspaceIndexEntry(
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
                    DashboardIndexFactsReader.Read(new DashboardWorkspaceRow(
                        "ws-b",
                        "beta-efgh5678",
                        "/repo/b",
                        "/repo/b/.miller/symbols.db",
                        "2026-05-31T10:00:00Z",
                        null,
                        null,
                        "missing",
                        "missing index"))),
            ],
            WorkspaceCount: 2,
            TotalFiles: 4,
            TotalSymbols: 3,
            LanguageCount: 2);

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
        Assert.DoesNotContain("Index.Entries", html);
    }

    [Fact]
    public void DashboardHost_PreservesFragmentCompatibilityRoutes()
    {
        string program = File.ReadAllText(Path.Combine(
            Miller.Tests.ScaleTestSupport.RepoRoot(),
            "src",
            "Miller.Dashboard",
            "Program.cs"));

        Assert.Contains("MapGet(\"/fragments/dashboard\"", program, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/fragments/workspaces\"", program, StringComparison.Ordinal);
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
        long? estTokens = null,
        long bytesReturned = 0,
        long sourceBytes = 0)
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
                (id, ts, tool, op, workspace_id, workspace_root, duration_ms, outcome, error_kind,
                 bytes_returned, source_bytes, est_tokens)
            VALUES
                ($id, $ts, $tool, $op, $ws, $root, $duration, $outcome, $error,
                 $bytesReturned, $sourceBytes, $tokens);
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
        cmd.Parameters.AddWithValue("$bytesReturned", bytesReturned);
        cmd.Parameters.AddWithValue("$sourceBytes", sourceBytes);
        cmd.Parameters.AddWithValue("$tokens", (object?)estTokens ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
