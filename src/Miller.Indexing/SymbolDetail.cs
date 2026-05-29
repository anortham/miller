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

/// <summary>
/// One occurrence of a name in julie's <c>identifiers</c> table: the exact per-occurrence byte token to
/// rewrite for an M6 workspace-wide rename (verified-fact 2 — e.g. a 5-char <c>Total</c> call at
/// <c>start_byte=120, end_byte=125</c>). Byte offsets are absolute UTF-8 byte indices into the file content
/// (NOT UTF-16 char indices). Matching is NAME-based because <c>target_symbol_id</c> is NULL at extract, so a
/// homonym site is also returned — the Server maps these into the pure <c>RenamePlanner</c>'s per-file sites
/// and the preview surfaces every one before any write.
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
