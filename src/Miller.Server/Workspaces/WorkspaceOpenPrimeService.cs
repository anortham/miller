using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server.Hosting;

namespace Miller.Server.Workspaces;

internal enum WorkspaceOpenPrimeEnqueueResult
{
    Queued,
    AlreadyQueued,
    Full,
    Stopping,
}

public sealed class WorkspaceOpenPrimeService : BackgroundService
{
    internal const int QueueCapacity = 64;

    private readonly IServiceProvider _services;
    private readonly ILogger<WorkspaceOpenPrimeService> _logger;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly object _gate = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private bool _stopping;

    public WorkspaceOpenPrimeService(
        IndexBootstrapService bootstrap,
        IServiceProvider services,
        ILogger<WorkspaceOpenPrimeService> logger)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _logger = logger;
    }

    internal WorkspaceOpenPrimeEnqueueResult TryEnqueue(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        lock (_gate)
        {
            if (_stopping)
                return WorkspaceOpenPrimeEnqueueResult.Stopping;
            if (!_active.Add(workspaceId))
                return WorkspaceOpenPrimeEnqueueResult.AlreadyQueued;
            if (_queue.Writer.TryWrite(workspaceId))
                return WorkspaceOpenPrimeEnqueueResult.Queued;
            _active.Remove(workspaceId);
            return WorkspaceOpenPrimeEnqueueResult.Full;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopping = true;
            _queue.Writer.TryComplete();
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (string workspaceId in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                WorkspaceRegistry? registry = null;
                try
                {
                    registry = _services.GetRequiredService<WorkspaceRegistry>();
                    CrossWorkspaceRefreshService refresh =
                        _services.GetRequiredService<CrossWorkspaceRefreshService>();
                    WorkspaceRefreshResult result = refresh.Refresh(
                        workspaceId,
                        force: false,
                        bypassBackoff: true);
                    ReconcileTerminalResult(registry, result);
                    _logger.LogInformation(
                        "Background workspace open prime finished for {WorkspaceId} with {Status}.",
                        workspaceId,
                        result.StatusText);
                }
                catch (Exception ex)
                {
                    try
                    {
                        registry ??= _services.GetRequiredService<WorkspaceRegistry>();
                        registry.MarkErrorIfRefreshing(workspaceId, ex.Message);
                    }
                    catch (Exception markError)
                    {
                        _logger.LogError(
                            markError,
                            "Could not mark background workspace open prime {WorkspaceId} as failed.",
                            workspaceId);
                    }

                    _logger.LogError(
                        ex,
                        "Background workspace open prime failed for {WorkspaceId}.",
                        workspaceId);
                }
                finally
                {
                    lock (_gate)
                        _active.Remove(workspaceId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static void ReconcileTerminalResult(
        WorkspaceRegistry registry,
        WorkspaceRefreshResult result)
    {
        if (result.Status is not (WorkspaceRefreshStatus.LockBusy or WorkspaceRefreshStatus.IneligibleExtractor))
            return;

        if (File.Exists(result.IndexDbPath))
        {
            registry.MarkLoadedExistingIfRefreshing(result.WorkspaceId, result.Revision ?? 0);
            return;
        }

        string error = result.Error
            ?? result.WarningText
            ?? $"Workspace open prime ended with {result.StatusText}, but no index exists at {result.IndexDbPath}.";
        registry.MarkErrorIfRefreshing(result.WorkspaceId, error);
    }
}
