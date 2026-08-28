using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Go;

public sealed class GoTestToolingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-go-tooling-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Environment_is_miller_owned_and_clears_selection_flags()
    {
        string goWork = Path.Combine(_root, "go.work");
        File.WriteAllText(goWork, "go 1.24\n");
        var workspace = Workspace(goWork);
        CtGenerationPaths paths = CtGenerationPaths.For(workspace, "g0123456789ab");

        IReadOnlyDictionary<string, string?> environment = GoTestTooling.Environment(workspace, paths,
            "-tags=integration -run=TestOld -count 7 -mod=readonly");

        Assert.Equal(goWork, environment["GOWORK"]);
        Assert.Equal(CtGenerationPaths.CacheDirectory(workspace, "go"), environment["GOCACHE"]);
        Assert.Equal(paths.TempDirectory, environment["GOTMPDIR"]);
        Assert.Equal("-tags=integration -mod=readonly", environment["GOFLAGS"]);
    }

    [Fact]
    public void Run_command_anchors_and_escapes_top_level_names_and_disables_cache()
    {
        var workspace = Workspace(goWork: null);
        CtGenerationPaths paths = CtGenerationPaths.For(workspace, "g0123456789ab");

        TestProcessCommand command = GoTestTooling.BuildRunCommand(
            workspace,
            paths,
            "example.com/math",
            ["TestAdd", "Test_Name"]);

        Assert.Equal("go", command.FileName);
        Assert.Contains("-json", command.Arguments);
        Assert.Contains("-count=1", command.Arguments);
        Assert.Contains("^(?:TestAdd|Test_Name)$", command.Arguments);
        Assert.Equal("off", command.Environment["GOWORK"]);
    }

    [Theory]
    [InlineData("go1.24.0", true)]
    [InlineData("go1.25.2", true)]
    [InlineData("go1.23.9", false)]
    public void TryParseVersion_requires_go_1_24(string output, bool supported)
    {
        Assert.True(GoTestTooling.TryParseVersion(output, out Version? version));
        Assert.Equal(supported, GoTestTooling.IsSupportedVersion(version!));
    }

    [Fact]
    public void ParsePackageList_accepts_pretty_printed_multiple_objects_and_excludes_packages_without_tests()
    {
        IReadOnlyList<GoTestTooling.GoPackageInfo> packages = GoTestTooling.ParsePackageList("""
            {
              "Dir": "/repo",
              "ImportPath": "example.com/math",
              "Module": {"Path": "example.com/math"},
              "TestGoFiles": ["math_test.go"]
            }
            {
              "Dir": "/repo/internal",
              "ImportPath": "example.com/math/internal",
              "Module": {"Path": "example.com/math"},
              "GoFiles": ["internal.go"]
            }
            """, "example.com/fallback");

        var packageInfo = Assert.Single(packages);
        Assert.Equal("example.com/math", packageInfo.ImportPath);
        Assert.Equal("example.com/math", packageInfo.ModulePath);
        Assert.Equal(["math_test.go"], packageInfo.TestFiles);
    }

    [Fact]
    public void ParsePackageList_refuses_malformed_streams()
    {
        Assert.Throws<ContinuousTestProviderException>(() =>
            GoTestTooling.ParsePackageList("{\"ImportPath\":\"example.com/math\"", "example.com/math"));
    }

    private ContinuousTestWorkspace Workspace(string? goWork) =>
        new(
            "ws:go",
            _root,
            Path.Combine(_root, "go.mod"),
            Path.Combine(_root, ".miller", "ct-go"),
            Framework: "go",
            Metadata: goWork is null
                ? null
                : new Dictionary<string, object?> { ["go_work"] = goWork });
}
