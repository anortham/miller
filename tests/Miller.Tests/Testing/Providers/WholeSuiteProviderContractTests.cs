using Miller.Testing;
using Miller.Testing.Parsing;
using Miller.Tests.Testing.Providers.Dotnet;
using Miller.Tests.Testing.Providers.Rust;
using Xunit;

namespace Miller.Tests.Testing.Providers;

/// <summary>
/// One rule, checked against every provider: a WHOLE-SUITE run spends no selection on argv, and it still
/// reports a verdict for every id it was handed.
///
/// <para>The whole-suite form used to be expressed by BLANKING the id list. Providers that attribute from a
/// result artifact survived that, because the artifact names the tests; the cargo provider did not, because
/// its run loop is driven by the list itself — it started no process and reported "passed" over zero results
/// (dogfood finding F6, 2026-08-21). No test held every provider to the same promise, so nothing caught
/// it.</para>
///
/// <para>Every runner here is a fake or a scripted recorder. No real toolchain is launched, so these stay in
/// the fast tier.</para>
/// </summary>
public sealed class WholeSuiteProviderContractTests : IDisposable
{
    private const string IndexIdentity = "store:test-identity";
    private const string Revision = "rev-1";

    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-whole-suite-contract-").FullName;

    private readonly HashSet<string> _ctTemps = new(StringComparer.Ordinal);

    public void Dispose()
    {
        BestEffortDelete(_dir);
        foreach (var temp in _ctTemps)
            BestEffortDelete(temp);
    }

    [Fact]
    public async Task Dotnet_whole_suite_run_is_unfiltered_and_reports_every_selected_case()
    {
        string[] ids = ["xunit:Sample.Tests.A", "xunit:Sample.Tests.B"];
        var runner = new FakeTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue("verbose progress");
        runner.OnRun = WriteXunitJunitArtifact;
        var provider = new DotnetTestProvider(runner);

        var result = await provider.RunAsync(
            Request(DotnetWorkspace(), ids) with { WholeSuite = true },
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("-method", runner.Calls[1].Arguments);
        Assert.Contains("-reporter", runner.Calls[1].Arguments);
        Assert.Contains("verbose", runner.Calls[1].Arguments);
        Assert.Contains("-noAutoReporters", runner.Calls[1].Arguments);
        Assert.Empty(result.CaseResults);
        Assert.NotNull(result.ResultArtifactPath);
        Assert.True(File.Exists(result.ResultArtifactPath));
        var parsed = JunitTestResultParser.Parse(result.ResultArtifactPath!);
        Assert.Equal(2, parsed.Cases.Count);
        Assert.Contains(parsed.Cases, testCase => testCase.Name == "A" && testCase.Status == "passed");
        Assert.Contains(parsed.Cases, testCase => testCase.Name == "B" && testCase.Status == "passed");
    }

    [Fact]
    public async Task Python_whole_suite_run_is_unfiltered_and_reports_every_selected_case()
    {
        string[] files = ["tests/test_a.py", "tests/test_b.py"];
        foreach (var file in files)
            WriteFile(PythonRoot, file, "def test_x():\n    assert True\n");
        WriteFile(PythonRoot, "pyproject.toml", "[project]\nname = \"sample\"\n");
        string[] ids = files.Select(PythonTestProvider.TestCaseId).ToArray();
        var runner = new FakeTestProcessRunner();
        runner.Enqueue();
        runner.OnRun = command => WriteJunit(command, files);
        var provider = new PythonTestProvider(runner);

        var result = await provider.RunAsync(
            Request(PythonWorkspace(), ids) with { WholeSuite = true },
            TestContext.Current.CancellationToken);

        var command = Assert.Single(runner.Calls);
        Assert.DoesNotContain(command.Arguments, argument => argument.EndsWith(".py", StringComparison.Ordinal));
        AssertOneResultPerId(ids, result);
    }

    [Fact]
    public async Task Node_whole_suite_run_is_unfiltered_and_reports_every_selected_case()
    {
        string[] files = ["src/math.test.ts", "src/string.test.ts"];
        foreach (var file in files)
            WriteFile(NodeRoot, file, "test('x', () => {})");
        WriteFile(NodeRoot, "package.json", """{ "devDependencies": { "vitest": "^1.0.0" } }""");
        string[] ids = files.Select(file => "js-test:" + file).ToArray();
        var runner = new FakeTestProcessRunner();
        runner.Enqueue();
        runner.OnRun = command => WriteJestReport(command, files);
        var provider = new JavaScriptTestProvider(runner);

        var result = await provider.RunAsync(
            Request(NodeWorkspace(), ids) with { WholeSuite = true },
            TestContext.Current.CancellationToken);

        var command = Assert.Single(runner.Calls);
        Assert.DoesNotContain(command.Arguments, argument => argument.EndsWith(".test.ts", StringComparison.Ordinal));
        AssertOneResultPerId(ids, result);
    }

    [Fact]
    public async Task Rust_whole_suite_run_is_unfiltered_and_reports_every_selected_case()
    {
        string[] ids =
        [
            "rust-test:adder::lib/adder::tests::add_works",
            "rust-test:adder::lib/adder::tests::add_zero",
        ];
        var runner = new ScriptedTestProcessRunner(command =>
            ScriptedTestProcessRunner.Has(command, "--no-run")
                ? new TestProcessResult(0, string.Empty, string.Empty)
                : new TestProcessResult(
                    0,
                    "running 2 tests\n"
                    + "test tests::add_works ... ok\n"
                    + "test tests::add_zero ... ok\n\n"
                    + "test result: ok. 2 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.02s\n",
                    string.Empty));
        var provider = new RustTestProvider(runner);

        var result = await provider.RunAsync(
            Request(RustWorkspace(), ids) with { WholeSuite = true },
            TestContext.Current.CancellationToken);

        var command = runner.Calls.Single(call => !ScriptedTestProcessRunner.Has(call, "--no-run"));
        Assert.DoesNotContain("--exact", command.Arguments);
        AssertOneResultPerId(ids, result);
    }

    private static void AssertOneResultPerId(IReadOnlyList<string> ids, ProviderRunResult result)
    {
        Assert.Equal(
            ids.Order(StringComparer.Ordinal).ToArray(),
            result.CaseResults.Select(row => row.TestCaseId).Order(StringComparer.Ordinal).ToArray());
        Assert.All(result.CaseResults, row => Assert.Equal("passed", row.Status));
    }

    private static void WriteXunitJunitArtifact(TestProcessCommand command)
    {
        var artifactFlag = command.Arguments.ToList().IndexOf("-jUnit");
        if (artifactFlag < 0)
            return;

        var artifactPath = command.Arguments[artifactFlag + 1];
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(
            artifactPath,
            "<testsuite name=\"xunit\"><testcase classname=\"Sample.Tests\" name=\"A\" />"
            + "<testcase classname=\"Sample.Tests\" name=\"B\" /></testsuite>");
    }

    private static void WriteJunit(TestProcessCommand command, IReadOnlyList<string> files)
    {
        var artifactPath = command.Arguments
            .Single(argument => argument.StartsWith("--junitxml=", StringComparison.Ordinal))
            ["--junitxml=".Length..];
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        var cases = string.Concat(files.Select(file =>
            $"""<testcase classname="{file[..^".py".Length].Replace('/', '.')}" name="test_x" time="0.25" />"""));
        File.WriteAllText(artifactPath, $"""<testsuite name="pytest">{cases}</testsuite>""");
    }

    private void WriteJestReport(TestProcessCommand command, IReadOnlyList<string> files)
    {
        var outputPath = command.Arguments[command.Arguments.ToList().IndexOf("--outputFile") + 1];
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var entries = files.Select(file =>
        {
            var absolute = Path.Combine(NodeRoot, file.Replace('/', Path.DirectorySeparatorChar))
                .Replace("\\", "\\\\", StringComparison.Ordinal);
            return $$"""{"name": "{{absolute}}", "status": "passed"}""";
        });
        File.WriteAllText(outputPath, $$"""{"testResults":[{{string.Join(",", entries)}}]}""");
    }

    private string PythonRoot => Path.Combine(_dir, "py");

    private string NodeRoot => Path.Combine(_dir, "package");

    private string RustRoot => Path.Combine(_dir, "rust");

    private ContinuousTestWorkspace DotnetWorkspace() =>
        Track(new ContinuousTestWorkspace(
            WorkspaceId: "ws:dotnet",
            WorkspaceRoot: Path.Combine(_dir, "repo"),
            ProjectPath: Path.Combine(_dir, "repo", "tests", "Sample.Tests", "Sample.Tests.csproj"),
            BuildOutputRoot: Path.Combine(_dir, "ct-build", "dotnet")));

    private ContinuousTestWorkspace PythonWorkspace() =>
        Track(new ContinuousTestWorkspace(
            WorkspaceId: "ws:python",
            WorkspaceRoot: PythonRoot,
            ProjectPath: Path.Combine(PythonRoot, "pyproject.toml"),
            BuildOutputRoot: Path.Combine(_dir, "ct-build", "python"),
            Framework: "pytest"));

    private ContinuousTestWorkspace NodeWorkspace() =>
        Track(new ContinuousTestWorkspace(
            WorkspaceId: "ws:node",
            WorkspaceRoot: NodeRoot,
            ProjectPath: Path.Combine(NodeRoot, "package.json"),
            BuildOutputRoot: Path.Combine(_dir, "ct-build", "node"),
            Framework: "vitest"));

    private ContinuousTestWorkspace RustWorkspace() =>
        Track(new ContinuousTestWorkspace(
            WorkspaceId: "ws:rust",
            WorkspaceRoot: RustRoot,
            ProjectPath: Path.Combine(RustRoot, "Cargo.toml"),
            BuildOutputRoot: Path.Combine(_dir, "ct-build", "rust")));

    private ContinuousTestWorkspace Track(ContinuousTestWorkspace workspace)
    {
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        return workspace;
    }

    private static ContinuousTestProviderRunRequest Request(
        ContinuousTestWorkspace workspace,
        IReadOnlyList<string> testCaseIds) =>
        new(
            Workspace: workspace,
            SelectedRevision: Revision,
            IndexIdentity: IndexIdentity,
            RunId: "run:whole-suite",
            TestCaseIds: testCaseIds);

    private static void WriteFile(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

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
