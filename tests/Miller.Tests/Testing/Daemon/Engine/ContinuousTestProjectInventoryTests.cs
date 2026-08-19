using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ContinuousTestProjectInventoryTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-inventory-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Materialize_keeps_build_output_outside_the_workspace()
    {
        string project = Path.Combine(_root, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project />");
        var items = ContinuousTestProjectInventory.MaterializeProjectWorkItems(
            [new ContinuousTestProject("proj:1", "ws:1", project, Framework: "xunit")],
            _root);

        ContinuousTestProjectWorkItem item = Assert.Single(items);
        Assert.Equal(Path.GetFullPath(project), item.Workspace.ProjectPath);
        Assert.False(IsInside(_root, item.Workspace.BuildOutputRoot));
        Assert.Contains("miller-ct", item.Workspace.BuildOutputRoot, StringComparison.Ordinal);
    }

    [Fact]
    public void Discover_skips_a_class_library_whose_name_contains_Test()
    {
        WriteProject("src/App.Testing/App.Testing.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    [Fact]
    public void Discover_skips_a_helper_host_whose_name_contains_Test()
    {
        WriteProject("tests/App.SharedTestHost/App.SharedTestHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    [Fact]
    public void Discover_accepts_a_test_sdk_project_regardless_of_name()
    {
        WriteProject("checks/App.Checks/App.Checks.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" />
              </ItemGroup>
            </Project>
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal("dotnet", project.Framework);
    }

    [Fact]
    public void Discover_accepts_a_testing_platform_project()
    {
        WriteProject("tests/App.Platform/App.Platform.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.Testing.Platform" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal("dotnet", project.Framework);
    }

    [Fact]
    public void Discover_seeds_trait_exclusions_from_the_projects_default_test_case_filter()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <VSTestTestCaseFilter>Category!=Scale</VSTestTestCaseFilter>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal("xunit", project.Framework);
        Assert.Equal(["Category=Scale"], project.ExcludeTraits);
    }

    [Fact]
    public void Discover_seeds_every_exclusion_from_a_conjunctive_filter()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <VSTestTestCaseFilter>Category!=Scale&amp;Category!=Nightly</VSTestTestCaseFilter>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal(["Category=Scale", "Category=Nightly"], project.ExcludeTraits);
    }

    [Fact]
    public void Discover_seeds_nothing_from_a_filter_it_cannot_represent_as_trait_exclusions()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <VSTestTestCaseFilter>Category!=Scale|Priority=1</VSTestTestCaseFilter>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Empty(project.ExcludeTraits);
    }

    private void WriteProject(string relativePath, string content)
    {
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void Disabled_projects_are_skipped()
    {
        string project = Path.Combine(_root, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project />");
        var items = ContinuousTestProjectInventory.MaterializeProjectWorkItems(
            [new ContinuousTestProject("proj:1", "ws:1", project, Enabled: false)],
            _root);
        Assert.Empty(items);
    }

    private static bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "."
            || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }
}
