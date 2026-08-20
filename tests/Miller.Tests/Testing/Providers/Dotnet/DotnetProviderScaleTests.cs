using System.Diagnostics;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Dotnet;

[Trait("Category", "Scale")]
public sealed class DotnetProviderScaleTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-provider-scale-").FullName;

    private readonly HashSet<string> _ctTemps = new(StringComparer.Ordinal);

    public void Dispose()
    {
        BestEffortDelete(_dir);
        foreach (var temp in _ctTemps)
            BestEffortDelete(temp);
    }

    /// <summary>
    /// The real-runner proof for <c>-preEnumerateTheories</c>, which a fake runner cannot give: without the
    /// flag xunit v3 reports one entry per test METHOD at discovery and folds every row of a theory onto one
    /// <c>TestCaseUniqueID</c> at run time. Measured on Miller's own suite, that was 6,233 discovered against
    /// 7,723 run.
    ///
    /// <para>This test builds a project whose test COUNT and METHOD count differ, so the two numbers cannot
    /// be confused. It fails if either command loses the flag: without it at discovery there are two cases
    /// instead of four; without it at run time the run reports ids the discovery never recorded.</para>
    /// </summary>
    [Fact]
    public async Task A_real_theory_is_discovered_and_run_as_one_case_per_row()
    {
        CtProviderTestSupport.RequireDotnet();

        CancellationToken ct = TestContext.Current.CancellationToken;
        string workspaceRoot = Path.Combine(_dir, "theory-repo");
        string projectDir = Path.Combine(workspaceRoot, "tests", "Sample.Tests");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "Sample.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="3.2.2" />
              </ItemGroup>
            </Project>
            """,
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "CalculatorTests.cs"),
            """
            using Xunit;

            namespace Sample.Tests;

            public sealed class CalculatorTests
            {
                [Fact]
                public void Adds() => Assert.Equal(2, 1 + 1);

                [Theory]
                [InlineData(1)]
                [InlineData(2)]
                [InlineData(3)]
                public void Positive(int value) => Assert.True(value > 0);
            }
            """,
            ct);

        var workspace = new ContinuousTestWorkspace(
            "ws:theory",
            workspaceRoot,
            Path.Combine(projectDir, "Sample.Tests.csproj"),
            Path.Combine(_dir, "theory-ct-build"));
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        var provider = new DotnetTestProvider(new TestProcessRunner());

        IReadOnlyList<ProviderTestCase> discovered = await provider.DiscoverAsync(workspace, ct);
        string[] discoveredIds = discovered
            .Select(row => row.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        // Two methods, four tests. The theory's three rows are three cases, each carrying its argument.
        Assert.Equal(
            [
                "xunit:Sample.Tests.CalculatorTests.Adds",
                "xunit:Sample.Tests.CalculatorTests.Positive(value: 1)",
                "xunit:Sample.Tests.CalculatorTests.Positive(value: 2)",
                "xunit:Sample.Tests.CalculatorTests.Positive(value: 3)",
            ],
            discoveredIds);

        ProviderRunResult run = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: "store:theory",
                RunId: "run:theory",
                TestCaseIds: discoveredIds),
            ct);

        Assert.Equal(
            discoveredIds,
            run.CaseResults.Select(row => row.TestCaseId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.All(run.CaseResults, row => Assert.Equal("passed", row.Status));
    }

    [Fact]
    public async Task Runner_cancel_kills_the_entire_process_tree()
    {
        var rootPidPath = Path.Combine(_dir, "root.pid");
        var childPidPath = Path.Combine(_dir, "child.pid");
        var command = BuildBlockingTreeCommand(rootPidPath, childPidPath);
        var runner = new TestProcessRunner();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var runTask = runner.RunAsync(command, cancellation.Token);
        int? rootPid = null;
        int? childPid = null;

        try
        {
            rootPid = await WaitForPidFileAsync(rootPidPath, TestContext.Current.CancellationToken);
            childPid = await WaitForPidFileAsync(childPidPath, TestContext.Current.CancellationToken);
            Assert.False(runTask.IsCompleted);
            Assert.True(IsProcessAlive(rootPid.Value));
            Assert.True(IsProcessAlive(childPid.Value));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
            Assert.False(IsProcessAlive(rootPid.Value));
            Assert.False(IsProcessAlive(childPid.Value));
        }
        finally
        {
            cancellation.Cancel();
            KillIfAlive(childPid);
            KillIfAlive(rootPid);
        }
    }

    [Fact]
    public async Task Owned_background_process_termination_kills_the_root_and_child_tree()
    {
        var rootPidPath = Path.Combine(_dir, "owned-root.pid");
        var childPidPath = Path.Combine(_dir, "owned-child.pid");
        var runner = new TestProcessRunner();
        await using var process = runner.Start(BuildBlockingTreeCommand(rootPidPath, childPidPath));
        int? rootPid = null;
        int? childPid = null;

        try
        {
            rootPid = await WaitForPidFileAsync(rootPidPath, TestContext.Current.CancellationToken);
            childPid = await WaitForPidFileAsync(childPidPath, TestContext.Current.CancellationToken);

            process.TerminateProcessTree();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.False(IsProcessAlive(rootPid.Value));
            Assert.False(IsProcessAlive(childPid.Value));
        }
        finally
        {
            process.TerminateProcessTree();
            KillIfAlive(childPid);
            KillIfAlive(rootPid);
        }
    }

    [Fact]
    public async Task Dotnet_smoke_executes_a_tiny_fixture_and_parses_results()
    {
        CtProviderTestSupport.RequireDotnet();
        var ct = TestContext.Current.CancellationToken;
        var workspaceRoot = Path.Combine(_dir, "repo");
        var projectDir = Path.Combine(workspaceRoot, "tests", "Sample.Tests");
        Directory.CreateDirectory(projectDir);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "Sample.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="3.2.2" />
              </ItemGroup>
            </Project>
            """,
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "CalculatorTests.cs"),
            """
            using Xunit;

            namespace Sample.Tests;

            public sealed class CalculatorTests
            {
                [Fact]
                public void Adds() => Assert.Equal(2, 1 + 1);
            }
            """,
            ct);

        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:scale",
            WorkspaceRoot: workspaceRoot,
            ProjectPath: Path.Combine(projectDir, "Sample.Tests.csproj"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build"));
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));

        var runner = new TestProcessRunner();
        var provider = new DotnetTestProvider(runner);

        var cases = await provider.DiscoverAsync(workspace, ct);
        var testCase = Assert.Single(cases);
        Assert.Equal("xunit:Sample.Tests.CalculatorTests.Adds", testCase.Id);
        Assert.Equal("xunit", testCase.Framework);

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
        Assert.True(CtGenerationPaths.IsGenerationId(result.GenerationId));
        var caseResult = Assert.Single(result.CaseResults);
        Assert.Equal(testCase.Id, caseResult.TestCaseId);
        Assert.Equal("passed", caseResult.Status);
        Assert.Equal("12", caseResult.ResultRevision);
        Assert.Equal("store:scale-identity", caseResult.IndexIdentity);

        Assert.Empty(WorkspaceGeneratedEntries(workspaceRoot));
        Assert.True(IsContained(workspace.BuildOutputRoot, result.GenerationId is null
            ? workspace.BuildOutputRoot
            : CtGenerationPaths.For(workspace, result.GenerationId).GenerationRoot));
        if (OperatingSystem.IsWindows())
            Assert.True(result.GenerationId!.Length <= 16);
    }

    private TestProcessCommand BuildBlockingTreeCommand(string rootPidPath, string childPidPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var powershell = CtProviderTestSupport.RequirePowerShell();
            return new TestProcessCommand(
                powershell,
                [
                    "-NoLogo",
                    "-NoProfile",
                    "-Command",
                    """
                    Set-Content -LiteralPath $env:MILLER_CT_ROOT_PID_PATH -Value $PID
                    $child = Start-Process -FilePath $env:MILLER_CT_POWERSHELL_PATH -ArgumentList '-NoLogo','-NoProfile','-Command','while ($true) { Start-Sleep -Seconds 60 }' -NoNewWindow -PassThru
                    Set-Content -LiteralPath $env:MILLER_CT_CHILD_PID_PATH -Value $child.Id
                    [Console]::Out.WriteLine('stdout-ready')
                    [Console]::Error.WriteLine('stderr-ready')
                    Wait-Process -Id $child.Id
                    """,
                ],
                _dir,
                new Dictionary<string, string?>
                {
                    ["MILLER_CT_ROOT_PID_PATH"] = rootPidPath,
                    ["MILLER_CT_CHILD_PID_PATH"] = childPidPath,
                    ["MILLER_CT_POWERSHELL_PATH"] = powershell,
                });
        }

        return new TestProcessCommand(
            "/bin/sh",
            [
                "-c",
                """
                printf '%s\n' "$$" > "$MILLER_CT_ROOT_PID_PATH"
                ( while :; do sleep 60; done ) &
                printf '%s\n' "$!" > "$MILLER_CT_CHILD_PID_PATH"
                printf 'stdout-ready\n'
                printf 'stderr-ready\n' >&2
                wait
                """,
            ],
            _dir,
            new Dictionary<string, string?>
            {
                ["MILLER_CT_ROOT_PID_PATH"] = rootPidPath,
                ["MILLER_CT_CHILD_PID_PATH"] = childPidPath,
            });
    }

    private static async Task<int> WaitForPidFileAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(path, cancellationToken);
                    if (int.TryParse(text.Trim(), out var pid))
                        return pid;
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException($"PID file was not written: {path}");
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void KillIfAlive(int? pid)
    {
        if (pid is null)
            return;

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static readonly HashSet<string> BuildOutputNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        "TestResults",
    };

    private static string[] WorkspaceGeneratedEntries(string workspaceRoot)
    {
        if (!Directory.Exists(workspaceRoot))
            return [];

        return Directory
            .EnumerateFileSystemEntries(workspaceRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetRelativePath(workspaceRoot, path)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(BuildOutputNames.Contains))
            .ToArray();
    }

    private static bool IsContained(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.Ordinal)
               || string.Equals(fullPath, Path.GetFullPath(root), StringComparison.Ordinal);
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
