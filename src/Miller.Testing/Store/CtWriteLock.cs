using System.Diagnostics;

namespace Miller.Testing;

/// <summary>
/// Cross-process write serialization for <c>ct.db</c>. Every store writer holds this short-lived
/// exclusive <c>FileShare.None</c> lease on the sibling <c>ct.lock</c> for the duration of its
/// write or <see cref="ContinuousTestStore.Transaction"/> only.
/// </summary>
public sealed class CtWriteLock : IDisposable
{
    public const string LockFileName = "ct.lock";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private FileStream? _stream;

    public string LockFilePath { get; }

    private CtWriteLock(FileStream stream, string lockFilePath)
    {
        _stream = stream;
        LockFilePath = lockFilePath;
    }

    public static string LockFilePathFor(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        string fullPath = Path.GetFullPath(dbPath);
        string dir = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"Path has no directory: {dbPath}", nameof(dbPath));
        return Path.Combine(dir, LockFileName);
    }

    public static CtWriteLock AcquireFor(string dbPath, TimeSpan? timeout = null)
    {
        string lockFilePath = LockFilePathFor(dbPath);
        string dir = Path.GetDirectoryName(lockFilePath)!;
        Directory.CreateDirectory(dir);

        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "CT write lock timeout must be >= 0.");

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new CtWriteLock(stream, lockFilePath);
            }
            catch (IOException ex) when (stopwatch.Elapsed >= effectiveTimeout)
            {
                throw new TimeoutException(
                    $"Could not acquire CT write lock at '{lockFilePath}' within {effectiveTimeout}.", ex);
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
        FileStream? stream = _stream;
        if (stream is null)
            return;
        _stream = null;
        stream.Dispose();
    }
}
