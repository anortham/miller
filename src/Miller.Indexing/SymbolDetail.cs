namespace Miller.Indexing;

/// <summary>
/// On-demand per-symbol detail fetched for an <c>inspect</c> call (M2 §2). Kept off the in-memory index
/// (which stays lean at ~565k rows) and read lazily because inspect is far lower-volume than search. Every
/// field is nullable: julie writes NULL for absent doc comments, visibility, and body spans.
///
/// <para>v1 dropped <c>code_context</c> from <c>symbols</c> (it lives only on <c>identifiers</c> now), so this
/// record no longer carries it (reconciliation #11).</para>
/// </summary>
public sealed record SymbolDetail(
    string? DocComment,
    string? Visibility,
    int? BodyStartByte,
    int? BodyEndByte,
    int? BodyStartLine,
    int? BodyEndLine,
    string? BodyHash);

/// <summary>
/// One symbol-scoped <c>complexity_metrics</c> row (schema v3; emitted broadly since julie-extract 2.3.0).
/// Read lazily for <c>inspect depth=full</c> like <see cref="SymbolDetail"/>. <see cref="ParameterCount"/> is
/// nullable: julie writes NULL where a parameter list does not apply.
/// </summary>
public sealed record SymbolComplexity(
    string AlgorithmId,
    long CoveredLines,
    long DecisionCount,
    long LoopCount,
    long MaxNestingDepth,
    long? ParameterCount);

/// <summary>
/// Legacy name-based identifier projection used by the current tool renderers.
/// <see cref="ContainingSymbolId"/> identifies the enclosing symbol but does not resolve the target.
/// Symbol-specific callers use <see cref="Miller.Core.References.ReferenceEvidence"/> instead.
/// </summary>
public sealed record SymbolRef(
    string Name,
    string Kind,
    string FilePath,
    int StartLine,
    string? ContainingSymbolId);

/// <summary>
/// One occurrence returned by the legacy name-based rename reader. Byte offsets are absolute UTF-8 byte
/// indices into the file content. This shape carries no resolved target identity; callers requiring safe
/// symbol-specific edits must filter through <see cref="Miller.Core.References.ReferenceEvidence"/>.
/// </summary>
/// <param name="FilePath">The file the occurrence lives in (julie's relative <c>file_path</c>).</param>
/// <param name="StartByte">Inclusive start of the name token (absolute UTF-8 byte offset).</param>
/// <param name="EndByte">Exclusive end of the name token (absolute UTF-8 byte offset).</param>
/// <param name="StartLine">1-based line of the occurrence (for the preview site list).</param>
public sealed record IdentifierSite(
    string FilePath,
    int StartByte,
    int EndByte,
    int StartLine);
