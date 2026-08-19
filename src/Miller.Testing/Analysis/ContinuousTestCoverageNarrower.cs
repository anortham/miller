using System.Globalization;

namespace Miller.Testing;

public sealed class ContinuousTestCoverageNarrower : ICtCoverageFactSource
{
    private static readonly HashSet<string> RemovableTiers =
    [
        "graph_reference",
        "identifier_reference",
        "path_stem",
    ];

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ContinuousTestStore _store;

    public ContinuousTestCoverageNarrower(ContinuousTestStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public IReadOnlyList<CtCoverageSpanFact> SpansCovering(
        string workspaceId,
        IReadOnlyList<string> symbolIds,
        IReadOnlyList<string> filePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(symbolIds);
        ArgumentNullException.ThrowIfNull(filePaths);

        return _store.ListCoverageSpansCovering(workspaceId, symbolIds, filePaths)
            .Select(span => new CtCoverageSpanFact(
                SpanId: span.Id,
                TestCaseId: Convert.ToString(span.Metadata.GetValueOrDefault("test_case_id")),
                SymbolId: span.SymbolName,
                Path: span.FilePath ?? "",
                StartLine: span.StartLine))
            .ToArray();
    }

    public ContinuousTestCoverageNarrowingResult Narrow(
        ContinuousTestSelectionResult staticSelection,
        string workspaceId,
        string projectPath,
        CtFreshnessKey selected)
    {
        ArgumentNullException.ThrowIfNull(staticSelection);
        IReadOnlyList<CtCoverageNarrowingEvidence> evidence = _store.ListCtCoverageNarrowingEvidence(
            workspaceId,
            projectPath,
            staticSelection.SelectedTestCaseIds,
            selected);
        return Narrow(staticSelection, workspaceId, projectPath, selected, evidence);
    }

    public static ContinuousTestCoverageNarrowingResult Narrow(
        ContinuousTestSelectionResult staticSelection,
        string workspaceId,
        string projectPath,
        CtFreshnessKey selected,
        IReadOnlyList<CtCoverageNarrowingEvidence> coverageEvidence)
    {
        ArgumentNullException.ThrowIfNull(staticSelection);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(coverageEvidence);

        string normalizedProjectPath = Path.GetFullPath(projectPath);
        Dictionary<string, IReadOnlyList<ContinuousTestSelectionEvidence>> staticEvidenceByTest = staticSelection.Evidence
            .GroupBy(row => row.TestCaseId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ContinuousTestSelectionEvidence>)group.ToArray(), StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<CtCoverageNarrowingEvidence>> coverageEvidenceByTest = coverageEvidence
            .GroupBy(row => row.TestCaseId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CtCoverageNarrowingEvidence>)group.ToArray(), StringComparer.Ordinal);

        var finalSelected = new List<string>();
        var dropped = new List<string>();
        var advisory = new List<string>();
        var decisions = new List<ContinuousTestCoverageNarrowingDecision>();

        foreach (string testCaseId in staticSelection.SelectedTestCaseIds.Distinct(StringComparer.Ordinal))
        {
            IReadOnlyList<ContinuousTestSelectionEvidence> staticRows = staticEvidenceByTest.GetValueOrDefault(testCaseId) ?? [];
            IReadOnlyList<CtCoverageNarrowingEvidence> coverageRows = coverageEvidenceByTest.GetValueOrDefault(testCaseId) ?? [];
            var evidence = new ContinuousTestCoverageNarrowingEvidence(testCaseId, staticRows, coverageRows);

            if (staticRows.Count == 0 || staticRows.Any(row => !RemovableTiers.Contains(row.Tier)))
            {
                finalSelected.Add(testCaseId);
                decisions.Add(new ContinuousTestCoverageNarrowingDecision(
                    testCaseId,
                    Selected: true,
                    Dropped: false,
                    Advisory: false,
                    Reason: "protected_static_evidence",
                    evidence));
                continue;
            }

            if (coverageRows.Count == 0)
            {
                finalSelected.Add(testCaseId);
                advisory.Add(testCaseId);
                decisions.Add(new ContinuousTestCoverageNarrowingDecision(
                    testCaseId,
                    Selected: true,
                    Dropped: false,
                    Advisory: true,
                    Reason: "coverage_map_missing",
                    evidence));
                continue;
            }

            if (coverageRows.Any(row => !IsTrusted(row, testCaseId, workspaceId, normalizedProjectPath, selected)))
            {
                finalSelected.Add(testCaseId);
                advisory.Add(testCaseId);
                decisions.Add(new ContinuousTestCoverageNarrowingDecision(
                    testCaseId,
                    Selected: true,
                    Dropped: false,
                    Advisory: true,
                    Reason: "coverage_map_untrusted",
                    evidence));
                continue;
            }

            dropped.Add(testCaseId);
            decisions.Add(new ContinuousTestCoverageNarrowingDecision(
                testCaseId,
                Selected: false,
                Dropped: true,
                Advisory: false,
                Reason: "trusted_coverage_map",
                evidence));
        }

        return new ContinuousTestCoverageNarrowingResult(
            staticSelection,
            finalSelected,
            dropped,
            advisory,
            decisions);
    }

    private static bool IsTrusted(
        CtCoverageNarrowingEvidence evidence,
        string testCaseId,
        string workspaceId,
        string projectPath,
        CtFreshnessKey selected)
    {
        CtCoverageMapRecord? map = evidence.Map;
        return evidence.IsTrustedAtRevision
            && StringComparer.Ordinal.Equals(evidence.TestCaseId, testCaseId)
            && map is not null
            && StringComparer.Ordinal.Equals(map.TestCaseId, testCaseId)
            && StringComparer.Ordinal.Equals(map.WorkspaceId, workspaceId)
            && PathEquals(map.ProjectPath, projectPath)
            && map.Complete
            && map.StartConverged
            && map.EndConverged
            && !string.IsNullOrEmpty(map.RevisionAtStart)
            && StringComparer.Ordinal.Equals(map.RevisionAtStart, map.RevisionAtEnd)
            && StringComparer.Ordinal.Equals(
                map.ValidThroughRevision,
                selected.Revision.ToString(CultureInfo.InvariantCulture))
            && map.InvalidatedAtRevision is null
            && StringComparer.Ordinal.Equals(map.IndexIdentity, selected.IndexIdentity);
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return PathComparer.Equals(Path.GetFullPath(left), Path.GetFullPath(right));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
