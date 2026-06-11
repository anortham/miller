using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the PURE <c>workspace</c> renderers (M7 decision-2/6): given already-assembled fact records they produce
/// compact text or JSON, deterministically and with no I/O (the tool does the SQLite/subprocess work and hands
/// the facts in). Covers the status view (workspace identity + index facts + the embedded telemetry breakdown),
/// the list view (the current single workspace, honestly labelled), and the action/open/remove result lines —
/// both formats, including the honesty cases (a non-leader <c>full</c> note, a remove refusal).
/// </summary>
public sealed class WorkspaceRenderTests
{
    private static WorkspaceFacts Facts() => new(
        Root: "/repo",
        WorkspaceId: "ws-123",
        DbPath: "/repo/.miller/symbols.db",
        IsLeader: true,
        DocumentCount: 565,
        KnownExtensionsCount: 7,
        BuiltRevision: 42,
        LatestObservedRevision: 42,
        IndexFresh: true,
        QueueEmpty: true);

    private static readonly TelemetrySummary Telemetry = new(
        new[]
        {
            new ToolStat("search", 10, 120.0, 250, 400, 1, 5000),
            new ToolStat("inspect", 1, 900.0, 900, 900, 0, 2000),
        },
        TotalCalls: 11, WindowStartTs: "2026-05-01T00:00:00.000Z", WindowEndTs: "2026-05-01T01:00:00.000Z",
        DroppedWrites: 0);

    private static WorkspaceExtractionHealthFacts ExtractionHealth() => new(
        ParseDiagnostics: HealthFactSection<ParseDiagnosticGroup>.FromRows(new[]
        {
            new ParseDiagnosticGroup("csharp", "parse_error", 2),
        }),
        CapabilityGaps: HealthFactSection<CapabilityGapGroup>.FromRows(new[]
        {
            new CapabilityGapGroup("typescript", "relationships", "open", 1),
        }),
        LanguageCapabilities: HealthFactSection<LanguageCapabilitySummary>.FromRows(new[]
        {
            new LanguageCapabilitySummary("csharp", 8, 7, 3, 2, 1, 1, 6, 5, 2, 1,
                KindCoverage:
                [
                    new KindCoverageDomain("doc_comments", Supported: ["method"], OpenGaps: ["property"], NotApplicable: []),
                ]),
        }),
        StructuralFacts: HealthFactSection<StructuralFactGroup>.FromRows(new[]
        {
            new StructuralFactGroup("typescript", "typescript.await_expression.v1", "await_expression", 2),
        }),
        ComplexityMetrics: HealthFactSection<ComplexityMetricGroup>.FromRows(new[]
        {
            new ComplexityMetricGroup("typescript", "symbol", "julie-ast-complexity-v1", 1, 3, 1, 2, 4),
        }),
        Files: HealthFactSection<FileStatusGroup>.FromRows(new[]
        {
            new FileStatusGroup("csharp", "indexed", 1),
        }));

    // ---- status ----

    [Fact]
    public void Status_Compact_ShowsWorkspaceIndexAndTelemetryFacts()
    {
        const string fullId = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
        string text = WorkspaceRender.Status(
            Facts() with { Root = "/repo/miller", WorkspaceId = fullId, ServerProcessId = 12345 },
            Telemetry,
            json: false);

        Assert.Contains("miller-abcdef123456", text);
        Assert.Contains("pid 12345", text);
        Assert.DoesNotContain(fullId, text);
        Assert.DoesNotContain("db:", text);
        Assert.Contains("565", text);            // document count
        Assert.Contains("42", text);             // built revision
        Assert.Contains("leader", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("telemetry:", text);      // concise telemetry summary
        Assert.Contains("search", text);         // top tool by p95
        Assert.Contains("250", text);            // a telemetry metric (p95)
    }

    [Fact]
    public void Status_Json_HasWorkspaceIndexAndTelemetrySections()
    {
        using var doc = JsonDocument.Parse(
            WorkspaceRender.Status(Facts() with { ServerProcessId = 12345 }, Telemetry, json: true));
        var root = doc.RootElement;

        var ws = root.GetProperty("workspace");
        Assert.Equal("/repo", ws.GetProperty("root").GetString());
        Assert.Equal("ws-123", ws.GetProperty("workspace_id").GetString());
        Assert.True(ws.GetProperty("leader").GetBoolean());
        Assert.Equal(12345, ws.GetProperty("server_pid").GetInt32());

        var idx = root.GetProperty("index");
        Assert.Equal(565, idx.GetProperty("document_count").GetInt64());
        Assert.Equal(7, idx.GetProperty("known_extensions").GetInt64());
        Assert.Equal(42, idx.GetProperty("built_revision").GetInt64());
        Assert.Equal(42, idx.GetProperty("latest_revision").GetInt64());
        Assert.True(idx.GetProperty("index_fresh").GetBoolean());
        Assert.True(idx.GetProperty("queue_empty").GetBoolean());

        // The telemetry breakdown is embedded as a nested object (the same shape TelemetryRender.Json emits).
        Assert.Equal(11, root.GetProperty("telemetry").GetProperty("total_calls").GetInt64());
    }

    [Fact]
    public void Status_Json_NullWorkspaceIdAndUnknownFreshness_AreJsonNull()
    {
        var facts = Facts() with { WorkspaceId = null, IndexFresh = null };

        using var doc = JsonDocument.Parse(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("workspace").GetProperty("workspace_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("index").GetProperty("index_fresh").ValueKind);
    }

    [Fact]
    public void Status_Compact_StaleIndex_IsCalledOut()
    {
        var facts = Facts() with { BuiltRevision = 40, LatestObservedRevision = 42, IndexFresh = false };

        string text = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false);
        Assert.Contains("stale", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_Compact_ShowsSearchSidecarState()
    {
        var facts = Facts() with
        {
            SearchSidecar = new SearchSidecarFacts(
                "stale", "/repo/.miller/search.db", Revision: 41, ExpectedRevision: 42, DocumentCount: 565, Error: null),
        };

        string text = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false);

        Assert.Contains("search_db:", text);
        Assert.Contains("stale", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expected 42", text);
    }

    [Fact]
    public void Status_Compact_StaleSearchSidecar_ReportsRevisionDirectionHonestly()
    {
        var facts = Facts() with
        {
            SearchSidecar = new SearchSidecarFacts(
                "stale", "/repo/.miller/search.db", Revision: 44, ExpectedRevision: 42, DocumentCount: 565, Error: null),
        };

        string text = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false);

        Assert.Contains("built 44 > expected 42", text);
        Assert.DoesNotContain("built 44 < expected 42", text);
    }

    [Fact]
    public void Status_Compact_ShowsContentCorpusState()
    {
        var facts = Facts() with
        {
            ContentCorpus = new ContentCorpusFacts(
                "stale", "/repo/.miller/content.db", SchemaVersion: 1, WorkspaceRevision: 41,
                SourceCount: 12, ChunkCount: 48, IndexedSourceBytes: 1200, StoredRawBytes: 1600),
        };

        string text = WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: false);

        Assert.Contains("content_db:", text);
        Assert.Contains("stale", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rev 41", text);
        Assert.Contains("chunks 48", text);
    }

    [Fact]
    public void Status_Json_ShowsSearchSidecarObject()
    {
        var facts = Facts() with
        {
            SearchSidecar = new SearchSidecarFacts(
                "current", "/repo/.miller/search.db", Revision: 42, ExpectedRevision: 42, DocumentCount: 565, Error: null),
        };

        using var doc = JsonDocument.Parse(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        JsonElement sidecar = doc.RootElement.GetProperty("index").GetProperty("search_sidecar");

        Assert.Equal("current", sidecar.GetProperty("state").GetString());
        Assert.Equal(42, sidecar.GetProperty("revision").GetInt64());
        Assert.Equal(42, sidecar.GetProperty("expected_revision").GetInt64());
        Assert.Equal(565, sidecar.GetProperty("document_count").GetInt32());
    }

    [Fact]
    public void Status_Json_ShowsContentCorpusObject()
    {
        var facts = Facts() with
        {
            ContentCorpus = new ContentCorpusFacts(
                "current", "/repo/.miller/content.db", SchemaVersion: 1, WorkspaceRevision: 42,
                SourceCount: 12, ChunkCount: 48, IndexedSourceBytes: 1200, StoredRawBytes: 1600,
                NonUtf8Skipped: 2),
        };

        using var doc = JsonDocument.Parse(WorkspaceRender.Status(facts, TelemetrySummary.Empty, json: true));
        JsonElement corpus = doc.RootElement.GetProperty("index").GetProperty("content_corpus");

        Assert.Equal("current", corpus.GetProperty("state").GetString());
        Assert.Equal(42, corpus.GetProperty("workspace_revision").GetInt64());
        Assert.Equal(12, corpus.GetProperty("source_count").GetInt32());
        Assert.Equal(48, corpus.GetProperty("chunk_count").GetInt32());
        Assert.Equal(2, corpus.GetProperty("non_utf8_skipped").GetInt32());
    }

    // ---- health ----

    [Fact]
    public void Health_Compact_LeadsWithVerdictAndShortWarnings()
    {
        var health = new WorkspaceHealthFacts(
            StatusFacts: Facts() with
            {
                SearchSidecar = new SearchSidecarFacts(
                    "current", "/repo/.miller/search.db", Revision: 42, ExpectedRevision: 42,
                    DocumentCount: 565, Error: null),
                ContentCorpus = new ContentCorpusFacts(
                    "current", "/repo/.miller/content.db", SchemaVersion: 1, WorkspaceRevision: 42,
                    SourceCount: 12, ChunkCount: 48, IndexedSourceBytes: 1200, StoredRawBytes: 1600),
            },
            Telemetry: TelemetrySummary.Empty,
            TelemetryHealth: new TelemetryHealthFacts(OkCount: 5, EmptyCount: 1, ErrorCount: 2),
            Extraction: ExtractionHealth(),
            Warnings: new[]
            {
                new HealthWarning("parse_diagnostics", "usable_with_warnings", "2 parse diagnostics reported"),
            },
            RecommendedActions: new[] { "inspect parse diagnostics before relying on unsupported language facts" },
            State: HealthState.UsableWithWarnings,
            Summary: "index readable with warnings");

        string text = WorkspaceRender.Health(health, json: false);

        Assert.StartsWith("# workspace health  usable_with_warnings", text, StringComparison.Ordinal);
        Assert.Contains("workspace: ws-123  /repo", text);
        Assert.Contains("index: fresh rev 42", text);
        Assert.Contains("search_db: current rev 42", text);
        Assert.Contains("content_db: current rev 42", text);
        Assert.Contains("quality: 2 parse diagnostics  1 open capability gaps", text);
        Assert.Contains("telemetry: 8 calls  errors=2  empty=1", text);
        Assert.Contains("recommended: inspect parse diagnostics", text);
    }

    [Fact]
    public void Health_Json_RendersStableSections()
    {
        var health = new WorkspaceHealthFacts(
            StatusFacts: Facts(),
            Telemetry: TelemetrySummary.Empty,
            TelemetryHealth: new TelemetryHealthFacts(OkCount: 5, EmptyCount: 1, ErrorCount: 2),
            Extraction: ExtractionHealth(),
            Warnings: new[]
            {
                new HealthWarning("parse_diagnostics", "usable_with_warnings", "2 parse diagnostics reported"),
            },
            RecommendedActions: new[] { "inspect parse diagnostics" },
            State: HealthState.UsableWithWarnings,
            Summary: "index readable with warnings");

        using var doc = JsonDocument.Parse(WorkspaceRender.Health(health, json: true));
        JsonElement root = doc.RootElement;

        Assert.Equal("usable_with_warnings", root.GetProperty("verdict").GetProperty("state").GetString());
        Assert.Equal("ws-123", root.GetProperty("workspace").GetProperty("workspace_id").GetString());
        Assert.Equal(565, root.GetProperty("index").GetProperty("document_count").GetInt64());
        Assert.Equal(2, root.GetProperty("telemetry").GetProperty("outcomes").GetProperty("error_count").GetInt64());
        Assert.Equal("csharp", root.GetProperty("extraction_quality")
            .GetProperty("parse_diagnostics").GetProperty("rows")[0].GetProperty("language").GetString());
        Assert.Equal("relationships", root.GetProperty("extraction_quality")
            .GetProperty("capability_gaps").GetProperty("rows")[0].GetProperty("capability").GetString());
        JsonElement capabilityRow = root.GetProperty("extraction_quality")
            .GetProperty("language_capabilities").GetProperty("rows")[0];
        JsonElement docComments = capabilityRow.GetProperty("kind_coverage").GetProperty("doc_comments");
        Assert.Equal("method", docComments.GetProperty("supported")[0].GetString());
        Assert.Equal("property", docComments.GetProperty("open_gaps")[0].GetString());
        Assert.Equal(0, docComments.GetProperty("not_applicable").GetArrayLength());
        Assert.True(root.GetProperty("extraction_quality").TryGetProperty("structural_facts", out JsonElement structural));
        Assert.True(structural.GetProperty("available").GetBoolean());
        Assert.True(root.GetProperty("extraction_quality").TryGetProperty("complexity_metrics", out JsonElement complexity));
        Assert.True(complexity.GetProperty("available").GetBoolean());
        Assert.Equal("parse_diagnostics", root.GetProperty("warnings")[0].GetProperty("code").GetString());
        Assert.Equal("inspect parse diagnostics", root.GetProperty("recommended_actions")[0].GetString());
    }

    // ---- list ----

    [Fact]
    public void List_Compact_ShowsCurrentWorkspace_HonestlyLabelledSingle()
    {
        string text = WorkspaceRender.List(Facts(), json: false);

        // Single-workspace-per-process: the list is the CURRENT workspace, labelled so it is not mistaken for a
        // multi-entry registry (which is eros/commercial-tier, out of Miller's scope).
        Assert.Contains("/repo", text);
        Assert.Contains("ws-123", text);
        Assert.Contains("current", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void List_Json_IsASingleEntryArrayMarkedCurrent()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.List(Facts(), json: true));
        var root = doc.RootElement;
        var workspaces = root.GetProperty("workspaces");
        Assert.Equal(1, workspaces.GetArrayLength());
        var only = workspaces[0];
        Assert.Equal("/repo", only.GetProperty("root").GetString());
        Assert.True(only.GetProperty("current").GetBoolean());
    }

    [Fact]
    public void List_Compact_RendersRegistryRowsAndMarksOnlyTheCurrentWorkspace()
    {
        var rows = new[]
        {
            new WorkspaceListEntry(
                WorkspaceId: "ws-current",
                DisplayId: "current-111111111111",
                Root: "/repo/current",
                DbPath: "/repo/current/.miller/symbols.db",
                State: "current",
                LastRevision: 12,
                Current: true,
                LastError: null),
            new WorkspaceListEntry(
                WorkspaceId: "ws-other",
                DisplayId: "other-222222222222",
                Root: "/repo/other",
                DbPath: "/repo/other/.miller/symbols.db",
                State: "ready",
                LastRevision: 7,
                Current: false,
                LastError: null),
        };

        string text = WorkspaceRender.List(rows, json: false);

        Assert.Contains("# workspaces (2)", text);
        Assert.Contains("current-111111111111", text);
        Assert.Contains("other-222222222222", text);
        Assert.DoesNotContain("workspace_id:", text);
        Assert.DoesNotContain("/repo/current/.miller/symbols.db", text);
        Assert.DoesNotContain("/repo/other/.miller/symbols.db", text);
        Assert.Contains("[current]", text);
        Assert.Contains("state: ready", text);
    }

    [Fact]
    public void List_Json_RendersStableRegistryRowShape()
    {
        var rows = new[]
        {
            new WorkspaceListEntry(
                WorkspaceId: "ws-current",
                DisplayId: "current-111111111111",
                Root: "/repo/current",
                DbPath: "/repo/current/.miller/symbols.db",
                State: "current",
                LastRevision: 12,
                Current: true,
                LastError: null),
            new WorkspaceListEntry(
                WorkspaceId: "ws-missing",
                DisplayId: "missing-222222222222",
                Root: "/repo/missing",
                DbPath: "/repo/missing/.miller/symbols.db",
                State: "missing",
                LastRevision: null,
                Current: false,
                LastError: "root missing"),
        };

        using var doc = JsonDocument.Parse(WorkspaceRender.List(rows, json: true));
        var workspaces = doc.RootElement.GetProperty("workspaces");

        Assert.Equal(2, workspaces.GetArrayLength());
        Assert.Equal("ws-current", workspaces[0].GetProperty("workspace_id").GetString());
        Assert.Equal("current-111111111111", workspaces[0].GetProperty("display_id").GetString());
        Assert.Equal("/repo/current", workspaces[0].GetProperty("root").GetString());
        Assert.Equal("/repo/current/.miller/symbols.db", workspaces[0].GetProperty("index_db_path").GetString());
        Assert.Equal("current", workspaces[0].GetProperty("state").GetString());
        Assert.Equal(12, workspaces[0].GetProperty("last_revision").GetInt64());
        Assert.True(workspaces[0].GetProperty("current").GetBoolean());
        Assert.Equal(JsonValueKind.Null, workspaces[0].GetProperty("last_error").ValueKind);
        Assert.Equal(JsonValueKind.Null, workspaces[1].GetProperty("last_revision").ValueKind);
        Assert.Equal("root missing", workspaces[1].GetProperty("last_error").GetString());
    }

    // ---- refresh / full action ----

    [Fact]
    public void Action_Compact_LeaderScannedAndSwapped_ReportsBoth()
    {
        var result = new WorkspaceActionResult(
            Operation: "full", Scanned: true, Swapped: true, Revision: 43, Note: null);

        string text = WorkspaceRender.Action(result, json: false);
        Assert.Contains("full", text);
        Assert.Contains("43", text);
        Assert.Contains("scanned", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("swapped", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Action_Compact_NonLeaderFull_CarriesTheHonestNote()
    {
        var result = new WorkspaceActionResult(
            Operation: "full", Scanned: false, Swapped: false, Revision: 42,
            Note: "Not the indexer leader; cannot force a global rescan here. The leader's watcher keeps the index fresh.");

        string text = WorkspaceRender.Action(result, json: false);
        Assert.Contains("leader", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot force", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Action_Json_RoundTripsTheFields()
    {
        var result = new WorkspaceActionResult(
            Operation: "refresh", Scanned: true, Swapped: false, Revision: 42, Note: null);

        using var doc = JsonDocument.Parse(WorkspaceRender.Action(result, json: true));
        var root = doc.RootElement;
        Assert.Equal("refresh", root.GetProperty("operation").GetString());
        Assert.True(root.GetProperty("scanned").GetBoolean());
        Assert.False(root.GetProperty("swapped").GetBoolean());
        Assert.Equal(42, root.GetProperty("revision").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("note").ValueKind);
    }

    [Fact]
    public void Action_Json_CanCarryPostRefreshArtifactFacts()
    {
        var result = new WorkspaceActionResult(
            Operation: "refresh",
            Scanned: true,
            Swapped: false,
            Revision: 42,
            Note: null,
            WorkspaceId: "ws-123",
            Root: "/repo",
            Status: "refreshed",
            IndexFresh: true,
            SearchSidecar: new SearchSidecarFacts(
                "current", "/repo/.miller/search.db", Revision: 42, ExpectedRevision: 42, DocumentCount: 10, Error: null),
            ContentCorpus: new ContentCorpusFacts(
                "current", "/repo/.miller/content.db", SchemaVersion: 1, WorkspaceRevision: 42,
                SourceCount: 2, ChunkCount: 8, IndexedSourceBytes: 1024, StoredRawBytes: 2048));

        using var doc = JsonDocument.Parse(WorkspaceRender.Action(result, json: true));
        JsonElement root = doc.RootElement;

        Assert.True(root.GetProperty("index_fresh").GetBoolean());
        Assert.Equal("current", root.GetProperty("search_sidecar").GetProperty("state").GetString());
        Assert.Equal("/repo/.miller/search.db", root.GetProperty("search_sidecar").GetProperty("path").GetString());
        Assert.Equal("current", root.GetProperty("content_corpus").GetProperty("state").GetString());
        Assert.Equal(8, root.GetProperty("content_corpus").GetProperty("chunk_count").GetInt32());
    }

    // ---- open (prime) ----

    [Fact]
    public void Open_Compact_ReportsPrimedPath_AndThatItIsNotALiveSwitch()
    {
        var result = new WorkspaceOpenResult(
            Path: "/other/repo", DbPath: "/other/repo/.miller/symbols.db",
            SymbolsExtracted: 1234, Revision: 1);

        string text = WorkspaceRender.Open(result, json: false);
        Assert.Contains("/other/repo", text);
        Assert.Contains("1234", text);
        // Honest: priming a path is NOT a live switch (the index/watcher are bound to CWD at bootstrap).
        Assert.Contains("not a live switch", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_Json_RoundTripsTheFields()
    {
        var result = new WorkspaceOpenResult(
            Path: "/other/repo", DbPath: "/other/repo/.miller/symbols.db",
            SymbolsExtracted: 1234, Revision: 7);

        using var doc = JsonDocument.Parse(WorkspaceRender.Open(result, json: true));
        var root = doc.RootElement;
        Assert.Equal("/other/repo", root.GetProperty("path").GetString());
        Assert.Equal(1234, root.GetProperty("symbols_extracted").GetInt64());
        Assert.Equal(7, root.GetProperty("revision").GetInt64());
    }

    // ---- remove ----

    [Fact]
    public void Remove_Compact_Removed_ReportsDeletion()
    {
        var result = WorkspaceRemoveResult.Removed("/other/repo/.miller");
        string text = WorkspaceRender.Remove(result, json: false);
        Assert.Contains("/other/repo/.miller", text);
        Assert.Contains("removed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_Compact_RefusedLive_IsAClearRefusal()
    {
        var result = WorkspaceRemoveResult.RefusedLive("/repo/.miller");
        string text = WorkspaceRender.Remove(result, json: false);
        Assert.Contains("refus", text, StringComparison.OrdinalIgnoreCase); // refuse/refused
        Assert.Contains("in use", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_Compact_NotFound_IsNotAnError()
    {
        var result = WorkspaceRemoveResult.NotFound("/gone/.miller");
        string text = WorkspaceRender.Remove(result, json: false);
        Assert.Contains("/gone/.miller", text);
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_Json_CarriesTheKind()
    {
        using var doc = JsonDocument.Parse(WorkspaceRender.Remove(WorkspaceRemoveResult.RefusedLive("/repo/.miller"), json: true));
        Assert.Equal("refused_live", doc.RootElement.GetProperty("result").GetString());
    }
}
