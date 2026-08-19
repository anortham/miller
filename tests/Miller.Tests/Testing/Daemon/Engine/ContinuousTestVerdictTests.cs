using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ContinuousTestVerdictTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-verdict-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Green_requires_complete_results_at_the_selected_composite_key()
    {
        var key = new CtFreshnessKey("gen-1", 4);
        var statuses = new[]
        {
            Status("test:a", ContinuousTestState.Green, key),
            Status("test:b", ContinuousTestState.Green, key),
        };
        Assert.Equal(ContinuousTestVerdict.Green, ContinuousTestFreshness.Evaluate(statuses, key, watchHealthy: true));
    }

    [Fact]
    public void Known_staleness_is_partial()
    {
        var selected = new CtFreshnessKey("gen-1", 5);
        var proven = new CtFreshnessKey("gen-1", 4);
        var statuses = new[]
        {
            Status("test:a", ContinuousTestState.Stale, selected, proven: null),
        };
        Assert.Equal(ContinuousTestVerdict.Partial, ContinuousTestFreshness.Evaluate(statuses, selected, watchHealthy: true));
        Assert.Equal(
            ContinuousTestVerdict.Partial,
            ContinuousTestFreshness.Evaluate(
                [Status("test:a", ContinuousTestState.Green, proven)],
                selected,
                watchHealthy: true));
    }

    [Fact]
    public void Unknown_watch_or_running_case_is_unknown()
    {
        var key = new CtFreshnessKey("gen-1", 4);
        Assert.Equal(
            ContinuousTestVerdict.Unknown,
            ContinuousTestFreshness.Evaluate(
                [Status("test:a", ContinuousTestState.Green, key)],
                key,
                watchHealthy: false));
        Assert.Equal(
            ContinuousTestVerdict.Unknown,
            ContinuousTestFreshness.Evaluate(
                [Status("test:a", ContinuousTestState.Running, key)],
                key,
                watchHealthy: true));
        Assert.Equal(
            ContinuousTestVerdict.Unknown,
            ContinuousTestFreshness.Evaluate([], key, watchHealthy: true));
    }

    [Fact]
    public void Rebuild_new_index_identity_demotes_prior_green()
    {
        var prior = new CtFreshnessKey("gen-old", 4);
        var rebuilt = new CtFreshnessKey("gen-new", 1);
        var statuses = new[]
        {
            Status("test:a", ContinuousTestState.Green, prior),
        };
        Assert.Equal(ContinuousTestVerdict.Green, ContinuousTestFreshness.Evaluate(statuses, prior, watchHealthy: true));
        Assert.Equal(ContinuousTestVerdict.Partial, ContinuousTestFreshness.Evaluate(statuses, rebuilt, watchHealthy: true));
    }

    [Fact]
    public async Task Policy_blocked_run_never_reports_green()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new FakeContinuousTestProvider
        {
            RunException = new ContinuousTestProviderException(
                "Application Control blocked the test host (0x800711C7)."),
        };
        var coordinator = new ContinuousTestCoordinator(provider, store);
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            coordinator);
        queue.Enqueue(EngineTestSupport.Change(workspace));
        await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var statuses = store.ListContinuousTestStatuses(EngineTestSupport.WorkspaceId);
        var selected = new CtFreshnessKey(EngineTestSupport.Identity, 2);
        ContinuousTestVerdict verdict = ContinuousTestFreshness.Evaluate(statuses, selected, watchHealthy: true);
        Assert.NotEqual(ContinuousTestVerdict.Green, verdict);
        Assert.True(verdict is ContinuousTestVerdict.Partial or ContinuousTestVerdict.Unknown);
        Assert.All(statuses, row => Assert.NotEqual(ContinuousTestState.Green, row.State));
    }

    [Fact]
    public void Watch_health_unknown_forces_unknown_verdict()
    {
        var health = new CtWatchHealth();
        health.RecordError("watch_overflow");
        Assert.False(health.IsHealthy);
        Assert.Equal("degraded", health.Snapshot(EngineTestSupport.WorkspaceId).State);
        var key = new CtFreshnessKey("gen-1", 1);
        Assert.Equal(
            ContinuousTestVerdict.Unknown,
            ContinuousTestFreshness.Evaluate(
                [Status("test:a", ContinuousTestState.Green, key)],
                key,
                watchHealthy: health.IsHealthy));
    }

    private static ContinuousTestStatus Status(
        string id,
        ContinuousTestState state,
        CtFreshnessKey key,
        CtFreshnessKey? proven = null) =>
        new(
            EngineTestSupport.WorkspaceId,
            id,
            state,
            key.IndexIdentity,
            key.Revision,
            ProvenFreshKey: proven ?? (state is ContinuousTestState.Green or ContinuousTestState.Red or ContinuousTestState.Skipped
                ? key
                : null));
}
