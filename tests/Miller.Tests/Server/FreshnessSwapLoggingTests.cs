using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the LEVEL each freshness swap logs at. A revision advance is the routine converge step every process
/// emits twice a second, so at Information it dominated the shared log (41% of one day's file); it belongs at
/// Debug. A REPLACED artifact means a full rebuild was promoted underneath the reader — rare, and the signal the
/// restarted revision counter cannot carry — so it stays at Information.
/// </summary>
public sealed class FreshnessSwapLoggingTests : IDisposable
{
    private const string Ws = "ws-swaplog-001";
    private readonly List<string> _tempHomes = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string dir in _tempHomes)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void RoutineRevisionAdvance_LogsAtDebug_NotInformation()
    {
        using var fx = NewFixture(revision: 7);
        var holder = new IndexHolder(Index(fx), builtRevision: 1);
        var log = new CapturingLogger();

        PollResult result = NewService(fx, holder, log).PollNow();

        Assert.True(result.Swapped);
        Assert.Contains(log.Entries, entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("swapped index view to revision 7"));
        Assert.DoesNotContain(log.Entries, entry => entry.Level == LogLevel.Information);
    }

    [Fact]
    public void ReplacedArtifact_LogsAtInformation_EvenWhenTheRevisionDidNotAdvance()
    {
        using var fx = NewFixture(revision: 7);
        var holder = new IndexHolder(Index(fx), builtRevision: 7, builtArtifactId: "artifact-before-the-rebuild");
        var log = new CapturingLogger();

        PollResult result = NewService(fx, holder, log).PollNow();

        Assert.True(result.Swapped);
        Assert.Contains(log.Entries, entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("artifact replaced"));
    }

    [Fact]
    public void NoSwap_LogsNeitherLevel()
    {
        using var fx = NewFixture(revision: 4);
        var holder = new IndexHolder(Index(fx), builtRevision: 4);
        var log = new CapturingLogger();

        PollResult result = NewService(fx, holder, log).PollNow();

        Assert.False(result.Swapped);
        Assert.DoesNotContain(log.Entries, entry =>
            entry.Level is LogLevel.Information or LogLevel.Debug && entry.Message.Contains("swapped index view"));
    }

    [Fact]
    public void NoSwapMessageClaimsARebuild_BecauseTheStorePathSwapsADeferredFactory()
    {
        using var fx = NewFixture(revision: 7);
        var holder = new IndexHolder(Index(fx), builtRevision: 1);
        var log = new CapturingLogger();

        NewService(fx, holder, log).PollNow();

        Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("rebuilt"));
    }

    private static JulieDbFixture NewFixture(long revision) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            JulieDbFixture.DefaultRows,
            workspaceId: Ws,
            revisions: [new JulieDbFixture.RevisionRow(revision)]);

    private static MillerRepositoryIndex Index(JulieDbFixture fx) =>
        MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));

    private FreshnessService NewService(JulieDbFixture fx, IndexHolder holder, ILogger<FreshnessService> logger)
    {
        string tempHome = Path.Combine(Path.GetTempPath(), "miller-swaplog-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        _tempHomes.Add(tempHome);

        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance)
        {
            TestHomeDirectoryOverride = tempHome,
        };
        var workspace = WorkspaceContext.Create(Path.GetDirectoryName(fx.DbPath)!, AppContext.BaseDirectory, tempHome) with
        {
            ExtractDbPath = fx.DbPath,
            WorkspaceId = Ws,
        };
        bootstrap.SeedForTest(workspace, holder);
        return new FreshnessService(bootstrap, logger);
    }

    private sealed class CapturingLogger : ILogger<FreshnessService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
