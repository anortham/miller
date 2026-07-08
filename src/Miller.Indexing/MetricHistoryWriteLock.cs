using System.Diagnostics;

namespace Miller.Indexing;

/// <summary>
/// Cross-process write serialization for the append-only <c>history.db</c> metric-history sidecar. The leader
/// (after converge) and CLI one-shots (heavy metric arms) may append concurrently, so every history writer holds
/// this short-lived lease for the duration of its append transaction only. <c>workspace remove</c> also acquires
/// it (short timeout) before deleting <c>.miller</c> contents so a delete can never race an in-flight append.
///
/// <para>Mechanics mirror <see cref="ContentCorpusWriteLock"/> (an exclusive <c>FileShare.None</c> handle on a
/// sibling lock file). Lock order across Miller's workspace-local write locks is fixed — indexer
/// <c>SingleWriterLock</c> first where held, then <c>content.lock</c>, then <c>history.lock</c> — so no writer
/// pair can deadlock.</para>
/// </summary>
public sealed class MetricHistoryWriteLock : IDisposable
{
    public const string LockFileName = "history.lock";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private FileStream? _stream;

    public string LockFilePath { get; }

    private MetricHistoryWriteLock(FileStream stream, string lockFilePath)
    {
        _stream = stream;
        LockFilePath = lockFilePath;
    }

    public static string LockFilePathFor(string historyDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyDbPath);
        string fullPath = Path.GetFullPath(historyDbPath);
        string dir = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"Path has no directory: {historyDbPath}", nameof(historyDbPath));
        return Path.Combine(dir, LockFileName);
    }

    /// <summary>
    /// Acquire the history write lease. A <paramref name="timeout"/> of <see cref="TimeSpan.Zero"/> makes this a
    /// single non-blocking attempt (the leader's skip-on-busy converge path); a positive timeout polls until it
    /// elapses (CLI heavy arms and <c>workspace remove</c>). Throws <see cref="TimeoutException"/> on expiry.
    /// </summary>
    public static MetricHistoryWriteLock AcquireFor(string historyDbPath, TimeSpan? timeout = null)
    {
        string lockFilePath = LockFilePathFor(historyDbPath);
        string dir = Path.GetDirectoryName(lockFilePath)!;
        Directory.CreateDirectory(dir);

        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "History write lock timeout must be >= 0.");

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new MetricHistoryWriteLock(stream, lockFilePath);
            }
            catch (IOException ex) when (stopwatch.Elapsed >= effectiveTimeout)
            {
                throw new TimeoutException(
                    $"Could not acquire history write lock at '{lockFilePath}' within {effectiveTimeout}.", ex);
            }
            catch (IOException)
            {
                TimeSpan remaining = effectiveTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    continue;
                Thread.Sleep(remaining < PollInterval ? remaining : PollInterval);
            }
        }
    }

    public void Dispose()
    {
        var stream = _stream;
        if (stream is null)
            return;
        _stream = null;
        stream.Dispose();
    }
}
