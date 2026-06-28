namespace Miller.Core.Editing;

/// <summary>
/// The single-file mutation operations M6 <c>edit</c> exposes (decision log #1, the agreed tool surface).
/// Each maps to one <see cref="EditPlanner"/> method (or, for <see cref="RenameSymbol"/>, the workspace-wide
/// <see cref="RenamePlanner"/>). The enum lets the Server map the tool's <c>operation</c> string to a planner
/// call without re-deriving the dispatch per call site.
/// </summary>
public enum EditOperation
{
    /// <summary>Replace literal <c>old_text</c> occurrences within a file.</summary>
    ReplaceText,

    /// <summary>Replace a symbol's body span <c>[body_start_byte, body_end_byte)</c>.</summary>
    ReplaceSymbolBody,

    /// <summary>Replace a symbol's signature span <c>[start_byte, body_start_byte)</c>.</summary>
    ReplaceSymbolSignature,

    /// <summary>Rename a symbol at every name-matched occurrence, workspace-wide.</summary>
    RenameSymbol,

    /// <summary>Insert text immediately before a symbol (zero-width at <c>start_byte</c>).</summary>
    InsertBefore,

    /// <summary>Insert text immediately after a symbol (zero-width at <c>end_byte</c>).</summary>
    InsertAfter,

    /// <summary>Insert a doc-comment block on the line where a symbol starts.</summary>
    AddDoc,
}

/// <summary>Which occurrence(s) of <c>old_text</c> a <see cref="EditOperation.ReplaceText"/> targets.</summary>
public enum Occurrence
{
    /// <summary>The first (lowest byte offset) match only.</summary>
    First,

    /// <summary>The last (highest byte offset) match only.</summary>
    Last,

    /// <summary>Every match.</summary>
    All,
}

/// <summary>How <see cref="EditOperation.ReplaceText"/> should locate <c>old_text</c> within current content.</summary>
public enum TextMatchMode
{
    /// <summary>Try exact first, then normalized line matching, then bounded fuzzy line matching.</summary>
    Auto,

    /// <summary>Ordinal, non-overlapping literal matching. This is the historical <c>replace_text</c> behavior.</summary>
    Exact,

    /// <summary>Line-aware matching that ignores indentation, trailing whitespace, and line-ending differences.</summary>
    Normalized,

    /// <summary>Bounded edit-distance matching for short localized snippets.</summary>
    Fuzzy,
}

/// <summary>
/// One byte-span splice: replace the absolute UTF-8 byte range <c>[StartByte, EndByte)</c> of a file's content
/// with <paramref name="Replacement"/>. A zero-width span (<c>EndByte == StartByte</c>) is a pure insertion at
/// that offset. Offsets are absolute UTF-8 byte indices (julie's span convention; see ExtractReader.SliceByBytes),
/// NOT UTF-16 char indices.
/// </summary>
/// <param name="StartByte">Inclusive start of the byte range to replace.</param>
/// <param name="EndByte">Exclusive end of the byte range to replace; equal to <paramref name="StartByte"/> for an insert.</param>
/// <param name="Replacement">The text spliced in (verbatim; the planner supplies any comment prefixes/newlines).</param>
public sealed record TextEdit(int StartByte, int EndByte, string Replacement);

/// <summary>
/// One selected <c>replace_text</c> match, expressed as UTF-8 byte offsets over the original content plus
/// line metadata for preview output.
/// </summary>
public sealed record TextReplaceMatch(
    int StartByte,
    int EndByte,
    int StartLine,
    int EndLine,
    TextMatchMode Mode,
    int Distance);

/// <summary>
/// A <c>replace_text</c> plan with match evidence. <see cref="Plan"/> remains the normal byte-splice contract;
/// the extra fields let callers render why a token-saving match was accepted without re-reading the full file.
/// </summary>
public sealed record TextReplaceMatchPlan(
    EditPlan Plan,
    TextMatchMode RequestedMode,
    TextMatchMode? MatchedMode,
    IReadOnlyList<TextReplaceMatch> Matches,
    int MatchCount)
{
    public bool IsSuccess => Plan.IsSuccess;

    public IReadOnlyList<TextEdit> Edits => Plan.Edits;

    public EditError? Error => Plan.Error;
}

/// <summary>
/// The span facts an <see cref="EditPlanner"/> operation needs about one symbol, read from julie's
/// <c>symbols</c> row (verified facts #1). <see cref="BodyStartByte"/>/<see cref="BodyEndByte"/> are null for
/// symbols with no body (e.g. a field) — body/signature ops reject those with a clean error.
/// </summary>
/// <param name="StartByte">The symbol's whole-span start (signature start).</param>
/// <param name="EndByte">The symbol's whole-span end.</param>
/// <param name="BodyStartByte">The body span start, or null when the symbol has no body.</param>
/// <param name="BodyEndByte">The body span end, or null when the symbol has no body.</param>
/// <param name="StartLine">1-based line on which the symbol starts (for <see cref="EditOperation.AddDoc"/>).</param>
/// <param name="Name">The symbol's name (for diagnostics / rename def-site location).</param>
public sealed record SymbolEditSpan(
    int StartByte,
    int EndByte,
    int? BodyStartByte,
    int? BodyEndByte,
    int StartLine,
    string Name);

/// <summary>
/// The per-file result of planning: the original content, the spliced content, and the exact
/// <see cref="TextEdit"/>s that produced it. The Server renders <see cref="UnifiedDiff"/> from
/// <see cref="OldContent"/>→<see cref="NewContent"/> for the preview, and applies the same plan on
/// <c>apply=true</c>.
/// </summary>
/// <param name="FilePath">Absolute path of the file this plan targets.</param>
/// <param name="OldContent">The content the plan was computed against (the current disk text).</param>
/// <param name="NewContent">The content after applying <paramref name="Edits"/>.</param>
/// <param name="Edits">The byte-span splices, in any order (the splicer sorts/validates them).</param>
public sealed record PlannedEdit(
    string FilePath,
    string OldContent,
    string NewContent,
    IReadOnlyList<TextEdit> Edits);
