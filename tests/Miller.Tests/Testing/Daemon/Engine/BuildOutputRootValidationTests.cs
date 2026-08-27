using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class BuildOutputRootValidationTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-buildroot-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Enqueue_accepts_a_build_root_inside_the_workspace_miller_sidecar()
    {
        ContinuousTestWorkspace workspace = WorkspaceWithBuildRoot(
            Path.Combine(_root, ".miller", "ct", "build", "0123456789ab"));
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        ContinuousTestDaemonQueue queue = Queue(store);

        ContinuousTestDaemonEnqueueResult result = queue.Enqueue(EngineTestSupport.Change(workspace));

        Assert.NotNull(result);
    }

    [Fact]
    public void Enqueue_accepts_the_flattened_build_root_directly_under_the_miller_sidecar()
    {
        ContinuousTestWorkspace workspace = WorkspaceWithBuildRoot(
            Path.Combine(_root, ".miller", "ct-0123456789ab"));
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        ContinuousTestDaemonQueue queue = Queue(store);

        ContinuousTestDaemonEnqueueResult result = queue.Enqueue(EngineTestSupport.Change(workspace));

        Assert.NotNull(result);
    }

    [Fact]
    public void Enqueue_accepts_a_build_root_under_the_machine_temp_build_root()
    {
        ContinuousTestWorkspace workspace = WorkspaceWithBuildRoot(
            Path.Combine(CtTempPaths.BuildRoot, "0123456789ab", "ba9876543210"));
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        ContinuousTestDaemonQueue queue = Queue(store);

        ContinuousTestDaemonEnqueueResult result = queue.Enqueue(EngineTestSupport.Change(workspace));

        Assert.NotNull(result);
    }

    [Fact]
    public void Enqueue_rejects_a_workspace_build_root_outside_the_miller_sidecar()
    {
        ContinuousTestWorkspace workspace = WorkspaceWithBuildRoot(
            Path.Combine(_root, "bin", "ct-build"));
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        ContinuousTestDaemonQueue queue = Queue(store);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => queue.Enqueue(EngineTestSupport.Change(workspace)));

        Assert.Contains(".miller", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enqueue_rejects_a_build_root_outside_both_permitted_locations()
    {
        ContinuousTestWorkspace workspace = WorkspaceWithBuildRoot(
            Path.Combine(Path.GetTempPath(), "miller-ct-build", "elsewhere"));
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        ContinuousTestDaemonQueue queue = Queue(store);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => queue.Enqueue(EngineTestSupport.Change(workspace)));

        Assert.Contains(".miller", exception.Message, StringComparison.Ordinal);
    }

    private static ContinuousTestDaemonQueue Queue(ContinuousTestStore store) =>
        new(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store));

    private ContinuousTestWorkspace WorkspaceWithBuildRoot(string buildRoot)
    {
        string project = Path.Combine(_root, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        if (!File.Exists(project))
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return new ContinuousTestWorkspace(
            EngineTestSupport.WorkspaceId,
            _root,
            project,
            buildRoot);
    }
}
