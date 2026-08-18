using Miller.Core.Graph;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;

namespace Miller.Indexing;

/// <summary>
/// Reads extractor relationship evidence into the dependency graph:
/// <list type="bullet">
/// <item><b><c>relationships</c></b> (precise, sparse): <c>from_symbol_id → to_symbol_id</c> directly, by id,
///   carrying <c>kind</c>. No name resolution — both endpoints are already resolved symbol ids.</item>
/// <item><b>resolved <c>pending_relationships</c></b> (precise, sparse): resolve each pending row at query
///   time, then emit its <c>from_symbol_id → target</c> by id, carrying the pending row's <c>kind</c>.
///   Unresolved pending rows are omitted.</item>
/// <item><b><c>identifiers</c></b> (dense): resolve each identifier at query time. Use name fallback only when
///   the outcome is not Resolved and the name maps to exactly one symbol.</item>
/// </list>
///
/// <para>Rows without a source node, external names, ambiguous fallback names, and self-edges are omitted.
/// <see cref="SymbolGraph.Build"/> deduplicates repeated endpoint pairs.</para>
///
/// <para>Same D4 read discipline as the other readers: <c>Mode=ReadOnly</c> via
/// <see cref="SqliteReadOnlyAccess.Open"/>, parameterized, single startup pass (sync by design).</para>
/// </summary>
public static class SymbolGraphReader
{
    /// <summary>
    /// Read exact relationship sources plus unambiguous identifier fallback from the extract.
    /// </summary>
    /// <param name="dbPath">Path to the julie extract DB (opened <c>Mode=ReadOnly</c>).</param>
    /// <param name="resolveName">Provides bounded fallback candidates for unresolved identifiers.</param>
    /// <returns>
    /// The unioned, drop-filtered (but NOT de-duplicated — the graph dedups) directed dependency edges.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="dbPath"/> is null/empty/whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="resolveName"/> is null.</exception>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The DB's directory is not writable (WAL sidecar trap).</exception>
    public static IReadOnlyList<GraphEdge> Read(
        string dbPath,
        Func<string, IReadOnlyList<string>> resolveName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(resolveName);
        using LegacyArtifactReadSession session = LegacyArtifactReadSession.Open(dbPath);
        return ReadSession(session, resolveName);
    }

    public static IReadOnlyList<GraphEdge> ReadSession(
        IWorkspaceReadSession session,
        Func<string, IReadOnlyList<string>> resolveName)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(resolveName);
        return session.Read(connection => Read(session, connection, resolveName));
    }

    private static IReadOnlyList<GraphEdge> Read(
        IWorkspaceReadSession session,
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Func<string, IReadOnlyList<string>> resolveName)
    {
        var edges = new List<GraphEdge>();
        ReadRelationships(connection, edges);
        if (HasTable(connection, "files") && HasTable(connection, "identifiers"))
        {
            QueryTimeResolutionReader reader = ReferenceEvidenceReader.ReaderFor(session, connection);
            IReadOnlyList<string> symbolIds = ReadSymbolIds(connection);
            foreach (FamilyGraphResolutionEdge edge in reader.ReadResolutionEdges(
                         connection, symbolIds, Direction.Both, statementObserver: null))
            {
                if (!string.Equals(edge.FromId, edge.ToId, StringComparison.Ordinal))
                    edges.Add(new GraphEdge(edge.FromId, edge.ToId, edge.Kind, edge.Confidence, edge.Source));
            }

            foreach (FamilyGraphUnresolvedNameEdge edge in reader.ReadUnresolvedNameEdges(
                         connection, symbolIds, Direction.Both, statementObserver: null))
            {
                if (!string.Equals(edge.FromId, edge.ToId, StringComparison.Ordinal))
                    edges.Add(new GraphEdge(edge.FromId, edge.ToId, edge.Kind, edge.Confidence, edge.Source));
            }
        }

        _ = resolveName;
        edges.AddRange(TestLinkageReader.Read(connection));
        return edges;
    }

    private static bool HasTable(Microsoft.Data.Sqlite.SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private static IReadOnlyList<string> ReadSymbolIds(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT symbol_id FROM symbols;";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    // relationships: precise by-id edges. Both endpoints are NOT NULL resolved ids; we only guard self-loops.
    private static void ReadRelationships(Microsoft.Data.Sqlite.SqliteConnection connection, List<GraphEdge> edges)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT from_symbol_id, to_symbol_id, kind, confidence
            FROM relationships;
            """;

        using var reader = command.ExecuteReader();
        // By-name reads (D6): a future column add/reorder can never silently shift a value into the wrong field.
        int oFrom = reader.GetOrdinal("from_symbol_id");
        int oTo = reader.GetOrdinal("to_symbol_id");
        int oKind = reader.GetOrdinal("kind");
        int oConfidence = reader.GetOrdinal("confidence");
        while (reader.Read())
        {
            string from = reader.GetString(oFrom); // from_symbol_id NOT NULL
            string to = reader.GetString(oTo);     // to_symbol_id   NOT NULL
            string kind = reader.GetString(oKind); // kind           NOT NULL

            if (string.Equals(from, to, StringComparison.Ordinal))
                continue; // defensive self-loop drop (the graph drops it too — defense in depth)

            edges.Add(new GraphEdge(from, to, kind, reader.GetDouble(oConfidence), "relationship"));
        }
    }
}

