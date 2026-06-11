namespace Miller.Indexing;

/// <summary>
/// The cross-process leader-election primitive (decision-1 / -9). Each <c>miller</c> instance tries to
/// <see cref="TryAcquire"/> an OS-level exclusive lock on <c>&lt;.miller&gt;/indexer.lock</c>. The winner is the
/// <em>leader</em>: it runs the file watcher and shells <c>extract</c>; every other instance is refused
/// (<see cref="TryAcquire"/> returns null) and stays a pure reader. The lock is an open <see cref="FileStream"/>
/// with <see cref="FileShare.None"/> — the OS denies any other handle (this process or another) while it is held,
/// and releases it on dispose / process exit. This is Miller's election; julie's own
/// <c>&lt;db&gt;.julie-extract.lock</c> 30s flock remains the lower-level cross-process backstop even if two
/// writers ever race.
///
/// <para>The returned <see cref="SingleWriterLock"/> IS the lease: hold it for the lifetime of leadership and
/// dispose it to step down (enabling failover — another instance can then acquire it).</para>
/// </summary>
public sealed class SingleWriterLock : IDisposable
{
    /// <summary>The lock file name created inside the supplied <c>.miller</c> directory.</summary>
    public const string LockFileName = "indexer.lock";

    private FileStream? _stream;

    /// <summary>The absolute path of the lock file this lease holds.</summary>
    public string LockFilePath { get; }

    private SingleWriterLock(FileStream stream, string lockFilePath)
    {
        _stream = stream;
        LockFilePath = lockFilePath;
    }

    /// <summary>
    /// Attempt to acquire leadership by taking an exclusive lock on <c>&lt;millerDir&gt;/indexer.lock</c>. Creates
    /// <paramref name="millerDir"/> if it does not exist (first run). Returns a held <see cref="SingleWriterLock"/>
    /// on success, or <c>null</c> if another holder already has it (this instance stays a reader).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="millerDir"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="millerDir"/> is empty/whitespace.</exception>
    public static SingleWriterLock? TryAcquire(string millerDir)
    {
        ArgumentNullException.ThrowIfNull(millerDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);

        Directory.CreateDirectory(millerDir); // first-run: the .miller dir may not exist yet
        string lockFilePath = Path.Combine(Path.GetFullPath(millerDir), LockFileName);

        FileStream stream;
        try
        {
            // FileShare.None => the OS grants this handle exclusively; any other open (here or in another
            // process) fails until this one is closed. OpenOrCreate so the file need not pre-exist.
            stream = new FileStream(
                lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Already held by another instance — this one stays a reader. (A genuine path/permission error
            // would surface as UnauthorizedAccessException, which we deliberately do NOT swallow.)
            return null;
        }

        return new SingleWriterLock(stream, lockFilePath);
    }

    /// <summary>
    /// Delete everything inside <paramref name="millerDir"/> EXCEPT the lock file. Call ONLY while HOLDING the
    /// writer lock: the destructive part of a workspace remove must happen under mutual exclusion so a writer
    /// that acquires the lock later finds an already-empty index (clean rebuild), never a half-deleted live one.
    /// The held lock file is skipped because an open <see cref="FileShare.None"/> handle cannot be deleted on
    /// Windows; the caller releases the lease and then calls <see cref="TryDeleteEmptiedDir"/>.
    /// </summary>
    public static void DeleteContentsExceptLock(string millerDir)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(millerDir))
        {
            if (string.Equals(Path.GetFileName(entry), LockFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
    }

    /// <summary>
    /// Best-effort removal of a <c>.miller</c> dir already gutted by <see cref="DeleteContentsExceptLock"/>,
    /// after the lease is released. If another writer acquires the lock in the window after release, the dir is
    /// left to it (it rebuilds a fresh index in place) — the index data itself was deleted under the lock, so
    /// nothing live is ever lost here.
    /// </summary>
    public static void TryDeleteEmptiedDir(string millerDir)
    {
        try
        {
            Directory.Delete(millerDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Release leadership: close the exclusive handle so another instance can acquire it. Idempotent.</summary>
    public void Dispose()
    {
        var stream = _stream;
        if (stream is null)
            return;
        _stream = null;
        stream.Dispose();
    }
}
