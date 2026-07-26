using System.Text;

namespace Miller.Core.Editing;

/// <summary>
/// Renders a minimal line-level unified diff between two versions of a file (M6 Components/1, the preview
/// renderer). Pure: takes the old/new content + a path label and returns standard unified-diff text, or an
/// empty string when the two are identical. Used by the <c>edit</c> tool to show what a plan would do.
/// The diff is computed by a longest-common-subsequence (LCS) line alignment, grouped into hunks with up to
/// <see cref="ContextLines"/> lines of surrounding context.
/// </summary>
public static class UnifiedDiff
{
    /// <summary>Lines of unchanged context kept around each change region (standard unified-diff value).</summary>
    private const int ContextLines = 3;
    private const long MaxLcsCells = 1_000_000;
    private const int MaxFallbackLinesPerSide = 40;

    /// <summary>
    /// Render the unified diff transforming <paramref name="oldContent"/> into <paramref name="newContent"/>.
    /// </summary>
    /// <param name="oldContent">The original file text.</param>
    /// <param name="newContent">The proposed file text.</param>
    /// <param name="path">The path label placed in the <c>---</c>/<c>+++</c> headers.</param>
    /// <returns>Standard unified-diff text, or an empty string when the two are byte-identical.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static string Render(string oldContent, string newContent, string path)
    {
        ArgumentNullException.ThrowIfNull(oldContent);
        ArgumentNullException.ThrowIfNull(newContent);
        ArgumentNullException.ThrowIfNull(path);

        if (string.Equals(oldContent, newContent, StringComparison.Ordinal))
            return string.Empty;

        var oldLines = SplitLines(oldContent);
        var newLines = SplitLines(newContent);

        if (!TryDiffLines(oldLines, newLines, out var ops))
            return RenderBoundedFallback(oldLines, newLines, path);
        var hunks = GroupIntoHunks(ops);
        if (hunks.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("--- ").Append(path).Append('\n');
        sb.Append("+++ ").Append(path).Append('\n');
        foreach (var hunk in hunks)
            hunk.Write(sb);
        return sb.ToString();
    }

    private static string RenderBoundedFallback(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        string path)
    {
        int prefix = 0;
        while (prefix < oldLines.Count &&
               prefix < newLines.Count &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < oldLines.Count - prefix &&
               suffix < newLines.Count - prefix &&
               string.Equals(oldLines[oldLines.Count - suffix - 1], newLines[newLines.Count - suffix - 1], StringComparison.Ordinal))
        {
            suffix++;
        }

        int oldChanged = oldLines.Count - prefix - suffix;
        int newChanged = newLines.Count - prefix - suffix;
        int oldShown = Math.Min(oldChanged, MaxFallbackLinesPerSide);
        int newShown = Math.Min(newChanged, MaxFallbackLinesPerSide);
        var output = new StringBuilder()
            .Append("--- ").Append(path).Append('\n')
            .Append("+++ ").Append(path).Append('\n')
            .Append("@@ -").Append(prefix + 1).Append(',').Append(oldChanged)
            .Append(" +").Append(prefix + 1).Append(',').Append(newChanged).Append(" @@\n");
        for (int index = 0; index < oldShown; index++)
            output.Append('-').Append(oldLines[prefix + index]).Append('\n');
        if (oldChanged > oldShown)
            output.Append("# diff preview truncated: ").Append(oldChanged - oldShown).Append(" old lines omitted\n");
        for (int index = 0; index < newShown; index++)
            output.Append('+').Append(newLines[prefix + index]).Append('\n');
        if (newChanged > newShown)
            output.Append("# diff preview truncated: ").Append(newChanged - newShown).Append(" new lines omitted\n");
        return output.ToString();
    }

    /// <summary>
    /// Split content into logical lines. A trailing newline is the line terminator of the last content line,
    /// not a separate empty line, so <c>"a\nb\n"</c> → <c>["a","b"]</c>. Empty content → no lines.
    /// </summary>
    private static IReadOnlyList<string> SplitLines(string content)
    {
        if (content.Length == 0)
            return [];
        var parts = content.Split('\n');
        // A trailing '\n' yields a final empty element that is not a line of its own.
        if (parts.Length > 0 && parts[^1].Length == 0)
            return parts[..^1];
        return parts;
    }

    private enum OpKind { Equal, Delete, Insert }

    private readonly record struct DiffOp(OpKind Kind, string Text);

    private static bool TryDiffLines(
        IReadOnlyList<string> a,
        IReadOnlyList<string> b,
        out List<DiffOp> ops)
    {
        int prefix = 0;
        while (prefix < a.Count &&
               prefix < b.Count &&
               string.Equals(a[prefix], b[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < a.Count - prefix &&
               suffix < b.Count - prefix &&
               string.Equals(a[a.Count - suffix - 1], b[b.Count - suffix - 1], StringComparison.Ordinal))
        {
            suffix++;
        }

        int n = a.Count - prefix - suffix;
        int m = b.Count - prefix - suffix;
        if ((long)(n + 1) * (m + 1) > MaxLcsCells)
        {
            ops = [];
            return false;
        }

        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = string.Equals(a[prefix + i], b[prefix + j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        ops = new List<DiffOp>(a.Count + b.Count);
        for (int i = 0; i < prefix; i++)
            ops.Add(new DiffOp(OpKind.Equal, a[i]));

        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(a[prefix + x], b[prefix + y], StringComparison.Ordinal))
            {
                ops.Add(new DiffOp(OpKind.Equal, a[prefix + x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                ops.Add(new DiffOp(OpKind.Delete, a[prefix + x]));
                x++;
            }
            else
            {
                ops.Add(new DiffOp(OpKind.Insert, b[prefix + y]));
                y++;
            }
        }
        while (x < n)
            ops.Add(new DiffOp(OpKind.Delete, a[prefix + x++]));
        while (y < m)
            ops.Add(new DiffOp(OpKind.Insert, b[prefix + y++]));

        for (int i = suffix; i > 0; i--)
            ops.Add(new DiffOp(OpKind.Equal, a[a.Count - i]));

        return true;
    }

    private sealed class Hunk
    {
        public int OldStart;   // 1-based first old line in the hunk
        public int OldCount;
        public int NewStart;   // 1-based first new line in the hunk
        public int NewCount;
        public readonly List<string> Body = [];

        public void Write(StringBuilder sb)
        {
            sb.Append("@@ -").Append(OldStart).Append(',').Append(OldCount)
              .Append(" +").Append(NewStart).Append(',').Append(NewCount).Append(" @@\n");
            foreach (var line in Body)
                sb.Append(line).Append('\n');
        }
    }

    /// <summary>
    /// Group the flat op stream into hunks: each change region is padded with up to <see cref="ContextLines"/>
    /// context lines on each side, and regions whose context windows touch are merged into one hunk.
    /// </summary>
    private static List<Hunk> GroupIntoHunks(List<DiffOp> ops)
    {
        // Index of every changed op (delete or insert).
        var changeIndices = new List<int>();
        for (var i = 0; i < ops.Count; i++)
            if (ops[i].Kind != OpKind.Equal)
                changeIndices.Add(i);

        if (changeIndices.Count == 0)
            return [];

        // Merge adjacent change windows: a gap of <= 2*ContextLines equal ops keeps them in one hunk.
        var ranges = new List<(int Start, int End)>();
        var rangeStart = changeIndices[0];
        var rangeEnd = changeIndices[0];
        for (var k = 1; k < changeIndices.Count; k++)
        {
            var idx = changeIndices[k];
            if (idx - rangeEnd - 1 <= ContextLines * 2)
            {
                rangeEnd = idx;
            }
            else
            {
                ranges.Add((rangeStart, rangeEnd));
                rangeStart = idx;
                rangeEnd = idx;
            }
        }
        ranges.Add((rangeStart, rangeEnd));

        var hunks = new List<Hunk>();
        foreach (var (start, end) in ranges)
        {
            var from = Math.Max(0, start - ContextLines);
            var to = Math.Min(ops.Count - 1, end + ContextLines);
            hunks.Add(BuildHunk(ops, from, to));
        }
        return hunks;
    }

    /// <summary>Build a single hunk covering ops[from..to] inclusive, computing 1-based line ranges.</summary>
    private static Hunk BuildHunk(List<DiffOp> ops, int from, int to)
    {
        // Count old/new lines preceding 'from' to compute the 1-based hunk start lines.
        var oldLineNo = 1;
        var newLineNo = 1;
        for (var i = 0; i < from; i++)
        {
            switch (ops[i].Kind)
            {
                case OpKind.Equal: oldLineNo++; newLineNo++; break;
                case OpKind.Delete: oldLineNo++; break;
                case OpKind.Insert: newLineNo++; break;
            }
        }

        var hunk = new Hunk { OldStart = oldLineNo, NewStart = newLineNo };
        var oldCount = 0;
        var newCount = 0;
        for (var i = from; i <= to; i++)
        {
            switch (ops[i].Kind)
            {
                case OpKind.Equal:
                    hunk.Body.Add(" " + ops[i].Text);
                    oldCount++;
                    newCount++;
                    break;
                case OpKind.Delete:
                    hunk.Body.Add("-" + ops[i].Text);
                    oldCount++;
                    break;
                case OpKind.Insert:
                    hunk.Body.Add("+" + ops[i].Text);
                    newCount++;
                    break;
            }
        }

        hunk.OldCount = oldCount;
        hunk.NewCount = newCount;
        // A hunk that starts at line 1 with zero lines of that side still reports start 1 per convention;
        // when a side has zero lines the conventional start is 0, but Miller's files always have >=1 line in a
        // hunk's context, so OldStart/NewStart computed above are correct as-is.
        return hunk;
    }
}
