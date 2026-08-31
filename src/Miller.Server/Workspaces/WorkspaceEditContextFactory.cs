using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server.Hosting;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Microsoft.Extensions.Logging;

namespace Miller.Server.Workspaces;

/// <summary>Owns the target-bound dependencies used by one edit call.</summary>
public sealed class WorkspaceEditContext : IDisposable
{
    private readonly WorkspaceSymbolReadContext _readContext;

    internal WorkspaceEditContext(WorkspaceSymbolReadContext readContext, EditService service)
    {
        ArgumentNullException.ThrowIfNull(readContext);
        ArgumentNullException.ThrowIfNull(service);
        _readContext = readContext;
        Service = service;
    }

    public EditService Service { get; }

    public void Dispose() => _readContext.Dispose();
}

/// <summary>Resolves an edit target and constructs every edit dependency against that target.</summary>
public sealed class WorkspaceEditContextFactory
{
    private readonly IWorkspaceSymbolReadProvider _symbolReads;
    private readonly WorkspaceRegistry _registry;
    private readonly Func<WorkspaceContext?> _primaryWorkspace;
    private readonly IEditWriteThrough _primaryWriteThrough;
    private readonly Func<string, WorkspaceRefreshResult> _fallbackRefresh;
    private readonly ILogger<RegisteredWorkspaceWriteThrough> _logger;

    /// <summary>Construct the production target resolver over lazy primary services.</summary>
    public WorkspaceEditContextFactory(
        IWorkspaceSymbolReadProvider symbolReads,
        WorkspaceRegistry registry,
        IndexBootstrapService primary,
        LeaderWriteThrough primaryWriteThrough,
        CrossWorkspaceRefreshService refreshService,
        ILogger<RegisteredWorkspaceWriteThrough> logger)
        : this(
            symbolReads,
            registry,
            () => primary.IsBound ? primary.Workspace : null,
            primaryWriteThrough,
            workspaceId => refreshService.Refresh(
                workspaceId,
                scanAdmission: ScanAdmissionBudget.Of(TimeSpan.Zero),
                bypassBackoff: true),
            logger)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(refreshService);
    }

    internal WorkspaceEditContextFactory(
        IWorkspaceSymbolReadProvider symbolReads,
        WorkspaceRegistry registry,
        Func<WorkspaceContext?> primaryWorkspace,
        IEditWriteThrough primaryWriteThrough,
        Func<string, WorkspaceRefreshResult> fallbackRefresh,
        ILogger<RegisteredWorkspaceWriteThrough> logger)
    {
        ArgumentNullException.ThrowIfNull(symbolReads);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(primaryWorkspace);
        ArgumentNullException.ThrowIfNull(primaryWriteThrough);
        ArgumentNullException.ThrowIfNull(fallbackRefresh);
        ArgumentNullException.ThrowIfNull(logger);
        _symbolReads = symbolReads;
        _registry = registry;
        _primaryWorkspace = primaryWorkspace;
        _primaryWriteThrough = primaryWriteThrough;
        _fallbackRefresh = fallbackRefresh;
        _logger = logger;
    }

    /// <summary>Build a service whose read, lock, disk, and convergence paths all use the selected workspace.</summary>
    public WorkspaceEditContext Create(string? workspaceId)
    {
        WorkspaceRegistryRow? row = null;
        WorkspaceSymbolReadContext readContext;
        string targetWorkspaceId;

        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            readContext = _symbolReads.ResolveCompleteCurrentSymbolRead();
            targetWorkspaceId = readContext.WorkspaceId
                ?? throw new InvalidOperationException("The current workspace has no resolved workspace ID.");
        }
        else
        {
            row = WorkspaceRegistrySelector.Resolve(
                _registry,
                workspaceId,
                WorkspaceSelectorIntent.Mutate);
            if (!Directory.Exists(row.CanonicalRoot))
                throw new DirectoryNotFoundException($"Workspace root not found: {row.CanonicalRoot}");
            WorkspaceRootSafety.RejectSensitiveRoot(row.CanonicalRoot, fromCwd: false);
            targetWorkspaceId = row.WorkspaceId;
            readContext = _symbolReads.ResolveSymbolRead(targetWorkspaceId, WorkspaceRefreshMode.None);
        }

        try
        {
            string targetRoot;
            string indexDbPath;
            if (row is not null)
            {
                targetRoot = row.CanonicalRoot;
                indexDbPath = row.IndexDbPath;
            }
            else
            {
                WorkspaceContext primary = _primaryWorkspace()
                    ?? throw new InvalidOperationException("The current workspace is not ready for edits.");
                targetRoot = primary.CanonicalRoot ?? primary.WorkspaceRoot;
                indexDbPath = primary.CanonicalExtractDbPath ?? primary.ExtractDbPath;
                if (!string.Equals(readContext.WorkspaceId, primary.WorkspaceId, StringComparison.Ordinal) ||
                    !WorkspaceSafety.IsLiveWorkspace(readContext.WorkspaceRoot, targetRoot))
                {
                    throw new InvalidOperationException(
                        "The current workspace resolved to inconsistent target metadata.");
                }
            }
            TelemetryContext.Current?.SetWorkspace(targetWorkspaceId, targetRoot);
            if (row is not null &&
                (!string.Equals(readContext.WorkspaceId, row.WorkspaceId, StringComparison.Ordinal) ||
                 !WorkspaceSafety.IsLiveWorkspace(readContext.WorkspaceRoot, row.CanonicalRoot)))
            {
                throw new InvalidOperationException(
                    $"The selected workspace '{row.WorkspaceId}' resolved to inconsistent target metadata.");
            }
            string millerDir = Path.GetDirectoryName(Path.GetFullPath(indexDbPath))
                ?? throw new InvalidOperationException(
                    $"Cannot determine the .miller directory for workspace '{targetWorkspaceId}'.");
            var applier = new EditApplier(() => EditWriteLock.TryAcquire(millerDir));
            IEditWriteThrough writeThrough = IsServicedPrimary(readContext)
                ? _primaryWriteThrough
                : new RegisteredWorkspaceWriteThrough(
                    targetWorkspaceId,
                    targetRoot,
                    indexDbPath,
                    _registry,
                    _fallbackRefresh,
                    _logger);
            Func<WorkspaceSymbolReadContext> resolveFreshContext = string.IsNullOrWhiteSpace(workspaceId)
                ? _symbolReads.ResolveCompleteCurrentSymbolRead
                : () => _symbolReads.ResolveSymbolRead(targetWorkspaceId, WorkspaceRefreshMode.None);
            var service = new EditService(
                readContext.Index,
                new SmartTargetResolver(readContext.Index),
                indexDbPath,
                targetRoot,
                applier,
                writeThrough,
                readSession: readContext.ReadSession,
                resolveFreshContext: resolveFreshContext);
            return new WorkspaceEditContext(readContext, service);
        }
        catch
        {
            readContext.Dispose();
            throw;
        }
    }

    private bool IsServicedPrimary(WorkspaceSymbolReadContext readContext)
    {
        WorkspaceContext? primary = _primaryWorkspace();
        if (primary is null)
            return false;

        string primaryRoot = primary.CanonicalRoot ?? primary.WorkspaceRoot;
        bool sameRoot = WorkspaceSafety.IsLiveWorkspace(readContext.WorkspaceRoot, primaryRoot);
        return sameRoot && string.Equals(
            readContext.WorkspaceId,
            primary.WorkspaceId,
            StringComparison.Ordinal);
    }
}
