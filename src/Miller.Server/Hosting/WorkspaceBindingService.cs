using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Miller.Server.Hosting;

public interface IWorkspaceBindingService
{
    int BindingGeneration { get; }

    bool IsDeferred { get; }

    Task WaitUntilBoundAsync(CancellationToken cancellationToken);

    Task EnsurePrimaryBoundAsync(McpServer server, CancellationToken cancellationToken);

    void MarkRootsDirty();
}

/// <summary>
/// Resolves the primary workspace from MCP client roots on demand and drives deferred bootstrap.
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

    public Task WaitUntilBoundAsync(CancellationToken cancellationToken) =>
        _bootstrap.WaitUntilBoundAsync(cancellationToken);

    public void MarkRootsDirty()
    {
        lock (_gate)
        {
            _rootsDirty = true;
            _cachedRootUris = null;
        }
    }

    /// <summary>Test seam: bind using explicit root URIs without an MCP transport.</summary>
    internal Task EnsurePrimaryBoundFromRootsAsync(
        IReadOnlyList<string>? rootUris, CancellationToken cancellationToken) =>
        EnsurePrimaryBoundCoreAsync(rootUris, cancellationToken);

    public async Task EnsurePrimaryBoundAsync(McpServer server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (_bootstrap.IsBound && !NeedsRefresh())
            return;

        IReadOnlyList<string>? rootUris = await GetRootUrisAsync(server, cancellationToken).ConfigureAwait(false);
        await EnsurePrimaryBoundCoreAsync(rootUris, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsurePrimaryBoundCoreAsync(
        IReadOnlyList<string>? rootUris, CancellationToken cancellationToken)
    {
        if (_bootstrap.IsBound && !NeedsRefresh())
            return;

        await _bindLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bootstrap.IsBound && !NeedsRefresh())
                return;

            var resolved = WorkspaceBindingResolver.TryResolve(Environment.CurrentDirectory, rootUris)
                ?? throw WorkspaceBindingResolver.CreateBindingFailureException();

            _logger.LogInformation(
                "Binding primary workspace to {Root} (source={Source}).",
                resolved.Path, resolved.Source);
            _bootstrap.BootstrapForRoot(resolved.Path, resolved.Source);

            lock (_gate)
            {
                _rootsDirty = false;
                if (rootUris is not null)
                    _cachedRootUris = rootUris;
            }
        }
        finally
        {
            _bindLock.Release();
        }
    }

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
