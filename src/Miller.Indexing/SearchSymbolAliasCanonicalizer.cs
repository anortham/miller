namespace Miller.Indexing;

internal static class SearchSymbolAliasCanonicalizer
{
    public static IReadOnlyList<IndexedSymbol> Canonicalize(IReadOnlyList<IndexedSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (symbols.Count < 2)
            return symbols;

        var groups = new Dictionary<string, List<IndexedSymbol>>(symbols.Count, StringComparer.Ordinal);
        bool hasAliases = false;
        foreach (IndexedSymbol symbol in symbols)
        {
            if (!groups.TryGetValue(symbol.SymbolId, out List<IndexedSymbol>? group))
                groups[symbol.SymbolId] = group = new List<IndexedSymbol>(1);
            else
                hasAliases = true;
            group.Add(symbol);
        }

        if (!hasAliases)
            return symbols;

        var canonical = new List<IndexedSymbol>(groups.Count);
        foreach ((string symbolId, List<IndexedSymbol> group) in groups)
        {
            IndexedSymbol survivor = group[0];
            for (int i = 1; i < group.Count; i++)
            {
                IndexedSymbol alias = group[i];
                if (!AreExactAliases(survivor, alias))
                    throw new InvalidDataException(
                        $"Search symbol '{symbolId}' has divergent aliases at '{survivor.FilePath}' and " +
                        $"'{alias.FilePath}'; aliases must match every field except DocId and FilePath.");
                if (CompareDeterministically(alias, survivor) < 0)
                    survivor = alias;
            }
            canonical.Add(survivor);
        }

        canonical.Sort(CompareDeterministically);
        for (int i = 0; i < canonical.Count; i++)
            canonical[i] = canonical[i] with { DocId = i };
        return canonical;
    }

    private static bool AreExactAliases(IndexedSymbol left, IndexedSymbol right) =>
        string.Equals(left.SymbolId, right.SymbolId, StringComparison.Ordinal) &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(left.Signature, right.Signature, StringComparison.Ordinal) &&
        string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) &&
        string.Equals(left.Language, right.Language, StringComparison.Ordinal) &&
        left.StartLine == right.StartLine &&
        left.EndLine == right.EndLine &&
        string.Equals(left.ParentId, right.ParentId, StringComparison.Ordinal) &&
        left.IsTest == right.IsTest &&
        left.TestContainer == right.TestContainer &&
        left.TestLifecycle == right.TestLifecycle &&
        string.Equals(left.TestEvidenceStatus, right.TestEvidenceStatus, StringComparison.Ordinal) &&
        string.Equals(left.TestEvidenceReason, right.TestEvidenceReason, StringComparison.Ordinal) &&
        string.Equals(left.Visibility, right.Visibility, StringComparison.Ordinal);

    private static int CompareDeterministically(IndexedSymbol left, IndexedSymbol right)
    {
        int byPath = StringComparer.Ordinal.Compare(left.FilePath, right.FilePath);
        if (byPath != 0)
            return byPath;

        int byStartLine = left.StartLine.CompareTo(right.StartLine);
        if (byStartLine != 0)
            return byStartLine;

        int bySymbolId = StringComparer.Ordinal.Compare(left.SymbolId, right.SymbolId);
        if (bySymbolId != 0)
            return bySymbolId;

        return left.DocId.CompareTo(right.DocId);
    }
}
