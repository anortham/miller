using Microsoft.Data.Sqlite;
using Miller.Core.References;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Testing;
using Miller.Testing;
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
    public void Current_UsesSnapshotIndexIdentityAndRevision()
    {
        using ResolutionArtifactFixture fixture = CreateFixture();
        using LegacyArtifactReadSession session = LegacyArtifactReadSession.Open(fixture.DbPath);
        using var adapter = new CtFactAdapter(session);

        Assert.Equal(session.Snapshot.IndexIdentity, adapter.Current.IndexIdentity);
        Assert.Equal(session.Snapshot.Freshness.Revision, adapter.Current.Revision);
        Assert.Equal("art-1", adapter.Current.IndexIdentity);
        Assert.Equal(1, adapter.Current.Revision);
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
