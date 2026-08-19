using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Analysis;

public sealed class ContinuousTestConfidenceEngineTests : IDisposable
{
    private const string Workspace = "ws:1";
    private static readonly CtFreshnessKey Fresh = new("gen-1", 1);
    private const string SourceHash = "src/payments/service.py";

    private readonly string _dir;
    private readonly string _dbPath;

    public ContinuousTestConfidenceEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-confidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, CtSchema.DbFileName);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Verified_requires_passed_result_and_coverage_or_direct_link()
    {
        using var store = SeedSubjectAndTests();
        PutCoverage(store, "tc:coverage", symbolName: "sym:charge", hits: 1);
        PutResult(store, "tc:coverage", "passed");

        var snapshot = ContinuousTestConfidenceEngine.ConfidenceForSymbol(
            store, Workspace, "sym:charge", Fresh, SourceHash);

        Assert.Equal(ContinuousTestConfidenceState.Verified, snapshot.State);
        Assert.Equal(0.9, snapshot.Score);
        Assert.Equal(ContinuousTestConfidenceState.Verified, store.GetConfidenceSnapshot(snapshot.Id)!.State);
    }

    [Fact]
    public void Covered_without_latest_result_is_covered_not_verified()
    {
        using var store = SeedSubjectAndTests();
        PutCoverage(store, "tc:coverage", symbolName: "sym:charge", hits: 1);

        var snapshot = ContinuousTestConfidenceEngine.ConfidenceForSymbol(
            store, Workspace, "sym:charge", Fresh, SourceHash);

        Assert.Equal(ContinuousTestConfidenceState.Covered, snapshot.State);
    }

    [Fact]
    public void Aggregate_coverage_without_test_case_id_is_covered()
    {
        using var store = SeedSubjectAndTests();
        PutCoverage(store, testCaseId: null, symbolName: "sym:charge", hits: 1);

        var snapshot = ContinuousTestConfidenceEngine.ConfidenceForSymbol(
            store, Workspace, "sym:charge", Fresh, SourceHash);

        Assert.Equal(ContinuousTestConfidenceState.Covered, snapshot.State);
        Assert.Equal(0.72, snapshot.Score);
        Assert.Equal("coverage:src/payments/service.py", snapshot.Evidence[0]["selector"]);
    }

    [Fact]
    public void Failed_latest_result_overrides_coverage()
    {
        using var store = SeedSubjectAndTests();
        PutCoverage(store, "tc:coverage", symbolName: "sym:charge", hits: 1);
        PutResult(store, "tc:coverage", "failed");

        var snapshot = ContinuousTestConfidenceEngine.ConfidenceForSymbol(
            store, Workspace, "sym:charge", Fresh, SourceHash);

        Assert.Equal(ContinuousTestConfidenceState.Failed, snapshot.State);
    }

    [Fact]
    public void Weak_quality_finding_reduces_verified_to_weak()
    {
        using var store = SeedSubjectAndTests();
        PutCoverage(store, "tc:coverage", symbolName: "sym:charge", hits: 1);
        PutResult(store, "tc:coverage", "passed");
        store.PutTestQualityFinding(new ContinuousTestQualityFinding(
            Id: "quality:weak",
            WorkspaceId: Workspace,
            TestCaseId: "tc:coverage",
            FindingType: "no_assertion",
            Severity: "warning",
            Confidence: 0.9,
            Explanation: "test has no assertion",
            Evidence: new Dictionary<string, object?>(),
            FilePath: "tests/test_charge.py",
            SymbolName: "sym:test_coverage"));

        var snapshot = ContinuousTestConfidenceEngine.ConfidenceForSymbol(
            store, Workspace, "sym:charge", Fresh, SourceHash);

        Assert.Equal(ContinuousTestConfidenceState.Weak, snapshot.State);
    }

    [Fact]
    public void Missing_artifacts_returns_unknown_not_untested()
    {
        using var store = SeedSubjectAndTests();

        var snapshot = ContinuousTestConfidenceEngine.ConfidenceForSymbol(
            store, Workspace, "sym:charge", Fresh, SourceHash);

        Assert.Equal(ContinuousTestConfidenceState.Unknown, snapshot.State);
        Assert.Equal("miller tests import-results ws:1 <path>", snapshot.RecommendedCommand);
    }

    [Fact]
    public void No_applicable_evidence_after_search_returns_untested()
    {
        using var store = SeedSubjectAndTests();
        PutResult(store, "tc:coverage", "passed");
        store.DeleteTestCase(Workspace, "tc:path");

        var snapshot = ContinuousTestConfidenceEngine.ConfidenceForSymbol(
            store, Workspace, "sym:charge", Fresh, SourceHash);

        Assert.Equal(ContinuousTestConfidenceState.Untested, snapshot.State);
    }

    [Fact]
    public void Untested_after_results_without_coverage_recommends_coverage_import()
    {
        using var store = SeedSubjectAndTests();
        PutResult(store, "tc:coverage", "passed");
        store.DeleteTestCase(Workspace, "tc:path");

        var snapshot = ContinuousTestConfidenceEngine.ConfidenceForSymbol(
            store, Workspace, "sym:charge", Fresh, SourceHash);

        Assert.Equal(ContinuousTestConfidenceState.Untested, snapshot.State);
        Assert.Equal("miller tests import-coverage ws:1 <path>", snapshot.RecommendedCommand);
    }

    [Fact]
    public void Untested_after_results_and_coverage_has_no_generic_import_recommendation()
    {
        using var store = SeedSubjectAndTests();
        PutResult(store, "tc:coverage", "passed");
        PutCoverage(store, testCaseId: null, symbolName: "sym:unrelated", hits: 1);
        store.DeleteTestCase(Workspace, "tc:path");

        var snapshot = ContinuousTestConfidenceEngine.ConfidenceForSymbol(
            store, Workspace, "sym:charge", Fresh, SourceHash);

        Assert.Equal(ContinuousTestConfidenceState.Untested, snapshot.State);
        Assert.Null(snapshot.RecommendedCommand);
    }

    [Fact]
    public void Stale_hash_returns_stale_with_limitation()
    {
        using var store = SeedSubjectAndTests();
        PutCoverage(store, "tc:coverage", symbolName: "sym:charge", hits: 1);
        PutResult(store, "tc:coverage", "passed");
        store.PutTestLink(new CtTestLink(
            Id: "link:stale",
            WorkspaceId: Workspace,
            Tier: "coverage",
            Confidence: 0.9,
            Explanation: "coverage artifact artifact:coverage covers src/payments/service.py:1",
            TestCaseId: "tc:coverage",
            SourceSymbolName: "sym:charge",
            Metadata: new Dictionary<string, object?> { ["source_hash"] = "sha256:stale" }));

        var snapshot = ContinuousTestConfidenceEngine.ConfidenceForSymbol(
            store, Workspace, "sym:charge", Fresh, SourceHash);

        Assert.Equal(ContinuousTestConfidenceState.Stale, snapshot.State);
        Assert.Equal(["source hash mismatch"], snapshot.Limitations);
    }

    [Fact]
    public void Confidence_for_file_uses_file_path_not_file_id()
    {
        using var store = SeedSubjectAndTests();
        PutCoverage(store, "tc:coverage", symbolName: "sym:charge", hits: 1, filePath: "src/payments/service.py");
        PutResult(store, "tc:coverage", "passed");

        var snapshot = ContinuousTestConfidenceEngine.ConfidenceForFile(
            store, Workspace, "src/payments/service.py", Fresh, SourceHash);

        Assert.Equal(ContinuousTestConfidenceState.Verified, snapshot.State);
        Assert.Equal("file", snapshot.SubjectType);
        Assert.Equal("src/payments/service.py", snapshot.SubjectId);
    }

    private ContinuousTestStore SeedSubjectAndTests()
    {
        var store = new ContinuousTestStore(_dbPath);
        foreach (var (testCaseId, name) in new[]
        {
            ("tc:coverage", "test_coverage"),
            ("tc:result", "test_result"),
            ("tc:explicit", "test_explicit"),
            ("tc:graph", "test_graph"),
            ("tc:identifier", "test_identifier"),
            ("tc:path", "test_path_stem"),
        })
        {
            var path = testCaseId == "tc:path"
                ? "tests/payments/test_service.py"
                : "tests/test_charge.py";
            store.PutTestCase(new ContinuousTestCase(
                Id: testCaseId,
                WorkspaceId: Workspace,
                Name: name,
                QualifiedName: name,
                Selector: $"{path}::{name}",
                FilePath: path,
                ContentHash: path,
                SymbolName: $"sym:{name}",
                SymbolPath: path,
                Framework: "pytest",
                Role: ContinuousTestRole.TestCase,
                Source: "artifact",
                Confidence: 0.8));
        }

        return store;
    }

    private static void PutCoverage(
        ContinuousTestStore store,
        string? testCaseId,
        string symbolName,
        int hits,
        string filePath = "src/payments/service.py")
    {
        store.PutRunArtifact(new ContinuousTestRunArtifact(
            Id: "artifact:coverage",
            WorkspaceId: Workspace,
            Kind: "coverage",
            Path: "coverage/lcov.info"));
        store.PutCoverageFile(new CoverageFile(
            Id: "cov:file",
            WorkspaceId: Workspace,
            IndexIdentity: Fresh.IndexIdentity,
            Revision: Fresh.Revision,
            ArtifactId: "artifact:coverage",
            Format: "lcov",
            Path: filePath,
            Parser: "lcov",
            SourceHash: "sha256:service"));
        store.PutCoverageSpan(new CoverageSpan(
            Id: $"cov:span:{symbolName}:{testCaseId ?? "aggregate"}",
            WorkspaceId: Workspace,
            IndexIdentity: Fresh.IndexIdentity,
            Revision: Fresh.Revision,
            CoverageFileId: "cov:file",
            FilePath: filePath,
            SymbolName: symbolName,
            StartLine: 1,
            EndLine: 1,
            Hits: hits,
            Metadata: testCaseId is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["test_case_id"] = testCaseId }));
    }

    private static void PutResult(ContinuousTestStore store, string testCaseId, string status)
    {
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: "run:latest",
                WorkspaceId: Workspace,
                Status: status,
                SelectedRevision: "1",
                IndexIdentity: Fresh.IndexIdentity,
                Revision: Fresh.Revision,
                Framework: "pytest",
                StartedAt: DateTimeOffset.UtcNow),
            [testCaseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: Workspace,
            TestRunId: "run:latest",
            SelectedRevision: "1",
            CurrentRevision: "1",
            IndexIdentity: Fresh.IndexIdentity,
            Revision: Fresh.Revision,
            Status: status,
            EndedAt: DateTimeOffset.UtcNow,
            Results:
            [
                new ContinuousTestResult(
                    Id: $"result:{testCaseId}:{status}",
                    WorkspaceId: Workspace,
                    TestCaseId: testCaseId,
                    TestRunId: "run:latest",
                    Status: status,
                    ResultRevision: "1",
                    IndexIdentity: Fresh.IndexIdentity,
                    Revision: Fresh.Revision),
            ]));
    }
}
