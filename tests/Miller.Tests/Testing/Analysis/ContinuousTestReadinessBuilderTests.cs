using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Analysis;

public sealed class ContinuousTestReadinessBuilderTests : IDisposable
{
    private const string Workspace = "ws:1";
    private static readonly CtFreshnessKey Fresh = new("gen-1", 1);

    private readonly string _dir;
    private readonly string _dbPath;

    public ContinuousTestReadinessBuilderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, CtSchema.DbFileName);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Missing_artifacts_returns_unknown_with_import_actions()
    {
        using var store = new ContinuousTestStore(_dbPath);

        var readiness = ContinuousTestReadinessBuilder.BuildTestConfidenceReadiness(store, Workspace);

        Assert.Equal("unknown_no_artifacts", readiness.State);
        Assert.Equal("missing", readiness.ArtifactReadiness.TestResults);
        Assert.Equal("missing", readiness.ArtifactReadiness.Coverage);
        Assert.Equal(0, readiness.ArtifactCounts["test_results"]);
        Assert.Equal(0, readiness.ArtifactCounts["coverage_spans"]);
        Assert.Equal(0, readiness.ConfidenceCounts["verified"]);
        Assert.Equal(["import_test_results", "import_coverage"], readiness.Actions.Select(action => action.Code));
        Assert.All(readiness.Actions, action => Assert.StartsWith("miller tests ", action.Command, StringComparison.Ordinal));
    }

    [Fact]
    public void Ready_when_results_and_coverage_are_present_without_attention()
    {
        using var store = SeedSubjectAndTestCase();
        PutResultArtifact(store);
        PutCoverage(store);
        PutResult(store, "passed", runId: "run:passed", observedAt: new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero));
        PutConfidenceSnapshot(store, state: "verified");

        var readiness = ContinuousTestReadinessBuilder.BuildTestConfidenceReadiness(store, Workspace);

        Assert.Equal("ready", readiness.State);
        Assert.Equal("ready", readiness.ArtifactReadiness.TestResults);
        Assert.Equal("ready", readiness.ArtifactReadiness.Coverage);
        Assert.Equal(1, readiness.ArtifactCounts["result_artifacts"]);
        Assert.Equal(1, readiness.ArtifactCounts["coverage_artifacts"]);
        Assert.Equal(1, readiness.ConfidenceCounts["verified"]);
        Assert.StartsWith("2026-06-16T10:00:00", readiness.LatestResultAt);
        Assert.StartsWith("2026-06-16T09:00:00", readiness.LatestCoverageAt);
        Assert.Empty(readiness.Actions);
    }

    [Fact]
    public void Parser_diagnostics_take_precedence_after_artifacts()
    {
        using var store = SeedSubjectAndTestCase();
        PutResultArtifact(store);
        PutCoverage(store);
        PutResult(store, "passed");
        store.PutParserDiagnostic(
            Workspace,
            new ContinuousTestParserDiagnostic(
                Code: "test_artifact.coverage_parse",
                Message: "could not parse coverage",
                Severity: "error"));

        var readiness = ContinuousTestReadinessBuilder.BuildTestConfidenceReadiness(store, Workspace);

        Assert.Equal("artifact_diagnostics", readiness.State);
        var diagnostic = Assert.Single(readiness.ParserDiagnostics);
        Assert.Equal("test_artifact.coverage_parse", diagnostic.Code);
        Assert.Equal("could not parse coverage", diagnostic.Message);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal(1, readiness.ArtifactReadiness.Diagnostics);
    }

    [Fact]
    public void Flaky_tests_take_precedence_over_attention()
    {
        using var store = SeedSubjectAndTestCase();
        PutResultArtifact(store);
        PutCoverage(store);
        PutResult(store, "passed", runId: "run:1", observedAt: new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero));
        PutResult(store, "failed", runId: "run:2", observedAt: new DateTimeOffset(2026, 6, 16, 10, 1, 0, TimeSpan.Zero));
        PutResult(store, "passed", runId: "run:3", observedAt: new DateTimeOffset(2026, 6, 16, 10, 2, 0, TimeSpan.Zero));
        PutResult(store, "failed", runId: "run:4", observedAt: new DateTimeOffset(2026, 6, 16, 10, 3, 0, TimeSpan.Zero));
        PutConfidenceSnapshot(store, state: "failed");

        var readiness = ContinuousTestReadinessBuilder.BuildTestConfidenceReadiness(store, Workspace);

        Assert.Equal("flaky_tests", readiness.State);
        Assert.Equal(1, readiness.Flakiness.Counts["flaky"]);
        var test = Assert.Single(readiness.Flakiness.Tests);
        Assert.Equal("tc:charge", test.TestCaseId);
        Assert.Equal("tests/test_charge.py::test_charge", test.Selector);
        Assert.Equal(ContinuousTestFlakinessState.Flaky, test.Score.State);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("weak")]
    public void Failed_or_weak_confidence_yields_attention(string state)
    {
        using var store = SeedSubjectAndTestCase();
        PutResultArtifact(store);
        PutCoverage(store);
        PutResult(store, "passed");
        PutConfidenceSnapshot(store, state);

        var readiness = ContinuousTestReadinessBuilder.BuildTestConfidenceReadiness(store, Workspace);

        Assert.Equal("attention", readiness.State);
        Assert.Equal(1, readiness.ConfidenceCounts[state]);
    }

    [Fact]
    public void Untested_confidence_after_artifacts_is_visible()
    {
        using var store = SeedSubjectAndTestCase();
        PutResultArtifact(store);
        PutCoverage(store);
        PutResult(store, "passed");
        PutConfidenceSnapshot(store, state: "untested");

        var readiness = ContinuousTestReadinessBuilder.BuildTestConfidenceReadiness(store, Workspace);

        Assert.Equal("untested_after_artifacts", readiness.State);
        Assert.Equal(1, readiness.ConfidenceCounts["untested"]);
    }

    [Fact]
    public void Partial_artifacts_return_partial_with_missing_action()
    {
        using var store = SeedSubjectAndTestCase();
        PutResultArtifact(store);
        PutResult(store, "passed");

        var readiness = ContinuousTestReadinessBuilder.BuildTestConfidenceReadiness(store, Workspace);

        Assert.Equal("partial_artifacts", readiness.State);
        Assert.Equal("ready", readiness.ArtifactReadiness.TestResults);
        Assert.Equal("missing", readiness.ArtifactReadiness.Coverage);
        var action = Assert.Single(readiness.Actions);
        Assert.Equal("import_coverage", action.Code);
        Assert.Equal("miller tests import-coverage ws:1 <path>", action.Command);
    }

    [Fact]
    public void Quality_counts_are_reported_without_changing_state()
    {
        using var store = SeedSubjectAndTestCase();
        PutResultArtifact(store);
        PutCoverage(store);
        PutResult(store, "passed");
        store.PutTestQualityFinding(new ContinuousTestQualityFinding(
            Id: "quality:weak",
            WorkspaceId: Workspace,
            TestCaseId: "tc:charge",
            FindingType: "no_assertion",
            Severity: "warning",
            Confidence: 0.9,
            Explanation: "test has no assertion",
            Evidence: new Dictionary<string, object?>()));
        store.PutImplementationQualityFinding(new ContinuousImplementationQualityFinding(
            Id: "quality:stub",
            WorkspaceId: Workspace,
            FindingType: "stub_implementation",
            Severity: "warning",
            Confidence: 0.9,
            Explanation: "implementation is a stub",
            Evidence: new Dictionary<string, object?>(),
            FilePath: "src/service.py"));

        var readiness = ContinuousTestReadinessBuilder.BuildTestConfidenceReadiness(store, Workspace);

        Assert.Equal("ready", readiness.State);
        Assert.Equal(1, readiness.QualityCounts.WeakTests);
        Assert.Equal(1, readiness.QualityCounts.Stubs);
    }

    private ContinuousTestStore SeedSubjectAndTestCase()
    {
        var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(new ContinuousTestCase(
            Id: "tc:charge",
            WorkspaceId: Workspace,
            Name: "test_charge",
            QualifiedName: "test_charge",
            Selector: "tests/test_charge.py::test_charge",
            FilePath: "tests/test_charge.py",
            Framework: "pytest",
            Role: ContinuousTestRole.TestCase,
            Source: "artifact",
            Confidence: 0.9));
        return store;
    }

    private static void PutResultArtifact(ContinuousTestStore store) =>
        store.PutRunArtifact(new ContinuousTestRunArtifact(
            Id: "artifact:results",
            WorkspaceId: Workspace,
            Kind: "test_results",
            Path: "artifacts/junit.xml"));

    private static void PutCoverage(ContinuousTestStore store)
    {
        store.PutRunArtifact(new ContinuousTestRunArtifact(
            Id: "artifact:coverage",
            WorkspaceId: Workspace,
            Kind: "coverage",
            Path: "artifacts/lcov.info"));
        store.PutCoverageFile(new CoverageFile(
            Id: "coverage:file",
            WorkspaceId: Workspace,
            IndexIdentity: Fresh.IndexIdentity,
            Revision: Fresh.Revision,
            Format: "lcov",
            Path: "src/service.py",
            Parser: "lcov",
            SourceHash: "sha256:service",
            ArtifactId: "artifact:coverage",
            GeneratedAt: new DateTimeOffset(2026, 6, 16, 9, 0, 0, TimeSpan.Zero)));
        store.PutCoverageSpan(new CoverageSpan(
            Id: "coverage:span",
            WorkspaceId: Workspace,
            IndexIdentity: Fresh.IndexIdentity,
            Revision: Fresh.Revision,
            CoverageFileId: "coverage:file",
            StartLine: 1,
            EndLine: 3,
            Hits: 3,
            FilePath: "src/service.py",
            Metadata: new Dictionary<string, object?> { ["test_case_id"] = "tc:charge" }));
    }

    private static void PutResult(
        ContinuousTestStore store,
        string status,
        string runId = "run:latest",
        DateTimeOffset? observedAt = null)
    {
        var timestamp = observedAt ?? new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero);
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: Workspace,
                Status: "running",
                SelectedRevision: "1",
                IndexIdentity: Fresh.IndexIdentity,
                Revision: Fresh.Revision,
                Framework: "pytest",
                StartedAt: timestamp),
            ["tc:charge"]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: Workspace,
            TestRunId: runId,
            SelectedRevision: "1",
            CurrentRevision: "1",
            IndexIdentity: Fresh.IndexIdentity,
            Revision: Fresh.Revision,
            Status: status,
            EndedAt: timestamp,
            Results:
            [
                new ContinuousTestResult(
                    Id: $"result:{runId}",
                    WorkspaceId: Workspace,
                    TestCaseId: "tc:charge",
                    TestRunId: runId,
                    Status: status,
                    ResultRevision: "1",
                    IndexIdentity: Fresh.IndexIdentity,
                    Revision: Fresh.Revision),
            ]));
    }

    private static void PutConfidenceSnapshot(ContinuousTestStore store, string state)
    {
        store.PutConfidenceSnapshot(new ContinuousTestConfidenceSnapshot(
            Id: $"confidence:{state}",
            WorkspaceId: Workspace,
            SubjectType: "symbol",
            SubjectId: "sym:charge",
            State: Enum.Parse<ContinuousTestConfidenceState>(state, ignoreCase: true),
            Score: 0.5,
            Evidence: [],
            Freshness: new Dictionary<string, object?>(),
            Limitations: [],
            RecommendedCommand: null,
            IndexIdentity: Fresh.IndexIdentity,
            Revision: Fresh.Revision));
    }
}
