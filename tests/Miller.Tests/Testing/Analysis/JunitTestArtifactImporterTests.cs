using Miller.Testing;
using Miller.Testing.Parsing;
using Xunit;

namespace Miller.Tests.Testing.Analysis;

public sealed class JunitTestArtifactImporterTests : IDisposable
{
    private const string Workspace = "ws:1";
    private static readonly CtFreshnessKey Fresh = new("gen-1", 1);

    private readonly string _dir;
    private readonly string _dbPath;

    public JunitTestArtifactImporterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-junit-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, CtSchema.DbFileName);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Import_writes_artifact_cases_results_and_current_statuses()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteJunitArtifact(root);

        var report = JunitTestArtifactImporter.Import(store, Request(root, artifact));

        Assert.Equal("test_results", report.Kind);
        Assert.Equal("junit", report.Parser);
        Assert.Equal("failed", report.State);
        Assert.Equal(1, report.Counts["artifacts"]);
        Assert.Equal(1, report.Counts["suites"]);
        Assert.Equal(3, report.Counts["cases"]);
        Assert.Equal(3, report.Counts["results"]);
        Assert.Equal("artifacts/junit.xml", report.ArtifactPath);

        Assert.Single(store.ListRunArtifacts(Workspace));
        Assert.Single(store.ListTestRuns(Workspace));
        Assert.Equal(3, store.ListTestCases(Workspace).Count);
        Assert.Equal(3, store.ListTestResults(Workspace).Count);
        Assert.Equal(report.ArtifactId, Assert.Single(store.ListTestRuns(Workspace)).ArtifactId);
        Assert.Equal(3, store.ListTestResults(Workspace).Count(row => row.SourceArtifactId == report.ArtifactId));

        var cases = store.ListTestCases(Workspace);
        Assert.Equal(
            [
                "tests/test_billing::test_charge_card",
                "tests/test_billing::test_declined_card",
                "tests/test_billing::test_refund_card",
            ],
            cases.Select(row => row.Selector).ToArray());
        Assert.All(cases, row =>
        {
            Assert.Equal("artifact", row.Source);
            Assert.Equal(ContinuousTestRole.TestCase, row.Role);
            Assert.Equal(0.75, row.Confidence);
        });

        var caseNames = cases.ToDictionary(row => row.Id, row => row.Name, StringComparer.Ordinal);
        var statuses = store.ListContinuousTestStatuses(Workspace);
        Assert.Equal(
            [
                ("test_charge_card", ContinuousTestState.Green),
                ("test_declined_card", ContinuousTestState.Red),
                ("test_refund_card", ContinuousTestState.Skipped),
            ],
            statuses
                .Select(status => (caseNames[status.TestCaseId], status.State))
                .OrderBy(row => row.Item1, StringComparer.Ordinal)
                .ToArray());
        var red = statuses.Single(status => status.State == ContinuousTestState.Red);
        Assert.Equal("AssertionError: card declined", red.FailureSummary);
        Assert.Equal("1", red.LastRunRevision);
    }

    [Fact]
    public void Import_is_idempotent_by_artifact_hash()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteJunitArtifact(root);

        var first = JunitTestArtifactImporter.Import(store, Request(root, artifact));
        var second = JunitTestArtifactImporter.Import(store, Request(root, artifact));

        Assert.Equal(first.ArtifactId, second.ArtifactId);
        Assert.Equal(first.TestRunId, second.TestRunId);
        Assert.Single(store.ListRunArtifacts(Workspace));
        Assert.Single(store.ListTestRuns(Workspace));
        Assert.Equal(3, store.ListTestCases(Workspace).Count);
        Assert.Equal(3, store.ListTestResults(Workspace).Count);
    }

    [Fact]
    public void Import_reconciles_parsed_cases_to_existing_provider_test_case_ids()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteSingleJunitArtifactAt(root, Path.Combine("artifacts", "junit.xml"));
        store.PutTestCase(new ContinuousTestCase(
            Id: "provider:test:1",
            WorkspaceId: Workspace,
            Name: "test_charge_card",
            QualifiedName: "tests.test_billing.test_charge_card",
            Selector: "-id xunit-id-1",
            Framework: "xunit",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:dotnet",
            Confidence: 1.0,
            Metadata: new Dictionary<string, object?>
            {
                ["class"] = "tests.test_billing",
                ["method"] = "test_charge_card",
            }));

        var report = JunitTestArtifactImporter.Import(
            store,
            Request(
                root,
                artifact,
                runId: "run:provider",
                testCaseIdsBySelector: new Dictionary<string, string>
                {
                    ["tests/test_billing::test_charge_card"] = "provider:test:1",
                }));

        Assert.Equal("run:provider", report.TestRunId);
        var testCase = Assert.Single(store.ListTestCases(Workspace));
        Assert.Equal("ct-provider:dotnet", testCase.Source);
        Assert.Equal(1.0, testCase.Confidence);
        var result = Assert.Single(store.ListTestResults(Workspace));
        Assert.Equal("provider:test:1", result.TestCaseId);
        Assert.Equal(report.ArtifactId, result.SourceArtifactId);
        Assert.Equal("passed", store.ListContinuousTestStatuses(Workspace).Single().LastResultStatus);
    }

    [Fact]
    public void Import_rejects_artifacts_outside_workspace_root()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var outside = WriteJunitArtifactAt(_dir, "outside-junit.xml");

        var ex = Assert.Throws<ArgumentException>(() =>
            JunitTestArtifactImporter.Import(store, Request(root, outside)));

        Assert.Equal("artifactPath", ex.ParamName);
        Assert.Empty(store.ListRunArtifacts(Workspace));
    }

    [Fact]
    public void Import_allows_artifact_names_that_start_with_two_dots_inside_workspace_root()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteJunitArtifactAt(root, "..junit.xml");

        var report = JunitTestArtifactImporter.Import(store, Request(root, artifact));

        Assert.Equal("..junit.xml", report.ArtifactPath);
        Assert.Equal(3, store.ListTestResults(Workspace).Count);
    }

    [Fact]
    public void Import_rejects_dtd_entity_payload_and_writes_nothing()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(
            root,
            Path.Combine("artifacts", "xxe.xml"),
            """
            <?xml version="1.0"?>
            <!DOCTYPE testsuite [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <testsuite name="pytest"><testcase name="&xxe;" /></testsuite>
            """);

        var ex = Assert.Throws<TestArtifactParseException>(() =>
            JunitTestArtifactImporter.Import(store, Request(root, artifact)));

        Assert.Equal("test_artifact.parse_error", ex.Code);
        Assert.Contains("unsafe XML", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.ListRunArtifacts(Workspace));
        Assert.Empty(store.ListTestCases(Workspace));
        Assert.Empty(store.ListTestResults(Workspace));
    }

    [Fact]
    public void Import_rejects_truncated_xml_and_writes_nothing()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(root, Path.Combine("artifacts", "trunc.xml"), "<testsuite><testcase name=\"oops\"");

        var ex = Assert.Throws<TestArtifactParseException>(() =>
            JunitTestArtifactImporter.Import(store, Request(root, artifact)));

        Assert.Contains("malformed XML", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.ListRunArtifacts(Workspace));
        Assert.Empty(store.ListTestResults(Workspace));
    }

    [Fact]
    public void Import_rejects_garbage_payload_and_writes_nothing()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(root, Path.Combine("artifacts", "junk.xml"), "this is not xml at all <<<");

        Assert.Throws<TestArtifactParseException>(() =>
            JunitTestArtifactImporter.Import(store, Request(root, artifact)));
        Assert.Empty(store.ListRunArtifacts(Workspace));
        Assert.Empty(store.ListTestCases(Workspace));
    }

    private string WorkspaceRoot()
    {
        var root = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        return root;
    }

    private static JunitTestArtifactImportRequest Request(
        string root,
        string artifact,
        string? runId = null,
        IReadOnlyDictionary<string, string>? testCaseIdsBySelector = null) =>
        new(
            WorkspaceId: Workspace,
            WorkspaceRoot: root,
            ArtifactPath: artifact,
            SelectedRevision: "1",
            IndexIdentity: Fresh.IndexIdentity,
            Revision: Fresh.Revision,
            RunId: runId,
            TestCaseIdsBySelector: testCaseIdsBySelector);

    private static string WriteJunitArtifact(string root) =>
        WriteJunitArtifactAt(root, Path.Combine("artifacts", "junit.xml"));

    private static string WriteSingleJunitArtifactAt(string root, string relativePath) =>
        WriteArtifact(
            root,
            relativePath,
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuite name="pytest" tests="1">
              <testcase classname="tests.test_billing" name="test_charge_card" time="0.041" />
            </testsuite>
            """);

    private static string WriteJunitArtifactAt(string root, string relativePath) =>
        WriteArtifact(
            root,
            relativePath,
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuite name="pytest" tests="3" failures="1" skipped="1" time="0.126">
              <testcase classname="tests.test_billing" name="test_charge_card" time="0.041" />
              <testcase classname="tests.test_billing" name="test_declined_card" time="0.052">
                <failure message="assert False">AssertionError: card declined</failure>
              </testcase>
              <testcase classname="tests.test_billing" name="test_refund_card" time="0.033">
                <skipped message="not implemented" />
              </testcase>
            </testsuite>
            """);

    private static string WriteArtifact(string root, string relativePath, string content)
    {
        var artifact = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, content);
        return artifact;
    }
}
