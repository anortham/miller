namespace Miller.Indexing;

public interface ISymbolLookupIndex : ISymbolSearchIndex
{
    IReadOnlySet<string> KnownExtensions { get; }

    IReadOnlyList<IndexedSymbol> FindByName(string name);

    IndexedSymbol? FindBySymbolId(string symbolId);

    IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath);

    bool IsIndexedFilePath(string path);

    string? ResolveIndexedFilePath(string target);
}
