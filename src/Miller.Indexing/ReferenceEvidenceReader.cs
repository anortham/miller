using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Miller.Core.References;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;

namespace Miller.Indexing;

/// <summary>Inbound, outgoing, and kind-partitioned evidence read from one artifact snapshot.</summary>
public sealed record ReferenceEvidenceBundle(
    ReferenceEvidenceSet Inbound,
    OutgoingReferenceEvidenceSet Outgoing,
    IReadOnlyDictionary<ReferenceKind, ReferenceEvidenceSet> InboundKinds,
    IReadOnlyDictionary<ReferenceKind, OutgoingReferenceEvidenceSet> OutgoingKinds);

internal enum ReferenceEvidenceReadPhase
{
    TargetInfo,
    InboundExact,
    InboundFallback,
    OutgoingExact,
    OutgoingFallback,
}

internal sealed record ReferenceEvidenceObservation(
    ReferenceEvidenceReadPhase Phase,
    int RequestedCandidateCount,
    int ReturnedRawRowCount,
    double ElapsedMilliseconds,
    IReadOnlyList<string> QueryPlan);

internal sealed record ReferenceEvidenceObservationOptions(
    Action<ReferenceEvidenceObservation> Observe,
    bool CaptureQueryPlan = false);

/// <summary>Reads bounded, normalized reference evidence keyed by resolved symbol IDs.</summary>
public static partial class ReferenceEvidenceReader
{
    private const int ReadManyChunkSize = 128;

    private static readonly string[] RequiredResolutionTables =
        ["reference_sites", "pending_relationships"];

    internal static QueryTimeResolutionReader ReaderFor(
        IWorkspaceReadSession session,
        SqliteConnection connection)
    {
        if (session is WorkspaceReadHandle handle && handle.ResolutionReader is { } fromHandle)
            return fromHandle;
        if (session is IQueryTimeResolutionHost host)
            return host.Resolution;
        return new QueryTimeResolutionReader(RevisionFactCache.LoadFromArtifact(connection), visibility: null);
    }

    private static ReferenceEvidenceBundle ReadForSymbolResolved(
        QueryTimeResolutionReader reader,
        SqliteConnection connection,
        string symbolId,
        ReferenceEvidenceQuery inboundQuery,
        ReferenceEvidenceQuery outgoingQuery,
        ReferenceEvidenceBounds kindBounds,
        IReadOnlyList<ReferenceKind> kinds)
    {
        string targetName = ReadTargetName(connection, symbolId);
        List<ReferenceEvidence> inboundExactRows = TakeSingle(reader.ReadInboundExact(connection, [symbolId]), symbolId);
        List<ReferenceEvidence> inboundFallbackRows = TakeSingle(reader.ReadInboundFallback(connection, [symbolId]), symbolId);
        int sameNameDefinitionCount = CountDefinitions(connection, symbolId, targetName);
        List<OutgoingReferenceEvidence> outgoingExactRows = TakeSingle(reader.ReadOutgoingExact(connection, [symbolId]), symbolId);
        List<OutgoingReferenceEvidence> outgoingFallbackRows = TakeSingle(reader.ReadOutgoingFallback(connection, [symbolId]), symbolId);
        ReferenceEvidenceSnapshot snapshot = ReadSnapshot(connection);
        ReferenceKind[] distinctKinds = kinds.Distinct().ToArray();
        return new ReferenceEvidenceBundle(
            BuildInboundSet(inboundExactRows, inboundFallbackRows, inboundQuery, sameNameDefinitionCount, snapshot),
            BuildOutgoingSet(outgoingExactRows, outgoingFallbackRows, outgoingQuery, snapshot),
            distinctKinds.ToDictionary(
                static kind => kind,
                kind => BuildInboundSet(
                    inboundExactRows,
                    inboundFallbackRows,
                    new ReferenceEvidenceQuery(kindBounds, kind),
                    sameNameDefinitionCount,
                    snapshot)),
            distinctKinds.ToDictionary(
                static kind => kind,
                kind => BuildOutgoingSet(
                    outgoingExactRows,
                    outgoingFallbackRows,
                    new ReferenceEvidenceQuery(kindBounds, kind),
                    snapshot)));
    }

    private static ReferenceEvidenceSet ReadInboundResolved(
        QueryTimeResolutionReader reader,
        SqliteConnection connection,
        string targetSymbolId,
        ReferenceEvidenceQuery query)
    {
        string targetName = ReadTargetName(connection, targetSymbolId);
        return BuildInboundSet(
            TakeSingle(reader.ReadInboundExact(connection, [targetSymbolId]), targetSymbolId),
            TakeSingle(reader.ReadInboundFallback(connection, [targetSymbolId]), targetSymbolId),
            query,
            CountDefinitions(connection, targetSymbolId, targetName),
            ReadSnapshot(connection));
    }

    private static OutgoingReferenceEvidenceSet ReadOutgoingResolved(
        QueryTimeResolutionReader reader,
        SqliteConnection connection,
        string containingSymbolId,
        ReferenceEvidenceQuery query)
    {
        RequireSymbol(connection, containingSymbolId);
        return BuildOutgoingSet(
            TakeSingle(reader.ReadOutgoingExact(connection, [containingSymbolId]), containingSymbolId),
            TakeSingle(reader.ReadOutgoingFallback(connection, [containingSymbolId]), containingSymbolId),
            query,
            ReadSnapshot(connection));
    }

    private static IReadOnlyDictionary<string, ReferenceEvidenceBundle> ReadManyResolved(
        QueryTimeResolutionReader reader,
        SqliteConnection connection,
        IReadOnlyList<string> orderedIds,
        ReferenceEvidenceQuery query,
        ReferenceEvidenceObservationOptions? observationOptions)
    {
        Dictionary<string, List<ReferenceEvidence>> inboundExact = reader.ReadInboundExact(connection, orderedIds);
        Dictionary<string, List<ReferenceEvidence>> inboundFallback = reader.ReadInboundFallback(connection, orderedIds);
        Dictionary<string, List<OutgoingReferenceEvidence>> outgoingExact = reader.ReadOutgoingExact(connection, orderedIds);
        Dictionary<string, List<OutgoingReferenceEvidence>> outgoingFallback = reader.ReadOutgoingFallback(connection, orderedIds);
        ReferenceEvidenceSnapshot snapshot = ReadSnapshot(connection);
        var result = new Dictionary<string, ReferenceEvidenceBundle>(orderedIds.Count, StringComparer.Ordinal);
        foreach (string symbolId in orderedIds)
        {
            string targetName = ReadTargetName(connection, symbolId);
            result[symbolId] = new ReferenceEvidenceBundle(
                BuildInboundSet(
                    inboundExact.GetValueOrDefault(symbolId, []),
                    inboundFallback.GetValueOrDefault(symbolId, []),
                    query,
                    CountDefinitions(connection, symbolId, targetName),
                    snapshot),
                BuildOutgoingSet(
                    outgoingExact.GetValueOrDefault(symbolId, []),
                    outgoingFallback.GetValueOrDefault(symbolId, []),
                    query,
                    snapshot),
                new Dictionary<ReferenceKind, ReferenceEvidenceSet>(),
                new Dictionary<ReferenceKind, OutgoingReferenceEvidenceSet>());
            observationOptions?.Observe(new ReferenceEvidenceObservation(
                ReferenceEvidenceReadPhase.InboundExact,
                orderedIds.Count,
                inboundExact.GetValueOrDefault(symbolId, []).Count,
                0,
                QueryPlan: []));
        }

        return result;
    }

    private static List<T> TakeSingle<T>(Dictionary<string, List<T>> rows, string symbolId) =>
        rows.TryGetValue(symbolId, out List<T>? list) ? list : [];

    public static ReferenceEvidenceBundle ReadForSymbol(
        IWorkspaceReadSession session,
        string symbolId,
        ReferenceEvidenceQuery inboundQuery,
        ReferenceEvidenceQuery outgoingQuery,
        ReferenceEvidenceBounds kindBounds,
        IReadOnlyList<ReferenceKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        inboundQuery.Validate();
        outgoingQuery.Validate();
        kindBounds.Validate();
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
            throw new ArgumentException("At least one reference kind is required.", nameof(kinds));

        return session.Read(connection =>
            ReadForSymbolResolved(
                ReaderFor(session, connection),
                connection,
                symbolId,
                inboundQuery,
                outgoingQuery,
                kindBounds,
                kinds));
    }

    /// <summary>Read inbound, outgoing, and selected relationship kinds for one symbol from one snapshot.</summary>
    public static ReferenceEvidenceBundle ReadForSymbol(
        string dbPath,
        string symbolId,
        ReferenceEvidenceQuery inboundQuery,
        ReferenceEvidenceQuery outgoingQuery,
        ReferenceEvidenceBounds kindBounds,
        IReadOnlyList<ReferenceKind> kinds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        inboundQuery.Validate();
        outgoingQuery.Validate();
        kindBounds.Validate();
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
            throw new ArgumentException("At least one reference kind is required.", nameof(kinds));

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);
        QueryTimeResolutionReader reader = new(RevisionFactCache.LoadFromArtifact(connection), visibility: null);
        return ReadForSymbolResolved(
            reader,
            connection,
            symbolId,
            inboundQuery,
            outgoingQuery,
            kindBounds,
            kinds);
    }

    /// <summary>Read bounded inbound and outgoing evidence for several symbols from one snapshot.</summary>
    public static IReadOnlyDictionary<string, ReferenceEvidenceBundle> ReadMany(
        IWorkspaceReadSession session,
        IReadOnlyList<string> symbolIds,
        ReferenceEvidenceQuery query) =>
        ReadManyObserved(session, symbolIds, query, observationOptions: null);

    internal static IReadOnlyDictionary<string, ReferenceEvidenceBundle> ReadManyObserved(
        IWorkspaceReadSession session,
        IReadOnlyList<string> symbolIds,
        ReferenceEvidenceQuery query,
        ReferenceEvidenceObservationOptions? observationOptions)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(symbolIds);
        query.Validate();

        var orderedIds = new List<string>(symbolIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string symbolId in symbolIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
            if (seen.Add(symbolId))
                orderedIds.Add(symbolId);
        }

        if (orderedIds.Count == 0)
            return new Dictionary<string, ReferenceEvidenceBundle>(StringComparer.Ordinal);

        return session.Read(connection =>
            ReadManyResolved(ReaderFor(session, connection), connection, orderedIds, query, observationOptions));
    }

    /// <summary>Read exact inbound sites and separately typed fallback candidates for one symbol.</summary>
    public static ReferenceEvidenceSet Read(
        string dbPath,
        string targetSymbolId,
        ReferenceEvidenceBounds bounds) =>
        Read(dbPath, targetSymbolId, new ReferenceEvidenceQuery(bounds));

    /// <summary>Read one filtered, stateless inbound evidence page.</summary>
    public static ReferenceEvidenceSet Read(
        string dbPath,
        string targetSymbolId,
        ReferenceEvidenceQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSymbolId);
        query.Validate();

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);
        QueryTimeResolutionReader reader = new(RevisionFactCache.LoadFromArtifact(connection), visibility: null);
        return ReadInboundResolved(reader, connection, targetSymbolId, query);
    }

    public static ReferenceEvidenceSet Read(
        IWorkspaceReadSession session,
        string targetSymbolId,
        ReferenceEvidenceBounds bounds) =>
        Read(session, targetSymbolId, new ReferenceEvidenceQuery(bounds));

    public static ReferenceEvidenceSet Read(
        IWorkspaceReadSession session,
        string targetSymbolId,
        ReferenceEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSymbolId);
        query.Validate();
        return session.Read(connection =>
            ReadInboundResolved(ReaderFor(session, connection), connection, targetSymbolId, query));
    }

    /// <summary>Read several independently bounded inbound relationship kinds from one artifact snapshot.</summary>
    public static IReadOnlyDictionary<ReferenceKind, ReferenceEvidenceSet> ReadKinds(
        string dbPath,
        string targetSymbolId,
        ReferenceEvidenceBounds bounds,
        IReadOnlyList<ReferenceKind> kinds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSymbolId);
        bounds.Validate();
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
            throw new ArgumentException("At least one reference kind is required.", nameof(kinds));

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        QueryTimeResolutionReader reader = new(RevisionFactCache.LoadFromArtifact(connection), visibility: null);
        JulieSchemaGate.Verify(connection);
        string targetName = ReadTargetName(connection, targetSymbolId);
        List<ReferenceEvidence> exactRows = TakeSingle(reader.ReadInboundExact(connection, [targetSymbolId]), targetSymbolId);
        List<ReferenceEvidence> fallbackRows = TakeSingle(reader.ReadInboundFallback(connection, [targetSymbolId]), targetSymbolId);
        int sameNameDefinitionCount = CountDefinitions(connection, targetSymbolId, targetName);
        ReferenceEvidenceSnapshot snapshot = ReadSnapshot(connection);
        return kinds
            .Distinct()
            .ToDictionary(
                static kind => kind,
                kind => BuildInboundSet(
                    exactRows,
                    fallbackRows,
                    new ReferenceEvidenceQuery(bounds, kind),
                    sameNameDefinitionCount,
                    snapshot));
    }

    private static ReferenceEvidenceSet BuildInboundSet(
        List<ReferenceEvidence> allExactRows,
        List<ReferenceEvidence> allFallbackRows,
        ReferenceEvidenceQuery query,
        int sameNameDefinitionCount,
        ReferenceEvidenceSnapshot snapshot)
    {
        var exactRows = FilterKind(allExactRows, query.Kind);
        var exact = Deduplicate(exactRows);
        int exactAvailable = exact.Count;
        var boundedExact = exact.Skip(query.ExactOffset).Take(query.Bounds.ExactLimit).ToArray();

        var fallbackRows = FilterKind(allFallbackRows, query.Kind);
        var fallbackCandidates = Deduplicate(fallbackRows);
        IReadOnlyList<ReferenceEvidence> fallback;
        int fallbackAvailable = fallbackCandidates.Count;
        ReferenceFallbackStatus fallbackStatus;
        if (sameNameDefinitionCount > 1)
        {
            fallback = Array.Empty<ReferenceEvidence>();
            fallbackStatus = ReferenceFallbackStatus.SuppressedAmbiguousName;
        }
        else
        {
            fallback = fallbackCandidates
                .Skip(query.FallbackOffset)
                .Take(query.Bounds.FallbackLimit)
                .ToArray();
            fallbackStatus = fallbackCandidates.Count == 0
                ? ReferenceFallbackStatus.NoCandidates
                : ReferenceFallbackStatus.Available;
        }

        return new ReferenceEvidenceSet(
            boundedExact,
            fallback,
            new ReferenceEvidenceCoverage(
                exactRows.Count,
                exactAvailable,
                boundedExact.Length,
                fallbackAvailable,
                fallback.Count,
                sameNameDefinitionCount,
                exactAvailable > query.ExactOffset + boundedExact.Length,
                fallbackStatus == ReferenceFallbackStatus.Available &&
                fallbackAvailable > query.FallbackOffset + fallback.Count,
                fallbackStatus),
            snapshot)
        {
            ExactCallerSymbolIds = ExactContainingSymbolIds(exact, callLike: true),
            ExactReferencedBySymbolIds = ExactContainingSymbolIds(exact, callLike: false),
        };
    }

    /// <summary>Read resolved outgoing sites and separately typed unresolved fallbacks for one symbol.</summary>
    public static OutgoingReferenceEvidenceSet ReadOutgoing(
        string dbPath,
        string containingSymbolId,
        ReferenceEvidenceBounds bounds) =>
        ReadOutgoing(dbPath, containingSymbolId, new ReferenceEvidenceQuery(bounds));

    /// <summary>Read one filtered, stateless outgoing evidence page.</summary>
    public static OutgoingReferenceEvidenceSet ReadOutgoing(
        string dbPath,
        string containingSymbolId,
        ReferenceEvidenceQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(containingSymbolId);
        query.Validate();

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);
        QueryTimeResolutionReader reader = new(RevisionFactCache.LoadFromArtifact(connection), visibility: null);
        return ReadOutgoingResolved(reader, connection, containingSymbolId, query);
    }

    public static OutgoingReferenceEvidenceSet ReadOutgoing(
        IWorkspaceReadSession session,
        string containingSymbolId,
        ReferenceEvidenceBounds bounds) =>
        ReadOutgoing(session, containingSymbolId, new ReferenceEvidenceQuery(bounds));

    public static OutgoingReferenceEvidenceSet ReadOutgoing(
        IWorkspaceReadSession session,
        string containingSymbolId,
        ReferenceEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(containingSymbolId);
        query.Validate();
        return session.Read(connection =>
            ReadOutgoingResolved(ReaderFor(session, connection), connection, containingSymbolId, query));
    }

    /// <summary>Read several independently bounded outgoing relationship kinds from one artifact snapshot.</summary>
    public static IReadOnlyDictionary<ReferenceKind, OutgoingReferenceEvidenceSet> ReadOutgoingKinds(
        string dbPath,
        string containingSymbolId,
        ReferenceEvidenceBounds bounds,
        IReadOnlyList<ReferenceKind> kinds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(containingSymbolId);
        bounds.Validate();
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
            throw new ArgumentException("At least one reference kind is required.", nameof(kinds));

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        QueryTimeResolutionReader reader = new(RevisionFactCache.LoadFromArtifact(connection), visibility: null);
        JulieSchemaGate.Verify(connection);
        RequireSymbol(connection, containingSymbolId);
        List<OutgoingReferenceEvidence> exactRows =
            TakeSingle(reader.ReadOutgoingExact(connection, [containingSymbolId]), containingSymbolId);
        List<OutgoingReferenceEvidence> fallbackRows =
            TakeSingle(reader.ReadOutgoingFallback(connection, [containingSymbolId]), containingSymbolId);
        ReferenceEvidenceSnapshot snapshot = ReadSnapshot(connection);
        return kinds
            .Distinct()
            .ToDictionary(
                static kind => kind,
                kind => BuildOutgoingSet(
                    exactRows,
                    fallbackRows,
                    new ReferenceEvidenceQuery(bounds, kind),
                    snapshot));
    }

    private static OutgoingReferenceEvidenceSet BuildOutgoingSet(
        List<OutgoingReferenceEvidence> allExactRows,
        List<OutgoingReferenceEvidence> allFallbackRows,
        ReferenceEvidenceQuery query,
        ReferenceEvidenceSnapshot snapshot)
    {
        var exactRows = FilterOutgoingKind(allExactRows, query.Kind);
        var exact = DeduplicateOutgoing(exactRows);
        var fallbackRows = FilterOutgoingKind(allFallbackRows, query.Kind);
        var fallback = DeduplicateOutgoing(fallbackRows);
        var boundedExact = exact.Skip(query.ExactOffset).Take(query.Bounds.ExactLimit).ToArray();
        var boundedFallback = fallback
            .Skip(query.FallbackOffset)
            .Take(query.Bounds.FallbackLimit)
            .ToArray();

        return new OutgoingReferenceEvidenceSet(
            boundedExact,
            boundedFallback,
            new OutgoingReferenceEvidenceCoverage(
                exactRows.Count,
                exact.Count,
                boundedExact.Length,
                fallback.Count,
                boundedFallback.Length,
                exact.Count > query.ExactOffset + boundedExact.Length,
                fallback.Count > query.FallbackOffset + boundedFallback.Length),
            snapshot);
    }

    /// <summary>Read the current extractor artifact identity used by stateless reference continuations.</summary>
    public static ReferenceEvidenceSnapshot ReadSnapshot(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        return ReadSnapshot(connection);
    }

    public static ReferenceEvidenceSnapshot ReadSnapshot(IWorkspaceReadSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Read(connection =>
        {
            JulieSchemaGate.Verify(connection);
            return ReadSnapshot(connection);
        });
    }


    private static ReferenceEvidenceSnapshot ReadSnapshot(SqliteConnection connection)
    {
        using var artifact = connection.CreateCommand();
        artifact.CommandText =
            "SELECT value FROM artifact_metadata WHERE key = 'artifact_id' LIMIT 1;";
        string artifactId = artifact.ExecuteScalar() as string
            ?? throw new IncompatibleExtractException(
                "Reference evidence requires artifact_metadata.artifact_id.");

        using var revision = connection.CreateCommand();
        revision.CommandText = "SELECT COALESCE(MAX(revision_id), 0) FROM extraction_revisions;";
        long revisionId = Convert.ToInt64(
            revision.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
        return new ReferenceEvidenceSnapshot(artifactId, revisionId);
    }

    private static string ReadTargetName(SqliteConnection connection, string targetSymbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM symbols WHERE symbol_id = $target;";
        command.Parameters.AddWithValue("$target", targetSymbolId);
        return command.ExecuteScalar() as string
            ?? throw new ArgumentException($"Unknown symbol ID '{targetSymbolId}'.", nameof(targetSymbolId));
    }

    private static void RequireSymbol(SqliteConnection connection, string symbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM symbols WHERE symbol_id = $symbol;";
        command.Parameters.AddWithValue("$symbol", symbolId);
        if (command.ExecuteScalar() is null)
            throw new ArgumentException($"Unknown symbol ID '{symbolId}'.", nameof(symbolId));
    }

    private static void RequireResolutionTables(SqliteConnection connection)
    {
        foreach (string table in RequiredResolutionTables)
        {
            if (!SqliteSchemaObjects.Exists(connection, table))
                throw new IncompatibleExtractException(
                    $"Reference evidence requires the '{table}' table. Restore the pinned julie-extract artifact.");
        }
    }

    private static int CountDefinitions(SqliteConnection connection, string targetSymbolId, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM symbols
            WHERE name = $name
              AND (symbol_id = $target OR kind NOT IN ('constructor', 'import'));
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$target", targetSymbolId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }


    private static List<ReferenceEvidence> Deduplicate(IEnumerable<ReferenceEvidence> rows) =>
        WithoutRedundantSpanlessRows(
            rows.GroupBy(SiteKey)
                .Select(group => group
                    .OrderBy(row => SourcePrecedence(row.Source))
                    .ThenByDescending(row => row.Confidence)
                    .ThenBy(row => row.ContainingSymbolId, StringComparer.Ordinal)
                    .First()),
            row => row.IsExact,
            row => new SpanlessCoverageKey(row.FilePath, row.ContainingSymbolId, row.TargetSymbolId, row.Kind))
            .OrderBy(row => row.FilePath, StringComparer.Ordinal)
            .ThenBy(row => row.StartByte ?? long.MaxValue)
            .ThenBy(row => row.StartLine ?? int.MaxValue)
            .ThenBy(row => row.StartColumn ?? int.MaxValue)
            .ThenBy(row => row.ContainingSymbolId, StringComparer.Ordinal)
            .ThenBy(row => row.Kind)
            .ToList();

    private static List<ReferenceEvidence> FilterKind(
        List<ReferenceEvidence> rows,
        ReferenceKind? kind) =>
        kind is null ? rows : rows.Where(row => row.Kind == kind.Value).ToList();

    private static IReadOnlyList<string> ExactContainingSymbolIds(
        IReadOnlyList<ReferenceEvidence> rows,
        bool callLike) =>
        rows.Where(row =>
                row.ContainingSymbolId is not null &&
                IsCallLike(row.Kind) == callLike)
            .Select(row => row.ContainingSymbolId!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbolId => symbolId, StringComparer.Ordinal)
            .ToArray();

    private static bool IsCallLike(ReferenceKind kind) =>
        kind is ReferenceKind.Call or ReferenceKind.Instantiation;

    private static List<OutgoingReferenceEvidence> DeduplicateOutgoing(
        IEnumerable<OutgoingReferenceEvidence> rows) =>
        WithoutRedundantSpanlessRows(
            rows.GroupBy(OutgoingSiteKey)
                .Select(group => group
                    .OrderBy(row => SourcePrecedence(row.Source))
                    .ThenByDescending(row => row.Confidence)
                    .ThenBy(row => row.TargetSymbolId, StringComparer.Ordinal)
                    .ThenBy(row => row.TargetName, StringComparer.Ordinal)
                    .First()),
            row => row.IsExact,
            row => new SpanlessCoverageKey(row.FilePath, row.ContainingSymbolId, row.TargetSymbolId, row.Kind))
            .OrderBy(row => row.FilePath, StringComparer.Ordinal)
            .ThenBy(row => row.StartByte ?? long.MaxValue)
            .ThenBy(row => row.StartLine ?? int.MaxValue)
            .ThenBy(row => row.StartColumn ?? int.MaxValue)
            .ThenBy(row => row.Kind)
            .ThenBy(row => row.TargetName, StringComparer.Ordinal)
            .ThenBy(row => row.TargetSymbolId, StringComparer.Ordinal)
            .ToList();

    private static List<OutgoingReferenceEvidence> FilterOutgoingKind(
        List<OutgoingReferenceEvidence> rows,
        ReferenceKind? kind) =>
        kind is null ? rows : rows.Where(row => row.Kind == kind.Value).ToList();

    /// <summary>
    /// Drops spanless rows that only restate a binding a spanned row already covers, after site-identity
    /// deduplication has run. Available counts and the rows themselves are one logical reference per occurrence;
    /// <c>ExactObserved</c> keeps the raw row total.
    /// </summary>
    /// <remarks>
    /// julie-extract emits a schema-5 spanless pending row alongside the spanned identifier
    /// row for the same occurrence, under its own <c>reference_site_spanless-</c> identity, so site-identity
    /// deduplication cannot collapse the pair and every consumer counted one call twice. A spanless row with no
    /// spanned row at the same file, containing symbol, target, and kind is the only evidence that occurrence
    /// has, so it is kept and still reported as spanless.
    /// </remarks>
    private static IEnumerable<T> WithoutRedundantSpanlessRows<T>(
        IEnumerable<T> deduplicated,
        Func<T, bool> isSpanned,
        Func<T, SpanlessCoverageKey> coverageKey)
    {
        var rows = deduplicated as IReadOnlyList<T> ?? deduplicated.ToArray();
        var covered = rows.Where(isSpanned).Select(coverageKey).ToHashSet();
        return rows.Where(row => isSpanned(row) || !covered.Contains(coverageKey(row)));
    }

    private static ReferenceSiteKey SiteKey(ReferenceEvidence row) => new(row.ReferenceSiteId, row.TargetSymbolId, row.Kind);

    private static OutgoingReferenceSiteKey OutgoingSiteKey(OutgoingReferenceEvidence row) => new(row.ReferenceSiteId, row.TargetSymbolId, row.TargetName, row.Kind);

    private static int SourcePrecedence(ReferenceEvidenceSource source) => source switch
    {
        ReferenceEvidenceSource.IdentifierDirect => 0,
        ReferenceEvidenceSource.IdentifierResolution => 1,
        ReferenceEvidenceSource.Relationship => 2,
        ReferenceEvidenceSource.PendingResolution => 3,
        ReferenceEvidenceSource.NameFallback => 4,
        _ => int.MaxValue,
    };

    private static T ExecuteObserved<T>(
        SqliteCommand command,
        ReferenceEvidenceReadPhase? phase,
        int requestedCandidateCount,
        ReferenceEvidenceObservationOptions? observationOptions,
        Func<SqliteDataReader, (T Result, int RawRowCount)> read)
    {
        IReadOnlyList<string> queryPlan = observationOptions?.CaptureQueryPlan == true
            ? ExplainQueryPlan(command)
            : [];
        Stopwatch? stopwatch = observationOptions is null ? null : Stopwatch.StartNew();
        using SqliteDataReader reader = command.ExecuteReader();
        (T result, int rawRowCount) = read(reader);
        if (observationOptions is not null && phase is not null)
        {
            observationOptions.Observe(new ReferenceEvidenceObservation(
                phase.Value,
                requestedCandidateCount,
                rawRowCount,
                stopwatch!.Elapsed.TotalMilliseconds,
                queryPlan));
        }

        return result;
    }

    private static IReadOnlyList<string> ExplainQueryPlan(SqliteCommand command)
    {
        using SqliteCommand explain = command.Connection!.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        foreach (SqliteParameter parameter in command.Parameters)
            explain.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);

        using SqliteDataReader reader = explain.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(reader.GetString(reader.FieldCount - 1));
        return details;
    }

    private static List<ReferenceEvidence> ReadRows(
        SqliteCommand command,
        string targetSymbolId,
        ReferenceResolutionStatus resolutionStatus)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<ReferenceEvidence>();
        while (reader.Read())
        {
            string sourceKind = reader.GetString(8);
            string source = reader.GetString(10);
            rows.Add(new ReferenceEvidence(
                resolutionStatus == ReferenceResolutionStatus.Exact ? targetSymbolId : null,
                ReadString(reader, 0),
                reader.GetString(1),
                ReadInt32(reader, 2),
                ReadInt32(reader, 3),
                ReadInt32(reader, 4),
                ReadInt32(reader, 5),
                ReadInt64(reader, 6),
                ReadInt64(reader, 7),
                NormalizeKind(sourceKind),
                sourceKind,
                ParseSource(source),
                ReadInt32(reader, 11),
                reader.GetDouble(9),
                resolutionStatus,
                ReadString(reader, 12),
                reader.GetString(13),
                reader.GetInt64(14) == 1,
                reader.GetString(15)));
        }

        return rows;
    }

    private static List<OutgoingReferenceEvidence> ReadOutgoingRows(
        SqliteCommand command,
        string containingSymbolId,
        ReferenceResolutionStatus resolutionStatus)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<OutgoingReferenceEvidence>();
        while (reader.Read())
        {
            string sourceKind = reader.GetString(9);
            rows.Add(new OutgoingReferenceEvidence(
                containingSymbolId,
                ReadString(reader, 0),
                reader.GetString(1),
                reader.GetString(2),
                ReadInt32(reader, 3),
                ReadInt32(reader, 4),
                ReadInt32(reader, 5),
                ReadInt32(reader, 6),
                ReadInt64(reader, 7),
                ReadInt64(reader, 8),
                NormalizeKind(sourceKind),
                sourceKind,
                ParseSource(reader.GetString(11)),
                ReadInt32(reader, 12),
                reader.GetDouble(10),
                resolutionStatus,
                ReadString(reader, 13),
                reader.GetString(14),
                reader.GetInt64(15) == 1,
                reader.GetString(16)));
        }

        return rows;
    }

    private static Dictionary<string, List<ReferenceEvidence>> ReadRowsBySymbol(
        SqliteCommand command,
        ReferenceResolutionStatus resolutionStatus,
        ReferenceEvidenceReadPhase? phase = null,
        int requestedCandidateCount = 0,
        ReferenceEvidenceObservationOptions? observationOptions = null)
    {
        return ExecuteObserved(
            command,
            phase,
            requestedCandidateCount,
            observationOptions,
            reader =>
            {
                var rows = new Dictionary<string, List<ReferenceEvidence>>(StringComparer.Ordinal);
                int rawRowCount = 0;
                while (reader.Read())
                {
                    rawRowCount++;
                    string symbolId = reader.GetString(0);
                    string sourceKind = reader.GetString(9);
                    string source = reader.GetString(11);
                    AddRow(
                        rows,
                        symbolId,
                        new ReferenceEvidence(
                            resolutionStatus == ReferenceResolutionStatus.Exact ? symbolId : null,
                            ReadString(reader, 1),
                            reader.GetString(2),
                            ReadInt32(reader, 3),
                            ReadInt32(reader, 4),
                            ReadInt32(reader, 5),
                            ReadInt32(reader, 6),
                            ReadInt64(reader, 7),
                            ReadInt64(reader, 8),
                            NormalizeKind(sourceKind),
                            sourceKind,
                            ParseSource(source),
                            ReadInt32(reader, 12),
                            reader.GetDouble(10),
                            resolutionStatus,
                            ReadString(reader, 13),
                            reader.GetString(14),
                            reader.GetInt64(15) == 1,
                            reader.GetString(16)));
                }

                return (rows, rawRowCount);
            });
    }

    private static Dictionary<string, List<OutgoingReferenceEvidence>> ReadOutgoingRowsBySymbol(
        SqliteCommand command,
        ReferenceResolutionStatus resolutionStatus,
        ReferenceEvidenceReadPhase? phase = null,
        int requestedCandidateCount = 0,
        ReferenceEvidenceObservationOptions? observationOptions = null)
    {
        return ExecuteObserved(
            command,
            phase,
            requestedCandidateCount,
            observationOptions,
            reader =>
            {
                var rows = new Dictionary<string, List<OutgoingReferenceEvidence>>(StringComparer.Ordinal);
                int rawRowCount = 0;
                while (reader.Read())
                {
                    rawRowCount++;
                    string symbolId = reader.GetString(0);
                    string sourceKind = reader.GetString(10);
                    AddRow(
                        rows,
                        symbolId,
                        new OutgoingReferenceEvidence(
                            symbolId,
                            ReadString(reader, 1),
                            reader.GetString(2),
                            reader.GetString(3),
                            ReadInt32(reader, 4),
                            ReadInt32(reader, 5),
                            ReadInt32(reader, 6),
                            ReadInt32(reader, 7),
                            ReadInt64(reader, 8),
                            ReadInt64(reader, 9),
                            NormalizeKind(sourceKind),
                            sourceKind,
                            ParseSource(reader.GetString(12)),
                            ReadInt32(reader, 13),
                            reader.GetDouble(11),
                            resolutionStatus,
                            ReadString(reader, 14),
                            reader.GetString(15),
                            reader.GetInt64(16) == 1,
                            reader.GetString(17)));
                }

                return (rows, rawRowCount);
            });
    }

    private static void AddRow<T>(
        Dictionary<string, List<T>> rows,
        string symbolId,
        T row)
    {
        if (!rows.TryGetValue(symbolId, out List<T>? existing))
        {
            existing = [];
            rows.Add(symbolId, existing);
        }

        existing.Add(row);
    }

    private static ReferenceEvidenceSource ParseSource(string source) => source switch
    {
        "identifier_direct" => ReferenceEvidenceSource.IdentifierDirect,
        "identifier_resolution" => ReferenceEvidenceSource.IdentifierResolution,
        "relationship" => ReferenceEvidenceSource.Relationship,
        "pending_resolution" => ReferenceEvidenceSource.PendingResolution,
        "name_fallback" => ReferenceEvidenceSource.NameFallback,
        _ => throw new InvalidOperationException($"Unknown reference evidence source '{source}'."),
    };

    public static ReferenceKind NormalizeKind(string kind) => kind switch
    {
        "call" or "calls" => ReferenceKind.Call,
        "type_usage" => ReferenceKind.TypeUsage,
        "member_access" => ReferenceKind.MemberAccess,
        "variable_ref" => ReferenceKind.VariableReference,
        "instantiates" => ReferenceKind.Instantiation,
        "extends" => ReferenceKind.Inheritance,
        "implements" => ReferenceKind.Implementation,
        "import" or "imports" => ReferenceKind.Import,
        "references" => ReferenceKind.Reference,
        "uses" => ReferenceKind.Usage,
        _ => ReferenceKind.Unknown,
    };

    private static string? ReadString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? ReadInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long? ReadInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private readonly record struct SpanlessCoverageKey(
        string FilePath,
        string? ContainingSymbolId,
        string? TargetSymbolId,
        ReferenceKind Kind);

    private readonly record struct ReferenceSiteKey(
        string ReferenceSiteId,
        string? TargetSymbolId,
        ReferenceKind Kind);

    private readonly record struct OutgoingReferenceSiteKey(
        string ReferenceSiteId,
        string? TargetSymbolId,
        string TargetName,
        ReferenceKind Kind);

    private readonly record struct ReferenceEvidenceTargetInfo(
        string Name,
        int SameNameDefinitionCount);
}
