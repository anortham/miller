using Microsoft.Extensions.Logging;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the idle bind heartbeat defense: a completed no-work bind is Debug so a quiet 250 ms
/// drain tick cannot flood the shared daily log. Failed binds and did-work binds stay Information.
/// Import and sidecar_total stay Information.
/// </summary>
public sealed class IndexerPhaseRecordTests
{
    [Fact]
    public void CompletedNoWorkBind_LogsAtDebug_NotInformation()
    {
        var log = new RecordingLogger();
        var sink = new LoggingIndexerPhaseSink(log);

        sink.Record(Bind(didWork: false, IndexerPhaseOutcomes.Completed));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("indexer_phase_record", entry.Message, StringComparison.Ordinal);
        Assert.Contains(IndexerPhaseNames.Bind, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public void FailedBind_LogsAtInformation()
    {
        var log = new RecordingLogger();
        var sink = new LoggingIndexerPhaseSink(log);

        sink.Record(Bind(didWork: false, IndexerPhaseOutcomes.Failed));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(IndexerPhaseNames.Bind, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedDidWorkBind_LogsAtInformation()
    {
        var log = new RecordingLogger();
        var sink = new LoggingIndexerPhaseSink(log);

        sink.Record(Bind(didWork: true, IndexerPhaseOutcomes.Completed));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(IndexerPhaseNames.Bind, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedImport_LogsAtInformation()
    {
        AssertInformation(IndexerPhaseNames.Import);
    }

    [Fact]
    public void CompletedSidecarTotal_LogsAtInformation()
    {
        AssertInformation(IndexerPhaseNames.SidecarTotal);
    }

    private static void AssertInformation(string phase)
    {
        var log = new RecordingLogger();
        var sink = new LoggingIndexerPhaseSink(log);

        sink.Record(new IndexerPhaseRecord(
            phase,
            TimeSpan.FromMilliseconds(12),
            IndexerPhaseOutcomes.Completed,
            StoreSequence: 4,
            DidWork: false));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(phase, entry.Message, StringComparison.Ordinal);
    }

    private static IndexerPhaseRecord Bind(bool didWork, string outcome) =>
        new(IndexerPhaseNames.Bind, TimeSpan.FromMilliseconds(1), outcome, StoreSequence: null, DidWork: didWork);

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
