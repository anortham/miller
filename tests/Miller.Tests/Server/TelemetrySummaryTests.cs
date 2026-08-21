using Microsoft.Data.Sqlite;
using Miller.Dashboard;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Contract pin for the telemetry aggregation read path (M7 decision-5): <see cref="TelemetryLedger.Summarize"/>
/// rolls the append-only <c>tool_telemetry</c> rows into a per-tool breakdown (count / avg / p95 / max /
/// error_count / sum_est_tokens) plus the overall call count, the time window, and dropped-writes. Runs against
/// a REAL temp <c>telemetry.db</c> built via the ledger's own write/test helpers — Microsoft.Data.Sqlite on a
/// temp file is fast, so this stays in the default suite (mirrors <c>TelemetryLedgerTests</c>, which is not
/// Scale).
/// </summary>
public sealed class TelemetrySummaryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public TelemetrySummaryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "telemetry.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Insert one fully-specified row directly (bypasses the prepared INSERT to control duration/tokens).
    /// <paramref name="workspaceId"/> stamps the row's <c>workspace_id</c> — the shared ledger is scoped by it,
    /// so it defaults to the <c>ws1</c> id the tests open the ledger with (rows must match to be summarised).
    /// </summary>
    private void InsertRow(string tool, long durationMs, string outcome, long? estTokens, string ts,
        string workspaceId = "ws1")
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tool_telemetry (id, ts, tool, workspace_id, duration_ms, outcome, est_tokens) " +
            "VALUES ($id, $ts, $tool, $ws, $dur, $outcome, $est);";
        cmd.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$dur", durationMs);
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$est", (object?)estTokens ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void InsertRows(string tool, IEnumerable<(long DurationMs, string Timestamp)> rows)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        c.Open();
        using var transaction = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "INSERT INTO tool_telemetry (id, ts, tool, workspace_id, duration_ms, outcome, est_tokens) " +
            "VALUES ($id, $ts, $tool, 'ws1', $dur, 'ok', 1);";
        SqliteParameter id = cmd.Parameters.Add("$id", SqliteType.Text);
        SqliteParameter ts = cmd.Parameters.Add("$ts", SqliteType.Text);
        cmd.Parameters.AddWithValue("$tool", tool);
        SqliteParameter duration = cmd.Parameters.Add("$dur", SqliteType.Integer);

        foreach ((long durationMs, string timestamp) in rows)
        {
            id.Value = Guid.CreateVersion7().ToString();
            ts.Value = timestamp;
            duration.Value = durationMs;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    [Fact]
    public void Summarize_EmptyLedger_ReturnsEmptySummary()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
        var summary = ledger.Summarize();

        Assert.Empty(summary.Tools);
        Assert.Equal(0, summary.TotalCalls);
        Assert.Null(summary.WindowStartTs);
        Assert.Null(summary.WindowEndTs);
        Assert.Equal(0, summary.DroppedWrites);
    }

    [Fact]
    public void Summarize_AggregatesPerTool_Counts_Avg_Max_ErrorCount_SumTokens()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
        // search: 3 rows. durations 100/200/300 → avg 200, max 300. one error. tokens 10+20+30 = 60.
        InsertRow("search", 100, "ok", 10, "2026-05-01T00:00:00.000Z");
        InsertRow("search", 200, "error", 20, "2026-05-01T00:00:01.000Z");
        InsertRow("search", 300, "empty", 30, "2026-05-01T00:00:02.000Z");
        // inspect: 1 row. duration 50, no error, tokens null (contributes 0 to the sum).
        InsertRow("inspect", 50, "ok", null, "2026-05-01T00:00:03.000Z");

        var summary = ledger.Summarize();

        Assert.Equal(4, summary.TotalCalls);
        Assert.Equal(2, summary.Tools.Count);

        var search = Assert.Single(summary.Tools, t => t.Tool == "search");
        Assert.Equal(3, search.Calls);
        Assert.Equal(200d, search.AvgMs, precision: 6);
        Assert.Equal(300, search.MaxMs);
        Assert.Equal(1, search.ErrorCount);
        Assert.Equal(60, search.SumEstTokens);

        var inspect = Assert.Single(summary.Tools, t => t.Tool == "inspect");
        Assert.Equal(1, inspect.Calls);
        Assert.Equal(50d, inspect.AvgMs, precision: 6);
        Assert.Equal(50, inspect.MaxMs);
        Assert.Equal(0, inspect.ErrorCount);
        Assert.Equal(0, inspect.SumEstTokens); // null est_tokens sum to 0, not null
    }

    [Fact]
    public void Summarize_ScopesToThisWorkspace_ExcludingOtherWorkspacesRows()
    {
        // The telemetry DB is now machine-global (one shared file across workspaces). A per-workspace status view
        // must report ONLY this workspace's rows, not the whole machine's — Summarize scopes to the ledger's id.
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
        InsertRow("search", 100, "ok", 10, "2026-05-01T00:00:00.000Z", workspaceId: "ws1");
        InsertRow("search", 200, "ok", 20, "2026-05-01T00:00:01.000Z", workspaceId: "ws1");
        // Rows from a DIFFERENT workspace sharing the same ledger file must NOT leak into ws1's summary.
        InsertRow("search", 999, "error", 500, "2026-05-01T00:00:02.000Z", workspaceId: "ws2");
        InsertRow("inspect", 999, "ok", 500, "2026-05-01T00:00:03.000Z", workspaceId: "ws2");

        var summary = ledger.Summarize();

        Assert.Equal(2, summary.TotalCalls);                 // only ws1's two rows
        var search = Assert.Single(summary.Tools);           // ws2's 'inspect' tool is absent entirely
        Assert.Equal("search", search.Tool);
        Assert.Equal(2, search.Calls);
        Assert.Equal(200, search.MaxMs);                     // ws2's 999ms row excluded from max
        Assert.Equal(0, search.ErrorCount);                  // ws2's error excluded
        Assert.Equal(30, search.SumEstTokens);               // 10 + 20, not ws2's 500
    }

    [Fact]
    public void SummarizeForWorkspace_ReadsRequestedWorkspaceInsteadOfLedgerWorkspace()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
        InsertRow("current-search", 100, "ok", 10, "2026-05-01T00:00:00.000Z", workspaceId: "ws1");
        InsertRow("target-search", 200, "ok", 20, "2026-05-01T00:00:01.000Z", workspaceId: "ws2");

        var summary = ledger.SummarizeForWorkspace("ws2");

        Assert.Equal(1, summary.TotalCalls);
        var stat = Assert.Single(summary.Tools);
        Assert.Equal("target-search", stat.Tool);
        Assert.Equal(200, stat.MaxMs);
    }

    // The headline p95 the status line shows must describe RECENT behaviour. Over the full 30-day retention one
    // bad day inflates it for a month, and the cause is often already-retired code.
    [Fact]
    public void SummarizeRecent_ExcludesRowsOlderThanTheWindow()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1", clock: clock);
        InsertRow("search", 9000, "error", 10, "2026-08-01T00:00:00.000Z"); // 20 days old
        InsertRow("search", 100, "ok", 20, "2026-08-20T00:00:00.000Z");     // 1 day old

        TelemetrySummary recent = ledger.SummarizeRecent(7);

        Assert.Equal(1, recent.TotalCalls);
        Assert.Equal(7, recent.WindowDays);
        ToolStat search = Assert.Single(recent.Tools);
        Assert.Equal(1, search.Calls);
        Assert.Equal(100, search.P95Ms);
        Assert.Equal(100, search.MaxMs);
        Assert.Equal(0, search.ErrorCount);
        Assert.Equal(20, search.SumEstTokens);
        Assert.Equal("2026-08-20T00:00:00.000Z", recent.WindowStartTs);
    }

    [Fact]
    public void SummarizeRecent_WindowRollsForwardWithTheClock()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1", clock: clock);
        InsertRow("search", 100, "ok", 20, "2026-08-20T00:00:00.000Z");

        Assert.Equal(1, ledger.SummarizeRecent(7).TotalCalls);

        clock.Now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        TelemetrySummary rolled = ledger.SummarizeRecent(7);
        Assert.Equal(0, rolled.TotalCalls);
        Assert.Empty(rolled.Tools);
        Assert.Null(rolled.WindowStartTs);
    }

    [Fact]
    public void Summarize_WithoutAWindow_StaysLifetimeWideAndReportsNoWindowDays()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1", clock: clock);
        InsertRow("search", 9000, "error", 10, "2026-08-01T00:00:00.000Z");
        InsertRow("search", 100, "ok", 20, "2026-08-20T00:00:00.000Z");

        TelemetrySummary lifetime = ledger.Summarize();

        Assert.Equal(2, lifetime.TotalCalls);
        Assert.Null(lifetime.WindowDays);
        Assert.Equal(9000, Assert.Single(lifetime.Tools).MaxMs);
    }

    [Fact]
    public void SummarizeRecentForWorkspace_ScopesToTheRequestedWorkspaceAndTheWindow()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1", clock: clock);
        InsertRow("target-search", 100, "ok", 10, "2026-08-20T00:00:00.000Z", workspaceId: "ws2");
        InsertRow("target-search", 900, "ok", 10, "2026-08-01T00:00:00.000Z", workspaceId: "ws2");
        InsertRow("current-search", 100, "ok", 10, "2026-08-20T00:00:00.000Z", workspaceId: "ws1");

        TelemetrySummary summary = ledger.SummarizeRecentForWorkspace("ws2", 7);

        Assert.Equal(1, summary.TotalCalls);
        Assert.Equal(7, summary.WindowDays);
        Assert.Equal("target-search", Assert.Single(summary.Tools).Tool);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SummarizeRecent_RejectsANonPositiveWindow(int windowDays)
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");

        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.SummarizeRecent(windowDays));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void SummarizeOutcomesForWorkspace_GroupsOkEmptyAndError()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
        InsertRow("search", 100, "ok", 10, "2026-05-01T00:00:00.000Z", workspaceId: "ws1");
        InsertRow("inspect", 200, "empty", 20, "2026-05-01T00:00:01.000Z", workspaceId: "ws1");
        InsertRow("trace", 300, "error", 30, "2026-05-01T00:00:02.000Z", workspaceId: "ws1");
        InsertRow("other-search", 400, "error", 40, "2026-05-01T00:00:03.000Z", workspaceId: "ws2");

        TelemetryHealthFacts facts = ledger.SummarizeOutcomesForWorkspace("ws1");

        Assert.Equal(1, facts.OkCount);
        Assert.Equal(1, facts.EmptyCount);
        Assert.Equal(1, facts.ErrorCount);
        Assert.Equal(3, facts.TotalCalls);
    }

    [Fact]
    public void Summarize_P95_UsesDocumentedOffset_OnAKnownDistribution()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
        // 100 rows, durations 1..100. p95 offset = floor((100-1)*0.95) = floor(94.05) = 94 → the 95th value
        // when ordered ascending (0-based offset 94) = 95.
        InsertRows("search", Enumerable.Range(1, 100)
            .Select(i => ((long)i, $"2026-05-01T00:00:{i:00}.000Z")));

        var summary = ledger.Summarize();
        var search = Assert.Single(summary.Tools);
        Assert.Equal(95, search.P95Ms);
        Assert.Equal(100, search.MaxMs);
    }

    [Fact]
    public void Summarize_P95_SingleRow_IsThatRowsDuration()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
        InsertRow("search", 42, "ok", 1, "2026-05-01T00:00:00.000Z");

        var search = Assert.Single(ledger.Summarize().Tools);
        Assert.Equal(42, search.P95Ms); // offset floor(0*0.95)=0 → the only row
        Assert.Equal(42, search.MaxMs);
    }

    [Fact]
    public void Summarize_Window_IsMinAndMaxTimestamp()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
        InsertRow("search", 1, "ok", 1, "2026-05-01T08:00:00.000Z");
        InsertRow("inspect", 1, "ok", 1, "2026-05-01T12:30:00.000Z");
        InsertRow("search", 1, "ok", 1, "2026-05-01T10:00:00.000Z");

        var summary = ledger.Summarize();
        Assert.Equal("2026-05-01T08:00:00.000Z", summary.WindowStartTs);
        Assert.Equal("2026-05-01T12:30:00.000Z", summary.WindowEndTs);
    }

    [Fact]
    public void Summarize_ReportsDroppedWrites()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");

        // Force a drop: a negative duration violates the CHECK and is swallowed + counted.
        var bad = new TelemetryRecord(
            Tool: "search", Op: null, WorkspaceId: "ws1", WorkspaceRoot: null,
            DurationMs: -1, Outcome: "ok", ErrorKind: null,
            ResultCount: null, BytesExamined: 0, BytesReturned: 0, SourceBytes: 0,
            EstTokens: null, IndexFresh: null, TargetHash: null, MetadataJson: "{}");
        ledger.Record(in bad);

        Assert.Equal(ledger.DroppedWrites, ledger.Summarize().DroppedWrites);
        Assert.True(ledger.Summarize().DroppedWrites >= 1);
    }

    [Fact]
    public void Summarize_AfterDispose_ReturnsEmptySummary_DoesNotThrow()
    {
        var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
        InsertRow("search", 1, "ok", 1, "2026-05-01T00:00:00.000Z");
        ledger.Dispose();

        var summary = ledger.Summarize(); // best-effort: disposed → empty, never throws
        Assert.Empty(summary.Tools);
        Assert.Equal(0, summary.TotalCalls);
    }

    // A null WindowDays means "every retained row". The disposed path must not claim that about a windowed read,
    // or the field added to stop an unlabelled figure reading as lifetime behaviour does exactly that.
    [Fact]
    public void SummarizeRecent_AfterDispose_StillNamesTheWindow()
    {
        var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
        InsertRow("search", 1, "ok", 1, "2026-05-01T00:00:00.000Z");
        ledger.Dispose();

        TelemetrySummary summary = ledger.SummarizeRecent(7);

        Assert.Empty(summary.Tools);
        Assert.Equal(0, summary.TotalCalls);
        Assert.Equal(7, summary.WindowDays);
    }
}

/// <summary>
/// Contract pin for the DASHBOARD telemetry read path (<see cref="DashboardData.ReadTelemetrySummary"/>),
/// distinct from the server-side <see cref="TelemetryLedger.Summarize"/> above. These pin the exact P95
/// percentile semantics BEFORE the N+1 → single-pass rewrite (Task 5): p95 = the ascending-sorted duration at
/// 0-based index <c>floor((count-1)*0.95)</c> per tool, computed over the same telemetry window in one pass;
/// plus the recent-errors display-id resolution from the sibling registry (<c>workspaces.db</c>). Runs against
/// REAL temp <c>telemetry.db</c> + <c>workspaces.db</c> siblings — fast, so it stays in the default suite.
/// </summary>
public sealed class DashboardTelemetrySummaryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _telemetryDb;
    private readonly string _registryDb;

    public DashboardTelemetrySummaryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-dash-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _telemetryDb = Path.Combine(_dir, "telemetry.db");
        _registryDb = Path.Combine(_dir, "workspaces.db");
        using TelemetryLedger ledger = TelemetryLedger.Open(_telemetryDb, "ws-a", "/repo/test");
    }

    public void Dispose()
    {
        // The dashboard readers use Pooling=false, but clear defensively before deleting the SQLite files.
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Insert one telemetry row via direct SQL (the ledger's prepared INSERT does not let us pin ts/duration).
    /// Opening the ledger once first creates the <c>tool_telemetry</c> schema.
    /// </summary>
    private string InsertRow(
        string tool,
        long durationMs,
        string outcome,
        string ts,
        string workspaceId = "ws-a",
        string? errorKind = null,
        string? errorMessage = null,
        string? errorDetail = null,
        string? id = null)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _telemetryDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        string rowId = id ?? Guid.CreateVersion7().ToString();
        cmd.CommandText =
            "INSERT INTO tool_telemetry (id, ts, tool, workspace_id, duration_ms, outcome, error_kind, " +
            "error_message, error_detail) " +
            "VALUES ($id, $ts, $tool, $ws, $dur, $outcome, $errkind, $errmsg, $errdetail);";
        cmd.Parameters.AddWithValue("$id", rowId);
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$dur", durationMs);
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$errkind", (object?)errorKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errmsg", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errdetail", (object?)errorDetail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return rowId;
    }

    private void InsertRows(string tool, IEnumerable<(long DurationMs, string Timestamp)> rows)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _telemetryDb, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        c.Open();
        using var transaction = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "INSERT INTO tool_telemetry (id, ts, tool, workspace_id, duration_ms, outcome) " +
            "VALUES ($id, $ts, $tool, 'ws-a', $dur, 'ok');";
        SqliteParameter id = cmd.Parameters.Add("$id", SqliteType.Text);
        SqliteParameter ts = cmd.Parameters.Add("$ts", SqliteType.Text);
        cmd.Parameters.AddWithValue("$tool", tool);
        SqliteParameter duration = cmd.Parameters.Add("$dur", SqliteType.Integer);

        foreach ((long durationMs, string timestamp) in rows)
        {
            id.Value = Guid.CreateVersion7().ToString();
            ts.Value = timestamp;
            duration.Value = durationMs;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void SeedRegistry(string workspaceId, string displayId)
    {
        using var registry = WorkspaceRegistry.Open(_registryDb);
        registry.UpsertSeen(
            workspaceId,
            displayId,
            Path.Combine(_dir, displayId),
            Path.Combine(_dir, displayId, ".miller", "symbols.db"),
            WorkspaceRegistryState.Current,
            DateTimeOffset.Parse("2026-05-31T10:00:00Z"));
    }

    private DashboardToolStat ToolStat(string? scope, string tool) =>
        Assert.Single(DashboardData.ReadTelemetrySummary(_telemetryDb, scope).Tools, t => t.Tool == tool);

    // ---- P95 percentile semantics (pinned BEFORE the single-pass rewrite) ----

    [Fact]
    public void P95_KnownDistribution_Scoped_UsesFloorOffset()
    {
        // 100 rows durations 1..100. offset = floor((100-1)*0.95) = floor(94.05) = 94 → 0-based index 94 = 95.
        InsertRows("search", Enumerable.Range(1, 100)
            .Select(i => ((long)i, $"2026-05-01T00:{i / 60:00}:{i % 60:00}.000Z")));

        DashboardToolStat search = ToolStat("ws-a", "search");
        Assert.Equal(100, search.Calls);
        Assert.Equal(95, search.P95Ms);
        Assert.Equal(100, search.MaxMs);
    }

    [Fact]
    public void P95_KnownDistribution_MachineWide_MatchesScopedFormula()
    {
        // Exercises the allWorkspaces SQL branch. Same 1..100 distribution → same p95 = 95.
        InsertRows("search", Enumerable.Range(1, 100)
            .Select(i => ((long)i, $"2026-05-01T00:{i / 60:00}:{i % 60:00}.000Z")));

        DashboardToolStat search = ToolStat("all", "search");
        Assert.Equal(95, search.P95Ms);
        Assert.Equal(100, search.MaxMs);
    }

    [Fact]
    public void P95_SingleRow_IsThatRowsDuration()
    {
        InsertRow("search", 42, "ok", "2026-05-01T00:00:00.000Z");
        Assert.Equal(42, ToolStat("ws-a", "search").P95Ms); // offset floor(0*0.95)=0 → the only row
    }

    [Fact]
    public void P95_TwoRows_ReturnsLowerSample_PinsFloorRounding()
    {
        // The floor offset means 2 samples yield the LOWER value, not the higher. This odd-but-current
        // behavior must survive the rewrite byte-for-byte.
        InsertRow("search", 10, "ok", "2026-05-01T00:00:00.000Z");
        InsertRow("search", 90, "ok", "2026-05-01T00:00:01.000Z");
        Assert.Equal(10, ToolStat("ws-a", "search").P95Ms); // offset floor(1*0.95)=0 → sorted[0] = 10
    }

    [Fact]
    public void P95_AllEqualDurations_IsThatDuration()
    {
        InsertRows("search", Enumerable.Range(0, 5)
            .Select(i => (42L, $"2026-05-01T00:00:{i:00}.000Z")));
        Assert.Equal(42, ToolStat("ws-a", "search").P95Ms); // offset floor(4*0.95)=3 → sorted[3] = 42
    }

    [Fact]
    public void P95_ZeroDuration_IsPreservedNotTreatedAsMissing()
    {
        InsertRow("search", 0, "ok", "2026-05-01T00:00:00.000Z");
        Assert.Equal(0, ToolStat("ws-a", "search").P95Ms); // a real 0ms row, not the null→0 fallback
    }

    [Fact]
    public void P95_MultipleTools_UnevenCounts_ComputedIndependentlyInOnePass()
    {
        // tool-a: 3 rows 10/20/30 → offset floor(2*0.95)=1 → sorted[1] = 20
        InsertRow("tool-a", 10, "ok", "2026-05-01T00:00:00.000Z");
        InsertRow("tool-a", 20, "ok", "2026-05-01T00:00:01.000Z");
        InsertRow("tool-a", 30, "ok", "2026-05-01T00:00:02.000Z");
        // tool-b: 1 row 99 → offset 0 → 99
        InsertRow("tool-b", 99, "ok", "2026-05-01T00:00:03.000Z");
        // tool-c: 20 rows 1..20 → offset floor(19*0.95)=18 → sorted[18] = 19
        InsertRows("tool-c", Enumerable.Range(1, 20)
            .Select(i => ((long)i, $"2026-05-01T00:01:{i:00}.000Z")));

        Assert.Equal(20, ToolStat("ws-a", "tool-a").P95Ms);
        Assert.Equal(99, ToolStat("ws-a", "tool-b").P95Ms);
        Assert.Equal(19, ToolStat("ws-a", "tool-c").P95Ms);
    }

    [Fact]
    public void P95_MachineWide_PoolsAcrossWorkspacesLikeTheOldPerToolQuery()
    {
        // The machine-wide p95 pools every workspace's rows for a tool. tool durations 10,20,30,40,50 across
        // two workspaces → offset floor(4*0.95)=3 → sorted[3] = 40.
        InsertRow("search", 10, "ok", "2026-05-01T00:00:00.000Z", workspaceId: "ws-a");
        InsertRow("search", 30, "ok", "2026-05-01T00:00:01.000Z", workspaceId: "ws-a");
        InsertRow("search", 50, "ok", "2026-05-01T00:00:02.000Z", workspaceId: "ws-a");
        InsertRow("search", 20, "ok", "2026-05-01T00:00:03.000Z", workspaceId: "ws-b");
        InsertRow("search", 40, "ok", "2026-05-01T00:00:04.000Z", workspaceId: "ws-b");

        Assert.Equal(40, ToolStat("all", "search").P95Ms);
    }

    [Fact]
    public void EmptyWindow_NoRows_YieldsNoTools()
    {
        using (TelemetryLedger.Open(_telemetryDb, "ws-a", "/repo/test")) { }
        DashboardTelemetrySummary summary = DashboardData.ReadTelemetrySummary(_telemetryDb, "ws-a");
        Assert.Empty(summary.Tools);
        Assert.Equal(0, summary.TotalCalls);
    }

    // ---- recent-errors display-id resolution (B5 fix) ----

    [Fact]
    public void MachineWide_RecentErrors_ResolveRegisteredDisplayIds_NullForUnregistered()
    {
        SeedRegistry("ws-a", "alpha-abcd1234");
        InsertRow("search", 5, "error", "2026-05-31T10:00:00.000Z", workspaceId: "ws-a",
            errorKind: "InvalidOperationException");
        InsertRow("inspect", 6, "error", "2026-05-31T10:01:00.000Z", workspaceId: "ws-zzz",
            errorKind: "KeyNotFoundException");

        DashboardTelemetrySummary summary = DashboardData.ReadTelemetrySummary(_telemetryDb, "all", _registryDb);

        DashboardRecentError registered = Assert.Single(summary.RecentErrors, e => e.WorkspaceId == "ws-a");
        Assert.Equal("alpha-abcd1234", registered.WorkspaceDisplayId);
        DashboardRecentError unregistered = Assert.Single(summary.RecentErrors, e => e.WorkspaceId == "ws-zzz");
        Assert.Null(unregistered.WorkspaceDisplayId);
    }

    [Fact]
    public void Scoped_RecentErrors_ResolveRegisteredDisplayId()
    {
        SeedRegistry("ws-a", "alpha-abcd1234");
        InsertRow("search", 5, "error", "2026-05-31T10:00:00.000Z", workspaceId: "ws-a",
            errorKind: "InvalidOperationException");

        DashboardRecentError error = Assert.Single(
            DashboardData.ReadTelemetrySummary(_telemetryDb, "ws-a", _registryDb).RecentErrors);
        Assert.Equal("ws-a", error.WorkspaceId);
        Assert.Equal("alpha-abcd1234", error.WorkspaceDisplayId);
    }

    [Fact]
    public void NullRegistryDbPath_DisplayIdStaysNull_EvenWhenRegistryExists()
    {
        // Explicit path threading: a null registryDbPath yields null display ids even though a resolvable
        // registry sits right beside the telemetry DB — there is NO sibling-path guessing.
        SeedRegistry("ws-a", "alpha-abcd1234");
        InsertRow("search", 5, "error", "2026-05-31T10:00:00.000Z", workspaceId: "ws-a",
            errorKind: "InvalidOperationException");

        DashboardRecentError error = Assert.Single(
            DashboardData.ReadTelemetrySummary(_telemetryDb, "ws-a").RecentErrors); // registryDbPath omitted → null
        Assert.Equal("ws-a", error.WorkspaceId);
        Assert.Null(error.WorkspaceDisplayId);
    }

    [Fact]
    public void MissingRegistryFile_DisplayIdStaysNull()
    {
        // A registry path that does not exist degrades safely (ReadWorkspaces returns empty) → null display id.
        InsertRow("search", 5, "error", "2026-05-31T10:00:00.000Z", workspaceId: "ws-a",
            errorKind: "InvalidOperationException");

        DashboardRecentError error = Assert.Single(
            DashboardData.ReadTelemetrySummary(_telemetryDb, "ws-a", _registryDb).RecentErrors);
        Assert.Null(error.WorkspaceDisplayId);
    }
}
