using Miller.Core.Search;
using Xunit;

namespace Miller.Tests.Search;

public sealed class SearchRelaxationTests
{
    [Theory]
    [InlineData("SearchWorkspace", 1)]
    [InlineData("search workspace", 2)]
    [InlineData("Type::Member", 2)]
    [InlineData("search search", 1)]
    public void DistinctTermCount_UsesLogicalQueryTerms(string query, int expected) =>
        Assert.Equal(expected, SearchRelaxation.DistinctTermCount(query));

    [Theory]
    [InlineData(1, 0, 6, false)]
    [InlineData(2, 6, 6, false)]
    [InlineData(2, 5, 6, true)]
    [InlineData(3, 0, 1, true)]
    public void Decide_RelaxesOnlyMultiTermQueriesThatCannotFillTheRequestedPage(
        int distinctTerms,
        int strictVisibleResults,
        int requestedLimit,
        bool expectedRelaxed)
    {
        SearchRelaxationDecision decision =
            SearchRelaxation.Decide(distinctTerms, strictVisibleResults, requestedLimit);

        Assert.Equal(
            distinctTerms > 1 ? SearchMode.And : SearchMode.Or,
            decision.PrimaryMode);
        Assert.Equal(expectedRelaxed, decision.Relaxed);
        Assert.Equal(expectedRelaxed ? SearchMode.Or : (SearchMode?)null, decision.FallbackMode);
    }

    [Fact]
    public void Merge_KeepsStrictOrderThenAddsUniqueRelaxedRows()
    {
        SymbolCandidate[] strict =
        [
            Candidate(2, 8.0),
            Candidate(1, 7.0),
        ];
        SymbolCandidate[] relaxed =
        [
            Candidate(1, 12.0),
            Candidate(3, 6.0),
            Candidate(4, 5.0),
        ];

        IReadOnlyList<SymbolCandidate> merged =
            SearchRelaxation.Merge(strict, relaxed, limit: 3);

        Assert.Equal([2, 1, 3], merged.Select(row => row.DocId));
    }

    private static SymbolCandidate Candidate(int docId, double score) =>
        new(
            docId,
            docId.ToString("x32"),
            $"Symbol{docId}",
            null,
            "class",
            $"src/Symbol{docId}.cs",
            1,
            score);
}
