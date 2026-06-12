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
        Assert.Contains("every 2s", html);
        Assert.Contains("data-ts=\"2026-06-12T10:00:00.000Z\"", html);
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
    }

    [Fact]
    public async Task Shells_IncludeRelativeTimeScript()
    {
        var index = new DashboardWorkspaceIndex(Array.Empty<DashboardWorkspaceIndexEntry>(), 0, 0, 0, 0);
        string landingHtml = await RenderComponentAsync<WorkspacesShell>(new Dictionary<string, object?>
        {
            ["Index"] = index,
        });

        Assert.Contains("updateRelativeTimes", landingHtml);

        var snapshot = new DashboardSnapshot(
            Array.Empty<DashboardWorkspaceRow>(),
            new DashboardTelemetrySummary("ws-a", [], 0, null, null, []),
            "ws-a");
        string workspaceHtml = await RenderComponentAsync<WorkspaceShell>(new Dictionary<string, object?>
        {
            ["Snapshot"] = snapshot,
        });

        Assert.Contains("updateRelativeTimes", workspaceHtml);
    }

    [Fact]
    public void DashboardHost_ServesActivityRoutes()
    {
        string program = File.ReadAllText(Path.Combine(
            Miller.Tests.ScaleTestSupport.RepoRoot(),
            "src",
            "Miller.Dashboard",
            "Program.cs"));

        Assert.Contains("MapGet(\"/fragments/activity\"", program, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/activity.json\"", program, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/fragments/refresh\"", program, StringComparison.Ordinal);
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
            "ready", null, 1, 1, 1, 100, 42, "2026-06-12T09:00:00Z", "current",
            Array.Empty<DashboardLanguageStat>(), Array.Empty<DashboardSymbolKindStat>());

        string html = await RenderComponentAsync<WorkspaceDetailPanel>(new Dictionary<string, object?>
        {
            ["Facts"] = facts,
        });

        Assert.Contains("hx-post=\"/fragments/refresh?workspace_id=ws-a\"", html);
        Assert.Contains("id=\"refresh-status\"", html);
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
    }

    [Fact]
    public void DashboardHost_ServesVendoredFontAssets()
    {
        string dashboardRoot = Path.Combine(
            Miller.Tests.ScaleTestSupport.RepoRoot(),
            "src",
            "Miller.Dashboard");
        string program = File.ReadAllText(Path.Combine(dashboardRoot, "Program.cs"));
        string css = File.ReadAllText(Path.Combine(dashboardRoot, "wwwroot", "dashboard.css"));

        Assert.Contains("/fonts/archivo-latin.woff2", program, StringComparison.Ordinal);
        Assert.Contains("/fonts/jetbrains-mono-latin.woff2", program, StringComparison.Ordinal);
        Assert.Contains("@font-face", css, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dashboardRoot, "wwwroot", "fonts", "archivo-latin.woff2")));
        Assert.True(File.Exists(Path.Combine(dashboardRoot, "wwwroot", "fonts", "jetbrains-mono-latin.woff2")));
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
                 result_count, est_tokens)
            VALUES
                ($id, $ts, $tool, $op, $ws, $root, $duration, $outcome, $error, $results, $tokens);
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
        cmd.Parameters.AddWithValue("$results", (object?)resultCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tokens", (object?)estTokens ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
