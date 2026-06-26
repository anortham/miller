using Microsoft.Extensions.DependencyInjection;
using Miller.Server.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Miller.Server.Telemetry;

/// <summary>
/// Ensures the primary workspace is bound via MCP roots before any tool handler runs.
/// </summary>
public static class WorkspaceBindingCallToolFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        return next => async (request, cancellationToken) =>
        {
            var binding = request.Services?.GetService<IWorkspaceBindingService>();
            var server = request.Services?.GetService<McpServer>();
            if (binding is not null && server is not null)
                await binding.EnsurePrimaryBoundAsync(server, cancellationToken).ConfigureAwait(false);

            return await next(request, cancellationToken).ConfigureAwait(false);
        };
    }
}
