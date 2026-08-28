using System.Security.Cryptography;
using Miller.Testing;
using Miller.Testing.Providers.Qml;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Testing.Providers.Qml;

[Trait("Category", "Scale")]
[Collection(QmlProviderEnvironmentCollection.Name)]
public sealed class QtQuickTestQmakeProviderScaleTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-qmake-scale-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Qmake_quick_test_fixture_runs_a_selected_target_without_source_writes()
    {
        string qmake = CtProviderTestSupport.RequireQmakeQuickTest();
        string make = CtProviderTestSupport.RequireQmakeMake();
        string repositoryFixture = Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "tests",
            "Miller.Tests",
            "Fixtures",
            "QtQuickTestQmakeScale");
        var sourceBefore = Snapshot(repositoryFixture);
        string fixture = CopyFixture(repositoryFixture, Path.Combine(_dir, "qmake quicktest fixture"));
        var copiedBefore = Snapshot(fixture);
        string buildRoot = Path.Combine(_dir, "state with spaces", "ct-build");
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:qmake-scale",
            WorkspaceRoot: fixture,
            ProjectPath: Path.Combine(fixture, "quicktest.pro"),
            BuildOutputRoot: buildRoot,
            Framework: "qt-quick-test",
            Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["backend"] = "qmake",
                ["configure_root"] = fixture,
                ["evidence_root"] = fixture,
                ["project_id"] = "qmake-scale-fixture",
            });
        var provider = new QtQuickTestProvider(
            new TestProcessRunner(),
            qmakePath: qmake,
            makePath: make);
        var cancellationToken = TestContext.Current.CancellationToken;

        var discovered = await provider.DiscoverAsync(workspace, cancellationToken);

        var test = Assert.Single(discovered);
        Assert.Equal("tst_qmake_smoke", test.DisplayName);
        Assert.Equal("qt-quick-test", test.Framework);
        Assert.Equal("qmake", test.Metadata["backend"]);
        var run = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-qmake-scale",
                IndexIdentity: "index-qmake-scale",
                RunId: "run:qmake-selected",
                TestCaseIds: [test.Id]),
            cancellationToken);

        Assert.Equal("passed", run.Status);
        Assert.Equal(test.Id, Assert.Single(run.CaseResults).TestCaseId);
        Assert.True(File.Exists(run.ResultArtifactPath));
        AssertSnapshotUnchanged(sourceBefore, repositoryFixture);
        AssertSnapshotUnchanged(copiedBefore, fixture);
    }

    private static string CopyFixture(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
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

    private static void AssertSnapshotUnchanged(
        IReadOnlyDictionary<string, string> before,
        string root)
    {
        var after = Snapshot(root);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (string path in before.Keys)
            Assert.Equal(before[path], after[path]);
    }
}
