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
