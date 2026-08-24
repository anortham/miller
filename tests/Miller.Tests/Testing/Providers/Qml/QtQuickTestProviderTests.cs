using System.Text;
using Miller.Testing;
using Miller.Testing.Providers.Qml;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Testing.Providers.Qml;

[Collection(QmlProviderEnvironmentCollection.Name)]
public sealed class QtQuickTestProviderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-qml-provider-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "miller-ct"), recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Discover_configures_and_builds_outside_the_source_tree_with_stable_target_cases()
    {
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (command.FileName == "cmake" && command.Arguments.SequenceEqual(["--version"]))
                return new TestProcessResult(0, "cmake version 3.27.9\n", string.Empty);
            if (command.FileName == "cmake" && command.Arguments.Contains("-S"))
            {
                var buildDirectory = ArgumentAfter(command, "-B");
                Directory.CreateDirectory(buildDirectory);
                File.WriteAllText(Path.Combine(buildDirectory, "CMakeCache.txt"), "cache");
                File.WriteAllText(Path.Combine(buildDirectory, "CTestTestfile.cmake"), "tests");
                return new TestProcessResult(0, string.Empty, string.Empty);
            }
            if (command.FileName == "cmake" && command.Arguments.Contains("--build"))
                return new TestProcessResult(0, string.Empty, string.Empty);
            if (command.FileName == "ctest")
                return new TestProcessResult(0, DiscoveryJson("Z/π (smoke)", "A/basic"), string.Empty);
            throw new Xunit.Sdk.XunitException($"unexpected command: {command.ToDisplayString()}");
        });
        var provider = new QtQuickTestProvider(runner);

        var cases = await provider.DiscoverAsync(Workspace(), TestContext.Current.CancellationToken);

        Assert.Equal(["A/basic", "Z/π (smoke)"], cases.Select(testCase => testCase.DisplayName));
        Assert.All(cases, testCase => Assert.Equal("qt-quick-test", testCase.Framework));
        Assert.All(cases, testCase => Assert.Equal("qml", testCase.Metadata["language"]));
        Assert.Equal(4, runner.Calls.Count);
        var configure = runner.Calls.Single(command => command.Arguments.Contains("-S"));
        Assert.Equal("cmake", configure.FileName);
        Assert.Equal(ConfigureRoot, ArgumentAfter(configure, "-S"));
        Assert.StartsWith(Path.Combine(BuildRoot, "g"), ArgumentAfter(configure, "-B"), StringComparison.Ordinal);
        Assert.DoesNotContain(ConfigureRoot, ArgumentAfter(configure, "-B"), StringComparison.Ordinal);
        Assert.Contains("-DBUILD_TESTING=ON", configure.Arguments);

        var second = await provider.DiscoverAsync(Workspace(), TestContext.Current.CancellationToken);
        Assert.Equal(cases.Select(testCase => testCase.Id), second.Select(testCase => testCase.Id));
    }

    [Fact]
    public async Task Selected_run_reuses_discovery_generation_and_maps_junit_to_exact_cases()
    {
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (command.FileName == "cmake" && command.Arguments.SequenceEqual(["--version"]))
                return new TestProcessResult(0, "cmake version 3.27.9\n", string.Empty);
            if (command.FileName == "cmake" && command.Arguments.Contains("-S"))
            {
                var buildDirectory = ArgumentAfter(command, "-B");
                Directory.CreateDirectory(buildDirectory);
                File.WriteAllText(Path.Combine(buildDirectory, "CMakeCache.txt"), "cache");
                File.WriteAllText(Path.Combine(buildDirectory, "CTestTestfile.cmake"), "tests");
                return new TestProcessResult(0, string.Empty, string.Empty);
            }
            if (command.FileName == "cmake")
                return new TestProcessResult(0, string.Empty, string.Empty);
            if (command.FileName == "ctest" && command.Arguments.Contains("--show-only=json-v1"))
                return new TestProcessResult(0, DiscoveryJson("A/basic", "B/slow"), string.Empty);
            if (command.FileName == "ctest")
            {
                var artifact = ArgumentAfter(command, "--output-junit");
                Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
                File.WriteAllText(artifact, "<testsuite name=\"CTest\"><testcase classname=\"CTest\" name=\"A/basic\" time=\"0.25\" /></testsuite>");
                return new TestProcessResult(0, string.Empty, string.Empty);
            }
            throw new Xunit.Sdk.XunitException($"unexpected command: {command.ToDisplayString()}");
        });
        var provider = new QtQuickTestProvider(runner);
        var workspace = Workspace();
        var discovered = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        var selected = discovered.Single(testCase => testCase.DisplayName == "A/basic");

        var result = await provider.RunAsync(
            Request(workspace, [selected.Id]),
            TestContext.Current.CancellationToken);

        var run = runner.Calls.Last(command => command.FileName == "ctest" && !command.Arguments.Contains("--show-only=json-v1"));
        Assert.Contains("--no-tests=error", run.Arguments);
        Assert.Contains("--output-junit", run.Arguments);
        Assert.Contains("-R", run.Arguments);
        Assert.Equal("^(?:A/basic)$", ArgumentAfter(run, "-R"));
        Assert.Equal("offscreen", run.Environment["QT_QPA_PLATFORM"]);
        Assert.Single(result.CaseResults);
        Assert.Equal(selected.Id, result.CaseResults[0].TestCaseId);
        Assert.Equal("passed", result.CaseResults[0].Status);
        Assert.NotNull(result.ResultArtifactPath);
        Assert.True(File.Exists(result.ResultArtifactPath));
        Assert.DoesNotContain(runner.Calls.Skip(4), command => command.FileName == "cmake");
    }

    [Fact]
    public async Task Whole_suite_run_uses_no_selection_and_returns_every_reported_case()
    {
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (command.FileName == "cmake" && command.Arguments.SequenceEqual(["--version"]))
                return new TestProcessResult(0, "cmake version 3.27.9\n", string.Empty);
            if (command.FileName == "cmake" && command.Arguments.Contains("-S"))
            {
                var buildDirectory = ArgumentAfter(command, "-B");
                Directory.CreateDirectory(buildDirectory);
                File.WriteAllText(Path.Combine(buildDirectory, "CMakeCache.txt"), "cache");
                File.WriteAllText(Path.Combine(buildDirectory, "CTestTestfile.cmake"), "tests");
                return new TestProcessResult(0, string.Empty, string.Empty);
            }
            if (command.FileName == "cmake")
                return new TestProcessResult(0, string.Empty, string.Empty);
            if (command.FileName == "ctest" && command.Arguments.Contains("--show-only=json-v1"))
                return new TestProcessResult(0, DiscoveryJson("A/basic", "B/slow"), string.Empty);
            if (command.FileName == "ctest")
            {
                var artifact = ArgumentAfter(command, "--output-junit");
                Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
                File.WriteAllText(artifact, "<testsuite name=\"CTest\"><testcase classname=\"CTest\" name=\"A/basic\" /><testcase classname=\"CTest\" name=\"B/slow\" /></testsuite>");
                return new TestProcessResult(0, string.Empty, string.Empty);
            }
            throw new Xunit.Sdk.XunitException($"unexpected command: {command.ToDisplayString()}");
        });
        var provider = new QtQuickTestProvider(runner);
        var workspace = Workspace();
        var discovered = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        var result = await provider.RunAsync(
            Request(workspace, discovered.Select(testCase => testCase.Id).ToArray()) with { WholeSuite = true },
            TestContext.Current.CancellationToken);

        var run = runner.Calls.Last(command => command.FileName == "ctest" && !command.Arguments.Contains("--show-only=json-v1"));
        Assert.DoesNotContain("-R", run.Arguments);
        Assert.Equal(
            discovered.Select(testCase => testCase.Id).Order(StringComparer.Ordinal),
            result.CaseResults.Select(caseResult => caseResult.TestCaseId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Coverage_fails_before_any_process_execution()
    {
        var runner = new ScriptedTestProcessRunner(_ => throw new Xunit.Sdk.XunitException("runner must not execute"));
        var provider = new QtQuickTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.RunAsync(
                Request(Workspace(), ["qml-test:unknown"]) with { CoverageMode = ContinuousTestCoverageMode.PerTest },
                TestContext.Current.CancellationToken));

        Assert.Contains("coverage", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Discovery_rejects_truncated_machine_output_and_zero_targets()
    {
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (command.FileName == "cmake" && command.Arguments.SequenceEqual(["--version"]))
                return new TestProcessResult(0, "cmake version 3.27.9\n", string.Empty);
            if (command.FileName == "cmake" && command.Arguments.Contains("-S"))
            {
                var buildDirectory = ArgumentAfter(command, "-B");
                Directory.CreateDirectory(buildDirectory);
                File.WriteAllText(Path.Combine(buildDirectory, "CMakeCache.txt"), "cache");
                File.WriteAllText(Path.Combine(buildDirectory, "CTestTestfile.cmake"), "tests");
                return new TestProcessResult(0, string.Empty, string.Empty);
            }
            if (command.FileName == "cmake")
                return new TestProcessResult(0, string.Empty, string.Empty);
            return new TestProcessResult(
                0,
                "{\"kind\":\"ctestInfo\",\"version\":{\"major\":1,\"minor\":0},\"tests\":[]}",
                string.Empty,
                StandardOutputTruncated: true);
        });
        var provider = new QtQuickTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(Workspace(), TestContext.Current.CancellationToken));

        Assert.Contains("partial", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explicit_qt_platform_is_preserved_case_insensitively()
    {
        string? original = Environment.GetEnvironmentVariable("QT_QPA_PLATFORM");
        try
        {
            Environment.SetEnvironmentVariable("QT_QPA_PLATFORM", "minimal");
            var runner = new ScriptedTestProcessRunner(command =>
            {
                if (command.FileName == "cmake" && command.Arguments.SequenceEqual(["--version"]))
                    return new TestProcessResult(0, "cmake version 3.27.9\n", string.Empty);
                if (command.FileName == "cmake" && command.Arguments.Contains("-S"))
                {
                    var buildDirectory = ArgumentAfter(command, "-B");
                    Directory.CreateDirectory(buildDirectory);
                    File.WriteAllText(Path.Combine(buildDirectory, "CMakeCache.txt"), "cache");
                    File.WriteAllText(Path.Combine(buildDirectory, "CTestTestfile.cmake"), "tests");
                    return new TestProcessResult(0, string.Empty, string.Empty);
                }
                if (command.FileName == "cmake")
                    return new TestProcessResult(0, string.Empty, string.Empty);
                if (command.Arguments.Contains("--show-only=json-v1"))
                    return new TestProcessResult(0, DiscoveryJson("A/basic"), string.Empty);
                var artifact = ArgumentAfter(command, "--output-junit");
                Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
                File.WriteAllText(artifact, "<testsuite name=\"CTest\"><testcase name=\"A/basic\" /></testsuite>");
                return new TestProcessResult(0, string.Empty, string.Empty);
            });
            var provider = new QtQuickTestProvider(runner);
            var workspace = Workspace();
            var discovered = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
            await provider.RunAsync(
                Request(workspace, [Assert.Single(discovered).Id]),
                TestContext.Current.CancellationToken);

            var run = runner.Calls.Last(command => command.FileName == "ctest" && !command.Arguments.Contains("--show-only=json-v1"));
            Assert.Equal("minimal", run.Environment["QT_QPA_PLATFORM"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QT_QPA_PLATFORM", original);
        }
    }

    [Fact]
    public async Task Configured_build_configuration_is_shared_by_configure_build_discovery_and_run()
    {
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (command.FileName == "cmake" && command.Arguments.SequenceEqual(["--version"]))
                return new TestProcessResult(0, "cmake version 3.27.9\n", string.Empty);
            if (command.FileName == "cmake" && command.Arguments.Contains("-S"))
            {
                var buildDirectory = ArgumentAfter(command, "-B");
                Directory.CreateDirectory(buildDirectory);
                File.WriteAllText(Path.Combine(buildDirectory, "CMakeCache.txt"), "cache");
                File.WriteAllText(Path.Combine(buildDirectory, "CTestTestfile.cmake"), "tests");
                return new TestProcessResult(0, string.Empty, string.Empty);
            }
            if (command.FileName == "cmake")
                return new TestProcessResult(0, string.Empty, string.Empty);
            if (command.FileName == "ctest" && command.Arguments.Contains("--show-only=json-v1"))
                return new TestProcessResult(0, DiscoveryJson("A/basic"), string.Empty);

            var artifact = ArgumentAfter(command, "--output-junit");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            File.WriteAllText(artifact, "<testsuite name=\"CTest\"><testcase name=\"A/basic\" /></testsuite>");
            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        var provider = new QtQuickTestProvider(runner);
        var workspace = Workspace() with
        {
            Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["configure_root"] = ConfigureRoot,
                ["evidence_root"] = Path.Combine(ConfigureRoot, "tests"),
                ["configuration"] = "Debug",
            },
        };

        var discovered = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        await provider.RunAsync(
            Request(workspace, [Assert.Single(discovered).Id]),
            TestContext.Current.CancellationToken);

        var configure = runner.Calls.Single(command => command.Arguments.Contains("-S"));
        Assert.Contains("-DCMAKE_BUILD_TYPE=Debug", configure.Arguments);
        Assert.Contains("-DBUILD_TESTING=ON", configure.Arguments);
        var build = runner.Calls.Single(command => command.Arguments.Contains("--build"));
        Assert.Equal("Debug", ArgumentAfter(build, "--config"));
        var discovery = runner.Calls.Single(command => command.FileName == "ctest" && command.Arguments.Contains("--show-only=json-v1"));
        Assert.Equal("Debug", ArgumentAfter(discovery, "-C"));
        var run = runner.Calls.Last(command => command.FileName == "ctest" && !command.Arguments.Contains("--show-only=json-v1"));
        Assert.Equal("Debug", ArgumentAfter(run, "-C"));
    }

    [Fact]
    public async Task Generated_run_identity_is_used_for_case_results_when_request_has_no_run_id()
    {
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (command.FileName == "cmake" && command.Arguments.SequenceEqual(["--version"]))
                return new TestProcessResult(0, "cmake version 3.27.9\n", string.Empty);
            if (command.FileName == "cmake" && command.Arguments.Contains("-S"))
            {
                var buildDirectory = ArgumentAfter(command, "-B");
                Directory.CreateDirectory(buildDirectory);
                File.WriteAllText(Path.Combine(buildDirectory, "CMakeCache.txt"), "cache");
                File.WriteAllText(Path.Combine(buildDirectory, "CTestTestfile.cmake"), "tests");
                return new TestProcessResult(0, string.Empty, string.Empty);
            }
            if (command.FileName == "cmake")
                return new TestProcessResult(0, string.Empty, string.Empty);
            if (command.FileName == "ctest" && command.Arguments.Contains("--show-only=json-v1"))
                return new TestProcessResult(0, DiscoveryJson("A/basic"), string.Empty);
            var artifact = ArgumentAfter(command, "--output-junit");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            File.WriteAllText(artifact, "<testsuite name=\"CTest\"><testcase name=\"A/basic\" /></testsuite>");
            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        var provider = new QtQuickTestProvider(runner);
        var workspace = Workspace();
        var discovered = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        var request = Request(workspace, [Assert.Single(discovered).Id]) with { RunId = null };

        var first = await provider.RunAsync(request, TestContext.Current.CancellationToken);
        var second = await provider.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.RunId, second.RunId);
        Assert.NotEqual(Assert.Single(first.CaseResults).Id, Assert.Single(second.CaseResults).Id);
        Assert.StartsWith("ct_run:", first.RunId, StringComparison.Ordinal);
        Assert.StartsWith("ct_run:", second.RunId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_selected_junit_cases_fail_with_their_stable_ids()
    {
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (command.FileName == "cmake" && command.Arguments.SequenceEqual(["--version"]))
                return new TestProcessResult(0, "cmake version 3.27.9\n", string.Empty);
            if (command.FileName == "cmake" && command.Arguments.Contains("-S"))
            {
                var buildDirectory = ArgumentAfter(command, "-B");
                Directory.CreateDirectory(buildDirectory);
                File.WriteAllText(Path.Combine(buildDirectory, "CMakeCache.txt"), "cache");
                File.WriteAllText(Path.Combine(buildDirectory, "CTestTestfile.cmake"), "tests");
                return new TestProcessResult(0, string.Empty, string.Empty);
            }
            if (command.FileName == "cmake")
                return new TestProcessResult(0, string.Empty, string.Empty);
            if (command.FileName == "ctest" && command.Arguments.Contains("--show-only=json-v1"))
                return new TestProcessResult(0, DiscoveryJson("A/basic", "B/slow"), string.Empty);
            var artifact = ArgumentAfter(command, "--output-junit");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            File.WriteAllText(artifact, "<testsuite name=\"CTest\"><testcase name=\"A/basic\" /></testsuite>");
            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        var provider = new QtQuickTestProvider(runner);
        var workspace = Workspace();
        var discovered = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        var selected = discovered.Select(testCase => testCase.Id).ToArray();
        var missingId = discovered.Single(testCase => testCase.DisplayName == "B/slow").Id;

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.RunAsync(Request(workspace, selected), TestContext.Current.CancellationToken));

        Assert.Contains("did not report selected test cases", exception.Message, StringComparison.Ordinal);
        Assert.Contains(missingId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Raw_launch_failure_is_stamped_with_command_context_and_generation()
    {
        var runner = new ScriptedTestProcessRunner(_ => throw new InvalidOperationException("tool missing"));
        var provider = new QtQuickTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(Workspace(), TestContext.Current.CancellationToken));

        Assert.Contains("cmake", exception.Message, StringComparison.Ordinal);
        Assert.Contains(ConfigureRoot, exception.Message, StringComparison.Ordinal);
        Assert.Contains("tool missing", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.GenerationId);
    }

    [Fact]
    public async Task Unsupported_cmake_version_is_stamped_and_rejected()
    {
        var runner = new ScriptedTestProcessRunner(_ =>
            new TestProcessResult(0, "cmake version 3.20.6\n", string.Empty));
        var provider = new QtQuickTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(Workspace(), TestContext.Current.CancellationToken));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3.21", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.GenerationId);
    }

    [Fact]
    public async Task Cancellation_from_a_timeout_shaped_runner_is_preserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new ScriptedTestProcessRunner(_ =>
            throw new OperationCanceledException("timeout-shaped cancellation", cancellation.Token));
        var provider = new QtQuickTestProvider(runner);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.DiscoverAsync(Workspace(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Nonzero_configure_exit_is_reported_with_exit_code()
    {
        var runner = new ScriptedTestProcessRunner(command =>
            command.Arguments.SequenceEqual(["--version"])
                ? new TestProcessResult(0, "cmake version 3.27.9\n", string.Empty)
                : new TestProcessResult(9, string.Empty, "configure failed"));
        var provider = new QtQuickTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(Workspace(), TestContext.Current.CancellationToken));

        Assert.Contains("configure failed with exit code 9", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.GenerationId);
    }

    [Fact]
    public async Task Nonzero_ctest_run_exit_is_reported_with_exit_code()
    {
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (command.FileName == "cmake" && command.Arguments.SequenceEqual(["--version"]))
                return new TestProcessResult(0, "cmake version 3.27.9\n", string.Empty);
            if (command.FileName == "cmake" && command.Arguments.Contains("-S"))
            {
                var buildDirectory = ArgumentAfter(command, "-B");
                Directory.CreateDirectory(buildDirectory);
                File.WriteAllText(Path.Combine(buildDirectory, "CMakeCache.txt"), "cache");
                File.WriteAllText(Path.Combine(buildDirectory, "CTestTestfile.cmake"), "tests");
                return new TestProcessResult(0, string.Empty, string.Empty);
            }
            if (command.FileName == "cmake")
                return new TestProcessResult(0, string.Empty, string.Empty);
            if (command.Arguments.Contains("--show-only=json-v1"))
                return new TestProcessResult(0, DiscoveryJson("A/basic"), string.Empty);
            return new TestProcessResult(7, string.Empty, "CTest failed");
        });
        var provider = new QtQuickTestProvider(runner);
        var workspace = Workspace();
        var discovered = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.RunAsync(Request(workspace, [Assert.Single(discovered).Id]), TestContext.Current.CancellationToken));

        Assert.Contains("CTest run failed with exit code 7", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.GenerationId);
    }

    private string ConfigureRoot => Path.Combine(_root, "source");

    private string BuildRoot => Path.Combine(_root, "build");

    private ContinuousTestWorkspace Workspace()
    {
        Directory.CreateDirectory(ConfigureRoot);
        File.WriteAllText(Path.Combine(ConfigureRoot, "CMakeLists.txt"), "project(qml)");
        return new ContinuousTestWorkspace(
            WorkspaceId: "ws:qml",
            WorkspaceRoot: _root,
            ProjectPath: Path.Combine(ConfigureRoot, "CMakeLists.txt"),
            BuildOutputRoot: BuildRoot,
            Framework: "qt-quick-test",
            Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["configure_root"] = ConfigureRoot,
                ["evidence_root"] = Path.Combine(ConfigureRoot, "tests"),
            });
    }

    private static ContinuousTestProviderRunRequest Request(
        ContinuousTestWorkspace workspace,
        IReadOnlyList<string> ids) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: "index-1",
            RunId: "run:qml",
            TestCaseIds: ids);

    private static string ArgumentAfter(TestProcessCommand command, string argument)
    {
        var index = command.Arguments.ToList().IndexOf(argument);
        Assert.True(index >= 0 && index + 1 < command.Arguments.Count, $"missing value after {argument}");
        return command.Arguments[index + 1];
    }

    private static string DiscoveryJson(params string[] names) =>
        "{\"kind\":\"ctestInfo\",\"version\":{\"major\":1,\"minor\":0},\"tests\":["
        + string.Join(',', names.Select(name => $"{{\"name\":\"{EscapeJson(name)}\",\"command\":[\"qml-test\"]}}"))
        + "]}";

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
