using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Python;

[Trait("Category", "Scale")]
public sealed class PythonProviderScaleTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-python-scale-").FullName;

    private readonly HashSet<string> _ctTemps = new(StringComparer.Ordinal);

    public void Dispose()
    {
        BestEffortDelete(_dir);
        foreach (var temp in _ctTemps)
            BestEffortDelete(temp);
    }

    [Fact]
    public async Task Python_smoke_executes_a_tiny_pytest_fixture_and_parses_results()
    {
        var python = CtProviderTestSupport.RequirePython();
        var ct = TestContext.Current.CancellationToken;
        var projectRoot = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "tests"));
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "pyproject.toml"),
            """
            [project]
            name = "sample"
            version = "0.0.1"
            """,
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "tests", "test_math.py"),
            """
            def test_add():
                assert 1 + 1 == 2
            """,
            ct);
        await EnsurePytestAsync(projectRoot, python, ct);

        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:scale",
            WorkspaceRoot: projectRoot,
            ProjectPath: Path.Combine(projectRoot, "pyproject.toml"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build"),
            Framework: "pytest");
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));

        var runner = new TestProcessRunner();
        var provider = new PythonTestProvider(runner);

        var cases = await provider.DiscoverAsync(workspace, ct);
        var testCase = Assert.Single(cases);
        Assert.Equal("py-test:tests/test_math.py", testCase.Id);
        Assert.Equal("pytest", testCase.Framework);

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "12",
                IndexIdentity: "store:scale-identity",
                RunId: "run:scale-smoke",
                TestCaseIds: [testCase.Id]),
            ct);

        Assert.Equal("passed", result.Status);
        Assert.Equal("run:scale-smoke", result.RunId);
        var caseResult = Assert.Single(result.CaseResults);
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

    private static async Task EnsurePytestAsync(string projectRoot, string python, CancellationToken cancellationToken)
    {
        if (await ModuleAvailableAsync(python, "pytest", cancellationToken))
            return;

        var uv = CtProviderTestSupport.LocateOnPath(OperatingSystem.IsWindows() ? "uv.exe" : "uv");
        if (uv is null)
            Assert.Skip("pytest is required for PythonTestProvider Scale smoke");

        var venvPython = OperatingSystem.IsWindows()
            ? Path.Combine(projectRoot, ".venv", "Scripts", "python.exe")
            : Path.Combine(projectRoot, ".venv", "bin", "python");
        await RunOrSkipAsync(
            uv,
            ["venv", Path.Combine(projectRoot, ".venv")],
            projectRoot,
            "uv venv is required for PythonTestProvider Scale smoke",
            cancellationToken);
        await RunOrSkipAsync(
            uv,
            ["pip", "install", "--python", venvPython, "pytest"],
            projectRoot,
            "uv pip install pytest is required for PythonTestProvider Scale smoke",
            cancellationToken);
        if (!await ModuleAvailableAsync(venvPython, "pytest", cancellationToken))
            Assert.Skip("pytest is required for PythonTestProvider Scale smoke");
    }

    private static async Task<bool> ModuleAvailableAsync(
        string python,
        string module,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync(python, ["-c", $"import {module}"], workingDirectory: null, cancellationToken);
            return result == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task RunOrSkipAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string skipReason,
        CancellationToken cancellationToken)
    {
        try
        {
            var exitCode = await RunAsync(fileName, arguments, workingDirectory, cancellationToken);
            if (exitCode != 0)
                Assert.Skip(skipReason);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip(skipReason);
        }
    }

    private static async Task<int> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = System.Diagnostics.Process.Start(start);
        if (process is null)
            return 1;

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
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
