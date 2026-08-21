using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Workspaces;

namespace Miller.Tests;

internal sealed class RecordingWorkspaceIndexProvider
    : IWorkspaceIndexProvider, IWorkspaceSearchProvider, IWorkspaceSymbolReadProvider, IWorkspaceContentSearchProvider,
      IWorkspaceRegionSearchProvider, IWorkspaceTextContentSearchProvider
{
    private readonly WorkspaceReadContext _current;
    private readonly Dictionary<string, WorkspaceReadContext> _targets;
    private readonly WorkspaceContentSearchContext? _currentContent;
    private readonly Dictionary<string, WorkspaceContentSearchContext> _contentTargets;
    private readonly WorkspaceTextContentSearchContext? _currentTextContent;
    private readonly Dictionary<string, WorkspaceTextContentSearchContext> _textContentTargets;
    private readonly WorkspaceRegionSearchContext? _currentRegion;
    private readonly Dictionary<string, WorkspaceRegionSearchContext> _regionTargets;

    public RecordingWorkspaceIndexProvider(
        WorkspaceReadContext current,
        params (string WorkspaceId, WorkspaceReadContext Context)[] targets)
    {
        _current = current;
        _targets = targets.ToDictionary(x => x.WorkspaceId, x => x.Context, StringComparer.Ordinal);
        _currentContent = null;
        _contentTargets = new Dictionary<string, WorkspaceContentSearchContext>(StringComparer.Ordinal);
        _currentTextContent = null;
        _textContentTargets = new Dictionary<string, WorkspaceTextContentSearchContext>(StringComparer.Ordinal);
        _currentRegion = null;
        _regionTargets = new Dictionary<string, WorkspaceRegionSearchContext>(StringComparer.Ordinal);
    }

    public RecordingWorkspaceIndexProvider(
        WorkspaceReadContext current,
        WorkspaceContentSearchContext currentContent,
        (string WorkspaceId, WorkspaceContentSearchContext Context)[] contentTargets,
        params (string WorkspaceId, WorkspaceReadContext Context)[] targets)
    {
        _current = current;
        _currentContent = currentContent;
        _contentTargets = contentTargets.ToDictionary(x => x.WorkspaceId, x => x.Context, StringComparer.Ordinal);
        _targets = targets.ToDictionary(x => x.WorkspaceId, x => x.Context, StringComparer.Ordinal);
        _currentTextContent = null;
        _textContentTargets = new Dictionary<string, WorkspaceTextContentSearchContext>(StringComparer.Ordinal);
        _currentRegion = null;
        _regionTargets = new Dictionary<string, WorkspaceRegionSearchContext>(StringComparer.Ordinal);
    }

    public RecordingWorkspaceIndexProvider(
        WorkspaceReadContext current,
        WorkspaceTextContentSearchContext currentTextContent,
        (string WorkspaceId, WorkspaceTextContentSearchContext Context)[] textContentTargets,
        params (string WorkspaceId, WorkspaceReadContext Context)[] targets)
    {
        _current = current;
        _targets = targets.ToDictionary(x => x.WorkspaceId, x => x.Context, StringComparer.Ordinal);
        _currentContent = null;
        _contentTargets = new Dictionary<string, WorkspaceContentSearchContext>(StringComparer.Ordinal);
        _currentTextContent = currentTextContent;
        _textContentTargets = textContentTargets.ToDictionary(x => x.WorkspaceId, x => x.Context, StringComparer.Ordinal);
        _currentRegion = null;
        _regionTargets = new Dictionary<string, WorkspaceRegionSearchContext>(StringComparer.Ordinal);
    }

    public RecordingWorkspaceIndexProvider(
        WorkspaceReadContext current,
        WorkspaceRegionSearchContext currentRegion,
        (string WorkspaceId, WorkspaceRegionSearchContext Context)[] regionTargets,
        params (string WorkspaceId, WorkspaceReadContext Context)[] targets)
    {
        _current = current;
        _targets = targets.ToDictionary(x => x.WorkspaceId, x => x.Context, StringComparer.Ordinal);
        _currentContent = null;
        _contentTargets = new Dictionary<string, WorkspaceContentSearchContext>(StringComparer.Ordinal);
        _currentTextContent = null;
        _textContentTargets = new Dictionary<string, WorkspaceTextContentSearchContext>(StringComparer.Ordinal);
        _currentRegion = currentRegion;
        _regionTargets = regionTargets.ToDictionary(x => x.WorkspaceId, x => x.Context, StringComparer.Ordinal);
    }

    public string? LastWorkspaceId { get; private set; }
    public WorkspaceRefreshMode? LastRefreshMode { get; private set; }
    public int ResolveCount { get; private set; }
    public int SymbolSearchResolveCount { get; private set; }
    public int ContentSearchResolveCount { get; private set; }
    public int TextContentSearchResolveCount { get; private set; }
    public int RegionSearchResolveCount { get; private set; }

    public WorkspaceReadContext Resolve(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        LastWorkspaceId = workspaceId;
        LastRefreshMode = refresh;
        ResolveCount++;

        return ResolveContext(workspaceId);
    }

    public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        LastWorkspaceId = workspaceId;
        LastRefreshMode = refresh;
        ResolveCount++;
        SymbolSearchResolveCount++;

        return ReadToolRoutingTestSupport.SearchContextFor(ResolveContext(workspaceId));
    }

    public WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        LastWorkspaceId = workspaceId;
        LastRefreshMode = refresh;
        ResolveCount++;

        return ReadToolRoutingTestSupport.SymbolReadContextFor(ResolveContext(workspaceId));
    }

    public WorkspaceContentSearchContext ResolveContentSearch(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        LastWorkspaceId = workspaceId;
        LastRefreshMode = refresh;
        ResolveCount++;
        ContentSearchResolveCount++;

        if (workspaceId is null)
            return _currentContent ?? throw new InvalidOperationException("no current content context configured");

        return _contentTargets.TryGetValue(workspaceId, out WorkspaceContentSearchContext? context)
            ? context
            : throw new KeyNotFoundException(workspaceId);
    }

    public WorkspaceRegionSearchContext ResolveRegionSearch(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        LastWorkspaceId = workspaceId;
        LastRefreshMode = refresh;
        ResolveCount++;
        RegionSearchResolveCount++;

        if (workspaceId is null)
            return _currentRegion ?? throw new InvalidOperationException("no current region context configured");

        return _regionTargets.TryGetValue(workspaceId, out WorkspaceRegionSearchContext? context)
            ? context
            : throw new KeyNotFoundException(workspaceId);
    }

    public WorkspaceTextContentSearchContext ResolveTextContentSearch(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        LastWorkspaceId = workspaceId;
        LastRefreshMode = refresh;
        ResolveCount++;
        TextContentSearchResolveCount++;

        if (workspaceId is null)
            return _currentTextContent ?? throw new InvalidOperationException("no current text content context configured");

        return _textContentTargets.TryGetValue(workspaceId, out WorkspaceTextContentSearchContext? context)
            ? context
            : throw new KeyNotFoundException(workspaceId);
    }

    private WorkspaceReadContext ResolveContext(string? workspaceId)
    {
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
        string freshnessStatus = "current",
        string? displayId = null) =>
        new(
            index,
            new SmartTargetResolver(index),
            indexDbPath,
            workspaceId,
            workspaceRoot,
            Revision: 1,
            indexFresh,
            freshnessStatus,
            WarningText: null,
            DisplayId: displayId);

    public static WorkspaceSymbolSearchContext SearchContextFor(WorkspaceReadContext context) =>
        new(
            context.Index,
            context.ReadSession.LegacyArtifactPath!,
            context.WorkspaceId,
            context.WorkspaceRoot,
            context.Revision,
            context.IndexFresh,
            context.FreshnessStatus,
            context.WarningText,
            context.DisplayId);

    public static WorkspaceSymbolReadContext SymbolReadContextFor(WorkspaceReadContext context) =>
        new(
            context.Index,
            context.ReadSession.LegacyArtifactPath!,
            context.WorkspaceId,
            context.WorkspaceRoot,
            context.Revision,
            context.IndexFresh,
            context.FreshnessStatus,
            context.WarningText,
            context.DisplayId);

    public static WorkspaceContentSearchContext ContentContextFor(
        IContentSearchIndex index,
        string indexDbPath,
        string? workspaceId,
        string workspaceRoot,
        bool? indexFresh = true,
        string freshnessStatus = "current",
        string? displayId = null) =>
        new(
            index,
            indexDbPath,
            workspaceId,
            workspaceRoot,
            Revision: 1,
            indexFresh,
            freshnessStatus,
            WarningText: null,
            DisplayId: displayId);

    public static WorkspaceRegionSearchContext RegionContextFor(
        IRegionSearchIndex index,
        string indexDbPath,
        string? workspaceId,
        string workspaceRoot,
        bool? indexFresh = true,
        string freshnessStatus = "current",
        string? displayId = null) =>
        new(
            index,
            indexDbPath,
            workspaceId,
            workspaceRoot,
            Revision: index.Revision,
            indexFresh,
            freshnessStatus,
            WarningText: null,
            DisplayId: displayId);

    public static WorkspaceTextContentSearchContext TextContentContextFor(
        ITextContentSearchIndex index,
        string indexDbPath,
        string? workspaceId,
        string workspaceRoot,
        bool? indexFresh = true,
        string freshnessStatus = "current",
        string? displayId = null) =>
        new(
            index,
            indexDbPath,
            workspaceId,
            workspaceRoot,
            Revision: 1,
            indexFresh,
            freshnessStatus,
            WarningText: null,
            DisplayId: displayId);
}

internal sealed class HolderWorkspaceIndexProvider
    : IWorkspaceIndexProvider, IWorkspaceSearchProvider, IWorkspaceSymbolReadProvider, IWorkspaceContentSearchProvider,
      IWorkspaceRegionSearchProvider, IWorkspaceTextContentSearchProvider
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

    public WorkspaceReadContext Resolve(string? workspaceId, WorkspaceRefreshMode refresh)
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

    public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        (MillerRepositoryIndex index, long revision) = _holder.Snapshot();
        return new WorkspaceSymbolSearchContext(
            index,
            _indexDbPath,
            _workspaceId,
            _workspaceRoot,
            revision,
            IndexFresh: null,
            FreshnessStatus: "current",
            WarningText: null);
    }

    public WorkspaceSymbolReadContext ResolveSymbolRead(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        (MillerRepositoryIndex index, long revision) = _holder.Snapshot();
        return new WorkspaceSymbolReadContext(
            index,
            _indexDbPath,
            _workspaceId,
            _workspaceRoot,
            revision,
            IndexFresh: null,
            FreshnessStatus: "current",
            WarningText: null);
    }

    // These freshness/repoint fixtures exercise symbol search only; content search has no holder-backed
    // index, so this double does not serve it (a content-mode test wires a content provider explicitly).
    public WorkspaceContentSearchContext ResolveContentSearch(string? workspaceId, WorkspaceRefreshMode refresh) =>
        throw new NotSupportedException("HolderWorkspaceIndexProvider does not serve content search.");

    public WorkspaceRegionSearchContext ResolveRegionSearch(string? workspaceId, WorkspaceRefreshMode refresh) =>
        throw new NotSupportedException("HolderWorkspaceIndexProvider does not serve region search.");

    public WorkspaceTextContentSearchContext ResolveTextContentSearch(string? workspaceId, WorkspaceRefreshMode refresh) =>
        throw new NotSupportedException("HolderWorkspaceIndexProvider does not serve text content search.");
}
