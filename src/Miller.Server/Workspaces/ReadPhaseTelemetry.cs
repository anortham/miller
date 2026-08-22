using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Miller.Core.Graph;
using Miller.Core.Search;
using Miller.Indexing;

namespace Miller.Server.Workspaces;

internal readonly record struct ReadMeasurementSnapshot(long CallCount, long ElapsedTicks);

internal enum SymbolLookupMethodFamily
{
    DocumentCount,
    KnownExtensions,
    Search,
    ResolveDoc,
    FindByName,
    FindBySymbolId,
    FindChildren,
    FindByFilePath,
    FindByFilePathFragment,
    FindFilePathsByFragment,
    IsIndexedFilePath,
    ResolveIndexedFilePath,
}

/// <summary>
/// Which lookup index answered the symbol lookups one read measured. It rides beside the per-method counts so a
/// FindByName burst is attributable from telemetry alone. The 2026-08-21 context latency diagnosis recorded two
/// bursts of 468 FindByName calls at 13.0 s and could not tell the on-disk FTS search sidecar from the in-memory
/// generation projection, because nothing said which one served the read (open question 3).
/// </summary>
internal enum SymbolLookupBackend
{
    /// <summary>No route claimed a backend for this wrapper. Absence of a claim, never a guess at one.</summary>
    Unattributed,

    /// <summary>The on-disk FTS search sidecar answered.</summary>
    SearchSidecar,

    /// <summary>The in-memory whole-generation <c>SymbolSearchProjection</c> answered.</summary>
    SessionProjection,

    /// <summary>
    /// A search sidecar that LAGS the live generation supplied the recall, and every returned row was re-read
    /// from the live artifact (<see cref="LaggingSidecarSymbolLookup"/>). It is its own value because that read
    /// pays both costs; calling it a plain sidecar read would hide the live re-read.
    /// </summary>
    LaggingSidecar,

    /// <summary>
    /// One measuring wrapper served more than one backend, so its counts belong to neither. Recorded rather than
    /// resolved: a wrapper that mixed indexes must say so instead of naming one it cannot prove.
    /// </summary>
    Mixed,
}

internal static class SymbolLookupBackends
{
    /// <summary>The stable name written to the log line and the telemetry metadata.</summary>
    internal static string Name(SymbolLookupBackend backend) => backend switch
    {
        SymbolLookupBackend.SearchSidecar => "search_sidecar",
        SymbolLookupBackend.SessionProjection => "session_projection",
        SymbolLookupBackend.LaggingSidecar => "lagging_sidecar",
        SymbolLookupBackend.Mixed => "mixed",
        _ => "unattributed",
    };

    /// <summary>
    /// Fold a new claim into the standing one. An unclaimed wrapper takes the claim, a repeated claim keeps it,
    /// and two different claims become <see cref="SymbolLookupBackend.Mixed"/>.
    /// </summary>
    internal static SymbolLookupBackend Merge(SymbolLookupBackend current, SymbolLookupBackend declared)
    {
        if (declared == SymbolLookupBackend.Unattributed || current == declared)
            return current;
        return current == SymbolLookupBackend.Unattributed ? declared : SymbolLookupBackend.Mixed;
    }
}

internal enum ContextLookupPhase
{
    SourceRescue,
    QueryRetrieval,
    TermRetrieval,
    AnchorResolution,
    GraphReach,
    SymbolHydration,
    FileNeighbours,
    CandidateOrdering,
}

internal sealed record FtsTextSearchQueryTelemetrySnapshot(
    SearchRequestFamilyTelemetry OpenMetadata,
    SearchRequestFamilyTelemetry OpenChunkMetadata,
    SearchRequestFamilyTelemetry OpenSymbolSpans,
    SearchRequestFamilyTelemetry ConnectionOpen,
    SearchRequestFamilyTelemetry AverageDocumentLength,
    SearchRequestFamilyTelemetry DocumentFrequency,
    SearchRequestFamilyTelemetry StrictCandidates,
    SearchRequestFamilyTelemetry WidenedCandidates,
    SearchRequestFamilyTelemetry CandidateFiltering,
    SearchRequestFamilyTelemetry NarrowTokenScoring,
    SearchRequestFamilyTelemetry FullHydration,
    SearchRequestFamilyTelemetry SymbolSpanHydration,
    SearchRequestFamilyTelemetry RawTextAnalysis,
    SearchRequestFamilyTelemetry SymbolMapping,
    SearchRequestFamilyTelemetry ResultConstruction,
    SearchRequestFamilyTelemetry Scoring,
    SearchRequestFamilyTelemetry FinalOrdering)
{
    internal long TotalCallCount =>
        OpenMetadata.CallCount +
        OpenChunkMetadata.CallCount +
        OpenSymbolSpans.CallCount +
        ConnectionOpen.CallCount +
        AverageDocumentLength.CallCount +
        DocumentFrequency.CallCount +
        StrictCandidates.CallCount +
        WidenedCandidates.CallCount +
        CandidateFiltering.CallCount +
        NarrowTokenScoring.CallCount +
        FullHydration.CallCount +
        SymbolSpanHydration.CallCount +
        RawTextAnalysis.CallCount +
        SymbolMapping.CallCount +
        ResultConstruction.CallCount +
        Scoring.CallCount +
        FinalOrdering.CallCount;
}

internal enum TextContentIndexResolveFamily
{
    Resolve,
    ReadSessionOpen,
    CacheHit,
    CacheMiss,
    IndexLoad,
}

internal readonly record struct TextContentIndexResolveObservation(
    TextContentIndexResolveFamily Family,
    TimeSpan Elapsed);

internal sealed record TextContentIndexResolveTelemetrySnapshot(
    SearchRequestFamilyTelemetry Resolve,
    SearchRequestFamilyTelemetry ReadSessionOpen,
    SearchRequestFamilyTelemetry CacheHit,
    SearchRequestFamilyTelemetry CacheMiss,
    SearchRequestFamilyTelemetry IndexLoad)
{
    internal long TotalCallCount =>
        Resolve.CallCount +
        ReadSessionOpen.CallCount +
        CacheHit.CallCount +
        CacheMiss.CallCount +
        IndexLoad.CallCount;
}

internal readonly record struct LookupMethodTelemetry(long CallCount, long ElapsedMilliseconds);

internal readonly record struct SearchRequestFamilyTelemetry(
    long CallCount,
    long ElapsedMilliseconds,
    long ReturnedRowCount);

internal sealed record FtsSearchQueryTelemetrySnapshot(
    SearchRequestFamilyTelemetry ConnectionOpen,
    SearchRequestFamilyTelemetry AndIntersectionProbe,
    SearchRequestFamilyTelemetry WordCandidates,
    SearchRequestFamilyTelemetry WordHydration,
    SearchRequestFamilyTelemetry WordScoring,
    SearchRequestFamilyTelemetry TrigramCandidates,
    SearchRequestFamilyTelemetry TrigramScoring,
    SearchRequestFamilyTelemetry FinalOrdering)
{
    internal long TotalCallCount =>
        ConnectionOpen.CallCount +
        AndIntersectionProbe.CallCount +
        WordCandidates.CallCount +
        WordHydration.CallCount +
        WordScoring.CallCount +
        TrigramCandidates.CallCount +
        TrigramScoring.CallCount +
        FinalOrdering.CallCount;
}

internal sealed record SearchRequestTelemetrySnapshot(
    SearchRequestFamilyTelemetry FirstQuery,
    SearchRequestFamilyTelemetry ModeVariant,
    SearchRequestFamilyTelemetry WindowVariant,
    SearchRequestFamilyTelemetry ExactRepeat,
    SearchRequestFamilyTelemetry CacheHit,
    SearchRequestFamilyTelemetry And,
    SearchRequestFamilyTelemetry Or,
    long DroppedCallCount)
{
    internal long TotalCallCount =>
        FirstQuery.CallCount +
        ModeVariant.CallCount +
        WindowVariant.CallCount +
        ExactRepeat.CallCount +
        CacheHit.CallCount +
        DroppedCallCount;
}

internal sealed record SymbolLookupTelemetrySnapshot(
    LookupMethodTelemetry DocumentCount,
    LookupMethodTelemetry KnownExtensions,
    LookupMethodTelemetry Search,
    LookupMethodTelemetry ResolveDoc,
    LookupMethodTelemetry FindByName,
    LookupMethodTelemetry FindBySymbolId,
    LookupMethodTelemetry FindChildren,
    LookupMethodTelemetry FindByFilePath,
    LookupMethodTelemetry FindByFilePathFragment,
    LookupMethodTelemetry FindFilePathsByFragment,
    LookupMethodTelemetry IsIndexedFilePath,
    LookupMethodTelemetry ResolveIndexedFilePath)
{
    internal long TotalCallCount =>
        DocumentCount.CallCount +
        KnownExtensions.CallCount +
        Search.CallCount +
        ResolveDoc.CallCount +
        FindByName.CallCount +
        FindBySymbolId.CallCount +
        FindChildren.CallCount +
        FindByFilePath.CallCount +
        FindByFilePathFragment.CallCount +
        FindFilePathsByFragment.CallCount +
        IsIndexedFilePath.CallCount +
        ResolveIndexedFilePath.CallCount;
}

/// <param name="LookupBackend">
/// Which lookup index answered the calls in <paramref name="Delta"/>. It travels in the same record as the
/// per-method counts so a FindByName burst names its own index.
/// </param>
internal sealed record ContextLookupPhaseObservation(
    ContextLookupPhase Phase,
    SymbolLookupBackend LookupBackend,
    SymbolLookupTelemetrySnapshot Delta,
    SymbolLookupTelemetrySnapshot Total,
    SearchRequestTelemetrySnapshot SearchDelta,
    SearchRequestTelemetrySnapshot SearchTotal,
    FtsSearchQueryTelemetrySnapshot FtsSearchDelta,
    FtsSearchQueryTelemetrySnapshot FtsSearchTotal,
    FtsTextSearchQueryTelemetrySnapshot FtsTextSearchDelta,
    FtsTextSearchQueryTelemetrySnapshot FtsTextSearchTotal,
    TextContentIndexResolveTelemetrySnapshot TextContentIndexResolveDelta,
    TextContentIndexResolveTelemetrySnapshot TextContentIndexResolveTotal);

internal enum SearchRequestClassification
{
    FirstQuery,
    ModeVariant,
    WindowVariant,
    ExactRepeat,
}

internal readonly record struct SearchQueryIdentity(string Digest);

internal readonly record struct SearchQueryModeIdentity(SearchQueryIdentity Query, SearchMode Mode);

internal readonly record struct SearchRequestIdentity(SearchQueryIdentity Query, SearchMode Mode, int Limit);

internal readonly record struct SearchRequestObservation(
    SearchRequestIdentity Identity,
    long ElapsedTicks,
    long ReturnedRowCount);

internal sealed class ReadPhaseTelemetry
{
    private readonly MeasuredSymbolLookupIndex _lookup;
    private readonly ReadMeasurementSnapshot _lookupBaseline;
    private readonly ReadMeasurementSnapshot[] _lookupFamilyBaseline;
    private ReadMeasurementSnapshot[] _lookupPhaseBaseline;
    private readonly SearchRequestTelemetryCollector _searchTelemetry = new();
    private SearchRequestTelemetrySnapshot _searchPhaseBaseline = SearchRequestTelemetryCollector.EmptySnapshot;
    private readonly FtsSearchQueryTelemetryCollector _ftsSearchTelemetry = new();
    private FtsSearchQueryMeasurementSnapshot _ftsSearchPhaseBaseline =
        FtsSearchQueryTelemetryCollector.EmptySnapshot;
    private readonly FtsTextSearchQueryTelemetryCollector _ftsTextSearchTelemetry = new();
    private FtsTextSearchQueryMeasurementSnapshot _ftsTextSearchPhaseBaseline =
        FtsTextSearchQueryTelemetryCollector.EmptySnapshot;
    private readonly TextContentIndexResolveTelemetryCollector _textContentIndexResolveTelemetry = new();
    private TextContentIndexResolveTelemetrySnapshot _textContentIndexResolvePhaseBaseline =
        TextContentIndexResolveTelemetryCollector.EmptySnapshot;
    private readonly MeasuredSymbolGraphReachability? _graph;
    private readonly ReadMeasurementSnapshot _graphBaseline;

    public ReadPhaseTelemetry(
        MeasuredSymbolLookupIndex lookup,
        MeasuredSymbolGraphReachability? graph,
        int providerCacheEntries)
    {
        _lookup = lookup;
        _lookupBaseline = lookup.Snapshot();
        _lookupFamilyBaseline = lookup.SnapshotByFamily();
        _lookupPhaseBaseline = _lookupFamilyBaseline;
        _graph = graph;
        _graphBaseline = graph?.Snapshot() ?? default;
        ProviderCacheEntries = providerCacheEntries;
    }

    public long ResolveElapsedMilliseconds { get; private set; }

    public int ProviderCacheEntries { get; }

    /// <summary>Which lookup index answered every measured call on this read.</summary>
    public SymbolLookupBackend LookupBackend => _lookup.Backend;

    public long LookupCallCount => Delta(_lookup.Snapshot(), _lookupBaseline).CallCount;

    public long LookupElapsedMilliseconds =>
        ElapsedMilliseconds(Delta(_lookup.Snapshot(), _lookupBaseline).ElapsedTicks);

    public long GraphCallCount =>
        _graph is null ? 0 : Delta(_graph.Snapshot(), _graphBaseline).CallCount;

    public long GraphElapsedMilliseconds =>
        _graph is null ? 0 : ElapsedMilliseconds(Delta(_graph.Snapshot(), _graphBaseline).ElapsedTicks);

    public void CompleteResolve(long startedAt) =>
        ResolveElapsedMilliseconds = Math.Max(
            0,
            (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

    internal ContextLookupPhaseObservation CompleteLookupPhase(ContextLookupPhase phase)
    {
        ReadMeasurementSnapshot[] current = _lookup.SnapshotByFamily();
        SearchRequestTelemetrySnapshot searchCurrent = _searchTelemetry.Snapshot();
        FtsSearchQueryMeasurementSnapshot ftsSearchCurrent = _ftsSearchTelemetry.Snapshot();
        FtsTextSearchQueryMeasurementSnapshot ftsTextSearchCurrent = _ftsTextSearchTelemetry.Snapshot();
        TextContentIndexResolveTelemetrySnapshot textContentIndexResolveCurrent =
            _textContentIndexResolveTelemetry.Snapshot();
        var observation = new ContextLookupPhaseObservation(
            phase,
            _lookup.Backend,
            LookupTelemetry(current, _lookupPhaseBaseline),
            LookupTelemetry(current, _lookupFamilyBaseline),
            SearchTelemetryDelta(searchCurrent, _searchPhaseBaseline),
            searchCurrent,
            FtsSearchTelemetryDelta(ftsSearchCurrent, _ftsSearchPhaseBaseline),
            FtsSearchTelemetry(ftsSearchCurrent),
            FtsTextSearchTelemetryDelta(ftsTextSearchCurrent, _ftsTextSearchPhaseBaseline),
            FtsTextSearchTelemetry(ftsTextSearchCurrent),
            TextContentIndexResolveTelemetryDelta(
                textContentIndexResolveCurrent,
                _textContentIndexResolvePhaseBaseline),
            textContentIndexResolveCurrent);
        _lookupPhaseBaseline = current;
        _searchPhaseBaseline = searchCurrent;
        _ftsSearchPhaseBaseline = ftsSearchCurrent;
        _ftsTextSearchPhaseBaseline = ftsTextSearchCurrent;
        _textContentIndexResolvePhaseBaseline = textContentIndexResolveCurrent;
        return observation;
    }

    internal IDisposable ActivateSearchTelemetry() =>
        new CompositeActivation(
            _searchTelemetry.Activate(),
            _ftsSearchTelemetry.Activate(),
            _ftsTextSearchTelemetry.Activate(),
            _textContentIndexResolveTelemetry.Activate());

    private static TextContentIndexResolveTelemetrySnapshot TextContentIndexResolveTelemetryDelta(
        TextContentIndexResolveTelemetrySnapshot current,
        TextContentIndexResolveTelemetrySnapshot baseline) =>
        new(
            SearchFamilyDelta(current.Resolve, baseline.Resolve),
            SearchFamilyDelta(current.ReadSessionOpen, baseline.ReadSessionOpen),
            SearchFamilyDelta(current.CacheHit, baseline.CacheHit),
            SearchFamilyDelta(current.CacheMiss, baseline.CacheMiss),
            SearchFamilyDelta(current.IndexLoad, baseline.IndexLoad));

    private static FtsTextSearchQueryTelemetrySnapshot FtsTextSearchTelemetryDelta(
        FtsTextSearchQueryMeasurementSnapshot current,
        FtsTextSearchQueryMeasurementSnapshot baseline) =>
        new(
            FtsTextSearchFamilyDelta(current.OpenMetadata, baseline.OpenMetadata),
            FtsTextSearchFamilyDelta(current.OpenChunkMetadata, baseline.OpenChunkMetadata),
            FtsTextSearchFamilyDelta(current.OpenSymbolSpans, baseline.OpenSymbolSpans),
            FtsTextSearchFamilyDelta(current.ConnectionOpen, baseline.ConnectionOpen),
            FtsTextSearchFamilyDelta(current.AverageDocumentLength, baseline.AverageDocumentLength),
            FtsTextSearchFamilyDelta(current.DocumentFrequency, baseline.DocumentFrequency),
            FtsTextSearchFamilyDelta(current.StrictCandidates, baseline.StrictCandidates),
            FtsTextSearchFamilyDelta(current.WidenedCandidates, baseline.WidenedCandidates),
            FtsTextSearchFamilyDelta(current.CandidateFiltering, baseline.CandidateFiltering),
            FtsTextSearchFamilyDelta(current.NarrowTokenScoring, baseline.NarrowTokenScoring),
            FtsTextSearchFamilyDelta(current.FullHydration, baseline.FullHydration),
            FtsTextSearchFamilyDelta(current.SymbolSpanHydration, baseline.SymbolSpanHydration),
            FtsTextSearchFamilyDelta(current.RawTextAnalysis, baseline.RawTextAnalysis),
            FtsTextSearchFamilyDelta(current.SymbolMapping, baseline.SymbolMapping),
            FtsTextSearchFamilyDelta(current.ResultConstruction, baseline.ResultConstruction),
            FtsTextSearchFamilyDelta(current.Scoring, baseline.Scoring),
            FtsTextSearchFamilyDelta(current.FinalOrdering, baseline.FinalOrdering));

    private static FtsTextSearchQueryTelemetrySnapshot FtsTextSearchTelemetry(
        FtsTextSearchQueryMeasurementSnapshot current) =>
        new(
            FtsTextSearchFamily(current.OpenMetadata),
            FtsTextSearchFamily(current.OpenChunkMetadata),
            FtsTextSearchFamily(current.OpenSymbolSpans),
            FtsTextSearchFamily(current.ConnectionOpen),
            FtsTextSearchFamily(current.AverageDocumentLength),
            FtsTextSearchFamily(current.DocumentFrequency),
            FtsTextSearchFamily(current.StrictCandidates),
            FtsTextSearchFamily(current.WidenedCandidates),
            FtsTextSearchFamily(current.CandidateFiltering),
            FtsTextSearchFamily(current.NarrowTokenScoring),
            FtsTextSearchFamily(current.FullHydration),
            FtsTextSearchFamily(current.SymbolSpanHydration),
            FtsTextSearchFamily(current.RawTextAnalysis),
            FtsTextSearchFamily(current.SymbolMapping),
            FtsTextSearchFamily(current.ResultConstruction),
            FtsTextSearchFamily(current.Scoring),
            FtsTextSearchFamily(current.FinalOrdering));

    private static SearchRequestFamilyTelemetry FtsTextSearchFamilyDelta(
        FtsTextSearchQueryFamilyMeasurement current,
        FtsTextSearchQueryFamilyMeasurement baseline) =>
        FtsTextSearchFamily(new FtsTextSearchQueryFamilyMeasurement(
            Math.Max(0, current.CallCount - baseline.CallCount),
            Math.Max(0, current.ElapsedTicks - baseline.ElapsedTicks),
            Math.Max(0, current.ReturnedRowCount - baseline.ReturnedRowCount)));

    private static SearchRequestFamilyTelemetry FtsTextSearchFamily(
        FtsTextSearchQueryFamilyMeasurement measurement) =>
        new(
            measurement.CallCount,
            Math.Max(0, (long)TimeSpan.FromTicks(measurement.ElapsedTicks).TotalMilliseconds),
            measurement.ReturnedRowCount);

    private static ReadMeasurementSnapshot Delta(
        ReadMeasurementSnapshot current,
        ReadMeasurementSnapshot baseline) =>
        new(
            Math.Max(0, current.CallCount - baseline.CallCount),
            Math.Max(0, current.ElapsedTicks - baseline.ElapsedTicks));

    private static long ElapsedMilliseconds(long ticks) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds);

    private static SymbolLookupTelemetrySnapshot LookupTelemetry(
        IReadOnlyList<ReadMeasurementSnapshot> current,
        IReadOnlyList<ReadMeasurementSnapshot> baseline)
    {
        LookupMethodTelemetry Family(SymbolLookupMethodFamily family)
        {
            int index = (int)family;
            ReadMeasurementSnapshot delta = Delta(current[index], baseline[index]);
            return new LookupMethodTelemetry(delta.CallCount, ElapsedMilliseconds(delta.ElapsedTicks));
        }

        return new SymbolLookupTelemetrySnapshot(
            Family(SymbolLookupMethodFamily.DocumentCount),
            Family(SymbolLookupMethodFamily.KnownExtensions),
            Family(SymbolLookupMethodFamily.Search),
            Family(SymbolLookupMethodFamily.ResolveDoc),
            Family(SymbolLookupMethodFamily.FindByName),
            Family(SymbolLookupMethodFamily.FindBySymbolId),
            Family(SymbolLookupMethodFamily.FindChildren),
            Family(SymbolLookupMethodFamily.FindByFilePath),
            Family(SymbolLookupMethodFamily.FindByFilePathFragment),
            Family(SymbolLookupMethodFamily.FindFilePathsByFragment),
            Family(SymbolLookupMethodFamily.IsIndexedFilePath),
            Family(SymbolLookupMethodFamily.ResolveIndexedFilePath));
    }

    private static SearchRequestTelemetrySnapshot SearchTelemetryDelta(
        SearchRequestTelemetrySnapshot current,
        SearchRequestTelemetrySnapshot baseline) =>
        new(
            SearchFamilyDelta(current.FirstQuery, baseline.FirstQuery),
            SearchFamilyDelta(current.ModeVariant, baseline.ModeVariant),
            SearchFamilyDelta(current.WindowVariant, baseline.WindowVariant),
            SearchFamilyDelta(current.ExactRepeat, baseline.ExactRepeat),
            SearchFamilyDelta(current.CacheHit, baseline.CacheHit),
            SearchFamilyDelta(current.And, baseline.And),
            SearchFamilyDelta(current.Or, baseline.Or),
            Math.Max(0, current.DroppedCallCount - baseline.DroppedCallCount));

    private static SearchRequestFamilyTelemetry SearchFamilyDelta(
        SearchRequestFamilyTelemetry current,
        SearchRequestFamilyTelemetry baseline) =>
        new(
            Math.Max(0, current.CallCount - baseline.CallCount),
            Math.Max(0, current.ElapsedMilliseconds - baseline.ElapsedMilliseconds),
            Math.Max(0, current.ReturnedRowCount - baseline.ReturnedRowCount));

    private static FtsSearchQueryTelemetrySnapshot FtsSearchTelemetryDelta(
        FtsSearchQueryMeasurementSnapshot current,
        FtsSearchQueryMeasurementSnapshot baseline) =>
        new(
            FtsSearchFamilyDelta(current.ConnectionOpen, baseline.ConnectionOpen),
            FtsSearchFamilyDelta(current.AndIntersectionProbe, baseline.AndIntersectionProbe),
            FtsSearchFamilyDelta(current.WordCandidates, baseline.WordCandidates),
            FtsSearchFamilyDelta(current.WordHydration, baseline.WordHydration),
            FtsSearchFamilyDelta(current.WordScoring, baseline.WordScoring),
            FtsSearchFamilyDelta(current.TrigramCandidates, baseline.TrigramCandidates),
            FtsSearchFamilyDelta(current.TrigramScoring, baseline.TrigramScoring),
            FtsSearchFamilyDelta(current.FinalOrdering, baseline.FinalOrdering));

    private static FtsSearchQueryTelemetrySnapshot FtsSearchTelemetry(
        FtsSearchQueryMeasurementSnapshot current) =>
        new(
            FtsSearchFamily(current.ConnectionOpen),
            FtsSearchFamily(current.AndIntersectionProbe),
            FtsSearchFamily(current.WordCandidates),
            FtsSearchFamily(current.WordHydration),
            FtsSearchFamily(current.WordScoring),
            FtsSearchFamily(current.TrigramCandidates),
            FtsSearchFamily(current.TrigramScoring),
            FtsSearchFamily(current.FinalOrdering));

    private static SearchRequestFamilyTelemetry FtsSearchFamilyDelta(
        FtsSearchQueryFamilyMeasurement current,
        FtsSearchQueryFamilyMeasurement baseline) =>
        FtsSearchFamily(new FtsSearchQueryFamilyMeasurement(
            Math.Max(0, current.CallCount - baseline.CallCount),
            Math.Max(0, current.ElapsedTicks - baseline.ElapsedTicks),
            Math.Max(0, current.ReturnedRowCount - baseline.ReturnedRowCount)));

    private static SearchRequestFamilyTelemetry FtsSearchFamily(
        FtsSearchQueryFamilyMeasurement measurement) =>
        new(
            measurement.CallCount,
            Math.Max(0, (long)TimeSpan.FromTicks(measurement.ElapsedTicks).TotalMilliseconds),
            measurement.ReturnedRowCount);

    private sealed class CompositeActivation(
        IDisposable first,
        IDisposable second,
        IDisposable third,
        IDisposable fourth) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            fourth.Dispose();
            third.Dispose();
            second.Dispose();
            first.Dispose();
        }
    }
}

internal sealed class TextContentIndexResolveTelemetryCollector
{
    private static readonly AsyncLocal<TextContentIndexResolveTelemetryCollector?> CurrentCollector = new();
    private readonly long[] _calls = new long[Enum.GetValues<TextContentIndexResolveFamily>().Length];
    private readonly long[] _elapsedTicks = new long[Enum.GetValues<TextContentIndexResolveFamily>().Length];

    internal static TextContentIndexResolveTelemetrySnapshot EmptySnapshot { get; } =
        new(default, default, default, default, default);

    internal static TextContentIndexResolveTelemetryCollector? Current => CurrentCollector.Value;

    internal IDisposable Activate()
    {
        TextContentIndexResolveTelemetryCollector? previous = CurrentCollector.Value;
        CurrentCollector.Value = this;
        return new Activation(previous);
    }

    internal void Record(TextContentIndexResolveObservation observation)
    {
        int index = (int)observation.Family;
        Interlocked.Increment(ref _calls[index]);
        Interlocked.Add(ref _elapsedTicks[index], observation.Elapsed.Ticks);
    }

    internal TextContentIndexResolveTelemetrySnapshot Snapshot() =>
        new(
            Family(TextContentIndexResolveFamily.Resolve),
            Family(TextContentIndexResolveFamily.ReadSessionOpen),
            Family(TextContentIndexResolveFamily.CacheHit),
            Family(TextContentIndexResolveFamily.CacheMiss),
            Family(TextContentIndexResolveFamily.IndexLoad));

    private SearchRequestFamilyTelemetry Family(TextContentIndexResolveFamily family)
    {
        int index = (int)family;
        return new SearchRequestFamilyTelemetry(
            Interlocked.Read(ref _calls[index]),
            Math.Max(0, (long)TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks[index])).TotalMilliseconds),
            0);
    }

    private sealed class Activation(TextContentIndexResolveTelemetryCollector? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CurrentCollector.Value = previous;
        }
    }
}

internal sealed class SearchRequestTelemetryAccumulator
{
    private readonly long[] _classificationCalls = new long[Enum.GetValues<SearchRequestClassification>().Length];
    private readonly long[] _classificationElapsedTicks = new long[Enum.GetValues<SearchRequestClassification>().Length];
    private readonly long[] _classificationRows = new long[Enum.GetValues<SearchRequestClassification>().Length];
    private readonly long[] _modeCalls = new long[2];
    private readonly long[] _modeElapsedTicks = new long[2];
    private readonly long[] _modeRows = new long[2];
    private long _cacheHitCalls;
    private long _cacheHitRows;
    private long _droppedCalls;

    internal void Add(SearchRequestClassification classification, SearchRequestObservation observation)
    {
        int index = (int)classification;
        _classificationCalls[index]++;
        _classificationElapsedTicks[index] += observation.ElapsedTicks;
        _classificationRows[index] += observation.ReturnedRowCount;
    }

    internal void AddMode(SearchRequestObservation observation)
    {
        int index = observation.Identity.Mode == SearchMode.And ? 0 : 1;
        _modeCalls[index]++;
        _modeElapsedTicks[index] += observation.ElapsedTicks;
        _modeRows[index] += observation.ReturnedRowCount;
    }

    internal void AddDropped(long callCount) => _droppedCalls += callCount;

    internal void AddCacheHit(long returnedRows)
    {
        _cacheHitCalls++;
        _cacheHitRows += returnedRows;
    }

    internal SearchRequestTelemetrySnapshot Snapshot() =>
        new(
            Classification(SearchRequestClassification.FirstQuery),
            Classification(SearchRequestClassification.ModeVariant),
            Classification(SearchRequestClassification.WindowVariant),
            Classification(SearchRequestClassification.ExactRepeat),
            new SearchRequestFamilyTelemetry(_cacheHitCalls, 0, _cacheHitRows),
            Mode(0),
            Mode(1),
            _droppedCalls);

    private SearchRequestFamilyTelemetry Classification(SearchRequestClassification classification)
    {
        int index = (int)classification;
        return new SearchRequestFamilyTelemetry(
            _classificationCalls[index],
            ElapsedMilliseconds(_classificationElapsedTicks[index]),
            _classificationRows[index]);
    }

    private SearchRequestFamilyTelemetry Mode(int index) =>
        new(
            _modeCalls[index],
            ElapsedMilliseconds(_modeElapsedTicks[index]),
            _modeRows[index]);

    private static long ElapsedMilliseconds(long ticks) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds);
}

internal sealed class SearchRequestTelemetryCollector
{
    private const int IdentityCapacity = 64;
    private static readonly AsyncLocal<SearchRequestTelemetryCollector?> CurrentCollector = new();

    private readonly byte[] _identityKey = RandomNumberGenerator.GetBytes(32);
    private readonly HashSet<SearchQueryIdentity> _seenQueries = [];
    private readonly HashSet<SearchQueryModeIdentity> _seenQueryModes = [];
    private readonly HashSet<SearchRequestIdentity> _seenRequests = [];
    private readonly SearchRequestTelemetryAccumulator _total = new();

    internal static SearchRequestTelemetrySnapshot EmptySnapshot { get; } =
        new(default, default, default, default, default, default, default, 0);

    internal static SearchRequestTelemetryCollector? Current => CurrentCollector.Value;

    internal IDisposable Activate()
    {
        SearchRequestTelemetryCollector? previous = CurrentCollector.Value;
        CurrentCollector.Value = this;
        return new Activation(previous);
    }

    internal void Record(string? query, int limit, SearchMode mode, long elapsedTicks, long returnedRows)
    {
        byte[] digest = HMACSHA256.HashData(_identityKey, Encoding.UTF8.GetBytes(query ?? string.Empty));
        var request = new SearchRequestIdentity(
            new SearchQueryIdentity(Convert.ToHexString(digest)),
            mode,
            limit);
        var observation = new SearchRequestObservation(request, elapsedTicks, returnedRows);
        _total.AddMode(observation);

        SearchRequestClassification? classification = Classify(request);
        if (classification is null)
        {
            _total.AddDropped(1);
            return;
        }
        _total.Add(classification.Value, observation);
    }

    internal SearchRequestTelemetrySnapshot Snapshot() => _total.Snapshot();

    internal void RecordCacheHit(long returnedRows) => _total.AddCacheHit(returnedRows);

    private SearchRequestClassification? Classify(SearchRequestIdentity request)
    {
        if (_seenRequests.Contains(request))
            return SearchRequestClassification.ExactRepeat;

        var queryMode = new SearchQueryModeIdentity(request.Query, request.Mode);
        if (_seenQueryModes.Contains(queryMode))
        {
            if (_seenRequests.Count >= IdentityCapacity)
                return null;
            _seenRequests.Add(request);
            return SearchRequestClassification.WindowVariant;
        }

        if (_seenQueries.Contains(request.Query))
        {
            if (_seenQueryModes.Count >= IdentityCapacity || _seenRequests.Count >= IdentityCapacity)
                return null;
            _seenQueryModes.Add(queryMode);
            _seenRequests.Add(request);
            return SearchRequestClassification.ModeVariant;
        }

        if (_seenQueries.Count >= IdentityCapacity ||
            _seenQueryModes.Count >= IdentityCapacity ||
            _seenRequests.Count >= IdentityCapacity)
        {
            return null;
        }
        _seenQueries.Add(request.Query);
        _seenQueryModes.Add(queryMode);
        _seenRequests.Add(request);
        return SearchRequestClassification.FirstQuery;
    }

    private sealed class Activation(SearchRequestTelemetryCollector? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CurrentCollector.Value = previous;
        }
    }
}

/// <param name="backend">
/// Which index <paramref name="inner"/> is. It defaults to <see cref="SymbolLookupBackend.Unattributed"/> so an
/// undeclared wrapper reports absence of a claim rather than a guess; the production routes all declare through
/// <c>WorkspaceIndexProvider.MeasureFamilyLookup</c>.
/// </param>
internal sealed class MeasuredSymbolLookupIndex(
    ISymbolLookupIndex inner,
    SymbolLookupBackend backend = SymbolLookupBackend.Unattributed) : ISymbolLookupIndex
{
    private readonly long[] _elapsedTicks = new long[Enum.GetValues<SymbolLookupMethodFamily>().Length];
    private readonly long[] _callCounts = new long[Enum.GetValues<SymbolLookupMethodFamily>().Length];
    private int _backend = (int)backend;

    /// <summary>Which lookup index this wrapper measures, or <c>Mixed</c> when two routes claimed it apart.</summary>
    internal SymbolLookupBackend Backend => (SymbolLookupBackend)Volatile.Read(ref _backend);

    /// <summary>
    /// Claim this wrapper for one backend. A cached wrapper is reached by several routes, so the claim FOLDS:
    /// a repeat keeps the standing value and a conflict becomes <see cref="SymbolLookupBackend.Mixed"/>.
    /// </summary>
    internal void DeclareBackend(SymbolLookupBackend declared)
    {
        while (true)
        {
            int current = Volatile.Read(ref _backend);
            var merged = SymbolLookupBackends.Merge((SymbolLookupBackend)current, declared);
            if (merged == (SymbolLookupBackend)current)
                return;
            if (Interlocked.CompareExchange(ref _backend, (int)merged, current) == current)
                return;
        }
    }

    public int DocumentCount => Measure(SymbolLookupMethodFamily.DocumentCount, () => inner.DocumentCount);

    public IReadOnlySet<string> KnownExtensions =>
        Measure(SymbolLookupMethodFamily.KnownExtensions, () => inner.KnownExtensions);

    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or)
    {
        long startedAt = Stopwatch.GetTimestamp();
        long returnedRows = 0;
        try
        {
            IReadOnlyList<SearchHit> result = inner.Search(query, limit, mode);
            returnedRows = result.Count;
            return result;
        }
        finally
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - startedAt;
            int family = (int)SymbolLookupMethodFamily.Search;
            Interlocked.Increment(ref _callCounts[family]);
            Interlocked.Add(ref _elapsedTicks[family], elapsedTicks);
            SearchRequestTelemetryCollector.Current?.Record(query, limit, mode, elapsedTicks, returnedRows);
        }
    }

    public IndexedSymbol Resolve(int docId) => Measure(SymbolLookupMethodFamily.ResolveDoc, () => inner.Resolve(docId));

    public IReadOnlyList<IndexedSymbol> FindByName(string name) =>
        Measure(SymbolLookupMethodFamily.FindByName, () => inner.FindByName(name));

    public IndexedSymbol? FindBySymbolId(string symbolId) =>
        Measure(SymbolLookupMethodFamily.FindBySymbolId, () => inner.FindBySymbolId(symbolId));

    public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) =>
        Measure(SymbolLookupMethodFamily.FindChildren, () => inner.FindChildren(parentId));

    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) =>
        Measure(SymbolLookupMethodFamily.FindByFilePath, () => inner.FindByFilePath(filePath));

    public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
        Measure(SymbolLookupMethodFamily.FindByFilePathFragment, () => inner.FindByFilePathFragment(query, limit));

    public IReadOnlyList<string> FindFilePathsByFragment(string query, int limit) =>
        Measure(SymbolLookupMethodFamily.FindFilePathsByFragment, () => inner.FindFilePathsByFragment(query, limit));

    public bool IsIndexedFilePath(string path) =>
        Measure(SymbolLookupMethodFamily.IsIndexedFilePath, () => inner.IsIndexedFilePath(path));

    public string? ResolveIndexedFilePath(string target) =>
        Measure(SymbolLookupMethodFamily.ResolveIndexedFilePath, () => inner.ResolveIndexedFilePath(target));

    public ReadMeasurementSnapshot Snapshot()
    {
        ReadMeasurementSnapshot[] families = SnapshotByFamily();
        return new(
            families.Sum(static family => family.CallCount),
            families.Sum(static family => family.ElapsedTicks));
    }

    internal ReadMeasurementSnapshot[] SnapshotByFamily()
    {
        var snapshot = new ReadMeasurementSnapshot[_callCounts.Length];
        for (int index = 0; index < snapshot.Length; index++)
        {
            snapshot[index] = new ReadMeasurementSnapshot(
                Interlocked.Read(ref _callCounts[index]),
                Interlocked.Read(ref _elapsedTicks[index]));
        }
        return snapshot;
    }

    private T Measure<T>(SymbolLookupMethodFamily family, Func<T> action)
    {
        int index = (int)family;
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            return action();
        }
        finally
        {
            Interlocked.Increment(ref _callCounts[index]);
            Interlocked.Add(ref _elapsedTicks[index], Stopwatch.GetTimestamp() - startedAt);
        }
    }
}

internal sealed class MeasuredSymbolGraphReachability : ISymbolGraphReachability
{
    private readonly ISymbolGraphReachability _inner;
    private long _elapsedTicks;
    private long _callCount;

    internal MeasuredSymbolGraphReachability(
        ISymbolGraphReachability inner,
        Action<GraphStatementObservation>? statementObserver = null)
    {
        _inner = inner;
        if (inner is SqliteSymbolGraphIndex sqlite)
            sqlite.StatementObserver = statementObserver;
    }

    public GraphReachResult ReachWithEvidence(
        IEnumerable<string> starts,
        int maxDepth,
        int limit,
        Direction dir) => Measure(() => _inner.ReachWithEvidence(starts, maxDepth, limit, dir));

    public IReadOnlyList<ReachedNode> Reach(
        IEnumerable<string> starts,
        int maxDepth,
        int limit,
        Direction dir) => Measure(() => _inner.Reach(starts, maxDepth, limit, dir));

    public IReadOnlyList<string>? ShortestPath(string from, string to, int maxDepth) =>
        Measure(() => _inner.ShortestPath(from, to, maxDepth));

    public GraphPath? ShortestPathWithEvidence(
        string from,
        string to,
        int maxDepth,
        Func<GraphNeighbour, bool> edgeFilter) =>
        Measure(() => _inner.ShortestPathWithEvidence(from, to, maxDepth, edgeFilter));

    public ReadMeasurementSnapshot Snapshot() =>
        new(Interlocked.Read(ref _callCount), Interlocked.Read(ref _elapsedTicks));

    private T Measure<T>(Func<T> action)
    {
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            return action();
        }
        finally
        {
            Interlocked.Increment(ref _callCount);
            Interlocked.Add(ref _elapsedTicks, Stopwatch.GetTimestamp() - startedAt);
        }
    }
}
