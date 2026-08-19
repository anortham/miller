using System.Diagnostics;
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
            Assert.Equal(_root, captured.Environment[CtEnvironment.WorkspaceRoot]);
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
