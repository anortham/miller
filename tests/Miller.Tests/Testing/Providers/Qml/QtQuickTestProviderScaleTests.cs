using System.Security.Cryptography;
using Miller.Testing;
using Miller.Testing.Providers.Qml;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Testing.Providers.Qml;

[Trait("Category", "Scale")]
[Collection(QmlProviderEnvironmentCollection.Name)]
public sealed class QtQuickTestProviderScaleTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-qml-scale-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Exact_test_name_regex_runs_selected_plain_ctest_targets()
    {
        string cmake = CtProviderTestSupport.RequireCMake();
        string ctest = CtProviderTestSupport.RequireCTest();
        string fixture = Path.Combine(_dir, "ctest regex fixture");
        string build = Path.Combine(_dir, "ctest regex build");
        string report = Path.Combine(_dir, "ctest regex results.xml");
        Directory.CreateDirectory(fixture);
        File.WriteAllText(
            Path.Combine(fixture, "CMakeLists.txt"),
            """
            cmake_minimum_required(VERSION 3.21)
            project(CtestRegex NONE)
            enable_testing()
            add_test(NAME "a" COMMAND "${CMAKE_COMMAND}" -E true)
            add_test(NAME ":a" COMMAND "${CMAKE_COMMAND}" -E true)
            add_test(NAME "xa" COMMAND "${CMAKE_COMMAND}" -E true)
            """);
        var runner = new TestProcessRunner();
        var cancellationToken = TestContext.Current.CancellationToken;

        TestProcessResult configure = await runner.RunAsync(
            new TestProcessCommand(cmake, ["-S", fixture, "-B", build], fixture),
            cancellationToken);
        Assert.Equal(0, configure.ExitCode);

        string regex = QtQuickTestTooling.ExactTestNameRegex(["a", ":a"]);
        TestProcessResult run = await runner.RunAsync(
            new TestProcessCommand(
                ctest,
                ["--test-dir", build, "-R", regex, "--no-tests=error", "--output-junit", report],
                build),
            cancellationToken);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            [":a", "a"],
            System.Xml.Linq.XDocument.Load(report)
                .Descendants("testcase")
                .Select(element => (string?)element.Attribute("name"))
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Qt_quick_test_fixture_discovers_selects_runs_whole_suite_and_keeps_source_unchanged()
    {
        string cmake = CtProviderTestSupport.RequireCMake();
        string ctest = CtProviderTestSupport.RequireCTest();
        string qtPrefix = CtProviderTestSupport.RequireQtQuickTestCMakePrefix();
        string? previousPrefix = Environment.GetEnvironmentVariable("CMAKE_PREFIX_PATH");
        try
        {
            Environment.SetEnvironmentVariable(
                "CMAKE_PREFIX_PATH",
                string.IsNullOrWhiteSpace(previousPrefix)
                    ? qtPrefix
                    : string.Join(Path.PathSeparator, qtPrefix, previousPrefix));

            string repositoryFixture = Path.Combine(
                ScaleTestSupport.RepoRoot(),
                "tests",
                "Miller.Tests",
                "Fixtures",
                "QtQuickTestScale");
            var sourceBefore = Snapshot(repositoryFixture);
            string fixture = CopyFixture(repositoryFixture, Path.Combine(_dir, "qt qml fixture"));
            var copiedBefore = Snapshot(fixture);
            string buildRoot = Path.Combine(_dir, "state with spaces", "ct-build");
            var workspace = new ContinuousTestWorkspace(
                WorkspaceId: "ws:qml-scale",
                WorkspaceRoot: fixture,
                ProjectPath: Path.Combine(fixture, "CMakeLists.txt"),
                BuildOutputRoot: buildRoot,
                Framework: "qt-quick-test",
                Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["configure_root"] = fixture,
                    ["evidence_root"] = fixture,
                    ["project_id"] = "qml-scale-fixture",
                    ["configuration"] = "Release",
                });
            var provider = new QtQuickTestProvider(new TestProcessRunner(), cmake, ctest);
            var cancellationToken = TestContext.Current.CancellationToken;

            var discovered = await provider.DiscoverAsync(workspace, cancellationToken);
            Assert.Equal(["qml/basic", "qml/second"], discovered.Select(test => test.DisplayName));
            Assert.All(discovered, test => Assert.Equal("qt-quick-test", test.Framework));
            Assert.All(discovered, test => Assert.Equal("qml", test.Metadata["language"]));

            var selected = Assert.Single(discovered, test => test.DisplayName == "qml/basic");
            var selectedRun = await provider.RunAsync(
                new ContinuousTestProviderRunRequest(
                    Workspace: workspace,
                    SelectedRevision: "rev-qml-scale",
                    IndexIdentity: "index-qml-scale",
                    RunId: "run:qml-selected",
                    TestCaseIds: [selected.Id]),
                cancellationToken);

            var selectedResult = Assert.Single(selectedRun.CaseResults);
            Assert.Equal("passed", selectedRun.Status);
            Assert.Equal(selected.Id, selectedResult.TestCaseId);
            Assert.Equal("passed", selectedResult.Status);
            Assert.Equal("qml/basic", selectedResult.Metadata["ctest_name"]);
            Assert.NotNull(selectedRun.ResultArtifactPath);
            Assert.True(File.Exists(selectedRun.ResultArtifactPath!));

            var wholeRun = await provider.RunAsync(
                new ContinuousTestProviderRunRequest(
                    Workspace: workspace,
                    SelectedRevision: "rev-qml-scale",
                    IndexIdentity: "index-qml-scale",
                    RunId: "run:qml-whole",
                    TestCaseIds: discovered.Select(test => test.Id).ToArray(),
                    WholeSuite: true),
                cancellationToken);

            Assert.Equal("passed", wholeRun.Status);
            Assert.Equal(
                discovered.Select(test => test.Id).Order(StringComparer.Ordinal),
                wholeRun.CaseResults.Select(result => result.TestCaseId).Order(StringComparer.Ordinal));
            Assert.All(wholeRun.CaseResults, result => Assert.Equal("passed", result.Status));
            Assert.NotEqual(selectedRun.GenerationId, wholeRun.GenerationId);
            Assert.NotNull(wholeRun.ResultArtifactPath);
            Assert.True(File.Exists(wholeRun.ResultArtifactPath!));
            Assert.StartsWith(Path.Combine(_dir, "state with spaces"), wholeRun.ResultArtifactPath!, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(fixture, "*", SearchOption.AllDirectories),
                path => string.Equals(Path.GetFileName(path), "CMakeCache.txt", StringComparison.Ordinal));
            AssertSnapshotUnchanged(sourceBefore, repositoryFixture);
            AssertSnapshotUnchanged(copiedBefore, fixture);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CMAKE_PREFIX_PATH", previousPrefix);
        }
    }

    private static string CopyFixture(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return destination;
    }

    private static IReadOnlyDictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(root, file),
                file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
                StringComparer.Ordinal);

    private static void AssertSnapshotUnchanged(
        IReadOnlyDictionary<string, string> before,
        string root)
    {
        var after = Snapshot(root);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (string path in before.Keys)
            Assert.Equal(before[path], after[path]);
    }
}
