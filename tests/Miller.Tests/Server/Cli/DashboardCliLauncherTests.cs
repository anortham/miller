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

        Assert.Equal($"http://127.0.0.1:4977/?workspace_id={id}", url.ToString());
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
        Assert.Equal("http://127.0.0.1:4977/", result.Url.GetLeftPart(UriPartial.Path));
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

    private WorkspaceContext Context() =>
        WorkspaceContext.Create(_root, _dir, _home);

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
