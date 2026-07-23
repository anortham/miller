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
    private readonly Dictionary<string, bool> _symbolExistsCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _symbolNameCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _nameResolutionCache = new(StringComparer.Ordinal);
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
        GraphTraversal.ReachWithEvidence(starts, maxDepth, limit, dir, Contains, Neighbours);

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
