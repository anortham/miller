using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the docs-like scope filter for content search (phase 3): prose/markup/config files and anything
/// under a docs/ tree are content-searchable; source files are not (symbol search already covers them).
/// </summary>
public sealed class ContentFileClassifierTests
{
    [Theory]
    [InlineData("README.md", "markdown")]
    [InlineData("notes.txt", "text")]
    [InlineData("guide.rst", "restructuredtext")]
    [InlineData("design.adoc", "asciidoc")]
    [InlineData("page.mdx", "mdx")]
    [InlineData("CHANGES.markdown", "markdown")]
    [InlineData("outline.org", "org")]
    public void IsDocsLike_ProseAndMarkup_True(string path, string language) =>
        Assert.True(ContentFileClassifier.IsDocsLike(path, language));

    [Theory]
    [InlineData("appsettings.json", "json")]
    [InlineData("config.yaml", "yaml")]
    [InlineData("compose.yml", "yaml")]
    [InlineData("Cargo.toml", "toml")]
    [InlineData("settings.ini", "ini")]
    public void IsDocsLike_Config_True(string path, string language) =>
        Assert.True(ContentFileClassifier.IsDocsLike(path, language));

    [Theory]
    [InlineData("src/Foo.cs", "csharp")]
    [InlineData("core/math.rs", "rust")]
    [InlineData("app/main.py", "python")]
    [InlineData("server.go", "go")]
    public void IsDocsLike_SourceFiles_False(string path, string language) =>
        Assert.False(ContentFileClassifier.IsDocsLike(path, language));

    [Theory]
    [InlineData("docs/architecture.cs")]      // path heuristic wins over extension
    [InlineData("docs/guide.md")]
    [InlineData("project/docs/setup.txt")]
    [InlineData("documentation/api.cs")]
    public void IsDocsLike_UnderDocsTree_True(string path) =>
        Assert.True(ContentFileClassifier.IsDocsLike(path, "csharp"));

    [Fact]
    public void IsDocsLike_LanguageMarkdown_IsCaseInsensitive() =>
        Assert.True(ContentFileClassifier.IsDocsLike("weird.xyz", "Markdown"));

    [Fact]
    public void IsDocsLike_ExtensionIsCaseInsensitive() =>
        Assert.True(ContentFileClassifier.IsDocsLike("READXME.MD", "x"));

    [Theory]
    [InlineData("src/Miller.Server/Miller.Server.csproj", "xml")]
    [InlineData("Directory.Build.props", "xml")]
    [InlineData("build/common.targets", "xml")]
    [InlineData("src/App/App.vbproj", "xml")]
    [InlineData("src/App/App.fsproj", "xml")]
    [InlineData("Miller.slnx", "xml")]
    [InlineData("pack/Miller.nuspec", "xml")]
    [InlineData("src/App/Resources.resx", "xml")]
    public void WorkspaceContentKind_MsBuildXml_IsWorkspaceConfig(string path, string language) =>
        Assert.Equal(TextContentKind.WorkspaceConfig, ContentFileClassifier.WorkspaceContentKind(path, language));

    [Fact]
    public void WorkspaceContentKind_LanguageXml_IsWorkspaceConfig() =>
        Assert.Equal(TextContentKind.WorkspaceConfig, ContentFileClassifier.WorkspaceContentKind("nuget.config", "xml"));

    [Theory]
    [InlineData("src/Miller.Server/Miller.Server.csproj")]
    [InlineData("Directory.Build.props")]
    public void IsDocsLike_MsBuildXml_True(string path) =>
        Assert.True(ContentFileClassifier.IsDocsLike(path, "xml"));

    [Fact]
    public void IsDocsLike_DocSubstringInDirName_DoesNotFalseMatch()
    {
        // "redoc"/"docker" must NOT trip the /doc/ heuristic.
        Assert.False(ContentFileClassifier.IsDocsLike("src/docker/Server.cs", "csharp"));
        Assert.False(ContentFileClassifier.IsDocsLike("redoc/Spec.cs", "csharp"));
    }
}
