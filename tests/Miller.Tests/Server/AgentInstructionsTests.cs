using System.ComponentModel;
using System.Reflection;
using Miller.Server;
using Miller.Server.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins <see cref="AgentInstructions.Load"/> — the embedded MCP server instructions wired onto
/// <c>McpServerOptions.ServerInstructions</c> — and the ten tool <c>[Description]</c> attributes that act as the
/// post-discovery usage contracts. The ServerInstructions core is the DISCOVERY contract and must fit Claude
/// Code's real delivery window (see <see cref="MaxServerInstructionsChars"/>); the per-tool descriptions are the
/// USAGE contracts (delivered separately, un-shared, deferred under Tool Search) and must each be a self-sufficient
/// routing contract within per-tool and total budgets. See
/// <c>docs/adr/ADR-0001-guidance-delivery-channels.md</c>.
/// </summary>
public sealed class AgentInstructionsTests
{
    // Claude Code truncates MCP ServerInstructions at ~2KB per server, inside a shared ~4KB block across all
    // connected servers; a measured cut landed at char 2,047 on 2026-07-02. The old 12,000-char budget was
    // fiction — anything past ~2KB never reaches the agent. The core is the discovery contract that must fit
    // that window; per-tool detail lives in the tool [Description] attributes (delivered separately, un-shared).
    private const int MaxServerInstructionsChars = 1_900;
    private const int MaxParameterDescriptionChars = 250;

    // Per-tool [Description] budgets. Claude Code's client-side hard cap is ~2KB per tool description (delivered
    // un-shared and deferred under Tool Search). These Miller ceilings sit well under that: 900 default, with two
    // documented overrides where a tool legitimately carries more routing detail (trace is the known stress case).
    private const int DefaultToolDescriptionChars = 900;
    private static readonly IReadOnlyDictionary<string, int> ToolDescriptionBudgets =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["trace"] = 1_500,
            ["search"] = 1_100,
        };

    private static int ToolDescriptionBudget(string toolName) =>
        ToolDescriptionBudgets.TryGetValue(toolName, out int budget) ? budget : DefaultToolDescriptionChars;

    // Total schema budget (design §4): the ten tool descriptions are the post-discovery usage contract; their
    // combined length is gated so the pool cannot silently regrow into the deleted 12k ServerInstructions fiction.
    // The baseline the design records — 4,512 chars on 2026-07-02 — is descriptions-only (each parameter
    // description is separately capped at 250, below), so this total tracks the description text that grows.
    private const int MaxCombinedToolDescriptionChars = 9_000;

    // The seven tools cut off by the old ~2KB ServerInstructions window depend on their own description to redirect
    // misuse, so each must name at least one OTHER Miller tool in its "NOT for:" clause (design §4, Codex finding 4).
    private static readonly IReadOnlySet<string> ToolsThatMustRedirectInNotForClause =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "context",
            "trace",
            "impact",
            "edit",
            "patterns",
            "content",
            "workspace",
            "tests",
        };

    private static readonly string[] AllToolNames =
    {
        "content",
        "context",
        "edit",
        "impact",
        "inspect",
        "patterns",
        "search",
        "tests",
        "trace",
        "workspace",
    };

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
        Assert.Contains("Trust the index: results are current for the indexed revision", instructions);
        Assert.Contains("beats re-checking by hand", instructions);
    }

    [Fact]
    public void Load_PinsSemanticDiscoveryRouting()
    {
        string instructions = AgentInstructions.Load();

        Assert.Contains("auto may use semantics", instructions);
        Assert.Contains("lexical does zero vector work", instructions);
        Assert.Contains("semantic-broker health", instructions);
    }

    [Fact]
    public void Load_PinsStatelessWorkspaceTargeting()
    {
        string instructions = AgentInstructions.Load();

        Assert.Contains("workspace operation=list", instructions);
        Assert.Contains("workspace operation=open path=/absolute/project", instructions);
        Assert.Contains("workspace_id", instructions);
        Assert.Contains("every workspace-bound call", instructions);
        Assert.Contains("User-level GUI clients", instructions);
        Assert.DoesNotContain("set `MILLER_WORKSPACE_ROOT`", instructions);
        Assert.DoesNotContain("set `GOLDFISH_WORKSPACE`", instructions);
        Assert.DoesNotContain("roots/list", instructions);
        Assert.DoesNotContain("use `current`", instructions);
        Assert.DoesNotContain("use `primary`", instructions);
    }

    [Fact]
    public void PublicGuidance_UsesStatelessWorkspaceSelection()
    {
        string repoRoot = ScaleTestSupport.RepoRoot();
        string readme = File.ReadAllText(Path.Combine(repoRoot, "README.md"));
        string install = File.ReadAllText(Path.Combine(repoRoot, "docs", "install.md"));
        string migration = File.ReadAllText(Path.Combine(repoRoot, "docs", "migration-from-julie.md"));
        string site = File.ReadAllText(Path.Combine(repoRoot, "docs", "site", "index.html"));
        string finding = File.ReadAllText(Path.Combine(repoRoot, "docs", "findings", "2026-06-08-cursor-plugin-relative-launcher-root-cause.md"));

        foreach (string guidance in new[] { readme, install, migration, site, finding })
        {
            Assert.Contains("workspace operation=list", guidance);
            Assert.Contains("operation=open", guidance);
            Assert.Contains("path=/absolute/project", guidance);
            Assert.Contains("workspace_id", guidance);
        }

        Assert.DoesNotContain("If your client lacks MCP roots support, set", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("\"env\": { \"MILLER_WORKSPACE_ROOT\"", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Miller resolves the open project via MCP", install, StringComparison.Ordinal);
        Assert.DoesNotContain("For clients without MCP roots support, set", install, StringComparison.Ordinal);
        Assert.DoesNotContain("Miller binds from MCP client roots", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("set `MILLER_WORKSPACE_ROOT`", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("Miller binds the open project via MCP roots on the first tool call", site, StringComparison.Ordinal);
        Assert.DoesNotContain("**Current recommendation:**", finding, StringComparison.Ordinal);
        Assert.DoesNotContain("Miller binds via MCP roots", finding, StringComparison.Ordinal);
        Assert.DoesNotContain("Optional `MILLER_WORKSPACE_ROOT` env", finding, StringComparison.Ordinal);
        Assert.DoesNotContain("Miller now binds workspace from MCP", finding, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolDescriptions_PinSemanticActivationAndBrokerDiagnostics()
    {
        IReadOnlyDictionary<string, string> descriptions = DiscoverToolMethods()
            .ToDictionary(
                ToolName,
                static method => method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
                StringComparer.Ordinal);

        Assert.Contains("on by default", descriptions["search"]);
        Assert.Contains("MILLER_SEMANTIC=off", descriptions["search"]);
        Assert.Contains("permanent process-wide zero-work", descriptions["search"]);

        Assert.Contains("semantic-broker readiness", descriptions["workspace"]);
        Assert.Contains("role", descriptions["workspace"]);
        Assert.Contains("backend", descriptions["workspace"]);
        Assert.Contains("accelerator lease", descriptions["workspace"]);
        Assert.Contains("degradation", descriptions["workspace"]);
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
    public void PublicMcpToolNames_AreTheDocumentedSurface()
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
                "tests",
                "trace",
                "workspace",
            },
            toolNames);
    }

    [Fact]
    public void TestsDescription_LeadsWithCheapStatusAndExplicitStart()
    {
        string description = DiscoverToolMethods()
            .Single(static method => string.Equals(ToolName(method), "tests", StringComparison.Ordinal))
            .GetCustomAttribute<DescriptionAttribute>()
            ?.Description ?? string.Empty;

        int statusAt = description.IndexOf("status", StringComparison.OrdinalIgnoreCase);
        int startAt = description.IndexOf("start", StringComparison.OrdinalIgnoreCase);
        int enableAt = description.IndexOf("enable", StringComparison.OrdinalIgnoreCase);
        Assert.True(statusAt >= 0, "tests description must name status.");
        Assert.True(startAt > statusAt, "tests description must put status before start.");
        Assert.True(enableAt > startAt, "tests description must put start before enable.");
        Assert.Contains("opt-in", description, StringComparison.OrdinalIgnoreCase);
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
    public void ToolDescriptions_DoNotEmbedSelfMatchingExampleQueries()
    {
        string search = typeof(SearchTool).GetMethod(nameof(SearchTool.Search))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description!;
        string context = typeof(ContextTool).GetMethod(nameof(ContextTool.ContextWithCancellation))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description!;

        Assert.DoesNotContain("promote rebuild", search, StringComparison.Ordinal);
        Assert.DoesNotContain("converge the search sidecar", context, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ToolMethods))]
    public void ToolDescriptions_StayWithinPerToolBudget(MethodInfo method)
    {
        string toolName = ToolName(method);
        string methodName = $"{method.DeclaringType?.Name}.{method.Name}";
        string? description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.False(string.IsNullOrWhiteSpace(description), $"{methodName} is missing a tool description.");

        int budget = ToolDescriptionBudget(toolName);
        Assert.True(
            description!.Length <= budget,
            $"{methodName} description is {description.Length} chars; keep '{toolName}' under its {budget}-char budget (client-side hard cap is ~2KB/description).");
    }

    [Theory]
    [MemberData(nameof(ToolMethods))]
    public void ToolParameterDescriptions_StayWithinBudget(MethodInfo method)
    {
        string methodName = $"{method.DeclaringType?.Name}.{method.Name}";
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

    [Theory]
    [MemberData(nameof(ToolMethods))]
    public void ToolDescriptions_AreSelfSufficientUsageContracts(MethodInfo method)
    {
        string toolName = ToolName(method);
        string methodName = $"{method.DeclaringType?.Name}.{method.Name}";
        string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        // Every description carries a when-NOT-to routing clause and a copyable example call, so the description
        // alone is a sufficient usage contract once Tool Search surfaces it (marker-only checks are vacuous —
        // design §4, Codex finding 4).
        Assert.True(
            description.Contains("NOT for:", StringComparison.Ordinal),
            $"{methodName} description must include a 'NOT for:' routing clause.");
        // Match "Example" rather than "Example:" — patterns legitimately uses the plural "Examples:".
        Assert.True(
            description.Contains("Example", StringComparison.Ordinal),
            $"{methodName} description must include a copyable example call ('Example:' / 'Examples:').");

        if (ToolsThatMustRedirectInNotForClause.Contains(toolName))
        {
            string notForClause = NotForClause(description);
            bool namesAnotherTool = AllToolNames.Any(other =>
                !string.Equals(other, toolName, StringComparison.Ordinal)
                && notForClause.Contains(other, StringComparison.Ordinal));
            Assert.True(
                namesAnotherTool,
                $"{methodName} 'NOT for:' clause must redirect to at least one other Miller tool by name; clause was: \"{notForClause}\".");
        }
    }

    // Redirect vocabulary rule: a description may say what a tool is NOT for only while naming where to go
    // instead. The template's "NOT for: X (use Y)" clause does exactly that and is required above, so it is
    // excluded here; what remains must not prohibit without redirecting. Measured effect (context-mode, 2026-07):
    // bare-NOT negations regressed smaller models, so this is a gate rather than a style note.
    private static readonly string[] ProhibitionMarkers =
    {
        "never ",
        "do not ",
        "do NOT ",
        "don't ",
        "cannot ",
        "must not ",
    };

    [Theory]
    [MemberData(nameof(ToolMethods))]
    public void ToolDescriptions_RedirectInsteadOfProhibiting(MethodInfo method)
    {
        string methodName = $"{method.DeclaringType?.Name}.{method.Name}";
        string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
        string outsideNotForClause = description.Replace(NotForClause(description), string.Empty, StringComparison.Ordinal);

        foreach (string marker in ProhibitionMarkers)
        {
            Assert.False(
                outsideNotForClause.Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"{methodName} description prohibits with \"{marker.Trim()}\" outside its 'NOT for:' clause; state the capability and name the tool to use instead.");
        }
    }

    [Fact]
    public void CombinedToolDescriptions_StayWithinTotalSchemaBudget()
    {
        MethodInfo[] methods = DiscoverToolMethods().ToArray();
        int descriptionTotal = methods.Sum(static method =>
            (method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty).Length);
        int parameterTotal = methods.Sum(static method => method.GetParameters()
            .Sum(static parameter => (parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty).Length));

        Assert.True(
            descriptionTotal <= MaxCombinedToolDescriptionChars,
            $"Combined tool-description text is {descriptionTotal} chars (parameters add {parameterTotal}; full schema total {descriptionTotal + parameterTotal}); keep descriptions under {MaxCombinedToolDescriptionChars} so the usage-contract pool cannot regrow into the deleted 12k fiction.");
    }

    // The "NOT for:" clause runs from "NOT for:" up to the following example call ("Example" / "Examples").
    private static string NotForClause(string description)
    {
        int start = description.IndexOf("NOT for:", StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;
        int example = description.IndexOf("Example", start, StringComparison.Ordinal);
        return example > start ? description[start..example] : description[start..];
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
}
