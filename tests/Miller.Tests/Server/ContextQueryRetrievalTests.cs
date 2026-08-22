using Miller.Core.Graph;
using Miller.Indexing;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>Pins the per-call retrieval memo: one retrieval per distinct question, and never across indexes.</summary>
public sealed class ContextQueryRetrievalTests
{
    private static MillerRepositoryIndex IndexWith(string name, string symbolId) =>
        MillerRepositoryIndex.Build(
            [
                new IndexedSymbol(
                    0,
                    symbolId,
                    name,
                    $"class {name}",
                    "class",
                    "csharp",
                    $"src/{name}.cs",
                    1,
                    20,
                    null,
                    false),
            ],
            Array.Empty<GraphEdge>());

    [Fact]
    public void Collect_TheSameQuestionTwice_RunsOneRetrieval()
    {
        var retrieval = new ContextQueryRetrieval(IndexWith("OrderService", "00000000000000000000000000000101"));

        SymbolCandidateSet first = retrieval.Collect("OrderService", limit: 10, excludeTests: false);
        SymbolCandidateSet second = retrieval.Collect("OrderService", limit: 10, excludeTests: false);

        Assert.Same(first, second);
        Assert.Equal(1, retrieval.RetrievalCount);
    }

    [Theory]
    [InlineData("OrderService", 2, false)]
    [InlineData("OrderService", 10, true)]
    [InlineData("OrderRepo", 10, false)]
    public void Collect_ADifferentArgument_RunsASecondRetrieval(string query, int limit, bool excludeTests)
    {
        var retrieval = new ContextQueryRetrieval(IndexWith("OrderService", "00000000000000000000000000000102"));

        _ = retrieval.Collect("OrderService", limit: 10, excludeTests: false);
        _ = retrieval.Collect(query, limit, excludeTests);

        // Every argument the retrieval reads is part of the key, so no caller is served an answer to a
        // different question.
        Assert.Equal(2, retrieval.RetrievalCount);
    }

    [Fact]
    public void For_TheSameIndex_ReusesTheMemo()
    {
        MillerRepositoryIndex index = IndexWith("OrderService", "00000000000000000000000000000103");
        var retrieval = new ContextQueryRetrieval(index);
        _ = retrieval.Collect("OrderService", limit: 10, excludeTests: false);

        ContextQueryRetrieval reused = ContextQueryRetrieval.For(index, retrieval);

        Assert.Same(retrieval, reused);
        _ = reused.Collect("OrderService", limit: 10, excludeTests: false);
        Assert.Equal(1, reused.RetrievalCount);
    }

    [Fact]
    public void For_ADifferentIndex_StartsAFreshMemo()
    {
        MillerRepositoryIndex first = IndexWith("OrderService", "00000000000000000000000000000104");
        MillerRepositoryIndex second = IndexWith("OrderRepo", "00000000000000000000000000000105");
        var retrieval = new ContextQueryRetrieval(first);
        _ = retrieval.Collect("OrderRepo", limit: 10, excludeTests: false);

        ContextQueryRetrieval other = ContextQueryRetrieval.For(second, retrieval);

        // The memo key carries no index, so reusing one memo across indexes would answer with the first
        // index's rows. The factory refuses instead.
        Assert.NotSame(retrieval, other);
        Assert.Same(second, other.Index);
        SymbolCandidateSet collected = other.Collect("OrderRepo", limit: 10, excludeTests: false);
        Assert.Equal(
            "00000000000000000000000000000105",
            Assert.Single(collected.Candidates).SymbolId);
    }
}
