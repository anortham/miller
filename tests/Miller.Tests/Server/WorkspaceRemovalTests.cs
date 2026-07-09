using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the shared removal core <see cref="WorkspaceRemoval"/> extracted from the CLI's
/// <c>workspace remove</c>: registry row resolution, the gone-root best-effort prune (R4), the live-root
/// refusal (applied only when a live root is supplied), the in-use lock refusal, and registry row removal.
/// Everything runs against a per-test temp registry and temp <c>.miller</c> dirs — never the real
/// <c>~/.miller</c>.
/// </summary>
public sealed class WorkspaceRemovalTests : IDisposable
{
    private readonly string _dir;
    private readonly string _registryDb;

    public WorkspaceRemovalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-removal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registryDb = Path.Combine(_dir, "workspaces.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (string Root, string MillerDir) MakeWorkspace(string name)
    {
        string root = Path.Combine(_dir, name);
        string millerDir = Path.Combine(root, ".miller");
        Directory.CreateDirectory(millerDir);
        File.WriteAllText(Path.Combine(millerDir, "symbols.db"), "stand-in index");
        return (root, millerDir);
    }

    private WorkspaceRegistry OpenRegistry() => WorkspaceRegistry.Open(_registryDb);

    private static void Register(WorkspaceRegistry registry, string id, string display, string root) =>
        registry.UpsertSeen(id, display, root, Path.Combine(root, ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready);

    // ---------- RemoveById ----------

    [Fact]
    public void RemoveById_DeletesMillerDirAndUnregisters()
    {
        var (root, millerDir) = MakeWorkspace("ws-remove");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-remove-00000001", "remove-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "remove-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.True(result.IndexDirDeleted);
        Assert.Equal("ws-remove-00000001", result.WorkspaceId);
        Assert.Equal(root, result.Root);
        Assert.False(Directory.Exists(millerDir));
        Assert.Null(registry.Get("ws-remove-00000001"));
    }

    [Fact]
    public void RemoveById_UnknownSelector_ThrowsKeyNotFound()
    {
        using WorkspaceRegistry registry = OpenRegistry();
        Assert.Throws<KeyNotFoundException>(
            () => WorkspaceRemoval.RemoveById(registry, "does-not-exist", liveRoot: null));
    }

    [Fact]
    public void RemoveById_MissingMillerDir_PrunesOrphanRowWithoutDirDelete()
    {
        string root = Path.Combine(_dir, "ws-orphan");
        Directory.CreateDirectory(root); // root exists but holds no .miller dir
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-orphan-00000001", "orphan-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "orphan-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(result.IndexDirDeleted);
        Assert.Null(registry.Get("ws-orphan-00000001"));
    }

    [Fact]
    public void RemoveById_WriterLockHeld_RefusedInUse_NothingDeleted()
    {
        var (root, millerDir) = MakeWorkspace("ws-locked");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-locked-00000001", "locked-disp", root);

        using IDisposable? held = SingleWriterLock.TryAcquire(millerDir);
        Assert.NotNull(held); // this test IS the other writer

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "locked-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.RefusedInUse, result.Result);
        Assert.True(File.Exists(Path.Combine(millerDir, "symbols.db")));
        Assert.NotNull(registry.Get("ws-locked-00000001"));
    }

    [Fact]
    public void RemoveById_LiveRoot_RefusedLive_NothingDeleted()
    {
        var (root, millerDir) = MakeWorkspace("ws-live");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-live-00000001", "live-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "live-disp", liveRoot: root);

        Assert.Equal(WorkspaceRemoveResult.Outcome.RefusedLive, result.Result);
        Assert.True(Directory.Exists(millerDir));
        Assert.NotNull(registry.Get("ws-live-00000001"));
    }

    [Fact]
    public void RemoveById_DifferentLiveRoot_StillRemoves()
    {
        var (root, millerDir) = MakeWorkspace("ws-other");
        string liveRoot = Path.Combine(_dir, "ws-live-elsewhere");
        Directory.CreateDirectory(liveRoot);
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-other-00000001", "other-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "other-disp", liveRoot: liveRoot);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(Directory.Exists(millerDir));
    }

    // ---------- RemoveByPath ----------

    [Fact]
    public void RemoveByPath_RegisteredDir_DeletesAndUnregisters()
    {
        var (root, millerDir) = MakeWorkspace("ws-bypath");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-bypath-00000001", "bypath-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveByPath(registry, root, liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(Directory.Exists(millerDir));
        Assert.Null(registry.Get("ws-bypath-00000001"));
    }

    [Fact]
    public void RemoveByPath_UnregisteredDirWithMillerData_DeletesLocally()
    {
        var (root, millerDir) = MakeWorkspace("ws-unregistered");
        using WorkspaceRegistry registry = OpenRegistry();

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveByPath(registry, root, liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(Directory.Exists(millerDir));
    }

    [Fact]
    public void RemoveByPath_GoneRoot_PrunesStaleRegistryRow()
    {
        string goneRoot = Path.Combine(_dir, "ws-gone"); // never created on disk
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-gone-00000001", "gone-disp", goneRoot);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveByPath(registry, goneRoot, liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(result.IndexDirDeleted);
        Assert.Equal("ws-gone-00000001", result.WorkspaceId);
        Assert.Null(registry.Get("ws-gone-00000001"));
    }

    [Fact]
    public void RemoveByPath_GoneRootUnregistered_NotFound()
    {
        string goneRoot = Path.Combine(_dir, "ws-never-registered");
        using WorkspaceRegistry registry = OpenRegistry();

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveByPath(registry, goneRoot, liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.NotFound, result.Result);
    }
}
