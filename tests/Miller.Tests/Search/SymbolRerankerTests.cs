using Miller.Core.Search;
using Xunit;

namespace Miller.Tests.Search;

public sealed class SymbolRerankerTests
{
    [Fact]
    public void Rank_ExactSourceDefinitionOutranksGeneratedAndImportCopies()
    {
        SymbolCandidate[] candidates =
        [
            Candidate(1, "SearchTool", "class", "generated/SearchTool.g.cs", 9.0),
            Candidate(2, "SearchTool", "import", "src/Imports.cs", 12.0),
            Candidate(3, "SearchTool", "class", "src/SearchTool.cs", 5.0),
        ];

        IReadOnlyList<SymbolRerankResult> ranked = SymbolReranker.Rank("SearchTool", candidates);

        Assert.Equal(3, ranked[0].Candidate.DocId);
        Assert.True(ranked[0].Features.Exactness > 0);
        Assert.True(ranked[0].Features.SourceRole > 0);
        Assert.True(ranked.Single(row => row.Candidate.DocId == 1).Features.PathRole < 0);
        Assert.True(ranked.Single(row => row.Candidate.DocId == 2).Features.SourceRole < 0);
    }

    [Fact]
    public void Rank_PhraseProximityPrefersAdjacentNameTerms()
    {
        SymbolCandidate[] candidates =
        [
            Candidate(1, "SearchWorkspaceProvider", "class", "src/SearchWorkspaceProvider.cs", 4.0),
            Candidate(2, "SearchProvider", "class", "src/SearchProvider.cs", 4.0,
                signature: "SearchProvider(IWorkspace workspace)"),
        ];

        IReadOnlyList<SymbolRerankResult> ranked =
            SymbolReranker.Rank("search workspace", candidates);

        Assert.Equal(1, ranked[0].Candidate.DocId);
        Assert.True(ranked[0].Features.PhraseProximity >
                    ranked[1].Features.PhraseProximity);
    }

    [Fact]
    public void Rank_DominantLanguageBreaksOtherwiseEqualScores()
    {
        SymbolCandidate[] candidates =
        [
            Candidate(1, "Resolve", "method", "src/resolve.rs", 4.0, language: "rust"),
            Candidate(2, "Resolve", "method", "src/Resolve.cs", 4.0, language: "csharp"),
        ];

        IReadOnlyList<SymbolRerankResult> ranked =
            SymbolReranker.Rank("resolve", candidates, dominantLanguage: "csharp");

        Assert.Equal(2, ranked[0].Candidate.DocId);
        Assert.Equal(0.5, ranked[0].Features.LanguageAffinity);
    }

    [Fact]
    public void Rank_IsDeterministicAndExposesEveryContribution()
    {
        SymbolCandidate[] candidates =
        [
            Candidate(2, "Run", "method", "src/Run.cs", 3.0),
            Candidate(1, "Run", "method", "src/Run.cs", 3.0),
        ];

        IReadOnlyList<SymbolRerankResult> first = SymbolReranker.Rank("run", candidates);
        IReadOnlyList<SymbolRerankResult> second = SymbolReranker.Rank("run", candidates);

        Assert.Equal([1, 2], first.Select(row => row.Candidate.DocId));
        Assert.Equal(first, second);
        Assert.All(first, row =>
            Assert.Equal(
                row.Features.RawScore +
                row.Features.Exactness +
                row.Features.PhraseProximity +
                row.Features.SourceRole +
                row.Features.PathRole +
                row.Features.LanguageAffinity +
                row.Features.ContainerEvidence,
                row.Features.FinalScore,
                precision: 10));
    }

    [Fact]
    public void Rank_ContainerEvidenceCanSurfaceAnUnmatchedChoicePoint()
    {
        SymbolCandidate parent = Candidate(
            0,
            "SemanticCandidateFactory",
            "class",
            "src/SemanticCandidates.cs",
            0);
        SymbolCandidate firstChild = Candidate(
            1,
            "TokenBaseline",
            "constant",
            "src/SemanticCandidates.cs",
            18) with
        {
            ParentId = parent.SymbolId,
        };
        SymbolCandidate secondChild = Candidate(
            2,
            "CreateInMemory",
            "method",
            "src/SemanticCandidates.cs",
            16) with
        {
            ParentId = parent.SymbolId,
        };
        SymbolCandidate thirdChild = Candidate(
            3,
            "CreateSqliteVector",
            "method",
            "src/SemanticCandidates.cs",
            14) with
        {
            ParentId = parent.SymbolId,
        };

        SymbolRerankInput input = SymbolReranker.ExpandContainers(
            "choose token baseline in memory or sqlite vector",
            [firstChild, secondChild, thirdChild],
            symbolId => symbolId == parent.SymbolId ? parent : null);
        IReadOnlyList<SymbolRerankResult> ranked = SymbolReranker.Rank(
            "choose token baseline in memory or sqlite vector",
            input.Candidates,
            containerEvidence: input.ContainerEvidence);

        Assert.Equal(parent.SymbolId, ranked[0].Candidate.SymbolId);
        Assert.Equal(0, ranked[0].Features.RawScore);
        Assert.True(ranked[0].Features.ContainerEvidence > 0);
    }

    private static SymbolCandidate Candidate(
        int docId,
        string name,
        string kind,
        string path,
        double score,
        string? signature = null,
        string? language = null) =>
        new(
            docId,
            docId.ToString("x32"),
            name,
            signature,
            kind,
            path,
            1,
            score,
            language);
}
