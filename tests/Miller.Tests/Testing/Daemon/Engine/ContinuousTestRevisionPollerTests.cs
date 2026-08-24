using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Indexing.Resolution;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ContinuousTestRevisionPollerTests
{
    [Fact]
    public async Task Restart_reconciles_a_persisted_empty_interval_before_enqueue_arm()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-restart-empty-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(root);
            using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
            var persisted = new CtFreshnessKey(EngineTestSupport.Identity, 2);
            store.SaveLastReconciledCursor(EngineTestSupport.WorkspaceId, persisted);
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(Observation(3));
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
            var poller = new ContinuousTestRevisionPoller(source, impact, cursorStore: store);

            ContinuousTestRevisionPollResult result = await poller.PollAsync(
                Request(workspace, enqueuer, armed: false),
                TestContext.Current.CancellationToken);

            Assert.Single(enqueuer.Changes);
            Assert.Equal("no_source_delta", result.Reason);
            Assert.Equal(new CtFreshnessKey(EngineTestSupport.Identity, 3),
                store.ReadLastReconciledCursor(EngineTestSupport.WorkspaceId));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Restart_reconciles_a_persisted_changed_interval_before_enqueue_arm()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-restart-changed-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(root);
            using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
            store.SaveLastReconciledCursor(
                EngineTestSupport.WorkspaceId,
                new CtFreshnessKey(EngineTestSupport.Identity, 2));
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(Observation(3));
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
            var poller = new ContinuousTestRevisionPoller(source, impact, cursorStore: store);

            ContinuousTestRevisionPollResult result = await poller.PollAsync(
                Request(workspace, enqueuer, armed: false),
                TestContext.Current.CancellationToken);

            Assert.Single(enqueuer.Changes);
            Assert.Equal("enqueued", result.Reason);
            Assert.Equal(new CtFreshnessKey(EngineTestSupport.Identity, 3),
                store.ReadLastReconciledCursor(EngineTestSupport.WorkspaceId));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Moving_cursor_retries_a_bounded_number_of_full_session_reads()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        string root = CreateWorkspaceRoot(fixture, out string dbPath);
        try
        {
            Execute(
                dbPath,
                "CREATE TABLE revision_file_changes (path TEXT, revision_id INTEGER, change_kind TEXT);");
            int opens = 0;
            WorkspaceReadHandle OpenSession(string workspaceRoot)
            {
                opens++;
                WorkspaceReadHandle handle = WorkspaceReadSessionFactory.Open(
                    dbPath,
                    workspaceRoot,
                    workspaceId: null,
                    storeEnabled: false);
                if (opens == 1)
                    Execute(dbPath, "INSERT INTO extraction_revisions VALUES (2);");
                return handle;
            }

            var source = new MillerFactImpactSource(null, OpenSession);
            var current = new CtFreshnessKey("ctgen1:artifact:art-1:blake3", 1);
            ContinuousTestImpactResult? result = await source.ImpactAsync(
                root,
                current,
                new CtFreshnessKey(current.IndexIdentity, 0),
                TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.Equal(ContinuousTestImpactOutcome.Unavailable, result!.Outcome);
            Assert.Equal("moving_cursor", result.Reason);
            Assert.Equal(3, opens);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Restart_identity_mismatch_does_not_persist_an_unreconciled_cursor()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-restart-identity-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(root);
            using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
            var persisted = new CtFreshnessKey("gen-old", 9);
            store.SaveLastReconciledCursor(EngineTestSupport.WorkspaceId, persisted);
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(new ContinuousTestRevisionObservation(
                EngineTestSupport.WorkspaceId,
                new CtFreshnessKey("gen-new", 1),
                true,
                "fresh",
                DateTimeOffset.UtcNow));
            var enqueuer = new RecordingEnqueuer();
            var poller = new ContinuousTestRevisionPoller(
                source,
                new ScriptedImpactSource(),
                cursorStore: store);

            ContinuousTestRevisionPollResult result = await poller.PollAsync(
                Request(workspace, enqueuer, armed: false),
                TestContext.Current.CancellationToken);

            Assert.Equal("rebuild", result.Reason);
            Assert.Empty(enqueuer.Changes);
            Assert.Equal(persisted, store.ReadLastReconciledCursor(EngineTestSupport.WorkspaceId));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

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

    /// <summary>
    /// Defect D3 (2026-08-21 live validation): the poller used to absorb an EMPTY revision delta
    /// without calling the enqueuer, so <c>ApplyRevisionAdvance</c> never ran and every green
    /// watermark stranded at the old revision — a routine refresh read stale forever. An empty
    /// complete delta must reach the queue exactly like a known-empty change: one change per
    /// project with NO paths and NO impact, carrying the proven interval, so the store can carry
    /// every currently fresh green to the new revision while nothing executes.
    /// </summary>
    [Fact]
    public async Task Empty_complete_delta_enqueues_an_empty_advance()
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
                    Reason = "no_source_delta",
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

            ContinuousTestDaemonChange change = Assert.Single(enqueuer.Changes);
            Assert.False(change.WorkspaceScope);
            Assert.Equal(ContinuousTestDeltaCompleteness.Complete, change.DeltaCompleteness);
            Assert.Empty(change.ChangedPaths);
            Assert.Empty(change.ImpactedSymbols);
            Assert.Empty(change.ImpactedTests);
            Assert.Equal(2, change.DeltaFromRevision);
            Assert.Equal(3, change.DeltaToRevision);
            Assert.Equal(1, result.EnqueuedProjects);
            Assert.Equal("no_source_delta", result.Reason);
            Assert.Equal(2, result.DeltaFromRevision);
            Assert.Equal(3, result.DeltaToRevision);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Fail closed: an EMPTY claim without a trustworthy interval cannot anchor a watermark
    /// advance, so the poller treats it as unavailable — no enqueue, and the span is retried on
    /// the next poll instead of being absorbed.
    /// </summary>
    [Fact]
    public async Task Empty_delta_without_interval_is_unavailable_and_does_not_enqueue()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-empty-noiv-").FullName;
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
                    Reason = "no_source_delta",
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
            Assert.Equal(0, result.EnqueuedProjects);
            Assert.Equal("unavailable_delta", result.Reason);
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

    /// <summary>
    /// Carry-forward from the Task 4 review: the impact source used to drop the truncation flags of
    /// its own fact read, so it could claim a COMPLETE impact ("Changed") off a truncated blast
    /// radius. A truncated read must degrade to Unavailable, which the poller never enqueues and
    /// the selector treats as Unknown — fail closed, never a silently narrow run.
    /// </summary>
    [Fact]
    public async Task Miller_impact_source_reports_a_truncated_impact_read_as_unavailable()
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
            var facts = new Miller.Tests.Testing.Selection.FakeCtFactSource();
            facts.Inner.Symbols.Add(Miller.Tests.Testing.Selection.FakeMillerFactSource.Symbol(
                "sym:service", "Service", "src/Service.cs"));
            facts.Inner.Tests.Add(Miller.Tests.Testing.Selection.FakeMillerFactSource.Hit(
                "test:service", "ServiceTests", "tests/ServiceTests.cs", isTest: true));
            facts.Inner.ImpactTruncatedByLimit = true;

            ContinuousTestImpactResult? impact = await new MillerFactImpactSource(_ => facts).ImpactAsync(
                root, current, from, TestContext.Current.CancellationToken);

            Assert.NotNull(impact);
            Assert.Equal(ContinuousTestImpactOutcome.Unavailable, impact!.Outcome);
            Assert.Equal("impact_truncated", impact.Reason);
            Assert.Empty(impact.ImpactedTests);
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
