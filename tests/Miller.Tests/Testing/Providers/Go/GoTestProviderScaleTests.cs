using System.Security.Cryptography;
using Miller.Testing;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Testing.Providers.Go;

[Trait("Category", "Scale")]
public sealed class GoTestProviderScaleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("miller-ct-go-scale-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Single_module_fixture_discovers_and_runs_selected_test_without_source_writes()
    {
        string go = CtProviderTestSupport.RequireGo();
        string repositoryFixture = Path.Combine(ScaleTestSupport.RepoRoot(), "tests", "Miller.Tests", "Fixtures", "GoCtScale");
        IReadOnlyDictionary<string, string> sourceBefore = Snapshot(repositoryFixture);
        string fixture = CopyFixture(repositoryFixture, Path.Combine(_dir, "single module"));
        IReadOnlyDictionary<string, string> copiedBefore = Snapshot(fixture);
        var workspace = new ContinuousTestWorkspace(
            "ws:go-scale",
            fixture,
            Path.Combine(fixture, "go.mod"),
            Path.Combine(_dir, "single state", "ct-build"),
            Framework: "go");
        var provider = new GoTestProvider(new TestProcessRunner());

        IReadOnlyList<ProviderTestCase> discovered = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        ProviderTestCase selected = discovered.Single(test => test.Metadata["test_name"] as string == "Test1");
        ProviderRunResult run = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                workspace,
                "rev-go-scale",
                "identity-go-scale",
                RunId: "run:go-scale",
                TestCaseIds: [selected.Id]),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, discovered.Count);
        Assert.Equal("passed", run.Status);
        Assert.Equal("passed", Assert.Single(run.CaseResults).Status);
        Assert.Equal(selected.Id, run.CaseResults[0].TestCaseId);
        AssertSnapshotUnchanged(sourceBefore, repositoryFixture);
        AssertSnapshotUnchanged(copiedBefore, fixture);
        Assert.Equal(go, CtProviderTestSupport.RequireGo());
    }

    [Fact]
    public async Task In_root_workspace_fixture_keeps_modules_as_separate_projects()
    {
        string go = CtProviderTestSupport.RequireGo();
        string repositoryFixture = Path.Combine(ScaleTestSupport.RepoRoot(), "tests", "Miller.Tests", "Fixtures", "GoCtWorkspaceScale");
        string fixture = CopyFixture(repositoryFixture, Path.Combine(_dir, "workspace modules"));
        var first = new ContinuousTestWorkspace(
            "ws:go-work-scale",
            fixture,
            Path.Combine(fixture, "first", "go.mod"),
            Path.Combine(_dir, "first state", "ct-build"),
            Framework: "go",
            Metadata: new Dictionary<string, object?> { ["go_work"] = Path.Combine(fixture, "go.work") });
        var second = first with
        {
            ProjectPath = Path.Combine(fixture, "second", "go.mod"),
            BuildOutputRoot = Path.Combine(_dir, "second state", "ct-build"),
        };

        var factoryProjects = ContinuousTestProjectInventory.Discover(fixture, "ws:go-work-scale")
            .Where(project => project.Framework == "go")
            .ToArray();
        Assert.Equal(2, factoryProjects.Length);
        Assert.All(factoryProjects, project => Assert.Equal(Path.Combine(fixture, "go.work"), project.Metadata["go_work"]));

        var provider = new GoTestProvider(new TestProcessRunner());
        IReadOnlyList<ProviderTestCase> firstCases = await provider.DiscoverAsync(first, TestContext.Current.CancellationToken);
        IReadOnlyList<ProviderTestCase> secondCases = await provider.DiscoverAsync(second, TestContext.Current.CancellationToken);
        Assert.Equal("TestFirst", Assert.Single(firstCases).Metadata["test_name"]);
        Assert.Equal("TestSecond", Assert.Single(secondCases).Metadata["test_name"]);
        Assert.NotEqual(Assert.Single(firstCases).Id, Assert.Single(secondCases).Id);
        Assert.Equal(go, CtProviderTestSupport.RequireGo());
    }

    private static string CopyFixture(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return destination;
    }

    private static IReadOnlyDictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(root, file),
                file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
                StringComparer.Ordinal);

    private static void AssertSnapshotUnchanged(IReadOnlyDictionary<string, string> before, string root)
    {
        IReadOnlyDictionary<string, string> after = Snapshot(root);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (string path in before.Keys)
            Assert.Equal(before[path], after[path]);
    }
}
