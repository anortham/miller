using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

[Trait("Category", "Scale")]
public sealed class CtDaemonLauncherTests : IDisposable
{
    private readonly string _root;

    public CtDaemonLauncherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miller-ct-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ResolveCurrentExecutable_ReturnsAnExistingPath()
    {
        string path = CtDaemonLauncher.ResolveCurrentExecutable();
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SpawnDetached_RefusesSensitiveRoot_BeforeCreatingControlPlane()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(home));
        Assert.True(WorkspaceRootSafety.IsSensitiveRoot(home, WorkspaceRootSafety.SensitiveRootCandidates()));

        string ctDir = Path.Combine(home, ".miller", "ct");
        bool existed = Directory.Exists(ctDir);
        bool started = false;

        var error = Assert.Throws<InvalidOperationException>(
            () => CtDaemonLauncher.SpawnDetached(home, startProcess: _ =>
            {
                started = true;
                return null;
            }));

        Assert.Contains(home, error.Message, StringComparison.Ordinal);
        Assert.False(started);
        if (!existed)
            Assert.False(Directory.Exists(ctDir));
    }

    [Fact]
    public void SpawnDetached_RefusesFilesystemRoot()
    {
        string driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.True(WorkspaceRootSafety.IsSensitiveRoot(driveRoot, WorkspaceRootSafety.SensitiveRootCandidates()));

        Assert.Throws<InvalidOperationException>(
            () => CtDaemonLauncher.SpawnDetached(driveRoot, startProcess: _ =>
                throw new InvalidOperationException("must not spawn")));
    }

    [Fact]
    public void SpawnDetached_WhenLiveLeaseHeld_DoesNotStartProcess()
    {
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.20.0-test");
        Assert.NotNull(lease);
        bool started = false;

        CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(_root, startProcess: _ =>
        {
            started = true;
            return null;
        });

        Assert.Equal(CtDaemonSpawnStatus.AlreadyRunning, result.Status);
        Assert.False(started);
        Assert.Equal(lease.Record.Identity.Pid, result.ProcessId);
    }

    /// <summary>
    /// An explicit start from a build the live daemon is not running replaces it. Until this landed,
    /// an upgraded Miller answered exit 0 and left the old daemon watching the tree with old code.
    ///
    /// <para><c>stopDaemon</c> is a seam, not a convenience: the lease these tests hold is held by the
    /// xUnit process itself, so the real stop would kill the test run.</para>
    /// </summary>
    [Fact]
    public void SpawnDetached_WhenTheLiveLeaseRunsAnOlderBuild_ReplacesIt()
    {
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.9.0+aaa");
        Assert.NotNull(lease);
        var stopped = new List<string>();
        bool started = false;
        using Process holder = StartStub();
        try
        {
            CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(
                _root,
                startProcess: _ =>
                {
                    started = true;
                    return holder;
                },
                ownVersion: "1.13.0+bbb",
                stopDaemon: root =>
                {
                    stopped.Add(root);
                    return new CtDaemonStopResult(CtDaemonStopStatus.Stopped, "stopped");
                },
                publication: NoWaitPublication());

            Assert.Equal(CtDaemonSpawnStatus.Replaced, result.Status);
            Assert.Single(stopped);
            Assert.True(started, "the replacement daemon was never started");
            Assert.Contains("1.9.0+aaa", result.Reason, StringComparison.Ordinal);
        }
        finally
        {
            if (!holder.HasExited)
            {
                holder.Kill(entireProcessTree: true);
                holder.WaitForExit(2000);
            }
        }
    }

    /// <summary>
    /// Direction is numeric, not textual. As text "1.13.0" sorts BELOW "1.9.0", so a text comparison
    /// would call the newer daemon older and stop it.
    /// </summary>
    [Fact]
    public void SpawnDetached_WhenTheLiveLeaseRunsANewerBuild_DoesNotStart()
    {
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.13.0+bbb");
        Assert.NotNull(lease);
        var stopped = new List<string>();
        bool started = false;

        CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(
            _root,
            startProcess: _ =>
            {
                started = true;
                return null;
            },
            ownVersion: "1.9.0+aaa",
            stopDaemon: root =>
            {
                stopped.Add(root);
                return new CtDaemonStopResult(CtDaemonStopStatus.Stopped, "stopped");
            });

        Assert.Equal(CtDaemonSpawnStatus.AlreadyRunning, result.Status);
        Assert.Empty(stopped);
        Assert.False(started);
        Assert.Contains("1.13.0+bbb", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>Concurrent agents run one build, so nothing contends and the reason stays unchanged.</summary>
    [Fact]
    public void SpawnDetached_WhenTheLiveLeaseRunsThisBuild_DoesNotStart()
    {
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.13.0+bbb");
        Assert.NotNull(lease);
        var stopped = new List<string>();

        CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(
            _root,
            startProcess: _ => throw new InvalidOperationException("must not spawn"),
            ownVersion: "1.13.0+bbb",
            stopDaemon: root =>
            {
                stopped.Add(root);
                return new CtDaemonStopResult(CtDaemonStopStatus.Stopped, "stopped");
            });

        Assert.Equal(CtDaemonSpawnStatus.AlreadyRunning, result.Status);
        Assert.Equal("daemon already running", result.Reason);
        Assert.Empty(stopped);
    }

    /// <summary>
    /// A replace that cannot stop the old daemon must not start a second one beside it. Two daemons
    /// on one root is worse than the stale daemon this was trying to fix.
    /// </summary>
    [Fact]
    public void SpawnDetached_WhenTheReplaceStopFails_ReportsFailedAndStartsNothing()
    {
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.9.0+aaa");
        Assert.NotNull(lease);
        bool started = false;

        CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(
            _root,
            startProcess: _ =>
            {
                started = true;
                return null;
            },
            ownVersion: "1.13.0+bbb",
            stopDaemon: _ => new CtDaemonStopResult(CtDaemonStopStatus.Failed, "process still live"));

        Assert.Equal(CtDaemonSpawnStatus.Failed, result.Status);
        Assert.False(started);
        Assert.Contains("process still live", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnDetached_ResolvesExecutableAndSetsWorkspaceRoot()
    {
        ProcessStartInfo? captured = null;
        using Process holder = StartStub();
        try
        {
            CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(_root, startProcess: info =>
            {
                captured = info;
                return holder;
            }, publication: NoWaitPublication());

            Assert.Equal(CtDaemonSpawnStatus.Started, result.Status);
            Assert.NotNull(captured);
            Assert.False(string.IsNullOrWhiteSpace(result.Executable));
            Assert.True(File.Exists(result.Executable));
            Assert.Equal(holder.Id, result.ProcessId);
            Assert.Equal(_root, captured.Environment[CtEnvironment.DaemonWorkspaceRoot]);
            // The verb stays its OWN argument on every platform. TestsCliTests and TestsToolTests read the
            // spawn argv as a list, so a launcher that folded the argv into one shell string would pass here
            // and break them instead.
            Assert.Contains(CtDaemonLauncher.DaemonVerb, captured.ArgumentList);
            Assert.False(
                captured.Environment.ContainsKey(CtEnvironment.WorkspaceRoot),
                "the daemon spawn must not use the provider-facing workspace variable: " +
                "test processes under CT inherit it, and a CLI verb run inside such a test " +
                "would bind the real workspace instead of the test's own root");
            Assert.Contains(CtDaemonLauncher.DaemonVerb, StartArguments(captured));
            Assert.False(captured.UseShellExecute);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(captured.CreateNewProcessGroup);
                Assert.Equal(result.Executable, captured.FileName);
            }
            else
            {
                Assert.Equal("/bin/sh", captured.FileName);
            }
        }
        finally
        {
            if (!holder.HasExited)
            {
                holder.Kill(entireProcessTree: true);
                holder.WaitForExit(2000);
            }
        }
    }

    [Fact]
    public void SpawnDetached_ReportsReadyAfterLeaseAndStatusPublication()
    {
        using Process holder = StartStub();
        try
        {
            CtDaemonLeaseIdentity identity = IdentityOf(holder);
            CtDaemonLeaseRecord lease = new(identity, DateTimeOffset.UtcNow, _root, "test");
            CtDaemonStatusRecord status = new(
                CtDaemonLifecycleState.Running,
                "status-only",
                identity,
                DateTimeOffset.UtcNow);

            CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(
                _root,
                startProcess: _ => holder,
                publication: new CtDaemonPublicationProbe
                {
                    ReadLease = _ => lease,
                    ReadStatus = _ => status,
                    IsProcessLive = _ => true,
                    Grace = TimeSpan.FromSeconds(2),
                    PollInterval = TimeSpan.FromMilliseconds(1),
                });

            Assert.Equal(CtDaemonSpawnStatus.Started, result.Status);
            Assert.Equal(CtDaemonPublicationReadiness.Ready, result.Publication?.Readiness);
            Assert.Contains("started", result.Reason, StringComparison.Ordinal);
        }
        finally
        {
            if (!holder.HasExited)
                holder.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void SpawnDetached_OnUnixWithoutSetsid_StillLaunchesDaemon()
    {
        if (OperatingSystem.IsWindows())
            return;

        string tools = Path.Combine(_root, "fallback-tools");
        Directory.CreateDirectory(tools);
        string marker = Path.Combine(_root, "fallback.marker");
        string nohup = Path.Combine(tools, "nohup");
        string daemon = Path.Combine(_root, "fallback-daemon.sh");
        WriteExecutable(nohup, "#!/bin/sh\nexec \"$@\"\n");
        WriteExecutable(
            daemon,
            "#!/bin/sh\nprintf '%s' \"$1\" > \"$MILLER_CT_FALLBACK_MARKER\"\n/bin/sleep 1\n");

        Process? process = null;
        try
        {
            CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(
                _root,
                startProcess: info =>
                {
                    info.Environment["PATH"] = tools;
                    info.Environment["MILLER_CT_FALLBACK_MARKER"] = marker;
                    process = Process.Start(info);
                    return process;
                },
                resolveImage: (_, _) => new CtDaemonImage(daemon, false, "test"),
                publication: NoWaitPublication());

            Assert.Equal(CtDaemonSpawnStatus.Started, result.Status);
            Assert.NotNull(process);
            Assert.True(
                SpinWait.SpinUntil(() => File.Exists(marker), TimeSpan.FromSeconds(5)),
                "the daemon did not execute through the no-setsid fallback");
            Assert.Equal("ct-daemon", File.ReadAllText(marker));
        }
        finally
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void SpawnDetached_ReportsPublicationLagWithoutChangingSpawnAcceptance()
    {
        using Process holder = StartStub();
        var clock = new ManualTimeProvider();
        try
        {
            CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(
                _root,
                startProcess: _ => holder,
                publication: new CtDaemonPublicationProbe
                {
                    Clock = clock,
                    ReadLease = _ => null,
                    ReadStatus = _ => null,
                    IsProcessLive = _ => true,
                    Grace = TimeSpan.FromSeconds(2),
                    PollInterval = TimeSpan.FromMilliseconds(25),
                    Delay = clock.Advance,
                });

            Assert.Equal(CtDaemonSpawnStatus.Started, result.Status);
            Assert.Equal(
                CtDaemonPublicationReadiness.NotPublishedWithinGrace,
                result.Publication?.Readiness);
            Assert.Contains("started", result.Reason, StringComparison.Ordinal);
        }
        finally
        {
            if (!holder.HasExited)
                holder.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void SpawnDetached_ReportsEarlyExitBeforePublication()
    {
        using Process holder = StartStub();
        try
        {
            CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(
                _root,
                startProcess: _ => holder,
                publication: new CtDaemonPublicationProbe
                {
                    ReadLease = _ => null,
                    ReadStatus = _ => null,
                    IsProcessLive = _ => false,
                    Grace = TimeSpan.FromSeconds(2),
                    PollInterval = TimeSpan.FromMilliseconds(1),
                });

            Assert.Equal(CtDaemonSpawnStatus.Started, result.Status);
            Assert.Equal(
                CtDaemonPublicationReadiness.DaemonExitedBeforePublish,
                result.Publication?.Readiness);
            Assert.Contains("started", result.Reason, StringComparison.Ordinal);
        }
        finally
        {
            if (!holder.HasExited)
                holder.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void SpawnDetached_RejectsNonPositivePublicationPollInterval()
    {
        foreach (TimeSpan pollInterval in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1) })
        {
            using Process holder = StartStub();
            try
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => CtDaemonLauncher.SpawnDetached(
                    _root,
                    startProcess: _ => holder,
                    publication: new CtDaemonPublicationProbe
                    {
                        Grace = TimeSpan.FromSeconds(1),
                        PollInterval = pollInterval,
                    }));
            }
            finally
            {
                if (!holder.HasExited)
                    holder.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public void SpawnDetached_RunsTheDaemonOutsideTheWorkspaceTree()
    {
        ProcessStartInfo? captured = null;
        CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(_root, startProcess: info =>
        {
            captured = info;
            return null;
        }, publication: NoWaitPublication());

        Assert.Equal(CtDaemonSpawnStatus.Failed, result.Status);
        Assert.NotNull(captured);

        // A live process holds its working directory open. On Windows that handle refuses a rename or a
        // delete of the directory, so a daemon that inherits the launcher's cwd pins the very tree Miller
        // indexes for as long as it runs.
        Assert.False(string.IsNullOrWhiteSpace(captured.WorkingDirectory));
        Assert.True(Directory.Exists(captured.WorkingDirectory));

        string workingDirectory = Path.GetFullPath(captured.WorkingDirectory);
        string root = Path.GetFullPath(_root);
        Assert.False(
            string.Equals(root, workingDirectory, StringComparison.OrdinalIgnoreCase),
            "the CT daemon must not run in the workspace root");
        Assert.False(
            workingDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "the CT daemon must not run inside the workspace it indexes");
    }

    [Fact]
    public void ResolveDaemonWorkingDirectory_SkipsCandidatesInsideTheWorkspace()
    {
        string inside = Path.Combine(_root, ".miller");
        string nested = Path.Combine(_root, "nested", "deeper");
        string outside = Path.Combine(Path.GetTempPath(), "miller-ct-outside-" + Guid.NewGuid().ToString("N"));
        string expected = Path.GetFullPath(outside);

        Assert.Equal(expected, CtDaemonLauncher.ResolveDaemonWorkingDirectory(_root, [inside, outside]));
        Assert.Equal(expected, CtDaemonLauncher.ResolveDaemonWorkingDirectory(_root, [nested, outside]));
        // The workspace root is itself inside the workspace.
        Assert.Equal(expected, CtDaemonLauncher.ResolveDaemonWorkingDirectory(_root, [_root, outside]));
        // A blank candidate is skipped, not used.
        Assert.Equal(expected, CtDaemonLauncher.ResolveDaemonWorkingDirectory(_root, ["  ", outside]));
        // A directory that merely shares a name prefix with the root is NOT inside it.
        string sibling = _root + "-sibling";
        Assert.Equal(
            Path.GetFullPath(sibling),
            CtDaemonLauncher.ResolveDaemonWorkingDirectory(_root, [sibling, outside]));
    }

    [Fact]
    public void SpawnDetached_OnWindows_GivesTheDaemonTheLogFilesAsItsOwnStreams()
    {
        if (!OperatingSystem.IsWindows())
            return;

        ProcessStartInfo? captured = null;
        _ = CtDaemonLauncher.SpawnDetached(_root, startProcess: info =>
        {
            captured = info;
            return null;
        }, publication: NoWaitPublication());

        Assert.NotNull(captured);
        Assert.False(captured.UseShellExecute);
        // stdout and stderr must NOT be redirected. A redirected stream is a pipe that only the LAUNCHER can
        // drain, and `miller tests serve` prints one line and exits about a millisecond after the spawn — so
        // the capture died before the daemon had finished starting. Leaving both alone makes .NET pass this
        // process's CURRENT standard handles to CreateProcess, and the spawn swaps those two to the log
        // files, so the daemon is born writing into the files themselves.
        Assert.False(captured.RedirectStandardOutput);
        Assert.False(captured.RedirectStandardError);
        // stdin stays redirected so the launcher can close the write end at once: the daemon reads EOF
        // instead of stealing bytes from an MCP server's protocol channel.
        Assert.True(captured.RedirectStandardInput);
    }

    [Fact]
    public void SpawnDetached_OnWindows_LeavesTheDaemonHoldingTheLogFileItWritesTo()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string marker = "ct-daemon-startup-diagnostic";
        Process? daemon = null;
        try
        {
            CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(_root, startProcess: info =>
            {
                // Keep the launcher's own stream decision, but run a stub that stays alive for a few seconds
                // and prints only at the END — the shape of a daemon whose diagnostic arrives long after a
                // one-shot `miller tests serve` launcher has exited.
                var stub = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    WorkingDirectory = info.WorkingDirectory,
                    UseShellExecute = info.UseShellExecute,
                    CreateNoWindow = true,
                    RedirectStandardInput = info.RedirectStandardInput,
                    RedirectStandardOutput = info.RedirectStandardOutput,
                    RedirectStandardError = info.RedirectStandardError,
                };
                stub.ArgumentList.Add("/c");
                stub.ArgumentList.Add($"ping -n 3 127.0.0.1 >nul & echo {marker}");
                daemon = Process.Start(stub);
                return daemon;
            }, publication: NoWaitPublication());

            Assert.Equal(CtDaemonSpawnStatus.Started, result.Status);

            string stdoutLog = Path.Combine(CtDaemonProtocol.RootDirectory(_root), "daemon.out.log");
            Assert.True(
                File.Exists(stdoutLog),
                "the spawn must open .miller/ct/daemon.out.log BEFORE it starts the daemon, because that "
                + "open handle is what becomes the daemon's own stdout");

            // The discriminating assertion. The stub is still running and has not printed yet. If the log
            // file is the daemon's own stdout, the DAEMON holds it open for its whole life, so an exclusive
            // open must fail with a sharing violation. A launcher-side pump holds a PIPE instead and touches
            // the file only while it copies a chunk, so this open would succeed — and that capture dies with
            // the launcher.
            Assert.ThrowsAny<IOException>(() =>
            {
                using (new FileStream(stdoutLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }
            });

            Assert.True(
                WaitForText(stdoutLog, marker, TimeSpan.FromSeconds(30)),
                "output the daemon writes seconds after the spawn must still reach "
                + ".miller/ct/daemon.out.log");
        }
        finally
        {
            KillIfRunning(daemon);
        }
    }

    private static void KillIfRunning(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or NotSupportedException or IOException or Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool WaitForText(string path, string expected, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (ReadShared(path).Contains(expected, StringComparison.Ordinal))
                return true;
            Thread.Sleep(25);
        }

        return false;
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            // The log file, or its directory, may not exist yet. Both derive from IOException.
            return string.Empty;
        }
    }

    private static Process StartStub()
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
        }
        else
        {
            info.FileName = "sleep";
            info.ArgumentList.Add("30");
        }

        return Process.Start(info) ?? throw new InvalidOperationException("stub process did not start");
    }

    private static void WriteExecutable(string path, string contents)
    {
        File.WriteAllText(path, contents);
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void RunWithNoDaemon_IsForegroundOneShot_AndDoesNotSpawn()
    {
        bool started = false;
        CtRunDisposition disposition = CtDaemonLauncher.ResolveRun(_root);

        Assert.Equal(CtRunExecution.ForegroundOneShot, disposition.Execution);
        Assert.Null(disposition.Lease);
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));

        CtRunResult run = CtCommandChannel.Run(_root, startProcess: _ =>
        {
            started = true;
            return null;
        });

        Assert.Equal(CtRunExecution.ForegroundOneShot, run.Execution);
        Assert.Null(run.Ack);
        Assert.False(started);
        Assert.False(Directory.Exists(CtDaemonProtocol.CommandDirectory(_root)));
    }

    private static string StartArguments(ProcessStartInfo info) =>
        string.Join(' ', info.ArgumentList.Count > 0 ? info.ArgumentList : [info.Arguments]);

    private static CtDaemonLeaseIdentity IdentityOf(Process process) =>
        new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()));

    private static CtDaemonPublicationProbe NoWaitPublication() => new()
    {
        Grace = TimeSpan.Zero,
        PollInterval = TimeSpan.FromMilliseconds(1),
        ReadLease = _ => null,
        ReadStatus = _ => null,
        IsProcessLive = _ => true,
    };

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) =>
            _timestamp += (long)(duration.TotalSeconds * Stopwatch.Frequency);
    }
}

/// <summary>
/// The short-lived-launcher proof for the Windows daemon capture, run as real processes.
///
/// <para>Every in-process test above runs the launcher inside the xUnit host, which lives for the whole
/// suite. That is the ONE shape the defect could not appear in: while the launcher lives, even a
/// launcher-owned pipe pump records everything. The documented start path is the opposite shape —
/// <c>miller tests serve</c> spawns the daemon, prints one JSON line and exits within a couple of
/// milliseconds — and it produced no log file at all. So this test uses the real binary: it waits for the
/// launcher process to EXIT, and only then asks the daemon to write.</para>
///
/// <para>Scale, and separate from the class above, because it spawns real <c>miller</c> processes and takes
/// seconds. It needs no julie-extract and no CT provider toolchain, only a built <c>miller</c>.</para>
/// </summary>
[Trait("Category", "Scale")]
public sealed class CtDaemonLauncherServeScaleTests : IDisposable
{
    private readonly string _work;
    private readonly string _root;
    private readonly string _home;

    public CtDaemonLauncherServeScaleTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "miller-ct-serve-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_work, "workspace");
        _home = Path.Combine(_work, "home");
        Directory.CreateDirectory(Path.Combine(_root, ".miller"));
        Directory.CreateDirectory(Path.Combine(_home, ".miller"));
    }

    public void Dispose()
    {
        KillDaemon();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ServeLauncherExitsFirst_AndTheDaemonStillWritesToTheCtLogFile()
    {
        string miller = RequireMillerBinary();

        File.WriteAllText(ContinuousTestPolicy.EnabledMarkerPath(_root), string.Empty);
        ScaleTestSupport.WriteFreshnessArtifact(_root, "artifact-" + Guid.NewGuid().ToString("N"), 1);

        (int serveExit, string serveOutput) = RunMiller(miller, ["tests", "serve", "--workspace", _root, "--json"]);
        Assert.True(serveExit == 0, $"`miller tests serve` exited {serveExit}: {serveOutput}");

        int? daemonPid = WaitForLiveDaemon(TimeSpan.FromSeconds(30));
        Assert.True(
            daemonPid is not null,
            "the daemon never took the lease, so this test cannot ask it to write. Logs so far:\n"
            + ReadLogs());

        using JsonDocument serveJson = JsonDocument.Parse(serveOutput);
        string? readiness = serveJson.RootElement.GetProperty("publication").GetProperty("readiness").GetString();
        Assert.True(
            readiness is "ready" or "not_published_within_grace",
            $"unexpected publication readiness: {readiness}");
        Assert.Equal(daemonPid.Value, serveJson.RootElement.GetProperty("pid").GetInt32());

        long before = LogBytes();

        (int stopExit, string stopOutput) = RunMiller(miller, ["tests", "stop", "--workspace", _root, "--json"]);
        Assert.True(stopExit == 0, $"`miller tests stop` exited {stopExit}: {stopOutput}");

        Assert.True(
            WaitForLogGrowth(before, TimeSpan.FromSeconds(30)),
            "the daemon wrote nothing to .miller/ct/daemon.out.log or daemon.err.log after the launcher "
            + "process exited, so `miller tests serve` still leaves no startup diagnostic. Logs:\n"
            + ReadLogs());
    }

    [Fact]
    public void UnixLauncherProcessGroupExit_DoesNotKillDaemon()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string miller = RequireMillerBinary();

        File.WriteAllText(ContinuousTestPolicy.EnabledMarkerPath(_root), string.Empty);
        ScaleTestSupport.WriteFreshnessArtifact(_root, "artifact-" + Guid.NewGuid().ToString("N"), 1);

        string launcherGroupPath = Path.Combine(_work, "launcher-group.pid");
        (Process launcher, StringBuilder serveOutput) = StartLauncher(miller, launcherGroupPath);
        using (launcher)
        {
            try
            {
                int launcherGroupId = WaitForGroupId(launcherGroupPath, TimeSpan.FromSeconds(5));
                launcher.StandardInput.WriteLine("start");
                launcher.StandardInput.Close();

                int? daemonPid = WaitForLiveDaemon(TimeSpan.FromSeconds(30));
                Assert.True(
                    daemonPid is not null,
                    "the daemon never took the lease, so this test cannot exercise process-group teardown. "
                    + ReadLogs());
                Assert.True(
                    WaitForOutput(serveOutput, "\"publication\":{\"readiness\":\"ready\"", TimeSpan.FromSeconds(30)),
                    $"serve output did not publish readiness: {ReadOutput(serveOutput)}");

                using (JsonDocument serveJson = JsonDocument.Parse(ReadOutput(serveOutput)))
                    Assert.Equal(daemonPid.Value, serveJson.RootElement.GetProperty("pid").GetInt32());

                Assert.NotEqual(GetSessionId(launcherGroupId), GetSessionId(daemonPid.Value));
                Assert.NotEqual(launcherGroupId, GetProcessGroup(daemonPid.Value));
                Assert.Equal(0, KillProcessGroup(-launcherGroupId, 9));
                Assert.True(
                    WaitForProcess(daemonPid.Value, TimeSpan.FromSeconds(5)),
                    "the daemon died when its launcher process group was torn down. Logs:\n" + ReadLogs());
            }
            finally
            {
                if (!launcher.HasExited)
                    launcher.Kill(entireProcessTree: true);
            }
        }
    }

    private int? WaitForLiveDaemon(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (CtDaemonLease.TryReadLive(_root) is { } live)
                return live.Identity.Pid;
            Thread.Sleep(100);
        }

        return null;
    }

    private bool WaitForLogGrowth(long before, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (LogBytes() > before)
                return true;
            Thread.Sleep(100);
        }

        return false;
    }

    private long LogBytes()
    {
        long total = 0;
        foreach (string path in LogPaths())
        {
            var file = new FileInfo(path);
            if (file.Exists)
                total += file.Length;
        }

        return total;
    }

    private string ReadLogs()
    {
        var text = new StringBuilder();
        foreach (string path in LogPaths())
        {
            text.Append(Path.GetFileName(path)).Append(": ");
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                text.AppendLine(reader.ReadToEnd());
            }
            catch (IOException)
            {
                text.AppendLine("<absent>");
            }
        }

        return text.ToString();
    }

    private string[] LogPaths()
    {
        string ctDir = CtDaemonProtocol.RootDirectory(_root);
        return [Path.Combine(ctDir, "daemon.out.log"), Path.Combine(ctDir, "daemon.err.log")];
    }

    private void KillDaemon()
    {
        if (CtDaemonLease.TryReadLive(_root) is not { } live)
            return;

        try
        {
            using Process daemon = Process.GetProcessById(live.Identity.Pid);
            daemon.Kill(entireProcessTree: true);
            daemon.WaitForExit(5000);
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or NotSupportedException or Win32Exception)
        {
        }
    }

    /// <summary>
    /// Runs one miller verb to completion in an isolated home. Output is read through the ASYNC handlers;
    /// after exit, the helper waits under the sink lock only for the first line, with a bounded timeout.
    /// It never waits for end-of-stream because the detached daemon inherits the launcher's pipe handles.
    /// </summary>
    private (int ExitCode, string Output) RunMiller(string miller, string[] args)
    {
        var info = new ProcessStartInfo(miller)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _work,
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        info.Environment[MillerHome.EnvironmentVariable] = _home;
        info.Environment["HOME"] = _home;
        info.Environment["USERPROFILE"] = _home;
        // CT must be ON for `tests serve` to spawn at all, and semantics are irrelevant here — a broker
        // would only add a second subprocess to this test.
        info.Environment.Remove(CtEnvironment.KillSwitch);
        info.Environment["MILLER_SEMANTIC"] = "off";

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException($"miller {string.Join(' ', args)} did not start");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendLine(output, e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Assert.True(
            process.WaitForExit(120_000),
            $"miller {string.Join(' ', args)} did not exit within 120s");

        string capturedOutput;
        lock (output)
        {
            if (output.Length == 0)
                Monitor.Wait(output, TimeSpan.FromSeconds(5));
            capturedOutput = output.ToString();
        }

        Assert.False(
            string.IsNullOrWhiteSpace(capturedOutput),
            $"miller {string.Join(' ', args)} exited without producing output within 5s");
        return (process.ExitCode, capturedOutput);
    }

    private (Process Process, StringBuilder Output) StartLauncher(string miller, string groupPath)
    {
        var info = new ProcessStartInfo("setsid")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _work,
        };
        info.ArgumentList.Add("/bin/sh");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(
            "echo $$ > \"$7\"; read -r _; \"$1\" \"$2\" \"$3\" \"$4\" \"$5\" \"$6\"; sleep 30");
        info.ArgumentList.Add("ct-launcher");
        info.ArgumentList.Add(miller);
        info.ArgumentList.Add("tests");
        info.ArgumentList.Add("serve");
        info.ArgumentList.Add("--workspace");
        info.ArgumentList.Add(_root);
        info.ArgumentList.Add("--json");
        info.ArgumentList.Add(groupPath);
        info.Environment[MillerHome.EnvironmentVariable] = _home;
        info.Environment["HOME"] = _home;
        info.Environment["USERPROFILE"] = _home;
        info.Environment.Remove(CtEnvironment.KillSwitch);
        info.Environment["MILLER_SEMANTIC"] = "off";

        Process process = Process.Start(info)
            ?? throw new InvalidOperationException("the launcher process did not start");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendLine(output, e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return (process, output);
    }

    private static int WaitForGroupId(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path), out int groupId))
                return groupId;
            Thread.Sleep(25);
        }

        throw new TimeoutException($"the launcher did not publish its process-group id in {path}");
    }

    private static bool WaitForOutput(StringBuilder output, string expected, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (ReadOutput(output).Contains(expected, StringComparison.Ordinal))
                return true;
            Thread.Sleep(25);
        }

        return false;
    }

    private static string ReadOutput(StringBuilder output)
    {
        lock (output)
            return output.ToString();
    }

    private static bool WaitForProcess(int pid, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (!process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return false;
            }

            Thread.Sleep(25);
        }

        return false;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillProcessGroup(int processId, int signal = 15);

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetProcessGroup(int processId);

    [DllImport("libc", EntryPoint = "getsid", SetLastError = true)]
    private static extern int GetSessionId(int processId);

    private static void AppendLine(StringBuilder sink, string? line)
    {
        if (line is null)
            return;
        lock (sink)
        {
            sink.AppendLine(line);
            Monitor.PulseAll(sink);
        }
    }

    // The CLI resolves .tools relative to its own AppContext.BaseDirectory, so the binary must be the built
    // one, not a published archive. Same resolution the scan-governor scale fixture uses.
    /// <summary>
    /// The miller binary built from THIS run's configuration - never "whichever configuration exists".
    ///
    /// <para>Trying Release first and falling back to Debug looks harmless and is not. A Debug test run then
    /// spawned a stale RELEASE binary that predated the fix under test: the daemon it launched had no
    /// file-backed capture, the log assertion failed, and the failure message blamed production for a defect
    /// that was already repaired. The stale Release build was itself unavoidable - a running miller MCP server
    /// holds Miller.Testing.dll open, so `-c Release` fails MSB3027 until the user stops it.</para>
    ///
    /// <para>So the configuration comes from this assembly's own <see cref="AssemblyConfigurationAttribute"/>,
    /// which MSBuild stamps with the Configuration that built it. A Debug run tests Debug; a Release run tests
    /// Release; neither can silently measure the other.</para>
    /// </summary>
    private static string RequireMillerBinary()
    {
        string name = OperatingSystem.IsWindows() ? "miller.exe" : "miller";
        string configuration = TestAssemblyConfiguration();
        string outputDir = Path.Combine(
            ScaleTestSupport.RepoRoot(), "src", "Miller.Server", "bin", configuration, "net10.0");
        string binary = Path.Combine(outputDir, name);
        Assert.SkipWhen(
            !File.Exists(binary),
            $"no {configuration} miller binary at {binary}. Run `dotnet build Miller.slnx -c {configuration}` "
            + "to enable the Scale test.");

        RequireBinaryNotOlderThanSource(outputDir, binary);
        return binary;
    }

    /// <summary>
    /// The Configuration that built this test assembly, from the attribute MSBuild stamps on every build.
    /// </summary>
    private static string TestAssemblyConfiguration()
    {
        string? configuration = typeof(CtDaemonLauncherServeScaleTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(configuration),
            "this test assembly carries no AssemblyConfigurationAttribute, so the matching miller build "
            + "cannot be identified. Refusing to guess a configuration.");
        return configuration!;
    }

    /// <summary>
    /// Fail - loudly, and before the test spawns anything - when the built output predates the source it
    /// claims to exercise. The apphost <c>miller.exe</c> cannot answer this: it is rewritten only when project
    /// properties change, so its timestamp sat at an old build while the managed code moved on. The managed
    /// assemblies beside it carry the code, so they are what gets compared.
    ///
    /// <para>The match is case-INSENSITIVE on purpose. Miller.Server's own assembly is <c>miller.dll</c>, not
    /// <c>Miller.Server.dll</c>, and it holds Program.cs plus every tool core. A <c>Miller.*.dll</c> glob
    /// matches it on Windows and macOS and MISSES it on Linux, so the guard measured only the referenced
    /// projects there: a change confined to Miller.Server failed as stale because those DLLs correctly did not
    /// relink, and a stale miller.dll beside a fresh Miller.Core.dll would have passed.</para>
    /// </summary>
    private static void RequireBinaryNotOlderThanSource(string outputDir, string binary)
    {
        DateTime built = Directory
            .EnumerateFiles(outputDir, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(static path => Path.GetFileName(path)
                .StartsWith("miller", StringComparison.OrdinalIgnoreCase))
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(File.GetLastWriteTimeUtc(binary))
            .Max();

        string sourceRoot = Path.Combine(ScaleTestSupport.RepoRoot(), "src");
        (string Path, DateTime Written) newest = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !IsBuildOutput(path))
            .Select(static path => (Path: path, Written: File.GetLastWriteTimeUtc(path)))
            .DefaultIfEmpty((Path: string.Empty, Written: DateTime.MinValue))
            .MaxBy(static entry => entry.Written);

        Assert.True(
            newest.Written <= built,
            $"the {Path.GetFileName(binary)} under test was built at {built:O}, which is OLDER than "
            + $"{newest.Path} ({newest.Written:O}). This test would measure code that is not the code in the "
            + "working tree. Rebuild before running it. If the build fails with MSB3027, a running miller MCP "
            + "server holds the output open - stop it first.");
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
