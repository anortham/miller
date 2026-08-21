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

/// <summary>
/// What a selection concluded about reachability. The fail-closed rule lives in the result itself:
/// only <see cref="Impacted"/> and <see cref="WorkspaceScope"/> selections may enqueue provider
/// execution (see <see cref="ContinuousTestSelectionResult.MayExecute"/>), so a consumer cannot run
/// tests off an <see cref="Unknown"/> selection without deliberately ignoring the contract.
/// <see cref="Unknown"/> is deliberately the enum default: a result whose outcome nobody set
/// refuses execution instead of permitting it.
/// </summary>
public enum ContinuousTestSelectionOutcome
{
    /// <summary>
    /// Reachability could not be established: a truncated impact read, a changed path the index
    /// cannot account for, or impact evidence that cannot be mapped to a runnable case. Everything
    /// previously fresh goes stale and NOTHING executes — never a full-suite fallback.
    /// </summary>
    Unknown,

    /// <summary>A complete read that maps the change to specific test cases; only those (plus the
    /// already-owed backlog) go stale, and only they run.</summary>
    Impacted,

    /// <summary>A complete read proving the change reaches no test: an empty stale delta and no
    /// run.</summary>
    KnownEmpty,

    /// <summary>An explicit whole-workspace request (generation change, inventory refresh,
    /// explicit run): everything is stale and everything is selected.</summary>
    WorkspaceScope,
}

public sealed record ContinuousTestSelectionResult(
    IReadOnlyList<string> SelectedTestCaseIds,
    IReadOnlyList<string> StaleTestCaseIds,
    IReadOnlyList<ContinuousTestSelectionEvidence> Evidence,
    ContinuousTestSelectionOutcome Outcome = ContinuousTestSelectionOutcome.Unknown)
{
    /// <summary>
    /// The fail-closed execution gate. Consumers that turn selections into provider runs must
    /// check this instead of the outcome enum, so a future outcome value defaults to refusing
    /// execution rather than permitting it.
    /// </summary>
    public bool MayExecute => Outcome
        is ContinuousTestSelectionOutcome.Impacted
        or ContinuousTestSelectionOutcome.WorkspaceScope;
}

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
