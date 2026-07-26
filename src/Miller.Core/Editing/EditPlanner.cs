using System.Text;

namespace Miller.Core.Editing;

/// <summary>
/// Pure per-operation planner (M6 Components/1). Each method turns an operation's inputs (content + params +
/// a symbol's <see cref="SymbolEditSpan"/>) into a list of byte-span <see cref="TextEdit"/>s, or a typed
/// <see cref="EditError"/> for an expected failure. No I/O, no language-specific syntax: <see cref="AddDoc"/>
/// inserts exactly the caller-supplied text (the caller owns the comment prefix), it never synthesizes "///".
/// </summary>
public static class EditPlanner
{
    /// <summary>
    /// Plan a literal <c>old_text</c> replacement. The returned edits carry empty replacements (the splice text
    /// is the caller's <c>new_text</c>, applied by the Server); only the byte spans are decided here. Matching
    /// is ordinal and non-overlapping. <paramref name="occurrence"/> selects first / last / all matches.
    /// </summary>
    /// <returns>
    /// On success, one <see cref="TextEdit"/> per targeted match (replacement left empty for the caller to fill);
    /// <see cref="EditErrorKind.TextNotFound"/> when no match exists; <see cref="EditErrorKind.MissingArgument"/>
    /// when <paramref name="oldText"/> is empty.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="oldText"/> is null.</exception>
    public static EditPlan ReplaceText(string content, string oldText, Occurrence occurrence)
    {
        return TextReplaceMatcher.Plan(content, oldText, occurrence, TextMatchMode.Exact).Plan;
    }

    /// <summary>
    /// Plan a symbol body replacement over <c>[body_start_byte, body_end_byte)</c> (verified facts #1).
    /// Rejects a symbol with no body span (decision log #7).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="span"/> or <paramref name="newText"/> is null.</exception>
    public static EditPlan ReplaceSymbolBody(SymbolEditSpan span, string newText)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(newText);

        if (span.BodyStartByte is not { } bodyStart || span.BodyEndByte is not { } bodyEnd)
            return BodySpanUnavailable(span, "body");
        if (newText.Length == 0)
            return EmptyNewText("replace_symbol_body");

        return EditPlan.Success([new TextEdit(bodyStart, bodyEnd, newText)]);
    }

    /// <summary>
    /// Plan a symbol signature replacement over <c>[start_byte, body_start_byte)</c> (verified facts #1).
    /// Rejects a symbol with no body span (the signature span's exclusive end is undefined without it).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="span"/> or <paramref name="newText"/> is null.</exception>
    public static EditPlan ReplaceSymbolSignature(SymbolEditSpan span, string newText)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(newText);

        if (span.BodyStartByte is not { } bodyStart)
            return BodySpanUnavailable(span, "signature");
        if (newText.Length == 0)
            return EmptyNewText("replace_symbol_signature");

        return EditPlan.Success([new TextEdit(span.StartByte, bodyStart, newText)]);
    }

    /// <summary>Plan a zero-width insertion at the symbol's <c>start_byte</c> (decision log #7).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="span"/> or <paramref name="newText"/> is null.</exception>
    public static EditPlan InsertBefore(SymbolEditSpan span, string newText)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(newText);

        if (IsDegenerate(span))
            return DegenerateSpan(span);
        if (newText.Length == 0)
            return EmptyNewText("insert_before");

        return EditPlan.Success([new TextEdit(span.StartByte, span.StartByte, newText)]);
    }

    /// <summary>Plan a zero-width insertion at the symbol's <c>end_byte</c> (decision log #7).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="span"/> or <paramref name="newText"/> is null.</exception>
    public static EditPlan InsertAfter(SymbolEditSpan span, string newText)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(newText);

        if (IsDegenerate(span))
            return DegenerateSpan(span);
        if (newText.Length == 0)
            return EmptyNewText("insert_after");

        return EditPlan.Success([new TextEdit(span.EndByte, span.EndByte, newText)]);
    }

    /// <summary>
    /// Plan a documentation insert: a zero-width insertion at the byte offset where the symbol's start line
    /// begins (line→byte mapped on <paramref name="content"/>), of the caller's text followed by one newline.
    /// The planner adds NO comment prefix — the caller supplies "///", "#", "--", etc., so this works for every
    /// language. It DOES align the inserted block to the symbol's own indentation: the doc is dedented to its
    /// common leading whitespace then re-indented to the indent of the symbol's start line, so an un-indented
    /// "/// ..." lands flush with an indented member instead of at column 0. Decision log #7.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="content"/>, <paramref name="span"/>, or <paramref name="newText"/> is null.</exception>
    public static EditPlan AddDoc(string content, SymbolEditSpan span, string newText)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(newText);

        if (IsDegenerate(span))
            return DegenerateSpan(span);
        if (newText.Length == 0)
            return EmptyNewText("add_doc");

        var lineStartByte = ByteOffsetOfLineStart(content, span.StartLine);
        string newline = LineEndingOfLine(content, span.StartLine);
        string normalizedDoc = newText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var aligned = AlignDocToSymbolIndent(normalizedDoc, IndentOfLine(content, span.StartLine));
        string replacement = aligned.Replace("\n", newline, StringComparison.Ordinal) + newline;
        return EditPlan.Success([new TextEdit(lineStartByte, lineStartByte, replacement)]);
    }

    /// <summary>
    /// Re-indent a (possibly multi-line) doc block to <paramref name="symbolIndent"/>: strip the block's common
    /// leading whitespace (dedent) then prefix every non-blank line with the symbol's indent. Blank lines are
    /// emitted empty (no trailing whitespace). No comment prefix is synthesised — only whitespace is touched.
    /// </summary>
    private static string AlignDocToSymbolIndent(string doc, string symbolIndent)
    {
        var lines = doc.Split('\n');

        string? common = null;
        foreach (var line in lines)
        {
            if (IsBlank(line))
                continue;
            var lead = LeadingWhitespace(line);
            common = common is null ? lead : CommonPrefix(common, lead);
        }
        common ??= string.Empty;

        for (var i = 0; i < lines.Length; i++)
            lines[i] = IsBlank(lines[i]) ? string.Empty : symbolIndent + lines[i][common.Length..];

        return string.Join('\n', lines);
    }

    /// <summary>The leading run of spaces/tabs on the 1-based <paramref name="line"/> of <paramref name="content"/> (empty if none/out of range).</summary>
    private static string IndentOfLine(string content, int line)
    {
        var lines = content.Split('\n');
        if (line < 1 || line > lines.Length)
            return string.Empty;
        return LeadingWhitespace(lines[line - 1]);
    }

    private static string LineEndingOfLine(string content, int line)
    {
        string[] lines = content.Split('\n');
        if (line >= 1 && line <= lines.Length)
        {
            string target = lines[line - 1];
            if (target.EndsWith('\r'))
                return "\r\n";
            if (line < lines.Length)
                return "\n";
        }

        if (line > 1 && line - 2 < lines.Length && lines[line - 2].EndsWith('\r'))
            return "\r\n";
        return "\n";
    }

    /// <summary>The leading run of spaces/tabs of <paramref name="line"/> (a trailing '\r' is not whitespace we indent with).</summary>
    private static string LeadingWhitespace(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            i++;
        return line[..i];
    }

    /// <summary>True when <paramref name="line"/> is empty or only whitespace.</summary>
    private static bool IsBlank(string line) => line.AsSpan().Trim().IsEmpty;

    /// <summary>The shared character prefix of <paramref name="a"/> and <paramref name="b"/>.</summary>
    private static string CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i])
            i++;
        return a[..i];
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Byte offset where 1-based <paramref name="line"/> begins in <paramref name="content"/>, counting UTF-8
    /// bytes (a multibyte char on a preceding line shifts the offset). Lines are split on '\n'; a line past the
    /// content (or line &lt;= 1) clamps to 0 / the content's byte length.
    /// </summary>
    private static int ByteOffsetOfLineStart(string content, int line)
    {
        if (line <= 1)
            return 0;

        var bytes = Encoding.UTF8.GetBytes(content);
        var newlinesNeeded = line - 1; // line N begins right after the (N-1)th newline
        var seen = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                seen++;
                if (seen == newlinesNeeded)
                    return i + 1;
            }
        }
        // Fewer lines than requested: clamp to end (append point).
        return bytes.Length;
    }

    /// <summary>
    /// True when the symbol's whole span is degenerate — <c>[0, 0)</c>. ExtractReader.ReadEditSpan substitutes 0
    /// for a NULL <c>start_byte</c>/<c>end_byte</c> (a symbol whose byte location the index never recorded), so a
    /// <c>[0, 0)</c> span means "no usable location", NOT "a symbol at the top of the file" (that has a non-zero
    /// end). Insert/add_doc ops would otherwise silently splice at file position 0 — they reject it instead.
    /// </summary>
    private static bool IsDegenerate(SymbolEditSpan span) => span.StartByte == 0 && span.EndByte == 0;

    private static EditPlan DegenerateSpan(SymbolEditSpan span) =>
        EditPlan.Failure(new EditError(
            EditErrorKind.InvalidSpan,
            $"Symbol \"{span.Name}\" has no recorded location in the index (degenerate span [0, 0)); cannot " +
            "perform the edit. Run a workspace refresh to update the index, then retry."));

    private static EditPlan BodySpanUnavailable(SymbolEditSpan span, string what) =>
        EditPlan.Failure(new EditError(
            EditErrorKind.BodySpanUnavailable,
            $"Symbol \"{span.Name}\" has no body span; cannot replace its {what} (e.g. a field has no body)."));

    private static EditPlan EmptyNewText(string operation) =>
        EditPlan.Failure(new EditError(
            EditErrorKind.MissingArgument,
            $"new_text must not be empty for {operation}."));
}
