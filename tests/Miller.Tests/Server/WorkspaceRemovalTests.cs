using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Testing;
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
        Directory.CreateDirectory(Path.Combine(root, ".miller"));
        root = PathCanonicalizer.CanonicalizeRoot(root);
        string millerDir = Path.Combine(root, ".miller");
        File.WriteAllText(Path.Combine(millerDir, "symbols.db"), "stand-in index");
        return (root, millerDir);
    }

    private WorkspaceRegistry OpenRegistry() => WorkspaceRegistry.Open(_registryDb);

    private static void Register(WorkspaceRegistry registry, string id, string display, string root)
    {
        string registeredRoot = Directory.Exists(root) ? PathCanonicalizer.CanonicalizeRoot(root) : root;
        registry.UpsertSeen(
            id,
            display,
            registeredRoot,
            Path.Combine(registeredRoot, ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready);
    }

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
    public void RemoveById_WhenCtStoreLockHeld_RefusedInUse_CtDbIntact()
    {
        var (root, millerDir) = MakeWorkspace("ws-ct-locked");
        string ctDb = Path.Combine(millerDir, CtSchema.DbFileName);
        File.WriteAllText(ctDb, "active-ct-store");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-ct-locked-000001", "ct-locked-disp", root);

        using CtWriteLock held = CtWriteLock.AcquireFor(ctDb, TimeSpan.FromMilliseconds(200));

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "ct-locked-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.RefusedInUse, result.Result);
        Assert.True(File.Exists(ctDb));
        Assert.Equal("active-ct-store", File.ReadAllText(ctDb));
        Assert.True(File.Exists(Path.Combine(millerDir, "symbols.db")));
        Assert.NotNull(registry.Get("ws-ct-locked-000001"));
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

    [Fact]
    public void RemoveById_RegistryIndexPathOutsideWorkspaceRefusesWithoutDeleting()
    {
        string registeredRoot = Path.Combine(_dir, "registered-root");
        Directory.CreateDirectory(registeredRoot);
        registeredRoot = PathCanonicalizer.CanonicalizeRoot(registeredRoot);
        var (_, victimMillerDir) = MakeWorkspace("victim-root");
        string victimDb = Path.Combine(victimMillerDir, "symbols.db");
        using WorkspaceRegistry registry = OpenRegistry();
        registry.UpsertSeen(
            "ws-corrupt-00000001",
            "corrupt-disp",
            registeredRoot,
            victimDb,
            WorkspaceRegistryState.Ready);

        WorkspaceRemoveResult result =
            WorkspaceRemoval.RemoveById(registry, "corrupt-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.RefusedInvalidRegistration, result.Result);
        Assert.True(File.Exists(victimDb));
        Assert.NotNull(registry.Get("ws-corrupt-00000001"));
    }

    [Fact]
    public void RemoveById_SymlinkedRegisteredMillerDirectoryRefusesWithoutDeletingTarget()
    {
        string registeredRoot = Path.Combine(_dir, "registered-root");
        Directory.CreateDirectory(registeredRoot);
        var (_, victimMillerDir) = MakeWorkspace("victim-root");
        string registeredMillerDir = Path.Combine(registeredRoot, ".miller");
        if (!TryCreateDirectoryLink(registeredMillerDir, victimMillerDir))
            return;

        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-symlink-00000001", "symlink-disp", registeredRoot);

        WorkspaceRemoveResult result =
            WorkspaceRemoval.RemoveById(registry, "symlink-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.RefusedInvalidRegistration, result.Result);
        Assert.True(File.Exists(Path.Combine(victimMillerDir, "symbols.db")));
        Assert.NotNull(registry.Get("ws-symlink-00000001"));
    }

    [Fact]
    public void RemoveById_SymlinkedRegisteredRootRefusesWithoutDeletingTarget()
    {
        var (victimRoot, victimMillerDir) = MakeWorkspace("victim-root");
        string registeredRoot = Path.Combine(_dir, "registered-root-link");
        if (!TryCreateDirectoryLink(registeredRoot, victimRoot))
            return;

        using WorkspaceRegistry registry = OpenRegistry();
        registry.UpsertSeen(
            "ws-root-symlink-0001",
            "root-symlink-disp",
            registeredRoot,
            Path.Combine(registeredRoot, ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready);

        WorkspaceRemoveResult result =
            WorkspaceRemoval.RemoveById(registry, "root-symlink-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.RefusedInvalidRegistration, result.Result);
        Assert.True(File.Exists(Path.Combine(victimMillerDir, "symbols.db")));
        Assert.NotNull(registry.Get("ws-root-symlink-0001"));
    }

    [Fact]
    public void RemoveById_ProtectedMillerDirectoryRefusesWithoutDeleting()
    {
        var (root, millerDir) = MakeWorkspace("ws-protected");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-protected-000001", "protected-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(
            registry,
            "protected-disp",
            liveRoot: null,
            protectedMillerDir: millerDir);

        Assert.Equal(WorkspaceRemoveResult.Outcome.RefusedSensitive, result.Result);
        Assert.True(File.Exists(Path.Combine(millerDir, "symbols.db")));
        Assert.NotNull(registry.Get("ws-protected-000001"));
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
    public void RemoveByPath_ProtectedMillerDirectoryRefusesWithoutDeleting()
    {
        var (root, millerDir) = MakeWorkspace("ws-bypath-protected");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-bypath-protected-1", "bypath-protected-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveByPath(
            registry,
            root,
            liveRoot: null,
            protectedMillerDir: millerDir);

        Assert.Equal(WorkspaceRemoveResult.Outcome.RefusedSensitive, result.Result);
        Assert.True(File.Exists(Path.Combine(millerDir, "symbols.db")));
        Assert.NotNull(registry.Get("ws-bypath-protected-1"));
    }

    [Fact]
    public void RemoveByPath_UnregisteredDirWithMillerDataRefusesImplicitDeletion()
    {
        var (root, millerDir) = MakeWorkspace("ws-unregistered");
        using WorkspaceRegistry registry = OpenRegistry();

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveByPath(registry, root, liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.NotFound, result.Result);
        Assert.True(Directory.Exists(millerDir));
        Assert.Contains("no registered workspace", WorkspaceRender.Remove(result, json: false));
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

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
