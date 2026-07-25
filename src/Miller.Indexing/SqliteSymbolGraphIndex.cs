using Miller.Core.Graph;

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

    private readonly string _dbPath;
    private readonly Dictionary<(string Id, Direction Direction), IReadOnlyList<string>> _neighbourCache = new();
    private readonly Dictionary<(string Id, Direction Direction), IReadOnlyList<GraphNeighbour>>
        _evidenceCache = new();
    private readonly Dictionary<string, bool> _symbolExistsCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _symbolNameCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _nameResolutionCache = new(StringComparer.Ordinal);
    private IReadOnlyList<GraphEdge>? _supplementalEdges;
    private Microsoft.Data.Sqlite.SqliteConnection? _connection;

    public SqliteSymbolGraphIndex(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        _dbPath = dbPath;
    }

    public IReadOnlyList<ReachedNode> Reach(IEnumerable<string> starts, int maxDepth, int limit, Direction dir) =>
        GraphTraversal.Reach(starts, maxDepth, limit, dir, Contains, Neighbours);

    public GraphReachResult ReachWithEvidence(
        IEnumerable<string> starts,
        int maxDepth,
        int limit,
        Direction dir)
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
    }

    public IReadOnlyList<string>? ShortestPath(string from, string to, int maxDepth) =>
        GraphTraversal.ShortestPath(from, to, maxDepth, Contains, Dependencies);

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

        string values = string.Join(
            ", ",
            Enumerable.Range(0, missingIds.Length).Select(index => $"($id{index})"));
        using (var command = Connection.CreateCommand())
        {
            command.CommandText = $"""
                WITH candidates(id) AS (VALUES {values}),
                selected_edges(current_id, from_id, to_id, kind, confidence, source) AS (
                    SELECT candidates.id, r.from_symbol_id, r.to_symbol_id,
                           r.kind, r.confidence, 'relationship'
                    FROM candidates
                    JOIN relationships r ON r.from_symbol_id = candidates.id
                    JOIN symbols target_symbol ON target_symbol.symbol_id = r.to_symbol_id
                    WHERE $forward = 1
                    UNION ALL
                    SELECT candidates.id, r.from_symbol_id, r.to_symbol_id,
                           r.kind, r.confidence, 'relationship'
                    FROM candidates
                    JOIN relationships r ON r.to_symbol_id = candidates.id
                    JOIN symbols source_symbol ON source_symbol.symbol_id = r.from_symbol_id
                    WHERE $reverse = 1
                    UNION ALL
                    SELECT candidates.id, p.from_symbol_id, pr.target_symbol_id, p.kind,
                           MIN(p.confidence, pr.confidence), 'pending_resolution'
                    FROM candidates
                    JOIN pending_relationships p ON p.from_symbol_id = candidates.id
                    JOIN pending_resolutions pr
                      ON pr.pending_relationship_id = p.pending_relationship_id
                    JOIN symbols target_symbol ON target_symbol.symbol_id = pr.target_symbol_id
                    WHERE $forward = 1
                    UNION ALL
                    SELECT candidates.id, p.from_symbol_id, pr.target_symbol_id, p.kind,
                           MIN(p.confidence, pr.confidence), 'pending_resolution'
                    FROM candidates
                    JOIN pending_resolutions pr ON pr.target_symbol_id = candidates.id
                    JOIN pending_relationships p
                      ON p.pending_relationship_id = pr.pending_relationship_id
                    JOIN symbols source_symbol ON source_symbol.symbol_id = p.from_symbol_id
                    WHERE $reverse = 1
                    UNION ALL
                    SELECT candidates.id,
                           i.containing_symbol_id,
                           COALESCE(i.target_symbol_id, ir.target_symbol_id),
                           i.kind,
                           CASE
                               WHEN ir.target_symbol_id IS NOT NULL AND ir.confidence IS NOT NULL
                                   THEN ir.confidence
                               ELSE i.confidence
                           END,
                           CASE
                               WHEN i.target_symbol_id IS NOT NULL THEN 'identifier_target'
                               ELSE 'identifier_resolution'
                           END
                    FROM candidates
                    JOIN identifiers i ON i.containing_symbol_id = candidates.id
                    LEFT JOIN identifier_resolutions ir ON ir.identifier_id = i.identifier_id
                    JOIN symbols target_symbol
                      ON target_symbol.symbol_id = COALESCE(i.target_symbol_id, ir.target_symbol_id)
                    WHERE $forward = 1
                    UNION ALL
                    SELECT candidates.id, i.containing_symbol_id, i.target_symbol_id,
                           i.kind,
                           CASE
                               WHEN ir.target_symbol_id IS NOT NULL AND ir.confidence IS NOT NULL
                                   THEN ir.confidence
                               ELSE i.confidence
                           END,
                           'identifier_target'
                    FROM candidates
                    JOIN identifiers i ON i.target_symbol_id = candidates.id
                    LEFT JOIN identifier_resolutions ir ON ir.identifier_id = i.identifier_id
                    JOIN symbols source_symbol ON source_symbol.symbol_id = i.containing_symbol_id
                    WHERE $reverse = 1
                    UNION ALL
                    SELECT candidates.id, i.containing_symbol_id, ir.target_symbol_id,
                           i.kind, COALESCE(ir.confidence, i.confidence), 'identifier_resolution'
                    FROM candidates
                    JOIN identifier_resolutions ir ON ir.target_symbol_id = candidates.id
                    JOIN identifiers i ON i.identifier_id = ir.identifier_id
                    JOIN symbols source_symbol ON source_symbol.symbol_id = i.containing_symbol_id
                    WHERE $reverse = 1 AND i.target_symbol_id IS NULL
                    UNION ALL
                    SELECT candidates.id, i.containing_symbol_id, target_symbol.symbol_id,
                           i.kind, i.confidence * 0.5, 'identifier_name'
                    FROM candidates
                    JOIN identifiers i ON i.containing_symbol_id = candidates.id
                    LEFT JOIN identifier_resolutions ir ON ir.identifier_id = i.identifier_id
                    JOIN symbols target_symbol ON target_symbol.name = i.name
                    WHERE $forward = 1
                      AND COALESCE(i.target_symbol_id, ir.target_symbol_id) IS NULL
                      AND NOT EXISTS (
                          SELECT 1
                          FROM symbols duplicate
                          WHERE duplicate.name = i.name
                            AND duplicate.symbol_id <> target_symbol.symbol_id
                      )
                    UNION ALL
                    SELECT candidates.id, i.containing_symbol_id, target_symbol.symbol_id,
                           i.kind, i.confidence * 0.5, 'identifier_name'
                    FROM candidates
                    JOIN symbols target_symbol ON target_symbol.symbol_id = candidates.id
                    JOIN identifiers i ON i.name = target_symbol.name
                    LEFT JOIN identifier_resolutions ir ON ir.identifier_id = i.identifier_id
                    JOIN symbols source_symbol ON source_symbol.symbol_id = i.containing_symbol_id
                    WHERE $reverse = 1
                      AND COALESCE(i.target_symbol_id, ir.target_symbol_id) IS NULL
                      AND NOT EXISTS (
                          SELECT 1
                          FROM symbols duplicate
                          WHERE duplicate.name = target_symbol.name
                            AND duplicate.symbol_id <> target_symbol.symbol_id
                      )
                )
                SELECT current_id, from_id, to_id, kind, confidence, source
                FROM selected_edges
                WHERE from_id <> to_id;
                """;
            for (int index = 0; index < missingIds.Length; index++)
                command.Parameters.AddWithValue($"$id{index}", missingIds[index]);
            command.Parameters.AddWithValue(
                "$forward",
                direction is Direction.Forward or Direction.Both ? 1 : 0);
            command.Parameters.AddWithValue(
                "$reverse",
                direction is Direction.Reverse or Direction.Both ? 1 : 0);

            using var reader = command.ExecuteReader();
            int currentOrdinal = reader.GetOrdinal("current_id");
            int fromOrdinal = reader.GetOrdinal("from_id");
            int toOrdinal = reader.GetOrdinal("to_id");
            int kindOrdinal = reader.GetOrdinal("kind");
            int confidenceOrdinal = reader.GetOrdinal("confidence");
            int sourceOrdinal = reader.GetOrdinal("source");
            while (reader.Read())
            {
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
        }

        foreach (GraphEdge edge in SupplementalEdges())
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
        if (direction != Direction.Both && cacheResults)
        {
            foreach ((string id, IReadOnlyList<GraphNeighbour> neighbours) in loaded)
            {
                if (_evidenceCache.Count < MaximumEvidenceCacheEntries)
                    _evidenceCache[(id, direction)] = neighbours;
            }
            return ids.ToDictionary(
                static id => id,
                id => _evidenceCache.TryGetValue((id, direction), out var cached)
                    ? cached
                    : loaded[id],
                StringComparer.Ordinal);
        }
        return loaded;
    }

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

    private IReadOnlyList<GraphEdge> SupplementalEdges() =>
        _supplementalEdges ??=
        [
            .. TestLinkageReader.Read(Connection),
            .. BlazorComponentGraphReader.Read(
                _dbPath,
                SqliteBridgeReader.ReadStructuralFacts(
                    _dbPath,
                    [BridgeStructuralPatterns.BlazorComponentReference])),
        ];

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
            using var reader = command.ExecuteReader();
            while (reader.Read())
                AddCandidate(ids, id, reader.GetString(0));
        }

        using (var command = Connection.CreateCommand())
        {
            command.CommandText = """
                SELECT pr.target_symbol_id
                FROM pending_relationships p
                JOIN pending_resolutions pr
                  ON pr.pending_relationship_id = p.pending_relationship_id
                JOIN symbols s ON s.symbol_id = pr.target_symbol_id
                WHERE p.from_symbol_id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                AddCandidate(ids, id, reader.GetString(0));
        }

        using (var command = Connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.name, COALESCE(i.target_symbol_id, ir.target_symbol_id)
                FROM identifiers i
                LEFT JOIN identifier_resolutions ir ON ir.identifier_id = i.identifier_id
                LEFT JOIN symbols target
                  ON target.symbol_id = COALESCE(i.target_symbol_id, ir.target_symbol_id)
                WHERE i.containing_symbol_id = $id
                  AND (
                      COALESCE(i.target_symbol_id, ir.target_symbol_id) IS NULL
                      OR target.symbol_id IS NOT NULL
                  );
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                {
                    AddCandidate(ids, id, reader.GetString(1));
                    continue;
                }

                IReadOnlyList<string> targets = ResolveNameIds(reader.GetString(0));
                if (targets.Count == 1)
                    AddCandidate(ids, id, targets[0]);
            }
        }

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
            using var reader = command.ExecuteReader();
            while (reader.Read())
                AddCandidate(ids, id, reader.GetString(0));
        }

        using (var command = Connection.CreateCommand())
        {
            command.CommandText = """
                SELECT p.from_symbol_id
                FROM pending_relationships p
                JOIN pending_resolutions pr
                  ON pr.pending_relationship_id = p.pending_relationship_id
                JOIN symbols s ON s.symbol_id = p.from_symbol_id
                WHERE pr.target_symbol_id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                AddCandidate(ids, id, reader.GetString(0));
        }

        using (var command = Connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.containing_symbol_id
                FROM identifiers i
                LEFT JOIN identifier_resolutions ir ON ir.identifier_id = i.identifier_id
                JOIN symbols s ON s.symbol_id = i.containing_symbol_id
                WHERE COALESCE(i.target_symbol_id, ir.target_symbol_id) = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                AddCandidate(ids, id, reader.GetString(0));
        }

        IReadOnlyList<string> nameTargets = ResolveNameIds(targetName);
        if (nameTargets.Count == 1)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                SELECT i.containing_symbol_id
                FROM identifiers i
                LEFT JOIN identifier_resolutions ir ON ir.identifier_id = i.identifier_id
                JOIN symbols s ON s.symbol_id = i.containing_symbol_id
                WHERE i.name = $name
                  AND COALESCE(i.target_symbol_id, ir.target_symbol_id) IS NULL;
                """;
            command.Parameters.AddWithValue("$name", targetName);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                AddCandidate(ids, id, reader.GetString(0));
        }

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

    private Microsoft.Data.Sqlite.SqliteConnection Connection => _connection ??= SqliteReadOnlyAccess.Open(_dbPath);

    private static void AddCandidate(SortedSet<string> ids, string sourceId, string candidateId)
    {
        if (!string.Equals(sourceId, candidateId, StringComparison.Ordinal))
            ids.Add(candidateId);
    }

    private bool SymbolExists(string id)
    {
        if (_symbolExistsCache.TryGetValue(id, out bool exists))
            return exists;

        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM symbols WHERE symbol_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        exists = command.ExecuteScalar() is not null;
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
        string? name = command.ExecuteScalar() as string;
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
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));

        IReadOnlyList<string> result = ids.Count == 0 ? Empty : ids.ToArray();
        _nameResolutionCache[name] = result;
        return result;
    }
}
