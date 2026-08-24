namespace Miller.Testing;

internal enum CtOperationLockState
{
    Missing,
    Available,
    Held,
    Unknown,
}

internal sealed class CtBuildRootOperationLease : IDisposable
{
    internal const string LockFileName = ".miller-ct-operation.lock";

    private FileStream? _stream;

    private CtBuildRootOperationLease(FileStream stream)
    {
        _stream = stream;
    }

    internal static CtBuildRootOperationLease Acquire(
        string buildOutputRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildOutputRoot);
        Directory.CreateDirectory(buildOutputRoot);
        string path = Path.Combine(buildOutputRoot, LockFileName);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new CtBuildRootOperationLease(
                    new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new IOException($"could not acquire CT build-root operation lock: {path}", exception);
            }
        }
    }

    internal static CtOperationLockState Probe(string buildOutputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildOutputRoot);
        string path = Path.Combine(buildOutputRoot, LockFileName);
        if (!File.Exists(path))
            return CtOperationLockState.Missing;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return CtOperationLockState.Available;
        }
        catch (IOException exception) when (IsLockContention(exception))
        {
            return CtOperationLockState.Held;
        }
        catch (IOException)
        {
            return CtOperationLockState.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return CtOperationLockState.Unknown;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }

    private static bool IsLockContention(IOException exception)
    {
        int nativeError = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows()
            ? nativeError is 32 or 33
            : nativeError is 11 or 35;
    }
}

internal sealed class CtMachineBuildJanitorLease : IDisposable
{
    internal const string LockFileName = ".miller-ct-cache-janitor.lock";

    private FileStream? _stream;

    private CtMachineBuildJanitorLease(FileStream stream)
    {
        _stream = stream;
    }

    internal static CtMachineBuildJanitorLease Acquire(string machineBuildRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineBuildRoot);
        Directory.CreateDirectory(machineBuildRoot);
        string path = Path.Combine(machineBuildRoot, LockFileName);
        try
        {
            return new CtMachineBuildJanitorLease(
                new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception)
        {
            throw new IOException($"could not acquire CT machine janitor lock: {path}", exception);
        }
    }

    internal static CtMachineBuildJanitorLease? TryAcquire(string machineBuildRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineBuildRoot);
        if (!Directory.Exists(machineBuildRoot))
            return null;
        string path = Path.Combine(machineBuildRoot, LockFileName);
        try
        {
            return new CtMachineBuildJanitorLease(
                new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
