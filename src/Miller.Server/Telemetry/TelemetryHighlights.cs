namespace Miller.Server.Telemetry;

/// <summary>
/// Pure selectors over an aggregated <see cref="TelemetrySummary"/>: the two questions the compact
/// <c>workspace status</c> telemetry line answers. "Which tool do I call most" and "which tool is slow" are
/// DIFFERENT questions — the line once reported only the most-called tool, under a <c>top=</c> label that read
/// as "the slow one", so a fast, heavily used tool looked like the latency problem.
/// </summary>
public static class TelemetryHighlights
{
    /// <summary>
    /// Rolling window, in days, the status line summarizes. Short enough that a single bad day ages out instead
    /// of inflating the headline p95 for the whole 30-day retention.
    /// </summary>
    public const int RecentWindowDays = 7;

    /// <summary>
    /// Calls a tool needs before its p95 may be reported as the slowest tool. A nearest-rank p95 over one or two
    /// samples IS that sample, so without a floor one cold call wins the label forever.
    /// </summary>
    public const int SlowestMinimumCalls = 5;

    /// <summary>
    /// The most-called tool, or null when nothing was recorded. Ties break by p95 then by name, so the choice is
    /// deterministic for a byte-identical render.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="tools"/> is null.</exception>
    public static ToolStat? Busiest(IReadOnlyList<ToolStat> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count == 0)
            return null;

        return tools
            .OrderByDescending(static tool => tool.Calls)
            .ThenByDescending(static tool => tool.P95Ms)
            .ThenBy(static tool => tool.Tool, StringComparer.Ordinal)
            .First();
    }

    /// <summary>
    /// The highest-p95 tool among those with at least <paramref name="minimumCalls"/> calls, or null when no tool
    /// reaches the floor. Ties break by calls then by name.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="tools"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumCalls"/> is negative.</exception>
    public static ToolStat? Slowest(IReadOnlyList<ToolStat> tools, int minimumCalls = SlowestMinimumCalls)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumCalls);

        List<ToolStat> eligible = [.. tools.Where(tool => tool.Calls >= minimumCalls)];
        if (eligible.Count == 0)
            return null;

        return eligible
            .OrderByDescending(static tool => tool.P95Ms)
            .ThenByDescending(static tool => tool.Calls)
            .ThenBy(static tool => tool.Tool, StringComparer.Ordinal)
            .First();
    }
}
