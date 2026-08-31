using System.Text.Json;

namespace Miller.Server.Tools;

internal enum McpWorkspaceTargetKind
{
    Unscoped,
    Explicit,
    All,
    Missing,
    Implicit,
}

internal readonly record struct McpWorkspaceTargetDecision(
    McpWorkspaceTargetKind Kind,
    string? WorkspaceId,
    ToolDiagnostic? Diagnostic)
{
    public bool IsAllowed => Diagnostic is null;
}

internal static class McpWorkspaceTargetPolicy
{
    public const string WorkspaceIdRequiredCode = "workspace_id_required";
    public const string ImplicitWorkspaceSelectorRefusedCode = "implicit_workspace_selector_refused";

    private static readonly HashSet<string> WorkspaceBoundTools =
    [
        "search",
        "inspect",
        "context",
        "trace",
        "impact",
        "edit",
        "patterns",
        "content",
        "tests",
    ];

    private static readonly HashSet<string> WorkspaceGlobalOperations =
    [
        "list",
        "open",
        "remove",
        "prune",
        "dashboard",
    ];

    public static McpWorkspaceTargetDecision Evaluate(
        string? toolName,
        IDictionary<string, JsonElement>? arguments)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return Unscoped();

        string tool = toolName.Trim();
        bool knownWorkspaceTool = WorkspaceBoundTools.Contains(tool)
            || string.Equals(tool, "workspace", StringComparison.Ordinal);
        if (!knownWorkspaceTool)
            return Unscoped();

        if (TryGetString(arguments, "workspace_id", out string suppliedWorkspaceId)
            && IsImplicitSelector(suppliedWorkspaceId))
        {
            return Implicit(tool, suppliedWorkspaceId.Trim());
        }

        bool requiresWorkspace = WorkspaceBoundTools.Contains(tool)
            || !WorkspaceGlobalOperations.Contains(Operation(arguments));
        if (!requiresWorkspace)
            return Unscoped();

        if (!TryGetString(arguments, "workspace_id", out string workspaceId))
        {
            return new(
                McpWorkspaceTargetKind.Missing,
                null,
                ToolDiagnostic.Refusal(
                    WorkspaceIdRequiredCode,
                    $"MCP tool '{tool}' requires an explicit workspace_id. Pass a registered workspace ID or root path."));
        }

        workspaceId = workspaceId.Trim();

        if (IsImplicitSelector(workspaceId))
        {
            return Implicit(tool, workspaceId);
        }

        if (string.Equals(workspaceId, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(tool, "content", StringComparison.Ordinal)
                && string.Equals(Operation(arguments), "search", StringComparison.Ordinal))
            {
                return new(McpWorkspaceTargetKind.All, workspaceId, null);
            }

            return Implicit(tool, workspaceId);
        }

        return new(McpWorkspaceTargetKind.Explicit, workspaceId, null);
    }

    private static McpWorkspaceTargetDecision Unscoped() =>
        new(McpWorkspaceTargetKind.Unscoped, null, null);

    private static McpWorkspaceTargetDecision Implicit(string tool, string workspaceId) =>
        new(
            McpWorkspaceTargetKind.Implicit,
            workspaceId,
            ToolDiagnostic.Refusal(
                ImplicitWorkspaceSelectorRefusedCode,
                $"MCP tool '{tool}' refuses implicit workspace selector '{workspaceId}'. Pass an explicit registered workspace_id."));

    private static bool IsImplicitSelector(string workspaceId) =>
        string.Equals(workspaceId.Trim(), "current", StringComparison.OrdinalIgnoreCase)
        || string.Equals(workspaceId.Trim(), "primary", StringComparison.OrdinalIgnoreCase);

    private static string Operation(IDictionary<string, JsonElement>? arguments)
    {
        return TryGetString(arguments, "operation", out string operation)
            ? operation.Trim().ToLowerInvariant()
            : "status";
    }

    private static bool TryGetString(
        IDictionary<string, JsonElement>? arguments,
        string name,
        out string value)
    {
        value = string.Empty;
        if (arguments is null || !arguments.TryGetValue(name, out JsonElement element))
            return false;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}
