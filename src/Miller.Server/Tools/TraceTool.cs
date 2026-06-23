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
using Miller.Server.Workspaces;
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
/// <item><b>bridge</b> — the provider-scoped structural chain via <see cref="BridgeGraph.Walk"/> over the loaded
///   <see cref="BridgeGraph"/>, rendering each scored bridge edge with its confidence band and score. The current
///   provider is dotnet-web.</item>
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
    private readonly IWorkspaceIndexProvider _workspaceProvider;

    /// <summary>Construct over the live index holder (production / freshness-aware). The <see cref="Run"/> core is
    /// DB-free (it traverses the in-memory graphs), so it takes no WorkspaceContext.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public TraceTool(IWorkspaceIndexProvider workspaceProvider)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        _workspaceProvider = workspaceProvider;
    }

    [McpServerTool(Name = "trace")]
    [Description(
        "Follow a thread of code through the repository. mode=auto shows a symbol's callers and callees; mode=path " +
        "shows the shortest dependency path from target to 'to'; mode=bridge follows provider-scoped cross-language " +
        "chains (currently dotnet-web: client call to endpoint to DTO/entity/table) with a confidence band. Reduced-confidence " +
        "links are flagged [verb-unknown] / [ambiguous] — never trust an unflagged link more than a flagged one. " +
        "Use before manual caller/callee file hopping. Pass format=json for structured output, or format=full to also " +
        "see the signals behind each bridge link in compact output.")]
    public string Trace(
        [Description("A symbol name/id where the trace starts. In bridge mode, route/table nodes and single-symbol files are also accepted.")]
        string target,
        [Description("Disambiguate an ambiguous target symbol name to a file. Optional.")]
        string? scope = null,
        [Description("Trace mode: auto (callers+callees) | path (shortest path to 'to') | bridge (provider-scoped cross-language chain). Default auto.")]
        string mode = "auto",
        [Description("For mode=path: the destination symbol name/id the path must reach. Ignored otherwise.")]
        string? to = null,
        [Description("How many hops to follow. Default 3.")] int depth = 3,
        [Description("Max links/neighbours to return. Default 20.")] int limit = 20,
        [Description("Output format: compact|json|full. full adds the firing signals per bridge link in compact output. Default compact.")]
        string format = "compact",
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")] string? workspace_id = null,
        [Description("Refresh a registered workspace before reading. Defaults true when workspace_id is supplied.")]
        bool? ensure_fresh = null)
    {
        var telemetry = TelemetryContext.Current;
        try
        {
            bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            bool full = string.Equals(format, "full", StringComparison.OrdinalIgnoreCase);
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            WorkspaceReadContext context = _workspaceProvider.Resolve(workspace_id, ensureFresh);
            string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            string output = Run(context.Index, context.Resolver,
                target, scope, mode, to, depth, limit, full,
                json,
                out int emitted, out int nodesVisited);
            output = ReadToolWorkspaceRouting.PrefixCompact(output, compactBanner);

            if (telemetry is not null)
            {
                ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
                string normalizedMode = NormalizeMode(mode);
                telemetry.Op = normalizedMode;
                telemetry.SetTarget(target);
                telemetry.ResultCount = emitted;
                // D10 work proxy (bytes_examined ≈ nodes visited): the edges/neighbours the walk produced.
                telemetry.BytesExamined = nodesVisited;
                telemetry.Outcome = emitted == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
                telemetry.SetMetadata("format", full ? "full" : json ? "json" : "compact");
                telemetry.SetMetadata("has_scope", !string.IsNullOrWhiteSpace(scope));
                telemetry.SetMetadata("has_to", !string.IsNullOrWhiteSpace(to));
                telemetry.SetMetadata("depth_bucket", DepthBucket(depth));
                telemetry.SetMetadata("limit_bucket", LimitBucket(limit));
                if (emitted == 0)
                    telemetry.SetEmptyReason(TraceEmptyReason(normalizedMode, output));
            }
            return output;
        }
        catch (Exception ex)
        {
            if (telemetry is not null)
            {
                telemetry.Outcome = TelemetryOutcome.Error;
                telemetry.SetError(ex);
            }
            return $"trace failed: {ex.Message}";
        }
    }

    private const string ModeAuto = "auto";
    private const string ModePath = "path";
    private const string ModeBridge = "bridge";

    private static string NormalizeMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? ModeAuto : mode.Trim().ToLowerInvariant();

    private static string TraceEmptyReason(string mode, string output) => mode switch
    {
        ModePath => "no_path",
        ModeBridge => "no_bridge_path",
        _ when output.StartsWith("No neighbours", StringComparison.Ordinal) => "no_neighbours",
        _ when output.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               output.StartsWith("Multiple candidates", StringComparison.Ordinal) => "unresolved_target",
        _ => "no_trace_edges",
    };

    private static string LimitBucket(int limit) => limit switch
    {
        <= 0 => "0",
        <= 5 => "1-5",
        <= 10 => "6-10",
        <= 25 => "11-25",
        <= 50 => "26-50",
        _ => "51+",
    };

    private static string DepthBucket(int depth) => depth switch
    {
        <= 0 => "0",
        1 => "1",
        2 => "2",
        <= 5 => "3-5",
        _ => "6+",
    };

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
        string target, string? scope, string mode, string? to, int depth, int limit, bool fullFormat,
        out int emitted, out int nodesVisited) =>
        Run(index, resolver, target, scope, mode, to, depth, limit, fullFormat, json: false,
            out emitted, out nodesVisited);

    public static string Run(
        MillerRepositoryIndex index, SmartTargetResolver resolver,
        string target, string? scope, string mode, string? to, int depth, int limit, bool fullFormat, bool json,
        out int emitted, out int nodesVisited)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);

        string normalizedMode = (mode ?? ModeAuto).Trim().ToLowerInvariant();

        return normalizedMode switch
        {
            ModeAuto => RunGraph(index, index.Graph, resolver, target, scope, normalizedMode, to, depth, limit, fullFormat,
                json, out emitted, out nodesVisited),
            ModePath => RunGraph(index, index.Graph, resolver, target, scope, normalizedMode, to, depth, limit, fullFormat,
                json, out emitted, out nodesVisited),
            ModeBridge => RunBridge(index, resolver, target, scope, depth, limit, fullFormat, json, out emitted, out nodesVisited),
            _ =>
                UnknownMode(mode, json, target, to, depth, limit, out emitted, out nodesVisited),
        };
    }

    public static string RunGraph(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string target, string? scope, string mode, string? to, int depth, int limit, bool fullFormat,
        out int emitted, out int nodesVisited) =>
        RunGraph(index, graph, resolver, target, scope, mode, to, depth, limit, fullFormat, json: false,
            out emitted, out nodesVisited);

    public static string RunGraph(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string target, string? scope, string mode, string? to, int depth, int limit, bool fullFormat, bool json,
        out int emitted, out int nodesVisited)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(resolver);
        if (depth < 1) depth = 1;
        if (limit < 1) limit = 1;
        emitted = 0;
        nodesVisited = 0;

        string normalizedMode = (mode ?? ModeAuto).Trim().ToLowerInvariant();

        return normalizedMode switch
        {
            ModeAuto => RunAuto(index, graph, resolver, target, scope, depth, limit, json, out emitted, out nodesVisited),
            ModePath => RunPath(index, graph, resolver, target, scope, to, depth, limit, json, out emitted, out nodesVisited),
            ModeBridge => json
                ? RenderTraceJson(ModeBridge, target, to, depth, limit, emitted: 0, nodesVisited: 0,
                    note: "trace mode=bridge requires the full repository bridge graph.",
                    diagnosticCode: "bridge_requires_full_index")
                : "trace mode=bridge requires the full repository bridge graph.",
            _ => UnknownMode(mode, json, target, to, depth, limit, out emitted, out nodesVisited),
        };
    }

    public static string Run(
        MillerRepositoryIndex index, SmartTargetResolver resolver,
        string target, string mode, string? to, int depth, int limit, bool fullFormat,
        out int emitted, out int nodesVisited) =>
        Run(index, resolver, target, scope: null, mode, to, depth, limit, fullFormat, out emitted, out nodesVisited);

    public static string Run(
        MillerRepositoryIndex index, SmartTargetResolver resolver,
        string target, string mode, string? to, int depth, int limit, bool fullFormat, bool json,
        out int emitted, out int nodesVisited) =>
        Run(index, resolver, target, scope: null, mode, to, depth, limit, fullFormat, json,
            out emitted, out nodesVisited);

    // ---------- mode: auto (callers + callees neighbourhood) ----------

    private static string RunAuto(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver, string target, string? scope,
        int depth, int limit, bool json, out int emitted, out int nodesVisited)
    {
        emitted = 0;
        nodesVisited = 0;

        if (!ResolveSymbol(index, resolver, target, scope, out string seedId, out string? note))
            return json
                ? RenderTraceJson(ModeAuto, target, to: null, depth, limit, emitted, nodesVisited, note, DiagnosticCode(note!))
                : note!;

        IReadOnlyList<ReachedNode> reached =
            graph.Reach([seedId], depth, limit, Direction.Both);
        nodesVisited = reached.Count;

        if (reached.Count == 0)
        {
            string message = RenderNoNeighboursMessage(index, target, depth, seedId);
            return json
                ? RenderAutoJson(index, target, to: null, depth, limit, emitted, nodesVisited, seedId, reached, message, "no_neighbours")
                : message;
        }

        if (json)
        {
            emitted = reached.Count;
            return RenderAutoJson(index, target, to: null, depth, limit, emitted: reached.Count, nodesVisited, seedId, reached, note: null, diagnosticCode: null);
        }

        var seed = index.FindBySymbolId(seedId);
        var sb = new StringBuilder();
        sb.Append("# trace ").Append(seed is not null ? seed.Name : target)
          .Append(" (auto, ").Append(reached.Count).Append(" neighbour(s))\n");
        var symbolsById = SymbolLookupBatch.FindBySymbolIds(index, reached.Select(static node => node.Id));
        foreach (var node in reached)
        {
            if (!symbolsById.TryGetValue(node.Id, out IndexedSymbol? symbol))
                continue; // inconsistent build — drop rather than NRE
            sb.Append(NeighbourLine(symbol, node.Hop)).Append('\n');
            emitted++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    // "Name  kind  file:line  (hop N)" — an auto-mode neighbour line.
    private static string NeighbourLine(IndexedSymbol s, int hop) =>
        $"{s.Name}  {s.Kind}  {s.FilePath}:{s.StartLine}  (hop {hop})";

    private static string RenderNoNeighboursMessage(ISymbolLookupIndex index, string target, int depth, string seedId)
    {
        var sb = new StringBuilder();
        sb.Append("No neighbours — nothing connects to '").Append(target).Append("' within ")
          .Append(depth).Append(" hop(s).");

        IndexedSymbol? seed = index.FindBySymbolId(seedId);
        if (seed is null)
            return sb.ToString();

        sb.Append('\n')
          .Append("Resolved target: ")
          .Append(seed.Name).Append(' ')
          .Append(seed.Kind).Append(' ')
          .Append(seed.FilePath).Append(':').Append(seed.StartLine);

        var sameFile = index.FindByFilePath(seed.FilePath)
            .Where(symbol => !string.Equals(symbol.SymbolId, seed.SymbolId, StringComparison.Ordinal))
            .Take(5)
            .ToArray();
        if (sameFile.Length > 0)
        {
            sb.Append('\n').Append("Same-file symbols:");
            foreach (IndexedSymbol symbol in sameFile)
            {
                sb.Append('\n')
                  .Append("  ")
                  .Append(symbol.Name).Append("  ")
                  .Append(symbol.Kind).Append("  ")
                  .Append(symbol.FilePath).Append(':').Append(symbol.StartLine);
            }
        }

        sb.Append('\n')
          .Append("Next: inspect ")
          .Append(seed.FilePath)
          .Append(" for nearby symbols, or search mode=source for text references not represented in the graph.");
        return sb.ToString();
    }

    // ---------- mode: path (shortest dependency path target -> to) ----------

    private static string RunPath(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver, string target, string? scope, string? to,
        int depth, int limit, bool json, out int emitted, out int nodesVisited)
    {
        emitted = 0;
        nodesVisited = 0;

        if (string.IsNullOrWhiteSpace(to))
        {
            string message = "Usage: mode=path requires 'to' (the destination symbol to find a path to).";
            return json
                ? RenderTraceJson(ModePath, target, to, depth, limit, emitted, nodesVisited, message, "missing_to")
                : message;
        }

        if (!ResolveSymbol(index, resolver, target, scope, out string fromId, out string? fromNote))
            return json
                ? RenderTraceJson(ModePath, target, to, depth, limit, emitted, nodesVisited, fromNote!, DiagnosticCode(fromNote!))
                : fromNote!;
        if (!ResolveSymbol(index, resolver, to, scope: null, out string toId, out string? toNote))
            return json
                ? RenderPathJson(index, target, to, depth, limit, emitted, nodesVisited, fromId, toId: null, path: null,
                    note: toNote!, diagnosticCode: DiagnosticCode(toNote!))
                : toNote!;

        IReadOnlyList<string>? path = graph.ShortestPath(fromId, toId, depth);
        if (path is null)
        {
            string message = $"No path from '{target}' to '{to}' within {depth} hop(s).";
            return json
                ? RenderPathJson(index, target, to, depth, limit, emitted, nodesVisited, fromId, toId, path: null,
                    note: message, diagnosticCode: "no_path")
                : message;
        }

        // The path is from..to inclusive (ShortestPath includes both endpoints). Hops = path.Count - 1.
        nodesVisited = path.Count;
        if (json)
        {
            int shownCount = Math.Min(path.Count, limit);
            emitted = shownCount;
            return RenderPathJson(index, target, to, depth, limit, emitted, nodesVisited, fromId, toId, path,
                note: shownCount < path.Count ? "path truncated by limit." : null,
                diagnosticCode: shownCount < path.Count ? "limit_truncated" : null);
        }

        var sb = new StringBuilder();
        sb.Append("# trace path ").Append(target).Append(" -> ").Append(to)
          .Append(" (").Append(path.Count - 1).Append(" hop(s))\n");

        int shown = 0;
        var symbolsById = SymbolLookupBatch.FindBySymbolIds(index, path);
        for (int i = 0; i < path.Count && shown < limit; i++)
        {
            string label = symbolsById.TryGetValue(path[i], out IndexedSymbol? symbol) && symbol is not null
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
        MillerRepositoryIndex index, SmartTargetResolver resolver, string target, string? scope,
        int depth, int limit, bool fullFormat, bool json, out int emitted, out int nodesVisited)
    {
        emitted = 0;
        nodesVisited = 0;

        if (!ResolveBridgeStart(index, resolver, target, scope, out string startId, out string? note))
            return json
                ? RenderBridgeJson(index.BridgeGraph, target, to: null, depth, limit, emitted, nodesVisited, startId: null,
                    edges: [], note!, DiagnosticCode(note!))
                : note!;

        // A symbol with no incident bridge edges is not on any cross-language thread — whether it is absent from the
        // bridge node lookup entirely or present but edge-less, the honest answer is the same. Incident subsumes both.
        if (index.BridgeGraph.Incident(startId).Count == 0)
        {
            var message = new StringBuilder();
            message.Append($"'{target}' is not on a cross-language bridge. trace bridge follows DTO/entity/table/route links; ")
              .Append("this symbol has none.");
            AppendBridgeCapabilityStatus(message, index.BridgeGraph.CapabilityReport);
            return json
                ? RenderBridgeJson(index.BridgeGraph, target, to: null, depth, limit, emitted, nodesVisited, startId,
                    edges: [], $"'{target}' is not on a cross-language bridge. trace bridge follows DTO/entity/table/route links; this symbol has none.",
                    "not_on_bridge")
                : message.ToString();
        }

        IReadOnlyList<ScoredEdge> edges = index.BridgeGraph.Walk(startId, depth);
        nodesVisited = edges.Count;

        // The start has direct incident edges (checked above), so an empty Walk means depth could not reach them.
        if (edges.Count == 0)
        {
            string message = $"No bridge links from '{target}' within {depth} hop(s).";
            return json
                ? RenderBridgeJson(index.BridgeGraph, target, to: null, depth, limit, emitted, nodesVisited, startId,
                    edges: [], message, "no_bridge_links")
                : message;
        }

        if (json)
        {
            int shownCount = Math.Min(edges.Count, limit);
            emitted = shownCount;
            return RenderBridgeJson(index.BridgeGraph, target, to: null, depth, limit, emitted, nodesVisited, startId,
                edges.Take(shownCount).ToArray(),
                note: shownCount < edges.Count ? "bridge trace truncated by limit." : null,
                diagnosticCode: shownCount < edges.Count ? "limit_truncated" : null);
        }

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

    private static void AppendBridgeCapabilityStatus(StringBuilder sb, BridgeCapabilityReport report)
    {
        if (!report.HasStatus)
            return;

        sb.Append('\n');
        if (report.ActiveProviders.Count == 0)
        {
            sb.Append("bridge providers active: none\n");
        }
        else
        {
            sb.Append("bridge providers active: ")
              .Append(string.Join(", ", report.ActiveProviders))
              .Append('\n');
        }

        foreach (var skipped in report.SkippedProviders)
            sb.Append(skipped.ProviderId).Append(" skipped: ").Append(skipped.Reason).Append('\n');

        foreach (var note in report.Notes)
            sb.Append("bridge note: ").Append(note).Append('\n');
    }

    private static bool ResolveBridgeStart(
        MillerRepositoryIndex index, SmartTargetResolver resolver, string target, string? scope,
        out string startId, out string? note)
    {
        startId = string.Empty;
        note = null;

        if (string.IsNullOrWhiteSpace(target))
        {
            note = "trace: a target symbol is required.";
            return false;
        }

        if (TryResolveSyntheticBridgeNode(index.BridgeGraph, target, out startId))
            return true;

        if (TryResolveBridgeRouteTarget(index.BridgeGraph, target, out startId, out note))
            return true;
        if (note is not null)
            return false;

        switch (resolver.Resolve(target, scope))
        {
            case TargetResolution.Symbol sym:
                startId = sym.Value.SymbolId;
                return true;

            case TargetResolution.File file:
                return ResolveBridgeFileStart(index, target, file.Path, out startId, out note);

            case TargetResolution.Candidates cands:
                if (TryResolveSingleBridgeCandidate(index, cands.Matches, out startId))
                    return true;
                note = RenderCandidatesNote(cands.Matches);
                return false;

            case TargetResolution.NotFound nf:
                note = nf.RenderMessage();
                return false;

            default:
                note = "trace: unrecognized target resolution.";
                return false;
        }
    }

    private static bool TryResolveSingleBridgeCandidate(
        MillerRepositoryIndex index,
        IReadOnlyList<IndexedSymbol> candidates,
        out string startId)
    {
        startId = string.Empty;
        var bridgeCandidates = candidates
            .Where(s => index.BridgeGraph.Incident(s.SymbolId).Count > 0)
            .ToList();
        if (bridgeCandidates.Count != 1)
            return false;

        startId = bridgeCandidates[0].SymbolId;
        return true;
    }

    private static bool TryResolveSyntheticBridgeNode(BridgeGraph graph, string target, out string startId)
    {
        startId = string.Empty;

        var route = RouteNormalizer.FromClientCall("fetch", target).Route;
        if (route.Length > 0)
        {
            string routeId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, route);
            if (graph.Contains(routeId))
            {
                startId = routeId;
                return true;
            }
        }

        var table = target.Trim();
        if (table.Length > 0)
        {
            string tableId = BridgeGraph.SynthesizeId(BridgeNodeKind.DbTable, table);
            if (graph.Contains(tableId))
            {
                startId = tableId;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveBridgeRouteTarget(
        BridgeGraph graph, string target, out string startId, out string? note)
    {
        startId = string.Empty;
        note = null;

        if (!target.Contains('/', StringComparison.Ordinal))
            return false;

        var route = RouteNormalizer.FromClientCall("fetch", target).Route;
        if (route.Length == 0)
            return false;

        var starts = graph.Edges
            .Where(e => e.Edge.Kind == BridgeKind.Hits && HitsRouteMatches(e.Edge, route))
            .Select(e => BridgeGraph.NodeIdOf(e.Edge.SourceRef, e.Edge.Kind, EndpointSide.Source))
            .Where(id => id is not null && graph.Contains(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        if (starts.Count == 1)
        {
            startId = starts[0];
            return true;
        }

        if (starts.Count > 1)
            note = RenderBridgeRouteCandidatesNote(graph, route, starts);

        return false;
    }

    private static bool HitsRouteMatches(CandidateEdge edge, string route) =>
        string.Equals(edge.SourceRef.Display, route, StringComparison.Ordinal)
        || string.Equals(edge.TargetRef.Display, route, StringComparison.Ordinal);

    private static string RenderBridgeRouteCandidatesNote(
        BridgeGraph graph, string route, IReadOnlyList<string> startIds)
    {
        var sb = new StringBuilder();
        sb.Append("Multiple bridge starts match route '").Append(route)
          .Append("' — pass a symbol name/id:\n");
        foreach (var id in startIds)
        {
            var node = graph.Node(id);
            sb.Append(node?.Display ?? id);
            if (node?.FilePath is { Length: > 0 })
                sb.Append("  ").Append(node.FilePath);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static bool ResolveBridgeFileStart(
        MillerRepositoryIndex index, string target, string filePath,
        out string startId, out string? note)
    {
        startId = string.Empty;
        note = null;

        var symbols = index.FindByFilePath(filePath);
        if (symbols.Count == 0 && !index.IsIndexedFilePath(filePath))
        {
            note = $"'{target}' is not a bridge route/table node or indexed file. Try search to locate a symbol.";
            return false;
        }

        var bridgeSymbols = symbols
            .Where(s => index.BridgeGraph.Incident(s.SymbolId).Count > 0)
            .OrderBy(s => s.StartLine)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        if (bridgeSymbols.Count == 1)
        {
            startId = bridgeSymbols[0].SymbolId;
            return true;
        }

        if (bridgeSymbols.Count == 0)
        {
            note = $"'{target}' is a file, but no symbols in it are on a cross-language bridge. " +
                   "Pass a symbol name/id, frontend route, or table name.";
            return false;
        }

        note = RenderBridgeFileCandidatesNote(filePath, bridgeSymbols);
        return false;
    }

    private static string RenderBridgeFileCandidatesNote(string filePath, IReadOnlyList<IndexedSymbol> matches)
    {
        var sb = new StringBuilder();
        sb.Append("Multiple bridge-connected symbols in '").Append(filePath)
          .Append("' — pass a symbol name/id:\n");
        foreach (var s in matches)
            sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
              .Append(s.FilePath).Append(':').Append(s.StartLine).Append('\n');
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
        ISymbolLookupIndex index, SmartTargetResolver resolver, string target, string? scope,
        out string symbolId, out string? note)
    {
        symbolId = string.Empty;
        note = null;

        if (string.IsNullOrWhiteSpace(target))
        {
            note = "trace: a target symbol is required.";
            return false;
        }

        switch (resolver.Resolve(target, scope))
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
                note = nf.RenderMessage();
                return false;

            default:
                note = "trace: unrecognized target resolution.";
                return false;
        }
    }

    private static string RenderCandidatesNote(IReadOnlyList<IndexedSymbol> matches)
    {
        var sb = new StringBuilder();
        sb.Append(CandidateOutput.Header(
            matches,
            supportsScope: true,
            fallback: "Multiple candidates — pass a more specific target:")).Append('\n');
        foreach (var s in CandidateOutput.Visible(matches))
            sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
              .Append(s.FilePath).Append(':').Append(s.StartLine).Append('\n');
        CandidateOutput.AppendRemainderNote(sb, matches.Count);
        return sb.ToString().TrimEnd('\n');
    }

    private static string UnknownMode(string? mode, bool json, string target, string? to, int depth, int limit, out int emitted, out int nodesVisited)
    {
        emitted = 0;
        nodesVisited = 0;
        string message = $"Unknown mode '{mode}'. Use one of: auto, path, bridge.";
        return json
            ? RenderTraceJson(mode ?? string.Empty, target, to, depth, limit, emitted, nodesVisited, message, "unknown_mode")
            : message;
    }

    // ---------- JSON rendering ----------

    private static string RenderAutoJson(
        ISymbolLookupIndex index, string target, string? to, int depth, int limit, int emitted, int nodesVisited,
        string seedId, IReadOnlyList<ReachedNode> reached, string? note, string? diagnosticCode)
    {
        var symbolsById = SymbolLookupBatch.FindBySymbolIds(index, reached.Select(static node => node.Id).Prepend(seedId));
        symbolsById.TryGetValue(seedId, out IndexedSymbol? seed);
        return RenderTraceJson(ModeAuto, target, to, depth, limit, emitted, nodesVisited, note, diagnosticCode,
            writeResolvedTarget: w => WriteSymbolOrNull(w, seed),
            writeNodes: w =>
            {
                w.WriteStartArray();
                if (seed is not null)
                    WriteSymbolNode(w, seed, "target", hop: 0);
                foreach (var node in reached)
                {
                    if (symbolsById.TryGetValue(node.Id, out IndexedSymbol? symbol))
                        WriteSymbolNode(w, symbol, "neighbour", node.Hop);
                }
                w.WriteEndArray();
            },
            writeLinks: w =>
            {
                w.WriteStartArray();
                foreach (var node in reached)
                {
                    w.WriteStartObject();
                    w.WriteString("source", seedId);
                    w.WriteString("target", node.Id);
                    w.WriteString("kind", "neighbour");
                    w.WriteString("direction", "both");
                    w.WriteNumber("hop", node.Hop);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
    }

    private static string RenderPathJson(
        ISymbolLookupIndex index, string target, string? to, int depth, int limit, int emitted, int nodesVisited,
        string? fromId, string? toId, IReadOnlyList<string>? path, string? note, string? diagnosticCode)
    {
        var ids = new List<string>();
        if (fromId is not null)
            ids.Add(fromId);
        if (toId is not null)
            ids.Add(toId);
        if (path is not null)
            ids.AddRange(path);

        var symbolsById = SymbolLookupBatch.FindBySymbolIds(index, ids);
        symbolsById.TryGetValue(fromId ?? string.Empty, out IndexedSymbol? fromSymbol);
        symbolsById.TryGetValue(toId ?? string.Empty, out IndexedSymbol? toSymbol);
        int shownCount = path is null ? 0 : Math.Min(path.Count, limit);

        return RenderTraceJson(ModePath, target, to, depth, limit, emitted, nodesVisited, note, diagnosticCode,
            writeExtraRootProperties: w =>
            {
                if (path is null)
                    w.WriteNull("hops");
                else
                    w.WriteNumber("hops", path.Count - 1);
            },
            writeResolvedTarget: w => WriteSymbolOrNull(w, fromSymbol),
            writeResolvedTo: w => WriteSymbolOrNull(w, toSymbol),
            writeNodes: w =>
            {
                w.WriteStartArray();
                if (path is not null)
                {
                    for (int i = 0; i < shownCount; i++)
                    {
                        if (symbolsById.TryGetValue(path[i], out IndexedSymbol? symbol))
                            WriteSymbolNode(w, symbol, i == 0 ? "target" : i == path.Count - 1 ? "destination" : "path", hop: i);
                    }
                }
                w.WriteEndArray();
            },
            writeLinks: w =>
            {
                w.WriteStartArray();
                if (path is not null)
                {
                    for (int i = 1; i < shownCount; i++)
                    {
                        w.WriteStartObject();
                        w.WriteString("source", path[i - 1]);
                        w.WriteString("target", path[i]);
                        w.WriteString("kind", "dependency_path");
                        w.WriteNumber("hop", i);
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();
            });
    }

    private static string RenderBridgeJson(
        BridgeGraph graph, string target, string? to, int depth, int limit, int emitted, int nodesVisited,
        string? startId, IReadOnlyList<ScoredEdge> edges, string? note, string? diagnosticCode)
    {
        BridgeNode? startNode = startId is null ? null : graph.Node(startId);
        var nodeIds = new SortedSet<string>(StringComparer.Ordinal);
        if (startId is not null)
            nodeIds.Add(startId);
        foreach (var edge in edges)
        {
            string? sourceId = BridgeGraph.NodeIdOf(edge.Edge.SourceRef, edge.Edge.Kind, EndpointSide.Source);
            string? targetId = BridgeGraph.NodeIdOf(edge.Edge.TargetRef, edge.Edge.Kind, EndpointSide.Target);
            if (sourceId is not null)
                nodeIds.Add(sourceId);
            if (targetId is not null)
                nodeIds.Add(targetId);
        }

        return RenderTraceJson(ModeBridge, target, to, depth, limit, emitted, nodesVisited, note, diagnosticCode,
            writeResolvedTarget: w => WriteBridgeNodeOrNull(w, startNode),
            writeProvider: w => WriteProvider(w, graph.CapabilityReport),
            writeNodes: w =>
            {
                w.WriteStartArray();
                foreach (string id in nodeIds)
                {
                    BridgeNode? node = graph.Node(id);
                    if (node is not null)
                        WriteBridgeNode(w, node, string.Equals(id, startId, StringComparison.Ordinal) ? "target" : "bridge");
                }
                w.WriteEndArray();
            },
            writeLinks: w =>
            {
                w.WriteStartArray();
                foreach (var edge in edges)
                    WriteBridgeLink(w, graph, edge);
                w.WriteEndArray();
            });
    }

    private static string RenderTraceJson(
        string mode, string target, string? to, int depth, int limit, int emitted, int nodesVisited,
        string? note, string? diagnosticCode,
        Action<Utf8JsonWriter>? writeExtraRootProperties = null,
        Action<Utf8JsonWriter>? writeResolvedTarget = null,
        Action<Utf8JsonWriter>? writeResolvedTo = null,
        Action<Utf8JsonWriter>? writeProvider = null,
        Action<Utf8JsonWriter>? writeNodes = null,
        Action<Utf8JsonWriter>? writeLinks = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("mode", mode);
            w.WriteString("target", target);
            if (to is null) w.WriteNull("to"); else w.WriteString("to", to);
            w.WriteNumber("depth", depth);
            w.WriteNumber("limit", limit);
            w.WriteNumber("emitted", emitted);
            w.WriteNumber("nodes_visited", nodesVisited);
            if (note is null) w.WriteNull("note"); else w.WriteString("note", note);
            writeExtraRootProperties?.Invoke(w);

            w.WritePropertyName("resolved_target");
            if (writeResolvedTarget is null) w.WriteNullValue(); else writeResolvedTarget(w);
            w.WritePropertyName("resolved_to");
            if (writeResolvedTo is null) w.WriteNullValue(); else writeResolvedTo(w);
            w.WritePropertyName("provider");
            if (writeProvider is null) w.WriteNullValue(); else writeProvider(w);
            w.WritePropertyName("nodes");
            if (writeNodes is null) w.WriteStartArray(); else writeNodes(w);
            if (writeNodes is null) w.WriteEndArray();
            w.WritePropertyName("links");
            if (writeLinks is null) w.WriteStartArray(); else writeLinks(w);
            if (writeLinks is null) w.WriteEndArray();
            w.WritePropertyName("diagnostics");
            WriteDiagnostics(w, diagnosticCode, note);
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    private static void WriteDiagnostics(Utf8JsonWriter w, string? code, string? message)
    {
        w.WriteStartArray();
        if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(message))
        {
            w.WriteStartObject();
            w.WriteString("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteSymbolOrNull(Utf8JsonWriter w, IndexedSymbol? symbol)
    {
        if (symbol is null)
        {
            w.WriteNullValue();
            return;
        }
        WriteSymbolNode(w, symbol, role: null, hop: null);
    }

    private static void WriteSymbolNode(Utf8JsonWriter w, IndexedSymbol symbol, string? role, int? hop)
    {
        w.WriteStartObject();
        w.WriteString("id", symbol.SymbolId);
        w.WriteString("symbol_id", symbol.SymbolId);
        w.WriteString("name", symbol.Name);
        w.WriteString("kind", symbol.Kind);
        w.WriteString("file", symbol.FilePath);
        w.WriteNumber("line", symbol.StartLine);
        if (role is null) w.WriteNull("role"); else w.WriteString("role", role);
        if (hop is null) w.WriteNull("hop"); else w.WriteNumber("hop", hop.Value);
        w.WriteEndObject();
    }

    private static void WriteBridgeNodeOrNull(Utf8JsonWriter w, BridgeNode? node)
    {
        if (node is null)
        {
            w.WriteNullValue();
            return;
        }
        WriteBridgeNode(w, node, role: null);
    }

    private static void WriteBridgeNode(Utf8JsonWriter w, BridgeNode node, string? role)
    {
        w.WriteStartObject();
        w.WriteString("id", node.Id);
        w.WriteString("kind", BridgeNodeKindJson(node.Kind));
        w.WriteString("display", node.Display);
        if (node.FilePath is null) w.WriteNull("file"); else w.WriteString("file", node.FilePath);
        w.WriteNumber("line", node.Line);
        if (role is null) w.WriteNull("role"); else w.WriteString("role", role);
        w.WriteEndObject();
    }

    private static void WriteProvider(Utf8JsonWriter w, BridgeCapabilityReport report)
    {
        w.WriteStartObject();
        w.WritePropertyName("active_providers");
        w.WriteStartArray();
        foreach (string provider in report.ActiveProviders)
            w.WriteStringValue(provider);
        w.WriteEndArray();

        w.WritePropertyName("skipped_providers");
        w.WriteStartArray();
        foreach (var skipped in report.SkippedProviders)
        {
            w.WriteStartObject();
            w.WriteString("provider_id", skipped.ProviderId);
            w.WriteString("reason", skipped.Reason);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WritePropertyName("notes");
        w.WriteStartArray();
        foreach (string providerNote in report.Notes)
            w.WriteStringValue(providerNote);
        w.WriteEndArray();

        w.WritePropertyName("evidence_counts");
        w.WriteStartArray();
        foreach (var item in report.EvidenceCounts.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.WriteString("name", item.Key);
            w.WriteNumber("count", item.Value);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteBridgeLink(Utf8JsonWriter w, BridgeGraph graph, ScoredEdge edge)
    {
        string? sourceId = BridgeGraph.NodeIdOf(edge.Edge.SourceRef, edge.Edge.Kind, EndpointSide.Source);
        string? targetId = BridgeGraph.NodeIdOf(edge.Edge.TargetRef, edge.Edge.Kind, EndpointSide.Target);

        w.WriteStartObject();
        if (sourceId is null) w.WriteNull("source"); else w.WriteString("source", sourceId);
        if (targetId is null) w.WriteNull("target"); else w.WriteString("target", targetId);
        w.WriteString("source_display", EndpointDisplay(graph, edge.Edge.SourceRef, edge.Edge.Kind, EndpointSide.Source));
        w.WriteString("target_display", EndpointDisplay(graph, edge.Edge.TargetRef, edge.Edge.Kind, EndpointSide.Target));
        w.WriteString("kind", BridgeKindJson(edge.Edge.Kind));
        w.WriteString("label", KindLabel(edge.Edge.Kind));
        w.WriteNumber("score", edge.Score);
        w.WriteString("confidence", edge.Band.ToString().ToLowerInvariant());
        w.WriteBoolean("multi_signal", edge.IsMultiSignal);
        w.WritePropertyName("flags");
        WriteFlags(w, edge);
        w.WritePropertyName("evidence");
        WriteEvidenceArray(w, edge.Edge.Evidence);
        w.WritePropertyName("signals");
        WriteSignalsArray(w, edge.Edge.Signals);
        w.WriteEndObject();
    }

    private static void WriteFlags(Utf8JsonWriter w, ScoredEdge edge)
    {
        w.WriteStartArray();
        if (edge.HasAmbiguousName)
            w.WriteStringValue("ambiguous");
        if (edge.IsVerbUnknown)
            w.WriteStringValue("verb_unknown");
        w.WriteEndArray();
    }

    private static void WriteEvidenceArray(Utf8JsonWriter w, IReadOnlyList<Miller.Core.Contracts.Evidence> evidence)
    {
        w.WriteStartArray();
        foreach (var item in evidence)
            WriteEvidence(w, item);
        w.WriteEndArray();
    }

    private static void WriteSignalsArray(Utf8JsonWriter w, IReadOnlyList<Signal> signals)
    {
        w.WriteStartArray();
        foreach (var signal in signals)
        {
            w.WriteStartObject();
            w.WriteString("rule", signal.Rule.ToString());
            switch (signal)
            {
                case StructuralSignal structural:
                    w.WriteString("type", "structural");
                    w.WriteBoolean("present", structural.Present);
                    break;
                case FieldSetSignal fieldSet:
                    w.WriteString("type", "field_set");
                    w.WriteNumber("field_count", fieldSet.FieldCount);
                    w.WriteNumber("jaccard", fieldSet.Jaccard);
                    break;
                case NameSignal name:
                    w.WriteString("type", "name");
                    w.WriteString("tier", name.Tier.ToString().ToLowerInvariant());
                    break;
                case NameResolutionSignal resolution:
                    w.WriteString("type", "name_resolution");
                    w.WriteString("endpoint", resolution.Endpoint.ToString().ToLowerInvariant());
                    w.WriteString("status", resolution.Status.ToString().ToLowerInvariant());
                    w.WriteNumber("match_count", resolution.MatchCount);
                    break;
                default:
                    w.WriteString("type", "unknown");
                    break;
            }
            w.WritePropertyName("evidence");
            if (signal.Evidence is null)
                w.WriteNullValue();
            else
                WriteEvidence(w, signal.Evidence);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteEvidence(Utf8JsonWriter w, Miller.Core.Contracts.Evidence evidence)
    {
        w.WriteStartObject();
        w.WriteString("file", evidence.FilePath);
        w.WriteNumber("line", evidence.Line);
        w.WriteEndObject();
    }

    private static string BridgeKindJson(BridgeKind kind) => kind switch
    {
        BridgeKind.StoredIn => "stored_in",
        BridgeKind.MapsTo => "maps_to",
        BridgeKind.Hits => "hits",
        BridgeKind.Responds => "responds",
        BridgeKind.Consumes => "consumes",
        BridgeKind.NameMatch => "name_match",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string BridgeNodeKindJson(BridgeNodeKind kind) => kind switch
    {
        BridgeNodeKind.TsType => "ts_type",
        BridgeNodeKind.CsDto => "cs_dto",
        BridgeNodeKind.CsEntity => "cs_entity",
        BridgeNodeKind.DbTable => "db_table",
        BridgeNodeKind.Endpoint => "endpoint",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string DiagnosticCode(string note)
    {
        if (note.StartsWith("trace: a target symbol is required.", StringComparison.Ordinal))
            return "missing_target";
        if (note.Contains("Multiple candidates", StringComparison.Ordinal) ||
            note.Contains("Multiple bridge", StringComparison.Ordinal))
            return "ambiguous_target";
        if (note.Contains("not found", StringComparison.Ordinal))
            return "target_not_found";
        if (note.Contains("is a file", StringComparison.Ordinal))
            return "file_target";
        return "trace_note";
    }

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Utf8(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan);
}
