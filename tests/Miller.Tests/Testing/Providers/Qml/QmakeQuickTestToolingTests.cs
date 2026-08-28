using Miller.Testing;
using Miller.Testing.Providers.Qml;
using Xunit;

namespace Miller.Tests.Testing.Providers.Qml;

public sealed class QmakeQuickTestToolingTests
{
    [Fact]
    public void ParseQmakeVersion_reads_qmake_and_qt_versions()
    {
        var version = QmakeQuickTestTooling.ParseQmakeVersion(
            new TestProcessResult(
                0,
                "QMake version 3.1\nUsing Qt version 6.7.2 in /opt/Qt/6.7.2/gcc_64\n",
                string.Empty));

        Assert.Equal(new QtVersion(6, 7, 2), version);
    }

    [Fact]
    public void ParseQmakeVersion_rejects_qt_versions_outside_the_supported_major_range()
    {
        var exception = Assert.Throws<ContinuousTestProviderException>(() =>
            QmakeQuickTestTooling.ParseQmakeVersion(
                new TestProcessResult(0, "QMake version 3.1\nUsing Qt version 4.8.7 in /opt/Qt\n", "")));

        Assert.Contains("Qt 5 or Qt 6", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseQmakeVersion_rejects_incomplete_probe_output()
    {
        var exception = Assert.Throws<ContinuousTestProviderException>(() =>
            QmakeQuickTestTooling.ParseQmakeVersion(
                new TestProcessResult(0, "QMake version 3.1\n", "")));

        Assert.Contains("complete", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildConfigureArguments_keep_the_project_and_makefile_in_generation_output()
    {
        const string projectPath = "/source dir/quicktest.pro";
        const string outputDirectory = "/generation/out";
        var arguments = QmakeQuickTestTooling.BuildConfigureArguments(projectPath, outputDirectory);

        Assert.Equal(
            ["-o", Path.Combine(outputDirectory, "Makefile"), Path.GetFullPath(projectPath)],
            arguments);
    }

    [Fact]
    public void BuildMakeArguments_probe_build_and_check_without_shell_joining()
    {
        Assert.Equal(["--version"], QmakeQuickTestTooling.BuildMakeVersionArguments());
        Assert.Equal([], QmakeQuickTestTooling.BuildBuildArguments());
        const string resultArtifactPath = "/generation/TestResults/run.xml";
        Assert.Equal(
            ["check", $"TESTARGS=-o {Path.GetFullPath(resultArtifactPath)},junitxml"],
            QmakeQuickTestTooling.BuildCheckArguments(resultArtifactPath, new QtVersion(6, 5, 0)));
    }

    [Fact]
    public void BuildMakeVersionArguments_use_nmake_help_on_windows_toolchains()
    {
        Assert.Equal(["/?"], QmakeQuickTestTooling.BuildMakeVersionArguments("nmake.exe"));
    }

    [Theory]
    [InlineData(5, "xunitxml")]
    [InlineData(6, "junitxml")]
    public void LoggerFormat_uses_the_qt_major_contract(int major, string expected)
    {
        Assert.Equal(expected, QmakeQuickTestTooling.LoggerFormat(new QtVersion(major, 0, 0)));
    }

    [Fact]
    public void BuildCheckArguments_rejects_a_result_path_outside_generation_results()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            QmakeQuickTestTooling.BuildCheckArguments("/tmp/result.xml", new QtVersion(6, 5, 0)));

        Assert.Contains("result", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCheckArguments_neutralizes_make_and_shell_expansion_in_result_paths()
    {
        string resultArtifactPath = Path.Combine(
            Path.GetTempPath(), "gen$1", "`echo", "TestResults", "run.xml");
        var arguments = QmakeQuickTestTooling.BuildCheckArguments(resultArtifactPath, new QtVersion(6, 5, 0));

        string testArgs = Assert.Single(arguments, argument =>
            argument.StartsWith("TESTARGS=", StringComparison.Ordinal));
        Assert.Contains("gen$$1", testArgs, StringComparison.Ordinal);
        Assert.Contains("`echo", testArgs, StringComparison.Ordinal);
        Assert.DoesNotContain("\\$$", testArgs, StringComparison.Ordinal);
        Assert.DoesNotContain("\\`", testArgs, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCheckArguments_uses_runtime_shell_safe_encoding_for_path_metacharacters()
    {
        string resultArtifactPath = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetTempPath(), "gen $`it's", "TestResults", "run.xml")
            : Path.Combine(Path.GetTempPath(), "gen\\\"\\`$(", "it's", "TestResults", "run.xml");
        string fullResultPath = Path.GetFullPath(resultArtifactPath);
        var arguments = QmakeQuickTestTooling.BuildCheckArguments(fullResultPath, new QtVersion(6, 5, 0));

        string testArgs = Assert.Single(arguments, argument =>
            argument.StartsWith("TESTARGS=", StringComparison.Ordinal));
        string makeSafePath = fullResultPath.Replace("$", "$$", StringComparison.Ordinal);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains($"-o \"{makeSafePath},junitxml\"", testArgs, StringComparison.Ordinal);
            Assert.Contains("`", testArgs, StringComparison.Ordinal);
            Assert.DoesNotContain("\\$$", testArgs, StringComparison.Ordinal);
        }
        else
        {
            string shellSafeValue = $"{makeSafePath},junitxml";
            string shellSafeValueWithQuotes =
                $"'{shellSafeValue.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
            Assert.Contains($"-o {shellSafeValueWithQuotes}", testArgs, StringComparison.Ordinal);
            Assert.Contains("gen\\\"\\`$$", testArgs, StringComparison.Ordinal);
            Assert.Contains("'\"'\"'", testArgs, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    public void BuildCheckArguments_rejects_line_breaks_in_paths(string lineBreak)
    {
        string resultArtifactPath = Path.Combine(
            Path.GetTempPath(), $"gen{lineBreak}", "TestResults", "run.xml");

        Assert.Throws<ArgumentException>(() =>
            QmakeQuickTestTooling.BuildCheckArguments(resultArtifactPath, new QtVersion(6, 5, 0)));
    }

    [Fact]
    public void BuildCheckArguments_quotes_windows_paths_with_spaces_and_parentheses()
    {
        string resultArtifactPath = Path.Combine(
            Path.GetTempPath(), "Program Files (x86)", "TestResults", "run.xml");
        string fullResultPath = Path.GetFullPath(resultArtifactPath);
        var arguments = QmakeQuickTestTooling.BuildCheckArguments(fullResultPath, new QtVersion(6, 5, 0));
        string testArgs = Assert.Single(arguments, argument =>
            argument.StartsWith("TESTARGS=", StringComparison.Ordinal));

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains($"-o \"{fullResultPath},junitxml\"", testArgs, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"-o '{fullResultPath},junitxml'", testArgs, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HasVariableValue_honors_the_append_unique_operator()
    {
        Assert.True(QmakeQuickTestTooling.HasVariableValue("CONFIG *= qmltestcase\n", "CONFIG", "qmltestcase"));
    }

    [Fact]
    public void TryReadProjectModel_splices_includes_in_qmake_inline_order()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-qmake-order-").FullName;
        try
        {
            string projectPath = Path.Combine(root, "app.pro");
            File.WriteAllText(Path.Combine(root, "vars.pri"), "CONFIG = release\n");
            File.WriteAllText(projectPath, "include(vars.pri)\nCONFIG += qmltestcase\n");

            Assert.True(QmakeQuickTestTooling.TryReadProjectModel(projectPath, out QmakeProjectModel? model));
            Assert.True(QmakeQuickTestTooling.HasVariableValue(model!.EffectiveText, "CONFIG", "qmltestcase"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryReadProjectModel_lets_a_reset_after_the_include_clear_earlier_values()
    {
        string root = Directory.CreateTempSubdirectory("miller-ct-qmake-reset-").FullName;
        try
        {
            string projectPath = Path.Combine(root, "app.pro");
            File.WriteAllText(Path.Combine(root, "vars.pri"), "CONFIG += qmltestcase\n");
            File.WriteAllText(projectPath, "include(vars.pri)\nCONFIG = release\n");

            Assert.True(QmakeQuickTestTooling.TryReadProjectModel(projectPath, out QmakeProjectModel? model));
            Assert.False(QmakeQuickTestTooling.HasVariableValue(model!.EffectiveText, "CONFIG", "qmltestcase"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
