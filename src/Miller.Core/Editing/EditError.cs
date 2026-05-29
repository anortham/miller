namespace Miller.Core.Editing;

/// <summary>The category of a planning failure — lets the Server choose an exit/message without string-matching.</summary>
public enum EditErrorKind
{
    /// <summary>The <c>old_text</c> to replace was not found in the content.</summary>
    TextNotFound,

    /// <summary>The operation needs a body span but the symbol has none (e.g. a field).</summary>
    BodySpanUnavailable,

    /// <summary>
    /// The symbol's span is degenerate (<c>[0, 0)</c>) — its byte location was not recorded in the index
    /// (ExtractReader.ReadEditSpan substitutes 0 for a NULL start/end). Editing it would silently splice at
    /// file position 0; the planner rejects it instead (decision log #7).
    /// </summary>
    InvalidSpan,

    /// <summary>The proposed new identifier is not a plausible identifier token.</summary>
    InvalidNewName,

    /// <summary>A required argument (e.g. <c>old_text</c>, <c>new_text</c>) was missing or empty.</summary>
    MissingArgument,
}

/// <summary>
/// A typed planning failure (M6 Components/1). Planners return this instead of throwing for expected,
/// caller-actionable conditions (text not found, NULL body span, bad new name); the Server turns it into a
/// clean tool error. Programmer errors (null args) still throw.
/// </summary>
/// <param name="Kind">The failure category.</param>
/// <param name="Message">A human-readable, actionable description.</param>
public sealed record EditError(EditErrorKind Kind, string Message);

/// <summary>
/// The outcome of planning a single-file operation: either the byte-span <see cref="TextEdit"/>s to apply, or a
/// typed <see cref="EditError"/>. Exactly one of <see cref="Edits"/>/<see cref="Error"/> is populated.
/// </summary>
public sealed record EditPlan
{
    /// <summary>The planned edits when <see cref="IsSuccess"/>; empty when this is an error.</summary>
    public IReadOnlyList<TextEdit> Edits { get; }

    /// <summary>The failure when planning did not succeed; null on success.</summary>
    public EditError? Error { get; }

    /// <summary>True when planning produced edits (no error).</summary>
    public bool IsSuccess => Error is null;

    private EditPlan(IReadOnlyList<TextEdit> edits, EditError? error)
    {
        Edits = edits;
        Error = error;
    }

    /// <summary>A successful plan carrying the given edits.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="edits"/> is null.</exception>
    public static EditPlan Success(IReadOnlyList<TextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        return new EditPlan(edits, error: null);
    }

    /// <summary>A failed plan carrying the given typed error.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static EditPlan Failure(EditError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new EditPlan([], error);
    }
}
