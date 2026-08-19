using Miller.Testing;
using Miller.Tests.Testing.Providers.Dotnet;
using Xunit;

namespace Miller.Tests.Testing.Providers.Python;

public sealed class PythonTestProviderTests : IDisposable
{
    private const string IndexIdentity = "store:test-identity";

    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-python-provider-tests-").FullName;

    private readonly HashSet<string> _ctTemps = new(StringComparer.Ordinal);

    private string ProjectRoot => Path.Combine(_dir, "project");

    public void Dispose()
    {
        BestEffortDelete(_dir);
        foreach (var temp in _ctTemps)
            BestEffortDelete(temp);
    }

    [Fact]
    public async Task Discover_returns_stable_file_level_cases_and_excludes_generated_dirs()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        WriteProjectFile("tests/test_math.py", "def test_add():\n    assert 1 + 1 == 2\n");
        WriteProjectFile("tests/widgets_test.py", "def test_widget():\n    assert True\n");
        WriteProjectFile("tests/helpers.py", "def helper():\n    pass\n");
        WriteProjectFile(".venv/lib/python3.12/site-packages/test_hidden.py", "def test_hidden():\n    pass\n");
        WriteProjectFile(".pytest_cache/v/cache/test_cache.py", "def test_cache():\n    pass\n");
        WriteProjectFile(".worktrees/shadow/tests/test_shadow.py", "def test_shadow():\n    pass\n");
        WriteProjectFile(".claude/worktrees/shadow/tests/test_claude_shadow.py", "def test_claude_shadow():\n    pass\n");

        var provider = new PythonTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(Workspace(framework: null), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["tests/test_math.py", "tests/widgets_test.py"],
            cases.Select(row => row.Selector).ToArray());
        Assert.All(cases, row =>
        {
            Assert.StartsWith("py-test:", row.Id, StringComparison.Ordinal);
            Assert.Equal("pytest", row.Framework);
            Assert.Equal("python-test-file", row.Metadata["kind"]);
        });
    }

    [Fact]
    public void Build_run_command_for_pytest_uses_python_module_and_junit_artifact()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        WriteProjectFile("tests/test_math.py", "def test_add():\n    assert True\n");
        var provider = new PythonTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace("pytest");

        var command = provider.BuildRunCommand(
            Request(workspace, PythonTestProvider.TestCaseId("tests/test_math.py")));

        Assert.Equal(ExpectedPythonExecutable(), command.FileName);
        Assert.Equal(ProjectRoot, command.WorkingDirectory);
        Assert.Equal(["-m", "pytest"], command.Arguments.Take(2).ToArray());
        Assert.Contains(command.Arguments, arg => arg.StartsWith("--junitxml=", StringComparison.Ordinal));
        Assert.Contains("tests/test_math.py", command.Arguments);
        Assert.Equal(ProjectRoot, command.Environment[CtEnvironment.WorkspaceRoot]);
        var temp = CtTempPaths.ForWorkspace(workspace);
        Assert.Contains("miller-ct", temp, StringComparison.Ordinal);
        Assert.Equal(temp, command.Environment["TMPDIR"]);
        Assert.Equal(temp, command.Environment["TMP"]);
        Assert.Equal(temp, command.Environment["TEMP"]);
        Assert.True(Directory.Exists(temp));
    }

    [Fact]
    public void Build_run_command_uses_uv_when_uv_lock_is_present()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        WriteProjectFile("uv.lock", string.Empty);
        var provider = new PythonTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(
            Request(Workspace(null), PythonTestProvider.TestCaseId("tests/test_math.py")));

        Assert.Equal("uv", command.FileName);
        Assert.Equal(["run", "python", "-m", "pytest"], command.Arguments.Take(4).ToArray());
    }

    [Fact]
    public async Task Run_parses_pytest_junit_to_file_level_results()
    {
        WriteProjectFile("pyproject.toml", "[tool.pytest.ini_options]\ntestpaths = [\"tests\"]\n");
        WriteProjectFile("tests/test_math.py", "def test_add():\n    assert True\n");
        WriteProjectFile("tests/widgets_test.py", "def test_widget():\n    assert False\n");
        var runner = new FakeTestProcessRunner();
        runner.Enqueue(exitCode: 1);
        runner.OnRun = command =>
        {
            var artifactPath = command.Arguments
                .Single(arg => arg.StartsWith("--junitxml=", StringComparison.Ordinal))
                ["--junitxml=".Length..];
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(
                artifactPath,
                """
                <testsuite name="pytest" tests="2" failures="1">
                  <testcase classname="tests.test_math" name="test_add" time="0.125" />
                  <testcase classname="tests.widgets_test" name="test_widget" time="0.25">
                    <failure message="assert False">AssertionError: assert False</failure>
                  </testcase>
                </testsuite>
                """);
        };
        var provider = new PythonTestProvider(runner);

        var result = await provider.RunAsync(
            Request(
                Workspace(null),
                PythonTestProvider.TestCaseId("tests/test_math.py"),
                PythonTestProvider.TestCaseId("tests/widgets_test.py")),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        var byCaseId = result.CaseResults.ToDictionary(row => row.TestCaseId, StringComparer.Ordinal);
        Assert.Equal("passed", byCaseId[PythonTestProvider.TestCaseId("tests/test_math.py")].Status);
        Assert.Equal(0.125, byCaseId[PythonTestProvider.TestCaseId("tests/test_math.py")].DurationSeconds);
        Assert.Equal("failed", byCaseId[PythonTestProvider.TestCaseId("tests/widgets_test.py")].Status);
        Assert.Contains("AssertionError", byCaseId[PythonTestProvider.TestCaseId("tests/widgets_test.py")].FailureSummary);
        Assert.Equal("pytest", byCaseId[PythonTestProvider.TestCaseId("tests/widgets_test.py")].Metadata["framework"]);
        Assert.All(result.CaseResults, row => Assert.Equal(IndexIdentity, row.IndexIdentity));
        Assert.All(result.CaseResults, row => Assert.Equal("rev-1", row.ResultRevision));
    }

    [Fact]
    public async Task Run_without_result_artifact_returns_failed_results_for_selected_files()
    {
        WriteProjectFile("pyproject.toml", "[project]\nname = \"sample\"\n");
        var runner = new FakeTestProcessRunner();
        runner.Enqueue(standardError: "pytest: command not found", exitCode: 127);
        var provider = new PythonTestProvider(runner);

        var result = await provider.RunAsync(
            Request(Workspace(null), PythonTestProvider.TestCaseId("tests/test_missing.py")),
            TestContext.Current.CancellationToken);

        var row = Assert.Single(result.CaseResults);
        Assert.Equal("failed", result.Status);
        Assert.Equal("failed", row.Status);
        Assert.Equal("pytest: command not found", row.FailureSummary);
        Assert.Equal(127, row.Metadata["exit_code"]);
        Assert.Equal(IndexIdentity, row.IndexIdentity);
    }

    private ContinuousTestWorkspace Workspace(string? framework)
    {
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:1",
            WorkspaceRoot: ProjectRoot,
            ProjectPath: Path.Combine(ProjectRoot, "pyproject.toml"),
            BuildOutputRoot: Path.Combine(_dir, "ct-build"),
            Framework: framework);
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        return workspace;
    }

    private static ContinuousTestProviderRunRequest Request(
        ContinuousTestWorkspace workspace,
        params string[] testCaseIds) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:pytest",
            TestCaseIds: testCaseIds);

    private void WriteProjectFile(string relativePath, string contents)
    {
        var path = Path.Combine(ProjectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string ExpectedPythonExecutable() =>
        OperatingSystem.IsWindows() ? "python" : "python3";

    private static void BestEffortDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
