using System.Diagnostics;

namespace Miller.Indexing;

/// <summary>
/// Cross-process write serialization for <c>content.db</c>. Workspace rebuilds replace the whole file while
/// external/web imports mutate it in place, so both paths take this short-lived lease around the actual DB write.
/// </summary>
public sealed class ContentCorpusWriteLock : IDisposable
{
    public const string LockFileName = "content.lock";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private FileStream? _stream;

    public string LockFilePath { get; }

    private ContentCorpusWriteLock(FileStream stream, string lockFilePath)
    {
        _stream = stream;
        LockFilePath = lockFilePath;
    }

    public static string LockFilePathFor(string contentDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        string fullPath = Path.GetFullPath(contentDbPath);
        string dir = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"Path has no directory: {contentDbPath}", nameof(contentDbPath));
        return Path.Combine(dir, LockFileName);
    }

    public static ContentCorpusWriteLock AcquireFor(string contentDbPath, TimeSpan? timeout = null)
    {
        string lockFilePath = LockFilePathFor(contentDbPath);
        string dir = Path.GetDirectoryName(lockFilePath)!;
        Directory.CreateDirectory(dir);

        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Content corpus write lock timeout must be >= 0.");

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new ContentCorpusWriteLock(stream, lockFilePath);
            }
            catch (IOException ex) when (stopwatch.Elapsed >= effectiveTimeout)
            {
                throw new TimeoutException(
                    $"Could not acquire content corpus write lock at '{lockFilePath}' within {effectiveTimeout}.",
                    ex);
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
