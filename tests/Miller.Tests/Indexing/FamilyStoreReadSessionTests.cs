using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using System.Security.Cryptography;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class FamilyStoreReadSessionTests
{
    [Fact]
    public void CurrentManifestFiltersRetainedVersionsBeforeReaderQueries()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding, "workspace-a");

        (long Files, long Symbols, string Name) actual = session.Read(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT (SELECT COUNT(*) FROM files), (SELECT COUNT(*) FROM symbols), " +
                "(SELECT name FROM symbols ORDER BY symbol_id LIMIT 1)";
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2));
        });

        Assert.Equal((1, 1, "Visible"), actual);
        Assert.Equal(WorkspaceReadMode.FamilyStore, session.Snapshot.Mode);
        Assert.Equal("view-a", session.Snapshot.ViewId);
        Assert.Equal("manifest-current", session.Snapshot.Freshness.ManifestHash);
        Assert.Equal(2, session.Snapshot.Freshness.StoreLogSequence);
        Assert.Equal(IndexLevels.FullMetadataValue, session.Snapshot.IndexLevel);
        Assert.Equal("11111111-1111-4111-8111-111111111111:gen-001", session.Snapshot.Freshness.StoreInstanceId);
        Assert.Equal("view-a", session.Snapshot.Freshness.ViewId);
        Assert.Equal("gen-001", session.Snapshot.Freshness.GenerationName);
        Assert.Equal(2, session.Snapshot.Freshness.ManifestGeneration);
        Assert.Equal(IndexLevels.FullMetadataValue, session.Snapshot.Freshness.IndexLevel);
        Assert.NotNull(session.Snapshot.Freshness.LevelStampL1);
        Assert.NotNull(session.Snapshot.Freshness.LevelStampL2);
        Assert.NotNull(session.Snapshot.Freshness.LevelStampL3);
        Assert.NotEqual(
            session.Snapshot.IndexIdentity,
            (session.Snapshot with
            {
                Freshness = session.Snapshot.Freshness with { IndexLevel = IndexLevels.SymbolsMetadataValue },
                IndexLevel = IndexLevels.SymbolsMetadataValue,
            }).IndexIdentity);
    }

    [Fact]
    public void StoreLogCursorIgnoresSiblingViewEventsButTracksSharedVersionChanges()
    {
        using StoreFixture fixture = StoreFixture.Create();

        Assert.Equal(2, ReadStoreLogSequence(fixture));
        AppendStoreLog(fixture, 3, "view-b", versionId: null);
        Assert.Equal(2, ReadStoreLogSequence(fixture));
        AppendStoreLog(fixture, 4, "view-b", versionId: 2);
        Assert.Equal(4, ReadStoreLogSequence(fixture));
        AppendStoreLog(fixture, 5, viewId: null, versionId: null);
        Assert.Equal(5, ReadStoreLogSequence(fixture));
    }

    [Fact]
    public void SnapshotRevisionUsesStoreLogSequenceWhenItDiffersFromManifestGeneration()
    {
        using StoreFixture fixture = StoreFixture.Create();
        AppendStoreLog(fixture, 5, viewId: null, versionId: null);

        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding, "workspace-a");

        Assert.Equal(5, session.Snapshot.Freshness.Revision);
        Assert.Equal(5, session.Snapshot.Freshness.StoreLogSequence);
        Assert.Equal(2, session.Snapshot.Freshness.ManifestGeneration);
    }

    [Fact]
    public void FreshnessProbeReadsStoreCursorWithoutOpeningAProjection()
    {
        using StoreFixture fixture = StoreFixture.Create();

        WorkspaceFreshnessProbe probe = FamilyStoreReadSession.Probe(fixture.Binding);

        Assert.Equal(2, probe.Revision);
        Assert.Equal("11111111-1111-4111-8111-111111111111:gen-001", probe.StoreInstanceId);
        Assert.Equal("view-a", probe.ViewId);
        Assert.Equal(2, probe.ManifestGeneration);
        Assert.Equal("manifest-current", probe.ManifestHash);
        Assert.Equal(fixture.Binding.StoreRoot, probe.StoreRoot);
        Assert.Equal("2.31.0", probe.BinaryVersion);
    }

    [Fact]
    public void FamilyMismatchRefusesBeforeOpeningAReadSession()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreFamilyBinding wrong = fixture.Binding with { FamilyId = Guid.Parse("22222222-2222-4222-8222-222222222222") };

        FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() =>
            FamilyStoreReadSession.Open(wrong));

        Assert.Equal(FamilyStoreReadFailure.FamilyMismatch, error.Failure);
    }

    [Fact]
    public void MissingExtractionIdentityEpochRefusesBeforeBuildingCompatibilityViews()
    {
        using StoreFixture fixture = StoreFixture.Create();
        DeleteStoreMetadata(fixture, "extraction_identity_epoch");

        FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() =>
            FamilyStoreReadSession.Open(fixture.Binding));

        Assert.Equal(FamilyStoreReadFailure.SchemaIncompatible, error.Failure);
    }

    [Fact]
    public void MalformedExtractionIdentityEpochRefusesBeforeBuildingCompatibilityViews()
    {
        using StoreFixture fixture = StoreFixture.Create();
        UpdateStoreMetadata(fixture, "extraction_identity_epoch", "not-an-epoch");

        FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() =>
            FamilyStoreReadSession.Open(fixture.Binding));

        Assert.Equal(FamilyStoreReadFailure.SchemaIncompatible, error.Failure);
    }

    [Fact]
    public void SessionConnectionIsQueryOnlyAfterProjectionSetup()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);

        SqliteException error = Assert.Throws<SqliteException>(() => session.Read(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO store_log VALUES (3,'bad','bad',NULL,NULL,NULL,NULL,0,'{}','2026-08-09T00:00:02Z')";
            return command.ExecuteNonQuery();
        }));

        Assert.Equal(8, error.SqliteErrorCode);
    }

    [Fact]
    public void PatternFactsReaderUsesTheManifestScopedStoreProjection()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);

        IReadOnlyList<PatternListRow> rows = new PatternFactsReader().List(
            session,
            patternId: null,
            language: null,
            pathGlob: null,
            metadataFilters: null);

        PatternListRow row = Assert.Single(rows);
        Assert.Equal("visible.pattern.v1", row.PatternId);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public void RevisionDeltaReaderComparesThePriorAndCurrentStoreManifests()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);

        RevisionDeltaResult delta = RevisionDeltaReader.Read(
            session,
            fromRevision: 1,
            fromArtifactId: fixture.Binding.FamilyId.ToString("D"));

        Assert.Equal(RevisionDeltaStatus.Complete, delta.Status);
        Assert.Equal(["same.cs"], delta.ChangedPaths);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(delta.DeletedPaths));
    }

    [Fact]
    public void RevisionDeltaReaderUsesTheServingViewStoreCursor()
    {
        using StoreFixture fixture = StoreFixture.Create();
        AppendStoreLog(fixture, 3, "view-b", versionId: null);
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);

        RevisionDeltaResult delta = RevisionDeltaReader.Read(
            session,
            fromRevision: 1,
            fromArtifactId: fixture.Binding.FamilyId.ToString("D"));

        Assert.Equal(2, delta.ToRevision);
    }

    [Fact]
    public void RevisionDeltaReaderRefusesAStoreSpanWithoutARecoverableBaselineManifest()
    {
        using StoreFixture fixture = StoreFixture.Create();
        DeleteManifest(fixture, generation: 1);
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);

        RevisionDeltaResult delta = RevisionDeltaReader.Read(
            session,
            fromRevision: 1,
            fromArtifactId: fixture.Binding.FamilyId.ToString("D"));

        Assert.Equal(RevisionDeltaStatus.Unavailable, delta.Status);
        Assert.Equal("pruned_history", delta.Reason);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(delta.ChangedPaths));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(delta.DeletedPaths));
    }

    [Fact]
    public void SearchSidecarFastForwardsAcrossReusedManifestImportChunks()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var sidecar = new SymbolSearchSidecar(enabled: true, RegionIndexOptions.Disabled);
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            Assert.True(sidecar.EnsureStoreCurrent(fixture.Binding.StoreRoot, initial));

        string databasePath = StoreSidecarCatalog.PathFor(
            fixture.Binding.StoreRoot,
            StoreSidecarKind.Search,
            fixture.Binding.ViewId);
        AddSentinel(databasePath);
        AppendReusedManifestImport(fixture);

        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        Assert.True(sidecar.EnsureStoreCurrent(fixture.Binding.StoreRoot, updated));

        AssertFastForwarded(databasePath, StoreSidecarKind.Search, updated, "SELECT revision FROM meta;");
    }

    [Fact]
    public void ContentSidecarFastForwardsAcrossReusedManifestImportChunks()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var sidecar = new ContentCorpusSidecar();
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            Assert.True(sidecar.EnsureStoreCurrent(fixture.Binding.StoreRoot, initial));

        string databasePath = StoreSidecarCatalog.PathFor(
            fixture.Binding.StoreRoot,
            StoreSidecarKind.Content,
            fixture.Binding.ViewId);
        AddSentinel(databasePath);
        AppendReusedManifestImport(fixture);

        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        Assert.True(sidecar.EnsureStoreCurrent(fixture.Binding.StoreRoot, updated));

        AssertFastForwarded(
            databasePath,
            StoreSidecarKind.Content,
            updated,
            "SELECT workspace_revision FROM content_meta;");
    }

    [Fact]
    public void StoreSidecarFastForwardRollsBackMetadataWhenTheStampUpdateFails()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var sidecar = new SymbolSearchSidecar(enabled: true, RegionIndexOptions.Disabled);
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            Assert.True(sidecar.EnsureStoreCurrent(fixture.Binding.StoreRoot, initial));

        string databasePath = StoreSidecarCatalog.PathFor(
            fixture.Binding.StoreRoot,
            StoreSidecarKind.Search,
            fixture.Binding.ViewId);
        RejectStampUpdates(databasePath);
        AppendReusedManifestImport(fixture);

        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, updated.Snapshot);
        bool advanced = StoreSidecarCatalog.TryFastForwardEmptyDelta(
            databasePath,
            expected,
            updated,
            (connection, transaction, revision) =>
                SearchIndexWriter.TryFastForwardStoreMetadata(
                    connection,
                    transaction,
                    revision,
                    RegionIndexOptions.Disabled));

        Assert.False(advanced);
        Assert.Equal(2, ReadInt64(databasePath, "SELECT revision FROM meta;"));
        Assert.Equal(2, StoreSidecarCatalog.TryRead(databasePath)!.StoreLogSequence);
    }

    [Fact]
    public void SearchSidecarRebuildsWhenTheStoreDeltaContainsChanges()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var sidecar = new SymbolSearchSidecar(enabled: true, RegionIndexOptions.Disabled);
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);
        Assert.True(sidecar.EnsureStoreCurrent(fixture.Binding.StoreRoot, session));

        string databasePath = StoreSidecarCatalog.PathFor(
            fixture.Binding.StoreRoot,
            StoreSidecarKind.Search,
            fixture.Binding.ViewId);
        StoreSidecarStamp current = StoreSidecarStamp.FromSnapshot(StoreSidecarKind.Search, session.Snapshot);
        StoreSidecarCatalog.Stamp(databasePath, current with { StoreLogSequence = 1 });
        AddSentinel(databasePath);

        Assert.True(sidecar.EnsureStoreCurrent(fixture.Binding.StoreRoot, session));

        Assert.False(TableExists(databasePath, "fast_forward_sentinel"));
        Assert.Equal(current, StoreSidecarCatalog.TryRead(databasePath));
    }

    [Fact]
    public void EnabledWorkspaceFactoryUsesTheValidatedPointerInsteadOfTheLegacyArtifact()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);

        using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
            Path.Combine(fixture.Root, "missing-legacy.db"),
            fixture.Binding.WorkspaceRoot,
            "workspace-a",
            storeEnabled: true);

        Assert.Equal(WorkspaceReadMode.FamilyStore, session.Snapshot.Mode);
        Assert.Equal("view-a", session.Snapshot.ViewId);
    }

    [Fact]
    public void DisabledWorkspaceFactoryRefusesLegacyReadsWhileStorePointerRemains()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);

        FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() =>
            WorkspaceReadSessionFactory.Open(
                Path.Combine(fixture.Root, "legacy.db"),
                fixture.Binding.WorkspaceRoot,
                "workspace-a",
                storeEnabled: false));

        Assert.Equal(FamilyStoreReadFailure.BindingNotReady, error.Failure);
    }

    [Fact]
    public void DisabledWorkspaceFactoryProbeRefusesLegacyReadsWhileStorePointerRemains()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);

        FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() =>
            WorkspaceReadSessionFactory.Probe(
                Path.Combine(fixture.Root, "legacy.db"),
                fixture.Binding.WorkspaceRoot,
                "workspace-a",
                storeEnabled: false));

        Assert.Equal(FamilyStoreReadFailure.BindingNotReady, error.Failure);
    }

    [Fact]
    public void ServingGenerationSymlinkOutsideTheFamilyRootIsRejected()
    {
        using StoreFixture fixture = StoreFixture.Create();
        string generation = Path.Combine(fixture.Binding.StoreRoot, "gen-001");
        string outside = Path.Combine(fixture.Root, "outside-generation");
        Directory.Move(generation, outside);
        Directory.CreateSymbolicLink(generation, outside);

        FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() =>
            FamilyStoreReadSession.Open(fixture.Binding));

        Assert.Equal(FamilyStoreReadFailure.Corrupt, error.Failure);
    }

    [Fact]
    public void StoreSequenceAdvanceReopensTheRotatedResolutionBasePath()
    {
        using StoreFixture fixture = StoreFixture.Create();
        InstallResolutionBase(
            fixture,
            "base-before",
            "manifest-prior",
            baseVersionId: 1,
            baseTargetSymbolId: "target-prior",
            deltaTargetSymbolId: "target-before",
            sequence: 3,
            deltaGeneration: 1);

        using (FamilyStoreReadSession before = FamilyStoreReadSession.Open(fixture.Binding))
        {
            Assert.Equal(3, before.Snapshot.Freshness.StoreLogSequence);
            Assert.Equal("target-before", ReadResolutionTarget(before));
        }

        string oldBasePath = ResolutionBasePath(fixture, "base-before");
        InstallResolutionBase(
            fixture,
            "base-after",
            "manifest-current",
            baseVersionId: 2,
            baseTargetSymbolId: "target-after",
            deltaTargetSymbolId: null,
            sequence: 4,
            deltaGeneration: 2);
        (string ManifestHash, long ResolverOutputEpoch, long VersionId) beforeIdentity =
            ReadResolutionBaseIdentity(fixture, "base-before");
        (string ManifestHash, long ResolverOutputEpoch, long VersionId) afterIdentity =
            ReadResolutionBaseIdentity(fixture, "base-after");
        Assert.Equal(("manifest-prior", 6L, 1L), beforeIdentity);
        Assert.Equal(("manifest-current", 6L, 2L), afterIdentity);
        Assert.NotEqual(
            (beforeIdentity.ManifestHash, beforeIdentity.ResolverOutputEpoch),
            (afterIdentity.ManifestHash, afterIdentity.ResolverOutputEpoch));
        File.Delete(oldBasePath);

        using FamilyStoreReadSession after = FamilyStoreReadSession.Open(fixture.Binding);
        Assert.Equal(4, after.Snapshot.Freshness.StoreLogSequence);
        Assert.Equal("target-after", ReadResolutionTarget(after));
    }

    [Fact]
    public void ResolutionViewsExposeTargetVersionForIndexedReverseGraphReads()
    {
        using StoreFixture fixture = StoreFixture.Create();
        InstallResolutionBase(
            fixture,
            "base-target-version",
            "manifest-current",
            baseVersionId: 2,
            baseTargetSymbolId: "symbol",
            deltaTargetSymbolId: null,
            sequence: 3,
            deltaGeneration: 1);
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);

        (string[] IdentifierColumns, string[] PendingColumns) columns = session.Read(connection =>
        {
            static string[] ReadColumns(SqliteConnection connection, string view)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = $"PRAGMA table_info({view});";
                using SqliteDataReader reader = command.ExecuteReader();
                var names = new List<string>();
                while (reader.Read())
                    names.Add(reader.GetString(1));
                return names.ToArray();
            }

            return (
                ReadColumns(connection, "identifier_resolutions"),
                ReadColumns(connection, "pending_resolutions"));
        });

        Assert.Contains("target_version_id", columns.IdentifierColumns);
        Assert.Contains("target_version_id", columns.PendingColumns);
    }

    [Fact]
    public void ReverseGraphReadsUseTargetVersionIndexesOnPinnedFamilyView()
    {
        const string targetId = "60000000000000000000000000000001";
        const string identifierCallerId = "60000000000000000000000000000002";
        const string pendingCallerId = "60000000000000000000000000000003";
        using StoreFixture fixture = StoreFixture.Create();
        InstallResolutionBase(
            fixture,
            "base-graph-plan",
            "manifest-current",
            baseVersionId: 2,
            baseTargetSymbolId: targetId,
            deltaTargetSymbolId: null,
            sequence: 3,
            deltaGeneration: 1,
            includeGraphRows: true);
        InstallGraphRows(fixture, targetId, identifierCallerId, pendingCallerId);
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);
        session.Read(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA automatic_index=OFF;";
            command.ExecuteNonQuery();
            return 0;
        });
        session.CaptureGraphResolutionQueryPlan = true;
        using var graph = new SqliteSymbolGraphIndex(session);

        IReadOnlyList<ReachedNode> result = graph.Reach([targetId], 1, 10, Direction.Reverse);

        Assert.Equal([identifierCallerId, pendingCallerId], result.Select(static node => node.Id));
        Assert.True(
            session.LastGraphResolutionQueryPlan.Any(detail =>
                detail.Contains("idx_read_resolution_identifiers_target", StringComparison.Ordinal) &&
                detail.Contains("target_version_id=? AND target_symbol_id=?", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, session.LastGraphResolutionQueryPlan));
        Assert.True(
            session.LastGraphResolutionQueryPlan.Any(detail =>
                detail.Contains("idx_read_resolution_pending_target", StringComparison.Ordinal) &&
                detail.Contains("target_version_id=? AND target_symbol_id=?", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, session.LastGraphResolutionQueryPlan));
        Assert.DoesNotContain(
            session.LastGraphResolutionQueryPlan,
            detail => detail.Contains("AUTOMATIC", StringComparison.Ordinal));
    }

    [Fact]
    public void GraphResolutionReadsHonorDeltaReplacementAndPendingTombstone()
    {
        const string baseTargetId = "61000000000000000000000000000001";
        const string deltaTargetId = "61000000000000000000000000000002";
        const string identifierCallerId = "61000000000000000000000000000003";
        const string pendingCallerId = "61000000000000000000000000000004";
        using StoreFixture fixture = StoreFixture.Create();
        InstallResolutionBase(
            fixture,
            "base-graph-overlay",
            "manifest-current",
            baseVersionId: 2,
            baseTargetSymbolId: baseTargetId,
            deltaTargetSymbolId: deltaTargetId,
            sequence: 3,
            deltaGeneration: 1,
            includeGraphRows: true);
        InstallGraphRows(fixture, baseTargetId, identifierCallerId, pendingCallerId);
        InstallGraphOverlayRows(fixture, deltaTargetId);
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);
        using var graph = new SqliteSymbolGraphIndex(session);

        IReadOnlyList<ReachedNode> baseResult = graph.Reach([baseTargetId], 1, 10, Direction.Reverse);
        IReadOnlyList<ReachedNode> deltaResult = graph.Reach([deltaTargetId], 1, 10, Direction.Reverse);

        Assert.Empty(baseResult);
        ReachedNode reached = Assert.Single(deltaResult);
        Assert.Equal(identifierCallerId, reached.Id);
    }

    [Fact]
    public void FamilyFrontierUsesBoundedStorageCapabilitiesWithExactParity()
    {
        const string targetId = "62000000000000000000000000000001";
        const string identifierCallerId = "62000000000000000000000000000002";
        const string pendingCallerId = "62000000000000000000000000000003";
        const string relationshipTargetId = "62000000000000000000000000000004";
        const string relationshipCallerId = "62000000000000000000000000000005";
        const string nameTargetId = "62000000000000000000000000000006";
        const string nameCallerId = "62000000000000000000000000000007";
        using StoreFixture fixture = StoreFixture.Create();
        InstallResolutionBase(
            fixture,
            "base-combined-frontier",
            "manifest-current",
            baseVersionId: 2,
            baseTargetSymbolId: targetId,
            deltaTargetSymbolId: null,
            sequence: 3,
            deltaGeneration: 1,
            includeGraphRows: true);
        InstallGraphRows(fixture, targetId, identifierCallerId, pendingCallerId);
        InstallCombinedFrontierRows(
            fixture,
            targetId,
            relationshipTargetId,
            relationshipCallerId,
            nameTargetId,
            nameCallerId);
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);
        using var graph = new SqliteSymbolGraphIndex(session)
        {
            CaptureFrontierQueryPlan = true,
        };

        IReadOnlyList<ReachedNode> result = graph.Reach([targetId], 1, 20, Direction.Both);
        GraphQueryTelemetrySnapshot telemetry = graph.QueryTelemetry;

        Assert.Equal(
            [identifierCallerId, pendingCallerId, relationshipTargetId, relationshipCallerId, nameTargetId, nameCallerId],
            result.Select(static node => node.Id));
        Assert.Equal(2, telemetry.FrontierRelationships.Executions);
        Assert.Equal(0, telemetry.FrontierUnresolvedNames.Executions);
        Assert.Equal(1, telemetry.FrontierBatch.Executions);
        Assert.Equal(2, graph.LastFrontierQueryPlan.RelationshipStatements.Count);
        Assert.Empty(graph.LastFrontierQueryPlan.UnresolvedNameStatements);
        Assert.All(
            graph.LastFrontierQueryPlan.RelationshipStatements,
            plan => Assert.DoesNotContain(
                plan,
                detail => detail.Contains("identifier_resolutions", StringComparison.Ordinal)));
    }

    [Fact]
    public void FamilyUnresolvedNameReadsPreserveOverlayAndHomonymParityWithoutCompatibilityView()
    {
        const string forwardSourceId = "64000000000000000000000000000001";
        const string uniqueTargetId = "64000000000000000000000000000002";
        const string deltaUnresolvedTargetId = "64000000000000000000000000000003";
        const string reverseTargetId = "64000000000000000000000000000004";
        const string reverseCallerId = "64000000000000000000000000000005";
        using StoreFixture fixture = StoreFixture.Create();
        InstallResolutionBase(
            fixture,
            "base-unresolved-name",
            "manifest-current",
            baseVersionId: 2,
            baseTargetSymbolId: "64000000000000000000000000000010",
            deltaTargetSymbolId: null,
            sequence: 3,
            deltaGeneration: 1,
            includeGraphRows: false);
        InstallGraphRows(
            fixture,
            "64000000000000000000000000000010",
            "64000000000000000000000000000011",
            "64000000000000000000000000000012");
        InstallUnresolvedNameGraphRows(
            fixture,
            forwardSourceId,
            uniqueTargetId,
            deltaUnresolvedTargetId,
            reverseTargetId,
            reverseCallerId);
        FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);
        session.CaptureGraphUnresolvedNameQueryPlan = true;
        session.CaptureGraphResolutionQueryPlan = true;
        using var handle = new WorkspaceReadHandle(session);
        using var graph = new SqliteSymbolGraphIndex(handle)
        {
            CaptureFrontierQueryPlan = true,
        };
        var observations = new List<GraphStatementObservation>();
        graph.StatementObserver = observations.Add;

        IReadOnlyList<ReachedNode> result = graph.Reach(
            [forwardSourceId, reverseTargetId],
            1,
            20,
            Direction.Both);

        Assert.Equal(
            [
                uniqueTargetId,
                deltaUnresolvedTargetId,
                reverseCallerId,
                "64000000000000000000000000000006",
                "64000000000000000000000000000007",
            ],
            result.Select(static node => node.Id));
        Assert.Equal(0, graph.QueryTelemetry.FrontierUnresolvedNames.Executions);
        Assert.Empty(graph.LastFrontierQueryPlan.UnresolvedNameStatements);
        Assert.Contains(
            session.LastGraphUnresolvedNameQueryPlan,
            detail => detail.Contains("idx_read_identifiers_containing", StringComparison.Ordinal));
        Assert.Contains(
            session.LastGraphUnresolvedNameQueryPlan,
            detail => detail.Contains("idx_read_identifiers_name_kind", StringComparison.Ordinal));
        Assert.DoesNotContain(
            session.LastGraphUnresolvedNameQueryPlan,
            detail => detail.Contains("MATERIALIZE", StringComparison.Ordinal)
                || detail.Contains("SCAN r", StringComparison.Ordinal));
        Assert.Contains(
            session.LastGraphResolutionQueryPlan,
            detail => detail.Contains("idx_read_resolution_identifiers_target", StringComparison.Ordinal));
        Assert.Contains(
            session.LastGraphResolutionQueryPlan,
            detail => detail.Contains("idx_read_resolution_pending_target", StringComparison.Ordinal));
        Assert.Equal(
            [
                (GraphStatementPhase.UnresolvedNameForward, 2),
                (GraphStatementPhase.UnresolvedNameReverse, 1),
            ],
            observations
                .Where(static observation => observation.Phase is GraphStatementPhase.UnresolvedNameForward
                    or GraphStatementPhase.UnresolvedNameReverse)
                .Select(static observation => (observation.Phase, observation.Rows)));
    }

    [Fact]
    public void FamilyResolutionObserverReportsOnlyCompletedArmsBeforeCancellation()
    {
        const string targetId = "63000000000000000000000000000001";
        const string identifierCallerId = "63000000000000000000000000000002";
        const string pendingCallerId = "63000000000000000000000000000003";
        using StoreFixture fixture = StoreFixture.Create();
        InstallResolutionBase(
            fixture,
            "base-resolution-observer",
            "manifest-current",
            baseVersionId: 2,
            baseTargetSymbolId: targetId,
            deltaTargetSymbolId: null,
            sequence: 3,
            deltaGeneration: 1,
            includeGraphRows: true);
        InstallGraphRows(fixture, targetId, identifierCallerId, pendingCallerId);
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);
        using var graph = new SqliteSymbolGraphIndex(session);
        var observations = new List<GraphStatementObservation>();
        graph.StatementObserver = observation =>
        {
            observations.Add(observation);
            if (observation.Phase == GraphStatementPhase.PendingBaseForward)
                throw new OperationCanceledException("stop after the completed pending base forward arm");
        };

        Assert.Throws<OperationCanceledException>(() =>
            graph.Reach([targetId], 1, 20, Direction.Both));

        Assert.Equal(
            [
                GraphStatementPhase.RelationshipForward,
                GraphStatementPhase.RelationshipReverse,
                GraphStatementPhase.UnresolvedNameForward,
                GraphStatementPhase.UnresolvedNameReverse,
                GraphStatementPhase.IdentifierBaseForward,
                GraphStatementPhase.IdentifierDeltaForward,
                GraphStatementPhase.PendingBaseForward,
            ],
            observations.Select(static observation => observation.Phase));
        Assert.DoesNotContain(
            observations,
            static observation => observation.Phase is GraphStatementPhase.PendingDeltaForward
                or GraphStatementPhase.IdentifierBaseReverse
                or GraphStatementPhase.IdentifierDeltaReverse
                or GraphStatementPhase.PendingBaseReverse
                or GraphStatementPhase.PendingDeltaReverse
                or GraphStatementPhase.FamilyResolution
                or GraphStatementPhase.Supplemental
                or GraphStatementPhase.Completion);
    }

    private static long ReadStoreLogSequence(StoreFixture fixture)
    {
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);
        return Assert.IsType<long>(session.Snapshot.Freshness.StoreLogSequence);
    }

    private static string ReadResolutionTarget(FamilyStoreReadSession session) =>
        session.Read(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT target_symbol_id FROM identifier_resolutions WHERE identifier_id='identifier';";
            return Assert.IsType<string>(command.ExecuteScalar());
        });

    private static void InstallResolutionBase(
        StoreFixture fixture,
        string baseId,
        string baseManifestHash,
        long baseVersionId,
        string baseTargetSymbolId,
        string? deltaTargetSymbolId,
        long sequence,
        long deltaGeneration)
        => InstallResolutionBase(
            fixture,
            baseId,
            baseManifestHash,
            baseVersionId,
            baseTargetSymbolId,
            deltaTargetSymbolId,
            sequence,
            deltaGeneration,
            includeGraphRows: false);

    private static void InstallResolutionBase(
        StoreFixture fixture,
        string baseId,
        string baseManifestHash,
        long baseVersionId,
        string baseTargetSymbolId,
        string? deltaTargetSymbolId,
        long sequence,
        long deltaGeneration,
        bool includeGraphRows)
    {
        string basePath = ResolutionBasePath(fixture, baseId);
        using (var baseConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = basePath,
            Pooling = false,
        }.ToString()))
        {
            baseConnection.Open();
            using SqliteCommand command = baseConnection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE base_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
                INSERT INTO base_meta VALUES
                  ('completed','1'),
                  ('manifest_hash',$manifest),
                  ('resolver_output_epoch','6');
                CREATE TABLE resolution_base_versions (version_id INTEGER PRIMARY KEY) STRICT;
                INSERT INTO resolution_base_versions VALUES ($version);
                CREATE TABLE identifier_resolutions (
                  version_id INTEGER NOT NULL,
                  identifier_id TEXT NOT NULL,
                  target_version_id INTEGER,
                  target_symbol_id TEXT,
                  tier INTEGER,
                  confidence REAL,
                  method TEXT,
                  outcome TEXT NOT NULL,
                  candidates INTEGER,
                  PRIMARY KEY(version_id,identifier_id)) STRICT;
                CREATE TABLE pending_resolutions (
                  version_id INTEGER NOT NULL,
                  pending_relationship_id TEXT NOT NULL,
                  target_version_id INTEGER NOT NULL,
                  target_symbol_id TEXT NOT NULL,
                  tier INTEGER NOT NULL,
                  confidence REAL NOT NULL,
                  method TEXT NOT NULL,
                  PRIMARY KEY(version_id,pending_relationship_id)) STRICT;
                CREATE INDEX idx_read_resolution_identifiers_target ON identifier_resolutions(
                  target_version_id,target_symbol_id,version_id,identifier_id);
                CREATE INDEX idx_read_resolution_pending_target ON pending_resolutions(
                  target_version_id,target_symbol_id,version_id,pending_relationship_id);
                INSERT INTO identifier_resolutions
                  VALUES ($version,'identifier',$version,$baseTarget,1,1.0,'exact','resolved',1);
                INSERT INTO pending_resolutions
                  SELECT $version,'pending',$version,$baseTarget,1,1.0,'exact' WHERE $graph=1;
                """;
            command.Parameters.AddWithValue("$manifest", baseManifestHash);
            command.Parameters.AddWithValue("$version", baseVersionId);
            command.Parameters.AddWithValue("$baseTarget", baseTargetSymbolId);
            command.Parameters.AddWithValue("$graph", includeGraphRows ? 1 : 0);
            command.ExecuteNonQuery();
        }
        long bytes = new FileInfo(basePath).Length;
        string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(basePath))).ToLowerInvariant();
        string storePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand store = connection.CreateCommand();
        store.CommandText =
            """
            CREATE TABLE IF NOT EXISTS resolution_bases (
              base_id TEXT PRIMARY KEY,
              manifest_hash TEXT NOT NULL,
              resolver_output_epoch INTEGER NOT NULL,
              state TEXT NOT NULL,
              relative_path TEXT NOT NULL,
              identifier_count INTEGER NOT NULL,
              pending_count INTEGER NOT NULL,
              file_bytes INTEGER,
              file_sha256 TEXT,
              request_id TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL) STRICT;
            CREATE UNIQUE INDEX IF NOT EXISTS uidx_read_resolution_bases_identity
              ON resolution_bases(manifest_hash,resolver_output_epoch);
            CREATE TABLE IF NOT EXISTS resolution_base_versions (
              base_id TEXT NOT NULL,
              version_id INTEGER NOT NULL,
              PRIMARY KEY(base_id,version_id),
              FOREIGN KEY(base_id) REFERENCES resolution_bases(base_id) ON DELETE CASCADE,
              FOREIGN KEY(version_id) REFERENCES file_versions(version_id) ON DELETE RESTRICT) STRICT;
            CREATE TABLE IF NOT EXISTS resolution_deltas (
              view_id TEXT NOT NULL,
              delta_generation INTEGER NOT NULL,
              base_id TEXT NOT NULL,
              manifest_generation INTEGER NOT NULL,
              manifest_hash TEXT NOT NULL,
              resolver_output_epoch INTEGER NOT NULL,
              identifier_replacements INTEGER NOT NULL,
              pending_replacements INTEGER NOT NULL,
              pending_tombstones INTEGER NOT NULL,
              exact_gap_rows INTEGER NOT NULL,
              exact_gap_files INTEGER NOT NULL,
              exact_gap_json TEXT NOT NULL,
              request_id TEXT NOT NULL,
              created_at TEXT NOT NULL,
              PRIMARY KEY(view_id,delta_generation)) STRICT;
            CREATE TABLE IF NOT EXISTS resolution_identifier_deltas (
              view_id TEXT NOT NULL,
              delta_generation INTEGER NOT NULL,
              version_id INTEGER NOT NULL,
              identifier_id TEXT NOT NULL,
              target_version_id INTEGER,
              target_symbol_id TEXT,
              tier INTEGER,
              confidence REAL,
              method TEXT,
              outcome TEXT NOT NULL,
              candidates INTEGER,
              PRIMARY KEY(view_id,delta_generation,version_id,identifier_id)) STRICT;
            CREATE TABLE IF NOT EXISTS resolution_pending_deltas (
              view_id TEXT NOT NULL,
              delta_generation INTEGER NOT NULL,
              version_id INTEGER NOT NULL,
              pending_relationship_id TEXT NOT NULL,
              operation TEXT NOT NULL,
              target_version_id INTEGER,
              target_symbol_id TEXT,
              tier INTEGER,
              confidence REAL,
              method TEXT,
              PRIMARY KEY(view_id,delta_generation,version_id,pending_relationship_id)) STRICT;
            INSERT INTO resolution_bases VALUES
              ($base,$baseManifest,6,'ready',$relative,1,$pendingCount,$bytes,$sha,$request,$now,$now);
            INSERT INTO resolution_base_versions VALUES ($base,$baseVersion);
            INSERT INTO resolution_deltas VALUES
              ('view-a',$delta,$base,2,'manifest-current',6,$identifierReplacements,0,0,0,0,
               '{"files":[],"rows":[]}',$request,$now);
            INSERT INTO resolution_identifier_deltas
              SELECT 'view-a',$delta,2,'identifier',2,$deltaTarget,1,1.0,'exact','resolved',1
              WHERE $deltaTarget IS NOT NULL;
            UPDATE views SET resolution_state='exact',resolution_base_id=$base,
              resolution_delta_generation=$delta,resolution_exact_at=2,updated_at=$now
              WHERE view_id='view-a';
            INSERT INTO store_log VALUES
              ($sequence,$request,'resolution_exact_rebased','view-a',2,NULL,NULL,0,
               '{}',$now);
            """;
        store.Parameters.AddWithValue("$base", baseId);
        store.Parameters.AddWithValue("$baseManifest", baseManifestHash);
        store.Parameters.AddWithValue("$baseVersion", baseVersionId);
        store.Parameters.AddWithValue("$relative", $"bases/{baseId}.db");
        store.Parameters.AddWithValue("$bytes", bytes);
        store.Parameters.AddWithValue("$sha", sha256);
        store.Parameters.AddWithValue("$request", $"request-{baseId}");
        store.Parameters.AddWithValue("$now", $"2026-08-09T00:00:0{sequence}Z");
        store.Parameters.AddWithValue("$delta", deltaGeneration);
        store.Parameters.AddWithValue("$identifierReplacements", deltaTargetSymbolId is null ? 0 : 1);
        store.Parameters.AddWithValue("$pendingCount", includeGraphRows ? 1 : 0);
        store.Parameters.AddWithValue("$deltaTarget", (object?)deltaTargetSymbolId ?? DBNull.Value);
        store.Parameters.AddWithValue("$sequence", sequence);
        store.ExecuteNonQuery();
    }

    private static void InstallGraphRows(
        StoreFixture fixture,
        string targetId,
        string identifierCallerId,
        string pendingCallerId)
    {
        string storePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE identifiers (
              version_id INTEGER NOT NULL,identifier_id TEXT NOT NULL,reference_site_id TEXT,path TEXT NOT NULL,
              language TEXT NOT NULL,name TEXT NOT NULL,kind TEXT NOT NULL,containing_symbol_id TEXT,
              start_line INTEGER NOT NULL,start_column INTEGER NOT NULL,end_line INTEGER NOT NULL,end_column INTEGER NOT NULL,
              start_byte INTEGER NOT NULL,end_byte INTEGER NOT NULL,confidence REAL,code_context TEXT,metadata_json TEXT,
              PRIMARY KEY(version_id,identifier_id)) STRICT;
            CREATE TABLE relationships (
              version_id INTEGER NOT NULL,relationship_id TEXT NOT NULL,reference_site_id TEXT,from_symbol_id TEXT NOT NULL,
              to_symbol_id TEXT NOT NULL,path TEXT NOT NULL,kind TEXT NOT NULL,start_line INTEGER,start_column INTEGER,
              end_line INTEGER,end_column INTEGER,start_byte INTEGER,end_byte INTEGER,confidence REAL,metadata_json TEXT,
              PRIMARY KEY(version_id,relationship_id)) STRICT;
            CREATE TABLE pending_relationships (
              version_id INTEGER NOT NULL,pending_relationship_id TEXT NOT NULL,reference_site_id TEXT,
              from_symbol_id TEXT NOT NULL,caller_scope_symbol_id TEXT,path TEXT NOT NULL,kind TEXT NOT NULL,
              target_display_name TEXT NOT NULL,target_terminal_name TEXT NOT NULL,target_receiver TEXT,
              target_namespace_json TEXT,target_import_context TEXT,start_line INTEGER,start_column INTEGER,
              end_line INTEGER,end_column INTEGER,start_byte INTEGER,end_byte INTEGER,confidence REAL,metadata_json TEXT,
              PRIMARY KEY(version_id,pending_relationship_id)) STRICT;
            INSERT INTO symbols VALUES
              (2,$target,'same.cs','csharp','Target','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
              (2,$identifierCaller,'same.cs','csharp','IdentifierCaller','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
              (2,$pendingCaller,'same.cs','csharp','PendingCaller','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL);
            INSERT INTO identifiers VALUES
              (2,'identifier',NULL,'same.cs','csharp','Target','call',$identifierCaller,1,1,1,2,0,1,1.0,NULL,NULL);
            INSERT INTO pending_relationships VALUES
              (2,'pending',NULL,$pendingCaller,$pendingCaller,'same.cs','calls','Target','Target',NULL,'[]',NULL,
               1,1,1,2,0,1,1.0,NULL);
            """;
        command.Parameters.AddWithValue("$target", targetId);
        command.Parameters.AddWithValue("$identifierCaller", identifierCallerId);
        command.Parameters.AddWithValue("$pendingCaller", pendingCallerId);
        command.ExecuteNonQuery();
    }

    private static void InstallGraphOverlayRows(StoreFixture fixture, string deltaTargetId)
    {
        string storePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO symbols VALUES
              (2,$target,'same.cs','csharp','DeltaTarget','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL);
            INSERT INTO resolution_pending_deltas VALUES
              ('view-a',1,2,'pending','tombstone',NULL,NULL,NULL,NULL,NULL);
            """;
        command.Parameters.AddWithValue("$target", deltaTargetId);
        command.ExecuteNonQuery();
    }

    private static void InstallCombinedFrontierRows(
        StoreFixture fixture,
        string targetId,
        string relationshipTargetId,
        string relationshipCallerId,
        string nameTargetId,
        string nameCallerId)
    {
        string storePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO symbols VALUES
              (2,$relationshipTarget,'same.cs','csharp','RelationshipTarget','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
              (2,$relationshipCaller,'same.cs','csharp','RelationshipCaller','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
              (2,$nameTarget,'same.cs','csharp','UniqueNameTarget','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
              (2,$nameCaller,'same.cs','csharp','NameCaller','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL);
            INSERT INTO relationships
              (version_id,relationship_id,from_symbol_id,to_symbol_id,path,kind,confidence)
              VALUES
              (2,'relationship-forward',$target,$relationshipTarget,'same.cs','calls',1.0),
              (2,'relationship-reverse',$relationshipCaller,$target,'same.cs','calls',1.0);
            INSERT INTO identifiers
              (version_id,identifier_id,path,language,name,kind,containing_symbol_id,
               start_line,start_column,end_line,end_column,start_byte,end_byte,confidence)
              VALUES
              (2,'name-forward','same.cs','csharp','UniqueNameTarget','call',$target,1,1,1,2,0,1,1.0),
              (2,'name-reverse','same.cs','csharp','Target','call',$nameCaller,1,1,1,2,0,1,1.0);
            """;
        command.Parameters.AddWithValue("$target", targetId);
        command.Parameters.AddWithValue("$relationshipTarget", relationshipTargetId);
        command.Parameters.AddWithValue("$relationshipCaller", relationshipCallerId);
        command.Parameters.AddWithValue("$nameTarget", nameTargetId);
        command.Parameters.AddWithValue("$nameCaller", nameCallerId);
        command.ExecuteNonQuery();
    }

    private static void InstallUnresolvedNameGraphRows(
        StoreFixture fixture,
        string forwardSourceId,
        string uniqueTargetId,
        string deltaUnresolvedTargetId,
        string reverseTargetId,
        string reverseCallerId)
    {
        string storePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storePath,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE INDEX idx_read_identifiers_containing ON identifiers(containing_symbol_id,version_id);
                CREATE INDEX idx_read_identifiers_name_kind ON identifiers(name,kind,version_id);
                INSERT INTO symbols VALUES
                  (2,$forwardSource,'same.cs','csharp','ForwardSource','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
                  (2,$uniqueTarget,'same.cs','csharp','UniqueTarget','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
                  (2,$deltaUnresolvedTarget,'same.cs','csharp','DeltaUnresolvedTarget','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
                  (2,$reverseTarget,'same.cs','csharp','ReverseTarget','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
                  (2,$reverseCaller,'same.cs','csharp','ReverseCaller','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
                  (2,'64000000000000000000000000000006','same.cs','csharp','BaseResolvedTarget','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
                  (2,'64000000000000000000000000000007','same.cs','csharp','DeltaResolvedTarget','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
                  (2,'64000000000000000000000000000008','same.cs','csharp','Homonym','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
                  (2,'64000000000000000000000000000009','same.cs','csharp','Homonym','method',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL);
                INSERT INTO identifiers
                  (version_id,identifier_id,path,language,name,kind,containing_symbol_id,
                   start_line,start_column,end_line,end_column,start_byte,end_byte,confidence)
                  VALUES
                  (2,'name-forward','same.cs','csharp','UniqueTarget','call',$forwardSource,1,1,1,2,0,1,1.0),
                  (2,'name-reverse','same.cs','csharp','ReverseTarget','call',$reverseCaller,1,1,1,2,0,1,1.0),
                  (2,'base-resolved','same.cs','csharp','BaseResolvedTarget','call',$forwardSource,1,1,1,2,0,1,1.0),
                  (2,'delta-resolved','same.cs','csharp','DeltaResolvedTarget','call',$forwardSource,1,1,1,2,0,1,1.0),
                  (2,'delta-unresolved','same.cs','csharp','DeltaUnresolvedTarget','call',$forwardSource,1,1,1,2,0,1,1.0),
                  (2,'homonym','same.cs','csharp','Homonym','call',$forwardSource,1,1,1,2,0,1,1.0);
                INSERT INTO resolution_identifier_deltas VALUES
                  ('view-a',1,2,'delta-resolved',2,'64000000000000000000000000000007',1,1.0,'exact','resolved',1),
                  ('view-a',1,2,'delta-unresolved',NULL,NULL,NULL,NULL,NULL,'unresolved',0);
                """;
            command.Parameters.AddWithValue("$forwardSource", forwardSourceId);
            command.Parameters.AddWithValue("$uniqueTarget", uniqueTargetId);
            command.Parameters.AddWithValue("$deltaUnresolvedTarget", deltaUnresolvedTargetId);
            command.Parameters.AddWithValue("$reverseTarget", reverseTargetId);
            command.Parameters.AddWithValue("$reverseCaller", reverseCallerId);
            command.ExecuteNonQuery();
        }

        using var baseConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ResolutionBasePath(fixture, "base-unresolved-name"),
            Pooling = false,
        }.ToString());
        baseConnection.Open();
        using SqliteCommand resolution = baseConnection.CreateCommand();
        resolution.CommandText =
            """
            INSERT INTO identifier_resolutions VALUES
              (2,'base-resolved',2,'64000000000000000000000000000006',1,1.0,'exact','resolved',1),
              (2,'delta-resolved',NULL,NULL,NULL,NULL,NULL,'unresolved',0),
              (2,'delta-unresolved',2,$deltaUnresolvedTarget,1,1.0,'exact','resolved',1);
            """;
        resolution.Parameters.AddWithValue("$deltaUnresolvedTarget", deltaUnresolvedTargetId);
        resolution.ExecuteNonQuery();
        baseConnection.Close();

        string basePath = ResolutionBasePath(fixture, "base-unresolved-name");
        long bytes = new FileInfo(basePath).Length;
        string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(basePath))).ToLowerInvariant();
        using var storeConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storePath,
            Pooling = false,
        }.ToString());
        storeConnection.Open();
        using SqliteCommand metadata = storeConnection.CreateCommand();
        metadata.CommandText =
            "UPDATE resolution_bases SET identifier_count=4,file_bytes=$bytes,file_sha256=$sha WHERE base_id='base-unresolved-name';";
        metadata.Parameters.AddWithValue("$bytes", bytes);
        metadata.Parameters.AddWithValue("$sha", sha256);
        metadata.ExecuteNonQuery();
    }

    private static string ResolutionBasePath(StoreFixture fixture, string baseId) =>
        Path.Combine(fixture.Binding.StoreRoot, "gen-001", "bases", $"{baseId}.db");

    private static (string ManifestHash, long ResolverOutputEpoch, long VersionId) ReadResolutionBaseIdentity(
        StoreFixture fixture,
        string baseId)
    {
        string storePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT base.manifest_hash,base.resolver_output_epoch,root.version_id
            FROM resolution_bases AS base
            JOIN resolution_base_versions AS root ON root.base_id=base.base_id
            WHERE base.base_id=$base;
            """;
        command.Parameters.AddWithValue("$base", baseId);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static void AppendStoreLog(StoreFixture fixture, long sequence, string? viewId, long? versionId)
    {
        string databasePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO store_log VALUES ($sequence,$request,'version_level_completed',$view,NULL,$version,2,0,'{}','2026-08-09T00:00:03Z')";
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$request", $"request-{sequence}");
        command.Parameters.AddWithValue("$view", (object?)viewId ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", (object?)versionId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void AppendReusedManifestImport(StoreFixture fixture)
    {
        string databasePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO store_log VALUES
              (3,'request-reuse','store_import_l3_chunk','view-a',2,2,3,0,'{}','2026-08-09T00:00:03Z'),
              (4,'request-reuse','store_import_completed','view-a',2,NULL,3,1,
               '{"manifest":{"disposition":"reused"}}','2026-08-09T00:00:04Z'),
              (5,'request-reuse','store_resolve_completed','view-a',2,NULL,3,1,
               '{}','2026-08-09T00:00:05Z');
            UPDATE views
            SET resolution_state='exact',
                resolution_base_id='base-1',
                resolution_delta_generation=2,
                resolution_exact_at=5,
                updated_at='2026-08-09T00:00:05Z'
            WHERE view_id='view-a';
            """;
        command.ExecuteNonQuery();
    }

    private static void AddSentinel(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE fast_forward_sentinel(value INTEGER NOT NULL); INSERT INTO fast_forward_sentinel VALUES (1);";
        command.ExecuteNonQuery();
    }

    private static void RejectStampUpdates(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_store_stamp
            BEFORE UPDATE OF store_log_sequence ON store_sidecar_stamp
            BEGIN
                SELECT RAISE(ABORT, 'reject stamp update');
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static long ReadInt64(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TableExists(string databasePath, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static void AssertFastForwarded(
        string databasePath,
        StoreSidecarKind kind,
        FamilyStoreReadSession session,
        string revisionSql)
    {
        StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(kind, session.Snapshot);
        Assert.Equal(expected, StoreSidecarCatalog.TryRead(databasePath));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT ({revisionSql.TrimEnd(';')}), (SELECT value FROM fast_forward_sentinel);";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expected.StoreLogSequence, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
    }

    private static void DeleteManifest(StoreFixture fixture, long generation)
    {
        string databasePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM manifests WHERE view_id='view-a' AND generation=$generation;";
        command.Parameters.AddWithValue("$generation", generation);
        command.ExecuteNonQuery();
    }

    private static void DeleteStoreMetadata(StoreFixture fixture, string key)
    {
        string databasePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM store_meta WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static void UpdateStoreMetadata(StoreFixture fixture, string key, string value)
    {
        string databasePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE store_meta SET value=$value WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private sealed class StoreFixture : IDisposable
    {
        private StoreFixture(string root, StoreFamilyBinding binding)
        {
            Root = root;
            Binding = binding;
        }

        public string Root { get; }

        public StoreFamilyBinding Binding { get; }

        public static StoreFixture Create()
        {
            string root = Path.Combine(Path.GetTempPath(), "miller-family-read-" + Guid.NewGuid().ToString("N"));
            string workspace = Path.Combine(root, "workspace");
            string store = Path.Combine(root, "store");
            string generation = Path.Combine(store, "gen-001");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(generation, "bases"));
            Directory.CreateDirectory(Path.Combine(store, "spool"));
            Directory.CreateDirectory(Path.Combine(store, "scratch"));
            File.WriteAllText(Path.Combine(store, "CURRENT"), "gen-001\n");
            CreateCoordinator(Path.Combine(store, "coord.db"));
            workspace = PathCanonicalizer.CanonicalizeRoot(workspace);
            store = PathCanonicalizer.CanonicalizeRoot(store);
            CreateStore(Path.Combine(generation, "store.db"), workspace);
            var binding = new StoreFamilyBinding(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                store,
                "view-a",
                workspace,
                StoreBindingState.Ready);
            return new StoreFixture(root, binding);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private static void CreateCoordinator(string path)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE consumer_cursors (consumer_id TEXT PRIMARY KEY, generation_name TEXT NOT NULL, store_log_sequence INTEGER NOT NULL, updated_at INTEGER NOT NULL) STRICT;";
            command.ExecuteNonQuery();
        }

        private static void CreateStore(string path, string workspace)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE store_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
                INSERT INTO store_meta VALUES
                  ('family_id','11111111-1111-4111-8111-111111111111'),
                  ('store_sqlite_schema_version','2'),
                  ('store_format_epoch','1'),
                  ('min_reader_version','2.31.0'),
                  ('binary_version','2.31.0'),
                  ('extraction_identity_epoch','1'),
                  ('generation_state','serving');
                CREATE TABLE views (
                  view_id TEXT PRIMARY KEY,
                  root TEXT NOT NULL,
                  current_generation INTEGER,
                  resolution_state TEXT NOT NULL,
                  resolution_base_id TEXT,
                  resolution_delta_generation INTEGER,
                  resolution_exact_at INTEGER,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL) STRICT;
                CREATE TABLE manifests (
                  view_id TEXT NOT NULL,
                  generation INTEGER NOT NULL,
                  manifest_hash TEXT NOT NULL,
                  request_id TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  PRIMARY KEY(view_id,generation)) STRICT;
                CREATE TABLE file_versions (
                  version_id INTEGER PRIMARY KEY,
                  path TEXT NOT NULL,
                  content_hash TEXT NOT NULL,
                  extraction_epoch INTEGER NOT NULL,
                  language TEXT NOT NULL,
                  content_bytes INTEGER NOT NULL,
                  line_count INTEGER,
                  metadata_json TEXT,
                  complete_l1 INTEGER,
                  complete_l2 INTEGER,
                  complete_l3 INTEGER) STRICT;
                CREATE TABLE manifest_entries (
                  view_id TEXT NOT NULL,
                  generation INTEGER NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  version_id INTEGER,
                  status TEXT NOT NULL,
                  observed_content_hash TEXT,
                  indexed_at TEXT NOT NULL,
                  error_class TEXT,
                  error_json TEXT,
                  PRIMARY KEY(view_id,generation,path)) STRICT;
                CREATE TABLE symbols (
                  version_id INTEGER NOT NULL,
                  symbol_id TEXT NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  name TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  signature TEXT,
                  doc_comment TEXT,
                  visibility TEXT,
                  parent_symbol_id TEXT,
                  start_line INTEGER NOT NULL,
                  start_column INTEGER NOT NULL,
                  end_line INTEGER NOT NULL,
                  end_column INTEGER NOT NULL,
                  start_byte INTEGER NOT NULL,
                  end_byte INTEGER NOT NULL,
                  body_start_line INTEGER,
                  body_start_column INTEGER,
                  body_end_line INTEGER,
                  body_end_column INTEGER,
                  body_start_byte INTEGER,
                  body_end_byte INTEGER,
                  body_hash TEXT,
                  semantic_group TEXT,
                  confidence REAL,
                  content_type TEXT,
                  is_test INTEGER NOT NULL,
                  test_container INTEGER NOT NULL,
                  test_lifecycle INTEGER NOT NULL,
                  metadata_json TEXT,
                  PRIMARY KEY(version_id,symbol_id)) STRICT;
                CREATE TABLE store_log (
                  sequence INTEGER PRIMARY KEY,
                  request_id TEXT NOT NULL,
                  event_kind TEXT NOT NULL,
                  view_id TEXT,
                  generation INTEGER,
                  version_id INTEGER,
                  level INTEGER,
                  terminal INTEGER NOT NULL,
                  payload_json TEXT NOT NULL,
                  created_at TEXT NOT NULL) STRICT;
                CREATE TABLE structural_facts (
                  structural_fact_id INTEGER PRIMARY KEY,
                  version_id INTEGER NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  pattern_id TEXT NOT NULL,
                  capture_name TEXT NOT NULL,
                  node_kind TEXT NOT NULL,
                  containing_symbol_id TEXT,
                  start_line INTEGER NOT NULL,
                  start_column INTEGER NOT NULL,
                  end_line INTEGER NOT NULL,
                  end_column INTEGER NOT NULL,
                  start_byte INTEGER NOT NULL,
                  end_byte INTEGER NOT NULL,
                  confidence REAL,
                  metadata_json TEXT) STRICT;
                CREATE TABLE parse_diagnostics (
                  diagnostic_id TEXT PRIMARY KEY,
                  version_id INTEGER NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  message TEXT,
                  start_line INTEGER NOT NULL,
                  start_column INTEGER NOT NULL,
                  end_line INTEGER NOT NULL,
                  end_column INTEGER NOT NULL,
                  start_byte INTEGER NOT NULL,
                  end_byte INTEGER NOT NULL,
                  metadata_json TEXT) STRICT;
                """;
            command.ExecuteNonQuery();
            command.CommandText =
                """
                INSERT INTO views VALUES ('view-a',$root,2,'unbound',NULL,NULL,NULL,'2026-08-09T00:00:00Z','2026-08-09T00:00:00Z');
                INSERT INTO manifests VALUES
                  ('view-a',1,'manifest-prior','request-prior','2026-08-08T00:00:00Z'),
                  ('view-a',2,'manifest-current','request-a','2026-08-09T00:00:00Z');
                INSERT INTO file_versions VALUES
                  (1,'same.cs','blake3:hidden',1,'csharp',10,1,NULL,1,2,3),
                  (2,'same.cs','blake3:visible',1,'csharp',11,1,NULL,1,2,3);
                INSERT INTO manifest_entries VALUES
                  ('view-a',1,'same.cs','csharp',1,'indexed','blake3:hidden','2026-08-08T00:00:00Z',NULL,NULL),
                  ('view-a',2,'same.cs','csharp',2,'indexed','blake3:visible','2026-08-09T00:00:00Z',NULL,NULL);
                INSERT INTO symbols VALUES
                  (1,'symbol','same.cs','csharp','Hidden','class',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
                  (2,'symbol','same.cs','csharp','Visible','class',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL);
                INSERT INTO structural_facts VALUES
                  (1,1,'same.cs','csharp','hidden.pattern.v1','node','class',NULL,1,1,1,2,0,1,1.0,NULL),
                  (2,2,'same.cs','csharp','visible.pattern.v1','node','class',NULL,1,1,1,2,0,1,1.0,NULL);
                INSERT INTO store_log VALUES
                  (1,'request-prior','manifest_flipped','view-a',1,NULL,NULL,0,'{}','2026-08-08T00:00:00Z'),
                  (2,'request-a','manifest_flipped','view-a',2,NULL,NULL,1,'{}','2026-08-09T00:00:01Z');
                """;
            command.Parameters.AddWithValue("$root", workspace);
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public void StoreReadSessionExposesProducerVersionForLeadershipChecks()
    {
        using StoreFixture fixture = StoreFixture.Create();

        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);

        Assert.Equal("2.31.0", session.Visibility.BinaryVersion);
    }

    [Fact]
    public void StoreArtifactVersionReaderUsesTheServingStoreVersion()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);

        string legacyPath = Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "symbols.db");

        Assert.Equal("2.31.0", StoreArtifactVersionReader.TryRead(legacyPath));
    }

    [Fact]
    public void StoreArtifactVersionReaderDoesNotUseLegacyVersionWhenTheServingStoreCannotOpen()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(
            fixture.Binding.WorkspaceRoot,
            fixture.Binding with { StoreRoot = Path.Combine(fixture.Binding.StoreRoot, "missing") });

        string legacyPath = Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "symbols.db");

        Assert.Null(StoreArtifactVersionReader.TryReadOrFallback(legacyPath, _ => "legacy-2.0.0"));
    }

    [Fact]
    public void StoreArtifactVersionReaderUsesLegacyVersionToRecoverAMissingStoreRoot()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(
            fixture.Binding.WorkspaceRoot,
            fixture.Binding with { StoreRoot = Path.Combine(fixture.Binding.StoreRoot, "missing") });

        string legacyPath = Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "symbols.db");

        Assert.Equal(
            "legacy-2.0.0",
            StoreArtifactVersionReader.ReadForLeadership(legacyPath, _ => "legacy-2.0.0"));
        Assert.True(StoreArtifactVersionReader.RequiresRootRebind(legacyPath));
    }

    [Fact]
    public void StoreArtifactVersionReaderRejectsAnExistingStoreRootBehindAnInaccessibleDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        using StoreFixture fixture = StoreFixture.Create();
        string inaccessibleParent = Path.Combine(fixture.Binding.StoreRoot, "inaccessible");
        string existingStoreRoot = Path.Combine(inaccessibleParent, "store");
        Directory.CreateDirectory(existingStoreRoot);
        StoreWorkspacePointer.Write(
            fixture.Binding.WorkspaceRoot,
            fixture.Binding with { StoreRoot = existingStoreRoot });
        string legacyPath = Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "symbols.db");
        UnixFileMode originalMode = File.GetUnixFileMode(inaccessibleParent);
        File.SetUnixFileMode(inaccessibleParent, UnixFileMode.None);
        try
        {
            Assert.Throws<StoreArtifactVersionReadException>(() =>
                StoreArtifactVersionReader.ReadForLeadership(legacyPath, _ => "legacy-2.0.0"));
            Assert.False(StoreArtifactVersionReader.RequiresRootRebind(legacyPath));
        }
        finally
        {
            File.SetUnixFileMode(inaccessibleParent, originalMode);
        }
    }

    [Fact]
    public void StoreArtifactVersionReaderDoesNotFallBackWhenThePointerCannotBeRead()
    {
        if (OperatingSystem.IsWindows())
            return;

        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);
        string pointerPath = Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "store.json");
        UnixFileMode originalMode = File.GetUnixFileMode(pointerPath);
        File.SetUnixFileMode(pointerPath, UnixFileMode.None);
        try
        {
            string legacyPath = Path.Combine(fixture.Binding.WorkspaceRoot, ".miller", "symbols.db");

            StoreArtifactVersionReadException error = Assert.Throws<StoreArtifactVersionReadException>(() =>
                StoreArtifactVersionReader.ReadForLeadership(legacyPath, _ => "legacy-2.0.0"));

            Assert.Contains("refusing to claim leadership", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(pointerPath, originalMode);
        }
    }
}
