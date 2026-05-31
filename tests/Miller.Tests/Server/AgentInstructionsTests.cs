using Miller.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins <see cref="AgentInstructions.Load"/> — the embedded MCP server instructions wired onto
/// <c>McpServerOptions.ServerInstructions</c>. Guards that the resource is actually embedded (a dropped
/// EmbeddedResource in the csproj would otherwise ship a server with empty guidance) and that the content names
/// every tool the agent is told to prefer, so the doc cannot silently drift out of covering a tool.
/// </summary>
public sealed class AgentInstructionsTests
{
    [Fact]
    public void Load_ReturnsNonEmptyInstructions()
    {
        string instructions = AgentInstructions.Load();
        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.Contains("Search before reading", instructions); // the lead behavioral rule
    }

    [Theory]
    [InlineData("search")]
    [InlineData("inspect")]
    [InlineData("context")]
    [InlineData("trace")]
    [InlineData("impact")]
    [InlineData("edit")]
    [InlineData("workspace")]
    public void Load_DocumentsEveryTool(string toolName)
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("`" + toolName + "`", instructions);
    }

    [Theory]
    [InlineData("`workspace_id`")]
    [InlineData("`ensure_fresh`")]
    public void Load_DocumentsCrossWorkspaceReadParameters(string parameterName)
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains(parameterName, instructions);
    }
}
