using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Xunit;

namespace Miller.Tests.Server.Cli;

public sealed class DashboardCliLauncherTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root;
    private readonly string _home;
    private readonly string _registryDb;
    private readonly string _dashboardDll;

    public DashboardCliLauncherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-dashboard-cli-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_dir, "repo");
        _home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(Path.Combine(_dir, ".tools"));
        _registryDb = Path.Combine(_home, ".miller", "workspaces.db");
        _dashboardDll = Path.Combine(_dir, "Miller.Dashboard.dll");
        File.WriteAllText(_dashboardDll, "fake");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MILLER_DASHBOARD_DLL", null);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void WorkspaceUrl_UsesRegisteredCurrentWorkspaceId()
    {
        WorkspaceContext ctx = Context();
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(_root);
        string id = WorkspaceId.FromCanonicalRoot(canonicalRoot);
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
            registry.UpsertSeen(id, "repo-id", canonicalRoot, Path.Combine(canonicalRoot, ".miller", "symbols.db"),
                WorkspaceRegistryState.Ready);

        Uri url = DashboardCliLauncher.WorkspaceUrl(DashboardCliLauncher.BaseUri(4977), ctx);

        Assert.Equal($"http://127.0.0.1:4977/workspace?workspace_id={id}", url.ToString());
    }

    [Fact]
    public void EnsureRunning_WhenAlreadyHealthy_DoesNotLaunchProcess()
    {
        int starts = 0;
        var launcher = new DashboardCliLauncher(
            startProcess: _ =>
            {
                starts++;
                return Process.GetCurrentProcess();
            },
            isHealthy: _ => true,
            tryAcquireLaunchLock: _ => throw new InvalidOperationException("lock should not be acquired"),
            writeMetadata: (_, _) => throw new InvalidOperationException("metadata should not be written"),
            sleep: _ => throw new InvalidOperationException("sleep should not run"));

        DashboardLaunchResult result = launcher.EnsureRunning(new DashboardLaunchRequest(
            Context(), DashboardCliLauncher.DefaultPort, TimeSpan.FromMilliseconds(10)));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Equal(0, starts);
    }

    [Fact]
    public void EnsureRunning_WhenMetadataUrlIsHealthy_ReusesExistingInstance()
    {
        WorkspaceContext ctx = Context();
        string machineMillerDir = Path.GetDirectoryName(ctx.RegistryDbPath)!;
        Directory.CreateDirectory(machineMillerDir);
        File.WriteAllText(
            Path.Combine(machineMillerDir, "dashboard.json"),
            """
            {
              "ProcessId": 42,
              "Url": "http://127.0.0.1:4977",
              "StartedAtUtc": "2026-06-05T00:00:00Z"
            }
            """);
        int starts = 0;
        var launcher = new DashboardCliLauncher(
            startProcess: _ =>
            {
                starts++;
                return Process.GetCurrentProcess();
            },
            isHealthy: uri => uri.Port == 4977,
            tryAcquireLaunchLock: _ => throw new InvalidOperationException("lock should not be acquired"),
            writeMetadata: (_, _) => throw new InvalidOperationException("metadata should not be written"),
            sleep: _ => throw new InvalidOperationException("sleep should not run"));

        DashboardLaunchResult result = launcher.EnsureRunning(new DashboardLaunchRequest(
            ctx, 5001, TimeSpan.FromMilliseconds(10)));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Equal("http://127.0.0.1:4977/workspace", result.Url.GetLeftPart(UriPartial.Path));
        Assert.Equal(0, starts);
    }

    [Fact]
    public void EnsureRunning_WhenNotHealthy_StartsOnceUnderLaunchLockAndWritesMetadata()
    {
        Environment.SetEnvironmentVariable("MILLER_DASHBOARD_DLL", _dashboardDll);
        bool started = false;
        int starts = 0;
        string? lockPath = null;
        DashboardProcessMetadata? metadata = null;
        var launcher = new DashboardCliLauncher(
            startProcess: info =>
            {
                starts++;
                started = true;
                if (OperatingSystem.IsWindows())
                {
                    Assert.Equal("dotnet", info.FileName);
                    Assert.Equal(_dashboardDll, Assert.Single(info.ArgumentList));
                    Assert.True(info.CreateNewProcessGroup);
                }
                else
                {
                    Assert.Equal("/bin/sh", info.FileName);
                    Assert.Contains(_dashboardDll, info.ArgumentList);
                    string pidPath = info.Environment["MILLER_DASHBOARD_PID_FILE"]!;
                    File.WriteAllText(pidPath, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                }
                Assert.Equal(Path.GetDirectoryName(_registryDb), info.WorkingDirectory);
                Assert.Equal("5002", info.Environment["MILLER_DASHBOARD_PORT"]);
                Assert.Equal(_root, info.Environment["MILLER_DASHBOARD_PREFERRED_ROOT"]);
                return Process.GetCurrentProcess();
            },
            isHealthy: _ => started,
            tryAcquireLaunchLock: path =>
            {
                lockPath = path;
                return new NoopDisposable();
            },
            writeMetadata: (_, value) => metadata = value,
            sleep: _ => { });

        DashboardLaunchResult result = launcher.EnsureRunning(new DashboardLaunchRequest(
            Context(), 5002, TimeSpan.FromSeconds(1)));

        Assert.Equal(DashboardLaunchOutcome.Started, result.Outcome);
        Assert.Equal(1, starts);
        Assert.EndsWith(Path.Combine(".miller", "dashboard.lock"), lockPath);
        Assert.NotNull(metadata);
        Assert.Equal(Process.GetCurrentProcess().Id, metadata!.ProcessId);
        Assert.Equal("http://127.0.0.1:5002", metadata.Url);
    }

    /// <summary>
    /// The Windows launch used to leave the dashboard on the caller's own stdout: no redirection, and .NET
    /// creates every child with handle inheritance on. A shell that piped the command
    /// (<c>miller dashboard | anything</c>) had its pipe duplicated into the dashboard, which held it for the
    /// dashboard's whole life — miller printed the URL, exited, and the pipeline still hung.
    ///
    /// <para>The spawn now goes through <c>DetachedProcessStreams</c>, which opens both log files and swaps
    /// them in as the child's standard handles. Their existence after a launch is the observable half; the
    /// inheritance half is guarded by <c>DetachedSpawnHandleConventionTests</c> and
    /// <c>StandardHandleInheritanceTests</c>, because only a shell that CAPTURES output can see it.</para>
    /// </summary>
    [Fact]
    public void EnsureRunning_OnWindows_GivesTheDashboardItsOwnLogFilesRatherThanTheCallersStdout()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Unix redirects inside its /bin/sh launch script.");

        Environment.SetEnvironmentVariable("MILLER_DASHBOARD_DLL", _dashboardDll);
        string machineMillerDir = Path.GetDirectoryName(_registryDb)!;
        Directory.CreateDirectory(machineMillerDir);
        string stdoutLog = Path.Combine(machineMillerDir, "dashboard.out.log");
        string stderrLog = Path.Combine(machineMillerDir, "dashboard.err.log");
        Assert.False(File.Exists(stdoutLog), "the log must not exist before the launch");

        bool started = false;
        var launcher = new DashboardCliLauncher(
            startProcess: info =>
            {
                started = true;

                // Asking for no redirection is exactly the shape that inherited the caller's handles, so the
                // launch must NOT have switched to pipes to solve this.
                Assert.False(info.RedirectStandardOutput);
                Assert.False(info.RedirectStandardError);
                return Process.GetCurrentProcess();
            },
            isHealthy: _ => started,
            tryAcquireLaunchLock: _ => new NoopDisposable(),
            writeMetadata: (_, _) => { },
            sleep: _ => { });

        DashboardLaunchResult result = launcher.EnsureRunning(new DashboardLaunchRequest(
            Context(), 5009, TimeSpan.FromSeconds(1)));

        Assert.Equal(DashboardLaunchOutcome.Started, result.Outcome);
        Assert.True(File.Exists(stdoutLog), $"{stdoutLog} was not opened for the dashboard");
        Assert.True(File.Exists(stderrLog), $"{stderrLog} was not opened for the dashboard");
    }

    [Fact]
    public void EnsureRunning_UsesPackagedDashboardExecutable()
    {
        string dashboardDir = Path.Combine(_dir, "dashboard");
        Directory.CreateDirectory(dashboardDir);
        string dashboardExe = Path.Combine(
            dashboardDir,
            OperatingSystem.IsWindows() ? "Miller.Dashboard.exe" : "Miller.Dashboard");
        File.WriteAllText(dashboardExe, "fake");

        bool started = false;
        var launcher = new DashboardCliLauncher(
            startProcess: info =>
            {
                started = true;
                if (OperatingSystem.IsWindows())
                {
                    Assert.Equal(dashboardExe, info.FileName);
                    Assert.Empty(info.ArgumentList);
                    Assert.True(info.CreateNewProcessGroup);
                }
                else
                {
                    Assert.Equal("/bin/sh", info.FileName);
                    Assert.Contains(dashboardExe, info.ArgumentList);
                    string pidPath = info.Environment["MILLER_DASHBOARD_PID_FILE"]!;
                    File.WriteAllText(pidPath, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                }

                return Process.GetCurrentProcess();
            },
            isHealthy: _ => started,
            tryAcquireLaunchLock: _ => new NoopDisposable(),
            writeMetadata: (_, _) => { },
            sleep: _ => { });

        DashboardLaunchResult result = launcher.EnsureRunning(new DashboardLaunchRequest(
            Context(), 5004, TimeSpan.FromSeconds(1)));

        Assert.Equal(DashboardLaunchOutcome.Started, result.Outcome);
    }

    [Fact]
    public void EnsureRunning_RemovesStalePidFileBeforeLaunch()
    {
        Environment.SetEnvironmentVariable("MILLER_DASHBOARD_DLL", _dashboardDll);
        WorkspaceContext ctx = Context();
        string machineMillerDir = Path.GetDirectoryName(ctx.RegistryDbPath)!;
        Directory.CreateDirectory(machineMillerDir);
        string pidPath = Path.Combine(machineMillerDir, "dashboard.pid");
        File.WriteAllText(pidPath, "123");
        bool started = false;
        var launcher = new DashboardCliLauncher(
            startProcess: _ =>
            {
                Assert.False(File.Exists(pidPath));
                started = true;
                File.WriteAllText(pidPath, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                return Process.GetCurrentProcess();
            },
            isHealthy: _ => started,
            tryAcquireLaunchLock: _ => new NoopDisposable(),
            writeMetadata: (_, _) => { },
            sleep: _ => { });

        DashboardLaunchResult result = launcher.EnsureRunning(new DashboardLaunchRequest(
            ctx, 5003, TimeSpan.FromSeconds(1)));

        Assert.Equal(DashboardLaunchOutcome.Started, result.Outcome);
    }

    [Fact]
    public void EnsureRunning_WhenTheRunningDashboardIsThisBuild_ReusesIt()
    {
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.23.0+aaaaaaa");
        var kills = new List<int>();
        int starts = 0;

        DashboardLaunchResult result = Launcher(
            startProcess: _ => { starts++; return Process.GetCurrentProcess(); },
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart),
            killProcess: pid => { kills.Add(pid); return true; })
            .EnsureRunning(Request(port: 4977, ownVersion: "1.23.0+aaaaaaa"));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Equal("already running", result.Message);
        Assert.Empty(kills);
        Assert.Equal(0, starts);
    }

    [Fact]
    public void EnsureRunning_WhenTheRunningDashboardIsAnOlderBuild_StopsItAndStartsThisOne()
    {
        Environment.SetEnvironmentVariable("MILLER_DASHBOARD_DLL", _dashboardDll);
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");
        var kills = new List<int>();
        bool stopped = false;
        bool started = false;
        DashboardProcessMetadata? written = null;

        DashboardLaunchResult result = Launcher(
            startProcess: info => { started = true; WritePidFile(info); return Process.GetCurrentProcess(); },
            isHealthy: _ => started || !stopped,
            probeProcess: _ => stopped ? null : new DashboardProcessProbe(RecordedStart),
            killProcess: pid => { kills.Add(pid); stopped = true; return true; },
            writeMetadata: (_, value) => written = value)
            .EnsureRunning(Request(port: 4977, ownVersion: "1.23.0+bbbbbbb"));

        Assert.Equal(DashboardLaunchOutcome.Replaced, result.Outcome);
        Assert.Equal("replaced the dashboard on 1.22.0+aaaaaaa", result.Message);
        Assert.Equal(42, Assert.Single(kills));
        Assert.Equal("1.23.0+bbbbbbb", written!.MillerVersion);
    }

    [Fact]
    public void EnsureRunning_WhenTheRecordPredatesTheVersionField_ReplacesTheDashboard()
    {
        Environment.SetEnvironmentVariable("MILLER_DASHBOARD_DLL", _dashboardDll);
        WriteRecordText($$"""
            {
              "ProcessId": 42,
              "Url": "http://127.0.0.1:4977",
              "StartedAtUtc": "{{RecordedStart:O}}"
            }
            """);
        bool stopped = false;
        bool started = false;

        DashboardLaunchResult result = Launcher(
            startProcess: info => { started = true; WritePidFile(info); return Process.GetCurrentProcess(); },
            isHealthy: _ => started || !stopped,
            probeProcess: _ => stopped ? null : new DashboardProcessProbe(RecordedStart),
            killProcess: _ => { stopped = true; return true; })
            .EnsureRunning(Request(port: 4977, ownVersion: "1.23.0+bbbbbbb"));

        Assert.Equal(DashboardLaunchOutcome.Replaced, result.Outcome);
        Assert.Equal("replaced the dashboard on unknown", result.Message);
    }

    [Fact]
    public void EnsureRunning_WhenTheRunningDashboardIsANewerBuild_ReusesItAndReportsTheMismatch()
    {
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "2.0.0+ccccccc");
        var kills = new List<int>();

        DashboardLaunchResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("a newer dashboard must not be replaced"),
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart),
            killProcess: pid => { kills.Add(pid); return true; })
            .EnsureRunning(Request(port: 4977, ownVersion: "1.23.0+bbbbbbb"));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Contains("newer build (2.0.0+ccccccc)", result.Message!);
        Assert.Empty(kills);
    }

    [Fact]
    public void EnsureRunning_WhenNeitherBuildCanBeOrdered_ReusesItAndReportsTheMismatch()
    {
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "nightly");
        var kills = new List<int>();

        DashboardLaunchResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("an unorderable pair must be left alone"),
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart),
            killProcess: pid => { kills.Add(pid); return true; })
            .EnsureRunning(Request(port: 4977, ownVersion: "experimental"));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Contains("neither can be ordered", result.Message!);
        Assert.Empty(kills);
    }

    [Fact]
    public void EnsureRunning_WhenTheRecordedPidIsNotTheDashboard_ReusesRatherThanKillingBlindly()
    {
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");
        var kills = new List<int>();

        DashboardLaunchResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("an unproven pid must not be replaced"),
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart.AddHours(-3)),
            killProcess: pid => { kills.Add(pid); return true; })
            .EnsureRunning(Request(port: 4977, ownVersion: "1.23.0+bbbbbbb"));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Contains("could not be replaced", result.Message!);
        Assert.Contains("is not the recorded dashboard", result.Message!);
        Assert.Empty(kills);
    }

    [Fact]
    public void EnsureRunning_WhenThereIsNoBinaryToStart_LeavesTheOlderDashboardRunning()
    {
        File.Delete(_dashboardDll);
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");
        var kills = new List<int>();

        DashboardLaunchResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("there is no binary to start"),
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart),
            killProcess: pid => { kills.Add(pid); return true; })
            .EnsureRunning(Request(port: 4977, ownVersion: "1.23.0+bbbbbbb"));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Contains("dashboard binary not found", result.Message!);
        Assert.Empty(kills);
    }

    [Fact]
    public void EnsureRunning_WhenTheCallerNamesNoBuild_ReusesARecordWrittenBeforeTheVersionField()
    {
        WriteRecordText($$"""
            {
              "ProcessId": 42,
              "Url": "http://127.0.0.1:4977",
              "StartedAtUtc": "{{RecordedStart:O}}"
            }
            """);

        DashboardLaunchResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("an unversioned caller must not replace"),
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart),
            killProcess: _ => throw new InvalidOperationException("an unversioned caller must not kill"))
            .EnsureRunning(Request(port: 4977, ownVersion: null));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Equal("already running", result.Message);
    }

    [Fact]
    public void Stop_WhenTheRecordedDashboardIsRunning_KillsItAndClearsTheRecord()
    {
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");
        bool stopped = false;
        var kills = new List<int>();

        DashboardStopResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("stop must not launch"),
            isHealthy: _ => !stopped,
            probeProcess: _ => stopped ? null : new DashboardProcessProbe(RecordedStart),
            killProcess: pid => { kills.Add(pid); stopped = true; return true; })
            .Stop(new DashboardStopRequest(Context(), TimeSpan.FromSeconds(1)));

        Assert.Equal(DashboardStopOutcome.Stopped, result.Outcome);
        Assert.Equal(42, Assert.Single(kills));
        Assert.Equal("1.22.0+aaaaaaa", result.Version);
        Assert.Contains("stopped the dashboard on 1.22.0+aaaaaaa", result.Message);
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void Stop_WhenNoDashboardIsRecorded_SucceedsWithAnHonestMessage()
    {
        DashboardStopResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("stop must not launch"),
            isHealthy: _ => throw new InvalidOperationException("nothing to probe"),
            probeProcess: _ => throw new InvalidOperationException("nothing to probe"),
            killProcess: _ => throw new InvalidOperationException("nothing to kill"))
            .Stop(new DashboardStopRequest(Context(), TimeSpan.FromSeconds(1)));

        Assert.Equal(DashboardStopOutcome.NotRunning, result.Outcome);
        Assert.True(result.Success);
        Assert.Equal("no dashboard is recorded as running", result.Message);
    }

    [Fact]
    public void Stop_WhenTheRecordedPidIsNotTheDashboard_RefusesToKillAndReportsWhy()
    {
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");
        var kills = new List<int>();

        DashboardStopResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("stop must not launch"),
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart.AddHours(-3)),
            killProcess: pid => { kills.Add(pid); return true; })
            .Stop(new DashboardStopRequest(Context(), TimeSpan.FromSeconds(1)));

        Assert.Equal(DashboardStopOutcome.Failed, result.Outcome);
        Assert.Empty(kills);
        Assert.Contains("is not the recorded dashboard", result.Message);
        Assert.True(File.Exists(RecordPath));
    }

    [Fact]
    public void EnsureRunning_RecordsTheStartTimeOfTheProcessItSpawned()
    {
        Environment.SetEnvironmentVariable("MILLER_DASHBOARD_DLL", _dashboardDll);
        DateTimeOffset spawnedStart = RecordedStart.AddMinutes(-7);
        bool started = false;
        DashboardProcessMetadata? written = null;

        DashboardLaunchResult result = Launcher(
            startProcess: info => { started = true; WritePidFile(info); return Process.GetCurrentProcess(); },
            isHealthy: _ => started,
            probeProcess: _ => new DashboardProcessProbe(spawnedStart),
            killProcess: _ => throw new InvalidOperationException("nothing to kill"),
            writeMetadata: (_, value) => written = value)
            .EnsureRunning(Request(port: 5010, ownVersion: "1.23.0+bbbbbbb"));

        Assert.Equal(DashboardLaunchOutcome.Started, result.Outcome);
        Assert.Equal(spawnedStart, written!.ProcessStartedAtUtc);
    }

    [Fact]
    public void EnsureRunning_WhenTheDashboardNeverAnswers_LeavesNoRecordNamingIt()
    {
        Environment.SetEnvironmentVariable("MILLER_DASHBOARD_DLL", _dashboardDll);
        WriteRecord(pid: 42, port: 5011, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");

        DashboardLaunchResult result = Launcher(
            startProcess: info => { WritePidFile(info); return Process.GetCurrentProcess(); },
            isHealthy: _ => false,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart),
            killProcess: _ => throw new InvalidOperationException("nothing to kill"),
            writeMetadata: (_, _) => throw new InvalidOperationException("a silent dashboard must not be recorded"))
            .EnsureRunning(Request(port: 5011, ownVersion: "1.23.0+bbbbbbb", timeout: TimeSpan.Zero));

        Assert.Equal(DashboardLaunchOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void EnsureRunning_WhenTheOldDashboardRefusesToStop_LeavesItRunning()
    {
        Environment.SetEnvironmentVariable("MILLER_DASHBOARD_DLL", _dashboardDll);
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");

        DashboardLaunchResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("a dashboard that would not stop must not be replaced"),
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart),
            killProcess: _ => false)
            .EnsureRunning(Request(port: 4977, ownVersion: "1.23.0+bbbbbbb"));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Contains("could not be replaced", result.Message!);
        Assert.Contains("refused to stop", result.Message!);
    }

    [Fact]
    public void EnsureRunning_WhenTheSignalledDashboardKeepsAnswering_FailsRatherThanClaimingItStillRuns()
    {
        Environment.SetEnvironmentVariable("MILLER_DASHBOARD_DLL", _dashboardDll);
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");
        var kills = new List<int>();

        DashboardLaunchResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("a dashboard still answering must not be replaced"),
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart),
            killProcess: pid => { kills.Add(pid); return true; })
            .EnsureRunning(Request(port: 4977, ownVersion: "1.23.0+bbbbbbb", timeout: TimeSpan.Zero));

        Assert.Equal(DashboardLaunchOutcome.Failed, result.Outcome);
        Assert.Contains("still answering", result.Message!);
        Assert.Equal(42, Assert.Single(kills));
    }

    [Fact]
    public void EnsureRunning_WhenTheSystemWillNotReportTheProcessStartTime_ReusesRatherThanKillingBlindly()
    {
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");
        var kills = new List<int>();

        DashboardLaunchResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("an unproven pid must not be replaced"),
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(null),
            killProcess: pid => { kills.Add(pid); return true; })
            .EnsureRunning(Request(port: 4977, ownVersion: "1.23.0+bbbbbbb"));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Contains("would not report when process 42 started", result.Message!);
        Assert.Empty(kills);
    }

    [Fact]
    public void EnsureRunning_WhenTheRecordedProcessStartTimeIsMinutesOff_ReusesRatherThanKillingBlindly()
    {
        WriteRecord(
            pid: 42,
            port: 4977,
            startedAt: RecordedStart,
            version: "1.22.0+aaaaaaa",
            processStartedAt: RecordedStart);
        var kills = new List<int>();

        DashboardLaunchResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("a recycled pid must not be replaced"),
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart.AddMinutes(4)),
            killProcess: pid => { kills.Add(pid); return true; })
            .EnsureRunning(Request(port: 4977, ownVersion: "1.23.0+bbbbbbb"));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Contains("is not the recorded dashboard", result.Message!);
        Assert.Empty(kills);
    }

    [Fact]
    public void EnsureRunning_WhenAnotherLaunchReplacedTheDashboardWhileThisOneWaited_ReusesTheNewBuild()
    {
        Environment.SetEnvironmentVariable("MILLER_DASHBOARD_DLL", _dashboardDll);
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");
        var kills = new List<int>();

        DashboardLaunchResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("the replace already happened"),
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart),
            killProcess: pid => { kills.Add(pid); return true; },
            tryAcquireLaunchLock: _ =>
            {
                WriteRecord(pid: 99, port: 4977, startedAt: RecordedStart, version: "1.23.0+bbbbbbb");
                return new NoopDisposable();
            })
            .EnsureRunning(Request(port: 4977, ownVersion: "1.23.0+bbbbbbb"));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Equal("already running", result.Message);
        Assert.Empty(kills);
    }

    [Fact]
    public void EnsureRunning_WhenAnotherLaunchHoldsTheLock_ReportsThatItDidNotReplace()
    {
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");
        var kills = new List<int>();

        DashboardLaunchResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("a contended lock must not launch"),
            isHealthy: _ => true,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart),
            killProcess: pid => { kills.Add(pid); return true; },
            tryAcquireLaunchLock: _ => null)
            .EnsureRunning(Request(port: 4977, ownVersion: "1.23.0+bbbbbbb"));

        Assert.Equal(DashboardLaunchOutcome.AlreadyRunning, result.Outcome);
        Assert.Contains("was not replaced", result.Message!);
        Assert.Empty(kills);
    }

    [Fact]
    public void Stop_WhenTheRecordedDashboardIsAlreadyGone_SucceedsAndClearsTheStaleRecord()
    {
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");

        DashboardStopResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("stop must not launch"),
            isHealthy: _ => false,
            probeProcess: _ => null,
            killProcess: _ => throw new InvalidOperationException("a dead pid must not be killed"))
            .Stop(new DashboardStopRequest(Context(), TimeSpan.FromSeconds(1)));

        Assert.Equal(DashboardStopOutcome.NotRunning, result.Outcome);
        Assert.True(result.Success);
        Assert.Contains("process 42 is not running", result.Message);
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void Stop_WhenARecordWithoutAProcessStartTimeHasASilentUrl_RefusesToKillThePid()
    {
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");

        DashboardStopResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("stop must not launch"),
            isHealthy: _ => false,
            probeProcess: _ => new DashboardProcessProbe(RecordedStart),
            killProcess: _ => throw new InvalidOperationException("an unproven pid must not be killed"))
            .Stop(new DashboardStopRequest(Context(), TimeSpan.FromSeconds(1)));

        Assert.Equal(DashboardStopOutcome.NotRunning, result.Outcome);
        Assert.Contains("predates the process-start check", result.Message);
    }

    [Fact]
    public void Stop_WhenALaunchReplacedTheDashboardWhileThisOneWaited_StopsThePidRecordedUnderTheLock()
    {
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");
        bool stopped = false;
        var kills = new List<int>();

        DashboardStopResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("stop must not launch"),
            isHealthy: _ => !stopped,
            probeProcess: _ => stopped ? null : new DashboardProcessProbe(RecordedStart),
            killProcess: pid => { kills.Add(pid); stopped = true; return true; },
            tryAcquireLaunchLock: _ =>
            {
                WriteRecord(
                    pid: 99,
                    port: 4977,
                    startedAt: RecordedStart,
                    version: "1.23.0+bbbbbbb",
                    processStartedAt: RecordedStart);
                return new NoopDisposable();
            })
            .Stop(new DashboardStopRequest(Context(), TimeSpan.FromSeconds(1)));

        Assert.Equal(DashboardStopOutcome.Stopped, result.Outcome);
        Assert.Equal(99, Assert.Single(kills));
        Assert.Equal("1.23.0+bbbbbbb", result.Version);
    }

    [Fact]
    public void Stop_WhenALaunchHoldsTheLock_StopsNothing()
    {
        WriteRecord(pid: 42, port: 4977, startedAt: RecordedStart, version: "1.22.0+aaaaaaa");

        DashboardStopResult result = Launcher(
            startProcess: _ => throw new InvalidOperationException("stop must not launch"),
            isHealthy: _ => throw new InvalidOperationException("a contended lock must not probe"),
            probeProcess: _ => throw new InvalidOperationException("a contended lock must not probe"),
            killProcess: _ => throw new InvalidOperationException("a contended lock must not kill"),
            tryAcquireLaunchLock: _ => null)
            .Stop(new DashboardStopRequest(Context(), TimeSpan.FromSeconds(1)));

        Assert.Equal(DashboardStopOutcome.Failed, result.Outcome);
        Assert.Contains("holds the launch lock", result.Message);
        Assert.True(File.Exists(RecordPath));
    }

    private static readonly DateTimeOffset RecordedStart =
        new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private string RecordPath => Path.Combine(_home, ".miller", "dashboard.json");

    private void WriteRecord(
        int pid,
        int port,
        DateTimeOffset startedAt,
        string? version,
        DateTimeOffset? processStartedAt = null) =>
        WriteRecordText(ServerJson.Serialize(new DashboardProcessMetadata(
            pid,
            $"http://127.0.0.1:{port}",
            startedAt,
            version,
            processStartedAt)));

    private void WriteRecordText(string json)
    {
        Directory.CreateDirectory(Path.Combine(_home, ".miller"));
        File.WriteAllText(RecordPath, json);
    }

    private DashboardLaunchRequest Request(int port, string? ownVersion, TimeSpan? timeout = null) =>
        new(Context(), port, timeout ?? TimeSpan.FromSeconds(1), ownVersion);

    private DashboardCliLauncher Launcher(
        Func<ProcessStartInfo, Process?> startProcess,
        Func<Uri, bool> isHealthy,
        Func<int, DashboardProcessProbe?> probeProcess,
        Func<int, bool> killProcess,
        Action<string, DashboardProcessMetadata>? writeMetadata = null,
        Func<string, IDisposable?>? tryAcquireLaunchLock = null) =>
        new(
            startProcess,
            isHealthy,
            tryAcquireLaunchLock ?? (_ => new NoopDisposable()),
            writeMetadata ?? ((_, _) => { }),
            _ => { },
            probeProcess,
            killProcess);

    private static void WritePidFile(ProcessStartInfo info)
    {
        if (info.Environment.TryGetValue("MILLER_DASHBOARD_PID_FILE", out string? pidPath) && pidPath is not null)
            File.WriteAllText(pidPath, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
    }

    private WorkspaceContext Context() =>
        WorkspaceContext.Create(_root, _dir, _home);

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
