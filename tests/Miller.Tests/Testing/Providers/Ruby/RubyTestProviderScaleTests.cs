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
