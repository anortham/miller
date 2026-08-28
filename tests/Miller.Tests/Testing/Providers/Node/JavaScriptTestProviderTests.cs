using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Miller.Testing;
using Miller.Tests.Testing.Providers.Dotnet;
using Xunit;

namespace Miller.Tests.Testing.Providers.Node;

public sealed class JavaScriptTestProviderTests : IDisposable
{
    private const string IndexIdentity = "store:test-identity";
    private const string RunId = "run:1";

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
    public async Task Discover_rejects_an_installed_vitest_version_outside_the_supported_range()
    {
        var workspace = Workspace("vitest");
        WriteInstalledPackage("vitest", "5.0.0");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("vitest", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5.0.0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("0.34", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_rejects_an_installed_jest_version_outside_the_supported_range()
    {
        var workspace = Workspace("jest");
        WriteInstalledPackage("jest", "28.1.3");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("jest", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("28.1.3", exception.Message, StringComparison.Ordinal);
        Assert.Contains("29.x", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("^0.34.0")]
    [InlineData("^0.35.0")]
    [InlineData("~0.99.0")]
    public async Task Discover_accepts_a_supported_dependency_range_without_an_installed_manifest(string range)
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "package.json",
            "{\"devDependencies\":{\"vitest\":\"" + range + "\"}}");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["src/math.test.ts"], cases.Select(row => row.Selector).ToArray());
    }

    [Fact]
    public async Task Discover_rejects_an_installed_manifest_without_a_version()
    {
        var workspace = Workspace("vitest");
        WritePackageFile("node_modules/vitest/package.json", "{\"name\":\"vitest\"}");
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("^0.33.0")]
    [InlineData("^4.0.0 || ^5.0.0")]
    [InlineData("workspace:*")]
    public async Task Discover_rejects_an_unprovable_or_cross_boundary_dependency_range(string range)
    {
        var workspace = Workspace("vitest");
        WritePackageFile(
            "package.json",
            $$"""
            { "devDependencies": { "vitest": "{{range}}" } }
            """);
        WritePackageFile("src/math.test.ts", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains(range, exception.Message, StringComparison.Ordinal);
        Assert.Contains("supported", exception.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 1), result.GenerationId);
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
    public async Task Sequential_runs_allocate_distinct_generation_directories()
    {
        var workspace = Workspace("jest");
        var runner = new FakeTestProcessRunner();
        runner.OnRun = WriteEmptyJestArtifact;
        runner.Enqueue(exitCode: 0);
        runner.Enqueue(exitCode: 0);
        var provider = new JavaScriptTestProvider(runner);

        var first = await provider.RunAsync(
            Request(workspace, "js-test:src/math.test.ts"),
            TestContext.Current.CancellationToken);
        var second = await provider.RunAsync(
            Request(workspace, "js-test:src/string.spec.js"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 1), first.GenerationId);
        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 2), second.GenerationId);
        Assert.NotEqual(first.ResultArtifactPath, second.ResultArtifactPath);
        Assert.StartsWith(
            CtGenerationPaths.For(workspace, first.GenerationId!).ResultsDirectory,
            first.ResultArtifactPath!,
            StringComparison.Ordinal);
        Assert.StartsWith(
            CtGenerationPaths.For(workspace, second.GenerationId!).ResultsDirectory,
            second.ResultArtifactPath!,
            StringComparison.Ordinal);
        AssertWorkspaceIsolation(workspace);
    }

    [Fact]
    public void Build_run_command_for_vitest_uses_local_bin_and_json_output_file()
    {
        var workspace = Workspace("vitest");
        WriteInstalledPackage("vitest", "3.2.4");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());
        var generation = CtGenerationPaths.ResolveLatestOrFirst(workspace);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.Equal(LocalBin("vitest"), command.FileName);
        Assert.Equal(PackageRoot, command.WorkingDirectory);
        Assert.Contains("run", command.Arguments);
        Assert.Contains("--reporter=json", command.Arguments);
        Assert.Contains("--outputFile", command.Arguments);
        Assert.Contains("src/math.test.ts", command.Arguments);
        var artifactPath = command.Arguments[command.Arguments.ToList().IndexOf("--outputFile") + 1];
        Assert.EndsWith(".json", artifactPath, StringComparison.Ordinal);
        AssertUsesGeneration(command, workspace, generation, artifactPath);
        Assert.Contains("--cache.dir", command.Arguments);
        Assert.Equal(
            CacheDirectory(workspace),
            command.Arguments[command.Arguments.ToList().IndexOf("--cache.dir") + 1]);
    }

    /// <summary>
    /// Dot-notation CLI options arrived in vitest 1.x. Vitest 0.29.8 stops at its CLI parser with
    /// "CACError: Unknown option `--cache`" and runs no file at all, so a real 0.x workspace reported
    /// every selected test as failed. A 0.x install must not see the flag.
    /// </summary>
    [Fact]
    public void Build_run_command_for_vitest_0x_omits_the_dot_notation_cache_option()
    {
        var workspace = Workspace("vitest");
        WriteInstalledPackage("vitest", "0.29.8");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.DoesNotContain(
            command.Arguments,
            argument => argument.StartsWith("--cache", StringComparison.Ordinal));
        // Everything else about the invocation is unchanged: it still runs, and still writes the report
        // where continuous testing reads it.
        Assert.Equal(LocalBin("vitest"), command.FileName);
        Assert.Contains("run", command.Arguments);
        Assert.Contains("--reporter=json", command.Arguments);
        Assert.Contains("--outputFile", command.Arguments);
        Assert.Contains("src/math.test.ts", command.Arguments);
    }

    /// <summary>
    /// 1.0.0 is the first version that accepts the flag, so the gate opens exactly there.
    /// </summary>
    [Fact]
    public void Build_run_command_for_vitest_1x_passes_the_dot_notation_cache_option()
    {
        var workspace = Workspace("vitest");
        WriteInstalledPackage("vitest", "1.0.0");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());
        var generation = CtGenerationPaths.ResolveLatestOrFirst(workspace);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.Contains("--cache.dir", command.Arguments);
        Assert.Equal(
            CacheDirectory(workspace),
            command.Arguments[command.Arguments.ToList().IndexOf("--cache.dir") + 1]);
    }

    [Fact]
    public void Build_run_command_for_vitest_4x_disables_cache_with_the_supported_boolean_option()
    {
        var workspace = Workspace("vitest");
        WriteInstalledPackage("vitest", "4.1.5");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.Contains("--cache=false", command.Arguments);
        Assert.DoesNotContain("--cache.dir", command.Arguments);
    }

    /// <summary>
    /// No installed manifest means no proof the flag is accepted. Omitting it costs cache isolation
    /// between generations; passing it on a guess costs the whole run.
    /// </summary>
    [Fact]
    public void Build_run_command_for_vitest_omits_the_cache_option_when_no_install_is_readable()
    {
        var workspace = Workspace("vitest");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.DoesNotContain(
            command.Arguments,
            argument => argument.StartsWith("--cache", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_run_command_for_vitest_omits_the_cache_option_when_the_installed_version_is_unparseable()
    {
        var workspace = Workspace("vitest");
        WriteInstalledPackage("vitest", "workspace:*");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.DoesNotContain(
            command.Arguments,
            argument => argument.StartsWith("--cache", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_run_command_for_jest_uses_local_bin_and_json_output_file()
    {
        var workspace = Workspace("jest");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());
        var generation = CtGenerationPaths.ResolveLatestOrFirst(workspace);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.Equal(LocalBin("jest"), command.FileName);
        Assert.Equal(PackageRoot, command.WorkingDirectory);
        Assert.Contains("--json", command.Arguments);
        Assert.Contains("--outputFile", command.Arguments);
        Assert.Contains("src/math.test.ts", command.Arguments);
        var artifactPath = command.Arguments[command.Arguments.ToList().IndexOf("--outputFile") + 1];
        Assert.EndsWith(".json", artifactPath, StringComparison.Ordinal);
        AssertUsesGeneration(command, workspace, generation, artifactPath);
        Assert.Contains("--cacheDirectory", command.Arguments);
        Assert.Equal(
            CacheDirectory(workspace),
            command.Arguments[command.Arguments.ToList().IndexOf("--cacheDirectory") + 1]);
    }

    [Fact]
    public void Build_run_command_for_node_test_uses_node_junit_output_file()
    {
        var workspace = Workspace("node-test");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());
        var generation = CtGenerationPaths.ResolveLatestOrFirst(workspace);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.js"));

        Assert.Equal("node", command.FileName);
        Assert.Equal(PackageRoot, command.WorkingDirectory);
        Assert.Contains("--test", command.Arguments);
        Assert.Contains("--test-reporter", command.Arguments);
        Assert.Contains("junit", command.Arguments);
        Assert.Contains("--test-reporter-destination", command.Arguments);
        Assert.Contains("src/math.test.js", command.Arguments);
        var artifactPath = command.Arguments[command.Arguments.ToList().IndexOf("--test-reporter-destination") + 1];
        Assert.EndsWith(".xml", artifactPath, StringComparison.Ordinal);
        AssertUsesGeneration(command, workspace, generation, artifactPath);
        Assert.Equal(CacheDirectory(workspace), command.Environment["NODE_COMPILE_CACHE"]);
    }

    [Fact]
    public void BuildRunCommands_splits_selected_node_test_files_into_one_invocation_each()
    {
        var workspace = Workspace("node-test");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());
        var files = new[] { "test/a.test.js", "test/b.test.js", "test/c.test.js" };

        var commands = provider.BuildRunCommands(Request(workspace, files.Select(TestCaseIdFor).ToArray()));

        Assert.Equal(files, commands.Select(SelectedNodeFile).ToArray());
        Assert.All(commands, command => Assert.Single(SelectedNodeFiles(command)));
    }

    [Fact]
    public void BuildRunCommands_splits_a_whole_suite_node_test_selection_into_one_invocation_each()
    {
        var workspace = Workspace("node-test");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());
        var files = new[] { "test/a.test.js", "test/b.test.js", "test/c.test.js" };

        var commands = provider.BuildRunCommands(
            Request(workspace, files.Select(TestCaseIdFor).ToArray()) with { WholeSuite = true });

        Assert.Equal(files, commands.Select(SelectedNodeFile).ToArray());
        Assert.All(commands, command => Assert.Single(SelectedNodeFiles(command)));
    }

    [Fact]
    public async Task Run_refuses_an_unattributed_node_junit_report_for_multiple_ids()
    {
        var workspace = Workspace("node-test");
        var runner = new FakeTestProcessRunner();
        runner.OnRun = WriteUnattributedNodeJunitArtifact;
        runner.Enqueue(exitCode: 0);
        var provider = new JavaScriptTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.RunAsync(
                Request(workspace, "other:test-a", "other:test-b"),
                TestContext.Current.CancellationToken));

        Assert.Contains("file attribution", exception.Message, StringComparison.OrdinalIgnoreCase);
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
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner(), NoPackageManagerOnPath);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        // No shim on PATH, so the bare name — the same name the provider sent before any Windows
        // suffix existed, and the one CreateProcessW resolves for itself. Which file the probe picks
        // when PATH does hold a shim is pinned by the package-manager probe tests below.
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
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner(), NoPackageManagerOnPath);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/components/account.spec.ts"));

        // No shim on PATH, so the bare name. See the package-manager probe tests below.
        Assert.Equal("npm", command.FileName);
        Assert.Equal("run", command.Arguments[0]);
        Assert.Equal("test:unit", command.Arguments[1]);
        Assert.Contains("--json", command.Arguments);
        Assert.Contains("--outputFile", command.Arguments);
        Assert.Contains("src/components/account.spec.ts", command.Arguments);
    }

    // ------------------------------------------------------- the package-manager launchable name

    /// <summary>
    /// A PATH that holds no package-manager shim at all. Injected wherever a test is about something
    /// else, so that test cannot depend on what the developer's machine installed.
    /// </summary>
    private static readonly Func<string, string?> NoPackageManagerOnPath = _ => null;

    /// <summary>A Node MSI / nvm-windows install: npm.cmd, and an extensionless MSYS shell script.</summary>
    private const string NodeJsDirectory = "C:/Program Files/nodejs";

    /// <summary>A Volta-managed install: npm.exe, and no npm.cmd anywhere on the machine.</summary>
    private const string VoltaBinDirectory = "C:/Users/dev/AppData/Local/Volta/bin";

    [Fact]
    public void Build_run_command_launches_the_package_manager_the_probe_found_on_path()
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
        // Volta: npm.exe is on PATH and npm.cmd exists nowhere. A hard-coded ".cmd" names a file that
        // is not there, and CreateProcessW does not append ".exe" to a name that already carries an
        // extension, so Process.Start throws Win32Exception before any test runs.
        var probed = new List<string>();
        var provider = new JavaScriptTestProvider(
            new FakeTestProcessRunner(),
            manager =>
            {
                probed.Add(manager);
                return Path.Join(VoltaBinDirectory, manager + ".exe");
            });

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.Equal(Path.Join(VoltaBinDirectory, "npm.exe"), command.FileName);
        Assert.Equal("npm", Assert.Single(probed));
    }

    [Fact]
    public void Build_run_command_probes_for_the_manager_the_lockfile_names_and_falls_back_to_the_bare_name()
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
        WritePackageFile("pnpm-lock.yaml", "lockfileVersion: '9.0'");
        string? probed = null;
        var provider = new JavaScriptTestProvider(
            new FakeTestProcessRunner(),
            manager =>
            {
                probed = manager;
                return null;
            });

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        // The lockfile chooses WHICH manager is probed...
        Assert.Equal("pnpm", probed);
        // ...and an empty PATH falls back to the bare name, which is what this provider sent before
        // any Windows suffix existed. A suffix appended here instead would name a missing file.
        Assert.Equal("pnpm", command.FileName);
    }

    [Fact]
    public void Build_run_command_for_pnpm_package_script_passes_reporter_arguments_without_separator()
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
        WritePackageFile("pnpm-lock.yaml", "lockfileVersion: '9.0'");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner(), NoPackageManagerOnPath);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.Equal("pnpm", command.FileName);
        Assert.Equal("run", command.Arguments[0]);
        Assert.Equal("test", command.Arguments[1]);
        Assert.DoesNotContain("--", command.Arguments);
        Assert.Contains("--json", command.Arguments);
        Assert.Contains("--outputFile", command.Arguments);
    }

    [Fact]
    public void Package_manager_probe_prefers_cmd_then_exe_then_bat()
    {
        string Probe(params string[] present) =>
            JavaScriptTestProvider.FindPackageManagerOnPath(
                "npm",
                [NodeJsDirectory],
                candidate => present.Any(file =>
                    string.Equals(candidate, Path.Join(NodeJsDirectory, file), StringComparison.Ordinal)))
            ?? "<none>";

        // .cmd first: it is the shim npm authors itself, and it is the kind whose 8,191-character
        // cmd.exe cap the chunk budget is sized under.
        Assert.Equal(Path.Join(NodeJsDirectory, "npm.cmd"), Probe("npm.cmd", "npm.exe", "npm.bat"));
        Assert.Equal(Path.Join(NodeJsDirectory, "npm.exe"), Probe("npm.exe", "npm.bat"));
        Assert.Equal(Path.Join(NodeJsDirectory, "npm.bat"), Probe("npm.bat"));
    }

    [Fact]
    public void Package_manager_probe_finds_an_exe_shim_when_no_directory_on_path_holds_a_cmd_shim()
    {
        string[] searchDirectories = ["C:/Windows/System32", VoltaBinDirectory];

        var resolved = JavaScriptTestProvider.FindPackageManagerOnPath(
            "npm",
            searchDirectories,
            candidate => string.Equals(
                candidate,
                Path.Join(VoltaBinDirectory, "npm.exe"),
                StringComparison.Ordinal));

        Assert.Equal(Path.Join(VoltaBinDirectory, "npm.exe"), resolved);
    }

    [Fact]
    public void Package_manager_probe_returns_null_when_no_directory_on_path_holds_a_shim()
    {
        var resolved = JavaScriptTestProvider.FindPackageManagerOnPath(
            "yarn",
            [NodeJsDirectory, VoltaBinDirectory],
            _ => false);

        Assert.Null(resolved);
    }

    [Fact]
    public void Package_manager_probe_reads_a_quoted_or_blank_path_entry()
    {
        // Windows PATH entries are routinely quoted, and a trailing ';' leaves an empty entry.
        var resolved = JavaScriptTestProvider.FindPackageManagerOnPath(
            "npm",
            ["   ", $"\"{NodeJsDirectory}\""],
            candidate => string.Equals(
                candidate,
                Path.Join(NodeJsDirectory, "npm.cmd"),
                StringComparison.Ordinal));

        Assert.Equal(Path.Join(NodeJsDirectory, "npm.cmd"), resolved);
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
        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 1), result.GenerationId);
        Assert.StartsWith(
            CtGenerationPaths.For(workspace, result.GenerationId!).ResultsDirectory,
            result.ResultArtifactPath!,
            StringComparison.Ordinal);
        AssertUsesGeneration(runner.Calls[0], workspace, FirstGeneration(workspace), result.ResultArtifactPath!);
        AssertWorkspaceIsolation(workspace);
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
        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 1), result.GenerationId);
        Assert.StartsWith(
            CtGenerationPaths.For(workspace, result.GenerationId!).ResultsDirectory,
            result.ResultArtifactPath!,
            StringComparison.Ordinal);
    }

    // -------------------------------------------- per-file node:test attribution (dogfood finding F9)

    /// <summary>
    /// A file-aware JUnit report for a partially red node:test suite. The provider must preserve one red
    /// file among three green files when the report supplies a file attribute for each row.
    /// </summary>
    [Fact]
    public async Task Run_attributes_a_node_junit_failure_to_the_file_that_failed()
    {
        var workspace = Workspace("node-test");
        var files = new[] { "test/a.test.js", "test/b.test.js", "test/c.test.js", "test/d.test.js" };
        foreach (var file in files)
            WritePackageFile(file, "test('case', () => {})");
        var runner = new FakeTestProcessRunner();
        runner.OnRun = command => WriteNodeJunitArtifact(command, files, failingFile: "test/d.test.js");
        for (var index = 0; index < files.Length; index++)
            runner.Enqueue(exitCode: 1);
        var provider = new JavaScriptTestProvider(runner);

        var result = await provider.RunAsync(
            Request(workspace, files.Select(TestCaseIdFor).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.Equal(
            files.Select(TestCaseIdFor).Order(StringComparer.Ordinal),
            result.CaseResults.Select(row => row.TestCaseId).Order(StringComparer.Ordinal));
        var failed = Assert.Single(result.CaseResults, row => row.Status == "failed");
        Assert.Equal(TestCaseIdFor("test/d.test.js"), failed.TestCaseId);
        Assert.Equal("test/d.test.js is not two", failed.FailureSummary);
        Assert.All(
            result.CaseResults.Where(row => row.TestCaseId != failed.TestCaseId),
            row =>
            {
                Assert.Equal("passed", row.Status);
                Assert.Null(row.FailureSummary);
            });
    }

    /// <summary>
    /// A selected file the report never names gets NO result. It must not inherit a sibling's verdict in
    /// either direction: the store flips a case the run never reported back to stale, which is the honest
    /// answer for a file whose outcome the report does not state.
    /// </summary>
    [Fact]
    public async Task Run_leaves_a_node_test_file_the_report_never_named_unreported()
    {
        var workspace = Workspace("node-test");
        var files = new[] { "test/a.test.js", "test/b.test.js", "test/missing.test.js", "test/d.test.js" };
        foreach (var file in files)
            WritePackageFile(file, "test('case', () => {})");
        var reported = new[] { "test/a.test.js", "test/b.test.js", "test/d.test.js" };
        var runner = new FakeTestProcessRunner();
        runner.OnRun = command => WriteNodeJunitArtifact(command, reported, failingFile: "test/d.test.js");
        for (var index = 0; index < files.Length; index++)
            runner.Enqueue(exitCode: 1);
        var provider = new JavaScriptTestProvider(runner);

        var result = await provider.RunAsync(
            Request(workspace, files.Select(TestCaseIdFor).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            reported.Select(TestCaseIdFor).Order(StringComparer.Ordinal),
            result.CaseResults.Select(row => row.TestCaseId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(
            result.CaseResults,
            row => row.TestCaseId == TestCaseIdFor("test/missing.test.js"));
    }

    /// <summary>
    /// Writes a file-aware JUnit fixture for an alternate Node reporter, including the platform's directory
    /// separator in each absolute source path.
    /// </summary>
    private void WriteNodeJunitArtifact(
        TestProcessCommand command,
        IReadOnlyList<string> reportedFiles,
        string? failingFile)
    {
        var outputPath = command.Arguments[command.Arguments.ToList().IndexOf("--test-reporter-destination") + 1];
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var cases = reportedFiles.Select(file =>
        {
            var absolute = Path.Combine(PackageRoot, file.Replace('/', Path.DirectorySeparatorChar));
            return string.Equals(file, failingFile, StringComparison.Ordinal)
                ? $"""
                     <testcase name="case" time="0.002" classname="test" file="{absolute}">
                       <failure type="testCodeFailure" message="{file} is not two">{file} is not two</failure>
                     </testcase>
                   """
                : $"""  <testcase name="case" time="0.001" classname="test" file="{absolute}" />""";
        });
        File.WriteAllText(
            outputPath,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<testsuites>\n"
            + string.Join("\n", cases)
            + "\n</testsuites>\n");
    }

    // -------------------------------------------- chained package scripts (dogfood finding F10)

    /// <summary>
    /// vercel/ms, verbatim. Its <c>test</c> entry point chains the two halves of the suite, and each half
    /// names jest. Continuous testing appends its reporter and isolation flags to the END of whatever it
    /// runs, so neither the chain nor either half can be routed through: the chain delivers the flags to
    /// its last command only, and a half is a fragment of the suite under its own environment. Running one
    /// half produced no report, and the npm banner was then attributed to all four test files as a failure.
    /// </summary>
    private void WriteChainedTestScriptPackage() =>
        WritePackageFile(
            "package.json",
            """
            {
              "scripts": {
                "test": "pnpm run test:nodejs && pnpm run test:edge",
                "test:nodejs": "jest --env node",
                "test:edge": "jest --env @edge-runtime/jest-environment --no-coverage"
              },
              "devDependencies": { "jest": "30.0.5" }
            }
            """);

    [Fact]
    public void Build_run_command_bypasses_a_chained_package_test_script_and_runs_jest_directly()
    {
        var workspace = Workspace(null);
        WriteChainedTestScriptPackage();
        WriteLocalBin("jest");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner(), NoPackageManagerOnPath);
        var generation = CtGenerationPaths.ResolveLatestOrFirst(workspace);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/index.test.ts"));

        // The local jest binary, not the package manager: the flags now reach jest itself.
        Assert.Equal(LocalBin("jest"), command.FileName);
        Assert.Equal(PackageRoot, command.WorkingDirectory);
        Assert.DoesNotContain("run", command.Arguments);
        Assert.DoesNotContain("--", command.Arguments);
        Assert.Contains("--json", command.Arguments);
        Assert.Contains("--outputFile", command.Arguments);
        Assert.Contains("src/index.test.ts", command.Arguments);
        Assert.Equal(
            CacheDirectory(workspace),
            command.Arguments[command.Arguments.ToList().IndexOf("--cacheDirectory") + 1]);
    }

    [Fact]
    public void Build_run_command_fails_honestly_when_a_chained_script_leaves_no_local_runner()
    {
        var workspace = Workspace(null);
        WriteChainedTestScriptPackage();
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner(), NoPackageManagerOnPath);

        var exception = Assert.Throws<ContinuousTestProviderException>(
            () => provider.BuildRunCommand(Request(workspace, "js-test:src/index.test.ts")));

        // A visible reason, naming the script that cannot be used and the binary that is not there.
        Assert.Contains("chains commands", exception.Message, StringComparison.Ordinal);
        Assert.Contains("pnpm run test:nodejs && pnpm run test:edge", exception.Message, StringComparison.Ordinal);
        Assert.Contains(LocalBin("jest"), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a chained script is bypassed. A plain one still routes through the package manager, so a
    /// project's own jest configuration keeps applying.
    /// </summary>
    [Fact]
    public void Build_run_command_still_routes_a_plain_package_test_script_through_the_manager()
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
        WriteLocalBin("jest");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner(), NoPackageManagerOnPath);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.Equal("npm", command.FileName);
        Assert.Equal("run", command.Arguments[0]);
        Assert.Equal("test", command.Arguments[1]);
    }

    /// <summary>
    /// A sibling script the chained entry point does NOT invoke stays usable: only the fragments of the
    /// chain are refused, not every script in the manifest.
    /// </summary>
    [Fact]
    public void Build_run_command_uses_a_sibling_script_the_chained_entry_point_does_not_invoke()
    {
        var workspace = Workspace(null);
        WritePackageFile(
            "package.json",
            """
            {
              "scripts": {
                "test": "rimraf coverage && jest --coverage",
                "test:unit": "jest --config jest.unit.config.js"
              },
              "devDependencies": { "jest": "^30.0.0" }
            }
            """);
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner(), NoPackageManagerOnPath);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.Equal("npm", command.FileName);
        Assert.Equal("test:unit", command.Arguments[1]);
    }

    /// <summary>
    /// A quoted pipe is text, not a chain. Refusing this script would drop a project's own configuration
    /// for no reason.
    /// </summary>
    [Fact]
    public void Build_run_command_reads_a_quoted_operator_as_one_command()
    {
        var workspace = Workspace(null);
        WritePackageFile(
            "package.json",
            """
            {
              "scripts": { "test": "jest --testPathPattern \"unit|contract\"" },
              "devDependencies": { "jest": "^30.0.0" }
            }
            """);
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner(), NoPackageManagerOnPath);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:src/math.test.ts"));

        Assert.Equal("npm", command.FileName);
        Assert.Equal("test", command.Arguments[1]);
    }

    /// <summary>
    /// Node stops reading options at the first positional argument, so a script that already names test
    /// paths swallows the appended reporter flags as more paths: the run exits 0, prints the default spec
    /// output, and writes no report at all. Measured against a real node 24 run of
    /// <c>npm run test -- --test-reporter junit --test-reporter-destination out.xml tests/index.js</c>
    /// against <c>"test": "node --test ./tests/*.js"</c>.
    /// </summary>
    [Fact]
    public void Build_run_command_bypasses_a_node_test_script_that_already_names_test_paths()
    {
        var workspace = Workspace(null);
        WritePackageFile("package.json", """{"scripts":{"test":"node --test ./tests/*.js"}}""");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner(), NoPackageManagerOnPath);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:tests/index.js"));

        Assert.Equal("node", command.FileName);
        Assert.Equal("--test", command.Arguments[0]);
        Assert.Contains("--test-reporter", command.Arguments);
        Assert.Contains("junit", command.Arguments);
        Assert.Contains("tests/index.js", command.Arguments);
        // Every option precedes every path, which is the only order node reads them in.
        var arguments = command.Arguments.ToList();
        Assert.True(
            arguments.FindLastIndex(argument => argument.StartsWith("--", StringComparison.Ordinal))
                < arguments.IndexOf("tests/index.js"));
    }

    [Fact]
    public void Build_run_command_routes_a_node_test_script_with_no_paths_through_the_manager()
    {
        var workspace = Workspace(null);
        WritePackageFile("package.json", """{"scripts":{"test":"node --test"}}""");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner(), NoPackageManagerOnPath);

        var command = provider.BuildRunCommand(Request(workspace, "js-test:tests/index.js"));

        Assert.Equal("npm", command.FileName);
        Assert.Equal("run", command.Arguments[0]);
        Assert.Equal("test", command.Arguments[1]);
    }

    /// <summary>
    /// A spawn that never happened must say why. The dogfood run's first attempt died on a missing pnpm
    /// and the reason reached the daemon log only, which cost a diagnosis step.
    /// </summary>
    [Fact]
    public async Task Run_reports_why_the_test_process_could_not_be_launched()
    {
        var workspace = Workspace("jest");
        var runner = new FakeTestProcessRunner();
        runner.OnRun = _ => throw new Win32Exception(2, "The system cannot find the file specified.");
        var provider = new JavaScriptTestProvider(runner);

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(
            () => provider.RunAsync(
                Request(workspace, "js-test:src/math.test.ts"),
                TestContext.Current.CancellationToken));

        Assert.Contains("The system cannot find the file specified.", exception.Message, StringComparison.Ordinal);
        Assert.Contains(LocalBin("jest"), exception.Message, StringComparison.Ordinal);
        Assert.Contains(PackageRoot, exception.Message, StringComparison.Ordinal);
        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 1), exception.GenerationId);
    }

    // ------------------------------------------- node:test file discovery (dogfood finding F8)

    /// <summary>
    /// classnames, verbatim: <c>node --test ./tests/*.js</c>. Node runs the paths its command line names,
    /// so the suite is those files - none of which carries a <c>.test.</c> or <c>.spec.</c> stem. Discovery
    /// found zero cases against a suite of 63 passing tests (dogfood finding F8, 2026-08-21).
    /// </summary>
    [Fact]
    public async Task Discover_finds_node_test_files_in_the_directory_the_package_script_names()
    {
        var workspace = Workspace(null);
        WritePackageFile("package.json", """{"scripts":{"test":"node --test ./tests/*.js"}}""");
        WritePackageFile("tests/index.js", "test('a', () => {})");
        WritePackageFile("tests/dedupe.js", "test('b', () => {})");
        WritePackageFile("tests/deep/nested.js", "test('c', () => {})");
        WritePackageFile("index.js", "module.exports = {}");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        // A single '*' stays inside one path segment, exactly as glob(7) has it, so the nested file the
        // script's glob does not name is not claimed either.
        Assert.Equal(["tests/dedupe.js", "tests/index.js"], cases.Select(row => row.Selector).ToArray());
        Assert.All(cases, row => Assert.Equal("node-test", row.Framework));
    }

    [Fact]
    public async Task Discover_walks_a_directory_the_package_script_names()
    {
        var workspace = Workspace(null);
        WritePackageFile("package.json", """{"scripts":{"test":"node --test tests/"}}""");
        WritePackageFile("tests/index.js", "test('a', () => {})");
        WritePackageFile("tests/deep/nested.mjs", "test('b', () => {})");
        WritePackageFile("tests/fixture.json", "{}");
        WritePackageFile("src/app.js", "module.exports = {}");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["tests/deep/nested.mjs", "tests/index.js"], cases.Select(row => row.Selector).ToArray());
    }

    /// <summary>
    /// Node's own documented defaults, one file per pattern. Source:
    /// <c>https://nodejs.org/api/test.html</c> — "By default, Node.js will run all files matching these
    /// patterns": <c>**/*.test.*</c>, <c>**/*-test.*</c>, <c>**/*_test.*</c>, <c>**/test-*.*</c>,
    /// <c>**/test.*</c> and <c>**/test/**/*.*</c>, over <c>cjs,mjs,js</c> plus <c>cts,mts,ts</c>.
    /// </summary>
    [Fact]
    public async Task Discover_uses_node_default_patterns_when_the_script_names_no_path()
    {
        var workspace = Workspace(null);
        WritePackageFile("package.json", """{"scripts":{"test":"node --test"}}""");
        WritePackageFile("src/math.test.js", "");
        WritePackageFile("src/string-test.mjs", "");
        WritePackageFile("src/date_test.cjs", "");
        WritePackageFile("src/parse.test.mts", "");
        WritePackageFile("test-helpers.js", "");
        WritePackageFile("test.js", "");
        WritePackageFile("test/deep/anything.js", "");
        WritePackageFile("src/helper.js", "");
        WritePackageFile("src/notes.md", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "src/date_test.cjs",
                "src/math.test.js",
                "src/parse.test.mts",
                "src/string-test.mjs",
                "test-helpers.js",
                "test.js",
                "test/deep/anything.js",
            ],
            cases.Select(row => row.Selector).ToArray());
    }

    /// <summary>
    /// jest's extra default is <c>__tests__/</c>, not a bare <c>tests/</c> or <c>test/</c> directory.
    /// Node's directory rule must not leak: a jest project's <c>tests/index.js</c> helper is not a
    /// jest test file.
    /// </summary>
    [Fact]
    public async Task Discover_keeps_test_and_spec_naming_for_jest_projects()
    {
        var workspace = Workspace("jest");
        WritePackageFile("src/math.test.ts", "");
        WritePackageFile("tests/index.js", "");
        WritePackageFile("test/deep/anything.js", "");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Equal(["src/math.test.ts"], cases.Select(row => row.Selector).ToArray());
    }

    // ------------------------------------------------------------ cmd.exe command-line cap

    /// <summary>
    /// The cap this provider must respect is 8,191 characters, not the 32,767 Windows itself allows:
    /// npm, pnpm and yarn ship as <c>.cmd</c> shims and cmd.exe applies its own, much lower limit.
    /// Measured on Windows 11: a ~7,691 character command line launched, a ~8,331 character one exited
    /// 1 with "The command line is too long." on stderr and produced no test report at all. That is a
    /// nonzero exit, NOT an exception, so an unchunked provider read a launch it never made as a
    /// failed run and reported every test in the selection as failed.
    /// </summary>
    private const int CmdShimCommandLineCap = 8191;

    [Fact]
    public void BuildRunCommands_keeps_a_small_selection_in_a_single_invocation()
    {
        var workspace = Workspace("vitest");
        WriteInstalledPackage("vitest", "3.2.4");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());
        var request = Request(workspace, "js-test:src/math.test.ts", "js-test:src/string.spec.js");
        var generation = CtGenerationPaths.ResolveLatestOrFirst(workspace);

        var commands = provider.BuildRunCommands(request);

        var single = Assert.Single(commands);
        // The literal pre-chunking argv, element by element and in order. Comparing against
        // BuildRunCommand instead would compare the chunker with itself: both sides come from
        // BuildRunInvocations(...)[0], so they move together for ANY argv change — a reordered
        // isolation/reporter pair, a dropped flag, a stray suffix — and the claim this test makes
        // ("a selection that already fits is byte-identical to before") would go untested.
        Assert.Equal(LocalBin("vitest"), single.FileName);
        Assert.Equal(PackageRoot, single.WorkingDirectory);
        Assert.Equal(
            new[]
            {
                "run",
                "--cache.dir",
                CacheDirectory(workspace),
                "--reporter=json",
                "--outputFile",
                ExpectedResultArtifactPath(generation, "json"),
                "src/math.test.ts",
                "src/string.spec.js",
            },
            single.Arguments);
        // The artifact filename keeps its unsuffixed pre-chunking form.
        Assert.DoesNotContain(".part", string.Join('\0', single.Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRunCommands_splits_a_wide_selection_under_the_cmd_shim_cap()
    {
        var workspace = Workspace("vitest");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var commands = provider.BuildRunCommands(Request(workspace, LongTestCaseIds(400)));

        Assert.True(commands.Count > 1, "a 400-file selection cannot fit one cmd.exe command line");
        foreach (var command in commands)
        {
            var length = CommandLineLength(command);
            Assert.True(
                length <= CmdShimCommandLineCap,
                $"invocation joined to {length} chars, over the {CmdShimCommandLineCap} cmd.exe cap");
        }
    }

    [Fact]
    public void BuildRunCommands_selects_every_requested_file_exactly_once_across_invocations()
    {
        var workspace = Workspace("vitest");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());
        var files = LongTestFiles(400);

        var commands = provider.BuildRunCommands(Request(workspace, LongTestCaseIds(400)));

        var selected = commands.SelectMany(SelectedFiles).ToArray();
        // Nothing dropped and nothing duplicated.
        Assert.Equal(files.Count, selected.Length);
        Assert.Equal(files.Count, selected.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(files.Order(StringComparer.Ordinal), selected.Order(StringComparer.Ordinal));
        // An invocation with no file argument runs the WHOLE suite, not the requested subset.
        Assert.All(commands, command => Assert.NotEmpty(SelectedFiles(command)));
    }

    [Fact]
    public void BuildRunCommands_gives_each_invocation_its_own_result_artifact_path()
    {
        var workspace = Workspace("vitest");
        var provider = new JavaScriptTestProvider(new FakeTestProcessRunner());

        var commands = provider.BuildRunCommands(Request(workspace, LongTestCaseIds(400)));

        Assert.True(commands.Count > 1);
        var artifacts = commands.Select(OutputFile).ToArray();
        // One shared path would let the last invocation overwrite every earlier chunk's report.
        Assert.Equal(artifacts.Length, artifacts.Distinct(StringComparer.Ordinal).Count());
        Assert.All(artifacts, artifact => Assert.Contains(".part", artifact, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_merges_chunked_invocations_and_the_worst_status_wins()
    {
        var workspace = Workspace("jest");
        var files = LongTestFiles(400);
        var failingFile = files[files.Count - 1];
        var request = Request(workspace, LongTestCaseIds(400));
        var runner = new FakeTestProcessRunner();
        runner.OnRun = command => WriteJestArtifact(command, failingFile);
        var provider = new JavaScriptTestProvider(runner);
        var commandCount = provider.BuildRunCommands(request).Count;
        for (var index = 0; index < commandCount; index++)
            runner.Enqueue(exitCode: 0);

        var result = await provider.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(runner.Calls.Count > 1, "the selection must have been split");
        // The red last chunk must never be masked by its green siblings.
        Assert.Equal("failed", result.Status);
        Assert.Equal(files.Count, result.CaseResults.Count);
        Assert.Equal(
            files.Count,
            result.CaseResults.Select(row => row.TestCaseId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            new[] { TestCaseIdFor(failingFile) },
            result.CaseResults.Where(row => row.Status == "failed").Select(row => row.TestCaseId));
    }

    [Fact]
    public async Task Run_reports_only_the_failed_chunks_own_selection_as_failed()
    {
        var workspace = Workspace("jest");
        var files = LongTestFiles(400);
        var request = Request(workspace, LongTestCaseIds(400));
        var runner = new FakeTestProcessRunner();
        var provider = new JavaScriptTestProvider(runner);
        var commands = provider.BuildRunCommands(request);
        Assert.True(commands.Count > 1, "the selection must have been split");

        // Every chunk but the last writes a green report. The last exits 1 and writes nothing at all -
        // exactly what a .cmd shim does when it refuses the command line.
        var call = 0;
        runner.OnRun = command =>
        {
            call++;
            if (call < commands.Count)
                WriteJestArtifact(command, failingFile: null);
        };
        for (var index = 0; index < commands.Count; index++)
            runner.Enqueue(exitCode: index == commands.Count - 1 ? 1 : 0);

        var result = await provider.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.Equal(files.Count, result.CaseResults.Count);
        Assert.Equal(
            SelectedFiles(commands[commands.Count - 1]).Select(TestCaseIdFor).Order(StringComparer.Ordinal),
            result.CaseResults
                .Where(row => row.Status == "failed")
                .Select(row => row.TestCaseId)
                .Order(StringComparer.Ordinal));
    }

    private static int CommandLineLength(TestProcessCommand command) =>
        command.FileName.Length + 1 + command.Arguments.Sum(argument => argument.Length + 1);

    /// <summary>Shaped like a real Vue/Jest suite path: 59 characters, ~400 files in a repo.</summary>
    private static IReadOnlyList<string> LongTestFiles(int count) =>
        Enumerable.Range(0, count)
            .Select(index =>
                "tests/unit/components/AccountViewSelectorScreen"
                + index.ToString("D4", CultureInfo.InvariantCulture)
                + ".spec.ts")
            .ToArray();

    private static string[] LongTestCaseIds(int count) =>
        LongTestFiles(count).Select(TestCaseIdFor).ToArray();

    private static string TestCaseIdFor(string relativePath) => "js-test:" + relativePath;

    private static IReadOnlyList<string> SelectedFiles(TestProcessCommand command) =>
        command.Arguments
            .Where(argument => argument.EndsWith(".spec.ts", StringComparison.Ordinal))
            .ToArray();
    private static string SelectedNodeFile(TestProcessCommand command) =>
        Assert.Single(SelectedNodeFiles(command));

    private static IReadOnlyList<string> SelectedNodeFiles(TestProcessCommand command) =>
        command.Arguments
            .Where(argument => argument.EndsWith(".test.js", StringComparison.Ordinal))
            .ToArray();

    private void WriteUnattributedNodeJunitArtifact(TestProcessCommand command)
    {
        var outputPath = command.Arguments[command.Arguments.ToList().IndexOf("--test-reporter-destination") + 1];
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(
            outputPath,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <testsuites>
              <testcase name="one" time="0.001" classname="test" />
              <testcase name="two" time="0.001" classname="test">
                <failure type="testCodeFailure" message="failure">failure</failure>
              </testcase>
            </testsuites>
            """);
    }


    private static string OutputFile(TestProcessCommand command) =>
        command.Arguments[command.Arguments.ToList().IndexOf("--outputFile") + 1];

    private void WriteJestArtifact(TestProcessCommand command, string? failingFile)
    {
        var outputPath = OutputFile(command);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var entries = SelectedFiles(command)
            .Select(file => JestFileResult(file, failed: string.Equals(file, failingFile, StringComparison.Ordinal)));
        File.WriteAllText(outputPath, $$"""{"testResults":[{{string.Join(",", entries)}}]}""");
    }

    private string JestFileResult(string relativePath, bool failed) =>
        $$"""{"name": "{{JsonPath(relativePath)}}", "status": "{{(failed ? "failed" : "passed")}}"}""";

    private string JsonPath(string relativePath) =>
        Path.Combine(PackageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))
            .Replace("\\", "\\\\", StringComparison.Ordinal);

    private string PackageRoot => Path.Combine(_dir, "package");

    private ContinuousTestWorkspace Workspace(string? framework)
    {
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:1",
            WorkspaceRoot: PackageRoot,
            ProjectPath: Path.Combine(PackageRoot, "package.json"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build", framework ?? "auto"),
            Framework: framework);
        if (framework is "jest" or "vitest")
        {
            var version = framework == "jest" ? "29.0.0" : "4.0.0";
            WritePackageFile(
                "package.json",
                "{\"devDependencies\":{\"" + framework + "\":\"^" + version + "\"}}");
        }
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        return workspace;
    }

    private ContinuousTestProviderRunRequest Request(ContinuousTestWorkspace workspace, params string[] testCaseIds) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-1",
            IndexIdentity: IndexIdentity,
            RunId: RunId,
            TestCaseIds: testCaseIds);

    /// <summary>
    /// The unsuffixed result-artifact path a single-invocation run is expected to write, derived here
    /// from the run id. Reading the path back off the command under test would agree with whatever the
    /// provider chose, including a ".part000" suffix a chunked build should never put on a run of one.
    /// </summary>
    private static string ExpectedResultArtifactPath(CtGenerationPaths generation, string extension)
    {
        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RunId))).ToLowerInvariant();
        return Path.Combine(generation.ResultsDirectory, $"run-{runHash}.{extension}");
    }

    private void WritePackageFile(string relativePath, string contents)
    {
        var path = Path.Combine(PackageRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    /// <summary>
    /// Writes the manifest npm leaves at <c>node_modules/&lt;name&gt;/package.json</c>. That file is the
    /// only honest statement of which version is installed: a dependency range in the workspace manifest
    /// names what was asked for, not what the install resolved.
    /// </summary>
    private void WriteInstalledPackage(string name, string version) =>
        WritePackageFile(
            Path.Combine("node_modules", name, "package.json"),
            $$"""{"name":"{{name}}","version":"{{version}}"}""");

    /// <summary>
    /// The launchable shim npm, pnpm and yarn write into <c>node_modules/.bin</c> for an installed runner.
    /// Its presence is what makes a direct invocation possible at all.
    /// </summary>
    private void WriteLocalBin(string name) =>
        WritePackageFile(
            Path.Combine("node_modules", ".bin", name + (OperatingSystem.IsWindows() ? ".cmd" : "")),
            "");

    private string LocalBin(string name) =>
        Path.Combine(PackageRoot, "node_modules", ".bin", name + (OperatingSystem.IsWindows() ? ".cmd" : ""));

    private static void WriteEmptyJestArtifact(TestProcessCommand command)
    {
        var outputIndex = command.Arguments.ToList().IndexOf("--outputFile");
        if (outputIndex < 0)
            return;

        var outputPath = command.Arguments[outputIndex + 1];
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, """{"testResults":[]}""");
    }

    private static void AssertUsesGeneration(
        TestProcessCommand command,
        ContinuousTestWorkspace workspace,
        CtGenerationPaths generation,
        string artifactPath)
    {
        Assert.Equal(generation.TempDirectory, command.Environment["TMPDIR"]);
        Assert.Equal(generation.TempDirectory, command.Environment["TMP"]);
        Assert.Equal(generation.TempDirectory, command.Environment["TEMP"]);
        Assert.True(Directory.Exists(generation.TempDirectory));
        Assert.Equal(workspace.WorkspaceRoot, command.Environment[CtEnvironment.WorkspaceRoot]);
        Assert.StartsWith(generation.ResultsDirectory, artifactPath, StringComparison.Ordinal);
        AssertWorkspaceIsolation(workspace);
    }

    private static void AssertWorkspaceIsolation(ContinuousTestWorkspace workspace)
    {
        var repoBin = Path.Combine(workspace.WorkspaceRoot, "bin");
        var repoObj = Path.Combine(workspace.WorkspaceRoot, "obj");
        var repoTestResults = Path.Combine(workspace.WorkspaceRoot, "TestResults");
        Assert.False(Directory.Exists(repoBin) && Directory.EnumerateFileSystemEntries(repoBin).Any());
        Assert.False(Directory.Exists(repoObj) && Directory.EnumerateFileSystemEntries(repoObj).Any());
        Assert.False(Directory.Exists(repoTestResults));
    }

    // PROJECT-stable, not per-generation: a fresh cache directory per operation made every run a cold
    // compile (finding F7). It sits beside the generations under the build output root.
    private static string CacheDirectory(ContinuousTestWorkspace workspace) =>
        CtGenerationPaths.CacheDirectory(workspace, "node");

    private static CtGenerationPaths FirstGeneration(ContinuousTestWorkspace workspace) =>
        CtGenerationPaths.For(workspace, CtGenerationPaths.IdForOrdinal(workspace, 1));

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
