namespace Miller.Core.Search;

/// <summary>
/// The Okapi-BM25 scoring math shared by every Miller symbol-search backend. Pure arithmetic, zero
/// state: <see cref="MillerSearchIndex"/> (in-memory postings) and the on-disk FTS5 reader both score
/// candidates through these functions so their rankings are byte-for-byte identical given identical
/// corpus statistics. Keeping the formula in ONE place is what makes the on-disk reader's
/// ranking-parity guarantee real instead of a hand-copied near-miss.
/// </summary>
public static class Bm25
{
    /// <summary>BM25 term-frequency saturation parameter.</summary>
    public const double K1 = 1.2;

    /// <summary>BM25 document-length normalization parameter.</summary>
    public const double B = 0.75;

    /// <summary>Multiplicative boost applied once when the query exactly equals a document's name.</summary>
    public const double ExactNameBoost = 1.5;

    /// <summary>
    /// Multiplicative penalty applied to low-signal exact-name rows after <see cref="ExactNameBoost"/>.
    /// This keeps import/module rows visible for identifier queries without letting duplicate query terms
    /// in import signatures outrank the concrete definition with the same name.
    /// </summary>
    public const double ExactNameLowSignalKindPenalty = 0.75;

    /// <summary>
    /// Inverse document frequency, the non-negative "+1 inside log" probabilistic variant
    /// <c>ln(1 + (N - df + 0.5) / (df + 0.5))</c>. Unlike the classic <c>ln((N-df+0.5)/(df+0.5))</c>
    /// it never goes negative for very common terms — this is the variant Miller's ranking is tuned to,
    /// and it differs from SQLite FTS5's built-in <c>bm25()</c>, which is why ranking stays in Miller's C#.
    /// </summary>
    /// <param name="documentCount">N — total documents in the corpus.</param>
    /// <param name="documentFrequency">df — documents containing the term.</param>
    public static double Idf(int documentCount, int documentFrequency)
        => Math.Log(1.0 + (documentCount - documentFrequency + 0.5) / (documentFrequency + 0.5));

    /// <summary>
    /// The per-term BM25 contribution for one document:
    /// <c>idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * docLen / avgdl))</c>. A document's total score
    /// is the sum of this over the distinct query terms it matches; an exact name match then multiplies
    /// the sum by <see cref="ExactNameBoost"/>.
    /// </summary>
    /// <param name="idf">Pre-computed <see cref="Idf"/> for the term.</param>
    /// <param name="termFrequency">tf — occurrences of the term in this document's token stream.</param>
    /// <param name="documentLength">docLen — total tokens (incl. component splits + duplicates) in the document.</param>
    /// <param name="averageDocumentLength">avgdl — mean document length across the corpus.</param>
    public static double TermScore(double idf, int termFrequency, int documentLength, double averageDocumentLength)
    {
        double tf = termFrequency;
        return idf * (tf * (K1 + 1)) /
               (tf + K1 * (1 - B + B * documentLength / averageDocumentLength));
    }

    /// <summary>
    /// Apply Miller's exact-name ranking adjustments. <paramref name="normalizedQuery"/> must be the
    /// trimmed, lowercased query string used by the caller for all documents in the result set.
    /// </summary>
    public static double ApplyExactNameAdjustments(
        double score,
        string documentName,
        string documentKind,
        string normalizedQuery)
    {
        if (!string.Equals(documentName.ToLowerInvariant(), normalizedQuery, StringComparison.Ordinal))
            return score;

        score *= ExactNameBoost;
        if (IsLowSignalKind(documentKind))
            score *= ExactNameLowSignalKindPenalty;

        return score;
    }

    private static bool IsLowSignalKind(string kind) =>
        string.Equals(kind, "import", StringComparison.Ordinal) ||
        string.Equals(kind, "module", StringComparison.Ordinal);
}
