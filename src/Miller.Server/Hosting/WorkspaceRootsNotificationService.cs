using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Miller.Server.Hosting;

/// <summary>
/// Registers the MCP roots/list_changed notification handler so cached client roots refresh on workspace switch.
/// </summary>
public sealed class WorkspaceRootsNotificationService : IHostedService
{
    private readonly McpServer _server;
    private readonly IWorkspaceBindingService _binding;
    private readonly ILogger<WorkspaceRootsNotificationService> _logger;

    public WorkspaceRootsNotificationService(
        McpServer server,
        IWorkspaceBindingService binding,
        ILogger<WorkspaceRootsNotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(logger);
        _server = server;
        _binding = binding;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _server.RegisterNotificationHandler(
            NotificationMethods.RootsListChangedNotification,
            (_, _) =>
            {
                _logger.LogDebug("Received notifications/roots/list_changed; invalidating cached MCP roots.");
                _binding.MarkRootsDirty();
                return ValueTask.CompletedTask;
            });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
