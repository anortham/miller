using Miller.Core.Contracts;
using Miller.Core.Graph;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Miller.Indexing;

/// <summary>
/// On-demand symbol dependency reachability over a julie extract DB. This mirrors <see cref="SymbolGraphReader"/>
/// edge semantics without materializing the whole graph: precise <c>relationships</c> edges are read by id, and
/// fallback <c>identifiers</c> edges are resolved by name through the DB's indexed <c>symbols</c> table.
/// </summary>
public sealed class SqliteSymbolGraphIndex : ISymbolGraphReachability, IDisposable
{
    private const int MaximumBatchIds = 500;
    private const int FrontierProofBatchIds = 100;
    private const int MaximumEvidenceCacheEntries = 4000;
    private static readonly IReadOnlyList<string> Empty = Array.Empty<string>();

    private readonly string? _dbPath;
    private readonly IWorkspaceReadSession? _readSession;
    private readonly IFamilyGraphResolutionReader? _familyGraphResolutionReader;
    private readonly IFamilyGraphUnresolvedNameReader? _familyGraphUnresolvedNameReader;
    private readonly IFamilyGraphRelationshipReader? _familyGraphRelationshipReader;
    private QueryTimeResolutionReader? _queryTimeResolutionReader;
    private readonly Dictionary<(string Id, Direction Direction), IReadOnlyList<string>> _neighbourCache = new();
    private readonly Dictionary<(string Id, Direction Direction), IReadOnlyList<GraphNeighbour>>
        _evidenceCache = new();
    private readonly Dictionary<string, bool> _symbolExistsCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _symbolNameCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _nameResolutionCache = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlyList<GraphEdge>>? _loadSupplementalEdges;
    private IReadOnlyList<GraphEdge>? _supplementalEdges;
    private HashSet<string>? _supplementalEndpoints;
    private bool _supplementalEndpointsPrimed;
    private Microsoft.Data.Sqlite.SqliteConnection? _connection;
    private Microsoft.Data.Sqlite.SqliteConnection? _activeSessionConnection;
    private readonly GraphQueryTelemetry _queryTelemetry = new();

    internal GraphQueryTelemetrySnapshot QueryTelemetry => _queryTelemetry.Snapshot();
    internal bool CaptureFrontierQueryPlan { get; set; }
    internal FrontierQueryPlan LastFrontierQueryPlan { get; private set; } = FrontierQueryPlan.Empty;
    internal Action<GraphStatementObservation>? StatementObserver { get; set; }

    public SqliteSymbolGraphIndex(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        _dbPath = dbPath;
    }

    public SqliteSymbolGraphIndex(
        IWorkspaceReadSession readSession,
        Func<IReadOnlyList<GraphEdge>>? loadSupplementalEdges = null)
    {
        ArgumentNullException.ThrowIfNull(readSession);

        _readSession = readSession;
        _loadSupplementalEdges = loadSupplementalEdges;
        _familyGraphResolutionReader = readSession is WorkspaceReadHandle handle
            ? handle.FamilyGraphResolutionReader
            : readSession as IFamilyGraphResolutionReader;
        _familyGraphUnresolvedNameReader = readSession is WorkspaceReadHandle unresolvedNameHandle
            ? unresolvedNameHandle.FamilyGraphUnresolvedNameReader
            : readSession as IFamilyGraphUnresolvedNameReader;
        _familyGraphRelationshipReader = readSession is WorkspaceReadHandle relationshipHandle
            ? relationshipHandle.FamilyGraphRelationshipReader
            : readSession as IFamilyGraphRelationshipReader;
    }

    public IReadOnlyList<ReachedNode> Reach(IEnumerable<string> starts, int maxDepth, int limit, Direction dir) =>
        Read(() => ReachBatched(starts, maxDepth, limit, dir));

    private IReadOnlyList<ReachedNode> ReachBatched(
        IEnumerable<string> starts,
        int maxDepth,
        int limit,
        Direction direction)
    {
        ArgumentNullException.ThrowIfNull(starts);
        if (maxDepth <= 0 || limit <= 0)
            return [];

        var hops = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string start in starts)
        {
            if (Contains(start))
                hops.TryAdd(start, 0);
        }

        string[] frontier = hops.Keys.ToArray();
        for (int hop = 1; hop <= maxDepth && frontier.Length > 0; hop++)
        {
            IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbour>> neighbours =
                BatchNeighbourEvidence(frontier, direction);
            var next = new List<string>();
            foreach (string current in frontier)
            {
                foreach (GraphNeighbour neighbour in neighbours[current])
                {
                    if (hops.TryAdd(neighbour.Id, hop))
                        next.Add(neighbour.Id);
                }
            }
            frontier = next.ToArray();
        }

        return hops
            .Where(static pair => pair.Value > 0)
            .OrderBy(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(static pair => new ReachedNode(pair.Key, pair.Value))
            .ToArray();
    }

    public GraphReachResult ReachWithEvidence(
        IEnumerable<string> starts,
        int maxDepth,
        int limit,
        Direction dir)
        => Read(() =>
        {
            _evidenceCache.Clear();
            try
            {
                GraphReachResult result =
                    GraphTraversal.ReachWithEvidence(
                        starts,
                        maxDepth,
                        limit,
                        dir,
                        Contains,
                        null,
                        (ids, direction) => BatchNeighbourEvidence(ids, direction),
                        HasUnseenNeighbours);
                return result with { Nodes = EnrichImpactEvidence(result.Nodes) };
            }
            finally
            {
                _evidenceCache.Clear();
            }
        });

    public IReadOnlyList<string>? ShortestPath(string from, string to, int maxDepth) =>
        Read(() => GraphTraversal.ShortestPath(from, to, maxDepth, Contains, Dependencies));

    public GraphPath? ShortestPathWithEvidence(
        string from,
        string to,
        int maxDepth,
        Func<GraphNeighbour, bool> edgeFilter)
        => Read(() =>
        {
            _evidenceCache.Clear();
            try
            {
                return GraphTraversal.ShortestPathWithEvidence(
                    from,
                    to,
                    maxDepth,
                    Contains,
                    id => BatchNeighbourEvidence([id], Direction.Forward)
                        .TryGetValue(id, out IReadOnlyList<GraphNeighbour>? neighbours)
                            ? neighbours
                            : Array.Empty<GraphNeighbour>(),
                    edgeFilter);
            }
            finally
            {
                _evidenceCache.Clear();
            }
        });

    private IReadOnlyList<ReachedNode> EnrichImpactEvidence(IReadOnlyList<ReachedNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var neighboursById = nodes.ToDictionary(
            static node => node.Id,
            static _ => new HashSet<(string Neighbour, Direction Direction)>(),
            StringComparer.Ordinal);
        var visibilityById = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (ReachedNode[] batch in nodes.Chunk(MaximumBatchIds))
            ReadImpactEvidenceBatch(batch, neighboursById, visibilityById);

        return nodes
            .Select(node => node with
            {
                Centrality = neighboursById[node.Id].Count,
                Visibility = visibilityById.GetValueOrDefault(node.Id),
            })
            .ToArray();
    }

    private bool HasUnseenNeighbours(
        IReadOnlyList<string> frontier,
        IReadOnlySet<string> reached,
        Direction direction)
    {
        if (frontier.Count == 0)
            return false;

        foreach (string[] batch in frontier.Chunk(FrontierProofBatchIds))
        {
            IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbour>> adjacent =
                BatchNeighbourEvidence(batch, direction, cacheResults: false);
            if (adjacent.Values.SelectMany(static neighbours => neighbours).Any(neighbour =>
                    !reached.Contains(neighbour.Id)))
            {
                return true;
            }
        }
        return false;
    }

    private void ReadImpactEvidenceBatch(
        IReadOnlyList<ReachedNode> nodes,
        IReadOnlyDictionary<string, HashSet<(string Neighbour, Direction Direction)>> neighboursById,
        IDictionary<string, string?> visibilityById)
    {
        if (nodes.Count == 0)
            return;

        string[] ids = nodes.Select(static node => node.Id).ToArray();
        foreach (Direction direction in new[] { Direction.Forward, Direction.Reverse })
        {
            IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbour>> adjacent =
                BatchNeighbourEvidence(ids, direction);
            foreach ((string id, IReadOnlyList<GraphNeighbour> neighbours) in adjacent)
            {
                foreach (GraphNeighbour neighbour in neighbours)
                    neighboursById[id].Add((neighbour.Id, direction));
            }
        }

        string values = string.Join(", ", Enumerable.Range(0, ids.Length).Select(index => $"($id{index})"));
        using var command = Connection.CreateCommand();
        command.CommandText = $"""
            WITH candidates(id) AS (VALUES {values})
            SELECT candidates.id, symbols.visibility
            FROM candidates
            JOIN symbols ON symbols.symbol_id = candidates.id
            ORDER BY candidates.id;
            """;
        for (int index = 0; index < ids.Length; index++)
            command.Parameters.AddWithValue($"$id{index}", ids[index]);

        using var reader = command.ExecuteReader();
        int idOrdinal = reader.GetOrdinal("id");
        int visibilityOrdinal = reader.GetOrdinal("visibility");
        while (reader.Read())
        {
            string id = reader.GetString(idOrdinal);
            visibilityById[id] =
                reader.IsDBNull(visibilityOrdinal) ? null : reader.GetString(visibilityOrdinal);
        }
    }

    private bool Contains(string id) => SymbolExists(id);

    private IReadOnlyList<string> Dependencies(string id) => Neighbours(id, Direction.Forward);

    private IReadOnlyList<string> Dependents(string id) => Neighbours(id, Direction.Reverse);

    private IReadOnlyList<string> Neighbours(string id, Direction direction)
    {
        if (direction == Direction.Both)
            return Dependencies(id).Concat(Dependents(id)).Distinct(StringComparer.Ordinal).ToArray();

        var key = (id, direction);
        if (_neighbourCache.TryGetValue(key, out IReadOnlyList<string>? cached))
            return cached;

        IReadOnlyList<string> loaded = direction switch
        {
            Direction.Forward => LoadDependencies(id),
            Direction.Reverse => LoadDependents(id),
            _ => Empty,
        };
        _neighbourCache[key] = loaded;
        return loaded;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbour>> BatchNeighbourEvidence(
        IReadOnlyList<string> ids,
        Direction direction,
        bool cacheResults = true)
    {
        if (ids.Count > MaximumBatchIds)
        {
            var batched = new Dictionary<string, IReadOnlyList<GraphNeighbour>>(StringComparer.Ordinal);
            foreach (string[] batch in ids.Chunk(MaximumBatchIds))
            {
                foreach ((string id, IReadOnlyList<GraphNeighbour> neighbours) in
                         BatchNeighbourEvidence(batch, direction, cacheResults))
                {
                    batched[id] = neighbours;
                }
            }
            return batched;
        }

        string[] missingIds = direction == Direction.Both || !cacheResults
            ? ids.ToArray()
            : ids.Where(id => !_evidenceCache.ContainsKey((id, direction))).ToArray();
        if (missingIds.Length == 0)
        {
            return ids.ToDictionary(
                static id => id,
                id => _evidenceCache[(id, direction)],
                StringComparer.Ordinal);
        }

        var edgesById = missingIds.ToDictionary(
            static id => id,
            static _ => new Dictionary<string, GraphEdge>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        long frontierStarted = Stopwatch.GetTimestamp();
        int frontierRows = 0;
        var relationshipPlans = new List<IReadOnlyList<string>>();
        var unresolvedNamePlans = new List<IReadOnlyList<string>>();
        if (_familyGraphRelationshipReader is { } relationshipReader)
        {
            IReadOnlyList<FamilyGraphRelationshipEdge> relationshipEdges =
                relationshipReader.ReadRelationshipEdges(missingIds, direction, StatementObserver);
            frontierRows += relationshipEdges.Count;
            foreach (FamilyGraphRelationshipEdge edge in relationshipEdges)
            {
                if (!edgesById.TryGetValue(edge.CurrentId, out Dictionary<string, GraphEdge>? currentEdges))
                    continue;
                string neighbour = string.Equals(edge.FromId, edge.CurrentId, StringComparison.Ordinal)
                    ? edge.ToId
                    : edge.FromId;
                AddEdge(
                    currentEdges,
                    edge.CurrentId,
                    neighbour,
                    new GraphEdge(edge.FromId, edge.ToId, edge.Kind, edge.Confidence, edge.Source));
            }
        }
        else
        {
            if (direction is Direction.Forward or Direction.Both)
            {
                frontierRows += ExecuteObservedFrontierStatement(
                    missingIds,
                    RelationshipForwardSql,
                    edgesById,
                    _queryTelemetry.FrontierRelationships,
                    relationshipPlans,
                    GraphStatementPhase.RelationshipForward);
            }
            if (direction is Direction.Reverse or Direction.Both)
            {
                frontierRows += ExecuteObservedFrontierStatement(
                    missingIds,
                    RelationshipReverseSql,
                    edgesById,
                    _queryTelemetry.FrontierRelationships,
                    relationshipPlans,
                    GraphStatementPhase.RelationshipReverse);
            }
        }
        if (_familyGraphUnresolvedNameReader is { } unresolvedNameReader)
        {
            IReadOnlyList<FamilyGraphUnresolvedNameEdge> unresolvedNameEdges =
                unresolvedNameReader.ReadUnresolvedNameEdges(missingIds, direction, StatementObserver);
            frontierRows += unresolvedNameEdges.Count;
            foreach (FamilyGraphUnresolvedNameEdge edge in unresolvedNameEdges)
            {
                if (!edgesById.TryGetValue(edge.CurrentId, out Dictionary<string, GraphEdge>? currentEdges))
                    continue;
                string neighbour = string.Equals(edge.FromId, edge.CurrentId, StringComparison.Ordinal)
                    ? edge.ToId
                    : edge.FromId;
                AddEdge(
                    currentEdges,
                    edge.CurrentId,
                    neighbour,
                    new GraphEdge(edge.FromId, edge.ToId, edge.Kind, edge.Confidence, edge.Source));
            }
        }
        else
        {
            // Query-time unresolved names are emitted with resolution edges below.
        }
        _queryTelemetry.FrontierBatch.Add(frontierRows, Stopwatch.GetElapsedTime(frontierStarted));
        if (CaptureFrontierQueryPlan)
            LastFrontierQueryPlan = new FrontierQueryPlan(relationshipPlans, unresolvedNamePlans);

        long resolutionStarted = Stopwatch.GetTimestamp();
        int resolutionRows = 0;
        if (_familyGraphResolutionReader is { } resolutionReader)
        {
            IReadOnlyList<FamilyGraphResolutionEdge> resolutionEdges =
                resolutionReader.ReadResolutionEdges(missingIds, direction, StatementObserver);
            resolutionRows = resolutionEdges.Count;
            foreach (FamilyGraphResolutionEdge edge in resolutionEdges)
            {
                if (!edgesById.TryGetValue(edge.CurrentId, out Dictionary<string, GraphEdge>? currentEdges))
                    continue;
                string neighbour = string.Equals(edge.FromId, edge.CurrentId, StringComparison.Ordinal)
                    ? edge.ToId
                    : edge.FromId;
                AddEdge(
                    currentEdges,
                    edge.CurrentId,
                    neighbour,
                    new GraphEdge(edge.FromId, edge.ToId, edge.Kind, edge.Confidence, edge.Source));
            }
        }
        else
        {
            QueryTimeResolutionReader resolution = ResolutionReader(Connection);
            IReadOnlyList<FamilyGraphResolutionEdge> resolutionEdges =
                resolution.ReadResolutionEdges(Connection, missingIds, direction, StatementObserver);
            resolutionRows = resolutionEdges.Count;
            foreach (FamilyGraphResolutionEdge edge in resolutionEdges)
            {
                if (!edgesById.TryGetValue(edge.CurrentId, out Dictionary<string, GraphEdge>? currentEdges))
                    continue;
                string neighbour = string.Equals(edge.FromId, edge.CurrentId, StringComparison.Ordinal)
                    ? edge.ToId
                    : edge.FromId;
                AddEdge(
                    currentEdges,
                    edge.CurrentId,
                    neighbour,
                    new GraphEdge(edge.FromId, edge.ToId, edge.Kind, edge.Confidence, edge.Source));
            }

            foreach (FamilyGraphUnresolvedNameEdge edge in resolution.ReadUnresolvedNameEdges(
                         Connection, missingIds, direction, StatementObserver))
            {
                if (!edgesById.TryGetValue(edge.CurrentId, out Dictionary<string, GraphEdge>? currentEdges))
                    continue;
                string neighbour = string.Equals(edge.FromId, edge.CurrentId, StringComparison.Ordinal)
                    ? edge.ToId
                    : edge.FromId;
                AddEdge(
                    currentEdges,
                    edge.CurrentId,
                    neighbour,
                    new GraphEdge(edge.FromId, edge.ToId, edge.Kind, edge.Confidence, edge.Source));
            }
        }
        ObserveStatement(GraphStatementPhase.FamilyResolution, resolutionRows, resolutionStarted, missingIds);

        long supplementalStarted = Stopwatch.GetTimestamp();
        IReadOnlyList<GraphEdge> supplementalEdges = SupplementalEdges();
        foreach (GraphEdge edge in supplementalEdges)
        {
            if (direction is Direction.Forward or Direction.Both &&
                edgesById.TryGetValue(edge.From, out Dictionary<string, GraphEdge>? forward) &&
                Contains(edge.To))
            {
                AddEdge(forward, edge.From, edge.To, edge);
            }
            if (direction is Direction.Reverse or Direction.Both &&
                edgesById.TryGetValue(edge.To, out Dictionary<string, GraphEdge>? reverse) &&
                Contains(edge.From))
            {
                AddEdge(reverse, edge.To, edge.From, edge);
            }
        }
        ObserveStatement(
            GraphStatementPhase.Supplemental,
            supplementalEdges.Count,
            supplementalStarted,
            missingIds);

        IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbour>> loaded = edgesById.ToDictionary(
            static pair => pair.Key,
            pair => (IReadOnlyList<GraphNeighbour>)OrderedEdges(pair.Value)
                .Select(edge =>
                {
                    string neighbour = NeighbourId(edge, pair.Key);
                    return new GraphNeighbour(
                        neighbour,
                        edge.Kind,
                        edge.Confidence,
                        edge.Source,
                        0,
                        null);
                })
                .ToArray(),
            StringComparer.Ordinal);
        IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbour>> result = loaded;
        if (direction != Direction.Both && cacheResults)
        {
            foreach ((string id, IReadOnlyList<GraphNeighbour> neighbours) in loaded)
            {
                if (_evidenceCache.Count < MaximumEvidenceCacheEntries)
                    _evidenceCache[(id, direction)] = neighbours;
            }
            result = ids.ToDictionary(
                static id => id,
                id => _evidenceCache.TryGetValue((id, direction), out var cached)
                    ? cached
                    : loaded[id],
                StringComparer.Ordinal);
        }
        ObserveStatement(
            GraphStatementPhase.Completion,
            result.Sum(static pair => pair.Value.Count),
            frontierStarted,
            missingIds);
        return result;
    }

    private int ExecuteObservedFrontierStatement(
        IReadOnlyList<string> ids,
        string sql,
        Dictionary<string, Dictionary<string, GraphEdge>> edgesById,
        GraphQueryFamilyAccumulator telemetry,
        List<IReadOnlyList<string>> plans,
        GraphStatementPhase phase)
    {
        long started = Stopwatch.GetTimestamp();
        int rows = ExecuteFrontierStatement(ids, sql, edgesById, telemetry, plans);
        ObserveStatement(phase, rows, started, ids);
        return rows;
    }

    private void ObserveStatement(
        GraphStatementPhase phase,
        int rows,
        long started,
        IReadOnlyList<string> candidateIds) =>
        StatementObserver?.Invoke(GraphStatementObservation.Completed(
            phase,
            rows,
            Stopwatch.GetElapsedTime(started),
            candidateIds));

    private int ExecuteFrontierStatement(
        IReadOnlyList<string> ids,
        string sql,
        Dictionary<string, Dictionary<string, GraphEdge>> edgesById,
        GraphQueryFamilyAccumulator? telemetry,
        List<IReadOnlyList<string>>? plans)
    {
        string values = string.Join(", ", Enumerable.Range(0, ids.Count).Select(index => $"($id{index})"));
        using Microsoft.Data.Sqlite.SqliteCommand command = Connection.CreateCommand();
        command.CommandText = $"WITH candidates(id) AS (VALUES {values})\n" + sql;
        for (int index = 0; index < ids.Count; index++)
            command.Parameters.AddWithValue($"$id{index}", ids[index]);
        if (CaptureFrontierQueryPlan && plans is not null)
            plans.Add(ReadQueryPlan(command));

        long started = Stopwatch.GetTimestamp();
        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
        int currentOrdinal = reader.GetOrdinal("current_id");
        int fromOrdinal = reader.GetOrdinal("from_id");
        int toOrdinal = reader.GetOrdinal("to_id");
        int kindOrdinal = reader.GetOrdinal("kind");
        int confidenceOrdinal = reader.GetOrdinal("confidence");
        int sourceOrdinal = reader.GetOrdinal("source");
        int rows = 0;
        while (reader.Read())
        {
            rows++;
            string current = reader.GetString(currentOrdinal);
            string from = reader.GetString(fromOrdinal);
            string to = reader.GetString(toOrdinal);
            string neighbour = string.Equals(from, current, StringComparison.Ordinal) ? to : from;
            AddEdge(
                edgesById[current],
                current,
                neighbour,
                new GraphEdge(
                    from,
                    to,
                    reader.GetString(kindOrdinal),
                    reader.GetDouble(confidenceOrdinal),
                    reader.GetString(sourceOrdinal)));
        }
        telemetry?.Add(rows, Stopwatch.GetElapsedTime(started));
        return rows;
    }

    private static IReadOnlyList<string> ReadQueryPlan(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        using Microsoft.Data.Sqlite.SqliteCommand explain = command.Connection!.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        foreach (Microsoft.Data.Sqlite.SqliteParameter parameter in command.Parameters)
            explain.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);
        using Microsoft.Data.Sqlite.SqliteDataReader reader = explain.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read())
            plan.Add(reader.GetString(3));
        return plan;
    }

    private const string RelationshipForwardSql = """
        SELECT candidates.id AS current_id,r.from_symbol_id AS from_id,r.to_symbol_id AS to_id,
               r.kind,r.confidence,'relationship' AS source
        FROM candidates
        JOIN relationships r ON r.from_symbol_id=candidates.id
        JOIN symbols target_symbol ON target_symbol.symbol_id=r.to_symbol_id
        WHERE r.from_symbol_id<>r.to_symbol_id;
        """;

    private const string RelationshipReverseSql = """
        SELECT candidates.id AS current_id,r.from_symbol_id AS from_id,r.to_symbol_id AS to_id,
               r.kind,r.confidence,'relationship' AS source
        FROM candidates
        JOIN relationships r ON r.to_symbol_id=candidates.id
        JOIN symbols source_symbol ON source_symbol.symbol_id=r.from_symbol_id
        WHERE r.from_symbol_id<>r.to_symbol_id;
        """;


    private static void AddEdge(
        Dictionary<string, GraphEdge> edges,
        string sourceId,
        string neighbourId,
        GraphEdge candidate)
    {
        if (string.Equals(sourceId, neighbourId, StringComparison.Ordinal))
            return;
        if (!edges.TryGetValue(neighbourId, out GraphEdge? current) ||
            CompareEdges(candidate, current) < 0)
            edges[neighbourId] = candidate;
    }

    private static IReadOnlyList<GraphEdge> OrderedEdges(Dictionary<string, GraphEdge> edges) =>
        edges.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Value)
            .ToArray();

    private static int CompareEdges(GraphEdge left, GraphEdge right)
    {
        int comparison = ImpactRanker.RelationshipPriority(left.Kind)
            .CompareTo(ImpactRanker.RelationshipPriority(right.Kind));
        if (comparison != 0)
            return comparison;

        comparison = ImpactRanker.SourcePriority(left.Source)
            .CompareTo(ImpactRanker.SourcePriority(right.Source));
        if (comparison != 0)
            return comparison;

        comparison = right.Confidence.CompareTo(left.Confidence);
        if (comparison != 0)
            return comparison;

        comparison = StringComparer.Ordinal.Compare(left.Source, right.Source);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Kind, right.Kind);
    }

    private static string NeighbourId(GraphEdge edge, string currentId) =>
        string.Equals(edge.From, currentId, StringComparison.Ordinal) ? edge.To : edge.From;

    private IReadOnlyList<GraphEdge> SupplementalEdges()
    {
        if (_supplementalEdges is not null)
            return _supplementalEdges;

        long started = Stopwatch.GetTimestamp();
        _supplementalEdges = _loadSupplementalEdges is not null
            ? _loadSupplementalEdges()
            : LoadSupplementalEdges();
        _queryTelemetry.SupplementalEdges.Add(_supplementalEdges.Count, Stopwatch.GetElapsedTime(started));
        return _supplementalEdges;
    }

    /// <summary>
    /// Resolves every supplemental edge endpoint in ONE statement per batch instead of one
    /// <c>SELECT 1 FROM symbols</c> per endpoint per frontier, but only once the query actually asks about an
    /// endpoint.
    /// </summary>
    /// <remarks>
    /// <para>The edge set is fixed for the life of this instance and <see cref="_symbolExistsCache"/> starts
    /// empty on every instance (the provider builds a new graph per call), so the per-endpoint probes were paid
    /// in full on every single call — 48 round trips for this repo's 24 edges. The cached answers are identical
    /// to what <see cref="SymbolExists"/> would return: an id the batch does not report is recorded as absent.
    /// </para>
    /// <para>The prime is LAZY because the calls it replaces were all short-circuited: the frontier reader asks
    /// only about edges whose other endpoint it already holds, <c>LoadDependencies</c> only about edges leaving
    /// the queried id, and <c>LoadDependents</c> never asks at all. Priming at load time would put work
    /// proportional to the WHOLE edge set on queries that touch none of it — the exact shape this lane removed
    /// from the linkage scan (2026-08-21 review, finding 3).</para>
    /// </remarks>
    private bool TryPrimeSupplementalEndpointExistence(string id)
    {
        if (_supplementalEndpointsPrimed || _supplementalEdges is null || _supplementalEdges.Count == 0)
            return false;

        // In-memory only: no statement runs unless the asked-for id is one of the endpoints.
        if (_supplementalEndpoints is null)
        {
            _supplementalEndpoints = new HashSet<string>(StringComparer.Ordinal);
            foreach (GraphEdge edge in _supplementalEdges)
            {
                _supplementalEndpoints.Add(edge.From);
                _supplementalEndpoints.Add(edge.To);
            }
        }

        if (!_supplementalEndpoints.Contains(id))
            return false;

        _supplementalEndpointsPrimed = true;
        PrimeSupplementalEndpointExistence(_supplementalEdges);
        return true;
    }

    private void PrimeSupplementalEndpointExistence(IReadOnlyList<GraphEdge> edges)
    {
        var pending = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphEdge edge in edges)
        {
            AddPendingEndpoint(edge.From, pending, seen);
            AddPendingEndpoint(edge.To, pending, seen);
        }

        if (pending.Count == 0)
            return;

        foreach (string[] batch in pending.Chunk(MaximumBatchIds))
        {
            using Microsoft.Data.Sqlite.SqliteCommand command = Connection.CreateCommand();
            string placeholders = string.Join(
                ", ",
                Enumerable.Range(0, batch.Length).Select(static index => $"$id{index}"));
            command.CommandText =
                $"SELECT symbol_id FROM symbols WHERE symbol_id IN ({placeholders});";
            for (int index = 0; index < batch.Length; index++)
                command.Parameters.AddWithValue($"$id{index}", batch[index]);

            long started = Stopwatch.GetTimestamp();
            using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
            int rows = 0;
            while (reader.Read())
            {
                rows++;
                _symbolExistsCache[reader.GetString(0)] = true;
            }
            _queryTelemetry.SymbolExists.Add(rows, Stopwatch.GetElapsedTime(started));

            foreach (string id in batch)
            {
                if (!_symbolExistsCache.ContainsKey(id))
                    _symbolExistsCache[id] = false;
            }
        }
    }

    private void AddPendingEndpoint(string id, List<string> pending, HashSet<string> seen)
    {
        if (!_symbolExistsCache.ContainsKey(id) && seen.Add(id))
            pending.Add(id);
    }

    internal IReadOnlyList<GraphEdge> ReadCurrentSupplementalEdges() => LoadSupplementalEdges();

    internal static IReadOnlyList<GraphEdge> ReadSupplementalEdges(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        IWorkspaceReadSession? session,
        string? dbPath)
    {
        ArgumentNullException.ThrowIfNull(connection);

        IReadOnlyList<StructuralFactRecord> facts = SqliteBridgeReader.ReadStructuralFacts(
            connection,
            [BridgeStructuralPatterns.BlazorComponentReference]);
        IReadOnlyList<GraphEdge> componentEdges = session is null
            ? BlazorComponentGraphReader.Read(dbPath!, facts)
            : BlazorComponentGraphReader.ReadSession(
                new BorrowedReadSession(session.Snapshot, connection),
                facts);
        return [.. TestLinkageReader.Read(connection), .. componentEdges];
    }

    private IReadOnlyList<GraphEdge> LoadSupplementalEdges() =>
        ReadSupplementalEdges(Connection, _readSession, _dbPath);

    private IReadOnlyList<string> LoadDependencies(string id)
    {
        if (!Contains(id))
            return Empty;

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        using (var command = Connection.CreateCommand())
        {
            command.CommandText = """
                SELECT r.to_symbol_id
                FROM relationships r
                JOIN symbols s ON s.symbol_id = r.to_symbol_id
                WHERE r.from_symbol_id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            long started = Stopwatch.GetTimestamp();
            using var reader = command.ExecuteReader();
            int rows = 0;
            while (reader.Read())
            {
                rows++;
                AddCandidate(ids, id, reader.GetString(0));
            }
            _queryTelemetry.RelationshipsForward.Add(rows, Stopwatch.GetElapsedTime(started));
        }

        long resolutionStarted = Stopwatch.GetTimestamp();
        QueryTimeResolutionReader resolution = ResolutionReader(Connection);
        int resolutionRows = 0;
        foreach (FamilyGraphResolutionEdge edge in resolution.ReadResolutionEdges(
                     Connection, [id], Direction.Forward, StatementObserver))
        {
            resolutionRows++;
            AddCandidate(ids, id, edge.ToId);
        }

        foreach (FamilyGraphUnresolvedNameEdge edge in resolution.ReadUnresolvedNameEdges(
                     Connection, [id], Direction.Forward, StatementObserver))
        {
            resolutionRows++;
            AddCandidate(ids, id, edge.ToId);
        }

        _queryTelemetry.IdentifiersForward.Add(resolutionRows, Stopwatch.GetElapsedTime(resolutionStarted));

        foreach (GraphEdge edge in SupplementalEdges())
        {
            if (string.Equals(edge.From, id, StringComparison.Ordinal) && Contains(edge.To))
                AddCandidate(ids, id, edge.To);
        }

        return ids.Count == 0 ? Empty : ids.ToArray();
    }

    private IReadOnlyList<string> LoadDependents(string id)
    {
        string? targetName = SymbolName(id);
        if (targetName is null)
            return Empty;

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        using (var command = Connection.CreateCommand())
        {
            command.CommandText = """
                SELECT r.from_symbol_id
                FROM relationships r
                JOIN symbols s ON s.symbol_id = r.from_symbol_id
                WHERE r.to_symbol_id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            long started = Stopwatch.GetTimestamp();
            using var reader = command.ExecuteReader();
            int rows = 0;
            while (reader.Read())
            {
                rows++;
                AddCandidate(ids, id, reader.GetString(0));
            }
            _queryTelemetry.RelationshipsReverse.Add(rows, Stopwatch.GetElapsedTime(started));
        }

        long resolutionStarted = Stopwatch.GetTimestamp();
        QueryTimeResolutionReader resolution = ResolutionReader(Connection);
        int resolutionRows = 0;
        foreach (FamilyGraphResolutionEdge edge in resolution.ReadResolutionEdges(
                     Connection, [id], Direction.Reverse, StatementObserver))
        {
            resolutionRows++;
            AddCandidate(ids, id, edge.FromId);
        }

        foreach (FamilyGraphUnresolvedNameEdge edge in resolution.ReadUnresolvedNameEdges(
                     Connection, [id], Direction.Reverse, StatementObserver))
        {
            resolutionRows++;
            AddCandidate(ids, id, edge.FromId);
        }

        _queryTelemetry.IdentifiersReverse.Add(resolutionRows, Stopwatch.GetElapsedTime(resolutionStarted));

        foreach (GraphEdge edge in SupplementalEdges())
        {
            if (string.Equals(edge.To, id, StringComparison.Ordinal))
                AddCandidate(ids, id, edge.From);
        }

        return ids.Count == 0 ? Empty : ids.ToArray();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    private Microsoft.Data.Sqlite.SqliteConnection Connection =>
        _activeSessionConnection ?? (_connection ??= SqliteReadOnlyAccess.Open(_dbPath!));

    private QueryTimeResolutionReader ResolutionReader(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        if (_readSession is WorkspaceReadHandle handle && handle.ResolutionReader is { } hosted)
            return hosted;
        if (_readSession is IQueryTimeResolutionHost host)
            return host.Resolution;
        return _queryTimeResolutionReader ??= new QueryTimeResolutionReader(
            RevisionFactCache.LoadFromArtifact(connection),
            visibility: null);
    }

    private TResult Read<TResult>(Func<TResult> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_readSession is null)
            return query();

        return _readSession.Read(connection =>
        {
            _activeSessionConnection = connection;
            try
            {
                return query();
            }
            finally
            {
                _activeSessionConnection = null;
            }
        });
    }

    private static void AddCandidate(SortedSet<string> ids, string sourceId, string candidateId)
    {
        if (!string.Equals(sourceId, candidateId, StringComparison.Ordinal))
            ids.Add(candidateId);
    }

    private bool SymbolExists(string id)
    {
        if (_symbolExistsCache.TryGetValue(id, out bool exists))
            return exists;

        if (TryPrimeSupplementalEndpointExistence(id) && _symbolExistsCache.TryGetValue(id, out exists))
            return exists;

        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM symbols WHERE symbol_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        long started = Stopwatch.GetTimestamp();
        exists = command.ExecuteScalar() is not null;
        _queryTelemetry.SymbolExists.Add(exists ? 1 : 0, Stopwatch.GetElapsedTime(started));
        _symbolExistsCache[id] = exists;
        return exists;
    }

    private string? SymbolName(string id)
    {
        if (_symbolNameCache.TryGetValue(id, out string? cached))
            return cached;

        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT name FROM symbols WHERE symbol_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        long started = Stopwatch.GetTimestamp();
        string? name = command.ExecuteScalar() as string;
        _queryTelemetry.SymbolName.Add(name is null ? 0 : 1, Stopwatch.GetElapsedTime(started));
        _symbolNameCache[id] = name;
        return name;
    }

    private IReadOnlyList<string> ResolveNameIds(string name)
    {
        if (_nameResolutionCache.TryGetValue(name, out IReadOnlyList<string>? cached))
            return cached;

        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT symbol_id FROM symbols WHERE name = $name ORDER BY symbol_id;";
        command.Parameters.AddWithValue("$name", name);
        long started = Stopwatch.GetTimestamp();
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        _queryTelemetry.ResolveName.Add(ids.Count, Stopwatch.GetElapsedTime(started));

        IReadOnlyList<string> result = ids.Count == 0 ? Empty : ids.ToArray();
        _nameResolutionCache[name] = result;
        return result;
    }

    private sealed class BorrowedReadSession(
        WorkspaceReadSnapshot snapshot,
        Microsoft.Data.Sqlite.SqliteConnection connection) : IWorkspaceReadSession
    {
        public WorkspaceReadSnapshot Snapshot { get; } = snapshot;

        public TResult Read<TResult>(Func<Microsoft.Data.Sqlite.SqliteConnection, TResult> query) =>
            query(connection);

        public void Dispose()
        {
        }
    }
}

internal sealed record GraphQueryFamilyTelemetry(int Executions, long Rows, TimeSpan Elapsed);

internal enum GraphStatementPhase
{
    RelationshipForward,
    RelationshipReverse,
    UnresolvedNameForward,
    UnresolvedNameReverse,
    IdentifierBaseForward,
    IdentifierDeltaForward,
    PendingBaseForward,
    PendingDeltaForward,
    IdentifierBaseReverse,
    IdentifierDeltaReverse,
    PendingBaseReverse,
    PendingDeltaReverse,
    FamilyResolution,
    Supplemental,
    Completion,
}

internal sealed record GraphStatementObservation(
    GraphStatementPhase Phase,
    int Rows,
    TimeSpan Elapsed,
    int CandidateCount,
    ImmutableArray<string> CandidateSample)
{
    private const int CandidateSampleLimit = 8;

    internal static GraphStatementObservation Completed(
        GraphStatementPhase phase,
        int rows,
        TimeSpan elapsed,
        IReadOnlyList<string> candidateIds) =>
        new(
            phase,
            rows,
            elapsed,
            candidateIds.Count,
            [.. candidateIds.Take(CandidateSampleLimit)]);
}

internal sealed record FrontierQueryPlan(
    IReadOnlyList<IReadOnlyList<string>> RelationshipStatements,
    IReadOnlyList<IReadOnlyList<string>> UnresolvedNameStatements)
{
    internal static FrontierQueryPlan Empty { get; } = new([], []);
}

internal sealed record GraphQueryTelemetrySnapshot(
    GraphQueryFamilyTelemetry SymbolExists,
    GraphQueryFamilyTelemetry SymbolName,
    GraphQueryFamilyTelemetry RelationshipsForward,
    GraphQueryFamilyTelemetry PendingForward,
    GraphQueryFamilyTelemetry IdentifiersForward,
    GraphQueryFamilyTelemetry RelationshipsReverse,
    GraphQueryFamilyTelemetry PendingReverse,
    GraphQueryFamilyTelemetry IdentifiersReverse,
    GraphQueryFamilyTelemetry UnresolvedIdentifiersReverse,
    GraphQueryFamilyTelemetry ResolveName,
    GraphQueryFamilyTelemetry FrontierRelationships,
    GraphQueryFamilyTelemetry FrontierUnresolvedNames,
    GraphQueryFamilyTelemetry FrontierBatch,
    GraphQueryFamilyTelemetry SupplementalEdges)
{
    public int TotalExecutions =>
        SymbolExists.Executions + SymbolName.Executions + RelationshipsForward.Executions +
        PendingForward.Executions + IdentifiersForward.Executions + RelationshipsReverse.Executions +
        PendingReverse.Executions + IdentifiersReverse.Executions + UnresolvedIdentifiersReverse.Executions +
        ResolveName.Executions + FrontierRelationships.Executions + FrontierUnresolvedNames.Executions +
        FrontierBatch.Executions + SupplementalEdges.Executions;

    public TimeSpan TotalElapsed =>
        SymbolExists.Elapsed + SymbolName.Elapsed + RelationshipsForward.Elapsed + PendingForward.Elapsed +
        IdentifiersForward.Elapsed + RelationshipsReverse.Elapsed + PendingReverse.Elapsed +
        IdentifiersReverse.Elapsed + UnresolvedIdentifiersReverse.Elapsed + ResolveName.Elapsed +
        FrontierRelationships.Elapsed + FrontierUnresolvedNames.Elapsed + FrontierBatch.Elapsed +
        SupplementalEdges.Elapsed;
}

internal sealed class GraphQueryTelemetry
{
    internal GraphQueryFamilyAccumulator SymbolExists { get; } = new();
    internal GraphQueryFamilyAccumulator SymbolName { get; } = new();
    internal GraphQueryFamilyAccumulator RelationshipsForward { get; } = new();
    internal GraphQueryFamilyAccumulator PendingForward { get; } = new();
    internal GraphQueryFamilyAccumulator IdentifiersForward { get; } = new();
    internal GraphQueryFamilyAccumulator RelationshipsReverse { get; } = new();
    internal GraphQueryFamilyAccumulator PendingReverse { get; } = new();
    internal GraphQueryFamilyAccumulator IdentifiersReverse { get; } = new();
    internal GraphQueryFamilyAccumulator UnresolvedIdentifiersReverse { get; } = new();
    internal GraphQueryFamilyAccumulator ResolveName { get; } = new();
    internal GraphQueryFamilyAccumulator FrontierRelationships { get; } = new();
    internal GraphQueryFamilyAccumulator FrontierUnresolvedNames { get; } = new();
    internal GraphQueryFamilyAccumulator FrontierBatch { get; } = new();
    internal GraphQueryFamilyAccumulator SupplementalEdges { get; } = new();

    internal GraphQueryTelemetrySnapshot Snapshot() => new(
        SymbolExists.Snapshot(),
        SymbolName.Snapshot(),
        RelationshipsForward.Snapshot(),
        PendingForward.Snapshot(),
        IdentifiersForward.Snapshot(),
        RelationshipsReverse.Snapshot(),
        PendingReverse.Snapshot(),
        IdentifiersReverse.Snapshot(),
        UnresolvedIdentifiersReverse.Snapshot(),
        ResolveName.Snapshot(),
        FrontierRelationships.Snapshot(),
        FrontierUnresolvedNames.Snapshot(),
        FrontierBatch.Snapshot(),
        SupplementalEdges.Snapshot());
}

internal sealed class GraphQueryFamilyAccumulator
{
    private int _executions;
    private long _rows;
    private TimeSpan _elapsed;

    internal void Add(long rows, TimeSpan elapsed)
    {
        _executions++;
        _rows += rows;
        _elapsed += elapsed;
    }

    internal GraphQueryFamilyTelemetry Snapshot() => new(_executions, _rows, _elapsed);
}
