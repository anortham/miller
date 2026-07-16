using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miller.Dashboard;
using Miller.Dashboard.Components;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Xunit;

namespace Miller.Tests.Server;

public sealed class DashboardActivityFeedTests : IDisposable
{
    private readonly string _dir;
    private readonly string _registryDb;
    private readonly string _telemetryDb;

    public DashboardActivityFeedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-activity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registryDb = Path.Combine(_dir, "workspaces.db");
        _telemetryDb = Path.Combine(_dir, "telemetry.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ReadRecentActivity_ScopesToWorkspaceNewestFirst()
    {
        InsertTelemetryRow("ws-a", "search", "ok", "2026-06-12T10:00:00.000Z",
            durationMs: 12, op: "auto", resultCount: 6, estTokens: 120);
        InsertTelemetryRow("ws-a", "inspect", "error", "2026-06-12T10:01:00.000Z",
            durationMs: 40, errorKind: "KeyNotFoundException");
        InsertTelemetryRow("ws-b", "trace", "ok", "2026-06-12T10:02:00.000Z", durationMs: 9);

        DashboardActivityFeed feed = DashboardData.ReadRecentActivity(_telemetryDb, _registryDb, "ws-a");

        Assert.Equal("ws-a", feed.WorkspaceId);
        Assert.Equal(2, feed.Entries.Count);
        Assert.Equal("inspect", feed.Entries[0].Tool);
        Assert.Equal("2026-06-12T10:01:00.000Z", feed.Entries[0].Ts);
        Assert.Equal("error", feed.Entries[0].Outcome);
        Assert.Equal("KeyNotFoundException", feed.Entries[0].ErrorKind);
        Assert.Equal("search", feed.Entries[1].Tool);
        Assert.Equal("auto", feed.Entries[1].Op);
        Assert.Equal(12, feed.Entries[1].DurationMs);
        Assert.Equal(6, feed.Entries[1].ResultCount);
        Assert.Equal(120, feed.Entries[1].EstTokens);
    }

    [Fact]
    public void ReadRecentActivity_AllWorkspacesAnnotatesDisplayIds()
    {
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen(
                "ws-a",
                "alpha-abcd1234",
                Path.Combine(_dir, "alpha"),
                Path.Combine(_dir, "alpha", ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready,
                DateTimeOffset.Parse("2026-06-12T09:00:00Z"));
        }

        InsertTelemetryRow("ws-a", "search", "ok", "2026-06-12T10:00:00.000Z", durationMs: 12);
        InsertTelemetryRow("ws-unregistered", "trace", "ok", "2026-06-12T10:01:00.000Z", durationMs: 9);

        DashboardActivityFeed feed = DashboardData.ReadRecentActivity(_telemetryDb, _registryDb, workspaceId: null);

        Assert.Null(feed.WorkspaceId);
        Assert.Equal(2, feed.Entries.Count);
        Assert.Equal("ws-unregistered", feed.Entries[0].WorkspaceId);
        Assert.Equal("ws-unregistered", feed.Entries[0].WorkspaceDisplayId);
        Assert.Equal("ws-a", feed.Entries[1].WorkspaceId);
        Assert.Equal("alpha-abcd1234", feed.Entries[1].WorkspaceDisplayId);
    }

    [Fact]
    public void ReadRecentActivity_HonorsLimit()
    {
        for (int i = 0; i < 5; i++)
        {
            InsertTelemetryRow("ws-a", "search", "ok", $"2026-06-12T10:00:0{i}.000Z", durationMs: i);
        }

        DashboardActivityFeed feed = DashboardData.ReadRecentActivity(_telemetryDb, _registryDb, "ws-a", limit: 3);

        Assert.Equal(3, feed.Entries.Count);
        Assert.Equal("2026-06-12T10:00:04.000Z", feed.Entries[0].Ts);
        Assert.Equal("2026-06-12T10:00:02.000Z", feed.Entries[2].Ts);
    }

    [Fact]
    public void ReadRecentActivity_MissingDatabasesReturnEmpty()
    {
        DashboardActivityFeed feed = DashboardData.ReadRecentActivity(_telemetryDb, _registryDb, "ws-a");

        Assert.Empty(feed.Entries);
        // A missing DB is a healthy no-data state, not a corruption: the degrade channel stays clean.
        Assert.Null(feed.Error);
    }

    [Fact]
    public void ReadRecentActivity_IncludesErrorMessageAndDetail_WhenTelemetryHasDiagnostics()
    {
        const string detail = "System.ArgumentException: bad input\n   at Miller.Tests.Known()";
        InsertTelemetryRow(
            "ws-a",
            "inspect",
            "error",
            "2026-06-12T10:00:00.000Z",
            durationMs: 12,
            errorKind: "ArgumentException",
            errorMessage: "bad input",
            errorDetail: detail);

        DashboardActivityFeed feed = DashboardData.ReadRecentActivity(_telemetryDb, _registryDb, "ws-a");

        DashboardActivityEntry entry = Assert.Single(feed.Entries);
        Assert.Equal("ArgumentException", entry.ErrorKind);
        Assert.Equal("bad input", entry.ErrorMessage);
        Assert.Equal(detail, entry.ErrorDetail);
    }

    [Fact]
    public void ReadRecentActivity_OldTelemetrySchemaWithoutErrorDetailsStillReads()
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
                    ('old-row', '2026-06-12T10:00:00.000Z', 'inspect', 'ws-a', 12, 'error', 'ArgumentException');
                """;
            insert.ExecuteNonQuery();
        }

        DashboardActivityFeed feed = DashboardData.ReadRecentActivity(_telemetryDb, _registryDb, "ws-a");

        DashboardActivityEntry entry = Assert.Single(feed.Entries);
        Assert.Equal("ArgumentException", entry.ErrorKind);
        Assert.Null(entry.ErrorMessage);
        Assert.Null(entry.ErrorDetail);
    }

    [Fact]
    public void RenderActivityJson_UsesStableSnakeCaseContract()
    {
        InsertTelemetryRow("ws-a", "search", "ok", "2026-06-12T10:00:00.000Z",
            durationMs: 12, op: "auto", resultCount: 6, estTokens: 120);

        string json = DashboardData.RenderActivityJson(_telemetryDb, _registryDb, "ws-a");

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("ws-a", root.GetProperty("workspace_id").GetString());
        JsonElement entry = root.GetProperty("entries")[0];
        Assert.Equal("2026-06-12T10:00:00.000Z", entry.GetProperty("ts").GetString());
        Assert.Equal("search", entry.GetProperty("tool").GetString());
        Assert.Equal("auto", entry.GetProperty("op").GetString());
        Assert.Equal("ws-a", entry.GetProperty("workspace_id").GetString());
        Assert.Equal(12, entry.GetProperty("duration_ms").GetInt64());
        Assert.Equal("ok", entry.GetProperty("outcome").GetString());
        Assert.Equal(6, entry.GetProperty("result_count").GetInt64());
        Assert.Equal(120, entry.GetProperty("est_tokens").GetInt64());
    }

    [Fact]
    public async Task ActivityFeedPanel_RendersRowsAndPollingHooks()
    {
        var feed = new DashboardActivityFeed("ws-a",
        [
            new DashboardActivityEntry(
                Id: "0197a000-0000-7000-8000-000000000001",
                Ts: "2026-06-12T10:00:00.000Z",
                Tool: "search",
                Op: "auto",
                WorkspaceId: "ws-a",
                WorkspaceDisplayId: "alpha-abcd1234",
                DurationMs: 12,
                Outcome: "ok",
                ErrorKind: null,
                ResultCount: 6,
                EstTokens: 120),
        ]);

        string html = await RenderComponentAsync<ActivityFeedPanel>(new Dictionary<string, object?>
        {
            ["Feed"] = feed,
        });

        Assert.Contains("id=\"activity-feed-panel\"", html);
        Assert.Contains("hx-get=\"/fragments/activity?workspace_id=ws-a\"", html);
        Assert.Contains("every 5s", html);
        Assert.Contains("data-ts=\"2026-06-12T10:00:00.000Z\"", html);
        // Server-side humanized text renders inside time.rel-ts; the raw ISO stays only in data-ts/datetime.
        Assert.DoesNotContain(">2026-06-12T10:00:00.000Z</time>", html);
        Assert.Contains(" ago</time>", html);
        Assert.Contains("search", html);
        Assert.Contains("12 ms", html);
        // Scoped feed: rows do not repeat the workspace name.
        Assert.DoesNotContain("activity-ws", html);
    }

    [Fact]
    public async Task ActivityFeedPanel_MachineWideShowsWorkspaceAndErrors()
    {
        var feed = new DashboardActivityFeed(null,
        [
            new DashboardActivityEntry(
                Id: "0197a000-0000-7000-8000-000000000002",
                Ts: "2026-06-12T10:01:00.000Z",
                Tool: "inspect",
                Op: null,
                WorkspaceId: "ws-a",
                WorkspaceDisplayId: "alpha-abcd1234",
                DurationMs: 40,
                Outcome: "error",
                ErrorKind: "KeyNotFoundException",
                ErrorMessage: "missing key",
                ErrorDetail: "System.Collections.Generic.KeyNotFoundException: missing key\n   at Miller.Tests.Known()",
                ResultCount: null,
                EstTokens: null),
        ]);

        string html = await RenderComponentAsync<ActivityFeedPanel>(new Dictionary<string, object?>
        {
            ["Feed"] = feed,
        });

        Assert.Contains("hx-get=\"/fragments/activity?workspace_id=\"", html);
        Assert.Contains("alpha-abcd1234", html);
        Assert.Contains("KeyNotFoundException", html);
        Assert.Contains("missing key", html);
        Assert.Contains("System.Collections.Generic.KeyNotFoundException", html);
        Assert.Contains("<details", html);
        Assert.Contains("data-issue-details", html);
        Assert.Contains("data-issue-id=\"0197a000-0000-7000-8000-000000000002\"", html);
        Assert.Contains("<summary>view issue details</summary>", html);
        Assert.Contains("class=\"copy-issue-button\"", html);
        Assert.Contains("data-copy-target=\"issue-copy-0197a000-0000-7000-8000-000000000002\"", html);
        Assert.Contains(">Copy</button>", html);
        Assert.Contains("id=\"issue-copy-0197a000-0000-7000-8000-000000000002\"", html);
        Assert.DoesNotContain("copy issue details", html);
        Assert.Contains("outcome error", html);
    }

    [Fact]
    public async Task Shells_MountActivityFeedPanel()
    {
        var index = new DashboardWorkspaceIndex(Array.Empty<DashboardWorkspaceIndexEntry>(), 0, 0, 0, 0);
        var machineFeed = new DashboardActivityFeed(null, Array.Empty<DashboardActivityEntry>());
        string landingHtml = await RenderComponentAsync<WorkspacesShell>(new Dictionary<string, object?>
        {
            ["Index"] = index,
            ["Activity"] = machineFeed,
        });

        Assert.Contains("id=\"activity-feed-panel\"", landingHtml);
        Assert.Contains("hx-get=\"/fragments/activity?workspace_id=\"", landingHtml);

        var snapshot = new DashboardSnapshot(
            Array.Empty<DashboardWorkspaceRow>(),
            new DashboardTelemetrySummary("ws-a", [], 0, null, null, []),
            "ws-a");
        var scopedFeed = new DashboardActivityFeed("ws-a", Array.Empty<DashboardActivityEntry>());
        string workspaceHtml = await RenderComponentAsync<WorkspaceShell>(new Dictionary<string, object?>
        {
            ["Snapshot"] = snapshot,
            ["Activity"] = scopedFeed,
        });

        Assert.Contains("id=\"activity-feed-panel\"", workspaceHtml);
        Assert.Contains("hx-get=\"/fragments/activity?workspace_id=ws-a\"", workspaceHtml);
    }

    [Fact]
    public async Task TelemetryPanel_RendersDegradeNoticeWhenErrorPresent()
    {
        var telemetry = new DashboardTelemetrySummary(
            "ws-a", [], 0, null, null, [],
            Error: "telemetry read degraded: database disk image is malformed");

        string html = await RenderComponentAsync<TelemetryPanel>(new Dictionary<string, object?>
        {
            ["Telemetry"] = telemetry,
            ["SelectedWorkspaceId"] = "ws-a",
        });

        // Same notice markup/classes as the registry-unavailable notice in WorkspaceIndex.
        Assert.Contains("class=\"notice error-notice\"", html);
        Assert.Contains("telemetry read degraded: database disk image is malformed", html);
        // The (empty) panel content still renders below the notice.
        Assert.Contains("No telemetry recorded", html);
    }

    [Fact]
    public async Task TelemetryPanel_HealthyEmptyRendersNoDegradeNotice()
    {
        var telemetry = new DashboardTelemetrySummary("ws-a", [], 0, null, null, []);

        string html = await RenderComponentAsync<TelemetryPanel>(new Dictionary<string, object?>
        {
            ["Telemetry"] = telemetry,
            ["SelectedWorkspaceId"] = "ws-a",
        });

        Assert.DoesNotContain("error-notice", html);
        Assert.Contains("No telemetry recorded", html);
    }

    [Fact]
    public async Task ActivityFeedPanel_RendersDegradeNoticeWhenErrorPresent()
    {
        var feed = new DashboardActivityFeed(
            "ws-a",
            Array.Empty<DashboardActivityEntry>(),
            Error: "telemetry read degraded: database disk image is malformed");

        string html = await RenderComponentAsync<ActivityFeedPanel>(new Dictionary<string, object?>
        {
            ["Feed"] = feed,
        });

        Assert.Contains("class=\"notice error-notice\"", html);
        Assert.Contains("telemetry read degraded: database disk image is malformed", html);
        // The (empty) feed content still renders below the notice.
        Assert.Contains("No tool calls recorded yet", html);
    }

    [Fact]
    public async Task ActivityFeedPanel_HealthyEmptyRendersNoDegradeNotice()
    {
        var feed = new DashboardActivityFeed("ws-a", Array.Empty<DashboardActivityEntry>());

        string html = await RenderComponentAsync<ActivityFeedPanel>(new Dictionary<string, object?>
        {
            ["Feed"] = feed,
        });

        Assert.DoesNotContain("error-notice", html);
        Assert.Contains("No tool calls recorded yet", html);
    }

    [Fact]
    public void ReadTelemetrySummary_AllSentinelAggregatesAcrossWorkspaces()
    {
        InsertTelemetryRow("ws-a", "search", "ok", "2026-06-12T10:00:00.000Z", durationMs: 10, estTokens: 100);
        InsertTelemetryRow("ws-b", "search", "ok", "2026-06-12T10:01:00.000Z", durationMs: 30, estTokens: 50);
        InsertTelemetryRow("ws-b", "inspect", "error", "2026-06-12T10:02:00.000Z",
            durationMs: 7, errorKind: "Boom");

        DashboardTelemetrySummary summary = DashboardData.ReadTelemetrySummary(_telemetryDb, "all");

        Assert.Equal("all", summary.WorkspaceId);
        Assert.Equal(3, summary.TotalCalls);
        DashboardToolStat search = Assert.Single(summary.Tools, t => t.Tool == "search");
        Assert.Equal(2, search.Calls);
        Assert.Equal(30, search.MaxMs);
        Assert.Equal(150, search.SumEstTokens);
        Assert.Equal("2026-06-12T10:01:00.000Z", search.LastCallTs);
        DashboardRecentError error = Assert.Single(summary.RecentErrors);
        Assert.Equal("Boom", error.ErrorKind);
    }

    [Fact]
    public async Task WorkspacesShell_MountsMachineWideTelemetryPanel()
    {
        var index = new DashboardWorkspaceIndex(Array.Empty<DashboardWorkspaceIndexEntry>(), 0, 0, 0, 0);
        var telemetry = new DashboardTelemetrySummary("all",
        [
            new DashboardToolStat("search", 2, 20.0, 30, 30, 0, 150,
                "2026-06-12T10:01:00.000Z", "ok", null, null),
        ], 2, "2026-06-12T10:00:00.000Z", "2026-06-12T10:01:00.000Z", []);

        string html = await RenderComponentAsync<WorkspacesShell>(new Dictionary<string, object?>
        {
            ["Index"] = index,
            ["Telemetry"] = telemetry,
        });

        Assert.Contains("id=\"telemetry-panel\"", html);
        Assert.Contains("hx-get=\"/fragments/telemetry?workspace_id=all\"", html);
        Assert.Contains("search", html);
    }

    [Fact]
    public async Task TelemetryPanel_RendersRelativeTimeHooks()
    {
        var telemetry = new DashboardTelemetrySummary("ws-a",
        [
            new DashboardToolStat("search", 2, 20.0, 30, 30, 1, 150,
                "2026-06-12T10:01:00.000Z", "ok", "2026-06-12T10:00:30.000Z", "Boom"),
        ], 2, "2026-06-12T10:00:00.000Z", "2026-06-12T10:01:00.000Z",
        [
            new DashboardRecentError("2026-06-12T10:00:30.000Z", "search", "auto", "Boom", 20),
        ]);

        string html = await RenderComponentAsync<TelemetryPanel>(new Dictionary<string, object?>
        {
            ["Telemetry"] = telemetry,
            ["SelectedWorkspaceId"] = "ws-a",
        });

        Assert.Contains("data-ts=\"2026-06-12T10:01:00.000Z\"", html);
        Assert.Contains("data-ts=\"2026-06-12T10:00:30.000Z\"", html);
        Assert.Contains("rel-ts", html);
        // <time> text is humanized server-side; raw ISO stays only in data-ts/datetime.
        Assert.DoesNotContain(">2026-06-12T10:01:00.000Z</time>", html);
        Assert.Contains(" ago</time>", html);
        // Window label is humanized to a short absolute form — no raw ISO timestamp string.
        Assert.Contains("Jun 12, 10:00 UTC", html);
        Assert.Contains("Jun 12, 10:01 UTC", html);
        Assert.DoesNotContain("from 2026-06-12T10:00:00.000Z to 2026-06-12T10:01:00.000Z", html);
    }

    [Fact]
    public async Task TelemetryPanel_RendersSortableHeadersWithNumericSortKeys()
    {
        var telemetry = new DashboardTelemetrySummary("ws-a",
        [
            new DashboardToolStat("search", 1234, 20.5, 30, 44, 2, 9876,
                "2026-06-12T10:01:00.000Z", "ok", "2026-06-12T10:00:30.000Z", "Boom"),
        ], 1234, "2026-06-12T10:00:00.000Z", "2026-06-12T10:01:00.000Z", []);

        string html = await RenderComponentAsync<TelemetryPanel>(new Dictionary<string, object?>
        {
            ["Telemetry"] = telemetry,
            ["SelectedWorkspaceId"] = "ws-a",
        });

        Assert.Contains("x-data=\"telemetryTableSort\"", html);
        Assert.Contains("data-sort-col=\"tool\"", html);
        Assert.Contains("data-sort-col=\"calls\"", html);
        Assert.Contains("data-sort-col=\"avg\"", html);
        Assert.Contains("data-sort-col=\"p95\"", html);
        Assert.Contains("data-sort-col=\"max\"", html);
        Assert.Contains("data-sort-col=\"errors\"", html);
        Assert.Contains("data-sort-col=\"tokens\"", html);
        Assert.Contains("aria-sort=\"none\"", html);

        Assert.Contains("data-sort-tool=\"search\"", html);
        Assert.Contains("data-sort-calls=\"1234\"", html);
        Assert.Contains("data-sort-avg=\"20.5\"", html);
        Assert.Contains("data-sort-p95=\"30\"", html);
        Assert.Contains("data-sort-max=\"44\"", html);
        Assert.Contains("data-sort-errors=\"2\"", html);
        Assert.Contains("data-sort-tokens=\"9876\"", html);
        Assert.DoesNotContain("data-sort-calls=\"1,234\"", html);
        Assert.DoesNotContain("data-sort-tokens=\"9,876\"", html);
    }

    [Fact]
    public async Task TelemetryPanel_SortableHeadersKeepResponsiveOptionalColumns()
    {
        var telemetry = new DashboardTelemetrySummary("ws-a",
        [
            new DashboardToolStat("search", 2, 20.0, 30, 30, 0, 150,
                "2026-06-12T10:01:00.000Z", "ok", null, null),
        ], 2, "2026-06-12T10:00:00.000Z", "2026-06-12T10:01:00.000Z", []);

        string html = await RenderComponentAsync<TelemetryPanel>(new Dictionary<string, object?>
        {
            ["Telemetry"] = telemetry,
            ["SelectedWorkspaceId"] = "ws-a",
        });

        Assert.Equal(10, CountOccurrences(html, "telemetry-col-optional"));
    }

    [Fact]
    public async Task TelemetryPanel_RendersRecentErrorsAsFixedGridCells()
    {
        var telemetry = new DashboardTelemetrySummary("ws-a",
        [
            new DashboardToolStat("search", 2, 20.0, 30, 30, 1, 150,
                "2026-06-12T10:01:00.000Z", "ok", "2026-06-12T10:00:30.000Z", "Boom"),
        ], 2, "2026-06-12T10:00:00.000Z", "2026-06-12T10:01:00.000Z",
        [
            new DashboardRecentError("2026-06-12T10:00:30.000Z", "search", "auto", "Boom", 20,
                Id: "cid-1", ErrorMessage: "boom happened"),
            new DashboardRecentError("2026-06-12T10:00:10.000Z", "inspect", null, null, 7),
        ]);

        string html = await RenderComponentAsync<TelemetryPanel>(new Dictionary<string, object?>
        {
            ["Telemetry"] = telemetry,
            ["SelectedWorkspaceId"] = "ws-a",
        });

        Assert.Contains("recent-error-row", html);
        Assert.Equal(2, CountOccurrences(html, "recent-error-time"));
        Assert.Equal(2, CountOccurrences(html, "recent-error-tool"));
        Assert.Equal(2, CountOccurrences(html, "recent-error-op"));
        Assert.Equal(2, CountOccurrences(html, "recent-error-kind"));
        Assert.Equal(2, CountOccurrences(html, "recent-error-duration"));

        Assert.Contains("data-issue-details", html);
        Assert.Contains("data-issue-id=\"cid-1\"", html);
        Assert.Contains("recent-error-detail", html);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task Shells_IncludeDashboardBehaviorScripts()
    {
        var index = new DashboardWorkspaceIndex(Array.Empty<DashboardWorkspaceIndexEntry>(), 0, 0, 0, 0);
        string landingHtml = await RenderComponentAsync<WorkspacesShell>(new Dictionary<string, object?>
        {
            ["Index"] = index,
        });

        Assert.Contains("/js/dashboard-site.js", landingHtml);
        Assert.Contains("/js/alpine-components.js", landingHtml);
        Assert.Contains("/lib/alpine/cspalpine.min.js", landingHtml);
        Assert.DoesNotContain("onclick=", landingHtml);

        var snapshot = new DashboardSnapshot(
            Array.Empty<DashboardWorkspaceRow>(),
            new DashboardTelemetrySummary("ws-a", [], 0, null, null, []),
            "ws-a");
        string workspaceHtml = await RenderComponentAsync<WorkspaceShell>(new Dictionary<string, object?>
        {
            ["Snapshot"] = snapshot,
        });

        Assert.Contains("/js/dashboard-site.js", workspaceHtml);
        Assert.Contains("data-toggle-theme", workspaceHtml);
        Assert.DoesNotContain("onclick=", workspaceHtml);
    }

    [Fact]
    public void DashboardHost_ServesActivityRoutes()
    {
        string endpoints = File.ReadAllText(Path.Combine(
            Miller.Tests.ScaleTestSupport.RepoRoot(),
            "src",
            "Miller.Dashboard",
            "Endpoints",
            "DashboardEndpoints.cs"));

        Assert.Contains("MapGet(\"/fragments/activity\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/activity.json\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/fragments/refresh\"", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshStatusPanel_RendersResultAndSwapTarget()
    {
        var result = new Miller.Server.Workspaces.WorkspaceRefreshResult(
            Miller.Server.Workspaces.WorkspaceRefreshStatus.Refreshed,
            "ws-a",
            "/repo/a",
            "/repo/a/.miller/symbols.db",
            Revision: 43,
            Scanned: true);

        string html = await RenderComponentAsync<RefreshStatusPanel>(new Dictionary<string, object?>
        {
            ["Result"] = result,
        });

        Assert.Contains("id=\"refresh-status\"", html);
        Assert.Contains("refreshed", html);
        Assert.Contains("rev 43", html);
    }

    [Fact]
    public async Task WorkspaceDetailPanel_WiresRefreshButton()
    {
        var facts = new DashboardWorkspaceFacts(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db",
            "ready", null, 1, 1, 1, 100, 42, "2026-06-12T09:00:00Z", "fresh",
            Array.Empty<DashboardLanguageStat>(), Array.Empty<DashboardSymbolKindStat>());

        string html = await RenderComponentAsync<WorkspaceDetailPanel>(new Dictionary<string, object?>
        {
            ["Facts"] = facts,
        });

        Assert.Contains("hx-post=\"/fragments/refresh?workspace_id=ws-a\"", html);
        Assert.Contains("id=\"refresh-status\"", html);
    }

    [Fact]
    public async Task WorkspaceDetailPanel_RefreshButtonShowsInProgressIndicator()
    {
        var facts = new DashboardWorkspaceFacts(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db",
            "ready", null, 1, 1, 1, 100, 42, "2026-06-12T09:00:00Z", "fresh",
            Array.Empty<DashboardLanguageStat>(), Array.Empty<DashboardSymbolKindStat>());

        string html = await RenderComponentAsync<WorkspaceDetailPanel>(new Dictionary<string, object?>
        {
            ["Facts"] = facts,
        });

        // Idle label plus an htmx-request-only "Refreshing…" indicator, both inside the button.
        Assert.Contains("refresh-button-label", html);
        Assert.Contains("refresh-button-indicator", html);
        Assert.Contains("Refreshing", html);
    }

    [Fact]
    public async Task WorkspaceDetailPanel_OpenFolderIsButtonWithToastHook()
    {
        var facts = new DashboardWorkspaceFacts(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db",
            "ready", null, 1, 1, 1, 100, 42, "2026-06-12T09:00:00Z", "fresh",
            Array.Empty<DashboardLanguageStat>(), Array.Empty<DashboardSymbolKindStat>());

        string html = await RenderComponentAsync<WorkspaceDetailPanel>(new Dictionary<string, object?>
        {
            ["Facts"] = facts,
        });

        // Real button styling (not the .subtle-link look) plus a success-toast hook the JS reads on 2xx.
        Assert.Contains("open-folder-button", html);
        Assert.DoesNotContain("subtle-link open-folder-link", html);
        Assert.Contains("data-toast-success=", html);
        Assert.Contains("hx-post=\"/workspaces/ws-a/open-folder\"", html);
    }

    [Fact]
    public async Task WorkspaceDetailPanel_ArtifactIdRendersTruncatedCopyableChip()
    {
        var facts = new DashboardWorkspaceFacts(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db",
            "ready", null, 1, 1, 1, 100, 42, "2026-06-12T09:00:00Z", "fresh",
            Array.Empty<DashboardLanguageStat>(), Array.Empty<DashboardSymbolKindStat>(),
            ArtifactId: "artifact-0123456789-tail");

        string html = await RenderComponentAsync<WorkspaceDetailPanel>(new Dictionary<string, object?>
        {
            ["Facts"] = facts,
        });

        Assert.Contains("artifact-012&#x2026;", html);                            // first 12 chars + ellipsis
        Assert.Contains("title=\"artifact-0123456789-tail\"", html);            // full value in title
        Assert.Contains("id=\"copy-artifact-id\"", html);                       // hidden full-value copy source
        Assert.Contains("data-copy-target=\"copy-artifact-id\"", html);         // reuses the copy-button pattern
    }

    [Fact]
    public async Task WorkspaceDetailPanel_JargonTermsCarryTitleExplanations()
    {
        var facts = new DashboardWorkspaceFacts(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db",
            "ready", null, 1, 1, 1, 100, 42, "2026-06-12T09:00:00Z", "fresh",
            Array.Empty<DashboardLanguageStat>(), Array.Empty<DashboardSymbolKindStat>());

        string html = await RenderComponentAsync<WorkspaceDetailPanel>(new Dictionary<string, object?>
        {
            ["Facts"] = facts,
        });

        Assert.Contains("<dt title=\"", html);
        Assert.Contains("revision", html, StringComparison.OrdinalIgnoreCase);
        // Each called-out term gets a plain-English hover explanation.
        Assert.Contains("re-extract", html, StringComparison.OrdinalIgnoreCase); // revision explanation
        Assert.Contains("full rebuild", html, StringComparison.OrdinalIgnoreCase); // artifact explanation
        Assert.Contains("derived search index", html, StringComparison.OrdinalIgnoreCase); // sidecar explanation
    }

    [Fact]
    public async Task WorkspaceDetailPanel_LastScanRendersRelativeTime()
    {
        var facts = new DashboardWorkspaceFacts(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db",
            "ready", null, 1, 1, 1, 100, 42, "2026-06-12T09:00:00Z", "fresh",
            Array.Empty<DashboardLanguageStat>(), Array.Empty<DashboardSymbolKindStat>());

        string html = await RenderComponentAsync<WorkspaceDetailPanel>(new Dictionary<string, object?>
        {
            ["Facts"] = facts,
        });

        Assert.Contains("data-ts=\"2026-06-12T09:00:00Z\"", html);        // raw ISO stays in data-ts
        Assert.DoesNotContain(">2026-06-12T09:00:00Z</time>", html);      // visible text is humanized
        Assert.Contains(" ago</time>", html);
    }

    [Fact]
    public async Task WorkspaceLocalMetricsPanel_CloneHashRendersTruncatedCopyableChip()
    {
        var metrics = new DashboardLocalMetricsPanel(
            "ws-a",
            "ready",
            Array.Empty<DashboardMetricComplexityHotspot>(),
            [
                new DashboardMetricCloneGroup(
                    "blake3:0123456789abcdef",
                    2,
                    [new DashboardMetricCloneSymbol("Foo", "method", "src/A.cs", 10)]),
            ]);

        string html = await RenderComponentAsync<WorkspaceLocalMetricsPanel>(new Dictionary<string, object?>
        {
            ["Metrics"] = metrics,
        });

        Assert.Contains("blake3:01234&#x2026;", html);                            // first 12 chars + ellipsis
        Assert.Contains("title=\"blake3:0123456789abcdef\"", html);             // full value in title
        Assert.Contains("id=\"copy-clone-hash-0\"", html);                      // hidden full-value copy source
        Assert.Contains("data-copy-target=\"copy-clone-hash-0\"", html);        // reuses the copy-button pattern
    }

    [Fact]
    public async Task WorkspaceTrendsPanel_SparklineShowsMinMaxLatestLabels()
    {
        var trends = new DashboardWorkspaceTrendsPanel(
            "ws-a",
            [
                new DashboardTrendSeries("symbol_count", "symbols", [10d, 30d, 20d], First: 10d, Latest: 20d),
            ]);

        string html = await RenderComponentAsync<WorkspaceTrendsPanel>(new Dictionary<string, object?>
        {
            ["Trends"] = trends,
        });

        Assert.Contains("sparkline-scale", html);
        Assert.Contains("min 10", html);
        Assert.Contains("max 30", html);
        Assert.Contains("latest 20", html);
    }

    [Fact]
    public void TryRefreshWorkspace_WrapsFailuresInsteadOfThrowing()
    {
        using (var registry = WorkspaceRegistry.Open(_registryDb))
        {
            // Registry exists but the requested workspace is not registered.
        }

        Miller.Server.Workspaces.WorkspaceRefreshResult result = DashboardData.TryRefreshWorkspace(
            _registryDb,
            Path.Combine(_dir, "no-tools"),
            "ws-not-registered");

        Assert.Equal(Miller.Server.Workspaces.WorkspaceRefreshStatus.Failed, result.Status);
        Assert.Equal("ws-not-registered", result.WorkspaceId);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task DashboardHead_UsesVendoredFontsNotCdn()
    {
        string html = await RenderComponentAsync<DashboardHead>(new Dictionary<string, object?>
        {
            ["Title"] = "Miller — Test",
        });

        Assert.DoesNotContain("fonts.googleapis.com", html);
        Assert.DoesNotContain("fonts.gstatic.com", html);
        Assert.Contains("/js/theme-init.js", html);
        Assert.DoesNotContain("<script>\n", html);
    }

    [Fact]
    public void DashboardHost_ServesVendoredFontAssets()
    {
        string dashboardRoot = Path.Combine(
            Miller.Tests.ScaleTestSupport.RepoRoot(),
            "src",
            "Miller.Dashboard");
        // The endpoint composition lives in DashboardHostPipeline (extracted from Program.cs for TestServer).
        string pipeline = File.ReadAllText(Path.Combine(dashboardRoot, "DashboardHostPipeline.cs"));
        string css = File.ReadAllText(Path.Combine(dashboardRoot, "wwwroot", "dashboard.css"));

        Assert.Contains("/fonts/archivo-latin.woff2", pipeline, StringComparison.Ordinal);
        Assert.Contains("/fonts/jetbrains-mono-latin.woff2", pipeline, StringComparison.Ordinal);
        Assert.Contains("/js/dashboard-site.js", pipeline, StringComparison.Ordinal);
        Assert.Contains("/lib/alpine/cspalpine.min.js", pipeline, StringComparison.Ordinal);
        Assert.Contains("@font-face", css, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dashboardRoot, "wwwroot", "fonts", "archivo-latin.woff2")));
        Assert.True(File.Exists(Path.Combine(dashboardRoot, "wwwroot", "fonts", "jetbrains-mono-latin.woff2")));
    }

    [Fact]
    public void ReleaseWorkflow_VerifiesVendoredDashboardScriptAssets()
    {
        string workflow = File.ReadAllText(Path.Combine(
            Miller.Tests.ScaleTestSupport.RepoRoot(),
            ".github",
            "workflows",
            "release.yml"));

        Assert.Contains("dashboard/wwwroot/js/theme-init.js", workflow, StringComparison.Ordinal);
        Assert.Contains("dashboard/wwwroot/js/dashboard-site.js", workflow, StringComparison.Ordinal);
        Assert.Contains("dashboard/wwwroot/js/alpine-components.js", workflow, StringComparison.Ordinal);
        Assert.Contains("dashboard/wwwroot/lib/alpine/cspalpine.min.js", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceOnboardingPanel_PluralizesCommonMisses()
    {
        var onboarding = new DashboardWorkspaceOnboardingPanel(
            "ws-a",
            "ready",
            TotalCalls: 20,
            Array.Empty<string>(),
            Array.Empty<DashboardOnboardingTarget>(),
            [
                new DashboardOnboardingMiss("search", "auto", "no results", 4),
                new DashboardOnboardingMiss("inspect", null, "unknown symbol", 2),
            ],
            Array.Empty<string>());

        string html = await RenderComponentAsync<WorkspaceOnboardingPanel>(new Dictionary<string, object?>
        {
            ["Onboarding"] = onboarding,
        });

        Assert.Contains("2 common misses", html);
        Assert.DoesNotContain("misss", html);
    }

    [Fact]
    public async Task WorkspaceOnboardingPanel_CollapsesUnresolvedHotTargetsIntoOneSummaryRow()
    {
        var onboarding = new DashboardWorkspaceOnboardingPanel(
            "ws-a",
            "ready",
            TotalCalls: 30,
            Array.Empty<string>(),
            [
                new DashboardOnboardingTarget("resolved", "FullRebuildPromotion", "class", "src/A.cs", 10, 9),
                new DashboardOnboardingTarget("unresolved_hash", null, null, null, null, 4),
                new DashboardOnboardingTarget("unresolved_hash", null, null, null, null, 3),
                new DashboardOnboardingTarget("unresolved_hash", null, null, null, null, 1),
            ],
            Array.Empty<DashboardOnboardingMiss>(),
            Array.Empty<string>());

        string html = await RenderComponentAsync<WorkspaceOnboardingPanel>(new Dictionary<string, object?>
        {
            ["Onboarding"] = onboarding,
        });

        Assert.Contains("FullRebuildPromotion", html);
        Assert.Contains("9 calls", html);
        Assert.Contains("3 unresolved targets", html);
        Assert.Contains("8 calls", html);
        Assert.Contains("hashes not present in the current index", html);
        Assert.DoesNotContain("unresolved_hash", html);
        Assert.Equal(1, html.Split("unresolved target").Length - 1);
        Assert.Contains("<strong>1 hot target</strong>", html);
        Assert.DoesNotContain("4 hot targets", html);
    }

    [Fact]
    public async Task WorkspaceOnboardingPanel_AllHotTargetsUnresolved_RendersSummaryRowOnly()
    {
        var onboarding = new DashboardWorkspaceOnboardingPanel(
            "ws-a",
            "ready",
            TotalCalls: 12,
            Array.Empty<string>(),
            [
                new DashboardOnboardingTarget("unresolved_hash", null, null, null, null, 5),
                new DashboardOnboardingTarget("unresolved_hash", "   ", null, null, null, 2),
            ],
            Array.Empty<DashboardOnboardingMiss>(),
            Array.Empty<string>());

        string html = await RenderComponentAsync<WorkspaceOnboardingPanel>(new Dictionary<string, object?>
        {
            ["Onboarding"] = onboarding,
        });

        Assert.Contains("2 unresolved targets", html);
        Assert.Contains("7 calls", html);
        Assert.DoesNotContain("No hot targets.", html);
        Assert.Equal(1, html.Split("<li>").Length - 1);
        Assert.Contains("<strong>0 hot targets</strong>", html);
    }

    [Fact]
    public async Task PatternInventoryPanel_OmitsRedundantCapturesAndSinglePatternCount()
    {
        var inventory = new DashboardPatternInventoryPanel(
            "ws-a",
            "ready",
            [new DashboardPatternFamily("json.property", 1, 128, ["json"], ["property"])]);

        string html = await RenderComponentAsync<PatternInventoryPanel>(new Dictionary<string, object?>
        {
            ["Inventory"] = inventory,
        });

        Assert.Contains("json.property", html);
        Assert.Contains("128 facts", html);
        Assert.Contains("languages: json", html);
        Assert.DoesNotContain("captures:", html);
        Assert.DoesNotContain("1 pattern", html);
    }

    [Fact]
    public async Task PatternInventoryPanel_ShowsPatternCountAndCapturesWhenInformative()
    {
        var inventory = new DashboardPatternInventoryPanel(
            "ws-a",
            "ready",
            [new DashboardPatternFamily("dotnet.route", 3, 12, ["csharp", "razor"], ["route", "verb"])]);

        string html = await RenderComponentAsync<PatternInventoryPanel>(new Dictionary<string, object?>
        {
            ["Inventory"] = inventory,
        });

        Assert.Contains("3 patterns", html);
        Assert.Contains("languages: csharp, razor", html);
        Assert.Contains("captures: route, verb", html);
    }

    [Fact]
    public async Task WorkspaceIndex_RendersLastUsedColumnWithRelativeTime()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = IndexWithActivity(lastActivityTs: "2026-06-12T10:00:00.000Z"),
        });

        Assert.Contains("data-sort-col=\"activity\"", html);
        Assert.Contains("Last used", html);
        Assert.Contains("data-ts=\"2026-06-12T10:00:00.000Z\"", html);
        Assert.Contains("class=\"rel-ts timestamp\"", html);
        Assert.Contains(" ago</time>", html);
        Assert.DoesNotContain(">2026-06-12T10:00:00.000Z</time>", html);
    }

    [Fact]
    public async Task WorkspaceIndex_LastUsedSortKeyIsEpochSecondsAndMinusOneWhenNever()
    {
        string withActivity = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = IndexWithActivity(lastActivityTs: "2026-06-12T10:00:00.000Z"),
        });

        Assert.Contains("data-sort-activity=\"1781258400\"", withActivity);

        string withoutActivity = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = IndexWithActivity(lastActivityTs: null),
        });

        Assert.Contains("data-sort-activity=\"-1\"", withoutActivity);
        Assert.Contains(">never<", withoutActivity);
    }

    [Fact]
    public async Task WorkspaceIndex_StretchedLinkRowsDropInlineTextDecorationStyle()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = IndexWithActivity(lastActivityTs: null),
        });

        Assert.Contains("<a class=\"workspace-name\" href=\"/workspace?workspace_id=ws-a\"", html);
        Assert.DoesNotContain("style=\"text-decoration: none\"", html);
    }

    [Fact]
    public async Task WorkspaceIndex_RemoveControlSitsInRightRailKeepingIssueDetailsAttributes()
    {
        string html = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = IndexWithActivity(lastActivityTs: null),
        });

        Assert.Contains("class=\"ws-cell ws-row-actions\" role=\"cell\"", html);
        Assert.Contains("data-issue-details", html);
        Assert.Contains("data-issue-id=\"remove-ws-a\"", html);
        Assert.Contains(">Remove…</summary>", html);
    }

    [Fact]
    public async Task WorkspaceIndex_FactlessRowsExplainMissingCountsAndEmptyIndexes()
    {
        string factless = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = IndexWithFacts(FactsWithStatus("missing", fileCount: 0)),
        });

        Assert.Contains("no facts", factless);
        // Blazor renders attribute values through HtmlEncoder.Default, so the em dash arrives as an entity.
        Assert.Contains("title=\"index facts unavailable", factless);
        Assert.Contains("open the workspace to inspect\"", factless);

        string emptyIndex = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = IndexWithFacts(FactsWithStatus("ready", fileCount: 0)),
        });

        Assert.Contains("empty index", emptyIndex);
        Assert.DoesNotContain("no facts", emptyIndex);

        string populated = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = IndexWithFacts(FactsWithStatus("ready", fileCount: 4)),
        });

        Assert.DoesNotContain("no facts", populated);
        Assert.DoesNotContain("empty index", populated);
    }

    [Fact]
    public async Task WorkspaceRemoveConfirm_CancelIsAButtonThatClosesTheDetailsInsteadOfNavigating()
    {
        string html = await RenderComponentAsync<WorkspaceRemoveConfirm>(new Dictionary<string, object?>
        {
            ["WorkspaceId"] = "ws-a",
        });

        Assert.Contains("data-close-details", html, StringComparison.Ordinal);
        Assert.Contains(">Cancel</button>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Cancel</a>", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/workspace/remove\"", html, StringComparison.Ordinal);
        Assert.Contains("data-issue-id=\"remove-ws-a\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceDetailPanel_RemoveCancelDropsTheWorkspaceHrefNavigation()
    {
        string html = await RenderComponentAsync<WorkspaceDetailPanel>(new Dictionary<string, object?>
        {
            ["Facts"] = FactsWithStatus("ready", fileCount: 4),
        });

        Assert.Contains("data-close-details", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Cancel</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceIndex_NoticeCarriesToneMarkersSoJsCanMirrorItAsAToast()
    {
        string removed = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = IndexWithActivity(lastActivityTs: null),
            ["Notice"] = "removed",
            ["NoticeDetail"] = "/repo/a",
        });

        Assert.Contains("data-notice", removed, StringComparison.Ordinal);
        Assert.Contains("data-notice-tone=\"ok\"", removed, StringComparison.Ordinal);

        string failed = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = IndexWithActivity(lastActivityTs: null),
            ["Notice"] = "remove-error",
            ["NoticeDetail"] = "locked",
        });

        Assert.Contains("data-notice-tone=\"danger\"", failed, StringComparison.Ordinal);

        string none = await RenderComponentAsync<WorkspaceIndex>(new Dictionary<string, object?>
        {
            ["Index"] = IndexWithActivity(lastActivityTs: null),
        });

        Assert.DoesNotContain("data-notice", none, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shells_ThemeButtonRendersBothLabelsSoCssPicksOneWithoutAFlash()
    {
        var index = new DashboardWorkspaceIndex(Array.Empty<DashboardWorkspaceIndexEntry>(), 0, 0, 0, 0);
        string landing = await RenderComponentAsync<WorkspacesShell>(new Dictionary<string, object?>
        {
            ["Index"] = index,
        });

        var snapshot = new DashboardSnapshot(
            Array.Empty<DashboardWorkspaceRow>(),
            new DashboardTelemetrySummary("ws-a", [], 0, null, null, []),
            "ws-a");
        string workspace = await RenderComponentAsync<WorkspaceShell>(new Dictionary<string, object?>
        {
            ["Snapshot"] = snapshot,
        });

        string notFound = await RenderComponentAsync<NotFoundPage>(new Dictionary<string, object?>
        {
            ["Message"] = "workspace_id 'bogus' is not registered.",
        });

        foreach (string html in new[] { landing, workspace, notFound })
        {
            Assert.Contains("class=\"theme-label-dark\">Dark</span>", html, StringComparison.Ordinal);
            Assert.Contains("class=\"theme-label-light\">Light</span>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("id=\"theme-toggle-label\"", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DashboardCss_ShowsExactlyOneThemeLabelPerTheme()
    {
        string css = File.ReadAllText(Path.Combine(
            Miller.Tests.ScaleTestSupport.RepoRoot(),
            "src", "Miller.Dashboard", "wwwroot", "dashboard.css"));

        Assert.Contains(".theme-label-light { display: none; }", css, StringComparison.Ordinal);
        Assert.Contains("html[data-theme=\"dark\"] .theme-label-dark { display: none; }", css, StringComparison.Ordinal);
        Assert.Contains("html[data-theme=\"dark\"] .theme-label-light { display: inline; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardSiteScript_ClosesDetailsMirrorsNoticeToastAndLeavesThemeLabelToCss()
    {
        string js = File.ReadAllText(Path.Combine(
            Miller.Tests.ScaleTestSupport.RepoRoot(),
            "src", "Miller.Dashboard", "wwwroot", "js", "dashboard-site.js"));

        Assert.Contains("[data-close-details]", js, StringComparison.Ordinal);
        Assert.Contains("data-notice-tone", js, StringComparison.Ordinal);
        Assert.Contains("showDashboardToast", js, StringComparison.Ordinal);
        Assert.Contains("aria-pressed", js, StringComparison.Ordinal);
        Assert.DoesNotContain("theme-toggle-label", js, StringComparison.Ordinal);
    }

    private static DashboardWorkspaceFacts FactsWithStatus(string status, long fileCount) =>
        new(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db", status, null,
            FileCount: fileCount, SymbolCount: fileCount, LanguageCount: 1, ContentBytes: 100,
            LastRevision: 42, LastScanAt: "2026-05-31T10:01:00Z", SearchSidecarStatus: "fresh",
            Languages: [], SymbolKinds: []);

    private static DashboardWorkspaceIndex IndexWithFacts(DashboardWorkspaceFacts facts)
    {
        var row = new DashboardWorkspaceRow(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db",
            "2026-05-31T10:00:00Z", "2026-05-31T10:01:00Z", 42, "ready", null);

        return new DashboardWorkspaceIndex(
            Entries: [new DashboardWorkspaceIndexEntry(row, facts, RootExists: true)],
            WorkspaceCount: 1, TotalFiles: facts.FileCount, TotalSymbols: facts.SymbolCount,
            LanguageCount: 1, LiveCount: 1, MissingRootCount: 0, ErrorCount: 0);
    }

    private static DashboardWorkspaceIndex IndexWithActivity(string? lastActivityTs)
    {
        var row = new DashboardWorkspaceRow(
            "ws-a", "alpha-abcd1234", "/repo/a", "/repo/a/.miller/symbols.db",
            "2026-05-31T10:00:00Z", "2026-05-31T10:01:00Z", 42, "ready", null);
        var facts = FactsWithStatus("ready", fileCount: 4);

        return new DashboardWorkspaceIndex(
            Entries: [new DashboardWorkspaceIndexEntry(row, facts, RootExists: true, lastActivityTs)],
            WorkspaceCount: 1, TotalFiles: 4, TotalSymbols: 4,
            LanguageCount: 1, LiveCount: 1, MissingRootCount: 0, ErrorCount: 0);
    }

    private static async Task<string> RenderComponentAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The detail panel's remove form embeds <AntiforgeryToken/>; outside a real HTTP request the provider
        // is this fixed-token stub so the hidden input still renders (the value is never validated here).
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

    private void InsertTelemetryRow(
        string workspaceId,
        string tool,
        string outcome,
        string ts,
        long durationMs,
        string? errorKind = null,
        string? errorMessage = null,
        string? errorDetail = null,
        string? op = null,
        long? resultCount = null,
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
                (id, ts, tool, op, workspace_id, workspace_root, duration_ms, outcome, error_kind,
                 error_message, error_detail, result_count, est_tokens)
            VALUES
                ($id, $ts, $tool, $op, $ws, $root, $duration, $outcome, $error,
                 $message, $detail, $results, $tokens);
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
        cmd.Parameters.AddWithValue("$message", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$detail", (object?)errorDetail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$results", (object?)resultCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tokens", (object?)estTokens ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
