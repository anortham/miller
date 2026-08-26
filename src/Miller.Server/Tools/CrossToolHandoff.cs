namespace Miller.Server.Tools;

/// <summary>
/// The single place that decides which OTHER Miller tool an empty result should hand the agent to.
///
/// <para>Every read tool already explains its own empty result and offers a retry on its own surface. What an
/// agent could not see is the case where the answer lives in a DIFFERENT tool: a symbol with no graph
/// references whose real uses are string literals, a file the index holds with no symbols at all, a change that
/// touched only docs and config. Those handoffs are written here so nine renderers cannot drift into nine
/// different opinions about when <c>patterns</c> beats <c>search</c>.</para>
///
/// <para>Every action is <see cref="ToolDiagnosticAction.CompactOnly"/>: this is the ADR-0001 nudge channel, so
/// JSON <c>diagnostic.next_actions</c> stays byte-identical. Callers place a handoff FIRST in the action list
/// when it is the better answer — JSON drops it, so leading with it reorders nothing a machine consumer reads.
/// </para>
/// </summary>
internal static class CrossToolHandoff
{
    /// <summary>
    /// A filter, not the query, produced the empty page: hits for this query exist outside the requested scope.
    /// Retrying a different MODE searches the same narrow scope again, so the honest next call drops the filter.
    /// </summary>
    internal static ToolDiagnosticAction SearchWithoutScope(string query, string? mode, string scopeDescription) =>
        new(
            SearchCall(query, mode),
            $"drop {scopeDescription} — matches exist outside it",
            CompactOnly: true);

    /// <summary>
    /// A docs/config prose miss. Source bodies are the other half of the workspace text corpus and are indexed
    /// separately, so a term absent from prose can still be present in code.
    /// </summary>
    internal static ToolDiagnosticAction SearchSourceText(string query) =>
        new(SearchCall(query, "source"), "search source-body text instead of docs/config prose", CompactOnly: true);

    /// <summary>
    /// A source-body miss. Prose is the other half of the corpus: a term absent from code can still be named in
    /// docs, config, or a design note.
    /// </summary>
    internal static ToolDiagnosticAction SearchDocsText(string query) =>
        new(SearchCall(query, "content"), "search docs/config prose instead of source bodies", CompactOnly: true);

    /// <summary>
    /// An imported-corpus miss (external/web/all-text). Nothing about the query is wrong when the corpus is
    /// empty, so the next call inventories what was imported rather than rephrasing.
    /// </summary>
    internal static ToolDiagnosticAction ContentInventory() =>
        new("content(operation=\"list\")", "list what is imported before rephrasing", CompactOnly: true);

    /// <summary>
    /// A source-region miss (comments, doc comments, string literals). Regions are a SUBSET of source text, so
    /// the whole body is the strictly wider retry.
    /// </summary>
    internal static ToolDiagnosticAction SearchWholeSourceBodies(string query) =>
        new(SearchCall(query, "source"), "widen from regions to whole source bodies", CompactOnly: true);

    /// <summary>
    /// No extracted marker facts. The marker words still appear as literal text, so source search answers
    /// "does this repo mention TODO at all" when the fact layer says nothing.
    /// </summary>
    internal static ToolDiagnosticAction SearchMarkerText(string markers) =>
        new(SearchCall(markers, "source"), "find the marker words as literal source text", CompactOnly: true);

    /// <summary>
    /// A symbol the reference graph links to nothing. Names reached only through dependency injection,
    /// reflection, or configuration appear as string literals, which the graph cannot resolve into edges.
    /// </summary>
    internal static ToolDiagnosticAction StringLiteralUsages(string symbol) =>
        new(
            $"search(query=\"{ToolDiagnosticText.EscapeCallArgument(symbol)}\", regions=\"string_literal\")",
            "find DI/reflection/config uses the graph cannot link",
            CompactOnly: true);

    /// <summary>
    /// A file the index holds with no symbols, or a change that touched only such files. Data, markup, and
    /// config files carry structural facts (routes, config keys, document structure) instead of symbols.
    /// </summary>
    internal static ToolDiagnosticAction FileStructureFacts(string path) =>
        new(
            $"patterns(operation=\"summary\", path=\"{ToolDiagnosticText.EscapeCallArgument(path)}\")",
            "read structure facts for a file with no indexed symbols",
            CompactOnly: true);

    /// <summary>
    /// A name that resolved to nothing at all. Search reaches paths, docs, and source text that symbol
    /// resolution does not, so it is the widest place left to look for a name inspect could not place.
    /// </summary>
    internal static ToolDiagnosticAction SearchForUnresolvedName(string target) =>
        new(
            $"search(query=\"{ToolDiagnosticText.EscapeCallArgument(target)}\")",
            "locate the name across symbols, paths, and text",
            CompactOnly: true);

    /// <summary>
    /// A file listing emptied by its own <c>kind</c> filter. The file IS indexed and DOES hold symbols, so the
    /// fix is the same call without the filter, not another tool.
    /// </summary>
    internal static ToolDiagnosticAction InspectFileWithoutKind(string path) =>
        new(
            $"inspect(target=\"{ToolDiagnosticText.EscapeCallArgument(path)}\")",
            "list every kind in this file",
            CompactOnly: true);

    /// <summary>
    /// A structural-fact miss. The extractor recognizes a fixed catalog of shapes; text the catalog does not
    /// name is still readable as source text.
    /// </summary>
    internal static ToolDiagnosticAction SearchRawTextForPattern(string query) =>
        new(SearchCall(query, "source"), "read the raw text when no extractor fact exists", CompactOnly: true);

    private static string SearchCall(string query, string? mode)
    {
        string call = $"search(query=\"{ToolDiagnosticText.EscapeCallArgument(query)}\")";
        return mode is null
            ? call
            : $"search(query=\"{ToolDiagnosticText.EscapeCallArgument(query)}\", mode=\"{mode}\")";
    }
}
