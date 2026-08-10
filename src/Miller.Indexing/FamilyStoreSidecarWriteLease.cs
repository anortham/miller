using System.Diagnostics;

namespace Miller.Indexing;

public sealed class FamilyStoreSidecarWriteLease : IDisposable
{
    public const string LockFileName = "sidecar-converger.lock";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private FileStream? _stream;

    private FamilyStoreSidecarWriteLease(FileStream stream, string lockFilePath)
    {
        _stream = stream;
        LockFilePath = lockFilePath;
    }

    public string LockFilePath { get; }

    public static string LockFilePathFor(string storeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(storeRoot);
        return Path.Combine(canonicalRoot, "sidecars", LockFileName);
    }

    public static FamilyStoreSidecarWriteLease AcquireFor(
        string storeRoot,
        TimeSpan? timeout = null)
    {
        string lockFilePath = LockFilePathFor(storeRoot);
        string directory = Path.GetDirectoryName(lockFilePath)!;
        Directory.CreateDirectory(directory);

        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Sidecar lease timeout must be >= 0.");

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return new FamilyStoreSidecarWriteLease(stream, lockFilePath);
            }
            catch (IOException ex) when (stopwatch.Elapsed >= effectiveTimeout)
            {
                throw new TimeoutException(
                    $"Could not acquire family-store sidecar lease at '{lockFilePath}' within {effectiveTimeout}.",
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
        FileStream? stream = _stream;
        if (stream is null)
            return;
        _stream = null;
        stream.Dispose();
    }
}
