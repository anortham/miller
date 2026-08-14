using Microsoft.Extensions.Logging;

namespace Miller.Server.Hosting;

internal static class IndexerPhaseNames
{
    public const string Import = "import";
    public const string Resolve = "resolve";
    public const string Bind = "bind";
    public const string CoordinatorTotal = "coordinator_total";
    public const string Content = "content";
    public const string Search = "search";
    public const string Metrics = "metrics";
    public const string Vector = "vector";
    public const string SidecarTotal = "sidecar_total";
    public const string StartupTotal = "startup_total";
}

internal static class IndexerPhaseOutcomes
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

internal sealed record IndexerPhaseRecord(
    string Phase,
    TimeSpan Elapsed,
    string Outcome,
    long? StoreSequence,
    bool DidWork)
{
    public double ElapsedMilliseconds => Math.Max(0, Elapsed.TotalMilliseconds);
}

internal interface IIndexerPhaseSink
{
    void Record(IndexerPhaseRecord record);
}

internal static class IndexerPhaseSinkExtensions
{
    public static void RecordSafely(
        this IIndexerPhaseSink sink,
        string phase,
        TimeSpan elapsed,
        string outcome,
        long? storeSequence,
        bool didWork)
    {
        try
        {
            sink.Record(new IndexerPhaseRecord(phase, elapsed, outcome, storeSequence, didWork));
        }
        catch
        {
        }
    }
}

internal sealed class IndexerPhaseScope : IDisposable
{
    private readonly IIndexerPhaseSink _sink;
    private readonly string _phase;
    private readonly long _started;
    private string _outcome = IndexerPhaseOutcomes.Failed;
    private long? _storeSequence;
    private bool _didWork;
    private bool _disposed;

    public IndexerPhaseScope(IIndexerPhaseSink sink, string phase)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _phase = phase ?? throw new ArgumentNullException(nameof(phase));
        _started = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public void Complete(long? storeSequence, bool didWork)
    {
        _outcome = IndexerPhaseOutcomes.Completed;
        _storeSequence = storeSequence;
        _didWork = didWork;
    }

    public void Skip(long? storeSequence = null)
    {
        _outcome = IndexerPhaseOutcomes.Skipped;
        _storeSequence = storeSequence;
        _didWork = false;
    }

    public void Fail(long? storeSequence = null)
    {
        _outcome = IndexerPhaseOutcomes.Failed;
        _storeSequence = storeSequence;
        _didWork = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sink.RecordSafely(
            _phase,
            System.Diagnostics.Stopwatch.GetElapsedTime(_started),
            _outcome,
            _storeSequence,
            _didWork);
    }
}

internal sealed class NullIndexerPhaseSink : IIndexerPhaseSink
{
    public static NullIndexerPhaseSink Instance { get; } = new();

    private NullIndexerPhaseSink()
    {
    }

    public void Record(IndexerPhaseRecord record)
    {
    }
}

internal sealed class LoggingIndexerPhaseSink(ILogger logger) : IIndexerPhaseSink
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public void Record(IndexerPhaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _logger.LogInformation(
            "indexer_phase_record {Phase} {ElapsedMilliseconds} {Outcome} {StoreSequence} {DidWork}",
            record.Phase,
            record.ElapsedMilliseconds,
            record.Outcome,
            record.StoreSequence,
            record.DidWork);
    }
}
