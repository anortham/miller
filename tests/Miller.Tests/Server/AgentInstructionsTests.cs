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
/// EmbeddedResource in the csproj would otherwise ship a server with empty guidance), that it fits the real
/// Claude Code delivery window (see <see cref="MaxServerInstructionsChars"/>), and that its routing table names
/// every tool the agent is told to prefer, so the discovery core cannot silently drift out of covering a tool.
/// </summary>
public sealed class AgentInstructionsTests
{
    // Claude Code truncates MCP ServerInstructions at ~2KB per server, inside a shared ~4KB block across all
    // connected servers; a measured cut landed at char 2,047 on 2026-07-02. The old 12,000-char budget was
    // fiction — anything past ~2KB never reaches the agent. The core is the discovery contract that must fit
    // that window; per-tool detail lives in the tool [Description] attributes (delivered separately, un-shared).
    private const int MaxServerInstructionsChars = 1_900;
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
    public void Load_CoreFitsClaudeCodeDeliveryWindow()
    {
        string instructions = AgentInstructions.Load();
        int clientWorstCaseLength = instructions.ReplaceLineEndings("\r\n").Length;
        Assert.True(
            clientWorstCaseLength <= MaxServerInstructionsChars,
            $"Server instructions are {clientWorstCaseLength} chars after CRLF normalization; the discovery core must stay <= {MaxServerInstructionsChars} to survive Claude Code's ~2KB ServerInstructions truncation (measured cut at char 2,047 on 2026-07-02).");
    }

    [Fact]
    public void Load_PinsBehavioralAdoptionLanguage()
    {
        string instructions = AgentInstructions.Load();
        Assert.Contains("One Miller call beats shell greps and full-file reads", instructions);
        Assert.Contains("Structure before content", instructions);
        Assert.Contains("Impact before changing", instructions);
        Assert.Contains("do NOT re-verify Miller results with grep/find", instructions);
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void Load_RoutingTableNamesEveryTool(string toolName)
    {
        string instructions = AgentInstructions.Load();
        // The "When to reach for each tool" routing table lists one line per tool: `- <name> — …` (em dash).
        // Reflection drives the tool set so a new tool without a routing line fails here.
        Assert.Contains("- " + toolName + " — ", instructions);
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
    public void WorkspaceToolDescription_RoutesDashboardLaunchRequests()
    {
        MethodInfo method = ToolMethod<WorkspaceTool>(nameof(WorkspaceTool.Workspace));
        string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        Assert.Contains("dashboard/start/open/show requests", description);
        Assert.Contains("operation=dashboard", description);
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
