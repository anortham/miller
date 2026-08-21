using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Miller.Server.Tools;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The <c>tests</c> verbs issued against a linked worktree reach the FAMILY daemon on the repo's
/// main checkout: <c>run</c> and <c>stop</c> submit routed commands to its endpoint, <c>start</c>
/// anchors the spawn there, and a status read under the kill switch still creates nothing.
/// </summary>
public sealed class TestsWorktreeRoutingTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-tests-route-").FullName;

    private string MainRoot => Path.Combine(_dir, "main");
    private string WorktreeRoot => Path.Combine(_dir, "wt");
    private string MillerHomeDir => Path.Combine(_dir, "home", ".miller");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Status_under_the_kill_switch_creates_nothing_anywhere()
    {
        BuildLinkedWorktree();
        EnableMain();

        TestsStatusResult status = TestsCore.Status(new TestsCoreRequest(
            WorkspaceRoot: WorktreeRoot,
            MillerHome: MillerHomeDir,
            KillSwitch: "off"));

        Assert.False(status.Enabled);
        Assert.False(Directory.Exists(Path.Combine(WorktreeRoot, ".miller")));
        Assert.False(File.Exists(CtSchema.DbPathFor(WorktreeRoot)));
        Assert.False(Directory.Exists(MillerHomeDir));
    }

    [Fact]
    public async Task Stop_on_an_adopted_worktree_detaches_via_the_family_daemon()
    {
        BuildLinkedWorktree();
        EnableMain();
        using CtDaemonLease? family = CtDaemonLease.TryAcquire(MainRoot, "test");
        Assert.NotNull(family);

        using var cts = new CancellationTokenSource();
        Task acker = AckRequestsAsync(MainRoot, "detached", cts.Token);
        TestsStopResult result;
        try
        {
            result = await Task.Run(() => TestsCore.Stop(new TestsCoreRequest(WorkspaceRoot: WorktreeRoot)));
        }
        finally
        {
            await cts.CancelAsync();
            await acker;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("detached", result.Status);
        Assert.Contains(Path.GetFullPath(MainRoot), result.Reason, StringComparison.Ordinal);

        // The command traveled to the MAIN endpoint carrying the worktree's identity...
        CtDaemonCommandRequest routed = Assert.Single(ReadRequests(MainRoot));
        Assert.Equal(CtDaemonCommandKind.Stop, routed.Kind);
        Assert.Equal(Path.GetFullPath(WorktreeRoot), routed.WorkspaceRoot);

        // ...and nothing appeared under the worktree, and no daemon died.
        Assert.False(Directory.Exists(Path.Combine(WorktreeRoot, ".miller")));
        Assert.NotNull(CtDaemonLease.TryReadLive(MainRoot));
    }

    [Fact]
    public async Task Run_on_an_adopted_worktree_submits_to_the_family_daemon()
    {
        BuildLinkedWorktree();
        EnableMain();
        using CtDaemonLease? family = CtDaemonLease.TryAcquire(MainRoot, "test");
        Assert.NotNull(family);

        using var cts = new CancellationTokenSource();
        Task acker = AckRequestsAsync(MainRoot, "run", cts.Token);
        TestsRunResult result;
        try
        {
            result = await Task.Run(() => TestsCore.Run(new TestsCoreRequest(
                WorkspaceRoot: WorktreeRoot,
                MillerHome: MillerHomeDir)));
        }
        finally
        {
            await cts.CancelAsync();
            await acker;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CtRunExecution.Daemon, result.Execution);

        CtDaemonCommandRequest routed = Assert.Single(ReadRequests(MainRoot));
        Assert.Equal(CtDaemonCommandKind.Run, routed.Kind);
        Assert.Equal(Path.GetFullPath(WorktreeRoot), routed.WorkspaceRoot);

        // Routing executed nothing locally: the worktree gained no ct.db.
        Assert.False(File.Exists(CtSchema.DbPathFor(WorktreeRoot)));
    }

    [Fact]
    public void Start_on_a_worktree_anchors_the_family_daemon_at_the_main_checkout()
    {
        BuildLinkedWorktree();
        EnableMain();
        ProcessStartInfo? captured = null;
        using Process stub = StartStub();
        try
        {
            TestsServeResult result = TestsCore.Start(new TestsCoreRequest(
                WorkspaceRoot: WorktreeRoot,
                Hooks: new TestsCoreHooks(StartProcess: info =>
                {
                    captured = info;
                    return stub;
                })));

            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(captured);
            Assert.Equal(
                Path.GetFullPath(MainRoot),
                captured.Environment[CtEnvironment.DaemonWorkspaceRoot]);
            Assert.Contains(Path.GetFullPath(MainRoot), result.Reason, StringComparison.Ordinal);
        }
        finally
        {
            try { stub.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    private static IReadOnlyList<CtDaemonCommandRequest> ReadRequests(string endpointRoot)
    {
        string dir = CtDaemonProtocol.CommandDirectory(endpointRoot);
        if (!Directory.Exists(dir))
            return [];
        return Directory.EnumerateFiles(dir, "*.request.json")
            .Select(path => Path.GetFileName(path)[..^".request.json".Length])
            .Select(id => CtCommandChannel.TryReadRequest(endpointRoot, id))
            .Where(request => request is not null)
            .Select(request => request!)
            .ToArray();
    }

    /// <summary>Plays the daemon's side of the file channel: acks every request it sees.</summary>
    private static Task AckRequestsAsync(string endpointRoot, string reason, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            string dir = CtDaemonProtocol.CommandDirectory(endpointRoot);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Directory.Exists(dir))
                {
                    foreach (string path in Directory.EnumerateFiles(dir, "*.request.json"))
                    {
                        string id = Path.GetFileName(path)[..^".request.json".Length];
                        if (CtCommandChannel.TryReadAck(endpointRoot, id) is not null)
                            continue;
                        CtCommandChannel.WriteAck(endpointRoot, new CtDaemonCommandAck(
                            id,
                            CtDaemonCommandState.Acknowledged,
                            DateTimeOffset.UtcNow,
                            reason));
                    }
                }

                try
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, CancellationToken.None);

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

    private void BuildLinkedWorktree()
    {
        string adminDir = Path.Combine(MainRoot, ".git", "worktrees", "wt");
        Directory.CreateDirectory(adminDir);
        File.WriteAllText(Path.Combine(adminDir, "commondir"), "../..\n");
        Directory.CreateDirectory(WorktreeRoot);
        File.WriteAllText(Path.Combine(WorktreeRoot, ".git"), $"gitdir: {adminDir}\n");
    }

    private void EnableMain()
    {
        string marker = ContinuousTestPolicy.EnabledMarkerPath(MainRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, string.Empty);
    }
}
