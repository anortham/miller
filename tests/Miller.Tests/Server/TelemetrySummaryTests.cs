using Microsoft.Data.Sqlite;
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
    public void Summarize_P95_UsesDocumentedOffset_OnAKnownDistribution()
    {
        using var ledger = TelemetryLedger.Open(_dbPath, workspaceId: "ws1");
        // 100 rows, durations 1..100. p95 offset = floor((100-1)*0.95) = floor(94.05) = 94 → the 95th value
        // when ordered ascending (0-based offset 94) = 95.
        for (int i = 1; i <= 100; i++)
            InsertRow("search", i, "ok", 1, $"2026-05-01T00:00:{i:00}.000Z");

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
}
