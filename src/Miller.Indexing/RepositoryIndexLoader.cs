using Miller.Core.Graph;

// Miller.Indexing also defines its own inspect-detail record `SymbolDetail` (SymbolDetail.cs), so the Core bridge
// contract must be aliased to disambiguate — an unqualified `SymbolDetail` here binds to the Indexing one.
using CoreSymbolDetail = Miller.Core.Contracts.SymbolDetail;

namespace Miller.Indexing;

/// <summary>
/// The SINGLE production build path (D9): read symbols + edges + bridge breadcrumbs from a julie extract and build
/// a <see cref="MillerRepositoryIndex"/> (index + dependency graph + M4 cross-language bridge graph) as one
/// immutable unit. Both the startup bootstrap and the freshness rebuild route through here, so all three graphs are
/// always ready when the index is published (the holder's atomic reference swap, M3) — no build site can ship an
/// index without its graphs.
///
/// <para>Order of operations: read symbols (<see cref="SqliteSymbolReader.Read"/>) → build the name→ids map
/// from those symbols → read the bridge breadcrumbs (<see cref="SqliteBridgeReader.Read"/>) → read edges
/// (<see cref="SymbolGraphReader.Read"/>, resolving identifier names through that map) → project the symbols to Core
/// <see cref="SymbolDetail"/>s and run <see cref="BridgeGraphBuilder.Build"/> →
/// <see cref="MillerRepositoryIndex.Build(System.Collections.Generic.IReadOnlyList{IndexedSymbol},System.Collections.Generic.IReadOnlyList{Miller.Core.Graph.GraphEdge},Miller.Core.Graph.BridgeGraph)"/>.
/// The name map is built once here (not via the not-yet-constructed index) so the edge resolver and the index
/// agree on the exact symbol set: a name resolves only to symbols this extract actually indexed (the same
/// bound the graph then enforces on edge endpoints).</para>
///
/// <para>Each read uses the shared D4 read discipline (<c>Mode=ReadOnly</c>, parameterized) via the underlying
/// readers; this is a single startup/rebuild pass (sync by design).</para>
/// </summary>
public static class RepositoryIndexLoader
{
    private const int MaxIdentifierFallbackTargets = 16;

    /// <summary>
    /// Read the julie extract at <paramref name="dbPath"/> and build the index + dependency graph + bridge graph as
    /// one immutable unit.
    /// </summary>
    /// <param name="dbPath">Path to the julie extract DB (opened <c>Mode=ReadOnly</c> by the readers).</param>
    /// <param name="onBridgeGraphBuilt">
    /// Optional measurement callback invoked with the wall-clock time the <see cref="BridgeGraphBuilder.Build"/> pass
    /// took (plan Task 9 requires MEASURING the bridge-graph build cost — name resolution over the code-symbol set is
    /// the cost driver). Miller.Indexing is logger-free by design, so the caller (the bootstrap/freshness services,
    /// which hold an <c>ILogger</c>) supplies the sink. No caching/persistence is implemented — measure only.
    /// </param>
    /// <returns>The immutable repository index with its populated dependency + bridge graphs.</returns>
    /// <exception cref="ArgumentException"><paramref name="dbPath"/> is null/empty/whitespace.</exception>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The DB's directory is not writable (WAL sidecar trap).</exception>
    /// <exception cref="IncompatibleExtractException">The DB is not a compatible julie-extract v1 artifact.</exception>
    public static MillerRepositoryIndex Load(string dbPath, Action<TimeSpan>? onBridgeGraphBuilt = null)
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

        var bridgeData = SqliteBridgeReader.Read(dbPath);

        var edges = new List<GraphEdge>(SymbolGraphReader.Read(
            dbPath,
            name => nameToIds.TryGetValue(name, out var ids)
                ? ids
                : (IReadOnlyList<string>)Array.Empty<string>(),
            maxNameResolutionTargets: MaxIdentifierFallbackTargets));
        edges.AddRange(BlazorComponentGraphReader.Read(dbPath, bridgeData.StructuralFacts));

        var symbolDetails = ProjectToSymbolDetails(symbols);
        var bridgeProviders = BridgeProviderSelection.ProvidersForDatabase(dbPath);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var bridgeGraph = BridgeGraphBuilder.Build(
            symbolDetails,
            bridgeData.TypeArguments,
            bridgeData.Literals,
            bridgeData.Annotations,
            bridgeData.DbSetProperties,
            bridgeProviders,
            bridgeData.LiteralSites,
            bridgeData.StructuralFacts);
        stopwatch.Stop();
        onBridgeGraphBuilt?.Invoke(stopwatch.Elapsed);

        return MillerRepositoryIndex.Build(symbols, edges, bridgeGraph);
    }

    /// <summary>
    /// Project the indexed symbols to the lean Core bridge <see cref="CoreSymbolDetail"/>s the bridge legs +
    /// resolver consume. The <c>ParentClassName</c> is resolved by looking the symbol's
    /// <see cref="IndexedSymbol.ParentId"/> up in the same symbol set (a method/property's declaring class name,
    /// needed for Leg 1 <c>[controller]</c> expansion); the namespace is left null (not read into
    /// <see cref="IndexedSymbol"/>) — the resolver tolerates that.
    /// </summary>
    private static IReadOnlyList<CoreSymbolDetail> ProjectToSymbolDetails(IReadOnlyList<IndexedSymbol> symbols)
    {
        // id -> simple name, for the ParentId -> ParentClassName lookup.
        var nameById = new Dictionary<string, string>(symbols.Count, StringComparer.Ordinal);
        foreach (var symbol in symbols)
            nameById[symbol.SymbolId] = symbol.Name;

        var details = new List<CoreSymbolDetail>(symbols.Count);
        foreach (var symbol in symbols)
        {
            string? parentClassName =
                symbol.ParentId is { } pid && nameById.TryGetValue(pid, out var parentName)
                    ? parentName
                    : null;

            details.Add(new CoreSymbolDetail(
                Id: symbol.SymbolId,
                Name: symbol.Name,
                Kind: symbol.Kind,
                FilePath: symbol.FilePath,
                Signature: symbol.Signature ?? string.Empty,
                Namespace: null,
                IsTest: symbol.IsTest,
                ParentClassName: parentClassName));
        }
        return details;
    }
}
