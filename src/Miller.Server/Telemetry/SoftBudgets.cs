namespace Miller.Server.Telemetry;

/// <summary>
/// The dimension of a soft-budget breach (M7 decision-4).
/// </summary>
public enum BudgetDimension
{
    /// <summary>Wall-clock call latency, in milliseconds.</summary>
    Latency,

    /// <summary>Estimated returned tokens (the north-star cost KPI).</summary>
    EstTokens,
}

/// <summary>
/// One per-tool budget: the latency ceiling (ms) and the estimated-token ceiling. A value is the INCLUSIVE
/// limit — actual == limit is allowed; only strictly-over is a breach (see <see cref="SoftBudgets.Evaluate"/>).
/// </summary>
/// <param name="LatencyMs">The inclusive latency ceiling in milliseconds.</param>
/// <param name="EstTokens">The inclusive estimated-token ceiling.</param>
public readonly record struct ToolBudget(long LatencyMs, long EstTokens);

/// <summary>
/// One detected budget overage: which dimension, the actual value, and the limit it exceeded. The actual is
/// strictly greater than the limit (the boundary is allowed).
/// </summary>
/// <param name="Dimension">The breached dimension.</param>
/// <param name="Actual">The measured value (strictly greater than <paramref name="Limit"/>).</param>
/// <param name="Limit">The budget limit that was exceeded.</param>
public readonly record struct Breach(BudgetDimension Dimension, long Actual, long Limit);

/// <summary>
/// Per-tool soft budgets for latency + estimated tokens (M7 decision-4). PURE: no logging, no I/O — it only
/// detects breaches; the ONE central <see cref="TelemetryCallToolFilter"/> logs a Serilog WARN for each.
/// Miller starts WARN-only (eros owns hard gates), so a breach never blocks or fails a call.
/// <para>
/// Defaults rationale: the lean tools (search/inspect/workspace) are in-memory index reads or small admin calls
/// — a 500ms latency budget flags anything pathological. The slow/fat tools earn higher ceilings because they
/// do real DB span reads and assemble larger payloads: <c>context</c> stitches a neighbourhood (1200ms),
/// <c>impact</c> walks the reference graph (1500ms), and <c>edit</c> takes the writer lock + reindexes inline
/// (2000ms). Token ceilings follow the same shape (lean tools return compact result lists; context/impact/edit
/// return bodies/graphs). A tool with no specific entry falls back to <see cref="DefaultBudget"/> so a
/// future/unlisted tool is still bounded rather than unmonitored. These are warn thresholds, not SLAs — tune
/// from the ledger's own breakdown once real traffic accrues.
/// </para>
/// </summary>
public sealed class SoftBudgets
{
    private readonly IReadOnlyDictionary<string, ToolBudget> _perTool;

    /// <summary>The budget applied to any tool not present in the per-tool table.</summary>
    public ToolBudget DefaultBudget { get; }

    /// <summary>
    /// Construct with an explicit per-tool table + a default (used for tools absent from the table). Tool names
    /// are matched case-insensitively (MCP delivers them lower-case, but the lookup is robust to casing so a
    /// budgeted tool is never silently demoted to the looser default by a casing mismatch).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="perTool"/> is null.</exception>
    public SoftBudgets(IReadOnlyDictionary<string, ToolBudget> perTool, ToolBudget defaultBudget)
    {
        ArgumentNullException.ThrowIfNull(perTool);
        // Copy into a case-insensitive dictionary so callers cannot mutate the table and casing never matters.
        _perTool = new Dictionary<string, ToolBudget>(perTool, StringComparer.OrdinalIgnoreCase);
        DefaultBudget = defaultBudget;
    }

    /// <summary>
    /// The production defaults (see the type-level rationale). Registered as a DI singleton in Program.cs.
    /// </summary>
    public static SoftBudgets Default { get; } = new(
        new Dictionary<string, ToolBudget>(StringComparer.OrdinalIgnoreCase)
        {
            // Lean tools: in-memory index reads / small admin calls.
            ["search"] = new(LatencyMs: 500, EstTokens: 4_000),
            ["inspect"] = new(LatencyMs: 500, EstTokens: 4_000),
            ["workspace"] = new(LatencyMs: 500, EstTokens: 4_000),
            // Slow/fat tools: DB span reads + graph walks + larger payloads.
            ["context"] = new(LatencyMs: 1_200, EstTokens: 8_000),
            ["impact"] = new(LatencyMs: 1_500, EstTokens: 8_000),
            ["edit"] = new(LatencyMs: 2_000, EstTokens: 6_000),
        },
        // Default for an unlisted tool: lean-tool latency, a generous token ceiling so it is bounded but not noisy.
        defaultBudget: new(LatencyMs: 1_000, EstTokens: 8_000));

    /// <summary>
    /// The budget for <paramref name="tool"/> — its specific entry, or <see cref="DefaultBudget"/> if none.
    /// </summary>
    public ToolBudget For(string tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return _perTool.TryGetValue(tool, out var budget) ? budget : DefaultBudget;
    }

    /// <summary>
    /// Evaluate one call against its budget. Returns one <see cref="Breach"/> per exceeded dimension (0, 1, or
    /// 2). Boundary rule: actual == limit is NOT a breach (strictly greater-than only) — documented so a tool
    /// sitting exactly on its ceiling does not emit a warning every call. Pure: no logging, no I/O.
    /// </summary>
    /// <param name="tool">The tool name (case-insensitive).</param>
    /// <param name="durationMs">The measured call latency in milliseconds.</param>
    /// <param name="estTokens">The measured estimated returned tokens.</param>
    public IReadOnlyList<Breach> Evaluate(string tool, long durationMs, long estTokens)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var budget = For(tool);

        // Most calls breach nothing; allocate the list only when there is at least one breach to report.
        List<Breach>? breaches = null;
        if (durationMs > budget.LatencyMs)
            (breaches ??= new List<Breach>(2)).Add(new Breach(BudgetDimension.Latency, durationMs, budget.LatencyMs));
        if (estTokens > budget.EstTokens)
            (breaches ??= new List<Breach>(2)).Add(new Breach(BudgetDimension.EstTokens, estTokens, budget.EstTokens));

        return breaches ?? (IReadOnlyList<Breach>)Array.Empty<Breach>();
    }
}
