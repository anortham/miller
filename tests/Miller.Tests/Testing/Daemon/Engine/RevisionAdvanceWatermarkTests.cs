using Microsoft.Data.Sqlite;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

/// <summary>
/// The queue's revision-observation path is the ONE production caller of
/// <see cref="ContinuousTestStore.ApplyRevisionAdvance"/>: every enqueue applies staleness and the
/// watermark advance as one crash-atomic store operation, so a green that an edit cannot reach
/// stays fresh at the new revision.
/// </summary>
public sealed class RevisionAdvanceWatermarkTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-watermark-queue-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Unrelated_change_watermarks_all_greens_and_the_verdict_stays_green()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        CommitGreen(store, "test:app", 2);
        ContinuousTestDaemonQueue queue = UnreachableChangeQueue(store, revision: 3);

        ContinuousTestDaemonEnqueueResult result = queue.Enqueue(EngineTestSupport.Change(
            workspace,
            revision: "3",
            changedPaths: ["src/Persistence.cs"],
            from: 2,
            to: 3));

        Assert.Equal(ContinuousTestSelectionOutcome.KnownEmpty, result.Selection.Outcome);
        IReadOnlyDictionary<string, CtFreshnessKey> watermarks =
            store.ListContinuousTestFreshWatermarks(EngineTestSupport.WorkspaceId, EngineTestSupport.Identity);
        Assert.Equal(3, watermarks["test:app"].Revision);
        ContinuousTestProjectedStatus projected = ProjectedAt(store, new CtFreshnessKey(EngineTestSupport.Identity, 3));
        Assert.Equal(ContinuousTestVerdict.Green, projected.Verdict);
        Assert.Equal(0, projected.StaleCount);
    }

    [Fact]
    public void Chained_unrelated_changes_stay_green_via_watermark_alone()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        CommitGreen(store, "test:app", 2);

        UnreachableChangeQueue(store, revision: 3).Enqueue(EngineTestSupport.Change(
            workspace, revision: "3", changedPaths: ["src/Persistence.cs"], from: 2, to: 3));
        UnreachableChangeQueue(store, revision: 4).Enqueue(EngineTestSupport.Change(
            workspace, revision: "4", changedPaths: ["src/Persistence.cs"], from: 3, to: 4));

        // The committed row never moved: revision 4 freshness rides the watermark alone.
        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(EngineTestSupport.WorkspaceId));
        Assert.Equal(ContinuousTestState.Green, status.State);
        Assert.Equal(2, status.Revision);
        Assert.Equal(
            4,
            store.ListContinuousTestFreshWatermarks(
                EngineTestSupport.WorkspaceId, EngineTestSupport.Identity)["test:app"].Revision);
        ContinuousTestProjectedStatus projected = ProjectedAt(store, new CtFreshnessKey(EngineTestSupport.Identity, 4));
        Assert.Equal(ContinuousTestVerdict.Green, projected.Verdict);
        Assert.Equal(0, projected.StaleCount);
    }

    [Fact]
    public void Unknown_change_clears_watermarks_and_advances_nothing()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        CommitGreen(store, "test:app", 2);
        UnreachableChangeQueue(store, revision: 3).Enqueue(EngineTestSupport.Change(
            workspace, revision: "3", changedPaths: ["src/Persistence.cs"], from: 2, to: 3));

        ContinuousTestDaemonEnqueueResult result = UnreachableChangeQueue(store, revision: 4)
            .Enqueue(EngineTestSupport.Change(
                workspace, revision: "4", changedPaths: ["src/Mystery.xyz"], from: 3, to: 4));

        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Selection.Outcome);
        Assert.Empty(store.ListContinuousTestFreshWatermarks(
            EngineTestSupport.WorkspaceId, EngineTestSupport.Identity));
        ContinuousTestProjectedStatus projected = ProjectedAt(store, new CtFreshnessKey(EngineTestSupport.Identity, 4));
        Assert.Equal(1, projected.StaleCount);
        Assert.NotEqual(ContinuousTestVerdict.Green, projected.Verdict);
    }

    /// <summary>A queue whose selector resolves the changed file to a non-test symbol reaching no
    /// test: an unrelated (known-empty) change.</summary>
    private static ContinuousTestDaemonQueue UnreachableChangeQueue(ContinuousTestStore store, long revision)
    {
        var facts = new FakeMillerFactSource
        {
            Current = new CtIndexCursor(EngineTestSupport.Identity, revision),
        };
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:persistence", "Persist", "src/Persistence.cs"));
        return new ContinuousTestDaemonQueue(
            store,
            new ContinuousTestImpactSelector(store, facts),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store));
    }

    private static ContinuousTestProjectedStatus ProjectedAt(ContinuousTestStore store, CtFreshnessKey key) =>
        ContinuousTestStatusProjection.Project(
            key,
            store.ListContinuousTestStatuses(EngineTestSupport.WorkspaceId),
            store.ListContinuousTestFreshWatermarks(EngineTestSupport.WorkspaceId, key.IndexIdentity));

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
}
