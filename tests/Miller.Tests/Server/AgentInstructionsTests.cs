using System.ComponentModel;
using System.Reflection;
using Miller.Server;
using Miller.Server.Tools;
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
    private const int MaxServerInstructionsChars = 12_000;
    private const int MaxToolDescriptionChars = 900;
    private const int MaxParameterDescriptionChars = 250;

    [Fact]
    public void Load_ReturnsNonEmptyInstructions()
    {
        string instructions = AgentInstructions.Load();
        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.Contains("Search before reading", instructions); // the lead behavioral rule
    }

    [Fact]
    public void Load_StaysUnderClaudeCodeInstructionBudget()
    {
        string instructions = AgentInstructions.Load();
        int clientWorstCaseLength = instructions.ReplaceLineEndings("\r\n").Length;
        Assert.True(
            clientWorstCaseLength <= MaxServerInstructionsChars,
            $"Server instructions are {clientWorstCaseLength} chars after CRLF normalization; keep them under {MaxServerInstructionsChars} for MCP clients with instruction limits.");
    }

    [Fact]
    public void Load_PinsBehavioralAdoptionLanguage()
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("Reach for a Miller tool before a raw", instructions);
        Assert.Contains("Do not use `grep`/`find`/`rg` when a Miller tool fits", instructions);
        Assert.Contains("Do not read a whole file before `inspect`", instructions);
        Assert.Contains("refresh and retry before raw reads", instructions);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("inspect")]
    [InlineData("context")]
    [InlineData("trace")]
    [InlineData("impact")]
    [InlineData("edit")]
    [InlineData("todos")]
    [InlineData("content")]
    [InlineData("patterns")]
    [InlineData("workspace")]
    public void Load_DocumentsEveryTool(string toolName)
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("`" + toolName + "`", instructions);
    }

    [Theory]
    [InlineData("`workspace_id`")]
    [InlineData("`ensure_fresh`")]
    [InlineData("display ID")]
    public void Load_DocumentsCrossWorkspaceReadParameters(string parameterName)
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains(parameterName, instructions);
    }

    // The canonical inline search-mode enum must list `content` (the phase-3 mode the tool's [Description] and
    // SearchTool.ParseMode accept) so the doc can't drift back to `auto|text|symbol|file` while the tool supports
    // content/docs. `file|content` is the adjacency that proves content is IN the enum, not just the prose.
    [Fact]
    public void Load_SearchModeEnum_IncludesContentMode()
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("file|content", instructions);
        Assert.Contains("alias `docs`", instructions);
    }

    [Fact]
    public void Load_DocumentsRegionSearchAndHasDoc()
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("regions=comment|doc_comment|string_literal", instructions);
        Assert.Contains("MILLER_REGION_INDEX=0", instructions);
        Assert.Contains("has_doc", instructions);
    }

    [Fact]
    public void Load_DocumentsSubagentToolPrimer()
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("Subagent Dispatching", instructions);
        Assert.Contains("Code Intelligence Tools (use instead of Grep/Glob/Read)", instructions);
        Assert.Contains("Do NOT fall back to Glob/Read/Grep chains", instructions);
    }

    [Fact]
    public void Load_DocumentsDashboardLaunchWorkflow()
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains(
            "If the user asks to start, open, or show the Miller dashboard, call `workspace` with `operation=dashboard`",
            instructions);
        Assert.Contains("dashboard request is a tool operation, not a file-finding task", instructions);
        Assert.Contains("Do not search plugin cache directories for dashboard files", instructions);
    }

    [Fact]
    public void WorkspaceToolDescription_RoutesDashboardLaunchRequests()
    {
        MethodInfo method = ToolMethod<WorkspaceTool>(nameof(WorkspaceTool.Workspace));
        string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        Assert.Contains("dashboard/start/open/show requests", description);
        Assert.Contains("operation=dashboard", description);
    }

    [Fact]
    public void Load_DocumentsWebContentWorkflow()
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("miller-web-research", instructions);
        Assert.Contains("browser39", instructions);
        Assert.Contains("add_markdown", instructions);
        Assert.Contains("content_kind=web", instructions);
    }

    [Theory]
    [MemberData(nameof(ToolMethods))]
    public void ToolDescriptions_StayWithinClaudeCodeBudgets(MethodInfo method)
    {
        string methodName = $"{method.DeclaringType?.Name}.{method.Name}";
        string? description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.False(string.IsNullOrWhiteSpace(description), $"{methodName} is missing a tool description.");
        Assert.True(
            description!.Length <= MaxToolDescriptionChars,
            $"{methodName} description is {description.Length} chars; keep it under {MaxToolDescriptionChars}.");

        foreach (ParameterInfo parameter in method.GetParameters())
        {
            string? parameterDescription = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.False(
                string.IsNullOrWhiteSpace(parameterDescription),
                $"{methodName}.{parameter.Name} is missing a parameter description.");
            Assert.True(
                parameterDescription!.Length <= MaxParameterDescriptionChars,
                $"{methodName}.{parameter.Name} description is {parameterDescription.Length} chars; keep it under {MaxParameterDescriptionChars}.");
        }
    }

    public static TheoryData<MethodInfo> ToolMethods()
    {
        return new TheoryData<MethodInfo>
        {
            ToolMethod<SearchTool>(nameof(SearchTool.Search)),
            ToolMethod<InspectTool>(nameof(InspectTool.Inspect)),
            ToolMethod<ContextTool>(nameof(ContextTool.Context)),
            ToolMethod<TraceTool>(nameof(TraceTool.Trace)),
            ToolMethod<ImpactTool>(nameof(ImpactTool.Impact)),
            ToolMethod<EditTool>(nameof(EditTool.Edit)),
            ToolMethod<TodosTool>(nameof(TodosTool.Todos)),
            ToolMethod<ContentTool>(nameof(ContentTool.Content)),
            ToolMethod<PatternsTool>(nameof(PatternsTool.Patterns)),
            ToolMethod<WorkspaceTool>(nameof(WorkspaceTool.Workspace)),
        };
    }

    private static MethodInfo ToolMethod<TTool>(string name)
    {
        return typeof(TTool).GetMethod(name)
            ?? throw new InvalidOperationException($"Could not find {typeof(TTool).Name}.{name}.");
    }
}
