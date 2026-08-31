using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Tools;

namespace Miller.Server.Cli;

internal interface IDashboardLauncher
{
    DashboardLaunchResult EnsureRunning(DashboardLaunchRequest request);

    DashboardStopResult Stop(DashboardStopRequest request);
}

/// <summary>
/// <paramref name="OwnVersion"/> is the calling build, and it turns the version check on. Left null,
/// a healthy dashboard is always reused — the behaviour every caller had before the check existed.
/// Production passes <see cref="MillerVersion.Current"/>.
/// <paramref name="OpenWorkspaceView"/> false means the caller has no workspace to show, so the
/// returned URL is the registry list view (<c>/</c>) instead of a workspace detail derived from
/// <paramref name="Context"/>; the context still supplies the machine paths the launch needs.
/// </summary>
internal sealed record DashboardLaunchRequest(
    WorkspaceContext Context,
    int Port,
    TimeSpan StartupTimeout,
    string? OwnVersion = null,
    bool OpenWorkspaceView = true);

internal sealed record DashboardStopRequest(WorkspaceContext Context, TimeSpan StopTimeout);

internal enum DashboardLaunchOutcome
{
    AlreadyRunning,
    Started,

    /// <summary>
    /// A dashboard on a different build was stopped and this build started in its place. A third
    /// outcome on purpose: a caller must not read it as a fresh start, and must not read it as
    /// nothing-happened.
    /// </summary>
    Replaced,
    Failed,
}

internal enum DashboardStopOutcome
{
    Stopped,
    NotRunning,
    Failed,
}

internal sealed record DashboardLaunchResult(
    DashboardLaunchOutcome Outcome,
    Uri Url,
    int? ProcessId,
    string? Message)
{
    public bool Success => Outcome
        is DashboardLaunchOutcome.AlreadyRunning
        or DashboardLaunchOutcome.Started
        or DashboardLaunchOutcome.Replaced;
}

internal sealed record DashboardStopResult(
    DashboardStopOutcome Outcome,
    int? ProcessId,
    string? Version,
    string Message)
{
    public bool Success => Outcome is DashboardStopOutcome.Stopped or DashboardStopOutcome.NotRunning;
}

/// <summary>
/// What a live-process probe found. A null probe means the pid runs nothing; a probe whose
/// <paramref name="StartedAtUtc"/> is null means the pid runs something whose start time the OS would
/// not report, so its identity cannot be proven and it must not be killed.
/// </summary>
internal sealed record DashboardProcessProbe(DateTimeOffset? StartedAtUtc);

internal sealed class DashboardCliLauncher : IDashboardLauncher
{
    internal const int DefaultPort = 4977;

    /// <summary>
    /// The <c>/healthz</c> body's stable PREFIX. The dashboard appends its own build after it, so the
    /// probe matches the prefix and never the whole line — an existing health check that looks for this
    /// text keeps passing, and a body that grows again does not turn every dashboard unhealthy.
    /// </summary>
    internal const string HealthBody = "miller-dashboard ok";

    /// <summary>
    /// How far the probed start time may sit from <see cref="DashboardProcessMetadata.ProcessStartedAtUtc"/>.
    /// The CT lease's two seconds: both stamps are the same process's own start time, read the same way.
    /// </summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The window for a record that predates <see cref="DashboardProcessMetadata.ProcessStartedAtUtc"/>,
    /// whose only stamp is the launcher's clock at the moment it wrote the record. That is up to a second
    /// after the spawn on Unix, where the pid comes from a file the launch script writes, so the window
    /// has to be wider — and a wider window is a wider pid-reuse hole, which is why a record proven this
    /// way must also have a url that still answers before anything is killed.
    /// </summary>
    private static readonly TimeSpan UnrecordedStartTolerance = TimeSpan.FromSeconds(10);

    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Func<Uri, bool> _isHealthy;
    private readonly Func<string, IDisposable?> _tryAcquireLaunchLock;
    private readonly Action<string, DashboardProcessMetadata> _writeMetadata;
    private readonly Action<TimeSpan> _sleep;
    private readonly Func<int, DashboardProcessProbe?> _probeProcess;
    private readonly Func<int, bool> _killProcess;

    public DashboardCliLauncher()
        : this(
            static info => Process.Start(info),
            IsHealthy,
            TryAcquireLaunchLock,
            WriteMetadata,
            static delay => Thread.Sleep(delay))
    {
    }

    internal DashboardCliLauncher(
        Func<ProcessStartInfo, Process?> startProcess,
        Func<Uri, bool> isHealthy,
        Func<string, IDisposable?> tryAcquireLaunchLock,
        Action<string, DashboardProcessMetadata> writeMetadata,
        Action<TimeSpan> sleep,
        Func<int, DashboardProcessProbe?>? probeProcess = null,
        Func<int, bool>? killProcess = null)
    {
        ArgumentNullException.ThrowIfNull(startProcess);
        ArgumentNullException.ThrowIfNull(isHealthy);
        ArgumentNullException.ThrowIfNull(tryAcquireLaunchLock);
        ArgumentNullException.ThrowIfNull(writeMetadata);
        ArgumentNullException.ThrowIfNull(sleep);
        _startProcess = startProcess;
        _isHealthy = isHealthy;
        _tryAcquireLaunchLock = tryAcquireLaunchLock;
        _writeMetadata = writeMetadata;
        _sleep = sleep;
        _probeProcess = probeProcess ?? ProbeProcess;
        _killProcess = killProcess ?? KillProcess;
    }

    public DashboardLaunchResult EnsureRunning(DashboardLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string machineMillerDir = Path.GetDirectoryName(request.Context.RegistryDbPath)
            ?? throw new InvalidOperationException("Cannot determine the machine Miller directory.");
        Directory.CreateDirectory(machineMillerDir);
        string metadataPath = Path.Combine(machineMillerDir, "dashboard.json");

        RecordedDashboard existing = InspectRecorded(metadataPath, request);
        if (existing.Reuse is { } reuse)
            return reuse;

        Uri baseUri = BaseUri(request.Port);
        Uri url = LaunchUrl(baseUri, request);
        if (existing.Replace is null && _isHealthy(baseUri))
            return new DashboardLaunchResult(DashboardLaunchOutcome.AlreadyRunning, url, null, "already running");

        string lockPath = Path.Combine(machineMillerDir, "dashboard.lock");
        using IDisposable? launchLock = _tryAcquireLaunchLock(lockPath);
        if (launchLock is null)
        {
            return WaitForHealthy(baseUri, request.StartupTimeout)
                ? new DashboardLaunchResult(
                    DashboardLaunchOutcome.AlreadyRunning,
                    url,
                    null,
                    existing.Replace is null
                        ? "already running"
                        : "already running; another dashboard launch holds the launch lock, "
                            + $"so the dashboard on {existing.Replace.RunningVersionLabel} was not replaced")
                : new DashboardLaunchResult(
                    DashboardLaunchOutcome.Failed,
                    url,
                    null,
                    "dashboard launch is already in progress but did not become healthy");
        }

        // Everything read before the lock is a snapshot. Another launcher may have replaced the dashboard
        // and written a new record in between, so the pid this one is about to kill can already belong to
        // something else. The record is read again, and the decision taken again, under the lock.
        existing = InspectRecorded(metadataPath, request);
        if (existing.Reuse is { } reuseUnderLock)
            return reuseUnderLock;
        if (existing.Replace is null && _isHealthy(baseUri))
            return new DashboardLaunchResult(DashboardLaunchOutcome.AlreadyRunning, url, null, "already running");

        // Resolved BEFORE the old dashboard is stopped. A replace that killed a working dashboard and
        // then discovered it had nothing to start would leave the machine with no dashboard at all.
        const string BinaryMissing = "dashboard binary not found; build Miller.Dashboard or set MILLER_DASHBOARD_DLL";
        if (ResolveDashboardCommand(request.Context) is not { } command)
        {
            return existing.Replace is null
                ? new DashboardLaunchResult(DashboardLaunchOutcome.Failed, url, null, BinaryMissing)
                : new DashboardLaunchResult(
                    DashboardLaunchOutcome.AlreadyRunning,
                    LaunchUrl(existing.BaseUri!, request),
                    null,
                    $"already running; the dashboard on {existing.Replace.RunningVersionLabel} could not be "
                        + $"replaced: {BinaryMissing}");
        }

        if (existing.Replace is { } replace)
        {
            DashboardStopAttempt stopped = StopRecorded(existing.Recorded!, existing.BaseUri!, request.StartupTimeout);
            if (stopped.Outcome == DashboardStopOutcome.Failed)
            {
                // A kill cannot be taken back. Once the old dashboard has been signalled, "already
                // running" is a claim about a process this launch just told the system to end.
                return stopped.Signalled
                    ? new DashboardLaunchResult(DashboardLaunchOutcome.Failed, url, null, stopped.Message)
                    : new DashboardLaunchResult(
                        DashboardLaunchOutcome.AlreadyRunning,
                        LaunchUrl(existing.BaseUri!, request),
                        null,
                        $"already running; the dashboard on {replace.RunningVersionLabel} could not be "
                            + $"replaced: {stopped.Message}");
            }

            if (_isHealthy(baseUri))
                return new DashboardLaunchResult(DashboardLaunchOutcome.AlreadyRunning, url, null, "already running");
        }

        string pidPath = Path.Combine(machineMillerDir, "dashboard.pid");
        DeleteFileIfExists(pidPath);
        DateTimeOffset launchStartedUtc = DateTimeOffset.UtcNow;
        ProcessStartInfo startInfo = command.ToStartInfo(request.Context, request.Port, machineMillerDir, pidPath);
        Process? process;
        try
        {
            // Windows: hand the dashboard the two log FILES as its stdout and stderr, and clear the
            // inheritable flag on this process's own handles first. Without that the dashboard inherited the
            // caller's stdout — so `miller dashboard | anything` never returned, because the pipe stayed open
            // for the dashboard's whole life even after miller exited. Unix already redirects inside its
            // /bin/sh launch script, and DetachedProcessStreams passes that branch straight through.
            process = DetachedProcessStreams.Start(
                startInfo,
                DashboardCommand.StdoutLogPath(machineMillerDir),
                DashboardCommand.StderrLogPath(machineMillerDir),
                _startProcess);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new DashboardLaunchResult(DashboardLaunchOutcome.Failed, url, null, ex.Message);
        }

        if (process is null)
            return new DashboardLaunchResult(DashboardLaunchOutcome.Failed, url, null, "dashboard process did not start");
        int processId = ResolveStartedProcessId(process, pidPath, launchStartedUtc);

        // Asked for while the process is certainly alive, and recorded only once it answers. The
        // record is the kill list for the next replace, so the pid must come with the one fact that
        // proves the pid is still this dashboard rather than whatever inherited the number.
        DateTimeOffset? processStartedAtUtc = _probeProcess(processId)?.StartedAtUtc;

        if (!WaitForHealthy(baseUri, request.StartupTimeout))
        {
            // A dashboard that never answered may already be gone, and its pid may already belong to
            // something else, so nothing about it is left on disk for a later stop to act on.
            DeleteFileIfExists(metadataPath);
            return new DashboardLaunchResult(
                DashboardLaunchOutcome.Failed,
                url,
                processId,
                "dashboard process started but /healthz did not become healthy");
        }

        _writeMetadata(metadataPath, new DashboardProcessMetadata(
            ProcessId: processId,
            Url: baseUri.ToString().TrimEnd('/'),
            StartedAtUtc: DateTimeOffset.UtcNow,
            MillerVersion: request.OwnVersion,
            ProcessStartedAtUtc: processStartedAtUtc));

        return existing.Replace is null
            ? new DashboardLaunchResult(DashboardLaunchOutcome.Started, url, processId, "started")
            : new DashboardLaunchResult(
                DashboardLaunchOutcome.Replaced,
                url,
                processId,
                $"replaced the dashboard on {existing.Replace.RunningVersionLabel}");
    }

    public DashboardStopResult Stop(DashboardStopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string machineMillerDir = Path.GetDirectoryName(request.Context.RegistryDbPath)
            ?? throw new InvalidOperationException("Cannot determine the machine Miller directory.");
        string metadataPath = Path.Combine(machineMillerDir, "dashboard.json");

        DashboardProcessMetadata? recorded = TryReadMetadata(metadataPath);
        if (recorded is null)
        {
            return new DashboardStopResult(
                DashboardStopOutcome.NotRunning,
                null,
                null,
                "no dashboard is recorded as running");
        }

        // The same lock the replace path takes, so a stop and a launch cannot interleave.
        using IDisposable? stopLock = _tryAcquireLaunchLock(Path.Combine(machineMillerDir, "dashboard.lock"));
        if (stopLock is null)
        {
            return new DashboardStopResult(
                DashboardStopOutcome.Failed,
                recorded.ProcessId,
                recorded.MillerVersion,
                "a dashboard launch holds the launch lock; nothing was stopped");
        }

        // Read again under the lock. The record above is a snapshot, and a launch that replaced the
        // dashboard while this stop waited left a different pid on disk — killing the snapshot's pid
        // would end whatever now wears the number.
        recorded = TryReadMetadata(metadataPath);
        if (recorded is null)
        {
            return new DashboardStopResult(
                DashboardStopOutcome.NotRunning,
                null,
                null,
                "no dashboard is recorded as running");
        }

        if (TryParseBaseUri(recorded) is not { } baseUri)
        {
            DeleteFileIfExists(metadataPath);
            return new DashboardStopResult(
                DashboardStopOutcome.NotRunning,
                recorded.ProcessId,
                recorded.MillerVersion,
                "the recorded dashboard has no usable url; the record was cleared");
        }

        DashboardStopAttempt attempt = StopRecorded(recorded, baseUri, request.StopTimeout);
        if (attempt.Outcome != DashboardStopOutcome.Failed)
            DeleteFileIfExists(metadataPath);

        return new DashboardStopResult(
            attempt.Outcome,
            recorded.ProcessId,
            recorded.MillerVersion,
            attempt.Message);
    }

    /// <summary>
    /// Kills the recorded dashboard, but only after its identity is confirmed: the pid must run a
    /// process that started when the dashboard started. A pid alone proves nothing — the operating
    /// system reuses pids, and the process wearing this one may be anything.
    ///
    /// <para>An unconfirmed identity is never a kill. Whether it is a failure or a plain "not running"
    /// is settled by the health probe: a URL that still answers means a dashboard is up that this
    /// process may not stop, while a silent URL means the recorded dashboard is simply gone.</para>
    /// </summary>
    private DashboardStopAttempt StopRecorded(
        DashboardProcessMetadata recorded,
        Uri baseUri,
        TimeSpan timeout)
    {
        string version = string.IsNullOrWhiteSpace(recorded.MillerVersion) ? "unknown" : recorded.MillerVersion!;

        DashboardProcessProbe? probe = _probeProcess(recorded.ProcessId);
        if (probe is null)
            return Unstoppable(baseUri, $"process {recorded.ProcessId} is not running");
        if (probe.StartedAtUtc is not { } started)
        {
            return Unstoppable(
                baseUri,
                $"the system would not report when process {recorded.ProcessId} started, so it cannot be "
                    + "proven to be the recorded dashboard");
        }

        if (!IsRecordedProcess(started, recorded))
        {
            return Unstoppable(
                baseUri,
                $"process {recorded.ProcessId} did not start when the dashboard did, so it is not the "
                    + "recorded dashboard");
        }

        if (recorded.ProcessStartedAtUtc is null && !_isHealthy(baseUri))
        {
            return new DashboardStopAttempt(
                DashboardStopOutcome.NotRunning,
                $"the record for process {recorded.ProcessId} predates the process-start check and the "
                    + "recorded dashboard url is silent; no dashboard was stopped");
        }

        if (!_killProcess(recorded.ProcessId))
        {
            return new DashboardStopAttempt(
                DashboardStopOutcome.Failed,
                $"process {recorded.ProcessId} refused to stop");
        }

        if (!WaitForStopped(recorded, baseUri, timeout))
        {
            return new DashboardStopAttempt(
                DashboardStopOutcome.Failed,
                $"process {recorded.ProcessId} was signalled but the dashboard is still answering",
                Signalled: true);
        }

        return new DashboardStopAttempt(
            DashboardStopOutcome.Stopped,
            $"stopped the dashboard on {version} (pid {recorded.ProcessId})",
            Signalled: true);
    }

    private DashboardStopAttempt Unstoppable(Uri baseUri, string reason) =>
        _isHealthy(baseUri)
            ? new DashboardStopAttempt(
                DashboardStopOutcome.Failed,
                $"{reason}, and the recorded dashboard url still answers")
            : new DashboardStopAttempt(DashboardStopOutcome.NotRunning, $"{reason}; no dashboard was stopped");

    private bool WaitForStopped(DashboardProcessMetadata recorded, Uri baseUri, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            DashboardProcessProbe? probe = _probeProcess(recorded.ProcessId);
            bool exited = probe?.StartedAtUtc is not { } started || !IsRecordedProcess(started, recorded);
            if (exited && !_isHealthy(baseUri))
                return true;
            if (DateTimeOffset.UtcNow >= deadline)
                return false;
            _sleep(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// Whether the process now wearing the recorded pid is the dashboard the record names.
    ///
    /// <para>The strong proof is the process's OWN start time, stamped into the record at launch and
    /// compared inside the CT lease's tolerance. A record that carries none was written before that
    /// field existed — and those are exactly the stale dashboards this feature replaces, so they keep a
    /// weaker proof: the process must have started when the RECORD was written. That stamp is the
    /// launcher's clock rather than the process's, so it needs a wider window, and the caller pairs it
    /// with a live url before anything is killed.</para>
    /// </summary>
    private static bool IsRecordedProcess(DateTimeOffset started, DashboardProcessMetadata recorded) =>
        recorded.ProcessStartedAtUtc is { } processStarted
            ? (started - processStarted).Duration() <= StartTimeTolerance
            : (started - recorded.StartedAtUtc).Duration() <= UnrecordedStartTolerance;

    private static DashboardProcessProbe? ProbeProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
                return null;
            return new DashboardProcessProbe(new DateTimeOffset(process.StartTime.ToUniversalTime()));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            // Live, but the OS would not report its start time. Identity stays unproven, so the caller
            // reuses rather than killing a process it cannot name.
            return new DashboardProcessProbe(null);
        }
    }

    private static bool KillProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException or AggregateException)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="Signalled"/> records that the kill was issued. A launch cannot report the old
    /// dashboard as still running once that has happened, because the signal cannot be withdrawn.
    /// </summary>
    private sealed record DashboardStopAttempt(
        DashboardStopOutcome Outcome,
        string Message,
        bool Signalled = false);

    /// <summary>
    /// What <c>dashboard.json</c> says right now: the reuse answer when the recorded dashboard stands,
    /// or the verdict that authorizes replacing it. Read once before the launch lock as a fast path and
    /// again under it, because only the reading taken under the lock may be acted on.
    /// </summary>
    private sealed record RecordedDashboard(
        DashboardProcessMetadata? Recorded,
        Uri? BaseUri,
        DashboardVersionDecision? Replace,
        DashboardLaunchResult? Reuse);

    private RecordedDashboard InspectRecorded(string metadataPath, DashboardLaunchRequest request)
    {
        DashboardProcessMetadata? recorded = TryReadMetadata(metadataPath);
        if (TryParseBaseUri(recorded) is not { } recordedBaseUri || !_isHealthy(recordedBaseUri))
            return new RecordedDashboard(null, null, null, null);

        DashboardVersionDecision? decision = request.OwnVersion is { Length: > 0 } ownVersion
            ? DashboardVersionDecision.For(ownVersion, recorded!.MillerVersion)
            : null;
        if (decision is { MayReplace: true })
            return new RecordedDashboard(recorded, recordedBaseUri, decision, Reuse: null);

        return new RecordedDashboard(
            recorded,
            recordedBaseUri,
            Replace: null,
            new DashboardLaunchResult(
                DashboardLaunchOutcome.AlreadyRunning,
                LaunchUrl(recordedBaseUri, request),
                null,
                decision is { Mismatch: true } ? $"already running; {decision.Reason}" : "already running"));
    }

    private bool WaitForHealthy(Uri baseUri, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            if (_isHealthy(baseUri))
                return true;
            _sleep(TimeSpan.FromMilliseconds(100));
        }
        while (DateTimeOffset.UtcNow < deadline);
        return false;
    }

    private static int ResolveStartedProcessId(Process process, string pidPath, DateTimeOffset launchStartedUtc)
    {
        if (!OperatingSystem.IsWindows())
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
            do
            {
                if (TryReadProcessId(pidPath, launchStartedUtc) is { } pid)
                    return pid;
                Thread.Sleep(TimeSpan.FromMilliseconds(20));
            }
            while (DateTimeOffset.UtcNow < deadline);
        }

        return process.Id;
    }

    private static int? TryReadProcessId(string pidPath, DateTimeOffset launchStartedUtc)
    {
        try
        {
            if (!File.Exists(pidPath))
                return null;
            if (File.GetLastWriteTimeUtc(pidPath) < launchStartedUtc.UtcDateTime.AddMilliseconds(-100))
                return null;
            string text = File.ReadAllText(pidPath).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)
                ? pid
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The freshness check in TryReadProcessId prevents stale pid reuse if deletion fails.
        }
    }

    internal static Uri BaseUri(int port)
    {
        if (port is < 1 or > 65535)
            port = DefaultPort;
        return new Uri($"http://127.0.0.1:{port}/");
    }

    internal static Uri LaunchUrl(Uri baseUri, DashboardLaunchRequest request) =>
        request.OpenWorkspaceView ? WorkspaceUrl(baseUri, request.Context) : baseUri;

    internal static Uri WorkspaceUrl(Uri baseUri, WorkspaceContext context)
    {
        string workspaceId = ResolveWorkspaceId(context);
        var builder = new UriBuilder(baseUri)
        {
            Path = "/workspace",
            Query = "workspace_id=" + Uri.EscapeDataString(workspaceId),
        };
        return builder.Uri;
    }

    internal static string ResolveWorkspaceId(WorkspaceContext context)
    {
        try
        {
            if (File.Exists(context.RegistryDbPath))
            {
                using WorkspaceRegistry registry = WorkspaceRegistry.Open(context.RegistryDbPath);
                WorkspaceRegistryRow? row = registry.List()
                    .FirstOrDefault(r => WorkspaceSafety.IsLiveWorkspace(r.CanonicalRoot, context.WorkspaceRoot));
                if (row is not null)
                    return row.WorkspaceId;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Fall through to deterministic id derivation; the dashboard can still open and show the selector URL.
        }

        string root = Directory.Exists(context.WorkspaceRoot)
            ? PathCanonicalizer.CanonicalizeRoot(context.WorkspaceRoot)
            : Path.GetFullPath(context.WorkspaceRoot);
        return WorkspaceId.FromCanonicalRoot(root);
    }

    private static bool IsHealthy(Uri baseUri)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(500),
        };
        try
        {
            Uri health = new(baseUri, "healthz");
            using HttpResponseMessage response = client.GetAsync(health).GetAwaiter().GetResult();
            if (response.StatusCode != HttpStatusCode.OK)
                return false;
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Trim().StartsWith(HealthBody, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }

    private static IDisposable? TryAcquireLaunchLock(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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

    private static DashboardCommand? ResolveDashboardCommand(WorkspaceContext context)
    {
        if (Environment.GetEnvironmentVariable("MILLER_DASHBOARD_DLL") is { Length: > 0 } configured)
        {
            string full = Path.GetFullPath(configured);
            if (File.Exists(full))
                return DashboardCommand.ForPath(full);
        }

        string appBase = Path.GetFullPath(Path.Combine(context.ToolsRoot, ".."));
        foreach (string packaged in PackagedDashboardCandidates(appBase))
        {
            if (File.Exists(packaged))
                return DashboardCommand.ForPath(packaged);
        }

        string sourceSiblingDll = Path.GetFullPath(Path.Combine(
            appBase,
            "..",
            "..",
            "..",
            "..",
            "Miller.Dashboard",
            "bin",
            "Release",
            "net10.0",
            "Miller.Dashboard.dll"));
        if (File.Exists(sourceSiblingDll))
            return DashboardCommand.ForPath(sourceSiblingDll);

        return null;
    }

    private static IEnumerable<string> PackagedDashboardCandidates(string appBase)
    {
        string packagedDir = Path.Combine(appBase, "dashboard");
        if (OperatingSystem.IsWindows())
            yield return Path.Combine(packagedDir, "Miller.Dashboard.exe");
        else
            yield return Path.Combine(packagedDir, "Miller.Dashboard");
        yield return Path.Combine(packagedDir, "Miller.Dashboard.dll");
        yield return Path.Combine(appBase, "Miller.Dashboard.dll");
    }

    private static void WriteMetadata(string path, DashboardProcessMetadata metadata)
    {
        string json = ServerJson.Serialize(metadata);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Reads <c>dashboard.json</c>. A record written before the version field existed carries no
    /// <see cref="DashboardProcessMetadata.MillerVersion"/> and must still parse — it deserializes with
    /// a null version, which the version decision reads as a build older than any recorded one.
    /// </summary>
    private static DashboardProcessMetadata? TryReadMetadata(string metadataPath)
    {
        try
        {
            if (!File.Exists(metadataPath))
                return null;

            return JsonSerializer.Deserialize(
                File.ReadAllText(metadataPath),
                DashboardMetadataJsonContext.Default.DashboardProcessMetadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static Uri? TryParseBaseUri(DashboardProcessMetadata? metadata) =>
        metadata is not null && Uri.TryCreate(metadata.Url, UriKind.Absolute, out Uri? uri) ? uri : null;

    private sealed record DashboardCommand(string FileName, string? Argument)
    {
        /// <summary>
        /// The dashboard's stdout log. One pair of names for both platforms: Unix redirects into them from
        /// its launch script, Windows hands the same files to the child as its standard handles.
        /// </summary>
        public static string StdoutLogPath(string machineMillerDir) =>
            Path.Combine(machineMillerDir, "dashboard.out.log");

        public static string StderrLogPath(string machineMillerDir) =>
            Path.Combine(machineMillerDir, "dashboard.err.log");

        public static DashboardCommand ForPath(string path)
        {
            if (string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
                return new DashboardCommand("dotnet", path);
            return new DashboardCommand(path, null);
        }

        public ProcessStartInfo ToStartInfo(
            WorkspaceContext context,
            int port,
            string machineMillerDir,
            string pidPath)
        {
            Directory.CreateDirectory(machineMillerDir);

            ProcessStartInfo startInfo = OperatingSystem.IsWindows()
                ? DirectStartInfo(machineMillerDir)
                : UnixDetachedStartInfo(machineMillerDir, pidPath);

            SetCommonEnvironment(startInfo, context, port);
            return startInfo;
        }

        [SupportedOSPlatform("windows")]
        private ProcessStartInfo DirectStartInfo(string machineMillerDir)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FileName,
                WorkingDirectory = machineMillerDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                CreateNewProcessGroup = true,
            };
            if (Argument is not null)
                startInfo.ArgumentList.Add(Argument);
            return startInfo;
        }

        // Detach the dashboard on Unix with POSIX /bin/sh + nohup — present on every macOS/Linux host, unlike
        // python3 (a minimal Linux image may lack it, and invoking `python3` on macOS can trigger an Xcode
        // Command-Line-Tools install prompt). sh backgrounds nohup and exits, so the dashboard is reparented to
        // init/launchd and ignores SIGHUP, outliving the launching miller; stdin is detached and stdout/stderr
        // append to the machine log files. nohup exec-replaces itself, so the recorded $! is the dashboard's pid.
        private ProcessStartInfo UnixDetachedStartInfo(string machineMillerDir, string pidPath)
        {
            string stdoutPath = StdoutLogPath(machineMillerDir);
            string stderrPath = StderrLogPath(machineMillerDir);
            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                WorkingDirectory = machineMillerDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(UnixLaunchScript);
            startInfo.ArgumentList.Add("sh");                     // $0
            startInfo.ArgumentList.Add(FileName);                 // $1: the dashboard exe (or "dotnet")
            startInfo.ArgumentList.Add(Argument ?? string.Empty); // $2: the dll arg, or empty
            startInfo.ArgumentList.Add(pidPath);                  // $3
            startInfo.ArgumentList.Add(stdoutPath);               // $4
            startInfo.ArgumentList.Add(stderrPath);               // $5
            startInfo.Environment["MILLER_DASHBOARD_PID_FILE"] = pidPath;
            return startInfo;
        }

        private static void SetCommonEnvironment(ProcessStartInfo startInfo, WorkspaceContext context, int port)
        {
            startInfo.Environment["MILLER_DASHBOARD_PORT"] = port.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["MILLER_REGISTRY_DB"] = context.RegistryDbPath;
            startInfo.Environment["MILLER_TELEMETRY_DB"] = context.TelemetryDbPath;
            startInfo.Environment["MILLER_TOOLS_ROOT"] = context.ToolsRoot;
            startInfo.Environment["MILLER_DASHBOARD_PREFERRED_ROOT"] = context.WorkspaceRoot;
        }

        private const string UnixLaunchScript = """
exe="$1"
arg="$2"
pid_path="$3"
stdout_path="$4"
stderr_path="$5"
if [ -n "$arg" ]; then
  nohup "$exe" "$arg" >>"$stdout_path" 2>>"$stderr_path" </dev/null &
else
  nohup "$exe" >>"$stdout_path" 2>>"$stderr_path" </dev/null &
fi
printf '%s' "$!" > "$pid_path"
""";
    }
}

/// <summary>
/// What <c>~/.miller/dashboard.json</c> records about the live dashboard.
/// <paramref name="MillerVersion"/> is nullable because records written before the version check
/// existed have no such field, and those are exactly the stale dashboards the check must replace.
///
/// <para><paramref name="StartedAtUtc"/> is when the RECORD was written; <paramref name="ProcessStartedAtUtc"/>
/// is when the dashboard process itself started, which is the stamp that proves the pid has not been
/// recycled onto something else. It is nullable for the same reason the version is, plus one more: a
/// system that will not report a process's start time gives the launcher nothing to record.</para>
/// </summary>
internal sealed record DashboardProcessMetadata(
    int ProcessId,
    string Url,
    DateTimeOffset StartedAtUtc,
    string? MillerVersion = null,
    DateTimeOffset? ProcessStartedAtUtc = null);
