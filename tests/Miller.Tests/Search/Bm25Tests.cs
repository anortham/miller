using Miller.Core.Search;
using Xunit;

namespace Miller.Tests.Search;

/// <summary>
/// Pins the shared Okapi-BM25 scorer extracted from <see cref="MillerSearchIndex"/>. The on-disk
/// <c>FtsSymbolSearchIndex</c> re-ranks FTS5 candidates with this SAME math, so exact ranking parity
/// with the in-memory index depends on these constants and formulas not drifting. Values are computed
/// by hand from the BM25 definition (k1=1.2, b=0.75), independent of the implementation.
/// </summary>
public sealed class Bm25Tests
{
    [Fact]
    public void Constants_ArePinned()
    {
        Assert.Equal(1.2, Bm25.K1);
        Assert.Equal(0.75, Bm25.B);
        Assert.Equal(1.5, Bm25.ExactNameBoost);
    }

    [Fact]
    public void Idf_NonNegativeProbabilisticVariant_MatchesHandComputed()
    {
        // idf = ln(1 + (N - df + 0.5)/(df + 0.5)). N=2, df=1 => ln(1 + 1.5/1.5) = ln 2.
        Assert.Equal(0.6931471805599453, Bm25.Idf(documentCount: 2, documentFrequency: 1), precision: 12);
        // N=3, df=2 => ln(1 + 1.5/2.5) = ln 1.6.
        Assert.Equal(0.4700036292457356, Bm25.Idf(documentCount: 3, documentFrequency: 2), precision: 12);
    }

    [Fact]
    public void Idf_IsAlwaysNonNegative_EvenForVeryCommonTerms()
    {
        // The "+1 inside log" variant never goes negative (a term in every doc still yields idf > 0),
        // unlike the classic log((N-df+0.5)/(df+0.5)) — load-bearing for not under-scoring common terms.
        Assert.True(Bm25.Idf(documentCount: 1000, documentFrequency: 1000) >= 0.0);
    }

    [Fact]
    public void Idf_DecreasesAsDocumentFrequencyRises()
    {
        // Rarer term (lower df) must carry more weight.
        Assert.True(Bm25.Idf(100, 1) > Bm25.Idf(100, 50));
    }

    [Fact]
    public void TermScore_MatchesHandComputed_AtAverageLength()
    {
        // idf=2, tf=3, docLen=avgdl=10 => denom = 3 + 1.2*(1-0.75+0.75*1) = 4.2; numer = 2*(3*2.2)=13.2.
        Assert.Equal(3.142857142857143,
            Bm25.TermScore(idf: 2.0, termFrequency: 3, documentLength: 10, averageDocumentLength: 10),
            precision: 12);
    }

    [Fact]
    public void TermScore_PenalizesLongerThanAverageDocuments()
    {
        // idf=1, tf=1, docLen=20, avgdl=10 => denom = 1 + 1.2*(0.25+0.75*2)=3.1; numer=2.2 => 2.2/3.1.
        Assert.Equal(0.7096774193548387,
            Bm25.TermScore(idf: 1.0, termFrequency: 1, documentLength: 20, averageDocumentLength: 10),
            precision: 12);
    }

    [Fact]
    public void TermScore_RisesWithTermFrequency_ForEqualLength()
    {
        double tf1 = Bm25.TermScore(idf: 1.0, termFrequency: 1, documentLength: 5, averageDocumentLength: 5);
        double tf3 = Bm25.TermScore(idf: 1.0, termFrequency: 3, documentLength: 5, averageDocumentLength: 5);
        Assert.True(tf3 > tf1);
    }

    [Fact]
    public void TermScore_FallsWithDocumentLength_ForEqualTermFrequency()
    {
        double shortDoc = Bm25.TermScore(idf: 1.0, termFrequency: 1, documentLength: 2, averageDocumentLength: 10);
        double longDoc = Bm25.TermScore(idf: 1.0, termFrequency: 1, documentLength: 20, averageDocumentLength: 10);
        Assert.True(shortDoc > longDoc);
    }
}
