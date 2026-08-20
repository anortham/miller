using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

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
            });

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
    public void SpawnDetached_RunsTheDaemonOutsideTheWorkspaceTree()
    {
        ProcessStartInfo? captured = null;
        CtDaemonSpawnResult result = CtDaemonLauncher.SpawnDetached(_root, startProcess: info =>
        {
            captured = info;
            return null;
        });

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
        });

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
            });

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
        Assert.SkipWhen(
            !OperatingSystem.IsWindows(),
            "the launcher-owned-capture hole is Windows-only; the Unix branch redirects through /bin/sh.");
        string miller = RequireMillerBinary();

        // Opt the workspace in, and give the revision poller the minimal artifact it reads. Neither is the
        // subject of this test — they only let the daemon reach its normal idle loop.
        File.WriteAllText(ContinuousTestPolicy.EnabledMarkerPath(_root), string.Empty);
        ScaleTestSupport.WriteFreshnessArtifact(_root, "artifact-" + Guid.NewGuid().ToString("N"), 1);

        // The launcher. WaitForExit returns only once this process is GONE, so everything after this line
        // happens with no launcher alive to drain a pipe.
        (int serveExit, string serveOutput) = RunMiller(miller, ["tests", "serve", "--workspace", _root, "--json"]);
        Assert.True(serveExit == 0, $"`miller tests serve` exited {serveExit}: {serveOutput}");

        int? daemonPid = WaitForLiveDaemon(TimeSpan.FromSeconds(30));
        Assert.True(
            daemonPid is not null,
            "the daemon never took the lease, so this test cannot ask it to write. Logs so far:\n"
            + ReadLogs());

        // Everything the daemon has written up to here happened while the launcher MIGHT still have been
        // alive. Only growth after this snapshot proves the capture outlived the launcher.
        long before = LogBytes();

        (int stopExit, string stopOutput) = RunMiller(miller, ["tests", "stop", "--workspace", _root, "--json"]);
        Assert.True(stopExit == 0, $"`miller tests stop` exited {stopExit}: {stopOutput}");

        // On its way out the daemon writes its final line to stdout (or, if it fails, `ct-daemon failed: …`
        // to stderr). Either lands in .miller/ct only if the FILE owns the capture. With the pump running in
        // the launcher, both handles closed when `tests serve` exited seconds ago and nothing can arrive.
        Assert.True(
            WaitForLogGrowth(before, TimeSpan.FromSeconds(30)),
            "the daemon wrote nothing to .miller/ct/daemon.out.log or daemon.err.log after the launcher "
            + "process exited, so `miller tests serve` still leaves no startup diagnostic. Logs:\n"
            + ReadLogs());
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
    /// Runs one miller verb to completion in an isolated home. Output is read through the ASYNC handlers and
    /// waited on with the TIMEOUT overload, never the parameterless <c>WaitForExit()</c>: the detached daemon
    /// inherits the launcher's inheritable pipe handles, so waiting for end-of-stream would wait for the
    /// DAEMON, which is exactly the process this test needs to keep alive.
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
        return (process.ExitCode, output.ToString());
    }

    private static void AppendLine(StringBuilder sink, string? line)
    {
        if (line is null)
            return;
        lock (sink)
            sink.AppendLine(line);
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
    /// </summary>
    private static void RequireBinaryNotOlderThanSource(string outputDir, string binary)
    {
        DateTime built = Directory
            .EnumerateFiles(outputDir, "Miller.*.dll", SearchOption.TopDirectoryOnly)
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
