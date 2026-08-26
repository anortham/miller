using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Store.Coverage;

public sealed class DurableFreshnessTests : IDisposable
{
    private const string Workspace = "ws:1";
    private const string Identity = "gen-1";

    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-durable-freshness-").FullName;

    private string DbPath => Path.Combine(_dir, CtSchema.DbFileName);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Committed_freshness_round_trips_the_composite_key()
    {
        using var store = CreateStoreWithProviderCase("a:1", ProjectA());
        CommitGreen(store, "a:1", Identity, 12);

        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.True(ContinuousTestDurableFreshness.IsCommittedFreshAt(status, new CtFreshnessKey(Identity, 12)));
        Assert.False(ContinuousTestDurableFreshness.IsCommittedFreshAt(status, new CtFreshnessKey(Identity, 13)));
        Assert.False(ContinuousTestDurableFreshness.IsCommittedFreshAt(status, new CtFreshnessKey("gen-2", 12)));
    }

    [Fact]
    public void Known_empty_advance_watermarks_committed_greens_in_project_scope()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "a:1", ProjectA());
        SeedProviderCase(store, "a:2", ProjectA());
        SeedProviderCase(store, "b:1", ProjectB());
        CommitGreen(store, "a:1", Identity, 267);
        CommitGreen(store, "a:2", Identity, 267);
        CommitGreen(store, "b:1", Identity, 267);

        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(267), Key(273), [], ContinuousTestSelectionOutcome.KnownEmpty);

        IReadOnlyDictionary<string, CtFreshnessKey> watermarks = store.ListContinuousTestFreshWatermarks(Workspace, Identity);
        Assert.Equal(new CtFreshnessKey(Identity, 273), watermarks["a:1"]);
        Assert.Equal(new CtFreshnessKey(Identity, 273), watermarks["a:2"]);
        Assert.False(watermarks.ContainsKey("b:1"));
    }

    [Fact]
    public void Advance_seeds_greens_committed_below_the_from_key_and_skips_uncommitted_cases()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "committed-at-from", ProjectA());
        SeedProviderCase(store, "committed-below-from", ProjectA());
        SeedProviderCase(store, "running", ProjectA());
        SeedProviderCase(store, "never-run", ProjectA());
        CommitGreen(store, "committed-at-from", Identity, 267);
        CommitGreen(store, "committed-below-from", Identity, 200);
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: "run:live",
                WorkspaceId: Workspace,
                Status: "running",
                SelectedRevision: "267",
                IndexIdentity: Identity,
                Revision: 267),
            ["running"]);

        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(267), Key(273), [], ContinuousTestSelectionOutcome.KnownEmpty);

        IReadOnlyDictionary<string, CtFreshnessKey> watermarks = store.ListContinuousTestFreshWatermarks(Workspace, Identity);
        Assert.Equal(273, watermarks["committed-at-from"].Revision);
        Assert.Equal(273, watermarks["committed-below-from"].Revision);
        Assert.False(watermarks.ContainsKey("running"));
        Assert.False(watermarks.ContainsKey("never-run"));
    }

    [Fact]
    public void Older_green_stays_fresh_across_empty_advances_and_stale_count_does_not_move()
    {
        using var store = CreateStoreWithProviderCase("test:1", ProjectA());
        CommitGreen(store, "test:1", Identity, 200);

        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(267), Key(273), [], ContinuousTestSelectionOutcome.KnownEmpty);

        Assert.Equal(273, store.ListContinuousTestFreshWatermarks(Workspace, Identity)["test:1"].Revision);
        ContinuousTestProjectedStatus projected = ProjectedAt(store, Key(273));
        Assert.Equal(ContinuousTestVerdict.Green, projected.Verdict);
        Assert.Equal(0, projected.StaleCount);

        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(273), Key(280), [], ContinuousTestSelectionOutcome.KnownEmpty);

        Assert.Equal(280, store.ListContinuousTestFreshWatermarks(Workspace, Identity)["test:1"].Revision);
        projected = ProjectedAt(store, Key(280));
        Assert.Equal(ContinuousTestVerdict.Green, projected.Verdict);
        Assert.Equal(0, projected.StaleCount);
    }

    [Fact]
    public void Seeded_green_does_not_ride_when_the_advance_names_it_impacted()
    {
        using var store = CreateStoreWithProviderCase("test:1", ProjectA());
        CommitGreen(store, "test:1", Identity, 200);

        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(267), Key(273), ["test:1"], ContinuousTestSelectionOutcome.Impacted);

        Assert.Empty(store.ListContinuousTestFreshWatermarks(Workspace, Identity));
        ContinuousTestProjectedStatus projected = ProjectedAt(store, Key(273));
        Assert.Equal(1, projected.StaleCount);
    }

    [Fact]
    public void Red_and_skipped_rows_never_ride_the_watermark()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "green", ProjectA());
        SeedProviderCase(store, "red", ProjectA());
        SeedProviderCase(store, "skipped", ProjectA());
        SeedProviderCase(store, "red-below-from", ProjectA());
        SeedProviderCase(store, "skipped-below-from", ProjectA());
        CommitGreen(store, "green", Identity, 267);
        CommitResult(store, "red", Identity, 267, "failed");
        CommitResult(store, "skipped", Identity, 267, "skipped");
        CommitResult(store, "red-below-from", Identity, 200, "failed");
        CommitResult(store, "skipped-below-from", Identity, 200, "skipped");

        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(267), Key(273), [], ContinuousTestSelectionOutcome.KnownEmpty);

        IReadOnlyDictionary<string, CtFreshnessKey> watermarks = store.ListContinuousTestFreshWatermarks(Workspace, Identity);
        Assert.Equal(273, watermarks["green"].Revision);
        Assert.False(watermarks.ContainsKey("red"));
        Assert.False(watermarks.ContainsKey("skipped"));
        Assert.False(watermarks.ContainsKey("red-below-from"));
        Assert.False(watermarks.ContainsKey("skipped-below-from"));

        ContinuousTestProjectedStatus projected = ProjectedAt(store, Key(273));
        Assert.Equal(4, projected.StaleCount);
        Assert.Equal(ContinuousTestVerdict.Partial, projected.Verdict);
    }

    [Fact]
    public void Advance_never_lowers_and_chains_forward_via_watermark_alone()
    {
        using var store = CreateStoreWithProviderCase("test:1", ProjectA());
        CommitGreen(store, "test:1", Identity, 9);
        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(9), Key(273), [], ContinuousTestSelectionOutcome.KnownEmpty);
        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(9), Key(100), [], ContinuousTestSelectionOutcome.KnownEmpty);
        Assert.Equal(273, store.ListContinuousTestFreshWatermarks(Workspace, Identity)["test:1"].Revision);

        // The committed row still sits at revision 9; the second hop rides the watermark alone.
        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(273), Key(281), [], ContinuousTestSelectionOutcome.KnownEmpty);
        Assert.Equal(281, store.ListContinuousTestFreshWatermarks(Workspace, Identity)["test:1"].Revision);
        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(9, status.Revision);
        ContinuousTestProjectedStatus projected = ProjectedAt(store, Key(281));
        Assert.Equal(ContinuousTestVerdict.Green, projected.Verdict);
        Assert.Equal(0, projected.StaleCount);
        Assert.True(ContinuousTestDurableFreshness.IsWatermarkFreshAt(
            new CtFreshnessKey(Identity, 281),
            new CtFreshnessKey(Identity, 281)));
        Assert.False(ContinuousTestDurableFreshness.IsWatermarkFreshAt(
            new CtFreshnessKey(Identity, 9),
            new CtFreshnessKey(Identity, 10)));
    }

    [Fact]
    public void Impacted_advance_stales_the_impacted_set_and_advances_the_keep_set()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "impacted", ProjectA());
        SeedProviderCase(store, "kept", ProjectA());
        CommitGreen(store, "impacted", Identity, 267);
        CommitGreen(store, "kept", Identity, 267);

        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(267), Key(273), ["impacted"], ContinuousTestSelectionOutcome.Impacted);

        IReadOnlyDictionary<string, CtFreshnessKey> watermarks = store.ListContinuousTestFreshWatermarks(Workspace, Identity);
        Assert.False(watermarks.ContainsKey("impacted"));
        Assert.Equal(273, watermarks["kept"].Revision);
        ContinuousTestStatus impacted = Assert.Single(
            store.ListContinuousTestStatuses(Workspace), row => row.TestCaseId == "impacted");
        Assert.Equal(ContinuousTestState.Stale, impacted.State);
        ContinuousTestProjectedStatus projected = ProjectedAt(store, Key(273));
        Assert.Equal(1, projected.StaleCount);
        Assert.Equal(ContinuousTestVerdict.Partial, projected.Verdict);
    }

    [Fact]
    public void Impacted_advance_keeps_a_red_verdict_and_still_reads_stale_for_execution()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "red", ProjectA());
        SeedProviderCase(store, "kept", ProjectA());
        CommitResult(store, "red", Identity, 267, "failed");
        CommitGreen(store, "kept", Identity, 267);

        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(267), Key(273), ["red"], ContinuousTestSelectionOutcome.Impacted);

        IReadOnlyDictionary<string, CtFreshnessKey> watermarks = store.ListContinuousTestFreshWatermarks(Workspace, Identity);
        Assert.False(watermarks.ContainsKey("red"));
        Assert.Equal(273, watermarks["kept"].Revision);
        ContinuousTestStatus red = Assert.Single(
            store.ListContinuousTestStatuses(Workspace), row => row.TestCaseId == "red");
        Assert.Equal(ContinuousTestState.Red, red.State);
        Assert.Equal("273", red.StaleSinceRevision);
        Assert.False(ContinuousTestDurableFreshness.IsFreshAt(red, Key(273), watermarks));
        ContinuousTestProjectedStatus projected = ProjectedAt(store, Key(273));
        Assert.Equal(1, projected.StaleCount);
        Assert.Equal(ContinuousTestVerdict.Partial, projected.Verdict);
    }

    [Fact]
    public void Unknown_advance_stales_everything_and_advances_nothing()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "a:1", ProjectA());
        SeedProviderCase(store, "a:2", ProjectA());
        CommitGreen(store, "a:1", Identity, 267);
        CommitGreen(store, "a:2", Identity, 267);
        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(267), Key(273), [], ContinuousTestSelectionOutcome.KnownEmpty);

        // The selector's Unknown result names every case in scope as stale.
        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(273), Key(280), ["a:1", "a:2"], ContinuousTestSelectionOutcome.Unknown);

        Assert.Empty(store.ListContinuousTestFreshWatermarks(Workspace, Identity));
        Assert.All(
            store.ListContinuousTestStatuses(Workspace),
            row => Assert.Equal(ContinuousTestState.Stale, row.State));
        ContinuousTestProjectedStatus projected = ProjectedAt(store, Key(280));
        Assert.Equal(2, projected.StaleCount);
    }

    [Fact]
    public void Unknown_advance_with_no_named_cases_still_advances_nothing()
    {
        using var store = CreateStoreWithProviderCase("test:1", ProjectA());
        CommitGreen(store, "test:1", Identity, 267);
        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(267), Key(273), [], ContinuousTestSelectionOutcome.KnownEmpty);

        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(273), Key(280), [], ContinuousTestSelectionOutcome.Unknown);

        // The watermark stays behind the new key, so the case reads stale at 280 — fail closed.
        Assert.Equal(273, store.ListContinuousTestFreshWatermarks(Workspace, Identity)["test:1"].Revision);
        ContinuousTestProjectedStatus projected = ProjectedAt(store, Key(280));
        Assert.Equal(1, projected.StaleCount);
        Assert.Equal(ContinuousTestVerdict.Partial, projected.Verdict);
    }

    [Fact]
    public void Workspace_scope_advance_stales_the_named_set_and_advances_nothing()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "a:1", ProjectA());
        SeedProviderCase(store, "a:2", ProjectA());
        CommitGreen(store, "a:1", Identity, 267);
        CommitGreen(store, "a:2", Identity, 267);

        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(273), Key(273), ["a:1", "a:2"], ContinuousTestSelectionOutcome.WorkspaceScope);

        Assert.Empty(store.ListContinuousTestFreshWatermarks(Workspace, Identity));
        Assert.All(
            store.ListContinuousTestStatuses(Workspace),
            row => Assert.Equal(ContinuousTestState.Stale, row.State));
    }

    [Fact]
    public void Changed_index_identity_invalidates_stored_freshness()
    {
        using var store = CreateStoreWithProviderCase("test:1", ProjectA());
        CommitGreen(store, "test:1", Identity, 12);
        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(12), Key(20), [], ContinuousTestSelectionOutcome.KnownEmpty);

        IReadOnlyDictionary<string, CtFreshnessKey> oldIdentity =
            store.ListContinuousTestFreshWatermarks(Workspace, Identity);
        IReadOnlyDictionary<string, CtFreshnessKey> newIdentity =
            store.ListContinuousTestFreshWatermarks(Workspace, "gen-2");
        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));

        Assert.Equal(new CtFreshnessKey(Identity, 20), oldIdentity["test:1"]);
        Assert.Empty(newIdentity);
        Assert.False(ContinuousTestDurableFreshness.IsCommittedFreshAt(status, new CtFreshnessKey("gen-2", 12)));
        Assert.False(ContinuousTestDurableFreshness.IsWatermarkFreshAt(
            oldIdentity["test:1"],
            new CtFreshnessKey("gen-2", 20)));

        store.ApplyRevisionAdvance(
            Workspace,
            ProjectA(),
            new CtFreshnessKey("gen-2", 12),
            new CtFreshnessKey("gen-2", 20),
            [],
            ContinuousTestSelectionOutcome.KnownEmpty);
        Assert.Empty(store.ListContinuousTestFreshWatermarks(Workspace, "gen-2"));
        Assert.Equal(20, store.ListContinuousTestFreshWatermarks(Workspace, Identity)["test:1"].Revision);
    }

    [Fact]
    public void Mark_stale_invalidates_every_watermark_for_the_case()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "test:1", ProjectA());
        SeedProviderCase(store, "test:2", ProjectA());
        CommitGreen(store, "test:1", Identity, 267);
        CommitGreen(store, "test:2", Identity, 267);
        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(267), Key(273), [], ContinuousTestSelectionOutcome.KnownEmpty);

        store.MarkContinuousTestsStale(Workspace, ["test:1"], Key(274));

        IReadOnlyDictionary<string, CtFreshnessKey> watermarks = store.ListContinuousTestFreshWatermarks(Workspace, Identity);
        Assert.False(watermarks.ContainsKey("test:1"));
        Assert.Equal(273, watermarks["test:2"].Revision);
    }

    [Fact]
    public void Aborted_advance_between_staleness_and_watermark_leaves_no_fresh_impacted_case()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "impacted", ProjectA());
        SeedProviderCase(store, "kept", ProjectA());
        CommitGreen(store, "impacted", Identity, 267);
        CommitGreen(store, "kept", Identity, 267);
        bool fired = false;
        store.RevisionAdvanceFaultInjection = () =>
        {
            fired = true;
            throw new InvalidOperationException("crash between staleness and advance");
        };

        Assert.Throws<InvalidOperationException>(() => store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(267), Key(273), ["impacted"], ContinuousTestSelectionOutcome.Impacted));
        store.RevisionAdvanceFaultInjection = null;

        Assert.True(fired);

        // The whole operation rolled back: no watermark reached 273, so nothing — impacted or
        // kept — reads fresh at the new key. Stale, never wrongly fresh.
        Assert.Empty(store.ListContinuousTestFreshWatermarks(Workspace, Identity));
        ContinuousTestProjectedStatus projected = ProjectedAt(store, Key(273));
        Assert.Equal(2, projected.StaleCount);
        Assert.NotEqual(ContinuousTestVerdict.Green, projected.Verdict);
    }

    [Fact]
    public void Aborted_transaction_after_both_halves_rolls_the_whole_operation_back()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "impacted", ProjectA());
        SeedProviderCase(store, "kept", ProjectA());
        CommitGreen(store, "impacted", Identity, 267);
        CommitGreen(store, "kept", Identity, 267);

        Assert.Throws<InvalidOperationException>(() => store.Transaction(() =>
        {
            store.ApplyRevisionAdvance(
                Workspace, ProjectA(), Key(267), Key(273), ["impacted"], ContinuousTestSelectionOutcome.Impacted);
            throw new InvalidOperationException("crash after both halves, before commit");
        }));

        // Staleness and advance are one unit: neither survived the abort, so the impacted case
        // still reads stale at 273 and no case reads fresh there.
        Assert.Empty(store.ListContinuousTestFreshWatermarks(Workspace, Identity));
        ContinuousTestStatus impacted = Assert.Single(
            store.ListContinuousTestStatuses(Workspace), row => row.TestCaseId == "impacted");
        Assert.Equal(ContinuousTestState.Green, impacted.State);
        Assert.Equal(267, impacted.Revision);
        ContinuousTestProjectedStatus projected = ProjectedAt(store, Key(273));
        Assert.Equal(2, projected.StaleCount);
        Assert.NotEqual(ContinuousTestVerdict.Green, projected.Verdict);
    }

    [Fact]
    public void Complete_delta_normalizes_paths_and_rejects_unavailable_changes()
    {
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: Workspace,
            WorkspaceRoot: _dir,
            ProjectPath: Path.Combine(_dir, "proj.csproj"),
            BuildOutputRoot: Path.Combine(_dir, "out"));
        var complete = new ContinuousTestDaemonChange(
            Workspace: workspace,
            CurrentRevision: "12",
            IndexIdentity: Identity,
            ChangedPaths: [@"src\Foo.cs", "/src/Foo.cs", "src/Bar.cs"],
            DeltaCompleteness: ContinuousTestDeltaCompleteness.Complete,
            DeltaFromRevision: 11,
            DeltaToRevision: 12);
        var unavailable = new ContinuousTestDaemonChange(
            Workspace: workspace,
            CurrentRevision: "12",
            IndexIdentity: Identity,
            ChangedPaths: ["src/Foo.cs"]);

        Assert.True(ContinuousTestDurableFreshness.TryGetCompleteDelta(
            complete,
            out long from,
            out long to,
            out IReadOnlyList<string> paths));
        Assert.Equal(11, from);
        Assert.Equal(12, to);
        Assert.Equal(["src/Bar.cs", "src/Foo.cs"], paths);
        Assert.False(ContinuousTestDurableFreshness.TryGetCompleteDelta(unavailable, out _, out _, out _));

        // Defect D3: an EMPTY complete delta (a proven no-change interval) is still a complete
        // delta — it must name its interval so the queue anchors the watermark advance at the
        // from-revision instead of the new key (where it could confirm nothing).
        var emptyDelta = new ContinuousTestDaemonChange(
            Workspace: workspace,
            CurrentRevision: "12",
            IndexIdentity: Identity,
            DeltaCompleteness: ContinuousTestDeltaCompleteness.Complete,
            DeltaFromRevision: 11,
            DeltaToRevision: 12);
        Assert.True(ContinuousTestDurableFreshness.TryGetCompleteDelta(
            emptyDelta,
            out long emptyFrom,
            out long emptyTo,
            out IReadOnlyList<string> emptyPaths));
        Assert.Equal(11, emptyFrom);
        Assert.Equal(12, emptyTo);
        Assert.Empty(emptyPaths);
    }

    [Fact]
    public void Discovery_failure_is_active_only_for_the_matching_project()
    {
        var cases = new List<ContinuousTestCase>
        {
            new(
                Id: "fail:1",
                WorkspaceId: Workspace,
                Name: "fail",
                QualifiedName: "fail",
                Selector: "fail",
                Source: "ct-project-status",
                Metadata: new Dictionary<string, object?>
                {
                    ["kind"] = "ct-project-discovery-failure",
                    ["ct_project_path"] = ProjectA(),
                }),
        };

        Assert.True(ContinuousTestDurableFreshness.HasActiveDiscoveryFailure(cases, ProjectA()));
        Assert.False(ContinuousTestDurableFreshness.HasActiveDiscoveryFailure(cases, ProjectB()));
    }

    [Fact]
    public void Missing_db_watermark_reads_return_empty()
    {
        using var store = new ContinuousTestStore(DbPath);
        Assert.Empty(store.ListContinuousTestFreshWatermarks(Workspace));
        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public void Missing_db_advance_creates_nothing()
    {
        using var store = new ContinuousTestStore(DbPath);
        store.ApplyRevisionAdvance(
            Workspace, ProjectA(), Key(1), Key(2), [], ContinuousTestSelectionOutcome.KnownEmpty);
        Assert.False(File.Exists(DbPath));
    }

    private ContinuousTestStore CreateStoreWithProviderCase(string id, string projectPath)
    {
        var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, id, projectPath);
        return store;
    }

    private static void SeedProviderCase(ContinuousTestStore store, string id, string projectPath) =>
        store.PutTestCase(new ContinuousTestCase(
            Id: id,
            WorkspaceId: Workspace,
            Name: id.Replace(":", "_", StringComparison.Ordinal),
            QualifiedName: $"Tests.{id.Replace(":", "_", StringComparison.Ordinal)}",
            Selector: $"{id}.selector",
            Source: "ct-provider:dotnet",
            Metadata: new Dictionary<string, object?> { ["ct_project_path"] = projectPath }));

    private static void CommitGreen(ContinuousTestStore store, string testCaseId, string identity, long revision) =>
        CommitResult(store, testCaseId, identity, revision, "passed");

    private static void CommitResult(
        ContinuousTestStore store,
        string testCaseId,
        string identity,
        long revision,
        string status)
    {
        string runId = "run:" + testCaseId + ":" + revision;
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: Workspace,
                Status: "running",
                SelectedRevision: revision.ToString(),
                IndexIdentity: identity,
                Revision: revision),
            [testCaseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: Workspace,
            TestRunId: runId,
            SelectedRevision: revision.ToString(),
            CurrentRevision: revision.ToString(),
            IndexIdentity: identity,
            Revision: revision,
            Status: status == "passed" ? "passed" : "completed",
            Results:
            [
                new ContinuousTestResult(
                    Id: "res:" + testCaseId + ":" + revision,
                    WorkspaceId: Workspace,
                    TestCaseId: testCaseId,
                    TestRunId: runId,
                    Status: status,
                    ResultRevision: revision.ToString(),
                    IndexIdentity: identity,
                    Revision: revision),
            ]));
    }

    private static ContinuousTestProjectedStatus ProjectedAt(ContinuousTestStore store, CtFreshnessKey key) =>
        ContinuousTestStatusProjection.Project(
            key,
            store.ListContinuousTestStatuses(Workspace),
            store.ListContinuousTestFreshWatermarks(Workspace, key.IndexIdentity));

    private string ProjectA() => Path.GetFullPath(Path.Combine(_dir, "repo", "A.Tests", "A.Tests.csproj"));

    private string ProjectB() => Path.GetFullPath(Path.Combine(_dir, "repo", "B.Tests", "B.Tests.csproj"));

    private static CtFreshnessKey Key(long revision) => new(Identity, revision);
}
