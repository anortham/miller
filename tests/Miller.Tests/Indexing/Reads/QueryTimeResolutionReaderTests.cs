using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using Miller.Tests.Indexing.Resolution;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing.Reads;

public sealed class QueryTimeResolutionReaderTests
{
    private const string App = "cls-app";
    private const string Run = "fn-run";
    private const string Helper = "fn-help";
    private const string Count = "var-count";
    private const string DupA = "cls-dup-a";
    private const string DupB = "cls-dup-b";

    [Fact]
    public void FamilyStoreGraphEdgesMatchRetiredSqlLiterals()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        string[] actual = SerializeGraph(reader, connection, [Run, Helper, Count, DupA]);

        Assert.Equal(RetiredGraphTuples, actual);
    }

    [Fact]
    public void ArtifactGraphEdgesMatchRetiredSqlLiterals()
    {
        using ResolutionArtifactFixture fixture = PopulateArtifact();
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = ArtifactReader(connection);

        string[] actual = SerializeGraph(reader, connection, [Run, Helper, Count, DupA]);

        Assert.Equal(RetiredGraphTuples, actual);
    }

    [Fact]
    public void FamilyStoreEvidenceMatchesRetiredSqlLiterals()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        string[] actual = SerializeEvidence(reader, connection, [Run, Helper, DupA]);

        Assert.Equal(RetiredEvidenceTuples, actual);
    }

    [Fact]
    public void ArtifactEvidenceMatchesRetiredSqlLiterals()
    {
        using ResolutionArtifactFixture fixture = PopulateArtifact();
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = ArtifactReader(connection);

        string[] actual = SerializeEvidence(reader, connection, [Run, Helper, DupA]);

        Assert.Equal(RetiredEvidenceTuples, actual);
    }

    [Fact]
    public void FamilyStoreExportRowsMatchRetiredSqlLiterals()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        string[] actual = SerializeExport(reader, connection);

        Assert.Equal(RetiredExportTuples, actual);
    }

    [Fact]
    public void ArtifactExportRowsMatchRetiredSqlLiterals()
    {
        using ResolutionArtifactFixture fixture = PopulateArtifact();
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = ArtifactReader(connection);

        string[] actual = SerializeExport(reader, connection);

        Assert.Equal(RetiredExportTuples, actual);
    }

    [Fact]
    public void PendingOverrideSuppressesIdentifierTarget()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/App.cs");
        fixture.AddSymbol(1, Run, "Run", "method", "src/App.cs");
        fixture.AddSymbol(1, Helper, "Helper", "function", "src/App.cs");
        fixture.AddIdentifier(1, "id-located", "Helper", "src/App.cs", kind: "call", containingSymbolId: Run, startByte: 20, endByte: 26);
        fixture.AddPending(1, "pend-help", Run, "Helper", "src/App.cs", startByte: 20, endByte: 26);
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        IReadOnlyList<FamilyGraphResolutionEdge> edges = reader.ReadResolutionEdges(
            connection, [Run], Direction.Forward, statementObserver: null);
        Assert.DoesNotContain(edges, edge => edge.Source == "identifier_target");
        FamilyGraphResolutionEdge pending = Assert.Single(edges);
        Assert.Equal("pending_resolution", pending.Source);
        Assert.Equal(Helper, pending.ToId);
    }

    [Fact]
    public void FamilyStoreIdentifierReceiverTypeBindsEnclosingTypeMember()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/App.cs");
        fixture.AddSymbol(1, App, "App", "class", "src/App.cs");
        fixture.AddSymbol(1, Run, "Run", "method", "src/App.cs", parentId: App);
        fixture.AddSymbol(1, Helper, "Helper", "method", "src/App.cs", parentId: App);
        fixture.AddSymbol(1, "fn-other-helper", "Helper", "function", "src/App.cs");
        fixture.AddIdentifier(
            1, "id-this-help", "Helper", "src/App.cs", kind: "call", containingSymbolId: Run, startByte: 20, endByte: 26,
            metadataJson: """{"receiver":"this","receiver_type":"App"}""");
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        IReadOnlyList<FamilyGraphResolutionEdge> edges = reader.ReadResolutionEdges(
            connection, [Run], Direction.Forward, statementObserver: null);

        FamilyGraphResolutionEdge edge = Assert.Single(edges);
        Assert.Equal("identifier_target", edge.Source);
        Assert.Equal(Helper, edge.ToId);
        Assert.Equal(0.75, edge.Confidence);
    }

    [Fact]
    public void FamilyStorePendingReceiverTypeBindsEnclosingTypeMember()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/App.cs");
        fixture.AddSymbol(1, App, "App", "class", "src/App.cs");
        fixture.AddSymbol(1, Run, "Run", "method", "src/App.cs", parentId: App);
        fixture.AddSymbol(1, Helper, "Helper", "method", "src/App.cs", parentId: App);
        fixture.AddSymbol(1, "fn-other-helper", "Helper", "function", "src/App.cs");
        fixture.AddPending(
            1, "pend-this-help", Run, "Helper", "src/App.cs", startByte: 20, endByte: 26,
            receiver: "this", metadataJson: """{"receiver_type":"App"}""");
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        IReadOnlyList<FamilyGraphResolutionEdge> edges = reader.ReadResolutionEdges(
            connection, [Run], Direction.Forward, statementObserver: null);

        FamilyGraphResolutionEdge edge = Assert.Single(edges);
        Assert.Equal("pending_resolution", edge.Source);
        Assert.Equal(Helper, edge.ToId);
        Assert.Equal(0.75, edge.Confidence);
    }

    [Fact]
    public void ArtifactPendingReceiverTypeBindsEnclosingTypeMember()
    {
        const string AppFile = "file-9e7a11";
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile(AppFile, "src/App.cs");
        fixture.AddSymbol(AppFile, App, "App", "class", "src/App.cs");
        fixture.AddSymbol(AppFile, Run, "Run", "method", "src/App.cs", parentId: App);
        fixture.AddSymbol(AppFile, Helper, "Helper", "method", "src/App.cs", parentId: App);
        fixture.AddSymbol(AppFile, "fn-other-helper", "Helper", "function", "src/App.cs");
        fixture.AddPending(
            AppFile, "pend-this-help", Run, "Helper", "src/App.cs", startByte: 20, endByte: 26,
            receiver: "this", metadataJson: """{"receiver_type":"App"}""");
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = ArtifactReader(connection);

        IReadOnlyList<FamilyGraphResolutionEdge> edges = reader.ReadResolutionEdges(
            connection, [Run], Direction.Forward, statementObserver: null);

        FamilyGraphResolutionEdge edge = Assert.Single(edges);
        Assert.Equal("pending_resolution", edge.Source);
        Assert.Equal(Helper, edge.ToId);
        Assert.Equal(0.75, edge.Confidence);
    }

    [Fact]
    public void QmlPendingInstantiationUsesCatalogAndConsumerPath()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(fixture);
        fixture.AddPending(
            1,
            "pending-remote",
            "source",
            "RemoteCard",
            "source.qml",
            kind: "instantiates",
            language: "qml");
        fixture.ExecuteWrite(
            "UPDATE pending_relationships SET target_display_name='Components.RemoteCard', target_terminal_name='RemoteCard', target_receiver='Components' WHERE pending_relationship_id='pending-remote';");

        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        IReadOnlyList<FamilyGraphResolutionEdge> edges = reader.ReadResolutionEdges(
            connection,
            ["source"],
            Direction.Forward,
            statementObserver: null);

        FamilyGraphResolutionEdge edge = Assert.Single(edges, item => item.Source == "pending_resolution");
        Assert.Equal("remote", edge.ToId);
        Assert.Equal("instantiates", edge.Kind);
    }

    [Fact]
    public void ReleasedV2351QmlPendingInstantiationUsesCatalog()
    {
        string artifactPath = Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "tests",
            "Miller.Tests",
            "Fixtures",
            "QmlFirstClass",
            "symbols.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = artifactPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        string sourceId;
        string remoteId;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value FROM artifact_metadata WHERE key='binary_version';";
            Assert.Equal("2.35.1", (string)command.ExecuteScalar()!);
            command.CommandText = "SELECT symbol_id FROM symbols WHERE path='source.qml' AND name='source';";
            sourceId = (string)command.ExecuteScalar()!;
            command.CommandText = "SELECT symbol_id FROM symbols WHERE path='components/RemoteCard.qml' AND name='RemoteCard';";
            remoteId = (string)command.ExecuteScalar()!;
        }

        QueryTimeResolutionReader reader = ArtifactReader(connection);
        IReadOnlyList<FamilyGraphResolutionEdge> edges = reader.ReadResolutionEdges(
            connection,
            [sourceId],
            Direction.Forward,
            statementObserver: null);

        FamilyGraphResolutionEdge edge = Assert.Single(
            edges,
            item => item.Source == "pending_resolution" && item.ToId == remoteId);
        Assert.Equal(remoteId, edge.ToId);
        Assert.Equal("instantiates", edge.Kind);
    }

    [Fact]
    public void FamilyModeEvidenceWorksWithNoAttachedResolutionBase()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        using var session = new FixtureReadSession(fixture, WorkspaceReadMode.FamilyStore);
        var bounds = new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10);

        ReferenceEvidenceBundle bundle = ReferenceEvidenceReader.ReadForSymbol(
            session,
            Helper,
            new ReferenceEvidenceQuery(bounds),
            new ReferenceEvidenceQuery(bounds),
            bounds,
            [ReferenceKind.Call]);

        Assert.Contains(bundle.Inbound.Exact, row => row.Source == ReferenceEvidenceSource.IdentifierResolution);
        Assert.Contains(bundle.Inbound.Exact, row => row.Source == ReferenceEvidenceSource.PendingResolution);
        Assert.Contains(bundle.Inbound.Exact, row => row.Source == ReferenceEvidenceSource.Relationship);
        long attached = session.Read(static connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pragma_database_list WHERE name='resolution_base';";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });
        Assert.Equal(0, attached);
    }

    [Fact]
    public void ArtifactInboundResolvesIdentifierWithNullContainingSymbol()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("1", "src/App.cs");
        fixture.AddSymbol("1", Helper, "Helper", "function", "src/App.cs");
        fixture.AddIdentifier("1", "id-orphan", "Helper", "src/App.cs", kind: "call", containingSymbolId: null, startByte: 10, endByte: 16);
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = ArtifactReader(connection);

        Dictionary<string, List<Miller.Core.References.ReferenceEvidence>> inbound =
            reader.ReadInboundExact(connection, [Helper]);

        Miller.Core.References.ReferenceEvidence row = Assert.Single(inbound[Helper]);
        Assert.Equal(Miller.Core.References.ReferenceEvidenceSource.IdentifierResolution, row.Source);
        Assert.Equal(Helper, row.TargetSymbolId);
    }

    [Fact]
    public void FamilyStoreSiteFactsComeFromReferenceSites()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        fixture.AddIdentifier(
            1, "id-spanless", "Helper", "src/App.cs", kind: "call", containingSymbolId: Run,
            startByte: 90, endByte: 96, siteProvenance: "spanless", siteExact: false, siteSpanless: true);
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        QueryTimeExportEvidence row = Assert.Single(
            reader.ReadExportEvidence(connection), r => r.ReferenceSiteId == "site-id-spanless");
        Assert.False(row.IsExact);
        Assert.Equal("spanless", row.SiteProvenance);
        Assert.Null(row.StartLine);
        Assert.Null(row.StartColumn);
        Assert.Null(row.EndLine);
        Assert.Null(row.EndColumn);
        Assert.Null(row.StartByte);
        Assert.Null(row.EndByte);

        Dictionary<string, List<ReferenceEvidence>> inbound = reader.ReadInboundExact(connection, [Helper]);
        ReferenceEvidence evidence = Assert.Single(
            inbound[Helper], r => r.ReferenceSiteId == "site-id-spanless");
        Assert.False(evidence.IsExact);
        Assert.Equal("spanless", evidence.SiteProvenance);
        Assert.Null(evidence.StartLine);
        Assert.Null(evidence.StartByte);
    }

    [Fact]
    public void ArtifactSiteFactsComeFromReferenceSites()
    {
        using ResolutionArtifactFixture fixture = PopulateArtifact();
        fixture.AddIdentifier(
            "file-9e7a11", "id-spanless", "Helper", "src/App.cs", kind: "call", containingSymbolId: Run,
            startByte: 90, endByte: 96, siteProvenance: "spanless", siteExact: false, siteSpanless: true);
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = ArtifactReader(connection);

        QueryTimeExportEvidence row = Assert.Single(
            reader.ReadExportEvidence(connection), r => r.ReferenceSiteId == "site-id-spanless");
        Assert.False(row.IsExact);
        Assert.Equal("spanless", row.SiteProvenance);
        Assert.Null(row.StartColumn);
        Assert.Null(row.StartByte);
    }

    [Fact]
    public void ExportPendingSpanComesFromReferenceSites()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        fixture.AddPending(
            1, "pend-cols", Run, "Helper", "src/App.cs", startByte: 100, endByte: 106,
            siteStartColumn: 7, siteEndColumn: 9);
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        QueryTimeExportEvidence row = Assert.Single(
            reader.ReadExportEvidence(connection), r => r.ReferenceSiteId == "site-pend-cols");
        Assert.Equal(7L, row.StartColumn);
        Assert.Equal(9L, row.EndColumn);
        Assert.Equal(100L, row.StartByte);
        Assert.Equal(106L, row.EndByte);
    }

    [Fact]
    public void ExportAndEvidenceDropRowsWithoutReferenceSite()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        fixture.RemoveReferenceSite("id-help");
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        Assert.DoesNotContain(
            reader.ReadExportEvidence(connection), r => r.ReferenceSiteId == "site-id-help");
        Dictionary<string, List<ReferenceEvidence>> inbound = reader.ReadInboundExact(connection, [Helper]);
        Assert.DoesNotContain(inbound[Helper], row => row.ReferenceSiteId == "site-id-help");
        IReadOnlyList<FamilyGraphResolutionEdge> edges = reader.ReadResolutionEdges(
            connection, [Run], Direction.Forward, statementObserver: null);
        Assert.Contains(edges, edge => edge.Source == "identifier_target" && edge.ToId == Helper);
    }

    [Fact]
    public void ExportSkipsRelationshipRowsWithoutTargetSymbol()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        fixture.AddRelationship(1, "rel-ghost", Run, "fn-ghost", "src/App.cs", startByte: 110, endByte: 116);
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        Assert.DoesNotContain(
            reader.ReadExportEvidence(connection), r => r.ReferenceSiteId == "site-rel-ghost");
    }

    [Fact]
    public void AnswersWhenResolutionStateIsNotExact()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        fixture.ExecuteWrite("UPDATE views SET resolution_state='converging', resolution_base_id=NULL, resolution_delta_generation=NULL, resolution_exact_at=NULL;");
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        Assert.NotEmpty(reader.ReadResolutionEdges(connection, [Run], Direction.Forward, statementObserver: null));
    }

    [Fact]
    public void GraphFrontierReusesScratchAndBatchesIdentifierDetails()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);
        string[] candidates = [Run, Helper, Count, DupA];

        IReadOnlyList<FamilyGraphUnresolvedNameEdge> unresolved = reader.ReadUnresolvedNameEdges(
            connection, candidates, Direction.Both, statementObserver: null);
        IReadOnlyList<FamilyGraphResolutionEdge> resolution = reader.ReadResolutionEdges(
            connection, candidates, Direction.Both, statementObserver: null);

        Assert.NotEmpty(resolution);
        Assert.NotEmpty(unresolved);
        Assert.Equal(1, reader.Counters.ResolvePasses);
        Assert.Equal(1, reader.Counters.IdentifierDetailCommands);
        Assert.Equal(5, reader.Counters.IdentifierDetailRows);
    }

    [Fact]
    public void GraphResolutionObservationReportsEachResolutionSubphaseOnce()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);
        var observations = new List<GraphStatementObservation>();

        reader.ReadResolutionEdges(connection, [Run, Helper, Count, DupA], Direction.Both, observations.Add);

        GraphStatementObservation observation = Assert.Single(observations);
        GraphResolutionBreakdown breakdown = Assert.IsType<GraphResolutionBreakdown>(observation.ResolutionBreakdown);

        Assert.Equal(4, breakdown.CandidateLookup.Rows);
        Assert.Equal(1, breakdown.CandidateLookup.Operations);
        Assert.Equal(5, breakdown.IdentifierWithin.Rows);
        Assert.Equal(1, breakdown.IdentifierWithin.Operations);
        Assert.Equal(4, breakdown.IdentifierNamed.Rows);
        Assert.Equal(4, breakdown.IdentifierNamed.Operations);
        Assert.Equal(1, breakdown.PendingWithin.Rows);
        Assert.Equal(1, breakdown.PendingWithin.Operations);
        Assert.Equal(1, breakdown.PendingNamed.Rows);
        Assert.Equal(4, breakdown.PendingNamed.Operations);
        Assert.Equal(5, breakdown.IdentifierDetails.Rows);
        Assert.Equal(1, breakdown.IdentifierDetails.Operations);
        Assert.Equal(5, breakdown.IdentifierResolution.Rows);
        Assert.Equal(5, breakdown.IdentifierResolution.Operations);
        Assert.Equal(1, breakdown.PendingResolution.Rows);
        Assert.Equal(1, breakdown.PendingResolution.Operations);
        Assert.Equal(1, breakdown.Relationships.Rows);
        Assert.Equal(1, breakdown.Relationships.Operations);
        Assert.All(
            new[]
            {
                breakdown.CandidateLookup,
                breakdown.IdentifierWithin,
                breakdown.IdentifierNamed,
                breakdown.PendingWithin,
                breakdown.PendingNamed,
                breakdown.IdentifierDetails,
                breakdown.IdentifierResolution,
                breakdown.PendingResolution,
                breakdown.Relationships,
            },
            static phase => Assert.True(phase.Elapsed >= TimeSpan.Zero));
    }

    [Fact]
    public void ForwardGraphResolutionObservationSkipsReverseArms()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);
        var observations = new List<GraphStatementObservation>();

        reader.ReadResolutionEdges(connection, [Run, Helper, Count, DupA], Direction.Forward, observations.Add);

        GraphResolutionBreakdown breakdown = Assert.IsType<GraphResolutionBreakdown>(
            Assert.Single(observations).ResolutionBreakdown);
        Assert.Equal(5, breakdown.IdentifierWithin.Rows);
        Assert.Equal(1, breakdown.IdentifierWithin.Operations);
        Assert.Equal(0, breakdown.IdentifierNamed.Rows);
        Assert.Equal(0, breakdown.IdentifierNamed.Operations);
        Assert.Equal(TimeSpan.Zero, breakdown.IdentifierNamed.Elapsed);
        Assert.Equal(1, breakdown.PendingWithin.Rows);
        Assert.Equal(1, breakdown.PendingWithin.Operations);
        Assert.Equal(0, breakdown.PendingNamed.Rows);
        Assert.Equal(0, breakdown.PendingNamed.Operations);
        Assert.Equal(TimeSpan.Zero, breakdown.PendingNamed.Elapsed);
    }

    [Fact]
    public void ReverseGraphResolutionObservationSkipsForwardArms()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);
        var observations = new List<GraphStatementObservation>();

        reader.ReadResolutionEdges(connection, [Run, Helper, Count, DupA], Direction.Reverse, observations.Add);

        GraphResolutionBreakdown breakdown = Assert.IsType<GraphResolutionBreakdown>(
            Assert.Single(observations).ResolutionBreakdown);
        Assert.Equal(0, breakdown.IdentifierWithin.Rows);
        Assert.Equal(0, breakdown.IdentifierWithin.Operations);
        Assert.Equal(TimeSpan.Zero, breakdown.IdentifierWithin.Elapsed);
        Assert.Equal(4, breakdown.IdentifierNamed.Rows);
        Assert.Equal(4, breakdown.IdentifierNamed.Operations);
        Assert.Equal(0, breakdown.PendingWithin.Rows);
        Assert.Equal(0, breakdown.PendingWithin.Operations);
        Assert.Equal(TimeSpan.Zero, breakdown.PendingWithin.Elapsed);
        Assert.Equal(1, breakdown.PendingNamed.Rows);
        Assert.Equal(4, breakdown.PendingNamed.Operations);
    }

    [Fact]
    public void DirectionAwareGraphFrontierReusesScratchInEitherConsumerOrder()
    {
        foreach (Direction direction in new[] { Direction.Forward, Direction.Reverse })
        {
            foreach (bool unresolvedFirst in new[] { true, false })
            {
                using ResolutionStoreFixture fixture = PopulateStore();
                using SqliteConnection connection = fixture.OpenRead();
                QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

                IReadOnlyList<FamilyGraphResolutionEdge> resolution;
                IReadOnlyList<FamilyGraphUnresolvedNameEdge> unresolved;
                if (unresolvedFirst)
                {
                    unresolved = reader.ReadUnresolvedNameEdges(
                        connection, [Run, Helper, Count, DupA], direction, statementObserver: null);
                    resolution = reader.ReadResolutionEdges(
                        connection, [Run, Helper, Count, DupA], direction, statementObserver: null);
                }
                else
                {
                    resolution = reader.ReadResolutionEdges(
                        connection, [Run, Helper, Count, DupA], direction, statementObserver: null);
                    unresolved = reader.ReadUnresolvedNameEdges(
                        connection, [Run, Helper, Count, DupA], direction, statementObserver: null);
                }

                Assert.Equal(1, reader.Counters.ResolvePasses);
                Assert.Equal(
                    direction == Direction.Forward
                        ? [
                            "fn-run|fn-run|fn-help|calls|0.55|pending_resolution",
                            "fn-run|fn-run|fn-help|call|0.55|identifier_target",
                            "fn-run|fn-run|fn-help|member_access|0.50|identifier_name",
                            "fn-run|fn-run|var-count|variable_ref|0.95|identifier_target",
                        ]
                        : [
                            "fn-help|fn-run|fn-help|calls|0.55|pending_resolution",
                            "fn-help|fn-run|fn-help|call|0.55|identifier_target",
                            "fn-help|fn-run|fn-help|member_access|0.50|identifier_name",
                            "var-count|fn-run|var-count|variable_ref|0.95|identifier_target",
                        ],
                    SerializeGraphEdges(resolution, unresolved));
            }
        }
    }

    [Fact]
    public void GraphResolutionObservationIsEmittedOnlyForThePassThatBuiltScratch()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);
        var observations = new List<GraphStatementObservation>();

        reader.ReadUnresolvedNameEdges(connection, [Run, Helper], Direction.Both, observations.Add);
        reader.ReadResolutionEdges(connection, [Run, Helper], Direction.Both, observations.Add);

        Assert.Equal(
            [GraphStatementPhase.UnresolvedNameForward, GraphStatementPhase.FamilyResolution],
            observations.Select(static observation => observation.Phase));
        Assert.NotNull(observations[0].ResolutionBreakdown);
        Assert.Null(observations[1].ResolutionBreakdown);
    }

    [Fact]
    public void GraphFrontierPreservesCandidateOrderAndDuplicates()
    {
        using ResolutionStoreFixture fixture = PopulateStore();
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        IReadOnlyList<FamilyGraphResolutionEdge> single = reader.ReadResolutionEdges(
            connection, [Run], Direction.Forward, statementObserver: null);
        IReadOnlyList<FamilyGraphResolutionEdge> repeated = reader.ReadResolutionEdges(
            connection, [Run, Count, Run], Direction.Forward, statementObserver: null);

        Assert.NotEmpty(single);
        Assert.Equal(single.Count * 2, repeated.Count);
        Assert.Equal(single, repeated.Take(single.Count));
        Assert.Equal(single, repeated.Skip(single.Count));
    }

    private static readonly string[] RetiredGraphTuples =
    [
        "fn-help|fn-run|fn-help|calls|0.55|pending_resolution",
        "fn-help|fn-run|fn-help|call|0.55|identifier_target",
        "fn-help|fn-run|fn-help|member_access|0.50|identifier_name",
        "fn-run|fn-run|fn-help|calls|0.55|pending_resolution",
        "fn-run|fn-run|fn-help|call|0.55|identifier_target",
        "fn-run|fn-run|fn-help|member_access|0.50|identifier_name",
        "fn-run|fn-run|var-count|variable_ref|0.95|identifier_target",
        "var-count|fn-run|var-count|variable_ref|0.95|identifier_target",
    ];

    private static readonly string[] RetiredEvidenceTuples =
    [
        "in|cls-dup-a|fn-run|type_usage|name_fallback|0.50|",
        "in|fn-help|fn-run|calls|pending_resolution|0.55|4",
        "in|fn-help|fn-run|calls|relationship|1.00|",
        "in|fn-help|fn-run|call|identifier_resolution|0.55|4",
        "in|fn-help|fn-run|member_access|name_fallback|0.50|",
        "out|fn-run|Dup|type_usage|name_fallback|0.50|",
        "out|fn-run|Helper|member_access|name_fallback|0.50|",
        "out|fn-run|Missing|call|name_fallback|0.50|",
        "out|fn-run|fn-help|calls|pending_resolution|0.55|4",
        "out|fn-run|fn-help|calls|relationship|1.00|",
        "out|fn-run|fn-help|call|identifier_resolution|0.55|4",
        "out|fn-run|var-count|variable_ref|identifier_resolution|0.95|1",
    ];

    private static readonly string[] RetiredExportTuples =
    [
        "site-id-count|variable_ref|var-count|identifier_resolution|0.95|1",
        "site-id-dup|type_usage||name_fallback|1.00|",
        "site-id-help-member|member_access||name_fallback|1.00|",
        "site-id-help|call|fn-help|identifier_resolution|0.55|4",
        "site-id-missing|call||name_fallback|1.00|",
        "site-pend-help|call|fn-help|pending_resolution|0.55|4",
        "site-rel-help|call|fn-help|relationship|1.00|",
    ];

    [Fact]
    public void UnresolvedOverloadSetCallGetsAnEdgePerSameParentCandidate()
    {
        using ResolutionStoreFixture fixture = PopulateOverloadStore();
        fixture.AddIdentifier(1, "id-save", "Save", "src/Svc.cs", kind: "call", containingSymbolId: "fn-caller", startByte: 10, endByte: 14);
        fixture.AddPending(1, "pend-save", "fn-caller", "Save", "src/Svc.cs", startByte: 10, endByte: 14);
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        IReadOnlyList<FamilyGraphUnresolvedNameEdge> forward = reader.ReadUnresolvedNameEdges(
            connection, ["fn-caller"], Direction.Forward, statementObserver: null);
        IReadOnlyList<FamilyGraphUnresolvedNameEdge> reverse = reader.ReadUnresolvedNameEdges(
            connection, ["ov-save-a"], Direction.Reverse, statementObserver: null);

        Assert.Equal(
            ["fn-caller|ov-save-a|calls|0.40|identifier_name", "fn-caller|ov-save-b|calls|0.40|identifier_name"],
            forward
                .Select(e => $"{e.FromId}|{e.ToId}|{e.Kind}|{e.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}|{e.Source}")
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray());
        FamilyGraphUnresolvedNameEdge inbound = Assert.Single(reverse);
        Assert.Equal("fn-caller", inbound.FromId);
        Assert.Equal("ov-save-a", inbound.ToId);
        Assert.Equal(0.40, inbound.Confidence, precision: 2);
    }

    [Fact]
    public void OverloadSiblingCallExcludesTheContainingOverloadItself()
    {
        using ResolutionStoreFixture fixture = PopulateOverloadStore();
        fixture.AddIdentifier(1, "id-save-self", "Save", "src/Svc.cs", kind: "call", containingSymbolId: "ov-save-a", startByte: 20, endByte: 24);
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        IReadOnlyList<FamilyGraphUnresolvedNameEdge> forward = reader.ReadUnresolvedNameEdges(
            connection, ["ov-save-a"], Direction.Forward, statementObserver: null);

        FamilyGraphUnresolvedNameEdge edge = Assert.Single(forward);
        Assert.Equal("ov-save-a", edge.FromId);
        Assert.Equal("ov-save-b", edge.ToId);
    }

    [Fact]
    public void SameNameSymbolsInDifferentParentsStillGetNoFallbackEdge()
    {
        using ResolutionStoreFixture fixture = PopulateOverloadStore();
        fixture.AddSymbol(1, "cls-other", "Other", "class", "src/Svc.cs");
        fixture.AddSymbol(1, "ov-save-c", "Save", "method", "src/Svc.cs", parentId: "cls-other");
        fixture.AddIdentifier(1, "id-save", "Save", "src/Svc.cs", kind: "call", containingSymbolId: "fn-caller", startByte: 10, endByte: 14);
        using SqliteConnection connection = fixture.OpenRead();
        QueryTimeResolutionReader reader = FamilyReader(connection, fixture);

        Assert.Empty(reader.ReadUnresolvedNameEdges(
            connection, ["fn-caller"], Direction.Forward, statementObserver: null));
    }

    private static ResolutionStoreFixture PopulateOverloadStore()
    {
        ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/Svc.cs");
        fixture.AddSymbol(1, "cls-svc", "Svc", "class", "src/Svc.cs");
        fixture.AddSymbol(1, "ov-save-a", "Save", "method", "src/Svc.cs", parentId: "cls-svc");
        fixture.AddSymbol(1, "ov-save-b", "Save", "method", "src/Svc.cs", parentId: "cls-svc");
        fixture.AddSymbol(1, "fn-caller", "Caller", "method", "src/Svc.cs", parentId: "cls-svc");
        return fixture;
    }

    private static QueryTimeResolutionReader FamilyReader(SqliteConnection connection, ResolutionStoreFixture fixture) =>
        new(RevisionFactCache.Load(connection, fixture.Visibility()), fixture.Visibility());

    private static QueryTimeResolutionReader ArtifactReader(SqliteConnection connection) =>
        new(RevisionFactCache.LoadFromArtifact(connection), visibility: null);

    private static ResolutionStoreFixture PopulateStore()
    {
        ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/App.cs");
        fixture.AddSymbol(1, App, "App", "class", "src/App.cs");
        fixture.AddSymbol(1, Run, "Run", "method", "src/App.cs", parentId: App);
        fixture.AddSymbol(1, Helper, "Helper", "function", "src/App.cs", parentId: App);
        fixture.AddSymbol(1, Count, "count", "variable", "src/App.cs", parentId: Run);
        fixture.AddSymbol(1, DupA, "Dup", "class", "src/App.cs");
        fixture.AddSymbol(1, DupB, "Dup", "class", "src/App.cs");
        fixture.AddIdentifier(1, "id-help", "Helper", "src/App.cs", kind: "call", containingSymbolId: Run, startByte: 10, endByte: 16);
        fixture.AddIdentifier(1, "id-count", "count", "src/App.cs", kind: "variable_ref", containingSymbolId: Run, startByte: 40, endByte: 45);
        fixture.AddIdentifier(1, "id-missing", "Missing", "src/App.cs", kind: "call", containingSymbolId: Run, startByte: 50, endByte: 57);
        fixture.AddIdentifier(1, "id-dup", "Dup", "src/App.cs", kind: "type_usage", containingSymbolId: Run, startByte: 60, endByte: 63);
        fixture.AddIdentifier(1, "id-help-member", "Helper", "src/App.cs", kind: "member_access", containingSymbolId: Run, startByte: 70, endByte: 76);
        fixture.AddPending(1, "pend-help", Run, "Helper", "src/App.cs", startByte: 20, endByte: 26);
        fixture.AddRelationship(1, "rel-help", Run, Helper, "src/App.cs", startByte: 80, endByte: 86);
        return fixture;
    }

    private static ResolutionArtifactFixture PopulateArtifact()
    {
        const string AppFile = "file-9e7a11";
        ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile(AppFile, "src/App.cs");
        fixture.AddSymbol(AppFile, App, "App", "class", "src/App.cs");
        fixture.AddSymbol(AppFile, Run, "Run", "method", "src/App.cs", parentId: App);
        fixture.AddSymbol(AppFile, Helper, "Helper", "function", "src/App.cs", parentId: App);
        fixture.AddSymbol(AppFile, Count, "count", "variable", "src/App.cs", parentId: Run);
        fixture.AddSymbol(AppFile, DupA, "Dup", "class", "src/App.cs");
        fixture.AddSymbol(AppFile, DupB, "Dup", "class", "src/App.cs");
        fixture.AddIdentifier(AppFile, "id-help", "Helper", "src/App.cs", kind: "call", containingSymbolId: Run, startByte: 10, endByte: 16);
        fixture.AddIdentifier(AppFile, "id-count", "count", "src/App.cs", kind: "variable_ref", containingSymbolId: Run, startByte: 40, endByte: 45);
        fixture.AddIdentifier(AppFile, "id-missing", "Missing", "src/App.cs", kind: "call", containingSymbolId: Run, startByte: 50, endByte: 57);
        fixture.AddIdentifier(AppFile, "id-dup", "Dup", "src/App.cs", kind: "type_usage", containingSymbolId: Run, startByte: 60, endByte: 63);
        fixture.AddIdentifier(AppFile, "id-help-member", "Helper", "src/App.cs", kind: "member_access", containingSymbolId: Run, startByte: 70, endByte: 76);
        fixture.AddPending(AppFile, "pend-help", Run, "Helper", "src/App.cs", startByte: 20, endByte: 26);
        fixture.AddRelationship(AppFile, "rel-help", Run, Helper, "src/App.cs", startByte: 80, endByte: 86);
        return fixture;
    }

    private static string[] SerializeGraph(
        QueryTimeResolutionReader reader,
        SqliteConnection connection,
        IReadOnlyList<string> candidates)
    {
        var rows = new List<string>();
        foreach (FamilyGraphResolutionEdge edge in reader.ReadResolutionEdges(
                     connection, candidates, Direction.Both, statementObserver: null))
        {
            rows.Add($"{edge.CurrentId}|{edge.FromId}|{edge.ToId}|{edge.Kind}|{edge.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}|{edge.Source}");
        }

        foreach (FamilyGraphUnresolvedNameEdge edge in reader.ReadUnresolvedNameEdges(
                     connection, candidates, Direction.Both, statementObserver: null))
        {
            rows.Add($"{edge.CurrentId}|{edge.FromId}|{edge.ToId}|{edge.Kind}|{edge.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}|{edge.Source}");
        }

        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    private static string[] SerializeGraphEdges(
        IReadOnlyList<FamilyGraphResolutionEdge> resolution,
        IReadOnlyList<FamilyGraphUnresolvedNameEdge> unresolved)
    {
        var rows = new List<string>();
        foreach (FamilyGraphResolutionEdge edge in resolution)
            rows.Add($"{edge.CurrentId}|{edge.FromId}|{edge.ToId}|{edge.Kind}|{edge.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}|{edge.Source}");

        foreach (FamilyGraphUnresolvedNameEdge edge in unresolved)
            rows.Add($"{edge.CurrentId}|{edge.FromId}|{edge.ToId}|{edge.Kind}|{edge.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}|{edge.Source}");

        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    private static string[] SerializeEvidence(
        QueryTimeResolutionReader reader,
        SqliteConnection connection,
        IReadOnlyList<string> candidates)
    {
        var rows = new List<string>();
        Dictionary<string, List<ReferenceEvidence>> inboundExact = reader.ReadInboundExact(connection, candidates);
        Dictionary<string, List<ReferenceEvidence>> inboundFallback = reader.ReadInboundFallback(connection, candidates);
        Dictionary<string, List<OutgoingReferenceEvidence>> outgoingExact = reader.ReadOutgoingExact(connection, candidates);
        Dictionary<string, List<OutgoingReferenceEvidence>> outgoingFallback = reader.ReadOutgoingFallback(connection, candidates);
        foreach (string id in candidates)
        {
            if (inboundExact.TryGetValue(id, out List<ReferenceEvidence>? exact))
            {
                foreach (ReferenceEvidence row in exact)
                    rows.Add(FormatInbound("in", id, row));
            }

            if (inboundFallback.TryGetValue(id, out List<ReferenceEvidence>? fallback))
            {
                foreach (ReferenceEvidence row in fallback)
                    rows.Add(FormatInbound("in", id, row));
            }

            if (outgoingExact.TryGetValue(id, out List<OutgoingReferenceEvidence>? outExact))
            {
                foreach (OutgoingReferenceEvidence row in outExact)
                    rows.Add(FormatOutgoing(row));
            }

            if (outgoingFallback.TryGetValue(id, out List<OutgoingReferenceEvidence>? outFallback))
            {
                foreach (OutgoingReferenceEvidence row in outFallback)
                    rows.Add(FormatOutgoing(row));
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    private static string FormatInbound(string direction, string current, ReferenceEvidence row) =>
        string.Join(
            '|',
            direction,
            current,
            row.ContainingSymbolId,
            row.SourceKind,
            SourceName(row.Source),
            row.Confidence.ToString("0.00", CultureInfo.InvariantCulture),
            row.ResolutionTier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static string FormatOutgoing(OutgoingReferenceEvidence row) =>
        string.Join(
            '|',
            "out",
            row.ContainingSymbolId,
            row.TargetSymbolId ?? row.TargetName,
            row.SourceKind,
            SourceName(row.Source),
            row.Confidence.ToString("0.00", CultureInfo.InvariantCulture),
            row.ResolutionTier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static string SourceName(ReferenceEvidenceSource source) => source switch
    {
        ReferenceEvidenceSource.IdentifierResolution => "identifier_resolution",
        ReferenceEvidenceSource.PendingResolution => "pending_resolution",
        ReferenceEvidenceSource.Relationship => "relationship",
        ReferenceEvidenceSource.NameFallback => "name_fallback",
        _ => source.ToString(),
    };

    private static string[] SerializeExport(QueryTimeResolutionReader reader, SqliteConnection connection)
    {
        var rows = new List<string>();
        foreach (QueryTimeExportEvidence row in reader.ReadExportEvidence(connection))
        {
            rows.Add(string.Join(
                '|',
                row.ReferenceSiteId,
                row.CanonicalKind,
                row.TargetSymbolId ?? string.Empty,
                row.EvidenceSource,
                row.Confidence.ToString("0.00", CultureInfo.InvariantCulture),
                row.ResolutionTier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty));
        }

        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    private sealed class FixtureReadSession : IWorkspaceReadSession, IQueryTimeResolutionHost
    {
        private readonly SqliteConnection _connection;

        public FixtureReadSession(ResolutionStoreFixture fixture, WorkspaceReadMode mode)
        {
            _connection = fixture.OpenRead();
            Resolution = new QueryTimeResolutionReader(
                RevisionFactCache.Load(_connection, fixture.Visibility()),
                fixture.Visibility());
            Snapshot = new WorkspaceReadSnapshot(
                "/tmp/ws",
                "workspace-a",
                fixture.Visibility().FamilyId,
                fixture.ViewId,
                new WorkspaceFreshnessToken(fixture.Visibility().FamilyId, fixture.Generation),
                "full",
                mode);
        }

        public WorkspaceReadSnapshot Snapshot { get; }

        public QueryTimeResolutionReader Resolution { get; }

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) => query(_connection);

        public void Dispose() => _connection.Dispose();
    }
}
