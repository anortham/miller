using Microsoft.Data.Sqlite;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Indexing.Resolution;
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

    [Fact]
    public async Task Miller_source_keeps_identity_on_a_revision_advance_and_flags_only_a_rebuild()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        string root = CreateWorkspaceRoot(fixture, out string dbPath);
        try
        {
            var source = new MillerArtifactRevisionSource();
            ContinuousTestRevisionObservation? first = await source.RefreshAsync(
                EngineTestSupport.WorkspaceId, root, TestContext.Current.CancellationToken);
            Assert.NotNull(first);
            CtFreshnessKey firstKey = first!.Freshness!.Value;
            Assert.Equal("ctgen1:artifact:art-1:blake3", firstKey.IndexIdentity);
            Assert.Equal(1, firstKey.Revision);
            Assert.False(first.Rebuild);

            // A file-change delta: the revision advances, the identity does not, no rebuild.
            Execute(dbPath, "INSERT INTO extraction_revisions VALUES (2);");
            ContinuousTestRevisionObservation? second = await source.RefreshAsync(
                EngineTestSupport.WorkspaceId, root, TestContext.Current.CancellationToken);
            CtFreshnessKey secondKey = second!.Freshness!.Value;
            Assert.Equal(firstKey.IndexIdentity, secondKey.IndexIdentity);
            Assert.Equal(2, secondKey.Revision);
            Assert.False(second.Rebuild);

            // A promoted rebuild: the artifact id changes, so the identity changes.
            Execute(dbPath, "UPDATE artifact_metadata SET value = 'art-2' WHERE key = 'artifact_id';");
            ContinuousTestRevisionObservation? third = await source.RefreshAsync(
                EngineTestSupport.WorkspaceId, root, TestContext.Current.CancellationToken);
            Assert.NotEqual(firstKey.IndexIdentity, third!.Freshness!.Value.IndexIdentity);
            Assert.True(third.Rebuild);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Both_intakes_produce_the_same_cursor_for_the_same_artifact()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        string root = CreateWorkspaceRoot(fixture, out _);
        try
        {
            ContinuousTestRevisionObservation? observation = await new MillerArtifactRevisionSource().RefreshAsync(
                EngineTestSupport.WorkspaceId, root, TestContext.Current.CancellationToken);
            using var adapter = CtFactAdapter.OpenArtifact(Path.Combine(root, ".miller", "symbols.db"));

            CtFreshnessKey pollerKey = observation!.Freshness!.Value;
            Assert.Equal(adapter.Current.IndexIdentity, pollerKey.IndexIdentity);
            Assert.Equal(adapter.Current.Revision, pollerKey.Revision);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Miller_impact_source_passes_the_family_id_to_the_delta_reader()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("file-service", "src/Service.cs");
        fixture.AddSymbol("file-service", "cls-service", "Service", "class", "src/Service.cs");
        string root = CreateWorkspaceRoot(fixture, out string dbPath);
        try
        {
            Execute(
                dbPath,
                """
                CREATE TABLE revision_file_changes (path TEXT, revision_id INTEGER, change_kind TEXT);
                INSERT INTO revision_file_changes VALUES ('src/Service.cs', 2, 'updated');
                INSERT INTO extraction_revisions VALUES (2);
                """);
            ContinuousTestRevisionObservation? observation = await new MillerArtifactRevisionSource().RefreshAsync(
                EngineTestSupport.WorkspaceId, root, TestContext.Current.CancellationToken);
            CtFreshnessKey current = observation!.Freshness!.Value;
            var from = new CtFreshnessKey(current.IndexIdentity, 1);

            ContinuousTestImpactResult? impact = await new MillerFactImpactSource().ImpactAsync(
                root, current, from, TestContext.Current.CancellationToken);

            // The delta reader compares its from-artifact id with artifact_metadata.artifact_id.
            // Passing the generation identity instead would report "artifact_changed".
            Assert.NotNull(impact);
            Assert.Equal(ContinuousTestImpactOutcome.Changed, impact!.Outcome);
            Assert.Equal(["src/Service.cs"], impact.ChangedPaths);
            Assert.Equal(1, impact.FromRevision);
            Assert.Equal(2, impact.ToRevision);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static string CreateWorkspaceRoot(ResolutionArtifactFixture fixture, out string dbPath)
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-source-").FullName;
        string millerDir = Path.Combine(root, ".miller");
        Directory.CreateDirectory(millerDir);
        dbPath = Path.Combine(millerDir, "symbols.db");
        SqliteConnection.ClearAllPools();
        File.Copy(fixture.DbPath, dbPath);
        return root;
    }

    private static void Execute(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
