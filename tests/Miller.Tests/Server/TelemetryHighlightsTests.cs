using Miller.Server.Telemetry;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the two pure selectors the compact status telemetry line reads: the BUSIEST tool (most calls) and the
/// SLOWEST tool (highest p95 among tools with enough calls to trust). They are separate questions — the old line
/// answered "most calls" under a "top" label, which readers parsed as "the slow one".
/// </summary>
public sealed class TelemetryHighlightsTests
{
    private static ToolStat Stat(string tool, long calls, long p95Ms) =>
        new(tool, calls, AvgMs: p95Ms / 2.0, P95Ms: p95Ms, MaxMs: p95Ms, ErrorCount: 0, SumEstTokens: 0);

    [Fact]
    public void Busiest_PicksTheMostCalledTool_EvenWhenItIsTheFastest()
    {
        ToolStat[] tools = [Stat("search", 400, 191), Stat("inspect", 40, 8953)];

        Assert.Equal("search", TelemetryHighlights.Busiest(tools)!.Value.Tool);
    }

    [Fact]
    public void Busiest_BreaksACallTieByP95ThenName()
    {
        ToolStat[] tools = [Stat("trace", 10, 100), Stat("context", 10, 900), Stat("alpha", 10, 900)];

        Assert.Equal("alpha", TelemetryHighlights.Busiest(tools)!.Value.Tool);
    }

    [Fact]
    public void Busiest_ReturnsNull_WhenNoToolWasRecorded()
    {
        Assert.Null(TelemetryHighlights.Busiest([]));
    }

    [Fact]
    public void Slowest_PicksTheHighestP95AmongToolsThatMeetTheCallFloor()
    {
        ToolStat[] tools = [Stat("search", 400, 191), Stat("inspect", 40, 8953), Stat("trace", 12, 2274)];

        Assert.Equal("inspect", TelemetryHighlights.Slowest(tools)!.Value.Tool);
    }

    [Fact]
    public void Slowest_IgnoresAToolBelowTheCallFloor_SoOneOutlierNeverWins()
    {
        ToolStat[] tools =
        [
            Stat("search", 400, 191),
            Stat("edit", TelemetryHighlights.SlowestMinimumCalls - 1, 99_999),
        ];

        Assert.Equal("search", TelemetryHighlights.Slowest(tools)!.Value.Tool);
    }

    [Fact]
    public void Slowest_ReturnsNull_WhenNoToolMeetsTheCallFloor()
    {
        ToolStat[] tools = [Stat("edit", 1, 99_999), Stat("trace", 2, 5_000)];

        Assert.Null(TelemetryHighlights.Slowest(tools));
    }

    [Fact]
    public void Slowest_BreaksAP95TieByCallsThenName()
    {
        ToolStat[] tools = [Stat("trace", 10, 900), Stat("context", 30, 900), Stat("alpha", 30, 900)];

        Assert.Equal("alpha", TelemetryHighlights.Slowest(tools)!.Value.Tool);
    }
}
