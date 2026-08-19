namespace Miller.Testing;

public enum ContinuousTestConfidenceState
{
    Verified,
    Covered,
    Likely,
    Weak,
    Failed,
    Untested,
    Unknown,
    Stale,
}

public sealed record ContinuousTestConfidenceSnapshot(
    string Id,
    string WorkspaceId,
    string SubjectType,
    string SubjectId,
    ContinuousTestConfidenceState State,
    double Score,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Evidence,
    IReadOnlyDictionary<string, object?> Freshness,
    IReadOnlyList<string> Limitations,
    string? RecommendedCommand,
    string IndexIdentity,
    long Revision);

public static class ContinuousTestConfidenceEngine
{
    internal static readonly string[] StateNames =
    [
        "verified",
        "covered",
        "likely",
        "weak",
        "failed",
        "untested",
        "unknown",
        "stale",
    ];

    public static ContinuousTestConfidenceSnapshot ConfidenceForSymbol(
        ContinuousTestStore store,
        string workspaceId,
        string symbolId,
        CtFreshnessKey freshness,
        string sourceHash)
    {
        ArgumentNullException.ThrowIfNull(store);
        IReadOnlyList<ConfidenceEvidenceRow> rows = EvidenceForSymbol(store, workspaceId, symbolId);
        ContinuousTestConfidenceSnapshot snapshot = SnapshotForSubject(
            store,
            workspaceId,
            subjectType: "symbol",
            subjectId: symbolId,
            rows,
            sourceHash,
            freshness);
        store.PutConfidenceSnapshot(snapshot);
        return snapshot;
    }

    public static ContinuousTestConfidenceSnapshot ConfidenceForFile(
        ContinuousTestStore store,
        string workspaceId,
        string filePath,
        CtFreshnessKey freshness,
        string sourceHash)
    {
        ArgumentNullException.ThrowIfNull(store);
        IReadOnlyList<ConfidenceEvidenceRow> rows = EvidenceForFile(store, workspaceId, filePath);
        ContinuousTestConfidenceSnapshot snapshot = SnapshotForSubject(
            store,
            workspaceId,
            subjectType: "file",
            subjectId: filePath,
            rows,
            sourceHash,
            freshness);
        store.PutConfidenceSnapshot(snapshot);
        return snapshot;
    }

    public static IReadOnlyList<ContinuousTestConfidenceSnapshot> ConfidenceForChange(
        ContinuousTestStore store,
        string workspaceId,
        IReadOnlyList<string> changedPaths,
        CtFreshnessKey freshness,
        IMillerFactSource facts)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changedPaths);
        ArgumentNullException.ThrowIfNull(facts);

        var snapshots = new List<ContinuousTestConfidenceSnapshot>();
        foreach (string path in changedPaths)
        {
            var symbols = facts.SymbolsForChangedFiles([path]);
            string hash = symbols.Select(row => row.ContentHash).FirstOrDefault(static value => !string.IsNullOrEmpty(value))
                ?? "";
            snapshots.Add(ConfidenceForFile(store, workspaceId, path, freshness, hash));
        }

        return snapshots;
    }

    private static ContinuousTestConfidenceSnapshot SnapshotForSubject(
        ContinuousTestStore store,
        string workspaceId,
        string subjectType,
        string subjectId,
        IReadOnlyList<ConfidenceEvidenceRow> rows,
        string sourceHash,
        CtFreshnessKey freshness)
    {
        IReadOnlyDictionary<string, int> artifactCounts = store.CountArtifacts(workspaceId);
        if (StaleLinkExists(store, workspaceId, subjectType, subjectId, sourceHash))
        {
            return Snapshot(
                workspaceId,
                subjectType,
                subjectId,
                ContinuousTestConfidenceState.Stale,
                rows,
                score: 0.0,
                freshness,
                limitations: ["source hash mismatch"]);
        }

        if (!HasAnyArtifactEvidence(artifactCounts))
        {
            return Snapshot(
                workspaceId,
                subjectType,
                subjectId,
                ContinuousTestConfidenceState.Unknown,
                [],
                Score(ContinuousTestConfidenceState.Unknown),
                freshness,
                recommendedCommand: $"miller tests import-results {workspaceId} <path>");
        }

        if (rows.Count == 0 || !HasApplicableEvidence(rows))
        {
            return Snapshot(
                workspaceId,
                subjectType,
                subjectId,
                ContinuousTestConfidenceState.Untested,
                rows,
                Score(ContinuousTestConfidenceState.Untested),
                freshness,
                recommendedCommand: ArtifactRecommendation(workspaceId, artifactCounts));
        }

        ConfidenceEvidenceRow[] flakyRows = rows.Where(row => IsFlaky(row.Flakiness)).ToArray();
        string[] limitations = flakyRows.Length > 0 ? ["flaky test history"] : [];
        if (rows.Any(row => LatestStatus(row) is "failed" or "error" or "errored"))
        {
            return Snapshot(
                workspaceId,
                subjectType,
                subjectId,
                ContinuousTestConfidenceState.Failed,
                rows,
                Score(ContinuousTestConfidenceState.Failed),
                freshness,
                limitations);
        }

        if (flakyRows.Length > 0)
        {
            return Snapshot(
                workspaceId,
                subjectType,
                subjectId,
                ContinuousTestConfidenceState.Weak,
                rows,
                Score(ContinuousTestConfidenceState.Weak),
                freshness,
                limitations);
        }

        ContinuousTestConfidenceState baseState = BaseState(rows);
        if (rows.Any(row => row.QualityWarnings.Count > 0))
            baseState = ContinuousTestConfidenceState.Weak;

        return Snapshot(workspaceId, subjectType, subjectId, baseState, rows, Score(baseState), freshness);
    }

    private static ContinuousTestConfidenceState BaseState(IReadOnlyList<ConfidenceEvidenceRow> rows)
    {
        bool hasPass = rows.Any(row => LatestStatus(row) == "passed");
        bool hasCoverageOrLink = rows.Any(row =>
            row.Coverage is not null ||
            row.Tier is "coverage" or "explicit_linkage" or "test_result");
        if (hasPass && hasCoverageOrLink)
            return ContinuousTestConfidenceState.Verified;

        if (rows.Any(row => row.Coverage is not null || row.Tier is "coverage" or "explicit_linkage"))
            return ContinuousTestConfidenceState.Covered;

        return ContinuousTestConfidenceState.Likely;
    }

    private static bool HasApplicableEvidence(IReadOnlyList<ConfidenceEvidenceRow> rows) =>
        rows.Any(row => row.TestCaseId is not null || row.LatestResult is not null || row.Coverage is not null);

    private static ContinuousTestConfidenceSnapshot Snapshot(
        string workspaceId,
        string subjectType,
        string subjectId,
        ContinuousTestConfidenceState state,
        IReadOnlyList<ConfidenceEvidenceRow> rows,
        double score,
        CtFreshnessKey freshness,
        IReadOnlyList<string>? limitations = null,
        string? recommendedCommand = null)
    {
        int flakyCount = rows.Count(row => IsFlaky(row.Flakiness));
        return new ContinuousTestConfidenceSnapshot(
            Id: CtStableIds.StableId("confidence_snapshot", workspaceId, subjectType, subjectId),
            WorkspaceId: workspaceId,
            SubjectType: subjectType,
            SubjectId: subjectId,
            State: state,
            Score: score,
            Evidence: rows.Select(EvidenceRow).ToArray(),
            Freshness: new Dictionary<string, object?>
            {
                ["latest_evidence_count"] = rows.Count,
                ["flaky_test_count"] = flakyCount,
            },
            Limitations: limitations ?? [],
            RecommendedCommand: recommendedCommand,
            IndexIdentity: freshness.IndexIdentity,
            Revision: freshness.Revision);
    }

    private static Dictionary<string, object?> EvidenceRow(ConfidenceEvidenceRow row) =>
        new()
        {
            ["selector"] = row.Selector,
            ["path"] = row.Path,
            ["tier"] = row.Tier,
            ["confidence"] = row.Confidence,
            ["explanation"] = row.Explanation,
            ["source_fact_ids"] = row.SourceFactIds,
            ["latest_result"] = row.LatestResult,
            ["flakiness"] = row.Flakiness,
            ["coverage"] = row.Coverage,
            ["quality_warnings"] = row.QualityWarnings,
        };

    private static IReadOnlyList<ConfidenceEvidenceRow> EvidenceForSymbol(
        ContinuousTestStore store,
        string workspaceId,
        string symbolId)
    {
        var rows = new List<ConfidenceEvidenceRow>();
        rows.AddRange(CoverageEvidence(store, workspaceId, symbolName: symbolId, filePath: null));
        rows.AddRange(TestLinkEvidence(store, workspaceId, sourceSymbolName: symbolId, sourceFilePath: null));
        return rows
            .OrderByDescending(row => row.Confidence)
            .ThenBy(row => row.Selector, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ConfidenceEvidenceRow> EvidenceForFile(
        ContinuousTestStore store,
        string workspaceId,
        string filePath)
    {
        var rows = new List<ConfidenceEvidenceRow>();
        rows.AddRange(CoverageEvidence(store, workspaceId, symbolName: null, filePath: filePath));
        rows.AddRange(TestLinkEvidence(store, workspaceId, sourceSymbolName: null, sourceFilePath: filePath));
        return rows
            .OrderByDescending(row => row.Confidence)
            .ThenBy(row => row.Selector, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<ConfidenceEvidenceRow> CoverageEvidence(
        ContinuousTestStore store,
        string workspaceId,
        string? symbolName,
        string? filePath)
    {
        string[] symbols = string.IsNullOrEmpty(symbolName) ? [] : [symbolName];
        string[] paths = string.IsNullOrEmpty(filePath) ? [] : [filePath];
        foreach (CoverageSpan span in store.ListCoverageSpansCovering(workspaceId, symbols, paths))
        {
            string path = span.FilePath ?? "";
            string? testCaseId = span.Metadata.TryGetValue("test_case_id", out object? rawTestCaseId)
                ? Convert.ToString(rawTestCaseId)
                : null;
            ContinuousTestCase? testCase = testCaseId is null ? null : store.GetTestCase(workspaceId, testCaseId);
            string selector = testCase?.Selector ?? $"coverage:{path}";
            int coveredLines = Math.Max(1, span.EndLine - span.StartLine + 1);
            yield return new ConfidenceEvidenceRow(
                Selector: selector,
                Path: path,
                Tier: "coverage",
                Confidence: testCase is null ? 0.72 : 0.9,
                Explanation: $"coverage artifact covers {path}:{span.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                SourceFactIds: [span.Id],
                TestCaseId: testCaseId,
                LatestResult: testCaseId is null ? null : LatestResult(store, workspaceId, testCaseId),
                Flakiness: null,
                Coverage: new Dictionary<string, object?>
                {
                    ["covered_lines"] = coveredLines,
                    ["hit_lines"] = coveredLines,
                },
                QualityWarnings: testCaseId is null ? [] : QualityWarnings(store, workspaceId, testCaseId));
        }
    }

    private static IEnumerable<ConfidenceEvidenceRow> TestLinkEvidence(
        ContinuousTestStore store,
        string workspaceId,
        string? sourceSymbolName,
        string? sourceFilePath)
    {
        foreach (CtTestLink link in store.ListTestLinks(workspaceId, sourceSymbolName, sourceFilePath))
        {
            ContinuousTestCase? testCase = link.TestCaseId is null ? null : store.GetTestCase(workspaceId, link.TestCaseId);
            string selector = testCase?.Selector ?? $"test-link:{link.Id}";
            yield return new ConfidenceEvidenceRow(
                Selector: selector,
                Path: testCase?.FilePath ?? link.SourceFilePath,
                Tier: link.Tier,
                Confidence: link.Confidence,
                Explanation: link.Explanation,
                SourceFactIds: link.SourceFactIds,
                TestCaseId: link.TestCaseId,
                LatestResult: link.TestCaseId is null ? null : LatestResult(store, workspaceId, link.TestCaseId),
                Flakiness: null,
                Coverage: null,
                QualityWarnings: link.TestCaseId is null ? [] : QualityWarnings(store, workspaceId, link.TestCaseId));
        }
    }

    private static IReadOnlyDictionary<string, object?>? LatestResult(
        ContinuousTestStore store,
        string workspaceId,
        string testCaseId)
    {
        CtLatestTestResult? row = store.GetLatestTestResult(workspaceId, testCaseId);
        if (row is null)
            return null;

        return new Dictionary<string, object?>
        {
            ["id"] = row.Id,
            ["status"] = row.Status,
            ["test_run_id"] = row.TestRunId,
            ["result_revision"] = row.ResultRevision,
            ["failure_summary"] = row.FailureSummary,
        };
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> QualityWarnings(
        ContinuousTestStore store,
        string workspaceId,
        string testCaseId)
    {
        return store.ListTestQualityFindings(workspaceId, testCaseId)
            .Select(static row => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = row.Id,
                ["finding_type"] = row.FindingType,
                ["severity"] = row.Severity,
                ["confidence"] = row.Confidence,
                ["explanation"] = row.Explanation,
                ["evidence"] = row.Evidence,
            })
            .ToArray();
    }

    private static bool HasAnyArtifactEvidence(IReadOnlyDictionary<string, int> counts) =>
        counts.GetValueOrDefault("test_results") > 0 || counts.GetValueOrDefault("coverage_spans") > 0;

    private static string? ArtifactRecommendation(string workspaceId, IReadOnlyDictionary<string, int> counts)
    {
        if (counts.GetValueOrDefault("test_results") <= 0)
            return $"miller tests import-results {workspaceId} <path>";

        if (counts.GetValueOrDefault("coverage_spans") <= 0)
            return $"miller tests import-coverage {workspaceId} <path>";

        return null;
    }

    private static bool StaleLinkExists(
        ContinuousTestStore store,
        string workspaceId,
        string subjectType,
        string subjectId,
        string sourceHash)
    {
        IReadOnlyList<CtTestLink> links = subjectType == "symbol"
            ? store.ListTestLinks(workspaceId, sourceSymbolName: subjectId)
            : store.ListTestLinks(workspaceId, sourceFilePath: subjectId);

        foreach (CtTestLink link in links)
        {
            string? rowHash = Convert.ToString(link.Metadata.GetValueOrDefault("source_hash"))
                ?? link.SourceContentHash;
            if (!string.IsNullOrEmpty(rowHash) && !string.Equals(rowHash, sourceHash, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    internal static double Score(ContinuousTestConfidenceState state) =>
        state switch
        {
            ContinuousTestConfidenceState.Verified => 0.9,
            ContinuousTestConfidenceState.Covered => 0.72,
            ContinuousTestConfidenceState.Likely => 0.55,
            ContinuousTestConfidenceState.Weak => 0.4,
            ContinuousTestConfidenceState.Failed => 0.2,
            ContinuousTestConfidenceState.Untested => 0.1,
            ContinuousTestConfidenceState.Unknown => 0.0,
            ContinuousTestConfidenceState.Stale => 0.0,
            _ => 0.0,
        };

    internal static string StateValue(ContinuousTestConfidenceState state) =>
        state switch
        {
            ContinuousTestConfidenceState.Verified => "verified",
            ContinuousTestConfidenceState.Covered => "covered",
            ContinuousTestConfidenceState.Likely => "likely",
            ContinuousTestConfidenceState.Weak => "weak",
            ContinuousTestConfidenceState.Failed => "failed",
            ContinuousTestConfidenceState.Untested => "untested",
            ContinuousTestConfidenceState.Unknown => "unknown",
            ContinuousTestConfidenceState.Stale => "stale",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };

    internal static ContinuousTestConfidenceState ParseState(string value) =>
        value switch
        {
            "verified" => ContinuousTestConfidenceState.Verified,
            "covered" => ContinuousTestConfidenceState.Covered,
            "likely" => ContinuousTestConfidenceState.Likely,
            "weak" => ContinuousTestConfidenceState.Weak,
            "failed" => ContinuousTestConfidenceState.Failed,
            "untested" => ContinuousTestConfidenceState.Untested,
            "unknown" => ContinuousTestConfidenceState.Unknown,
            "stale" => ContinuousTestConfidenceState.Stale,
            _ => Enum.TryParse(value, ignoreCase: true, out ContinuousTestConfidenceState state)
                ? state
                : throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static string? LatestStatus(ConfidenceEvidenceRow row) =>
        row.LatestResult is not null && row.LatestResult.TryGetValue("status", out object? value)
            ? Convert.ToString(value)
            : null;

    private static bool IsFlaky(IReadOnlyDictionary<string, object?>? flakiness) =>
        flakiness is not null &&
        string.Equals(Convert.ToString(flakiness.GetValueOrDefault("state")), "flaky", StringComparison.Ordinal);

    private sealed record ConfidenceEvidenceRow(
        string Selector,
        string? Path,
        string Tier,
        double Confidence,
        string Explanation,
        IReadOnlyList<string> SourceFactIds,
        string? TestCaseId,
        IReadOnlyDictionary<string, object?>? LatestResult,
        IReadOnlyDictionary<string, object?>? Flakiness,
        IReadOnlyDictionary<string, object?>? Coverage,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> QualityWarnings);
}
