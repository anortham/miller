using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Go;

public sealed class GoTestProviderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-go-provider-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Discover_uses_go_list_and_list_and_returns_stable_package_cases()
    {
        WriteModule("example.com/math");
        var runner = new RecordingRunner(command =>
        {
            if (command.Arguments.SequenceEqual(["version"]))
                return new TestProcessResult(0, "go1.24.0 linux/amd64\n", string.Empty);
            if (command.Arguments.Contains("env", StringComparer.Ordinal))
                return new TestProcessResult(0, EnvironmentJson(), string.Empty);
            if (command.Arguments.Contains("list", StringComparer.Ordinal))
                return new TestProcessResult(0, ListJson(), string.Empty);
            if (command.Arguments.Contains("-list", StringComparer.Ordinal)
                && command.Arguments.Contains("example.com/math", StringComparer.Ordinal))
                return new TestProcessResult(0, "TestAdd\nTestSub\nok example.com/math 0.001s\n", string.Empty);
            throw new InvalidOperationException(command.ToDisplayString());
        });
        var provider = new GoTestProvider(runner);

        IReadOnlyList<ProviderTestCase> cases = await provider.DiscoverAsync(Workspace(), TestContext.Current.CancellationToken);

        Assert.Equal(2, cases.Count);
        Assert.Equal(["TestAdd", "TestSub"], cases.Select(test => test.Metadata["test_name"]!).ToArray());
        Assert.All(cases, test =>
        {
            Assert.Equal("go", test.Framework);
            Assert.Equal("example.com/math", test.Metadata["import_path"]);
            Assert.Equal("example.com/math", test.Metadata["module"]);
            Assert.Equal("test", test.Metadata["kind"]);
            Assert.StartsWith("go-test:", test.Id, StringComparison.Ordinal);
        });
        Assert.Contains(runner.Calls, command => command.Arguments.Contains("-json", StringComparer.Ordinal)
            && command.Arguments.Contains("list", StringComparer.Ordinal));
    }

    [Fact]
    public async Task Discover_persists_workspace_relative_package_directory_evidence()
    {
        WriteModule("example.com/math");
        string packageDirectory = Path.Combine(_root, "services", "foo");
        string packagePath = packageDirectory.Replace('\\', '/');
        var runner = new RecordingRunner(command =>
        {
            if (command.Arguments.SequenceEqual(["version"]))
                return new TestProcessResult(0, "go1.24.0 linux/amd64\n", string.Empty);
            if (command.Arguments.Contains("env", StringComparer.Ordinal))
                return new TestProcessResult(0, EnvironmentJson(), string.Empty);
            if (command.Arguments.Contains("list", StringComparer.Ordinal))
                return new TestProcessResult(0, $$"""
                    {"Dir":"{{packagePath}}","ImportPath":"example.com/math","Name":"math","Module":{"Path":"example.com/math","Dir":"{{packagePath}}"},"TestGoFiles":["math_test.go"],"XTestGoFiles":[]}
                    """, string.Empty);
            if (command.Arguments.Contains("-list", StringComparer.Ordinal))
                return new TestProcessResult(0, "TestAdd\nok example.com/math 0.001s\n", string.Empty);
            throw new InvalidOperationException(command.ToDisplayString());
        });

        IReadOnlyList<ProviderTestCase> cases = await new GoTestProvider(runner)
            .DiscoverAsync(Workspace(), TestContext.Current.CancellationToken);

        ProviderTestCase testCase = Assert.Single(cases);
        Assert.Equal("services/foo", testCase.SourcePath);
        Assert.Equal(testCase.SourcePath, testCase.Metadata["package_dir"]);
    }

    [Fact]
    public async Task Run_groups_cases_by_package_and_parses_parent_verdicts()
    {
        WriteModule("example.com/math");
        var workspace = Workspace();
        var discoveryRunner = new RecordingRunner(command =>
        {
            if (command.Arguments.SequenceEqual(["version"]))
                return new TestProcessResult(0, "go1.24.0 linux/amd64\n", string.Empty);
            if (command.Arguments.Contains("env", StringComparer.Ordinal))
                return new TestProcessResult(0, EnvironmentJson(), string.Empty);
            if (command.Arguments.Contains("list", StringComparer.Ordinal))
                return new TestProcessResult(0, ListJson(), string.Empty);
            return new TestProcessResult(0, "TestAdd\nok example.com/math 0.001s\n", string.Empty);
        });
        var provider = new GoTestProvider(discoveryRunner);
        IReadOnlyList<ProviderTestCase> cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        ProviderTestCase selected = cases.Single(test => test.Metadata["test_name"] as string == "TestAdd");

        var runRunner = new RecordingRunner(command =>
            new TestProcessResult(0,
                """
                {"Action":"start","Package":"example.com/math"}
                {"Action":"run","Package":"example.com/math","Test":"TestAdd"}
                {"Action":"run","Package":"example.com/math","Test":"TestAdd/child"}
                {"Action":"pass","Package":"example.com/math","Test":"TestAdd/child","Elapsed":0.01}
                {"Action":"pass","Package":"example.com/math","Test":"TestAdd","Elapsed":0.02}
                {"Action":"pass","Package":"example.com/math","Elapsed":0.02}
                """, string.Empty));
        provider = new GoTestProvider(runRunner);

        ProviderRunResult result = await provider.RunAsync(Request(workspace, selected.Id), TestContext.Current.CancellationToken);

        TestProcessCommand command = Assert.Single(runRunner.Calls);
        Assert.Contains("-json", command.Arguments);
        Assert.Contains("-count=1", command.Arguments);
        Assert.Contains("^(?:TestAdd)$", command.Arguments);
        Assert.Equal("passed", result.Status);
        Assert.Equal("passed", Assert.Single(result.CaseResults).Status);
        Assert.Equal(selected.Id, result.CaseResults[0].TestCaseId);
        Assert.NotNull(result.ResultArtifactPath);
        Assert.True(File.Exists(result.ResultArtifactPath));
    }

    [Fact]
    public async Task Run_maps_package_build_failure_to_every_selected_case()
    {
        WriteModule("example.com/math");
        var workspace = Workspace();
        string first = GoTestTooling.EncodeCaseId("ws:go", workspace.ProjectPath, "example.com/math", "example.com/math", "TestAdd");
        string second = GoTestTooling.EncodeCaseId("ws:go", workspace.ProjectPath, "example.com/math", "example.com/math", "TestSub");
        var runner = new RecordingRunner(command =>
            new TestProcessResult(1,
                """
                {"Action":"start","Package":"example.com/math"}
                {"Action":"build-fail","ImportPath":"example.com/math","Output":"undefined: Missing\n"}
                {"Action":"fail","Package":"example.com/math","FailedBuild":"example.com/math","Output":"build failed\n"}
                """,
                ""));

        ProviderRunResult result = await new GoTestProvider(runner).RunAsync(
            Request(workspace, first, second), TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.Equal(2, result.CaseResults.Count);
        Assert.All(result.CaseResults, test =>
        {
            Assert.Equal("failed", test.Status);
            Assert.Contains("undefined: Missing", test.FailureSummary!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Run_refuses_nonzero_exit_with_only_passed_and_skipped_results()
    {
        WriteModule("example.com/math");
        var workspace = Workspace();
        string first = GoTestTooling.EncodeCaseId("ws:go", workspace.ProjectPath, "example.com/math", "example.com/math", "TestAdd");
        string second = GoTestTooling.EncodeCaseId("ws:go", workspace.ProjectPath, "example.com/math", "example.com/math", "TestSkip");
        var runner = new RecordingRunner(command =>
            new TestProcessResult(1,
                """
                {"Action":"start","Package":"example.com/math"}
                {"Action":"run","Package":"example.com/math","Test":"TestAdd"}
                {"Action":"pass","Package":"example.com/math","Test":"TestAdd","Elapsed":0.01}
                {"Action":"run","Package":"example.com/math","Test":"TestSkip"}
                {"Action":"skip","Package":"example.com/math","Test":"TestSkip","Elapsed":0.0}
                {"Action":"fail","Package":"example.com/math","Elapsed":0.02}
                """,
                ""));

        await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new GoTestProvider(runner).RunAsync(Request(workspace, first, second), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Run_refuses_malformed_or_incomplete_json_instead_of_returning_green()
    {
        WriteModule("example.com/math");
        var workspace = Workspace();
        string id = GoTestTooling.EncodeCaseId("ws:go", workspace.ProjectPath, "example.com/math", "example.com/math", "TestAdd");
        var runner = new RecordingRunner(command =>
            new TestProcessResult(0,
                "{\"Action\":\"start\",\"Package\":\"example.com/math\"}\nnot-json\n",
                ""));

        await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new GoTestProvider(runner).RunAsync(Request(workspace, id), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Discover_refuses_an_older_go_toolchain_before_listing()
    {
        WriteModule("example.com/math");
        var runner = new RecordingRunner(command =>
            command.Arguments.SequenceEqual(["version"])
                ? new TestProcessResult(0, "go1.23.9 linux/amd64\n", string.Empty)
                : throw new InvalidOperationException(command.ToDisplayString()));

        var exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new GoTestProvider(runner).DiscoverAsync(Workspace(), TestContext.Current.CancellationToken));

        Assert.Contains("requires Go 1.24", exception.Message, StringComparison.Ordinal);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Run_refuses_truncated_json_stdout()
    {
        WriteModule("example.com/math");
        var workspace = Workspace();
        string id = GoTestTooling.EncodeCaseId("ws:go", workspace.ProjectPath, "example.com/math", "example.com/math", "TestAdd");
        var runner = new RecordingRunner(command =>
            new TestProcessResult(
                0,
                "{\"Action\":\"start\",\"Package\":\"example.com/math\"}\n",
                string.Empty,
                StandardOutputTruncated: true));

        await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new GoTestProvider(runner).RunAsync(Request(workspace, id), TestContext.Current.CancellationToken));
    }

    private void WriteModule(string module)
    {
        File.WriteAllText(Path.Combine(_root, "go.mod"), $"module {module}\n\ngo 1.24\n");
        File.WriteAllText(Path.Combine(_root, "math_test.go"), "package math\n\nimport \"testing\"\nfunc TestAdd(t *testing.T) {}\nfunc TestSub(t *testing.T) {}\n");
    }

    private ContinuousTestWorkspace Workspace() =>
        new("ws:go", _root, Path.Combine(_root, "go.mod"), Path.Combine(_root, ".miller", "ct-go"), Framework: "go");

    private static ContinuousTestProviderRunRequest Request(ContinuousTestWorkspace workspace, params string[] ids) =>
        new(workspace, "rev-1", "identity-1", RunId: "run-go", TestCaseIds: ids);

    private static string ListJson() =>
        """
        {"Dir":"/tmp/math","ImportPath":"example.com/math","Name":"math","Module":{"Path":"example.com/math","Dir":"/tmp/math"},"TestGoFiles":["math_test.go"],"XTestGoFiles":[]}
        """;

    private static string EnvironmentJson() =>
        """
        {"GOVERSION":"go1.24.0","GOWORK":"off","GOOS":"linux","GOARCH":"amd64","CGO_ENABLED":"0","GOFLAGS":"","GOMOD":"go.mod"}
        """;

    private sealed class RecordingRunner(Func<TestProcessCommand, TestProcessResult> handler) : ITestProcessRunner
    {
        public List<TestProcessCommand> Calls { get; } = [];

        public Task<TestProcessResult> RunAsync(TestProcessCommand command, CancellationToken cancellationToken = default)
        {
            Calls.Add(command);
            return Task.FromResult(handler(command));
        }
    }
}
