using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.References;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using Miller.Tests.Indexing.Resolution;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreResolutionReaderTests
{
    private const string Run = "fn-run";
    private const string Helper = "fn-help";

    [Fact]
    public void ReferenceEvidenceReaderAcceptsFamilyStoreWithoutResolutionViews()
    {
        using ResolutionStoreFixture fixture = Populate();
        using var session = new HostSession(fixture);
        var bounds = new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10);

        ReferenceEvidenceBundle result = ReferenceEvidenceReader.ReadForSymbol(
            session,
            Helper,
            new ReferenceEvidenceQuery(bounds),
            new ReferenceEvidenceQuery(bounds),
            bounds,
            [ReferenceKind.Call]);

        Assert.Contains(result.Inbound.Exact, row => row.Source == ReferenceEvidenceSource.IdentifierResolution);
        Assert.Contains(result.Inbound.Exact, row => row.Source == ReferenceEvidenceSource.PendingResolution);
    }

    [Fact]
    public void FamilyStoreReferenceEvidenceMatchesEquivalentArtifact()
    {
        using ResolutionStoreFixture store = Populate();
        using ResolutionArtifactFixture artifact = PopulateArtifact();
        using var family = new HostSession(store);
        using var legacy = new ArtifactSession(artifact);
        var bounds = new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10);

        Assert.Equal(SerializeEdges(family, bounds), SerializeEdges(legacy, bounds));
    }

    [Fact]
    public void FamilyStoreAnswersWhenResolutionStateIsUnbound()
    {
        using ResolutionStoreFixture fixture = Populate();
        fixture.ExecuteWrite("UPDATE views SET resolution_state='unbound', resolution_base_id=NULL;");
        using var session = new HostSession(fixture);
        var bounds = new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10);

        ReferenceEvidenceSet inbound = ReferenceEvidenceReader.Read(session, Helper, bounds);
        Assert.NotEmpty(inbound.Exact);
    }

    private static string SerializeEdges(IWorkspaceReadSession session, ReferenceEvidenceBounds bounds)
    {
        ReferenceEvidenceSet inbound = ReferenceEvidenceReader.Read(session, Helper, bounds);
        OutgoingReferenceEvidenceSet outgoing = ReferenceEvidenceReader.ReadOutgoing(session, Run, bounds);
        return JsonSerializer.Serialize(new
        {
            Inbound = inbound.Exact.Select(static row => (row.Source, row.SourceKind, row.ContainingSymbolId, row.Confidence, row.ResolutionTier)),
            Outgoing = outgoing.Exact.Select(static row => (row.Source, row.SourceKind, row.TargetSymbolId, row.Confidence, row.ResolutionTier)),
        });
    }

    private static ResolutionStoreFixture Populate()
    {
        ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/App.cs");
        fixture.AddSymbol(1, Run, "Run", "method", "src/App.cs");
        fixture.AddSymbol(1, Helper, "Helper", "function", "src/App.cs");
        fixture.AddIdentifier(1, "id-help", "Helper", "src/App.cs", kind: "call", containingSymbolId: Run, startByte: 10, endByte: 16);
        fixture.AddPending(1, "pend-help", Run, "Helper", "src/App.cs", startByte: 20, endByte: 26);
        fixture.AddRelationship(1, "rel-help", Run, Helper, "src/App.cs", startByte: 80, endByte: 86);
        return fixture;
    }

    private static ResolutionArtifactFixture PopulateArtifact()
    {
        const string AppFile = "file-9e7a11";
        ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile(AppFile, "src/App.cs");
        fixture.AddSymbol(AppFile, Run, "Run", "method", "src/App.cs");
        fixture.AddSymbol(AppFile, Helper, "Helper", "function", "src/App.cs");
        fixture.AddIdentifier(AppFile, "id-help", "Helper", "src/App.cs", kind: "call", containingSymbolId: Run, startByte: 10, endByte: 16);
        fixture.AddPending(AppFile, "pend-help", Run, "Helper", "src/App.cs", startByte: 20, endByte: 26);
        fixture.AddRelationship(AppFile, "rel-help", Run, Helper, "src/App.cs", startByte: 80, endByte: 86);
        return fixture;
    }

    private sealed class HostSession : IWorkspaceReadSession, IQueryTimeResolutionHost
    {
        private readonly SqliteConnection _connection;

        public HostSession(ResolutionStoreFixture fixture)
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
                WorkspaceReadMode.FamilyStore);
        }

        public WorkspaceReadSnapshot Snapshot { get; }

        public QueryTimeResolutionReader Resolution { get; }

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) => query(_connection);

        public void Dispose() => _connection.Dispose();
    }

    private sealed class ArtifactSession : IWorkspaceReadSession, IQueryTimeResolutionHost
    {
        private readonly SqliteConnection _connection;

        public ArtifactSession(ResolutionArtifactFixture fixture)
        {
            _connection = fixture.OpenRead();
            Resolution = new QueryTimeResolutionReader(RevisionFactCache.LoadFromArtifact(_connection), visibility: null);
            Snapshot = new WorkspaceReadSnapshot(
                "/tmp/ws",
                "workspace-a",
                "art-1",
                "legacy",
                new WorkspaceFreshnessToken("art-1", 1),
                "full",
                WorkspaceReadMode.LegacyArtifact);
        }

        public WorkspaceReadSnapshot Snapshot { get; }

        public QueryTimeResolutionReader Resolution { get; }

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) => query(_connection);

        public void Dispose() => _connection.Dispose();
    }
}
