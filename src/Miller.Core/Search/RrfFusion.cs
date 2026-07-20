namespace Miller.Core.Search;

/// <summary>
/// The per-arm multipliers one fusion class applies to its reciprocal-rank contributions.
/// </summary>
/// <param name="Lexical">Multiplier on the lexical arm's reciprocal rank.</param>
/// <param name="Semantic">Multiplier on the semantic arm's reciprocal rank.</param>
public readonly record struct FusionWeights(double Lexical, double Semantic);

/// <summary>
/// One semantic hit already resolved to the symbol row rendering reads, plus its 1-based rank within the
/// semantic arm. Resolution happens in the caller because it needs an index; fusion stays pure.
/// </summary>
public sealed record SemanticRankedCandidate(SymbolCandidate Candidate, int Rank);

/// <summary>
/// One fused row: the candidate to render, its fused score, and the rank each arm gave it. A null rank means
/// that arm did not return the symbol at all, which is what lets rendering report per-arm provenance without
/// re-deriving it.
/// </summary>
public sealed record FusedCandidate(SymbolCandidate Candidate, double RrfScore, int? LexicalRank, int? SemanticRank);

/// <summary>
/// Weighted reciprocal-rank fusion of the lexical and semantic arms, profile <c>fusion-v1</c>.
/// </summary>
/// <remarks>
/// <para><b>Ranks, never scores.</b> The two arms score in incomparable spaces — BM25 magnitudes depend on
/// corpus statistics and cosine similarity does not — so only the ordinal position each arm assigned is read.
/// Mixing the raw scores would make the blend drift with corpus size.</para>
/// <para><b>Lexical rows win identity ties.</b> When both arms return a symbol, the lexical candidate is the
/// one rendered, so <see cref="SymbolCandidate.Score"/> keeps meaning "lexical score" in every mode.</para>
/// <para>Ordering is total and content-derived — fused score, then lexical score, then symbol id — so two runs
/// of one query over one artifact agree exactly.</para>
/// </remarks>
public static class RrfFusion
{
    /// <summary>Frozen identifier for these constants; bump when weights or the rank constant change.</summary>
    public const string FusionProfile = "fusion-v1";

    /// <summary>The RRF damping constant: how far down a list a hit still contributes meaningfully.</summary>
    public const int RankConstant = 60;

    /// <summary>The frozen <c>fusion-v1</c> weights for <paramref name="fusionClass"/> (design §6.2).</summary>
    public static FusionWeights WeightsFor(SemanticFusionClass fusionClass) => fusionClass switch
    {
        SemanticFusionClass.SymbolLookup => new FusionWeights(1.0, 0.3),
        SemanticFusionClass.Conceptual => new FusionWeights(0.5, 1.0),
        SemanticFusionClass.Mixed => new FusionWeights(0.8, 0.8),
        _ => throw new ArgumentOutOfRangeException(nameof(fusionClass)),
    };

    /// <summary>
    /// Fuses the two arms into one ordered list. Each arm is deduped by symbol id first — first occurrence
    /// wins and later ranks shift up — so a repeated symbol cannot buy extra weight for itself.
    /// </summary>
    public static IReadOnlyList<FusedCandidate> Fuse(
        IReadOnlyList<SymbolCandidate> lexical,
        IReadOnlyList<SemanticRankedCandidate> semantic,
        FusionWeights weights,
        int rankConstant = RankConstant)
    {
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(semantic);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rankConstant);

        var order = new List<string>(lexical.Count + semantic.Count);
        var rows = new Dictionary<string, Row>(lexical.Count + semantic.Count, StringComparer.Ordinal);

        int lexicalRank = 0;
        foreach (SymbolCandidate candidate in lexical)
        {
            if (rows.ContainsKey(candidate.SymbolId))
                continue;
            order.Add(candidate.SymbolId);
            rows.Add(candidate.SymbolId, new Row(candidate, ++lexicalRank, null));
        }

        var seenSemantic = new HashSet<string>(semantic.Count, StringComparer.Ordinal);
        foreach (SemanticRankedCandidate hit in semantic)
        {
            if (!seenSemantic.Add(hit.Candidate.SymbolId))
                continue;

            if (rows.TryGetValue(hit.Candidate.SymbolId, out Row? existing))
            {
                rows[hit.Candidate.SymbolId] = existing with { SemanticRank = hit.Rank };
                continue;
            }

            order.Add(hit.Candidate.SymbolId);
            rows.Add(hit.Candidate.SymbolId, new Row(hit.Candidate, null, hit.Rank));
        }

        var fused = new List<FusedCandidate>(order.Count);
        foreach (string symbolId in order)
        {
            Row row = rows[symbolId];
            double score =
                Contribution(weights.Lexical, row.LexicalRank, rankConstant) +
                Contribution(weights.Semantic, row.SemanticRank, rankConstant);
            fused.Add(new FusedCandidate(row.Candidate, score, row.LexicalRank, row.SemanticRank));
        }

        fused.Sort(static (left, right) =>
        {
            int byFused = right.RrfScore.CompareTo(left.RrfScore);
            if (byFused != 0)
                return byFused;

            int byLexicalScore = right.Candidate.Score.CompareTo(left.Candidate.Score);
            return byLexicalScore != 0
                ? byLexicalScore
                : string.CompareOrdinal(left.Candidate.SymbolId, right.Candidate.SymbolId);
        });

        return fused;
    }

    private static double Contribution(double weight, int? rank, int rankConstant) =>
        rank is { } value ? weight / (rankConstant + value) : 0;

    private sealed record Row(SymbolCandidate Candidate, int? LexicalRank, int? SemanticRank);
}
