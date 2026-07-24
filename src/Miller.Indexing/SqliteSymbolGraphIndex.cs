using Miller.Core.Graph;

namespace Miller.Indexing;

/// <summary>
/// On-demand symbol dependency reachability over a julie extract DB. This mirrors <see cref="SymbolGraphReader"/>
/// edge semantics without materializing the whole graph: precise <c>relationships</c> edges are read by id, and
/// fallback <c>identifiers</c> edges are resolved by name through the DB's indexed <c>symbols</c> table.
/// </summary>
public sealed class SqliteSymbolGraphIndex : ISymbolGraphReachability, IDisposable
{
    private static readonly IReadOnlyList<string> Empty = Array.Empty<string>();

    private readonly string _dbPath;
    private readonly Dictionary<(string Id, Direction Direction), IReadOnlyList<string>> _neighbourCache = new();
    private readonly Dictionary<(string Id, Direction Direction), IReadOnlyList<GraphEdge>> _edgeCache = new();
    private readonly Dictionary<string, bool> _symbolExistsCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _symbolNameCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _symbolVisibilityCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _nameResolutionCache = new(StringComparer.Ordinal);
    private IReadOnlyList<GraphEdge>? _testLinkageEdges;
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
        Direction dir) =>
        GraphTraversal.ReachWithEvidence(starts, maxDepth, limit, dir, Contains, NeighbourEvidence);

    public IReadOnlyList<string>? ShortestPath(string from, string to, int maxDepth) =>
        GraphTraversal.ShortestPath(from, to, maxDepth, Contains, Dependencies);

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

    private IEnumerable<GraphNeighbour> NeighbourEvidence(string id, Direction direction)
    {
        IEnumerable<GraphEdge> edges = direction switch
        {
            Direction.Forward => Edges(id, Direction.Forward),
            Direction.Reverse => Edges(id, Direction.Reverse),
            Direction.Both => Edges(id, Direction.Forward)
                .Concat(Edges(id, Direction.Reverse))
                .GroupBy(edge => NeighbourId(edge, id), StringComparer.Ordinal)
                .Select(group => group.OrderBy(static edge => edge, EdgeComparer).First()),
            _ => [],
        };

        return edges.Select(edge =>
        {
            string neighbourId = NeighbourId(edge, id);
            return new GraphNeighbour(
                neighbourId,
                edge.Kind,
                edge.Confidence,
                edge.Source,
                Degree(neighbourId),
                SymbolVisibility(neighbourId));
        });
    }

    private IReadOnlyList<GraphEdge> Edges(string id, Direction direction)
    {
        var key = (id, direction);
        if (_edgeCache.TryGetValue(key, out IReadOnlyList<GraphEdge>? cached))
            return cached;

        IReadOnlyList<GraphEdge> loaded = direction switch
        {
            Direction.Forward => LoadDependencyEdges(id),
            Direction.Reverse => LoadDependentEdges(id),
            _ => [],
        };
        _edgeCache[key] = loaded;
        return loaded;
    }

    private IReadOnlyList<GraphEdge> LoadDependencyEdges(string id)
    {
        if (!Contains(id))
            return [];

        var edges = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
        ReadEdges(
            """
            SELECT r.to_symbol_id AS neighbour_id, r.kind, r.confidence
            FROM relationships r
            JOIN symbols s ON s.symbol_id = r.to_symbol_id
            WHERE r.from_symbol_id = $id;
            """,
            id,
            (neighbour, kind, confidence) =>
                AddEdge(edges, id, neighbour, new GraphEdge(id, neighbour, kind, confidence, "relationship")));
        ReadEdges(
            """
            SELECT pr.target_symbol_id AS neighbour_id, p.kind,
                   MIN(p.confidence, pr.confidence) AS confidence
            FROM pending_relationships p
            JOIN pending_resolutions pr
              ON pr.pending_relationship_id = p.pending_relationship_id
            JOIN symbols s ON s.symbol_id = pr.target_symbol_id
            WHERE p.from_symbol_id = $id;
            """,
            id,
            (neighbour, kind, confidence) =>
                AddEdge(edges, id, neighbour, new GraphEdge(id, neighbour, kind, confidence, "pending_resolution")));

        using var command = Connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.name, i.kind, i.target_symbol_id,
                   ir.target_symbol_id AS overlay_target_symbol_id,
                   i.confidence, ir.confidence AS overlay_confidence
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
        int oName = reader.GetOrdinal("name");
        int oKind = reader.GetOrdinal("kind");
        int oDirect = reader.GetOrdinal("target_symbol_id");
        int oOverlay = reader.GetOrdinal("overlay_target_symbol_id");
        int oConfidence = reader.GetOrdinal("confidence");
        int oOverlayConfidence = reader.GetOrdinal("overlay_confidence");
        while (reader.Read())
        {
            string? direct = reader.IsDBNull(oDirect) ? null : reader.GetString(oDirect);
            string? overlay = reader.IsDBNull(oOverlay) ? null : reader.GetString(oOverlay);
            string? exact = direct ?? overlay;
            IReadOnlyList<string> targets = exact is null
                ? ResolveNameIds(reader.GetString(oName))
                : [exact];
            if (exact is null && targets.Count != 1)
                continue;

            string source = direct is not null
                ? "identifier_target"
                : overlay is not null
                    ? "identifier_resolution"
                    : "identifier_name";
            double confidence = overlay is not null && !reader.IsDBNull(oOverlayConfidence)
                ? reader.GetDouble(oOverlayConfidence)
                : reader.GetDouble(oConfidence);
            if (exact is null)
                confidence *= 0.5;
            foreach (string target in targets)
            {
                AddEdge(edges, id, target, new GraphEdge(
                    id, target, reader.GetString(oKind), confidence, source));
            }
        }

        foreach (GraphEdge edge in TestLinkageEdges())
        {
            if (string.Equals(edge.From, id, StringComparison.Ordinal) && Contains(edge.To))
                AddEdge(edges, id, edge.To, edge);
        }

        return OrderedEdges(edges);
    }

    private IReadOnlyList<GraphEdge> LoadDependentEdges(string id)
    {
        string? targetName = SymbolName(id);
        if (targetName is null)
            return [];

        var edges = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
        ReadEdges(
            """
            SELECT r.from_symbol_id AS neighbour_id, r.kind, r.confidence
            FROM relationships r
            JOIN symbols s ON s.symbol_id = r.from_symbol_id
            WHERE r.to_symbol_id = $id;
            """,
            id,
            (neighbour, kind, confidence) =>
                AddEdge(edges, id, neighbour, new GraphEdge(neighbour, id, kind, confidence, "relationship")));
        ReadEdges(
            """
            SELECT p.from_symbol_id AS neighbour_id, p.kind,
                   MIN(p.confidence, pr.confidence) AS confidence
            FROM pending_relationships p
            JOIN pending_resolutions pr
              ON pr.pending_relationship_id = p.pending_relationship_id
            JOIN symbols s ON s.symbol_id = p.from_symbol_id
            WHERE pr.target_symbol_id = $id;
            """,
            id,
            (neighbour, kind, confidence) =>
                AddEdge(edges, id, neighbour, new GraphEdge(neighbour, id, kind, confidence, "pending_resolution")));

        using (var command = Connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT i.containing_symbol_id, i.kind, i.target_symbol_id,
                       ir.target_symbol_id AS overlay_target_symbol_id,
                       i.confidence, ir.confidence AS overlay_confidence
                FROM identifiers i
                LEFT JOIN identifier_resolutions ir ON ir.identifier_id = i.identifier_id
                JOIN symbols s ON s.symbol_id = i.containing_symbol_id
                WHERE COALESCE(i.target_symbol_id, ir.target_symbol_id) = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            int oFrom = reader.GetOrdinal("containing_symbol_id");
            int oKind = reader.GetOrdinal("kind");
            int oDirect = reader.GetOrdinal("target_symbol_id");
            int oOverlay = reader.GetOrdinal("overlay_target_symbol_id");
            int oConfidence = reader.GetOrdinal("confidence");
            int oOverlayConfidence = reader.GetOrdinal("overlay_confidence");
            while (reader.Read())
            {
                string from = reader.GetString(oFrom);
                bool direct = !reader.IsDBNull(oDirect);
                bool overlay = !reader.IsDBNull(oOverlay);
                string source = direct ? "identifier_target" : "identifier_resolution";
                double confidence = overlay && !reader.IsDBNull(oOverlayConfidence)
                    ? reader.GetDouble(oOverlayConfidence)
                    : reader.GetDouble(oConfidence);
                AddEdge(edges, id, from, new GraphEdge(
                    from, id, reader.GetString(oKind), confidence, source));
            }
        }

        if (ResolveNameIds(targetName).Count == 1)
        {
            using var command = Connection.CreateCommand();
            command.CommandText =
                """
                SELECT i.containing_symbol_id, i.kind, i.confidence
                FROM identifiers i
                LEFT JOIN identifier_resolutions ir ON ir.identifier_id = i.identifier_id
                JOIN symbols s ON s.symbol_id = i.containing_symbol_id
                WHERE i.name = $name
                  AND COALESCE(i.target_symbol_id, ir.target_symbol_id) IS NULL;
                """;
            command.Parameters.AddWithValue("$name", targetName);
            using var reader = command.ExecuteReader();
            int oFrom = reader.GetOrdinal("containing_symbol_id");
            int oKind = reader.GetOrdinal("kind");
            int oConfidence = reader.GetOrdinal("confidence");
            while (reader.Read())
            {
                string from = reader.GetString(oFrom);
                AddEdge(edges, id, from, new GraphEdge(
                    from, id, reader.GetString(oKind), reader.GetDouble(oConfidence) * 0.5, "identifier_name"));
            }
        }

        foreach (GraphEdge edge in TestLinkageEdges())
        {
            if (string.Equals(edge.To, id, StringComparison.Ordinal))
                AddEdge(edges, id, edge.From, edge);
        }

        return OrderedEdges(edges);
    }

    private void ReadEdges(
        string sql,
        string id,
        Action<string, string, double> add)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        int oNeighbour = reader.GetOrdinal("neighbour_id");
        int oKind = reader.GetOrdinal("kind");
        int oConfidence = reader.GetOrdinal("confidence");
        while (reader.Read())
            add(reader.GetString(oNeighbour), reader.GetString(oKind), reader.GetDouble(oConfidence));
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

    private static readonly IComparer<GraphEdge> EdgeComparer =
        Comparer<GraphEdge>.Create(CompareEdges);

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

    private int Degree(string id) =>
        Dependencies(id).Count + Dependents(id).Count;

    private IReadOnlyList<GraphEdge> TestLinkageEdges() =>
        _testLinkageEdges ??= TestLinkageReader.Read(Connection);

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

        foreach (GraphEdge edge in TestLinkageEdges())
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

        foreach (GraphEdge edge in TestLinkageEdges())
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

    private string? SymbolVisibility(string id)
    {
        if (_symbolVisibilityCache.TryGetValue(id, out string? cached))
            return cached;

        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT visibility FROM symbols WHERE symbol_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        object? value = command.ExecuteScalar();
        string? visibility = value is null or DBNull ? null : Convert.ToString(
            value, System.Globalization.CultureInfo.InvariantCulture);
        _symbolVisibilityCache[id] = visibility;
        return visibility;
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
