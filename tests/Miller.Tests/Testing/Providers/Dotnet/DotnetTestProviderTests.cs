using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Dotnet;

public sealed class DotnetTestProviderTests : IDisposable
{
    private const string IndexIdentity = "store:test-identity";

    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-provider-tests-").FullName;

    private readonly HashSet<string> _ctTemps = new(StringComparer.Ordinal);

    public void Dispose()
    {
        BestEffortDelete(_dir);
        foreach (var temp in _ctTemps)
            BestEffortDelete(temp);
    }

    [Fact]
    public async Task Discover_builds_isolated_dotnet_command_and_parses_cases()
    {
        var runner = new FakeTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(
            """
            [{"Assembly":"/tmp/Sample.Tests.dll","DisplayName":"Sample.Tests.Passes","ID":"xunit-id-1","Class":"Sample.Tests","Method":"Passes"}]
            """);
        var provider = new DotnetTestProvider(runner);
        var workspace = Workspace();

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        var testCase = Assert.Single(cases);
        Assert.Equal("xunit:Sample.Tests.Passes", testCase.Id);
        Assert.Equal("Sample.Tests.Passes", testCase.DisplayName);
        Assert.Equal("Sample.Tests.Passes", testCase.FullyQualifiedName);
        Assert.Equal("-method Sample.Tests.Passes", testCase.Selector);
        Assert.Equal("xunit", testCase.Framework);
        Assert.Equal("Sample.Tests", testCase.Metadata["class"]?.ToString());
        Assert.Equal("Passes", testCase.Metadata["method"]?.ToString());

        var generation = FirstGeneration(workspace);
        Assert.Equal(2, runner.Calls.Count);
        var buildCommand = runner.Calls[0];
        Assert.Equal("dotnet", buildCommand.FileName);
        Assert.Equal(workspace.WorkspaceRoot, buildCommand.WorkingDirectory);
        Assert.Equal("build", buildCommand.Arguments[0]);
        Assert.Contains(workspace.ProjectPath, buildCommand.Arguments);
        AssertUsesCtBuildIsolation(buildCommand.Arguments, workspace, generation);
        AssertUsesGenerationTempDirectory(buildCommand.Environment, generation);

        var listCommand = runner.Calls[1];
        Assert.Equal(
            Path.Combine(generation.OutDir, "Sample.Tests", "Sample.Tests" + ExecutableExtension()),
            listCommand.FileName);
        Assert.Equal(DotnetTestProvider.TestExecutablePath(workspace), listCommand.FileName);
        Assert.Equal(workspace.WorkspaceRoot, listCommand.WorkingDirectory);
        Assert.Equal(["-list", "full/json", "-noLogo", "-noColor", "-preEnumerateTheories"], listCommand.Arguments);
        Assert.Equal(workspace.WorkspaceRoot, listCommand.Environment[CtEnvironment.WorkspaceRoot]);
        AssertUsesGenerationTempDirectory(listCommand.Environment, generation);
    }

    [Fact]
    public async Task Discover_xunit_ids_are_stable_across_generations_and_theory_selectors_drop_arguments()
    {
        var runner = new FakeTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(
            """
            [{"Assembly":"/tmp/Sample.Tests.dll","DisplayName":"Sample.Tests.Cases(value: 1)","ID":"generation-hash-a","Class":"Sample.Tests","Method":"Cases"},{"Assembly":"/tmp/Sample.Tests.dll","DisplayName":"Sample.Tests.Cases(value: 2)","ID":"generation-hash-b","Class":"Sample.Tests","Method":"Cases"}]
            """);
        var provider = new DotnetTestProvider(runner);
        var workspace = Workspace();

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["xunit:Sample.Tests.Cases(value: 1)", "xunit:Sample.Tests.Cases(value: 2)"],
            cases.Select(row => row.Id).ToArray());
        Assert.All(cases, row => Assert.Equal("-method Sample.Tests.Cases", row.Selector));
    }

    [Fact]
    public async Task Discover_for_mstest_uses_dotnet_test_list_tests_and_parses_cases()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace("mstest");
        var generation = FirstGeneration(workspace);
        var targetPath = Path.Combine(generation.OutDir, "Sample.Tests", "Custom.Assembly.dll");
        runner.Enqueue();
        runner.Enqueue(targetPath);
        runner.Enqueue(
            """
            The following Tests are available:
                Sample.Tests.CalculatorTests.Adds
                Sample.Tests.CalculatorTests.Subtracts
            """);
        runner.OnRun = command =>
        {
            if (command.Arguments.FirstOrDefault() == "build")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.WriteAllText(targetPath, string.Empty);
            }
        };
        var provider = new DotnetTestProvider(runner);

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Sample.Tests.CalculatorTests.Adds", "Sample.Tests.CalculatorTests.Subtracts"],
            cases.Select(row => row.FullyQualifiedName).ToArray());
        Assert.All(cases, row =>
        {
            Assert.Equal("mstest", row.Framework);
            Assert.Equal(row.FullyQualifiedName, row.Selector);
            Assert.StartsWith("mstest:", row.Id, StringComparison.Ordinal);
        });

        Assert.Equal(3, runner.Calls.Count);
        var targetQuery = runner.Calls[1];
        Assert.Contains("-getProperty:TargetPath", targetQuery.Arguments);
        Assert.Contains($"-p:OutDir={generation.OutDir}", targetQuery.Arguments);
        Assert.Contains("-p:GenerateProjectSpecificOutputFolder=true", targetQuery.Arguments);

        var listCommand = runner.Calls[2];
        Assert.Equal("test", listCommand.Arguments[0]);
        Assert.Equal(targetPath, listCommand.Arguments[1]);
        Assert.DoesNotContain(workspace.ProjectPath, listCommand.Arguments);
        Assert.Contains("--list-tests", listCommand.Arguments);
        AssertContainsAdjacentPair(listCommand.Arguments, "--results-directory", generation.ResultsDirectory);
        AssertUsesGenerationTempDirectory(listCommand.Environment, generation);
    }

    [Fact]
    public async Task Discover_for_nunit_prefers_all_diagnostic_identity_batches()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace("nunit");
        var generation = FirstGeneration(workspace);
        var targetPath = Path.Combine(generation.OutDir, "Sample.Tests", "Custom.Assembly.dll");
        runner.Enqueue();
        runner.Enqueue(targetPath);
        runner.Enqueue(
            """
            The following Tests are available:
                Uploads
                Downloads
            """);
        var diagnosticPath = Path.Combine(generation.GenerationRoot, "logs", "discovery.diag.log");
        Directory.CreateDirectory(Path.GetDirectoryName(diagnosticPath)!);
        File.WriteAllText(diagnosticPath, "stale discovery identity");
        runner.OnRun = command =>
        {
            if (command.Arguments.FirstOrDefault() == "build")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.WriteAllText(targetPath, string.Empty);
            }

            var diagIndex = command.Arguments.ToList().IndexOf("--diag");
            if (diagIndex < 0)
                return;

            Assert.False(File.Exists(command.Arguments[diagIndex + 1]));
            File.WriteAllText(
                command.Arguments[diagIndex + 1],
                """
                TpTrace: Received message: {"Version":7,"MessageType":"TestDiscovery.TestCasesFound","Payload":[{"FullyQualifiedName":"Sample.Tests.EdrFileUploadTests.Uploads(\"file.txt\",Terraform.Contracts.Edr.StoredFileDto)","DisplayName":"Uploads(\"file.txt\",Terraform.Contracts.Edr.StoredFileDto)"}]}
                TpTrace: Received message: {"Version":7,"MessageType":"TestDiscovery.Completed","Payload":{"LastDiscoveredTests":[{"FullyQualifiedName":"Sample.Tests.EdrFileUploadTests.Downloads","DisplayName":"Downloads"}]}}
                """);
        };
        var provider = new DotnetTestProvider(runner);

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "Sample.Tests.EdrFileUploadTests.Uploads(\"file.txt\",Terraform.Contracts.Edr.StoredFileDto)",
                "Sample.Tests.EdrFileUploadTests.Downloads",
            ],
            cases.Select(row => row.FullyQualifiedName).ToArray());
        Assert.Equal(
            ["Uploads(\"file.txt\",Terraform.Contracts.Edr.StoredFileDto)", "Downloads"],
            cases.Select(row => row.DisplayName).ToArray());
    }

    [Fact]
    public async Task Discover_stdout_fallback_ignores_vstest_diagnostics_banner()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace("nunit");
        var targetPath = Path.Combine(FirstGeneration(workspace).OutDir, "Sample.Tests", "Sample.Tests.dll");
        runner.Enqueue();
        runner.Enqueue(targetPath);
        runner.Enqueue(
            """
            Test run for C:\repo\Sample.Tests.dll (.NETCoreApp,Version=v10.0)
            Logging Vstest Diagnostics in file: C:\state\ct-build\g000002\logs\discovery.diag.log
            The following Tests are available:
                Sample.Tests.CalculatorTests.Adds
            """);
        runner.OnRun = command =>
        {
            if (command.Arguments.FirstOrDefault() == "build")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.WriteAllText(targetPath, string.Empty);
            }
        };
        var provider = new DotnetTestProvider(runner);

        var testCase = Assert.Single(await provider.DiscoverAsync(
            workspace,
            TestContext.Current.CancellationToken));

        Assert.Equal("Sample.Tests.CalculatorTests.Adds", testCase.FullyQualifiedName);
    }

    [Fact]
    public async Task Discover_for_nunit_rejects_target_path_outside_build_root()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace("nunit");
        var targetPath = Path.Combine(_dir, "outside", "Sample.Tests.dll");
        runner.Enqueue();
        runner.Enqueue(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, string.Empty);
        var provider = new DotnetTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("outside", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task Discover_for_nunit_rejects_missing_target_path()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace("nunit");
        runner.Enqueue();
        runner.Enqueue(Path.Combine(FirstGeneration(workspace).OutDir, "Sample.Tests", "Missing.Tests.dll"));
        var provider = new DotnetTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("does not exist", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FirstGeneration(workspace).GenerationId, exception.GenerationId);
        Assert.Equal(2, runner.Calls.Count);
    }

    /// <summary>
    /// Discovery and the run that follows it build the SAME source state. Each used to allocate its own
    /// generation, and the generation directory is the build's <c>OutDir</c>, so the second build found an
    /// empty output directory and repeated the whole copy step. Every project in a CT run was built twice for
    /// bytes that were already there.
    /// </summary>
    [Fact]
    public async Task A_run_after_a_discovery_reuses_the_generation_the_discovery_built()
    {
        var runner = new FakeTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(
            """
            [{"Assembly":"/tmp/Sample.Tests.dll","DisplayName":"Sample.Tests.Passes","ID":"xunit-id-1","Class":"Sample.Tests","Method":"Passes"}]
            """);
        runner.Enqueue();
        runner.Enqueue(XunitPassedRun, exitCode: 0);
        var provider = new DotnetTestProvider(runner);
        var workspace = Workspace();

        await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        await provider.RunAsync(Request(workspace), TestContext.Current.CancellationToken);

        string[] outDirs = runner.Calls
            .Where(call => call.Arguments.FirstOrDefault() == "build")
            .Select(call => call.Arguments.Single(argument => argument.StartsWith("-p:OutDir=", StringComparison.Ordinal)))
            .ToArray();

        Assert.Equal(2, outDirs.Length);
        Assert.Equal(outDirs[0], outDirs[1]);
        Assert.Equal(FirstGeneration(workspace).GenerationId, Assert.Single(GenerationDirectories(workspace)));
    }

    /// <summary>
    /// The handoff is one-shot. Two runs must never share a generation, because a generation is the immutable
    /// build a run executes against and its results directory.
    /// </summary>
    [Fact]
    public async Task A_second_run_with_no_discovery_between_them_allocates_its_own_generation()
    {
        var runner = new FakeTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(
            """
            [{"Assembly":"/tmp/Sample.Tests.dll","DisplayName":"Sample.Tests.Passes","ID":"xunit-id-1","Class":"Sample.Tests","Method":"Passes"}]
            """);
        runner.Enqueue();
        runner.Enqueue(XunitPassedRun, exitCode: 0);
        runner.Enqueue();
        runner.Enqueue(XunitPassedRun, exitCode: 0);
        var provider = new DotnetTestProvider(runner);
        var workspace = Workspace();

        await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        await provider.RunAsync(Request(workspace), TestContext.Current.CancellationToken);
        await provider.RunAsync(Request(workspace), TestContext.Current.CancellationToken);

        Assert.Equal(2, GenerationDirectories(workspace).Count);
    }

    /// <summary>
    /// The invariant behind <c>-preEnumerateTheories</c>: a case id that discovery records must be an id a
    /// run can report a result for.
    ///
    /// <para>Delay-enumerated theories broke it in both directions. <c>-list</c> reported one entry per
    /// METHOD, so twelve rows counted as one case — measured on Miller's own suite, 6,233 discovered against
    /// 7,723 run. And a run emitted ONE <c>test-case-starting</c> whose display name carried no arguments,
    /// with one <c>TestCaseUniqueID</c> shared by every row, so twelve rows folded into one verdict.</para>
    ///
    /// <para>Setting the flag on only ONE command is worse than neither: discovery would record row ids that
    /// no run could ever prove, and every row would stay unproven forever. This test fails if either command
    /// loses the flag, because then the two id sets stop matching.</para>
    /// </summary>
    [Fact]
    public async Task A_discovered_theory_row_is_an_id_the_run_reports_a_result_for()
    {
        var runner = new FakeTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(
            """
            [{"Assembly":"/tmp/Sample.Tests.dll","DisplayName":"Sample.Tests.Cases(value: 1)","ID":"x1","Class":"Sample.Tests","Method":"Cases"},
             {"Assembly":"/tmp/Sample.Tests.dll","DisplayName":"Sample.Tests.Cases(value: 2)","ID":"x2","Class":"Sample.Tests","Method":"Cases"}]
            """);
        runner.Enqueue();
        runner.Enqueue(XunitPreEnumeratedTheoryRun, exitCode: 0);
        var provider = new DotnetTestProvider(runner);
        var workspace = Workspace();

        IReadOnlyList<ProviderTestCase> discovered =
            await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        ProviderRunResult run = await provider.RunAsync(
            Request(workspace, discovered.Select(row => row.Id).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["xunit:Sample.Tests.Cases(value: 1)", "xunit:Sample.Tests.Cases(value: 2)"],
            discovered.Select(row => row.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            discovered.Select(row => row.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            run.CaseResults.Select(row => row.TestCaseId).OrderBy(id => id, StringComparer.Ordinal).ToArray());

        // One -method unit for the two rows: the selection collapses, so the argv does not grow with rows.
        TestProcessCommand runCommand = runner.Calls[3];
        Assert.Equal(["-method", "Sample.Tests.Cases"], runCommand.Arguments.Where(
            argument => argument == "-method" || argument == "Sample.Tests.Cases").ToArray());
        Assert.Contains("-preEnumerateTheories", runCommand.Arguments);
        Assert.Contains("-preEnumerateTheories", runner.Calls[1].Arguments);
    }

    [Fact]
    public async Task Run_builds_selected_dotnet_command_and_parses_run_result()
    {
        var runner = new FakeTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(XunitPassedRun, exitCode: 0);
        runner.OnRun = WriteEmptyJunitArtifact;
        var provider = new DotnetTestProvider(runner);
        var workspace = Workspace();

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:with:colons",
                TestCaseIds: ["xunit:Sample.Tests.Passes"]),
            TestContext.Current.CancellationToken);

        var generation = FirstGeneration(workspace);
        Assert.Equal("run:with:colons", result.RunId);
        Assert.Equal("passed", result.Status);
        Assert.Equal(generation.GenerationId, result.GenerationId);
        Assert.Equal(DateTimeOffset.Parse("2026-06-14T01:00:00Z"), result.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-06-14T01:00:02Z"), result.EndedAt);
        Assert.NotNull(result.ResultArtifactPath);
        Assert.StartsWith(generation.ResultsDirectory, result.ResultArtifactPath, StringComparison.Ordinal);
        Assert.EndsWith(".junit.xml", result.ResultArtifactPath, StringComparison.Ordinal);
        Assert.DoesNotContain(":", Path.GetFileName(result.ResultArtifactPath), StringComparison.Ordinal);
        var caseResult = Assert.Single(result.CaseResults);
        Assert.Equal("result-1", caseResult.Id);
        Assert.Equal("xunit:Sample.Tests.Passes", caseResult.TestCaseId);
        Assert.Equal("passed", caseResult.Status);
        Assert.Equal("rev-1", caseResult.ResultRevision);
        Assert.Equal(IndexIdentity, caseResult.IndexIdentity);
        Assert.Equal(0.125, caseResult.DurationSeconds);

        Assert.Equal(2, runner.Calls.Count);
        AssertUsesCtBuildIsolation(runner.Calls[0].Arguments, workspace, generation);
        var runCommand = runner.Calls[1];
        Assert.Equal(
            Path.Combine(generation.OutDir, "Sample.Tests", "Sample.Tests" + ExecutableExtension()),
            runCommand.FileName);
        AssertContainsAdjacentPair(runCommand.Arguments, "-method", "Sample.Tests.Passes");
        Assert.Equal(workspace.WorkspaceRoot, runCommand.Environment[CtEnvironment.WorkspaceRoot]);
        AssertUsesGenerationTempDirectory(runCommand.Environment, generation);
    }

    [Fact]
    public void BuildRunCommand_xunit_whole_suite_uses_verbose_artifact_transport()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(new ContinuousTestProviderRunRequest(
            Workspace: Workspace(),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:whole-suite",
            TestCaseIds: ["xunit:Sample.Tests.Passes"],
            WholeSuite: true));

        Assert.Equal(
            ["-noLogo", "-noColor", "-reporter", "verbose", "-noAutoReporters", "-preEnumerateTheories"],
            command.Arguments.Take(6).ToArray());
        Assert.Contains("-jUnit", command.Arguments);
        Assert.DoesNotContain("json", command.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("-method", command.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Run_xunit_whole_suite_returns_a_passing_artifact_result_without_stdout()
    {
        var runner = new ArtifactTestProcessRunner();
        runner.Enqueue(standardOutputTruncated: true);
        runner.Enqueue(standardOutput: "live verbose output", standardOutputTruncated: true);
        runner.OnRun = command => WriteJunitArtifact(command, failed: false);
        var provider = new DotnetTestProvider(runner);

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: Workspace(),
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:whole-suite:passed",
                TestCaseIds: ["xunit:Sample.Tests.Passes"],
                WholeSuite: true),
            TestContext.Current.CancellationToken);

        Assert.Equal("passed", result.Status);
        Assert.Empty(result.CaseResults);
        Assert.NotNull(result.ResultArtifactPath);
        Assert.True(File.Exists(result.ResultArtifactPath));
        Assert.Equal("verbose", runner.Calls[1].Arguments[3]);
        Assert.Contains("-noAutoReporters", runner.Calls[1].Arguments);
    }

    [Fact]
    public async Task Run_xunit_whole_suite_accepts_a_failed_artifact_on_red_exit()
    {
        var runner = new ArtifactTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(standardOutput: "failed verbose output", exitCode: 1, standardOutputTruncated: true);
        runner.OnRun = command => WriteJunitArtifact(command, failed: true);
        var provider = new DotnetTestProvider(runner);

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: Workspace(),
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:whole-suite:failed",
                TestCaseIds: ["xunit:Sample.Tests.Fails"],
                WholeSuite: true),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.Empty(result.CaseResults);
        Assert.NotNull(result.ResultArtifactPath);
    }

    [Fact]
    public async Task Run_xunit_whole_suite_rejects_a_missing_artifact()
    {
        var runner = new ArtifactTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue();
        var provider = new DotnetTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: Workspace(),
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:whole-suite:missing",
                TestCaseIds: ["xunit:Sample.Tests.Passes"],
                WholeSuite: true),
            TestContext.Current.CancellationToken));

        Assert.Contains("JUnit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_xunit_whole_suite_rejects_an_empty_artifact()
    {
        var runner = new ArtifactTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue();
        runner.OnRun = WriteEmptyJunitArtifact;
        var provider = new DotnetTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: Workspace(),
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:whole-suite:empty",
                TestCaseIds: ["xunit:Sample.Tests.Passes"],
                WholeSuite: true),
            TestContext.Current.CancellationToken));

        Assert.Contains("no test cases", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_xunit_whole_suite_rejects_malformed_artifact()
    {
        var runner = new ArtifactTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue();
        runner.OnRun = command => WriteArtifact(command, "<testsuite>");
        var provider = new DotnetTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: Workspace(),
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:whole-suite:malformed",
                TestCaseIds: ["xunit:Sample.Tests.Passes"],
                WholeSuite: true),
            TestContext.Current.CancellationToken));

        Assert.Contains("JUnit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("parse", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_xunit_whole_suite_rejects_an_exit_status_that_disagrees_with_the_artifact()
    {
        var runner = new ArtifactTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(exitCode: 0);
        runner.OnRun = command => WriteJunitArtifact(command, failed: true);
        var provider = new DotnetTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: Workspace(),
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:whole-suite:inconsistent",
                TestCaseIds: ["xunit:Sample.Tests.Fails"],
                WholeSuite: true),
            TestContext.Current.CancellationToken));

        Assert.Contains("exit code", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_xunit_whole_suite_rejects_a_non_test_failure_exit_with_a_failed_artifact()
    {
        var runner = new ArtifactTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(standardError: "testhost crashed", exitCode: 2, standardOutputTruncated: true);
        runner.OnRun = command => WriteJunitArtifact(command, failed: true);
        var provider = new DotnetTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: Workspace(),
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:whole-suite:crashed",
                TestCaseIds: ["xunit:Sample.Tests.Fails"],
                WholeSuite: true),
            TestContext.Current.CancellationToken));

        Assert.Contains("exit code 2", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_xunit_whole_suite_reports_a_rejected_invocation_diagnostic_without_an_artifact()
    {
        var runner = new ArtifactTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(standardError: "error: unknown option -noAutoReporters", exitCode: 2);
        var provider = new DotnetTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: Workspace(),
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:whole-suite:rejected",
                TestCaseIds: ["xunit:Sample.Tests.Passes"],
                WholeSuite: true),
            TestContext.Current.CancellationToken));

        Assert.Contains("unknown option -noAutoReporters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_xunit_rejects_a_selected_run_that_executed_no_cases()
    {
        var runner = new FakeTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(
            """
            {"$type":"test-assembly-starting","AssemblyUniqueID":"asm-1","StartTime":"2026-06-14T01:00:00Z"}
            {"$type":"test-assembly-finished","AssemblyUniqueID":"asm-1","TestsFailed":0,"TestsSkipped":0,"TestsTotal":0,"FinishTime":"2026-06-14T01:00:02Z"}
            """,
            exitCode: 0);
        runner.OnRun = WriteEmptyJunitArtifact;
        var provider = new DotnetTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: Workspace(),
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:xunit:noop",
                TestCaseIds: ["xunit:Sample.Tests.Passes"]),
            TestContext.Current.CancellationToken));

        Assert.Contains("xunit:Sample.Tests.Passes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_xunit_skip_reason_is_unchanged()
    {
        var runner = new FakeTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(
            """
            {"$type":"test-assembly-starting","AssemblyUniqueID":"asm-1","StartTime":"2026-06-14T01:00:00Z"}
            {"$type":"test-case-starting","AssemblyUniqueID":"asm-1","TestCaseUniqueID":"generation-hash-1","TestCaseDisplayName":"Sample.Tests.Skips"}
            {"$type":"test-skipped","TestCaseUniqueID":"generation-hash-1","TestUniqueID":"result-1","ExecutionTime":0.0,"FinishTime":"2026-06-14T01:00:01Z","Reason":"Requires network access"}
            {"$type":"test-assembly-finished","AssemblyUniqueID":"asm-1","TestsFailed":0,"TestsSkipped":1,"TestsTotal":1,"FinishTime":"2026-06-14T01:00:02Z"}
            """,
            exitCode: 0);
        runner.OnRun = WriteEmptyJunitArtifact;
        var provider = new DotnetTestProvider(runner);

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: Workspace(),
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:xunit:skipped",
                TestCaseIds: ["xunit:Sample.Tests.Skips"]),
            TestContext.Current.CancellationToken);

        Assert.Equal("skipped", result.Status);
        var caseResult = Assert.Single(result.CaseResults);
        Assert.Equal(IndexIdentity, caseResult.IndexIdentity);
        Assert.Equal("Requires network access", caseResult.FailureSummary);
    }

    [Fact]
    public async Task Run_for_nunit_uses_dotnet_test_trx_and_parses_run_result()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace("nunit");
        var generation = FirstGeneration(workspace);
        var targetPath = Path.Combine(generation.OutDir, "Sample.Tests", "Custom.Assembly.dll");
        runner.Enqueue();
        runner.Enqueue(targetPath);
        runner.Enqueue(exitCode: 1);
        runner.OnRun = command =>
        {
            if (command.Arguments.FirstOrDefault() == "build")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.WriteAllText(targetPath, string.Empty);
            }

            var logger = command.Arguments.FirstOrDefault(arg =>
                arg.StartsWith("trx;LogFileName=", StringComparison.Ordinal));
            if (logger is null)
                return;

            var resultsDirectory = command.Arguments[
                command.Arguments.ToList().IndexOf("--results-directory") + 1];
            var artifactPath = Path.Combine(resultsDirectory, logger["trx;LogFileName=".Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(
                artifactPath,
                """
                <?xml version="1.0" encoding="utf-8"?>
                <TestRun id="run-1" name="Sample" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Times start="2026-06-14T01:00:00.0000000Z" finish="2026-06-14T01:00:02.0000000Z" />
                  <Results>
                    <UnitTestResult executionId="exec-1" testId="test-def-1" testName="Adds" outcome="Failed" duration="00:00:00.1250000">
                      <Output>
                        <ErrorInfo>
                          <Message>Expected 2 to be 3</Message>
                        </ErrorInfo>
                      </Output>
                    </UnitTestResult>
                    <UnitTestResult executionId="exec-2" testId="test-def-2" testName="Cases(&quot;one.txt&quot;,Sample.Model)" outcome="Passed" duration="00:00:00.0250000" />
                  </Results>
                  <TestDefinitions>
                    <UnitTest name="Sample.Tests.CalculatorTests.Adds" id="test-def-1">
                      <TestMethod className="Sample.Tests.CalculatorTests" name="Adds" />
                    </UnitTest>
                    <UnitTest name="Sample.Tests.CalculatorTests.Cases(&quot;one.txt&quot;,Sample.Model)" id="test-def-2">
                      <TestMethod className="Sample.Tests.CalculatorTests" name="Cases(&quot;one.txt&quot;,Sample.Model)" />
                    </UnitTest>
                  </TestDefinitions>
                </TestRun>
                """);
        };
        var provider = new DotnetTestProvider(runner);

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:nunit",
                TestCaseIds:
                [
                    "nunit:Sample.Tests.CalculatorTests.Adds",
                    "nunit:Sample.Tests.CalculatorTests.Cases(\"one.txt\",Sample.Model)",
                ]),
            TestContext.Current.CancellationToken);

        Assert.Equal("run:nunit", result.RunId);
        Assert.Equal("failed", result.Status);
        Assert.Equal(generation.GenerationId, result.GenerationId);
        Assert.Equal(
            [
                "nunit:Sample.Tests.CalculatorTests.Adds",
                "nunit:Sample.Tests.CalculatorTests.Cases(\"one.txt\",Sample.Model)",
            ],
            result.CaseResults.Select(row => row.TestCaseId).ToArray());
        Assert.Equal(["failed", "passed"], result.CaseResults.Select(row => row.Status).ToArray());
        Assert.Equal("Expected 2 to be 3", result.CaseResults[0].FailureSummary);
        Assert.All(result.CaseResults, row => Assert.Equal(IndexIdentity, row.IndexIdentity));

        var runCommand = runner.Calls[2];
        Assert.Equal("test", runCommand.Arguments[0]);
        Assert.Equal(targetPath, runCommand.Arguments[1]);
        Assert.Equal(
            "FullyQualifiedName=Sample.Tests.CalculatorTests.Adds|FullyQualifiedName=Sample.Tests.CalculatorTests.Cases",
            FilterValue(runCommand.Arguments));
        AssertUsesGenerationTempDirectory(runCommand.Environment, generation);
    }

    [Fact]
    public async Task Sequential_runs_allocate_distinct_generation_directories()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace();
        for (var call = 0; call < 4; call++)
            runner.Enqueue(XunitPassedRun, exitCode: 0);
        runner.OnRun = WriteEmptyJunitArtifact;
        var provider = new DotnetTestProvider(runner);

        var first = await provider.RunAsync(Request(workspace), TestContext.Current.CancellationToken);
        var second = await provider.RunAsync(Request(workspace), TestContext.Current.CancellationToken);

        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 1), first.GenerationId);
        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 2), second.GenerationId);
        Assert.NotEqual(first.ResultArtifactPath, second.ResultArtifactPath);
        Assert.StartsWith(
            CtGenerationPaths.For(workspace, first.GenerationId!).ResultsDirectory,
            first.ResultArtifactPath!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_build_failure_includes_stdout_when_stderr_is_empty()
    {
        var runner = new FakeTestProcessRunner();
        runner.Enqueue(
            """
              Determining projects to restore...
            /repo/Sample.Tests.csproj : error NU1101: Unable to find package Private.Package.
            """,
            exitCode: 1);
        var provider = new DotnetTestProvider(runner);

        var ex = await Assert.ThrowsAsync<ContinuousTestProviderException>(
            () => provider.DiscoverAsync(Workspace(), TestContext.Current.CancellationToken));

        Assert.Contains("Unable to find package Private.Package", ex.Message);
    }

    [Fact]
    public void BuildDiscoverCommand_xunit_appends_trait_exclusions()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace(excludeTraits: ["Category=Scale"]);

        var command = provider.BuildDiscoverCommand(workspace);

        Assert.Equal(
            ["-list", "full/json", "-noLogo", "-noColor", "-preEnumerateTheories"],
            command.Arguments.Take(5).ToArray());
        AssertContainsAdjacentPair(command.Arguments, "-trait-", "Category=Scale");
    }

    [Fact]
    public void BuildRunCommand_uses_project_specific_generation_output()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace();
        var generation = FirstGeneration(workspace);

        var command = provider.BuildRunCommand(Request(workspace));

        Assert.Equal(
            Path.Combine(
                generation.OutDir,
                "Sample.Tests",
                "Sample.Tests" + ExecutableExtension()),
            command.FileName);
    }

    [Fact]
    public void BuildRunCommand_xunit_collapses_theory_cases_of_one_method_to_a_single_method_argument()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(new ContinuousTestProviderRunRequest(
            Workspace: Workspace(),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            TestCaseIds:
            [
                "xunit:Sample.Tests.Cases(value: 1)",
                "xunit:Sample.Tests.Cases(value: 2)",
                "xunit:Sample.Tests.Passes",
            ]));

        Assert.Equal(
            [
                "-noLogo", "-noColor", "-reporter", "json", "-preEnumerateTheories",
                "-method", "Sample.Tests.Cases",
                "-method", "Sample.Tests.Passes",
            ],
            command.Arguments);
    }

    [Fact]
    public void BuildRunCommand_xunit_passes_through_legacy_opaque_ids_as_id_selection()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(new ContinuousTestProviderRunRequest(
            Workspace: Workspace(),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            TestCaseIds: ["88c0d3ff", "xunit:Sample.Tests.Passes"]));

        AssertContainsAdjacentPair(command.Arguments, "-id", "88c0d3ff");
        AssertContainsAdjacentPair(command.Arguments, "-method", "Sample.Tests.Passes");
    }

    [Fact]
    public void BuildRunCommand_generic_filter_arguments_are_not_merged_with_exclusions()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace("mstest", excludeTraits: ["Category=Scale"]);

        var command = provider.BuildRunCommand(new ContinuousTestProviderRunRequest(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            FilterArguments: ["--filter", "Name=Adds"]));

        Assert.Equal("Name=Adds", FilterValue(command.Arguments));
        Assert.DoesNotContain(command.Arguments, arg => arg.Contains("TestCategory", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildRunCommand_mstest_composes_exclusion_filter()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace("mstest", excludeTraits: ["Category=Scale"]);

        var command = provider.BuildRunCommand(new ContinuousTestProviderRunRequest(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity));

        Assert.Equal("TestCategory!=Scale", FilterValue(command.Arguments));
    }

    /// <summary>
    /// An mstest DataRow/DynamicData display name keeps its parentheses in the selector — unlike nunit,
    /// which truncates at the first one — so the term must escape them. Bare parentheses inside a term
    /// are grouping to vstest, and the composed <c>(terms)&amp;exclusion</c> expression either fails to
    /// parse (a false red) or selects a different set than the caller asked for.
    /// </summary>
    [Fact]
    public void BuildRunCommand_mstest_escapes_filter_metacharacters_in_a_parameterized_display_name()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(new ContinuousTestProviderRunRequest(
            Workspace: Workspace("mstest"),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            TestCaseIds: ["mstest:Sample.Tests.CalculatorTests.Cases (1,2)"]));

        Assert.Equal(
            "FullyQualifiedName=Sample.Tests.CalculatorTests.Cases \\(1%2C2\\)",
            FilterValue(command.Arguments));
    }

    /// <summary>Trait values reach the exclusion clause verbatim, so they need the same escaping.</summary>
    [Fact]
    public void BuildRunCommand_mstest_escapes_filter_metacharacters_in_a_trait_exclusion()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace("mstest", excludeTraits: ["Category=Slow&Flaky (nightly)"]);

        var command = provider.BuildRunCommand(new ContinuousTestProviderRunRequest(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity));

        Assert.Equal("TestCategory!=Slow\\&Flaky \\(nightly\\)", FilterValue(command.Arguments));
    }

    /// <summary>
    /// nunit truncates a parameterized selector at the first parenthesis before the term is built, so
    /// escaping must leave that path byte-identical: nothing reserved survives the truncation.
    /// </summary>
    [Fact]
    public void BuildRunCommand_nunit_truncation_is_unchanged_by_escaping()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(new ContinuousTestProviderRunRequest(
            Workspace: Workspace("nunit"),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            TestCaseIds:
            [
                "nunit:Sample.Tests.CalculatorTests.Adds",
                "nunit:Sample.Tests.CalculatorTests.Cases(\"one.txt\",Sample.Model)",
            ]));

        Assert.Equal(
            "FullyQualifiedName=Sample.Tests.CalculatorTests.Adds|FullyQualifiedName=Sample.Tests.CalculatorTests.Cases",
            FilterValue(command.Arguments));
    }

    [Fact]
    public async Task Run_pertest_rejects_generic_frameworks()
    {
        var runner = new FakeTestProcessRunner();
        var provider = new DotnetTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: Workspace("nunit"),
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                CoverageMode: ContinuousTestCoverageMode.PerTest),
            TestContext.Current.CancellationToken));

        Assert.Contains("nunit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Run_pertest_rejects_a_generation_with_no_instrumentable_assemblies()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace();
        runner.Enqueue();
        var provider = new DotnetTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:coverage",
                TestCaseIds: ["xunit:Sample.Tests.Passes"],
                CoverageMode: ContinuousTestCoverageMode.PerTest),
            TestContext.Current.CancellationToken));

        Assert.Contains("instrumentable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FirstGeneration(workspace).GenerationId, exception.GenerationId);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Run_pertest_sets_miller_coverage_environment()
    {
        var runner = new CoverageTestProcessRunner();
        var workspace = Workspace();
        var generation = FirstGeneration(workspace);
        runner.Enqueue();
        runner.Enqueue();
        runner.Enqueue();
        runner.Enqueue();
        runner.Enqueue(XunitPassedRun);
        runner.Enqueue();
        runner.OnRun = command =>
        {
            if (command.Arguments.FirstOrDefault() == "build")
            {
                var projectOutput = Path.Combine(generation.OutDir, "Sample.Tests");
                Directory.CreateDirectory(projectOutput);
                File.WriteAllText(Path.Combine(projectOutput, "Sample.Tests.dll"), string.Empty);
                File.WriteAllText(Path.Combine(projectOutput, "Sample.Tests.pdb"), string.Empty);
                return;
            }

            if (command.Arguments.FirstOrDefault() == "snapshot")
            {
                var outputIndex = command.Arguments.ToList().IndexOf("-o");
                File.WriteAllText(command.Arguments[outputIndex + 1], string.Empty);
                return;
            }

            if (!command.Arguments.Contains("-reporter"))
                return;

            WriteEmptyJunitArtifact(command);
            var coverageDirectory = command.Environment["MILLER_CT_COVERAGE_DIR"]!;
            Directory.CreateDirectory(coverageDirectory);
            File.WriteAllText(Path.Combine(coverageDirectory, "session.coverage"), string.Empty);
        };
        var provider = new DotnetTestProvider(runner);

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:coverage",
                TestCaseIds: ["xunit:Sample.Tests.Passes"],
                CoverageMode: ContinuousTestCoverageMode.PerTest),
            TestContext.Current.CancellationToken);

        var run = runner.Calls.Single(call => call.Arguments.Contains("-reporter"));
        Assert.Equal($"miller-ct-{generation.GenerationId}", run.Environment["MILLER_CT_COVERAGE_SESSION"]);
        Assert.Equal("dotnet-coverage", run.Environment["MILLER_CT_COVERAGE_TOOL"]);
        Assert.StartsWith(generation.ResultsDirectory, run.Environment["MILLER_CT_COVERAGE_DIR"], StringComparison.Ordinal);
        Assert.Equal(generation.GenerationId, result.GenerationId);
        Assert.Contains("-parallel", run.Arguments);
        Assert.Equal(ProcessPriorityClass.BelowNormal, run.ProcessPriority);
    }

    [Fact]
    public void BuildProjectCommand_enables_project_specific_output_folders()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace();

        var command = provider.BuildProjectCommand(workspace);

        Assert.Contains("-p:GenerateProjectSpecificOutputFolder=true", command.Arguments);
    }

    [Fact]
    public void BuildProjectCommand_never_points_at_workspace_bin_or_obj()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace();

        var command = provider.BuildProjectCommand(workspace);
        var generation = CtGenerationPaths.ResolveLatestOrFirst(workspace);

        AssertUsesCtBuildIsolation(command.Arguments, workspace, generation);
        Assert.DoesNotContain("eros-ct", string.Join('\0', command.Arguments), StringComparison.Ordinal);
        Assert.DoesNotContain("EROS_", string.Join('\0', command.Environment.Keys), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Windows command-line cap

    /// <summary>
    /// Windows caps a process command line at 32,767 characters and does not truncate: Process.Start
    /// throws. Miller's own suite is ~6,000 xunit methods whose fully qualified names average ~100
    /// characters, so an unchunked selection builds a ~644 KB command line - 20x the cap - and the
    /// whole CT run dies with an opaque Win32 error instead of ever going green.
    /// </summary>
    private const int WindowsCommandLineCap = 32767;

    private static int CommandLineLength(TestProcessCommand command) =>
        command.FileName.Length + 1 + command.Arguments.Sum(argument => argument.Length + 1);

    private static IReadOnlyList<string> LongTestCaseIds(int count)
    {
        // Shaped like a real Miller test id: namespace + class + method, ~100 characters.
        const string Prefix = "xunit:Miller.Tests.Testing.Providers.Dotnet.DotnetTestProviderTests.";
        return Enumerable.Range(0, count)
            .Select(i => Prefix + "A_test_method_with_a_realistically_long_descriptive_name_" + i)
            .ToArray();
    }

    private static IReadOnlyList<string> SelectedMethods(TestProcessCommand command)
    {
        var methods = new List<string>();
        for (var i = 0; i < command.Arguments.Count - 1; i++)
        {
            if (command.Arguments[i] == "-method")
                methods.Add(command.Arguments[i + 1]);
        }

        return methods;
    }

    [Fact]
    public void BuildRunCommands_keeps_a_small_selection_in_a_single_invocation()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace();
        var request = new ContinuousTestProviderRunRequest(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            TestCaseIds: ["xunit:Sample.Tests.Passes", "xunit:Sample.Tests.Fails"]);

        var commands = provider.BuildRunCommands(request);

        // The literal pre-chunking argv, spelled out. Comparing against BuildRunCommand(request) would
        // compare the chunker with itself - that method IS BuildRunCommands(...)[0] - so any argv change
        // would move both sides together and the claim "byte-identical to before" would prove nothing.
        var single = Assert.Single(commands);
        Assert.Equal(
            Path.Combine(
                FirstGeneration(workspace).OutDir,
                "Sample.Tests",
                "Sample.Tests" + ExecutableExtension()),
            single.FileName);
        Assert.Equal(
            [
                "-noLogo", "-noColor", "-reporter", "json", "-preEnumerateTheories",
                "-method", "Sample.Tests.Passes",
                "-method", "Sample.Tests.Fails",
            ],
            single.Arguments);
    }

    [Fact]
    public void BuildRunCommands_splits_a_Miller_sized_selection_so_every_invocation_fits_the_windows_cap()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var commands = provider.BuildRunCommands(new ContinuousTestProviderRunRequest(
            Workspace: Workspace(),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            TestCaseIds: LongTestCaseIds(6047)));

        Assert.True(commands.Count > 1, "a 6,047-method selection cannot fit one Windows command line");
        foreach (var command in commands)
        {
            int length = CommandLineLength(command);
            Assert.True(
                length <= WindowsCommandLineCap,
                $"invocation joined to {length} chars, over the {WindowsCommandLineCap} Windows cap");
        }
    }

    [Fact]
    public void BuildRunCommands_selects_every_requested_method_exactly_once_across_invocations()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());
        var ids = LongTestCaseIds(1000);

        var commands = provider.BuildRunCommands(new ContinuousTestProviderRunRequest(
            Workspace: Workspace(),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            TestCaseIds: ids));

        var selected = commands.SelectMany(SelectedMethods).ToArray();
        // Nothing dropped, nothing duplicated, and never widened to an unfiltered superset.
        Assert.Equal(ids.Count, selected.Length);
        Assert.Equal(ids.Count, selected.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            ids.Select(id => id["xunit:".Length..]).OrderBy(m => m, StringComparer.Ordinal),
            selected.OrderBy(m => m, StringComparer.Ordinal));
    }

    [Fact]
    public void BuildRunCommands_repeats_trait_exclusions_on_every_invocation()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var commands = provider.BuildRunCommands(new ContinuousTestProviderRunRequest(
            Workspace: Workspace(),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            TestCaseIds: LongTestCaseIds(1000),
            ExcludeTraits: ["Category=Scale"]));

        Assert.True(commands.Count > 1);
        // A chunk that dropped the exclusion would run the Scale tests the request excluded.
        foreach (var command in commands)
            AssertContainsAdjacentPair(command.Arguments, "-trait-", "Category=Scale");
    }

    [Fact]
    public void BuildRunCommands_gives_each_invocation_its_own_result_artifact_path()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var commands = provider.BuildRunCommands(new ContinuousTestProviderRunRequest(
            Workspace: Workspace(),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:generation",
            TestCaseIds: LongTestCaseIds(1000)));

        Assert.True(commands.Count > 1);
        var artifacts = commands
            .Select(command => command.Arguments[command.Arguments.ToList().IndexOf("-jUnit") + 1])
            .ToArray();
        // Sharing one path would let the last invocation overwrite every earlier one's results.
        Assert.Equal(artifacts.Length, artifacts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void BuildRunCommands_never_splits_a_method_flag_from_its_value()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var commands = provider.BuildRunCommands(new ContinuousTestProviderRunRequest(
            Workspace: Workspace(),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            TestCaseIds: LongTestCaseIds(1000)));

        foreach (var command in commands)
        {
            // A trailing "-method" with no value would silently run the wrong set and still exit 0.
            Assert.NotEqual("-method", command.Arguments[^1]);
            Assert.NotEqual("-id", command.Arguments[^1]);
        }
    }

    // ------------------------------------------------- Windows command-line cap, mstest/nunit path

    /// <summary>
    /// The generic runner spends the selection in ONE argv element: a single conjunctive
    /// <c>--filter</c> expression holding every selected test. So unlike the xunit path, where each
    /// method costs its own pair of elements, a wide selection makes one argument carry the entire
    /// command line - a Miller-sized suite composes ~600 KB in that single string. Chunking by the
    /// NUMBER of selected tests cannot see that; the bound has to be the expression's byte length.
    /// </summary>
    private static IReadOnlyList<string> LongGenericTestCaseIds(int count, string framework = "mstest")
    {
        const string Prefix = "Miller.Tests.Testing.Providers.Dotnet.DotnetTestProviderTests.";
        return Enumerable.Range(0, count)
            .Select(i => $"{framework}:{Prefix}A_test_method_with_a_realistically_long_descriptive_name_{i}")
            .ToArray();
    }

    private static string? LoggerValue(IReadOnlyList<string> arguments)
    {
        var index = arguments.ToList().IndexOf("--logger");
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    /// <summary>The fully qualified names one invocation's <c>--filter</c> expression selects.</summary>
    private static IReadOnlyList<string> SelectedFilterTerms(TestProcessCommand command)
    {
        var filter = FilterValue(command.Arguments);
        if (filter is null)
            return [];

        // Unwrap the "(<selection>)&<exclusions>" form a chunk composes when exclusions ride along.
        var selection = filter;
        if (selection.StartsWith('('))
        {
            var wrapperEnd = selection.LastIndexOf(")&", StringComparison.Ordinal);
            if (wrapperEnd > 0)
                selection = selection[1..wrapperEnd];
        }

        return selection
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term[(term.IndexOf('=', StringComparison.Ordinal) + 1)..])
            .ToArray();
    }

    /// <summary>
    /// The TRX run key's hash, derived from the run id here rather than read back out of the provider,
    /// so the expected artifact name is an independent expectation and not the provider's own answer.
    /// </summary>
    private static string RunKeyHash(string runId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();

    [Fact]
    public void BuildRunCommands_generic_keeps_a_small_selection_in_a_single_invocation()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());
        var workspace = Workspace("mstest");
        var generation = FirstGeneration(workspace);
        var request = new ContinuousTestProviderRunRequest(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:mstest",
            TestCaseIds:
            [
                "mstest:Sample.Tests.CalculatorTests.Adds",
                "mstest:Sample.Tests.CalculatorTests.Subtracts",
            ]);

        var commands = provider.BuildRunCommands(request);

        // The literal pre-chunking argv, spelled out: same fixed flags, same filter string, same
        // unsuffixed TRX name. Comparing against BuildRunCommand(request) would compare the chunker with
        // itself - that method IS BuildRunCommands(...)[0].
        var single = Assert.Single(commands);
        Assert.Equal("dotnet", single.FileName);
        Assert.Equal(
            [
                "test",
                Path.Combine(generation.OutDir, "Sample.Tests", "Sample.Tests.dll"),
                "--nologo",
                "--results-directory",
                generation.ResultsDirectory,
                "--logger",
                $"trx;LogFileName=run-{RunKeyHash("run:mstest")}.trx",
                "--filter",
                "FullyQualifiedName=Sample.Tests.CalculatorTests.Adds|"
                + "FullyQualifiedName=Sample.Tests.CalculatorTests.Subtracts",
            ],
            single.Arguments);
        Assert.DoesNotContain(".part", LoggerValue(single.Arguments)!, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRunCommands_generic_spends_the_real_executable_command_line_budget()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var commands = provider.BuildRunCommands(new ContinuousTestProviderRunRequest(
            Workspace: Workspace("mstest"),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:mstest",
            TestCaseIds: LongGenericTestCaseIds(6047)));

        // `dotnet` is a real executable, never a .cmd shim, so the bound is the 32,767 Windows cap and
        // not the 8,191 cmd.exe cap the shared default holds back for shim-launched runners. On the
        // shim-sized budget this selection became ~155 invocations; vstest re-discovers the WHOLE
        // assembly on each one, so the run outlived the coordinator's 30-minute provider timeout and
        // every finished chunk's verdicts were thrown away.
        Assert.True(commands.Count > 1, "a 6,047-test filter expression cannot fit one command line");
        Assert.True(
            commands.Count <= 40,
            $"{commands.Count} invocations for 6,047 tests: the selection is still on the shim budget");

        foreach (var command in commands)
        {
            int length = CommandLineLength(command);
            Assert.True(
                length <= WindowsCommandLineCap,
                $"invocation joined to {length} chars, over the {WindowsCommandLineCap} Windows cap");
        }

        // And the budget is actually spent: an invocation well past the shim cap is the whole point.
        Assert.True(commands.Max(CommandLineLength) > 8191);
    }

    [Fact]
    public void BuildRunCommands_generic_splits_a_Miller_sized_selection_so_every_invocation_fits_the_windows_cap()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var commands = provider.BuildRunCommands(new ContinuousTestProviderRunRequest(
            Workspace: Workspace("mstest"),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:mstest",
            TestCaseIds: LongGenericTestCaseIds(6047)));

        Assert.True(commands.Count > 1, "a 6,047-test filter expression cannot fit one Windows command line");
        foreach (var command in commands)
        {
            // The whole command line, and the single --filter element inside it, both have to fit.
            int length = CommandLineLength(command);
            Assert.True(
                length <= WindowsCommandLineCap,
                $"invocation joined to {length} chars, over the {WindowsCommandLineCap} Windows cap");
            Assert.True(FilterValue(command.Arguments)!.Length <= WindowsCommandLineCap);
        }
    }

    [Fact]
    public void BuildRunCommands_generic_selects_every_requested_test_exactly_once_across_invocations()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());
        var ids = LongGenericTestCaseIds(1000, "nunit");

        var commands = provider.BuildRunCommands(new ContinuousTestProviderRunRequest(
            Workspace: Workspace("nunit"),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:nunit",
            TestCaseIds: ids));

        var selected = commands.SelectMany(SelectedFilterTerms).ToArray();
        // Nothing dropped, nothing duplicated, and never widened to an unfiltered superset.
        Assert.Equal(ids.Count, selected.Length);
        Assert.Equal(ids.Count, selected.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            ids.Select(id => id["nunit:".Length..]).OrderBy(m => m, StringComparer.Ordinal),
            selected.OrderBy(m => m, StringComparer.Ordinal));
    }

    [Fact]
    public void BuildRunCommands_generic_repeats_exclusions_on_every_invocation()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var commands = provider.BuildRunCommands(new ContinuousTestProviderRunRequest(
            Workspace: Workspace("mstest", excludeTraits: ["Category=Scale"]),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:mstest",
            TestCaseIds: LongGenericTestCaseIds(1000)));

        Assert.True(commands.Count > 1);
        // A chunk that dropped the exclusion would run the Scale tests the request excluded, and one
        // that dropped the parentheses would AND the exclusion onto the last selector only.
        foreach (var command in commands)
        {
            var filter = FilterValue(command.Arguments)!;
            Assert.StartsWith("(", filter, StringComparison.Ordinal);
            Assert.EndsWith(")&TestCategory!=Scale", filter, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildRunCommands_generic_gives_each_invocation_its_own_result_artifact_path()
    {
        var provider = new DotnetTestProvider(new FakeTestProcessRunner());

        var commands = provider.BuildRunCommands(new ContinuousTestProviderRunRequest(
            Workspace: Workspace("mstest"),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:mstest",
            TestCaseIds: LongGenericTestCaseIds(1000)));

        Assert.True(commands.Count > 1);
        var artifacts = commands.Select(command => LoggerValue(command.Arguments)!).ToArray();
        // Sharing one TRX name would let the last invocation overwrite every earlier one's results.
        Assert.Equal(artifacts.Length, artifacts.Distinct(StringComparer.Ordinal).Count());
        Assert.All(artifacts, artifact => Assert.Contains(".part", artifact, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_for_mstest_merges_chunked_invocations_into_one_verdict()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace("mstest");
        var generation = FirstGeneration(workspace);
        var targetPath = Path.Combine(generation.OutDir, "Sample.Tests", "Custom.Assembly.dll");
        var ids = LongGenericTestCaseIds(500);
        var failingTestName = ids[^1]["mstest:".Length..];
        runner.Enqueue();
        runner.Enqueue(targetPath);
        for (var invocation = 0; invocation < ids.Count; invocation++)
            runner.Enqueue();

        var trxCount = 0;
        runner.OnRun = command =>
        {
            if (command.Arguments.FirstOrDefault() == "build")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.WriteAllText(targetPath, string.Empty);
                return;
            }

            var logger = command.Arguments.FirstOrDefault(arg =>
                arg.StartsWith("trx;LogFileName=", StringComparison.Ordinal));
            if (logger is null)
                return;

            var resultsDirectory = command.Arguments[
                command.Arguments.ToList().IndexOf("--results-directory") + 1];
            var artifactPath = Path.Combine(resultsDirectory, logger["trx;LogFileName=".Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(
                artifactPath,
                TrxDocument(SelectedFilterTerms(command), $"chunk{trxCount++}", failingTestName));
        };
        var provider = new DotnetTestProvider(runner);

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:mstest",
                TestCaseIds: ids),
            TestContext.Current.CancellationToken);

        Assert.True(trxCount > 1, "a 500-test filter expression must be split across invocations");
        // Every selected test executed exactly once, and the red chunk is not masked by its green
        // siblings - a chunked run is ONE logical run.
        Assert.Equal(ids.Count, result.CaseResults.Count);
        Assert.Equal(
            ids.OrderBy(id => id, StringComparer.Ordinal),
            result.CaseResults.Select(row => row.TestCaseId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal("failed", result.Status);
        Assert.Equal("run:mstest", result.RunId);
        Assert.Equal(generation.GenerationId, result.GenerationId);
        Assert.EndsWith(".part000.trx", result.ResultArtifactPath!, StringComparison.Ordinal);

        // Each part kept its own artifact, so no invocation overwrote another's results.
        var artifacts = result.CaseResults
            .Select(row => row.Metadata["artifact_path"]?.ToString()!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(trxCount, artifacts.Length);
    }

    private static string TrxDocument(
        IReadOnlyList<string> testNames,
        string executionPrefix,
        string failingTestName)
    {
        var rows = string.Concat(testNames.Select((name, index) =>
        {
            var failed = string.Equals(name, failingTestName, StringComparison.Ordinal);
            var body = failed
                ? "<Output><ErrorInfo><Message>chunked failure</Message></ErrorInfo></Output>"
                : string.Empty;
            return $"""
                <UnitTestResult executionId="{executionPrefix}-{index}" testName="{name}" outcome="{(failed ? "Failed" : "Passed")}" duration="00:00:00.0100000">{body}</UnitTestResult>
                """;
        }));

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="{executionPrefix}" name="Sample" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Times start="2026-06-14T01:00:00.0000000Z" finish="2026-06-14T01:00:02.0000000Z" />
              <Results>{rows}</Results>
            </TestRun>
            """;
    }

    /// <summary>The isolated build writes the assembly the generic run then targets.</summary>
    private static void WriteGenericBuildTarget(TestProcessCommand command, string targetPath)
    {
        if (command.Arguments.FirstOrDefault() != "build")
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, string.Empty);
    }

    /// <summary>The TRX path one invocation's <c>--logger</c> names, or null for a non-run command.</summary>
    private static string? TrxArtifactPath(TestProcessCommand command)
    {
        var logger = command.Arguments.FirstOrDefault(argument =>
            argument.StartsWith("trx;LogFileName=", StringComparison.Ordinal));
        if (logger is null)
            return null;

        var resultsDirectory = command.Arguments[
            command.Arguments.ToList().IndexOf("--results-directory") + 1];
        return Path.Combine(resultsDirectory, logger["trx;LogFileName=".Length..]);
    }

    private static bool IsPart(string artifactPath, int part) =>
        Path.GetFileName(artifactPath).Contains($".part{part:D3}.", StringComparison.Ordinal);

    [Fact]
    public async Task Run_for_mstest_keeps_sibling_verdicts_when_one_chunk_writes_no_trx()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace("mstest");
        var targetPath = Path.Combine(FirstGeneration(workspace).OutDir, "Sample.Tests", "Custom.Assembly.dll");
        var ids = LongGenericTestCaseIds(500);
        runner.Enqueue();
        runner.Enqueue(targetPath);

        // Chunk 000 dies before the TRX logger writes - a killed testhost, or Defender still holding the
        // freshly built assembly. Every later chunk runs normally.
        runner.Enqueue(standardError: "testhost process crashed", exitCode: 1);
        for (var invocation = 0; invocation < ids.Count; invocation++)
            runner.Enqueue();

        var parts = 0;
        runner.OnRun = command =>
        {
            WriteGenericBuildTarget(command, targetPath);
            if (TrxArtifactPath(command) is not { } artifactPath || IsPart(artifactPath, 0))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(
                artifactPath,
                TrxDocument(SelectedFilterTerms(command), $"chunk{parts++}", failingTestName: string.Empty));
        };
        var provider = new DotnetTestProvider(runner);

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:mstest",
                TestCaseIds: ids),
            TestContext.Current.CancellationToken);

        Assert.True(
            runner.Calls.Count(call => TrxArtifactPath(call) is not null) > 1,
            "a 500-test filter expression must be split across invocations");
        Assert.True(parts > 0, "the siblings of the dead chunk must still run");
        var deadIds = SelectedFilterTerms(runner.Calls.Single(call =>
                TrxArtifactPath(call) is { } path && IsPart(path, 0)))
            .Select(name => "mstest:" + name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(deadIds);

        // Nothing is lost: the dead chunk answers for its OWN ids and every sibling keeps the verdict it
        // earned. Failing the whole run discarded the parts already on disk AND skipped the parts not
        // yet started, and the retry reproduced it forever.
        Assert.Equal(ids.Count, result.CaseResults.Count);
        Assert.Equal(
            ids.OrderBy(id => id, StringComparer.Ordinal),
            result.CaseResults.Select(row => row.TestCaseId).OrderBy(id => id, StringComparer.Ordinal));

        var byTestCaseId = result.CaseResults.ToDictionary(row => row.TestCaseId, StringComparer.Ordinal);
        foreach (var deadId in deadIds)
        {
            // A chunk that never ran must never read as "no failures".
            Assert.Equal("failed", byTestCaseId[deadId].Status);
            Assert.Contains("exit code 1", byTestCaseId[deadId].FailureSummary!, StringComparison.Ordinal);
            Assert.Contains(
                "testhost process crashed",
                byTestCaseId[deadId].FailureSummary!,
                StringComparison.Ordinal);
        }

        Assert.All(
            ids.Where(id => !deadIds.Contains(id)),
            id => Assert.Equal("passed", byTestCaseId[id].Status));
        Assert.Equal("failed", result.Status);

        // The artifact the run reports is one that exists: part 000 never wrote its file.
        Assert.EndsWith(".part001.trx", result.ResultArtifactPath!, StringComparison.Ordinal);
        Assert.True(File.Exists(result.ResultArtifactPath));
    }

    [Fact]
    public async Task Run_for_mstest_keeps_sibling_verdicts_when_one_chunk_matches_no_tests()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace("mstest");
        var targetPath = Path.Combine(FirstGeneration(workspace).OutDir, "Sample.Tests", "Custom.Assembly.dll");
        var ids = LongGenericTestCaseIds(500);
        runner.Enqueue();
        runner.Enqueue(targetPath);
        for (var invocation = 0; invocation < ids.Count; invocation++)
            runner.Enqueue();

        var parts = 0;
        runner.OnRun = command =>
        {
            WriteGenericBuildTarget(command, targetPath);
            if (TrxArtifactPath(command) is not { } artifactPath)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);

            // Chunk 000's filter matches nothing - the user just renamed that class - so vstest writes a
            // TRX with zero UnitTestResult rows.
            File.WriteAllText(
                artifactPath,
                IsPart(artifactPath, 0)
                    ? TrxDocument([], "empty", failingTestName: string.Empty)
                    : TrxDocument(SelectedFilterTerms(command), $"chunk{parts++}", failingTestName: string.Empty));
        };
        var provider = new DotnetTestProvider(runner);

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:mstest",
                TestCaseIds: ids),
            TestContext.Current.CancellationToken);

        Assert.True(
            runner.Calls.Count(call => TrxArtifactPath(call) is not null) > 1,
            "a 500-test filter expression must be split across invocations");
        Assert.True(parts > 0, "the siblings of the empty chunk must still be parsed");
        var unmatchedIds = SelectedFilterTerms(runner.Calls.Single(call =>
                TrxArtifactPath(call) is { } path && IsPart(path, 0)))
            .Select(name => "mstest:" + name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(unmatchedIds);

        // The empty part's ids stay UNREPORTED - the store flips an unreported id to stale when the run
        // completes, exactly as an unchunked run left an unmatched id stale - and every sibling keeps its
        // verdict instead of the whole run failing on one empty part.
        var reported = result.CaseResults.Select(row => row.TestCaseId).ToHashSet(StringComparer.Ordinal);
        Assert.All(unmatchedIds, id => Assert.DoesNotContain(id, reported));
        Assert.Equal(ids.Count - unmatchedIds.Count, result.CaseResults.Count);
        Assert.All(ids.Where(id => !unmatchedIds.Contains(id)), id => Assert.Contains(id, reported));
        Assert.Equal("passed", result.Status);
    }

    [Fact]
    public async Task Run_for_mstest_fails_the_run_when_no_invocation_produced_a_trx()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace("mstest");
        var targetPath = Path.Combine(FirstGeneration(workspace).OutDir, "Sample.Tests", "Custom.Assembly.dll");
        runner.Enqueue();
        runner.Enqueue(targetPath);
        runner.Enqueue(standardError: "Sample.Tests.csproj : error MSB1009: Project file does not exist.", exitCode: 1);
        runner.OnRun = command => WriteGenericBuildTarget(command, targetPath);
        var provider = new DotnetTestProvider(runner);

        // Nothing is selected, so no test case id can carry the failure: the run itself produced no
        // verdict at all and must still be heard. Chunk-local reporting must not swallow that.
        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:mstest"),
            TestContext.Current.CancellationToken));

        Assert.Contains("exit code 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MSB1009", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- chunked xunit merge, end to end

    /// <summary>
    /// One chunk's xunit v3 <c>-reporter json</c> stream, carrying exactly the methods that chunk
    /// selected. The merge is only observable when each invocation answers for its own slice.
    /// </summary>
    private static string XunitRunJson(
        int chunk,
        IReadOnlyList<string> methods,
        IReadOnlyCollection<string> failed,
        IReadOnlyCollection<string> skipped)
    {
        var lines = new List<string>
        {
            $$"""{"$type":"test-assembly-starting","AssemblyUniqueID":"asm-{{chunk}}","StartTime":"2026-06-14T01:00:00Z"}""",
        };

        for (var index = 0; index < methods.Count; index++)
        {
            var method = methods[index];
            var outcome = failed.Contains(method) ? "test-failed"
                : skipped.Contains(method) ? "test-skipped"
                : "test-passed";
            lines.Add(
                $$"""{"$type":"test-case-starting","AssemblyUniqueID":"asm-{{chunk}}","TestCaseUniqueID":"case-{{chunk}}-{{index}}","TestCaseDisplayName":"{{method}}"}""");
            lines.Add(
                $$"""{"$type":"{{outcome}}","TestCaseUniqueID":"case-{{chunk}}-{{index}}","TestUniqueID":"result-{{chunk}}-{{index}}","ExecutionTime":0.01,"FinishTime":"2026-06-14T01:00:01Z"}""");
        }

        lines.Add(
            $$"""{"$type":"test-assembly-finished","AssemblyUniqueID":"asm-{{chunk}}","TestsFailed":{{failed.Count}},"TestsSkipped":{{skipped.Count}},"TestsTotal":{{methods.Count}},"FinishTime":"2026-06-14T01:00:02Z"}""");
        return string.Join("\n", lines);
    }

    [Fact]
    public async Task Run_for_xunit_merges_chunked_invocations_and_one_red_chunk_fails_the_run()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace();
        var ids = LongTestCaseIds(200);
        var request = new ContinuousTestProviderRunRequest(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:xunit:chunked",
            TestCaseIds: ids);
        var provider = new DotnetTestProvider(runner);

        // The chunk boundaries are a pure function of the selection, so the invocations the preview seam
        // reports are the ones RunAsync makes: one queued stdout per chunk, carrying exactly the methods
        // THAT chunk selected.
        var chunks = provider.BuildRunCommands(request).Select(SelectedMethods).ToArray();
        Assert.True(chunks.Length > 1, "a 200-method selection cannot fit one command line");

        runner.Enqueue();
        for (var chunk = 0; chunk < chunks.Length; chunk++)
        {
            // The one red test sits in the FIRST chunk, so a green sibling must not mask it.
            IReadOnlyCollection<string> failed = chunk == 0 ? [chunks[0][^1]] : [];
            runner.Enqueue(XunitRunJson(chunk, chunks[chunk], failed, skipped: []));
        }

        runner.OnRun = WriteEmptyJunitArtifact;

        var result = await provider.RunAsync(request, TestContext.Current.CancellationToken);

        // Every selected method executed exactly once across the chunks, and the merge keeps them all.
        Assert.Equal(ids.Count, result.CaseResults.Count);
        Assert.Equal(
            ids.OrderBy(id => id, StringComparer.Ordinal),
            result.CaseResults.Select(row => row.TestCaseId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal("failed", result.Status);
        Assert.Equal("run:xunit:chunked", result.RunId);
        Assert.Equal(1, result.CaseResults.Count(row => row.Status == "failed"));
        Assert.EndsWith(".part000.junit.xml", result.ResultArtifactPath!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_for_xunit_reports_bounded_progress_for_every_chunk()
    {
        var runner = new FakeTestProcessRunner();
        var workspace = Workspace();
        var ids = LongTestCaseIds(200);
        var progress = new List<ContinuousTestProviderChunkProgress>();
        var request = new ContinuousTestProviderRunRequest(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:xunit:progress",
            TestCaseIds: ids,
            Progress: progress.Add);
        var provider = new DotnetTestProvider(runner);
        var chunks = provider.BuildRunCommands(request).Select(SelectedMethods).ToArray();
        Assert.True(chunks.Length > 1, "a 200-method selection cannot fit one command line");

        runner.Enqueue();
        for (var chunk = 0; chunk < chunks.Length; chunk++)
            runner.Enqueue(XunitRunJson(chunk, chunks[chunk], failed: [], skipped: []));
        runner.OnRun = WriteEmptyJunitArtifact;

        await provider.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(chunks.Length, progress.Count);
        Assert.Equal(Enumerable.Range(1, chunks.Length), progress.Select(static row => row.CurrentPart));
        Assert.All(progress, row => Assert.Equal(chunks.Length, row.ChunkCount));
        Assert.Equal(chunks.Sum(static chunk => chunk.Count), progress.Sum(static row => row.CurrentPartUnitCount));
        Assert.Equal(ids.Count, progress[0].RequestedUniqueUnitCount);
        Assert.True(progress[0].NamesTruncated);
        Assert.False(string.IsNullOrWhiteSpace(progress[0].NameDigest));
    }

    [Fact]
    public async Task Run_for_xunit_does_not_report_a_whole_run_skipped_for_one_all_skipped_chunk()
    {
        var runner = new FakeTestProcessRunner();
        var ids = LongTestCaseIds(200);
        var request = new ContinuousTestProviderRunRequest(
            Workspace: Workspace(),
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:xunit:skipped-chunk",
            TestCaseIds: ids);
        var provider = new DotnetTestProvider(runner);

        var chunks = provider.BuildRunCommands(request).Select(SelectedMethods).ToArray();
        Assert.True(chunks.Length > 1, "a 200-method selection cannot fit one command line");

        runner.Enqueue();
        for (var chunk = 0; chunk < chunks.Length; chunk++)
        {
            IReadOnlyCollection<string> allSkipped = chunk == 0 ? chunks[0] : [];
            runner.Enqueue(XunitRunJson(chunk, chunks[chunk], failed: [], skipped: allSkipped));
        }

        runner.OnRun = WriteEmptyJunitArtifact;

        var result = await provider.RunAsync(request, TestContext.Current.CancellationToken);

        // One chunk of nothing but skipped methods is a chunking artefact, not a verdict about the run:
        // the same selection run unchunked reported "passed". Only a run whose every part was skipped is
        // a skipped run.
        Assert.Equal("passed", result.Status);
        Assert.Equal(ids.Count, result.CaseResults.Count);
        Assert.Equal(chunks[0].Count, result.CaseResults.Count(row => row.Status == "skipped"));
    }

    // ------------------------------------------------------ cleanup deletes on the failure path

    [Fact]
    public void TryDeleteWithRetry_never_lets_a_delete_failure_escape_a_finally_block()
    {
        var path = Path.Combine(_dir, "held.coverage");
        File.WriteAllText(path, string.Empty);
        var attempts = 0;
        var waits = new List<TimeSpan>();

        DotnetTestProvider.TryDeleteWithRetry(
            path,
            _ =>
            {
                attempts++;
                throw new IOException("the file is open in another process");
            },
            waits.Add);

        // The delete is retried - the race it loses closes in tens of milliseconds - but a delete that
        // never wins is swallowed, because this call sits in a finally block behind a real failure.
        Assert.True(attempts > 1, "a delete that loses a sharing race must be retried");
        Assert.True(
            waits.Aggregate(TimeSpan.Zero, (total, wait) => total + wait) <= TimeSpan.FromMilliseconds(500),
            "cleanup must not stall the real failure it is running behind");
    }

    [Fact]
    public void DeleteWithRetry_still_throws_when_the_caller_needs_the_file_gone()
    {
        var path = Path.Combine(_dir, "stale.diag.log");
        File.WriteAllText(path, string.Empty);

        // Only the finally-block wrapper swallows. A delete whose failure changes what the next command
        // reads - a stale discovery diagnostic, say - must still be heard.
        Assert.Throws<IOException>(() => DotnetTestProvider.DeleteWithRetry(
            path,
            _ => throw new IOException("the file is open in another process"),
            _ => { }));
    }

    [Fact]
    public async Task Run_pertest_readiness_failure_is_not_replaced_by_the_cleanup_delete()
    {
        var runner = new CoverageTestProcessRunner();
        var workspace = Workspace();
        var generation = FirstGeneration(workspace);
        runner.Enqueue();
        runner.Enqueue();
        runner.Enqueue();
        runner.Enqueue(standardError: "collector is not listening", exitCode: 1);

        runner.OnRun = command =>
        {
            if (command.Arguments.FirstOrDefault() == "build")
            {
                var projectOutput = Path.Combine(generation.OutDir, "Sample.Tests");
                Directory.CreateDirectory(projectOutput);
                File.WriteAllText(Path.Combine(projectOutput, "Sample.Tests.dll"), string.Empty);
                File.WriteAllText(Path.Combine(projectOutput, "Sample.Tests.pdb"), string.Empty);
                return;
            }

            if (command.Arguments.FirstOrDefault() != "snapshot")
                return;

            // The readiness snapshot lands on disk, so the cleanup delete behind the readiness failure
            // has a real file to fail on.
            File.WriteAllText(command.Arguments[command.Arguments.ToList().IndexOf("-o") + 1], string.Empty);
        };

        // The delete is failed through the injected seam, NOT through a held file handle: on Linux and
        // macOS FileShare is advisory and unlink ignores it, so a real lock makes the delete fail on
        // Windows only - and this test then proved nothing on the platform the default CI job runs.
        var readinessDeletes = 0;
        var provider = new DotnetTestProvider(
            runner,
            "dotnet",
            "dotnet-coverage",
            TimeSpan.FromSeconds(10),
            deleteFile: path =>
            {
                if (!path.EndsWith("readiness.coverage", StringComparison.Ordinal))
                {
                    File.Delete(path);
                    return;
                }

                readinessDeletes++;
                throw new IOException("the file is open in another process");
            },
            deleteRetrySleep: _ => { });

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:coverage",
                TestCaseIds: ["xunit:Sample.Tests.Passes"],
                CoverageMode: ContinuousTestCoverageMode.PerTest),
            TestContext.Current.CancellationToken));

        // The cleanup delete really ran and really failed, so a swallow is what kept it out of the way.
        Assert.True(readinessDeletes > 0, "the cleanup delete never ran, so nothing was swallowed");

        // The coverage failure the caller needs, not an IOException about the temp file behind it.
        Assert.Contains("readiness failed with exit code 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("collector is not listening", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// CT runs the built test executable, not <c>dotnet test</c>, so nothing applied a project's
    /// <c>RunSettingsFilePath</c> environment block and every test that needed one failed. CT's own
    /// variables still win: TMP/TEMP/TMPDIR are a containment guarantee, and the daemon's workspace root
    /// must be REMOVED rather than inherited, or a `miller` CLI verb run inside a test binds the daemon's
    /// workspace instead of the test's own root.
    /// </summary>
    [Fact]
    public void Discover_command_environment_applies_run_settings_and_drops_the_daemon_variable()
    {
        var workspace = Workspace();
        var projectDirectory = Path.GetDirectoryName(workspace.ProjectPath)!;
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            workspace.ProjectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<RunSettingsFilePath>$(MSBuildProjectDirectory)/test.runsettings</RunSettingsFilePath>"
            + "</PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(projectDirectory, "test.runsettings"),
            "<RunSettings><RunConfiguration><EnvironmentVariables>"
            + "<MILLER_INDEX_STORE>off</MILLER_INDEX_STORE>"
            + "<TEMP>the-project-must-not-win</TEMP>"
            + "</EnvironmentVariables></RunConfiguration></RunSettings>");

        var command = new DotnetTestProvider(new FakeTestProcessRunner()).BuildDiscoverCommand(workspace);

        Assert.Equal("off", command.Environment["MILLER_INDEX_STORE"]);
        Assert.NotEqual("the-project-must-not-win", command.Environment["TEMP"]);
        Assert.Equal(workspace.WorkspaceRoot, command.Environment[CtEnvironment.WorkspaceRoot]);
        Assert.True(command.Environment.TryGetValue(CtEnvironment.DaemonWorkspaceRoot, out var daemonRoot));
        Assert.Null(daemonRoot);
    }

    /// <summary>
    /// A run the stall guard killed must never be recorded "passed". The xUnit parser takes the run status
    /// from the <c>test-assembly-finished</c> event, which a killed child never emits, so a wedged assembly
    /// whose executed tests all passed was stored as a clean run. CLAUDE.md is explicit that green needs
    /// COMPLETE results at the selected key.
    /// </summary>
    [Fact]
    public async Task Run_for_a_killed_assembly_is_failed_even_though_every_executed_test_passed()
    {
        var runner = new FakeTestProcessRunner();
        runner.Enqueue();
        runner.Enqueue(XunitKilledRun, exitCode: -4109);
        runner.OnRun = WriteEmptyJunitArtifact;
        var provider = new DotnetTestProvider(runner);
        var workspace = Workspace();

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: IndexIdentity,
                RunId: "run:killed",
                TestCaseIds: ["xunit:Sample.Tests.Passes"]),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.Equal("passed", Assert.Single(result.CaseResults).Status);
    }

    private ContinuousTestWorkspace Workspace(
        string? framework = null,
        IReadOnlyList<string>? excludeTraits = null)
    {
        var workspaceRoot = Path.Combine(_dir, "repo");
        var projectPath = Path.Combine(workspaceRoot, "tests", "Sample.Tests", "Sample.Tests.csproj");
        var ctBuildRoot = Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build");
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:1",
            WorkspaceRoot: workspaceRoot,
            ProjectPath: projectPath,
            BuildOutputRoot: ctBuildRoot,
            Framework: framework,
            ExcludeTraits: excludeTraits);
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        return workspace;
    }

    private static ContinuousTestProviderRunRequest Request(ContinuousTestWorkspace workspace) =>
        Request(workspace, ["xunit:Sample.Tests.Passes"]);

    private static ContinuousTestProviderRunRequest Request(
        ContinuousTestWorkspace workspace,
        IReadOnlyList<string> testCaseIds) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:generation",
            TestCaseIds: testCaseIds);

    private static void WriteEmptyJunitArtifact(TestProcessCommand command)
    {
        var artifactFlag = command.Arguments.ToList().IndexOf("-jUnit");
        if (artifactFlag < 0)
            return;

        var artifactPath = command.Arguments[artifactFlag + 1];
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(artifactPath, "<testsuite />");
    }

    private static void WriteJunitArtifact(TestProcessCommand command, bool failed)
    {
        var artifactFlag = command.Arguments.ToList().IndexOf("-jUnit");
        if (artifactFlag < 0)
            return;

        var artifactPath = command.Arguments[artifactFlag + 1];
        var testcase = failed
            ? "<testcase classname=\"Sample.Tests\" name=\"Fails\"><failure message=\"boom\">boom</failure></testcase>"
            : "<testcase classname=\"Sample.Tests\" name=\"Passes\" time=\"0.1\" />";
        WriteArtifact(command, $"<testsuite name=\"xunit\">{testcase}</testsuite>");
    }

    private static void WriteArtifact(TestProcessCommand command, string content)
    {
        var artifactFlag = command.Arguments.ToList().IndexOf("-jUnit");
        if (artifactFlag < 0)
            return;

        var artifactPath = command.Arguments[artifactFlag + 1];
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(artifactPath, content);
    }

    private static void AssertContainsAdjacentPair(
        IReadOnlyList<string> arguments,
        string flag,
        string value)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] == flag && arguments[i + 1] == value)
                return;
        }

        Assert.Fail($"Expected adjacent pair [{flag}, {value}] in [{string.Join(", ", arguments)}]");
    }

    private static string? FilterValue(IReadOnlyList<string> arguments)
    {
        var index = arguments.ToList().IndexOf("--filter");
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    private static void AssertUsesCtBuildIsolation(
        IReadOnlyList<string> arguments,
        ContinuousTestWorkspace workspace,
        CtGenerationPaths generation)
    {
        var buildRoot = workspace.BuildOutputRoot;

        Assert.Contains("--disable-build-servers", arguments);
        Assert.Contains("-nr:false", arguments);
        Assert.Contains("--artifacts-path", arguments);
        Assert.Contains(buildRoot, arguments);
        Assert.Contains("-p:GenerateProjectSpecificOutputFolder=true", arguments);
        Assert.Contains($"-p:OutDir={generation.OutDir}", arguments);
        Assert.Contains($"-p:ResultsDirectory={generation.ResultsDirectory}", arguments);
        Assert.Contains($"-bl:{generation.BinlogPath};ProjectImports=None", arguments);

        var repoBin = Path.Combine(workspace.WorkspaceRoot, "bin");
        var repoObj = Path.Combine(workspace.WorkspaceRoot, "obj");
        var repoTestResults = Path.Combine(workspace.WorkspaceRoot, "TestResults");
        Assert.DoesNotContain(arguments, arg => arg.Contains(repoBin, StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, arg => arg.Contains(repoObj, StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, arg => arg.Contains(repoTestResults, StringComparison.Ordinal));
    }

    private static void AssertUsesGenerationTempDirectory(
        IReadOnlyDictionary<string, string?> environment,
        CtGenerationPaths generation)
    {
        Assert.Equal(generation.TempDirectory, environment["TMPDIR"]);
        Assert.Equal(generation.TempDirectory, environment["TMP"]);
        Assert.Equal(generation.TempDirectory, environment["TEMP"]);
        Assert.True(Directory.Exists(generation.TempDirectory));
    }

    private static CtGenerationPaths FirstGeneration(ContinuousTestWorkspace workspace) =>
        CtGenerationPaths.For(workspace, CtGenerationPaths.IdForOrdinal(workspace, 1));

    private static IReadOnlyList<string> GenerationDirectories(ContinuousTestWorkspace workspace) =>
        Directory.Exists(workspace.BuildOutputRoot)
            ? Directory.GetDirectories(workspace.BuildOutputRoot)
                .Select(Path.GetFileName)
                .Where(name => name is not null && CtGenerationPaths.IsGenerationId(name))
                .Select(name => name!)
                .ToArray()
            : [];

    private static string ExecutableExtension() => OperatingSystem.IsWindows() ? ".exe" : "";

    /// <summary>
    /// What the stall guard leaves behind: the assembly started, cases reported, and then the process tree
    /// was killed, so there is no <c>test-assembly-finished</c> line and the exit code is non-zero.
    /// </summary>
    private const string XunitKilledRun =
        """
        {"$type":"test-assembly-starting","AssemblyUniqueID":"asm-1","StartTime":"2026-06-14T01:00:00Z"}
        {"$type":"test-case-starting","AssemblyUniqueID":"asm-1","TestCaseUniqueID":"generation-hash-1","TestCaseDisplayName":"Sample.Tests.Passes"}
        {"$type":"test-passed","TestCaseUniqueID":"generation-hash-1","TestUniqueID":"result-1","ExecutionTime":0.125,"FinishTime":"2026-06-14T01:00:01Z"}
        """;

    /// <summary>
    /// What a PRE-ENUMERATED theory looks like on the wire: one test-case per row, each with the row's
    /// arguments in its display name and its own TestCaseUniqueID. Without the flag there is a single
    /// test-case named "Sample.Tests.Cases" and both rows report under it.
    /// </summary>
    private const string XunitPreEnumeratedTheoryRun =
        """
        {"$type":"test-assembly-starting","AssemblyUniqueID":"asm-1","StartTime":"2026-06-14T01:00:00Z"}
        {"$type":"test-case-starting","AssemblyUniqueID":"asm-1","TestCaseUniqueID":"row-1","TestCaseDisplayName":"Sample.Tests.Cases(value: 1)"}
        {"$type":"test-passed","TestCaseUniqueID":"row-1","TestUniqueID":"t-1","ExecutionTime":0.1,"FinishTime":"2026-06-14T01:00:01Z"}
        {"$type":"test-case-starting","AssemblyUniqueID":"asm-1","TestCaseUniqueID":"row-2","TestCaseDisplayName":"Sample.Tests.Cases(value: 2)"}
        {"$type":"test-passed","TestCaseUniqueID":"row-2","TestUniqueID":"t-2","ExecutionTime":0.1,"FinishTime":"2026-06-14T01:00:01Z"}
        {"$type":"test-assembly-finished","AssemblyUniqueID":"asm-1","FinishTime":"2026-06-14T01:00:02Z","TestsFailed":0,"TestsTotal":2}
        """;

    private const string XunitPassedRun =
        """
        {"$type":"test-assembly-starting","AssemblyUniqueID":"asm-1","StartTime":"2026-06-14T01:00:00Z"}
        {"$type":"test-case-starting","AssemblyUniqueID":"asm-1","TestCaseUniqueID":"generation-hash-1","TestCaseDisplayName":"Sample.Tests.Passes"}
        {"$type":"test-passed","TestCaseUniqueID":"generation-hash-1","TestUniqueID":"result-1","ExecutionTime":0.125,"FinishTime":"2026-06-14T01:00:01Z"}
        {"$type":"test-assembly-finished","AssemblyUniqueID":"asm-1","TestsFailed":0,"TestsSkipped":0,"TestsTotal":1,"FinishTime":"2026-06-14T01:00:02Z"}
        """;

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

    private sealed class CoverageTestProcessRunner : ITestProcessRunner, ITestBackgroundProcessRunner
    {
        private readonly Queue<TestProcessResult> _results = new();

        public List<TestProcessCommand> Calls { get; } = [];

        public Action<TestProcessCommand>? OnRun { get; set; }

        public void Enqueue(string standardOutput = "", string standardError = "", int exitCode = 0) =>
            _results.Enqueue(new TestProcessResult(exitCode, standardOutput, standardError));

        public Task<TestProcessResult> RunAsync(
            TestProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(command);
            OnRun?.Invoke(command);
            if (_results.Count == 0)
                throw new InvalidOperationException("No fake result was queued.");
            return Task.FromResult(_results.Dequeue());
        }

        public ITestBackgroundProcess Start(TestProcessCommand command)
        {
            Calls.Add(command);
            OnRun?.Invoke(command);
            var result = _results.Count == 0
                ? new TestProcessResult(0, string.Empty, string.Empty)
                : _results.Dequeue();
            return new CompletedBackgroundProcess(result);
        }

        private sealed class CompletedBackgroundProcess(TestProcessResult result) : ITestBackgroundProcess
        {
            public int ProcessId => 1;

            // Already exited with its output collected, so it has never been silent.
            public TimeSpan SinceLastOutput => TimeSpan.Zero;

            public Task<TestProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(result);

            public void TerminateProcessTree()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ArtifactTestProcessRunner : ITestProcessRunner
    {
        private readonly Queue<TestProcessResult> _results = new();

        public List<TestProcessCommand> Calls { get; } = [];

        public Action<TestProcessCommand>? OnRun { get; set; }

        public void Enqueue(
            string standardOutput = "",
            string standardError = "",
            int exitCode = 0,
            bool standardOutputTruncated = false,
            bool standardErrorTruncated = false) =>
            _results.Enqueue(new TestProcessResult(
                exitCode,
                standardOutput,
                standardError,
                standardOutputTruncated,
                standardErrorTruncated));

        public Task<TestProcessResult> RunAsync(
            TestProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(command);
            OnRun?.Invoke(command);
            if (_results.Count == 0)
                throw new InvalidOperationException("No fake result was queued.");
            return Task.FromResult(_results.Dequeue());
        }
    }
}
