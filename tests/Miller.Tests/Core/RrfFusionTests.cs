using Miller.Core.Search;
using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Core;

public sealed class RrfFusionTests
{
    [Fact]
    public void FusionProfile_MatchesTheArtifactContractProfile()
    {
        Assert.Equal(MillerSemanticContract.FusionProfile, RrfFusion.FusionProfile);
        Assert.Equal("fusion-v1", RrfFusion.FusionProfile);
        Assert.Equal(60, RrfFusion.RankConstant);
    }

    [Theory]
    [InlineData(SemanticFusionClass.SymbolLookup, 1.0, 0.3)]
    [InlineData(SemanticFusionClass.Conceptual, 0.5, 1.0)]
    [InlineData(SemanticFusionClass.Mixed, 0.8, 0.8)]
    public void WeightsFor_MatchesTheFrozenFusionV1Profile(
        SemanticFusionClass fusionClass, double lexical, double semantic)
    {
        FusionWeights weights = RrfFusion.WeightsFor(fusionClass);

        Assert.Equal(lexical, weights.Lexical);
        Assert.Equal(semantic, weights.Semantic);
    }

    [Fact]
    public void Fuse_ScoresALexicalOnlyHitByItsWeightedReciprocalRank()
    {
        IReadOnlyList<FusedCandidate> fused = RrfFusion.Fuse(
            [Lexical("a", 9.0)],
            [],
            new FusionWeights(1.0, 0.3));

        FusedCandidate only = Assert.Single(fused);
        Assert.Equal(1.0 / 61, only.RrfScore, 10);
        Assert.Equal(1, only.LexicalRank);
        Assert.Null(only.SemanticRank);
    }

    [Fact]
    public void Fuse_SumsBothArmsWhenOneSymbolAppearsInEach()
    {
        IReadOnlyList<FusedCandidate> fused = RrfFusion.Fuse(
            [Lexical("a", 9.0), Lexical("b", 8.0)],
            [Semantic("b", 1)],
            new FusionWeights(0.8, 0.8));

        FusedCandidate b = fused.Single(candidate => candidate.Candidate.SymbolId == "b");
        Assert.Equal((0.8 / 62) + (0.8 / 61), b.RrfScore, 10);
        Assert.Equal(2, b.LexicalRank);
        Assert.Equal(1, b.SemanticRank);
    }

    [Fact]
    public void Fuse_KeepsTheLexicalRowForASymbolBothArmsFoundSoScoreStaysTheLexicalScore()
    {
        var semanticView = new SymbolCandidate(99, "a", "A", "sig-from-semantic", "class", "other/A.cs", 5, 0);

        IReadOnlyList<FusedCandidate> fused = RrfFusion.Fuse(
            [Lexical("a", 9.0)],
            [new SemanticRankedCandidate(semanticView, 1)],
            new FusionWeights(0.8, 0.8));

        FusedCandidate only = Assert.Single(fused);
        Assert.Equal(9.0, only.Candidate.Score);
        Assert.Equal("src/a.cs", only.Candidate.FilePath);
    }

    [Fact]
    public void Fuse_ExtendsTheListWithSemanticOnlySymbols()
    {
        IReadOnlyList<FusedCandidate> fused = RrfFusion.Fuse(
            [Lexical("a", 9.0)],
            [Semantic("z", 1)],
            new FusionWeights(0.5, 1.0));

        Assert.Equal(["z", "a"], fused.Select(candidate => candidate.Candidate.SymbolId));
        FusedCandidate z = fused[0];
        Assert.Null(z.LexicalRank);
        Assert.Equal(1, z.SemanticRank);
        Assert.Equal(0, z.Candidate.Score);
    }

    [Fact]
    public void Fuse_DedupesRepeatedSymbolIdsBeforeRankingSoLaterRanksShiftUp()
    {
        IReadOnlyList<FusedCandidate> fused = RrfFusion.Fuse(
            [Lexical("a", 9.0), Lexical("a", 1.0), Lexical("b", 8.0)],
            [],
            new FusionWeights(1.0, 0.3));

        Assert.Equal(["a", "b"], fused.Select(candidate => candidate.Candidate.SymbolId));
        Assert.Equal(9.0, fused[0].Candidate.Score);
        Assert.Equal(2, fused[1].LexicalRank);
    }

    [Fact]
    public void Fuse_DedupesTheSemanticArmByFirstRankSeen()
    {
        IReadOnlyList<FusedCandidate> fused = RrfFusion.Fuse(
            [],
            [Semantic("z", 1), Semantic("z", 2)],
            new FusionWeights(0.5, 1.0));

        FusedCandidate only = Assert.Single(fused);
        Assert.Equal(1, only.SemanticRank);
    }

    [Fact]
    public void Fuse_BreaksScoreTiesByLexicalScoreThenSymbolId()
    {
        IReadOnlyList<FusedCandidate> fused = RrfFusion.Fuse(
            [],
            [Semantic("b", 1), Semantic("a", 1), Semantic("c", 1)],
            new FusionWeights(0.5, 1.0));

        Assert.Equal(["a", "b", "c"], fused.Select(candidate => candidate.Candidate.SymbolId));
    }

    [Fact]
    public void Fuse_PrefersTheHigherLexicalScoreWhenFusedScoresTie()
    {
        var low = new SymbolCandidate(1, "a", "A", null, "class", "src/A.cs", 1, 1.0);
        var high = new SymbolCandidate(2, "b", "B", null, "class", "src/B.cs", 1, 5.0);

        IReadOnlyList<FusedCandidate> fused = RrfFusion.Fuse(
            [],
            [new SemanticRankedCandidate(low, 1), new SemanticRankedCandidate(high, 1)],
            new FusionWeights(0.5, 1.0));

        Assert.Equal(["b", "a"], fused.Select(candidate => candidate.Candidate.SymbolId));
    }

    [Fact]
    public void Fuse_WithNoSemanticHitsPreservesLexicalOrder()
    {
        IReadOnlyList<FusedCandidate> fused = RrfFusion.Fuse(
            [Lexical("a", 9.0), Lexical("b", 8.0), Lexical("c", 7.0)],
            [],
            new FusionWeights(1.0, 0.3));

        Assert.Equal(["a", "b", "c"], fused.Select(candidate => candidate.Candidate.SymbolId));
    }

    [Fact]
    public void Fuse_UnderConceptualWeightsLetsATopSemanticHitOutrankTheTopLexicalHit()
    {
        IReadOnlyList<FusedCandidate> fused = RrfFusion.Fuse(
            [Lexical("a", 9.0)],
            [Semantic("z", 1)],
            RrfFusion.WeightsFor(SemanticFusionClass.Conceptual));

        Assert.Equal("z", fused[0].Candidate.SymbolId);
    }

    [Fact]
    public void Fuse_UnderSymbolLookupWeightsKeepsTheTopLexicalHitOnTop()
    {
        IReadOnlyList<FusedCandidate> fused = RrfFusion.Fuse(
            [Lexical("a", 9.0)],
            [Semantic("z", 1)],
            RrfFusion.WeightsFor(SemanticFusionClass.SymbolLookup));

        Assert.Equal("a", fused[0].Candidate.SymbolId);
    }

    [Fact]
    public void Fuse_IsDeterministicAcrossRepeatedRuns()
    {
        IReadOnlyList<SymbolCandidate> lexical = [Lexical("a", 9.0), Lexical("b", 9.0), Lexical("c", 9.0)];
        IReadOnlyList<SemanticRankedCandidate> semantic = [Semantic("c", 1), Semantic("a", 2)];
        FusionWeights weights = RrfFusion.WeightsFor(SemanticFusionClass.Mixed);

        Assert.Equal(
            RrfFusion.Fuse(lexical, semantic, weights).Select(candidate => candidate.Candidate.SymbolId),
            RrfFusion.Fuse(lexical, semantic, weights).Select(candidate => candidate.Candidate.SymbolId));
    }

    [Fact]
    public void Fuse_RejectsANonPositiveRankConstant() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RrfFusion.Fuse([Lexical("a", 1.0)], [], new FusionWeights(1.0, 0.3), rankConstant: 0));

    private static SymbolCandidate Lexical(string symbolId, double score) =>
        new(symbolId.GetHashCode(StringComparison.Ordinal), symbolId, symbolId.ToUpperInvariant(), null, "method",
            $"src/{symbolId}.cs", 1, score);

    private static SemanticRankedCandidate Semantic(string symbolId, int rank) =>
        new(
            new SymbolCandidate(0, symbolId, symbolId.ToUpperInvariant(), null, "method", $"src/{symbolId}.cs", 1, 0),
            rank);
}
