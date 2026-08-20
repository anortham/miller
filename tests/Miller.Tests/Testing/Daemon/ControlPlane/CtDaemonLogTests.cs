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
