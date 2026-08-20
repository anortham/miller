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
    public void Build_output_segments_stay_fixed_width_for_real_workspace_and_project_ids()
    {
        ContinuousTestProjectWorkItem item = MaterializeDeeplyNestedPytestProject();

        string[] segments = BuildRootSegments(item.Workspace.BuildOutputRoot);
        Assert.Equal(3, segments.Length);
        Assert.Equal("build", segments[0]);
        Assert.Equal(ContinuousTestProjectInventory.SegmentHashLength, segments[1].Length);
        Assert.Equal(ContinuousTestProjectInventory.SegmentHashLength, segments[2].Length);
        // The workspace segment is the same 12 characters `workspace list` prints as the display id,
        // so a person can match a build directory to a workspace by eye.
        Assert.Equal(RealWorkspaceId[..ContinuousTestProjectInventory.SegmentHashLength], segments[1]);
    }

    [Fact]
    public void Composed_provider_artifact_paths_stay_inside_the_windows_path_budget()
    {
        ContinuousTestProjectWorkItem item = MaterializeDeeplyNestedPytestProject();

        foreach (string artifactName in LongestProviderArtifactNames)
        {
            // <build root>/<generation id>/TestResults/<artifact> — the deepest path CT hands a
            // provider. CtGenerationPaths writes the generation id as 'g' plus 12 hex characters.
            string composed = Path.Combine(
                item.Workspace.BuildOutputRoot,
                "g0123456789ab",
                "TestResults",
                artifactName);
            string tail = Path.GetRelativePath(CtTempPaths.Root, composed);
            Assert.True(
                tail.Length <= BuildRootTailBudget,
                $"{tail.Length} characters below the CT temp root exceeds the {BuildRootTailBudget} budget: {tail}");
        }
    }

    /// <summary>
    /// Windows MAX_PATH is 260 characters and a machine without long paths enabled fails there. Only
    /// the part BELOW the continuous-test temp root belongs to Miller, so the budget covers that tail
    /// and leaves the other 100 characters for the ambient temp root (a Windows
    /// <c>&lt;temp&gt;\miller-ct</c> is about 45).
    /// </summary>
    private const int BuildRootTailBudget = 160;

    /// <summary>A real Miller workspace id: the 64-character SHA-256 digest of the canonical root.</summary>
    private const string RealWorkspaceId =
        "9f2b7c1d4e6a8035c1d9e7f3a5b40628d9c3e1f7a24b60d8c5e39f1a7b04c26d";

    /// <summary>A 64-character run hash, the width every provider stamps into an artifact name.</summary>
    private const string RunHash =
        "3c81f0a5b6d2e47390af51c8b6d0e2f4a7c93b15d8e604f2a1c7b3d90e58f24a";

    /// <summary>
    /// The longest artifact file name each provider composes under a generation's TestResults
    /// directory. Every one carries a full 64-character run hash, which is why the build root above
    /// it must stay short.
    /// </summary>
    private static readonly string[] LongestProviderArtifactNames =
    [
        $"run-{RunHash}.xml",               // PythonTestProvider: the pytest --junitxml argument.
        $"run-{RunHash}.part000.junit.xml", // DotnetTestProvider: one chunk of a split xunit v3 run.
        $"run-{RunHash}.trx",               // DotnetTestProvider: the VSTest trx.
        $"run-{RunHash}.json",              // JavaScriptTestProvider: vitest and jest.
        $"run-{RunHash}.cargo.log",         // RustTestProvider: the cargo log.
    ];

    private ContinuousTestProjectWorkItem MaterializeDeeplyNestedPytestProject()
    {
        string project = Path.Combine(
            _root, "tests", "integration", "python", "services", "billing", "pyproject.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "[tool.pytest.ini_options]");

        return Assert.Single(ContinuousTestProjectInventory.MaterializeProjectWorkItems(
            [
                new ContinuousTestProject(
                    ContinuousTestProjectInventory.ProjectId(RealWorkspaceId, _root, project),
                    RealWorkspaceId,
                    project,
                    Framework: "pytest"),
            ],
            _root));
    }

    private static string[] BuildRootSegments(string buildOutputRoot) =>
        Path.GetRelativePath(CtTempPaths.Root, buildOutputRoot)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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

    [Fact]
    public void Discover_stops_at_a_linked_worktree_whose_dot_git_is_a_file()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", XunitProject);
        WriteProject(".worktrees/other-branch/tests/App.Tests/App.Tests.csproj", XunitProject);
        // `git worktree add` writes a .git FILE holding "gitdir: <path>", never a directory.
        File.WriteAllText(
            Path.Combine(_root, ".worktrees", "other-branch", ".git"),
            "gitdir: " + Path.Combine(_root, ".git", "worktrees", "other-branch"));

        ContinuousTestProject project = Assert.Single(
            ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.DoesNotContain(".worktrees", project.ProjectPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Discover_stops_at_a_nested_clone_whose_dot_git_is_a_directory()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", XunitProject);
        WriteProject("vendored/clone/tests/App.Tests/App.Tests.csproj", XunitProject);
        Directory.CreateDirectory(Path.Combine(_root, "vendored", "clone", ".git"));

        ContinuousTestProject project = Assert.Single(
            ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.DoesNotContain("clone", project.ProjectPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// A submodule is part of THIS build: its source is in this working tree and this workspace's index
    /// covers it, so a developer who breaks one of its tests has to see the verdict go red. Dropping it
    /// because it carries a <c>.git</c> marker made continuous testing report green for a repository
    /// whose tests it had silently stopped running.
    /// </summary>
    [Fact]
    public void Discover_keeps_a_submodule_because_its_tests_are_part_of_this_build()
    {
        WriteRepositoryAdminDir();
        Directory.CreateDirectory(Path.Combine(_root, ".git", "modules", "shared"));
        WriteProject("tests/App.Tests/App.Tests.csproj", XunitProject);
        WriteProject("libs/shared/tests/Shared.Tests/Shared.Tests.csproj", XunitProject);
        // `git submodule add` writes a .git FILE too, but its gitdir lands under THIS repository's
        // own admin directory rather than under another checkout's.
        File.WriteAllText(
            Path.Combine(_root, "libs", "shared", ".git"),
            "gitdir: " + Path.Combine(_root, ".git", "modules", "shared"));

        var projects = ContinuousTestProjectInventory.Discover(_root, "ws:1");

        Assert.Equal(2, projects.Count);
        Assert.Contains(
            projects,
            project => string.Equals(
                Path.GetFileName(project.ProjectPath),
                "Shared.Tests.csproj",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// A linked worktree of this very repository points its gitdir into this root's own admin directory,
    /// so "the gitdir belongs to this repository" is NOT enough to keep a directory in the walk. Only the
    /// <c>modules</c> half of that admin directory holds submodules; <c>worktrees</c> holds another branch.
    /// </summary>
    [Fact]
    public void Discover_stops_at_a_linked_worktree_of_this_same_repository()
    {
        WriteRepositoryAdminDir();
        Directory.CreateDirectory(Path.Combine(_root, ".git", "worktrees", "other-branch"));
        WriteProject("tests/App.Tests/App.Tests.csproj", XunitProject);
        WriteProject(".worktrees/other-branch/tests/App.Tests/App.Tests.csproj", XunitProject);
        File.WriteAllText(
            Path.Combine(_root, ".worktrees", "other-branch", ".git"),
            "gitdir: " + Path.Combine(_root, ".git", "worktrees", "other-branch"));

        ContinuousTestProject project = Assert.Single(
            ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.DoesNotContain(".worktrees", project.ProjectPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>modules</c> segment on its own proves nothing either: a checkout of some OTHER repository,
    /// dropped inside this tree, carries the same shape under an admin directory this root does not own.
    /// </summary>
    [Fact]
    public void Discover_stops_at_a_checkout_whose_git_directory_belongs_to_another_repository()
    {
        WriteRepositoryAdminDir();
        WriteProject("tests/App.Tests/App.Tests.csproj", XunitProject);
        WriteProject("vendored/other/tests/Other.Tests/Other.Tests.csproj", XunitProject);
        string foreignGitDir = Path.Combine(
            Path.GetTempPath(), "miller-ct-other-repo", ".git", "modules", "shared");
        File.WriteAllText(
            Path.Combine(_root, "vendored", "other", ".git"),
            "gitdir: " + foreignGitDir);

        ContinuousTestProject project = Assert.Single(
            ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.DoesNotContain("vendored", project.ProjectPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// `git init` writes <c>.git</c> as a DIRECTORY, which is what makes this root the owner of the
    /// <c>.git/modules/&lt;name&gt;</c> directories its submodules point at.
    /// </summary>
    private void WriteRepositoryAdminDir() =>
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

    private const string XunitProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="xunit.v3" Version="1.0.0" />
          </ItemGroup>
        </Project>
        """;

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
