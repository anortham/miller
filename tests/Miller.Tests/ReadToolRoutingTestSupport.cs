using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Workspaces;

namespace Miller.Tests;

internal sealed class RecordingWorkspaceIndexProvider : IWorkspaceIndexProvider
{
    private readonly WorkspaceReadContext _current;
    private readonly Dictionary<string, WorkspaceReadContext> _targets;

    public RecordingWorkspaceIndexProvider(
        WorkspaceReadContext current,
        params (string WorkspaceId, WorkspaceReadContext Context)[] targets)
    {
        _current = current;
        _targets = targets.ToDictionary(x => x.WorkspaceId, x => x.Context, StringComparer.Ordinal);
    }

    public string? LastWorkspaceId { get; private set; }
    public bool? LastEnsureFresh { get; private set; }
    public int ResolveCount { get; private set; }

    public WorkspaceReadContext Resolve(string? workspaceId, bool ensureFresh)
    {
        LastWorkspaceId = workspaceId;
        LastEnsureFresh = ensureFresh;
        ResolveCount++;

        if (workspaceId is null)
            return _current;

        return _targets.TryGetValue(workspaceId, out WorkspaceReadContext? context)
            ? context
            : throw new KeyNotFoundException(workspaceId);
    }
}

internal static class ReadToolRoutingTestSupport
{
    public static WorkspaceReadContext ContextFor(
        MillerRepositoryIndex index,
        string indexDbPath,
        string? workspaceId,
        string workspaceRoot,
        bool? indexFresh = true,
        string freshnessStatus = "current") =>
        new(
            index,
            new SmartTargetResolver(index),
            indexDbPath,
            workspaceId,
            workspaceRoot,
            Revision: 1,
            indexFresh,
            freshnessStatus,
            WarningText: null);
}

internal sealed class HolderWorkspaceIndexProvider : IWorkspaceIndexProvider
{
    private readonly IndexHolder _holder;
    private readonly string _indexDbPath;
    private readonly string? _workspaceId;
    private readonly string _workspaceRoot;

    public HolderWorkspaceIndexProvider(
        IndexHolder holder,
        string indexDbPath,
        string? workspaceId,
        string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _holder = holder;
        _indexDbPath = indexDbPath;
        _workspaceId = workspaceId;
        _workspaceRoot = workspaceRoot;
    }

    public WorkspaceReadContext Resolve(string? workspaceId, bool ensureFresh)
    {
        (MillerRepositoryIndex index, long revision) = _holder.Snapshot();
        return new WorkspaceReadContext(
            index,
            new SmartTargetResolver(index),
            _indexDbPath,
            _workspaceId,
            _workspaceRoot,
            revision,
            IndexFresh: null,
            FreshnessStatus: "current",
            WarningText: null);
    }
}
