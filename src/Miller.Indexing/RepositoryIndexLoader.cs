namespace Miller.Indexing;

/// <summary>
/// The SINGLE production build path (D9): read symbols + edges from a julie extract and build a
/// <see cref="MillerRepositoryIndex"/> (index + dependency graph) as one immutable unit. Both the startup
/// bootstrap and the freshness rebuild route through here, so the graph is always ready when the index is
/// published (the holder's atomic reference swap, M3) — no build site can ship an index without its graph.
///
/// <para>Order of operations: read symbols (<see cref="SqliteSymbolReader.Read"/>) → build the name→ids map
/// from those symbols → read edges (<see cref="SymbolGraphReader.Read"/>, resolving identifier names through
/// that map) → <see cref="MillerRepositoryIndex.Build(System.Collections.Generic.IReadOnlyList{IndexedSymbol},System.Collections.Generic.IReadOnlyList{Miller.Core.Graph.GraphEdge})"/>.
/// The name map is built once here (not via the not-yet-constructed index) so the edge resolver and the index
/// agree on the exact symbol set: a name resolves only to symbols this extract actually indexed (the same
/// bound the graph then enforces on edge endpoints).</para>
///
/// <para>Each read uses the shared D4 read discipline (<c>Mode=ReadOnly</c>, parameterized) via the underlying
/// readers; this is a single startup/rebuild pass (sync by design).</para>
/// </summary>
public static class RepositoryIndexLoader
{
    /// <summary>
    /// Read the julie extract at <paramref name="dbPath"/> and build the index + dependency graph as one unit.
    /// </summary>
    /// <param name="dbPath">Path to the julie extract DB (opened <c>Mode=ReadOnly</c> by the readers).</param>
    /// <returns>The immutable repository index with its populated dependency graph.</returns>
    /// <exception cref="ArgumentException"><paramref name="dbPath"/> is null/empty/whitespace.</exception>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The DB's directory is not writable (WAL sidecar trap).</exception>
    /// <exception cref="IncompatibleExtractException">The DB is not a compatible v7.13.0 julie extract.</exception>
    public static MillerRepositoryIndex Load(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        // 1) Read the symbols (the schema gate fires here, before the edge reads).
        var symbols = SqliteSymbolReader.Read(dbPath);

        // 2) Build the name→ids map from THESE symbols, so the edge resolver and the index share the exact same
        //    symbol set: an identifier name resolves only to symbols this extract indexed (the graph then bounds
        //    edge endpoints to the same set). Multi-valued by design — homonyms resolve to every matching id (D2).
        var nameToIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (!nameToIds.TryGetValue(symbol.Name, out var ids))
                nameToIds[symbol.Name] = ids = new List<string>(1);
            ids.Add(symbol.SymbolId);
        }

        // 3) Read + resolve the edges (relationships ∪ name-resolved identifiers).
        var edges = SymbolGraphReader.Read(
            dbPath,
            name => nameToIds.TryGetValue(name, out var ids)
                ? ids
                : (IReadOnlyList<string>)Array.Empty<string>());

        // 4) Build the index + graph as one immutable unit.
        return MillerRepositoryIndex.Build(symbols, edges);
    }
}
