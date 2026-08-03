using Miller.Core.Graph;
using Miller.Core.Resolver;
using Miller.Core.Search;

namespace Miller.Indexing;

/// <summary>
/// The M1 in-process deliverable: a thin facade tying the SQLite read layer to Miller.Core's ranked index.
/// Builds <see cref="MillerSearchIndex"/> from the read symbols' scoring projections and retains the
/// <see cref="IndexedSymbol"/>s for hydration. This is where the opaque-string-id ⇄ int-DocId bridge lives:
/// a <see cref="SearchHit"/>'s <see cref="SearchableDocument.DocId"/> resolves back through
/// <see cref="Resolve"/> to the julie symbol id (the M4 join key) and its containment parent.
/// </summary>
public sealed class MillerRepositoryIndex : ISymbolLookupIndex
{
    private readonly MillerSearchIndex _index;
    private readonly IndexedSymbol[] _byDocId; // DocId == array index by construction (see Build)

    // M2 lookup maps for the read tools (search/inspect) + smart-string resolution. Built once at Build()
    // time so name/id/path/parent lookups are O(1)/O(k) rather than a linear scan of ~565k rows per call.
    private readonly Dictionary<string, IndexedSymbol> _bySymbolId;
    private readonly Dictionary<string, List<IndexedSymbol>> _byName;
    private readonly Dictionary<string, List<IndexedSymbol>> _byFilePath;
    private readonly Dictionary<string, List<IndexedSymbol>> _byParentId;

    // Cross-language file-detection support for SmartTargetResolver (decision-4): the "is this a file?"
    // decision is DERIVED from the indexed data, never a hardcoded extension whitelist. _byFileName maps a
    // bare basename to the distinct full indexed paths carrying it (so `UserService.cs` can resolve to
    // `auth/UserService.cs` when unique). _knownExtensions is the distinct set of extensions julie actually
    // emitted for THIS repo — precisely the supported languages, self-updating as julie adds more.
    private readonly Dictionary<string, List<string>> _byFileName;

    // The M5 dependency graph (D9): built as ONE immutable unit with the index so it is published atomically by
    // the holder's reference swap (symbol ids churn on edit; a graph keyed on stale ids must never outlive its
    // index). Always non-null — Build(symbols) installs an edge-less graph (back-compat for search/inspect).
    private readonly SymbolGraph _graph;

    // The M4 cross-language bridge graph (Task 8/9): built as part of the same immutable unit and published
    // atomically alongside _graph (same churn argument — a bridge graph keyed on stale ids must never outlive its
    // index). Always non-null — the search/inspect/edit Build paths install the empty bridge graph.
    private readonly BridgeGraph _bridgeGraph;

    // The empty bridge graph the non-M4 Build overloads install (no scored edges, no nodes). Shared + immutable.
    private static readonly BridgeGraph EmptyBridgeGraph =
        BridgeGraph.Build(Array.Empty<ScoredEdge>(), new Dictionary<string, BridgeNode>(StringComparer.Ordinal));

    private MillerRepositoryIndex(
        MillerSearchIndex index,
        IndexedSymbol[] byDocId,
        Dictionary<string, IndexedSymbol> bySymbolId,
        Dictionary<string, List<IndexedSymbol>> byName,
        Dictionary<string, List<IndexedSymbol>> byFilePath,
        Dictionary<string, List<IndexedSymbol>> byParentId,
        Dictionary<string, List<string>> byFileName,
        IReadOnlySet<string> knownExtensions,
        SymbolGraph graph,
        BridgeGraph bridgeGraph,
        string indexLevel)
    {
        IndexLevel = indexLevel;
        _index = index;
        _byDocId = byDocId;
        _bySymbolId = bySymbolId;
        _byName = byName;
        _byFilePath = byFilePath;
        _byParentId = byParentId;
        _byFileName = byFileName;
        KnownExtensions = knownExtensions;
        _graph = graph;
        _bridgeGraph = bridgeGraph;
    }

    /// <summary>
    /// The M5 dependency graph travelling with this index (D9). Forward + reverse adjacency over the indexed
    /// symbols; built once at <see cref="Build(IReadOnlyList{IndexedSymbol},IReadOnlyList{GraphEdge})"/> time.
    /// An index built via <see cref="Build(IReadOnlyList{IndexedSymbol})"/> carries every symbol as a node but
    /// no edges (an empty graph). Prefer the hydrating <see cref="Dependents"/>/<see cref="Dependencies"/>
    /// pass-throughs over the raw id graph when you need full <see cref="IndexedSymbol"/>s.
    /// </summary>
    public SymbolGraph Graph => _graph;

    /// <summary>
    /// The M4 cross-language bridge graph travelling with this index (plan Task 8/9; design §3/§4). The undirected
    /// scored-edge graph over TS/DTO/entity/table/endpoint nodes, built once as part of the same immutable unit so
    /// it is published atomically by the holder's reference swap. An index built via a non-M4 <c>Build</c> overload
    /// carries the EMPTY bridge graph (no edges, no nodes); only
    /// <see cref="Build(IReadOnlyList{IndexedSymbol},IReadOnlyList{GraphEdge},BridgeGraph)"/> installs a populated
    /// one (the production loader, Task 9).
    /// </summary>
    public BridgeGraph BridgeGraph => _bridgeGraph;

    /// <summary>Total number of indexed symbols.</summary>
    public int DocumentCount => _byDocId.Length;

    /// <summary>
    /// The artifact's recorded <c>index_level</c> metadata value (<c>"symbols"</c> or <c>"full"</c>; absent
    /// reads as full). Travels with the index so reference-dependent tools can tell an EMPTY reference layer
    /// (symbols-level artifact, upgrade converging) from a genuinely reference-free result. Default-built
    /// indexes (tests, non-loader paths) read as full.
    /// </summary>
    public string IndexLevel { get; }

    /// <summary>
    /// The distinct file extensions (lowercased, leading dot, e.g. <c>.cs</c>/<c>.rs</c>/<c>.vue</c>) present
    /// among the indexed file paths. DERIVED from julie's output — the cross-language replacement for a
    /// hardcoded code-extension whitelist (M2 §3 decision-4). Used by <see cref="SmartTargetResolver"/> to
    /// classify a non-indexed but plausibly-file target.
    /// </summary>
    public IReadOnlySet<string> KnownExtensions { get; }

    /// <summary>
    /// Build the repository index from symbols produced by <see cref="SqliteSymbolReader.Read"/>, with an EMPTY
    /// dependency graph. Back-compat for the search/inspect/edit paths that do not need edges — every symbol is a
    /// graph node, but there are no dependency edges. Delegates to
    /// <see cref="Build(IReadOnlyList{IndexedSymbol},IReadOnlyList{GraphEdge})"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// DocIds are not the contiguous 0..n-1 ordinals this facade requires (a reader regression).
    /// </exception>
    public static MillerRepositoryIndex Build(IReadOnlyList<IndexedSymbol> symbols) =>
        Build(symbols, Array.Empty<GraphEdge>(), EmptyBridgeGraph);

    /// <summary>
    /// Build the repository index AND its dependency graph (D9), with the EMPTY cross-language bridge graph.
    /// Back-compat for the M1–M3/M5 build sites (search/inspect/edit/impact) that do not need the M4 bridge.
    /// Delegates to <see cref="Build(IReadOnlyList{IndexedSymbol},IReadOnlyList{GraphEdge},BridgeGraph)"/>.
    /// </summary>
    /// <param name="symbols">The indexed symbols (contiguous 0-based DocIds).</param>
    /// <param name="edges">The resolved dependency edges (<see cref="SymbolGraphReader.Read"/>'s output).</param>
    /// <exception cref="ArgumentNullException"><paramref name="symbols"/> or <paramref name="edges"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// DocIds are not the contiguous 0..n-1 ordinals this facade requires (a reader regression).
    /// </exception>
    public static MillerRepositoryIndex Build(
        IReadOnlyList<IndexedSymbol> symbols, IReadOnlyList<GraphEdge> edges) =>
        Build(symbols, edges, EmptyBridgeGraph);

    /// <summary>
    /// Build the repository index, its dependency graph (D9), AND the M4 cross-language bridge graph (Task 8/9) as
    /// ONE immutable unit. The reader assigns contiguous 0-based DocIds in deterministic order, so the hydration
    /// array is indexed directly by DocId for O(1) <see cref="Resolve"/>; this contract is validated, not assumed.
    /// Each symbol becomes a dependency-graph node carrying its <see cref="IndexedSymbol.IsTest"/> flag (so
    /// <c>impact</c> can partition likely tests without a second lookup); the graph applies its own edge hygiene
    /// (unknown-endpoint / self-loop drop, per-direction dedup). The supplied <paramref name="bridgeGraph"/> (built
    /// by <see cref="Miller.Core.Graph.BridgeGraphBuilder"/> in the loader, Task 9) travels with the index and is
    /// published atomically by the holder's reference swap.
    /// </summary>
    /// <param name="symbols">The indexed symbols (contiguous 0-based DocIds).</param>
    /// <param name="edges">The resolved dependency edges (<see cref="SymbolGraphReader.Read"/>'s output).</param>
    /// <param name="bridgeGraph">The M4 cross-language bridge graph to publish with this index.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// DocIds are not the contiguous 0..n-1 ordinals this facade requires (a reader regression).
    /// </exception>
    public static MillerRepositoryIndex Build(
        IReadOnlyList<IndexedSymbol> symbols, IReadOnlyList<GraphEdge> edges, BridgeGraph bridgeGraph,
        string indexLevel = "full")
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexLevel);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(bridgeGraph);

        var byDocId = new IndexedSymbol[symbols.Count];
        var documents = new SearchableDocument[symbols.Count];
        // Name/file-path/parent lookups are intrinsically multi-valued; symbol-id is unique (PK).
        var bySymbolId = new Dictionary<string, IndexedSymbol>(symbols.Count, StringComparer.Ordinal);
        var byName = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);
        var byFilePath = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);
        var byParentId = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);
        // Graph nodes: one per indexed symbol, carrying its id + cross-language IsTest flag (D9). The graph
        // bounds its edges to these nodes (unknown-endpoint drop), so edges to non-indexed ids fall out.
        var nodes = new GraphNode[symbols.Count];

        for (int i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            // Pin the read-layer contract: DocId is the 0-based row ordinal. A drift here would silently
            // misroute Resolve(), so reject it loudly rather than corrupt the id bridge.
            if (symbol.DocId != i)
                throw new ArgumentException(
                    $"Symbol at position {i} has DocId {symbol.DocId}; MillerRepositoryIndex requires the " +
                    "contiguous 0-based ordinals SqliteSymbolReader assigns.", nameof(symbols));

            byDocId[i] = symbol;
            documents[i] = symbol.ToSearchableDocument();
            nodes[i] = new GraphNode(symbol.SymbolId, symbol.IsTest, symbol.Visibility);

            // symbol id is julie's PK — unique. A duplicate would mean a corrupt extract; last-wins is
            // harmless here (the reader already enforces uniqueness via the PK), so just index it.
            bySymbolId[symbol.SymbolId] = symbol;
            Add(byName, symbol.Name, symbol);
            Add(byFilePath, symbol.FilePath, symbol);
            if (symbol.ParentId is { } pid)
                Add(byParentId, pid, symbol);
        }

        // Derive the cross-language file-detection structures from the indexed paths (decision-4): basename
        // → distinct full paths, and the distinct extension set. Built from the path keys, not per-symbol, so
        // each path contributes once regardless of how many symbols it holds.
        var byFileName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var knownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in byFilePath.Keys)
        {
            string fileName = LastPathSegment(path);
            if (!byFileName.TryGetValue(fileName, out var paths))
                byFileName[fileName] = paths = new List<string>(1);
            paths.Add(path);

            string ext = Path.GetExtension(fileName);
            if (ext.Length > 1) // non-empty beyond the dot
                knownExtensions.Add(ext);
        }

        return new MillerRepositoryIndex(
            MillerSearchIndex.Build(documents), byDocId, bySymbolId, byName, byFilePath, byParentId,
            byFileName, knownExtensions, SymbolGraph.Build(nodes, edges), bridgeGraph, indexLevel);

        static void Add(Dictionary<string, List<IndexedSymbol>> map, string key, IndexedSymbol value)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<IndexedSymbol>(1);
            list.Add(value);
        }
    }

    // julie stores relative-unix paths; split on '/' (and tolerate '\') for the basename.
    private static string LastPathSegment(string path)
    {
        int slash = path.LastIndexOfAny(SeparatorChars);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static readonly char[] SeparatorChars = { '/', '\\' };

    private static readonly IReadOnlyList<IndexedSymbol> Empty = Array.Empty<IndexedSymbol>();

    /// <summary>
    /// All symbols sharing the exact <paramref name="name"/> (ordinal), in DocId order. Empty if none.
    /// This is the name-lookup the smart-string resolver and inspect use; it is exact, not tokenized
    /// (the tokenized/scored path is <see cref="Search"/>).
    /// </summary>
    public IReadOnlyList<IndexedSymbol> FindByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out var list) ? list : Empty;
    }

    /// <summary>The symbol with the given opaque julie id, or null if not indexed.</summary>
    public IndexedSymbol? FindBySymbolId(string symbolId)
    {
        ArgumentNullException.ThrowIfNull(symbolId);
        return _bySymbolId.TryGetValue(symbolId, out var symbol) ? symbol : null;
    }

    /// <summary>
    /// All symbols whose <see cref="IndexedSymbol.FilePath"/> equals <paramref name="filePath"/> exactly
    /// (ordinal), in DocId order (i.e. by start_line then id). Empty if the path is not indexed.
    /// </summary>
    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return _byFilePath.TryGetValue(filePath, out var list) ? list : Empty;
    }

    public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
        FilePathSymbolLookup.FindByFilePathFragment(_byFilePath, query, limit);

    public IReadOnlyList<string> FindFilePathsByFragment(string query, int limit) =>
        FilePathSymbolLookup.FindFilePathsByFragment(_byFilePath, query, limit);

    /// <summary>True if <paramref name="path"/> is exactly an indexed file path (ordinal). O(1).</summary>
    public bool IsIndexedFilePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _byFilePath.ContainsKey(path);
    }

    /// <summary>
    /// Resolve a file target to its canonical indexed path (decision-4 file detection). Returns the path
    /// unchanged if it is already an exact indexed path; otherwise, if the target is a bare basename that
    /// uniquely identifies one indexed file (e.g. <c>UserService.cs</c> → <c>auth/UserService.cs</c>), returns
    /// that full path; returns null if the target matches no indexed file or matches an ambiguous basename
    /// (the caller decides how to render "not found / ambiguous").
    /// </summary>
    public string? ResolveIndexedFilePath(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_byFilePath.ContainsKey(target))
            return target;
        if (_byFileName.TryGetValue(target, out var paths) && paths.Count == 1)
            return paths[0];
        return null;
    }

    /// <summary>
    /// The direct children of the symbol with id <paramref name="parentId"/> (those whose
    /// <see cref="IndexedSymbol.ParentId"/> equals it), in DocId order. Empty if none.
    /// </summary>
    public IReadOnlyList<IndexedSymbol> FindChildren(string parentId)
    {
        ArgumentNullException.ThrowIfNull(parentId);
        return _byParentId.TryGetValue(parentId, out var list) ? list : Empty;
    }

    /// <summary>
    /// The symbols that directly depend on <paramref name="symbolId"/> (its one-hop callers — the
    /// <c>impact</c> blast-radius adjacency), hydrated to full <see cref="IndexedSymbol"/>s in the graph's id
    /// order. A hydrating pass-through over <see cref="Graph"/>'s <see cref="SymbolGraph.Dependents"/>: each
    /// neighbour id is resolved via the symbol-id map; an id absent from the index is skipped (defensive — the
    /// graph already bounds edges to indexed nodes, so this only fires on an inconsistent build). Empty when the
    /// id is unknown or nothing depends on it.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="symbolId"/> is null.</exception>
    public IReadOnlyList<IndexedSymbol> Dependents(string symbolId)
    {
        ArgumentNullException.ThrowIfNull(symbolId);
        return Hydrate(_graph.Dependents(symbolId));
    }

    /// <summary>
    /// The symbols <paramref name="symbolId"/> directly depends on (its one-hop callees / used types), hydrated
    /// to full <see cref="IndexedSymbol"/>s in the graph's id order. A hydrating pass-through over
    /// <see cref="Graph"/>'s <see cref="SymbolGraph.Dependencies"/> with the same skip-unknown discipline as
    /// <see cref="Dependents"/>. Empty when the id is unknown or it depends on nothing indexed.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="symbolId"/> is null.</exception>
    public IReadOnlyList<IndexedSymbol> Dependencies(string symbolId)
    {
        ArgumentNullException.ThrowIfNull(symbolId);
        return Hydrate(_graph.Dependencies(symbolId));
    }

    // Resolve a list of graph neighbour ids to full IndexedSymbols via FindBySymbolId, preserving the graph's
    // id order and skipping any id not present in the index (the graph already bounds edges to indexed nodes, so
    // a missing id signals an inconsistent build rather than an expected case — drop it rather than NRE).
    private IReadOnlyList<IndexedSymbol> Hydrate(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
            return Empty;

        var hydrated = new List<IndexedSymbol>(ids.Count);
        foreach (var id in ids)
        {
            var symbol = FindBySymbolId(id);
            if (symbol is not null)
                hydrated.Add(symbol);
        }
        return hydrated;
    }

    /// <summary>Search the index (delegates to <see cref="MillerSearchIndex.Search"/>; see its semantics).</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
        _index.Search(query, limit, mode);

    /// <summary>
    /// Hydrate a search hit's <see cref="SearchableDocument.DocId"/> back to its full <see cref="IndexedSymbol"/>
    /// (carrying the opaque julie symbol id + parent id). O(1).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="docId"/> is not a valid DocId.</exception>
    public IndexedSymbol Resolve(int docId)
    {
        if ((uint)docId >= (uint)_byDocId.Length)
            throw new ArgumentOutOfRangeException(nameof(docId), docId,
                $"DocId must be in [0, {_byDocId.Length}).");
        return _byDocId[docId];
    }
}
