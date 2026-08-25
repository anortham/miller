using Microsoft.Data.Sqlite;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

/// <summary>
/// Task 6, the NCrunch loop: a change is observed, its staleness is recorded at once, and the
/// impacted set EXECUTES automatically after the debounce quiet period. The timer resets on each
/// newly observed change, so a save burst coalesces to one run. A change observed while a run
/// executes queues a follow-up selection; it never kills the running suite.
/// </summary>
public sealed class ContinuousTestDebouncedAutoRunTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-debounce-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(null, 2.0)]
    [InlineData("", 2.0)]
    [InlineData("   ", 2.0)]
    [InlineData("0", 0.0)]
    [InlineData("7", 7.0)]
    [InlineData("1.5", 1.5)]
    [InlineData("-3", 2.0)]
    [InlineData("abc", 2.0)]
    [InlineData("1e2", 2.0)]
    [InlineData("999999", 2.0)]
    public void Debounce_setting_parses_seconds_and_falls_back_to_the_default(string? raw, double seconds) =>
        Assert.Equal(
            TimeSpan.FromSeconds(seconds),
            ContinuousTestRevisionPoller.ResolveDebounceDelay(raw));

    [Fact]
    public void Default_debounce_is_two_seconds()
    {
        // Eight 250 ms poll ticks: long enough to coalesce a multi-file save burst, short enough
        // to keep the edit-to-verdict loop tight. Recorded here so a drive-by change to the
        // constant is a deliberate contract change, not an accident.
        Assert.Equal(TimeSpan.FromSeconds(2), ContinuousTestRevisionPoller.DefaultDebounceDelay);
        Assert.Equal("MILLER_CT_DEBOUNCE", ContinuousTestRevisionPoller.DebounceEnvironmentVariable);
    }

    [Fact]
    public async Task The_poller_stamps_its_debounce_on_every_enqueued_change()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(Observation(2));
        var impact = new ScriptedImpactSource { Result = Changed(3) };
        var enqueuer = new RecordingEnqueuer();
        var poller = new ContinuousTestRevisionPoller(source, impact, debounceDelay: TimeSpan.FromSeconds(3));

        await poller.PollAsync(PollRequest(workspace, enqueuer), TestContext.Current.CancellationToken);
        source.Observations.Enqueue(Observation(3));
        await poller.PollAsync(
            PollRequest(workspace, enqueuer, armed: true),
            TestContext.Current.CancellationToken);

        ContinuousTestDaemonChange change = Assert.Single(enqueuer.Changes);
        Assert.Equal(TimeSpan.FromSeconds(3), change.DebounceDelay);
    }

    [Fact]
    public async Task A_request_debounce_override_beats_the_poller_default()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(Observation(2));
        var impact = new ScriptedImpactSource { Result = Changed(3) };
        var enqueuer = new RecordingEnqueuer();
        var poller = new ContinuousTestRevisionPoller(source, impact, debounceDelay: TimeSpan.FromSeconds(3));

        await poller.PollAsync(
            PollRequest(workspace, enqueuer, debounce: TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken);
        source.Observations.Enqueue(Observation(3));
        await poller.PollAsync(
            PollRequest(workspace, enqueuer, armed: true, debounce: TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken);

        ContinuousTestDaemonChange change = Assert.Single(enqueuer.Changes);
        Assert.Equal(TimeSpan.FromSeconds(1), change.DebounceDelay);
    }

    [Fact]
    public async Task A_change_records_staleness_at_once_but_executes_only_after_the_quiet_period()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        CommitGreen(store, "test:app", 2);
        var provider = new FakeContinuousTestProvider { RunResult = Passed("test:app", "3") };
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 3);

        queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "3", from: 2, to: 3, debounce: TimeSpan.FromSeconds(2), observedAt: T0));

        // Staleness lands at the observation, honest while the debounce counts down.
        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(EngineTestSupport.WorkspaceId));
        Assert.Equal(ContinuousTestState.Stale, status.State);

        // Not ready inside the quiet period; nothing executes.
        DateTimeOffset early = T0 + TimeSpan.FromMilliseconds(1900);
        Assert.False(queue.HasReadyWork(early));
        Assert.Empty(await queue.DrainReadyAsync(early, TestContext.Current.CancellationToken));
        Assert.Empty(provider.RunRequests);

        // Exactly one run once the quiet period elapses.
        DateTimeOffset due = T0 + TimeSpan.FromSeconds(2);
        Assert.True(queue.HasReadyWork(due));
        IReadOnlyList<ContinuousTestDaemonDrainResult> results =
            await queue.DrainReadyAsync(due, TestContext.Current.CancellationToken);
        Assert.Single(results);
        ContinuousTestProviderRunRequest run = Assert.Single(provider.RunRequests);
        Assert.Contains("test:app", run.TestCaseIds);
    }

    [Fact]
    public async Task A_save_burst_resets_the_timer_and_coalesces_into_one_run()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new FakeContinuousTestProvider { RunResult = Passed("test:app", "4") };
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 4);

        queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "3", from: 2, to: 3, debounce: TimeSpan.FromSeconds(2), observedAt: T0));
        queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "4", from: 3, to: 4, debounce: TimeSpan.FromSeconds(2),
            observedAt: T0 + TimeSpan.FromSeconds(1)));

        // The second save reset the timer: the first deadline passes with no run.
        DateTimeOffset firstDeadline = T0 + TimeSpan.FromSeconds(2);
        Assert.False(queue.HasReadyWork(firstDeadline));
        Assert.Empty(await queue.DrainReadyAsync(firstDeadline, TestContext.Current.CancellationToken));
        Assert.Empty(provider.RunRequests);

        // One run for the whole burst at the reset deadline.
        DateTimeOffset resetDeadline = T0 + TimeSpan.FromSeconds(3);
        IReadOnlyList<ContinuousTestDaemonDrainResult> results =
            await queue.DrainReadyAsync(resetDeadline, TestContext.Current.CancellationToken);
        Assert.Single(results);
        Assert.Single(provider.RunRequests);
    }

    [Fact]
    public async Task A_change_during_execution_queues_a_follow_up_and_never_kills_the_run()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new MidRunEnqueueProvider { RunResult = Passed("test:app", "3") };
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 4);
        DateTimeOffset midRun = T0 + TimeSpan.FromMilliseconds(100);
        provider.OnFirstRun = () => queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "4", from: 3, to: 4, debounce: TimeSpan.FromSeconds(1), observedAt: midRun));

        queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "3", from: 2, to: 3, debounce: TimeSpan.Zero, observedAt: T0));
        IReadOnlyList<ContinuousTestDaemonDrainResult> first =
            await queue.DrainReadyAsync(T0, TestContext.Current.CancellationToken);

        // The running suite completed normally; the mid-run change did not kill it.
        ContinuousTestDaemonDrainResult completed = Assert.Single(first);
        Assert.Equal("passed", completed.CoordinatorResult.ProviderResult.Status);

        // The follow-up waits out its own quiet period, then runs.
        DateTimeOffset followUpDue = midRun + TimeSpan.FromSeconds(1);
        Assert.True(queue.HasReadyWork(followUpDue));
        IReadOnlyList<ContinuousTestDaemonDrainResult> second =
            await queue.DrainReadyAsync(followUpDue, TestContext.Current.CancellationToken);
        Assert.Single(second);
        Assert.Equal(2, provider.RunCount);
    }

    [Theory]
    [InlineData("other/CMakeLists.txt")]
    [InlineData("other/tests/runner.cpp")]
    public void A_production_qml_change_request_does_not_select_a_sibling_project(string changedPath)
    {
        string projectPath = Path.Combine(_root, "qml", "CMakeLists.txt");
        var workspace = EngineTestSupport.Workspace(_root, projectPath);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(new ContinuousTestCase(
            Id: "tc:qml",
            WorkspaceId: EngineTestSupport.WorkspaceId,
            Name: "Smoke/smoke",
            QualifiedName: "Smoke/smoke",
            Selector: "Smoke/smoke",
            FilePath: Path.Combine(_root, "qml", "tests"),
            Framework: "qt-quick-test",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:qml",
            Confidence: 1.0,
            Metadata: new Dictionary<string, object?>
            {
                ["ct_project_path"] = projectPath,
                ["source_path"] = Path.Combine(_root, "qml", "tests"),
            }));
        var provider = new FakeContinuousTestProvider { RunResult = Passed("tc:qml", "3") };
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 3);

        ContinuousTestDaemonEnqueueResult result = queue.Enqueue(EngineTestSupport.Change(
            workspace,
            revision: "3",
            changedPaths: [Path.Combine(_root, changedPath)],
            from: 2,
            to: 3));

        Assert.Empty(result.Selection.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Selection.Outcome);
        Assert.Empty(provider.RunRequests);
    }

    [Fact]
    public async Task Zero_debounce_executes_on_the_same_tick()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new FakeContinuousTestProvider { RunResult = Passed("test:app", "3") };
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 3);

        queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "3", from: 2, to: 3, debounce: TimeSpan.Zero, observedAt: T0));

        Assert.True(queue.HasReadyWork(T0));
        Assert.Single(await queue.DrainReadyAsync(T0, TestContext.Current.CancellationToken));
        Assert.Single(provider.RunRequests);
    }

    [Fact]
    public async Task A_flaky_retry_key_is_evicted_after_the_retry_completes()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        CommitOutcome(store, "test:app", 1, "passed");
        CommitOutcome(store, "test:app", 2, "failed");
        CommitOutcome(store, "test:app", 3, "passed");
        CommitOutcome(store, "test:app", 4, "failed");
        Assert.Single(store.ListContinuousTestStatuses(EngineTestSupport.WorkspaceId));
        var provider = new FlakyRetryProvider("test:app");
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 5);

        ContinuousTestDaemonEnqueueResult enqueued = queue.EnqueueExplicit(EngineTestSupport.Change(
            workspace,
            revision: "5",
            workspaceScope: true,
            completeness: ContinuousTestDeltaCompleteness.Unavailable,
            observedAt: T0));
        Assert.Equal(ContinuousTestSelectionOutcome.WorkspaceScope, enqueued.Selection.Outcome);
        Assert.Equal(["test:app"], enqueued.Selection.SelectedTestCaseIds);
        Assert.Equal(T0, enqueued.Pending.ReadyAt);
        Assert.Single(store.ListContinuousTestStatuses(EngineTestSupport.WorkspaceId));

        IReadOnlyList<ContinuousTestDaemonDrainResult> firstDrain =
            await queue.DrainReadyAsync(T0, TestContext.Current.CancellationToken);
        Assert.Single(firstDrain);
        Assert.Single(await queue.DrainReadyAsync(T0, TestContext.Current.CancellationToken));
        Assert.Equal(2, provider.RunCount);

        var retryAttempts = (System.Collections.IDictionary?)typeof(ContinuousTestDaemonQueue)
            .GetField("_retryAttempts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(queue);
        Assert.NotNull(retryAttempts);
        Assert.Empty(retryAttempts);
    }

    [Fact]
    public async Task An_unknown_selection_never_becomes_ready_work_even_after_the_quiet_period()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new FakeContinuousTestProvider();
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 3);

        ContinuousTestDaemonEnqueueResult result = queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "3", from: 2, to: 3, changedPaths: ["src/Mystery.xyz"],
            debounce: TimeSpan.FromSeconds(2), observedAt: T0));

        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Selection.Outcome);
        Assert.False(queue.HasReadyWork(T0 + TimeSpan.FromHours(1)));
        Assert.Empty(await queue.DrainReadyAsync(T0 + TimeSpan.FromHours(1), TestContext.Current.CancellationToken));
        Assert.Empty(provider.RunRequests);
    }

    [Fact]
    public async Task A_known_empty_selection_never_becomes_ready_work()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var facts = new FakeMillerFactSource
        {
            Current = new CtIndexCursor(EngineTestSupport.Identity, 3),
        };
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:persistence", "Persist", "src/Persistence.cs"));
        var provider = new FakeContinuousTestProvider();
        var queue = new ContinuousTestDaemonQueue(
            store,
            new ContinuousTestImpactSelector(store, facts),
            new ContinuousTestCoordinator(provider, store));

        ContinuousTestDaemonEnqueueResult result = queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "3", from: 2, to: 3, changedPaths: ["src/Persistence.cs"],
            debounce: TimeSpan.FromSeconds(2), observedAt: T0));

        Assert.Equal(ContinuousTestSelectionOutcome.KnownEmpty, result.Selection.Outcome);
        Assert.False(queue.HasReadyWork(T0 + TimeSpan.FromHours(1)));
        Assert.Empty(await queue.DrainReadyAsync(T0 + TimeSpan.FromHours(1), TestContext.Current.CancellationToken));
        Assert.Empty(provider.RunRequests);
    }

    /// <summary>
    /// Review finding F1: a pending run enqueued at an older revision must not execute after a
    /// NEWER observation with an Unknown selection superseded it. Unknown means everything at the
    /// new key is stale and nothing may execute, so the older pending's id list has no standing
    /// there — the queue must drop it, not run it at the stale key.
    /// </summary>
    [Fact]
    public async Task A_pending_run_does_not_survive_a_newer_unknown_advance()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new FakeContinuousTestProvider { RunResult = Passed("test:app", "3") };
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 4);

        ContinuousTestDaemonEnqueueResult first = queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "3", from: 2, to: 3, debounce: TimeSpan.FromSeconds(2), observedAt: T0));
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, first.Selection.Outcome);

        ContinuousTestDaemonEnqueueResult second = queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "4", from: 3, to: 4, changedPaths: ["src/Mystery.xyz"],
            debounce: TimeSpan.FromSeconds(2), observedAt: T0 + TimeSpan.FromSeconds(1)));
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, second.Selection.Outcome);

        DateTimeOffset longAfter = T0 + TimeSpan.FromHours(1);
        Assert.False(queue.HasReadyWork(longAfter));
        Assert.Empty(await queue.DrainReadyAsync(longAfter, TestContext.Current.CancellationToken));
        Assert.Empty(provider.RunRequests);
    }

    /// <summary>
    /// Review finding F1, the KnownEmpty side: a doc-only save (a complete impact read that
    /// reaches no test) SUPERSEDES the pending, it does not cancel it. The owed run survives,
    /// re-keyed to the new observation, and its debounce resets trailing-edge like any other save.
    /// </summary>
    [Fact]
    public async Task A_known_empty_advance_rekeys_the_pending_run_to_the_new_observation()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var facts = new FakeMillerFactSource { Current = new CtIndexCursor(EngineTestSupport.Identity, 4) };
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:app", "App", "src/App.cs"));
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:persistence", "Persist", "src/Persistence.cs"));
        facts.Tests.Add(FakeMillerFactSource.Hit("test:app", "AppTests", "tests/AppTests.cs", isTest: true));
        var provider = new FakeContinuousTestProvider { RunResult = Passed("test:app", "4") };
        var queue = new ContinuousTestDaemonQueue(
            store,
            new ContinuousTestImpactSelector(store, facts),
            new ContinuousTestCoordinator(provider, store));

        ContinuousTestDaemonEnqueueResult impacted = queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "3", from: 2, to: 3, debounce: TimeSpan.FromSeconds(2), observedAt: T0));
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, impacted.Selection.Outcome);

        ContinuousTestDaemonEnqueueResult knownEmpty = queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "4", from: 3, to: 4, changedPaths: ["src/Persistence.cs"],
            debounce: TimeSpan.FromSeconds(2), observedAt: T0 + TimeSpan.FromSeconds(1)));
        Assert.Equal(ContinuousTestSelectionOutcome.KnownEmpty, knownEmpty.Selection.Outcome);

        // The doc-only save reset the trailing-edge timer: the first deadline passes quiet.
        DateTimeOffset firstDeadline = T0 + TimeSpan.FromSeconds(2);
        Assert.False(queue.HasReadyWork(firstDeadline));
        Assert.Empty(await queue.DrainReadyAsync(firstDeadline, TestContext.Current.CancellationToken));
        Assert.Empty(provider.RunRequests);

        // The owed run survives, re-keyed to the NEW observation.
        DateTimeOffset resetDeadline = T0 + TimeSpan.FromSeconds(3);
        IReadOnlyList<ContinuousTestDaemonDrainResult> results =
            await queue.DrainReadyAsync(resetDeadline, TestContext.Current.CancellationToken);
        ContinuousTestDaemonDrainResult result = Assert.Single(results);
        Assert.Equal("4", result.Pending.CurrentRevision);
        ContinuousTestProviderRunRequest run = Assert.Single(provider.RunRequests);
        Assert.Contains("test:app", run.TestCaseIds);
    }

    /// <summary>
    /// The whole fixed observation-to-execution path, through the daemon loop itself: the first
    /// poll arms the poller, the second observes a new revision with a complete changed delta,
    /// and the daemon executes the impacted set with no explicit run command.
    /// </summary>
    [Fact]
    public async Task A_new_revision_auto_runs_the_impacted_set_through_the_daemon_loop()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new FakeContinuousTestProvider { RunResult = Passed("test:app", "3") };
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 3);
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(Observation(2));
        var impact = new ScriptedImpactSource { Result = Changed(3) };
        var poller = new ContinuousTestRevisionPoller(source, impact, debounceDelay: TimeSpan.Zero);
        var delay = new ManualDelay();
        var host = new ContinuousTestDaemonHost(
            workspace.WorkspaceRoot,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                Store = store,
                Queue = queue,
                Poller = poller,
                Projects = [new ContinuousTestProject("proj:1", EngineTestSupport.WorkspaceId, workspace.ProjectPath, Framework: "xunit")],
                Budget = CtExecutionBudget.Disabled(),
                AcquireLease = false,
                Clock = () => DateTimeOffset.UtcNow,
                Delay = delay.DelayAsync,
            });

        using var cancellation = new CancellationTokenSource();
        Task run = host.RunAsync(cancellation.Token);
        await delay.WaitForDelayCountAsync(1, TestContext.Current.CancellationToken);
        source.Observations.Enqueue(Observation(3));
        delay.CompleteNext();
        await WaitUntil(() => provider.RunRequests.Count == 1, TestContext.Current.CancellationToken);

        ContinuousTestProviderRunRequest request = provider.RunRequests[0];
        Assert.Contains("test:app", request.TestCaseIds);

        cancellation.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task An_explicit_workspace_run_merged_over_impact_pending_remains_whole_suite_eligible()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        store.PutTestCase(EngineTestSupport.Case("test:other", workspace.ProjectPath, "tests/OtherTests.cs"));
        var provider = new FakeContinuousTestProvider
        {
            DiscoverCases = ProviderCases("test:app", "test:other"),
            RunResult = PassedMany("2", "test:app", "test:other"),
        };
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 2);

        ContinuousTestDaemonEnqueueResult impacted = queue.Enqueue(
            EngineTestSupport.Change(workspace, revision: "2", debounce: TimeSpan.Zero));
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, impacted.Selection.Outcome);

        queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            workspace,
            "2",
            EngineTestSupport.Identity,
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow));
        IReadOnlyList<ContinuousTestDaemonDrainResult> drained =
            await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(provider.RunRequests).WholeSuite);
        ContinuousTestDaemonSelectionFacts facts = Assert.Single(drained).SelectionFacts;
        Assert.Equal(ContinuousTestSelectionOutcome.WorkspaceScope, facts.Scope);
        Assert.True(facts.Eligible);
        Assert.Equal("eligible", facts.ReasonCode);
    }

    private static ContinuousTestDaemonQueue Queue(
        ContinuousTestStore store,
        IContinuousTestProvider provider,
        long revision) =>
        new(
            store,
            EngineTestSupport.Selector(store, revision: revision),
            new ContinuousTestCoordinator(provider, store));

    private static ContinuousTestRevisionPollRequest PollRequest(
        ContinuousTestWorkspace workspace,
        IContinuousTestDaemonEnqueuer enqueuer,
        bool armed = false,
        TimeSpan? debounce = null) =>
        new(
            EngineTestSupport.WorkspaceId,
            workspace.WorkspaceRoot,
            [new ContinuousTestProject("proj:1", EngineTestSupport.WorkspaceId, workspace.ProjectPath, Framework: "xunit")],
            enqueuer,
            DebounceDelay: debounce,
            EnqueueArmed: armed);

    private static ContinuousTestRevisionObservation Observation(long revision) =>
        new(
            EngineTestSupport.WorkspaceId,
            new CtFreshnessKey(EngineTestSupport.Identity, revision),
            IndexFresh: true,
            Status: "fresh",
            ObservedAt: DateTimeOffset.UtcNow);

    private static ContinuousTestImpactResult Changed(long to) =>
        new(
            EngineTestSupport.WorkspaceId,
            ["src/App.cs"],
            [],
            [])
        {
            Outcome = ContinuousTestImpactOutcome.Changed,
            FromRevision = to - 1,
            ToRevision = to,
        };

    private static ProviderRunResult Passed(string testCaseId, string revision) =>
        new(
            "run:1",
            "passed",
            CaseResults:
            [
                new ProviderCaseResult("r1", testCaseId, "passed", revision, EngineTestSupport.Identity),
            ]);

    private static ProviderRunResult PassedMany(string revision, params string[] testCaseIds) =>
        new(
            "run:1",
            "passed",
            CaseResults: testCaseIds
                .Select((testCaseId, index) => new ProviderCaseResult(
                    $"r{index}",
                    testCaseId,
                    "passed",
                    revision,
                    EngineTestSupport.Identity))
                .ToArray());

    private static IReadOnlyList<ProviderTestCase> ProviderCases(params string[] testCaseIds) =>
        testCaseIds
            .Select(testCaseId => new ProviderTestCase(
                testCaseId,
                testCaseId,
                $"App.Tests.{testCaseId}",
                $"App.Tests.{testCaseId}",
                Framework: "xunit",
                SourcePath: testCaseId == "test:other"
                    ? "tests/OtherTests.cs"
                    : "tests/AppTests.cs"))
            .ToArray();

    private static void CommitGreen(ContinuousTestStore store, string testCaseId, long revision)
    {
        string revisionText = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string runId = "seed-run:" + testCaseId + ":" + revisionText;
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: EngineTestSupport.WorkspaceId,
                Status: "running",
                SelectedRevision: revisionText,
                IndexIdentity: EngineTestSupport.Identity,
                Revision: revision),
            [testCaseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: EngineTestSupport.WorkspaceId,
            TestRunId: runId,
            SelectedRevision: revisionText,
            CurrentRevision: revisionText,
            IndexIdentity: EngineTestSupport.Identity,
            Revision: revision,
            Status: "passed",
            Results:
            [
                new ContinuousTestResult(
                    Id: runId + ":result",
                    WorkspaceId: EngineTestSupport.WorkspaceId,
                    TestCaseId: testCaseId,
                    TestRunId: runId,
                    Status: "passed",
                    ResultRevision: revisionText,
                    IndexIdentity: EngineTestSupport.Identity,
                    Revision: revision),
            ]));
    }

    private static void CommitOutcome(
        ContinuousTestStore store,
        string testCaseId,
        long revision,
        string status)
    {
        string revisionText = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string runId = "seed-run:" + testCaseId + ":" + revisionText;
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: EngineTestSupport.WorkspaceId,
                Status: "running",
                SelectedRevision: revisionText,
                IndexIdentity: EngineTestSupport.Identity,
                Revision: revision),
            [testCaseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: EngineTestSupport.WorkspaceId,
            TestRunId: runId,
            SelectedRevision: revisionText,
            CurrentRevision: revisionText,
            IndexIdentity: EngineTestSupport.Identity,
            Revision: revision,
            Status: status,
            Results:
            [
                new ContinuousTestResult(
                    Id: runId + ":result",
                    WorkspaceId: EngineTestSupport.WorkspaceId,
                    TestCaseId: testCaseId,
                    TestRunId: runId,
                    Status: status,
                    ResultRevision: revisionText,
                    IndexIdentity: EngineTestSupport.Identity,
                    Revision: revision),
            ]));
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("condition was not met");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A provider that enqueues a fresh change DURING its first run, the way a poller
    /// observation lands while a suite executes.</summary>
    private sealed class MidRunEnqueueProvider : IContinuousTestProvider
    {
        public Action? OnFirstRun { get; set; }

        public ProviderRunResult? RunResult { get; set; }

        public int RunCount { get; private set; }

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderTestCase>>([]);

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            if (RunCount == 1)
                OnFirstRun?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RunResult ?? new ProviderRunResult(request.RunId ?? "run:1", "passed"));
        }
    }

    private sealed class FlakyRetryProvider(string testCaseId) : IContinuousTestProvider
    {
        public int RunCount { get; private set; }

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderTestCase>>
            ([new ProviderTestCase(
                testCaseId,
                testCaseId,
                testCaseId,
                testCaseId,
                Framework: workspace.Framework,
                SourcePath: "tests/AppTests.cs")]);

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            string status = RunCount == 1 ? "failed" : "passed";
            return Task.FromResult(new ProviderRunResult(
                request.RunId ?? "run:1",
                status,
                CaseResults:
                [
                    new ProviderCaseResult(
                        $"result:{RunCount}",
                        testCaseId,
                        status,
                        request.SelectedRevision,
                        EngineTestSupport.Identity),
                ]));
        }
    }
}
