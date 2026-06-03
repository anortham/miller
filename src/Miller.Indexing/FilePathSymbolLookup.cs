namespace Miller.Indexing;

internal static class FilePathSymbolLookup
{
    public static IReadOnlyList<IndexedSymbol> FindByFilePathFragment(
        IReadOnlyDictionary<string, List<IndexedSymbol>> byFilePath,
        string query,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(byFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit < 1)
            return Array.Empty<IndexedSymbol>();

        string normalizedQuery = query.Trim().Replace('\\', '/');
        var rankedPaths = new List<(string Path, int Rank)>();
        foreach (string path in byFilePath.Keys)
        {
            string fileName = LastPathSegment(path);
            int rank = Rank(path, fileName, normalizedQuery);
            if (rank >= 0)
                rankedPaths.Add((path, rank));
        }

        if (rankedPaths.Count == 0)
            return Array.Empty<IndexedSymbol>();

        rankedPaths.Sort(static (a, b) =>
        {
            int byRank = a.Rank.CompareTo(b.Rank);
            if (byRank != 0) return byRank;
            int byLength = a.Path.Length.CompareTo(b.Path.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a.Path, b.Path);
        });

        var results = new List<IndexedSymbol>(Math.Min(limit, rankedPaths.Count));
        foreach (var (path, _) in rankedPaths)
        {
            foreach (IndexedSymbol symbol in byFilePath[path])
            {
                results.Add(symbol);
                if (results.Count == limit)
                    return results;
            }
        }

        return results;
    }

    private static int Rank(string path, string fileName, string query)
    {
        if (string.Equals(path, query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (string.Equals(fileName, query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (path.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;
        return -1;
    }

    private static string LastPathSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
