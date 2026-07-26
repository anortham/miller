namespace Miller.Core.References;

/// <summary>Storage source that established a reference.</summary>
public enum ReferenceEvidenceSource
{
    IdentifierDirect,
    IdentifierResolution,
    Relationship,
    PendingResolution,
    NameFallback,
}

/// <summary>Whether a reference is resolved to the requested symbol or inferred by bounded fallback.</summary>
public enum ReferenceResolutionStatus
{
    Exact,
    Fallback,
}

/// <summary>Why the fallback arm did or did not return evidence.</summary>
public enum ReferenceFallbackStatus
{
    NoCandidates,
    Available,
    SuppressedAmbiguousName,
}

/// <summary>Explicit caps for exact and fallback evidence.</summary>
public readonly record struct ReferenceEvidenceBounds(int ExactLimit, int FallbackLimit)
{
    public void Validate()
    {
        if (ExactLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(ExactLimit), ExactLimit, "ExactLimit must be positive.");
        if (FallbackLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(FallbackLimit), FallbackLimit, "FallbackLimit cannot be negative.");
    }
}

/// <summary>Kind-aware, offset-based bounds for a stateless reference evidence page.</summary>
public readonly record struct ReferenceEvidenceQuery(
    ReferenceEvidenceBounds Bounds,
    ReferenceKind? Kind = null,
    int ExactOffset = 0,
    int FallbackOffset = 0)
{
    public void Validate()
    {
        Bounds.Validate();
        if (ExactOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(ExactOffset), ExactOffset, "ExactOffset cannot be negative.");
        if (FallbackOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(FallbackOffset), FallbackOffset, "FallbackOffset cannot be negative.");
    }
}

/// <summary>A normalized reference site with provenance and resolution facts.</summary>
public sealed record ReferenceEvidence(
    string? TargetSymbolId,
    string? ContainingSymbolId,
    string FilePath,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn,
    long? StartByte,
    long? EndByte,
    ReferenceKind Kind,
    string SourceKind,
    ReferenceEvidenceSource Source,
    int? ResolutionTier,
    double Confidence,
    ReferenceResolutionStatus ResolutionStatus,
    string? Language,
    string ReferenceSiteId,
    bool IsExact,
    string SiteProvenance);

/// <summary>Counts and fallback safety facts for one bounded reference read.</summary>
public sealed record ReferenceEvidenceCoverage(
    int ExactObserved,
    int ExactAvailable,
    int ExactReturned,
    int FallbackAvailable,
    int FallbackReturned,
    int SameNameDefinitionCount,
    bool ExactTruncated,
    bool FallbackTruncated,
    ReferenceFallbackStatus FallbackStatus);

/// <summary>Artifact identity that binds stateless reference continuation cursors.</summary>
public sealed record ReferenceEvidenceSnapshot(string ArtifactId, long Revision);

/// <summary>Bounded exact and fallback reference evidence for one resolved symbol.</summary>
public sealed record ReferenceEvidenceSet(
    IReadOnlyList<ReferenceEvidence> Exact,
    IReadOnlyList<ReferenceEvidence> Fallback,
    ReferenceEvidenceCoverage Coverage,
    ReferenceEvidenceSnapshot? Snapshot = null)
{
    public IReadOnlyList<string> ExactCallerSymbolIds { get; init; } = [];

    public IReadOnlyList<string> ExactReferencedBySymbolIds { get; init; } = [];
}

/// <summary>A normalized outgoing reference with an exact target or an unresolved fallback name.</summary>
public sealed record OutgoingReferenceEvidence(
    string ContainingSymbolId,
    string? TargetSymbolId,
    string TargetName,
    string FilePath,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn,
    long? StartByte,
    long? EndByte,
    ReferenceKind Kind,
    string SourceKind,
    ReferenceEvidenceSource Source,
    int? ResolutionTier,
    double Confidence,
    ReferenceResolutionStatus ResolutionStatus,
    string? Language,
    string ReferenceSiteId,
    bool IsExact,
    string SiteProvenance);

/// <summary>Counts for one independently bounded outgoing reference read.</summary>
public sealed record OutgoingReferenceEvidenceCoverage(
    int ExactObserved,
    int ExactAvailable,
    int ExactReturned,
    int FallbackAvailable,
    int FallbackReturned,
    bool ExactTruncated,
    bool FallbackTruncated);

/// <summary>Bounded resolved and unresolved outgoing evidence for one containing symbol.</summary>
public sealed record OutgoingReferenceEvidenceSet(
    IReadOnlyList<OutgoingReferenceEvidence> Exact,
    IReadOnlyList<OutgoingReferenceEvidence> Fallback,
    OutgoingReferenceEvidenceCoverage Coverage,
    ReferenceEvidenceSnapshot? Snapshot = null);
