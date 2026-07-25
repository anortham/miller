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

        int parentIndex = ranked.ToList().FindIndex(row => row.Candidate.SymbolId == parent.SymbolId);
        int strongestChildIndex = ranked.ToList().FindIndex(row =>
            row.Candidate.SymbolId != parent.SymbolId &&
            row.Features.FinalScore == ranked
                .Where(candidate => candidate.Candidate.SymbolId != parent.SymbolId)
                .Max(candidate => candidate.Features.FinalScore));
        Assert.True(parentIndex > strongestChildIndex);
        Assert.Equal(0, ranked[parentIndex].Features.RawScore);
        Assert.True(
            ranked[parentIndex].Features.FinalScore <
            ranked[strongestChildIndex].Features.FinalScore);
        Assert.True(ranked[parentIndex].Features.ContainerEvidence > 0);
    }

    [Fact]
    public void ExpandContainers_CapsSyntheticParents()
    {
        SymbolCandidate[] parents = Enumerable.Range(0, 12)
            .Select(index => Candidate(
                100 + index,
                $"Parent{index}",
                "class",
                $"src/Parent{index}.cs",
                0))
            .ToArray();
        SymbolCandidate[] children = parents
            .SelectMany((parent, index) => new[]
            {
                Candidate(
                    200 + index * 2,
                    $"First{index}",
                    "method",
                    parent.FilePath,
                    5) with { ParentId = parent.SymbolId },
                Candidate(
                    201 + index * 2,
                    $"Second{index}",
                    "method",
                    parent.FilePath,
                    4) with { ParentId = parent.SymbolId },
            })
            .ToArray();
        var parentById = parents.ToDictionary(parent => parent.SymbolId, StringComparer.Ordinal);

        SymbolRerankInput input = SymbolReranker.ExpandContainers(
            "first second choices",
            children,
            symbolId => parentById.GetValueOrDefault(symbolId));

        Assert.Equal(children.Length + 10, input.Candidates.Count);
        Assert.Equal(10, input.ContainerEvidence.Count);
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
