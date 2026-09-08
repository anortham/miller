using Microsoft.Data.Sqlite;
using Miller.Core.References;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Indexing;
using Miller.Tests.Indexing.Resolution;
using Miller.Tests.Support;
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
    public void Native_class_candidates_reject_excess_names_before_database_work()
    {
        using var adapter = new CtFactAdapter(new SnapshotOnlyReadSession(WorkspaceReadSnapshotTests.StoreSnapshot()));
        CtNativeSymbolCandidates result = adapter.NativeClassCandidates(Enumerable.Range(0, 1025).Select(i => "Class" + i).ToArray());
        Assert.True(result.Truncated);
        Assert.Empty(result.Symbols);
    }

    [Fact]
    public void Native_class_candidates_read_complete_parent_scopes_from_arbitrary_files()
    {
        using var fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("file", "Odd.kt", "kotlin");
        fixture.AddSymbol("file", "package", "sample", "namespace", "Odd.kt", "kotlin");
        fixture.AddSymbol("file", "type", "Suite", "class", "Odd.kt", "kotlin");
        fixture.AddSymbol("file", "method", "selected", "method", "Odd.kt", "kotlin", parentId: "type");
        using var adapter = CtFactAdapter.OpenArtifact(fixture.DbPath);
        CtNativeSymbolCandidates result = adapter.NativeClassCandidates(["Suite", "Suite"]);
        Assert.False(result.Truncated);
        Assert.Equal(["method", "package", "type"], result.Symbols.Select(symbol => symbol.SymbolId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Native_class_candidates_support_the_pinned_family_store_session()
    {
        using StoreFixture fixture = StoreFixture.Create();
        string storePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using (var connection = new SqliteConnection($"Data Source={storePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE file_versions SET language='scala' WHERE version_id=2;
                UPDATE manifest_entries SET language='scala' WHERE view_id='view-a' AND generation=2;
                UPDATE symbols SET language='scala', name='Suite' WHERE version_id=2 AND symbol_id='symbol';
                INSERT INTO symbols VALUES
                  (2,'package','same.cs','scala','sample','namespace',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
                  (2,'method','same.cs','scala','selected','method',NULL,NULL,NULL,'symbol',1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,1,0,0,NULL);
                """;
            command.ExecuteNonQuery();
        }
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding, "workspace");
        using var adapter = new CtFactAdapter(session);

        CtNativeSymbolCandidates result = adapter.NativeClassCandidates(["Suite"]);

        Assert.False(result.Truncated);
        Assert.Equal(["method", "package", "symbol"], result.Symbols.Select(symbol => symbol.SymbolId).Order(StringComparer.Ordinal));
        CtFileFact file = Assert.Single(result.Files!);
        Assert.Equal("scala", file.Language);
        Assert.Equal("blake3:visible", file.ContentHash);
    }

    [Fact]
    public void Native_class_candidates_refuse_oversized_symbol_population_without_partial_results()
    {
        using ResolutionArtifactFixture fixture = CreateFixture();
        fixture.ExecuteWrite("UPDATE symbols SET language='kotlin'; UPDATE files SET language='kotlin';");
        fixture.ExecuteWrite("""
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<16000)
            INSERT INTO symbols (symbol_id,file_id,path,language,name,kind,signature,doc_comment,visibility,parent_symbol_id,
                start_line,start_column,end_line,end_column,start_byte,end_byte,is_test,test_container,test_lifecycle,metadata_json)
            SELECT 'extra-'||n.x,file_id,path,language,'extra-'||n.x,kind,signature,doc_comment,visibility,parent_symbol_id,
                   start_line,start_column,end_line,end_column,start_byte,end_byte,is_test,test_container,test_lifecycle,metadata_json
            FROM symbols,n WHERE symbol_id='cls-service';
            """);
        using var adapter = CtFactAdapter.OpenArtifact(fixture.DbPath);
        CtNativeSymbolCandidates result = adapter.NativeClassCandidates(["Service"]);
        Assert.True(result.Truncated);
        Assert.Empty(result.Symbols);
    }

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
    public void Facts_PreserveDetailedRoleAndFileEvidence()
    {
        using var fixture = JulieDbFixture.CreateTestRoleEvidenceScenario("ct-facts");
        using var adapter = CtFactAdapter.OpenArtifact(fixture.DbPath);

        IReadOnlyList<CtSymbolFact> symbols = adapter.SymbolsForChangedFiles([
            "a-current.cs",
            "b-file-status.cs",
            "c-diagnostic.cs",
            "d-combined.cs",
            "e-unavailable.cs"]);

        CtSymbolFact current = Assert.Single(symbols, row => row.Name == "Current");
        Assert.Equal(true, current.TestCase);
        Assert.Equal(true, current.TestContainer);
        Assert.Equal(false, current.TestLifecycle);
        Assert.Equal(TestRoleEvidence.CurrentStatus, current.TestEvidenceStatus);
        Assert.Null(current.TestEvidenceReason);

        CtSymbolFact fileStatus = Assert.Single(symbols, row => row.Name == "FileStatus");
        Assert.Equal(false, fileStatus.TestCase);
        Assert.Equal(false, fileStatus.TestContainer);
        Assert.Equal(true, fileStatus.TestLifecycle);
        Assert.Equal(TestRoleEvidence.UnknownStatus, fileStatus.TestEvidenceStatus);
        Assert.Equal(TestRoleEvidence.FileStatusReason, fileStatus.TestEvidenceReason);

        CtSymbolFact diagnostic = Assert.Single(symbols, row => row.Name == "Diagnostic");
        Assert.Equal(false, diagnostic.TestCase);
        Assert.Equal(true, diagnostic.TestContainer);
        Assert.Equal(false, diagnostic.TestLifecycle);
        Assert.Equal(TestRoleEvidence.UnknownStatus, diagnostic.TestEvidenceStatus);
        Assert.Equal(TestRoleEvidence.ParseDiagnosticsReason, diagnostic.TestEvidenceReason);

        CtSymbolFact combined = Assert.Single(symbols, row => row.Name == "Combined");
        Assert.Equal(false, combined.TestCase);
        Assert.Equal(true, combined.TestContainer);
        Assert.Equal(true, combined.TestLifecycle);
        Assert.Equal(TestRoleEvidence.UnknownStatus, combined.TestEvidenceStatus);
        Assert.Equal(TestRoleEvidence.FileStatusAndParseDiagnosticsReason, combined.TestEvidenceReason);

        CtSymbolFact unavailable = Assert.Single(symbols, row => row.Name == "Unavailable");
        Assert.Equal(true, unavailable.TestCase);
        Assert.Equal(false, unavailable.TestContainer);
        Assert.Equal(false, unavailable.TestLifecycle);
        Assert.Equal(TestRoleEvidence.UnknownStatus, unavailable.TestEvidenceStatus);
        Assert.Equal(TestRoleEvidence.FileEvidenceUnavailableReason, unavailable.TestEvidenceReason);

        IReadOnlyList<CtFileFact> files = adapter.FileFactsForPaths([
            "a-current.cs",
            "b-file-status.cs",
            "c-diagnostic.cs",
            "d-combined.cs",
            "e-unavailable.cs"]);
        Assert.Equal(5, files.Count);
        CtFileFact currentFile = Assert.Single(files, row => row.Path == "a-current.cs");
        Assert.Equal("csharp", currentFile.Language);
        Assert.Equal("blake3:" + ContentHasher.Blake3Hex([]), currentFile.ContentHash);
        Assert.Equal("indexed", currentFile.Status);
        Assert.False(currentFile.HasParseDiagnostics);
        Assert.True(currentFile.EvidenceAvailable);
        CtFileFact diagnosticFile = Assert.Single(files, row => row.Path == "c-diagnostic.cs");
        Assert.True(diagnosticFile.HasParseDiagnostics);
        Assert.True(diagnosticFile.EvidenceAvailable);
        CtFileFact unavailableFile = Assert.Single(files, row => row.Path == "e-unavailable.cs");
        Assert.False(unavailableFile.EvidenceAvailable);
        Assert.Null(unavailableFile.Language);
        Assert.Null(unavailableFile.ContentHash);
        Assert.Null(unavailableFile.Status);
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
        CtImpactedSymbol test = Assert.Single(impact.Tests, row => row.SymbolId == ProcessWorksId);
        Assert.Equal(false, test.TestCase);
        Assert.Equal(false, test.TestContainer);
        Assert.Equal(true, test.TestLifecycle);
        Assert.Equal(TestRoleEvidence.CurrentStatus, test.TestEvidenceStatus);
        Assert.Null(test.TestEvidenceReason);
        Assert.DoesNotContain(impact.Impacted, row => row.SymbolId == ValidateId);
        Assert.True(impact.NodesVisited >= 2);
    }

    [Fact]
    public void FileFactsForPaths_ReturnsUnknownWhenFilesEvidenceIsAbsent()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.ExecuteWrite("DROP TABLE files;");
        using var adapter = CtFactAdapter.OpenArtifact(fixture.DbPath);

        CtFileFact fact = Assert.Single(adapter.FileFactsForPaths(["src/missing.cs"]));

        Assert.Equal("src/missing.cs", fact.Path);
        Assert.Null(fact.Language);
        Assert.Null(fact.ContentHash);
        Assert.Null(fact.Status);
        Assert.False(fact.HasParseDiagnostics);
        Assert.False(fact.EvidenceAvailable);
    }

    [Fact]
    public void SymbolsForChangedFiles_ReadsOldArtifactsWithoutRoleOrFileEvidence()
    {
        using ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("legacy-file", "src/Legacy.cs");
        fixture.AddSymbol("legacy-file", "legacy-symbol", "Legacy", "class", "src/Legacy.cs");
        fixture.ExecuteWrite("""
            UPDATE symbols SET is_test = 1 WHERE symbol_id = 'legacy-symbol';
            ALTER TABLE symbols DROP COLUMN test_container;
            ALTER TABLE symbols DROP COLUMN test_lifecycle;
            DROP TABLE files;
            """);
        using var adapter = CtFactAdapter.OpenArtifact(fixture.DbPath);

        CtSymbolFact fact = Assert.Single(adapter.SymbolsForChangedFiles(["src/Legacy.cs"]));

        Assert.True(fact.IsTest);
        Assert.Equal(true, fact.TestCase);
        Assert.Equal(false, fact.TestContainer);
        Assert.Equal(false, fact.TestLifecycle);
        Assert.Equal(TestRoleEvidence.UnknownStatus, fact.TestEvidenceStatus);
        Assert.Equal(TestRoleEvidence.FileEvidenceUnavailableReason, fact.TestEvidenceReason);
    }

    [Fact]
    public void MillerFactSource_ExposesCtFreshnessKeyWithoutIndexingDependingOnTesting()
    {
        using ResolutionArtifactFixture fixture = CreateFixture();
        using var adapter = CtFactAdapter.OpenArtifact(fixture.DbPath);
        IMillerFactSource facts = new MillerFactSource(adapter);

        Assert.Equal(new CtFreshnessKey(adapter.Current.IndexIdentity, adapter.Current.Revision), facts.Freshness);
        Assert.Equal(adapter.SymbolsForChangedFiles(["src/Service.cs"]).Count, facts.SymbolsForChangedFiles(["src/Service.cs"]).Count);
        Assert.Equal(adapter.FileFactsForPaths(["src/Service.cs"]), facts.FileFactsForPaths(["src/Service.cs"]));
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
