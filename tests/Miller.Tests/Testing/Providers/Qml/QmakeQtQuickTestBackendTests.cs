using Miller.Testing;
using Miller.Testing.Providers.Qml;
using Xunit;

namespace Miller.Tests.Testing.Providers.Qml;

public sealed class QmakeQtQuickTestBackendTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-qmake-backend-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(Path.Combine(Path.GetTempPath(), "miller-ct"), recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task EnsureBuild_discovers_qmake_qt_make_and_builds_only_in_generation_output()
    {
        string project = Write("quicktest.pro", "TEMPLATE = app\nTARGET = tst_smoke\nCONFIG += qmltestcase\n");
        var runner = new ScriptedTestProcessRunner(command =>
        {
            if (command.FileName == "qmake" && command.Arguments.SequenceEqual(["-v"]))
                return new TestProcessResult(0, "QMake version 3.1\nUsing Qt version 6.7.2 in /opt/Qt\n", "");
            if (command.FileName == "qmake" && command.Arguments.SequenceEqual(["-query", "QT_VERSION"]))
                return new TestProcessResult(0, "6.7.2\n", "");
            if (command.FileName == "make" && command.Arguments.SequenceEqual(["--version"]))
                return new TestProcessResult(0, "GNU Make 4.4\n", "");
            if (command.FileName == "qmake")
            {
                File.WriteAllText(Path.Combine(command.WorkingDirectory, "Makefile"), "check:\nall:\n");
                return new TestProcessResult(0, "", "");
            }
            if (command.FileName == "make" && command.Arguments.Count == 0)
                return new TestProcessResult(0, "built\n", "");
            throw new Xunit.Sdk.XunitException($"unexpected command: {command.ToDisplayString()}");
        });
        var backend = new QmakeQtQuickTestBackend(runner, "qmake", "make");
        var paths = Paths();

        await backend.EnsureBuildAsync(Workspace(project), paths, TestContext.Current.CancellationToken);

        Assert.Equal(5, runner.Calls.Count);
        Assert.All(runner.Calls, command => Assert.Equal(paths.OutDir, command.WorkingDirectory));
        var configure = runner.Calls.Single(command => command.FileName == "qmake" && command.Arguments.Contains(project));
        Assert.Equal(["-o", Path.Combine(paths.OutDir, "Makefile"), project], configure.Arguments);
        Assert.False(File.Exists(Path.Combine(_root, "Makefile")));
        Assert.True(File.Exists(Path.Combine(paths.OutDir, "Makefile")));
    }

    [Fact]
    public async Task EnsureBuild_retries_a_failed_build_instead_of_reusing_a_partial_generation()
    {
        string project = Write("quicktest.pro", "TEMPLATE = app\nTARGET = tst_smoke\nCONFIG += qmltestcase\n");
        int builds = 0;
        var runner = BuildRunner(onBuild: () => builds++ == 0 ? 9 : 0);
        var backend = new QmakeQtQuickTestBackend(runner, "qmake", "make");
        var paths = Paths();
        var workspace = Workspace(project);

        await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            backend.EnsureBuildAsync(workspace, paths, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(paths.GenerationRoot, ".qt-version")));
        await backend.EnsureBuildAsync(workspace, paths, TestContext.Current.CancellationToken);

        Assert.Equal(2, builds);
        Assert.True(File.Exists(Path.Combine(paths.GenerationRoot, ".qt-version")));
    }

    [Fact]
    public async Task Discover_returns_one_stable_qmake_target_case()
    {
        string project = Write("quicktest.pro", "TEMPLATE = app\nTARGET = tst_smoke\nCONFIG += qmltestcase\n");
        var runner = BuildRunner();
        var backend = new QmakeQtQuickTestBackend(runner, "qmake", "make");
        var paths = Paths();
        var workspace = Workspace(project);

        await backend.EnsureBuildAsync(workspace, paths, TestContext.Current.CancellationToken);
        var first = await backend.DiscoverAsync(workspace, paths, TestContext.Current.CancellationToken);
        var second = await backend.DiscoverAsync(workspace, paths, TestContext.Current.CancellationToken);

        var test = Assert.Single(first);
        Assert.Equal("tst_smoke", test.Name);
        Assert.Equal(test.Name, Assert.Single(second).Name);
        Assert.Equal([Path.Combine(paths.OutDir, "tst_smoke")], test.Command);
        Assert.Equal("qmake", test.Metadata["backend"]);
    }

    [Fact]
    public async Task Discover_uses_target_and_import_paths_from_a_literal_pri_include()
    {
        string project = Write("quicktest.pro", "TEMPLATE = app\ninclude(settings.pri)\n");
        string imports = Path.Combine(_root, "imports");
        Directory.CreateDirectory(imports);
        Write("settings.pri", "TARGET = tst_from_pri\nIMPORTPATH += imports\nCONFIG += qmltestcase\n");
        var backend = new QmakeQtQuickTestBackend(BuildRunner(), "qmake", "make");
        var paths = Paths();
        var workspace = Workspace(project);

        await backend.EnsureBuildAsync(workspace, paths, TestContext.Current.CancellationToken);
        var test = Assert.Single(await backend.DiscoverAsync(workspace, paths, TestContext.Current.CancellationToken));

        Assert.Equal("tst_from_pri", test.Name);
        Assert.Equal([imports], Assert.IsAssignableFrom<IReadOnlyList<string>>(test.Metadata["imports"]));
    }

    [Theory]
    [InlineData("include($$PWD/settings.pri)")]
    [InlineData("include(../settings.pri)")]
    public async Task EnsureBuild_refuses_nonliteral_or_out_of_root_includes(string include)
    {
        string project = Write("quicktest.pro", $"TEMPLATE = app\nCONFIG += qmltestcase\n{include}\n");
        var runner = new ScriptedTestProcessRunner(_ =>
            throw new Xunit.Sdk.XunitException("qmake must not run for an unavailable project model"));
        var backend = new QmakeQtQuickTestBackend(runner, "qmake", "make");

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            backend.EnsureBuildAsync(Workspace(project), Paths(), TestContext.Current.CancellationToken));

        Assert.Contains("literal in-root .pri", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task EnsureBuild_refuses_an_oversized_included_pri_file()
    {
        string project = Write("quicktest.pro", "TEMPLATE = app\nCONFIG += qmltestcase\ninclude(settings.pri)\n");
        Write("settings.pri", new string('x', 64 * 1024 + 1));
        var runner = new ScriptedTestProcessRunner(_ =>
            throw new Xunit.Sdk.XunitException("qmake must not run for an oversized project model"));
        var backend = new QmakeQtQuickTestBackend(runner, "qmake", "make");

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            backend.EnsureBuildAsync(Workspace(project), Paths(), TestContext.Current.CancellationToken));

        Assert.Contains("literal in-root .pri", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(5, "xunitxml")]
    [InlineData(6, "junitxml")]
    public async Task Run_uses_version_correct_logger_offscreen_environment_and_result_paths(int major, string logger)
    {
        string project = Write("quicktest.pro", "TEMPLATE = app\nTARGET = tst_smoke\nCONFIG += qmltestcase\n");
        var runner = BuildRunner(major, command =>
        {
            if (command.FileName == "make" && command.Arguments.Contains("check"))
            {
                string result = command.Arguments.Single(argument => argument.StartsWith("TESTARGS=", StringComparison.Ordinal))["TESTARGS=".Length..];
                string path = result[3..result.IndexOf(',', 3)];
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "<testsuite name=\"qml\"><testcase classname=\"Smoke\" name=\"test_pass\" /></testsuite>");
            }
        });
        var backend = new QmakeQtQuickTestBackend(runner, "qmake", "make");
        var paths = Paths();
        var workspace = Workspace(project);
        await backend.EnsureBuildAsync(workspace, paths, TestContext.Current.CancellationToken);

        var result = await backend.RunAsync(
            Request(workspace),
            paths,
            Path.Combine(paths.ResultsDirectory, "run.xml"),
            ["tst_smoke"],
            wholeSuite: false,
            TestContext.Current.CancellationToken);

        var check = runner.Calls.Last(command => command.FileName == "make" && command.Arguments.Contains("check"));
        Assert.Equal(["check", $"TESTARGS=-o {Path.Combine(paths.ResultsDirectory, "run.xml")},{logger}"], check.Arguments);
        Assert.Equal("offscreen", check.Environment["QT_QPA_PLATFORM"]);
        Assert.Equal("passed", Assert.Single(result.Cases).Status);
    }

    [Fact]
    public async Task Run_rejects_nonzero_exit_without_a_result_and_malformed_results()
    {
        string project = Write("quicktest.pro", "TEMPLATE = app\nTARGET = tst_smoke\nCONFIG += qmltestcase\n");
        bool malformed = false;
        var runner = BuildRunner(6, command =>
        {
            if (command.FileName == "make" && command.Arguments.Contains("check"))
            {
                if (malformed)
                {
                    string result = command.Arguments.Single(argument => argument.StartsWith("TESTARGS=", StringComparison.Ordinal))["TESTARGS=".Length..];
                    string path = result[3..result.IndexOf(',', 3)];
                    File.WriteAllText(path, "<testsuite>");
                }
            }
        }, exitCode: 7);
        var backend = new QmakeQtQuickTestBackend(runner, "qmake", "make");
        var paths = Paths();
        var workspace = Workspace(project);
        await backend.EnsureBuildAsync(workspace, paths, TestContext.Current.CancellationToken);
        var missing = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => backend.RunAsync(
            Request(workspace), paths, Path.Combine(paths.ResultsDirectory, "missing.xml"), ["tst_smoke"], false, TestContext.Current.CancellationToken));
        Assert.Contains("exit code 7", missing.Message, StringComparison.Ordinal);

        malformed = true;
        var parse = await Assert.ThrowsAsync<ContinuousTestProviderException>(() => backend.RunAsync(
            Request(workspace), paths, Path.Combine(paths.ResultsDirectory, "malformed.xml"), ["tst_smoke"], false, TestContext.Current.CancellationToken));
        Assert.Contains("could not be parsed", parse.Message, StringComparison.Ordinal);
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private ContinuousTestWorkspace Workspace(string project) => new(
        "ws:qmake",
        _root,
        project,
        Path.Combine(_root, ".miller", "ct-qmake"),
        Framework: "qt-quick-test",
        Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["backend"] = "qmake",
            ["configure_root"] = _root,
            ["evidence_root"] = _root,
            ["project_id"] = "qmake",
        });

    private CtGenerationPaths Paths()
    {
        var paths = CtGenerationPaths.Allocate(Workspace(Path.Combine(_root, "quicktest.pro")));
        paths.EnsureDirectories();
        return paths;
    }

    private ScriptedTestProcessRunner BuildRunner(
        int major = 6,
        Action<TestProcessCommand>? onCheck = null,
        int exitCode = 0,
        Func<int>? onBuild = null)
    {
        return new ScriptedTestProcessRunner(command =>
        {
            if (command.FileName == "qmake" && command.Arguments.SequenceEqual(["-v"]))
                return new TestProcessResult(0, $"QMake version 3.1\nUsing Qt version {major}.7.2 in /opt/Qt\n", "");
            if (command.FileName == "qmake" && command.Arguments.SequenceEqual(["-query", "QT_VERSION"]))
                return new TestProcessResult(0, $"{major}.7.2\n", "");
            if (command.FileName == "make" && command.Arguments.SequenceEqual(["--version"]))
                return new TestProcessResult(0, "GNU Make 4.4\n", "");
            if (command.FileName == "qmake")
            {
                File.WriteAllText(Path.Combine(command.WorkingDirectory, "Makefile"), "check:\nall:\n");
                return new TestProcessResult(0, "", "");
            }
            if (command.FileName == "make" && command.Arguments.Contains("check"))
            {
                onCheck?.Invoke(command);
                return new TestProcessResult(exitCode, "", exitCode == 0 ? "" : "check failed");
            }
            if (command.FileName == "make")
                return new TestProcessResult(onBuild?.Invoke() ?? 0, "built\n", "");
            throw new Xunit.Sdk.XunitException($"unexpected command: {command.ToDisplayString()}");
        });
    }

    private static ContinuousTestProviderRunRequest Request(ContinuousTestWorkspace workspace) => new(
        workspace,
        "rev-qmake",
        "index-qmake",
        "run:qmake",
        [QtQuickTestProvider.TestCaseId(workspace, "tst_smoke")]);
}
