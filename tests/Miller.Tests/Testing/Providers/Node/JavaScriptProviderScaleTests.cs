using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Node;

[Trait("Category", "Scale")]
public sealed class JavaScriptProviderScaleTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-js-scale-").FullName;

    private readonly HashSet<string> _ctTemps = new(StringComparer.Ordinal);

    public void Dispose()
    {
        BestEffortDelete(_dir);
        foreach (var temp in _ctTemps)
            BestEffortDelete(temp);
    }

    [Fact]
    public async Task Node_smoke_executes_a_tiny_node_test_fixture_and_parses_results()
    {
        CtProviderTestSupport.RequireNode();
        var ct = TestContext.Current.CancellationToken;
        var packageRoot = Path.Combine(_dir, "package");
        Directory.CreateDirectory(Path.Combine(packageRoot, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, "package.json"),
            """{"type":"module"}""",
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, "src", "math.test.js"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';

            test('adds numbers', () => {
              assert.equal(1 + 1, 2);
            });
            """,
            ct);

        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:scale",
            WorkspaceRoot: packageRoot,
            ProjectPath: Path.Combine(packageRoot, "package.json"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build"),
            Framework: "node-test");
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));

        var runner = new TestProcessRunner();
        var provider = new JavaScriptTestProvider(runner);
        var testCase = Assert.Single(await provider.DiscoverAsync(workspace, ct));

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "12",
                IndexIdentity: "store:scale-identity",
                RunId: "run:scale-smoke",
                TestCaseIds: [testCase.Id]),
            ct);

        var caseResult = Assert.Single(result.CaseResults);
        Assert.Equal("passed", result.Status);
        Assert.Equal(testCase.Id, caseResult.TestCaseId);
        Assert.Equal("passed", caseResult.Status);
        Assert.Equal("12", caseResult.ResultRevision);
        Assert.Equal("store:scale-identity", caseResult.IndexIdentity);
        Assert.NotNull(result.ResultArtifactPath);
        Assert.True(File.Exists(result.ResultArtifactPath!));
        Assert.True(CtGenerationPaths.IsGenerationId(result.GenerationId));
        Assert.StartsWith(
            CtGenerationPaths.For(workspace, result.GenerationId!).ResultsDirectory,
            result.ResultArtifactPath!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The classnames shape, against the real runner: a suite in <c>tests/*.js</c> named by the package
    /// script, with no <c>.test.</c> or <c>.spec.</c> stem anywhere. Discovery found zero cases here and
    /// the repo got no coverage at all, while its own <c>npm test</c> ran 63 passing tests (dogfood
    /// finding F8, 2026-08-21).
    /// </summary>
    [Fact]
    public async Task Node_smoke_runs_a_suite_the_package_script_names_by_directory_glob()
    {
        CtProviderTestSupport.RequireNode();
        var ct = TestContext.Current.CancellationToken;
        var packageRoot = Path.Combine(_dir, "globbed");
        Directory.CreateDirectory(Path.Combine(packageRoot, "tests"));
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, "package.json"),
            """{"type":"module","scripts":{"test":"node --test ./tests/*.js"}}""",
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, "tests", "index.js"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';

            test('joins names', () => {
              assert.equal(['a', 'b'].join(' '), 'a b');
            });
            """,
            ct);

        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:scale-glob",
            WorkspaceRoot: packageRoot,
            ProjectPath: Path.Combine(packageRoot, "package.json"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-glob", "ct-build"),
            Framework: "node-test");
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));

        var provider = new JavaScriptTestProvider(new TestProcessRunner());
        var testCase = Assert.Single(await provider.DiscoverAsync(workspace, ct));
        Assert.Equal("tests/index.js", testCase.Selector);

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "12",
                IndexIdentity: "store:scale-identity",
                RunId: "run:scale-glob",
                TestCaseIds: [testCase.Id]),
            ct);

        var caseResult = Assert.Single(result.CaseResults);
        Assert.Equal("passed", result.Status);
        Assert.Equal("passed", caseResult.Status);
        Assert.Equal(testCase.Id, caseResult.TestCaseId);
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
