using System.ComponentModel;
using System.Reflection;
using Miller.Server;
using Miller.Server.Tools;
using ModelContextProtocol.Server;
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
    [MemberData(nameof(ToolNames))]
    public void Load_DocumentsEveryTool(string toolName)
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("`" + toolName + "`", instructions);
    }

    [Fact]
    public void PublicMcpToolNames_AreTheDocumented1_0Surface()
    {
        string[] toolNames = DiscoverToolMethods().Select(static method => ToolName(method)).ToArray();

        Assert.Equal(
            new[]
            {
                "content",
                "context",
                "edit",
                "impact",
                "inspect",
                "patterns",
                "search",
                "trace",
                "workspace",
            },
            toolNames);
    }

    [Fact]
    public void Load_DoesNotAdvertiseTodosAsSeparateMcpTool()
    {
        string instructions = AgentInstructions.Load();

        Assert.DoesNotContain("- `todos`", instructions);
        Assert.DoesNotContain("todos(markers?", instructions);
    }

    [Fact]
    public void Load_DoesNotAdvertiseMetricsAsMcpTool()
    {
        string instructions = AgentInstructions.Load();

        Assert.DoesNotContain("- `metrics`", instructions);
        Assert.DoesNotContain("metrics(operation=", instructions);
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
    // content/docs. `markers|content` is the adjacency that proves content is IN the enum, not just the prose.
    [Fact]
    public void Load_SearchModeEnum_IncludesContentMode()
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("markers|content", instructions);
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
    public void Load_DocumentsTraceRecoveryGuidance()
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("no extracted graph path within depth, not proof unrelated", instructions);
        Assert.Contains("provider-scoped to", instructions);
        Assert.Contains("`dotnet-web`, `nextjs`, and `nuxt`", instructions);
        Assert.Contains("outside those providers use `mode=refs`/`mode=path`, or `inspect depth=full`", instructions);
        Assert.Contains("on empty, fall back to `search mode=source`", instructions);
        Assert.Contains("follow its `Next:` actions", instructions);
        Assert.Contains("ASP.NET, htmx, and Vue route facts", instructions);
    }

    [Fact]
    public void Load_DocumentsContentAndPatternsRecoveryGuidance()
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("pass the hit's `source_id`", instructions);
        Assert.Contains("workspace_id when reading cross-workspace hits", instructions);
        Assert.Contains("Empty content searches and failed reads include recovery guidance", instructions);
        Assert.Contains("patterns operation=list", instructions);
        Assert.Contains("List/no-match results include `next_actions`", instructions);
        Assert.Contains("patterns operation=search query=route", instructions);
        Assert.Contains("patterns operation=search pattern_id=htmx.attribute.v1", instructions);
        Assert.Contains("patterns operation=search pattern_id=vue.route_reference.v1", instructions);
    }

    [Fact]
    public void Load_DocumentsOverviewFirstInspectGuidance()
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("Default inspect depth is `summary`", instructions);
        Assert.Contains("first symbol read should usually be `inspect target depth=overview`", instructions);
        Assert.Contains("Use `depth=full` when you need the complete body or complete relation lists", instructions);
    }

    [Fact]
    public void InspectToolDescription_DocumentsOverviewFirstGuidance()
    {
        MethodInfo method = ToolMethod<InspectTool>(nameof(InspectTool.Inspect));
        string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        Assert.Contains("Default depth is summary", description);
        Assert.Contains("Start symbol reads with depth=overview", description);
        Assert.Contains("depth=full only when you need the complete body", description);
    }

    [Fact]
    public void TraceToolDescription_DocumentsRecoveryGuidance()
    {
        MethodInfo method = ToolMethod<TraceTool>(nameof(TraceTool.Trace));
        string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        Assert.Contains("Empty refs/no-neighbour/no-path/unsupported results include next actions", description);
        Assert.Contains("next_actions", description);
    }

    [Fact]
    public void ContentAndPatternsToolDescriptions_DocumentRecoveryGuidance()
    {
        string contentDescription = ToolMethod<ContentTool>(nameof(ContentTool.Content))
            .GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
        string patternsDescription = ToolMethod<PatternsTool>(nameof(PatternsTool.Patterns))
            .GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        Assert.Contains("source_id", contentDescription);
        Assert.Contains("next_actions", contentDescription);
        Assert.Contains("List/no-match results include next_actions", patternsDescription);
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

    [Fact]
    public void Load_DocumentsTokenSavingEditWorkflow()
    {
        string instructions = AgentInstructions.Load();

        Assert.Contains("localized existing-file edits", instructions);
        Assert.Contains("match_mode=auto|exact|normalized|fuzzy", instructions);
        Assert.Contains("`query`/`anchor`/`line`", instructions);
        Assert.Contains("review the match proof", instructions);
    }

    [Fact]
    public void EditToolDescription_DocumentsTokenSavingSelectors()
    {
        MethodInfo method = ToolMethod<EditTool>(nameof(EditTool.Edit));
        string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        Assert.Contains("match_mode", description);
        Assert.Contains("query/anchor/line", description);
        Assert.Contains("match proof", description);
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
        var data = new TheoryData<MethodInfo>();
        foreach (MethodInfo method in DiscoverToolMethods())
            data.Add(method);
        return data;
    }

    public static TheoryData<string> ToolNames()
    {
        var data = new TheoryData<string>();
        foreach (MethodInfo method in DiscoverToolMethods())
            data.Add(ToolName(method));
        return data;
    }

    private static IReadOnlyList<MethodInfo> DiscoverToolMethods() =>
        typeof(SearchTool).Assembly
            .GetTypes()
            .Where(static type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(static type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Where(static method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(static method => ToolName(method), StringComparer.Ordinal)
            .ToArray();

    private static string ToolName(MethodInfo method) =>
        method.GetCustomAttribute<McpServerToolAttribute>()?.Name
        ?? throw new InvalidOperationException($"{method.DeclaringType?.Name}.{method.Name} is missing a tool name.");

    private static MethodInfo ToolMethod<TTool>(string name) =>
        typeof(TTool).GetMethod(name)
        ?? throw new InvalidOperationException($"Could not find {typeof(TTool).Name}.{name}.");
}
