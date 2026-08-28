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
    public void Materialize_puts_build_output_inside_the_workspace_miller_sidecar()
    {
        string project = Path.Combine(_root, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project />");
        var items = ContinuousTestProjectInventory.MaterializeProjectWorkItems(
            [new ContinuousTestProject("proj:1", "ws:1", project, Framework: "xunit")],
            _root);

        ContinuousTestProjectWorkItem item = Assert.Single(items);
        Assert.Equal(Path.GetFullPath(project), item.Workspace.ProjectPath);
        Assert.True(IsInside(_root, item.Workspace.BuildOutputRoot));
        Assert.Equal(
            Path.Combine(_root, ".miller"),
            Path.GetDirectoryName(item.Workspace.BuildOutputRoot));
        Assert.StartsWith(
            "ct-",
            Path.GetFileName(item.Workspace.BuildOutputRoot),
            StringComparison.Ordinal);
        Assert.Null(item.BuildRootFallbackReason);
    }

    [Fact]
    public void Build_output_keeps_one_fixed_width_project_segment()
    {
        ContinuousTestProjectWorkItem item = MaterializeDeeplyNestedPytestProject(_root);

        string name = Path.GetFileName(item.Workspace.BuildOutputRoot);
        Assert.StartsWith("ct-", name, StringComparison.Ordinal);
        string segment = name["ct-".Length..];
        Assert.Equal(ContinuousTestProjectInventory.SegmentHashLength, segment.Length);
        Assert.Matches("^[0-9a-f]+$", segment);
        Assert.Equal(
            Path.Combine(_root, ".miller"),
            Path.GetDirectoryName(item.Workspace.BuildOutputRoot));
    }

    [Fact]
    public void The_deepest_assembly_directory_sits_exactly_five_levels_below_the_workspace_root()
    {
        ContinuousTestProjectWorkItem item = MaterializeDeeplyNestedPytestProject(_root);

        string assemblyDirectory = Path.Combine(
            item.Workspace.BuildOutputRoot, "g0123456789ab", "out", "App.Tests");
        string[] levels = Path.GetRelativePath(_root, assemblyDirectory)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Equal(5, levels.Length);
        Assert.Equal(".miller", levels[0]);
    }

    [Fact]
    public void Composed_provider_artifact_paths_stay_inside_the_windows_path_budget()
    {
        string root = WorkspaceRootOfLength(ContinuousTestProjectInventory.WorkspaceRootLengthBudget);
        ContinuousTestProjectWorkItem item = MaterializeDeeplyNestedPytestProject(root);

        Assert.True(IsInside(root, item.Workspace.BuildOutputRoot));
        Assert.Null(item.BuildRootFallbackReason);
        int longestComposed = 0;
        foreach (string artifactName in LongestProviderArtifactNames)
        {
            string composed = Path.Combine(
                item.Workspace.BuildOutputRoot,
                "g0123456789ab",
                "TestResults",
                artifactName);
            longestComposed = Math.Max(longestComposed, composed.Length);
            Assert.True(
                composed.Length <= ContinuousTestProjectInventory.WindowsPathBudget,
                $"{composed.Length} characters exceeds the {ContinuousTestProjectInventory.WindowsPathBudget} budget: {composed}");
        }

        Assert.Equal(ContinuousTestProjectInventory.WindowsPathBudget, longestComposed);
    }

    [Fact]
    public void An_over_budget_workspace_root_falls_back_to_the_machine_temp_build_root()
    {
        string root = WorkspaceRootOfLength(ContinuousTestProjectInventory.WorkspaceRootLengthBudget + 1);
        ContinuousTestProjectWorkItem item = MaterializeDeeplyNestedPytestProject(root);

        Assert.False(IsInside(root, item.Workspace.BuildOutputRoot));
        Assert.NotNull(item.BuildRootFallbackReason);
        string[] segments = Path.GetRelativePath(CtTempPaths.BuildRoot, item.Workspace.BuildOutputRoot)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Equal(2, segments.Length);
        Assert.Equal(RealWorkspaceId[..ContinuousTestProjectInventory.SegmentHashLength], segments[0]);
        Assert.Equal(ContinuousTestProjectInventory.SegmentHashLength, segments[1].Length);
        foreach (string artifactName in LongestProviderArtifactNames)
        {
            string tail = Path.GetRelativePath(CtTempPaths.Root, Path.Combine(
                item.Workspace.BuildOutputRoot,
                "g0123456789ab",
                "TestResults",
                artifactName));
            Assert.True(
                tail.Length <= LegacyBuildRootTailBudget,
                $"{tail.Length} characters below the CT temp root exceeds the {LegacyBuildRootTailBudget} budget: {tail}");
        }
    }

    private const int LegacyBuildRootTailBudget = 160;

    private const string RealWorkspaceId =
        "9f2b7c1d4e6a8035c1d9e7f3a5b40628d9c3e1f7a24b60d8c5e39f1a7b04c26d";

    private const string RunHash =
        "3c81f0a5b6d2e47390af51c8b6d0e2f4a7c93b15d8e604f2a1c7b3d90e58f24a";

    private static readonly string[] LongestProviderArtifactNames =
    [
        $"run-{RunHash}.xml",
        $"run-{RunHash}.part000.junit.xml",
        $"run-{RunHash}.trx",
        $"run-{RunHash}.json",
        $"run-{RunHash}.cargo.log",
    ];

    private string WorkspaceRootOfLength(int length)
    {
        int padLength = length - _root.Length - 1;
        Assert.True(padLength >= 1, $"temp root {_root} is already longer than {length} characters");
        string root = Path.Combine(_root, new string('a', padLength));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ContinuousTestProjectWorkItem MaterializeDeeplyNestedPytestProject(string root)
    {
        string project = Path.Combine(
            root, "tests", "integration", "python", "services", "billing", "pyproject.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "[tool.pytest.ini_options]");

        return Assert.Single(ContinuousTestProjectInventory.MaterializeProjectWorkItems(
            [
                new ContinuousTestProject(
                    ContinuousTestProjectInventory.ProjectId(RealWorkspaceId, root, project),
                    RealWorkspaceId,
                    project,
                    Framework: "pytest"),
            ],
            root));
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

    [Theory]
    [InlineData("<PackageReference Include=\"MSTest.TestAdapter\" Version=\"4.0.0\" />", "mstest")]
    [InlineData("<PackageReference Include=\"MSTest.TestFramework\" Version=\"4.0.0\" />", "mstest")]
    [InlineData("<PackageReference Include=\"NUnit.Framework\" Version=\"4.0.0\" />", "nunit")]
    [InlineData("<PackageReference Include=\"xunit.v3\" Version=\"3.0.0\" />", "xunit")]
    [InlineData("<PackageReference Include=\"xunit\" Version=\"2.9.2\" />", "xunit-v2")]
    [InlineData("<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.0\" />", "dotnet")]
    [InlineData("<PackageReference Include=\"Microsoft.Testing.Platform\" Version=\"2.0.0\" />", "dotnet")]
    public void Discover_classifies_vb_projects_from_framework_specific_package_ids(
        string packageReference,
        string expectedFramework)
    {
        WriteProject("tests/App.Tests/App.Tests.vbproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                {packageReference}
              </ItemGroup>
            </Project>
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal(expectedFramework, project.Framework);
    }

    [Fact]
    public void Discover_does_not_treat_a_shared_xunit_runner_package_as_a_framework()
    {
        WriteProject("tests/App.Tests/App.Tests.vbproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.runner.visualstudio" Version="3.0.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    [Fact]
    public void Discover_records_static_dotnet_runner_evidence_without_claiming_an_effective_backend()
    {
        WriteProject("global.json", """
            {
              "test": {
                "runner": "Microsoft.Testing.Platform"
              }
            }
            """);
        WriteProject("tests/App.Tests/App.Tests.vbproj", """
            <Project Sdk="MSTest.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <UseVSTest>true</UseVSTest>
                <EnableMSTestRunner>false</EnableMSTestRunner>
              </PropertyGroup>
            </Project>
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal("mstest", project.Framework);
        Assert.Equal("Microsoft.Testing.Platform", project.Metadata[DotnetTestBackend.MetadataGlobalJsonRunner]);
        Assert.Equal("MSTest.Sdk", project.Metadata[DotnetTestBackend.MetadataProjectSdk]);
        Assert.Equal("true", project.Metadata[DotnetTestBackend.MetadataStaticPropertyPrefix + "UseVSTest"]);
        Assert.Equal("false", project.Metadata[DotnetTestBackend.MetadataStaticPropertyPrefix + "EnableMSTestRunner"]);
        Assert.Equal("static", project.Metadata[DotnetTestBackend.MetadataEvidenceState]);
    }

    [Fact]
    public void Static_dotnet_backend_evidence_uses_the_nearest_global_json()
    {
        WriteProject("global.json", """
            {
              "test": {
                "runner": "VSTest"
              }
            }
            """);
        WriteProject("tests/global.json", """
            {
              "test": {
                "runner": "Microsoft.Testing.Platform"
              }
            }
            """);
        WriteProject("tests/App.Tests/App.Tests.vbproj", """
            <Project Sdk="MSTest.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        DotnetTestBackendEvidence evidence = DotnetTestBackend.ReadStatic(
            Path.Combine(_root, "tests", "App.Tests", "App.Tests.vbproj"));

        Assert.Equal("Microsoft.Testing.Platform", evidence.GlobalJsonTestRunner);
        Assert.True(evidence.IsComplete);
    }

    [Fact]
    public void Static_dotnet_backend_evidence_fails_closed_on_malformed_global_json()
    {
        WriteProject("global.json", "{ \"test\": { \"runner\": ");
        WriteProject("tests/App.Tests/App.Tests.vbproj", """
            <Project Sdk="MSTest.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        DotnetTestBackendEvidence evidence = DotnetTestBackend.ReadStatic(
            Path.Combine(_root, "tests", "App.Tests", "App.Tests.vbproj"));

        Assert.False(evidence.IsComplete);
        Assert.Contains("global.json", evidence.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_classifies_an_xunit_v2_project_as_the_generation_continuous_testing_cannot_run()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
              </ItemGroup>
            </Project>
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal(ContinuousTestFrameworkSupport.XunitV2, project.Framework);
        Assert.False(ContinuousTestFrameworkSupport.IsSupported(project.Framework));
        Assert.Equal(ContinuousTestFrameworkSupport.XunitV2Reason,
            ContinuousTestFrameworkSupport.ReasonFor(project.Framework));
    }

    [Fact]
    public void Discover_classifies_an_xunit_v3_project_as_the_generation_continuous_testing_runs()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="1.0.0" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.0.0" />
              </ItemGroup>
            </Project>
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal("xunit", project.Framework);
        Assert.True(ContinuousTestFrameworkSupport.IsSupported(project.Framework));
    }

    [Theory]
    [InlineData("xunit.v3.core")]
    [InlineData("xunit.v3.assert")]
    [InlineData("xunit.v3.extensibility.core")]
    public void Discover_reads_every_v3_package_as_the_v3_generation(string package)
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="{package}" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal("xunit", Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1")).Framework);
    }

    [Theory]
    [InlineData("xunit.core")]
    [InlineData("xunit.assert")]
    [InlineData("xunit.abstractions")]
    [InlineData("xunit.extensibility.execution")]
    public void Discover_reads_every_v2_only_package_as_the_v2_generation(string package)
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="{package}" Version="2.9.2" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal(
            ContinuousTestFrameworkSupport.XunitV2,
            Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1")).Framework);
    }

    /// <summary>
    /// A project migrating to v3 may still carry a v2 package for a compatibility shim. It builds the v3
    /// self-executing assembly, so v3 wins outright.
    /// </summary>
    [Fact]
    public void Discover_reads_a_project_carrying_both_generations_as_v3()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit.abstractions" Version="2.0.3" />
                <PackageReference Include="xunit.v3" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal("xunit", Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1")).Framework);
    }

    [Fact]
    public void Discover_does_not_classify_a_project_whose_only_xunit_packages_are_shared_by_both_generations()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
                <PackageReference Include="xunit.analyzers" Version="1.16.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    /// <summary>
    /// Under central package management the csproj names the id and the version lives in
    /// <c>Directory.Packages.props</c>. The id alone has to decide.
    /// </summary>
    [Fact]
    public void Discover_classifies_a_versionless_package_reference()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal(
            ContinuousTestFrameworkSupport.XunitV2,
            Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1")).Framework);
    }

    [Fact]
    public void Discover_classifies_a_mixed_repository_project_by_project()
    {
        WriteProject("tests/Old.Tests/Old.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.2" />
              </ItemGroup>
            </Project>
            """);
        WriteProject("tests/New.Tests/New.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        Dictionary<string, string?> frameworks = ContinuousTestProjectInventory.Discover(_root, "ws:1")
            .ToDictionary(project => Path.GetFileName(project.ProjectPath), project => project.Framework);

        Assert.Equal(ContinuousTestFrameworkSupport.XunitV2, frameworks["Old.Tests.csproj"]);
        Assert.Equal("xunit", frameworks["New.Tests.csproj"]);
    }

    [Fact]
    public void Identify_classifies_an_xunit_v2_project_named_by_a_person()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.2" />
              </ItemGroup>
            </Project>
            """);
        string project = Path.Combine(_root, "tests", "App.Tests", "App.Tests.csproj");

        ContinuousTestProject? identified =
            ContinuousTestProjectInventory.Identify(_root, "ws:1", project);

        Assert.NotNull(identified);
        Assert.Equal(ContinuousTestFrameworkSupport.XunitV2, identified.Framework);
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

    /// <summary>
    /// The literal manifest of C:\source\razorback. Its only test script runs node's own runner, and
    /// before that runner was recognized the whole repository enabled zero continuous-test projects.
    /// </summary>
    [Fact]
    public void Discover_identifies_a_node_test_runner_script_as_node_test()
    {
        WriteProject("package.json", """
            {
              "name": "razorback",
              "version": "0.34.0",
              "type": "module",
              "scripts": {
                "test": "node --test tests/*.test.mjs"
              },
              "main": "./index.js"
            }
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal("node-test", project.Framework);
    }

    /// <summary>
    /// The literal scripts and devDependencies of C:\source\classnames. Two things here must not confuse
    /// the rule: a "bench" script that runs plain node, and a "node-resolve" dependency name.
    /// </summary>
    [Fact]
    public void Discover_identifies_the_classnames_manifest_as_node_test()
    {
        WriteProject("package.json", """
            {
              "name": "classnames",
              "version": "2.5.1",
              "type": "module",
              "scripts": {
                "test": "node --test ./tests/*.js",
                "bench": "node ./benchmarks/run.js",
                "bench-browser": "rollup --plugin commonjs,json,node-resolve ./benchmarks/runInBrowser.js --file ./benchmarks/runInBrowser.bundle.js && http-server -c-1 ./benchmarks",
                "check-types": "tsd"
              },
              "devDependencies": {
                "@rollup/plugin-node-resolve": "^16.0.3",
                "http-server": "^14.1.1",
                "rollup": "^4.62.4"
              }
            }
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal("node-test", project.Framework);
    }

    /// <summary>
    /// The word "node" is not the signal. A package whose scripts only launch node programs has no test
    /// suite for continuous testing to run, and enabling it would report a verdict for a build script.
    /// </summary>
    [Fact]
    public void Discover_skips_a_package_whose_scripts_only_run_node_programs()
    {
        WriteProject("package.json", """
            {
              "name": "tool",
              "scripts": {
                "build": "node build.js",
                "start": "node ./server.js"
              }
            }
            """);

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    /// <summary>
    /// The flag is matched as a whole argument. Other runners spell options that begin with the same six
    /// characters, and none of them starts node's runner.
    /// </summary>
    [Fact]
    public void Discover_skips_a_package_whose_test_script_only_starts_with_the_test_flag_text()
    {
        WriteProject("package.json", """
            {
              "name": "tool",
              "scripts": {
                "test": "some-runner --testPathPattern=unit"
              }
            }
            """);

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    [Fact]
    public void Discover_collapses_nested_qml_build_files_and_keeps_the_evidence_root()
    {
        WriteProject("CMakeLists.txt", """
            cmake_minimum_required(VERSION 3.21)
            project(app LANGUAGES CXX)
            add_subdirectory(tests)
            """);
        WriteProject("tests/CMakeLists.txt", """
            find_package(Qt6 REQUIRED COMPONENTS QuickTest)
            enable_testing()
            qt_add_executable(app_tests runner.cpp)
            target_link_libraries(app_tests PRIVATE Qt6::QuickTest)
            add_test(NAME app_tests COMMAND app_tests)
            """);
        WriteProject("tests/runner.cpp", "QUICK_TEST_MAIN(app_tests)");
        WriteProject("tests/qml/tst_smoke.qml", """
            import QtQuickTest 1.3
            TestCase { name: "Smoke" }
            """);

        ContinuousTestProject project = Assert.Single(
            ContinuousTestProjectInventory.Discover(_root, "ws:1"));

        Assert.Equal("qt-quick-test", project.Framework);
        Assert.Equal(Path.Combine(_root, "CMakeLists.txt"), project.ProjectPath);
        Assert.Equal(_root, project.Metadata["configure_root"]);
        Assert.Equal(Path.Combine(_root, "tests", "qml"), project.Metadata["evidence_root"]);
    }

    [Fact]
    public void Discover_accepts_a_qmake_quick_testcase_project_and_marks_the_qmake_backend()
    {
        WriteProject("quicktest.pro", """
            TEMPLATE = app
            TARGET = tst_smoke
            CONFIG += qmltestcase
            SOURCES += runner.cpp
            """);
        WriteProject("runner.cpp", "#include <QtQuickTest>\nQUICK_TEST_MAIN(smoke)");
        WriteProject("tst_smoke.qml", "TestCase { name: \"Smoke\" }");

        ContinuousTestProject project = Assert.Single(
            ContinuousTestProjectInventory.Discover(_root, "ws:1"));

        Assert.Equal("qt-quick-test", project.Framework);
        Assert.Equal("qmake", project.Metadata["backend"]);
        Assert.Equal(Path.Combine(_root, "quicktest.pro"), project.ProjectPath);
        Assert.Equal(_root, project.Metadata["configure_root"]);
        Assert.Equal(_root, project.Metadata["evidence_root"]);
    }

    [Fact]
    public void Discover_accepts_qmake_qmltest_only_when_testcase_proves_the_check_target()
    {
        WriteProject("quicktest.pro", """
            TEMPLATE = app
            TARGET = tst_smoke
            QT += qmltest
            CONFIG += testcase
            SOURCES += runner.cpp
            """);
        WriteProject("runner.cpp", "#include <QtQuickTest>\nQUICK_TEST_MAIN(smoke)");
        WriteProject("tst_smoke.qml", "TestCase { name: \"Smoke\" }");

        ContinuousTestProject project = Assert.Single(
            ContinuousTestProjectInventory.Discover(_root, "ws:1"));

        Assert.Equal("qmake", project.Metadata["backend"]);
    }

    [Fact]
    public void Discover_rejects_qmake_quick_test_library_without_a_check_target()
    {
        WriteProject("quicktest.pro", """
            TEMPLATE = app
            TARGET = tst_smoke
            QT += qmltest
            SOURCES += runner.cpp
            """);
        WriteProject("runner.cpp", "#include <QtQuickTest>\nQUICK_TEST_MAIN(smoke)");
        WriteProject("tst_smoke.qml", "TestCase { name: \"Smoke\" }");

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    [Fact]
    public void Discover_rejects_native_qt_test_even_when_testcase_is_present()
    {
        WriteProject("quicktest.pro", """
            TEMPLATE = app
            TARGET = tst_native
            QT += testlib
            CONFIG += testcase
            SOURCES += runner.cpp
            """);
        WriteProject("runner.cpp", "#include <QtTest>\nQTEST_MAIN(tst_native)");
        WriteProject("tst_smoke.qml", "TestCase { name: \"Smoke\" }");

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    [Fact]
    public void Discover_reads_qmake_quick_test_evidence_from_a_literal_pri_include()
    {
        WriteProject("quicktest.pro", """
            TEMPLATE = app
            TARGET = tst_smoke
            include(test-settings.pri)
            SOURCES += runner.cpp
            """);
        WriteProject("test-settings.pri", "CONFIG += qmltestcase");
        WriteProject("runner.cpp", "#include <QtQuickTest>\nQUICK_TEST_MAIN(smoke)");
        WriteProject("tst_smoke.qml", "TestCase { name: \"Smoke\" }");

        ContinuousTestProject project = Assert.Single(
            ContinuousTestProjectInventory.Discover(_root, "ws:1"));

        Assert.Equal("qmake", project.Metadata["backend"]);
    }

    [Fact]
    public void Discover_does_not_follow_a_variable_qmake_include()
    {
        WriteProject("quicktest.pro", """
            TEMPLATE = app
            TARGET = tst_smoke
            SETTINGS = test-settings.pri
            include($$SETTINGS)
            SOURCES += runner.cpp
            """);
        WriteProject("test-settings.pri", "CONFIG += qmltestcase");
        WriteProject("runner.cpp", "#include <QtQuickTest>\nQUICK_TEST_MAIN(smoke)");
        WriteProject("tst_smoke.qml", "TestCase { name: \"Smoke\" }");

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    [Fact]
    public void Discover_does_not_borrow_qmake_evidence_from_an_independent_nested_project()
    {
        WriteProject("outer.pro", """
            TEMPLATE = app
            CONFIG += qmltestcase
            """);
        WriteProject("nested/quicktest.pro", """
            TEMPLATE = app
            TARGET = tst_nested
            CONFIG += qmltestcase
            SOURCES += runner.cpp
            """);
        WriteProject("nested/runner.cpp", "#include <QtQuickTest>\nQUICK_TEST_MAIN(nested)");
        WriteProject("nested/tst_nested.qml", "TestCase { name: \"Nested\" }");

        var projects = ContinuousTestProjectInventory.Discover(_root, "ws:1");

        ContinuousTestProject project = Assert.Single(projects);
        Assert.Equal(Path.Combine(_root, "nested", "quicktest.pro"), project.ProjectPath);
        Assert.Equal("qmake", project.Metadata["backend"]);
    }

    [Theory]
    [InlineData("QUICK_TEST_MAIN_WITH_SETUP(app_tests, Setup)")]
    [InlineData("QUICK_TEST_OPENGL_MAIN(app_tests)")]
    public void Discover_accepts_each_supported_quick_test_macro(string macro)
    {
        WriteProject("CMakeLists.txt", """
            cmake_minimum_required(VERSION 3.21)
            project(app LANGUAGES CXX)
            find_package(Qt6 REQUIRED COMPONENTS QuickTest)
            enable_testing()
            qt_add_executable(app_tests runner.cpp)
            target_link_libraries(app_tests PRIVATE Qt6::QuickTest)
            add_test(NAME app_tests COMMAND app_tests)
            """);
        WriteProject("runner.cpp", macro);
        WriteProject("tst_smoke.qml", "TestCase { name: \"Smoke\" }");

        ContinuousTestProject project = Assert.Single(
            ContinuousTestProjectInventory.Discover(_root, "ws:1"));

        Assert.Equal("qt-quick-test", project.Framework);
    }

    [Fact]
    public void Discover_rejects_a_quick_test_project_without_ctest_registration()
    {
        WriteProject("CMakeLists.txt", """
            cmake_minimum_required(VERSION 3.21)
            project(app LANGUAGES CXX)
            find_package(Qt6 REQUIRED COMPONENTS QuickTest)
            qt_add_executable(app_tests runner.cpp)
            target_link_libraries(app_tests PRIVATE Qt6::QuickTest)
            """);
        WriteProject("runner.cpp", "QUICK_TEST_MAIN(app_tests)");
        WriteProject("tst_smoke.qml", "TestCase { name: \"Smoke\" }");

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    [Fact]
    public void Discover_keeps_independent_nested_qml_cmake_projects_separate()
    {
        WriteQmlQuickTestProject("apps/parent");
        WriteQmlQuickTestProject("apps/parent/independent");

        var projects = ContinuousTestProjectInventory.Discover(_root, "ws:1");

        Assert.Equal(2, projects.Count);
        Assert.Equal(
            [
                Path.Combine(_root, "apps", "parent", "CMakeLists.txt"),
                Path.Combine(_root, "apps", "parent", "independent", "CMakeLists.txt"),
            ],
            projects.Select(project => project.ProjectPath).ToArray());
    }

    [Fact]
    public void Discover_collapses_a_nested_qml_project_when_the_parent_includes_it()
    {
        WriteQmlQuickTestProject("apps/parent");
        WriteQmlQuickTestProject("apps/parent/included");
        WriteProject("apps/parent/CMakeLists.txt", """
            cmake_minimum_required(VERSION 3.21)
            project(parent LANGUAGES CXX)
            find_package(Qt6 REQUIRED COMPONENTS QuickTest)
            enable_testing()
            qt_add_executable(parent_tests runner.cpp)
            target_link_libraries(parent_tests PRIVATE Qt6::QuickTest)
            add_test(NAME parent_tests COMMAND parent_tests)
            add_subdirectory(included)
            """);

        var projects = ContinuousTestProjectInventory.Discover(_root, "ws:1");

        ContinuousTestProject project = Assert.Single(projects);
        Assert.Equal(Path.Combine(_root, "apps", "parent", "CMakeLists.txt"), project.ProjectPath);
    }

    [Fact]
    public void Discover_does_not_borrow_qml_evidence_from_an_independent_nested_project()
    {
        WriteProject("CMakeLists.txt", """
            cmake_minimum_required(VERSION 3.21)
            project(outer LANGUAGES CXX)
            """);
        WriteQmlQuickTestProject("nested");

        var projects = ContinuousTestProjectInventory.Discover(_root, "ws:1");

        ContinuousTestProject project = Assert.Single(projects);
        Assert.Equal(Path.Combine(_root, "nested", "CMakeLists.txt"), project.ProjectPath);
    }

    [Fact]
    public void Discover_does_not_borrow_ctest_registration_from_an_independent_nested_project()
    {
        WriteProject("CMakeLists.txt", """
            cmake_minimum_required(VERSION 3.21)
            project(outer LANGUAGES CXX)
            find_package(Qt6 REQUIRED COMPONENTS QuickTest)
            qt_add_executable(outer_tests runner.cpp)
            target_link_libraries(outer_tests PRIVATE Qt6::QuickTest)
            """);
        WriteProject("runner.cpp", "QUICK_TEST_MAIN(outer_tests)");
        WriteProject("tst_outer.qml", "TestCase { name: \"Outer\" }");
        WriteQmlQuickTestProject("nested");

        var projects = ContinuousTestProjectInventory.Discover(_root, "ws:1");

        ContinuousTestProject project = Assert.Single(projects);
        Assert.Equal(Path.Combine(_root, "nested", "CMakeLists.txt"), project.ProjectPath);
    }

    [Fact]
    public void Identify_resolves_a_nested_qml_build_file_to_the_topmost_project()
    {
        WriteProject("CMakeLists.txt", """
            cmake_minimum_required(VERSION 3.21)
            project(app LANGUAGES CXX)
            add_subdirectory(tests)
            """);
        WriteProject("tests/CMakeLists.txt", """
            find_package(Qt6 REQUIRED COMPONENTS QuickTest)
            enable_testing()
            add_test(NAME app_tests COMMAND app_tests)
            """);
        WriteProject("tests/runner.cpp", "QUICK_TEST_MAIN(app_tests)");
        WriteProject("tests/tst_smoke.qml", "TestCase { name: \"Smoke\" }");

        ContinuousTestProject? project = ContinuousTestProjectInventory.Identify(
            _root,
            "ws:1",
            Path.Combine(_root, "tests", "CMakeLists.txt"));

        Assert.NotNull(project);
        Assert.Equal("qt-quick-test", project.Framework);
        Assert.Equal(Path.Combine(_root, "CMakeLists.txt"), project.ProjectPath);
    }

    [Fact]
    public void Discover_keeps_independent_qml_cmake_projects_separate()
    {
        WriteQmlQuickTestProject("apps/alpha");
        WriteQmlQuickTestProject("apps/beta");

        var projects = ContinuousTestProjectInventory.Discover(_root, "ws:1");

        Assert.Equal(2, projects.Count);
        Assert.All(projects, project => Assert.Equal("qt-quick-test", project.Framework));
        Assert.Equal(
            [
                Path.Combine(_root, "apps", "alpha", "CMakeLists.txt"),
                Path.Combine(_root, "apps", "beta", "CMakeLists.txt"),
            ],
            projects.Select(project => project.ProjectPath).ToArray());
    }

    [Fact]
    public void Discover_rejects_a_qml_project_without_quick_test_evidence()
    {
        WriteProject("CMakeLists.txt", """
            cmake_minimum_required(VERSION 3.21)
            project(app LANGUAGES CXX)
            """);
        WriteProject("qml/tst_smoke.qml", "TestCase { name: \"Smoke\" }");

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    [Fact]
    public void Discover_rejects_a_quick_test_project_without_qml_evidence()
    {
        WriteProject("CMakeLists.txt", """
            cmake_minimum_required(VERSION 3.21)
            project(app LANGUAGES CXX)
            find_package(Qt6 REQUIRED COMPONENTS QuickTest)
            qt_add_executable(app_tests runner.cpp)
            target_link_libraries(app_tests PRIVATE Qt6::QuickTest)
            """);
        WriteProject("runner.cpp", "QUICK_TEST_MAIN(app_tests)");

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    [Fact]
    public void Discover_ignores_qml_and_quick_test_words_in_comments_strings_and_unrelated_files()
    {
        WriteProject("CMakeLists.txt", """
            cmake_minimum_required(VERSION 3.21)
            project(app LANGUAGES CXX)
            # Qt6::QuickTest
            set(note "Qt6::QuickTest")
            """);
        WriteProject("runner.cpp", """
            // QUICK_TEST_MAIN(app_tests)
            const char* text = "QUICK_TEST_MAIN";
            """);
        WriteProject("qml/Main.qml", """
            // TestCase
            Item { property string text: "TestCase" }
            """);
        WriteProject("notes.txt", "Qt6::QuickTest QUICK_TEST_MAIN TestCase");

        Assert.Empty(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
    }

    /// <summary>
    /// vitest and jest are still decided first: a repository that installs vitest and also keeps a node
    /// script runs its suite through vitest.
    /// </summary>
    [Fact]
    public void Discover_prefers_vitest_over_a_node_test_script()
    {
        WriteProject("package.json", """
            {
              "name": "app",
              "scripts": {
                "test": "vitest run",
                "test:node": "node --test ./tests/*.js"
              },
              "devDependencies": { "vitest": "^3.2.4" }
            }
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal("vitest", project.Framework);
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
    /// A directory reparse point - a Windows junction, a symlink - re-enters a tree the walk has already
    /// covered, so ONE physical project is discovered under several logical paths. Each copy becomes a
    /// separately enabled project that builds and runs on every change; a link pointing at its own
    /// ancestor multiplies that per loop level. The indexing walk already skips reparse points
    /// (<c>BlazorNamespaceCatalog</c>), and this walk must too.
    ///
    /// The link here points at a SIBLING subtree rather than at an ancestor, because it proves the same
    /// guard without asking an unguarded walk to recurse until the platform path limit stops it.
    /// </summary>
    [Fact]
    public void Discover_does_not_follow_a_reparse_point_into_a_tree_it_already_walked()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", XunitProject);
        if (!TryCreateDirectoryLink(Path.Combine(_root, "mirror"), Path.Combine(_root, "tests")))
            Assert.Skip("This machine cannot create a directory reparse point.");

        ContinuousTestProject project = Assert.Single(
            ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.DoesNotContain("mirror", project.ProjectPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates a directory reparse point, or reports false when this machine forbids every shape of one.
    /// Windows refuses a directory SYMLINK to a caller without Developer Mode or elevation, but it allows
    /// a JUNCTION to any caller, and a junction carries the same <c>ReparsePoint</c> attribute the guard
    /// reads. The attribute is verified rather than assumed, so a link that lands as a plain directory
    /// skips the test instead of passing it vacuously.
    /// </summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows() || !TryCreateJunction(link, target))
                return false;
        }

        return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null)
            return false;

        process.WaitForExit(TimeSpan.FromSeconds(30));
        return process.HasExited && process.ExitCode == 0;
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
    /// One directory is ONE pytest project. The dogfood repository more-itertools carries
    /// <c>pyproject.toml</c>, <c>setup.cfg</c> and <c>tox.ini</c> side by side, and each one enabled its
    /// own project - so the same suite ran three times per change.
    /// </summary>
    [Fact]
    public void Discover_enables_one_pytest_project_for_a_directory_with_several_config_files()
    {
        WriteProject("pyproject.toml", "[tool.pytest.ini_options]");
        WriteProject("setup.cfg", "[tool:pytest]");
        WriteProject("tox.ini", "[pytest]");
        WriteProject("setup.py", "from setuptools import setup");

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal("pytest", project.Framework);
        Assert.Equal("pyproject.toml", Path.GetFileName(project.ProjectPath));
    }

    /// <summary>
    /// The winner follows pytest's own rootdir precedence, so the file Miller names is the file pytest
    /// reads: <c>pytest.ini</c> beats every other config file, even an empty one.
    /// </summary>
    [Fact]
    public void Discover_prefers_the_config_file_pytest_itself_reads_first()
    {
        WriteProject("pytest.ini", "[pytest]");
        WriteProject("pyproject.toml", "[tool.pytest.ini_options]");
        WriteProject("tox.ini", "[pytest]");

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal("pytest.ini", Path.GetFileName(project.ProjectPath));
    }

    /// <summary>
    /// The dedupe is per DIRECTORY, not per repository: two independent python packages stay two
    /// projects.
    /// </summary>
    [Fact]
    public void Discover_keeps_one_pytest_project_for_each_directory()
    {
        WriteProject("libs/alpha/pyproject.toml", "[tool.pytest.ini_options]");
        WriteProject("libs/alpha/setup.cfg", "[tool:pytest]");
        WriteProject("libs/beta/pyproject.toml", "[tool.pytest.ini_options]");

        var projects = ContinuousTestProjectInventory.Discover(_root, "ws:1");

        Assert.Equal(2, projects.Count);
        Assert.All(projects, project => Assert.Equal("pyproject.toml", Path.GetFileName(project.ProjectPath)));
    }

    /// <summary>
    /// A cargo workspace run already builds and tests every member crate, so a member's own
    /// <c>Cargo.toml</c> must not enable a second project. The dogfood repository julie-extractors
    /// listed its four member crates beside the workspace root and ran the whole suite twice.
    /// </summary>
    [Fact]
    public void Discover_skips_the_member_crates_of_a_cargo_workspace()
    {
        WriteProject("Cargo.toml", """
            [workspace]
            members = [
                "crates/julie-extract-cli",
                "crates/julie-extractors",
                "xtask",
            ]
            resolver = "2"
            """);
        WriteProject("crates/julie-extract-cli/Cargo.toml", "[package]\nname = \"cli\"\n");
        WriteProject("crates/julie-extractors/Cargo.toml", "[package]\nname = \"extractors\"\n");
        WriteProject("xtask/Cargo.toml", "[package]\nname = \"xtask\"\n");

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal("cargo", project.Framework);
        Assert.Equal(Path.Combine(_root, "Cargo.toml"), project.ProjectPath);
    }

    /// <summary>A member list is a glob list; <c>crates/*</c> names every crate below <c>crates</c>.</summary>
    [Fact]
    public void Discover_skips_member_crates_named_by_a_glob()
    {
        WriteProject("Cargo.toml", """
            [workspace]
            members = ["crates/*"]
            """);
        WriteProject("crates/alpha/Cargo.toml", "[package]\nname = \"alpha\"\n");
        WriteProject("crates/beta/Cargo.toml", "[package]\nname = \"beta\"\n");

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal(Path.Combine(_root, "Cargo.toml"), project.ProjectPath);
    }

    /// <summary>
    /// An excluded crate is NOT part of the workspace run, so dropping it would stop testing it. The
    /// exclude list therefore wins over a member glob that also matches it.
    /// </summary>
    [Fact]
    public void Discover_keeps_a_crate_the_workspace_excludes()
    {
        WriteProject("Cargo.toml", """
            [workspace]
            members = ["crates/*"]
            exclude = ["crates/standalone"]
            """);
        WriteProject("crates/alpha/Cargo.toml", "[package]\nname = \"alpha\"\n");
        WriteProject("crates/standalone/Cargo.toml", "[package]\nname = \"standalone\"\n");

        var projects = ContinuousTestProjectInventory.Discover(_root, "ws:1");

        Assert.Equal(2, projects.Count);
        Assert.Contains(
            projects,
            project => project.ProjectPath == Path.Combine(_root, "crates", "standalone", "Cargo.toml"));
    }

    /// <summary>
    /// When the workspace table names no members, cargo infers them from path dependencies - which this
    /// parser does not read. Keeping every candidate runs a suite twice; dropping one stops testing it,
    /// so the doubt resolves toward keeping.
    /// </summary>
    [Fact]
    public void Discover_keeps_every_crate_when_the_workspace_names_no_members()
    {
        WriteProject("Cargo.toml", """
            [workspace]
            resolver = "2"
            """);
        WriteProject("crates/alpha/Cargo.toml", "[package]\nname = \"alpha\"\n");

        Assert.Equal(2, ContinuousTestProjectInventory.Discover(_root, "ws:1").Count);
    }

    /// <summary>
    /// A crate that is not a member of any workspace keeps its own project, however deep it sits.
    /// </summary>
    [Fact]
    public void Discover_keeps_a_crate_that_no_workspace_lists()
    {
        WriteProject("Cargo.toml", """
            [workspace]
            members = ["crates/alpha"]
            """);
        WriteProject("crates/alpha/Cargo.toml", "[package]\nname = \"alpha\"\n");
        WriteProject("tools/standalone/Cargo.toml", "[package]\nname = \"standalone\"\n");

        var projects = ContinuousTestProjectInventory.Discover(_root, "ws:1");

        Assert.Equal(2, projects.Count);
        Assert.Contains(
            projects,
            project => project.ProjectPath == Path.Combine(_root, "tools", "standalone", "Cargo.toml"));
    }

    /// <summary>
    /// A manifest under a fixtures directory is test DATA. The dogfood repository julie-extractors
    /// enabled <c>fixtures/extraction/toml/cargo_deps/Cargo.toml</c> as a project and CT tried to build
    /// a parser fixture.
    /// </summary>
    [Theory]
    [InlineData("fixtures")]
    [InlineData("__fixtures__")]
    [InlineData("testdata")]
    public void Discover_skips_a_manifest_that_is_test_data(string fixtureSegment)
    {
        WriteProject("tests/App.Tests/App.Tests.csproj", XunitProject);
        WriteProject($"{fixtureSegment}/extraction/toml/cargo_deps/Cargo.toml", "[package]\nname = \"x\"\n");
        WriteProject($"{fixtureSegment}/python/pyproject.toml", "[tool.pytest.ini_options]");
        WriteProject($"src/{fixtureSegment}/nested/package.json", """
            { "name": "f", "devDependencies": { "vitest": "^3.2.4" } }
            """);

        ContinuousTestProject project = Assert.Single(ContinuousTestProjectInventory.Discover(_root, "ws:1"));
        Assert.Equal("App.Tests.csproj", Path.GetFileName(project.ProjectPath));
    }

    /// <summary>
    /// The fixture rule guards the WALK, not a path the user typed. <c>tests enable</c> names one file,
    /// and a person who names it means it.
    /// </summary>
    [Fact]
    public void Identify_still_accepts_a_fixture_path_the_user_names()
    {
        WriteProject("fixtures/python/pyproject.toml", "[tool.pytest.ini_options]");

        ContinuousTestProject? project = ContinuousTestProjectInventory.Identify(
            _root,
            "ws:1",
            Path.Combine(_root, "fixtures", "python", "pyproject.toml"));

        Assert.NotNull(project);
        Assert.Equal("pytest", project.Framework);
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

    private void WriteQmlQuickTestProject(string relativeRoot)
    {
        WriteProject($"{relativeRoot}/CMakeLists.txt", """
            cmake_minimum_required(VERSION 3.21)
            project(app LANGUAGES CXX)
            find_package(Qt6 REQUIRED COMPONENTS QuickTest)
            enable_testing()
            qt_add_executable(app_tests runner.cpp)
            target_link_libraries(app_tests PRIVATE Qt6::QuickTest)
            add_test(NAME app_tests COMMAND app_tests)
            """);
        WriteProject($"{relativeRoot}/runner.cpp", "QUICK_TEST_MAIN(app_tests)");
        WriteProject($"{relativeRoot}/tst_smoke.qml", "TestCase { name: \"Smoke\" }");
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
