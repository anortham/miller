using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class CtBuildCacheJanitorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-janitor-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Workspace_ttl_removes_only_inactive_cache_entries()
    {
        string buildRoot = BuildRoot("aaaaaaaaaaaa", "bbbbbbbbbbbb");
        string oldCache = Cache(buildRoot, "old", 3, Now.AddDays(-8));
        string newCache = Cache(buildRoot, "new", 3, Now.AddDays(-6));
        MarkRoot(buildRoot);

        CtCacheMaintenanceResult result = Janitor(workspaceBudgetBytes: 1024).EnforceWorkspace(buildRoot);

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(3, result.DeletedBytes);
        Assert.False(Directory.Exists(oldCache));
        Assert.True(Directory.Exists(newCache));
    }

    [Fact]
    public void Workspace_cap_removes_oldest_unused_cache_entries_until_protected_content_fits()
    {
        string buildRoot = BuildRoot("aaaaaaaaaaaa", "cccccccccccc");
        Directory.CreateDirectory(Path.Combine(buildRoot, "g111111111111"));
        WriteBytes(Path.Combine(buildRoot, "g111111111111", "out.bin"), 4);
        string oldest = Cache(buildRoot, "oldest", 3, Now.AddDays(-1));
        string newest = Cache(buildRoot, "newest", 2, Now.AddHours(-1));
        MarkRoot(buildRoot);

        CtCacheMaintenanceResult result = Janitor(workspaceBudgetBytes: 4).EnforceWorkspace(buildRoot);

        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(5, result.DeletedBytes);
        Assert.False(Directory.Exists(oldest));
        Assert.False(Directory.Exists(newest));
        Assert.True(Directory.Exists(Path.Combine(buildRoot, "g111111111111")));
        Assert.False(result.ProtectedOverBudget);
    }

    [Fact]
    public void Machine_cap_prunes_oldest_cache_entries_across_complete_roots()
    {
        string first = BuildRoot("aaaaaaaaaaaa", "dddddddddddd");
        string second = BuildRoot("aaaaaaaaaaaa", "eeeeeeeeeeee");
        string firstCache = Cache(first, "tool", 3, Now.AddDays(-8));
        string secondCache = Cache(second, "tool", 2, Now.AddDays(-7));
        MarkRoot(first);
        MarkRoot(second);

        CtCacheMaintenanceResult result = Janitor(workspaceBudgetBytes: 1024, machineBudgetBytes: 4)
            .EnforceMachine();

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(3, result.DeletedBytes);
        Assert.False(Directory.Exists(firstCache));
        Assert.True(Directory.Exists(secondCache));
    }

    [Fact]
    public void Machine_janitor_skips_a_root_with_a_live_operation_lock()
    {
        string buildRoot = BuildRoot("aaaaaaaaaaaa", "ffffffffffff");
        string cache = Cache(buildRoot, "tool", 6, Now.AddDays(-8));
        MarkRoot(buildRoot);
        using CtBuildRootOperationLease lease = CtBuildRootOperationLease.Acquire(
            buildRoot,
            TestContext.Current.CancellationToken);

        CtCacheMaintenanceResult result = Janitor(workspaceBudgetBytes: 1024, machineBudgetBytes: 1)
            .EnforceMachine();

        Assert.Equal(1, result.LockedRootCount);
        Assert.True(Directory.Exists(cache));
    }

    [Fact]
    public void Machine_janitor_skips_when_the_machine_lock_is_contended()
    {
        string buildRoot = BuildRoot("aaaaaaaaaaaa", "111111111111");
        string cache = Cache(buildRoot, "tool", 6, Now.AddDays(-8));
        MarkRoot(buildRoot);
        using CtMachineBuildJanitorLease lease = CtMachineBuildJanitorLease.Acquire(_root);

        CtCacheMaintenanceResult result = Janitor(workspaceBudgetBytes: 1024, machineBudgetBytes: 1)
            .EnforceMachine();

        Assert.True(result.MachineLockContended);
        Assert.True(Directory.Exists(cache));
    }

    [Fact]
    public void Machine_janitor_holds_the_root_lock_through_the_reap_call()
    {
        string buildRoot = BuildRoot("aaaaaaaaaaaa", "999999999999");
        Cache(buildRoot, "tool", 6, Now.AddDays(-8));
        MarkRoot(buildRoot);
        CtOperationLockState lockStateAtReap = CtOperationLockState.Unknown;
        var janitor = new CtBuildCacheJanitor(
            Path.Combine(_root, "build"),
            workspaceBudgetBytes: 1024,
            machineBudgetBytes: 1,
            inactivity: TimeSpan.FromDays(7),
            utcNow: static () => Now,
            reap: path =>
            {
                lockStateAtReap = CtBuildRootOperationLease.Probe(buildRoot);
                return CtGenerationPaths.TryReapDetailed(path);
            });

        janitor.EnforceMachine();

        Assert.Equal(CtOperationLockState.Held, lockStateAtReap);
        Assert.Equal(CtOperationLockState.Available, CtBuildRootOperationLease.Probe(buildRoot));
    }

    [Fact]
    public void Machine_janitor_skips_recent_and_ambiguous_roots()
    {
        string recent = BuildRoot("aaaaaaaaaaaa", "222222222222");
        string recentCache = Cache(recent, "tool", 6, Now.AddHours(-1));
        MarkRoot(recent);
        string ambiguous = BuildRoot("aaaaaaaaaaaa", "888888888888");
        string ambiguousCache = Cache(ambiguous, "tool", 6, Now.AddDays(-8));

        CtCacheMaintenanceResult result = Janitor(workspaceBudgetBytes: 1024, machineBudgetBytes: 1)
            .EnforceMachine();

        Assert.True(result.SkippedRecentRootCount > 0);
        Assert.True(result.SkippedAmbiguousRootCount > 0);
        Assert.True(Directory.Exists(recentCache));
        Assert.True(Directory.Exists(ambiguousCache));
    }

    [Fact]
    public void Machine_janitor_ignores_non_miller_paths_and_symlinked_cache_entries()
    {
        string foreign = Path.Combine(_root, "build", "foreign", "project");
        string foreignCache = Cache(foreign, "tool", 6, Now.AddDays(-8));
        string canonical = BuildRoot("aaaaaaaaaaaa", "333333333333");
        string target = Cache(canonical, "target", 6, Now.AddDays(-8));
        string link = Path.Combine(canonical, "cache", "link");
        MarkRoot(canonical);
        if (!OperatingSystem.IsWindows())
            Directory.CreateSymbolicLink(link, target);

        CtCacheMaintenanceResult result = Janitor(workspaceBudgetBytes: 1024, machineBudgetBytes: 1)
            .EnforceMachine();

        Assert.True(Directory.Exists(foreignCache));
        if (!OperatingSystem.IsWindows())
            Assert.True(Directory.Exists(target));
        Assert.True(result.SkippedAmbiguousRootCount > 0);
    }

    [Fact]
    public void Cache_janitor_never_removes_generation_directories()
    {
        string buildRoot = BuildRoot("aaaaaaaaaaaa", "444444444444");
        string active = Path.Combine(buildRoot, "g444444444444");
        string newest = Path.Combine(buildRoot, "g555555555555");
        Directory.CreateDirectory(active);
        Directory.CreateDirectory(newest);
        WriteBytes(Path.Combine(active, "active.bin"), 5);
        WriteBytes(Path.Combine(newest, "newest.bin"), 5);
        Cache(buildRoot, "tool", 1, Now.AddDays(-8));
        MarkRoot(buildRoot);

        Janitor(workspaceBudgetBytes: 1, machineBudgetBytes: 1).EnforceMachine();

        Assert.True(Directory.Exists(active));
        Assert.True(Directory.Exists(newest));
    }

    [Fact]
    public void Failed_reap_is_debt_and_does_not_throw()
    {
        string buildRoot = BuildRoot("aaaaaaaaaaaa", "666666666666");
        string cache = Cache(buildRoot, "tool", 3, Now.AddDays(-8));
        MarkRoot(buildRoot);
        var reported = new List<string>();
        var janitor = new CtBuildCacheJanitor(
            Path.Combine(_root, "build"),
            workspaceBudgetBytes: 1,
            machineBudgetBytes: 1,
            inactivity: TimeSpan.FromDays(7),
            utcNow: static () => Now,
            report: reported.Add,
            reap: static _ => CtReapOutcome.RenameFailed);

        CtCacheMaintenanceResult result = janitor.EnforceWorkspace(buildRoot);

        Assert.Equal(1, result.FailedCount);
        Assert.Single(result.Debts);
        Assert.True(Directory.Exists(cache));
        Assert.Contains(reported, message => message.Contains("cache_reap_failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_failure_debt_names_the_existing_reap_remnant()
    {
        string buildRoot = BuildRoot("aaaaaaaaaaaa", "aaaa11111111");
        string cache = Cache(buildRoot, "tool", 3, Now.AddDays(-8));
        MarkRoot(buildRoot);
        var janitor = new CtBuildCacheJanitor(
            Path.Combine(_root, "build"),
            workspaceBudgetBytes: 1,
            machineBudgetBytes: 1,
            inactivity: TimeSpan.FromDays(7),
            utcNow: static () => Now,
            reap: path => CtGenerationPaths.TryReapDetailed(
                path,
                static (source, destination) => Directory.Move(source, destination),
                static _ => throw new IOException("sharing violation")));

        CtCacheMaintenanceResult result = janitor.EnforceWorkspace(buildRoot);

        CtCacheReapDebt debt = Assert.Single(result.Debts);
        Assert.False(Directory.Exists(cache));
        Assert.True(Directory.Exists(debt.Path));
        Assert.Contains(".reap-", debt.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Protected_content_over_budget_is_reported_without_deletion()
    {
        string buildRoot = BuildRoot("aaaaaaaaaaaa", "777777777777");
        Directory.CreateDirectory(Path.Combine(buildRoot, "g777777777777"));
        WriteBytes(Path.Combine(buildRoot, "g777777777777", "out.bin"), 6);
        MarkRoot(buildRoot);

        CtCacheMaintenanceResult result = Janitor(workspaceBudgetBytes: 1).EnforceWorkspace(buildRoot);

        Assert.True(result.ProtectedOverBudget);
        Assert.Equal(6, result.ProtectedBytes);
        Assert.True(Directory.Exists(Path.Combine(buildRoot, "g777777777777")));
    }

    private CtBuildCacheJanitor Janitor(long workspaceBudgetBytes, long? machineBudgetBytes = null) =>
        new(
            Path.Combine(_root, "build"),
            workspaceBudgetBytes,
            machineBudgetBytes ?? 8,
            TimeSpan.FromDays(7),
            static () => Now);

    private string BuildRoot(string workspaceSegment, string projectSegment)
    {
        string path = Path.Combine(_root, "build", workspaceSegment, projectSegment);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Cache(string buildRoot, string name, int bytes, DateTimeOffset lastUsed)
    {
        string path = Path.Combine(buildRoot, "cache", name);
        Directory.CreateDirectory(path);
        WriteBytes(Path.Combine(path, "cache.bin"), bytes);
        Directory.SetLastWriteTimeUtc(path, lastUsed.UtcDateTime);
        return path;
    }

    private static void MarkRoot(string buildRoot)
    {
        File.WriteAllText(Path.Combine(buildRoot, CtBuildRootOperationLease.LockFileName), "marker");
    }

    private static void WriteBytes(string path, int count) =>
        File.WriteAllBytes(path, new byte[count]);
}
