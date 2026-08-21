using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Store.Core;

/// <summary>
/// Task 9 durable pin for the legacy-`ct.db` hard-gate scenario: rows written before the
/// generation-scale identity carry a pre-<c>ctgen1:</c> identity string (the fine-grained
/// <c>store:…</c> composite, or the bare artifact id). Those rows must read STALE exactly once at
/// the new <c>ctgen1:</c> key — even at the same numeric revision, because the prefix guarantees a
/// legacy identity can never equal a new-format one — and one completed run at the live key must
/// converge them to green. Verified live 2026-08-21 on a real daemon (task-9 evidence log,
/// scenario 7).
/// </summary>
public sealed class LegacyIdentityFreshnessTests : IDisposable
{
    private const string Workspace = "ws:legacy";

    /// <summary>The pre-change fine-grained store identity: no <c>ctgen1:</c> prefix, and every
    /// routine-write component (manifest generation, manifest hash, log sequence, level stamps)
    /// baked in.</summary>
    private const string LegacyStoreIdentity =
        "store:fam-1:view-1:gen-1:12:sha256-manifest:41:full:l1:l2:l3:rs:base:1:2";

    /// <summary>The pre-change legacy-artifact identity: the bare artifact id.</summary>
    private const string LegacyArtifactIdentity = "6275b741-822e-45ef-88fa-c857e318cbf7";

    private static readonly CtFreshnessKey LiveKey = new("ctgen1:store:fam-1:view-1:gen-1", 41);

    private readonly string _dir;
    private readonly string _dbPath;

    public LegacyIdentityFreshnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-legacy-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, CtSchema.DbFileName);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_green_row_at_the_legacy_store_identity_reads_stale_at_the_ctgen1_key_with_the_same_revision()
    {
        using ContinuousTestStore store = StoreWithGreenRunAt(LegacyStoreIdentity, LiveKey.Revision);

        ContinuousTestProjectedStatus projected = Project(store);

        Assert.Equal(ContinuousTestVerdict.Partial, projected.Verdict);
        Assert.Equal(1, projected.StaleCount);
        Assert.Equal(LiveKey, projected.SelectedKey);
    }

    [Fact]
    public void A_green_row_at_the_legacy_artifact_identity_reads_stale_at_the_ctgen1_key()
    {
        using ContinuousTestStore store = StoreWithGreenRunAt(LegacyArtifactIdentity, LiveKey.Revision);

        ContinuousTestProjectedStatus projected = Project(store);

        Assert.Equal(ContinuousTestVerdict.Partial, projected.Verdict);
        Assert.Equal(1, projected.StaleCount);
    }

    [Fact]
    public void A_legacy_row_converges_to_green_after_one_completed_run_at_the_live_key()
    {
        using ContinuousTestStore store = StoreWithGreenRunAt(LegacyStoreIdentity, LiveKey.Revision);
        Assert.Equal(ContinuousTestVerdict.Partial, Project(store).Verdict);

        CompleteGreenRun(store, "run:converge", LiveKey.IndexIdentity, LiveKey.Revision);

        ContinuousTestProjectedStatus projected = Project(store);
        Assert.Equal(ContinuousTestVerdict.Green, projected.Verdict);
        Assert.Equal(0, projected.StaleCount);
        ContinuousTestStatus row = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Green, row.State);
        Assert.Equal(LiveKey.IndexIdentity, row.IndexIdentity);
        Assert.Equal(LiveKey.Revision, row.Revision);
    }

    private ContinuousTestStore StoreWithGreenRunAt(string identity, long revision)
    {
        var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(new ContinuousTestCase(
            Id: "test:legacy",
            WorkspaceId: Workspace,
            Name: "Adds",
            QualifiedName: "Fixture.Tests.MathOpsTests.Adds",
            Selector: "Fixture.Tests.MathOpsTests.Adds",
            FilePath: "tests/MathOpsTests.cs",
            ContentHash: "blake3:abc",
            SymbolName: "Adds",
            SymbolPath: "tests/MathOpsTests.cs",
            Framework: "xunit"));
        CompleteGreenRun(store, "run:legacy", identity, revision);
        return store;
    }

    private static void CompleteGreenRun(
        ContinuousTestStore store,
        string runId,
        string identity,
        long revision)
    {
        string revisionText = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: Workspace,
                Status: "running",
                SelectedRevision: revisionText,
                IndexIdentity: identity,
                Revision: revision),
            ["test:legacy"]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: Workspace,
            TestRunId: runId,
            SelectedRevision: revisionText,
            CurrentRevision: revisionText,
            IndexIdentity: identity,
            Revision: revision,
            Status: "passed",
            EndedAt: null,
            Results:
            [
                new ContinuousTestResult(
                    Id: runId + ":test:legacy",
                    WorkspaceId: Workspace,
                    TestCaseId: "test:legacy",
                    TestRunId: runId,
                    Status: "passed",
                    ResultRevision: revisionText,
                    IndexIdentity: identity,
                    Revision: revision),
            ]));
    }

    private static ContinuousTestProjectedStatus Project(ContinuousTestStore store) =>
        ContinuousTestStatusProjection.Project(LiveKey, store.ListContinuousTestStatuses(Workspace));
}
