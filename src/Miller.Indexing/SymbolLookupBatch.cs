namespace Miller.Indexing;

public static class SymbolLookupBatch
{
    public static IReadOnlyDictionary<string, IndexedSymbol> FindBySymbolIds(
        ISymbolLookupIndex index,
        IEnumerable<string> symbolIds)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(symbolIds);

        if (index is FtsSymbolSearchIndex fts)
            return fts.FindBySymbolIds(symbolIds);

        var results = new Dictionary<string, IndexedSymbol>(StringComparer.Ordinal);
        foreach (string id in symbolIds)
        {
            if (string.IsNullOrWhiteSpace(id) || results.ContainsKey(id))
                continue;

            IndexedSymbol? symbol = index.FindBySymbolId(id);
            if (symbol is not null)
                results[id] = symbol;
        }
        return results;
    }
}
