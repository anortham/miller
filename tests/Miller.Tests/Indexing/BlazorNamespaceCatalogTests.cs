using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class BlazorNamespaceCatalogTests
{
    [Fact]
    public void QualifiedNames_LiteralRootNamespaceIncludesProjectRelativeFolders()
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "Sample.csproj", "<Project><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Project>");
        var component = Component("Features/Admin/Widget.razor", "Widget");
        MaterializeComponent(fixture, component.Path);

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [component], []);

        Assert.Equal(["Sample.Web.Features.Admin.Widget"], catalog.QualifiedNames(component));
    }

    [Fact]
    public void QualifiedNames_ProjectFileNameDefaultReplacesSpacesOnly()
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "Sample Portal.csproj", "<Project />");
        var component = Component("Features/Widget.razor", "Widget");
        MaterializeComponent(fixture, component.Path);

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [component], []);

        Assert.Equal(["Sample_Portal.Features.Widget"], catalog.QualifiedNames(component));
    }

    [Fact]
    public void QualifiedNames_NearestNamespaceDirectiveOverridesProjectRootAndAppendsSuffix()
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "Sample.csproj", "<Project><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Project>");
        var component = Component("Features/Admin/Widget.razor", "Widget");
        MaterializeComponent(fixture, component.Path);
        var directives = new[]
        {
            new RazorImportDirective("_Imports.razor", "namespace", "Ignored.Root"),
            new RazorImportDirective("Features/_Imports.razor", "namespace", "Custom.Features"),
        };

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [component], directives);

        Assert.Equal(["Custom.Features.Admin.Widget"], catalog.QualifiedNames(component));
    }

    [Fact]
    public void QualifiedNames_InvalidNearestNamespaceDoesNotFallBackToProjectRoot()
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "Sample.csproj", "<Project><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Project>");
        var component = Component("Features/Widget.razor", "Widget");
        MaterializeComponent(fixture, component.Path);
        var directives = new[]
        {
            new RazorImportDirective("Features/_Imports.razor", "namespace", "$(GeneratedNamespace)"),
        };

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [component], directives);

        Assert.Empty(catalog.QualifiedNames(component));
    }

    [Fact]
    public void QualifiedNames_ExplicitDottedNameRemainsAuthoritative()
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "Sample.csproj", "<Project><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Project>");
        var component = Component("Features/Widget.razor", "Widget", "External.Components.Widget");
        MaterializeComponent(fixture, component.Path);

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [component], []);

        Assert.Equal(["External.Components.Widget"], catalog.QualifiedNames(component));
    }

    [Fact]
    public void EffectiveNamespaces_IncludeSourceFolderAndProjectRoot()
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "Sample.csproj", "<Project><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Project>");
        var source = Component("Features/Admin/Page.razor", "Page");
        MaterializeComponent(fixture, source.Path);

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [source], []);

        Assert.Equal(["Sample.Web", "Sample.Web.Features.Admin"], catalog.EffectiveNamespaces(source, []));
    }

    [Fact]
    public void QualifiedNames_AmbiguousNearestProjectStopsBeforeParentProject()
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "Parent.csproj", "<Project><PropertyGroup><RootNamespace>Parent.Valid</RootNamespace></PropertyGroup></Project>");
        WriteFile(fixture, "Nested/One.csproj", "<Project><PropertyGroup><RootNamespace>Nested.One</RootNamespace></PropertyGroup></Project>");
        WriteFile(fixture, "Nested/Two.csproj", "<Project><PropertyGroup><RootNamespace>Nested.Two</RootNamespace></PropertyGroup></Project>");
        var component = Component("Nested/Pages/Widget.razor", "Widget");
        MaterializeComponent(fixture, component.Path);

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [component], []);

        Assert.Empty(catalog.QualifiedNames(component));
    }

    [Theory]
    [InlineData("<Project><PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\"><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><RootNamespace Condition=\"'$(Configuration)' == 'Debug'\">Sample.Web</RootNamespace></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><RootNamespace>$(Company).Web</RootNamespace></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><RootNamespace>Sample.One</RootNamespace><RootNamespace>Sample.Two</RootNamespace></PropertyGroup></Project>")]
    [InlineData("<Project><Import Project=\"Shared.props\" /><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Project>")]
    [InlineData("<Project><Target Name=\"SetNamespace\"><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Target></Project>")]
    [InlineData("<Project><Choose><Otherwise><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Otherwise></Choose></Project>")]
    [InlineData("<!DOCTYPE Project [<!ENTITY root 'Sample.Web'>]><Project><PropertyGroup><RootNamespace>&root;</RootNamespace></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><RootNamespace>Sample-Web</RootNamespace></PropertyGroup></Project>")]
    [InlineData("<Project><PropertyGroup><RootNamespace>Sample.Web</RootNamespace>")]
    public void QualifiedNames_UnsupportedProjectEvaluationFailsClosed(string projectXml)
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "Sample.csproj", projectXml);
        var component = Component("Pages/Widget.razor", "Widget");
        MaterializeComponent(fixture, component.Path);

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [component], []);

        Assert.Empty(catalog.QualifiedNames(component));
    }

    [Theory]
    [InlineData("Directory.Build.props", "<Project><PropertyGroup><RootNamespace>Imported.Root</RootNamespace></PropertyGroup></Project>")]
    [InlineData("Directory.Build.targets", "<Project><Import Project=\"Shared.targets\" /></Project>")]
    public void QualifiedNames_VisibleDirectoryBuildNamespaceEvidenceFailsClosed(string fileName, string content)
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "App/App.csproj", "<Project><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Project>");
        WriteFile(fixture, fileName, content);
        var component = Component("App/Pages/Widget.razor", "Widget");
        MaterializeComponent(fixture, component.Path);

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [component], []);

        Assert.Empty(catalog.QualifiedNames(component));
    }

    [Fact]
    public void QualifiedNames_SiblingProjectsStayIsolated()
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "First/First.csproj", "<Project><PropertyGroup><RootNamespace>First.Root</RootNamespace></PropertyGroup></Project>");
        WriteFile(fixture, "Second/Second.csproj", "<Project><PropertyGroup><RootNamespace>Second.Root</RootNamespace></PropertyGroup></Project>");
        var first = Component("First/Pages/Widget.razor", "Widget");
        var second = Component("Second/Pages/Widget.razor", "Widget");
        MaterializeComponent(fixture, first.Path);
        MaterializeComponent(fixture, second.Path);

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [first, second], []);

        Assert.Equal(["First.Root.Pages.Widget"], catalog.QualifiedNames(first));
        Assert.Equal(["Second.Root.Pages.Widget"], catalog.QualifiedNames(second));
    }

    [Fact]
    public void QualifiedNames_SymlinkedComponentDirectoryFailsClosed()
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "Sample.csproj", "<Project><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Project>");
        string realDirectory = Path.Combine(fixture.WorkspaceRoot, "Real");
        string linkedDirectory = Path.Combine(fixture.WorkspaceRoot, "Linked");
        System.IO.Directory.CreateDirectory(realDirectory);
        if (!TryCreateDirectoryLink(linkedDirectory, realDirectory))
            Assert.Skip("Symbolic directory links are unavailable on this platform.");
        var component = Component("Linked/Widget.razor", "Widget");

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [component], []);

        Assert.Empty(catalog.QualifiedNames(component));
    }

    [Fact]
    public void QualifiedNames_SymlinkedProjectFileFailsClosed()
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "ActualProject.xml", "<Project><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Project>");
        string projectLink = Path.Combine(fixture.WorkspaceRoot, "Linked.csproj");
        if (!TryCreateFileLink(projectLink, Path.Combine(fixture.WorkspaceRoot, "ActualProject.xml")))
            Assert.Skip("Symbolic file links are unavailable on this platform.");
        var component = Component("Pages/Widget.razor", "Widget");
        MaterializeComponent(fixture, component.Path);

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [component], []);

        Assert.Empty(catalog.QualifiedNames(component));
    }

    [Fact]
    public void QualifiedNames_OversizedProjectFileFailsClosed()
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "Sample.csproj", "<Project>" + new string(' ', 1_048_576) + "</Project>");
        var component = Component("Pages/Widget.razor", "Widget");
        MaterializeComponent(fixture, component.Path);

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [component], []);

        Assert.Empty(catalog.QualifiedNames(component));
    }

    [Fact]
    public void QualifiedNames_PathOutsideWorkspaceFailsClosed()
    {
        using var fixture = CreateFixture();
        WriteFile(fixture, "Sample.csproj", "<Project><PropertyGroup><RootNamespace>Sample.Web</RootNamespace></PropertyGroup></Project>");
        var component = Component("../Widget.razor", "Widget");

        var catalog = BlazorNamespaceCatalog.Build(fixture.WorkspaceRoot, [component], []);

        Assert.Empty(catalog.QualifiedNames(component));
    }

    private static JulieDbFixture CreateFixture() =>
        JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, []);

    private static BlazorComponentIdentity Component(
        string path,
        string name,
        string? declaredQualifiedName = null) =>
        new(Guid.NewGuid().ToString("N"), path, name, declaredQualifiedName ?? name);

    private static void MaterializeComponent(JulieDbFixture fixture, string path) =>
        WriteFile(fixture, path.Replace('\\', '/'), string.Empty);

    private static void WriteFile(JulieDbFixture fixture, string relativePath, string content)
    {
        string platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string path = Path.Combine(fixture.WorkspaceRoot, platformPath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            System.IO.Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
