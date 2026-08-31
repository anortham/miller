using System.Text.Json;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

public sealed class McpWorkspaceTargetPolicyTests
{
    [Fact]
    public void WorkspaceBoundTool_WithoutWorkspaceId_ReturnsStableRequiredDiagnostic()
    {
        McpWorkspaceTargetDecision decision = McpWorkspaceTargetPolicy.Evaluate("search", null);

        Assert.Equal(McpWorkspaceTargetKind.Missing, decision.Kind);
        Assert.Equal("workspace_id_required", decision.Diagnostic?.Code);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("primary")]
    [InlineData(" CURRENT ")]
    public void WorkspaceBoundTool_WithImplicitSelector_ReturnsStableRefusalDiagnostic(string selector)
    {
        McpWorkspaceTargetDecision decision = McpWorkspaceTargetPolicy.Evaluate(
            "inspect",
            Arguments(("workspace_id", selector)));

        Assert.Equal(McpWorkspaceTargetKind.Implicit, decision.Kind);
        Assert.Equal("implicit_workspace_selector_refused", decision.Diagnostic?.Code);
    }

    [Fact]
    public void WorkspaceBoundTool_WithRegisteredSelector_IsExplicitlyScoped()
    {
        McpWorkspaceTargetDecision decision = McpWorkspaceTargetPolicy.Evaluate(
            "context",
            Arguments(("workspace_id", "miller-main")));

        Assert.Equal(McpWorkspaceTargetKind.Explicit, decision.Kind);
        Assert.Null(decision.Diagnostic);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("open")]
    [InlineData("remove")]
    [InlineData("prune")]
    [InlineData("dashboard")]
    public void WorkspaceGlobalOperation_DoesNotRequireWorkspaceId(string operation)
    {
        McpWorkspaceTargetDecision decision = McpWorkspaceTargetPolicy.Evaluate(
            "workspace",
            Arguments(("operation", operation), ("path", "/tmp/other")));

        Assert.Equal(McpWorkspaceTargetKind.Unscoped, decision.Kind);
        Assert.Null(decision.Diagnostic);
    }

    [Fact]
    public void WorkspaceGlobalOperation_WithImplicitSelectorIsStillRefused()
    {
        McpWorkspaceTargetDecision decision = McpWorkspaceTargetPolicy.Evaluate(
            "workspace",
            Arguments(("operation", "list"), ("workspace_id", "current")));

        Assert.Equal(McpWorkspaceTargetKind.Implicit, decision.Kind);
        Assert.Equal("implicit_workspace_selector_refused", decision.Diagnostic?.Code);
    }

    [Fact]
    public void WorkspaceStatus_StillRequiresAnExplicitWorkspaceId()
    {
        McpWorkspaceTargetDecision decision = McpWorkspaceTargetPolicy.Evaluate(
            "workspace",
            Arguments(("operation", "status")));

        Assert.Equal(McpWorkspaceTargetKind.Missing, decision.Kind);
        Assert.Equal("workspace_id_required", decision.Diagnostic?.Code);
    }

    [Fact]
    public void ContentSearch_AllIsTheReadOnlyRegisteredWorkspaceException()
    {
        McpWorkspaceTargetDecision decision = McpWorkspaceTargetPolicy.Evaluate(
            "content",
            Arguments(("operation", "search"), ("workspace_id", "all")));

        Assert.Equal(McpWorkspaceTargetKind.All, decision.Kind);
        Assert.Null(decision.Diagnostic);
    }

    [Fact]
    public void ContentRead_AllIsRefusedOutsideRegisteredWorkspaceSearch()
    {
        McpWorkspaceTargetDecision decision = McpWorkspaceTargetPolicy.Evaluate(
            "content",
            Arguments(("operation", "read"), ("workspace_id", "all")));

        Assert.Equal(McpWorkspaceTargetKind.Implicit, decision.Kind);
        Assert.Equal("implicit_workspace_selector_refused", decision.Diagnostic?.Code);
    }

    [Fact]
    public void UnknownTool_IsNotChangedByWorkspacePolicy()
    {
        McpWorkspaceTargetDecision decision = McpWorkspaceTargetPolicy.Evaluate(
            "pin_greet",
            Arguments(("workspace_id", "current")));

        Assert.Equal(McpWorkspaceTargetKind.Unscoped, decision.Kind);
        Assert.Null(decision.Diagnostic);
    }

    private static Dictionary<string, JsonElement> Arguments(params (string Name, object? Value)[] values) =>
        values.ToDictionary(
            static pair => pair.Name,
            static pair => JsonSerializer.SerializeToElement(pair.Value));
}
