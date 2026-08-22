using Miller.Testing;
using Miller.Tests.Testing.Daemon.Engine;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Testing.Analysis;

/// <summary>
/// A run that covers every known test case is handed to the provider as the FULL selection plus a
/// <c>WholeSuite</c> flag, so the provider may run the whole assembly once under its seeded trait
/// exclusions without losing the plan.
///
/// <para>Both forms express the same run; only the cost differs. A per-case selection becomes one
/// <c>-method</c> pair per id, and Miller's own ~6,000 cases then exceed the command-line limit and split into
/// roughly 50 processes, each paying host startup and discovery again — 6+ minutes for a subset that
/// <c>dotnet test</c> runs in 25 seconds.</para>
///
/// <para>The flag replaced an EMPTY id list. Blanking the list read as "run everything" only to providers
/// that attribute from a result artifact; the cargo provider's run loop is driven by the list itself, so it
/// started no process, reported "passed" over zero results, and left 4,173 cases stale forever (dogfood
/// finding F6, 2026-08-21).</para>
///
/// <para>The dangerous half is bookkeeping, not speed. The run must still RECORD that it covered every case,
/// or freshness at the composite <c>(index_identity, revision)</c> key goes quietly wrong and a complete run
/// looks like it selected nothing. So these tests assert what the provider is told AND what the store
/// records.</para>
/// </summary>
public sealed class ContinuousTestWholeSuiteRunTests : IDisposable
{
    private const string WorkspaceId = "ws:whole-suite";
    private const string Identity = "gen-1";

    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-whole-suite-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_root + "-build", recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The flag travels; the plan does NOT get taken away. A provider whose run loop is driven by the id
    /// list must still be able to execute the run it was asked for.
    /// </summary>
    [Fact]
    public async Task A_whole_suite_run_hands_the_provider_the_full_selection_and_the_flag()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:a", "test:b", "test:c");
        var provider = new RecordingProvider();
        var coordinator = new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1");

        await coordinator.RunSelectedAsync(
            RunRequest(workspace, ["test:a", "test:b", "test:c"], wholeSuite: true),
            TestContext.Current.CancellationToken);

        ContinuousTestProviderRunRequest sent = Assert.Single(provider.Requests);
        Assert.Equal(["test:a", "test:b", "test:c"], sent.TestCaseIds);
        Assert.True(sent.WholeSuite);
    }

    /// <summary>
    /// FAIL-SAFE. A provider that reports a verdict for NOTHING it was asked to run knows nothing about
    /// those tests, so the coordinator must not record a completed run. This is the net that would have
    /// caught F6 on the first cargo run instead of after a 3.5-minute "passed" that executed no process.
    /// </summary>
    [Fact]
    public async Task A_run_that_reports_no_result_for_a_non_empty_selection_is_not_recorded_passed()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:a", "test:b");
        var coordinator = new ContinuousTestCoordinator(
            new SilentProvider(), store, runIdFactory: static () => "run:1");

        await Assert.ThrowsAsync<ContinuousTestProviderException>(() => coordinator.RunSelectedAsync(
            RunRequest(workspace, ["test:a", "test:b"], wholeSuite: true),
            TestContext.Current.CancellationToken));

        Assert.DoesNotContain(
            store.ListTestRuns(WorkspaceId),
            row => string.Equals(row.Status, "passed", StringComparison.OrdinalIgnoreCase));
        Assert.All(
            store.ListContinuousTestStatuses(WorkspaceId),
            row => Assert.NotEqual(ContinuousTestState.Green, row.State));
    }

    /// <summary>
    /// The whole-suite form must not cost the run its record of what it covered. The store still has to show
    /// all three cases selected at this key, because "green requires complete results at the selected
    /// composite key" is judged from exactly those rows.
    /// </summary>
    [Fact]
    public async Task A_whole_suite_run_still_records_every_case_it_covered()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:a", "test:b", "test:c");
        var coordinator = new ContinuousTestCoordinator(
            new RecordingProvider(), store, runIdFactory: static () => "run:1");

        await coordinator.RunSelectedAsync(
            RunRequest(workspace, ["test:a", "test:b", "test:c"], wholeSuite: true),
            TestContext.Current.CancellationToken);

        IReadOnlyList<ContinuousTestStatus> statuses = store.ListContinuousTestStatuses(WorkspaceId);
        Assert.Equal(
            new[] { "test:a", "test:b", "test:c" },
            statuses.Select(row => row.TestCaseId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The default is unchanged. A partial selection is still passed through case by case — running a whole
    /// assembly for three tests out of six thousand is the same mistake in the other direction.
    /// </summary>
    [Fact]
    public async Task A_partial_run_still_hands_the_provider_its_case_list()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:a", "test:b", "test:c");
        var provider = new RecordingProvider();
        var coordinator = new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1");

        await coordinator.RunSelectedAsync(
            RunRequest(workspace, ["test:b"], wholeSuite: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(["test:b"], Assert.Single(provider.Requests).TestCaseIds);
    }

    [Fact]
    public async Task A_run_propagates_provider_source_and_progress_callbacks()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:a");
        var provider = new RecordingProvider { InvokeProgress = true };
        var coordinator = new ContinuousTestCoordinator(
            new FixedContinuousTestProviderResolver(provider, "ct-provider:fixture"),
            store,
            runIdFactory: static () => "run:1");
        string? resolvedSource = null;
        ContinuousTestProviderChunkProgress? progress = null;

        ContinuousTestCoordinatorRunResult result = await coordinator.RunSelectedAsync(
            RunRequest(workspace, ["test:a"], wholeSuite: false) with
            {
                ProviderResolved = resolution => resolvedSource = resolution.ProviderSource,
                Progress = value => progress = value,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("ct-provider:fixture", resolvedSource);
        Assert.Equal("ct-provider:fixture", result.ProviderSource);
        Assert.True(provider.SawProgressCallback);
        Assert.NotNull(progress);
        Assert.Equal(1, progress!.RequestedUniqueUnitCount);
    }

    /// <summary>
    /// Contract clause (e): an IMPACT-DERIVED selection that happens to cover every known case
    /// still travels as its explicit id list. Only a workspace-scope request (a real generation
    /// change, an explicit run) may collapse to the whole-suite form — an impacted set that merely
    /// equals the inventory is a coincidence, not an instruction to run the world.
    /// </summary>
    [Fact]
    public async Task An_impact_derived_selection_covering_every_known_case_still_runs_as_an_id_list()
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        // The engine selector resolves exactly one impacted test, `test:app`, so seeding only that case makes
        // the selection cover the whole known inventory.
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new RecordingProvider();
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        queue.Enqueue(EngineTestSupport.Change(workspace, debounce: TimeSpan.Zero));
        IReadOnlyList<ContinuousTestDaemonDrainResult> drained =
            await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(["test:app"], Assert.Single(provider.Requests).TestCaseIds);
        ContinuousTestDaemonSelectionFacts facts = Assert.Single(drained).SelectionFacts;
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, facts.Scope);
        Assert.Equal(1, facts.KnownCount);
        Assert.Equal(1, facts.PreTrimSelectedCount);
        Assert.Equal(1, facts.PostTrimSelectedCount);
        Assert.True(facts.CoversEveryKnownCase);
        Assert.False(facts.Eligible);
        Assert.Equal("impact_scope", facts.ReasonCode);
    }

    [Fact]
    public async Task An_empty_inventory_reports_inventory_empty_without_whole_suite()
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new RecordingProvider();
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        ContinuousTestDaemonEnqueueResult enqueued =
            queue.Enqueue(EngineTestSupport.Change(workspace, debounce: TimeSpan.Zero));
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, enqueued.Selection.Outcome);
        store.PutTestCase(EngineTestSupport.Case(
            "test:app",
            Path.Combine(_root, "other", "Other.Tests.csproj")));

        IReadOnlyList<ContinuousTestDaemonDrainResult> drained =
            await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        ContinuousTestDaemonSelectionFacts facts = Assert.Single(drained).SelectionFacts;
        Assert.Equal(0, facts.KnownCount);
        Assert.False(facts.CoversEveryKnownCase);
        Assert.False(facts.Eligible);
        Assert.Equal("inventory_empty", facts.ReasonCode);
    }

    /// <summary>
    /// The whole-suite fast path survives where it is legitimate: an explicit workspace-scope run
    /// whose stale set really is the whole inventory. The queue SETS the flag here; the tests above
    /// prove the coordinator honours it.
    /// </summary>
    [Fact]
    public async Task An_explicit_workspace_scope_run_with_everything_stale_is_whole_suite()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:a", "test:b", "test:c");
        var provider = new RecordingProvider
        {
            DiscoverCases = ProviderCases("test:a", "test:b", "test:c"),
        };
        var queue = new ContinuousTestDaemonQueue(
            store,
            SelectorFor(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            workspace,
            "2",
            Identity,
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        IReadOnlyList<ContinuousTestDaemonDrainResult> drained =
            await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        ContinuousTestProviderRunRequest sent = Assert.Single(provider.Requests);
        Assert.Equal(["test:a", "test:b", "test:c"], sent.TestCaseIds.Order(StringComparer.Ordinal).ToArray());
        Assert.True(sent.WholeSuite);
        ContinuousTestDaemonSelectionFacts facts = Assert.Single(drained).SelectionFacts;
        Assert.Equal(ContinuousTestSelectionOutcome.WorkspaceScope, facts.Scope);
        Assert.Equal(ContinuousTestRunLane.Foreground, facts.Lane);
        Assert.Equal(3, facts.KnownCount);
        Assert.Equal(3, facts.PreTrimSelectedCount);
        Assert.Equal(3, facts.PostTrimSelectedCount);
        Assert.Equal(0, facts.RetainedRedCount);
        Assert.True(facts.CoversEveryKnownCase);
        Assert.True(facts.Eligible);
        Assert.Equal("eligible", facts.ReasonCode);
        Assert.Equal("5b57e9b32e63762eae11cb57", facts.SelectionDigest);
    }

    [Fact]
    public async Task A_foreground_retry_reports_eligibility_gate_after_workspace_failure()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:a", "test:b");
        var provider = new RecordingProvider
        {
            DiscoverCases = ProviderCases("test:a", "test:b"),
            ThrowOnRun = true,
        };
        var queue = new ContinuousTestDaemonQueue(
            store,
            SelectorFor(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            workspace,
            "2",
            Identity,
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.Single(provider.Requests);

        provider.ThrowOnRun = false;
        IReadOnlyList<ContinuousTestDaemonDrainResult> drained =
            await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        ContinuousTestDaemonSelectionFacts facts = Assert.Single(drained).SelectionFacts;
        Assert.Equal(ContinuousTestSelectionOutcome.WorkspaceScope, facts.Scope);
        Assert.True(facts.CoversEveryKnownCase);
        Assert.False(facts.Eligible);
        Assert.Equal("eligibility_gate", facts.ReasonCode);
    }

    /// <summary>
    /// Explicit run contract: the run executes exactly the CURRENT stale set. A case committed
    /// fresh at the live key is neither re-marked stale nor re-run, and because the stale set is a
    /// strict subset of the inventory the run travels as an id list, never as a whole suite.
    /// </summary>
    [Fact]
    public async Task An_explicit_run_executes_only_the_stale_set()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:fresh", "test:stale");
        CommitResult(store, "test:fresh", Identity, 2, passed: true);
        var provider = new RecordingProvider
        {
            DiscoverCases = ProviderCases("test:fresh", "test:stale"),
        };
        var queue = new ContinuousTestDaemonQueue(
            store,
            SelectorFor(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        ContinuousTestDaemonEnqueueResult enqueued = queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            workspace,
            "2",
            Identity,
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        IReadOnlyList<ContinuousTestDaemonDrainResult> drained =
            await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        // The enqueue itself must not destroy the committed-fresh row.
        Assert.DoesNotContain("test:fresh", enqueued.Selection.StaleTestCaseIds);
        Assert.Equal(["test:stale"], Assert.Single(provider.Requests).TestCaseIds);
        ContinuousTestDaemonSelectionFacts facts = Assert.Single(drained).SelectionFacts;
        Assert.Equal(2, facts.KnownCount);
        Assert.Equal(1, facts.PreTrimSelectedCount);
        Assert.Equal(1, facts.PostTrimSelectedCount);
        Assert.False(facts.CoversEveryKnownCase);
        Assert.False(facts.Eligible);
        Assert.Equal("coverage_incomplete", facts.ReasonCode);
        ContinuousTestStatus fresh = Assert.Single(
            store.ListContinuousTestStatuses(WorkspaceId),
            row => row.TestCaseId == "test:fresh");
        Assert.Equal(ContinuousTestState.Green, fresh.State);
    }

    /// <summary>
    /// A user-requested run means "prove it again". A RED case committed at the live key is fresh by
    /// the committed rule, so the explicit run trimmed it away and executed nothing: the verdict
    /// stayed red, the stale count stayed 0, and `last_run` never moved however many times the user
    /// typed `tests run` (observed live 2026-08-21). A red has something to prove; a green does not.
    /// </summary>
    [Fact]
    public async Task An_explicit_run_retries_a_red_case_on_an_unchanged_tree()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:green", "test:red");
        CommitResult(store, "test:green", Identity, 2, passed: true);
        CommitResult(store, "test:red", Identity, 2, passed: false);
        var provider = new RecordingProvider
        {
            DiscoverCases = ProviderCases("test:green", "test:red"),
        };
        var queue = new ContinuousTestDaemonQueue(
            store,
            SelectorFor(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            workspace,
            "2",
            Identity,
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        IReadOnlyList<ContinuousTestDaemonDrainResult> drained =
            await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        // The red is re-run; the green beside it still has nothing to prove.
        Assert.Equal(["test:red"], Assert.Single(provider.Requests).TestCaseIds);
        ContinuousTestDaemonSelectionFacts facts = Assert.Single(drained).SelectionFacts;
        Assert.Equal(2, facts.KnownCount);
        Assert.Equal(1, facts.PreTrimSelectedCount);
        Assert.Equal(1, facts.PostTrimSelectedCount);
        Assert.Equal(1, facts.RetainedRedCount);
        Assert.False(facts.CoversEveryKnownCase);
        Assert.False(facts.Eligible);
        Assert.Equal("coverage_incomplete", facts.ReasonCode);
    }

    /// <summary>
    /// A red that passes on the retry becomes green at the live key, and the next revision advance
    /// then carries it forward on its own watermark. Without the second half the retry would fix the
    /// verdict for exactly one revision.
    /// </summary>
    [Fact]
    public async Task A_red_that_passes_on_retry_goes_green_and_rides_the_watermark()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:red");
        CommitResult(store, "test:red", Identity, 2, passed: false);
        var queue = new ContinuousTestDaemonQueue(
            store,
            SelectorFor(store),
            new ContinuousTestCoordinator(
                new RecordingProvider { DiscoverCases = ProviderCases("test:red") },
                store,
                runIdFactory: static () => "run:1"));

        queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            workspace,
            "2",
            Identity,
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        ContinuousTestStatus retried = Assert.Single(store.ListContinuousTestStatuses(WorkspaceId));
        Assert.Equal(ContinuousTestState.Green, retried.State);

        // The index advances with nothing impacted; the fresh green rides its watermark to the new key.
        store.ApplyRevisionAdvance(
            WorkspaceId,
            workspace.ProjectPath,
            new CtFreshnessKey(Identity, 2),
            new CtFreshnessKey(Identity, 3),
            [],
            ContinuousTestSelectionOutcome.KnownEmpty);

        IReadOnlyDictionary<string, CtFreshnessKey> watermarks =
            store.ListContinuousTestFreshWatermarks(WorkspaceId, Identity);
        Assert.True(
            watermarks.TryGetValue("test:red", out CtFreshnessKey watermark),
            "the repaired case never got a watermark, so it goes stale again on the next revision");
        Assert.Equal(3, watermark.Revision);
    }

    /// <summary>
    /// The rule is "every red", not "every red at the live key". A red recorded at an OLDER index
    /// identity is selected too — it was never fresh at the live key, so the trim would have kept it
    /// either way, and stating the narrower rule in the contract would describe a filter that does
    /// not exist.
    /// </summary>
    [Fact]
    public async Task An_explicit_run_retries_a_red_recorded_at_an_older_key()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        // The green beside it keeps the selection a strict subset of the inventory, so the run
        // travels as an id list and this test can read WHICH case was chosen.
        SeedTestCases(store, workspace, "test:green", "test:red");
        CommitResult(store, "test:green", Identity, 2, passed: true);
        CommitResult(store, "test:red", "ctgen1:artifact:previous:blake3", 1, passed: false);
        var provider = new RecordingProvider { DiscoverCases = ProviderCases("test:green", "test:red") };
        var queue = new ContinuousTestDaemonQueue(
            store,
            SelectorFor(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            workspace,
            "2",
            Identity,
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(["test:red"], Assert.Single(provider.Requests).TestCaseIds);
    }

    /// <summary>
    /// SKIPPED is committed-fresh by the same rule green and red are, and the explicit run's
    /// exception covers RED only. A skipped test skips again, so "prove it again" has nothing to
    /// prove about it. The contract states this exclusion rather than leaving a user to discover it
    /// as a `last_run` that will not move.
    /// </summary>
    [Fact]
    public async Task An_explicit_run_leaves_a_fresh_skipped_case_alone()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:skipped", "test:red");
        CommitStatus(store, "test:skipped", Identity, 2, "skipped");
        CommitResult(store, "test:red", Identity, 2, passed: false);
        var provider = new RecordingProvider
        {
            DiscoverCases = ProviderCases("test:skipped", "test:red"),
        };
        var queue = new ContinuousTestDaemonQueue(
            store,
            SelectorFor(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            workspace,
            "2",
            Identity,
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(["test:red"], Assert.Single(provider.Requests).TestCaseIds);
    }

    /// <summary>
    /// The AUTOMATIC path is untouched. A debounced auto-run that includes reds would re-run every
    /// failing test on every save, which is the red loop this repo has always refused.
    /// </summary>
    [Fact]
    public void An_auto_run_leaves_a_red_case_alone()
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        // In a DIFFERENT file from the impacted `tests/AppTests.cs`, so the change cannot reach it.
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        store.PutTestCase(EngineTestSupport.Case("test:red", workspace.ProjectPath, "tests/OtherTests.cs"));
        CommitResultFor(store, EngineTestSupport.WorkspaceId, "test:red", EngineTestSupport.Identity, 2, passed: false);
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(
                new RecordingProvider(), store, runIdFactory: static () => "run:1"));

        ContinuousTestDaemonEnqueueResult enqueued =
            queue.Enqueue(EngineTestSupport.Change(workspace, debounce: TimeSpan.Zero));

        // The automatic selection is the impacted set, exactly as before. A red the change cannot
        // reach is not in it; only a user who typed `tests run` gets the retry.
        Assert.Equal(["test:app"], enqueued.Selection.SelectedTestCaseIds);
    }

    /// <summary>
    /// The converse, and the reason the rule is "covers everything" rather than "is a workspace-scope run".
    /// A selection of one case out of two must still travel as that one case; running a whole assembly for it
    /// would be slower than the chunking this change exists to avoid.
    /// </summary>
    [Fact]
    public async Task The_queue_leaves_a_partial_run_alone()
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        // A SECOND known case, in a DIFFERENT test file so the selector does not resolve it. The selection is
        // then a strict subset of the inventory, and the run must travel as its own case list.
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        store.PutTestCase(EngineTestSupport.Case("test:other", workspace.ProjectPath, "tests/OtherTests.cs"));
        var provider = new RecordingProvider();
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        ContinuousTestDaemonEnqueueResult enqueued =
            queue.Enqueue(EngineTestSupport.Change(workspace, debounce: TimeSpan.Zero));
        IReadOnlyList<ContinuousTestDaemonDrainResult> drained =
            await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        // Guard the premise: this test says nothing unless the selection really is a strict subset. A second
        // case in the SAME test file is selected too, and the run is then correctly a whole-suite one.
        Assert.Equal(["test:app"], enqueued.Selection.SelectedTestCaseIds);

        // The drain also runs the backfill lane for the case that has never run. Neither run covers the whole
        // inventory, so NEITHER may be inverted - an empty selection here would run the whole assembly for a
        // single test.
        Assert.NotEmpty(provider.Requests);
        Assert.All(provider.Requests, request => Assert.NotEmpty(request.TestCaseIds));
        Assert.Contains(provider.Requests, request => request.TestCaseIds.SequenceEqual(["test:app"]));
        Assert.Contains(
            drained,
            result => result.SelectionFacts.Lane == ContinuousTestRunLane.Backfill
                && result.SelectionFacts.KnownCount == 2
                && !result.SelectionFacts.Eligible
                && result.SelectionFacts.ReasonCode == "backfill_lane");
    }

    private static ContinuousTestCoordinatorRunRequest RunRequest(
        ContinuousTestWorkspace workspace,
        string[] testCaseIds,
        bool wholeSuite) =>
        new(
            Workspace: workspace,
            SelectedRevision: "2",
            CurrentRevision: "2",
            IndexIdentity: Identity,
            TestCaseIds: testCaseIds,
            WholeSuite: wholeSuite);

    private ContinuousTestWorkspace Workspace()
    {
        string project = Path.Combine(_root, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        // The build output root must live OUTSIDE the workspace root; the queue validates it.
        return new ContinuousTestWorkspace(
            WorkspaceId,
            _root,
            project,
            Path.Combine(_root + "-build", "whole-suite"));
    }

    private static ContinuousTestImpactSelector SelectorFor(ContinuousTestStore store) =>
        new(store, new FakeMillerFactSource());

    private static IReadOnlyList<ProviderTestCase> ProviderCases(params string[] ids) =>
        ids
            .Select(id => new ProviderTestCase(
                Id: id,
                DisplayName: id,
                FullyQualifiedName: $"App.Tests.{id}",
                Selector: $"App.Tests.{id}",
                Framework: "xunit",
                SourcePath: "tests/AppTests.cs"))
            .ToArray();

    /// <summary>Commits one result at <c>(identity, revision)</c> through the real run-completion
    /// path, so the case is committed-fresh at that key.</summary>
    private static void CommitResult(
        ContinuousTestStore store,
        string testCaseId,
        string identity,
        long revision,
        bool passed) =>
        CommitResultFor(store, WorkspaceId, testCaseId, identity, revision, passed);

    /// <summary>A committed result whose status is neither passed nor failed — "skipped".</summary>
    private static void CommitStatus(
        ContinuousTestStore store,
        string testCaseId,
        string identity,
        long revision,
        string status) =>
        CommitResultFor(store, WorkspaceId, testCaseId, identity, revision, passed: true, status);

    private static void CommitResultFor(
        ContinuousTestStore store,
        string workspaceId,
        string testCaseId,
        string identity,
        long revision,
        bool passed,
        string? resultStatus = null)
    {
        string revisionText = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string status = resultStatus ?? (passed ? "passed" : "failed");
        string runId = "seed-run:" + testCaseId;
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: workspaceId,
                Status: "running",
                SelectedRevision: revisionText,
                IndexIdentity: identity,
                Revision: revision),
            [testCaseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: workspaceId,
            TestRunId: runId,
            SelectedRevision: revisionText,
            CurrentRevision: revisionText,
            IndexIdentity: identity,
            Revision: revision,
            Status: status,
            Results:
            [
                new ContinuousTestResult(
                    Id: runId + ":result",
                    WorkspaceId: workspaceId,
                    TestCaseId: testCaseId,
                    TestRunId: runId,
                    Status: status,
                    ResultRevision: revisionText,
                    IndexIdentity: identity,
                    Revision: revision),
            ]));
    }

    private static void SeedTestCases(
        ContinuousTestStore store,
        ContinuousTestWorkspace workspace,
        params string[] ids)
    {
        foreach (string id in ids)
        {
            store.PutTestCase(new ContinuousTestCase(
                Id: id,
                WorkspaceId: WorkspaceId,
                Name: id,
                QualifiedName: $"App.Tests.{id}",
                Selector: $"App.Tests.{id}",
                FilePath: "tests/AppTests.cs",
                Framework: "xunit",
                Role: ContinuousTestRole.TestCase,
                Source: "ct-provider:dotnet",
                Confidence: 1.0,
                Metadata: new Dictionary<string, object?> { ["ct_project_path"] = workspace.ProjectPath }));
        }
    }

    /// <summary>
    /// A provider whose verdicts DERIVE from the selection it was handed, the way every real provider's
    /// do. The earlier fake reported results regardless of the selection — and a fake that answers for
    /// tests it was never asked to run cannot notice a provider that runs nothing, which is exactly how
    /// the blanked-id-list bug shipped.
    /// </summary>
    private sealed class RecordingProvider : IContinuousTestProvider
    {
        public List<ContinuousTestProviderRunRequest> Requests { get; } = [];

        public bool InvokeProgress { get; init; }

        public bool SawProgressCallback { get; private set; }

        public IReadOnlyList<ProviderTestCase> DiscoverCases { get; init; } = [];

        public bool ThrowOnRun { get; set; }

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DiscoverCases);

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (ThrowOnRun)
                throw new InvalidOperationException("provider failure");
            if (InvokeProgress && request.Progress is { } progress)
            {
                SawProgressCallback = true;
                progress(new ContinuousTestProviderChunkProgress(
                    RequestedUniqueUnitCount: request.TestCaseIds.Distinct(StringComparer.Ordinal).Count(),
                    ChunkCount: 1,
                    CurrentPart: 1,
                    CurrentPartUnitCount: request.TestCaseIds.Count,
                    NameSamples: request.TestCaseIds.Take(8).ToArray(),
                    NameDigest: "fixture-digest",
                    NamesTruncated: request.TestCaseIds.Count > 8));
            }

            string runId = request.RunId ?? "run:1";
            return Task.FromResult(new ProviderRunResult(
                runId,
                "passed",
                CaseResults: request.TestCaseIds
                    .Select(id => new ProviderCaseResult(
                        Id: $"{runId}:{id}",
                        TestCaseId: id,
                        Status: "passed",
                        ResultRevision: request.SelectedRevision,
                        IndexIdentity: request.IndexIdentity))
                    .ToArray()));
        }
    }

    /// <summary>A provider that claims "passed" and reports a verdict for nothing — the F6 shape.</summary>
    private sealed class SilentProvider : IContinuousTestProvider
    {
        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderTestCase>>([]);

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderRunResult(request.RunId ?? "run:1", "passed"));
    }
}
