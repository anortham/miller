using Miller.Testing;
using Miller.Testing.Providers.Ruby;
using Xunit;

namespace Miller.Tests.Testing.Providers.Ruby;

[Trait("Category", "Scale")]
public sealed class RubyTestProviderScaleTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-ruby-scale-").FullName;

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
    public async Task Direct_recipe_executes_literal_example_with_configured_runner()
    {
        CtProviderTestSupport.RequireRuby();
        CtProviderTestSupport.RequireRspec();
        File.WriteAllText(Path.Combine(_root, "Gemfile"), "source 'https://rubygems.org'\ngem 'rspec'\n");
        Directory.CreateDirectory(Path.Combine(_root, "spec"));
        File.WriteAllText(Path.Combine(_root, "spec", "literal_spec.rb"), """
            RSpec.describe 'Selector' do
              it('status: [ready]') { expect(true).to eq(true) }
              it('status: ready') { raise 'unrelated example must not run' }
            end
            """);
        ContinuousTestRunRecipe recipe = ContinuousTestRecipeBuilder.Build(
            new ContinuousTestRunRecipeRequest("ruby-recipe", _root, Path.Combine(_root, "Gemfile"),
                "rspec", "status: [ready]", ConfiguredCommand: "rspec --format progress"));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));

        TestProcessResult result = await new TestProcessRunner().RunAsync(
            OperatingSystem.IsWindows()
                ? new TestProcessCommand("powershell", ["-NoProfile", "-Command", recipe.PrimaryCommand], _root)
                : new TestProcessCommand("sh", ["-c", recipe.PrimaryCommand], _root),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("1 example, 0 failures", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rspec_smoke_discovers_and_runs_one_passing_and_one_failing_example()
    {
        CtProviderTestSupport.RequireRuby();
        CtProviderTestSupport.RequireRspec();
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(_root, "Gemfile"), "source 'https://rubygems.org'\ngem 'rspec'\n");
        Directory.CreateDirectory(Path.Combine(_root, "spec"));
        File.WriteAllText(Path.Combine(_root, "spec", "calculator_spec.rb"), """
            RSpec.describe 'Calculator' do
              it('adds') { expect(1 + 1).to eq(2) }
              it('subtracts') { expect(2 + 2).to eq(5) }
            end
            """);

        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:ruby-scale",
            WorkspaceRoot: _root,
            ProjectPath: Path.Combine(_root, "Gemfile"),
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-ruby"),
            Framework: "rspec");
        var provider = new RubyTestProvider(new TestProcessRunner());

        IReadOnlyList<ProviderTestCase> discovered = await provider.DiscoverAsync(workspace, cancellationToken);
        ProviderRunResult result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-ruby-scale",
                IndexIdentity: "store:ruby-scale",
                RunId: "run:ruby-scale",
                TestCaseIds: discovered.Select(test => test.Id).ToArray()),
            cancellationToken);

        Assert.Equal(2, discovered.Count);
        Assert.Equal("failed", result.Status);
        Assert.Equal(2, result.CaseResults.Count);
        Assert.Contains(result.CaseResults, row => row.Status == "passed");
        Assert.Contains(result.CaseResults, row => row.Status == "failed");
        Assert.NotNull(result.ResultArtifactPath);
        Assert.True(File.Exists(result.ResultArtifactPath!));
    }
}
