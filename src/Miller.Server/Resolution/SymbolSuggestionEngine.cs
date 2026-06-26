using Miller.Core.Search;
using Miller.Indexing;

namespace Miller.Server.Resolution;

internal static class SymbolSuggestionEngine
{
    private const int SearchCandidateMultiplier = 12;
    private const int MaxEditDistance = 2;

    public static IReadOnlyList<IndexedSymbol> Suggest(ISymbolLookupIndex index, string query, int limit)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit <= 0)
            return [];

        string normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
            return [];

        var candidates = new Dictionary<(string Name, string FilePath), IndexedSymbol>();
        foreach (IndexedSymbol symbol in ExactVariantCandidates(index, query))
            Add(symbol);

        int searchLimit = Math.Max(limit * SearchCandidateMultiplier, 24);
        foreach (SearchHit hit in index.Search(query, searchLimit, SearchMode.Or))
            Add(index.Resolve(hit.Document.DocId));

        if (candidates.Count == 0)
            return [];

        return candidates.Values
            .Select(symbol => (Symbol: symbol, Rank: Rank(symbol.Name, query, normalizedQuery)))
            .Where(candidate => candidate.Rank.Kind < SuggestionKind.Noise)
            .OrderBy(static candidate => candidate.Rank.Kind)
            .ThenBy(static candidate => candidate.Rank.Distance)
            .ThenBy(static candidate => candidate.Symbol.Name, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Symbol.FilePath, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Symbol.StartLine)
            .Take(limit)
            .Select(static candidate => candidate.Symbol)
            .ToList();

        void Add(IndexedSymbol symbol) => candidates.TryAdd((symbol.Name, symbol.FilePath), symbol);
    }

    private static IEnumerable<IndexedSymbol> ExactVariantCandidates(ISymbolLookupIndex index, string query)
    {
        var variants = new HashSet<string>(StringComparer.Ordinal) { query };
        string trimmed = query.Trim();
        if (trimmed.Length != query.Length)
            variants.Add(trimmed);

        for (int i = 0; i < trimmed.Length; i++)
            variants.Add(trimmed.Remove(i, 1));

        for (int i = 0; i < trimmed.Length - 1; i++)
        {
            char[] chars = trimmed.ToCharArray();
            (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
            variants.Add(new string(chars));
        }

        foreach (string variant in variants)
        {
            if (variant.Length == 0)
                continue;
            foreach (IndexedSymbol symbol in index.FindByName(variant))
                yield return symbol;
        }
    }

    private static SuggestionRank Rank(string candidateName, string query, string normalizedQuery)
    {
        if (string.Equals(candidateName, query, StringComparison.OrdinalIgnoreCase))
            return new SuggestionRank(SuggestionKind.CaseInsensitiveExact, 0);

        string normalizedCandidate = Normalize(candidateName);
        if (normalizedCandidate.Length == 0)
            return SuggestionRank.Noise;

        if (normalizedCandidate == normalizedQuery)
            return new SuggestionRank(SuggestionKind.NormalizedExact, 0);

        int distance = EditDistanceBounded(normalizedCandidate, normalizedQuery, MaxEditDistance);
        if (distance <= MaxEditDistance)
            return new SuggestionRank(SuggestionKind.EditDistance, distance);

        string tail = TailSegment(query);
        string normalizedTail = Normalize(tail);
        if (normalizedTail.Length >= 3 && normalizedCandidate.Contains(normalizedTail, StringComparison.Ordinal))
            return new SuggestionRank(SuggestionKind.Substring, 0);
        if (normalizedQuery.Length >= 3 && normalizedCandidate.Contains(normalizedQuery, StringComparison.Ordinal))
            return new SuggestionRank(SuggestionKind.Substring, 1);

        return SuggestionRank.Noise;
    }

    private static string TailSegment(string query)
    {
        int lastDot = query.LastIndexOf('.');
        return lastDot >= 0 && lastDot < query.Length - 1 ? query[(lastDot + 1)..] : query;
    }

    private static string Normalize(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        int written = 0;
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
                buffer[written++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..written]);
    }

    private static int EditDistanceBounded(string left, string right, int maxDistance)
    {
        if (Math.Abs(left.Length - right.Length) > maxDistance)
            return maxDistance + 1;
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;

        int[,] distances = new int[left.Length + 1, right.Length + 1];
        for (int i = 0; i <= left.Length; i++)
            distances[i, 0] = i;
        for (int j = 0; j <= right.Length; j++)
            distances[0, j] = j;

        for (int i = 1; i <= left.Length; i++)
        {
            int rowMin = int.MaxValue;
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                int deletion = distances[i - 1, j] + 1;
                int insertion = distances[i, j - 1] + 1;
                int substitution = distances[i - 1, j - 1] + cost;
                int value = Math.Min(Math.Min(deletion, insertion), substitution);
                if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1])
                    value = Math.Min(value, distances[i - 2, j - 2] + 1);
                distances[i, j] = value;
                rowMin = Math.Min(rowMin, value);
            }
            if (rowMin > maxDistance)
                return maxDistance + 1;
        }

        return distances[left.Length, right.Length];
    }

    private readonly record struct SuggestionRank(SuggestionKind Kind, int Distance)
    {
        public static SuggestionRank Noise { get; } = new(SuggestionKind.Noise, int.MaxValue);
    }

    private enum SuggestionKind
    {
        CaseInsensitiveExact = 0,
        NormalizedExact = 1,
        EditDistance = 2,
        Substring = 3,
        Noise = 4,
    }
}
