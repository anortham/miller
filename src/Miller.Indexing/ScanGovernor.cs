using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Indexing;

/// <summary>
/// What one whole-repo scan is asking admission for: the workspace it will extract, a short stable reason
/// token (<c>bootstrap</c>, <c>leader-ondemand</c>, <c>cross-workspace-refresh</c>, …), and the
/// <c>--jobs</c> cap that scan will run with.
/// </summary>
public readonly record struct ScanGovernorRequest(string WorkspaceRoot, string Reason, int Jobs);

/// <summary>
/// The diagnostics record written next to the scan lease. Advisory ONLY: a crash leaves it behind and a
/// mid-acquire holder has not written one yet, so it may name a live process that holds nothing.
/// </summary>
public sealed record ScanGovernorOwner(
    int Pid,
    string WorkspaceRoot,
    string Reason,
    int Jobs,
    DateTimeOffset StartedAtUtc);

/// <summary>
/// A held admission to run one whole-repo scan. The open <see cref="FileShare.None"/> handle IS the lease;
/// disposing it releases admission (and the OS releases it on process death, including SIGKILL).
/// </summary>
public sealed class ScanGovernorLease : IDisposable
{
    private readonly ScanGovernor? _governor;
    private FileStream? _stream;
    private bool _disposed;

    private ScanGovernorLease()
    {
    }

    internal ScanGovernorLease(ScanGovernor governor, FileStream stream)
    {
        _governor = governor;
        _stream = stream;
    }

    /// <summary>The admission a disabled governor hands out: holds nothing and releases nothing.</summary>
    internal static ScanGovernorLease NoOp() => new();

    /// <summary>Release admission. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _governor?.TryDeleteOwnerFile();
        FileStream? stream = _stream;
        _stream = null;
        stream?.Dispose();
        _governor?.ClearHeldWorkspace();
        _governor?.LeaveThread();
    }
}

/// <summary>
/// A capacity-1 admission lease over whole-repo <c>julie-extract</c> scans. It covers the extract subprocess
/// only: every governed caller releases it as soon as the scan returns, because the per-workspace sidecar
/// convergence that follows is already serialized by its own workspace lock and holding the machine-wide lease
/// across it serialized a worktree fleet on one queue (2026-08-06 P4 scale validation §3).
/// <see cref="SingleWriterLock"/> is strictly per-workspace, so N git worktrees
/// meant N independent leaders each spawning an extractor at once — the fleet behaviour that drove a reporter's
/// machine into the OOM killer (2026-08-01 multi-worktree field report). This lease sits ABOVE those locks so at
/// most one governed scan runs at a time.
///
/// <para><b>Scope is PER-USER, not per-machine.</b> The lease lives under the invoking user's
/// <c>~/.miller/scan/</c>, mirroring the semantic accelerator lease's path convention. Two OS users running
/// Miller on one box still run two concurrent scans.</para>
///
/// <para><b>Not re-entrant.</b> One process genuinely can scan two DIFFERENT workspaces at once (through
/// <c>WorkspaceIndexProvider</c>'s refresh), which is exactly what this exists to stop, so re-entrancy would
/// defeat it. Different threads contending QUEUE (the <see cref="FileShare.None"/> handle denies a second handle
/// to this process too); the SAME thread re-entering is an accidental double-wrap and throws.</para>
///
/// <para><b>Lock order.</b> <c>SingleWriterLock</c> (per workspace) → <c>ScanGovernor</c> (user-global) →
/// <c>_opsGate</c> → <c>content.lock</c> → <c>history.lock</c>. Nothing may acquire a workspace
/// <c>SingleWriterLock</c> while holding this lease.</para>
///
/// <para>Per-file <c>update</c>/<c>delete</c> are deliberately EXEMPT: they are sub-second and gating them would
/// stall interactive edits.</para>
/// </summary>
public sealed partial class ScanGovernor
{
    /// <summary>Kill switch. Set to <c>0/false/off/no</c> for a zero-work opt-out.</summary>
    public const string EnvVar = "MILLER_SCAN_GOVERNOR";

    /// <summary>Operator override for the long (leader/forced) admission budget: seconds or a TimeSpan.</summary>
    public const string WaitEnvVar = "MILLER_SCAN_GOVERNOR_WAIT";

    /// <summary>The directory name under the miller home that holds the lease and its owner record.</summary>
    public const string DirectoryName = "scan";

    /// <summary>The versioned lease file name.</summary>
    public const string LockFileName = "scan-v1.lock";

    /// <summary>The versioned owner-diagnostics file name.</summary>
    public const string OwnerFileName = "scan-v1.owner.json";

    /// <summary>The default admission budget for leader and forced paths.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(30);

    internal static readonly TimeSpan BasePollDelay = TimeSpan.FromMilliseconds(150);
    internal static readonly TimeSpan MinPollDelay = TimeSpan.FromMilliseconds(75);
    internal static readonly TimeSpan MaxPollDelay = TimeSpan.FromMilliseconds(225);

    // ONE thread-local slot for the whole type rather than a ThreadLocal per instance: a per-instance
    // ThreadLocal reserves an id for the instance's lifetime, and a governor is built per miller home at many
    // call sites. Tracking the INSTANCES this thread holds keeps re-entry detection per-instance, so two
    // governors over different homes on one thread stay legitimate.
    [ThreadStatic]
    private static List<ScanGovernor>? _threadHeld;

    private readonly Random _jitter = new();
    private readonly object _jitterGate = new();
    private readonly object _heldGate = new();
    private string? _heldWorkspaceRoot;

    private ScanGovernor(string? directoryPath)
    {
        Enabled = directoryPath is not null;
        DirectoryPath = directoryPath ?? string.Empty;
        LockFilePath = directoryPath is null ? string.Empty : Path.Combine(directoryPath, LockFileName);
        OwnerFilePath = directoryPath is null ? string.Empty : Path.Combine(directoryPath, OwnerFileName);
    }

    /// <summary>Whether admission is enforced. False for <see cref="Disabled"/> — a zero-work guarantee.</summary>
    public bool Enabled { get; }

    /// <summary>The <c>&lt;millerHome&gt;/scan</c> directory, or empty when disabled.</summary>
    public string DirectoryPath { get; }

    /// <summary>The absolute lease path, or empty when disabled.</summary>
    public string LockFilePath { get; }

    /// <summary>The absolute owner-diagnostics path, or empty when disabled.</summary>
    public string OwnerFilePath { get; }

    /// <summary>Build a governor over <paramref name="millerHome"/> (injected, never resolved internally).</summary>
    public static ScanGovernor ForMillerHome(string millerHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerHome);
        return new ScanGovernor(Path.Combine(Path.GetFullPath(millerHome), DirectoryName));
    }

    /// <summary>The off instance: every acquire admits immediately, and no directory or file is ever touched.</summary>
    public static ScanGovernor Disabled() => new(directoryPath: null);

    /// <summary>
    /// Build from the process environment: on by DEFAULT, off only for an explicit falsy
    /// <see cref="EnvVar"/> value (<c>0/false/off/no</c>, any case).
    /// </summary>
    public static ScanGovernor FromEnvironment(string millerHome) =>
        FromEnvValue(Environment.GetEnvironmentVariable(EnvVar), millerHome);

    /// <summary>The pure env-value ⇒ governor mapping behind <see cref="FromEnvironment"/>.</summary>
    internal static ScanGovernor FromEnvValue(string? raw, string millerHome) =>
        SymbolSearchSidecar.IsDisabledValue(raw) ? Disabled() : ForMillerHome(millerHome);

    /// <summary>The long admission budget from the environment, falling back to <see cref="DefaultWait"/>.</summary>
    public static TimeSpan WaitFromEnvironment() =>
        ParseWait(Environment.GetEnvironmentVariable(WaitEnvVar));

    /// <summary>
    /// Parse the admission budget: a plain number is seconds, otherwise a <see cref="TimeSpan"/> literal. An
    /// absent, unparsable, negative, non-finite, or out-of-range value falls back to <see cref="DefaultWait"/>;
    /// this never throws (a typo in an env var must not fail a scan).
    /// </summary>
    internal static TimeSpan ParseWait(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultWait;

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return !double.IsNaN(seconds) &&
                !double.IsInfinity(seconds) &&
                seconds >= 0 &&
                seconds <= TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.FromSeconds(seconds)
                : DefaultWait;
        }

        return TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out TimeSpan parsed) && parsed >= TimeSpan.Zero
            ? parsed
            : DefaultWait;
    }

    /// <summary>
    /// Wait up to <paramref name="timeout"/> for admission. Returns the held lease, or <c>null</c> when the
    /// budget expired with the lease still held elsewhere — never a <see cref="TimeoutException"/>, because every
    /// caller must be able to degrade rather than fail.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">This thread already holds admission (an accidental double-wrap).</exception>
    /// <exception cref="IOException">The lease could not be opened for a reason other than contention.</exception>
    public ScanGovernorLease? TryAcquire(
        ScanGovernorRequest request, TimeSpan timeout, CancellationToken cancellationToken) =>
        TryAcquire(request, timeout, cancellationToken, delay: null);

    /// <summary>Test seam: <paramref name="delay"/> replaces the real cancellable poll wait (null = real).</summary>
    internal ScanGovernorLease? TryAcquire(
        ScanGovernorRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<TimeSpan>? delay)
    {
        if (!Enabled)
            return ScanGovernorLease.NoOp();

        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Scan admission budget must be >= 0.");

        if (ThreadHoldsAdmission())
        {
            throw new InvalidOperationException(
                "The scan governor is not re-entrant, and this thread already holds admission. A nested acquire " +
                "is an accidental double-wrap: hoist the admission to the outermost governed scan.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DirectoryPath);

        EnterThread();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    var lease = new ScanGovernorLease(this, stream);
                    TryWriteOwnerFile(request);
                    RecordHeldWorkspace(request.WorkspaceRoot);
                    return lease;
                }
                catch (IOException ex) when (SingleWriterLock.IsLockContention(ex, OperatingSystem.IsWindows()))
                {
                    TimeSpan remaining = timeout - stopwatch.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        LeaveThread();
                        return null;
                    }

                    TimeSpan wait = NextPollDelay(remaining);
                    if (delay is not null)
                        delay(wait);
                    else
                        cancellationToken.WaitHandle.WaitOne(wait);
                }
            }
        }
        catch
        {
            LeaveThread();
            throw;
        }
    }

    /// <summary>
    /// The RECORDED owner, or null when none is recorded or the record is unreadable/malformed. Informational
    /// only: the OS handle is the lease, so a stale record naming a live pid must never prevent an acquire.
    /// </summary>
    public ScanGovernorOwner? TryReadOwner()
    {
        if (!Enabled)
            return null;

        try
        {
            if (!File.Exists(OwnerFilePath))
                return null;
            return JsonSerializer.Deserialize(
                File.ReadAllText(OwnerFilePath), ScanGovernorOwnerJsonContext.Default.ScanGovernorOwner);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A refusal-message description of the recorded owner. Each observed state is reported as exactly what it
    /// proves; nothing is inferred from file age.
    /// </summary>
    public string DescribeHolder()
    {
        if (!Enabled)
            return "The machine-wide scan governor is disabled.";

        if (TryReadOwner() is not { } owner)
        {
            return "No scan-governor owner is recorded — the holder is likely mid-acquire, or exited without " +
                "recording one.";
        }

        return "The recorded scan-governor owner is miller pid " +
            owner.Pid.ToString(CultureInfo.InvariantCulture) +
            " scanning '" + owner.WorkspaceRoot + "' (reason " + owner.Reason + ", recorded " +
            owner.StartedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) +
            "). That record is advisory — the OS lease handle is the authority.";
    }

    /// <summary>
    /// True when THIS governor instance currently holds the OS lease for <paramref name="workspaceRoot"/>.
    /// Distinct from <see cref="TryReadOwner"/>: that record is advisory and can name a live pid that holds
    /// nothing, so it must never prevent an acquire.
    /// </summary>
    public bool HoldsAdmissionFor(string workspaceRoot)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(workspaceRoot))
            return false;

        lock (_heldGate)
            return string.Equals(_heldWorkspaceRoot, workspaceRoot, StringComparison.Ordinal);
    }

    private bool ThreadHoldsAdmission() => _threadHeld is { } held && held.Contains(this);

    private void EnterThread() => (_threadHeld ??= new List<ScanGovernor>(1)).Add(this);

    internal void LeaveThread() => _threadHeld?.Remove(this);

    private void RecordHeldWorkspace(string workspaceRoot)
    {
        lock (_heldGate)
            _heldWorkspaceRoot = workspaceRoot;
    }

    internal void ClearHeldWorkspace()
    {
        lock (_heldGate)
            _heldWorkspaceRoot = null;
    }

    internal void TryDeleteOwnerFile()
    {
        if (!Enabled)
            return;
        TryDeleteFile(OwnerFilePath);
    }

    private TimeSpan NextPollDelay(TimeSpan remaining)
    {
        double jitteredMs;
        lock (_jitterGate)
            jitteredMs = BasePollDelay.TotalMilliseconds * (0.5 + _jitter.NextDouble());

        var jittered = TimeSpan.FromMilliseconds(jitteredMs);
        return jittered < remaining ? jittered : remaining;
    }

    private void TryWriteOwnerFile(ScanGovernorRequest request)
    {
        // The OS handle is the lease; this file is diagnostics and must never gate a decision.
        string tempPath = OwnerFilePath + ".tmp";
        try
        {
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(
                    new ScanGovernorOwner(
                        Environment.ProcessId,
                        request.WorkspaceRoot,
                        request.Reason,
                        request.Jobs,
                        DateTimeOffset.UtcNow),
                    ScanGovernorOwnerJsonContext.Default.ScanGovernorOwner));
            File.Move(tempPath, OwnerFilePath, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(ScanGovernorOwner))]
    internal sealed partial class ScanGovernorOwnerJsonContext : JsonSerializerContext;
}
