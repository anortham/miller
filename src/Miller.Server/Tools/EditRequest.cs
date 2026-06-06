namespace Miller.Server.Tools;

/// <summary>
/// The fully-parsed parameters of one <c>edit</c> call (the tool surface from miller-toolbox.md §6, decision
/// log #1). The MCP method (<see cref="EditTool"/>) maps its string/bool params onto this; the pure
/// <see cref="EditService"/> consumes it. Defaults mirror the surface: <see cref="Apply"/> = false (preview),
/// <see cref="AllowStale"/> = false, <see cref="Occurrence"/> = "first",
/// <see cref="Format"/> = "compact". A write happens ONLY when <see cref="Apply"/> is explicitly true.
/// </summary>
/// <param name="Operation">replace_text | replace_symbol_body | replace_symbol_signature | rename_symbol | insert_before | insert_after | add_doc.</param>
/// <param name="Target">The smart-resolved file path or symbol name/id.</param>
public sealed record EditRequest(string Operation, string Target)
{
    /// <summary>The literal text to replace, for <c>replace_text</c>.</summary>
    public string? OldText { get; init; }

    /// <summary>The replacement text, or the new identifier for <c>rename_symbol</c>.</summary>
    public string? NewText { get; init; }

    /// <summary>Which match(es) of <see cref="OldText"/> to replace: first | last | all. Default first.</summary>
    public string Occurrence { get; init; } = "first";

    /// <summary>Must be flipped true to commit the edit to disk; otherwise the call previews a diff and writes nothing.</summary>
    public bool Apply { get; init; }

    /// <summary>Bypass the freshness gate's "index stale for this file" refusal (NOT the TOCTOU mid-edit check).</summary>
    public bool AllowStale { get; init; }

    /// <summary>Disambiguate an ambiguous symbol name to a file (passed through to the resolver).</summary>
    public string? Scope { get; init; }

    /// <summary>Output format: compact | json. Default compact.</summary>
    public string Format { get; init; } = "compact";
}
