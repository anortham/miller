using Miller.SearchQuality;
using Xunit;

namespace Miller.Tests.SearchQuality;

public sealed class SearchQualityScorerTests
{
    [Fact]
    public void Score_FindsFirstMatchingExpectedHitAndComputesRankMetrics()
    {
        var searchCase = new SearchCaseSpec
        {
            Id = "flask-class",
            Repository = "flask",
            Query = "Flask",
            Expected =
            [
                new SearchExpectation { Path = "src/flask/app.py", Symbol = "Flask", Kind = "class", Line = 109 },
            ],
        };

        var hits = new[]
        {
            new SearchQualityHit { Provider = "miller", Title = "flask", Name = "flask", Kind = "property", Path = "pyproject.toml", Line = 83, Score = 9.0 },
            new SearchQualityHit { Provider = "miller", Title = "Flask", Name = "Flask", Kind = "class", Path = "src/flask/app.py", Line = 109, Score = 8.0 },
        };

        SearchCaseScore score = SearchQualityScorer.Score("miller", searchCase, hits);

        Assert.Equal(2, score.HitCount);
        Assert.Equal(2, score.MatchedRank);
        Assert.False(score.Top1);
        Assert.True(score.Top3);
        Assert.True(score.Top5);
        Assert.Equal(0.5, score.ReciprocalRank);
    }

    [Fact]
    public void Score_MatchesExpectedPathBySuffixSoAbsoluteToolOutputCanBeScored()
    {
        var searchCase = new SearchCaseSpec
        {
            Id = "absolute-path",
            Repository = "repo",
            Query = "CacheOrchestrator",
            Expected = [new SearchExpectation { Path = "MyraNext.Core/Caching/CacheOrchestrator.cs" }],
        };

        var hits = new[]
        {
            new SearchQualityHit
            {
                Provider = "eros",
                Title = "CacheOrchestrator",
                Path = "/Users/murphy/source/MyraNext/MyraNext/MyraNext.Core/Caching/CacheOrchestrator.cs",
            },
        };

        SearchCaseScore score = SearchQualityScorer.Score("eros", searchCase, hits);

        Assert.Equal(1, score.MatchedRank);
        Assert.True(score.Top1);
    }

    [Fact]
    public void Summarize_AggregatesTopKMissesAndMeanReciprocalRank()
    {
        var scores = new[]
        {
            new SearchCaseScore { Provider = "miller", Repository = "a", CaseId = "one", HitCount = 2, MatchedRank = 1, Top1 = true, Top3 = true, Top5 = true, ReciprocalRank = 1.0 },
            new SearchCaseScore { Provider = "miller", Repository = "a", CaseId = "two", HitCount = 2, MatchedRank = 4, Top1 = false, Top3 = false, Top5 = true, ReciprocalRank = 0.25 },
            new SearchCaseScore { Provider = "miller", Repository = "b", CaseId = "three", HitCount = 2, MatchedRank = null, Top1 = false, Top3 = false, Top5 = false, ReciprocalRank = 0.0 },
        };

        ProviderSummary summary = SearchQualityScorer.Summarize("miller", scores);

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Top1);
        Assert.Equal(1, summary.Top3);
        Assert.Equal(2, summary.Top5);
        Assert.Equal(1, summary.Misses);
        Assert.Equal(0.4167, summary.Mrr, precision: 4);
    }
}
