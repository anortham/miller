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
    string? ModeNote = null);

internal static class SearchRoutePlanner
{
    public static SearchRoute Plan(string? requestedMode, string? regions)
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
}
