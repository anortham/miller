using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Miller.SemanticModelEval;

internal static class EvaluationReadOnlyCallToolFilter
{
    private static readonly IReadOnlySet<string> ReadOnlyWorkspaceOperations =
        new HashSet<string>(["status", "health", "list", "onboarding"], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ReadOnlyContentOperations =
        new HashSet<string>(["list", "search", "read", "export"], StringComparer.Ordinal);

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        return next => (request, cancellationToken) =>
        {
            CallToolRequestParams? parameters = request.Params;
            string? tool = parameters?.Name;
            IDictionary<string, JsonElement>? arguments = parameters?.Arguments;

            if (string.Equals(tool, "edit", StringComparison.Ordinal))
                return ValueTask.FromResult(Refused("edit is disabled"));

            if (string.Equals(tool, "workspace", StringComparison.Ordinal)
                && Operation(arguments, "status") is { } workspaceOperation
                && !ReadOnlyWorkspaceOperations.Contains(workspaceOperation))
            {
                return ValueTask.FromResult(Refused($"workspace operation '{workspaceOperation}' is disabled"));
            }

            if (string.Equals(tool, "content", StringComparison.Ordinal)
                && Operation(arguments, "list") is { } contentOperation
                && !ReadOnlyContentOperations.Contains(contentOperation))
            {
                return ValueTask.FromResult(Refused($"content operation '{contentOperation}' is disabled"));
            }

            if (StringArgument(arguments, "workspace_id") is { } workspaceId
                && workspaceId is not "current" and not "primary")
            {
                return ValueTask.FromResult(
                    Refused("cross-workspace routing is disabled because it can refresh another index"));
            }

            return next(request, cancellationToken);
        };
    }

    private static string Operation(
        IDictionary<string, JsonElement>? arguments,
        string defaultOperation) =>
        StringArgument(arguments, "operation") ?? defaultOperation;

    private static string? StringArgument(
        IDictionary<string, JsonElement>? arguments,
        string name)
    {
        if (arguments is null
            || !arguments.TryGetValue(name, out JsonElement value)
            || value.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static CallToolResult Refused(string reason) =>
        new()
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = $"Semantic model evaluation is read-only: {reason}. Use a production Miller host for mutations.",
                },
            ],
        };
}
