using Miller.Core.Analysis;
using Xunit;

namespace Miller.Tests.Core;

public sealed class NearDuplicateAnalyzerTests
{
    private const string OriginalBody = """
        {
            var totalCount = 0;
            foreach (var order in orders)
            {
                if (order.Status == "open" && order.Amount > 10)
                {
                    totalCount = totalCount + order.Amount;
                }
                else
                {
                    logger.Warn("skipped order");
                }
            }
            return totalCount;
        }
        """;

    private const string RenamedBody = """
        {
            var runningSum = 0;
            foreach (var invoice in invoices)
            {
                if (invoice.State == "pending" && invoice.Value > 25)
                {
                    runningSum = runningSum + invoice.Value;
                }
                else
                {
                    tracer.Warn("ignored invoice");
                }
            }
            return runningSum;
        }
        """;

    private const string UnrelatedBody = """
        {
            using var client = new HttpClient();
            var response = await client.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<Report>(payload, SerializerOptions);
        }
        """;

    [Fact]
    public void FindGroups_RenamedIdentifiersAndChangedLiterals_AreOneNearDuplicateGroup()
    {
        var groups = NearDuplicateAnalyzer.FindGroups(
        [
            new NearDuplicateInput("a", OriginalBody),
            new NearDuplicateInput("b", RenamedBody),
        ]);

        NearDuplicateGroup group = Assert.Single(groups);
        Assert.Equal(["a", "b"], group.MemberIds);
        Assert.True(group.Similarity >= NearDuplicateAnalyzer.DefaultMinSimilarity);
        Assert.True(group.Similarity <= 1.0);
    }

    [Fact]
    public void FindGroups_UnrelatedBodies_AreNotGrouped()
    {
        var groups = NearDuplicateAnalyzer.FindGroups(
        [
            new NearDuplicateInput("a", OriginalBody),
            new NearDuplicateInput("u", UnrelatedBody),
        ]);

        Assert.Empty(groups);
    }

    [Fact]
    public void FindGroups_IdenticalBodies_AreLeftToTheExactCloneSurface()
    {
        var groups = NearDuplicateAnalyzer.FindGroups(
        [
            new NearDuplicateInput("a", OriginalBody),
            new NearDuplicateInput("copy", OriginalBody),
        ]);

        Assert.Empty(groups);
    }

    [Fact]
    public void FindGroups_IdenticalBodyClass_ContributesOneRepresentativeToANearGroup()
    {
        var groups = NearDuplicateAnalyzer.FindGroups(
        [
            new NearDuplicateInput("a", OriginalBody),
            new NearDuplicateInput("a-copy", OriginalBody),
            new NearDuplicateInput("b", RenamedBody),
        ]);

        NearDuplicateGroup group = Assert.Single(groups);
        Assert.Equal(["a", "b"], group.MemberIds);
    }

    [Fact]
    public void FindGroups_BodiesBelowTheTokenFloor_AreSkipped()
    {
        var groups = NearDuplicateAnalyzer.FindGroups(
        [
            new NearDuplicateInput("tiny-a", "{ return x + 1; }"),
            new NearDuplicateInput("tiny-b", "{ return y + 2; }"),
        ]);

        Assert.Empty(groups);
    }

    [Fact]
    public void FindGroups_RepeatedRuns_ProduceIdenticalGroups()
    {
        NearDuplicateInput[] inputs =
        [
            new NearDuplicateInput("b", RenamedBody),
            new NearDuplicateInput("a", OriginalBody),
            new NearDuplicateInput("u", UnrelatedBody),
            new NearDuplicateInput("a-copy", OriginalBody),
        ];

        var first = NearDuplicateAnalyzer.FindGroups(inputs);
        var second = NearDuplicateAnalyzer.FindGroups(inputs);

        Assert.Equal(
            first.Select(g => (g.Similarity, string.Join(',', g.MemberIds))),
            second.Select(g => (g.Similarity, string.Join(',', g.MemberIds))));
        Assert.Single(first);
    }

    [Fact]
    public void FindGroups_InputOrder_DoesNotChangeTheResult()
    {
        var forward = NearDuplicateAnalyzer.FindGroups(
        [
            new NearDuplicateInput("a", OriginalBody),
            new NearDuplicateInput("b", RenamedBody),
            new NearDuplicateInput("u", UnrelatedBody),
        ]);
        var reversed = NearDuplicateAnalyzer.FindGroups(
        [
            new NearDuplicateInput("u", UnrelatedBody),
            new NearDuplicateInput("b", RenamedBody),
            new NearDuplicateInput("a", OriginalBody),
        ]);

        Assert.Equal(
            forward.Select(g => string.Join(',', g.MemberIds)),
            reversed.Select(g => string.Join(',', g.MemberIds)));
    }

    [Fact]
    public void FindGroups_HigherMinSimilarity_RejectsWeakerPairs()
    {
        var options = new NearDuplicateOptions { MinSimilarity = 1.01 };

        var groups = NearDuplicateAnalyzer.FindGroups(
        [
            new NearDuplicateInput("a", OriginalBody),
            new NearDuplicateInput("b", RenamedBody),
        ], options);

        Assert.Empty(groups);
    }

    [Fact]
    public void FindGroups_MaxGroups_BoundsTheResult()
    {
        var options = new NearDuplicateOptions { MaxGroups = 1 };

        var groups = NearDuplicateAnalyzer.FindGroups(
        [
            new NearDuplicateInput("a", OriginalBody),
            new NearDuplicateInput("b", RenamedBody),
            new NearDuplicateInput("c", UnrelatedBody),
            new NearDuplicateInput("d", UnrelatedBody.Replace("client", "fetcher", StringComparison.Ordinal)),
        ], options);

        Assert.Single(groups);
    }

    [Fact]
    public void FindGroups_WhitespaceOnlyDifferences_AreOneFullySimilarNearGroup()
    {
        string reformatted = OriginalBody.Replace("\n", "\n    ", StringComparison.Ordinal);

        var groups = NearDuplicateAnalyzer.FindGroups(
        [
            new NearDuplicateInput("a", OriginalBody),
            new NearDuplicateInput("b", reformatted),
        ]);

        NearDuplicateGroup group = Assert.Single(groups);
        Assert.Equal(["a", "b"], group.MemberIds);
        Assert.Equal(1.0, group.Similarity);
    }

    [Fact]
    public void FindGroups_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(NearDuplicateAnalyzer.FindGroups([]));
    }

    [Fact]
    public void FindGroups_TransitiveMatches_CollapseIntoOneGroupWithTheWeakestEdgeSimilarity()
    {
        string mildlyEdited = RenamedBody.Replace("tracer.Warn(\"ignored invoice\");", "tracer.Warn();", StringComparison.Ordinal);

        var groups = NearDuplicateAnalyzer.FindGroups(
        [
            new NearDuplicateInput("a", OriginalBody),
            new NearDuplicateInput("b", RenamedBody),
            new NearDuplicateInput("c", mildlyEdited),
        ]);

        NearDuplicateGroup group = Assert.Single(groups);
        Assert.Equal(["a", "b", "c"], group.MemberIds);
        Assert.True(group.Similarity < 1.0);
    }
}
