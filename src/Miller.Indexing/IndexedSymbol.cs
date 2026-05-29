using Miller.Core.Search;

namespace Miller.Indexing;

/// <summary>
/// The Indexing-layer carrier for one julie symbol row. <see cref="SearchableDocument"/> deliberately
/// carries no julie id (Core is the scoring layer and stays id-agnostic); Indexing retains the opaque
/// <see cref="SymbolId"/> and <see cref="ParentId"/> as the M4 join keys (identifiers / relationships /
/// types all join on the opaque symbol id). <see cref="DocId"/> is the Miller-assigned 0-based row ordinal
/// from the reader's deterministic SELECT and is what the in-memory index ranks on.
/// </summary>
public sealed record IndexedSymbol(
    int DocId,          // Miller-assigned, 0-based row ordinal (opaque to the index)
    string SymbolId,    // julie opaque MD5-hex id — the M4 join key (treat as opaque)
    string Name,
    string? Signature,
    string Kind,
    string Language,
    string FilePath,    // relative-unix to root_path
    int StartLine,      // 1-based (NULL start_line in the DB maps to 0)
    string? ParentId,   // julie parent_id (containment; M4)
    bool IsTest = false) // julie's persisted symbols.metadata.is_test (cross-language, all 34 langs); see M2 §2 decision-4
{
    /// <summary>
    /// Project to the Core scoring document. Drops the join keys (<see cref="SymbolId"/>/<see cref="ParentId"/>)
    /// which Core never needs; the index ranks on Name + Signature (Decision D3).
    /// </summary>
    public SearchableDocument ToSearchableDocument() =>
        new(DocId, Name, Signature, Kind, Language, FilePath, StartLine);
}
