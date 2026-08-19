using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Miller.Indexing;

namespace Miller.Testing;

/// <summary>One execution-scoped request for the user-global CT run lease.</summary>
public readonly record struct CtExecutionBudgetRequest(string WorkspaceRoot, string Reason);

/// <summary>Advisory owner record. The OS lock handle is the lease.</summary>
public sealed record CtExecutionBudgetOwner(
    int Pid,
    string WorkspaceRoot,
    string Reason,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// Held admission to run tests. Disposing releases the user-global lease. An idle daemon holds
/// nothing.
/// </summary>
public sealed class CtExecutionBudgetLease : IDisposable
{
    private readonly CtExecutionBudget? _budget;
    private FileStream? _stream;
    private bool _disposed;

    private CtExecutionBudgetLease()
    {
    }

    internal CtExecutionBudgetLease(CtExecutionBudget budget, FileStream stream)
    {
        _budget = budget;
        _stream = stream;
    }

    internal static CtExecutionBudgetLease NoOp() => new();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _budget?.TryDeleteOwnerFile();
        FileStream? stream = _stream;
        _stream = null;
        stream?.Dispose();
    }
}

/// <summary>
/// Capacity-1 user-global lease modeled on <see cref="ScanGovernor"/>. Held only while tests
/// execute. A second workspace reports paused while the first executes; idle daemons starve
/// nobody.
/// </summary>
public sealed partial class CtExecutionBudget
{
    public const string EnvVar = "MILLER_CT_EXEC_BUDGET";
    public const string DirectoryName = "ct-exec";
    public const string LockFileName = "ct-exec-v1.lock";
    public const string OwnerFileName = "ct-exec-v1.owner.json";

    internal static readonly TimeSpan BasePollDelay = TimeSpan.FromMilliseconds(150);

    private readonly Random _jitter = new();
    private readonly object _jitterGate = new();

    private CtExecutionBudget(string? directoryPath)
    {
        Enabled = directoryPath is not null;
        DirectoryPath = directoryPath ?? string.Empty;
        LockFilePath = directoryPath is null ? string.Empty : Path.Combine(directoryPath, LockFileName);
        OwnerFilePath = directoryPath is null ? string.Empty : Path.Combine(directoryPath, OwnerFileName);
    }

    public bool Enabled { get; }

    public string DirectoryPath { get; }

    public string LockFilePath { get; }

    public string OwnerFilePath { get; }

    public static CtExecutionBudget ForMillerHome(string millerHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerHome);
        return new CtExecutionBudget(Path.Combine(Path.GetFullPath(millerHome), DirectoryName));
    }

    public static CtExecutionBudget Disabled() => new(directoryPath: null);

    public static CtExecutionBudget FromEnvironment(string millerHome)
    {
        string? raw = Environment.GetEnvironmentVariable(EnvVar);
        return CtEnvironment.IsOff(raw) ? Disabled() : ForMillerHome(millerHome);
    }

    public CtExecutionBudgetLease? TryAcquire(
        CtExecutionBudgetRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
            return CtExecutionBudgetLease.NoOp();
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
            throw new ArgumentException("must not be empty", nameof(request));

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DirectoryPath);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                TryWriteOwnerFile(request);
                return new CtExecutionBudgetLease(this, stream);
            }
            catch (IOException ex) when (IsLockContention(ex))
            {
                TimeSpan remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    return null;
                TimeSpan wait = NextPollDelay(remaining);
                cancellationToken.WaitHandle.WaitOne(wait);
            }
        }
    }

    public CtExecutionBudgetOwner? TryReadOwner()
    {
        if (!Enabled)
            return null;
        try
        {
            if (!File.Exists(OwnerFilePath))
                return null;
            return JsonSerializer.Deserialize(
                File.ReadAllText(OwnerFilePath),
                CtExecutionBudgetJsonContext.Default.CtExecutionBudgetOwner);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal void TryDeleteOwnerFile()
    {
        if (!Enabled)
            return;
        try
        {
            if (File.Exists(OwnerFilePath))
                File.Delete(OwnerFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void TryWriteOwnerFile(CtExecutionBudgetRequest request)
    {
        string tempPath = OwnerFilePath + ".tmp";
        try
        {
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(
                    new CtExecutionBudgetOwner(
                        Environment.ProcessId,
                        request.WorkspaceRoot,
                        request.Reason,
                        DateTimeOffset.UtcNow),
                    CtExecutionBudgetJsonContext.Default.CtExecutionBudgetOwner));
            File.Move(tempPath, OwnerFilePath, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception)
            {
            }
        }
    }

    private TimeSpan NextPollDelay(TimeSpan remaining)
    {
        double jitteredMs;
        lock (_jitterGate)
            jitteredMs = BasePollDelay.TotalMilliseconds * (0.5 + _jitter.NextDouble());
        var jittered = TimeSpan.FromMilliseconds(jitteredMs);
        return jittered < remaining ? jittered : remaining;
    }

    private static bool IsLockContention(IOException ex)
    {
        int nativeError = ex.HResult & 0xFFFF;
        if (OperatingSystem.IsWindows())
            return nativeError is 32 or 33;
        return nativeError is 11 or 35;
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(CtExecutionBudgetOwner))]
    internal sealed partial class CtExecutionBudgetJsonContext : JsonSerializerContext;
}
