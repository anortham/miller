using System.Text;

namespace Miller.Core.Editing;

/// <summary>
/// Pure text matcher for <c>replace_text</c>. It returns byte spans over the original content; it never edits,
/// reads, writes, or interprets language syntax.
/// </summary>
public static class TextReplaceMatcher
{
    public const int MaxFuzzySnippetChars = 160;

    public static TextReplaceMatchPlan Plan(string content, string oldText, Occurrence occurrence, TextMatchMode matchMode)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(oldText);

        if (oldText.Length == 0)
            return Failure(TextMatchMode.Exact, matchMode, EditErrorKind.MissingArgument, "old_text must not be empty for replace_text.");

        return matchMode switch
        {
            TextMatchMode.Exact => PlanExact(content, oldText, occurrence, matchMode),
            TextMatchMode.Normalized => PlanNormalized(content, oldText, occurrence, matchMode),
            TextMatchMode.Fuzzy => PlanFuzzy(content, oldText, occurrence, matchMode),
            TextMatchMode.Auto => PlanAuto(content, oldText, occurrence),
            _ => PlanAuto(content, oldText, occurrence),
        };
    }

    private static TextReplaceMatchPlan PlanAuto(string content, string oldText, Occurrence occurrence)
    {
        var exact = PlanExact(content, oldText, occurrence, TextMatchMode.Auto);
        if (exact.IsSuccess)
            return exact;

        var normalized = PlanNormalized(content, oldText, occurrence, TextMatchMode.Auto);
        if (normalized.IsSuccess)
            return normalized;

        return PlanFuzzy(content, oldText, occurrence, TextMatchMode.Auto);
    }

    private static TextReplaceMatchPlan PlanExact(string content, string oldText, Occurrence occurrence, TextMatchMode requestedMode)
    {
        var matches = new List<TextReplaceMatch>();
        var from = 0;
        while (from <= content.Length - oldText.Length)
        {
            var at = content.IndexOf(oldText, from, StringComparison.Ordinal);
            if (at < 0)
                break;

            matches.Add(new TextReplaceMatch(
                CharToByteOffset(content, at),
                CharToByteOffset(content, at + oldText.Length),
                LineNumberAt(content, at),
                LineNumberAt(content, at + Math.Max(0, oldText.Length - 1)),
                TextMatchMode.Exact,
                Distance: 0));
            from = at + oldText.Length;
        }

        return matches.Count == 0
            ? Failure(TextMatchMode.Exact, requestedMode, EditErrorKind.TextNotFound, $"old_text not found: \"{oldText}\".")
            : Success(matches, occurrence, requestedMode, TextMatchMode.Exact);
    }

    private static TextReplaceMatchPlan PlanNormalized(string content, string oldText, Occurrence occurrence, TextMatchMode requestedMode)
    {
        var target = NormalizedTargetLines(oldText);
        if (target.Count == 0)
            return Failure(TextMatchMode.Normalized, requestedMode, EditErrorKind.TextNotFound, "normalized old_text did not contain a matchable line.");

        var contentLines = SplitContentLines(content);
        var matches = new List<TextReplaceMatch>();
        for (var i = 0; i <= contentLines.Count - target.Count; i++)
        {
            if (!WindowEquals(contentLines, i, target))
                continue;

            if (TryCreateLineMatch(content, contentLines, i, target.Count, TextMatchMode.Normalized, distance: 0, out var match))
                matches.Add(match);
        }

        return matches.Count == 0
            ? Failure(TextMatchMode.Normalized, requestedMode, EditErrorKind.TextNotFound, "old_text not found with normalized matching.")
            : Success(matches, occurrence, requestedMode, TextMatchMode.Normalized);
    }

    private static TextReplaceMatchPlan PlanFuzzy(string content, string oldText, Occurrence occurrence, TextMatchMode requestedMode)
    {
        if (oldText.Length > MaxFuzzySnippetChars)
            return Failure(
                TextMatchMode.Fuzzy,
                requestedMode,
                EditErrorKind.TextNotFound,
                $"fuzzy old_text is too long ({oldText.Length} chars; max {MaxFuzzySnippetChars}).");

        var targetLines = NormalizedTargetLines(oldText);
        if (targetLines.Count == 0)
            return Failure(TextMatchMode.Fuzzy, requestedMode, EditErrorKind.TextNotFound, "fuzzy old_text did not contain a matchable line.");

        var targetText = string.Join('\n', targetLines);
        var threshold = MaxFuzzyDistance(targetText.Length);
        var contentLines = SplitContentLines(content);
        var matches = new List<TextReplaceMatch>();

        for (var i = 0; i <= contentLines.Count - targetLines.Count; i++)
        {
            var candidateText = NormalizedWindow(contentLines, i, targetLines.Count);
            if (candidateText.Length == 0)
                continue;

            var distance = BoundedLevenshteinDistance(candidateText, targetText, threshold);
            if (distance > threshold)
                continue;

            if (TryCreateLineMatch(content, contentLines, i, targetLines.Count, TextMatchMode.Fuzzy, distance, out var match))
                matches.Add(match);
        }

        return matches.Count == 0
            ? Failure(TextMatchMode.Fuzzy, requestedMode, EditErrorKind.TextNotFound, "old_text not found with bounded fuzzy matching.")
            : Success(matches, occurrence, requestedMode, TextMatchMode.Fuzzy);
    }

    private static TextReplaceMatchPlan Success(
        IReadOnlyList<TextReplaceMatch> allMatches,
        Occurrence occurrence,
        TextMatchMode requestedMode,
        TextMatchMode matchedMode)
    {
        var selected = occurrence switch
        {
            Occurrence.First => [allMatches[0]],
            Occurrence.Last => [allMatches[^1]],
            Occurrence.All => allMatches,
            _ => allMatches,
        };

        var edits = selected.Select(m => new TextEdit(m.StartByte, m.EndByte, string.Empty)).ToArray();
        return new TextReplaceMatchPlan(EditPlan.Success(edits), requestedMode, matchedMode, selected, allMatches.Count);
    }

    private static TextReplaceMatchPlan Failure(TextMatchMode attemptedMode, TextMatchMode requestedMode, EditErrorKind kind, string message) =>
        new(
            EditPlan.Failure(new EditError(kind, message + " " + RecoveryAction(attemptedMode, requestedMode, kind))),
            requestedMode,
            MatchedMode: null,
            Matches: [],
            MatchCount: 0);

    // A no-match is the edit tool's dominant failure and the agent cannot see the file, so the message has to
    // name the next call rather than only report the miss (design §7.2).
    private static string RecoveryAction(TextMatchMode attemptedMode, TextMatchMode requestedMode, EditErrorKind kind)
    {
        if (kind == EditErrorKind.MissingArgument)
            return "Pass the literal text to replace in old_text.";

        if (attemptedMode == TextMatchMode.Fuzzy && requestedMode == TextMatchMode.Fuzzy)
        {
            return $"Shorten old_text to one distinctive line (max {MaxFuzzySnippetChars} chars), or retry with " +
                   "match_mode=exact and the literal text copied from the file.";
        }

        return requestedMode switch
        {
            TextMatchMode.Exact =>
                "Retry with match_mode=normalized (ignores indentation and trailing whitespace) or " +
                "match_mode=fuzzy (tolerates small differences), or confirm the current text with inspect.",
            TextMatchMode.Normalized =>
                "Retry with match_mode=fuzzy (tolerates small differences), or confirm the current text with " +
                "inspect and copy old_text from the file.",
            _ =>
                "The exact→normalized→fuzzy ladder found nothing. Confirm the current text with inspect (or " +
                "search mode=source), then retry with old_text copied from the file, or narrow the edit with " +
                "query/anchor/line.",
        };
    }

    private static bool WindowEquals(IReadOnlyList<LineInfo> contentLines, int offset, IReadOnlyList<string> targetLines)
    {
        for (var i = 0; i < targetLines.Count; i++)
        {
            if (!StringComparer.Ordinal.Equals(contentLines[offset + i].Normalized, targetLines[i]))
                return false;
        }

        return true;
    }

    private static string NormalizedWindow(IReadOnlyList<LineInfo> contentLines, int offset, int count)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                builder.Append('\n');
            builder.Append(contentLines[offset + i].Normalized);
        }

        return builder.ToString();
    }

    private static bool TryCreateLineMatch(
        string content,
        IReadOnlyList<LineInfo> lines,
        int start,
        int count,
        TextMatchMode mode,
        int distance,
        out TextReplaceMatch match)
    {
        var first = lines[start];
        var last = lines[start + count - 1];
        var startChar = first.ContentStartChar + first.LeadingWhitespaceChars;
        var endChar = last.ContentEndChar - last.TrailingWhitespaceChars;
        if (endChar < startChar)
        {
            match = default!;
            return false;
        }

        match = new TextReplaceMatch(
            CharToByteOffset(content, startChar),
            CharToByteOffset(content, endChar),
            first.LineNumber,
            last.LineNumber,
            mode,
            distance);
        return true;
    }

    private static List<string> NormalizedTargetLines(string text)
    {
        var lines = SplitRawLines(text);
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var normalized = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            var value = NormalizeLine(line);
            if (value.Length == 0 && normalized.Count == 0)
                continue;
            normalized.Add(value);
        }

        while (normalized.Count > 0 && normalized[^1].Length == 0)
            normalized.RemoveAt(normalized.Count - 1);

        return normalized;
    }

    private static List<LineInfo> SplitContentLines(string content)
    {
        var lines = new List<LineInfo>();
        var lineStart = 0;
        var lineNumber = 1;

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
                continue;

            var contentEnd = i;
            if (contentEnd > lineStart && content[contentEnd - 1] == '\r')
                contentEnd--;
            lines.Add(CreateLine(content, lineStart, contentEnd, lineNumber));
            lineStart = i + 1;
            lineNumber++;
        }

        if (lineStart <= content.Length)
            lines.Add(CreateLine(content, lineStart, content.Length, lineNumber));

        return lines;
    }

    private static List<string> SplitRawLines(string text)
    {
        var lines = new List<string>();
        var lineStart = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;

            var lineEnd = i;
            if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
                lineEnd--;
            lines.Add(text[lineStart..lineEnd]);
            lineStart = i + 1;
        }

        lines.Add(text[lineStart..]);
        return lines;
    }

    private static LineInfo CreateLine(string content, int contentStart, int contentEnd, int lineNumber)
    {
        var leading = 0;
        while (contentStart + leading < contentEnd && IsNormalizedWhitespace(content[contentStart + leading]))
            leading++;

        var trailing = 0;
        while (contentEnd - trailing > contentStart + leading && IsNormalizedWhitespace(content[contentEnd - trailing - 1]))
            trailing++;

        var normalized = NormalizeLine(content[contentStart..contentEnd]);
        return new LineInfo(contentStart, contentEnd, leading, trailing, lineNumber, normalized);
    }

    private static string NormalizeLine(string line)
    {
        var start = 0;
        while (start < line.Length && IsNormalizedWhitespace(line[start]))
            start++;

        var end = line.Length;
        while (end > start && IsNormalizedWhitespace(line[end - 1]))
            end--;

        var trimmed = line[start..end];
        if (!ContainsUnicodeSpaceSubstitute(trimmed))
            return trimmed;

        var folded = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
            folded.Append(IsUnicodeSpaceSubstitute(ch) ? ' ' : ch);
        return folded.ToString();
    }

    private static bool IsNormalizedWhitespace(char ch) => ch is ' ' or '\t' || IsUnicodeSpaceSubstitute(ch);

    // Editors, docs, and terminal copy-paste routinely substitute these for an ASCII space; an agent that
    // pastes one into old_text would otherwise get a bare no-match it cannot see (design §7.3).
    private static bool IsUnicodeSpaceSubstitute(char ch) =>
        ch is '\f' or '\u00A0' or '\u202F' or '\u205F' or '\u3000' ||
        ch is >= '\u2000' and <= '\u200A';

    private static bool ContainsUnicodeSpaceSubstitute(string text)
    {
        foreach (var ch in text)
        {
            if (IsUnicodeSpaceSubstitute(ch))
                return true;
        }

        return false;
    }

    private static int MaxFuzzyDistance(int length)
    {
        if (length <= 12)
            return 1;
        if (length <= 48)
            return 2;
        return 3;
    }

    private static int BoundedLevenshteinDistance(string a, string b, int maxDistance)
    {
        if (Math.Abs(a.Length - b.Length) > maxDistance)
            return maxDistance + 1;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                var insertion = current[j - 1] + 1;
                var deletion = previous[j] + 1;
                current[j] = Math.Min(Math.Min(insertion, deletion), substitution);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > maxDistance)
                return maxDistance + 1;

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static int CharToByteOffset(string content, int charOffset) =>
        Encoding.UTF8.GetByteCount(content.AsSpan(0, charOffset));

    private static int LineNumberAt(string content, int charOffset)
    {
        var line = 1;
        for (var i = 0; i < charOffset && i < content.Length; i++)
        {
            if (content[i] == '\n')
                line++;
        }

        return line;
    }

    private sealed record LineInfo(
        int ContentStartChar,
        int ContentEndChar,
        int LeadingWhitespaceChars,
        int TrailingWhitespaceChars,
        int LineNumber,
        string Normalized);
}
