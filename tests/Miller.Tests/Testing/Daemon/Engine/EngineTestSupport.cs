using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Testing.Selection;

namespace Miller.Tests.Testing.Daemon.Engine;

internal static class EngineTestSupport
{
    internal const string WorkspaceId = "ws:engine";
    internal const string Identity = "gen-1";

    internal static ContinuousTestWorkspace Workspace(string root, string? projectPath = null)
    {
        string project = projectPath ?? Path.Combine(root, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        if (!File.Exists(project))
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        return new ContinuousTestWorkspace(
            WorkspaceId,
            root,
            project,
            Path.Combine(root, ".miller", "ct", "build", Guid.NewGuid().ToString("N")[..12]));
    }

    internal static ContinuousTestCase Case(
        string id,
        string? projectPath = null,
        string path = "tests/AppTests.cs") =>
        new(
            Id: id,
            WorkspaceId: WorkspaceId,
            Name: id,
            QualifiedName: id,
            Selector: id,
            FilePath: path,
            Source: "ct-provider:dotnet",
            Metadata: projectPath is null
                ? null
                : new Dictionary<string, object?> { ["ct_project_path"] = Path.GetFullPath(projectPath) });

    internal static ContinuousTestDaemonChange Change(
        ContinuousTestWorkspace workspace,
        string revision = "2",
        string indexIdentity = Identity,
        IReadOnlyList<string>? changedPaths = null,
        bool workspaceScope = false,
        ContinuousTestDeltaCompleteness completeness = ContinuousTestDeltaCompleteness.Complete,
        long? from = 1,
        long? to = 2,
        TimeSpan? debounce = null,
        DateTimeOffset? observedAt = null) =>
        new(
            workspace,
            revision,
            indexIdentity,
            ChangedPaths: changedPaths ?? ["src/App.cs"],
            WorkspaceScope: workspaceScope,
            DebounceDelay: debounce ?? TimeSpan.Zero,
            ObservedAt: observedAt,
            DeltaCompleteness: completeness,
            DeltaFromRevision: completeness == ContinuousTestDeltaCompleteness.Complete ? from : null,
            DeltaToRevision: completeness == ContinuousTestDeltaCompleteness.Complete ? to : null);

    internal static ContinuousTestImpactSelector Selector(ContinuousTestStore store, string identity = Identity, long revision = 2)
    {
        var facts = new FakeMillerFactSource { Current = new CtIndexCursor(identity, revision) };
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:app", "App", "src/App.cs"));
        facts.Tests.Add(FakeMillerFactSource.Hit("test:app", "AppTests", "tests/AppTests.cs", isTest: true));
        return new ContinuousTestImpactSelector(store, facts);
    }
}

internal sealed class RecordingEnqueuer : IContinuousTestDaemonEnqueuer
{
    public List<ContinuousTestDaemonChange> Changes { get; } = [];

    public ContinuousTestDaemonEnqueueResult Enqueue(ContinuousTestDaemonChange change)
    {
        Changes.Add(change);
        var pending = new ContinuousTestDaemonPendingRun(
            change.Workspace,
            change.CurrentRevision,
            change.CurrentRevision,
            change.IndexIdentity,
            [],
            change.FilterArguments,
            change.Command,
            change.Framework,
            change.WorkspaceScope,
            change.ObservedAt,
            change.ObservedAt);
        return new ContinuousTestDaemonEnqueueResult(
            new ContinuousTestSelectionResult([], [], []),
            pending);
    }
}

internal sealed class ScriptedRevisionSource : IContinuousTestRevisionSource
{
    private ContinuousTestRevisionObservation? _last;

    public Queue<ContinuousTestRevisionObservation?> Observations { get; } = new();

    public int RefreshCount { get; private set; }

    public Task<ContinuousTestRevisionObservation?> RefreshAsync(
        string workspaceId,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        if (Observations.Count > 0)
            _last = Observations.Dequeue();
        return Task.FromResult(_last);
    }
}

internal sealed class ScriptedImpactSource : IContinuousTestImpactSource
{
    public ContinuousTestImpactResult? Result { get; set; }

    public Exception? Throw { get; set; }

    public int Calls { get; private set; }

    public Task<ContinuousTestImpactResult?> ImpactAsync(
        string workspaceRoot,
        CtFreshnessKey current,
        CtFreshnessKey? from,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Throw is not null)
            throw Throw;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeContinuousTestProvider : IContinuousTestProvider
{
    public List<ContinuousTestProviderRunRequest> RunRequests { get; } = [];

    public IReadOnlyList<ProviderTestCase> DiscoverCases { get; set; } = [];

    public ProviderRunResult? RunResult { get; set; }

    public Exception? RunException { get; set; }

    public TaskCompletionSource<ContinuousTestProviderRunRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool BlockUntilCanceled { get; set; }

    public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(DiscoverCases);

    public async Task<ProviderRunResult> RunAsync(
        ContinuousTestProviderRunRequest request,
        CancellationToken cancellationToken = default)
    {
        RunRequests.Add(request);
        Started.TrySetResult(request);
        if (BlockUntilCanceled)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        if (RunException is not null)
            throw RunException;
        return RunResult ?? new ProviderRunResult(request.RunId ?? "run:1", "passed");
    }
}

internal sealed class ManualDelay
{
    private readonly Queue<TaskCompletionSource> _pending = new();
    private int _count;

    public int Count => _count;

    public Task DelayAsync(TimeSpan _, CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            _count++;
            _pending.Enqueue(source);
        }

        cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
        return source.Task;
    }

    public async Task WaitForDelayCountAsync(int count, CancellationToken cancellationToken)
    {
        while (Count < count)
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
    }

    public void CompleteNext()
    {
        TaskCompletionSource source;
        lock (_pending)
            source = _pending.Dequeue();
        source.TrySetResult();
    }
}
