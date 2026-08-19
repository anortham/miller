using Miller.Indexing;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

public sealed class WorkspaceWriteLeasesCtLockTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void SidecarLockFileNames_IncludesCtWriteLockFileName()
    {
        Assert.Equal("ct.lock", CtWriteLock.LockFileName);
        Assert.Equal(CtWriteLock.LockFileName, WorkspaceWriteLeases.ContinuousTestLockFileName);
        Assert.Contains(CtWriteLock.LockFileName, WorkspaceWriteLeases.SidecarLockFileNames);
    }

    [Fact]
    public void TryAcquireForRemove_AcquiresCtLockAfterHistory()
    {
        using var dir = new TempMillerDir();

        using WorkspaceWriteLeases? leases =
            WorkspaceWriteLeases.TryAcquireForRemove(dir.Path, SingleWriterLock.TryAcquire, ShortTimeout);

        Assert.NotNull(leases);
        Assert.Throws<TimeoutException>(() =>
            CtWriteLock.AcquireFor(Path.Combine(dir.Path, CtSchema.DbFileName), ShortTimeout));
    }

    [Fact]
    public void TryAcquireForRemove_WhenCtLockHeld_Refuses_AndReleasesPriorLocks()
    {
        using var dir = new TempMillerDir();
        using CtWriteLock held = CtWriteLock.AcquireFor(Path.Combine(dir.Path, CtSchema.DbFileName), ShortTimeout);

        WorkspaceWriteLeases? leases =
            WorkspaceWriteLeases.TryAcquireForRemove(dir.Path, SingleWriterLock.TryAcquire, ShortTimeout);

        Assert.Null(leases);
        using SingleWriterLock? indexer = SingleWriterLock.TryAcquire(dir.Path);
        Assert.NotNull(indexer);
        using ContentCorpusWriteLock content =
            ContentCorpusWriteLock.AcquireFor(Path.Combine(dir.Path, "content.db"), ShortTimeout);
        Assert.NotNull(content);
        using MetricHistoryWriteLock history =
            MetricHistoryWriteLock.AcquireFor(Path.Combine(dir.Path, "history.db"), ShortTimeout);
        Assert.NotNull(history);
    }

    private sealed class TempMillerDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "miller-ct-wleases-" + Guid.NewGuid().ToString("N"));

        public TempMillerDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
