using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

public sealed class WorkspaceRegistryScanPublisherTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "miller-registry-publisher-" + Guid.NewGuid());

    public WorkspaceRegistryScanPublisherTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void TryMarkScanned_NullWorkspaceId_SkipsRegistryWrite()
    {
        var logger = new RecordingLogger();
        var publisher = new WorkspaceRegistryScanPublisher(
            (_, _, _) => throw new InvalidOperationException("should not mark registry"),
            logger);

        bool marked = publisher.TryMarkScanned(Workspace(), workspaceId: null, revision: 7);

        Assert.False(marked);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void TryMarkScanned_RegistryFailure_LogsAndReturnsFalse()
    {
        var logger = new RecordingLogger();
        var publisher = new WorkspaceRegistryScanPublisher(
            (_, _, _) => throw new IOException("registry locked"),
            logger);

        bool marked = publisher.TryMarkScanned(Workspace(), "workspace-1", revision: 8);

        Assert.False(marked);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Failed to update workspace registry revision", entry.Message);
    }

    [Fact]
    public void MarkScanned_RegistryFailure_PropagatesForRequiredStartupStamp()
    {
        var publisher = new WorkspaceRegistryScanPublisher(
            (_, _, _) => throw new IOException("registry locked"),
            new RecordingLogger());

        Assert.Throws<IOException>(() => publisher.MarkScanned(Workspace(), "workspace-1", revision: 9));
    }

    [Fact]
    public void MarkScanned_RecordsReadyScanRevision()
    {
        WorkspaceContext workspace = Workspace();
        string workspaceId = workspace.WorkspaceId!;
        var publisher = new WorkspaceRegistryScanPublisher();

        WorkspaceRegistryRow row = publisher.MarkScanned(workspace, workspaceId, revision: 10);

        Assert.Equal(WorkspaceRegistryState.Ready, row.State);
        Assert.Equal(10, row.LastRevision);
        Assert.NotNull(row.LastScanAt);
        Assert.Equal(workspace.CanonicalRoot, row.CanonicalRoot);
        Assert.Equal(workspace.CanonicalExtractDbPath, row.IndexDbPath);
    }

    private WorkspaceContext Workspace()
    {
        string root = Path.Combine(_temp, "repo");
        string home = Path.Combine(_temp, "home");
        Directory.CreateDirectory(root);
        string canonicalRoot = Path.GetFullPath(root);
        string workspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);
        string canonicalDb = Path.Combine(canonicalRoot, ".miller", "symbols.db");
        return WorkspaceContext.Create(root, AppContext.BaseDirectory, home) with
        {
            WorkspaceId = workspaceId,
            CanonicalRoot = canonicalRoot,
            CanonicalExtractDbPath = canonicalDb,
        };
    }

    public void Dispose()
    {
        SqliteConnectionClearPools();
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    private static void SqliteConnectionClearPools() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));
    }
}
