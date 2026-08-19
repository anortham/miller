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
}
