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
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(oldText);

        if (oldText.Length == 0)
            return EditPlan.Failure(new EditError(EditErrorKind.MissingArgument, "old_text must not be empty for replace_text."));

        var charMatches = FindNonOverlappingMatches(content, oldText);
        if (charMatches.Count == 0)
            return EditPlan.Failure(new EditError(EditErrorKind.TextNotFound, $"old_text not found: \"{oldText}\"."));

        var selected = occurrence switch
        {
            Occurrence.First => [charMatches[0]],
            Occurrence.Last => [charMatches[^1]],
            Occurrence.All => charMatches,
            _ => charMatches,
        };

        var oldTextByteLen = Encoding.UTF8.GetByteCount(oldText);
        var edits = new List<TextEdit>(selected.Count);
        foreach (var charIndex in selected)
        {
            var startByte = Encoding.UTF8.GetByteCount(content.AsSpan(0, charIndex));
            edits.Add(new TextEdit(startByte, startByte + oldTextByteLen, string.Empty));
        }
        return EditPlan.Success(edits);
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
    /// The planner adds NO comment prefix — <paramref name="newText"/> is inserted verbatim, so this works for
    /// every language (the caller supplies "///", "#", "--", etc.). Decision log #7.
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
        return EditPlan.Success([new TextEdit(lineStartByte, lineStartByte, newText + "\n")]);
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>All non-overlapping ordinal match char-indices of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
    private static List<int> FindNonOverlappingMatches(string haystack, string needle)
    {
        var matches = new List<int>();
        var from = 0;
        while (from <= haystack.Length - needle.Length)
        {
            var at = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (at < 0)
                break;
            matches.Add(at);
            from = at + needle.Length; // non-overlapping: resume past this match
        }
        return matches;
    }

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
