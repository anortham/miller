namespace Miller.Indexing;

/// <summary>
/// The cross-process leader-election primitive (decision-1 / -9). Each <c>miller</c> instance tries to
/// <see cref="TryAcquire"/> an OS-level exclusive lock on <c>&lt;.miller&gt;/indexer.lock</c>. The winner is the
/// <em>leader</em>: it runs the file watcher and shells <c>extract</c>; every other instance is refused
/// (<see cref="TryAcquire"/> returns null) and stays a pure reader. The lock is an open <see cref="FileStream"/>
/// with <see cref="FileShare.None"/> — the OS denies any other handle (this process or another) while it is held,
/// and releases it on dispose / process exit. This lock is the ONLY serialization Miller has for a workspace:
/// julie-extract runs no lock of its own, so nothing below this catches two writers that race. It is also
/// strictly PER-WORKSPACE — N workspaces are N independent leaders with nothing machine-wide above them.
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
        catch (IOException ex) when (IsLockContention(ex, OperatingSystem.IsWindows()))
        {
            // Already held by another instance — this one stays a reader. On Windows, only the sharing/lock
            // violations produced by an existing holder are treated as contention; other IO failures surface.
            return null;
        }

        return new SingleWriterLock(stream, lockFilePath);
    }

    internal static bool IsLockContentionForTest(IOException ex, bool isWindows) =>
        IsLockContention(ex, isWindows);

    private static bool IsLockContention(IOException ex, bool isWindows)
    {
        int nativeError = ex.HResult & 0xFFFF;
        if (isWindows)
            return nativeError is 32 /* ERROR_SHARING_VIOLATION */ or 33 /* ERROR_LOCK_VIOLATION */;

        return nativeError is 11 /* Linux EAGAIN/EWOULDBLOCK */
            or 35 /* macOS EAGAIN/EWOULDBLOCK */;
    }

    /// <summary>
    /// Delete everything inside <paramref name="millerDir"/> EXCEPT the held lock files. Call ONLY while HOLDING
    /// the writer lock (and any workspace-local sidecar write leases named in
    /// <paramref name="additionalHeldLockFileNames"/>): the destructive part of a workspace remove must happen
    /// under mutual exclusion so a writer that acquires the lock later finds an already-empty index (clean
    /// rebuild), never a half-deleted live one. A held lock file is skipped because an open
    /// <see cref="FileShare.None"/> handle cannot be deleted on Windows; the caller releases the lease(s) and then
    /// calls <see cref="TryDeleteEmptiedDir"/>.
    ///
    /// <para>The indexer <see cref="LockFileName"/> is ALWAYS skipped (it is intrinsic to this lock's own remove
    /// contract). <paramref name="additionalHeldLockFileNames"/> is the EXPLICIT set of any other lock files the
    /// caller is holding across the delete (e.g. <c>content.lock</c>, <c>history.lock</c>). It is deliberately an
    /// explicit set, NOT a blanket <c>*.lock</c> skip — a stray, unheld <c>.lock</c> file is index debris that
    /// SHOULD be deleted here, and silently keeping it would hide a leaked-lock bug.</para>
    /// </summary>
    public static void DeleteContentsExceptLock(
        string millerDir, IReadOnlySet<string>? additionalHeldLockFileNames = null)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { LockFileName };
        if (additionalHeldLockFileNames is not null)
            skip.UnionWith(additionalHeldLockFileNames);

        foreach (string entry in Directory.EnumerateFileSystemEntries(millerDir))
        {
            if (skip.Contains(Path.GetFileName(entry)))
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

/// <summary>
/// The bundle of workspace-local write leases a destructive <c>workspace remove</c> must hold before deleting a
/// <c>.miller</c> dir. It exists to fix a real defect: CLI content imports hold <c>content.lock</c>
/// (<see cref="ContentCorpusWriteLock"/>) WITHOUT the indexer lock, and history appends hold <c>history.lock</c>
/// (<see cref="MetricHistoryWriteLock"/>) the same way — so guarding a remove with only the indexer
/// <see cref="SingleWriterLock"/> could delete <c>content.db</c>/<c>history.db</c> out from under an in-flight
/// write (Windows sharing-violation crash / POSIX unlinked-inode writes).
///
/// <para>Both remove call sites (the CLI <c>workspace remove</c> and the server <c>WorkspaceTool</c> remove
/// operation) acquire all three through <see cref="TryAcquireForRemove"/> so the FIXED lock order — indexer
/// <c>SingleWriterLock</c> → <c>content.lock</c> → <c>history.lock</c> — lives in exactly one place and cannot
/// drift between the two paths. That single order is what lets every writer pair avoid deadlock. This is one
/// small shared helper, not a general lock manager: it only acquires-in-order and disposes-in-reverse.</para>
/// </summary>
public sealed class WorkspaceWriteLeases : IDisposable
{
    /// <summary>
    /// The sidecar write-lock file names held ACROSS the delete in addition to the intrinsic indexer
    /// <see cref="SingleWriterLock.LockFileName"/> — the explicit skip-set to pass
    /// <see cref="SingleWriterLock.DeleteContentsExceptLock"/> so these held files survive the gutting and are
    /// cleaned up (with the indexer lock file) by <see cref="SingleWriterLock.TryDeleteEmptiedDir"/> after release.
    /// </summary>
    public static readonly IReadOnlySet<string> SidecarLockFileNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ContentCorpusWriteLock.LockFileName,
            MetricHistoryWriteLock.LockFileName,
        };

    /// <summary>
    /// The short per-lock acquire budget for a remove. A remove is interactive/CI teardown, not a long-running
    /// writer: if a sidecar lock is genuinely held by an in-flight import/append, we want to refuse promptly
    /// (nothing deleted) rather than block. Judgment call within the design's "short timeout" band.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly IDisposable _indexer;
    private readonly IDisposable _content;
    private readonly IDisposable _history;

    private WorkspaceWriteLeases(IDisposable indexer, IDisposable content, IDisposable history)
    {
        _indexer = indexer;
        _content = content;
        _history = history;
    }

    /// <summary>
    /// Acquire the indexer, content, and history write leases IN THAT FIXED ORDER, then hand back a bundle that
    /// releases them in reverse on <see cref="Dispose"/>. Returns <c>null</c> if ANY lease is unavailable — the
    /// caller's existing refused-in-use result — after releasing whatever was already taken, so a refusal never
    /// leaves a lease dangling and nothing is deleted.
    ///
    /// <para>The indexer acquisition is supplied by <paramref name="acquireIndexerLock"/> (the CLI passes
    /// <see cref="SingleWriterLock.TryAcquire"/>; the server passes its injected try-acquire) — a single
    /// non-blocking attempt, matching the existing remove behavior. The content and history leases poll up to
    /// <paramref name="timeout"/> (default <see cref="DefaultTimeout"/>) and throw
    /// <see cref="TimeoutException"/> on expiry, which is treated as unavailable.</para>
    /// </summary>
    public static WorkspaceWriteLeases? TryAcquireForRemove(
        string millerDir, Func<string, IDisposable?> acquireIndexerLock, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDir);
        ArgumentNullException.ThrowIfNull(acquireIndexerLock);

        IDisposable? indexer = acquireIndexerLock(millerDir);
        if (indexer is null)
            return null; // another writer owns the index — refuse, delete nothing.

        TimeSpan effective = timeout ?? DefaultTimeout;
        ContentCorpusWriteLock? content = null;
        MetricHistoryWriteLock? history = null;
        try
        {
            // The locks live NEXT TO their DB inside the same .miller dir; the *.db filename only supplies the
            // directory the lock derives its sibling <c>*.lock</c> path from.
            content = ContentCorpusWriteLock.AcquireFor(Path.Combine(millerDir, "content.db"), effective);
            history = MetricHistoryWriteLock.AcquireFor(
                Path.Combine(millerDir, MetricHistoryStore.HistoryDbFileName), effective);
            return new WorkspaceWriteLeases(indexer, content, history);
        }
        catch (TimeoutException)
        {
            // A sidecar lock is held by an in-flight import/append: release what we took (reverse order) and
            // refuse. content.db/history.db are left intact.
            history?.Dispose();
            content?.Dispose();
            indexer.Dispose();
            return null;
        }
    }

    /// <summary>Release all three leases in reverse acquisition order (history → content → indexer).</summary>
    public void Dispose()
    {
        _history.Dispose();
        _content.Dispose();
        _indexer.Dispose();
    }
}
