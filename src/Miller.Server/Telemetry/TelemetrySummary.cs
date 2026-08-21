namespace Miller.Server.Telemetry;

/// <summary>
/// An aggregated read of the append-only <c>tool_telemetry</c> ledger (M7 decision-5) — the per-tool breakdown
/// the <c>workspace</c> status surfaces (julie's tool-breakdown screen). Produced by
/// <see cref="TelemetryLedger.Summarize"/>. Rendering into compact/json is a pure formatter
/// (<see cref="Tools.TelemetryRender"/>), keeping the SQL thin.
/// </summary>
/// <param name="Tools">Per-tool stats, one entry per distinct tool in the window.</param>
/// <param name="TotalCalls">Total recorded rows across all tools.</param>
/// <param name="WindowStartTs">The earliest row timestamp (ISO-8601 UTC), or null when there are no rows.</param>
/// <param name="WindowEndTs">The latest row timestamp (ISO-8601 UTC), or null when there are no rows.</param>
/// <param name="DroppedWrites">Rows that failed to persist and were swallowed (the ledger's drop-rate KPI).</param>
public readonly record struct TelemetrySummary(
    IReadOnlyList<ToolStat> Tools,
    long TotalCalls,
    string? WindowStartTs,
    string? WindowEndTs,
    long DroppedWrites)
{
    /// <summary>
    /// The rolling window, in days, the rows were selected from; null when the summary covers every retained row.
    /// A windowed figure MUST be rendered with its window named — an unlabelled p95 reads as lifetime behaviour.
    /// </summary>
    public int? WindowDays { get; init; }

    /// <summary>An all-zero summary (no rows). Used for the empty-ledger and disposed-ledger paths.</summary>
    public static TelemetrySummary Empty { get; } =
        new(Array.Empty<ToolStat>(), TotalCalls: 0, WindowStartTs: null, WindowEndTs: null, DroppedWrites: 0);
}

/// <summary>
/// One tool's aggregated telemetry (M7 decision-5). <see cref="P95Ms"/> is computed per tool by an ordered
/// query (<c>ORDER BY duration_ms LIMIT 1 OFFSET floor((count-1)*0.95)</c>) because SQLite has no PERCENTILE
/// function; on small samples this is the nearest-rank p95 (e.g. a single row's p95 is its own duration).
/// </summary>
/// <param name="Tool">The tool name (grouping key).</param>
/// <param name="Calls">Number of recorded calls.</param>
/// <param name="AvgMs">Mean call latency in milliseconds.</param>
/// <param name="P95Ms">Nearest-rank 95th-percentile latency in milliseconds.</param>
/// <param name="MaxMs">Maximum call latency in milliseconds.</param>
/// <param name="ErrorCount">Calls whose outcome was <c>error</c>.</param>
/// <param name="SumEstTokens">Sum of est_tokens across the calls (null token rows count as 0).</param>
public readonly record struct ToolStat(
    string Tool,
    long Calls,
    double AvgMs,
    long P95Ms,
    long MaxMs,
    long ErrorCount,
    long SumEstTokens);
