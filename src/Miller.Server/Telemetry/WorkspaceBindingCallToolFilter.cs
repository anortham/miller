using System.Text.Json;
using Miller.Server.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Miller.Server.Telemetry;

/// <summary>Rejects ambiguous or missing MCP workspace targets without consulting process state or MCP Roots.</summary>
public static class WorkspaceBindingCallToolFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        return next => async (request, cancellationToken) =>
        {
            string? toolName = request.Params?.Name;
            McpWorkspaceTargetDecision decision = McpWorkspaceTargetPolicy.Evaluate(
                toolName,
                request.Params?.Arguments);
            if (decision.Diagnostic is not { } diagnostic)
                return await next(request, cancellationToken).ConfigureAwait(false);

            bool json = IsJson(request.Params?.Arguments);
            string output = ToolDiagnosticRenderer.Render(toolName ?? "(unknown)", diagnostic, json);
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = output }],
            };
        };
    }

    private static bool IsJson(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue("format", out JsonElement format))
            return false;
        return format.ValueKind == JsonValueKind.String
            && string.Equals(format.GetString(), "json", StringComparison.OrdinalIgnoreCase);
    }
}
