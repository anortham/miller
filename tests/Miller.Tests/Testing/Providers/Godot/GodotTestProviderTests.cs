using Miller.Testing;
using Miller.Testing.Providers.Godot;
using Miller.Tests.Testing.Providers.Dotnet;
using System.Text;
using Xunit;

namespace Miller.Tests.Testing.Providers.Godot;

[Collection("GodotEnvironment")]
public sealed class GodotTestProviderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-godot-provider-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Discover_returns_exact_script_cases_without_starting_a_process()
    {
        WriteProject("""
            {
              "dirs": ["tests"],
              "include_subdirs": true
            }
            """);
        WriteFile("tests/test_math.gd", "extends Node\n");
        WriteFile("tests/nested/test_other.gd", "extends Node\n");

        var runner = new FakeTestProcessRunner();
        IReadOnlyList<ProviderTestCase> cases = await new GodotTestProvider(runner).DiscoverAsync(
            Workspace(),
            TestContext.Current.CancellationToken);

        Assert.Equal(["res://tests/nested/test_other.gd", "res://tests/test_math.gd"],
            cases.Select(test => test.Selector).ToArray());
        Assert.All(cases, test =>
        {
            Assert.StartsWith("gut:res://", test.Id, StringComparison.Ordinal);
            Assert.Equal(test.Selector, test.DisplayName);
            Assert.Equal(test.Selector, test.FullyQualifiedName);
            Assert.Equal("gut", test.Framework);
            Assert.EndsWith(".gd", test.SourcePath, StringComparison.Ordinal);
        });
        Assert.Equal("tests/nested/test_other.gd", cases[0].SourcePath);
        Assert.Equal("tests/test_math.gd", cases[1].SourcePath);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Run_imports_once_then_uses_exact_config_and_aggregates_junit_rows()
    {
        WriteProject("""
            {
              "dirs": ["tests"],
              "include_subdirs": false,
              "log_level": 2
            }
            """);
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_math.gd", "extends Node\n");
        string sourceBefore = File.ReadAllText(Path.Combine(_root, "tests", "test_math.gd"));
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue(exitCode: 1);
            runner.OnRun = command =>
            {
                if (command.Arguments.Contains("-s"))
                {
                    string mirror = command.Arguments[command.Arguments.ToList().IndexOf("--path") + 1];
                    string reportArgument = command.Arguments.Single(argument => argument.StartsWith("-gjunit_xml_file=", StringComparison.Ordinal));
                    string report = Path.Combine(mirror, reportArgument["-gjunit_xml_file=res://".Length..].Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    File.WriteAllText(report, GutJUnit);
                }
            };

            ProviderRunResult result = await new GodotTestProvider(runner).RunAsync(
                Request(["gut:res://tests/test_math.gd"]),
                TestContext.Current.CancellationToken);

            Assert.Equal("failed", result.Status);
            ProviderCaseResult row = Assert.Single(result.CaseResults);
            Assert.Equal("gut:res://tests/test_math.gd", row.TestCaseId);
            Assert.Equal("failed", row.Status);
            Assert.Contains("Cannot compare", row.FailureSummary, StringComparison.Ordinal);
            Assert.Equal(IndexIdentity, row.IndexIdentity);
            Assert.Equal("rev-godot", row.ResultRevision);
            Assert.True((double)row.Metadata["mirror_elapsed_ms"]! >= 0);
            Assert.True((double)row.Metadata["import_duration_ms"]! >= 0);
            Assert.True((double)row.Metadata["gut_duration_ms"]! >= 0);
            Assert.True((double)row.Metadata["report_copy_duration_ms"]! >= 0);
            Assert.True((long)row.Metadata["project_candidate_bytes"]! > 0);
            Assert.True((long)row.Metadata["godot_home_bytes"]! > 0);
            Assert.Equal(3, runner.Calls.Count);
            Assert.Equal(["--version"], runner.Calls[0].Arguments);
            Assert.Contains("--import", runner.Calls[1].Arguments);
            Assert.Contains("-s", runner.Calls[2].Arguments);
            Assert.DoesNotContain(runner.Calls[2].Arguments, argument => argument.Contains("test_math", StringComparison.Ordinal));
            string configPath = Path.Combine(runner.Calls[2].WorkingDirectory, ".miller-gut-results", "miller.gutconfig.json");
            Assert.True(File.Exists(configPath));
            string config = File.ReadAllText(configPath);
            Assert.Contains("res://tests/test_math.gd", config, StringComparison.Ordinal);
            Assert.DoesNotContain("res://user.xml", config, StringComparison.Ordinal);
            Assert.Equal(sourceBefore, File.ReadAllText(Path.Combine(_root, "tests", "test_math.gd")));
            Assert.NotNull(result.ResultArtifactPath);
            Assert.True(File.Exists(result.ResultArtifactPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Warm_run_reuses_the_import_stamp_and_skips_import()
    {
        WriteProject("{\"tests\":[\"res://tests/test_math.gd\"]}");
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_math.gd", "extends Node\n");
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.OnRun = WritePassingReport;
            var provider = new GodotTestProvider(runner);
            ContinuousTestWorkspace workspace = Workspace();
            await provider.RunAsync(Request("gut:res://tests/test_math.gd"), TestContext.Current.CancellationToken);
            GodotProjectShadowResult warmShadow = GodotProjectShadow.Sync(workspace, TestContext.Current.CancellationToken);
            Assert.False(GodotProjectShadow.NeedsImport(warmShadow));
            ProviderRunResult warm = await provider.RunAsync(
                Request("gut:res://tests/test_math.gd") with { RunId = "run-godot-warm" },
                TestContext.Current.CancellationToken);

            Assert.Equal(5, runner.Calls.Count);
            Assert.Equal(["--version"], runner.Calls[0].Arguments);
            Assert.Contains("--import", runner.Calls[1].Arguments);
            Assert.Equal(["--version"], runner.Calls[3].Arguments);
            Assert.Contains("-s", runner.Calls[4].Arguments);
            Assert.DoesNotContain(runner.Calls[4].Arguments, argument => argument == "--import");
            Assert.False((bool)Assert.Single(warm.CaseResults).Metadata["imported"]!);
            Assert.Equal(0L, (long)Assert.Single(warm.CaseResults).Metadata["mirror_bytes_copied"]!);
            Assert.Equal(0L, (long)Assert.Single(warm.CaseResults).Metadata["mirror_bytes_hashed"]!);
            Assert.Equal(workspace.BuildOutputRoot, Path.GetDirectoryName(CtGenerationPaths.CacheRoot(workspace)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_rejects_a_replaced_project_candidate_before_touching_activity()
    {
        WriteProject("{\"tests\":[\"res://tests/test_math.gd\"]}");
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_math.gd", "extends Node\n");
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        string outside = Path.Combine(_root, "outside-candidate");
        Directory.CreateDirectory(outside);
        string sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.OnRun = command =>
            {
                if (!command.Arguments.Contains("--version"))
                    return;
                string candidate = Directory.GetParent(command.WorkingDirectory)!.FullName;
                Directory.Delete(candidate, recursive: true);
                try
                {
                    Directory.CreateSymbolicLink(candidate, outside);
                }
                catch (UnauthorizedAccessException)
                {
                    Assert.Skip("Symbolic directory links are unavailable on this host.");
                }
                catch (IOException)
                {
                    Assert.Skip("Symbolic directory links are unavailable on this host.");
                }
            };

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new GodotTestProvider(runner).RunAsync(
                    Request("gut:res://tests/test_math.gd"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.False(File.Exists(Path.Combine(outside, ".last-used")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_rejects_a_replaced_project_activity_marker_before_touching_it()
    {
        WriteProject("{\"tests\":[\"res://tests/test_math.gd\"]}");
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_math.gd", "extends Node\n");
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        string outsideMarker = Path.Combine(_root, "outside-marker.txt");
        File.WriteAllText(outsideMarker, "keep");
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue();
            runner.OnRun = command =>
            {
                string candidate = Directory.GetParent(command.WorkingDirectory)!.FullName;
                if (command.Arguments.Contains("--version"))
                {
                    try
                    {
                        File.CreateSymbolicLink(Path.Combine(candidate, ".last-used"), outsideMarker);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Assert.Skip("Symbolic links are unavailable on this host.");
                    }
                    catch (IOException)
                    {
                        Assert.Skip("Symbolic links are unavailable on this host.");
                    }
                }
                else
                {
                    WritePassingReport(command);
                }
            };

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new GodotTestProvider(runner).RunAsync(
                    Request("gut:res://tests/test_math.gd"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("keep", File.ReadAllText(outsideMarker));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_rejects_nested_results_reparse_points_before_cleanup()
    {
        WriteProject("{\"tests\":[\"res://tests/test_math.gd\"]}");
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_math.gd", "extends Node\n");
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        string outside = Path.Combine(_root, "outside-results");
        Directory.CreateDirectory(outside);
        string sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue();
            runner.OnRun = command =>
            {
                string resultsRoot = Path.Combine(command.WorkingDirectory, ".miller-gut-results");
                if (command.Arguments.Contains("--version"))
                {
                    Directory.CreateDirectory(resultsRoot);
                    try
                    {
                        Directory.CreateSymbolicLink(Path.Combine(resultsRoot, "nested"), outside);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Assert.Skip("Symbolic directory links are unavailable on this host.");
                    }
                    catch (IOException)
                    {
                        Assert.Skip("Symbolic directory links are unavailable on this host.");
                    }
                }
                else
                {
                    WritePassingReport(command);
                }
            };

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new GodotTestProvider(runner).RunAsync(
                    Request("gut:res://tests/test_math.gd"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_rejects_a_malformed_junit_report()
    {
        string godot = PrepareSingleScript();
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue();
            runner.OnRun = command =>
            {
                if (command.Arguments.Contains("--import"))
                    Directory.CreateDirectory(Path.Combine(command.WorkingDirectory, ".godot"));
                else if (command.Arguments.Contains("-s"))
                {
                    string report = Path.Combine(command.WorkingDirectory, ".miller-gut-results", "run.xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    File.WriteAllText(report, "<testsuite>");
                }
            };

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new GodotTestProvider(runner).RunAsync(
                    Request("gut:res://tests/test_math.gd"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("malformed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(exception.ResultArtifactPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_rejects_an_unsupported_GUT_exit_code()
    {
        string godot = PrepareSingleScript();
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue(exitCode: 2);
            runner.OnRun = WritePassingReport;

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new GodotTestProvider(runner).RunAsync(
                    Request("gut:res://tests/test_math.gd"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("unsupported code", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(exception.ResultArtifactPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_rejects_exit_one_when_junit_has_no_failure()
    {
        string godot = PrepareSingleScript();
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue(exitCode: 1);
            runner.OnRun = WritePassingReport;

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new GodotTestProvider(runner).RunAsync(
                    Request("gut:res://tests/test_math.gd"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("no failure", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(exception.ResultArtifactPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_rejects_an_unexpected_extra_junit_report()
    {
        string godot = PrepareSingleScript();
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue();
            runner.OnRun = command =>
            {
                if (command.Arguments.Contains("--import"))
                {
                    Directory.CreateDirectory(Path.Combine(command.WorkingDirectory, ".godot"));
                }
                else if (command.Arguments.Contains("-s"))
                {
                    string resultsRoot = Path.Combine(command.WorkingDirectory, ".miller-gut-results");
                    Directory.CreateDirectory(resultsRoot);
                    File.WriteAllText(Path.Combine(resultsRoot, "run.xml"), PassingJUnit);
                    File.WriteAllText(Path.Combine(resultsRoot, "unexpected.xml"), PassingJUnit);
                }
            };

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new GodotTestProvider(runner).RunAsync(
                    Request("gut:res://tests/test_math.gd"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("unexpected or duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(exception.ResultArtifactPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_rejects_a_nested_report_reparse_point_without_touching_outside()
    {
        string godot = PrepareSingleScript();
        string outside = Path.Combine(_root, "outside-report");
        Directory.CreateDirectory(outside);
        string sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue();
            runner.OnRun = command =>
            {
                if (command.Arguments.Contains("--import"))
                {
                    Directory.CreateDirectory(Path.Combine(command.WorkingDirectory, ".godot"));
                }
                else if (command.Arguments.Contains("-s"))
                {
                    string resultsRoot = Path.Combine(command.WorkingDirectory, ".miller-gut-results");
                    Directory.CreateDirectory(resultsRoot);
                    File.WriteAllText(Path.Combine(resultsRoot, "run.xml"), PassingJUnit);
                    try
                    {
                        Directory.CreateSymbolicLink(Path.Combine(resultsRoot, "nested"), outside);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Assert.Skip("Symbolic directory links are unavailable on this host.");
                    }
                    catch (IOException)
                    {
                        Assert.Skip("Symbolic directory links are unavailable on this host.");
                    }
                }
            };

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new GodotTestProvider(runner).RunAsync(
                    Request("gut:res://tests/test_math.gd"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Empty_focused_selection_fails_before_any_process_starts()
    {
        WriteProject("{}");
        var runner = new FakeTestProcessRunner();

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new GodotTestProvider(runner).RunAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Contains("empty selection", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Empty_whole_suite_inventory_returns_no_results_without_a_process()
    {
        WriteProject("{}");
        var runner = new FakeTestProcessRunner();

        ProviderRunResult result = await new GodotTestProvider(runner).RunAsync(
            Request() with { WholeSuite = true },
            TestContext.Current.CancellationToken);

        Assert.Empty(result.CaseResults);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Run_rejects_missing_report_and_stamps_the_generation()
    {
        WriteProject("{\"tests\":[\"res://tests/test_math.gd\"]}");
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_math.gd", "extends Node\n");
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue();
            runner.OnRun = command =>
            {
                if (command.Arguments.Contains("--import"))
                {
                    string mirror = command.Arguments[command.Arguments.ToList().IndexOf("--path") + 1];
                    Directory.CreateDirectory(Path.Combine(mirror, ".godot"));
                }
            };

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new GodotTestProvider(runner).RunAsync(
                    Request("gut:res://tests/test_math.gd"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("expected JUnit", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(exception.GenerationId);
            Assert.Null(exception.ResultArtifactPath);
            Assert.Equal(3, runner.Calls.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_rejects_exit_zero_when_junit_contains_a_failure()
    {
        WriteProject("{\"tests\":[\"res://tests/test_math.gd\"]}");
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_math.gd", "extends Node\n");
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue();
            runner.OnRun = command =>
            {
                string mirror = command.Arguments[command.Arguments.ToList().IndexOf("--path") + 1];
                if (command.Arguments.Contains("--import"))
                {
                    Directory.CreateDirectory(Path.Combine(mirror, ".godot"));
                }
                else if (command.Arguments.Contains("-s"))
                {
                    string report = Path.Combine(mirror, ".miller-gut-results", "run.xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    File.WriteAllText(report, GutJUnit);
                }
            };

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new GodotTestProvider(runner).RunAsync(
                    Request("gut:res://tests/test_math.gd"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("exited successfully", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(exception.ResultArtifactPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_rejects_an_unattributed_report_row()
    {
        WriteProject("{\"tests\":[\"res://tests/test_math.gd\"]}");
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_math.gd", "extends Node\n");
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue();
            runner.OnRun = command =>
            {
                string mirror = command.Arguments[command.Arguments.ToList().IndexOf("--path") + 1];
                if (command.Arguments.Contains("--import"))
                    Directory.CreateDirectory(Path.Combine(mirror, ".godot"));
                if (command.Arguments.Contains("-s"))
                {
                    string report = Path.Combine(mirror, ".miller-gut-results", "run.xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    File.WriteAllText(report, """
                        <testsuite name="res://tests/other.gd" tests="1" failures="0">
                          <testcase name="test_other" status="pass" classname="res://tests/other.gd" />
                        </testsuite>
                        """);
                }
            };

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new GodotTestProvider(runner).RunAsync(
                    Request("gut:res://tests/test_math.gd"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("not selected", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(exception.ResultArtifactPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_attributes_inner_class_rows_to_the_script_and_maps_pending_to_skipped()
    {
        WriteProject("{\"tests\":[\"res://tests/test_math.gd\"]}");
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_math.gd", "extends Node\n");
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue();
            runner.OnRun = command =>
            {
                string mirror = command.Arguments[command.Arguments.ToList().IndexOf("--path") + 1];
                if (command.Arguments.Contains("--import"))
                    Directory.CreateDirectory(Path.Combine(mirror, ".godot"));
                else if (command.Arguments.Contains("-s"))
                {
                    string report = Path.Combine(mirror, ".miller-gut-results", "run.xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    File.WriteAllText(report, """
                        <testsuite name="res://tests/test_math.gd" tests="2" failures="0" skipped="2">
                          <testcase name="test_inner" status="pending" classname="res://tests/test_math.gd::Inner" />
                          <testcase name="test_other" status="pending" classname="res://tests/test_math.gd.Inner" />
                        </testsuite>
                        """);
                }
            };

            ProviderRunResult result = await new GodotTestProvider(runner).RunAsync(
                Request("gut:res://tests/test_math.gd"),
                TestContext.Current.CancellationToken);

            Assert.Equal("skipped", result.Status);
            ProviderCaseResult row = Assert.Single(result.CaseResults);
            Assert.Equal("skipped", row.Status);
            Assert.Equal("gut:res://tests/test_math.gd", row.TestCaseId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Whole_suite_uses_every_discovered_script_in_the_derived_config()
    {
        WriteProject("{\"dirs\":[\"tests\"]}");
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_a.gd", "extends Node\n");
        WriteFile("tests/test_b.gd", "extends Node\n");
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue();
            runner.OnRun = command =>
            {
                string mirror = command.Arguments[command.Arguments.ToList().IndexOf("--path") + 1];
                if (command.Arguments.Contains("--import"))
                    Directory.CreateDirectory(Path.Combine(mirror, ".godot"));
                else if (command.Arguments.Contains("-s"))
                {
                    string report = Path.Combine(mirror, ".miller-gut-results", "run.xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    File.WriteAllText(report, """
                        <testsuite name="tests" tests="2" failures="0" skipped="0">
                          <testcase name="test_a" status="pass" classname="res://tests/test_a.gd" />
                          <testcase name="test_b" status="pass" classname="res://tests/test_b.gd" />
                        </testsuite>
                        """);
                }
            };

            ProviderRunResult result = await new GodotTestProvider(runner).RunAsync(
                Request() with { WholeSuite = true },
                TestContext.Current.CancellationToken);

            Assert.Equal("passed", result.Status);
            Assert.Equal(2, result.CaseResults.Count);
            TestProcessCommand command = Assert.Single(runner.Calls, call => call.Arguments.Contains("-s"));
            Assert.DoesNotContain(command.Arguments, argument => argument is "res://tests/test_a.gd" or "res://tests/test_b.gd");
            string mirror = command.Arguments[command.Arguments.ToList().IndexOf("--path") + 1];
            string config = File.ReadAllText(Path.Combine(mirror, ".miller-gut-results", "miller.gutconfig.json"));
            Assert.Contains("res://tests/test_a.gd", config, StringComparison.Ordinal);
            Assert.Contains("res://tests/test_b.gd", config, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_accepts_exit_one_when_only_one_of_multiple_scripts_failed()
    {
        WriteProject("{\"dirs\":[\"tests\"]}");
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_a.gd", "extends Node\n");
        WriteFile("tests/test_b.gd", "extends Node\n");
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue(exitCode: 1);
            runner.OnRun = command =>
            {
                string mirror = command.Arguments[command.Arguments.ToList().IndexOf("--path") + 1];
                if (command.Arguments.Contains("--import"))
                    Directory.CreateDirectory(Path.Combine(mirror, ".godot"));
                else if (command.Arguments.Contains("-s"))
                {
                    string report = Path.Combine(mirror, ".miller-gut-results", "run.xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    File.WriteAllText(report, """
                        <testsuite name="tests" tests="2" failures="1" skipped="0">
                          <testcase name="test_a" status="pass" classname="res://tests/test_a.gd" />
                          <testcase name="test_b" status="fail" classname="res://tests/test_b.gd">
                            <failure message="failed">broken</failure>
                          </testcase>
                        </testsuite>
                        """);
                }
            };

            ProviderRunResult result = await new GodotTestProvider(runner).RunAsync(
                Request("gut:res://tests/test_a.gd", "gut:res://tests/test_b.gd"),
                TestContext.Current.CancellationToken);

            Assert.Equal("failed", result.Status);
            Assert.Equal("passed", Assert.Single(result.CaseResults, row => row.TestCaseId.EndsWith("test_a.gd")).Status);
            Assert.Equal("failed", Assert.Single(result.CaseResults, row => row.TestCaseId.EndsWith("test_b.gd")).Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    [Fact]
    public async Task Run_rejects_duplicate_junit_rows()
    {
        WriteProject("{\"tests\":[\"res://tests/test_math.gd\"]}");
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_math.gd", "extends Node\n");
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            var runner = new FakeTestProcessRunner();
            runner.Enqueue("Godot Engine v4.7.2.stable\n");
            runner.Enqueue();
            runner.Enqueue();
            runner.OnRun = command =>
            {
                string mirror = command.Arguments[command.Arguments.ToList().IndexOf("--path") + 1];
                if (command.Arguments.Contains("--import"))
                    Directory.CreateDirectory(Path.Combine(mirror, ".godot"));
                else if (command.Arguments.Contains("-s"))
                {
                    string report = Path.Combine(mirror, ".miller-gut-results", "run.xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    File.WriteAllText(report, """
                        <testsuite name="res://tests/test_math.gd" tests="2" failures="0">
                          <testcase name="test_add" status="pass" classname="res://tests/test_math.gd" />
                          <testcase name="test_add" status="pass" classname="res://tests/test_math.gd" />
                        </testsuite>
                        """);
                }
            };

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new GodotTestProvider(runner).RunAsync(
                    Request("gut:res://tests/test_math.gd"),
                    TestContext.Current.CancellationToken));

            Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(exception.ResultArtifactPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
        }
    }

    private ContinuousTestWorkspace Workspace() => new(
        WorkspaceId: "workspace-godot",
        WorkspaceRoot: _root,
        ProjectPath: Path.Combine(_root, "project.godot"),
        BuildOutputRoot: Path.Combine(_root, ".miller", "ct-godot"),
        Framework: "gut");

    private void WriteProject(string config)
    {
        WriteFile("project.godot", "[application]\nconfig_version=5\n");
        WriteFile(".gutconfig.json", config);
    }

    private string PrepareSingleScript()
    {
        WriteProject("{\"tests\":[\"res://tests/test_math.gd\"]}");
        WriteFile("addons/gut/plugin.cfg", "[plugin]\nversion=\"9.7.1\"\n");
        WriteFile("tests/test_math.gd", "extends Node\n");
        string godot = Path.Combine(_root, "godot");
        File.WriteAllText(godot, string.Empty);
        return godot;
    }

    private void WriteFile(string relativePath, string contents)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static void WritePassingReport(TestProcessCommand command)
    {
        if (command.Arguments.Contains("--import"))
        {
            string importMirror = command.Arguments[command.Arguments.ToList().IndexOf("--path") + 1];
            Directory.CreateDirectory(Path.Combine(importMirror, ".godot"));
            return;
        }
        if (!command.Arguments.Contains("-s"))
            return;
        string mirror = command.Arguments[command.Arguments.ToList().IndexOf("--path") + 1];
        string report = Path.Combine(mirror, ".miller-gut-results", "run.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, """
            <testsuite name="res://tests/test_math.gd" tests="1" failures="0" skipped="0">
              <testcase name="test_add" status="pass" classname="res://tests/test_math.gd" />
            </testsuite>
            """);
    }

    private ContinuousTestProviderRunRequest Request(params string[] ids) =>
        new(Workspace(), "rev-godot", IndexIdentity, RunId: "run-godot", TestCaseIds: ids);

    private static readonly string IndexIdentity = "identity-godot";

    private const string PassingJUnit = """
        <testsuite name="res://tests/test_math.gd" tests="1" failures="0" skipped="0">
          <testcase name="test_add" status="pass" classname="res://tests/test_math.gd" />
        </testsuite>
        """;

    private const string GutJUnit = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites name="GutTests" tests="3" failures="1" skipped="1">
          <testsuite name="res://tests/test_math.gd" tests="3" failures="1" skipped="1">
            <testcase name="test_add" assertions="1" status="pass" classname="res://tests/test_math.gd"></testcase>
            <testcase name="test_subtract" assertions="1" status="fail" classname="res://tests/test_math.gd">
              <failure message="failed"><![CDATA[Cannot compare Int[3] to Int[2].]]></failure>
            </testcase>
            <testcase name="test_multiply" assertions="0" status="pending" classname="res://tests/test_math.gd">
              <skipped message="pending"></skipped>
            </testcase>
          </testsuite>
        </testsuites>
        """;
}
