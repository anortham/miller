using Miller.Core.Graph;
using Miller.Indexing.Reads;

namespace Miller.Indexing;

/// <summary>
/// Reads extractor relationship evidence into the dependency graph:
/// <list type="bullet">
/// <item><b><c>relationships</c></b> (precise, sparse): <c>from_symbol_id → to_symbol_id</c> directly, by id,
///   carrying <c>kind</c>. No name resolution — both endpoints are already resolved symbol ids.</item>
/// <item><b>resolved <c>pending_relationships</c></b> (precise, sparse): join each pending row to
///   <c>pending_resolutions</c> by <c>pending_relationship_id</c>, then emit its <c>from_symbol_id →
///   target_symbol_id</c> by id, carrying the pending row's <c>kind</c>. Unresolved pending rows are omitted.</item>
/// <item><b><c>identifiers</c></b> (dense): take the target from the <c>identifier_resolutions</c> row joined by
///   <c>identifier_id</c> — that table is the sole source of resolution outcomes. Use name fallback only when the
///   name resolves to exactly one symbol.</item>
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
        return session.Read(connection => Read(connection, resolveName));
    }

    private static IReadOnlyList<GraphEdge> Read(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Func<string, IReadOnlyList<string>> resolveName)
    {
        var edges = new List<GraphEdge>();
        ReadRelationships(connection, edges);
        ReadResolvedPendingRelationships(connection, edges);
        ReadIdentifiers(connection, resolveName, edges);
        edges.AddRange(TestLinkageReader.Read(connection));
        return edges;
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

    private static void ReadResolvedPendingRelationships(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        List<GraphEdge> edges)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pending.from_symbol_id, resolution.target_symbol_id, pending.kind,
                   MIN(pending.confidence, resolution.confidence) AS confidence
            FROM pending_relationships AS pending
            INNER JOIN pending_resolutions AS resolution
                ON resolution.pending_relationship_id = pending.pending_relationship_id;
            """;

        using var reader = command.ExecuteReader();
        int oFrom = reader.GetOrdinal("from_symbol_id");
        int oTo = reader.GetOrdinal("target_symbol_id");
        int oKind = reader.GetOrdinal("kind");
        int oConfidence = reader.GetOrdinal("confidence");
        while (reader.Read())
        {
            string from = reader.GetString(oFrom);
            string to = reader.GetString(oTo);
            string kind = reader.GetString(oKind);

            if (!string.Equals(from, to, StringComparison.Ordinal))
                edges.Add(new GraphEdge(
                    from, to, kind, reader.GetDouble(oConfidence), "pending_resolution"));
        }
    }

    private static void ReadIdentifiers(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Func<string, IReadOnlyList<string>> resolveName,
        List<GraphEdge> edges)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.name, i.kind, i.containing_symbol_id,
                   ir.target_symbol_id,
                   i.confidence,
                   ir.confidence AS overlay_confidence
            FROM identifiers i
            LEFT JOIN identifier_resolutions ir ON ir.identifier_id = i.identifier_id
            WHERE i.containing_symbol_id IS NOT NULL;
            """;

        using var reader = command.ExecuteReader();
        int oName = reader.GetOrdinal("name");
        int oKind = reader.GetOrdinal("kind");
        int oContaining = reader.GetOrdinal("containing_symbol_id");
        int oTarget = reader.GetOrdinal("target_symbol_id");
        int oConfidence = reader.GetOrdinal("confidence");
        int oOverlayConfidence = reader.GetOrdinal("overlay_confidence");
        while (reader.Read())
        {
            string name = reader.GetString(oName);
            string kind = reader.GetString(oKind);
            string from = reader.GetString(oContaining);
            string? exactTarget = reader.IsDBNull(oTarget) ? null : reader.GetString(oTarget);
            IReadOnlyList<string> targets = exactTarget is null
                ? resolveName(name) ?? Array.Empty<string>()
                : [exactTarget];
            if (exactTarget is null && targets.Count != 1)
                continue;

            foreach (var to in targets)
            {
                if (string.Equals(from, to, StringComparison.Ordinal))
                    continue;

                string source = exactTarget is not null ? "identifier_target" : "identifier_name";
                double confidence = exactTarget is not null && !reader.IsDBNull(oOverlayConfidence)
                    ? reader.GetDouble(oOverlayConfidence)
                    : reader.GetDouble(oConfidence);
                if (exactTarget is null)
                    confidence *= 0.5;
                edges.Add(new GraphEdge(from, to, kind, confidence, source));
            }
        }
    }
}
