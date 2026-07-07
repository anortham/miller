namespace Miller.Core.DeadCode;

/// <summary>
/// One candidate-kind symbol plus the inbound-evidence and suppression facts the reader gathered for it. A pure
/// projection consumed by <see cref="DeadCodeCandidates.Evaluate"/>; Miller.Core stays free of julie's row shape.
/// </summary>
/// <remarks>
/// The closure booleans (<see cref="IsTestSelfOrAncestor"/>, <see cref="HasStructuralFactSelfOrAncestor"/>) are
/// computed by the reader's <c>parent_symbol_id</c> walk; Core TRUSTS them as given rather than re-walking parents.
/// <see cref="HasAnnotation"/> is SELF-only. <see cref="LiteralMatch"/> drives the two-phase literal scan:
/// <c>null</c> = not yet scanned (provisional candidate), <c>true</c> = the name was found in a string literal
/// (suppressed), <c>false</c> = scanned with no match (candidate). <see cref="StartByte"/> / <see cref="EndByte"/>
/// are carried faithfully for downstream consumers even though the evaluator itself does not read them.
/// </remarks>
public sealed record DeadCodeSymbolRow(
    string SymbolId,
    string Name,
    string Kind,
    string Language,
    string Path,
    int StartLine,
    long StartByte,
    long EndByte,
    string? Visibility,
    bool IsTestSelfOrAncestor,
    string? ParentSymbolId,
    bool HasAnnotation,
    bool HasStructuralFactSelfOrAncestor,
    int NameMatchesOutside,
    int ResolvedInbound,
    int PendingResolvedInbound,
    int CallsInbound,
    bool? LiteralMatch);

/// <summary>
/// Per-language identifier / resolution counts for the artifact, computed by the reader at query time (never
/// hardcoded). Feeds the <c>low_evidence_language</c> suppression and the per-language evidence-label threshold.
/// </summary>
public sealed record LanguageCoverageRow(
    string Language,
    int IdentifierCount,
    int ResolvedCount);
