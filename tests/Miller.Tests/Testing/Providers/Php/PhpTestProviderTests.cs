using Miller.Testing;
using Miller.Testing.Providers.Php;
using Xunit;

namespace Miller.Tests.Testing.Providers.Php;

public sealed class PhpTestProviderTests : IDisposable
{
    private const string IndexIdentity = "store:php-identity";

    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-php-provider-").FullName;

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
    public async Task Discover_runs_phpunit_list_tests_xml_and_returns_class_method_cases()
    {
        WriteComposer("phpunit/phpunit");
        WriteVendorBinary("phpunit");
        var runner = new RecordingRunner(command =>
        {
            string artifactPath = ArgumentAfter(command, "--list-tests-xml");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(artifactPath, DiscoveryXml);
            return new TestProcessResult(0, "listing complete", string.Empty);
        });

        IReadOnlyList<ProviderTestCase> cases = await new PhpTestProvider(runner).DiscoverAsync(
            Workspace("phpunit"),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, cases.Count);
        Assert.Equal(
            ["Tests\\Unit\\CalculatorTest::testAdd", "Tests\\Unit\\CalculatorTest::testSubtract"],
            cases.Select(test => test.Selector).ToArray());
        Assert.All(cases, test =>
        {
            Assert.StartsWith("php-test:", test.Id, StringComparison.Ordinal);
            Assert.Equal("phpunit", test.Framework);
            Assert.Equal("Tests\\Unit\\CalculatorTest", test.Metadata["class_name"]);
        });
        TestProcessCommand command = Assert.Single(runner.Calls);
        Assert.Equal(Path.Combine(_root, "vendor", "bin", "phpunit"), command.FileName);
        Assert.Equal("--list-tests-xml", command.Arguments[0]);
        Assert.True(Path.IsPathRooted(command.Arguments[1]));
        Assert.EndsWith("php-discovery.xml", command.Arguments[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_routes_both_composer_tokens_to_pest()
    {
        WriteComposer("phpunit/phpunit", "pestphp/pest");
        WriteVendorBinary("pest");
        var runner = new RecordingRunner(command =>
        {
            string artifactPath = ArgumentAfter(command, "--list-tests-xml");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(artifactPath, DiscoveryXml);
            return new TestProcessResult(0, string.Empty, string.Empty);
        });

        IReadOnlyList<ProviderTestCase> cases = await new PhpTestProvider(runner).DiscoverAsync(
            Workspace(),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, cases.Count);
        Assert.Equal("pest", cases[0].Framework);
        Assert.Equal(Path.Combine(_root, "vendor", "bin", "pest"), Assert.Single(runner.Calls).FileName);
    }

    [Fact]
    public async Task Discover_reads_phpunit_12_listing_shape_and_preserves_data_set_tail()
    {
        WriteComposer("phpunit/phpunit");
        WriteVendorBinary("phpunit");
        var runner = new RecordingRunner(command =>
        {
            string artifactPath = ArgumentAfter(command, "--list-tests-xml");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(artifactPath, DiscoveryXmlPhpUnit12WithFile(Path.Combine(
                _root,
                "tests",
                "Unit",
                "CalculatorTest.php")));
            return Success();
        });

        IReadOnlyList<ProviderTestCase> cases = await new PhpTestProvider(runner).DiscoverAsync(
            Workspace("phpunit"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "Tests\\Unit\\CalculatorTest::testAdd",
                "Tests\\Unit\\CalculatorTest::testWithDataSet with data set \"fast\"",
            ],
            cases.Select(test => test.Selector).ToArray());
        Assert.All(cases, test =>
        {
            Assert.Equal("tests/Unit/CalculatorTest.php", test.SourcePath);
            Assert.Equal("tests/Unit/CalculatorTest.php", test.SymbolPath);
            Assert.Equal("Tests\\Unit\\CalculatorTest", test.Metadata["class_name"]);
            Assert.Equal("Tests\\Unit\\CalculatorTest", test.Metadata["class"]);
        });
        ProviderTestCase dataSetCase = Assert.Single(cases, test =>
            test.Selector.Contains("with data set", StringComparison.Ordinal));
        Assert.Equal("testWithDataSet", dataSetCase.SymbolName);
    }

    [Fact]
    public async Task Discover_rejects_a_phpunit_12_listing_file_outside_the_workspace()
    {
        WriteComposer("phpunit/phpunit");
        WriteVendorBinary("phpunit");
        string outsidePath = Path.Combine(Path.GetTempPath(), "miller-php-outside.php");
        var runner = new RecordingRunner(command =>
        {
            string artifactPath = ArgumentAfter(command, "--list-tests-xml");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(artifactPath, DiscoveryXmlPhpUnit12WithFile(outsidePath));
            return Success();
        });

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new PhpTestProvider(runner).DiscoverAsync(
                Workspace("phpunit"),
                TestContext.Current.CancellationToken));

        Assert.Contains("outside", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_run_command_uses_phpunit_and_escapes_filter_regex()
    {
        WriteComposer("phpunit/phpunit");
        WriteVendorBinary("phpunit");
        ContinuousTestWorkspace workspace = Workspace("phpunit");
        string id = PhpTestTooling.EncodeCaseId(
            workspace.WorkspaceId,
            workspace.ProjectPath,
            "Tests\\Unit\\CalculatorTest",
            "test[adds].case");

        TestProcessCommand command = new PhpTestProvider(new RecordingRunner(_ => Success()))
            .BuildRunCommand(Request(workspace, id));

        Assert.Equal(Path.Combine(_root, "vendor", "bin", "phpunit"), command.FileName);
        Assert.Equal(["--log-junit"], command.Arguments.Take(1).ToArray());
        Assert.Contains("--filter", command.Arguments);
        string filter = ArgumentAfter(command, "--filter");
        Assert.Contains("test\\[adds\\]\\.case", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("test[adds].case", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_run_command_anchors_prefix_collisions_and_escapes_slash_data_set_selectors()
    {
        WriteComposer("phpunit/phpunit");
        WriteVendorBinary("phpunit");
        ContinuousTestWorkspace workspace = Workspace("phpunit");
        string[] ids =
        [
            PhpTestTooling.EncodeCaseId(workspace.WorkspaceId, workspace.ProjectPath,
                "Tests\\Unit\\CalculatorTest", "testAdd"),
            PhpTestTooling.EncodeCaseId(workspace.WorkspaceId, workspace.ProjectPath,
                "Tests\\Unit\\CalculatorTest", "testAdd/with[data]"),
        ];

        TestProcessCommand command = new PhpTestProvider(new RecordingRunner(_ => Success()))
            .BuildRunCommand(Request(workspace, ids));

        string filter = ArgumentAfter(command, "--filter");
        Assert.StartsWith("^(?:", filter, StringComparison.Ordinal);
        Assert.EndsWith(")$", filter, StringComparison.Ordinal);
        Assert.Contains("testAdd", filter, StringComparison.Ordinal);
        Assert.Contains("testAdd\\/with\\[data\\]", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_run_command_uses_pest_binary_and_whole_suite_omits_selection()
    {
        WriteComposer("pestphp/pest");
        WriteVendorBinary("pest");
        ContinuousTestWorkspace workspace = Workspace("pest");
        string id = PhpTestTooling.EncodeCaseId(
            workspace.WorkspaceId,
            workspace.ProjectPath,
            "Tests\\Unit\\CalculatorTest",
            "testAdd");

        TestProcessCommand command = new PhpTestProvider(new RecordingRunner(_ => Success()))
            .BuildRunCommand(Request(workspace, id) with { WholeSuite = true });

        Assert.Equal(Path.Combine(_root, "vendor", "bin", "pest"), command.FileName);
        Assert.Contains("--log-junit", command.Arguments);
        Assert.DoesNotContain("--filter", command.Arguments);
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains("testAdd", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_run_commands_chunks_long_selection_filters()
    {
        WriteComposer("phpunit/phpunit");
        WriteVendorBinary("phpunit");
        ContinuousTestWorkspace workspace = Workspace("phpunit");
        string[] ids = Enumerable.Range(0, 150)
            .Select(index => PhpTestTooling.EncodeCaseId(
                workspace.WorkspaceId,
                workspace.ProjectPath,
                "Tests\\Unit\\CalculatorTest",
                "testCase" + index.ToString("D3") + new string('x', 50)))
            .ToArray();

        IReadOnlyList<TestProcessCommand> commands = new PhpTestProvider(
            new RecordingRunner(_ => Success())).BuildRunCommands(Request(workspace, ids));

        Assert.True(commands.Count > 1);
        Assert.All(commands, command => Assert.Contains("--filter", command.Arguments));
        Assert.Equal(commands.Count, commands.Select(command => ArgumentAfter(command, "--log-junit")).Distinct().Count());
    }

    [Fact]
    public void Build_run_command_rejects_an_empty_selection()
    {
        WriteComposer("phpunit/phpunit");
        WriteVendorBinary("phpunit");

        ContinuousTestProviderException exception = Assert.Throws<ContinuousTestProviderException>(() =>
            new PhpTestProvider(new RecordingRunner(_ => Success())).BuildRunCommand(
                Request(Workspace("phpunit"))));

        Assert.Contains("selected no test case IDs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_run_command_reports_missing_vendor_binary_with_composer_remedy()
    {
        WriteComposer("phpunit/phpunit");

        ContinuousTestProviderException exception = Assert.Throws<ContinuousTestProviderException>(() =>
            new PhpTestProvider(new RecordingRunner(_ => Success())).BuildRunCommand(
                Request(Workspace("phpunit"), CaseId("phpunit", "testAdd"))));

        Assert.Contains("run composer install", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_reads_the_junit_artifact_even_when_stdout_is_truncated()
    {
        WriteComposer("phpunit/phpunit");
        WriteVendorBinary("phpunit");
        ContinuousTestWorkspace workspace = Workspace("phpunit");
        string[] ids =
        [
            CaseId("phpunit", "testAdd"),
            CaseId("phpunit", "testSubtract"),
            CaseId("phpunit", "testPending"),
        ];
        var runner = new RecordingRunner(command =>
        {
            string artifactPath = ArgumentAfter(command, "--log-junit");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(artifactPath, ResultXml);
            return new TestProcessResult(
                1,
                "stdout is not the result artifact",
                "",
                StandardOutputTruncated: true);
        });

        ProviderRunResult result = await new PhpTestProvider(runner).RunAsync(
            Request(workspace, ids),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.Equal(3, result.CaseResults.Count);
        Assert.Equal(
            ["passed", "skipped", "failed"],
            result.CaseResults.OrderBy(row => row.TestCaseId, StringComparer.Ordinal)
                .Select(row => row.Status).ToArray());
        Assert.Equal(0.004, Assert.Single(result.CaseResults, row => row.Status == "failed").DurationSeconds);
        Assert.Contains("expected 2", Assert.Single(result.CaseResults, row => row.Status == "failed").FailureSummary,
            StringComparison.Ordinal);
        Assert.NotNull(result.ResultArtifactPath);
        Assert.True(File.Exists(result.ResultArtifactPath));
    }

    [Fact]
    public async Task Run_maps_junit_errored_to_failed()
    {
        WriteComposer("pestphp/pest");
        WriteVendorBinary("pest");
        ContinuousTestWorkspace workspace = Workspace("pest");
        string id = CaseId("pest", "testAdd");
        var runner = new RecordingRunner(command =>
        {
            string artifactPath = ArgumentAfter(command, "--log-junit");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(artifactPath,
                "<testsuite name=\"Tests\\\\Unit\\\\CalculatorTest\" tests=\"1\" failures=\"0\" errors=\"1\"><testcase class=\"Tests\\\\Unit\\\\CalculatorTest\" name=\"testAdd\"><error message=\"fixture setup failed\">setup</error></testcase></testsuite>");
            return new TestProcessResult(1, string.Empty, string.Empty);
        });

        ProviderRunResult result = await new PhpTestProvider(runner).RunAsync(
            Request(workspace, id),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.Equal("failed", Assert.Single(result.CaseResults).Status);
        Assert.Contains("fixture setup failed", Assert.Single(result.CaseResults).FailureSummary,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_rejects_malformed_or_aggregate_inconsistent_artifacts()
    {
        WriteComposer("phpunit/phpunit");
        WriteVendorBinary("phpunit");
        ContinuousTestWorkspace workspace = Workspace("phpunit");
        string id = CaseId("phpunit", "testAdd");

        foreach (string xml in new[]
        {
            "<testsuite><testcase /></testsuite>",
            "<testsuite name=\"Tests\\\\Unit\\\\CalculatorTest\" tests=\"2\"><testcase class=\"Tests\\\\Unit\\\\CalculatorTest\" name=\"testAdd\" /></testsuite>",
        })
        {
            var runner = new RecordingRunner(command =>
            {
                string artifactPath = ArgumentAfter(command, "--log-junit");
                Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
                File.WriteAllText(artifactPath, xml);
                return new TestProcessResult(0, string.Empty, string.Empty);
            });

            ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                new PhpTestProvider(runner).RunAsync(
                    Request(workspace, id),
                    TestContext.Current.CancellationToken));

            Assert.Contains("report", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Run_rejects_unexpected_and_missing_cases_on_partial_runs()
    {
        WriteComposer("phpunit/phpunit");
        WriteVendorBinary("phpunit");
        ContinuousTestWorkspace workspace = Workspace("phpunit");
        string id = CaseId("phpunit", "testAdd");

        var unexpectedRunner = new RecordingRunner(command =>
        {
            string artifactPath = ArgumentAfter(command, "--log-junit");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(artifactPath,
                "<testsuite name=\"Tests\\\\Unit\\\\CalculatorTest\" tests=\"1\"><testcase class=\"Tests\\\\Unit\\\\CalculatorTest\" name=\"testOther\" /></testsuite>");
            return Success();
        });
        ContinuousTestProviderException unexpected = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new PhpTestProvider(unexpectedRunner).RunAsync(
                Request(workspace, id),
                TestContext.Current.CancellationToken));
        Assert.Contains("not selected", unexpected.Message, StringComparison.Ordinal);

        var missingRunner = new RecordingRunner(command =>
        {
            string artifactPath = ArgumentAfter(command, "--log-junit");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(artifactPath,
                "<testsuite name=\"Tests\\\\Unit\\\\CalculatorTest\" tests=\"0\" />");
            return new TestProcessResult(1, string.Empty, "failed before test");
        });
        ContinuousTestProviderException missing = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new PhpTestProvider(missingRunner).RunAsync(
                Request(workspace, id),
                TestContext.Current.CancellationToken));
        Assert.Contains("did not report selected", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_rejects_case_id_owned_by_another_workspace()
    {
        WriteComposer("phpunit/phpunit");
        WriteVendorBinary("phpunit");
        ContinuousTestWorkspace workspace = Workspace("phpunit");
        string foreign = PhpTestTooling.EncodeCaseId(
            "ws:foreign",
            workspace.ProjectPath,
            "Tests\\Unit\\CalculatorTest",
            "testAdd");

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new PhpTestProvider(new RecordingRunner(_ => Success())).RunAsync(
                Request(workspace, foreign),
                TestContext.Current.CancellationToken));

        Assert.Contains("not owned", exception.Message, StringComparison.Ordinal);
    }

    private string CaseId(string framework, string method) => PhpTestTooling.EncodeCaseId(
        "ws:php",
        Path.Combine(_root, "composer.json"),
        "Tests\\Unit\\CalculatorTest",
        method);

    private void WriteComposer(params string[] packages)
    {
        string dependencies = string.Join(",", packages.Select(package => $"\"{package}\": \"^1.0\""));
        File.WriteAllText(
            Path.Combine(_root, "composer.json"),
            $"{{\"require-dev\":{{{dependencies}}}}}");
    }

    private void WriteVendorBinary(string framework)
    {
        string path = Path.Combine(_root, "vendor", "bin", framework);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    private ContinuousTestWorkspace Workspace(string? framework = null) =>
        new(
            WorkspaceId: "ws:php",
            WorkspaceRoot: _root,
            ProjectPath: Path.Combine(_root, "composer.json"),
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-php"),
            Framework: framework);

    private static ContinuousTestProviderRunRequest Request(
        ContinuousTestWorkspace workspace,
        params string[] ids) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-php",
            IndexIdentity: IndexIdentity,
            RunId: "run:php",
            TestCaseIds: ids);

    private static TestProcessResult Success() => new(0, string.Empty, string.Empty);

    private static string ArgumentAfter(TestProcessCommand command, string argument)
    {
        int index = command.Arguments.ToList().IndexOf(argument);
        Assert.True(index >= 0 && index + 1 < command.Arguments.Count);
        return command.Arguments[index + 1];
    }

    private static readonly string DiscoveryXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <tests>
          <testCaseClass name="Tests\Unit\CalculatorTest">
            <testCaseMethod id="Tests\Unit\CalculatorTest::testAdd" name="testAdd" />
            <testCaseMethod id="Tests\Unit\CalculatorTest::testSubtract" name="testSubtract" />
          </testCaseClass>
        </tests>
        """;

    private static string DiscoveryXmlPhpUnit12WithFile(string filePath) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <testSuite xmlns="https://xml.phpunit.de/testSuite">
          <tests>
            <testClass name="Tests\Unit\CalculatorTest" file="{filePath}">
              <testMethod id="Tests\Unit\CalculatorTest::testAdd" name="testAdd" />
              <testMethod id="Tests\Unit\CalculatorTest::testWithDataSet with data set &quot;fast&quot;" name="testWithDataSet" />
            </testClass>
          </tests>
          <groups />
        </testSuite>
        """;

    private static readonly string ResultXml = """
        <testsuite name="Tests\\Unit\\CalculatorTest" tests="3" failures="1" errors="0" skipped="1">
          <testcase classname="Tests\\Unit\\CalculatorTest" name="testAdd" time="0.003" />
          <testcase classname="Tests\\Unit\\CalculatorTest" name="testSubtract" time="0.004">
            <failure message="expected 2 but was 3">expected 2 but was 3</failure>
          </testcase>
          <testcase classname="Tests\\Unit\\CalculatorTest" name="testPending" time="0.001">
            <skipped message="not implemented" />
          </testcase>
        </testsuite>
        """;

    private sealed class RecordingRunner(Func<TestProcessCommand, TestProcessResult> execute) : ITestProcessRunner
    {
        public List<TestProcessCommand> Calls { get; } = [];

        public Task<TestProcessResult> RunAsync(
            TestProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(command);
            return Task.FromResult(execute(command));
        }
    }
}
