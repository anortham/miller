using Miller.Core.Search;

namespace Miller.Indexing;

/// <summary>
/// The M1 in-process deliverable: a thin facade tying the SQLite read layer to Miller.Core's ranked index.
/// Builds <see cref="MillerSearchIndex"/> from the read symbols' scoring projections and retains the
/// <see cref="IndexedSymbol"/>s for hydration. This is where the opaque-string-id ⇄ int-DocId bridge lives:
/// a <see cref="SearchHit"/>'s <see cref="SearchableDocument.DocId"/> resolves back through
/// <see cref="Resolve"/> to the julie symbol id (the M4 join key) and its containment parent.
/// </summary>
public sealed class MillerRepositoryIndex
{
    private readonly MillerSearchIndex _index;
    private readonly IndexedSymbol[] _byDocId; // DocId == array index by construction (see Build)

    private MillerRepositoryIndex(MillerSearchIndex index, IndexedSymbol[] byDocId)
    {
        _index = index;
        _byDocId = byDocId;
    }

    /// <summary>Total number of indexed symbols.</summary>
    public int DocumentCount => _byDocId.Length;

    /// <summary>
    /// Build the repository index from symbols produced by <see cref="SqliteSymbolReader.Read"/>. The reader
    /// assigns contiguous 0-based DocIds in deterministic order, so the hydration array is indexed directly by
    /// DocId for O(1) <see cref="Resolve"/>. This contract is validated, not assumed.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// DocIds are not the contiguous 0..n-1 ordinals this facade requires (a reader regression).
    /// </exception>
    public static MillerRepositoryIndex Build(IReadOnlyList<IndexedSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var byDocId = new IndexedSymbol[symbols.Count];
        var documents = new SearchableDocument[symbols.Count];
        for (int i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            // Pin the read-layer contract: DocId is the 0-based row ordinal. A drift here would silently
            // misroute Resolve(), so reject it loudly rather than corrupt the id bridge.
            if (symbol.DocId != i)
                throw new ArgumentException(
                    $"Symbol at position {i} has DocId {symbol.DocId}; MillerRepositoryIndex requires the " +
                    "contiguous 0-based ordinals SqliteSymbolReader assigns.", nameof(symbols));

            byDocId[i] = symbol;
            documents[i] = symbol.ToSearchableDocument();
        }

        return new MillerRepositoryIndex(MillerSearchIndex.Build(documents), byDocId);
    }

    /// <summary>Search the index (delegates to <see cref="MillerSearchIndex.Search"/>; see its semantics).</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
        _index.Search(query, limit, mode);

    /// <summary>
    /// Hydrate a search hit's <see cref="SearchableDocument.DocId"/> back to its full <see cref="IndexedSymbol"/>
    /// (carrying the opaque julie symbol id + parent id). O(1).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="docId"/> is not a valid DocId.</exception>
    public IndexedSymbol Resolve(int docId)
    {
        if ((uint)docId >= (uint)_byDocId.Length)
            throw new ArgumentOutOfRangeException(nameof(docId), docId,
                $"DocId must be in [0, {_byDocId.Length}).");
        return _byDocId[docId];
    }
}
