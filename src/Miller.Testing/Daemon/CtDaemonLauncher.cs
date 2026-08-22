using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Miller.Indexing;

namespace Miller.Testing;

public enum CtDaemonSpawnStatus
{
    Started,
    AlreadyRunning,

    /// <summary>
    /// A live daemon on a different build was stopped and this build started in its place. A third
    /// outcome on purpose: a caller must not read it as a fresh start, and must not read it as
    /// nothing-happened.
    /// </summary>
    Replaced,
    Failed,
    Refused,
}

public enum CtDaemonPublicationReadiness
{
    Ready,
    NotPublishedWithinGrace,
    DaemonExitedBeforePublish,
}

public sealed record CtDaemonPublicationResult(
    CtDaemonPublicationReadiness Readiness,
    TimeSpan Elapsed);

public sealed class CtDaemonPublicationProbe
{
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(25);

    public Func<string, CtDaemonLeaseRecord?>? ReadLease { get; init; }

    public Func<string, CtDaemonStatusRecord?>? ReadStatus { get; init; }

    public Func<int, bool>? IsProcessLive { get; init; }

    public TimeProvider? Clock { get; init; }

    public Action<TimeSpan>? Delay { get; init; }
}

public sealed record CtDaemonSpawnResult(
    CtDaemonSpawnStatus Status,
    int? ProcessId,
    string? Executable,
    string? Reason,
    CtDaemonPublicationResult? Publication = null);

public enum CtRunExecution
{
    ForegroundOneShot,
    Daemon,
}

/// <summary>
/// <paramref name="EndpointRoot"/> is a trailing optional so existing constructions keep compiling.
/// When the disposition is <see cref="CtRunExecution.Daemon"/>, it names the root whose control
/// plane carries the command files - the workspace's own root, or the repo's main checkout when
/// the FAMILY daemon serves this worktree by adoption.
/// </summary>
public sealed record CtRunDisposition(
    CtRunExecution Execution,
    CtDaemonLeaseRecord? Lease,
    string? EndpointRoot = null);

/// <summary>
/// Starts at most one detached CT daemon per workspace. A <c>run</c> with no live daemon is a
/// foreground one-shot in the calling process — this type exposes that disposition for Task 12
/// and does not start the engine.
/// </summary>
public static class CtDaemonLauncher
{
    public const string DaemonVerb = "ct-daemon";

    public static CtRunDisposition ResolveRun(string workspaceRoot)
    {
        CtDaemonEndpoint? endpoint = CtDaemonRouting.ResolveLiveEndpoint(workspaceRoot);
        return endpoint is null
            ? new CtRunDisposition(CtRunExecution.ForegroundOneShot, null)
            : new CtRunDisposition(CtRunExecution.Daemon, endpoint.Lease, endpoint.EndpointRoot);
    }

    /// <summary>
    /// Where <c>tests serve</c> anchors the daemon for this root. A linked worktree whose main
    /// checkout is opted in starts the FAMILY daemon on the main checkout - one daemon adopts every
    /// family worktree, so a worktree start must not mint a second, sibling-blind daemon. Everything
    /// else (a main checkout, a non-git root, a worktree whose main never opted in) serves itself.
    /// </summary>
    public static string ResolveSpawnRoot(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        string root = Path.GetFullPath(workspaceRoot);
        GitWorktreeLayout? layout = GitWorktreeLayout.Resolve(root);
        return layout is { IsLinkedWorktree: true, MainCheckoutRoot: { } main }
            && ContinuousTestPolicy.IsWorkspaceOptedIn(main)
            ? Path.GetFullPath(main)
            : root;
    }

    public static string ResolveCurrentExecutable()
    {
        if (Environment.ProcessPath is { Length: > 0 } processPath && File.Exists(processPath))
            return Path.GetFullPath(processPath);

        using var process = Process.GetCurrentProcess();
        string? module = process.MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(module) && File.Exists(module))
            return Path.GetFullPath(module);

        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 0 && File.Exists(args[0]))
            return Path.GetFullPath(args[0]);

        throw new InvalidOperationException("Cannot resolve the current executable for the CT daemon.");
    }

    /// <summary>
    /// Starts the daemon, or reports why it did not.
    ///
    /// <para><paramref name="ownVersion"/> turns on the version check. Left null, a live daemon always
    /// answers <see cref="CtDaemonSpawnStatus.AlreadyRunning"/> — the behaviour every caller had before
    /// the check existed. Supplied, a live daemon running a build this one can prove is older, or the
    /// same release from a different commit, is STOPPED and replaced: an explicit start is the user
    /// asking for this binary, and until now an upgraded Miller answered exit 0 and left the old daemon
    /// watching the tree with old code. A NEWER daemon is never replaced by an older build, and an
    /// unorderable pair is left alone.</para>
    ///
    /// <para><paramref name="stopDaemon"/> is the seam for that stop. A test holding a real lease holds
    /// it as its OWN process, so the real stop would kill the test run.</para>
    ///
    /// <para><paramref name="resolveImage"/> is the seam for the private per-build copy the daemon runs
    /// from (<see cref="CtDaemonShadowCopy"/>). It is a seam because the real one copies the whole
    /// output directory, which no fast test may do; production leaves it null.</para>
    /// </summary>
    public static CtDaemonSpawnResult SpawnDetached(
        string workspaceRoot,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        string? ownVersion = null,
        Func<string, CtDaemonStopResult>? stopDaemon = null,
        Func<string, string?, CtDaemonImage>? resolveImage = null,
        CtDaemonPublicationProbe? publication = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        RejectSensitiveRoot(workspaceRoot);

        string root = Path.GetFullPath(workspaceRoot);
        string? replacedVersion = null;
        if (CtDaemonLease.TryReadLive(root) is { } live)
        {
            if (string.IsNullOrWhiteSpace(ownVersion))
            {
                return new CtDaemonSpawnResult(
                    CtDaemonSpawnStatus.AlreadyRunning,
                    live.Identity.Pid,
                    Executable: null,
                    "daemon already running");
            }

            CtDaemonVersionVerdict verdict = CtDaemonVersion.Evaluate(ownVersion, live.MillerVersion);
            if (!verdict.MayReplace)
            {
                return new CtDaemonSpawnResult(
                    CtDaemonSpawnStatus.AlreadyRunning,
                    live.Identity.Pid,
                    Executable: null,
                    verdict.Match == CtDaemonVersionMatch.Same
                        ? "daemon already running"
                        : $"daemon already running; {verdict.Reason}");
            }

            CtDaemonStopResult stopped = (stopDaemon ?? (r => CtCommandChannel.Stop(r)))(root);
            if (stopped.Status == CtDaemonStopStatus.Failed)
            {
                return new CtDaemonSpawnResult(
                    CtDaemonSpawnStatus.Failed,
                    live.Identity.Pid,
                    Executable: null,
                    $"cannot replace the daemon: {stopped.Reason}");
            }

            replacedVersion = live.MillerVersion;
        }

        // The daemon runs from a PRIVATE per-build copy, never from the install or the build output:
        // a live Windows process locks its own image and every DLL it loaded, which is how a running
        // daemon broke `dotnet build` with MSB3027 and blocked a plugin upgrade from overwriting the
        // installed binary. A copy that cannot be made falls back to the in-place spawn and SAYS SO,
        // because a daemon that starts and locks the install is still better than no daemon at all.
        //
        // The default follows the STARTER: an injected starter decides for itself what process runs
        // and usually ignores this path entirely, so copying the whole output directory for it would
        // be waste in production and an 83 MB tree copy inside every test that fakes a spawn.
        Func<string, string?, CtDaemonImage> imageResolver = resolveImage
            ?? (startProcess is null ? CtDaemonShadowCopy.Resolve : CtDaemonShadowCopy.InPlace);
        CtDaemonImage image = imageResolver(ResolveCurrentExecutable(), ownVersion);
        string executable = image.Executable;
        (string stdoutPath, string stderrPath) = PrepareDaemonLogPaths(root);
        ProcessStartInfo startInfo = BuildStartInfo(executable, root, stdoutPath, stderrPath);
        Func<ProcessStartInfo, Process?> starter = startProcess ?? Process.Start;

        Process? process;
        try
        {
            // Both branches hand the daemon the two log FILES as its own stdout and stderr. Windows needs
            // the extra step because it has no /bin/sh to redirect through; see DetachedProcessStreams.
            process = DetachedProcessStreams.Start(startInfo, stdoutPath, stderrPath, starter);
        }
        // Win32Exception is the shape a real spawn failure takes (missing image, denied by policy). Without it
        // the exception escaped to the CLI catch-all and reported exit 1 "unexpected", while tests-cli-v1 calls
        // a failed spawn an operational refusal (3) — the same code every other Failed result already produces.
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new CtDaemonSpawnResult(CtDaemonSpawnStatus.Failed, null, executable, ex.Message);
        }

        if (process is null)
        {
            return new CtDaemonSpawnResult(
                CtDaemonSpawnStatus.Failed,
                null,
                executable,
                "daemon process did not start");
        }

        ReleaseDaemonStandardInput(process);
        int? pid = TryReadProcessId(process);
        string baseReason = replacedVersion is null
            ? "started"
            : $"replaced the daemon on {replacedVersion}";
        CtDaemonPublicationProbe probe = publication ?? new CtDaemonPublicationProbe();
        CtDaemonPublicationResult published = ObservePublication(root, pid, probe);
        return new CtDaemonSpawnResult(
            replacedVersion is null ? CtDaemonSpawnStatus.Started : CtDaemonSpawnStatus.Replaced,
            pid,
            executable,
            AppendInPlaceWarning(baseReason, image),
            published);
    }

    private static CtDaemonPublicationResult ObservePublication(
        string workspaceRoot,
        int? processId,
        CtDaemonPublicationProbe options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Grace < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.Grace));
        if (options.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.PollInterval));

        Func<string, CtDaemonLeaseRecord?> readLease =
            options.ReadLease ?? (root => CtDaemonLease.TryReadLive(root));
        Func<string, CtDaemonStatusRecord?> readStatus =
            options.ReadStatus ?? (root => CtDaemonLease.TryReadStatus(root));
        Func<int, bool> isProcessLive = options.IsProcessLive ?? IsProcessLive;
        TimeProvider clock = options.Clock ?? TimeProvider.System;
        Action<TimeSpan> delay = options.Delay ?? Delay;
        long started = clock.GetTimestamp();

        while (true)
        {
            CtDaemonLeaseRecord? lease = readLease(workspaceRoot);
            CtDaemonStatusRecord? status = readStatus(workspaceRoot);
            if (IsPublished(lease, status))
                return new CtDaemonPublicationResult(
                    CtDaemonPublicationReadiness.Ready,
                    clock.GetElapsedTime(started));

            if (processId is { } pid && !isProcessLive(pid))
                return new CtDaemonPublicationResult(
                    CtDaemonPublicationReadiness.DaemonExitedBeforePublish,
                    clock.GetElapsedTime(started));

            TimeSpan elapsed = clock.GetElapsedTime(started);
            if (elapsed >= options.Grace)
                return new CtDaemonPublicationResult(
                    CtDaemonPublicationReadiness.NotPublishedWithinGrace,
                    elapsed);

            TimeSpan remaining = options.Grace - elapsed;
            TimeSpan wait = remaining < options.PollInterval ? remaining : options.PollInterval;
            if (wait > TimeSpan.Zero)
                delay(wait);
            else
                Thread.Yield();
        }
    }

    private static bool IsPublished(CtDaemonLeaseRecord? lease, CtDaemonStatusRecord? status) =>
        lease is not null
        && status is not null
        && status.State is CtDaemonLifecycleState.Running or CtDaemonLifecycleState.Paused
        && status.Identity == lease.Identity;

    private static bool IsProcessLive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return false;
        }
    }

    private static void Delay(TimeSpan duration) => Thread.Sleep(duration);

    /// <summary>
    /// The reachable error path for a daemon that had to start from the install directory. MSBuild
    /// cannot test a file lock without spawning a process on every build, so the guidance rides the
    /// one message the user does see: this daemon will hold the install open until it is stopped.
    /// </summary>
    private static string AppendInPlaceWarning(string reason, CtDaemonImage image) =>
        image.IsShadowCopy
            ? reason
            : $"{reason} (running in place: {image.Reason ?? "no private copy"}; this daemon holds "
                + "the install directory open — run `miller tests stop` before you rebuild or upgrade)";

    internal static void RejectSensitiveRoot(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!IsSensitiveRoot(workspaceRoot))
            return;

        string full = Path.GetFullPath(workspaceRoot);
        throw new InvalidOperationException(
            $"Refusing to use sensitive system path '{full}' as a CT daemon workspace root.");
    }

    internal static bool IsSensitiveRoot(string candidate) =>
        IsSensitiveRoot(candidate, SensitiveRootCandidates());

    internal static bool IsSensitiveRoot(string candidate, IReadOnlyCollection<string> forbidden)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        ArgumentNullException.ThrowIfNull(forbidden);

        string normCandidate = Normalize(candidate);
        if (Path.GetDirectoryName(normCandidate) is null)
            return true;

        foreach (string entry in forbidden)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            if (string.Equals(normCandidate, Normalize(entry), PathComparison))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Both daemon log files, with their directory created. The daemon writes its own diagnostics nowhere
    /// else: <c>ct-daemon</c> is a CLI verb, and the CLI path starts no Serilog file sink.
    /// </summary>
    private static (string StdoutPath, string StderrPath) PrepareDaemonLogPaths(string workspaceRoot)
    {
        string ctDir = CtDaemonProtocol.RootDirectory(workspaceRoot);
        Directory.CreateDirectory(ctDir);
        return (
            Path.Combine(ctDir, "daemon.out.log"),
            Path.Combine(ctDir, "daemon.err.log"));
    }

    private static ProcessStartInfo BuildStartInfo(
        string executable, string workspaceRoot, string stdoutPath, string stderrPath)
    {
        bool isDll = string.Equals(Path.GetExtension(executable), ".dll", StringComparison.OrdinalIgnoreCase);
        string fileName = isDll ? "dotnet" : executable;
        string? dllArgument = isDll ? executable : null;

        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? WindowsStartInfo(fileName, dllArgument)
            : UnixDetachedStartInfo(fileName, dllArgument, stdoutPath, stderrPath);
        startInfo.Environment[CtEnvironment.DaemonWorkspaceRoot] = workspaceRoot;

        // The PROVIDER-facing variable must not survive into the daemon. `tests serve` can be run from
        // inside a CT test process, which carries the workspace under test, and a daemon that inherited it
        // would bind the wrong root. ResolveDaemonWorkspaceRoot already refuses to READ it; removing it here
        // means the daemon's own children cannot see a stale one either.
        startInfo.Environment.Remove(CtEnvironment.WorkspaceRoot);

        // A live process holds its working directory open, and on Windows that handle refuses a rename or a
        // delete of the directory — so a daemon started in the workspace root pins the very tree Miller
        // indexes for its whole life. The daemon never needs that root as its cwd: DaemonWorkspaceRoot above
        // carries it explicitly and beats the CLI context, and every provider command names its own
        // WorkingDirectory. Both branches use the same directory so the caller sees one behaviour.
        string workingDirectory = ResolveDaemonWorkingDirectory(workspaceRoot, DaemonWorkingDirectoryCandidates());
        Directory.CreateDirectory(workingDirectory);
        startInfo.WorkingDirectory = workingDirectory;
        return startInfo;
    }

    // stdout and stderr are deliberately NOT redirected. .NET only builds pipes for the streams the caller
    // asks for; for the others it passes the launcher's CURRENT standard handles to CreateProcess under
    // STARTF_USESTDHANDLES. DetachedProcessStreams swaps those two handles to the log files for the
    // length of the spawn, so the daemon is born writing straight into daemon.out.log / daemon.err.log.
    //
    // Redirecting them instead would put the capture in a pipe that only the LAUNCHER can drain, which is
    // the defect this shape replaces: `miller tests serve` prints one line and exits about a millisecond
    // later, long before the daemon has finished starting, and a launcher-side pump dies with it.
    //
    // Asking for stdin alone is what keeps the other two on the swapped handles AND mirrors the Unix
    // `</dev/null`: the launcher closes the write end at once, so the daemon reads EOF instead of stealing
    // bytes from the launcher's own stdin (an MCP server's stdin is its protocol channel).
    [SupportedOSPlatform("windows")]
    private static ProcessStartInfo WindowsStartInfo(string fileName, string? dllArgument)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            CreateNewProcessGroup = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        if (dllArgument is not null)
            startInfo.ArgumentList.Add(dllArgument);
        startInfo.ArgumentList.Add(DaemonVerb);
        return startInfo;
    }

    private static ProcessStartInfo UnixDetachedStartInfo(
        string fileName, string? dllArgument, string stdoutPath, string stderrPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(UnixLaunchScript);
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add(fileName);
        startInfo.ArgumentList.Add(dllArgument ?? string.Empty);
        startInfo.ArgumentList.Add(DaemonVerb);
        startInfo.ArgumentList.Add(stdoutPath);
        startInfo.ArgumentList.Add(stderrPath);
        return startInfo;
    }

    /// <summary>
    /// Closes the launcher's end of the daemon's stdin pipe so the daemon reads EOF, exactly as the Unix
    /// branch's <c>&lt;/dev/null</c> does. An injected test starter may have started its process with no
    /// redirection at all, which shows up here as "no stream", so a missing stream is tolerated.
    /// </summary>
    private static void ReleaseDaemonStandardInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        // ObjectDisposedException is an InvalidOperationException, so both shapes land here.
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }
    }

    /// <summary>
    /// The first candidate directory that is not inside the workspace. The last candidate is the user
    /// profile, which no legal workspace root can contain: <c>WorkspaceRootSafety</c> already refuses the
    /// home directory and every drive root, so no allowed root is an ancestor of the profile.
    /// </summary>
    internal static string ResolveDaemonWorkingDirectory(string workspaceRoot, IReadOnlyList<string> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(candidates);

        string root = Normalize(workspaceRoot);
        string? last = null;
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string full = Path.GetFullPath(candidate);
            last = full;
            if (!IsInside(Normalize(full), root))
                return full;
        }

        return last ?? Path.GetTempPath();
    }

    private static IReadOnlyList<string> DaemonWorkingDirectoryCandidates() =>
        [
            // Same choice the dashboard launcher makes for its own detached process.
            MillerHome.ResolveMillerDirectory(),
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ];

    private static bool IsInside(string normalizedCandidate, string normalizedRoot)
    {
        if (string.Equals(normalizedCandidate, normalizedRoot, PathComparison))
            return true;

        string prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, PathComparison);
    }

    private static int? TryReadProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> SensitiveRootCandidates()
    {
        var forbidden = new List<string>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            forbidden.Add(home);

        if (OperatingSystem.IsMacOS())
        {
            forbidden.Add("/Users");
            forbidden.Add("/var/root");
            forbidden.Add("/private/var/root");
        }
        else if (OperatingSystem.IsLinux())
        {
            forbidden.Add("/home");
            forbidden.Add("/root");
        }
        else if (OperatingSystem.IsWindows())
        {
            string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") is { Length: > 0 } sd
                ? sd
                : "C:";
            string driveRoot = systemDrive.TrimEnd('\\') + "\\";
            forbidden.Add(driveRoot + "Users");
            forbidden.Add(driveRoot + "Windows");
            forbidden.Add(driveRoot + "Windows\\System32");
            forbidden.Add(driveRoot + "Program Files");
            forbidden.Add(driveRoot + "Program Files (x86)");
            forbidden.Add(driveRoot + "ProgramData");
            foreach (string key in new[]
                     {
                         "SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData", "PUBLIC",
                     })
            {
                if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value)
                    forbidden.Add(value);
            }
        }

        return forbidden;
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private const string UnixLaunchScript = """
exe="$1"
arg="$2"
verb="$3"
stdout_path="$4"
stderr_path="$5"
if [ -n "$arg" ]; then
  exec nohup "$exe" "$arg" "$verb" >>"$stdout_path" 2>>"$stderr_path" </dev/null
else
  exec nohup "$exe" "$verb" >>"$stdout_path" 2>>"$stderr_path" </dev/null
fi
""";
}
