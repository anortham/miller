using Miller.Server.Telemetry;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the soft-budget evaluator (M7 decision-4): a pure, per-tool latency-ms + est-tokens threshold check.
/// Warn-only by policy — this component only DETECTS breaches; the central filter logs them. The slow/fat tools
/// (context/impact/edit) carry higher latency budgets than the lean ones (search/inspect/workspace); a tool with
/// no specific entry falls back to the default budget. Boundary rule (documented): at-threshold is NOT a breach
/// (the comparison is strictly greater-than), only strictly-over the limit is.
/// </summary>
public sealed class SoftBudgetsTests
{
    private static readonly SoftBudgets Budgets = SoftBudgets.Default;

    [Fact]
    public void Evaluate_UnderBothThresholds_NoBreach()
    {
        // 'search' is a lean tool. Comfortably under both its latency and token budgets.
        var budget = Budgets.For("search");
        var breaches = Budgets.Evaluate("search", durationMs: budget.LatencyMs - 1, estTokens: budget.EstTokens - 1);
        Assert.Empty(breaches);
    }

    [Fact]
    public void Evaluate_AtThreshold_IsNotABreach()
    {
        // Boundary: actual == limit is allowed (strictly greater-than only). Documented contract.
        var budget = Budgets.For("search");
        var breaches = Budgets.Evaluate("search", durationMs: budget.LatencyMs, estTokens: budget.EstTokens);
        Assert.Empty(breaches);
    }

    [Fact]
    public void Evaluate_OverLatencyOnly_BreachesLatencyDimensionOnly()
    {
        var budget = Budgets.For("search");
        var breaches = Budgets.Evaluate("search", durationMs: budget.LatencyMs + 1, estTokens: budget.EstTokens);

        var breach = Assert.Single(breaches);
        Assert.Equal(BudgetDimension.Latency, breach.Dimension);
        Assert.Equal(budget.LatencyMs + 1, breach.Actual);
        Assert.Equal(budget.LatencyMs, breach.Limit);
    }

    [Fact]
    public void Evaluate_OverTokensOnly_BreachesTokenDimensionOnly()
    {
        var budget = Budgets.For("search");
        var breaches = Budgets.Evaluate("search", durationMs: budget.LatencyMs, estTokens: budget.EstTokens + 1);

        var breach = Assert.Single(breaches);
        Assert.Equal(BudgetDimension.EstTokens, breach.Dimension);
        Assert.Equal(budget.EstTokens + 1, breach.Actual);
        Assert.Equal(budget.EstTokens, breach.Limit);
    }

    [Fact]
    public void Evaluate_OverBothThresholds_BreachesBothDimensions()
    {
        var budget = Budgets.For("inspect");
        var breaches = Budgets.Evaluate("inspect", durationMs: budget.LatencyMs + 100, estTokens: budget.EstTokens + 100);

        Assert.Equal(2, breaches.Count);
        Assert.Contains(breaches, b => b.Dimension == BudgetDimension.Latency);
        Assert.Contains(breaches, b => b.Dimension == BudgetDimension.EstTokens);
    }

    [Fact]
    public void Default_GivesSlowTools_HigherLatencyBudget_ThanLeanTools()
    {
        // The plan's premise: context/impact/edit are slow/fat; search/inspect/workspace are lean. A budgeted
        // slow tool must allow more latency than a lean one, or the warnings would be noise.
        Assert.True(Budgets.For("context").LatencyMs > Budgets.For("search").LatencyMs);
        Assert.True(Budgets.For("impact").LatencyMs > Budgets.For("inspect").LatencyMs);
        Assert.True(Budgets.For("edit").LatencyMs > Budgets.For("workspace").LatencyMs);
    }

    [Fact]
    public void For_UnknownTool_FallsBackToTheDefaultBudget()
    {
        // A tool with no specific entry uses the default budget (so a future/unlisted tool is still bounded).
        var unknown = Budgets.For("totally-unregistered-tool");
        Assert.Equal(SoftBudgets.Default.DefaultBudget.LatencyMs, unknown.LatencyMs);
        Assert.Equal(SoftBudgets.Default.DefaultBudget.EstTokens, unknown.EstTokens);
    }

    [Fact]
    public void Evaluate_DefaultTool_OverBothThresholds_BreachesBoth()
    {
        // Same over/under logic applies to a tool routed through the default budget, not just budgeted tools.
        var budget = SoftBudgets.Default.DefaultBudget;
        var breaches = Budgets.Evaluate(
            "totally-unregistered-tool", durationMs: budget.LatencyMs + 1, estTokens: budget.EstTokens + 1);

        Assert.Equal(2, breaches.Count);
        Assert.Contains(breaches, b => b.Dimension == BudgetDimension.Latency);
        Assert.Contains(breaches, b => b.Dimension == BudgetDimension.EstTokens);
    }

    [Fact]
    public void Evaluate_IsCaseInsensitive_OnToolName()
    {
        // Tool names arrive lower-case from MCP, but the lookup must be robust to casing so a budgeted tool is
        // never silently demoted to the (looser) default budget by a casing mismatch.
        Assert.Equal(Budgets.For("context").LatencyMs, Budgets.For("CONTEXT").LatencyMs);
    }
}
