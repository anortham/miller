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
        return Acquire(lockFilePath, timeout);
    }

    /// <summary>
    /// Take the lease WITHOUT creating anything but the lock file itself, or return null when the sidecar
    /// directory is absent or another process holds the lease.
    ///
    /// <para>A caller that is LEAVING a store — the sidecar reclaim of a removed workspace — must never
    /// manufacture the directory it is cleaning out, the same rule the CT control plane learned on 2026-08-21.
    /// <see cref="AcquireFor"/> creates the directory because its callers are convergers writing into a store
    /// they belong to; this variant is for the callers that do not.</para>
    /// </summary>
    public static FamilyStoreSidecarWriteLease? TryAcquireExisting(string storeRoot, TimeSpan timeout)
    {
        string lockFilePath;
        try
        {
            lockFilePath = LockFilePathFor(storeRoot);
            if (!Directory.Exists(Path.GetDirectoryName(lockFilePath)!))
                return null;
            return Acquire(lockFilePath, timeout);
        }
        catch (Exception ex) when (
            ex is TimeoutException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return null;
        }
    }

    private static FamilyStoreSidecarWriteLease Acquire(string lockFilePath, TimeSpan? timeout)
    {
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
            catch (DirectoryNotFoundException)
            {
                throw; // the sidecar directory is gone: waiting cannot bring it back, and we must not create it.
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
