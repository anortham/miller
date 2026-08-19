namespace Miller.Testing;

public sealed record ContinuousTestArtifactReadiness(
    string TestResults,
    string Coverage,
    int Diagnostics);

public sealed record ContinuousTestParserDiagnostic(
    string? Code,
    string Message,
    string Severity);

public sealed record ContinuousTestFlakinessTest(
    string TestCaseId,
    string Selector,
    ContinuousTestFlakinessScore Score);

public sealed record ContinuousTestFlakinessSummary(
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<ContinuousTestFlakinessTest> Tests);

public sealed record ContinuousTestQualityCounts(
    int WeakTests,
    int Stubs);

public sealed record ContinuousTestReadinessAction(
    string Code,
    string Command,
    string Reason);

public sealed record ContinuousTestReadiness(
    string State,
    ContinuousTestArtifactReadiness ArtifactReadiness,
    IReadOnlyDictionary<string, int> ArtifactCounts,
    string? LatestResultAt,
    string? LatestCoverageAt,
    IReadOnlyList<ContinuousTestParserDiagnostic> ParserDiagnostics,
    IReadOnlyDictionary<string, int> ConfidenceCounts,
    ContinuousTestFlakinessSummary Flakiness,
    ContinuousTestQualityCounts QualityCounts,
    IReadOnlyList<ContinuousTestReadinessAction> Actions);

public static class ContinuousTestReadinessBuilder
{
    private static readonly string[] FlakinessStates =
    [
        "stable",
        "flaky",
        "consistently_failing",
        "unknown",
    ];

    public static ContinuousTestReadiness BuildTestConfidenceReadiness(
        ContinuousTestStore store,
        string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        IReadOnlyDictionary<string, int> artifactCounts = store.CountArtifacts(workspaceId);
        IReadOnlyDictionary<string, int> confidenceCounts = store.CountConfidenceStates(workspaceId);
        ContinuousTestQualityCounts qualityCounts = store.CountQualityFindings(workspaceId);
        IReadOnlyList<ContinuousTestParserDiagnostic> parserDiagnostics = store.ListParserDiagnostics(workspaceId);
        ContinuousTestFlakinessSummary flakiness = FlakinessSummary(store, workspaceId);
        string state = State(artifactCounts, confidenceCounts, parserDiagnostics, flakiness);

        return new ContinuousTestReadiness(
            State: state,
            ArtifactReadiness: new ContinuousTestArtifactReadiness(
                TestResults: artifactCounts["test_results"] > 0 ? "ready" : "missing",
                Coverage: artifactCounts["coverage_spans"] > 0 ? "ready" : "missing",
                Diagnostics: artifactCounts["diagnostics"]),
            ArtifactCounts: artifactCounts,
            LatestResultAt: store.LatestTestRunAt(workspaceId),
            LatestCoverageAt: store.LatestCoverageGeneratedAt(workspaceId),
            ParserDiagnostics: parserDiagnostics,
            ConfidenceCounts: confidenceCounts,
            Flakiness: flakiness,
            QualityCounts: qualityCounts,
            Actions: Actions(workspaceId, artifactCounts));
    }

    private static ContinuousTestFlakinessSummary FlakinessSummary(ContinuousTestStore store, string workspaceId)
    {
        var histories = new Dictionary<string, List<ContinuousTestOutcome>>(StringComparer.Ordinal);
        var selectors = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset syntheticTimestamp = DateTimeOffset.UnixEpoch;

        foreach (CtTestResultHistoryRow row in store.ListTestResultHistories(workspaceId))
        {
            selectors[row.TestCaseId] = row.Selector;
            if (!histories.TryGetValue(row.TestCaseId, out List<ContinuousTestOutcome>? outcomes))
            {
                outcomes = [];
                histories[row.TestCaseId] = outcomes;
            }

            outcomes.Add(new ContinuousTestOutcome(
                Status: row.Status,
                ObservedAt: row.ObservedAt ?? syntheticTimestamp));
            syntheticTimestamp = syntheticTimestamp.AddSeconds(1);
        }

        Dictionary<string, int> counts = FlakinessStates.ToDictionary(state => state, _ => 0, StringComparer.Ordinal);
        var tests = new List<ContinuousTestFlakinessTest>();
        foreach ((string testCaseId, List<ContinuousTestOutcome> history) in histories
            .OrderBy(item => selectors.GetValueOrDefault(item.Key, item.Key), StringComparer.Ordinal))
        {
            ContinuousTestFlakinessScore score = ContinuousTestFlakiness.Score(history.TakeLast(ContinuousTestFlakiness.MaxHistory));
            string state = FlakinessStateValue(score.State);
            counts[state]++;
            if (!string.Equals(state, "stable", StringComparison.Ordinal))
            {
                tests.Add(new ContinuousTestFlakinessTest(
                    TestCaseId: testCaseId,
                    Selector: selectors.GetValueOrDefault(testCaseId, testCaseId),
                    Score: score));
            }
        }

        return new ContinuousTestFlakinessSummary(counts, tests);
    }

    private static string State(
        IReadOnlyDictionary<string, int> artifactCounts,
        IReadOnlyDictionary<string, int> confidenceCounts,
        IReadOnlyList<ContinuousTestParserDiagnostic> parserDiagnostics,
        ContinuousTestFlakinessSummary flakiness)
    {
        bool hasResults = artifactCounts["test_results"] > 0;
        bool hasCoverage = artifactCounts["coverage_spans"] > 0;
        if (!hasResults && !hasCoverage)
            return "unknown_no_artifacts";
        if (parserDiagnostics.Count > 0)
            return "artifact_diagnostics";
        if (flakiness.Counts["flaky"] > 0)
            return "flaky_tests";
        if (confidenceCounts.GetValueOrDefault("failed") > 0 || confidenceCounts.GetValueOrDefault("weak") > 0)
            return "attention";
        if (confidenceCounts.GetValueOrDefault("untested") > 0)
            return "untested_after_artifacts";
        if (hasResults && hasCoverage)
            return "ready";
        return "partial_artifacts";
    }

    private static IReadOnlyList<ContinuousTestReadinessAction> Actions(
        string workspaceId,
        IReadOnlyDictionary<string, int> artifactCounts)
    {
        var actions = new List<ContinuousTestReadinessAction>();
        if (artifactCounts["test_results"] == 0)
        {
            actions.Add(new ContinuousTestReadinessAction(
                Code: "import_test_results",
                Command: $"miller tests import-results {workspaceId} <path>",
                Reason: "test result artifacts are missing"));
        }

        if (artifactCounts["coverage_spans"] == 0)
        {
            actions.Add(new ContinuousTestReadinessAction(
                Code: "import_coverage",
                Command: $"miller tests import-coverage {workspaceId} <path>",
                Reason: "coverage artifacts are missing"));
        }

        return actions;
    }

    private static string FlakinessStateValue(ContinuousTestFlakinessState state) => state switch
    {
        ContinuousTestFlakinessState.Stable => "stable",
        ContinuousTestFlakinessState.Flaky => "flaky",
        ContinuousTestFlakinessState.ConsistentlyFailing => "consistently_failing",
        ContinuousTestFlakinessState.Unknown => "unknown",
        _ => "unknown",
    };
}
