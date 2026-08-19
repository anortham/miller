using Miller.Testing;
using Miller.Tests.Testing.Providers.Dotnet;
using Xunit;

namespace Miller.Tests.Testing.Providers.Node;

public sealed class JavaScriptTestProviderTests : IDisposable
{
    private const string IndexIdentity = "store:test-identity";

    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-js-provider-tests-").FullName;

    private readonly HashSet<string> _ctTemps = new(StringComparer.Ordinal);

    public void Dispose()
    {
        BestEffortDelete(_dir);
        foreach (var temp in _ctTemps)
            BestEffortDelete(temp);
    }

    [Fact]
    public async Task Discover_returns_stable_file_level_cases_and_excludes_generated_dirs()
    {
        var workspace = Workspace("vitest");
        WritePackageFile("src/math.test.ts", "test('adds', () => {})");
        WritePackageFile("src/string.spec.js", "test('trims', () => {})");
        WritePackageFile("tests/e2e/login.spec.ts", "test('browser flow', () => {})");
        WritePackageFile("cypress/e2e/login.cy.ts", "test('browser flow', () => {})");
        WritePackageFile("playwright/account.spec.ts", "test('browser flow', () => {})");
        WritePackageFile("node_modules/pkg/noise.test.ts", "test('noise', () => {})");
        WritePackageFile("dist/noise.spec.js", "test('noise', () => {})");
        WritePackageFile(".claude/worktrees/shadow/src/shadow.test.ts", "test('shadow', () => {})");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var first = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        var second = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["src/math.test.ts", "src/string.spec.js"], first.Select(row => row.Selector).ToArray());
        Assert.Equal(first.Select(row => row.Id).ToArray(), second.Select(row => row.Id).ToArray());
        Assert.All(first, row =>
        {
            Assert.StartsWith("js-test:", row.Id, StringComparison.Ordinal);
            Assert.Equal("vitest", row.Framework);
            Assert.Equal(row.Selector, row.SourcePath);
        });
    }

    [Fact]
    public async Task Discover_detects_vitest_from_package_json_when_framework_is_unspecified()
    {
        var workspace = Workspace(null);
        WritePackageFile(
            "package.json",
            """
            {
              "scripts": { "test": "vitest run" },
              "devDependencies": { "vitest": "^4.0.0" }
            }
            """);
        WritePackageFile("src/math.test.ts", "test('adds', () => {})");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var testCase = Assert.Single(await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Equal("vitest", testCase.Framework);
        Assert.Equal("src/math.test.ts", testCase.Selector);
    }

    [Fact]
    public async Task Run_without_result_artifact_returns_failed_results_for_selected_files()
    {
        var workspace = Workspace("jest");
        var runner = new FakeTestProcessRunner();
        runner.Enqueue(exitCode: 127, standardError: "sh: vue-cli-service: command not found");
        var provider = new JavaScriptTestProvider(runner);

        var result = await provider.RunAsync(
            Request(
                workspace,
                "js-test:tests/unit/components/AccountViewSelector.spec.ts",
                "js-test:tests/unit/components/ActiveAccountOverview.spec.ts"),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.Collection(
            result.CaseResults,
            first =>
            {
                Assert.Equal("js-test:tests/unit/components/AccountViewSelector.spec.ts", first.TestCaseId);
                Assert.Equal("failed", first.Status);
                Assert.Contains("vue-cli-service: command not found", first.FailureSummary);
                Assert.Equal(IndexIdentity, first.IndexIdentity);
            },
            second =>
            {
                Assert.Equal("js-test:tests/unit/components/ActiveAccountOverview.spec.ts", second.TestCaseId);
                Assert.Equal("failed", second.Status);
                Assert.Contains("vue-cli-service: command not found", second.FailureSummary);
                Assert.Equal(IndexIdentity, second.IndexIdentity);
            });
    }

    [Fact]
    public void Build_run_command_for_vitest_uses_local_bin_and_json_output_file()
    {
        var workspace = Workspace("vitest");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.Equal(LocalBin("vitest"), command.FileName);
        Assert.Equal(PackageRoot, command.WorkingDirectory);
        Assert.Contains("run", command.Arguments);
        Assert.Contains("--reporter=json", command.Arguments);
        Assert.Contains("--outputFile", command.Arguments);
        Assert.Contains("src/math.test.ts", command.Arguments);
        Assert.EndsWith(".json", command.Arguments[command.Arguments.ToList().IndexOf("--outputFile") + 1], StringComparison.Ordinal);
        var temp = CtTempPaths.ForWorkspace(workspace);
        Assert.Contains("miller-ct", temp, StringComparison.Ordinal);
        Assert.Equal(temp, command.Environment["TMPDIR"]);
        Assert.Equal(temp, command.Environment["TMP"]);
        Assert.Equal(temp, command.Environment["TEMP"]);
        Assert.Equal(workspace.WorkspaceRoot, command.Environment[CtEnvironment.WorkspaceRoot]);
        Assert.True(Directory.Exists(temp));
    }

    [Fact]
    public void Build_run_command_for_jest_uses_local_bin_and_json_output_file()
    {
        var workspace = Workspace("jest");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.Equal(LocalBin("jest"), command.FileName);
        Assert.Equal(PackageRoot, command.WorkingDirectory);
        Assert.Contains("--json", command.Arguments);
        Assert.Contains("--outputFile", command.Arguments);
        Assert.Contains("src/math.test.ts", command.Arguments);
        Assert.EndsWith(".json", command.Arguments[command.Arguments.ToList().IndexOf("--outputFile") + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void Build_run_command_for_node_test_uses_node_junit_output_file()
    {
        var workspace = Workspace("node-test");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.js"));

        Assert.Equal("node", command.FileName);
        Assert.Equal(PackageRoot, command.WorkingDirectory);
        Assert.Contains("--test", command.Arguments);
        Assert.Contains("--test-reporter", command.Arguments);
        Assert.Contains("junit", command.Arguments);
        Assert.Contains("--test-reporter-destination", command.Arguments);
        Assert.Contains("src/math.test.js", command.Arguments);
        Assert.EndsWith(".xml", command.Arguments[command.Arguments.ToList().IndexOf("--test-reporter-destination") + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void Build_run_command_uses_detected_jest_package_script_when_framework_is_unspecified()
    {
        var workspace = Workspace(null);
        WritePackageFile(
            "package.json",
            """
            {
              "scripts": { "test": "jest --runInBand" },
              "devDependencies": { "jest": "^30.0.0" }
            }
            """);
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.Equal("npm", command.FileName);
        Assert.Equal("run", command.Arguments[0]);
        Assert.Equal("test", command.Arguments[1]);
        Assert.Contains("--", command.Arguments);
        Assert.Contains("--json", command.Arguments);
        Assert.Contains("--outputFile", command.Arguments);
    }

    [Fact]
    public void Build_run_command_detects_vue_cli_jest_unit_script()
    {
        var workspace = Workspace(null);
        WritePackageFile(
            "package.json",
            """
            {
              "scripts": { "test:unit": "vue-cli-service test:unit" },
              "devDependencies": { "@vue/cli-plugin-unit-jest": "^4.5.18" }
            }
            """);
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/components/account.spec.ts"));

        Assert.Equal("npm", command.FileName);
        Assert.Equal("run", command.Arguments[0]);
        Assert.Equal("test:unit", command.Arguments[1]);
        Assert.Contains("--json", command.Arguments);
        Assert.Contains("--outputFile", command.Arguments);
        Assert.Contains("src/components/account.spec.ts", command.Arguments);
    }

    [Fact]
    public async Task Run_parses_jest_compatible_json_to_file_level_results()
    {
        var workspace = Workspace("jest");
        WritePackageFile("src/math.test.ts", "test('adds', () => {})");
        var runner = new FakeTestProcessRunner();
        runner.OnRun = command =>
        {
            var outputPath = command.Arguments[command.Arguments.ToList().IndexOf("--outputFile") + 1];
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(
                outputPath,
                $$"""
                {
                  "testResults": [
                    {
                      "name": "{{Path.Combine(PackageRoot, "src", "math.test.ts").Replace("\\", "\\\\")}}",
                      "status": "failed",
                      "assertionResults": [
                        {
                          "status": "failed",
                          "fullName": "adds numbers",
                          "failureMessages": ["Expected 2 to be 3"]
                        }
                      ]
                    }
                  ]
                }
                """);
        };
        runner.Enqueue(exitCode: 1);
        var provider = new JavaScriptTestProvider(runner);

        var result = await provider.RunAsync(
            Request(workspace, "js-test:src/math.test.ts"),
            TestContext.Current.CancellationToken);

        var caseResult = Assert.Single(result.CaseResults);
        Assert.Equal("failed", result.Status);
        Assert.Equal("js-test:src/math.test.ts", caseResult.TestCaseId);
        Assert.Equal("failed", caseResult.Status);
        Assert.Equal("Expected 2 to be 3", caseResult.FailureSummary);
        Assert.Equal(IndexIdentity, caseResult.IndexIdentity);
        Assert.Equal("rev-1", caseResult.ResultRevision);
    }

    [Fact]
    public async Task Run_parses_node_junit_to_file_level_results()
    {
        var workspace = Workspace("node-test");
        WritePackageFile("src/math.test.js", "test('adds', () => {})");
        var runner = new FakeTestProcessRunner();
        runner.OnRun = command =>
        {
            var outputPath = command.Arguments[command.Arguments.ToList().IndexOf("--test-reporter-destination") + 1];
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(
                outputPath,
                """
                <testsuite name="node" tests="1" failures="1">
                  <testcase classname="math" name="adds" time="0.01">
                    <failure message="Expected 2 to be 3">Expected 2 to be 3</failure>
                  </testcase>
                </testsuite>
                """);
        };
        runner.Enqueue(exitCode: 1);
        var provider = new JavaScriptTestProvider(runner);

        var result = await provider.RunAsync(
            Request(workspace, "js-test:src/math.test.js"),
            TestContext.Current.CancellationToken);

        var caseResult = Assert.Single(result.CaseResults);
        Assert.Equal("failed", result.Status);
        Assert.Equal("js-test:src/math.test.js", caseResult.TestCaseId);
        Assert.Equal("failed", caseResult.Status);
        Assert.Equal("Expected 2 to be 3", caseResult.FailureSummary);
        Assert.Equal(IndexIdentity, caseResult.IndexIdentity);
    }

    private string PackageRoot => Path.Combine(_dir, "package");

    private ContinuousTestWorkspace Workspace(string? framework)
    {
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:1",
            WorkspaceRoot: PackageRoot,
            ProjectPath: Path.Combine(PackageRoot, "package.json"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build", framework ?? "auto"),
            Framework: framework);
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        return workspace;
    }

    private ContinuousTestProviderRunRequest Request(ContinuousTestWorkspace workspace, params string[] testCaseIds) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:1",
            TestCaseIds: testCaseIds);

    private void WritePackageFile(string relativePath, string contents)
    {
        var path = Path.Combine(PackageRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private string LocalBin(string name) =>
        Path.Combine(PackageRoot, "node_modules", ".bin", name + (OperatingSystem.IsWindows() ? ".cmd" : ""));

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
