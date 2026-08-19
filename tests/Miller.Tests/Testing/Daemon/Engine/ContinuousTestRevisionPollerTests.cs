using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ContinuousTestRevisionPollerTests
{
    [Fact]
    public async Task Complete_changed_delta_enqueues_project_scope_after_start()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-poller-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(root);
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(Observation(2));
            var impact = new ScriptedImpactSource
            {
                Result = new ContinuousTestImpactResult(
                    EngineTestSupport.WorkspaceId,
                    ["src/App.cs"],
                    [new ContinuousTestImpactedSymbol(Name: "App", Path: "src/App.cs")],
                    [new ContinuousTestImpactedTest(Name: "AppTests", Path: "tests/AppTests.cs")])
                {
                    Outcome = ContinuousTestImpactOutcome.Changed,
                    FromRevision = 2,
                    ToRevision = 3,
                },
            };
            var enqueuer = new RecordingEnqueuer();
            var poller = new ContinuousTestRevisionPoller(source, impact);
            await poller.PollAsync(Request(workspace, enqueuer, armed: false), TestContext.Current.CancellationToken);
            Assert.Empty(enqueuer.Changes);

            source.Observations.Enqueue(Observation(3));
            ContinuousTestRevisionPollResult result = await poller.PollAsync(
                Request(workspace, enqueuer, armed: true),
                TestContext.Current.CancellationToken);

            ContinuousTestDaemonChange change = Assert.Single(enqueuer.Changes);
            Assert.False(change.WorkspaceScope);
            Assert.Equal(ContinuousTestDeltaCompleteness.Complete, change.DeltaCompleteness);
            Assert.Equal(["src/App.cs"], change.ChangedPaths);
            Assert.Equal(1, result.EnqueuedProjects);
            Assert.Equal("enqueued", result.Reason);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Identity_change_is_a_rebuild_and_does_not_enqueue()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-rebuild-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(root);
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(new ContinuousTestRevisionObservation(
                EngineTestSupport.WorkspaceId,
                new CtFreshnessKey("gen-old", 9),
                true,
                "fresh",
                DateTimeOffset.UtcNow));
            var enqueuer = new RecordingEnqueuer();
            var poller = new ContinuousTestRevisionPoller(source, new ScriptedImpactSource());
            await poller.PollAsync(Request(workspace, enqueuer, armed: false), TestContext.Current.CancellationToken);

            source.Observations.Enqueue(new ContinuousTestRevisionObservation(
                EngineTestSupport.WorkspaceId,
                new CtFreshnessKey("gen-new", 1),
                true,
                "fresh",
                DateTimeOffset.UtcNow,
                Rebuild: true));
            ContinuousTestRevisionPollResult result = await poller.PollAsync(
                Request(workspace, enqueuer, armed: true),
                TestContext.Current.CancellationToken);

            Assert.Empty(enqueuer.Changes);
            Assert.Equal("rebuild", result.Reason);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Degraded_index_skips_enqueue()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-degraded-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(root);
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(new ContinuousTestRevisionObservation(
                EngineTestSupport.WorkspaceId,
                Freshness: null,
                IndexFresh: false,
                Status: "degraded",
                ObservedAt: DateTimeOffset.UtcNow));
            var enqueuer = new RecordingEnqueuer();
            var poller = new ContinuousTestRevisionPoller(source, new ScriptedImpactSource());
            ContinuousTestRevisionPollResult result = await poller.PollAsync(
                Request(workspace, enqueuer, armed: true),
                TestContext.Current.CancellationToken);
            Assert.Empty(enqueuer.Changes);
            Assert.Equal("degraded", result.Reason);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Empty_complete_delta_absorbs_without_enqueue()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-empty-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(root);
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(Observation(2));
            var impact = new ScriptedImpactSource
            {
                Result = new ContinuousTestImpactResult(EngineTestSupport.WorkspaceId, [], [], [])
                {
                    Outcome = ContinuousTestImpactOutcome.Empty,
                    FromRevision = 2,
                    ToRevision = 3,
                },
            };
            var enqueuer = new RecordingEnqueuer();
            var poller = new ContinuousTestRevisionPoller(source, impact);
            await poller.PollAsync(Request(workspace, enqueuer, armed: false), TestContext.Current.CancellationToken);
            source.Observations.Enqueue(Observation(3));
            ContinuousTestRevisionPollResult result = await poller.PollAsync(
                Request(workspace, enqueuer, armed: true),
                TestContext.Current.CancellationToken);
            Assert.Empty(enqueuer.Changes);
            Assert.Equal("no_source_delta", result.Reason);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static ContinuousTestRevisionObservation Observation(long revision) =>
        new(
            EngineTestSupport.WorkspaceId,
            new CtFreshnessKey("gen-1", revision),
            true,
            "fresh",
            DateTimeOffset.UtcNow);

    private static ContinuousTestRevisionPollRequest Request(
        ContinuousTestWorkspace workspace,
        IContinuousTestDaemonEnqueuer enqueuer,
        bool armed) =>
        new(
            EngineTestSupport.WorkspaceId,
            workspace.WorkspaceRoot,
            [
                new ContinuousTestProject("proj:1", EngineTestSupport.WorkspaceId, workspace.ProjectPath),
            ],
            enqueuer,
            EnqueueArmed: armed);
}
