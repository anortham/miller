using Miller.Core.Editing;
using Xunit;

namespace Miller.Tests.Editing;

/// <summary>
/// The unified-diff renderer (M6 Components/1). Pins: identical content → empty; an added line, a removed line,
/// and a changed line each produce the correct hunk header + +/-/context lines; the path appears in the
/// ---/+++ headers; multiple separated change regions produce multiple hunks. Asserts on the produced diff
/// text, never just "non-empty".
/// </summary>
public sealed class UnifiedDiffTests
{
    private static readonly string NL = "\n";

    private static string[] Lines(string diff) => diff.Split('\n');

    [Fact]
    public void Render_IdenticalContent_ReturnsEmpty()
    {
        var diff = UnifiedDiff.Render("a\nb\nc\n", "a\nb\nc\n", "x.cs");
        Assert.Equal(string.Empty, diff);
    }

    [Fact]
    public void Render_IdenticalEmptyContent_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, UnifiedDiff.Render("", "", "x.cs"));
    }

    [Fact]
    public void Render_EmitsFileHeadersWithPath()
    {
        var diff = UnifiedDiff.Render("a\n", "b\n", "src/Foo.cs");
        var lines = Lines(diff);
        Assert.Equal("--- src/Foo.cs", lines[0]);
        Assert.Equal("+++ src/Foo.cs", lines[1]);
    }

    [Fact]
    public void Render_ChangedLine_ShowsMinusThenPlus()
    {
        var diff = UnifiedDiff.Render("alpha\nbeta\ngamma\n", "alpha\nBETA\ngamma\n", "x.cs");

        Assert.Contains("-beta", diff);
        Assert.Contains("+BETA", diff);
        // Unchanged neighbours appear as context (space prefix), not as +/-.
        Assert.Contains(" alpha", diff);
        Assert.Contains(" gamma", diff);
        // The changed line is not duplicated as context.
        Assert.DoesNotContain(" beta", diff);
    }

    [Fact]
    public void Render_AddedLine_ShowsOnlyPlus()
    {
        var diff = UnifiedDiff.Render("a\nc\n", "a\nb\nc\n", "x.cs");

        Assert.Contains("+b", diff);
        // 'a' and 'c' survive unchanged → context lines, not additions/removals.
        Assert.DoesNotContain("-a", diff);
        Assert.DoesNotContain("-c", diff);
        Assert.DoesNotContain("+a" + NL, diff);
    }

    [Fact]
    public void Render_RemovedLine_ShowsOnlyMinus()
    {
        var diff = UnifiedDiff.Render("a\nb\nc\n", "a\nc\n", "x.cs");

        Assert.Contains("-b", diff);
        Assert.DoesNotContain("+b", diff);
    }

    [Fact]
    public void Render_HunkHeader_HasCorrectLineRanges_ForSingleChange()
    {
        // Change line 2 of a 3-line file. With 3 lines of context the hunk covers the whole file: @@ -1,3 +1,3 @@
        var diff = UnifiedDiff.Render("a\nb\nc\n", "a\nB\nc\n", "x.cs");
        Assert.Contains("@@ -1,3 +1,3 @@", diff);
    }

    [Fact]
    public void Render_PureAddition_HunkCountsReflectGrowth()
    {
        // 2 → 3 lines: old count 2, new count 3.
        var diff = UnifiedDiff.Render("a\nc\n", "a\nb\nc\n", "x.cs");
        Assert.Contains("@@ -1,2 +1,3 @@", diff);
    }

    [Fact]
    public void Render_TwoSeparatedChanges_ProduceTwoHunks()
    {
        // Changes far apart (more than 2*context lines between) must split into two @@ hunks.
        var oldText = string.Join(NL, "l1", "l2", "l3", "l4", "l5", "l6", "l7", "l8", "l9", "l10") + NL;
        var newText = string.Join(NL, "X1", "l2", "l3", "l4", "l5", "l6", "l7", "l8", "l9", "X10") + NL;

        var diff = UnifiedDiff.Render(oldText, newText, "x.cs");
        var hunkCount = Lines(diff).Count(l => l.StartsWith("@@", StringComparison.Ordinal));
        Assert.Equal(2, hunkCount);
        Assert.Contains("-l1", diff);
        Assert.Contains("+X1", diff);
        Assert.Contains("-l10", diff);
        Assert.Contains("+X10", diff);
    }

    [Fact]
    public void Render_AdjacentChanges_StayInOneHunk()
    {
        // Two changes within the context window collapse into a single hunk.
        var oldText = "a\nb\nc\nd\ne\n";
        var newText = "a\nB\nc\nD\ne\n";
        var diff = UnifiedDiff.Render(oldText, newText, "x.cs");
        var hunkCount = Lines(diff).Count(l => l.StartsWith("@@", StringComparison.Ordinal));
        Assert.Equal(1, hunkCount);
    }

    [Fact]
    public void Render_LargeUnrelatedInputs_ReturnsBoundedChangeProofWithoutQuadraticMatrix()
    {
        string oldText = string.Join('\n', Enumerable.Range(0, 2500).Select(static i => $"old-{i:D4}")) + "\n";
        string newText = string.Join('\n', Enumerable.Range(0, 2500).Select(static i => $"new-{i:D4}")) + "\n";

        string diff = UnifiedDiff.Render(oldText, newText, "large.cs");

        Assert.Contains("diff preview truncated", diff, StringComparison.Ordinal);
        Assert.Contains("-old-0000", diff, StringComparison.Ordinal);
        Assert.Contains("+new-0000", diff, StringComparison.Ordinal);
        Assert.Contains("old lines omitted", diff, StringComparison.Ordinal);
        Assert.Contains("new lines omitted", diff, StringComparison.Ordinal);
        Assert.True(diff.Length < 8192, diff.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Render_LargeFileWithDistantSameLengthEdits_KeepsHunksSmall()
    {
        // A rename in a big file: two touched lines ~1200 apart, line count unchanged. The LCS matrix is
        // over the cell cap here, so this exercises the non-LCS path.
        var oldLines = Enumerable.Range(0, 2600).Select(i => $"line {i};").ToArray();
        var newLines = (string[])oldLines.Clone();
        newLines[900] = "line 900 RENAMED;";
        newLines[2100] = "line 2100 RENAMED;";

        var diff = UnifiedDiff.Render(
            string.Join("\n", oldLines) + "\n",
            string.Join("\n", newLines) + "\n",
            "big.cs");

        string[] headers = Lines(diff).Where(l => l.StartsWith("@@", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, headers.Length);

        int changed = Lines(diff).Count(l =>
            (l.StartsWith("+", StringComparison.Ordinal) || l.StartsWith("-", StringComparison.Ordinal)) &&
            !l.StartsWith("+++", StringComparison.Ordinal) &&
            !l.StartsWith("---", StringComparison.Ordinal));
        Assert.Equal(4, changed); // two deletes + two inserts

        Assert.DoesNotContain("omitted", diff, StringComparison.OrdinalIgnoreCase);
        Assert.True(Lines(diff).Length < 40, $"preview should stay reviewable; got {Lines(diff).Length} lines");
    }

    [Fact]
    public void Render_AppliedToOldContent_YieldsNewContent_RoundTrip()
    {
        // Sanity: the diff describes exactly the transformation old→new (verified by reconstructing new
        // from the +/context lines of the single full-file hunk).
        var oldText = "one\ntwo\nthree\n";
        var newText = "one\nTWO\nthree\nfour\n";
        var diff = UnifiedDiff.Render(oldText, newText, "x.cs");

        var reconstructed = string.Join(NL,
            Lines(diff)
                .SkipWhile(l => !l.StartsWith("@@", StringComparison.Ordinal))
                .Skip(1)
                .Where(l => l.StartsWith(" ", StringComparison.Ordinal) || l.StartsWith("+", StringComparison.Ordinal))
                .Where(l => l.Length > 0)
                .Select(l => l[1..]));
        Assert.Equal("one\nTWO\nthree\nfour", reconstructed);
    }
}
