using Miller.Indexing;

namespace Miller.Server.Tools;

internal enum SearchRouteKind
{
    Regions,
    Markers,
    Content,
    TextContent,
    Symbols,
}

internal sealed record SearchRoute(
    SearchRouteKind Kind,
    SearchToolMode Mode,
    IReadOnlySet<string>? RegionKinds = null,
    IReadOnlyCollection<string>? ContentKinds = null,
    string? ModeNote = null,
    bool Mixed = false,
    string? SymbolQuery = null,
    string? FileQuery = null);

internal static class SearchRoutePlanner
{
    public static SearchRoute Plan(
        string? requestedMode,
        string? regions,
        string? query = null)
    {
        string modeText = requestedMode ?? "auto";
        SearchToolMode mode = SearchTool.ParseMode(modeText);
        if (mode == SearchToolMode.Markers)
            return new SearchRoute(SearchRouteKind.Markers, mode);

        IReadOnlySet<string>? regionKinds = SearchTool.ParseRegionKinds(regions);
        if (regionKinds is not null)
        {
            string? modeNote = mode == SearchToolMode.Auto
                ? null
                : $"mode={modeText} ignored; regions search uses source-region text.";
            return new SearchRoute(SearchRouteKind.Regions, mode, RegionKinds: regionKinds, ModeNote: modeNote);
        }

        if (mode == SearchToolMode.Auto &&
            TrySplitMixedQuery(query, out string? fileQuery, out string? symbolQuery))
        {
            return new SearchRoute(
                SearchRouteKind.Symbols,
                mode,
                Mixed: true,
                SymbolQuery: symbolQuery,
                FileQuery: fileQuery);
        }

        return mode switch
        {
            SearchToolMode.Content => new SearchRoute(SearchRouteKind.Content, mode),
            SearchToolMode.Source => new SearchRoute(
                SearchRouteKind.TextContent,
                mode,
                ContentKinds: [TextContentKind.WorkspaceSource]),
            SearchToolMode.External or SearchToolMode.Web or SearchToolMode.AllText => new SearchRoute(
                SearchRouteKind.TextContent,
                mode,
                ContentKinds: SearchTool.ContentKindsForMode(mode)),
            _ => new SearchRoute(SearchRouteKind.Symbols, mode),
        };
    }

    private static bool TrySplitMixedQuery(
        string? query,
        out string? fileQuery,
        out string? symbolQuery)
    {
        fileQuery = null;
        symbolQuery = null;
        if (string.IsNullOrWhiteSpace(query))
            return false;

        string[] parts = query.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int pathIndex = Array.FindIndex(parts, static part =>
            part.Contains('/', StringComparison.Ordinal) ||
            part.Contains('\\', StringComparison.Ordinal));
        if (pathIndex < 0 || parts.Length < 2)
            return false;

        string[] symbolParts = parts
            .Where((_, index) => index != pathIndex)
            .ToArray();
        if (symbolParts.Length == 0 || symbolParts.All(string.IsNullOrWhiteSpace))
            return false;

        fileQuery = parts[pathIndex];
        symbolQuery = string.Join(' ', symbolParts);
        return true;
    }
}
