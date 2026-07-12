using Miller.Core.Graph;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class BlazorComponentGraphReaderTests
{
    private const string PageAId = "10000000000000000000000000000001";
    private const string PageBId = "10000000000000000000000000000002";
    private const string SharedWidgetId = "20000000000000000000000000000001";
    private const string AdminWidgetId = "20000000000000000000000000000002";
    private const string StoreWidgetId = "20000000000000000000000000000003";

    [Fact]
    public void Read_SimpleUniqueComponent_ProducesUsesEdgeAndReverseReachability()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Pages.PageA", "Pages/PageA.razor"),
            Component(SharedWidgetId, "SharedWidget", "Shared.SharedWidget", "Shared/SharedWidget.razor"));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "SharedWidget", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;
        var edges = BlazorComponentGraphReader.Read(fixture.DbPath, facts);

        Assert.Equal([new GraphEdge(PageAId, SharedWidgetId, "uses")], edges);

        var index = RepositoryIndexLoader.Load(fixture.DbPath);
        var dependent = Assert.Single(index.Dependents(SharedWidgetId));
        Assert.Equal(PageAId, dependent.SymbolId);
    }

    [Fact]
    public void Read_UnmatchedExternalTag_ProducesNoEdgeWithoutExternalMetadata()
    {
        using var fixture = CreateFixture(Component(PageAId, "PageA", "Pages.PageA", "Pages/PageA.razor"));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "FluentButton", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Empty(BlazorComponentGraphReader.Read(fixture.DbPath, facts));
    }

    [Fact]
    public void Read_AmbiguousSimpleTag_UsesNamespaceContextOrSkips()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Pages.PageA", "Pages/PageA.razor"),
            Component(PageBId, "PageB", "Pages.PageB", "Pages/PageB.razor"),
            Component(AdminWidgetId, "Widget", "Features.Admin.Widget", "Features/Admin/Widget.razor"),
            Component(StoreWidgetId, "Widget", "Features.Store.Widget", "Features/Store/Widget.razor"));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "Widget", "PageA", "[\"Features.Admin\"]");
        AddReference(fixture, "fact-2", "Pages/PageB.razor", "Widget", "PageB", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;
        var edges = BlazorComponentGraphReader.Read(fixture.DbPath, facts);

        Assert.Equal([new GraphEdge(PageAId, AdminWidgetId, "uses")], edges);
    }

    [Fact]
    public void Read_FullyQualifiedTag_ResolvesExactQualifiedName()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Pages.PageA", "Pages/PageA.razor"),
            Component(AdminWidgetId, "Widget", "Features.Admin.Widget", "Features/Admin/Widget.razor"),
            Component(StoreWidgetId, "Widget", "Features.Store.Widget", "Features/Store/Widget.razor"));
        AddReference(
            fixture,
            "fact-1",
            "Pages/PageA.razor",
            "Features.Store.Widget",
            "PageA",
            "[\"Features.Admin\"]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;
        var edges = BlazorComponentGraphReader.Read(fixture.DbPath, facts);

        Assert.Equal([new GraphEdge(PageAId, StoreWidgetId, "uses")], edges);
    }

    private static JulieDbFixture.SymbolRow Component(
        string id,
        string name,
        string qualifiedName,
        string path) =>
        new(id, name, "class", "razor", path, $"public partial class {name}", 1, null)
        {
            Metadata = $$"""{"type":"razor-component","qualifiedName":"{{qualifiedName}}"}""",
        };

    private static JulieDbFixture CreateFixture(params JulieDbFixture.SymbolRow[] rows) =>
        JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows);

    private static void AddReference(
        JulieDbFixture fixture,
        string factId,
        string path,
        string tag,
        string containingComponent,
        string namespaceContext)
    {
        fixture.AddStructuralFact(
            factId,
            null,
            path,
            language: "razor",
            patternId: BridgeStructuralPatterns.BlazorComponentReference,
            captureName: "component_reference",
            nodeKind: "markup_element");
        fixture.ExecuteWrite($$"""
            UPDATE structural_facts
            SET metadata_json = '{"tag":"{{tag}}","containing_component":"{{containingComponent}}","namespace_context":{{namespaceContext}},"generic_arguments":[]}'
            WHERE structural_fact_id = '{{factId}}';
            """);
    }
}
