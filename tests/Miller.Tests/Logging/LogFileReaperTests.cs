using Miller.Server.Logging;
using Xunit;

namespace Miller.Tests.Logging;

/// <summary>
/// Pins <see cref="LogFileReaper.Plan"/> (m8-design §D6): the pure planner for the startup sweep of stale per-pid
/// log files. Groups files by owning pid; NEVER deletes the current pid; keeps the newest <c>keep</c> pids by
/// most-recent last-write; returns the rest's paths in a deterministic order. The actual delete is thin infra
/// the caller performs.
/// </summary>
public sealed class LogFileReaperTests
{
    private const int CurrentPid = 1000;

    private static LogFileInfo File(string path, int pid, int daysAgo) =>
        new(path, pid, new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo));

    [Fact]
    public void CurrentPid_IsNeverDeleted_EvenWhenOldest()
    {
        var files = new[]
        {
            File("/logs/miller-1000-20260520.log", CurrentPid, daysAgo: 10), // oldest, but current
            File("/logs/miller-2000-20260530.log", 2000, daysAgo: 0),
            File("/logs/miller-3000-20260529.log", 3000, daysAgo: 1),
        };

        // keep=1 -> retain the single newest NON-current pid (2000); delete pid 3000; never the current pid.
        var toDelete = LogFileReaper.Plan(files, keep: 1, CurrentPid);

        Assert.Equal(new[] { "/logs/miller-3000-20260529.log" }, toDelete);
    }

    [Fact]
    public void KeepsNewestNPids_DeletesOlder()
    {
        var files = new[]
        {
            File("/logs/miller-2000-x.log", 2000, daysAgo: 0), // newest
            File("/logs/miller-3000-x.log", 3000, daysAgo: 1),
            File("/logs/miller-4000-x.log", 4000, daysAgo: 2),
            File("/logs/miller-5000-x.log", 5000, daysAgo: 3), // oldest
        };

        // keep=2 -> retain pids 2000 + 3000; delete 4000 + 5000, oldest pid first.
        var toDelete = LogFileReaper.Plan(files, keep: 2, CurrentPid);

        Assert.Equal(new[] { "/logs/miller-5000-x.log", "/logs/miller-4000-x.log" }, toDelete);
    }

    [Fact]
    public void MultipleFilesPerPid_AreKeptOrDeletedAsAGroup()
    {
        var files = new[]
        {
            // pid 2000: two rolled files, newest write 0 days ago -> kept as a group.
            File("/logs/miller-2000-20260530.log", 2000, daysAgo: 0),
            File("/logs/miller-2000-20260529.log", 2000, daysAgo: 1),
            // pid 3000: two rolled files, all older -> deleted as a group.
            File("/logs/miller-3000-20260525.log", 3000, daysAgo: 5),
            File("/logs/miller-3000-20260524.log", 3000, daysAgo: 6),
        };

        var toDelete = LogFileReaper.Plan(files, keep: 1, CurrentPid);

        // Both of pid 3000's files, path-ordered; none of pid 2000's.
        Assert.Equal(
            new[] { "/logs/miller-3000-20260524.log", "/logs/miller-3000-20260525.log" },
            toDelete);
    }

    [Fact]
    public void PidRecency_UsesItsMostRecentFile_NotItsOldest()
    {
        var files = new[]
        {
            // pid 2000 has an OLD file but also a very recent one -> its recency is the recent one (kept).
            File("/logs/miller-2000-old.log", 2000, daysAgo: 9),
            File("/logs/miller-2000-new.log", 2000, daysAgo: 0),
            // pid 3000's newest is older than pid 2000's newest -> deleted.
            File("/logs/miller-3000.log", 3000, daysAgo: 2),
        };

        var toDelete = LogFileReaper.Plan(files, keep: 1, CurrentPid);

        Assert.Equal(new[] { "/logs/miller-3000.log" }, toDelete);
    }

    [Fact]
    public void KeepAtOrAbovePidCount_DeletesNothing()
    {
        var files = new[]
        {
            File("/logs/miller-2000.log", 2000, daysAgo: 0),
            File("/logs/miller-3000.log", 3000, daysAgo: 1),
        };

        Assert.Empty(LogFileReaper.Plan(files, keep: 2, CurrentPid));
        Assert.Empty(LogFileReaper.Plan(files, keep: 5, CurrentPid));
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(LogFileReaper.Plan(Array.Empty<LogFileInfo>(), keep: 3, CurrentPid));
    }

    [Fact]
    public void OnlyCurrentPidFiles_ReturnsEmpty()
    {
        var files = new[]
        {
            File("/logs/miller-1000-a.log", CurrentPid, daysAgo: 5),
            File("/logs/miller-1000-b.log", CurrentPid, daysAgo: 6),
        };

        Assert.Empty(LogFileReaper.Plan(files, keep: 0, CurrentPid));
    }

    [Fact]
    public void KeepZero_DeletesAllNonCurrentPids()
    {
        var files = new[]
        {
            File("/logs/miller-2000.log", 2000, daysAgo: 0),
            File("/logs/miller-3000.log", 3000, daysAgo: 1),
            File("/logs/miller-1000.log", CurrentPid, daysAgo: 2),
        };

        var toDelete = LogFileReaper.Plan(files, keep: 0, CurrentPid);

        // Both non-current pids, oldest first; current pid untouched.
        Assert.Equal(new[] { "/logs/miller-3000.log", "/logs/miller-2000.log" }, toDelete);
    }

    [Fact]
    public void NegativeKeep_IsTreatedAsZero()
    {
        var files = new[]
        {
            File("/logs/miller-2000.log", 2000, daysAgo: 0),
            File("/logs/miller-3000.log", 3000, daysAgo: 1),
        };

        var toDelete = LogFileReaper.Plan(files, keep: -5, CurrentPid);

        Assert.Equal(new[] { "/logs/miller-3000.log", "/logs/miller-2000.log" }, toDelete);
    }

    [Fact]
    public void NullFiles_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LogFileReaper.Plan(null!, keep: 1, CurrentPid));
    }

    // ---- finding-1/-8: liveness — a still-running PEER's files are never planned for deletion ----
    // The recency-only policy (current-pid + newest-keep) is NOT sufficient in M3's multi-process reality: an
    // idle reader writes almost nothing after its banner, so its mtime freezes near process-start and it can
    // fall out of the newest-keep window while STILL RUNNING. On macOS/Linux File.Delete unlinks an open file
    // without error, silently destroying a live peer's logs. Plan must exclude live (non-current) pids too.

    [Fact]
    public void LivePid_OlderThanKeepWindow_IsNeverDeleted()
    {
        var files = new[]
        {
            File("/logs/miller-2000.log", 2000, daysAgo: 0), // newest, dead
            File("/logs/miller-3000.log", 3000, daysAgo: 1), // dead
            // pid 9000 is the OLDEST writer (its idle file froze 10 days ago) but it is STILL RUNNING.
            File("/logs/miller-9000.log", 9000, daysAgo: 10),
        };
        var live = new HashSet<int> { 9000 };

        // keep=1 -> by recency alone only pid 2000 survives and 3000+9000 would be deleted; but 9000 is live, so
        // it must NEVER be planned for deletion. Only the dead, out-of-window pid 3000 is reaped.
        var toDelete = LogFileReaper.Plan(files, keep: 1, CurrentPid, live);

        Assert.Equal(new[] { "/logs/miller-3000.log" }, toDelete);
    }

    [Fact]
    public void LivePid_DoesNotConsumeAKeepSlot_SoAnExtraDeadPidIsStillReaped()
    {
        var files = new[]
        {
            File("/logs/miller-2000.log", 2000, daysAgo: 0), // newest, dead
            File("/logs/miller-3000.log", 3000, daysAgo: 1), // dead
            File("/logs/miller-9000.log", 9000, daysAgo: 2), // live (excluded before the keep cull)
        };
        var live = new HashSet<int> { 9000 };

        // keep=1: the live pid 9000 is removed up front (kept, but not counted against the keep window), so among
        // the remaining DEAD pids {2000, 3000} the newest one (2000) is kept and 3000 is reaped.
        var toDelete = LogFileReaper.Plan(files, keep: 1, CurrentPid, live);

        Assert.Equal(new[] { "/logs/miller-3000.log" }, toDelete);
    }

    [Fact]
    public void EmptyLiveSet_BehavesLikeTheRecencyOnlyPlan()
    {
        var files = new[]
        {
            File("/logs/miller-2000.log", 2000, daysAgo: 0),
            File("/logs/miller-3000.log", 3000, daysAgo: 1),
            File("/logs/miller-4000.log", 4000, daysAgo: 2),
        };
        var live = new HashSet<int>(); // nothing alive => pure recency

        var toDelete = LogFileReaper.Plan(files, keep: 1, CurrentPid, live);

        // keep=1: pid 2000 kept; 3000 + 4000 reaped, oldest pid first.
        Assert.Equal(new[] { "/logs/miller-4000.log", "/logs/miller-3000.log" }, toDelete);
    }

    [Fact]
    public void NullLivePids_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => LogFileReaper.Plan(Array.Empty<LogFileInfo>(), keep: 1, CurrentPid, livePids: null!));
    }
}
