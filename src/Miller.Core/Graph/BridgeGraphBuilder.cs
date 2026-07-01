using Miller.Core.Contracts;
using Miller.Core.Resolver;

namespace Miller.Core.Graph;

/// <summary>
/// Assembles the in-memory <see cref="BridgeGraph"/> from the raw julie-derived contract collections. PURE Miller.Core
/// — it takes already-loaded value records, asks bridge providers to reduce framework-specific evidence into candidate
/// edges, scores every candidate, and builds the graph. No DB, no I/O.
///
/// <para>The default providers are <see cref="DotnetWebBridgeProvider"/> and <see cref="NextJsBridgeProvider"/>.
/// Framework-specific reductions stay behind those providers; the builder remains provider-agnostic so future bridge
/// models can plug in without changing scoring or graph traversal.</para>
///
/// <para><b>Literal evidence seam (Task 9 must match this).</b> <see cref="LiteralRecord"/> surfaces only a byte
/// <c>span</c> + <c>containing_symbol_id</c>; it does not re-expose the <c>literals</c> row's own file/line columns. So
/// providers receive a reader-supplied <c>literal → (file, line)</c> lookup (<paramref name="literalSites"/> on
/// <see cref="Build"/>) rather than extending <see cref="LiteralRecord"/>.</para>
/// </summary>
public static class BridgeGraphBuilder
{
    private static readonly IBridgeProvider[] DefaultProviders = [DotnetWebBridgeProvider.Instance, NextJsBridgeProvider.Instance];

    /// <summary>
    /// Build the cross-language <see cref="BridgeGraph"/> over a workspace's symbols + julie breadcrumbs.
    /// </summary>
    /// <param name="symbols">All resolvable symbols of the workspace (the <see cref="SymbolResolver"/> source + endpoint/field lookups).</param>
    /// <param name="typeArguments">The <c>type_arguments</c> rows (CreateMap grouping input).</param>
    /// <param name="literals">The <c>literals</c> rows (url client calls; sql literals are not paired — see remarks).</param>
    /// <param name="annotations">The <c>symbol_annotations</c> rows (http-verb endpoints, class <c>[Route]</c>).</param>
    /// <param name="dbSetProperties">The DbContext <c>DbSet&lt;T&gt;</c> property breadcrumbs (Leg 3 PRIMARY).</param>
    /// <param name="literalSites">
    /// The reader-supplied <c>literal → (file, line)</c> lookup (the literal-evidence seam — see the type remarks). May
    /// be null; a missing literal falls back to its containing symbol's file:line.
    /// </param>
    /// <exception cref="ArgumentNullException">Any required collection is null.</exception>
    public static BridgeGraph Build(
        IReadOnlyList<SymbolDetail> symbols,
        IReadOnlyList<TypeArgument> typeArguments,
        IReadOnlyList<LiteralRecord> literals,
        IReadOnlyList<SymbolAnnotation> annotations,
        IReadOnlyList<DbSetProperty> dbSetProperties,
        IReadOnlyDictionary<LiteralRecord, LiteralSite>? literalSites = null,
        IReadOnlyList<StructuralFactRecord>? structuralFacts = null) =>
        Build(symbols, typeArguments, literals, annotations, dbSetProperties, DefaultProviders, literalSites, structuralFacts);

    /// <summary>
    /// Build the cross-language <see cref="BridgeGraph"/> with an explicit provider set. Tests and future
    /// configuration use this seam to verify provider-specific capability boundaries without changing the scorer or
    /// graph traversal.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any required collection is null.</exception>
    public static BridgeGraph Build(
        IReadOnlyList<SymbolDetail> symbols,
        IReadOnlyList<TypeArgument> typeArguments,
        IReadOnlyList<LiteralRecord> literals,
        IReadOnlyList<SymbolAnnotation> annotations,
        IReadOnlyList<DbSetProperty> dbSetProperties,
        IReadOnlyList<IBridgeProvider> providers,
        IReadOnlyDictionary<LiteralRecord, LiteralSite>? literalSites = null,
        IReadOnlyList<StructuralFactRecord>? structuralFacts = null)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(typeArguments);
        ArgumentNullException.ThrowIfNull(literals);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(dbSetProperties);
        ArgumentNullException.ThrowIfNull(providers);

        var symbolsById = BuildSymbolIndex(symbols);
        var resolver = new SymbolResolver(symbols);

        // --- run enabled bridge providers; each emits candidates that the shared scorer scores -------------------
        var candidates = new List<CandidateEdge>();
        var activeProviders = new List<string>();
        var skippedProviders = new List<BridgeProviderSkip>();
        var notes = new List<string>();
        var evidenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var observationNodes = new Dictionary<string, BridgeNode>(StringComparer.Ordinal);

        if (providers.Count == 0)
            notes.Add("no bridge providers enabled");

        var context = new BridgeProviderContext(
            symbols,
            typeArguments,
            literals,
            annotations,
            dbSetProperties,
            structuralFacts ?? [],
            literalSites,
            symbolsById,
            resolver);

        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);

            var result = provider.BuildCandidates(context) ??
                         throw new InvalidOperationException($"Bridge provider '{provider.Id}' returned null.");
            AddEvidenceCounts(evidenceCounts, result.EvidenceCounts);
            AddObservationNodes(observationNodes, result.ObservationNodes);

            if (result.Active)
            {
                activeProviders.Add(provider.Id);
                candidates.AddRange(result.Candidates);
            }
            else
            {
                skippedProviders.Add(new BridgeProviderSkip(
                    provider.Id,
                    string.IsNullOrWhiteSpace(result.SkipReason) ? "no bridge evidence" : result.SkipReason));
            }
        }

        // --- score; drop nulls (no-edge per design §5) ----------------------------------------------------------
        var scored = new List<ScoredEdge>();
        foreach (var candidate in candidates)
        {
            var edge = BridgeScorer.Score(candidate);
            if (edge is not null)
                scored.Add(edge);
        }

        // --- build a node for every endpoint of a surviving edge, then the graph --------------------------------
        var nodes = BuildNodes(scored, symbolsById);
        AddObservationNodes(nodes, observationNodes);
        var capabilityReport = new BridgeCapabilityReport(
            activeProviders,
            skippedProviders,
            notes,
            evidenceCounts);
        return BridgeGraph.Build(scored, nodes, capabilityReport);
    }

    // ============================ node construction ============================================================

    /// <summary>
    /// Build a <see cref="BridgeNode"/> for every endpoint of a surviving scored edge, keyed by the same node id
    /// <see cref="BridgeGraph"/> uses (resolved symbol id, or a kind+display synthesis). A symbol-backed node renders
    /// with the resolved symbol's NAME (so a route endpoint shows its action method, e.g. GetById, not the route text
    /// its <see cref="EdgeRef.Display"/> carries) and is enriched with the symbol's file:line; a non-symbol node
    /// (table / route) carries the edge ref's display + file.
    /// </summary>
    private static Dictionary<string, BridgeNode> BuildNodes(
        IReadOnlyList<ScoredEdge> scored, IReadOnlyDictionary<string, SymbolDetail> symbolsById)
    {
        var nodes = new Dictionary<string, BridgeNode>(StringComparer.Ordinal);
        foreach (var edge in scored)
        {
            AddNode(nodes, edge, edge.Edge.SourceRef, edge.Edge.Kind, EndpointSide.Source, symbolsById);
            AddNode(nodes, edge, edge.Edge.TargetRef, edge.Edge.Kind, EndpointSide.Target, symbolsById);
        }
        return nodes;
    }

    private static void AddNode(
        Dictionary<string, BridgeNode> nodes,
        ScoredEdge scored,
        EdgeRef edgeRef,
        BridgeKind edgeKind,
        EndpointSide side,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById)
    {
        var id = BridgeGraph.NodeIdOf(edgeRef, edgeKind, side);
        if (id is null || nodes.ContainsKey(id))
            return;

        var kind = BridgeGraph.NodeKindFor(edgeKind, side);

        // A symbol-backed side: render with the symbol's own NAME and enrich with its declaration file (the edge ref
        // file is the use-site, not the decl). The display MUST be the symbol name, not edgeRef.Display: a Hits edge's
        // endpoint EdgeRef.Display carries the normalized ROUTE (RouteBridge sets it to endpointRoute.Route), so using
        // it would render the controller action as "api/appsettings/{}" instead of "GetById" in the trace output.
        if (!string.IsNullOrEmpty(edgeRef.SymbolId) && symbolsById.TryGetValue(edgeRef.SymbolId, out var symbol))
        {
            nodes[id] = new BridgeNode(id, kind, symbol.Name, symbol.FilePath, Line: 0);
            return;
        }

        nodes[id] = new BridgeNode(id, kind, edgeRef.Display, edgeRef.FilePath, Line: EvidenceLine(edgeRef, scored.Edge.Evidence));
    }

    private static int EvidenceLine(EdgeRef edgeRef, IReadOnlyList<Evidence> evidence)
    {
        if (string.IsNullOrWhiteSpace(edgeRef.FilePath))
            return 0;

        return evidence
            .Where(item => string.Equals(item.FilePath, edgeRef.FilePath, StringComparison.Ordinal))
            .Select(item => item.Line)
            .FirstOrDefault(line => line > 0);
    }

    private static void AddObservationNodes(
        Dictionary<string, BridgeNode> target,
        IReadOnlyDictionary<string, BridgeNode> source)
    {
        foreach (var item in source.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            target.TryAdd(item.Key, item.Value);
    }

    // ============================ shared helpers ===============================================================

    private static Dictionary<string, SymbolDetail> BuildSymbolIndex(IReadOnlyList<SymbolDetail> symbols)
    {
        var byId = new Dictionary<string, SymbolDetail>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
            byId[symbol.Id] = symbol; // last write wins for a duplicated id
        return byId;
    }

    private static void AddEvidenceCounts(
        Dictionary<string, int> target,
        IReadOnlyDictionary<string, int> source)
    {
        foreach (var item in source.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (target.TryGetValue(item.Key, out int existing))
                target[item.Key] = existing + item.Value;
            else
                target[item.Key] = item.Value;
        }
    }

}

/// <summary>
/// A literal's resolved use-site file:line (the literal-evidence seam — see <see cref="BridgeGraphBuilder"/> remarks).
/// The DB reader (plan Task 9) supplies the lookup; Miller.Core stays free of julie's row shape.
/// </summary>
/// <param name="FilePath">The workspace-relative file the literal lives in.</param>
/// <param name="Line">The 1-based line of the literal, or 0 when unknown.</param>
public readonly record struct LiteralSite(string FilePath, int Line);
