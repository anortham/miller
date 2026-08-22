using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Testing;
using System.Text.Json;
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
    private readonly string _millerDirectory;
    private readonly List<string> _globalPolicies = [];

    public WorkspaceRemovalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-removal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registryDb = Path.Combine(_dir, "workspaces.db");
        _millerDirectory = Path.Combine(_dir, "miller-home", ".miller");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string path in _globalPolicies)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                else if (Directory.Exists(path))
                    Directory.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
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

    private string WriteGlobalPolicy(string workspaceId, string content = "# Miller generated\n")
    {
        string path = JulieIgnoreSeeder.GeneratedGlobalIgnorePathForWorkspaceId(workspaceId, _millerDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _globalPolicies.Add(path);
        return path;
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
    public void RemoveById_DeletesOnlyTheMatchingGlobalPolicyAndLeavesRootPolicyUntouched()
    {
        var (root, millerDir) = MakeWorkspace("ws-ignore-remove");
        const string LegacyRootPolicy = "# generated-looking legacy file\nnode_modules/\n";
        string rootPolicy = Path.Combine(root, JulieIgnoreSeeder.WorkspaceIgnoreFileName);
        File.WriteAllText(rootPolicy, LegacyRootPolicy);
        string workspaceId = WorkspaceId.FromCanonicalRoot(root);
        string policy = WriteGlobalPolicy(workspaceId);
        string otherPolicy = WriteGlobalPolicy("ws-ignore-remove-000002", "# keep\n");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, workspaceId, "ignore-remove-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(
            registry,
            "ignore-remove-disp",
            liveRoot: null,
            millerDirectory: _millerDirectory);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(Directory.Exists(millerDir));
        Assert.False(File.Exists(policy));
        Assert.True(File.Exists(otherPolicy));
        Assert.Equal(LegacyRootPolicy, File.ReadAllText(rootPolicy));
        using JsonDocument json = JsonDocument.Parse(WorkspaceRender.Remove(result, json: true));
        Assert.Equal(
            JsonValueKind.Null,
            json.RootElement.GetProperty("ignore_policy_cleanup_error").ValueKind);
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

    /// <summary>
    /// A running CT daemon holds <c>.miller/ct/daemon-v1.lock</c> one level below the write leases the remove
    /// bundle can hold, so before the daemon joined the refuse-before-delete contract the recursive delete of
    /// <c>.miller</c> threw PARTWAY THROUGH: sidecars gone, registry row still there. The refusal must be clean.
    /// </summary>
    [Fact]
    public void RemoveById_WhenCtDaemonLeaseHeld_RefusedInUse_NothingDeleted()
    {
        var (root, millerDir) = MakeWorkspace("ws-ct-daemon");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-ct-daemon-000001", "ct-daemon-disp", root);

        using CtDaemonLease? daemon = CtDaemonLease.TryAcquire(root, "1.20.1-test");
        Assert.NotNull(daemon); // this test IS the running daemon

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "ct-daemon-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.RefusedInUse, result.Result);
        Assert.True(File.Exists(Path.Combine(millerDir, "symbols.db")));
        Assert.True(File.Exists(CtDaemonProtocol.LockPath(root)));
        Assert.True(File.Exists(CtDaemonProtocol.LeasePath(root)));
        Assert.NotNull(registry.Get("ws-ct-daemon-000001"));

        // The reason must reach the user through the SAME two channels as every other refusal.
        Assert.Contains("in use", WorkspaceRender.Remove(result, json: false), StringComparison.Ordinal);
        Assert.Contains("\"refused_in_use\"", WorkspaceRender.Remove(result, json: true), StringComparison.Ordinal);
    }

    /// <summary>
    /// The lock FILE outlives the daemon that held it, so the probe must test the HANDLE. If it tested file
    /// existence, every workspace that ever started CT would be unremovable forever.
    /// </summary>
    [Fact]
    public void RemoveById_StaleCtDaemonLockFileWithNoHolder_StillRemoves()
    {
        var (root, millerDir) = MakeWorkspace("ws-ct-daemon-stale");
        string ctDir = Path.Combine(millerDir, CtDaemonProtocol.DirectoryName);
        Directory.CreateDirectory(ctDir);
        File.WriteAllText(Path.Combine(ctDir, CtDaemonProtocol.LockFileName), "");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-ct-stale-000001", "ct-stale-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "ct-stale-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.True(result.IndexDirDeleted);
        Assert.False(Directory.Exists(millerDir));
        Assert.Null(registry.Get("ws-ct-stale-000001"));
    }

    /// <summary>
    /// The OS handle is the signal and <c>daemon.lease.json</c> is NOT authoritative. A daemon killed without
    /// releasing (kill -9, power loss) leaves that JSON behind, and <c>CtDaemonLease.IsIdentityLive</c> answers
    /// "live" for any PID it cannot probe. Here the leftover JSON names THIS process, so a lease-JSON probe
    /// reads a live daemon — asserted directly, so the test discriminates rather than assumes. No handle is
    /// held, so the remove must go through.
    /// </summary>
    [Fact]
    public void RemoveById_StaleCtDaemonLeaseJsonWithNoHeldHandle_StillRemoves()
    {
        var (root, millerDir) = MakeWorkspace("ws-ct-stale-json");
        string ctDir = Path.Combine(millerDir, CtDaemonProtocol.DirectoryName);
        Directory.CreateDirectory(ctDir);
        File.WriteAllText(Path.Combine(ctDir, CtDaemonProtocol.LockFileName), "");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var leftover = new CtDaemonLeaseRecord(CtDaemonLease.CurrentIdentity(), now, root, "1.20.1-test");
        File.WriteAllText(CtDaemonProtocol.LeasePath(root), CtDaemonJson.Serialize(leftover));
        Assert.NotNull(CtDaemonLease.TryReadLive(root)); // the weaker probe WOULD see a live daemon here

        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-ct-json-0000001", "ct-json-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "ct-json-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(Directory.Exists(millerDir));
        Assert.Null(registry.Get("ws-ct-json-0000001"));
    }

    /// <summary>
    /// The mirror of the test above: a held handle and NO lease JSON at all. Together the pair pins the handle
    /// as the signal and the JSON as non-authoritative — neither test alone can tell the two probes apart,
    /// because <c>CtDaemonLease.TryAcquire</c> takes the handle AND writes the JSON in one call.
    /// </summary>
    [Fact]
    public void RemoveById_HeldCtDaemonLockWithNoLeaseJson_RefusedInUse()
    {
        var (root, millerDir) = MakeWorkspace("ws-ct-bare-handle");
        Directory.CreateDirectory(Path.Combine(millerDir, CtDaemonProtocol.DirectoryName));
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-ct-bare-0000001", "ct-bare-disp", root);

        using var held = new FileStream(
            CtDaemonProtocol.LockPath(root), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(CtDaemonLease.TryRead(root)); // the weaker probe has NOTHING to see here

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "ct-bare-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.RefusedInUse, result.Result);
        Assert.True(File.Exists(Path.Combine(millerDir, "symbols.db")));
        Assert.NotNull(registry.Get("ws-ct-bare-0000001"));
    }

    /// <summary>
    /// A lease that cannot be opened AT ALL is not the same fact as a lease somebody holds. A
    /// <see cref="FileShare.None"/> holder produces a sharing violation; a read-only attribute, a denying ACL,
    /// or a directory where the file belongs produces an access denial with no daemon anywhere. Reading the
    /// second as "held" refused every future remove with a reason naming a writer that does not exist.
    /// The test asserts the denial is real and is NOT contention, so it discriminates the two paths.
    /// </summary>
    [Fact]
    public void RemoveById_CtDaemonLockThatCannotBeProbedAndNobodyHolds_StillRemoves()
    {
        var (root, millerDir) = MakeWorkspace("ws-ct-unprobeable");
        string lockPath = CtDaemonProtocol.LockPath(root);
        Directory.CreateDirectory(lockPath); // a directory sitting where the lock file belongs
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-ct-denied-000001", "ct-denied-disp", root);

        Exception? denial = null;
        try
        {
            using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            denial = ex;
        }

        Assert.NotNull(denial); // the probe genuinely cannot open this path
        Assert.False(
            denial is IOException io && SingleWriterLock.IsLockContention(io, OperatingSystem.IsWindows()),
            "this test only discriminates while the denial is something OTHER than a sharing violation");

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "ct-denied-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(File.Exists(Path.Combine(millerDir, "symbols.db")));
        Assert.Null(registry.Get("ws-ct-denied-000001"));
    }

    /// <summary>
    /// The daemon lease is HELD across the delete, exactly like the other four holders — not probed and let go.
    /// The injected writer-lock acquisition runs INSIDE the remove, after the daemon lease is taken and before
    /// anything is deleted, so a daemon that tries to start in that window must be refused.
    /// </summary>
    [Fact]
    public void RemoveById_HoldsTheCtDaemonLeaseAcrossTheDelete_SoNoDaemonCanStartMidDelete()
    {
        var (root, millerDir) = MakeWorkspace("ws-ct-hold");
        Directory.CreateDirectory(Path.Combine(millerDir, CtDaemonProtocol.DirectoryName));
        File.WriteAllText(CtDaemonProtocol.LockPath(root), "");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-ct-hold-0000001", "ct-hold-disp", root);

        CtDaemonLease? startedMidDelete = null;
        var reachedTheDelete = false;
        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(
            registry,
            "ct-hold-disp",
            liveRoot: null,
            protectedMillerDir: null,
            acquireWriterLock: dir =>
            {
                reachedTheDelete = true;
                startedMidDelete = CtDaemonLease.TryAcquire(root, "1.20.1-test");
                return SingleWriterLock.TryAcquire(dir);
            });

        startedMidDelete?.Dispose();
        Assert.True(reachedTheDelete, "the seam never ran, so the test proves nothing");
        Assert.Null(startedMidDelete); // the lease was NOT free while the delete ran
        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.Null(registry.Get("ws-ct-hold-0000001"));
    }

    /// <summary>
    /// The never-ran-CT case has no handle to hold, so a daemon really can start mid-delete. The guarded delete
    /// must therefore leave <c>.miller/ct</c> alone: on Windows the daemon's open <see cref="FileShare.None"/>
    /// handle makes a recursive delete of that directory throw, and the throw used to escape
    /// <c>RemoveById</c> with the index already half gone and the registry row still present.
    ///
    /// <para>The release hook observes the directory at the moment the write leases are released — after the
    /// guarded delete has run and before the best-effort <c>TryDeleteEmptiedDir</c>. That is the only instant at
    /// which "the guarded delete spared <c>ct/</c>" is visible, and it is visible on every platform: POSIX
    /// unlink ignores the daemon's advisory lock, so without the observation this case could only ever go red
    /// on Windows.</para>
    /// </summary>
    [Fact]
    public void RemoveById_WhenACtDaemonStartsMidDelete_CompletesCleanlyAndStillRemovesTheIndex()
    {
        var (root, millerDir) = MakeWorkspace("ws-ct-mid-delete"); // no .miller/ct at all
        string ctDir = Path.Combine(millerDir, CtDaemonProtocol.DirectoryName);
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-ct-mid-0000001", "ct-mid-disp", root);

        CtDaemonLease? startedMidDelete = null;
        bool? ctDirSurvivedTheGuardedDelete = null;
        try
        {
            WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(
                registry,
                "ct-mid-disp",
                liveRoot: null,
                protectedMillerDir: null,
                acquireWriterLock: dir =>
                {
                    startedMidDelete = CtDaemonLease.TryAcquire(root, "1.20.1-test");
                    SingleWriterLock? acquired = SingleWriterLock.TryAcquire(dir);
                    if (acquired is null)
                        return null;

                    SingleWriterLock indexer = acquired;
                    return new ReleaseHook(() =>
                    {
                        ctDirSurvivedTheGuardedDelete = Directory.Exists(ctDir);
                        indexer.Dispose();
                    });
                });

            Assert.NotNull(startedMidDelete); // nothing held it, so the daemon really did start
            Assert.True(
                ctDirSurvivedTheGuardedDelete is true,
                "the guarded delete must skip .miller/ct while a daemon owns a handle inside it");
            Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
            Assert.False(File.Exists(Path.Combine(millerDir, "symbols.db")));
            Assert.Null(registry.Get("ws-ct-mid-0000001"));
        }
        finally
        {
            startedMidDelete?.Dispose();
        }
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
    public void RemoveByPath_DeletesOnlyTheMatchingGlobalPolicyAndLeavesEditedRootPolicyUntouched()
    {
        var (root, _) = MakeWorkspace("ws-ignore-bypath");
        const string EditedRootPolicy = "# edited old policy\ncustom/\n";
        string rootPolicy = Path.Combine(root, JulieIgnoreSeeder.WorkspaceIgnoreFileName);
        File.WriteAllText(rootPolicy, EditedRootPolicy);
        string workspaceId = WorkspaceId.FromCanonicalRoot(root);
        string policy = WriteGlobalPolicy(workspaceId);
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, workspaceId, "ignore-bypath-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveByPath(
            registry,
            root,
            liveRoot: null,
            millerDirectory: _millerDirectory);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(File.Exists(policy));
        Assert.Equal(EditedRootPolicy, File.ReadAllText(rootPolicy));
    }

    [Fact]
    public void RemoveById_DerivesPolicyFromCanonicalRootInsteadOfRegistryId()
    {
        var (root, _) = MakeWorkspace("ws-ignore-canonical");
        string canonicalPolicy = WriteGlobalPolicy(WorkspaceId.FromCanonicalRoot(root), "# canonical\n");
        string rowPolicy = WriteGlobalPolicy("foreign-policy-id", "# foreign\n");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "foreign-policy-id", "ignore-canonical-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(
            registry,
            "ignore-canonical-disp",
            liveRoot: null,
            millerDirectory: _millerDirectory);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(File.Exists(canonicalPolicy));
        Assert.True(File.Exists(rowPolicy));
    }

    [Fact]
    public void RemoveById_MaliciousWorkspaceIdCannotTargetGlobalPolicy()
    {
        var (root, millerDir) = MakeWorkspace("ws-ignore-malicious");
        string escaped = Path.Combine(_millerDirectory, "escaped.julieignore");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(escaped)!);
            File.WriteAllText(escaped, "# must remain\n");
            using WorkspaceRegistry registry = OpenRegistry();
            Register(registry, "../escaped", "ignore-malicious-disp", root);

            WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(
                registry,
                "ignore-malicious-disp",
                liveRoot: null,
                millerDirectory: _millerDirectory);

            Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
            Assert.False(Directory.Exists(millerDir));
            Assert.True(File.Exists(escaped));
            Assert.Null(registry.Get("../escaped"));
        }
        finally
        {
            try { File.Delete(escaped); } catch (IOException) { }
        }
    }

    [Fact]
    public void RemoveById_ReportsGlobalPolicyCleanupFailureWithoutExpandingDeletion()
    {
        var (root, millerDir) = MakeWorkspace("ws-ignore-cleanup-failure");
        string workspaceId = WorkspaceId.FromCanonicalRoot(root);
        string policyPath = JulieIgnoreSeeder.GeneratedGlobalIgnorePathForWorkspaceId(
            workspaceId,
            _millerDirectory);
        Directory.CreateDirectory(policyPath);
        _globalPolicies.Add(policyPath);
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, workspaceId, "ignore-cleanup-failure-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(
            registry,
            "ignore-cleanup-failure-disp",
            liveRoot: null,
            millerDirectory: _millerDirectory);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(Directory.Exists(millerDir));
        Assert.Equal("the generated policy path is a directory", result.IgnorePolicyCleanupError);
        Assert.Contains(
            "generated ignore policy cleanup failed",
            WorkspaceRender.Remove(result, json: false),
            StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(WorkspaceRender.Remove(result, json: true));
        Assert.Equal(
            "the generated policy path is a directory",
            json.RootElement.GetProperty("ignore_policy_cleanup_error").GetString());
        Assert.True(Directory.Exists(policyPath));
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

    // ---------- family-store sidecar reclaim ----------

    private StoreFamilyRegistryRow SeedFamily(
        WorkspaceRegistry registry,
        string lineage,
        bool createStoreRoot = true)
    {
        StoreFamilyRegistryRow family = registry.GetOrCreateStoreFamily(
            lineage, canonicalCommonDir: null, commonDirCreatedAtUtc: null,
            storesRoot: Path.Combine(_dir, "stores"));
        if (createStoreRoot)
            Directory.CreateDirectory(Path.Combine(family.StoreRoot, "sidecars"));
        return family;
    }

    private static IReadOnlyList<string> WriteSidecars(string storeRoot, string viewId)
    {
        var paths = new List<string>();
        foreach (StoreSidecarKind kind in Enum.GetValues<StoreSidecarKind>())
        {
            string path = StoreSidecarCatalog.PathFor(storeRoot, kind, viewId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[128]);
            paths.Add(path);
        }

        return paths;
    }

    [Fact]
    public void RemoveById_RecordsTheOwedReclaimBeforeTheRegistryRowIsDeleted()
    {
        var (root, _) = MakeWorkspace("ws-store-intent");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-store-intent-01", "store-intent-disp", root);
        StoreFamilyRegistryRow family = SeedFamily(registry, "lineage-intent");
        registry.UpsertStoreMember(
            "ws-store-intent-01", family.FamilyId, "view-intent", root, WorkspaceRootIdentity.Unknown);
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-intent");
        string sidecarDir = StoreSidecarCatalog.DirectoryFor(family.StoreRoot);

        // The sidecar lease is taken INSIDE the reclaim, which runs after the registry delete. A record that is
        // already on disk at that moment was written BEFORE the delete, so a crash in the window between the two
        // still leaves the view id named on disk. Without the intent write there is nothing to find it by.
        bool recordedAtLeaseTime = false;
        bool rowGoneAtLeaseTime = false;
        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(
            registry,
            "store-intent-disp",
            liveRoot: null,
            acquireSidecarLease: _ =>
            {
                recordedAtLeaseTime = Directory
                    .GetFiles(sidecarDir, "*" + StoreSidecarReclaim.OwedRecordSuffix).Length == 1;
                rowGoneAtLeaseTime = registry.Get("ws-store-intent-01") is null;
                return null;
            });

        Assert.True(rowGoneAtLeaseTime, "the registry row must already be gone when the reclaim runs");
        Assert.True(recordedAtLeaseTime, "the owed record must be written before the registry row is deleted");
        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void RemoveById_DeletesTheRemovedViewsStoreSidecars()
    {
        var (root, _) = MakeWorkspace("ws-store-member");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-store-mem-000001", "store-mem-disp", root);
        StoreFamilyRegistryRow family = SeedFamily(registry, "lineage-remove");
        registry.UpsertStoreMember(
            "ws-store-mem-000001", family.FamilyId, "view-remove", root, WorkspaceRootIdentity.Unknown);
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-remove");

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "store-mem-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.Equal(3, result.SidecarReclaim.FilesDeleted);
        Assert.Equal(3 * 128, result.SidecarReclaim.BytesReclaimed);
        Assert.Null(result.SidecarReclaim.SkipReason);
        Assert.All(paths, p => Assert.False(File.Exists(p)));
    }

    [Fact]
    public void RemoveById_LeavesTheOtherMembersSidecarsAlone()
    {
        var (goingRoot, _) = MakeWorkspace("ws-store-going");
        var (stayingRoot, _) = MakeWorkspace("ws-store-staying");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-store-going-0001", "store-going-disp", goingRoot);
        Register(registry, "ws-store-stay-0001", "store-stay-disp", stayingRoot);
        StoreFamilyRegistryRow family = SeedFamily(registry, "lineage-neighbour");
        registry.UpsertStoreMember(
            "ws-store-going-0001", family.FamilyId, "view-going", goingRoot, WorkspaceRootIdentity.Unknown);
        registry.UpsertStoreMember(
            "ws-store-stay-0001", family.FamilyId, "view-staying", stayingRoot, WorkspaceRootIdentity.Unknown);
        WriteSidecars(family.StoreRoot, "view-going");
        IReadOnlyList<string> keep = WriteSidecars(family.StoreRoot, "view-staying");

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "store-going-disp", liveRoot: null);

        Assert.Equal(3, result.SidecarReclaim.FilesDeleted);
        Assert.All(keep, p => Assert.True(File.Exists(p)));
        Assert.NotNull(registry.GetStoreMember("ws-store-stay-0001"));
    }

    [Fact]
    public void RemoveById_MissingStoreRoot_StillRemovesAndReportsNothing()
    {
        var (root, millerDir) = MakeWorkspace("ws-store-no-root");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-store-noroot-01", "store-noroot-disp", root);
        StoreFamilyRegistryRow family = SeedFamily(registry, "lineage-absent", createStoreRoot: false);
        registry.UpsertStoreMember(
            "ws-store-noroot-01", family.FamilyId, "view-absent", root, WorkspaceRootIdentity.Unknown);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "store-noroot-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(Directory.Exists(millerDir));
        Assert.False(result.SidecarReclaim.HasReport);
        Assert.False(Directory.Exists(family.StoreRoot));
        Assert.Null(registry.Get("ws-store-noroot-01"));
    }

    [Fact]
    public void RemoveById_SidecarLeaseUnavailable_RemovesAnywayAndReportsTheSkip()
    {
        var (root, millerDir) = MakeWorkspace("ws-store-busy");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-store-busy-0001", "store-busy-disp", root);
        StoreFamilyRegistryRow family = SeedFamily(registry, "lineage-busy");
        registry.UpsertStoreMember(
            "ws-store-busy-0001", family.FamilyId, "view-busy", root, WorkspaceRootIdentity.Unknown);
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-busy");

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(
            registry, "store-busy-disp", liveRoot: null, acquireSidecarLease: _ => null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(Directory.Exists(millerDir));
        Assert.Null(registry.Get("ws-store-busy-0001"));
        Assert.Equal(StoreSidecarReclaim.LeaseBusyReason, result.SidecarReclaim.SkipReason);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        Assert.Contains(
            StoreSidecarReclaim.LeaseBusyReason,
            WorkspaceRender.Remove(result, json: false),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The worktree case that leaked: <c>git worktree remove</c> deletes the root, so the removal takes the
    /// gone-root prune branch. That branch deletes the registry row too, so it owes the same reclaim.
    /// </summary>
    [Fact]
    public void RemoveByPath_GoneRoot_StillReclaimsTheStoreSidecars()
    {
        string goneRoot = Path.Combine(_dir, "ws-store-gone");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-store-gone-0001", "store-gone-disp", goneRoot);
        StoreFamilyRegistryRow family = SeedFamily(registry, "lineage-gone");
        registry.UpsertStoreMember(
            "ws-store-gone-0001", family.FamilyId, "view-gone", goneRoot, WorkspaceRootIdentity.Unknown);
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-gone");

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveByPath(registry, goneRoot, liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.Equal(3, result.SidecarReclaim.FilesDeleted);
        Assert.All(paths, p => Assert.False(File.Exists(p)));
    }

    [Fact]
    public void RemoveById_NonStoreWorkspace_ReportsNoReclaim()
    {
        var (root, _) = MakeWorkspace("ws-standalone");
        using WorkspaceRegistry registry = OpenRegistry();
        Register(registry, "ws-standalone-00001", "standalone-disp", root);

        WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(registry, "standalone-disp", liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, result.Result);
        Assert.False(result.SidecarReclaim.HasReport);
        Assert.DoesNotContain(
            "sidecar", WorkspaceRender.Remove(result, json: false), StringComparison.Ordinal);
    }

    /// <summary>
    /// A stand-in for the removal's injected indexer lease that runs an observation at RELEASE time. The lease
    /// bundle disposes indexer-last, so this fires after the guarded delete and before the best-effort dir
    /// delete — the one instant at which what the guarded delete spared is observable.
    /// </summary>
    private sealed class ReleaseHook : IDisposable
    {
        private readonly Action _onRelease;

        public ReleaseHook(Action onRelease) => _onRelease = onRelease;

        public void Dispose() => _onRelease();
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
