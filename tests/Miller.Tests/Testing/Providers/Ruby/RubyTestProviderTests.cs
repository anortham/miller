using Miller.Testing;
using Miller.Testing.Providers.Ruby;
using Xunit;

namespace Miller.Tests.Testing.Providers.Ruby;

public sealed class RubyTestProviderTests : IDisposable
{
    private const string IndexIdentity = "store:ruby-identity";

    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-ruby-provider-").FullName;

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
    public async Task Discover_runs_rspec_dry_run_and_returns_location_selectors()
    {
        WriteGemfile(withLock: true);
        var runner = new RecordingRunner(_ => new TestProcessResult(0, DiscoveryJson, string.Empty));
        var provider = new RubyTestProvider(runner);

        IReadOnlyList<ProviderTestCase> cases = await provider.DiscoverAsync(
            Workspace(),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, cases.Count);
        Assert.Equal(["spec/calculator_spec.rb:3", "spec/calculator_spec.rb:7"],
            cases.Select(test => test.Selector).ToArray());
        Assert.Equal(["Calculator adds", "Calculator subtracts"],
            cases.Select(test => test.FullyQualifiedName).ToArray());
        Assert.All(cases, test =>
        {
            Assert.StartsWith("ruby-test:", test.Id, StringComparison.Ordinal);
            Assert.Equal("rspec", test.Framework);
            Assert.Equal("spec/calculator_spec.rb", test.SourcePath);
        });
        Assert.Equal(
            ["./spec/calculator_spec.rb[1:1]", "./spec/calculator_spec.rb[1:2]"],
            cases.Select(test => (string)test.Metadata["example_id"]!).ToArray());
        Assert.Equal("bundle", Assert.Single(runner.Calls).FileName);
        Assert.Equal(["exec", "rspec", "--dry-run", "--format", "json"],
            runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task Discover_uses_direct_rspec_when_no_lockfile_exists()
    {
        WriteGemfile(withLock: false);
        var runner = new RecordingRunner(_ => new TestProcessResult(0, DiscoveryJson, string.Empty));

        await new RubyTestProvider(runner).DiscoverAsync(
            Workspace(),
            TestContext.Current.CancellationToken);

        TestProcessCommand command = Assert.Single(runner.Calls);
        Assert.Equal("rspec", command.FileName);
        Assert.Equal(["--dry-run", "--format", "json"], command.Arguments);
    }

    [Fact]
    public async Task Run_rejects_unselected_report_examples_on_a_partial_run()
    {
        WriteGemfile(withLock: true);
        var workspace = Workspace();
        var discoveryRunner = new RecordingRunner(_ => new TestProcessResult(0, DiscoveryJson, string.Empty));
        var provider = new RubyTestProvider(discoveryRunner);
        IReadOnlyList<ProviderTestCase> cases = await provider.DiscoverAsync(
            workspace,
            TestContext.Current.CancellationToken);

        var runRunner = new RecordingRunner(command =>
        {
            string artifactPath = ArgumentAfter(command, "--out");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(artifactPath, RunJson);
            return new TestProcessResult(1, "this stdout is not the result artifact", string.Empty, StandardOutputTruncated: true);
        });
        provider = new RubyTestProvider(runRunner);

        ContinuousTestProviderException exception = await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            provider.RunAsync(
                Request(workspace, cases.Select(test => test.Id).ToArray()),
                TestContext.Current.CancellationToken));

        Assert.Contains("not selected", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.ResultArtifactPath);
        Assert.True(File.Exists(exception.ResultArtifactPath));
        Assert.Equal("bundle", Assert.Single(runRunner.Calls).FileName);
    }

    [Fact]
    public async Task Run_reads_the_out_artifact_and_maps_failure_and_pending_examples()
    {
        WriteGemfile(withLock: true);
        var discoveryRunner = new RecordingRunner(_ => new TestProcessResult(0, RunJson, string.Empty));
        var provider = new RubyTestProvider(discoveryRunner);
        ContinuousTestWorkspace workspace = Workspace();
        IReadOnlyList<ProviderTestCase> cases = await provider.DiscoverAsync(
            workspace,
            TestContext.Current.CancellationToken);

        var runRunner = new RecordingRunner(command =>
        {
            string artifactPath = ArgumentAfter(command, "--out");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(artifactPath, RunJson);
            return new TestProcessResult(
                1,
                "this stdout is not the result artifact",
                string.Empty,
                StandardOutputTruncated: true);
        });
        provider = new RubyTestProvider(runRunner);

        ProviderRunResult result = await provider.RunAsync(
            Request(workspace, cases.Select(test => test.Id).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.Equal(3, result.CaseResults.Count);
        Assert.Equal(
            ["passed", "failed", "skipped"],
            result.CaseResults.OrderBy(row => row.TestCaseId, StringComparer.Ordinal)
                .Select(row => row.Status).ToArray());
        ProviderCaseResult failed = Assert.Single(result.CaseResults, row => row.Status == "failed");
        Assert.Contains("expected: 2", failed.FailureSummary, StringComparison.Ordinal);
        Assert.Equal(0.002, failed.DurationSeconds);
        Assert.All(result.CaseResults, row =>
        {
            Assert.Equal(IndexIdentity, row.IndexIdentity);
            Assert.Equal("rev-ruby", row.ResultRevision);
        });
        Assert.NotNull(result.ResultArtifactPath);
        Assert.True(File.Exists(result.ResultArtifactPath));
        Assert.Equal("bundle", Assert.Single(runRunner.Calls).FileName);
    }

    [Fact]
    public async Task Run_round_trips_examples_that_share_a_location_selector()
    {
        WriteGemfile(withLock: false);
        var discoveryRunner = new RecordingRunner(_ => new TestProcessResult(0, CollisionJson, string.Empty));
        var provider = new RubyTestProvider(discoveryRunner);
        ContinuousTestWorkspace workspace = Workspace();
        IReadOnlyList<ProviderTestCase> discovered = await provider.DiscoverAsync(
            workspace,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, discovered.Count);
        Assert.Equal(
            "spec/calculator_spec.rb:3",
            discovered.Single(test => (string)test.Metadata["example_id"]! == "./spec/calculator_spec.rb[1:1]").Selector);
        Assert.Equal(
            "./spec/calculator_spec.rb[1:2]",
            discovered.Single(test => (string)test.Metadata["example_id"]! == "./spec/calculator_spec.rb[1:2]").Selector);

        var runRunner = new RecordingRunner(command =>
        {
            string artifactPath = ArgumentAfter(command, "--out");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(artifactPath, CollisionJson);
            return new TestProcessResult(0, string.Empty, string.Empty);
        });
        provider = new RubyTestProvider(runRunner);

        ProviderRunResult result = await provider.RunAsync(
            Request(workspace, discovered.Select(test => test.Id).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            discovered.Select(test => test.Id).Order(StringComparer.Ordinal).ToArray(),
            result.CaseResults.Select(row => row.TestCaseId).Order(StringComparer.Ordinal).ToArray());
        Assert.All(result.CaseResults, row => Assert.Equal("passed", row.Status));
    }

    [Fact]
    public void Build_run_command_adds_each_selected_location_after_the_report_options()
    {
        WriteGemfile(withLock: true);
        var workspace = Workspace();
        var provider = new RubyTestProvider(new RecordingRunner(_ => new TestProcessResult(0, string.Empty, string.Empty)));
        string first = RubyTestTooling.EncodeCaseId(
            workspace.WorkspaceId,
            workspace.ProjectPath,
            "spec/calculator_spec.rb",
            "./spec/calculator_spec.rb[1:1]",
            "spec/calculator_spec.rb:3");

        TestProcessCommand command = provider.BuildRunCommand(Request(workspace, first));

        Assert.Equal("bundle", command.FileName);
        Assert.Equal("exec", command.Arguments[0]);
        Assert.Equal("rspec", command.Arguments[1]);
        Assert.Equal(["--format", "json"], command.Arguments.Skip(2).Take(2).ToArray());
        Assert.Equal("spec/calculator_spec.rb:3", command.Arguments[^1]);
        Assert.Contains("--out", command.Arguments);
    }

    [Fact]
    public void Build_run_command_whole_suite_drops_location_selection()
    {
        WriteGemfile(withLock: true);
        var workspace = Workspace();
        var provider = new RubyTestProvider(new RecordingRunner(_ => new TestProcessResult(0, string.Empty, string.Empty)));
        string first = RubyTestTooling.EncodeCaseId(
            workspace.WorkspaceId,
            workspace.ProjectPath,
            "spec/calculator_spec.rb",
            "./spec/calculator_spec.rb[1:1]",
            "spec/calculator_spec.rb:3");

        TestProcessCommand command = provider.BuildRunCommand(
            Request(workspace, first) with { WholeSuite = true });

        Assert.DoesNotContain(command.Arguments, argument => argument.Contains("calculator_spec.rb:", StringComparison.Ordinal));
        Assert.Contains("--format", command.Arguments);
        Assert.Contains("json", command.Arguments);
        Assert.Contains("--out", command.Arguments);
    }

    [Fact]
    public void Build_run_command_rejects_an_empty_decodable_selection()
    {
        WriteGemfile(withLock: true);
        var workspace = Workspace();
        var provider = new RubyTestProvider(new RecordingRunner(_ => new TestProcessResult(0, string.Empty, string.Empty)));

        Assert.Throws<ContinuousTestProviderException>(() =>
            provider.BuildRunCommand(Request(workspace, "not-a-ruby-case-id")));
    }

    [Fact]
    public async Task Discover_refuses_truncated_stdout_before_parsing()
    {
        WriteGemfile(withLock: false);
        var runner = new RecordingRunner(_ => new TestProcessResult(
            0,
            DiscoveryJson,
            string.Empty,
            StandardOutputTruncated: true));

        await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new RubyTestProvider(runner).DiscoverAsync(
                Workspace(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Run_rejects_a_case_id_owned_by_another_workspace()
    {
        WriteGemfile(withLock: false);
        var workspace = Workspace();
        string foreign = RubyTestTooling.EncodeCaseId(
            "ws:foreign",
            workspace.ProjectPath,
            "spec/calculator_spec.rb",
            "./spec/calculator_spec.rb[1:1]");

        await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
            new RubyTestProvider(new RecordingRunner(_ => new TestProcessResult(0, string.Empty, string.Empty)))
                .RunAsync(Request(workspace, foreign), TestContext.Current.CancellationToken));
    }

    private static string ArgumentAfter(TestProcessCommand command, string argument)
    {
        int index = command.Arguments.ToList().IndexOf(argument);
        if (index >= 0 && index + 1 < command.Arguments.Count)
            return command.Arguments[index + 1];

        string prefix = argument + "=";
        return command.Arguments.Single(value => value.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
    }

    private void WriteGemfile(bool withLock)
    {
        File.WriteAllText(Path.Combine(_root, "Gemfile"), "source 'https://rubygems.org'\ngem 'rspec'\n");
        if (withLock)
            File.WriteAllText(Path.Combine(_root, "Gemfile.lock"), "GEM\n  specs:\n");
    }

    private ContinuousTestWorkspace Workspace() =>
        new(
            WorkspaceId: "ws:ruby",
            WorkspaceRoot: _root,
            ProjectPath: Path.Combine(_root, "Gemfile"),
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-ruby"),
            Framework: "rspec");

    private static ContinuousTestProviderRunRequest Request(
        ContinuousTestWorkspace workspace,
        params string[] ids) =>
        new(
            Workspace: workspace,
            SelectedRevision: "rev-ruby",
            IndexIdentity: IndexIdentity,
            RunId: "run:ruby",
            TestCaseIds: ids);

    private const string DiscoveryJson = """
        {
          "version": "3.12.3",
          "examples": [
            {"id":"./spec/calculator_spec.rb[1:1]","description":"adds","full_description":"Calculator adds","status":"passed","file_path":"./spec/calculator_spec.rb","line_number":3,"run_time":0.0},
            {"id":"./spec/calculator_spec.rb[1:2]","description":"subtracts","full_description":"Calculator subtracts","status":"passed","file_path":"./spec/calculator_spec.rb","line_number":7,"run_time":0.0}
          ],
          "summary": {"duration":0.0,"example_count":2,"failure_count":0,"pending_count":0,"errors_outside_of_examples_count":0},
          "summary_line":"2 examples, 0 failures"
        }
        """;

    private const string RunJson = """
        {
          "version": "3.12.3",
          "examples": [
            {"id":"./spec/calculator_spec.rb[1:1]","description":"adds","full_description":"Calculator adds","status":"passed","file_path":"./spec/calculator_spec.rb","line_number":3,"run_time":0.001},
            {"id":"./spec/calculator_spec.rb[1:2]","description":"subtracts","full_description":"Calculator subtracts","status":"failed","file_path":"./spec/calculator_spec.rb","line_number":7,"run_time":0.002,"exception":{"class":"RSpec::Expectations::ExpectationNotMetError","message":"expected: 2\\n     got: 3","backtrace":["./spec/calculator_spec.rb:8"]}},
            {"id":"./spec/calculator_spec.rb[1:3]","description":"divides","full_description":"Calculator divides","status":"pending","file_path":"./spec/calculator_spec.rb","line_number":11,"run_time":0.0,"pending_message":"Temporarily skipped"}
          ],
          "summary": {"duration":0.003,"example_count":3,"failure_count":1,"pending_count":1,"errors_outside_of_examples_count":0},
          "summary_line":"3 examples, 1 failure, 1 pending"
        }
        """;

    private const string CollisionJson = """
        {
          "version": "3.12.3",
          "examples": [
            {"id":"./spec/calculator_spec.rb[1:1]","description":"adds","full_description":"Calculator adds","status":"passed","file_path":"./spec/calculator_spec.rb","line_number":3,"run_time":0.001},
            {"id":"./spec/calculator_spec.rb[1:2]","description":"also adds","full_description":"Calculator also adds","status":"passed","file_path":"./spec/calculator_spec.rb","line_number":3,"run_time":0.002}
          ],
          "summary": {"duration":0.003,"example_count":2,"failure_count":0,"pending_count":0,"errors_outside_of_examples_count":0},
          "summary_line":"2 examples, 0 failures"
        }
        """;

    private sealed class RecordingRunner(Func<TestProcessCommand, TestProcessResult> handler) : ITestProcessRunner
    {
        public List<TestProcessCommand> Calls { get; } = [];

        public Task<TestProcessResult> RunAsync(
            TestProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(command);
            return Task.FromResult(handler(command));
        }
    }
}
