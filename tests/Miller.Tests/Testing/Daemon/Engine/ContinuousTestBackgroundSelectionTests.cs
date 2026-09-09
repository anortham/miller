using Microsoft.Data.Sqlite;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Support;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

[Collection(ContinuousTestDaemonAdoptionCollection.Name)]
public sealed class ContinuousTestBackgroundSelectionTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    public ContinuousTestBackgroundSelectionTests(ITestOutputHelper output) => _output = output;

    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-selection-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_blocked_large_delta_selector_publishes_progress_and_accepts_a_second_request(bool targetWorktree, bool selectorFails)
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        using var facts = new BlockingFacts { ThrowAfterRelease = selectorFails };
        var provider = new FakeContinuousTestProvider { DiscoverCases = [new ProviderTestCase("test:app", "test:app", "test:app", "test:app", SourcePath: "tests/AppTests.cs")] };
        string brokenProject = Path.Combine(_root, "Broken.Tests.csproj");
        File.WriteAllText(brokenProject, "<Project />");
        if (targetWorktree)
            store.PutTestCase(EngineTestSupport.Case("test:broken", brokenProject));
        var discovery = new DiscoveryFailureProvider(provider, brokenProject);
        var queue = new ContinuousTestDaemonQueue(store, new ContinuousTestImpactSelector(store, facts),
            new ContinuousTestCoordinator(discovery, store));
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(Observation(2));
        var impact = new ScriptedImpactSource
        {
            Result = new ContinuousTestImpactResult(EngineTestSupport.WorkspaceId,
                Enumerable.Range(0, 5000).Select(i => i == 0 ? "src/App.cs" : $"src/File{i}.cs").ToArray(), [], [])
            { FromRevision = 2, ToRevision = 3 },
        };
        string worktreeRoot = Path.Combine(_root, "linked");
        string admin = Path.Combine(_root, ".git", "worktrees", "linked");
        Directory.CreateDirectory(admin);
        Directory.CreateDirectory(worktreeRoot);
        File.WriteAllText(Path.Combine(admin, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(worktreeRoot, ".git"), $"gitdir: {admin}\n");
        File.WriteAllText(ContinuousTestPolicy.EnabledMarkerPath(_root), string.Empty);
        ContinuousTestWorkspace linked = EngineTestSupport.Workspace(worktreeRoot) with { WorkspaceId = "ws:linked" };
        using var linkedStore = new ContinuousTestStore(CtSchema.DbPathFor(worktreeRoot));
        linkedStore.PutTestCase(EngineTestSupport.Case("test:linked", linked.ProjectPath) with { WorkspaceId = linked.WorkspaceId });
        var linkedProvider = new FakeContinuousTestProvider { DiscoverCases = [new ProviderTestCase("test:linked", "test:linked", "test:linked", "test:linked", SourcePath: "tests/AppTests.cs")] };
        var linkedQueue = new ContinuousTestDaemonQueue(linkedStore,
            new ContinuousTestImpactSelector(linkedStore, new FakeMillerFactSource()),
            new ContinuousTestCoordinator(linkedProvider, linkedStore));
        var host = new ContinuousTestDaemonHost(_root, new ContinuousTestDaemonHostOptions
        {
            Enabled = true,
            WorkspaceId = EngineTestSupport.WorkspaceId,
            Store = store,
            Queue = queue,
            Poller = new ContinuousTestRevisionPoller(source, impact, debounceDelay: TimeSpan.Zero),
            Projects = targetWorktree
                ? [new ContinuousTestProject("proj:1", EngineTestSupport.WorkspaceId, workspace.ProjectPath, Framework: "xunit"),
                   new ContinuousTestProject("proj:broken", EngineTestSupport.WorkspaceId, brokenProject, Framework: "xunit")]
                : [new ContinuousTestProject("proj:1", EngineTestSupport.WorkspaceId, workspace.ProjectPath, Framework: "xunit")],
            Budget = CtExecutionBudget.Disabled(),
            AcquireLease = false,
            PollInterval = TimeSpan.FromMilliseconds(5),
            WorktreeAdoption = new ContinuousTestWorktreeAdoptionOptions
            {
                DiscoverRegisteredRoots = () => [worktreeRoot],
                ScanInterval = TimeSpan.Zero,
                CreateContext = root => new ContinuousTestWorkspaceContext
                {
                    WorkspaceRoot = root,
                    WorkspaceId = linked.WorkspaceId,
                    Store = linkedStore,
                    Queue = linkedQueue,
                    Projects = [new ContinuousTestProject("proj:linked", linked.WorkspaceId, linked.ProjectPath, Framework: "xunit")],
                },
            },
        });
        using var cancellation = new CancellationTokenSource();
        Task run = host.RunAsync(cancellation.Token);
        try
        {
            await WaitUntil(() => source.RefreshCount >= 2);
            source.Observations.Enqueue(Observation(3));
            await facts.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await WaitUntil(() => host.LastSnapshot?.Selection?.Phase == "provider identities");
            Assert.Equal(CtDaemonActivity.Selecting, host.LastSnapshot!.Activity);
            Assert.Equal(workspace.ProjectPath, host.LastSnapshot.Selection!.ProjectPath);
            Assert.Equal(new CtFreshnessKey(EngineTestSupport.Identity, 3), host.LastSnapshot.Selection.Freshness);
            Assert.Equal(5000, facts.PathCount);
            CtDaemonSelectionProgress progress = host.LastSnapshot.Selection;
            int polls = source.RefreshCount;
            var requestClock = System.Diagnostics.Stopwatch.StartNew();
            CtDaemonCommandRequest? mainRequest = targetWorktree
                ? CtCommandChannel.WriteRequest(_root, CtDaemonCommandKind.Run, "main projects", new CtFreshnessKey(EngineTestSupport.Identity, 3))
                : null;
            CtDaemonCommandRequest request = CtDaemonRouting.WriteRoutedRequest(_root, CtDaemonCommandKind.Run,
                reason: "second request", freshness: new CtFreshnessKey(EngineTestSupport.Identity, 3),
                targetWorkspaceRoot: targetWorktree ? worktreeRoot : _root);
            await WaitUntil(() => CtCommandChannel.TryReadAck(_root, request.CommandId) is not null);
            double ackMilliseconds = requestClock.Elapsed.TotalMilliseconds;
            Assert.False(facts.Release.IsSet);
            await WaitUntil(() => source.RefreshCount > polls + 2);
            Assert.Equal(progress.ProgressTimestampUtc, host.LastSnapshot!.Selection!.ProgressTimestampUtc);
            Assert.Equal(progress.ItemsProcessed, host.LastSnapshot.Selection.ItemsProcessed);
            Assert.Empty(provider.RunRequests);
            Assert.Empty(linkedProvider.RunRequests);
            facts.Release.Set();
            FakeContinuousTestProvider requestedProvider = targetWorktree ? linkedProvider : provider;
            ContinuousTestProviderRunRequest selected = await requestedProvider.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(targetWorktree ? linked.WorkspaceId : workspace.WorkspaceId, selected.Workspace.WorkspaceId);
            Assert.Contains(targetWorktree ? "test:linked" : "test:app", selected.TestCaseIds);
            if (mainRequest is not null)
            {
                await discovery.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                await WaitUntil(() => CtCommandChannel.TryReadAck(_root, mainRequest.CommandId)?.State == CtDaemonCommandState.Completed);
                CtDaemonCommandAck ack = CtCommandChannel.TryReadAck(_root, mainRequest.CommandId)!;
                Assert.Contains(ack.ProjectRuns!, project => project.ProjectPath == brokenProject);
                Assert.Contains(ack.ProjectRuns!, project => project.ProjectPath == workspace.ProjectPath);
            }
            await WaitUntil(() => CtCommandChannel.TryReadAck(_root, request.CommandId)?.State == CtDaemonCommandState.Completed);
            string ackJson = System.Text.Json.JsonSerializer.Serialize(CtCommandChannel.TryReadAck(_root, request.CommandId));
            _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                targetWorktree,
                selectorFails,
                changedPaths = facts.PathCount,
                requestToAckMilliseconds = ackMilliseconds,
                requestToCompletionMilliseconds = requestClock.Elapsed.TotalMilliseconds,
                ackBytes = System.Text.Encoding.UTF8.GetByteCount(ackJson),
                selectorSymbolReads = facts.SymbolReads,
                discoveryFailureObserved = discovery.Failed.Task.IsCompleted,
            }));
        }
        finally
        {
            facts.Release.Set();
            await cancellation.CancelAsync();
            await run;
        }
    }

    private static ContinuousTestRevisionObservation Observation(long revision) => new(
        EngineTestSupport.WorkspaceId, new CtFreshnessKey(EngineTestSupport.Identity, revision),
        true, "fresh", DateTimeOffset.UtcNow);

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(5, bound.Token);
    }

    private sealed class DiscoveryFailureProvider(FakeContinuousTestProvider inner, string brokenProject) : IContinuousTestProvider
    {
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(ContinuousTestWorkspace workspace, CancellationToken cancellationToken = default)
        {
            if (workspace.ProjectPath != brokenProject)
                return inner.DiscoverAsync(workspace, cancellationToken);
            Failed.TrySetResult();
            throw new ContinuousTestProviderException("injected discovery failure");
        }

        public Task<ProviderRunResult> RunAsync(ContinuousTestProviderRunRequest request, CancellationToken cancellationToken = default) =>
            inner.RunAsync(request, cancellationToken);
    }

    private sealed class BlockingFacts : IMillerFactSource, IDisposable
    {
        private readonly FakeMillerFactSource _inner = new() { Current = new CtIndexCursor(EngineTestSupport.Identity, 3) };
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public int PathCount { get; private set; }
        public bool ThrowAfterRelease { get; init; }
        public int SymbolReads { get; private set; }
        public CtIndexCursor Current => _inner.Current;

        public BlockingFacts()
        {
            _inner.Symbols.Add(FakeMillerFactSource.Symbol("sym:app", "App", "src/App.cs"));
            _inner.Tests.Add(FakeMillerFactSource.Hit("test:app", "test:app", "tests/AppTests.cs", true));
            _inner.FileFacts.Add(new CtFileFact("src/App.cs", "csharp", "hash", "indexed", false, true));
        }

        public IReadOnlyList<CtSymbolFact> SymbolsForChangedFiles(IReadOnlyList<string> paths)
        {
            SymbolReads++;
            PathCount = paths.Count;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Selection test gate was not released");
            if (ThrowAfterRelease)
                throw new InvalidOperationException("injected automatic selection failure");
            return _inner.SymbolsForChangedFiles(paths);
        }

        public IReadOnlyList<CtFileFact> FileFactsForPaths(IReadOnlyList<string> paths) => _inner.FileFactsForPaths(paths);
        public IReadOnlyList<CtReferenceFact> ReferencesTo(IReadOnlyList<string> ids) => _inner.ReferencesTo(ids);
        public IReadOnlyList<CtReferenceFact> IdentifierEvidenceTo(IReadOnlyList<string> ids) => _inner.IdentifierEvidenceTo(ids);
        public CtImpactResult Impact(IReadOnlyList<string> ids, int maxDepth = 2, int limit = 100) => _inner.Impact(ids, maxDepth, limit);
        public void Dispose() => Release.Dispose();
    }
}
