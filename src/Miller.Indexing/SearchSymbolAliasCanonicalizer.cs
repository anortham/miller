namespace Miller.Indexing;

internal static class SearchSymbolAliasCanonicalizer
{
    public static IReadOnlyList<IndexedSymbol> Canonicalize(IReadOnlyList<IndexedSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (symbols.Count < 2)
            return symbols;

        bool hasAliases = false;
        {
            var seen = new HashSet<string>(symbols.Count, StringComparer.Ordinal);
            foreach (IndexedSymbol symbol in symbols)
            {
                if (!seen.Add(symbol.SymbolId))
                {
                    hasAliases = true;
                    break;
                }
            }
        }

        if (!hasAliases)
            return symbols;

        var groups = new Dictionary<string, List<IndexedSymbol>>(symbols.Count, StringComparer.Ordinal);
        foreach (IndexedSymbol symbol in symbols)
        {
            if (!groups.TryGetValue(symbol.SymbolId, out List<IndexedSymbol>? group))
                groups[symbol.SymbolId] = group = new List<IndexedSymbol>(1);
            group.Add(symbol);
        }

        var canonical = new List<IndexedSymbol>(groups.Count);
        foreach ((string symbolId, List<IndexedSymbol> group) in groups)
        {
            IndexedSymbol survivor = group[0];
            for (int i = 1; i < group.Count; i++)
            {
                IndexedSymbol alias = group[i];
                if (!AreExactAliases(survivor, alias))
                    throw new InvalidDataException(BuildDivergentAliasMessage(
                        symbolId, survivor.FilePath, alias.FilePath));
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

    private static string BuildDivergentAliasMessage(string symbolId, string firstPath, string secondPath)
    {
        const string separator = "' and '";
        const string suffix = "'; aliases must match every field except DocId and FilePath.";
        string boundedId = symbolId;
        string prefix = $"Search symbol '{boundedId}' has divergent aliases at '";
        int pathBudget = 300 - prefix.Length - separator.Length - suffix.Length;
        if (pathBudget < 2)
        {
            int idBudget = 300 - "Search symbol '' has divergent aliases at '".Length - separator.Length - suffix.Length - 2;
            boundedId = Abbreviate(symbolId, Math.Max(1, idBudget));
            prefix = $"Search symbol '{boundedId}' has divergent aliases at '";
            pathBudget = 300 - prefix.Length - separator.Length - suffix.Length;
        }

        int firstPathBudget = Math.Max(1, pathBudget / 2);
        int secondPathBudget = Math.Max(1, pathBudget - firstPathBudget);
        return prefix + Abbreviate(firstPath, firstPathBudget) + separator +
            Abbreviate(secondPath, secondPathBudget) + suffix;
    }

    private static string Abbreviate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;
        if (maxLength <= 3)
            return value[..maxLength];

        int suffixLength = (maxLength - 3) / 2;
        int prefixLength = maxLength - 3 - suffixLength;
        return value[..prefixLength] + "..." + value[^suffixLength..];
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
