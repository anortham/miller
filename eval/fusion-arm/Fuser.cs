using Miller.Core.Search;

namespace FusionArm;

/// <summary>The swept fusion parameters plus the fixed doc truncation. <see cref="ConceptualRatio"/> is the
/// semantic multiplier used for Conceptual and forced-hybrid routes; SymbolLookup/Mixed keep the frozen
/// <c>fusion-v1</c> constants.</summary>
public sealed record FusionConfig(double ConceptualRatio, int RankConstant, bool ForcedHybrid, int TopDocs = 10);

/// <summary>Whether a query fuses or emits lexical order untouched.</summary>
public enum FusionMode { LexicalPassthrough, Fuse }

/// <summary>The routing decision for one query: how to combine the arms, and the weights fusion uses when it does.</summary>
public sealed record FusionPlan(FusionMode Mode, FusionWeights Weights);

/// <summary>
/// The offline fused arm. Routing IS <see cref="SemanticQueryPolicy"/> and fusion IS <see cref="RrfFusion"/> — this
/// only marshals the arm's per-query JSON onto those production types and collapses the fused symbol order back to
/// the retrieval-eval doc vocabulary.
/// </summary>
public static class Fuser
{
    /// <summary>Decides how <paramref name="query"/> combines its arms. <c>--forced-hybrid</c> bypasses
    /// <see cref="SemanticQueryPolicy.Route"/> and fuses everything under Conceptual weights; otherwise the route is
    /// honored and lexical-only queries pass through untouched.</summary>
    public static FusionPlan Plan(string? query, IReadOnlyList<ArmInputRow> lexical, FusionConfig config)
    {
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(config);

        if (config.ForcedHybrid)
            return new FusionPlan(FusionMode.Fuse, ConceptualWeights(config));

        SemanticQueryRoute route = SemanticQueryPolicy.Route(query, EvidenceFrom(lexical));
        if (!route.IsHybrid)
            return new FusionPlan(FusionMode.LexicalPassthrough, default);

        FusionWeights weights = route.HybridClass == SemanticFusionClass.Conceptual
            ? ConceptualWeights(config)
            : RrfFusion.WeightsFor(route.HybridClass);
        return new FusionPlan(FusionMode.Fuse, weights);
    }

    /// <summary>Applies <paramref name="plan"/> and returns the top ranked <c>doc_id</c>s, collapsed from the fused
    /// symbol order after fusion and deduped keeping each doc's best fused rank.</summary>
    public static IReadOnlyList<string> Apply(
        FusionPlan plan,
        IReadOnlyList<ArmInputRow> lexical,
        IReadOnlyList<ArmInputRow> semantic,
        FusionConfig config)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(semantic);
        ArgumentNullException.ThrowIfNull(config);

        if (plan.Mode == FusionMode.LexicalPassthrough)
            return CollapseDocs(lexical.Select(static row => row.DocId), config.TopDocs);

        var lexicalCandidates = lexical.Select(ToCandidate).ToList();
        var semanticCandidates = semantic.Select(ToSemanticCandidate).ToList();
        IReadOnlyList<FusedCandidate> fused =
            RrfFusion.Fuse(lexicalCandidates, semanticCandidates, plan.Weights, config.RankConstant);
        return CollapseDocs(fused.Select(static hit => hit.Candidate.FilePath), config.TopDocs);
    }

    static FusionWeights ConceptualWeights(FusionConfig config) => new(1.0, config.ConceptualRatio);

    static LexicalEvidence EvidenceFrom(IReadOnlyList<ArmInputRow> lexical) => new(
        HitCount: lexical.Count,
        TopScore: lexical.Count > 0 ? lexical[0].Score : 0,
        RunnerUpScore: lexical.Count > 1 ? lexical[1].Score : 0);

    static SymbolCandidate ToCandidate(ArmInputRow row) => new(
        DocId: 0,
        SymbolId: row.SymbolId,
        Name: row.SymbolId,
        Signature: null,
        Kind: "",
        FilePath: row.DocId,
        StartLine: 0,
        Score: row.Score);

    static SemanticRankedCandidate ToSemanticCandidate(ArmInputRow row) => new(
        ToCandidate(row),
        row.Rank ?? throw new InvalidDataException($"semantic row for symbol '{row.SymbolId}' is missing its rank"));

    static IReadOnlyList<string> CollapseDocs(IEnumerable<string> orderedDocs, int topDocs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ranked = new List<string>(topDocs);
        foreach (string doc in orderedDocs)
        {
            if (ranked.Count >= topDocs) break;
            if (seen.Add(doc)) ranked.Add(doc);
        }

        return ranked;
    }
}
