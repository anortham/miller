using RetrievalEval;
using Xunit;

namespace RetrievalEval.Tests;

public class MetricsTests
{
    [Fact]
    public void RecallAtK_counts_only_relevant_docs_inside_the_cutoff()
    {
        var relevant = new Dictionary<string, int> { ["a"] = 3, ["x"] = 2, ["y"] = 1 };
        var ranked = new[] { "a", "b", "c", "x" };

        Assert.Equal(1.0 / 3.0, Metrics.RecallAtK(ranked, relevant, 3), 1e-9);
        Assert.Equal(2.0 / 3.0, Metrics.RecallAtK(ranked, relevant, 4), 1e-9);
    }

    [Fact]
    public void RecallAtK_is_one_when_every_relevant_doc_is_inside_the_cutoff()
    {
        var relevant = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };

        Assert.Equal(1.0, Metrics.RecallAtK(["b", "z", "a"], relevant, 10), 1e-9);
    }

    [Fact]
    public void RecallAtK_ignores_duplicate_ranked_entries()
    {
        var relevant = new Dictionary<string, int> { ["a"] = 1, ["b"] = 1 };

        Assert.Equal(0.5, Metrics.RecallAtK(["a", "a", "a"], relevant, 10), 1e-9);
    }

    [Fact]
    public void RecallAtK_is_zero_with_no_hits_or_no_results()
    {
        var relevant = new Dictionary<string, int> { ["a"] = 1 };

        Assert.Equal(0.0, Metrics.RecallAtK(["q", "r"], relevant, 10), 1e-9);
        Assert.Equal(0.0, Metrics.RecallAtK([], relevant, 10), 1e-9);
    }

    [Fact]
    public void NdcgAtK_is_one_for_ideal_ordering()
    {
        var relevant = new Dictionary<string, int> { ["a"] = 3, ["b"] = 1 };

        Assert.Equal(1.0, Metrics.NdcgAtK(["a", "b", "c"], relevant, 10), 1e-9);
    }

    [Fact]
    public void NdcgAtK_is_zero_when_nothing_relevant_is_retrieved()
    {
        var relevant = new Dictionary<string, int> { ["a"] = 3 };

        Assert.Equal(0.0, Metrics.NdcgAtK(["x", "y"], relevant, 10), 1e-9);
    }

    [Fact]
    public void NdcgAtK_uses_exponential_gain_and_log2_position_discount()
    {
        var relevant = new Dictionary<string, int> { ["a"] = 3, ["c"] = 1 };
        var expected = (7.0 / 1.0 + 1.0 / 2.0) / (7.0 / 1.0 + 1.0 / Math.Log2(3.0));

        Assert.Equal(expected, Metrics.NdcgAtK(["a", "b", "c"], relevant, 10), 1e-12);
    }

    [Fact]
    public void NdcgAtK_penalizes_a_relevant_doc_pushed_past_the_cutoff()
    {
        var relevant = new Dictionary<string, int> { ["a"] = 1 };

        Assert.Equal(0.0, Metrics.NdcgAtK(["x", "y", "a"], relevant, 2), 1e-9);
        Assert.Equal(1.0 / Math.Log2(4.0), Metrics.NdcgAtK(["x", "y", "a"], relevant, 3), 1e-12);
    }

    [Fact]
    public void NdcgAtK_ideal_ordering_sorts_grades_descending()
    {
        var relevant = new Dictionary<string, int> { ["low"] = 1, ["high"] = 3 };
        var expected = (1.0 / 1.0 + 7.0 / Math.Log2(3.0)) / (7.0 / 1.0 + 1.0 / Math.Log2(3.0));

        Assert.Equal(expected, Metrics.NdcgAtK(["low", "high"], relevant, 10), 1e-12);
    }

    [Fact]
    public void ReciprocalRank_uses_the_first_positive_grade_hit()
    {
        var relevant = new Dictionary<string, int> { ["zero"] = 0, ["target"] = 2 };

        Assert.Equal(1.0 / 3.0, Metrics.ReciprocalRank(["zero", "other", "target"], relevant), 1e-12);
    }

    [Fact]
    public void ReciprocalRank_is_zero_without_a_positive_grade_hit()
    {
        var relevant = new Dictionary<string, int> { ["zero"] = 0, ["missing"] = 2 };

        Assert.Equal(0.0, Metrics.ReciprocalRank(["zero", "other"], relevant), 1e-12);
    }
}
