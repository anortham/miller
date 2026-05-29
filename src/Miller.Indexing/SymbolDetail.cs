namespace Miller.Indexing;

/// <summary>
/// On-demand per-symbol detail fetched for an <c>inspect</c> call (M2 §2). Kept off the in-memory index
/// (which stays lean at ~565k rows) and read lazily because inspect is far lower-volume than search. Every
/// field is nullable: julie writes NULL for absent doc comments, visibility, and body spans.
/// </summary>
public sealed record SymbolDetail(
    string? DocComment,
    string? Visibility,
    string? CodeContext,
    int? BodyStartByte,
    int? BodyEndByte,
    int? BodyStartLine,
    int? BodyEndLine);

/// <summary>
/// One identifier-table row: a name-based reference, callable site, type usage, or member access. Refs are
/// NAME-based because <c>identifiers.target_symbol_id</c> is ALWAYS NULL at extract — resolution is the
/// consumer's job (M4). <see cref="ContainingSymbolId"/> (the enclosing symbol) IS populated and is the
/// source of one-hop callers.
/// </summary>
public sealed record SymbolRef(
    string Name,
    string Kind,
    string FilePath,
    int StartLine,
    string? ContainingSymbolId);
