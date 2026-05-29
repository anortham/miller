using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the M6 <see cref="EditWriteLock"/> (impl-order step 9): a cross-process exclusive lease over
/// <c>&lt;.miller&gt;/edit.lock</c> that serializes concurrent <c>edit</c> applies WITHOUT colliding with the
/// indexer's separate <c>indexer.lock</c> (so an edit from any instance never deadlocks against the running
/// indexer leader). Mirrors <see cref="Miller.Indexing.SingleWriterLock"/>'s FileShare.None technique on a
/// distinct file. Fast suite.
/// </summary>
public sealed class EditWriteLockTests : IDisposable
{
    private readonly string _dir;

    public EditWriteLockTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-editlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TryAcquire_FirstCaller_Succeeds()
    {
        using var lease = EditWriteLock.TryAcquire(_dir);
        Assert.NotNull(lease);
    }

    [Fact]
    public void TryAcquire_WhileHeld_ReturnsNull_ThenSucceedsAfterRelease()
    {
        var first = EditWriteLock.TryAcquire(_dir);
        Assert.NotNull(first);

        // A second acquisition while the first lease is held is refused.
        Assert.Null(EditWriteLock.TryAcquire(_dir));

        first!.Dispose();
        // After release the lock is available again.
        using var second = EditWriteLock.TryAcquire(_dir);
        Assert.NotNull(second);
    }

    [Fact]
    public void TryAcquire_UsesADistinctFile_FromTheIndexerLock()
    {
        // Holding the edit lock must NOT prevent acquiring the indexer lock (and vice versa) — they are
        // different resources so an edit never deadlocks against the running indexer.
        using var editLease = EditWriteLock.TryAcquire(_dir);
        Assert.NotNull(editLease);
        using var indexerLease = Miller.Indexing.SingleWriterLock.TryAcquire(_dir);
        Assert.NotNull(indexerLease);
    }

    [Fact]
    public void TryAcquire_CreatesTheDirectoryIfMissing()
    {
        string nested = Path.Combine(_dir, "does", "not", "exist");
        using var lease = EditWriteLock.TryAcquire(nested);
        Assert.NotNull(lease);
        Assert.True(File.Exists(Path.Combine(nested, "edit.lock")));
    }
}
