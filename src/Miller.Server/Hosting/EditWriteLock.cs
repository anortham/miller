namespace Miller.Server.Hosting;

/// <summary>
/// The M6 apply serialization lease (m6-design decision-4): a cross-process exclusive lock over
/// <c>&lt;.miller&gt;/edit.lock</c> that ensures only one <c>edit</c> apply mutates the source tree at a time.
/// It mirrors <see cref="Miller.Indexing.SingleWriterLock"/>'s technique (an open <see cref="FileStream"/> with
/// <see cref="FileShare.None"/>, released on dispose / process exit) but on a SEPARATE file, deliberately: the
/// indexer leader holds <c>indexer.lock</c> for the life of the process, so re-using that lock here would mean
/// an edit from the leader's own process could never re-acquire it (it is held exclusively) — and an edit from a
/// non-leader could never take it while the leader runs. A distinct <c>edit.lock</c> serializes edits across
/// processes without ever deadlocking against the running indexer; write-through convergence (which DOES depend
/// on leadership) is handled separately by <see cref="IEditWriteThrough"/>.
/// </summary>
public sealed class EditWriteLock : IDisposable
{
    /// <summary>The lock file name created inside the <c>.miller</c> directory.</summary>
    public const string LockFileName = "edit.lock";

    private FileStream? _stream;

    private EditWriteLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Attempt to acquire the edit lock under <paramref name="millerDir"/> (created if absent). Returns a held
    /// lease on success, or <c>null</c> if another apply currently holds it (the caller retries / refuses).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="millerDir"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="millerDir"/> is empty/whitespace.</exception>
    public static EditWriteLock? TryAcquire(string millerDir)
    {
        ArgumentNullException.ThrowIfNull(millerDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);

        Directory.CreateDirectory(millerDir);
        string lockFilePath = Path.Combine(Path.GetFullPath(millerDir), LockFileName);

        try
        {
            var stream = new FileStream(
                lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new EditWriteLock(stream);
        }
        catch (IOException)
        {
            // Already held by another apply (this process or another) — refuse; a genuine path/permission error
            // surfaces as UnauthorizedAccessException, which is deliberately NOT swallowed.
            return null;
        }
    }

    /// <summary>Release the lock so another apply can acquire it. Idempotent.</summary>
    public void Dispose()
    {
        var stream = _stream;
        if (stream is null)
            return;
        _stream = null;
        stream.Dispose();
    }
}
