using Miller.Indexing;

namespace Miller.Server.Tools;

internal sealed record SearchRouteExecutionRequest(
    string Query,
    int Limit,
    bool Json,
    bool? ExcludeTests,
    string? CompactBanner = null,
    string? FilePattern = null,
    string? Language = null,
    Func<IReadOnlyCollection<string>, IReadOnlySet<string>>? HasDocLookup = null);

internal sealed record SearchRouteExecutionResult(string Output, int Count, long SourceBytes = 0);

internal static class SearchRouteExecutor
{
    public static SearchRouteExecutionResult RunSymbols(
        ISymbolLookupIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        EnsureKind(route, SearchRouteKind.Symbols);

        string output = SearchTool.Run(
            index,
            request.Query,
            route.Mode,
            request.Limit,
            request.ExcludeTests,
            request.Json,
            out int count,
            request.CompactBanner,
            request.HasDocLookup,
            request.FilePattern,
            request.Language);

        return new SearchRouteExecutionResult(output, count);
    }

    public static SearchRouteExecutionResult RunContent(
        ITextContentSearchIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        EnsureKind(route, SearchRouteKind.Content);

        string output = SearchTool.RunContentCorpus(
            index,
            request.Query,
            request.Limit,
            request.Json,
            out int count,
            out long sourceBytes,
            request.CompactBanner,
            request.FilePattern,
            request.Language);

        return new SearchRouteExecutionResult(output, count, sourceBytes);
    }

    public static SearchRouteExecutionResult RunTextContent(
        ITextContentSearchIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        EnsureKind(route, SearchRouteKind.TextContent);

        bool hideTests = SearchTool.ResolveExcludeTests(request.ExcludeTests, request.Query, route.Mode);
        string output = SearchTool.RunTextContent(
            index,
            request.Query,
            route.ContentKinds!,
            request.Limit,
            hideTests,
            request.Json,
            out int count,
            out long sourceBytes,
            request.CompactBanner,
            request.FilePattern,
            request.Language);

        return new SearchRouteExecutionResult(output, count, sourceBytes);
    }

    public static SearchRouteExecutionResult RunRegions(
        IRegionSearchIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        EnsureKind(route, SearchRouteKind.Regions);

        bool hideTests = SearchTool.ResolveExcludeTests(request.ExcludeTests, request.Query, route.Mode);
        string output = SearchTool.RunRegions(
            index,
            request.Query,
            route.RegionKinds!,
            request.Limit,
            hideTests,
            request.Json,
            out int count,
            request.CompactBanner,
            route.ModeNote,
            request.FilePattern,
            request.Language);

        return new SearchRouteExecutionResult(output, count);
    }

    public static SearchRouteExecutionResult RunMarkers(
        IRegionSearchIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(request);
        EnsureKind(route, SearchRouteKind.Markers);

        bool hideTests = request.ExcludeTests ?? false;
        IReadOnlyList<string> markers = MarkerSearch.ParseMarkers(request.Query);
        string output = MarkerSearch.Run(
            index,
            markers,
            request.Limit,
            hideTests,
            request.Json,
            request.CompactBanner,
            request.FilePattern,
            request.Language,
            out int count);

        return new SearchRouteExecutionResult(output, count);
    }

    private static void EnsureKind(SearchRoute route, SearchRouteKind expected)
    {
        if (route.Kind != expected)
            throw new InvalidOperationException($"Search route {route.Kind} cannot run as {expected}.");
    }
}
