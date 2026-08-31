using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Miller.Server.Hosting;

public interface IWorkspaceBindingService
{
    int BindingGeneration { get; }

    bool IsDeferred { get; }

    BootstrapSnapshot Snapshot { get; }

    Task WaitUntilBoundAsync(CancellationToken cancellationToken);

    Task WaitForRunAsync(int runGeneration, CancellationToken cancellationToken);

    Task EnsurePrimaryBoundAsync(McpServer server, CancellationToken cancellationToken);

    void MarkRootsDirty();
}

/// <summary>
/// Legacy test seam for the retired primary Roots binding path. Production never registers this service;
/// stateless MCP calls resolve registered workspace IDs instead.
/// </summary>
public sealed class WorkspaceBindingService : IWorkspaceBindingService
{
    private readonly IndexBootstrapService _bootstrap;
    private readonly ILogger<WorkspaceBindingService> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _bindLock = new(1, 1);

    private IReadOnlyList<string>? _cachedRootUris;
    private bool _rootsDirty = true;

    public WorkspaceBindingService(IndexBootstrapService bootstrap, ILogger<WorkspaceBindingService> logger)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(logger);
        _bootstrap = bootstrap;
        _logger = logger;
    }

    public int BindingGeneration => _bootstrap.BindingGeneration;

    public bool IsDeferred => _bootstrap.IsDeferred;

    public BootstrapSnapshot Snapshot => _bootstrap.Snapshot;

    public Task WaitUntilBoundAsync(CancellationToken cancellationToken) =>
        _bootstrap.WaitUntilBoundAsync(cancellationToken);

    public Task WaitForRunAsync(int runGeneration, CancellationToken cancellationToken) =>
        _bootstrap.WaitForRunAsync(runGeneration, cancellationToken);

    public void MarkRootsDirty()
    {
        lock (_gate)
        {
            _rootsDirty = true;
            _cachedRootUris = null;
        }
    }

    internal Task EnsurePrimaryBoundFromRootsAsync(
        IReadOnlyList<string>? rootUris, CancellationToken cancellationToken) =>
        EnsurePrimaryBoundCoreAsync(rootUris, cancellationToken);

    public async Task EnsurePrimaryBoundAsync(McpServer server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (IsSettled())
            return;

        IReadOnlyList<string>? rootUris = await GetRootUrisAsync(server, cancellationToken).ConfigureAwait(false);
        await EnsurePrimaryBoundCoreAsync(rootUris, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsurePrimaryBoundCoreAsync(
        IReadOnlyList<string>? rootUris, CancellationToken cancellationToken)
    {
        if (IsSettled())
            return;

        await _bindLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsSettled())
                return;

            var resolved = WorkspaceBindingResolver.TryResolve(Environment.CurrentDirectory, rootUris)
                ?? throw WorkspaceBindingResolver.CreateBindingFailureException();

            _logger.LogInformation(
                "Binding primary workspace to {Root} (source={Source}).",
                resolved.Path, resolved.Source);
            var outcome = _bootstrap.BootstrapForRoot(resolved.Path, resolved.Source);

            if (outcome != BindOutcome.RebindDeferred)
            {
                lock (_gate)
                {
                    _rootsDirty = false;
                    if (rootUris is not null)
                        _cachedRootUris = rootUris;
                }
            }
        }
        finally
        {
            _bindLock.Release();
        }
    }

    private bool IsSettled() =>
        _bootstrap.Snapshot.Phase == BootstrapPhase.Bound && !NeedsRefresh();

    private bool NeedsRefresh()
    {
        lock (_gate)
            return _rootsDirty;
    }

    private async Task<IReadOnlyList<string>?> GetRootUrisAsync(McpServer server, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_rootsDirty && _cachedRootUris is not null)
                return _cachedRootUris;
        }

        try
        {
            ListRootsResult result = await server
                .RequestRootsAsync(new ListRootsRequestParams(), cancellationToken)
                .ConfigureAwait(false);
            var uris = result.Roots?
                .Select(static r => r.Uri)
                .Where(static u => !string.IsNullOrWhiteSpace(u))
                .ToArray() ?? Array.Empty<string>();

            lock (_gate)
            {
                _cachedRootUris = uris;
            }

            return uris;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MCP roots/list unavailable; falling back to env/cwd resolution.");
            return null;
        }
    }
}
