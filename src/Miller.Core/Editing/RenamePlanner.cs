namespace Miller.Core.Editing;

/// <summary>
/// One occurrence of a name within a file: the exact byte token to rewrite (julie's <c>identifiers</c> span,
/// or the definition's name-token span). Byte offsets are absolute UTF-8 byte indices.
/// </summary>
/// <param name="StartByte">Inclusive start of the name token.</param>
/// <param name="EndByte">Exclusive end of the name token.</param>
/// <param name="StartLine">1-based line of the occurrence (for the preview site list).</param>
/// <param name="IsDefinition">True if this site is the symbol's definition name token (preview annotation).</param>
public sealed record RenameSite(int StartByte, int EndByte, int StartLine, bool IsDefinition = false);

/// <summary>
/// The Server-assembled per-file rename input: the file's current content and every name-token site in it
/// (def site + each reference). The Server reads these from julie's <c>identifiers</c> table; the planner is
/// pure and never touches the DB.
/// </summary>
/// <param name="FilePath">Absolute path of the file.</param>
/// <param name="Content">The file's current disk content the spans index into.</param>
/// <param name="Sites">Every occurrence of the old name in this file.</param>
public sealed record RenameFileInput(string FilePath, string Content, IReadOnlyList<RenameSite> Sites);

/// <summary>One file's entry in the rename preview: its path and how many sites will be rewritten in it.</summary>
/// <param name="FilePath">Absolute path of the file.</param>
/// <param name="SiteCount">Number of occurrences rewritten in this file.</param>
public sealed record RenameFileSummary(string FilePath, int SiteCount);

/// <summary>
/// The outcome of planning a workspace-wide rename: the per-file <see cref="PlannedEdit"/>s and a preview
/// summary, or a typed <see cref="EditError"/> (an invalid new name). Exactly one of
/// <see cref="PlannedEdits"/>+<see cref="Summary"/> / <see cref="Error"/> is populated.
/// </summary>
public sealed record RenamePlan
{
    /// <summary>The per-file edits to apply when <see cref="IsSuccess"/>; empty on error.</summary>
    public IReadOnlyList<PlannedEdit> PlannedEdits { get; }

    /// <summary>The per-file preview summary when <see cref="IsSuccess"/>; empty on error.</summary>
    public IReadOnlyList<RenameFileSummary> Summary { get; }

    /// <summary>Total sites rewritten across all files (the preview's headline count).</summary>
    public int TotalSites { get; }

    /// <summary>The failure when planning did not succeed; null on success.</summary>
    public EditError? Error { get; }

    /// <summary>True when planning produced edits (no error).</summary>
    public bool IsSuccess => Error is null;

    private RenamePlan(
        IReadOnlyList<PlannedEdit> plannedEdits,
        IReadOnlyList<RenameFileSummary> summary,
        int totalSites,
        EditError? error)
    {
        PlannedEdits = plannedEdits;
        Summary = summary;
        TotalSites = totalSites;
        Error = error;
    }

    internal static RenamePlan Success(IReadOnlyList<PlannedEdit> plannedEdits, IReadOnlyList<RenameFileSummary> summary, int totalSites)
        => new(plannedEdits, summary, totalSites, error: null);

    internal static RenamePlan Failure(EditError error)
        => new([], [], 0, error);
}

/// <summary>
/// Pure planner for a workspace-wide, exact-span, name-matched rename (M6 decision log #5, Components/1).
/// Given the old/new names and the per-file occurrence sites the Server read from julie's <c>identifiers</c>
/// table, it produces one <see cref="PlannedEdit"/> per file (each site → a <see cref="TextEdit"/> rewriting
/// that exact byte token to the new name) plus a preview summary listing every site. The planner does not resolve
/// or filter sites; it validates the new name and rewrites exactly what its caller supplies.
/// </summary>
public static class RenamePlanner
{
    /// <summary>
    /// Plan the rename. Files with no sites are omitted from the plan and the summary. A null/invalid
    /// <paramref name="newName"/> yields an <see cref="EditErrorKind.InvalidNewName"/> error with no edits.
    /// </summary>
    /// <param name="oldName">The current name (used only for the error message / preview narrative).</param>
    /// <param name="newName">The replacement identifier; must pass <see cref="IsValidIdentifier"/>.</param>
    /// <param name="files">The Server-assembled per-file inputs (content + occurrence sites).</param>
    /// <exception cref="ArgumentNullException"><paramref name="oldName"/>, <paramref name="newName"/>, or <paramref name="files"/> is null.</exception>
    public static RenamePlan Plan(string oldName, string newName, IReadOnlyList<RenameFileInput> files)
    {
        ArgumentNullException.ThrowIfNull(oldName);
        ArgumentNullException.ThrowIfNull(newName);
        ArgumentNullException.ThrowIfNull(files);

        if (!IsValidIdentifier(newName))
        {
            return RenamePlan.Failure(new EditError(
                EditErrorKind.InvalidNewName,
                $"new_name \"{newName}\" is not a valid identifier (must start with a letter or underscore and " +
                "contain only letters, digits, or underscores)."));
        }

        var plannedEdits = new List<PlannedEdit>();
        var summary = new List<RenameFileSummary>();
        var totalSites = 0;

        foreach (var file in files)
        {
            if (file.Sites.Count == 0)
                continue; // a file with no occurrences contributes nothing

            var edits = file.Sites
                .Select(s => new TextEdit(s.StartByte, s.EndByte, newName))
                .ToArray();

            // The sites come from julie's identifiers table but splice against the file's CURRENT content. If the
            // file drifted since indexing, a span can fall past EOF or two spans can overlap — TextSplicer.Apply
            // throws those. The planner is pure (decision log #2: it returns a typed EditError, never throws), so
            // convert the splice failure into a clean, actionable error for the WHOLE plan (all-or-nothing preview).
            string newContent;
            try
            {
                newContent = TextSplicer.Apply(file.Content, edits);
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                return RenamePlan.Failure(new EditError(
                    EditErrorKind.InvalidSpan,
                    $"rename span does not fit the current content of {file.FilePath} ({ex.Message}); " +
                    "the file changed since it was indexed — run a workspace refresh and retry."));
            }

            plannedEdits.Add(new PlannedEdit(file.FilePath, file.Content, newContent, edits));
            summary.Add(new RenameFileSummary(file.FilePath, edits.Length));
            totalSites += edits.Length;
        }

        return RenamePlan.Success(plannedEdits, summary, totalSites);
    }

    /// <summary>
    /// True if <paramref name="name"/> is a plausible identifier: a non-empty token whose first character is a
    /// Unicode letter or underscore and whose remaining characters are Unicode letters, digits, or underscores.
    /// Language-agnostic — it accepts identifiers from any of the supported languages and rejects member paths
    /// (<c>a.b</c>), call expressions (<c>a()</c>), whitespace, and leading-digit tokens.
    /// </summary>
    public static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        var first = name[0];
        if (first != '_' && !char.IsLetter(first))
            return false;

        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (c != '_' && !char.IsLetterOrDigit(c))
                return false;
        }
        return true;
    }
}
