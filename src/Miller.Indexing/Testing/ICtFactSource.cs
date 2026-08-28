using Miller.Core.References;
using Miller.Indexing.Reads;

namespace Miller.Indexing.Testing;

/// <summary>
/// CT freshness cursor: the generation identity (changes only when the served generation really
/// changes), the index revision for per-test watermarks, and the family/artifact id the
/// revision-delta reader compares. <see cref="FromSnapshot"/> is the single construction path
/// for every identity intake — do not hand-roll a cursor from a snapshot.
/// </summary>
public readonly record struct CtIndexCursor
{
    public string IndexIdentity { get; }
    public long Revision { get; }
    public string? FamilyId { get; }

    public CtIndexCursor(string IndexIdentity, long Revision, string? FamilyId = null)
    {
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(Revision), "must not be negative");
        this.IndexIdentity = IndexIdentity;
        this.Revision = Revision;
        this.FamilyId = FamilyId;
    }

    public static CtIndexCursor FromSnapshot(WorkspaceReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new CtIndexCursor(
            snapshot.IndexGenerationIdentity,
            snapshot.Freshness.Revision,
            snapshot.ArtifactOrStoreId);
    }
}

/// <summary>One indexed symbol, keyed by name+path rather than a cross-database id.</summary>
public sealed record CtSymbolFact(
    string SymbolId,
    string Name,
    string Kind,
    string Language,
    string FilePath,
    string? ContentHash,
    int StartLine,
    int EndLine,
    string? ParentId,
    bool IsTest,
    string? Signature,
    bool? TestCase = null,
    bool? TestContainer = null,
    bool? TestLifecycle = null,
    string? TestEvidenceStatus = null,
    string? TestEvidenceReason = null);

/// <summary>One inbound reference or identifier site targeting a symbol.</summary>
public sealed record CtReferenceFact(
    string? SourceSymbolId,
    string TargetSymbolId,
    string Kind,
    double Confidence,
    string Provenance,
    string? FilePath,
    int? StartLine,
    ReferenceResolutionStatus ResolutionStatus);

/// <summary>One reverse-reachability hit from typed impact.</summary>
public sealed record CtImpactedSymbol(
    string SymbolId,
    string Name,
    string Kind,
    string FilePath,
    bool IsTest,
    int Hop,
    string? EdgeKind,
    string? EdgeSource,
    bool? TestCase = null,
    bool? TestContainer = null,
    bool? TestLifecycle = null,
    string? TestEvidenceStatus = null,
    string? TestEvidenceReason = null);

/// <summary>Current file metadata and the completeness of its indexed evidence.</summary>
public sealed record CtFileFact(
    string Path,
    string? Language,
    string? ContentHash,
    string? Status,
    bool HasParseDiagnostics,
    bool EvidenceAvailable);

/// <summary>Typed blast-radius partition: non-test dependents versus likely tests.</summary>
public sealed record CtImpactResult(
    IReadOnlyList<CtImpactedSymbol> Impacted,
    IReadOnlyList<CtImpactedSymbol> Tests,
    int NodesVisited,
    bool TruncatedByDepth,
    bool TruncatedByLimit);

/// <summary>
/// Public typed Miller fact surface for continuous testing. Implemented in Indexing so it can read
/// <c>RevisionFactCache</c> without <c>InternalsVisibleTo</c>.
/// </summary>
public interface ICtFactSource
{
    CtIndexCursor Current { get; }

    IReadOnlyList<CtSymbolFact> SymbolsForChangedFiles(IReadOnlyList<string> changedPaths);

    IReadOnlyList<CtFileFact> FileFactsForPaths(IReadOnlyList<string> paths);

    IReadOnlyList<CtReferenceFact> ReferencesTo(IReadOnlyList<string> symbolIds);

    IReadOnlyList<CtReferenceFact> IdentifierEvidenceTo(IReadOnlyList<string> symbolIds);

    CtImpactResult Impact(IReadOnlyList<string> seedSymbolIds, int maxDepth = 2, int limit = 100);
}
