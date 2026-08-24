using System.Security.Cryptography;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Testing.Parsing;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Testing.Analysis;

public sealed class CoverageArtifactImporterTests : IDisposable
{
    private const string Workspace = "ws:1";
    private static readonly CtFreshnessKey Fresh = new("gen-1", 1);

    private readonly string _dir;
    private readonly string _dbPath;

    public CoverageArtifactImporterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-coverage-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, CtSchema.DbFileName);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Import_lcov_records_rows_and_maps_file_symbol_and_test_case()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(
            root,
            Path.Combine("artifacts", "lcov.info"),
            """
            TN:test_covers_service
            SF:src/service.cs
            DA:4,2
            DA:12,0
            end_of_record
            """);
        var facts = SeedFacts();
        SeedCoverageTestCase(store);

        var report = CoverageArtifactImporter.Import(store, Request(root, artifact), facts);

        Assert.Equal("coverage", report.Kind);
        Assert.Equal("lcov", report.Parser);
        Assert.Equal("imported", report.State);
        Assert.Equal("artifacts/lcov.info", report.ArtifactPath);
        Assert.Equal(1, report.Counts["artifacts"]);
        Assert.Equal(1, report.Counts["coverage_files"]);
        Assert.Equal(2, report.Counts["coverage_spans"]);
        Assert.Single(store.ListRunArtifacts(Workspace));
        var coverageFile = Assert.Single(store.ListCoverageFiles(Workspace));
        Assert.Equal(2, store.ListCoverageSpans(coverageFile.Id).Count);
        Assert.Equal(report.ArtifactId, coverageFile.ArtifactId);
        Assert.Equal("sha256:source", coverageFile.SourceHash);
        Assert.Equal(true, coverageFile.Metadata["mapped"]);

        var rows = store.ListCoverageSpans(coverageFile.Id);
        Assert.Equal(
            [
                (4, 2, "src/service.cs", "sym:run"),
                (12, 0, "src/service.cs", "sym:service"),
            ],
            rows.Select(row => (row.StartLine, row.Hits, row.FilePath, row.SymbolName)).ToArray());
        Assert.All(rows, row =>
        {
            Assert.Equal(report.ArtifactId, row.Metadata["artifact_id"]);
            Assert.Equal("test:coverage", row.Metadata["test_case_id"]);
        });
    }

    [Fact]
    public void Import_stamps_run_and_project_payload_keys()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(root, Path.Combine("artifacts", "payload.info"), "SF:src/service.cs\nDA:1,1\nend_of_record\n");
        string project = Path.Combine(root, "App.Tests.csproj");

        CoverageArtifactImporter.Import(store, Request(root, artifact, runId: "run:payload", projectPath: project));

        IReadOnlyDictionary<string, object?> payload = Assert.Single(store.ListRunArtifacts(Workspace)).Payload;
        Assert.Equal("run:payload", payload["run_id"]);
        Assert.Equal(Path.GetFullPath(project), payload["project_path"]);
    }

    [Fact]
    public void Import_is_idempotent_by_artifact_hash_and_uses_artifact_hash_for_unmapped_files()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(
            root,
            Path.Combine("artifacts", "missing.info"),
            """
            SF:src/missing.cs
            DA:1,1
            end_of_record
            """);

        var first = CoverageArtifactImporter.Import(store, Request(root, artifact));
        var second = CoverageArtifactImporter.Import(store, Request(root, artifact));

        Assert.Equal(first.ArtifactId, second.ArtifactId);
        Assert.Single(store.ListRunArtifacts(Workspace));
        var coverageFile = Assert.Single(store.ListCoverageFiles(Workspace));
        Assert.Single(store.ListCoverageSpans(coverageFile.Id));
        Assert.Equal(Sha256(artifact), coverageFile.SourceHash);
        Assert.Equal(false, coverageFile.Metadata["mapped"]);
        var span = Assert.Single(store.ListCoverageSpans(coverageFile.Id));
        Assert.Equal("src/missing.cs", span.FilePath);
        Assert.Null(span.SymbolName);
    }

    [Fact]
    public void Import_cobertura_records_coverage_rows()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(
            root,
            Path.Combine("artifacts", "cobertura.xml"),
            """
            <?xml version="1.0"?>
            <coverage>
              <packages>
                <package>
                  <classes>
                    <class filename="src/service.cs">
                      <lines>
                        <line number="9" hits="3" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);

        var report = CoverageArtifactImporter.Import(store, Request(root, artifact));

        Assert.Equal("cobertura", report.Parser);
        Assert.Single(store.ListRunArtifacts(Workspace));
        var coverageFile = Assert.Single(store.ListCoverageFiles(Workspace));
        var span = Assert.Single(store.ListCoverageSpans(coverageFile.Id));
        Assert.Equal(9, span.StartLine);
        Assert.Equal(3, span.Hits);
    }

    [Fact]
    public void Import_rejects_artifacts_outside_workspace_root()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var outside = WriteArtifact(_dir, "outside.info", "SF:src/service.cs\nDA:1,1\nend_of_record\n");

        var ex = Assert.Throws<ArgumentException>(() =>
            CoverageArtifactImporter.Import(store, Request(root, outside)));

        Assert.Equal("artifactPath", ex.ParamName);
        Assert.Empty(store.ListRunArtifacts(Workspace));
    }

    [Fact]
    public void Import_rejects_dtd_entity_cobertura_and_writes_nothing()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(
            root,
            Path.Combine("artifacts", "xxe.xml"),
            """
            <?xml version="1.0"?>
            <!DOCTYPE coverage [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <coverage>
              <packages>
                <package><classes><class filename="&xxe;" /></classes></package>
              </packages>
            </coverage>
            """);

        var ex = Assert.Throws<TestArtifactParseException>(() =>
            CoverageArtifactImporter.Import(store, Request(root, artifact)));

        Assert.Equal("test_artifact.parse_error", ex.Code);
        Assert.Contains("unsafe XML", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.ListRunArtifacts(Workspace));
        Assert.Empty(store.ListCoverageFiles(Workspace));
    }

    [Fact]
    public void Import_rejects_truncated_cobertura_and_writes_nothing()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(root, Path.Combine("artifacts", "trunc.xml"), "<coverage><packages>");

        Assert.Throws<TestArtifactParseException>(() =>
            CoverageArtifactImporter.Import(store, Request(root, artifact)));
        Assert.Empty(store.ListCoverageFiles(Workspace));
        Assert.Empty(store.ListRunArtifacts(Workspace));
    }

    [Fact]
    public void Import_rejects_garbage_lcov_da_line_and_writes_nothing()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(
            root,
            Path.Combine("artifacts", "bad.info"),
            """
            SF:src/service.py
            DA:not-a-number,1
            end_of_record
            """);

        var ex = Assert.Throws<TestArtifactParseException>(() =>
            CoverageArtifactImporter.Import(store, Request(root, artifact)));

        Assert.Equal("test_artifact.parse_error", ex.Code);
        Assert.Empty(store.ListRunArtifacts(Workspace));
        Assert.Empty(store.ListCoverageFiles(Workspace));
    }

    private string WorkspaceRoot()
    {
        var root = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        return root;
    }

    private static CoverageArtifactImportRequest Request(
        string root,
        string artifact,
        string? runId = null,
        string? projectPath = null) =>
        new(
            WorkspaceId: Workspace,
            WorkspaceRoot: root,
            ArtifactPath: artifact,
            IndexIdentity: Fresh.IndexIdentity,
            Revision: Fresh.Revision,
            RunId: runId,
            ProjectPath: projectPath);

    private static string WriteArtifact(string root, string relativePath, string content)
    {
        var artifact = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, content);
        return artifact;
    }

    private static FakeMillerFactSource SeedFacts()
    {
        return new FakeMillerFactSource
        {
            Symbols =
            {
                new CtSymbolFact(
                    "sym:service",
                    "Service",
                    "class",
                    "csharp",
                    "src/service.cs",
                    "sha256:source",
                    1,
                    20,
                    null,
                    false,
                    null),
                new CtSymbolFact(
                    "sym:run",
                    "Run",
                    "method",
                    "csharp",
                    "src/service.cs",
                    "sha256:source",
                    4,
                    8,
                    "sym:service",
                    false,
                    null),
            },
        };
    }

    private static void SeedCoverageTestCase(ContinuousTestStore store) =>
        store.PutTestCase(new ContinuousTestCase(
            Id: "test:coverage",
            WorkspaceId: Workspace,
            Name: "test_covers_service",
            QualifiedName: "Tests.ServiceTests.test_covers_service",
            Selector: "tests/service_tests.cs::test_covers_service",
            Framework: "xunit",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-discovery",
            Confidence: 1.0));

    private static string Sha256(string path)
    {
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
