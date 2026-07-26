using System.Text;
using Miller.Core.Editing;
using Xunit;

namespace Miller.Tests.Editing;

public sealed class TextReplaceMatcherTests
{
    private static int ByteLen(string s) => Encoding.UTF8.GetByteCount(s);

    [Fact]
    public void Plan_Exact_PreservesFirstLastAllAndByteOffsets()
    {
        const string content = "cafe foo foo";

        var first = TextReplaceMatcher.Plan(content, "foo", Occurrence.First, TextMatchMode.Exact);
        var last = TextReplaceMatcher.Plan(content, "foo", Occurrence.Last, TextMatchMode.Exact);
        var all = TextReplaceMatcher.Plan(content, "foo", Occurrence.All, TextMatchMode.Exact);

        Assert.True(first.IsSuccess);
        Assert.Equal(TextMatchMode.Exact, first.MatchedMode);
        Assert.Equal(2, first.MatchCount);
        Assert.Equal(ByteLen("cafe "), Assert.Single(first.Edits).StartByte);

        Assert.True(last.IsSuccess);
        Assert.Equal(ByteLen("cafe foo "), Assert.Single(last.Edits).StartByte);

        Assert.True(all.IsSuccess);
        Assert.Equal(2, all.Edits.Count);
        Assert.Equal("cafe bar bar", TextSplicer.Apply(content, all.Edits.Select(e => e with { Replacement = "bar" }).ToArray()));
    }

    [Fact]
    public void Plan_Normalized_MatchesWhenIndentationDiffers()
    {
        const string content = "if (ready)\n    return total;\n";

        var plan = TextReplaceMatcher.Plan(content, "return total;", Occurrence.First, TextMatchMode.Normalized);

        Assert.True(plan.IsSuccess);
        Assert.Equal(TextMatchMode.Normalized, plan.MatchedMode);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(ByteLen("if (ready)\n    "), edit.StartByte);
        Assert.Equal(ByteLen("if (ready)\n    return total;"), edit.EndByte);
    }

    [Fact]
    public void Plan_Normalized_MatchesWhenTrailingWhitespaceDiffers()
    {
        const string content = "return total;   \n";

        var plan = TextReplaceMatcher.Plan(content, "return total;", Occurrence.First, TextMatchMode.Normalized);

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(0, edit.StartByte);
        Assert.Equal(ByteLen("return total;"), edit.EndByte);
        Assert.Equal("return value;   \n", TextSplicer.Apply(content, [edit with { Replacement = "return value;" }]));
    }

    [Fact]
    public void Plan_Normalized_MatchesAcrossCrLfWithoutConsumingOutsideLineEndings()
    {
        const string content = "if (ready)\r\n    return total;  \r\n";

        var plan = TextReplaceMatcher.Plan(content, "return total;\n", Occurrence.First, TextMatchMode.Normalized);

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(ByteLen("if (ready)\r\n    "), edit.StartByte);
        Assert.Equal(ByteLen("if (ready)\r\n    return total;"), edit.EndByte);
        Assert.Equal("if (ready)\r\n    return value;  \r\n", TextSplicer.Apply(content, [edit with { Replacement = "return value;" }]));
    }

    [Fact]
    public void Plan_Normalized_TreatsTabsAndSpacesAsIndentation()
    {
        const string content = "\t\treturn total;\n";

        var plan = TextReplaceMatcher.Plan(content, "    return total;", Occurrence.First, TextMatchMode.Normalized);

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(ByteLen("\t\t"), edit.StartByte);
        Assert.Equal(ByteLen("\t\treturn total;"), edit.EndByte);
    }

    [Fact]
    public void Plan_Fuzzy_MatchesShortExtraCharacterDifference()
    {
        const string content = "const version = \"1.2.0\";\n";

        var plan = TextReplaceMatcher.Plan(content, "const versions = \"1.2.0\";", Occurrence.First, TextMatchMode.Fuzzy);

        Assert.True(plan.IsSuccess);
        Assert.Equal(TextMatchMode.Fuzzy, plan.MatchedMode);
        Assert.Equal(1, Assert.Single(plan.Matches).Distance);
    }

    [Fact]
    public void Plan_Fuzzy_MatchesShortMissingCharacterDifference()
    {
        const string content = "const version = \"1.2.0\";\n";

        var plan = TextReplaceMatcher.Plan(content, "const versio = \"1.2.0\";", Occurrence.First, TextMatchMode.Fuzzy);

        Assert.True(plan.IsSuccess);
        Assert.Equal(TextMatchMode.Fuzzy, plan.MatchedMode);
        Assert.Equal(1, Assert.Single(plan.Matches).Distance);
    }

    [Fact]
    public void Plan_Fuzzy_PrefersLowestDistanceBeforePosition()
    {
        const string content = "return totals;\nreturn total;\n";

        var plan = TextReplaceMatcher.Plan(content, "return total;", Occurrence.First, TextMatchMode.Fuzzy);

        Assert.True(plan.IsSuccess);
        var match = Assert.Single(plan.Matches);
        Assert.Equal(0, match.Distance);
        Assert.Equal(ByteLen("return totals;\n"), match.StartByte);
        Assert.Equal(2, plan.MatchCount);
        Assert.Equal(1, plan.AmbiguousMatchCount);
    }

    [Fact]
    public void Plan_Fuzzy_AllKeepsEveryCandidateWithinThreshold()
    {
        const string content = "return totals;\nreturn total;\n";

        var plan = TextReplaceMatcher.Plan(content, "return total;", Occurrence.All, TextMatchMode.Fuzzy);

        Assert.True(plan.IsSuccess);
        Assert.Equal(2, plan.MatchCount);
        Assert.Equal(2, plan.Edits.Count);
        Assert.Equal([1, 0], plan.Matches.Select(static match => match.Distance).ToArray());
    }

    [Fact]
    public void Plan_Fuzzy_AllKeepsLowestDistanceNonOverlappingCandidates()
    {
        const string content = "alpha\nalpha\nalpha\n";

        var plan = TextReplaceMatcher.Plan(content, "alpha\nalpha", Occurrence.All, TextMatchMode.Fuzzy);

        Assert.True(plan.IsSuccess);
        Assert.Equal(2, plan.MatchCount);
        Assert.Single(plan.Edits);
        Assert.Single(plan.Matches);
    }

    [Fact]
    public void Plan_Normalized_AllKeepsNonOverlappingCandidatesAndReportsFullPopulation()
    {
        const string content = "    foo();\n    foo();\n    foo();\n";

        var plan = TextReplaceMatcher.Plan(
            content,
            "foo();\nfoo();",
            Occurrence.All,
            TextMatchMode.Normalized);

        Assert.True(plan.IsSuccess);
        Assert.Equal(2, plan.MatchCount);
        Assert.Single(plan.Edits);
    }

    [Fact]
    public void Plan_Fuzzy_RefusesLongSnippets()
    {
        var content = new string('a', TextReplaceMatcher.MaxFuzzySnippetChars + 10);
        var oldText = new string('a', TextReplaceMatcher.MaxFuzzySnippetChars + 1);

        var plan = TextReplaceMatcher.Plan(content, oldText, Occurrence.First, TextMatchMode.Fuzzy);

        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.TextNotFound, plan.Error!.Kind);
        Assert.Contains("too long", plan.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_Fuzzy_RefusesLowConfidenceCandidates()
    {
        const string content = "const version = \"1.2.0\";\n";

        var plan = TextReplaceMatcher.Plan(content, "delete everything now", Occurrence.First, TextMatchMode.Fuzzy);

        Assert.False(plan.IsSuccess);
        Assert.Equal(EditErrorKind.TextNotFound, plan.Error!.Kind);
        Assert.Contains("fuzzy", plan.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_All_NeverProducesOverlappingEdits()
    {
        const string content = "aaaa";

        var plan = TextReplaceMatcher.Plan(content, "aa", Occurrence.All, TextMatchMode.Auto);

        Assert.True(plan.IsSuccess);
        Assert.Equal(2, plan.Edits.Count);
        Assert.Equal("bb", TextSplicer.Apply(content, plan.Edits.Select(e => e with { Replacement = "b" }).ToArray()));
    }

    [Fact]
    public void Plan_MatchAfterMultibyte_ProducesUtf8ByteOffsets()
    {
        const string content = "café\n    return total;\n";

        var plan = TextReplaceMatcher.Plan(content, "return total;", Occurrence.First, TextMatchMode.Auto);

        Assert.True(plan.IsSuccess);
        var edit = Assert.Single(plan.Edits);
        Assert.Equal(ByteLen("café\n    "), edit.StartByte);
        Assert.Equal(ByteLen("café\n    return total;"), edit.EndByte);
    }

    [Fact]
    public void Plan_Auto_UsesExactBeforeNormalized()
    {
        const string content = "    return total;\nreturn total;\n";

        var plan = TextReplaceMatcher.Plan(content, "return total;", Occurrence.First, TextMatchMode.Auto);

        Assert.True(plan.IsSuccess);
        Assert.Equal(TextMatchMode.Exact, plan.MatchedMode);
        Assert.Equal(ByteLen("    "), Assert.Single(plan.Edits).StartByte);
    }
}
