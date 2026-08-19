namespace Miller.Testing;

public enum ContinuousTestCoverageNarrowingMode
{
    Off,
    Shadow,
    Active,
}

public sealed record ContinuousTestImpactSelectionRequest
{
    public string WorkspaceId { get; init; }
    public IReadOnlyList<string> ChangedPaths { get; init; }
    public IReadOnlyList<ContinuousTestImpactedSymbol> ImpactedSymbols { get; init; }
    public IReadOnlyList<ContinuousTestImpactedTest> ImpactedTests { get; init; }
    public bool WorkspaceScope { get; init; }
    public string? ProjectPath { get; init; }

    public ContinuousTestImpactSelectionRequest(
        string WorkspaceId,
        IReadOnlyList<string>? ChangedPaths = null,
        IReadOnlyList<ContinuousTestImpactedSymbol>? ImpactedSymbols = null,
        IReadOnlyList<ContinuousTestImpactedTest>? ImpactedTests = null,
        bool WorkspaceScope = false,
        string? ProjectPath = null)
    {
        if (string.IsNullOrEmpty(WorkspaceId))
            throw new ArgumentException("must not be empty", nameof(WorkspaceId));

        this.WorkspaceId = WorkspaceId;
        this.ChangedPaths = ChangedPaths ?? [];
        this.ImpactedSymbols = ImpactedSymbols ?? [];
        this.ImpactedTests = ImpactedTests ?? [];
        this.WorkspaceScope = WorkspaceScope;
        this.ProjectPath = string.IsNullOrWhiteSpace(ProjectPath) ? null : Path.GetFullPath(ProjectPath);
    }
}

public sealed record ContinuousTestImpactedSymbol(
    string? SymbolId = null,
    string? NodeId = null,
    string? FileId = null,
    string? Path = null,
    string? Name = null);

public sealed record ContinuousTestImpactedTest(
    string? SymbolId = null,
    string? Path = null,
    string? Name = null,
    int? Line = null,
    int? Hop = null,
    bool? TestCase = null,
    string? EvidenceStatus = null,
    string? EvidenceReason = null);

public sealed record ContinuousTestSelectionResult(
    IReadOnlyList<string> SelectedTestCaseIds,
    IReadOnlyList<string> StaleTestCaseIds,
    IReadOnlyList<ContinuousTestSelectionEvidence> Evidence);

public sealed record ContinuousTestSelectionEvidence(
    string TestCaseId,
    string Selector,
    string Tier,
    double Confidence,
    string Explanation,
    IReadOnlyList<string> SourceFactIds,
    string? EvidenceStatus = null,
    string? EvidenceReason = null);

public sealed record ContinuousTestCoverageNarrowingEvidence(
    string TestCaseId,
    IReadOnlyList<ContinuousTestSelectionEvidence> StaticEvidence,
    IReadOnlyList<CtCoverageNarrowingEvidence> CoverageEvidence);

public sealed record ContinuousTestCoverageNarrowingDecision(
    string TestCaseId,
    bool Selected,
    bool Dropped,
    bool Advisory,
    string Reason,
    ContinuousTestCoverageNarrowingEvidence Evidence)
{
    public IReadOnlyList<ContinuousTestSelectionEvidence> StaticEvidence => Evidence.StaticEvidence;
    public IReadOnlyList<CtCoverageNarrowingEvidence> CoverageEvidence => Evidence.CoverageEvidence;
}

public sealed record ContinuousTestCoverageNarrowingResult(
    ContinuousTestSelectionResult StaticSelection,
    IReadOnlyList<string> FinalSelectedTestCaseIds,
    IReadOnlyList<string> DroppedTestCaseIds,
    IReadOnlyList<string> AdvisoryTestCaseIds,
    IReadOnlyList<ContinuousTestCoverageNarrowingDecision> Decisions)
{
    public IReadOnlyList<string> StaticSelectedTestCaseIds => StaticSelection.SelectedTestCaseIds;
    public IReadOnlyList<string> StaticStaleTestCaseIds => StaticSelection.StaleTestCaseIds;
    public IReadOnlyList<ContinuousTestSelectionEvidence> StaticEvidence => StaticSelection.Evidence;
}
