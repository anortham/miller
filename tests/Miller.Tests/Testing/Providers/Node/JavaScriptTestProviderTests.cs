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
            CacheDirectory(generation),
            command.Arguments[command.Arguments.ToList().IndexOf("--cache.dir") + 1]);
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
            CacheDirectory(generation),
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
        Assert.Equal(CacheDirectory(generation), command.Environment["NODE_COMPILE_CACHE"]);
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
                CacheDirectory(generation),
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

    private static string CacheDirectory(CtGenerationPaths generation) =>
        Path.Combine(generation.GenerationRoot, "cache");

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
