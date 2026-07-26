using Miller.Core.Search;

namespace FusionArm;

/// <summary>The swept fusion parameters plus the fixed doc truncation. <see cref="ConceptualRatio"/> is the
/// semantic multiplier used for Conceptual and forced-hybrid routes; SymbolLookup/Mixed keep the frozen
/// <c>fusion-v1</c> constants.</summary>
public sealed record FusionConfig(double ConceptualRatio, int RankConstant, bool ForcedHybrid, int TopDocs = 10);

/// <summary>Whether a query fuses or emits lexical order untouched.</summary>
public enum FusionMode { LexicalPassthrough, Fuse }

/// <summary>The routing and admission decision for one query.</summary>
public sealed record FusionPlan(
    FusionMode Mode,
    FusionWeights Weights,
    SemanticCandidateAdmission Admission,
    int PolicyVersion);

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

        SemanticCandidateAdmission admission =
            SemanticQueryPolicy.DecideAdmission(EvidenceFrom(lexical));
        if (config.ForcedHybrid)
        {
            return new FusionPlan(
                FusionMode.Fuse,
                ConceptualWeights(config),
                admission,
                SemanticQueryPolicy.PolicyVersion);
        }

        SemanticQueryRoute route = SemanticQueryPolicy.Route(query);
        if (!route.IsHybrid)
        {
            return new FusionPlan(
                FusionMode.LexicalPassthrough,
                default,
                admission,
                SemanticQueryPolicy.PolicyVersion);
        }

        FusionWeights weights = route.HybridClass == SemanticFusionClass.Conceptual
            ? ConceptualWeights(config)
            : RrfFusion.WeightsFor(route.HybridClass);
        return new FusionPlan(FusionMode.Fuse, weights, admission, SemanticQueryPolicy.PolicyVersion);
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
        IReadOnlyList<SemanticRankedCandidate> semanticCandidates =
            [.. semantic.Select(ToSemanticCandidate)];
        if (!plan.Admission.AllowsExpansion)
        {
            var lexicalIds = new HashSet<string>(
                lexicalCandidates.Select(static candidate => candidate.SymbolId),
                StringComparer.Ordinal);
            semanticCandidates =
                [.. semanticCandidates.Where(hit => lexicalIds.Contains(hit.Candidate.SymbolId))];
        }
        IReadOnlyList<FusedCandidate> fused =
            RrfFusion.Fuse(lexicalCandidates, semanticCandidates, plan.Weights, config.RankConstant);
        if (plan.Admission.ProtectedLexicalCount > 0)
            fused = ProtectLexicalPrefix(fused, lexicalCandidates, plan.Admission.ProtectedLexicalCount);
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

    static IReadOnlyList<FusedCandidate> ProtectLexicalPrefix(
        IReadOnlyList<FusedCandidate> fused,
        IReadOnlyList<SymbolCandidate> lexical,
        int protectedLexicalCount)
    {
        var byId = fused.ToDictionary(static row => row.Candidate.SymbolId, StringComparer.Ordinal);
        var protectedIds = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<FusedCandidate>(fused.Count);
        foreach (SymbolCandidate candidate in lexical.Take(protectedLexicalCount))
        {
            if (protectedIds.Add(candidate.SymbolId) &&
                byId.TryGetValue(candidate.SymbolId, out FusedCandidate? row))
            {
                ordered.Add(row);
            }
        }

        ordered.AddRange(fused.Where(row => !protectedIds.Contains(row.Candidate.SymbolId)));
        return ordered;
    }

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
