using Microsoft.Data.Sqlite;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Indexing.Resolution;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ContinuousTestRevisionPollerTests
{
    private const string FixtureIdentity = "ctgen1:artifact:art-1:blake3";

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
    public async Task Moving_cursor_reprobes_after_real_session_drift_and_completes_interval()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        string root = CreateWorkspaceRoot(fixture, out string dbPath);
        try
        {
            Execute(
                dbPath,
                "CREATE TABLE revision_file_changes (path TEXT, revision_id INTEGER, change_kind TEXT);");
            var workspace = EngineTestSupport.Workspace(root);
            using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
            store.SaveLastReconciledCursor(
                EngineTestSupport.WorkspaceId,
                new CtFreshnessKey("ctgen1:artifact:art-1:blake3", 0));
            var source = new MillerArtifactRevisionSource();
            var facts = new Miller.Tests.Testing.Selection.FakeCtFactSource();
            var impact = new RealMovingImpactSource(
                dbPath,
                new MillerFactImpactSource(_ => facts));
            var enqueuer = new RecordingEnqueuer();
            var poller = new ContinuousTestRevisionPoller(source, impact, cursorStore: store);

            ContinuousTestRevisionPollResult result = await poller.PollAsync(
                Request(workspace, enqueuer, armed: false),
                TestContext.Current.CancellationToken);

            ContinuousTestDaemonChange change = Assert.Single(enqueuer.Changes);
            Assert.Equal("enqueued", result.Reason);
            Assert.Equal(2, impact.Calls);
            Assert.Equal(2, change.DeltaToRevision);
            Assert.Equal(
                new CtFreshnessKey("ctgen1:artifact:art-1:blake3", 2),
                store.ReadLastReconciledCursor(EngineTestSupport.WorkspaceId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Moving_cursor_reconciliation_stops_after_three_total_attempts()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-drift-bound-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(root);
            using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
            var persisted = new CtFreshnessKey(EngineTestSupport.Identity, 1);
            store.SaveLastReconciledCursor(EngineTestSupport.WorkspaceId, persisted);
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(Observation(2));
            var impact = new AlwaysMovingImpactSource();
            var enqueuer = new RecordingEnqueuer();
            var poller = new ContinuousTestRevisionPoller(source, impact, cursorStore: store);

            ContinuousTestRevisionPollResult result = await poller.PollAsync(
                Request(workspace, enqueuer, armed: false),
                TestContext.Current.CancellationToken);

            Assert.Equal("unavailable_delta", result.Reason);
            Assert.Empty(enqueuer.Changes);
            Assert.Equal(3, source.RefreshCount);
            Assert.Equal(3, impact.Calls);
            Assert.Equal(persisted, store.ReadLastReconciledCursor(EngineTestSupport.WorkspaceId));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Poll_without_projects_leaves_the_saved_cursor_at_the_reconciled_revision()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-no-projects-").FullName;
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

            ContinuousTestRevisionPollResult first = await poller.PollAsync(
                new ContinuousTestRevisionPollRequest(
                    EngineTestSupport.WorkspaceId,
                    workspace.WorkspaceRoot,
                    [],
                    enqueuer,
                    EnqueueArmed: true),
                TestContext.Current.CancellationToken);

            Assert.Empty(enqueuer.Changes);
            Assert.Equal(0, first.EnqueuedProjects);
            Assert.Equal("no_projects", first.Reason);
            Assert.Equal(persisted, store.ReadLastReconciledCursor(EngineTestSupport.WorkspaceId));

            ContinuousTestRevisionPollResult second = await poller.PollAsync(
                Request(workspace, enqueuer, armed: true),
                TestContext.Current.CancellationToken);

            ContinuousTestDaemonChange change = Assert.Single(enqueuer.Changes);
            Assert.Equal("enqueued", second.Reason);
            Assert.Equal(2, change.DeltaFromRevision);
            Assert.Equal(3, change.DeltaToRevision);
            Assert.Equal(new CtFreshnessKey(EngineTestSupport.Identity, 3),
                store.ReadLastReconciledCursor(EngineTestSupport.WorkspaceId));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Unavailable_delta_leaves_the_saved_cursor_unchanged()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-unavailable-").FullName;
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
                    Outcome = ContinuousTestImpactOutcome.Unavailable,
                    Reason = "bridge_error",
                },
            };
            var enqueuer = new RecordingEnqueuer();
            var poller = new ContinuousTestRevisionPoller(source, impact, cursorStore: store);

            ContinuousTestRevisionPollResult result = await poller.PollAsync(
                Request(workspace, enqueuer, armed: true),
                TestContext.Current.CancellationToken);

            Assert.Empty(enqueuer.Changes);
            Assert.Equal("unavailable_delta", result.Reason);
            Assert.Equal("bridge_error", result.DeltaReason);
            Assert.Equal(persisted, store.ReadLastReconciledCursor(EngineTestSupport.WorkspaceId));
        }
        finally
        {
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

    [Fact]
    public async Task Miller_impact_source_delivers_a_truncated_impact_read_as_a_changed_delta()
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
            Assert.Equal(ContinuousTestImpactOutcome.Changed, impact!.Outcome);
            Assert.Equal("impact_truncated", impact.Reason);
            Assert.Equal(["src/Service.cs"], impact.ChangedPaths);
            Assert.Equal(1, impact.FromRevision);
            Assert.Equal(2, impact.ToRevision);
            ContinuousTestImpactedTest test = Assert.Single(impact.ImpactedTests);
            Assert.Equal("ServiceTests", test.Name);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Truncated_impact_read_advances_as_an_unknown_selection_and_saves_the_cursor()
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
            var workspace = EngineTestSupport.Workspace(root);
            using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
            store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
            CommitGreen(store, "test:app", FixtureIdentity, 1);
            var selectorFacts = new Miller.Tests.Testing.Selection.FakeMillerFactSource
            {
                Current = new CtIndexCursor(FixtureIdentity, 2),
                ImpactTruncatedByLimit = true,
            };
            selectorFacts.Symbols.Add(Miller.Tests.Testing.Selection.FakeMillerFactSource.Symbol(
                "sym:service", "Service", "src/Service.cs"));
            var enqueuer = new ForwardingEnqueuer(new ContinuousTestDaemonQueue(
                store,
                new ContinuousTestImpactSelector(store, selectorFacts),
                new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store)));
            var impactFacts = new Miller.Tests.Testing.Selection.FakeCtFactSource();
            impactFacts.Inner.Symbols.Add(Miller.Tests.Testing.Selection.FakeMillerFactSource.Symbol(
                "sym:service", "Service", "src/Service.cs"));
            impactFacts.Inner.ImpactTruncatedByLimit = true;
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(FixtureObservation(1));
            var poller = new ContinuousTestRevisionPoller(
                source,
                new MillerFactImpactSource(_ => impactFacts),
                cursorStore: store);

            await poller.PollAsync(Request(workspace, enqueuer, armed: false), TestContext.Current.CancellationToken);
            source.Observations.Enqueue(FixtureObservation(2));
            ContinuousTestRevisionPollResult result = await poller.PollAsync(
                Request(workspace, enqueuer, armed: true),
                TestContext.Current.CancellationToken);

            Assert.Equal("enqueued", result.Reason);
            Assert.Equal(1, result.EnqueuedProjects);
            Assert.Equal(0, result.SelectedTests);
            ContinuousTestDaemonEnqueueResult enqueue = Assert.Single(enqueuer.Results);
            Assert.Equal(ContinuousTestSelectionOutcome.Unknown, enqueue.Selection.Outcome);
            Assert.Equal(["test:app"], enqueue.Selection.StaleTestCaseIds);
            Assert.Equal(
                new CtFreshnessKey(FixtureIdentity, 2),
                store.ReadLastReconciledCursor(EngineTestSupport.WorkspaceId));
            Assert.Empty(store.ListContinuousTestFreshWatermarks(EngineTestSupport.WorkspaceId, FixtureIdentity));
            ContinuousTestProjectedStatus projected = ContinuousTestStatusProjection.Project(
                new CtFreshnessKey(FixtureIdentity, 2),
                store.ListContinuousTestStatuses(EngineTestSupport.WorkspaceId),
                store.ListContinuousTestFreshWatermarks(EngineTestSupport.WorkspaceId, FixtureIdentity));
            Assert.Equal(1, projected.StaleCount);
            Assert.NotEqual(ContinuousTestVerdict.Green, projected.Verdict);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ReadAsync_DoesNotEnqueueOrSaveCursor()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-read-async-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(root);
            using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
            var persisted = new CtFreshnessKey(EngineTestSupport.Identity, 2);
            store.SaveLastReconciledCursor(EngineTestSupport.WorkspaceId, persisted);
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(Observation(3));
            var impact = new ScriptedImpactSource { Result = ChangedImpact(from: 2, to: 3) };
            var enqueuer = new RecordingEnqueuer();
            var poller = new ContinuousTestRevisionPoller(source, impact, cursorStore: store);

            ContinuousTestRevisionReadResult read = await poller.ReadAsync(
                Request(workspace, enqueuer, armed: true),
                TestContext.Current.CancellationToken);

            Assert.Equal("enqueue-ready", read.Reason);
            Assert.NotNull(read.Impact);
            Assert.Empty(enqueuer.Changes);
            Assert.Equal(persisted, store.ReadLastReconciledCursor(EngineTestSupport.WorkspaceId));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ApplyRead_EnqueuesAndAdvancesCursor()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-apply-read-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(root);
            using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
            store.SaveLastReconciledCursor(
                EngineTestSupport.WorkspaceId,
                new CtFreshnessKey(EngineTestSupport.Identity, 2));
            var source = new ScriptedRevisionSource();
            source.Observations.Enqueue(Observation(3));
            var impact = new ScriptedImpactSource { Result = ChangedImpact(from: 2, to: 3) };
            var enqueuer = new RecordingEnqueuer();
            var poller = new ContinuousTestRevisionPoller(source, impact, cursorStore: store);
            ContinuousTestRevisionPollRequest request = Request(workspace, enqueuer, armed: true);
            ContinuousTestRevisionReadResult read = await poller.ReadAsync(
                request,
                TestContext.Current.CancellationToken);

            ContinuousTestRevisionPollResult result = poller.ApplyRead(request, read);

            Assert.Equal("enqueued", result.Reason);
            Assert.Equal(1, result.EnqueuedProjects);
            Assert.Single(enqueuer.Changes);
            Assert.Equal(
                new CtFreshnessKey(EngineTestSupport.Identity, 3),
                store.ReadLastReconciledCursor(EngineTestSupport.WorkspaceId));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ApplyRead_FiresOnRebuildOncePerFreshness()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-apply-rebuild-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(root);
            var enqueuer = new RecordingEnqueuer();
            var poller = new ContinuousTestRevisionPoller(new ScriptedRevisionSource());
            int rebuilds = 0;
            var request = new ContinuousTestRevisionPollRequest(
                EngineTestSupport.WorkspaceId,
                workspace.WorkspaceRoot,
                [
                    new ContinuousTestProject("proj:1", EngineTestSupport.WorkspaceId, workspace.ProjectPath),
                ],
                enqueuer,
                OnRebuild: _ => rebuilds++);
            var freshness = new CtFreshnessKey(EngineTestSupport.Identity, 4);
            var read = new ContinuousTestRevisionReadResult(
                EngineTestSupport.WorkspaceId,
                freshness,
                "fresh",
                Impact: null,
                Observation: null,
                Reason: "rebuild",
                DeltaFromRevision: null,
                DeltaToRevision: null)
            {
                RebuildDetected = true,
            };

            poller.ApplyRead(request, read);
            poller.ApplyRead(request, read);

            Assert.Equal(1, rebuilds);
            Assert.Empty(enqueuer.Changes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PollAsync_Facade_MatchesSplit()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-poll-facade-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(root);
            using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
            using var splitStore = new ContinuousTestStore(CtSchema.DbPathFor(Path.Combine(root, "split")));
            var persisted = new CtFreshnessKey(EngineTestSupport.Identity, 2);
            store.SaveLastReconciledCursor(EngineTestSupport.WorkspaceId, persisted);
            splitStore.SaveLastReconciledCursor(EngineTestSupport.WorkspaceId, persisted);
            var facadeSource = new ScriptedRevisionSource();
            facadeSource.Observations.Enqueue(Observation(3));
            var splitSource = new ScriptedRevisionSource();
            splitSource.Observations.Enqueue(Observation(3));
            var facadeImpact = new ScriptedImpactSource { Result = ChangedImpact(from: 2, to: 3) };
            var splitImpact = new ScriptedImpactSource { Result = ChangedImpact(from: 2, to: 3) };
            var facadeEnqueuer = new RecordingEnqueuer();
            var splitEnqueuer = new RecordingEnqueuer();
            var facadePoller = new ContinuousTestRevisionPoller(facadeSource, facadeImpact, cursorStore: store);
            var splitPoller = new ContinuousTestRevisionPoller(splitSource, splitImpact, cursorStore: splitStore);

            ContinuousTestRevisionPollResult facade = await facadePoller.PollAsync(
                Request(workspace, facadeEnqueuer, armed: true),
                TestContext.Current.CancellationToken);
            ContinuousTestRevisionPollRequest splitRequest = Request(workspace, splitEnqueuer, armed: true);
            ContinuousTestRevisionReadResult read = await splitPoller.ReadAsync(
                splitRequest,
                TestContext.Current.CancellationToken);
            ContinuousTestRevisionPollResult split = splitPoller.ApplyRead(splitRequest, read);

            Assert.Equal(facade.Status, split.Status);
            Assert.Equal(facade.EnqueuedProjects, split.EnqueuedProjects);
            Assert.Equal(facade.SelectedTests, split.SelectedTests);
            Assert.Equal(facade.Reason, split.Reason);
            Assert.Equal(facade.DeltaFromRevision, split.DeltaFromRevision);
            Assert.Equal(facade.DeltaToRevision, split.DeltaToRevision);
            Assert.Equal(facadeEnqueuer.Changes.Count, splitEnqueuer.Changes.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static ContinuousTestImpactResult ChangedImpact(long from, long to) =>
        new(
            EngineTestSupport.WorkspaceId,
            ["src/App.cs"],
            [new ContinuousTestImpactedSymbol(Name: "App", Path: "src/App.cs")],
            [new ContinuousTestImpactedTest(Name: "AppTests", Path: "tests/AppTests.cs")])
        {
            Outcome = ContinuousTestImpactOutcome.Changed,
            FromRevision = from,
            ToRevision = to,
        };

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

    private static ContinuousTestRevisionObservation FixtureObservation(long revision) =>
        new(
            EngineTestSupport.WorkspaceId,
            new CtFreshnessKey(FixtureIdentity, revision),
            true,
            "fresh",
            DateTimeOffset.UtcNow);

    private static void CommitGreen(ContinuousTestStore store, string testCaseId, string identity, long revision)
    {
        string revisionText = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string runId = "seed-run:" + testCaseId + ":" + revisionText;
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: EngineTestSupport.WorkspaceId,
                Status: "running",
                SelectedRevision: revisionText,
                IndexIdentity: identity,
                Revision: revision),
            [testCaseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: EngineTestSupport.WorkspaceId,
            TestRunId: runId,
            SelectedRevision: revisionText,
            CurrentRevision: revisionText,
            IndexIdentity: identity,
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
                    IndexIdentity: identity,
                    Revision: revision),
            ]));
    }

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

    private sealed class ForwardingEnqueuer : IContinuousTestDaemonEnqueuer
    {
        private readonly IContinuousTestDaemonEnqueuer _inner;

        public ForwardingEnqueuer(IContinuousTestDaemonEnqueuer inner)
        {
            _inner = inner;
        }

        public List<ContinuousTestDaemonEnqueueResult> Results { get; } = [];

        public ContinuousTestDaemonEnqueueResult Enqueue(ContinuousTestDaemonChange change)
        {
            ContinuousTestDaemonEnqueueResult result = _inner.Enqueue(change);
            Results.Add(result);
            return result;
        }
    }

    private sealed class RealMovingImpactSource : IContinuousTestImpactSource
    {
        private readonly string _dbPath;
        private readonly IContinuousTestImpactSource _inner;

        public RealMovingImpactSource(string dbPath, IContinuousTestImpactSource inner)
        {
            _dbPath = dbPath;
            _inner = inner;
        }

        public int Calls { get; private set; }

        public Task<ContinuousTestImpactResult?> ImpactAsync(
            string workspaceRoot,
            CtFreshnessKey current,
            CtFreshnessKey? from,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Calls == 1)
                Execute(
                    _dbPath,
                    "INSERT INTO revision_file_changes VALUES ('src/App.cs', 2, 'updated'); INSERT INTO extraction_revisions VALUES (2);");
            return _inner.ImpactAsync(workspaceRoot, current, from, cancellationToken);
        }
    }

    private sealed class AlwaysMovingImpactSource : IContinuousTestImpactSource
    {
        public int Calls { get; private set; }

        public Task<ContinuousTestImpactResult?> ImpactAsync(
            string workspaceRoot,
            CtFreshnessKey current,
            CtFreshnessKey? from,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult<ContinuousTestImpactResult?>(new ContinuousTestImpactResult("", [], [], [])
            {
                Outcome = ContinuousTestImpactOutcome.Unavailable,
                Reason = "moving_cursor",
            });
        }
    }
}
