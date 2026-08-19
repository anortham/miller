using System.Diagnostics;
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
            Path.Combine(generation.OutDir, "Sample.Tests" + ExecutableExtension()),
            listCommand.FileName);
        Assert.Equal(DotnetTestProvider.TestExecutablePath(workspace), listCommand.FileName);
        Assert.Equal(workspace.WorkspaceRoot, listCommand.WorkingDirectory);
        Assert.Equal(["-list", "full/json", "-noLogo", "-noColor"], listCommand.Arguments);
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
        var targetPath = Path.Combine(generation.OutDir, "Custom.Assembly.dll");
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
        var targetPath = Path.Combine(generation.OutDir, "Custom.Assembly.dll");
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
        var targetPath = Path.Combine(FirstGeneration(workspace).OutDir, "Sample.Tests.dll");
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
        runner.Enqueue(Path.Combine(FirstGeneration(workspace).OutDir, "Missing.Tests.dll"));
        var provider = new DotnetTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("does not exist", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FirstGeneration(workspace).GenerationId, exception.GenerationId);
        Assert.Equal(2, runner.Calls.Count);
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
            Path.Combine(generation.OutDir, "Sample.Tests" + ExecutableExtension()),
            runCommand.FileName);
        AssertContainsAdjacentPair(runCommand.Arguments, "-method", "Sample.Tests.Passes");
        Assert.Equal(workspace.WorkspaceRoot, runCommand.Environment[CtEnvironment.WorkspaceRoot]);
        AssertUsesGenerationTempDirectory(runCommand.Environment, generation);
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
        var targetPath = Path.Combine(generation.OutDir, "Custom.Assembly.dll");
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

        Assert.Equal(["-list", "full/json", "-noLogo", "-noColor"], command.Arguments.Take(4).ToArray());
        AssertContainsAdjacentPair(command.Arguments, "-trait-", "Category=Scale");
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
                "-noLogo", "-noColor", "-reporter", "json",
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
                Directory.CreateDirectory(generation.OutDir);
                File.WriteAllText(Path.Combine(generation.OutDir, "Sample.Tests.dll"), string.Empty);
                File.WriteAllText(Path.Combine(generation.OutDir, "Sample.Tests.pdb"), string.Empty);
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
        new(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: "run:generation",
            TestCaseIds: ["xunit:Sample.Tests.Passes"]);

    private static void WriteEmptyJunitArtifact(TestProcessCommand command)
    {
        var artifactFlag = command.Arguments.ToList().IndexOf("-jUnit");
        if (artifactFlag < 0)
            return;

        var artifactPath = command.Arguments[artifactFlag + 1];
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(artifactPath, "<testsuite />");
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

    private static string ExecutableExtension() => OperatingSystem.IsWindows() ? ".exe" : "";

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

            public Task<TestProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(result);

            public void TerminateProcessTree()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
