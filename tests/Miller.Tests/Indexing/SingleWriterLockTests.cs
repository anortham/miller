using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the leader-election primitive (decision-1 / -9). <see cref="SingleWriterLock.TryAcquire"/> takes an
/// OS-level exclusive lock on <c>&lt;.miller&gt;/indexer.lock</c> via a <c>FileShare.None</c> handle: the first
/// caller wins leadership; a second attempt while the first is held is refused (returns null); releasing the
/// first (dispose) makes it re-acquirable. The genuinely cross-PROCESS variant is the Scale suite; here a second
/// in-process handle stands in for "another instance" — it exercises the same <c>FileShare.None</c> exclusion
/// the OS enforces between processes.
/// </summary>
public sealed class SingleWriterLockTests
{
    private sealed class TempMillerDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "miller-lock-" + Guid.NewGuid().ToString("N"));

        public TempMillerDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void TryAcquire_OnAFreeLock_ReturnsLeadership()
    {
        using var dir = new TempMillerDir();
        using var lease = SingleWriterLock.TryAcquire(dir.Path);

        Assert.NotNull(lease);
        Assert.True(File.Exists(Path.Combine(dir.Path, "indexer.lock")));
    }

    [Fact]
    public void TryAcquire_WhileHeld_ByAnotherHandle_IsRefused()
    {
        using var dir = new TempMillerDir();
        using var first = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(first);

        // A second acquirer (standing in for another miller instance) must be refused while the first holds it.
        using var second = SingleWriterLock.TryAcquire(dir.Path);
        Assert.Null(second);
    }

    [Fact]
    public void TryAcquire_AfterRelease_IsReacquirable()
    {
        using var dir = new TempMillerDir();

        var first = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(first);
        first!.Dispose(); // leader dies / steps down

        // Failover: another instance must now be able to take the lock.
        using var second = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(second);
    }

    [Fact]
    public void TryAcquire_CreatesTheMillerDirectory_IfMissing()
    {
        // The .miller dir may not exist yet on a first run; the lock must create it rather than fail.
        string parent = Path.Combine(Path.GetTempPath(), "miller-lock-parent-" + Guid.NewGuid().ToString("N"));
        string millerDir = Path.Combine(parent, ".miller");
        try
        {
            using var lease = SingleWriterLock.TryAcquire(millerDir);
            Assert.NotNull(lease);
            Assert.True(Directory.Exists(millerDir));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Dispose_IsIdempotent_DoesNotThrowOnDoubleDispose()
    {
        using var dir = new TempMillerDir();
        var lease = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(lease);

        lease!.Dispose();
        lease.Dispose(); // second dispose must be a no-op, never throw

        // And the lock is genuinely free after the (double) dispose.
        using var again = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(again);
    }

    [Fact]
    public void TryAcquire_ReacquireRefuseReacquire_FullCycle()
    {
        // The full leadership lifecycle in one test: acquire → (held: refuse) → release → re-acquire.
        using var dir = new TempMillerDir();

        var a = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(a);

        Assert.Null(SingleWriterLock.TryAcquire(dir.Path)); // refused while a holds

        a!.Dispose();

        using var b = SingleWriterLock.TryAcquire(dir.Path); // re-acquirable after release
        Assert.NotNull(b);
        Assert.Null(SingleWriterLock.TryAcquire(dir.Path)); // and now b holds it exclusively
    }

    [Fact]
    public void TryAcquire_NullOrBlankDirectory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SingleWriterLock.TryAcquire(null!));
        Assert.Throws<ArgumentException>(() => SingleWriterLock.TryAcquire("   "));
    }

    [Fact]
    public void IsLockContention_WindowsSharingViolationsOnly_AreBusy()
    {
        Assert.True(SingleWriterLock.IsLockContentionForTest(
            new IOExceptionWithHResult(unchecked((int)0x80070020)), isWindows: true)); // ERROR_SHARING_VIOLATION
        Assert.True(SingleWriterLock.IsLockContentionForTest(
            new IOExceptionWithHResult(unchecked((int)0x80070021)), isWindows: true)); // ERROR_LOCK_VIOLATION
        Assert.False(SingleWriterLock.IsLockContentionForTest(
            new IOExceptionWithHResult(unchecked((int)0x8007007B)), isWindows: true)); // ERROR_INVALID_NAME
    }

    [Fact]
    public void IsLockContention_NonWindows_OnlyLockUnavailableErrorsAreBusy()
    {
        Assert.True(SingleWriterLock.IsLockContentionForTest(
            new IOExceptionWithHResult(11), isWindows: false)); // Linux EAGAIN/EWOULDBLOCK
        Assert.True(SingleWriterLock.IsLockContentionForTest(
            new IOExceptionWithHResult(35), isWindows: false)); // macOS EAGAIN/EWOULDBLOCK
        Assert.False(SingleWriterLock.IsLockContentionForTest(
            new IOExceptionWithHResult(28), isWindows: false)); // ENOSPC
        Assert.False(SingleWriterLock.IsLockContentionForTest(
            new IOExceptionWithHResult(unchecked((int)0x80131620)), isWindows: false)); // generic IOException
    }

    [Fact]
    public void DeleteContentsExceptLock_UnderTheHeldLock_GutsTheIndexButKeepsExclusion()
    {
        // The remove flow's destructive step runs while HOLDING the lock: index files and subdirs are deleted,
        // the held lock file survives (Windows cannot delete an open FileShare.None file), and — the point of
        // the design — no other writer can acquire leadership mid-delete.
        using var dir = new TempMillerDir();
        File.WriteAllText(Path.Combine(dir.Path, "symbols.db"), "db");
        Directory.CreateDirectory(Path.Combine(dir.Path, "logs"));
        File.WriteAllText(Path.Combine(dir.Path, "logs", "miller.log"), "log");

        using var lease = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(lease);

        SingleWriterLock.DeleteContentsExceptLock(dir.Path);

        Assert.Equal(
            new[] { Path.Combine(dir.Path, SingleWriterLock.LockFileName) },
            Directory.GetFileSystemEntries(dir.Path));
        Assert.Null(SingleWriterLock.TryAcquire(dir.Path)); // exclusion held throughout the delete
    }

    [Fact]
    public void TryDeleteEmptiedDir_AfterRelease_RemovesTheLockFileAndDir()
    {
        using var dir = new TempMillerDir();
        File.WriteAllText(Path.Combine(dir.Path, "symbols.db"), "db");

        using (var lease = SingleWriterLock.TryAcquire(dir.Path))
        {
            Assert.NotNull(lease);
            SingleWriterLock.DeleteContentsExceptLock(dir.Path);
        }

        SingleWriterLock.TryDeleteEmptiedDir(dir.Path);
        Assert.False(Directory.Exists(dir.Path));
    }

    [Fact]
    public void TryDeleteEmptiedDir_WhenANewWriterSneaksIn_DoesNotThrow()
    {
        // The residual race: another writer acquires the lock between our release and the final dir delete.
        // The data was already deleted under OUR lock; the shell delete must never throw — on Windows the open
        // lock file blocks it (dir is left to the new writer), on POSIX the unlink succeeds.
        using var dir = new TempMillerDir();
        using var newWriter = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(newWriter);

        SingleWriterLock.TryDeleteEmptiedDir(dir.Path);
    }

    // ---- generalized skip-set: DeleteContentsExceptLock must keep every HELD lock, but still delete debris ----

    [Fact]
    public void DeleteContentsExceptLock_WithHeldSidecarLocks_KeepsThem_ButDeletesUnheldLockDebris()
    {
        // The remove flow holds indexer.lock + content.lock + history.lock across the delete; all three must
        // survive. But a stray, UNHELD *.lock file is index debris and MUST be deleted — the skip-set is
        // explicit, not a blanket "*.lock" skip that would hide a leaked lock.
        using var dir = new TempMillerDir();
        File.WriteAllText(Path.Combine(dir.Path, "symbols.db"), "db");
        File.WriteAllText(Path.Combine(dir.Path, "content.db"), "content");
        File.WriteAllText(Path.Combine(dir.Path, "history.db"), "history");
        File.WriteAllText(Path.Combine(dir.Path, "content.lock"), "");
        File.WriteAllText(Path.Combine(dir.Path, "history.lock"), "");
        File.WriteAllText(Path.Combine(dir.Path, "stale.lock"), ""); // debris — unheld, must be deleted

        using var lease = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(lease);

        SingleWriterLock.DeleteContentsExceptLock(dir.Path, WorkspaceWriteLeases.SidecarLockFileNames);

        string[] survivors = Directory.GetFileSystemEntries(dir.Path)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray()!;
        Assert.Equal(new[] { "content.lock", "history.lock", "indexer.lock" }, survivors);
    }

    [Fact]
    public void DeleteContentsExceptLock_DefaultSkipSet_StillKeepsOnlyIndexerLock()
    {
        // Back-compat: the parameterless overload behaves exactly as before — only indexer.lock survives.
        using var dir = new TempMillerDir();
        File.WriteAllText(Path.Combine(dir.Path, "symbols.db"), "db");
        File.WriteAllText(Path.Combine(dir.Path, "content.lock"), ""); // NOT held here ⇒ debris ⇒ deleted

        using var lease = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(lease);

        SingleWriterLock.DeleteContentsExceptLock(dir.Path);

        Assert.Equal(
            new[] { Path.Combine(dir.Path, SingleWriterLock.LockFileName) },
            Directory.GetFileSystemEntries(dir.Path));
    }

    // ---- WorkspaceWriteLeases: fixed-order acquisition of all three remove leases ----

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void WorkspaceWriteLeases_OnAllFreeLocks_AcquiresAllThree()
    {
        using var dir = new TempMillerDir();

        using WorkspaceWriteLeases? leases =
            WorkspaceWriteLeases.TryAcquireForRemove(dir.Path, SingleWriterLock.TryAcquire, ShortTimeout);

        Assert.NotNull(leases);
        // While held, every underlying lock is exclusive: a second acquire of any of the three is refused.
        Assert.Null(SingleWriterLock.TryAcquire(dir.Path));
        Assert.Throws<TimeoutException>(() =>
            ContentCorpusWriteLock.AcquireFor(Path.Combine(dir.Path, "content.db"), ShortTimeout));
        Assert.Throws<TimeoutException>(() =>
            MetricHistoryWriteLock.AcquireFor(Path.Combine(dir.Path, "history.db"), ShortTimeout));
    }

    [Fact]
    public void WorkspaceWriteLeases_WhenIndexerLockHeld_Refuses()
    {
        using var dir = new TempMillerDir();
        using SingleWriterLock? held = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(held);

        WorkspaceWriteLeases? leases =
            WorkspaceWriteLeases.TryAcquireForRemove(dir.Path, SingleWriterLock.TryAcquire, ShortTimeout);

        Assert.Null(leases); // indexer unavailable ⇒ whole bundle refused
    }

    [Fact]
    public void WorkspaceWriteLeases_WhenContentLockHeld_Refuses_AndReleasesTheIndexerLock()
    {
        // Regression for the pre-existing defect: an in-flight content import holds content.lock WITHOUT the
        // indexer lock. Remove must refuse (delete nothing) — and must not strand the indexer lease it briefly took.
        using var dir = new TempMillerDir();
        using ContentCorpusWriteLock heldContent =
            ContentCorpusWriteLock.AcquireFor(Path.Combine(dir.Path, "content.db"), ShortTimeout);

        WorkspaceWriteLeases? leases =
            WorkspaceWriteLeases.TryAcquireForRemove(dir.Path, SingleWriterLock.TryAcquire, ShortTimeout);

        Assert.Null(leases);
        // The indexer lock the bundle grabbed first must have been released on the refusal.
        using SingleWriterLock? afterIndexer = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(afterIndexer);
    }

    [Fact]
    public void WorkspaceWriteLeases_WhenHistoryLockHeld_Refuses_AndReleasesIndexerAndContent()
    {
        using var dir = new TempMillerDir();
        using MetricHistoryWriteLock heldHistory =
            MetricHistoryWriteLock.AcquireFor(Path.Combine(dir.Path, "history.db"), ShortTimeout);

        WorkspaceWriteLeases? leases =
            WorkspaceWriteLeases.TryAcquireForRemove(dir.Path, SingleWriterLock.TryAcquire, ShortTimeout);

        Assert.Null(leases);
        // Both leases taken before history must have been released on the refusal.
        using SingleWriterLock? afterIndexer = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(afterIndexer);
        using ContentCorpusWriteLock afterContent =
            ContentCorpusWriteLock.AcquireFor(Path.Combine(dir.Path, "content.db"), ShortTimeout);
        Assert.NotNull(afterContent);
    }

    [Fact]
    public void WorkspaceWriteLeases_Dispose_ReleasesAllThree_MakingThemReacquirable()
    {
        using var dir = new TempMillerDir();

        WorkspaceWriteLeases? leases =
            WorkspaceWriteLeases.TryAcquireForRemove(dir.Path, SingleWriterLock.TryAcquire, ShortTimeout);
        Assert.NotNull(leases);
        leases!.Dispose();

        using SingleWriterLock? indexer = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(indexer);
        using ContentCorpusWriteLock content =
            ContentCorpusWriteLock.AcquireFor(Path.Combine(dir.Path, "content.db"), ShortTimeout);
        Assert.NotNull(content);
        using MetricHistoryWriteLock history =
            MetricHistoryWriteLock.AcquireFor(Path.Combine(dir.Path, "history.db"), ShortTimeout);
        Assert.NotNull(history);
    }

    private sealed class IOExceptionWithHResult : IOException
    {
        public IOExceptionWithHResult(int hresult) => HResult = hresult;
    }
}
