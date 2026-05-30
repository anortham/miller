using System.Text.Json;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the PURE telemetry-summary renderer (M7 decision-5/6): given a <see cref="TelemetrySummary"/> it
/// produces a compact text table and a JSON document, deterministically and with no I/O. The
/// <c>workspace status</c> tool uses this so the SQL/aggregation stays separate from formatting.
/// </summary>
public sealed class TelemetryRenderTests
{
    private static readonly ToolStat Search = new(
        Tool: "search", Calls: 10, AvgMs: 123.4, P95Ms: 250, MaxMs: 400, ErrorCount: 1, SumEstTokens: 5000);
    private static readonly ToolStat Inspect = new(
        Tool: "inspect", Calls: 4, AvgMs: 12.0, P95Ms: 20, MaxMs: 30, ErrorCount: 0, SumEstTokens: 800);

    [Fact]
    public void Compact_EmptySummary_StatesNoTelemetry()
    {
        string text = TelemetryRender.Compact(TelemetrySummary.Empty);
        Assert.Contains("no telemetry", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Json_EmptySummary_HasZeroTotalsAndEmptyToolsArray()
    {
        using var doc = JsonDocument.Parse(TelemetryRender.Json(TelemetrySummary.Empty));
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("total_calls").GetInt64());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("tools").ValueKind);
        Assert.Equal(0, root.GetProperty("tools").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("window_start").ValueKind);
    }

    [Fact]
    public void Compact_SingleTool_ShowsTheToolRow_WithAllMetrics()
    {
        var summary = new TelemetrySummary(
            new[] { Search }, TotalCalls: 10,
            WindowStartTs: "2026-05-01T00:00:00.000Z", WindowEndTs: "2026-05-01T01:00:00.000Z", DroppedWrites: 2);

        string text = TelemetryRender.Compact(summary);
        Assert.Contains("search", text);
        Assert.Contains("10", text);   // calls
        Assert.Contains("250", text);  // p95
        Assert.Contains("400", text);  // max
        Assert.Contains("5000", text); // est tokens
        Assert.Contains("2", text);    // dropped writes surfaced
    }

    [Fact]
    public void Compact_MultiTool_RendersOneRowPerTool()
    {
        var summary = new TelemetrySummary(
            new[] { Search, Inspect }, TotalCalls: 14,
            WindowStartTs: "2026-05-01T00:00:00.000Z", WindowEndTs: "2026-05-01T02:00:00.000Z", DroppedWrites: 0);

        string text = TelemetryRender.Compact(summary);
        var lines = text.Split('\n');
        Assert.Contains(lines, l => l.Contains("search"));
        Assert.Contains(lines, l => l.Contains("inspect"));
    }

    [Fact]
    public void Json_MultiTool_RoundTripsEveryMetric()
    {
        var summary = new TelemetrySummary(
            new[] { Search, Inspect }, TotalCalls: 14,
            WindowStartTs: "2026-05-01T00:00:00.000Z", WindowEndTs: "2026-05-01T02:00:00.000Z", DroppedWrites: 3);

        using var doc = JsonDocument.Parse(TelemetryRender.Json(summary));
        var root = doc.RootElement;
        Assert.Equal(14, root.GetProperty("total_calls").GetInt64());
        Assert.Equal(3, root.GetProperty("dropped_writes").GetInt64());
        Assert.Equal("2026-05-01T00:00:00.000Z", root.GetProperty("window_start").GetString());
        Assert.Equal("2026-05-01T02:00:00.000Z", root.GetProperty("window_end").GetString());

        var tools = root.GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());

        var search = tools.EnumerateArray().Single(t => t.GetProperty("tool").GetString() == "search");
        Assert.Equal(10, search.GetProperty("calls").GetInt64());
        Assert.Equal(123.4, search.GetProperty("avg_ms").GetDouble(), precision: 6);
        Assert.Equal(250, search.GetProperty("p95_ms").GetInt64());
        Assert.Equal(400, search.GetProperty("max_ms").GetInt64());
        Assert.Equal(1, search.GetProperty("error_count").GetInt64());
        Assert.Equal(5000, search.GetProperty("est_tokens").GetInt64());

        var inspect = tools.EnumerateArray().Single(t => t.GetProperty("tool").GetString() == "inspect");
        Assert.Equal(4, inspect.GetProperty("calls").GetInt64());
        Assert.Equal(0, inspect.GetProperty("error_count").GetInt64());
    }
}
