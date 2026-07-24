using Miller.Indexing;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

public sealed class SearchRoutePlannerTests
{
    [Fact]
    public void Plan_RegionsOverrideRequestedMode()
    {
        SearchRoute route = SearchRoutePlanner.Plan("source", "comment,docstring");

        Assert.Equal(SearchRouteKind.Regions, route.Kind);
        Assert.Equal(SearchToolMode.Source, route.Mode);
        Assert.Equal(["comment", "doc_comment"], route.RegionKinds);
        Assert.Equal("mode=source ignored; regions search uses source-region text.", route.ModeNote);
    }

    [Fact]
    public void Plan_InvalidRegions_IncludesCompactExample()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SearchRoutePlanner.Plan("source", "class"));

        Assert.Contains("regions=comment", ex.Message);
        Assert.Contains("doc_comment,string_literal", ex.Message);
    }

    [Fact]
    public void Plan_SourceRoutesToWorkspaceSourceContentKind()
    {
        SearchRoute route = SearchRoutePlanner.Plan("source", regions: null);

        Assert.Equal(SearchRouteKind.TextContent, route.Kind);
        Assert.Equal([TextContentKind.WorkspaceSource], route.ContentKinds);
    }

    [Fact]
    public void Plan_DocsAliasUsesLegacyContentShape()
    {
        SearchRoute route = SearchRoutePlanner.Plan("docs", regions: null);

        Assert.Equal(SearchRouteKind.Content, route.Kind);
        Assert.Equal(SearchToolMode.Content, route.Mode);
        Assert.Null(route.ContentKinds);
    }

    [Fact]
    public void Plan_AutoPathAndSymbolQueryCreatesMixedRoute()
    {
        SearchRoute route = SearchRoutePlanner.Plan(
            "auto",
            regions: null,
            query: "src/Miller.Server SearchTool");

        Assert.Equal(SearchRouteKind.Symbols, route.Kind);
        Assert.True(route.Mixed);
        Assert.Equal("SearchTool", route.SymbolQuery);
        Assert.Equal("src/Miller.Server", route.FileQuery);
    }

    [Fact]
    public void Plan_ExplicitSymbolModeKeepsPathAndSymbolQueryOnSymbolRoute()
    {
        SearchRoute route = SearchRoutePlanner.Plan(
            "symbol",
            regions: null,
            query: "src/Miller.Server SearchTool");

        Assert.False(route.Mixed);
        Assert.Null(route.SymbolQuery);
        Assert.Null(route.FileQuery);
    }
}
