using System.Text.RegularExpressions;
using Miller.Testing;
using Miller.Testing.Providers.Qml;
using Xunit;

namespace Miller.Tests.Testing.Providers.Qml;

public sealed class CTestDiscoveryParserTests
{
    [Fact]
    public void ParseCMakeVersion_accepts_complete_supported_output()
    {
        var version = QtQuickTestTooling.ParseCMakeVersion(
            "cmake version 3.27.9\n\nCMake suite maintained and supported by Kitware (kitware.com/cmake).\n");

        Assert.Equal(new CMakeVersion(3, 27, 9), version);
        Assert.True(version.IsSupported);
    }

    [Fact]
    public void ParseCMakeVersion_rejects_versions_below_the_floor()
    {
        var exception = Assert.Throws<ContinuousTestProviderException>(() =>
            QtQuickTestTooling.ParseCMakeVersion("cmake version 3.20.6\n"));

        Assert.Contains("3.21", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCMakeVersion_rejects_incomplete_output()
    {
        var exception = Assert.Throws<ContinuousTestProviderException>(() =>
            QtQuickTestTooling.ParseCMakeVersion("cmake version 3.21\n"));

        Assert.Contains("complete", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseCMakeVersion_requires_complete_process_output()
    {
        var exception = Assert.Throws<ContinuousTestProviderException>(() =>
            QtQuickTestTooling.ParseCMakeVersion(
                new TestProcessResult(0, "cmake version 3.27.9\n", "", StandardOutputTruncated: true)));

        Assert.Contains("partial", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseCMakeVersion_rejects_a_failed_probe()
    {
        var exception = Assert.Throws<ContinuousTestProviderException>(() =>
            QtQuickTestTooling.ParseCMakeVersion(new TestProcessResult(1, "", "cmake not found")));

        Assert.Contains("exit code 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_orders_targets_and_preserves_unicode_labels_commands_and_bounded_metadata()
    {
        var discovery = CTestDiscoveryParser.Parse(
            """
            {
              "kind": "ctestInfo",
              "version": {"major": 1, "minor": 0},
              "tests": [
                {
                  "name": "Z/π (smoke)",
                  "command": ["/tmp/qt runner", "--case", "π"],
                  "properties": [
                    {"name": "LABELS", "value": ["qml", "smoke"]},
                    {"name": "WORKING_DIRECTORY", "value": "/tmp/qt"},
                    {"name": "TIMEOUT", "value": 30},
                    {"name": "WILL_FAIL", "value": false}
                  ]
                },
                {
                  "name": "A/basic",
                  "command": ["/tmp/a"],
                  "properties": [
                    {"name": "LABELS", "value": "unit"}
                  ]
                }
              ]
            }
            """);

        Assert.Equal(1, discovery.SchemaMajor);
        Assert.Equal(0, discovery.SchemaMinor);
        Assert.Equal(["A/basic", "Z/π (smoke)"], discovery.Tests.Select(test => test.Name));

        var unicode = discovery.Tests[1];
        Assert.Equal(["qml", "smoke"], unicode.Labels);
        Assert.Equal(["/tmp/qt runner", "--case", "π"], unicode.Command);
        Assert.Equal("/tmp/qt", unicode.WorkingDirectory);
        Assert.Equal("30", unicode.Metadata["TIMEOUT"]);
        Assert.Equal(false, unicode.Metadata["WILL_FAIL"]);
    }

    [Fact]
    public void Parse_is_deterministic_when_ctest_changes_array_order()
    {
        var first = CTestDiscoveryParser.Parse(DiscoveryJson("A", "B"));
        var second = CTestDiscoveryParser.Parse(DiscoveryJson("B", "A"));

        Assert.Equal(first.Tests.Select(test => test.Name), second.Tests.Select(test => test.Name));
        Assert.Equal(
            first.Tests.Select(test => test.Command.ToArray()),
            second.Tests.Select(test => test.Command.ToArray()));
    }

    [Fact]
    public void Parse_rejects_unsupported_schema_without_partial_cases()
    {
        var exception = Assert.Throws<ContinuousTestProviderException>(() =>
            CTestDiscoveryParser.Parse(
                """{"kind":"ctestInfo","version":{"major":2,"minor":0},"tests":[{"name":"partial","command":["x"]}]}"""));

        Assert.Contains("schema version 1", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_malformed_or_truncated_json()
    {
        var exception = Assert.Throws<ContinuousTestProviderException>(() =>
            CTestDiscoveryParser.Parse("{\"kind\":\"ctestInfo\",\"version\":{\"major\":1},\"tests\":["));
        var truncated = Assert.Throws<ContinuousTestProviderException>(() =>
            CTestDiscoveryParser.Parse(new TestProcessResult(
                0,
                "{\"kind\":\"ctestInfo\",\"version\":{\"major\":1,\"minor\":0},\"tests\":[]}",
                "",
                StandardOutputTruncated: true)));

        Assert.Contains("valid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partial", truncated.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_duplicate_names_before_returning_any_case()
    {
        var exception = Assert.Throws<ContinuousTestProviderException>(() =>
            CTestDiscoveryParser.Parse(DiscoveryJson("same", "same")));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_zero_tests()
    {
        var exception = Assert.Throws<ContinuousTestProviderException>(() =>
            CTestDiscoveryParser.Parse(
                """{"kind":"ctestInfo","version":{"major":1,"minor":0},"tests":[]}"""));

        Assert.Contains("zero", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_rejects_empty_target_name_and_command()
    {
        var emptyName = Assert.Throws<ContinuousTestProviderException>(() =>
            CTestDiscoveryParser.Parse(
                """{"kind":"ctestInfo","version":{"major":1,"minor":0},"tests":[{"name":"","command":["x"]}]}"""));
        var emptyCommand = Assert.Throws<ContinuousTestProviderException>(() =>
            CTestDiscoveryParser.Parse(
                """{"kind":"ctestInfo","version":{"major":1,"minor":0},"tests":[{"name":"x","command":[]}]}"""));

        Assert.Contains("name", emptyName.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command", emptyCommand.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactTestNameRegex_escapes_metacharacters_and_matches_only_exact_names()
    {
        var regex = QtQuickTestTooling.ExactTestNameRegex("qml[smoke]+ (π)");

        Assert.True(Regex.IsMatch("qml[smoke]+ (π)", regex, RegexOptions.CultureInvariant));
        Assert.False(Regex.IsMatch("qmlsmoke (π)", regex, RegexOptions.CultureInvariant));
        Assert.False(Regex.IsMatch("prefix qml[smoke]+ (π)", regex, RegexOptions.CultureInvariant));
    }

    [Fact]
    public void CTestRunArguments_are_argument_arrays_with_an_exact_selection_fragment()
    {
        var arguments = QtQuickTestTooling.BuildCTestRunArguments(
            "/build dir",
            "/results/one report.xml",
            ["A[1]", "B (π)"],
            wholeSuite: false);

        Assert.Equal(
            [
                "--test-dir", "/build dir", "--output-junit", "/results/one report.xml",
                "--no-tests=error", "--output-on-failure", "-R", "^(?:A\\[1]|B\\ \\(π\\))$"
            ],
            arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains(' ')
            && argument.Contains("--test-dir", StringComparison.Ordinal));
    }

    [Fact]
    public void CTestRunArguments_omit_selection_for_whole_suite()
    {
        var arguments = QtQuickTestTooling.BuildCTestRunArguments("build", "results.xml", [], wholeSuite: true);

        Assert.DoesNotContain("-R", arguments);
        Assert.Contains("--no-tests=error", arguments);
    }

    [Fact]
    public void QtEnvironment_preserves_an_explicit_platform_and_defaults_when_absent()
    {
        var explicitPlatform = QtQuickTestTooling.WithDefaultQtPlatform(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["QT_QPA_PLATFORM"] = "minimal",
                ["CUSTOM"] = "value",
            });
        var defaultPlatform = QtQuickTestTooling.WithDefaultQtPlatform(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["CUSTOM"] = "value",
            });

        Assert.Equal("minimal", explicitPlatform["QT_QPA_PLATFORM"]);
        Assert.Equal("offscreen", defaultPlatform["QT_QPA_PLATFORM"]);
        Assert.Equal("value", defaultPlatform["CUSTOM"]);
    }

    [Fact]
    public async Task Scripted_runner_records_argument_arrays_without_shell_joining()
    {
        var runner = new ScriptedTestProcessRunner(_ => new TestProcessResult(0, "ok", ""));
        var command = new TestProcessCommand("ctest", ["--test-dir", "/path with spaces"], "/work");

        var result = await runner.RunAsync(command, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Same(command, Assert.Single(runner.Calls));
    }

    private static string DiscoveryJson(string first, string second) =>
        $"{{\"kind\":\"ctestInfo\",\"version\":{{\"major\":1,\"minor\":0}},\"tests\":["
        + $"{{\"name\":\"{first}\",\"command\":[\"{first}\"]}},"
        + $"{{\"name\":\"{second}\",\"command\":[\"{second}\"]}}]}}";
}
