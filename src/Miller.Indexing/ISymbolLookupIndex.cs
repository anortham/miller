namespace Miller.Indexing;

public interface ISymbolLookupIndex : ISymbolSearchIndex
{
    IReadOnlySet<string> KnownExtensions { get; }

    IReadOnlyList<IndexedSymbol> FindByName(string name);

    IndexedSymbol? FindBySymbolId(string symbolId);

    IReadOnlyList<IndexedSymbol> FindChildren(string parentId);

    IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath);

    IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit);

    IReadOnlyList<string> FindFilePathsByFragment(string query, int limit) =>
        FindByFilePathFragment(query, limit)
            .Select(static symbol => symbol.FilePath)
            .Distinct(StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

    bool IsIndexedFilePath(string path);

    string? ResolveIndexedFilePath(string target);

    IReadOnlyDictionary<int, IndexedSymbol> ResolveMany(IReadOnlyCollection<int> docIds)
    {
        ArgumentNullException.ThrowIfNull(docIds);

        var resolved = new Dictionary<int, IndexedSymbol>(docIds.Count);
        foreach (int docId in docIds)
            if (!resolved.ContainsKey(docId))
                resolved.Add(docId, Resolve(docId));
        return resolved;
    }
}
