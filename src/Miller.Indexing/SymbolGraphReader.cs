using Miller.Core.Graph;

namespace Miller.Indexing;

/// <summary>
/// The D2 edge-load + name-resolution layer (M5). Reads julie's two edge sources and unions them into the
/// resolved <see cref="GraphEdge"/> list that <see cref="MillerRepositoryIndex.Build(System.Collections.Generic.IReadOnlyList{IndexedSymbol},System.Collections.Generic.IReadOnlyList{GraphEdge})"/>
/// feeds to the Core <see cref="SymbolGraph"/>:
/// <list type="bullet">
/// <item><b><c>relationships</c></b> (precise, sparse): <c>from_symbol_id → to_symbol_id</c> directly, by id,
///   carrying <c>kind</c>. No name resolution — both endpoints are already resolved symbol ids.</item>
/// <item><b><c>identifiers</c></b> (dense): for each row with a non-NULL <c>containing_symbol_id</c> C and a
///   <c>name</c> N, resolve N to <b>every</b> indexed symbol of that name <c>{T₁…Tₖ}</c> (via the supplied
///   resolver) and emit <c>C → Tᵢ</c>, carrying <c>kind</c>, only while K stays under the caller's ambiguity
///   cap.</item>
/// </list>
///
/// <para>Drop discipline (D2): a NULL <c>containing_symbol_id</c> row has no source node and is dropped; a name
/// that resolves to no indexed symbol (an external/library ref — <c>Assert.Equal</c>, an import) is dropped,
/// bounding the graph to indexed symbols; a fallback name that resolves above <c>maxNameResolutionTargets</c> is
/// dropped as too ambiguous to be useful dependency evidence; a self-edge (a name resolving back to its own
/// container) is dropped defensively — a symbol is never its own dependency. <b>De-duplication is the graph's job</b>
/// (<see cref="SymbolGraph.Build"/> collapses duplicate <c>(from, to)</c> pairs per direction), so this reader
/// emits the union as-is, including the same <c>(from, to)</c> appearing in both sources.</para>
///
/// <para>Honesty (D2): name resolution over-approximates on homonyms — two methods both named <c>Process</c>
/// make a call to <c>Process</c> an edge to <i>both</i>. For a blast radius this is the safe direction
/// (over-include a caller rather than miss one); for context it widens the neighbour set slightly. This is the
/// documented limitation until julie's analyze pass resolves <c>identifiers.target_symbol_id</c>.</para>
///
/// <para>Same D4 read discipline as the other readers: <c>Mode=ReadOnly</c> via
/// <see cref="SqliteReadOnlyAccess.Open"/>, parameterized, single startup pass (sync by design).</para>
/// </summary>
public static class SymbolGraphReader
{
    /// <summary>
    /// Read both edge sources from the julie extract at <paramref name="dbPath"/> and union them into a resolved
    /// edge list. <paramref name="resolveName"/> maps a symbol name to every indexed symbol id of that name (the
    /// production caller passes <see cref="MillerRepositoryIndex.FindByName"/> projected to ids); it must return
    /// an empty list — never null — for an unknown name.
    /// </summary>
    /// <param name="dbPath">Path to the julie extract DB (opened <c>Mode=ReadOnly</c>).</param>
    /// <param name="resolveName">Resolves an identifier name to the indexed symbol ids it names (empty if none).</param>
    /// <returns>
    /// The unioned, drop-filtered (but NOT de-duplicated — the graph dedups) directed dependency edges.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="dbPath"/> is null/empty/whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="resolveName"/> is null.</exception>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The DB's directory is not writable (WAL sidecar trap).</exception>
    public static IReadOnlyList<GraphEdge> Read(
        string dbPath,
        Func<string, IReadOnlyList<string>> resolveName,
        int maxNameResolutionTargets = int.MaxValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(resolveName);
        if (maxNameResolutionTargets < 1)
            throw new ArgumentOutOfRangeException(
                nameof(maxNameResolutionTargets),
                maxNameResolutionTargets,
                "The fallback name-resolution target cap must be positive.");

        // Shared D4 read discipline (file-exists + writable-dir probe + Mode=ReadOnly + SQLITE_READONLY map).
        using var connection = SqliteReadOnlyAccess.Open(dbPath);

        var edges = new List<GraphEdge>();
        ReadRelationships(connection, edges);
        ReadIdentifiers(connection, resolveName, edges, maxNameResolutionTargets);
        return edges;
    }

    // relationships: precise by-id edges. Both endpoints are NOT NULL resolved ids; we only guard self-loops.
    private static void ReadRelationships(Microsoft.Data.Sqlite.SqliteConnection connection, List<GraphEdge> edges)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT from_symbol_id, to_symbol_id, kind
            FROM relationships;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string from = reader.GetString(0); // from_symbol_id NOT NULL
            string to = reader.GetString(1);   // to_symbol_id   NOT NULL
            string kind = reader.GetString(2); // kind           NOT NULL

            if (string.Equals(from, to, StringComparison.Ordinal))
                continue; // defensive self-loop drop (the graph drops it too — defense in depth)

            edges.Add(new GraphEdge(from, to, kind));
        }
    }

    // identifiers: dense name-resolved edges. Only rows with a source node (non-NULL containing_symbol_id);
    // the name resolves to every indexed symbol of that name (homonym over-approximation, D2).
    private static void ReadIdentifiers(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Func<string, IReadOnlyList<string>> resolveName,
        List<GraphEdge> edges,
        int maxNameResolutionTargets)
    {
        using var command = connection.CreateCommand();
        // WHERE containing_symbol_id IS NOT NULL: a NULL source node (namespace ref) yields no edge, so we never
        // read those rows at all.
        command.CommandText = """
            SELECT name, kind, containing_symbol_id
            FROM identifiers
            WHERE containing_symbol_id IS NOT NULL;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(0);      // name                 NOT NULL
            string kind = reader.GetString(1);      // kind                 NOT NULL
            string from = reader.GetString(2);      // containing_symbol_id  NOT NULL by the WHERE

            // Resolve the name to every indexed symbol it names. A name with no indexed target (external/library
            // ref) yields an empty list → no edge (bounds the graph to indexed symbols).
            var targets = resolveName(name);
            if (targets is null)
                continue; // a misbehaving resolver returned null; treat as "no indexed target"
            if (targets.Count > maxNameResolutionTargets)
                continue; // too ambiguous to be useful, and explosive on large repos without target_symbol_id

            foreach (var to in targets)
            {
                if (string.Equals(from, to, StringComparison.Ordinal))
                    continue; // a name resolving back to its own container is a self-loop: drop it

                edges.Add(new GraphEdge(from, to, kind));
            }
        }
    }
}
