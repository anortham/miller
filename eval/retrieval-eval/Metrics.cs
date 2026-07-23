namespace RetrievalEval;

/// <summary>Pure ranking-metric math over a ranked doc-id list and a graded relevance map.</summary>
public static class Metrics
{
    /// <summary>
    /// Fraction of the query's relevant docs that appear in the first <paramref name="k"/> ranked entries.
    /// Returns 0 when the query has no relevant docs (negative queries are scored by <see cref="Scorer"/>).
    /// </summary>
    public static double RecallAtK(IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> relevant, int k)
    {
        if (relevant.Count == 0) return 0.0;

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var docId in Cutoff(ranked, k))
        {
            if (relevant.ContainsKey(docId)) found.Add(docId);
        }

        return (double)found.Count / relevant.Count;
    }

    /// <summary>
    /// Graded nDCG at <paramref name="k"/>: exponential gain (2^grade - 1) with a log2 position discount,
    /// normalized by the DCG of the ideal ordering (grades sorted descending) truncated at the same cutoff.
    /// </summary>
    public static double NdcgAtK(IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> relevant, int k)
    {
        if (relevant.Count == 0) return 0.0;

        var ideal = relevant.Values.OrderByDescending(g => g).Take(k).ToArray();
        var idcg = 0.0;
        for (var i = 0; i < ideal.Length; i++) idcg += Gain(ideal[i]) / Discount(i);
        if (idcg <= 0.0) return 0.0;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dcg = 0.0;
        var position = 0;
        foreach (var docId in Cutoff(ranked, k))
        {
            if (relevant.TryGetValue(docId, out var grade) && seen.Add(docId)) dcg += Gain(grade) / Discount(position);
            position++;
        }

        return dcg / idcg;
    }

    /// <summary>Reciprocal rank of the first ranked document whose graded relevance is greater than zero.</summary>
    public static double ReciprocalRank(
        IReadOnlyList<string> ranked,
        IReadOnlyDictionary<string, int> relevant)
    {
        for (var i = 0; i < ranked.Count; i++)
        {
            if (relevant.TryGetValue(ranked[i], out var grade) && grade > 0)
                return 1.0 / (i + 1);
        }

        return 0.0;
    }

    static IEnumerable<string> Cutoff(IReadOnlyList<string> ranked, int k) => ranked.Take(Math.Max(0, k));

    static double Gain(int grade) => Math.Pow(2, grade) - 1;

    static double Discount(int zeroBasedPosition) => Math.Log2(zeroBasedPosition + 2);
}
