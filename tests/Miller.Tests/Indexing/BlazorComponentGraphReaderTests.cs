using System.Text.Json;
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
    public void Read_NoBlazorFacts_ReturnsEmptyWithoutOpeningDatabase()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        Assert.Empty(BlazorComponentGraphReader.Read(dbPath, []));
        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void Read_SimpleComponentWithLocalNamespaceContext_ProducesUsesEdgeAndReverseReachability()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Pages.PageA", "Pages/PageA.razor"),
            Component(SharedWidgetId, "SharedWidget", "Shared.SharedWidget", "Shared/SharedWidget.razor"));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "SharedWidget", "PageA", "[\"Shared\"]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;
        var edges = BlazorComponentGraphReader.Read(fixture.DbPath, facts);

        Assert.Equal([new GraphEdge(PageAId, SharedWidgetId, "uses")], edges);

        var index = RepositoryIndexLoader.Load(fixture.DbPath);
        var dependent = Assert.Single(index.Dependents(SharedWidgetId));
        Assert.Equal(PageAId, dependent.SymbolId);
    }

    [Fact]
    public void Read_SimpleUniqueComponentOutsideEffectiveNamespaces_ProducesNoEdge()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Pages.PageA", "Pages/PageA.razor"),
            Component(SharedWidgetId, "SharedWidget", "Shared.SharedWidget", "Shared/SharedWidget.razor"));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "SharedWidget", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Empty(BlazorComponentGraphReader.Read(fixture.DbPath, facts));
        Assert.Empty(RepositoryIndexLoader.Load(fixture.DbPath).Dependents(SharedWidgetId));
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
    public void Read_AmbiguousSimpleTag_UsesSourceNamespace()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Sample.Admin.PageA", "Pages/PageA.razor"),
            Component(AdminWidgetId, "Widget", "Sample.Admin.Widget", "Features/Admin/Widget.razor"),
            Component(StoreWidgetId, "Widget", "Sample.Store.Widget", "Features/Store/Widget.razor"));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "Widget", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Equal(
            [new GraphEdge(PageAId, AdminWidgetId, "uses")],
            BlazorComponentGraphReader.Read(fixture.DbPath, facts));
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

    [Fact]
    public void Read_InheritedUsingsAccumulateRootToLeafAndStayWithinSubtree()
    {
        const string rootWidgetId = "30000000000000000000000000000001";
        const string otherRootWidgetId = "30000000000000000000000000000002";
        const string nestedWidgetId = "30000000000000000000000000000003";
        const string otherNestedWidgetId = "30000000000000000000000000000004";
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Sample.Pages.PageA", "Pages/PageA.razor"),
            Component(PageBId, "PageB", "Sample.Sibling.PageB", "Sibling/PageB.razor"),
            Component(rootWidgetId, "RootWidget", "Sample.Shared.RootWidget", "Shared/RootWidget.razor"),
            Component(otherRootWidgetId, "RootWidget", "Other.Shared.RootWidget", "Other/RootWidget.razor"),
            Component(nestedWidgetId, "NestedWidget", "Sample.Admin.NestedWidget", "Admin/NestedWidget.razor"),
            Component(otherNestedWidgetId, "NestedWidget", "Other.Admin.NestedWidget", "Other/NestedWidget.razor"),
            RazorDirective("40000000000000000000000000000001", "_Imports.razor", "using", "Sample.Shared"),
            RazorDirective("40000000000000000000000000000002", "Pages/_Imports.razor", "using", "Sample.Admin"));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "RootWidget", "PageA", "[]");
        AddReference(fixture, "fact-2", "Pages/PageA.razor", "NestedWidget", "PageA", "[]");
        AddReference(fixture, "fact-3", "Sibling/PageB.razor", "NestedWidget", "PageB", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;
        var edges = BlazorComponentGraphReader.Read(fixture.DbPath, facts);

        Assert.Equal(
            [new GraphEdge(PageAId, rootWidgetId, "uses"), new GraphEdge(PageAId, nestedWidgetId, "uses")],
            edges);

        var index = RepositoryIndexLoader.Load(fixture.DbPath);
        Assert.Contains(index.Dependents(rootWidgetId), dependent => dependent.SymbolId == PageAId);
        Assert.Contains(index.Dependents(nestedWidgetId), dependent => dependent.SymbolId == PageAId);
        Assert.DoesNotContain(index.Dependents(nestedWidgetId), dependent => dependent.SymbolId == PageBId);
    }

    [Fact]
    public void Read_NearestImportNamespaceAddsDescendantFolderSuffix()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "PageA", "Pages/Admin/PageA.razor"),
            Component(AdminWidgetId, "Widget", "Widget", "Pages/Admin/Widget.razor"),
            Component(StoreWidgetId, "Widget", "Sample.Store.Widget", "Store/Widget.razor"),
            RazorDirective("40000000000000000000000000000001", "_Imports.razor", "namespace", "Sample.Root"),
            RazorDirective("40000000000000000000000000000002", "Pages/_Imports.razor", "namespace", "Sample.Pages"));
        AddReference(fixture, "fact-1", "Pages/Admin/PageA.razor", "Widget", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Equal(
            [new GraphEdge(PageAId, AdminWidgetId, "uses")],
            BlazorComponentGraphReader.Read(fixture.DbPath, facts));
    }

    [Theory]
    [InlineData("Alias = Sample.Admin")]
    [InlineData("static Sample.Admin")]
    [InlineData("Sample.Admin<T>")]
    [InlineData("$(Root).Admin")]
    public void Read_UnsupportedInheritedUsing_ProducesNoEdge(string directiveValue)
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Sample.Pages.PageA", "Pages/PageA.razor"),
            Component(AdminWidgetId, "Widget", "Sample.Admin.Widget", "Admin/Widget.razor"),
            RazorDirective("40000000000000000000000000000001", "_Imports.razor", "using", directiveValue));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "Widget", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Empty(BlazorComponentGraphReader.Read(fixture.DbPath, facts));
    }

    [Fact]
    public void Read_DuplicateTargetsInOneImportedNamespace_ProducesNoEdge()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Sample.Pages.PageA", "Pages/PageA.razor"),
            Component(AdminWidgetId, "Widget", "Sample.Admin.Widget", "Admin/Widget.razor"),
            Component(StoreWidgetId, "Widget", "Sample.Admin.Widget", "Store/Widget.razor"),
            RazorDirective("40000000000000000000000000000001", "_Imports.razor", "using", "Sample.Admin"));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "Widget", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Empty(BlazorComponentGraphReader.Read(fixture.DbPath, facts));
    }

    [Fact]
    public void Read_BackslashArtifactPathsUseImportsWithoutCrossingSiblings()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Sample.Pages.PageA", @"Pages\PageA.razor"),
            Component(PageBId, "PageB", "Sample.Sibling.PageB", @"Sibling\PageB.razor"),
            Component(AdminWidgetId, "Widget", "Sample.Admin.Widget", @"Admin\Widget.razor"),
            Component(StoreWidgetId, "Widget", "Other.Admin.Widget", @"Other\Widget.razor"),
            RazorDirective("40000000000000000000000000000001", @"Pages\_Imports.razor", "using", "Sample.Admin"));
        AddReference(fixture, "fact-1", @"Pages\PageA.razor", "Widget", "PageA", "[]");
        AddReference(fixture, "fact-2", @"Sibling\PageB.razor", "Widget", "PageB", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Equal(
            [new GraphEdge(PageAId, AdminWidgetId, "uses")],
            BlazorComponentGraphReader.Read(fixture.DbPath, facts));
    }

    [Fact]
    public void Read_ViewImportsDirectiveDoesNotScopeRazorComponentReference()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Sample.Pages.PageA", "Pages/PageA.razor"),
            Component(AdminWidgetId, "Widget", "Sample.Admin.Widget", "Admin/Widget.razor"),
            RazorDirective("40000000000000000000000000000001", "_ViewImports.cshtml", "using", "Sample.Admin"));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "Widget", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Empty(BlazorComponentGraphReader.Read(fixture.DbPath, facts));
    }

    [Fact]
    public void Read_TokenDirectiveDoesNotScopeRazorComponentReference()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Sample.Pages.PageA", "Pages/PageA.razor"),
            Component(AdminWidgetId, "Widget", "Sample.Admin.Widget", "Admin/Widget.razor"),
            RazorDirective(
                "40000000000000000000000000000001",
                "_Imports.razor",
                "using",
                "Sample.Admin",
                "razor-token-directive"));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "Widget", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Empty(BlazorComponentGraphReader.Read(fixture.DbPath, facts));
    }

    [Fact]
    public void Read_LiteralProjectRootNamespaceResolvesSameFolderAndProjectRootComponents()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "PageA", "Pages/PageA.razor"),
            Component(AdminWidgetId, "Widget", "Widget", "Pages/Widget.razor"),
            Component(SharedWidgetId, "RootWidget", "RootWidget", "RootWidget.razor"));
        fixture.SetArtifactMetadata("root_path", fixture.WorkspaceRoot);
        WriteProject(fixture, "Miller.Blazor.csproj", "<Project><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Project>");
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "Widget", "PageA", "[]");
        AddReference(fixture, "fact-2", "Pages/PageA.razor", "RootWidget", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Equal(
            [new GraphEdge(PageAId, AdminWidgetId, "uses"), new GraphEdge(PageAId, SharedWidgetId, "uses")],
            BlazorComponentGraphReader.Read(fixture.DbPath, facts));
    }

    [Theory]
    [InlineData("Areas/PageA.razor", "Areas/Widget.razor")]
    [InlineData(@"Areas\PageA.razor", @"Areas\Widget.razor")]
    public void Read_ProjectNameDefaultReplacesSpacesForBothArtifactSeparators(
        string pagePath,
        string widgetPath)
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "PageA", pagePath),
            Component(AdminWidgetId, "Widget", "Widget", widgetPath));
        fixture.SetArtifactMetadata("root_path", fixture.WorkspaceRoot);
        WriteProject(fixture, "Fancy App.csproj", "<Project />");
        System.IO.Directory.CreateDirectory(Path.Combine(fixture.WorkspaceRoot, "Areas"));
        AddReference(fixture, "fact-1", pagePath, "Widget", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Equal(
            [new GraphEdge(PageAId, AdminWidgetId, "uses")],
            BlazorComponentGraphReader.Read(fixture.DbPath, facts));
    }

    [Theory]
    [InlineData("{\"type\":7,\"qualifiedName\":\"Broken.Component\"}")]
    [InlineData("{")]
    public void Read_MalformedComponentMetadata_IsIgnored(string metadata)
    {
        using var fixture = CreateFixture(
            Component("00000000000000000000000000000001", "Broken", "Broken.Component", "Broken.razor") with
            {
                Metadata = metadata,
            },
            Component(PageAId, "PageA", "Sample.Pages.PageA", "Pages/PageA.razor"),
            Component(SharedWidgetId, "SharedWidget", "Sample.Shared.SharedWidget", "Shared/SharedWidget.razor"));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "SharedWidget", "PageA", "[\"Sample.Shared\"]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Equal(
            [new GraphEdge(PageAId, SharedWidgetId, "uses")],
            BlazorComponentGraphReader.Read(fixture.DbPath, facts));
    }

    [Theory]
    [InlineData("{\"type\":7,\"directiveName\":\"using\",\"directiveValue\":\"Broken.Namespace\"}")]
    [InlineData("{")]
    public void Read_MalformedDirectiveMetadata_IsIgnored(string metadata)
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "Sample.Pages.PageA", "Pages/PageA.razor"),
            Component(SharedWidgetId, "SharedWidget", "Sample.Shared.SharedWidget", "Shared/SharedWidget.razor"),
            RazorDirective("00000000000000000000000000000001", "_Imports.razor", "using", "Broken.Namespace") with
            {
                Metadata = metadata,
            },
            RazorDirective("40000000000000000000000000000001", "_Imports.razor", "using", "Sample.Shared"));
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "SharedWidget", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Equal(
            [new GraphEdge(PageAId, SharedWidgetId, "uses")],
            BlazorComponentGraphReader.Read(fixture.DbPath, facts));
    }

    [Fact]
    public void Read_MissingRootPathPreservesExactAndInheritedImportResolution()
    {
        using var fixture = CreateFixture(
            Component(PageAId, "PageA", "PageA", "Pages/PageA.razor"),
            Component(SharedWidgetId, "SharedWidget", "Sample.Shared.SharedWidget", "Shared/SharedWidget.razor"),
            Component(AdminWidgetId, "Widget", "Sample.Admin.Widget", "Admin/Widget.razor"),
            Component(StoreWidgetId, "ProjectWidget", "ProjectWidget", "Pages/ProjectWidget.razor"),
            RazorDirective("40000000000000000000000000000001", "_Imports.razor", "using", "Sample.Shared"));
        fixture.ExecuteWrite("DELETE FROM artifact_metadata WHERE key = 'root_path';");
        WriteProject(fixture, "Sample.csproj", "<Project><PropertyGroup><RootNamespace>Sample</RootNamespace></PropertyGroup></Project>");
        AddReference(fixture, "fact-1", "Pages/PageA.razor", "SharedWidget", "PageA", "[]");
        AddReference(fixture, "fact-2", "Pages/PageA.razor", "Sample.Admin.Widget", "PageA", "[]");
        AddReference(fixture, "fact-3", "Pages/PageA.razor", "ProjectWidget", "PageA", "[]");

        var facts = SqliteBridgeReader.Read(fixture.DbPath).StructuralFacts;

        Assert.Equal(
            [new GraphEdge(PageAId, SharedWidgetId, "uses"), new GraphEdge(PageAId, AdminWidgetId, "uses")],
            BlazorComponentGraphReader.Read(fixture.DbPath, facts));
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

    private static JulieDbFixture.SymbolRow RazorDirective(
        string id,
        string path,
        string directiveName,
        string directiveValue,
        string metadataType = "razor-directive")
    {
        string symbolName = directiveName == "using" ? directiveValue : $"@{directiveName}";
        return new(id, symbolName, "import", "razor", path, $"@{directiveName} {directiveValue}", 1, null)
        {
            Metadata = JsonSerializer.Serialize(new
            {
                type = metadataType,
                directiveName,
                directiveValue,
            }),
        };
    }

    private static JulieDbFixture CreateFixture(params JulieDbFixture.SymbolRow[] rows) =>
        JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows);

    private static void WriteProject(JulieDbFixture fixture, string relativePath, string content)
    {
        string path = Path.Combine(fixture.WorkspaceRoot, relativePath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

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
