using Miller.Core.Graph;

namespace Miller.Indexing;

/// <summary>
/// On-demand symbol dependency reachability over a julie extract DB. This mirrors <see cref="SymbolGraphReader"/>
/// edge semantics without materializing the whole graph: precise <c>relationships</c> edges are read by id, and
/// fallback <c>identifiers</c> edges are resolved by name through the DB's indexed <c>symbols</c> table.
/// </summary>
public sealed class SqliteSymbolGraphIndex : ISymbolGraphReachability, IDisposable
{
    private const int DefaultMaxNameResolutionTargets = 16;

    private static readonly IReadOnlyList<string> Empty = Array.Empty<string>();

    private readonly string _dbPath;
    private readonly int _maxNameResolutionTargets;
    private readonly Dictionary<(string Id, Direction Direction), IReadOnlyList<string>> _neighbourCache = new();
    private readonly Dictionary<string, bool> _symbolExistsCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _symbolNameCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _nameResolutionCache = new(StringComparer.Ordinal);
    private Microsoft.Data.Sqlite.SqliteConnection? _connection;

    public SqliteSymbolGraphIndex(
        string dbPath,
        int maxNameResolutionTargets = DefaultMaxNameResolutionTargets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (maxNameResolutionTargets < 1)
            throw new ArgumentOutOfRangeException(
                nameof(maxNameResolutionTargets),
                maxNameResolutionTargets,
                "The fallback name-resolution target cap must be positive.");

        _dbPath = dbPath;
        _maxNameResolutionTargets = maxNameResolutionTargets;
    }

    public IReadOnlyList<ReachedNode> Reach(IEnumerable<string> starts, int maxDepth, int limit, Direction dir)
    {
        ArgumentNullException.ThrowIfNull(starts);

        if (maxDepth <= 0 || limit <= 0)
            return [];

        var hop = new Dictionary<string, int>(StringComparer.Ordinal);
        var frontier = new Queue<string>();

        foreach (string start in starts)
        {
            if (!Contains(start))
                continue;
            if (hop.TryAdd(start, 0))
                frontier.Enqueue(start);
        }

        while (frontier.Count > 0)
        {
            string current = frontier.Dequeue();
            int currentHop = hop[current];
            if (currentHop >= maxDepth)
                continue;

            int nextHop = currentHop + 1;
            foreach (string neighbour in Neighbours(current, dir))
            {
                if (hop.ContainsKey(neighbour))
                    continue;
                hop[neighbour] = nextHop;
                frontier.Enqueue(neighbour);
            }
        }

        return hop
            .Where(static kv => kv.Value > 0)
            .OrderBy(static kv => kv.Value)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(static kv => new ReachedNode(kv.Key, kv.Value))
            .ToArray();
    }

    public IReadOnlyList<string>? ShortestPath(string from, string to, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (!Contains(from) || !Contains(to))
            return null;

        if (string.Equals(from, to, StringComparison.Ordinal))
            return [from];

        if (maxDepth <= 0)
            return null;

        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal) { [from] = 0 };
        var frontier = new Queue<string>();
        frontier.Enqueue(from);

        while (frontier.Count > 0)
        {
            string current = frontier.Dequeue();
            int currentDepth = depth[current];
            if (currentDepth >= maxDepth)
                continue;

            foreach (string neighbour in Dependencies(current))
            {
                if (depth.ContainsKey(neighbour))
                    continue;

                depth[neighbour] = currentDepth + 1;
                parent[neighbour] = current;

                if (string.Equals(neighbour, to, StringComparison.Ordinal))
                    return Reconstruct(parent, from, to);

                frontier.Enqueue(neighbour);
            }
        }

        return null;
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
                SELECT name
                FROM identifiers
                WHERE containing_symbol_id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                IReadOnlyList<string> targets = ResolveNameIds(reader.GetString(0));
                if (targets.Count > _maxNameResolutionTargets)
                    continue;

                foreach (string targetId in targets)
                    AddCandidate(ids, id, targetId);
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

        IReadOnlyList<string> nameTargets = ResolveNameIds(targetName);
        if (nameTargets.Count <= _maxNameResolutionTargets)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                SELECT i.containing_symbol_id
                FROM identifiers i
                JOIN symbols s ON s.symbol_id = i.containing_symbol_id
                WHERE i.name = $name AND i.containing_symbol_id IS NOT NULL;
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
        if (string.Equals(sourceId, candidateId, StringComparison.Ordinal))
            return;
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
        if (name is not null)
            _symbolExistsCache[id] = true;
        return name;
    }

    private IReadOnlyList<string> ResolveNameIds(string name)
    {
        if (_nameResolutionCache.TryGetValue(name, out IReadOnlyList<string>? cached))
            return cached;

        using var command = Connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_id
            FROM symbols
            WHERE name = $name
            ORDER BY path, start_line, symbol_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$limit", _maxNameResolutionTargets + 1);

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));

        IReadOnlyList<string> result = ids.Count == 0 ? Empty : ids;
        _nameResolutionCache[name] = result;
        foreach (string id in result)
            _symbolExistsCache[id] = true;
        return result;
    }

    private static IReadOnlyList<string> Reconstruct(IReadOnlyDictionary<string, string> parent, string from, string to)
    {
        var reversed = new List<string> { to };
        string node = to;
        while (!string.Equals(node, from, StringComparison.Ordinal))
        {
            node = parent[node];
            reversed.Add(node);
        }
        reversed.Reverse();
        return reversed;
    }
}
