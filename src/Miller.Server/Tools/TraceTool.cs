using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Graph;
using Miller.Core.Resolver;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// The <c>trace</c> tool (M4 Task 10): follow a thread of code through the repository. It has three modes over the two
/// in-memory graphs Miller already builds — no DB, no embeddings:
/// <list type="bullet">
/// <item><b>auto</b> — the symbol's immediate neighbourhood (callers + callees) via the dependency
///   <see cref="SymbolGraph.Reach"/> in <see cref="Direction.Both"/>, the same neighbour walk <c>context</c>/<c>impact</c>
///   use.</item>
/// <item><b>path</b> — the shortest dependency path from <c>target</c> to <c>to</c> via
///   <see cref="SymbolGraph.ShortestPath"/> (Task 8); a clean message when the two are not connected within
///   <c>depth</c>.</item>
/// <item><b>bridge</b> — the cross-language structural chain (TS call → endpoint → DTO → entity → table) via
///   <see cref="BridgeGraph.Walk"/> (Task 8) over the Task-9-populated <see cref="BridgeGraph"/>, rendering each scored
///   bridge edge with its confidence band and score.</item>
/// </list>
///
/// <para><b>Honesty flags are load-bearing.</b> A reduced-confidence bridge edge is never rendered as if it were
/// certain: <see cref="ScoredEdge.IsVerbUnknown"/> renders <c>[verb-unknown]</c> (a route matched on path alone, the
/// HTTP verb was not derivable) and <see cref="ScoredEdge.HasAmbiguousName"/> renders <c>[ambiguous]</c> (the name
/// resolved to more than one symbol). The <c>full</c> format additionally lists the firing signals per edge.</para>
///
/// <para>This is the thin MCP/DI/telemetry shell; the pure, DB-free <see cref="Run"/> core (mirroring
/// <see cref="ImpactTool.Run"/>) holds the correctness and is unit-tested directly. It reads the live
/// <see cref="IndexHolder"/> per call (M3 step 10) so a freshness Swap is reflected on the next trace.</para>
/// </summary>
[McpServerToolType]
public sealed class TraceTool
{
    private readonly IndexHolder _holder;
    private readonly SmartTargetResolver _resolver;

    /// <summary>Construct over the live index holder (production / freshness-aware). The <see cref="Run"/> core is
    /// DB-free (it traverses the in-memory graphs), so it takes no WorkspaceContext.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public TraceTool(IndexHolder holder, SmartTargetResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(resolver);
        _holder = holder;
        _resolver = resolver;
    }

    [McpServerTool(Name = "trace")]
    [Description(
        "Follow a thread of code through the repository. mode=auto shows a symbol's callers and callees; mode=path " +
        "shows the shortest dependency path from target to 'to'; mode=bridge follows the cross-language chain " +
        "(TS call to endpoint to DTO to entity to table) with a confidence band on each link. Reduced-confidence " +
        "links are flagged [verb-unknown] / [ambiguous] — never trust an unflagged link more than a flagged one. " +
        "Pass format=full to also see the signals behind each bridge link.")]
    public string Trace(
        [Description("A symbol name/id or a file path (smart-resolved) — where the trace starts.")]
        string target,
        [Description("Trace mode: auto (callers+callees) | path (shortest path to 'to') | bridge (cross-language chain). Default auto.")]
        string mode = "auto",
        [Description("For mode=path: the destination symbol name/id the path must reach. Ignored otherwise.")]
        string? to = null,
        [Description("How many hops to follow. Default 3.")] int depth = 3,
        [Description("Max links/neighbours to return. Default 20.")] int limit = 20,
        [Description("Output format: compact|full. full adds the firing signals per bridge link. Default compact.")]
        string format = "compact")
    {
        var telemetry = TelemetryContext.Current;
        try
        {
            bool full = string.Equals(format, "full", StringComparison.OrdinalIgnoreCase);
            string output = Run(_holder.Current, _resolver,
                target, mode, to, depth, limit, full,
                out int emitted, out int nodesVisited);

            if (telemetry is not null)
            {
                telemetry.SetTarget(target);
                telemetry.ResultCount = emitted;
                // D10 work proxy (bytes_examined ≈ nodes visited): the edges/neighbours the walk produced.
                telemetry.BytesExamined = nodesVisited;
                telemetry.Outcome = emitted == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
            }
            return output;
        }
        catch (Exception ex)
        {
            if (telemetry is not null)
            {
                telemetry.Outcome = TelemetryOutcome.Error;
                telemetry.ErrorKind = ex.GetType().Name;
            }
            return $"trace failed: {ex.Message}";
        }
    }

    private const string ModeAuto = "auto";
    private const string ModePath = "path";
    private const string ModeBridge = "bridge";

    /// <summary>
    /// The pure execution core (no MCP/DI/telemetry; no DB — the graphs are in-memory). Resolves the start
    /// <paramref name="target"/> (smart-resolved; ambiguous / not-found render their own message), dispatches on
    /// <paramref name="mode"/>, and renders compact (one line per link) or <paramref name="fullFormat"/> (also the
    /// firing signals per bridge link). <paramref name="emitted"/> is the number of links/neighbours rendered (the
    /// result-count KPI; a guard / not-found / empty walk yields 0). <paramref name="nodesVisited"/> is the work proxy
    /// (links produced before truncation); guard / not-found paths leave it 0.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> or <paramref name="resolver"/> is null.</exception>
    public static string Run(
        MillerRepositoryIndex index, SmartTargetResolver resolver,
        string target, string mode, string? to, int depth, int limit, bool fullFormat,
        out int emitted, out int nodesVisited)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);
        if (depth < 1) depth = 1;
        if (limit < 1) limit = 1;
        emitted = 0;
        nodesVisited = 0;

        string normalizedMode = (mode ?? ModeAuto).Trim().ToLowerInvariant();

        return normalizedMode switch
        {
            ModeAuto => RunAuto(index, resolver, target, depth, limit, out emitted, out nodesVisited),
            ModePath => RunPath(index, resolver, target, to, depth, limit, out emitted, out nodesVisited),
            ModeBridge => RunBridge(index, resolver, target, depth, limit, fullFormat, out emitted, out nodesVisited),
            _ => $"Unknown mode '{mode}'. Use one of: auto, path, bridge.",
        };
    }

    // ---------- mode: auto (callers + callees neighbourhood) ----------

    private static string RunAuto(
        MillerRepositoryIndex index, SmartTargetResolver resolver, string target,
        int depth, int limit, out int emitted, out int nodesVisited)
    {
        emitted = 0;
        nodesVisited = 0;

        if (!ResolveSymbol(index, resolver, target, out string seedId, out string? note))
            return note!;

        IReadOnlyList<ReachedNode> reached =
            index.Graph.Reach([seedId], depth, limit, Direction.Both);
        nodesVisited = reached.Count;

        if (reached.Count == 0)
            return $"No neighbours — nothing connects to '{target}' within {depth} hop(s).";

        var seed = index.FindBySymbolId(seedId);
        var sb = new StringBuilder();
        sb.Append("# trace ").Append(seed is not null ? seed.Name : target)
          .Append(" (auto, ").Append(reached.Count).Append(" neighbour(s))\n");
        foreach (var node in reached)
        {
            var symbol = index.FindBySymbolId(node.Id);
            if (symbol is null)
                continue; // inconsistent build — drop rather than NRE
            sb.Append(NeighbourLine(symbol, node.Hop)).Append('\n');
            emitted++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    // "Name  kind  file:line  (hop N)" — an auto-mode neighbour line.
    private static string NeighbourLine(IndexedSymbol s, int hop) =>
        $"{s.Name}  {s.Kind}  {s.FilePath}:{s.StartLine}  (hop {hop})";

    // ---------- mode: path (shortest dependency path target -> to) ----------

    private static string RunPath(
        MillerRepositoryIndex index, SmartTargetResolver resolver, string target, string? to,
        int depth, int limit, out int emitted, out int nodesVisited)
    {
        emitted = 0;
        nodesVisited = 0;

        if (string.IsNullOrWhiteSpace(to))
            return "Usage: mode=path requires 'to' (the destination symbol to find a path to).";

        if (!ResolveSymbol(index, resolver, target, out string fromId, out string? fromNote))
            return fromNote!;
        if (!ResolveSymbol(index, resolver, to, out string toId, out string? toNote))
            return toNote!;

        IReadOnlyList<string>? path = index.Graph.ShortestPath(fromId, toId, depth);
        if (path is null)
            return $"No path from '{target}' to '{to}' within {depth} hop(s).";

        // The path is from..to inclusive (ShortestPath includes both endpoints). Hops = path.Count - 1.
        nodesVisited = path.Count;
        var sb = new StringBuilder();
        sb.Append("# trace path ").Append(target).Append(" -> ").Append(to)
          .Append(" (").Append(path.Count - 1).Append(" hop(s))\n");

        int shown = 0;
        for (int i = 0; i < path.Count && shown < limit; i++)
        {
            var symbol = index.FindBySymbolId(path[i]);
            string label = symbol is not null
                ? $"{symbol.Name}  {symbol.Kind}  {symbol.FilePath}:{symbol.StartLine}"
                : path[i];
            sb.Append(i == 0 ? "  " : "  -> ").Append(label).Append('\n');
            shown++;
            emitted++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---------- mode: bridge (cross-language scored chain) ----------

    private static string RunBridge(
        MillerRepositoryIndex index, SmartTargetResolver resolver, string target,
        int depth, int limit, bool fullFormat, out int emitted, out int nodesVisited)
    {
        emitted = 0;
        nodesVisited = 0;

        // The bridge graph keys symbol-backed nodes by their symbol id, so resolve the target to a symbol id first.
        // (A pure route/table node has no code symbol and is not a smart-resolvable start; symbols are the entry.)
        if (!ResolveSymbol(index, resolver, target, out string startId, out string? note))
            return note!;

        // A symbol with no incident bridge edges is not on any cross-language thread — whether it is absent from the
        // bridge node lookup entirely or present but edge-less, the honest answer is the same. Incident subsumes both.
        if (index.BridgeGraph.Incident(startId).Count == 0)
            return $"'{target}' is not on a cross-language bridge. trace bridge follows DTO/entity/table/route links; " +
                   "this symbol has none.";

        IReadOnlyList<ScoredEdge> edges = index.BridgeGraph.Walk(startId, depth);
        nodesVisited = edges.Count;

        // The start has direct incident edges (checked above), so an empty Walk means depth could not reach them.
        if (edges.Count == 0)
            return $"No bridge links from '{target}' within {depth} hop(s).";

        var startNode = index.BridgeGraph.Node(startId);
        var sb = new StringBuilder();
        sb.Append("# trace bridge ").Append(startNode is not null ? startNode.Display : target)
          .Append(" (").Append(Math.Min(edges.Count, limit)).Append(" link(s))\n");

        int shown = 0;
        foreach (var edge in edges)
        {
            if (shown >= limit)
                break;
            sb.Append(BridgeLine(index.BridgeGraph, edge)).Append('\n');
            if (fullFormat)
                AppendSignals(sb, edge);
            shown++;
            emitted++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    // "<source> --<verb>--> <target>  [flags]  <score> (Band)" — one scored bridge link.
    // The verb token is the human bridge-kind label; flags are the load-bearing honesty markers.
    internal static string BridgeLine(BridgeGraph graph, ScoredEdge edge)
    {
        string source = EndpointDisplay(graph, edge.Edge.SourceRef, edge.Edge.Kind, EndpointSide.Source);
        string targetDisplay = EndpointDisplay(graph, edge.Edge.TargetRef, edge.Edge.Kind, EndpointSide.Target);

        var sb = new StringBuilder();
        sb.Append(source).Append("  --").Append(KindLabel(edge.Edge.Kind)).Append("-->  ").Append(targetDisplay);

        string flags = Flags(edge);
        if (flags.Length > 0)
            sb.Append("  ").Append(flags);

        sb.Append("  ").Append(FormatScore(edge.Score)).Append(" (").Append(edge.Band).Append(')');
        return sb.ToString();
    }

    // The honesty flags — NEVER omit a fired flag, or a reduced-confidence link reads as certain.
    private static string Flags(ScoredEdge edge)
    {
        var parts = new List<string>(2);
        if (edge.HasAmbiguousName)
            parts.Add("[ambiguous]");
        if (edge.IsVerbUnknown)
            parts.Add("[verb-unknown]");
        return string.Join(' ', parts);
    }

    // The display label for one endpoint: prefer the resolved node's Display (leaf type / table / route), else the
    // EdgeRef's own Display (the raw ref text the leg recorded).
    private static string EndpointDisplay(BridgeGraph graph, EdgeRef edgeRef, BridgeKind kind, EndpointSide side)
    {
        string? nodeId = BridgeGraph.NodeIdOf(edgeRef, kind, side);
        if (nodeId is not null)
        {
            var node = graph.Node(nodeId);
            if (node is not null && !string.IsNullOrWhiteSpace(node.Display))
                return node.Display;
        }
        return string.IsNullOrWhiteSpace(edgeRef.Display) ? "?" : edgeRef.Display;
    }

    // The human label for a bridge kind, matching the design's vocabulary (name/CreateMap/DbSet/route/responds/consumes).
    internal static string KindLabel(BridgeKind kind) => kind switch
    {
        BridgeKind.StoredIn => "DbSet",
        BridgeKind.MapsTo => "CreateMap",
        BridgeKind.Hits => "route",
        BridgeKind.Responds => "responds",
        BridgeKind.Consumes => "consumes",
        BridgeKind.NameMatch => "name",
        _ => kind.ToString().ToLowerInvariant(),
    };

    // The firing signals behind a bridge link (full format only): "    signals: Rule=Value, ...".
    private static void AppendSignals(StringBuilder sb, ScoredEdge edge)
    {
        var signals = edge.Edge.Signals;
        if (signals is null || signals.Count == 0)
        {
            sb.Append("    signals: (none)\n");
            return;
        }
        sb.Append("    signals: ");
        for (int i = 0; i < signals.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(SignalLabel(signals[i]));
        }
        sb.Append('\n');
    }

    // A signal rendered with its typed payload — enough to see WHY the edge scored as it did. The Signal hierarchy is
    // a closed set of sealed subtypes (Signal.cs); switch on the concrete type to surface the load-bearing payload
    // (a structural breadcrumb's present/absent, a field-set's count+Jaccard, a name match's tier, a resolution status).
    private static string SignalLabel(Signal signal) => signal switch
    {
        StructuralSignal s => $"{s.Rule}={(s.Present ? "present" : "absent")}",
        FieldSetSignal s =>
            $"FieldSetJaccard(count={s.FieldCount}, jaccard={s.Jaccard.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)})",
        NameSignal s => $"NameMatch={s.Tier}",
        NameResolutionSignal s => $"NameResolution({s.Endpoint}={s.Status}, matches={s.MatchCount})",
        _ => signal.Rule.ToString(),
    };

    // A stable, culture-invariant 2-decimal score so the rendered text (and its tests) are deterministic.
    private static string FormatScore(double score) =>
        score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    // ---------- shared target resolution ----------

    /// <summary>
    /// Smart-resolve <paramref name="target"/> to a single symbol id. Returns true with <paramref name="symbolId"/>
    /// set on a unique symbol resolution; false with a rendered <paramref name="note"/> on a file / ambiguous /
    /// not-found resolution (trace is a symbol-anchored walk — a file is not a graph start).
    /// </summary>
    private static bool ResolveSymbol(
        MillerRepositoryIndex index, SmartTargetResolver resolver, string target,
        out string symbolId, out string? note)
    {
        symbolId = string.Empty;
        note = null;

        if (string.IsNullOrWhiteSpace(target))
        {
            note = "trace: a target symbol is required.";
            return false;
        }

        switch (resolver.Resolve(target))
        {
            case TargetResolution.Symbol sym:
                symbolId = sym.Value.SymbolId;
                return true;

            case TargetResolution.File:
                note = $"'{target}' is a file. trace starts from a single symbol — pass a symbol name or id.";
                return false;

            case TargetResolution.Candidates cands:
                note = RenderCandidatesNote(cands.Matches);
                return false;

            case TargetResolution.NotFound nf:
                note = $"'{nf.Target}' not found. Try search to locate it.";
                return false;

            default:
                note = "trace: unrecognized target resolution.";
                return false;
        }
    }

    private static string RenderCandidatesNote(IReadOnlyList<IndexedSymbol> matches)
    {
        var sb = new StringBuilder();
        sb.Append("Multiple candidates — pass a more specific target:\n");
        foreach (var s in matches)
            sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
              .Append(s.FilePath).Append(':').Append(s.StartLine).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }
}
