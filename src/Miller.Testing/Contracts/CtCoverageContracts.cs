namespace Miller.Testing;

/// <summary>
/// The current coverage map for one test case. Trust is derived, never stored:
/// <c>Complete AND StartConverged AND EndConverged AND RevisionAtStart == RevisionAtEnd</c>
/// at the persisted <see cref="IndexIdentity"/> + <see cref="Revision"/> key.
/// </summary>
public sealed record CtCoverageMapRecord(
    string MapId,
    string WorkspaceId,
    string TestCaseId,
    string ProjectPath,
    string RunId,
    string GenerationId,
    string IndexIdentity,
    long Revision,
    string? RevisionAtStart,
    bool StartConverged,
    string? RevisionAtEnd,
    bool EndConverged,
    bool Complete,
    string? FailureReason,
    string Granularity,
    string? ValidThroughRevision,
    string? InvalidatedAtRevision,
    DateTimeOffset RecordedAt,
    string Source);

public sealed record CtCoverageNarrowingEvidence(
    string TestCaseId,
    CtCoverageMapRecord? Map,
    bool IsTrustedAtRevision);

public enum CtCoverageDeltaApplyStatus
{
    Applied,
    AlreadyApplied,
    Rejected,
}

public sealed record CtCoverageDeltaApplyResult(
    CtCoverageDeltaApplyStatus Status,
    int AdvancedMapCount,
    int InvalidatedMapCount);

public sealed record CtCoverageMaintenanceBatch(
    string ProjectPath,
    IReadOnlyList<string> TestCaseIds,
    long OfferSequence);
