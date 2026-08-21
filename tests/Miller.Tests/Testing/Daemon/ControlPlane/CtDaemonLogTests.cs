using System.Globalization;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

public sealed class CtDaemonLogTests : IDisposable
{
    private readonly string _root;

    public CtDaemonLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miller-ct-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Role_IsCt()
    {
        Assert.Equal("ct", CtDaemonLog.Role);
    }

    [Fact]
    public void LogFilePaths_MatchSharedDailyPair_AndDoNotCreateDirectories()
    {
        var when = new DateTimeOffset(2026, 8, 19, 15, 4, 5, TimeSpan.Zero);
        string logsDir = CtDaemonLog.LogsDirectory(_root);
        (string human, string json) = CtDaemonLog.LogFilePaths(logsDir, when);

        Assert.Equal(Path.Combine(_root, ".miller", "logs"), logsDir);
        Assert.Equal(Path.Combine(logsDir, "miller-20260819.log"), human);
        Assert.Equal(Path.Combine(logsDir, "miller-20260819.jsonl"), json);
        Assert.False(Directory.Exists(logsDir));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
    }

    /// <summary>
    /// The daemon logs for every adopted worktree, and a lifecycle line written while one is being
    /// torn down used to re-mint <c>&lt;worktree&gt;/.miller/logs/</c> — recreating the root
    /// <c>git worktree remove</c> had just deleted, which left the worktree untracked-dirty and made
    /// git refuse. A log line is an observable signal, never a reason to resurrect a workspace.
    /// </summary>
    [Fact]
    public void Write_DoesNotRecreateAWorkspaceRootThatIsGone()
    {
        string gone = Path.Combine(_root, "removed-worktree");

        CtDaemonLog.Write(gone, "detached");

        Assert.False(Directory.Exists(gone), "a log line recreated the workspace root");
    }

    /// <summary>
    /// The guard is on the ROOT, not on <c>.miller</c>. A worktree that inherits its opt-in from the
    /// main checkout has no <c>.miller</c> of its own, and refusing those would silently drop that
    /// worktree's logs.
    /// </summary>
    [Fact]
    public void Write_StillCreatesTheLogsDirectoryInsideARootThatExists()
    {
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));

        CtDaemonLog.Write(_root, "adopted");

        Assert.True(Directory.Exists(CtDaemonLog.LogsDirectory(_root)));
    }

    [Fact]
    public void Write_AppendsRoleCtToSharedPair()
    {
        var when = new DateTimeOffset(2026, 8, 19, 16, 0, 0, TimeSpan.Zero);
        CtDaemonLog.Write(_root, "lease acquired", when);

        (string human, string json) = CtDaemonLog.LogFilePaths(CtDaemonLog.LogsDirectory(_root), when);
        string humanText = File.ReadAllText(human);
        string jsonText = File.ReadAllText(json);

        Assert.Contains("role:ct", humanText, StringComparison.Ordinal);
        Assert.Contains($"pid:{Environment.ProcessId}", humanText, StringComparison.Ordinal);
        Assert.Contains("lease acquired", humanText, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"ct\"", jsonText, StringComparison.Ordinal);
        Assert.Contains("lease acquired", jsonText, StringComparison.Ordinal);
    }

    /// <summary>
    /// One call must write exactly ONE record. Callers interpolate values they do not control — a project
    /// path, a provider's own output — and on any filesystem that permits a newline in a name an
    /// un-flattened value writes EXTRA lines that read like genuine records. Both files are checked: the
    /// human log because a forged line there misleads a reader, and the JSONL because one object per line
    /// is the format's whole contract.
    /// </summary>
    [Fact]
    public void Write_KeepsAMultiLineMessageOnOneRecordInBothFiles()
    {
        var when = new DateTimeOffset(2026, 8, 19, 16, 0, 0, TimeSpan.Zero);
        CtDaemonLog.Write(
            _root,
            "ct discovery failed project=/tmp/a" + (char)10
                + "16:00:00.000 [INF] (role:ct pid:1 cid:) CtDaemon: all green",
            when);

        (string human, string json) = CtDaemonLog.LogFilePaths(CtDaemonLog.LogsDirectory(_root), when);
        string[] humanLines = File.ReadAllLines(human);
        string[] jsonLines = File.ReadAllLines(json);

        Assert.Single(humanLines);
        Assert.Single(jsonLines);
        Assert.Contains("all green", humanLines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A wedged provider that emits megabytes on every poll must not fill the disk one record at a time.
    /// The bound is far above any real stack trace, and the marker says plainly that something was cut.
    /// </summary>
    [Fact]
    public void Write_BoundsAnEnormousRecord()
    {
        var when = new DateTimeOffset(2026, 8, 19, 16, 0, 0, TimeSpan.Zero);
        CtDaemonLog.Write(_root, new string('x', 5_000_000), when);

        (string human, _) = CtDaemonLog.LogFilePaths(CtDaemonLog.LogsDirectory(_root), when);
        string line = Assert.Single(File.ReadAllLines(human));

        Assert.EndsWith("...[truncated]", line, StringComparison.Ordinal);
        Assert.True(line.Length < 10_000, $"the record was {line.Length} characters");
    }

    /// <summary>
    /// A file parked at the logs-directory path makes <c>Directory.CreateDirectory</c> throw
    /// <see cref="IOException"/>, so the log path can never be created. The write must degrade to
    /// no log and must leave the blocking file untouched.
    /// </summary>
    [Fact]
    public void Write_ReturnsNormally_WhenLogsDirectoryCannotBeCreated()
    {
        string millerDir = Path.Combine(_root, ".miller");
        Directory.CreateDirectory(millerDir);
        string blocker = Path.Combine(millerDir, "logs");
        File.WriteAllText(blocker, "blocker");

        var when = new DateTimeOffset(2026, 8, 19, 16, 0, 0, TimeSpan.Zero);
        CtDaemonLog.Write(_root, "lease acquired", when);

        Assert.True(File.Exists(blocker));
        Assert.Equal("blocker", File.ReadAllText(blocker));
    }

    /// <summary>
    /// A directory parked at the human log-file path makes the append throw
    /// <see cref="UnauthorizedAccessException"/>. The guard wraps the whole body, so the write
    /// stops at the first failed append and produces neither file of the daily pair.
    /// </summary>
    [Fact]
    public void Write_ReturnsNormally_WhenLogFileCannotBeOpened()
    {
        var when = new DateTimeOffset(2026, 8, 19, 16, 0, 0, TimeSpan.Zero);
        string logsDir = CtDaemonLog.LogsDirectory(_root);
        (string human, string json) = CtDaemonLog.LogFilePaths(logsDir, when);
        Directory.CreateDirectory(human);

        CtDaemonLog.Write(_root, "lease acquired", when);

        Assert.True(Directory.Exists(human));
        Assert.False(File.Exists(json));
    }

    /// <summary>
    /// The gate invariant. Task 2 calls <c>CtDaemonLog.Write</c> from the last-resort catch blocks
    /// of the daemon's <c>RunAsync</c> loop, where an escaping exception ends the loop. A failing
    /// append must not stop the caller: the loop must complete every iteration.
    /// </summary>
    [Fact]
    public void Write_FailingAppend_DoesNotStopACallerLoop()
    {
        var when = new DateTimeOffset(2026, 8, 19, 16, 0, 0, TimeSpan.Zero);
        string logsDir = CtDaemonLog.LogsDirectory(_root);
        (string human, string json) = CtDaemonLog.LogFilePaths(logsDir, when);
        Directory.CreateDirectory(human);

        const int Iterations = 5;
        int completed = 0;
        for (int i = 0; i < Iterations; i++)
        {
            CtDaemonLog.Write(_root, "poll " + i.ToString(CultureInfo.InvariantCulture), when);
            completed++;
        }

        Assert.Equal(Iterations, completed);
        Assert.False(File.Exists(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Write_ThrowsArgumentException_ForBadWorkspaceRoot(string? workspaceRoot)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => CtDaemonLog.Write(workspaceRoot!, "lease acquired"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Write_ThrowsArgumentException_ForBadMessage(string? message)
    {
        Assert.ThrowsAny<ArgumentException>(() => CtDaemonLog.Write(_root, message!));

        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
    }
}
