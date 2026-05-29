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
}
