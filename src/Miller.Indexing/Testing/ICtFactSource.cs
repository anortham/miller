using Miller.Core.References;

namespace Miller.Indexing.Testing;

/// <summary>Pinned store cursor or legacy artifact id, plus the integer revision for that generation.</summary>
public readonly record struct CtIndexCursor
{
    public string IndexIdentity { get; }
    public long Revision { get; }

    public CtIndexCursor(string IndexIdentity, long Revision)
    {
        if (string.IsNullOrWhiteSpace(IndexIdentity))
            throw new ArgumentException("must not be empty", nameof(IndexIdentity));
        if (Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(Revision), "must not be negative");
        this.IndexIdentity = IndexIdentity;
        this.Revision = Revision;
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
    string? Signature);

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
    string? EdgeSource);

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

    IReadOnlyList<CtReferenceFact> ReferencesTo(IReadOnlyList<string> symbolIds);

    IReadOnlyList<CtReferenceFact> IdentifierEvidenceTo(IReadOnlyList<string> symbolIds);

    CtImpactResult Impact(IReadOnlyList<string> seedSymbolIds, int maxDepth = 2, int limit = 100);
}
