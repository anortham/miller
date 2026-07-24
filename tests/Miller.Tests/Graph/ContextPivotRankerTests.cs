using Miller.Core.Graph;
using Xunit;

namespace Miller.Tests.Graph;

public sealed class ContextPivotRankerTests
{
    [Fact]
    public void Rank_ExplicitAnchorBeatsHigherRetrievalRank()
    {
        ContextPivot[] ranked = [.. ContextPivotRanker.Rank(
            [
                new ContextPivotSignal("lexical", 1, 10, 0, 1),
                new ContextPivotSignal("entry", 5, 2, 100, 2),
            ],
            4)];

        Assert.Equal(["entry", "lexical"], ranked.Select(static pivot => pivot.SymbolId));
    }

    [Fact]
    public void Rank_LineDistanceDisambiguatesEqualStackFrameAnchors()
    {
        ContextPivot[] ranked = [.. ContextPivotRanker.Rank(
            [
                new ContextPivotSignal("far", 1, 10, 90, 1, LineDistance: 20),
                new ContextPivotSignal("near", 2, 8, 90, 1, LineDistance: 0),
            ],
            4)];

        Assert.Equal(["near", "far"], ranked.Select(static pivot => pivot.SymbolId));
    }

    [Fact]
    public void Rank_MergesSignalsWithoutDuplicatingSymbol()
    {
        ContextPivot pivot = Assert.Single(ContextPivotRanker.Rank(
            [
                new ContextPivotSignal("same", 4, 2, 0, 4),
                new ContextPivotSignal("same", 8, 7, 80, 1, LineDistance: 3),
            ],
            4));

        Assert.Equal(4, pivot.RetrievalRank);
        Assert.Equal(7, pivot.RetrievalScore);
        Assert.Equal(80, pivot.AnchorStrength);
        Assert.Equal(1, pivot.AnchorOrder);
        Assert.Equal(3, pivot.LineDistance);
    }

    [Fact]
    public void Rank_UsesStableSymbolIdTieBreakAndLimit()
    {
        ContextPivot[] ranked = [.. ContextPivotRanker.Rank(
            [
                new ContextPivotSignal("c", 1, 1, 0, 1),
                new ContextPivotSignal("a", 1, 1, 0, 1),
                new ContextPivotSignal("b", 1, 1, 0, 1),
            ],
            2)];

        Assert.Equal(["a", "b"], ranked.Select(static pivot => pivot.SymbolId));
    }

    [Fact]
    public void Rank_DiversifiesEquivalentNamesAndFilesBeforeFilling()
    {
        ContextPivot[] ranked = [.. ContextPivotRanker.Rank(
            [
                new ContextPivotSignal("test-rust", 1, 10, 20, 1, DiversityKey: "testcanload", FilePath: "rust.rs"),
                new ContextPivotSignal("test-go", 2, 9, 20, 2, DiversityKey: "testcanload", FilePath: "go.go"),
                new ContextPivotSignal("binding", 3, 8, 20, 3, DiversityKey: "language", FilePath: "rust.rs"),
                new ContextPivotSignal("grammar", 4, 7, 20, 4, DiversityKey: "rules", FilePath: "grammar.js"),
            ],
            3)];

        Assert.Equal(["test-rust", "grammar", "binding"], ranked.Select(static pivot => pivot.SymbolId));
    }

    [Fact]
    public void Rank_UsesOneTestPivotBeforeProductionEvidence()
    {
        ContextPivot[] ranked = [.. ContextPivotRanker.Rank(
            [
                new ContextPivotSignal("test-a", 1, 10, 30, 1, DiversityKey: "testa", FilePath: "a.test", IsTest: true),
                new ContextPivotSignal("test-b", 2, 9, 30, 2, DiversityKey: "testb", FilePath: "b.test", IsTest: true),
                new ContextPivotSignal("production", 3, 8, 10, 3, DiversityKey: "production", FilePath: "src.cs"),
            ],
            2)];

        Assert.Equal(["test-a", "production"], ranked.Select(static pivot => pivot.SymbolId));
    }

    [Fact]
    public void Rank_DoesNotLetFileDiversityDisplaceStrongerSameFileEvidence()
    {
        ContextPivot[] ranked = [.. ContextPivotRanker.Rank(
            [
                new ContextPivotSignal("rust-test", 1, 10, 30, 1, DiversityKey: "test", FilePath: "rust.rs", IsTest: true),
                new ContextPivotSignal("python-binding", 2, 9, 20, 2, DiversityKey: "python", FilePath: "python.c"),
                new ContextPivotSignal("rust-language", 3, 8, 13, 3, DiversityKey: "language", FilePath: "rust.rs"),
                new ContextPivotSignal("node-binding", 4, 7, 10, 4, DiversityKey: "node", FilePath: "node.js"),
                new ContextPivotSignal("grammar", 5, 6, 8, 5, DiversityKey: "grammar", FilePath: "grammar.js"),
            ],
            4)];

        Assert.Equal(
            ["rust-test", "python-binding", "rust-language", "node-binding"],
            ranked.Select(static pivot => pivot.SymbolId));
    }
}
