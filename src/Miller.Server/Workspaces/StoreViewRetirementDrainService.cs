using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server.Hosting;

namespace Miller.Server.Workspaces;

/// <summary>
/// Finishes family-store view retirement after MCP <c>workspace remove</c>/<c>prune</c> has already
/// unregistered the row. Constructor takes no bootstrap getters.
/// </summary>
public sealed class StoreViewRetirementDrainService : BackgroundService
{
    internal const int QueueCapacity = 64;

    private readonly WorkspaceRegistry _registry;
    private readonly MillerHostPaths _hostPaths;
    private readonly ILogger<StoreViewRetirementDrainService> _logger;
    private readonly Channel<StoreSidecarReclaimTarget> _queue = Channel.CreateBounded<StoreSidecarReclaimTarget>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public StoreViewRetirementDrainService(
        WorkspaceRegistry registry,
        MillerHostPaths hostPaths,
        ILogger<StoreViewRetirementDrainService> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(hostPaths);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _hostPaths = hostPaths;
        _logger = logger;
    }

    public void Enqueue(StoreSidecarReclaimTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_queue.Writer.TryWrite(target))
        {
            _logger.LogWarning(
                "Store view retirement queue full; owed record remains for family {FamilyId} view {ViewId}.",
                target.FamilyId,
                target.ViewId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? retireView =
            StoreViewRetirementRunner.ForToolsRoot(_hostPaths.ToolsRoot);
        try
        {
            await foreach (StoreSidecarReclaimTarget target in _queue.Reader.ReadAllAsync(stoppingToken)
                .ConfigureAwait(false))
            {
                try
                {
                    _ = WorkspaceRemoval.FinishProducerRetirement(_registry, target, retireView);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Background store view retirement failed for family {FamilyId} view {ViewId}.",
                        target.FamilyId,
                        target.ViewId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
