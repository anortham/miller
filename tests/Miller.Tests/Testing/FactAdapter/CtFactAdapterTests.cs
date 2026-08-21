using Microsoft.Data.Sqlite;
using Miller.Core.References;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Indexing;
using Miller.Tests.Indexing.Resolution;
using Xunit;

namespace Miller.Tests.Testing.FactAdapter;

public sealed class CtFactAdapterTests
{
    private const string ServiceFileId = "file-service";
    private const string TestFileId = "file-tests";
    private const string ServiceClassId = "cls-service";
    private const string ValidateId = "fn-validate";
    private const string ProcessId = "fn-process";
    private const string TestClassId = "cls-tests";
    private const string ProcessWorksId = "fn-test";

    [Fact]
    public void Current_UsesTheSharedGenerationIdentityCursor()
    {
        using ResolutionArtifactFixture fixture = CreateFixture();
        using LegacyArtifactReadSession session = LegacyArtifactReadSession.Open(fixture.DbPath);
        using var adapter = new CtFactAdapter(session);

        Assert.Equal(CtIndexCursor.FromSnapshot(session.Snapshot), adapter.Current);
        Assert.Equal("ctgen1:artifact:art-1:blake3", adapter.Current.IndexIdentity);
        Assert.Equal(1, adapter.Current.Revision);
        Assert.Equal("art-1", adapter.Current.FamilyId);
    }

    [Fact]
    public void Current_StoreMode_UsesTheSharedGenerationIdentityCursor()
    {
        WorkspaceReadSnapshot snapshot = WorkspaceReadSnapshotTests.StoreSnapshot();
        using var adapter = new CtFactAdapter(new SnapshotOnlyReadSession(snapshot));

        Assert.Equal(CtIndexCursor.FromSnapshot(snapshot), adapter.Current);
        Assert.Equal(snapshot.IndexGenerationIdentity, adapter.Current.IndexIdentity);
        Assert.Equal("fam-1", adapter.Current.FamilyId);
    }

    [Fact]
    public void FromSnapshot_StoreMode_UsesFreshnessRevisionNeverTheStoreLogSequence()
    {
        WorkspaceReadSnapshot snapshot = WorkspaceReadSnapshotTests.StoreSnapshot(
            revision: 42, storeLogSequence: 200);

        CtIndexCursor cursor = CtIndexCursor.FromSnapshot(snapshot);

        Assert.Equal(snapshot.IndexGenerationIdentity, cursor.IndexIdentity);
        Assert.Equal(42, cursor.Revision);
        Assert.Equal("fam-1", cursor.FamilyId);
    }

    [Fact]
    public void FromSnapshot_RoutineDeltaImportKeepsTheIdentityWhileTheRevisionAdvances()
    {
        // A routine delta import moves the revision, the log sequence, the manifest generation,
        // and the manifest hash. The cursor identity must not move with them.
        CtIndexCursor before = CtIndexCursor.FromSnapshot(
            WorkspaceReadSnapshotTests.StoreSnapshot(
                revision: 42, storeLogSequence: 42, manifestGeneration: 736, manifestHash: "mh-a"));
        CtIndexCursor after = CtIndexCursor.FromSnapshot(
            WorkspaceReadSnapshotTests.StoreSnapshot(
                revision: 48, storeLogSequence: 48, manifestGeneration: 750, manifestHash: "mh-b"));

        Assert.Equal(before.IndexIdentity, after.IndexIdentity);
        Assert.Equal(before.FamilyId, after.FamilyId);
        Assert.Equal(42, before.Revision);
        Assert.Equal(48, after.Revision);
    }

    [Fact]
    public void FromSnapshot_SameRevisionUnderTwoGenerationsNeverComparesFresh()
    {
        CtIndexCursor genA = CtIndexCursor.FromSnapshot(
            WorkspaceReadSnapshotTests.StoreSnapshot(revision: 5, generationName: "gen-000002"));
        CtIndexCursor genB = CtIndexCursor.FromSnapshot(
            WorkspaceReadSnapshotTests.StoreSnapshot(revision: 5, generationName: "gen-000003"));

        Assert.Equal(genA.Revision, genB.Revision);
        Assert.NotEqual(genA.IndexIdentity, genB.IndexIdentity);
        Assert.NotEqual(
            new CtFreshnessKey(genA.IndexIdentity, genA.Revision),
            new CtFreshnessKey(genB.IndexIdentity, genB.Revision));
    }

    private sealed class SnapshotOnlyReadSession(WorkspaceReadSnapshot snapshot) : IWorkspaceReadSession
    {
        public WorkspaceReadSnapshot Snapshot { get; } = snapshot;

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) =>
            throw new NotSupportedException("Cursor reads never open the database.");

        public void Dispose()
        {
        }
    }

    [Fact]
    public void SymbolsForChangedFiles_ReturnsTypedSymbolsWithContentHash()
    {
        using ResolutionArtifactFixture fixture = CreateFixture();
        using var adapter = CtFactAdapter.OpenArtifact(fixture.DbPath);

        IReadOnlyList<CtSymbolFact> symbols = adapter.SymbolsForChangedFiles(["src/Service.cs"]);

        Assert.Equal(
            [ServiceClassId, ProcessId, ValidateId],
            symbols.Select(row => row.SymbolId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        CtSymbolFact process = Assert.Single(symbols, row => row.SymbolId == ProcessId);
        Assert.Equal("Process", process.Name);
        Assert.Equal("function", process.Kind);
        Assert.Equal("src/Service.cs", process.FilePath);
        Assert.Equal($"blake3:{ServiceFileId}", process.ContentHash);
        Assert.False(process.IsTest);
        Assert.Empty(adapter.SymbolsForChangedFiles(["missing.cs"]));
    }

    [Fact]
    public void IdentifierEvidenceTo_ReturnsResolvedInboundIdentifiers()
    {
        using ResolutionArtifactFixture fixture = CreateFixture();
        using var adapter = CtFactAdapter.OpenArtifact(fixture.DbPath);

        IReadOnlyList<CtReferenceFact> evidence = adapter.IdentifierEvidenceTo([ProcessId]);

        CtReferenceFact row = Assert.Single(evidence);
        Assert.Equal(ProcessWorksId, row.SourceSymbolId);
        Assert.Equal(ProcessId, row.TargetSymbolId);
        Assert.Equal("call", row.Kind);
        Assert.Equal("identifier_resolution", row.Provenance);
        Assert.Equal(ReferenceResolutionStatus.Exact, row.ResolutionStatus);
        Assert.DoesNotContain(adapter.IdentifierEvidenceTo([ProcessId]), item => item.Provenance == "relationship");
    }

    [Fact]
    public void ReferencesTo_IncludesRelationshipAndIdentifierSources()
    {
        using ResolutionArtifactFixture fixture = CreateFixture();
        using var adapter = CtFactAdapter.OpenArtifact(fixture.DbPath);

        IReadOnlyList<CtReferenceFact> inbound = adapter.ReferencesTo([ValidateId]);

        Assert.Contains(inbound, row =>
            row.SourceSymbolId == ProcessId
            && row.TargetSymbolId == ValidateId
            && row.Provenance == "relationship");
        Assert.Contains(adapter.ReferencesTo([ProcessId]), row =>
            row.SourceSymbolId == ProcessWorksId
            && row.Provenance == "identifier_resolution");
    }

    [Fact]
    public void Impact_PartitionsDependentsIntoImpactedAndTests()
    {
        using ResolutionArtifactFixture fixture = CreateFixture();
        using var adapter = CtFactAdapter.OpenArtifact(fixture.DbPath);

        CtImpactResult impact = adapter.Impact([ValidateId], maxDepth: 2, limit: 100);

        Assert.Contains(impact.Impacted, row => row.SymbolId == ProcessId && !row.IsTest);
        Assert.Contains(impact.Tests, row => row.SymbolId == ProcessWorksId && row.IsTest);
        Assert.DoesNotContain(impact.Impacted, row => row.SymbolId == ValidateId);
        Assert.True(impact.NodesVisited >= 2);
    }

    [Fact]
    public void MillerFactSource_ExposesCtFreshnessKeyWithoutIndexingDependingOnTesting()
    {
        using ResolutionArtifactFixture fixture = CreateFixture();
        using var adapter = CtFactAdapter.OpenArtifact(fixture.DbPath);
        IMillerFactSource facts = new MillerFactSource(adapter);

        Assert.Equal(new CtFreshnessKey(adapter.Current.IndexIdentity, adapter.Current.Revision), facts.Freshness);
        Assert.Equal(adapter.SymbolsForChangedFiles(["src/Service.cs"]).Count, facts.SymbolsForChangedFiles(["src/Service.cs"]).Count);
        Assert.False(typeof(IMillerFactSource).IsAssignableFrom(typeof(CtFactAdapter)));
        Assert.True(typeof(IMillerFactSource).IsAssignableFrom(typeof(MillerFactSource)));
    }

    private static ResolutionArtifactFixture CreateFixture()
    {
        ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile(ServiceFileId, "src/Service.cs");
        fixture.AddFile(TestFileId, "tests/ServiceTests.cs");
        fixture.AddSymbol(ServiceFileId, ServiceClassId, "Service", "class", "src/Service.cs");
        fixture.AddSymbol(ServiceFileId, ValidateId, "Validate", "method", "src/Service.cs", parentId: ServiceClassId);
        fixture.AddSymbol(ServiceFileId, ProcessId, "Process", "function", "src/Service.cs", parentId: ServiceClassId);
        fixture.AddSymbol(TestFileId, TestClassId, "ServiceTests", "class", "tests/ServiceTests.cs");
        fixture.AddSymbol(TestFileId, ProcessWorksId, "ProcessWorks", "method", "tests/ServiceTests.cs", parentId: TestClassId);
        fixture.AddRelationship(ServiceFileId, "rel-validate", ProcessId, ValidateId, "src/Service.cs", kind: "calls");
        fixture.AddRelationship(TestFileId, "rel-process", ProcessWorksId, ProcessId, "tests/ServiceTests.cs", kind: "calls");
        fixture.AddIdentifier(
            TestFileId,
            "id-process",
            "Process",
            "tests/ServiceTests.cs",
            kind: "call",
            containingSymbolId: ProcessWorksId,
            startByte: 40,
            endByte: 47);
        MarkTest(fixture, TestClassId, container: true);
        MarkTest(fixture, ProcessWorksId, container: false);
        return fixture;
    }

    private static void MarkTest(ResolutionArtifactFixture fixture, string symbolId, bool container)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE symbols
            SET is_test = 1, test_container = $container, test_lifecycle = 1
            WHERE symbol_id = $id;
            """;
        command.Parameters.AddWithValue("$container", container ? 1 : 0);
        command.Parameters.AddWithValue("$id", symbolId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
