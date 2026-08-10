using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Resolver;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// Follow exact references, dependency paths, and cross-language bridges. Path and bridge modes traverse the
/// in-memory graphs Miller already builds; refs reads target-resolved evidence from the extracted artifact:
/// <list type="bullet">
/// <item><b>path</b> — the shortest dependency path from <c>target</c> to <c>to</c> via
///   <see cref="SymbolGraph.ShortestPath"/> (Task 8); a clean message when the two are not connected within
///   <c>depth</c>.</item>
/// <item><b>refs</b> — exact identifier, relationship, and resolution evidence keyed by the target symbol ID, with
///   unresolved name matches isolated as fallback.</item>
/// <item><b>bridge</b> — the provider-scoped structural chain via <see cref="BridgeGraph.Walk"/> over the loaded
///   <see cref="BridgeGraph"/>, rendering each scored bridge edge with its confidence band and score. Current
///   providers are dotnet-web, nextjs, nextjs-api, nuxt, nuxt-api, vue, react, and backend-http.</item>
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
        "Follow a thread of code. mode=refs lists exact target-resolved references with enclosing symbols and " +
        "separately labels unresolved fallback evidence; JSON reference rows include exact spans plus target and " +
        "containing symbol ids for direct call-site evidence. " +
        "mode=path finds the shortest call-like path from target to 'to' and reports kind/confidence/provenance " +
        "per hop; path_kind=dependency opts into broad dependency edges. mode=bridge follows " +
        "provider-scoped cross-language chains (dotnet-web, nextjs, nextjs-api, nuxt, nuxt-api, vue, react, " +
        "backend-http) with a confidence band. refs may be empty for some languages; on empty, use " +
        "search mode=source for text occurrences. Reduced-confidence links are flagged [verb-unknown]/[ambiguous]; " +
        "treat flagged and unflagged links alike as leads to confirm with inspect. NOT for: a symbol's own definition/signature " +
        "(inspect), or ranking which tests to run before a change (impact). Example: trace target=FreshnessService " +
        "mode=refs. format=json for structure; format=full adds per-link signals in bridge output. Empty results " +
        "include next actions.")]
    public string Trace(
        [Description("A symbol name/id where the trace starts. In bridge mode, route/table nodes and single-symbol files are also accepted.")]
        string target,
        [Description("Disambiguate an ambiguous target symbol name to a file. Optional.")]
        string? scope = null,
        [Description("Trace mode: refs (exact usages plus labeled fallback) | path | bridge. Default refs.")]
        string mode = "refs",
        [Description("For mode=path: the destination symbol name/id the path must reach. Ignored otherwise.")]
        string? to = null,
        [Description("For mode=path: call (call-like edges only) or dependency (all dependency edges). Default call.")]
        string? path_kind = "call",
        [Description("For mode=refs: optional reference kind filter: call, variable_ref, type_usage, member_access, or import.")]
        string? reference_kind = null,
        [Description("For mode=refs: include the resolved target definition in compact output and JSON nodes. Default true.")]
        bool include_definition = true,
        [Description("For mode=refs: stateless continuation token returned by a prior truncated reference page.")]
        string? continuation = null,
        [Description("How many hops to follow. Default 3.")] int depth = 3,
        [Description("Max links/neighbours to return. Default 20.")] int limit = 20,
        [Description("Output format: compact|json|full. full adds the firing signals per bridge link in compact output. Default compact.")]
        string format = "compact",
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")] string? workspace_id = null,
        [Description("Refresh a registered workspace before reading. Defaults true when workspace_id is supplied.")]
        bool? ensure_fresh = null)
    {
        var telemetry = TelemetryContext.Current;
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (!string.Equals(format, "compact", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(format, "full", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                    "invalid_format",
                    "trace format must be compact, json, or full."));
            }
            bool full = string.Equals(format, "full", StringComparison.OrdinalIgnoreCase);
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            using WorkspaceReadContext context = _workspaceProvider.Resolve(workspace_id, ensureFresh);
            string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            string normalizedMode = NormalizeMode(mode);
            if (normalizedMode is not (ModeRefs or ModePath or ModeBridge))
            {
                throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                    "invalid_mode",
                    "trace mode must be refs, path, or bridge."));
            }
            if (normalizedMode == ModePath)
            {
                string normalizedPathKind = string.IsNullOrWhiteSpace(path_kind)
                    ? "call"
                    : path_kind.Trim().ToLowerInvariant();
                if (normalizedPathKind is not ("call" or "dependency"))
                {
                    throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                        "invalid_path_kind",
                        "trace path_kind must be call or dependency."));
                }
            }
            if (normalizedMode == ModeRefs &&
                !TryNormalizeReferenceKind(reference_kind, out _, out string? kindError))
            {
                throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                    "invalid_reference_kind",
                    kindError!));
            }
            bool resolutionConverging =
                IndexLevelGuard.ResolutionLayerConverging(context.ReadSession.Snapshot);
            string output;
            int emitted;
            int nodesVisited;
            if (resolutionConverging)
            {
                output = string.Empty;
                emitted = 0;
                nodesVisited = 0;
            }
            else
            {
                output = normalizedMode == ModePath
                    ? Run(
                        context.Index,
                        context.Resolver,
                        target,
                        scope,
                        mode,
                        to,
                        depth,
                        limit,
                        full,
                        json,
                        path_kind,
                        out emitted,
                        out nodesVisited)
                    : Run(context.Index, context.Resolver,
                        target, scope, mode, to, depth, limit, full,
                        json, reference_kind, include_definition,
                        (symbol, query) => ReferenceEvidenceReader.Read(context.ReadSession, symbol.SymbolId, query),
                        context.WorkspaceId ?? "current",
                        normalizedMode == ModeRefs
                            ? ReferenceEvidenceReader.ReadSnapshot(context.ReadSession)
                            : null,
                        continuation,
                        out emitted, out nodesVisited);
            }
            output = ReadToolWorkspaceRouting.PrefixCompact(output, compactBanner);
            ToolDiagnostic? diagnostic;
            if (resolutionConverging)
            {
                IndexLevelGuard.MarkDegraded(telemetry, "resolution_converging");
                diagnostic = IndexLevelGuard.ResolutionConverging();
            }
            else if (IndexLevelGuard.ReferenceLayerConverging(context.Index))
            {
                // The converging explanation beats the generic empty diagnostic here: an empty (or thin) trace
                // against a symbols-level artifact is expected, not evidence about the code.
                IndexLevelGuard.MarkDegraded(telemetry, "reference_layer_converging");
                diagnostic = IndexLevelGuard.Converging(
                    "identifier-level usage sites are missing from refs/path/bridge results.");
            }
            else
            {
                diagnostic = emitted == 0 ? TraceEmptyDiagnostic(normalizedMode, output, target, to) : null;
            }

            if (telemetry is not null)
            {
                ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
                telemetry.Op = normalizedMode;
                telemetry.SetTarget(target);
                telemetry.ResultCount = emitted;
                // D10 work proxy (bytes_examined ≈ nodes visited): the edges/neighbours the walk produced.
                telemetry.BytesExamined = nodesVisited;
                telemetry.Outcome = diagnostic is null ? TelemetryOutcome.Ok : TelemetryOutcome.Empty;
                telemetry.SetMetadata("format", full ? "full" : json ? "json" : "compact");
                telemetry.SetMetadata("has_scope", !string.IsNullOrWhiteSpace(scope));
                telemetry.SetMetadata("has_to", !string.IsNullOrWhiteSpace(to));
                telemetry.SetMetadata("path_kind", normalizedMode == ModePath ? path_kind : null);
                telemetry.SetMetadata("reference_kind", string.IsNullOrWhiteSpace(reference_kind) ? null : reference_kind.Trim());
                telemetry.SetMetadata("include_definition", include_definition);
                telemetry.SetMetadata("depth_bucket", DepthBucket(depth));
                telemetry.SetMetadata("limit_bucket", LimitBucket(limit));
            }
            if (diagnostic is not null)
            {
                output = ToolDiagnosticRenderer.Attach(
                    "trace",
                    output,
                    diagnostic,
                    json,
                    telemetry);
            }
            return output;
        }
        catch (Exception ex)
        {
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex);
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
                telemetry?.SetError(ex);
            return ToolDiagnosticRenderer.Render(
                "trace",
                diagnostic,
                json,
                telemetry);
        }
    }

    private const string ModePath = "path";
    private const string ModeRefs = "refs";
    private const string ModeBridge = "bridge";
    private const int MaxNextActions = 10;
    private const int MaxReferencePageBytes = 12 * 1024;

    private sealed record TraceNextAction(string Tool, string Reason, IReadOnlyList<KeyValuePair<string, string>> Args);

    private sealed record BridgeRouteDiagnostic(string Code, string Message);

    /// <summary>
    /// One row of the per-provider route diagnostic table. The navigation providers (nextjs/nuxt/vue/react)
    /// compare route references against <see cref="BridgeNodeKind.FileRoute"/> observation nodes; the API and
    /// backend providers (nextjs-api/nuxt-api/backend-http) compare client requests against
    /// <see cref="BridgeNodeKind.Endpoint"/> handler/route-fact observation nodes.
    /// <paramref name="DefinitionEvidenceName"/> is the definition-side evidence
    /// count key suffix; API providers participate only when it is non-zero, because both share the
    /// <c>http.client_request.v1</c> reference family and client requests alone cannot attribute a repo to
    /// Next.js vs Nuxt — the handler facts can. The noun fields keep rendered diagnostics honest per provider
    /// ("client request" vs "route reference", "route edge" vs "navigation edge").
    /// </summary>
    private sealed record FileRouteDiagnosticProvider(
        string ProviderId,
        string DisplayName,
        string ReferenceNoun,
        string TargetFactName,
        BridgeNodeKind DefinitionNodeKind,
        string DefinitionEvidenceName,
        string EdgeNoun)
    {
        public string DiagnosticCode(string suffix) => ProviderId + "_" + suffix;
    }

    private static readonly HashSet<string> KnownReferenceKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "call",
        "variable_ref",
        "type_usage",
        "member_access",
        "import",
    };

    private const string ReferenceKindUsage =
        "reference_kind must be one of: call, variable_ref, type_usage, member_access, import.";

    private static string NormalizeMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? ModeRefs : mode.Trim().ToLowerInvariant();

    private static ToolDiagnostic TraceEmptyDiagnostic(string mode, string output, string target, string? to)
    {
        if (mode is not (ModePath or ModeRefs or ModeBridge))
            return ToolDiagnostic.Unsupported("unsupported_mode", $"Trace mode '{mode}' is not supported.");
        if (mode == ModePath &&
            output.Contains("path_kind must be one of", StringComparison.Ordinal))
        {
            return ToolDiagnostic.Unsupported(
                "invalid_path_kind",
                "path_kind must be one of: call, dependency.");
        }

        bool ambiguous = output.Contains("Multiple candidates", StringComparison.Ordinal);
        bool unresolved =
            ambiguous ||
            output.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("is a file", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("target symbol is required", StringComparison.OrdinalIgnoreCase);
        if (unresolved)
        {
            var actions = new[]
            {
                new ToolDiagnosticAction(
                    $"search(query=\"{ToolDiagnosticText.EscapeCallArgument(target)}\")",
                    "resolve a concrete trace target"),
            };
            return ambiguous
                ? ToolDiagnostic.Ambiguity("unresolved_target", "Trace target is ambiguous.", actions)
                : ToolDiagnostic.ExpectedEmpty("unresolved_target", "Trace target could not be resolved.", actions);
        }

        ToolDiagnosticAction sourceRefs = new(
            $"trace(target=\"{ToolDiagnosticText.EscapeCallArgument(target)}\", mode=\"refs\")",
            "check extracted identifier references from the resolved target");
        ToolDiagnosticAction sourceSearch = new(
            $"search(query=\"{ToolDiagnosticText.EscapeCallArgument(target)}\", mode=\"source\")",
            "look for source text links not represented in the graph");
        if (mode == ModeRefs)
        {
            return ToolDiagnostic.ExpectedEmpty(
                "no_references",
                "Trace produced no references.",
                [
                    sourceSearch,
                    new ToolDiagnosticAction(
                        $"inspect(target=\"{ToolDiagnosticText.EscapeCallArgument(target)}\", depth=\"full\")",
                        "inspect callers and callees from the symbol graph"),
                ]);
        }

        if (mode == ModeBridge)
        {
            return ToolDiagnostic.ExpectedEmpty(
                "no_bridge_path",
                "Trace produced no bridge path.",
                [sourceRefs, sourceSearch]);
        }

        var pathActions = new List<ToolDiagnosticAction> { sourceRefs };
        if (!string.IsNullOrWhiteSpace(to) &&
            !string.Equals(target.Trim(), to.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            pathActions.Add(new ToolDiagnosticAction(
                $"trace(target=\"{ToolDiagnosticText.EscapeCallArgument(to)}\", mode=\"refs\")",
                "check extracted identifier references from the resolved destination"));
        }
        pathActions.Add(sourceSearch);
        return ToolDiagnostic.ExpectedEmpty("no_path", "Trace produced no dependency path.", pathActions);
    }

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
        MillerRepositoryIndex index,
        SmartTargetResolver resolver,
        string target,
        string? scope,
        string mode,
        string? to,
        int depth,
        int limit,
        bool fullFormat,
        string? pathKind,
        out int emitted,
        out int nodesVisited) =>
        Run(
            index,
            resolver,
            target,
            scope,
            mode,
            to,
            depth,
            limit,
            fullFormat,
            json: false,
            pathKind,
            out emitted,
            out nodesVisited);

    public static string Run(
        MillerRepositoryIndex index,
        SmartTargetResolver resolver,
        string target,
        string? scope,
        string mode,
        string? to,
        int depth,
        int limit,
        bool fullFormat,
        bool json,
        string? pathKind,
        out int emitted,
        out int nodesVisited)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);
        if (!string.Equals(NormalizeMode(mode), ModePath, StringComparison.Ordinal))
        {
            return Run(
                index,
                resolver,
                target,
                scope,
                mode,
                to,
                depth,
                limit,
                fullFormat,
                json,
                out emitted,
                out nodesVisited);
        }
        return RunPath(
            index,
            index.Graph,
            resolver,
            target,
            scope,
            to,
            depth,
            limit,
            json,
            pathKind,
            out emitted,
            out nodesVisited);
    }

    public static string Run(
        MillerRepositoryIndex index, SmartTargetResolver resolver,
        string target, string? scope, string mode, string? to, int depth, int limit, bool fullFormat, bool json,
        string? referenceKind, bool includeDefinition,
        Func<IndexedSymbol, ReferenceEvidenceQuery, ReferenceEvidenceSet>? readReferenceEvidence,
        out int emitted, out int nodesVisited) =>
        Run(
            index,
            resolver,
            target,
            scope,
            mode,
            to,
            depth,
            limit,
            fullFormat,
            json,
            referenceKind,
            includeDefinition,
            readReferenceEvidence,
            "current",
            null,
            null,
            out emitted,
            out nodesVisited);

    public static string Run(
        MillerRepositoryIndex index, SmartTargetResolver resolver,
        string target, string? scope, string mode, string? to, int depth, int limit, bool fullFormat, bool json,
        string? referenceKind, bool includeDefinition,
        Func<IndexedSymbol, ReferenceEvidenceQuery, ReferenceEvidenceSet>? readReferenceEvidence,
        string workspaceId,
        ReferenceEvidenceSnapshot? snapshot,
        string? continuation,
        out int emitted, out int nodesVisited)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);

        string normalizedMode = (mode ?? ModeRefs).Trim().ToLowerInvariant();

        return normalizedMode switch
        {
            ModePath => RunGraph(index, index.Graph, resolver, target, scope, normalizedMode, to, depth, limit, fullFormat,
                json, out emitted, out nodesVisited),
            ModeRefs => RunRefs(index, resolver, target, scope, depth, limit, json,
                referenceKind, includeDefinition, readReferenceEvidence, workspaceId, snapshot, continuation,
                out emitted, out nodesVisited),
            ModeBridge => RunBridge(index, resolver, target, scope, depth, limit, fullFormat, json, out emitted, out nodesVisited),
            _ => throw InvalidMode(mode),
        };
    }

    public static string Run(
        MillerRepositoryIndex index, SmartTargetResolver resolver,
        string target, string? scope, string mode, string? to, int depth, int limit, bool fullFormat, bool json,
        out int emitted, out int nodesVisited) =>
        Run(index, resolver, target, scope, mode, to, depth, limit, fullFormat, json,
            referenceKind: null, includeDefinition: true, readReferenceEvidence: null, out emitted, out nodesVisited);

    public static string RunGraph(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string target, string? scope, string mode, string? to, int depth, int limit, bool fullFormat,
        out int emitted, out int nodesVisited) =>
        RunGraph(index, graph, resolver, target, scope, mode, to, depth, limit, fullFormat, json: false,
            out emitted, out nodesVisited);

    public static string RunGraph(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string target, string? scope, string mode, string? to, int depth, int limit, bool fullFormat, bool json,
        string? referenceKind, bool includeDefinition,
        Func<IndexedSymbol, ReferenceEvidenceQuery, ReferenceEvidenceSet>? readReferenceEvidence,
        out int emitted, out int nodesVisited) =>
        RunGraph(
            index,
            graph,
            resolver,
            target,
            scope,
            mode,
            to,
            depth,
            limit,
            fullFormat,
            json,
            referenceKind,
            includeDefinition,
            readReferenceEvidence,
            "current",
            null,
            null,
            out emitted,
            out nodesVisited);

    public static string RunGraph(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string target, string? scope, string mode, string? to, int depth, int limit, bool fullFormat, bool json,
        string? referenceKind, bool includeDefinition,
        Func<IndexedSymbol, ReferenceEvidenceQuery, ReferenceEvidenceSet>? readReferenceEvidence,
        string workspaceId,
        ReferenceEvidenceSnapshot? snapshot,
        string? continuation,
        out int emitted, out int nodesVisited)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(resolver);
        if (depth < 1) depth = 1;
        if (limit < 1) limit = 1;
        emitted = 0;
        nodesVisited = 0;

        string normalizedMode = (mode ?? ModeRefs).Trim().ToLowerInvariant();

        return normalizedMode switch
        {
            ModePath => RunPath(
                index,
                graph,
                resolver,
                target,
                scope,
                to,
                depth,
                limit,
                json,
                pathKind: "call",
                out emitted,
                out nodesVisited),
            ModeRefs => RunRefs(index, resolver, target, scope, depth, limit, json,
                referenceKind, includeDefinition, readReferenceEvidence, workspaceId, snapshot, continuation,
                out emitted, out nodesVisited),
            ModeBridge => json
                ? RenderTraceJson(ModeBridge, target, to, depth, limit, emitted: 0, nodesVisited: 0,
                    note: "trace mode=bridge requires the full repository bridge graph.",
                    diagnosticCode: "bridge_requires_full_index")
                : "trace mode=bridge requires the full repository bridge graph.",
            _ => throw InvalidMode(mode),
        };
    }

    public static string RunGraph(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string target, string? scope, string mode, string? to, int depth, int limit, bool fullFormat, bool json,
        out int emitted, out int nodesVisited) =>
        RunGraph(index, graph, resolver, target, scope, mode, to, depth, limit, fullFormat, json,
            referenceKind: null, includeDefinition: true, readReferenceEvidence: null, out emitted, out nodesVisited);

    public static string RunGraph(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string target,
        string? scope,
        string mode,
        string? to,
        int depth,
        int limit,
        bool fullFormat,
        bool json,
        string? pathKind,
        out int emitted,
        out int nodesVisited)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(resolver);
        if (!string.Equals(NormalizeMode(mode), ModePath, StringComparison.Ordinal))
        {
            return RunGraph(
                index,
                graph,
                resolver,
                target,
                scope,
                mode,
                to,
                depth,
                limit,
                fullFormat,
                json,
                out emitted,
                out nodesVisited);
        }
        if (depth < 1) depth = 1;
        if (limit < 1) limit = 1;
        return RunPath(
            index,
            graph,
            resolver,
            target,
            scope,
            to,
            depth,
            limit,
            json,
            pathKind,
            out emitted,
            out nodesVisited);
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

    // ---------- mode: path (shortest dependency path target -> to) ----------

    private static string RunPath(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver, string target, string? scope, string? to,
        int depth, int limit, bool json, string? pathKind, out int emitted, out int nodesVisited)
    {
        emitted = 0;
        nodesVisited = 0;
        if (depth < 1) depth = 1;
        if (limit < 1) limit = 1;

        string normalizedPathKind = string.IsNullOrWhiteSpace(pathKind)
            ? "call"
            : pathKind.Trim().ToLowerInvariant();
        if (normalizedPathKind is not ("call" or "dependency"))
        {
            throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                "invalid_path_kind",
                "trace path_kind must be call or dependency."));
        }

        if (string.IsNullOrWhiteSpace(to))
        {
            string message = "Usage: mode=path requires 'to' (the destination symbol to find a path to).";
            return json
                ? RenderTraceJson(ModePath, target, to, depth, limit, emitted, nodesVisited, message, "missing_to",
                    writeExtraRootProperties: writer => writer.WriteString("path_kind", normalizedPathKind))
                : message;
        }

        if (!ResolveSymbol(index, resolver, target, scope, out string fromId, out string? fromNote, out IReadOnlyList<TraceNextAction> fromNextActions))
            return json
                ? RenderTraceJson(ModePath, target, to, depth, limit, emitted, nodesVisited, fromNote!, DiagnosticCode(fromNote!),
                    nextActions: fromNextActions)
                : AppendNextActions(fromNote!, fromNextActions);
        if (!ResolveSymbol(index, resolver, to, scope: null, out string toId, out string? toNote, out IReadOnlyList<TraceNextAction> toNextActions))
            return json
                ? RenderPathJson(index, target, to, depth, limit, emitted, nodesVisited, fromId, toId: null, path: null,
                    normalizedPathKind,
                    note: toNote!, diagnosticCode: DiagnosticCode(toNote!), nextActions: toNextActions)
                : AppendNextActions(toNote!, toNextActions);

        bool broadDependencyPath = normalizedPathKind == "dependency";
        GraphPath? graphPath = graph.ShortestPathWithEvidence(
            fromId,
            toId,
            depth,
            broadDependencyPath ? static _ => true : IsCallLikePathEdge);
        if (graphPath is null)
        {
            string message =
                $"No path from '{target}' to '{to}' within {depth} hop(s) using path_kind={normalizedPathKind}.";
            IReadOnlyList<TraceNextAction> noPathNextActions =
                NoPathNextActions(target, to, depth, normalizedPathKind);
            return json
                ? RenderPathJson(index, target, to, depth, limit, emitted, nodesVisited, fromId, toId, path: null,
                    normalizedPathKind,
                    note: message, diagnosticCode: "no_path", nextActions: noPathNextActions)
                : AppendNextActions(message, noPathNextActions);
        }

        // The path is from..to inclusive (ShortestPath includes both endpoints). Hops = path.Count - 1.
        IReadOnlyList<string> path = graphPath.Nodes;
        nodesVisited = path.Count;
        if (json)
        {
            int shownCount = Math.Min(path.Count, limit);
            emitted = shownCount;
            return RenderPathJson(index, target, to, depth, limit, emitted, nodesVisited, fromId, toId, graphPath,
                normalizedPathKind,
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
            sb.Append(i == 0 ? "  " : "  -> ").Append(label);
            if (i > 0)
            {
                GraphPathEdge edge = graphPath.Edges[i - 1];
                sb.Append("  edge=").Append(edge.Kind)
                    .Append(" confidence=").Append(edge.Confidence.ToString(
                        "0.00",
                        System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" provenance=").Append(edge.Source);
            }
            sb.Append('\n');
            shown++;
            emitted++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static bool IsCallLikePathEdge(GraphNeighbour edge) =>
        edge.EdgeKind.ToLowerInvariant() is "calls" or "call" or "invokes" or "instantiates";

    private static string RefsEmptyHint(string name, string? normalizedKind) =>
        normalizedKind is null
            ? $"No extracted refs for '{name}' — the extractor may not emit refs here."
            : $"No extracted refs for '{name}' with reference_kind={normalizedKind} — try without reference_kind.";

    private static string RunRefs(
        ISymbolLookupIndex index, SmartTargetResolver resolver, string target, string? scope,
        int depth, int limit, bool json, string? referenceKind, bool includeDefinition,
        Func<IndexedSymbol, ReferenceEvidenceQuery, ReferenceEvidenceSet>? readReferenceEvidence,
        string workspaceId,
        ReferenceEvidenceSnapshot? snapshot,
        string? continuation,
        out int emitted, out int nodesVisited)
    {
        emitted = 0;
        nodesVisited = 0;
        if (depth < 1)
            depth = 1;
        if (limit < 1)
            limit = 1;

        if (!TryNormalizeReferenceKind(referenceKind, out string? normalizedKind, out string? kindError))
        {
            throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                "invalid_reference_kind",
                kindError!));
        }

        if (readReferenceEvidence is null)
        {
            const string message = "trace mode=refs requires the workspace reference reader.";
            return json
                ? RenderRefsJson(index, target, depth, limit, emitted, nodesVisited, targetSymbol: null,
                    evidence: null, exact: [], fallback: [], normalizedKind, includeDefinition, message,
                    "refs_requires_reader")
                : message;
        }

        if (!ResolveSymbol(index, resolver, target, scope, out string seedId, out string? note, out IReadOnlyList<TraceNextAction> nextActions))
            return json
                ? RenderRefsJson(index, target, depth, limit, emitted, nodesVisited, targetSymbol: null,
                    evidence: null, exact: [], fallback: [], normalizedKind, includeDefinition, note!,
                    DiagnosticCode(note!), nextActions)
                : AppendNextActions(note!, nextActions);

        IndexedSymbol? targetSymbol = index.FindBySymbolId(seedId);
        if (targetSymbol is null)
        {
            const string message = "trace refs could not load the resolved target symbol.";
            return json
                ? RenderRefsJson(index, target, depth, limit, emitted, nodesVisited, targetSymbol: null,
                    evidence: null, exact: [], fallback: [], normalizedKind, includeDefinition, message,
                    "target_symbol_missing")
                : message;
        }

        ReferenceKind? kind = normalizedKind is null
            ? null
            : ReferenceEvidenceReader.NormalizeKind(normalizedKind);
        var populationQuery = new ReferenceEvidenceQuery(
            new ReferenceEvidenceBounds(limit, limit),
            kind,
            0,
            0);
        ReferenceEvidenceSet evidence = readReferenceEvidence(targetSymbol, populationQuery);
        ReferenceEvidence[] exactPopulation = evidence.Exact
            .Where(reference => kind is null || reference.Kind == kind)
            .Take(limit)
            .ToArray();
        ReferenceEvidence[] fallbackPopulation = evidence.Fallback
            .Where(reference => kind is null || reference.Kind == kind)
            .Take(Math.Max(0, limit - exactPopulation.Length))
            .ToArray();
        string populationFingerprint = ReferencePopulationFingerprint(
            exactPopulation,
            fallbackPopulation,
            evidence.Coverage);
        // trace-json-v1 §continuation: a ref cursor is bound to the artifact GENERATION that issued it, so a
        // token minted before a full-rebuild promote is refused rather than silently re-resolved against the
        // replaced index. Deliberately NOT bound to revision: the population fingerprint already invalidates a
        // token whose own reference rows moved, so an unrelated edit elsewhere must not kill in-flight cursors.
        string requestFingerprint = FingerprintFields(
            targetSymbol.SymbolId,
            normalizedKind ?? "all",
            includeDefinition ? "definition" : "no-definition",
            limit.ToString(CultureInfo.InvariantCulture),
            snapshot?.ArtifactId ?? "no-artifact");
        var continuationIdentity = new ToolPopulationContinuationIdentity(
            "trace_refs",
            workspaceId,
            populationFingerprint,
            requestFingerprint);
        var cursor = string.IsNullOrWhiteSpace(continuation)
            ? new ToolPopulationContinuationCursor(0)
            : ToolOutputBudget.DecodePopulationCursor(continuation, continuationIdentity);

        int consumed = cursor.Offset;
        if (consumed >= limit)
        {
            throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                "continuation_offset_invalid",
                "Reference continuation has already reached the requested limit."));
        }
        int pageSize = Math.Min(24, limit - consumed);
        int exactStart = Math.Min(consumed, exactPopulation.Length);
        int fallbackStart = Math.Max(0, consumed - exactPopulation.Length);
        ReferenceEvidence[] exact = exactPopulation
            .Skip(exactStart)
            .Take(pageSize)
            .ToArray();
        ReferenceEvidence[] fallback = fallbackPopulation
            .Skip(fallbackStart)
            .Take(Math.Max(0, pageSize - exact.Length))
            .ToArray();
        int populationCount = exactPopulation.Length + fallbackPopulation.Length;
        bool pageHadReferences = exact.Length + fallback.Length > 0;
        while (exact.Length + fallback.Length > 0)
        {
            int candidateNextOffset = consumed + exact.Length + fallback.Length;
            int candidateNextExactOffset = Math.Min(candidateNextOffset, exactPopulation.Length);
            int candidateNextFallbackOffset = Math.Max(0, candidateNextOffset - exactPopulation.Length);
            bool candidateMoreExact = evidence.Coverage.ExactAvailable > candidateNextExactOffset;
            bool candidateMoreFallback =
                evidence.Coverage.FallbackStatus == ReferenceFallbackStatus.Available &&
                evidence.Coverage.FallbackAvailable > candidateNextFallbackOffset;
            string? candidateContinuation =
                candidateNextOffset < populationCount
                    ? ToolOutputBudget.EncodePopulationCursor(
                        continuationIdentity,
                        new ToolPopulationContinuationCursor(candidateNextOffset))
                    : null;
            var candidateCoverage = evidence.Coverage with
            {
                ExactReturned = exact.Length,
                FallbackReturned = fallback.Length,
                ExactTruncated = candidateMoreExact,
                FallbackTruncated = candidateMoreFallback,
            };
            bool candidateFallbackSuppressed =
                evidence.Coverage.FallbackStatus == ReferenceFallbackStatus.SuppressedAmbiguousName;
            int candidateServableAvailable =
                evidence.Coverage.ExactAvailable +
                (evidence.Coverage.FallbackStatus == ReferenceFallbackStatus.Available
                    ? evidence.Coverage.FallbackAvailable
                    : 0);
            bool candidateLimitTruncated =
                candidateContinuation is null &&
                (candidateMoreExact ||
                 candidateMoreFallback ||
                 consumed + exact.Length + fallback.Length <
                 candidateServableAvailable);
            string? candidateNote = candidateContinuation is not null
                ? "reference page truncated by the 12 KiB output budget; continue with the returned token."
                : candidateLimitTruncated
                    ? LimitTruncatedNote(candidateServableAvailable)
                    : candidateFallbackSuppressed
                        ? FallbackSuppressionNote(evidence.Coverage)
                        : null;
            IReadOnlyList<TraceNextAction> candidateNextActions = candidateLimitTruncated
                ? RefsLimitTruncatedNextActions(target, normalizedKind, candidateServableAvailable)
                : [];
            string? candidateDiagnosticCode = candidateContinuation is not null
                ? "output_page_truncated"
                : candidateLimitTruncated
                    ? "limit_truncated"
                    : candidateFallbackSuppressed
                        ? "fallback_suppressed_ambiguous_name"
                        : null;
            string candidateJson = RenderRefsJson(
                index,
                target,
                depth,
                limit,
                exact.Length + fallback.Length,
                evidence.Coverage.ExactAvailable + evidence.Coverage.FallbackAvailable,
                targetSymbol,
                evidence with { Coverage = candidateCoverage },
                exact,
                fallback,
                normalizedKind,
                includeDefinition,
                candidateNote,
                candidateDiagnosticCode,
                candidateNextActions,
                continuation: candidateContinuation);
            if (Encoding.UTF8.GetByteCount(candidateJson) <= MaxReferencePageBytes)
                break;
            if (fallback.Length > 0)
                fallback = fallback[..^1];
            else
                exact = exact[..^1];
        }
        if (pageHadReferences && exact.Length + fallback.Length == 0)
        {
            throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                "reference_item_too_large",
                "A single reference row exceeds the 12 KiB output budget."));
        }
        int nextOffset = consumed + exact.Length + fallback.Length;
        int nextExactOffset = Math.Min(nextOffset, exactPopulation.Length);
        int nextFallbackOffset = Math.Max(0, nextOffset - exactPopulation.Length);
        bool moreExact = evidence.Coverage.ExactAvailable > nextExactOffset;
        bool moreFallback =
            evidence.Coverage.FallbackStatus == ReferenceFallbackStatus.Available &&
            evidence.Coverage.FallbackAvailable > nextFallbackOffset;
        string? nextContinuation =
            nextOffset < populationCount
                ? ToolOutputBudget.EncodePopulationCursor(
                    continuationIdentity,
                    new ToolPopulationContinuationCursor(nextOffset))
                : null;
        var displayCoverage = evidence.Coverage with
        {
            ExactReturned = exact.Length,
            FallbackReturned = fallback.Length,
            ExactTruncated = moreExact,
            FallbackTruncated = moreFallback,
        };
        evidence = evidence with { Coverage = displayCoverage };
        nodesVisited = evidence.Coverage.ExactAvailable + evidence.Coverage.FallbackAvailable;
        emitted = exact.Length + fallback.Length;

        string? resultNote = null;
        string? diagnosticCode = null;
        IReadOnlyList<TraceNextAction> resultNextActions = [];
        if (emitted == 0)
        {
            resultNote = RefsEmptyHint(targetSymbol.Name, normalizedKind);
            if (evidence.Coverage.FallbackStatus == ReferenceFallbackStatus.SuppressedAmbiguousName)
                resultNote += " " + FallbackSuppressionNote(evidence.Coverage);
            diagnosticCode = "no_references";
            resultNextActions = RefsEmptyNextActions(targetSymbol.Name);
        }
        else if (nextContinuation is not null)
        {
            resultNote = "reference page truncated by the 12 KiB output budget; continue with the returned token.";
            diagnosticCode = "output_page_truncated";
        }
        else if (evidence.Coverage.FallbackStatus == ReferenceFallbackStatus.SuppressedAmbiguousName)
        {
            resultNote = FallbackSuppressionNote(evidence.Coverage);
            diagnosticCode = "fallback_suppressed_ambiguous_name";
        }
        else if (evidence.Coverage.ExactTruncated ||
                 evidence.Coverage.FallbackTruncated ||
                 consumed + emitted <
                 evidence.Coverage.ExactAvailable +
                 (evidence.Coverage.FallbackStatus == ReferenceFallbackStatus.Available
                     ? evidence.Coverage.FallbackAvailable
                     : 0))
        {
            int reachableLimit =
                evidence.Coverage.ExactAvailable +
                (evidence.Coverage.FallbackStatus == ReferenceFallbackStatus.Available
                    ? evidence.Coverage.FallbackAvailable
                    : 0);
            resultNote = LimitTruncatedNote(reachableLimit);
            diagnosticCode = "limit_truncated";
            resultNextActions = RefsLimitTruncatedNextActions(target, normalizedKind, reachableLimit);
        }

        if (json)
            return RenderRefsJson(index, target, depth, limit, emitted, nodesVisited, targetSymbol, evidence,
                exact, fallback, normalizedKind, includeDefinition, resultNote, diagnosticCode,
                resultNextActions, nextContinuation);

        var sb = new StringBuilder();
        sb.Append("# trace refs ").Append(targetSymbol.Name)
          .Append(" (").Append(emitted).Append(" reference(s), exact=")
          .Append(exact.Length).Append(", fallback=").Append(fallback.Length);
        if (normalizedKind is not null)
            sb.Append(", kind=").Append(normalizedKind);
        sb.Append(")\n");

        if (includeDefinition)
        {
            sb.Append("definition:\n")
              .Append("  ")
              .Append(targetSymbol.Name).Append("  ")
              .Append(targetSymbol.Kind).Append("  ")
              .Append(targetSymbol.FilePath).Append(':').Append(targetSymbol.StartLine)
              .Append('\n');
        }

        if (emitted == 0)
        {
            sb.Append(resultNote);
            AppendNextActions(sb, resultNextActions);
        }
        else
        {
            if (exact.Length > 0)
            {
                sb.Append("exact:\n");
                foreach (ReferenceEvidence reference in exact)
                    sb.Append("  ").Append(ReferenceLine(index, reference)).Append('\n');
            }
            if (fallback.Length > 0)
            {
                sb.Append("fallback (unresolved):\n");
                foreach (ReferenceEvidence reference in fallback)
                    sb.Append("  ").Append(ReferenceLine(index, reference)).Append('\n');
            }
            if (resultNote is not null)
                sb.Append(resultNote).Append('\n');
            if (nextContinuation is not null)
            {
                sb.Append(NextStepHint.Render(
                    $"trace target=\"{targetSymbol.SymbolId}\" mode=refs limit={limit}" +
                    (normalizedKind is null ? "" : $" reference_kind={normalizedKind}") +
                    (includeDefinition ? "" : " include_definition=false") +
                    $" workspace_id=\"{EscapeShellishArgument(workspaceId)}\"" +
                    $" continuation=\"{EscapeShellishArgument(nextContinuation)}\"",
                    "continue exact/fallback reference evidence")).Append('\n');
            }
            else if (!targetSymbol.IsTest)
                sb.Append(NextStepHint.Render($"impact target=\"{targetSymbol.Name}\"", "before editing")).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string FallbackSuppressionNote(ReferenceEvidenceCoverage coverage) =>
        coverage.FallbackAvailable > 0
            ? "Unresolved same-name matches were suppressed because the target name is ambiguous."
            : "Unresolved same-name fallback is disabled because the target name is ambiguous.";

    private static string ReferenceLine(ISymbolLookupIndex index, ReferenceEvidence reference)
    {
        var sb = new StringBuilder();
        sb.Append(reference.FilePath);
        if (reference.StartLine is { } startLine)
            sb.Append(':').Append(startLine);
        sb.Append("  ").Append(reference.SourceKind);
        if (reference.ContainingSymbolId is { } containingId &&
            index.FindBySymbolId(containingId) is { } containing)
            sb.Append("  in=").Append(containing.Name);
        sb.Append("  [").Append(reference.ResolutionStatus == ReferenceResolutionStatus.Exact ? "exact" : "fallback")
              .Append(" source=").Append(EvidenceSourceLabel(reference.Source))
              .Append(" site=").Append(reference.ReferenceSiteId)
              .Append(" provenance=").Append(reference.SiteProvenance)
              .Append(" confidence=").Append(reference.Confidence.ToString("0.00", CultureInfo.InvariantCulture));
        if (reference.ResolutionTier is { } tier)
            sb.Append(" tier=").Append(tier);
        sb.Append(']');
        return sb.ToString();
    }

    private static string EvidenceSourceLabel(ReferenceEvidenceSource source) => source switch
    {
        ReferenceEvidenceSource.IdentifierDirect => "identifier_direct",
        ReferenceEvidenceSource.IdentifierResolution => "identifier_resolution",
        ReferenceEvidenceSource.Relationship => "relationship",
        ReferenceEvidenceSource.PendingResolution => "pending_resolution",
        ReferenceEvidenceSource.NameFallback => "name_fallback",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    private static bool TryNormalizeReferenceKind(string? referenceKind, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(referenceKind))
            return true;

        string candidate = referenceKind.Trim();
        if (!KnownReferenceKinds.Contains(candidate))
        {
            error = ReferenceKindUsage;
            return false;
        }

        normalized = candidate.ToLowerInvariant();
        return true;
    }

    // ---------- mode: bridge (cross-language scored chain) ----------

    private static string RunBridge(
        MillerRepositoryIndex index, SmartTargetResolver resolver, string target, string? scope,
        int depth, int limit, bool fullFormat, bool json, out int emitted, out int nodesVisited)
    {
        emitted = 0;
        nodesVisited = 0;

        if (!ResolveBridgeStart(index, resolver, target, scope, out string startId, out string? routeFilter, out string? note, out IReadOnlyList<TraceNextAction> nextActions))
        {
            if (target.Contains('/', StringComparison.Ordinal) &&
                TryBuildRouteDiagnostic(index.BridgeGraph, target, out var routeDiagnostic))
            {
                IReadOnlyList<TraceNextAction> routeNextActions = BridgeFallbackNextActions(target, index.BridgeGraph.CapabilityReport);
                return json
                    ? RenderBridgeJson(index.BridgeGraph, target, to: null, depth, limit, emitted, nodesVisited, startId: null,
                        edges: [], routeDiagnostic.Message, routeDiagnostic.Code, routeNextActions)
                    : AppendNextActions(routeDiagnostic.Message, routeNextActions);
            }

            if (nextActions.Count == 0)
                nextActions = BridgeFallbackNextActions(target, index.BridgeGraph.CapabilityReport);
            return json
                ? RenderBridgeJson(index.BridgeGraph, target, to: null, depth, limit, emitted, nodesVisited, startId: null,
                    edges: [], note!, DiagnosticCode(note!), nextActions)
                : AppendNextActions(note!, nextActions);
        }

        // A symbol with no incident bridge edges is not on any cross-language thread — whether it is absent from the
        // bridge node lookup entirely or present but edge-less, the honest answer is the same. Incident subsumes both.
        if (index.BridgeGraph.Incident(startId).Count == 0)
        {
            if (routeFilter is not null && TryBuildRouteDiagnostic(index.BridgeGraph, routeFilter, out var routeDiagnostic))
            {
                IReadOnlyList<TraceNextAction> routeNextActions = BridgeFallbackNextActions(target, index.BridgeGraph.CapabilityReport);
                return json
                    ? RenderBridgeJson(index.BridgeGraph, target, to: null, depth, limit, emitted, nodesVisited, startId,
                        edges: [], routeDiagnostic.Message, routeDiagnostic.Code, routeNextActions)
                    : AppendNextActions(routeDiagnostic.Message, routeNextActions);
            }

            var message = new StringBuilder();
            message.Append($"'{target}' is not on a cross-language bridge. trace bridge follows DTO/entity/table/route links; ")
              .Append("this symbol has none.");
            IReadOnlyList<TraceNextAction> bridgeNextActions = BridgeFallbackNextActions(target, index.BridgeGraph.CapabilityReport);
            AppendNextActions(message, bridgeNextActions);
            AppendBridgeCapabilityStatus(message, index.BridgeGraph.CapabilityReport);
            return json
                ? RenderBridgeJson(index.BridgeGraph, target, to: null, depth, limit, emitted, nodesVisited, startId,
                    edges: [], $"'{target}' is not on a cross-language bridge. trace bridge follows DTO/entity/table/route links; this symbol has none.",
                    "not_on_bridge", bridgeNextActions)
                : message.ToString();
        }

        IReadOnlyList<ScoredEdge> edges = index.BridgeGraph.Walk(startId, depth);
        if (routeFilter is not null)
            edges = FilterRouteTargetEdges(index.BridgeGraph, startId, edges, routeFilter);
        nodesVisited = edges.Count;

        // The start has direct incident edges (checked above), so an empty Walk means depth could not reach them.
        if (edges.Count == 0)
        {
            if (routeFilter is not null && TryBuildRouteDiagnostic(index.BridgeGraph, routeFilter, out var routeDiagnostic))
            {
                IReadOnlyList<TraceNextAction> routeNextActions = BridgeFallbackNextActions(target, index.BridgeGraph.CapabilityReport);
                return json
                    ? RenderBridgeJson(index.BridgeGraph, target, to: null, depth, limit, emitted, nodesVisited, startId,
                        edges: [], routeDiagnostic.Message, routeDiagnostic.Code, routeNextActions)
                    : AppendNextActions(routeDiagnostic.Message, routeNextActions);
            }

            string message = $"No bridge links from '{target}' within {depth} hop(s).";
            IReadOnlyList<TraceNextAction> bridgeNextActions = BridgeFallbackNextActions(target, index.BridgeGraph.CapabilityReport);
            return json
                ? RenderBridgeJson(index.BridgeGraph, target, to: null, depth, limit, emitted, nodesVisited, startId,
                    edges: [], message, "no_bridge_links", bridgeNextActions)
                : AppendNextActions(message, bridgeNextActions);
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

    private static IReadOnlyList<ScoredEdge> FilterRouteTargetEdges(
        BridgeGraph graph, string startId, IReadOnlyList<ScoredEdge> edges, string route)
    {
        var routeEdges = edges
            .Where(edge => IsRouteTargetEdge(edge.Edge, route))
            .ToList();
        if (routeEdges.Count == 0)
            return Array.Empty<ScoredEdge>();

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        allowed.Add(startId);
        foreach (var edge in routeEdges)
        {
            string? sourceId = BridgeGraph.NodeIdOf(edge.Edge.SourceRef, edge.Edge.Kind, EndpointSide.Source);
            string? targetId = BridgeGraph.NodeIdOf(edge.Edge.TargetRef, edge.Edge.Kind, EndpointSide.Target);
            if (sourceId is not null)
                allowed.Add(sourceId);
            if (targetId is not null)
                allowed.Add(targetId);
        }

        var filtered = new List<ScoredEdge>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var edge in edges)
            {
                string? sourceId = BridgeGraph.NodeIdOf(edge.Edge.SourceRef, edge.Edge.Kind, EndpointSide.Source);
                string? targetId = BridgeGraph.NodeIdOf(edge.Edge.TargetRef, edge.Edge.Kind, EndpointSide.Target);
                if (sourceId is null || targetId is null)
                    continue;

                bool routeEdge = IsRouteTargetEdge(edge.Edge, route);
                bool downstream = !IsRouteTargetKind(edge.Edge.Kind) && (allowed.Contains(sourceId) || allowed.Contains(targetId));
                if (!routeEdge && !downstream)
                    continue;

                var signature = $"{edge.Edge.Kind}:{sourceId}->{targetId}:{edge.Edge.SourceRef.Display}:{edge.Edge.TargetRef.Display}";
                if (seen.Add(signature))
                    filtered.Add(edge);
                if (allowed.Add(sourceId))
                    changed = true;
                if (allowed.Add(targetId))
                    changed = true;
            }
        }
        while (changed);

        return filtered;
    }

    private static bool TryBuildRouteDiagnostic(BridgeGraph graph, string route, out BridgeRouteDiagnostic diagnostic)
    {
        string targetRoute = NormalizeRouteDisplay(route);
        string targetRouteKey = RouteNormalizer.FromClientCall("fetch", route).Route;
        var frontendRoutes = ObservedRoutes(graph, BridgeNodeKind.TsType).ToArray();
        var backendRoutes = ObservedRoutes(graph, BridgeNodeKind.Endpoint).ToArray();
        var fileRoutes = ObservedRoutes(graph, BridgeNodeKind.FileRoute).ToArray();

        bool frontendPresent = frontendRoutes.Any(r => string.Equals(r.Normalized, targetRouteKey, StringComparison.Ordinal));
        bool backendPresent = backendRoutes.Any(r => string.Equals(r.Normalized, targetRouteKey, StringComparison.Ordinal));
        bool fileRoutePresent = fileRoutes.Any(r => string.Equals(r.Normalized, targetRouteKey, StringComparison.Ordinal));
        bool hasFileRouteEvidence = HasFileRouteProviderEvidence(graph);
        bool hasApiDefinitionEvidence = FileRouteDiagnosticProviders.Any(provider =>
            provider.DefinitionNodeKind == BridgeNodeKind.Endpoint && ProviderParticipates(graph, provider));
        if (fileRoutePresent ||
            (hasFileRouteEvidence &&
             (frontendPresent || fileRoutes.Length > 0 || (hasApiDefinitionEvidence && backendRoutes.Length > 0))))
        {
            if (TryBuildFileRouteDiagnostic(
                    graph,
                    targetRoute,
                    targetRouteKey,
                    frontendRoutes,
                    fileRoutes,
                    backendRoutes,
                    out diagnostic))
                return true;
        }

        if (!frontendPresent && frontendRoutes.Length == 0 && backendRoutes.Length == 0)
        {
            diagnostic = new BridgeRouteDiagnostic("route_not_observed", $"no frontend or backend route facts observed for {targetRoute}.");
            return false;
        }

        if (frontendPresent && !backendPresent)
        {
            string observedBackend = FormatObservedRoutes(backendRoutes);
            diagnostic = new BridgeRouteDiagnostic(
                "route_no_backend_match",
                $"frontend route fact exists: {targetRoute}; no matching backend route fact. observed backend routes: {observedBackend}");
            return true;
        }

        if (!frontendPresent && backendPresent)
        {
            string observedFrontend = FormatObservedRoutes(frontendRoutes);
            diagnostic = new BridgeRouteDiagnostic(
                "route_no_frontend_match",
                $"backend route fact exists: {targetRoute}; no matching frontend route fact. observed frontend routes: {observedFrontend}");
            return true;
        }

        diagnostic = new BridgeRouteDiagnostic(
            "route_no_bridge_link",
            $"frontend and backend route facts exist for {targetRoute}, but no bridge link was built for that route.");
        return true;
    }

    /// <summary>
    /// The per-provider route diagnostic table, scoped by observation-node provenance: a row's reference and
    /// definition sets contain only the routes THAT provider observed (<see cref="BridgeGraph.ObservationProviders"/>),
    /// so a navigation row can never narrate a client request and an API row can never claim another framework's
    /// endpoint. Claims from every participating row are collected first — row order never decides. A single row
    /// whose definition side matches the target wins (the frame with matching definitions is the honest story); a
    /// single reference-only claim wins only when NO pooled definition fact matches the target (otherwise another
    /// frame owns the definition and this row's "no matching definition" noun would lie); zero or conflicting
    /// claims fall back to the pooled generic diagnostics in <see cref="TryBuildRouteDiagnostic"/>. A graph built
    /// without provenance (hand-assembled node maps) keeps the pooled per-row inputs.
    /// </summary>
    private static bool TryBuildFileRouteDiagnostic(
        BridgeGraph graph,
        string targetRoute,
        string targetRouteKey,
        IReadOnlyList<(string Normalized, string Display)> pooledReferences,
        IReadOnlyList<(string Normalized, string Display)> pooledFileRoutes,
        IReadOnlyList<(string Normalized, string Display)> pooledBackendRoutes,
        out BridgeRouteDiagnostic diagnostic)
    {
        bool scoped = graph.HasObservationProvenance;
        bool pooledDefinitionMatchesTarget =
            AnyRouteMatches(pooledFileRoutes, targetRoute, targetRouteKey) ||
            AnyRouteMatches(pooledBackendRoutes, targetRoute, targetRouteKey);

        var definitionMatchClaims = new List<BridgeRouteDiagnostic>();
        var referenceOnlyClaims = new List<BridgeRouteDiagnostic>();

        foreach (var provider in FileRouteDiagnosticProviders)
        {
            if (!ProviderParticipates(graph, provider))
                continue;

            bool isApiProvider = provider.DefinitionNodeKind == BridgeNodeKind.Endpoint;
            IReadOnlyList<(string Normalized, string Display)> routeReferences = scoped
                ? ObservedRoutes(graph, BridgeNodeKind.TsType, provider.ProviderId).ToArray()
                : pooledReferences;
            IReadOnlyList<(string Normalized, string Display)> definitionRoutes = scoped
                ? ObservedRoutes(graph, provider.DefinitionNodeKind, provider.ProviderId).ToArray()
                : (isApiProvider ? pooledBackendRoutes : pooledFileRoutes);

            if (!TryBuildFrameworkFileRouteDiagnostic(
                    graph,
                    provider,
                    targetRoute,
                    routeReferencePresent: RoutePresent(routeReferences, targetRouteKey),
                    definitionPresent: RoutePresent(definitionRoutes, targetRouteKey),
                    routeReferences,
                    definitionRoutes,
                    out var claim,
                    out bool definitionMatchesTarget))
            {
                continue;
            }

            if (definitionMatchesTarget)
                definitionMatchClaims.Add(claim);
            else if (!pooledDefinitionMatchesTarget)
                referenceOnlyClaims.Add(claim);
            // else: another frame's definition fact matches the target — this row's "no matching definition"
            // noun cannot tell the honest story; the pooled generic diagnostics can.
        }

        if (definitionMatchClaims.Count == 1)
        {
            diagnostic = definitionMatchClaims[0];
            return true;
        }

        if (definitionMatchClaims.Count == 0 && referenceOnlyClaims.Count == 1)
        {
            diagnostic = referenceOnlyClaims[0];
            return true;
        }

        diagnostic = new BridgeRouteDiagnostic(
            "file_route_not_observed",
            $"no framework route reference or file route facts observed for {targetRoute}.");
        return false;
    }

    private static bool TryBuildFrameworkFileRouteDiagnostic(
        BridgeGraph graph,
        FileRouteDiagnosticProvider provider,
        string targetRoute,
        bool routeReferencePresent,
        bool definitionPresent,
        IReadOnlyList<(string Normalized, string Display)> routeReferences,
        IReadOnlyList<(string Normalized, string Display)> definitionRoutes,
        out BridgeRouteDiagnostic diagnostic,
        out bool definitionMatchesTarget)
    {
        var matchingDefinitions = definitionRoutes
            .Where(route => Miller.Core.Resolver.FileRouteMatcher.Matches(targetRoute, route.Display))
            .ToArray();
        definitionMatchesTarget = definitionPresent || matchingDefinitions.Length > 0;

        int ambiguousMatches = FileRouteEvidenceCount(graph, provider.ProviderId, "ambiguousMatches");
        if (routeReferencePresent && ambiguousMatches > 0 && matchingDefinitions.Length > 0)
        {
            diagnostic = new BridgeRouteDiagnostic(
                provider.DiagnosticCode("route_ambiguous_file_match"),
                $"{provider.DisplayName} {provider.ReferenceNoun} exists: {targetRoute}; multiple matching {provider.TargetFactName} facts were observed, so no {provider.EdgeNoun} was built. observed {provider.TargetFactName}s: {FormatObservedRoutes(matchingDefinitions)}");
            return true;
        }

        if (routeReferencePresent && !definitionMatchesTarget)
        {
            diagnostic = new BridgeRouteDiagnostic(
                provider.DiagnosticCode("route_no_file_match"),
                $"{provider.DisplayName} {provider.ReferenceNoun} exists: {targetRoute}; no matching {provider.TargetFactName} fact. observed {provider.TargetFactName}s: {FormatObservedRoutes(definitionRoutes)}");
            return true;
        }

        if (!routeReferencePresent && definitionMatchesTarget)
        {
            diagnostic = new BridgeRouteDiagnostic(
                provider.DiagnosticCode("route_no_reference_match"),
                $"{provider.DisplayName} {provider.TargetFactName} exists: {targetRoute}; no matching {provider.ReferenceNoun} fact. observed {provider.ReferenceNoun}s: {FormatObservedRoutes(routeReferences)}");
            return true;
        }

        if (routeReferencePresent && definitionMatchesTarget)
        {
            diagnostic = new BridgeRouteDiagnostic(
                provider.DiagnosticCode("route_no_bridge_link"),
                $"{provider.DisplayName} {provider.ReferenceNoun} and {provider.TargetFactName} facts exist for {targetRoute}, but no {provider.EdgeNoun} was built for that route.");
            return true;
        }

        diagnostic = new BridgeRouteDiagnostic(
            provider.DiagnosticCode("route_not_observed"),
            $"no {provider.DisplayName} {provider.ReferenceNoun} or {provider.TargetFactName} facts observed for {targetRoute}.");
        return false;
    }

    private static readonly FileRouteDiagnosticProvider[] FileRouteDiagnosticProviders =
    [
        new("nextjs", "Next.js", "route reference", "file route", BridgeNodeKind.FileRoute, "fileRoutes", "navigation edge"),
        new("nextjs-api", "Next.js", "client request", "route handler", BridgeNodeKind.Endpoint, "routeHandlers", "route edge"),
        new("nuxt", "Nuxt", "route reference", "file route", BridgeNodeKind.FileRoute, "fileRoutes", "navigation edge"),
        new("nuxt-api", "Nuxt", "client request", "server route", BridgeNodeKind.Endpoint, "serverRoutes", "route edge"),
        new("vue", "Vue", "route reference", "route definition", BridgeNodeKind.FileRoute, "fileRoutes", "navigation edge"),
        new("react", "React", "route reference", "route definition", BridgeNodeKind.FileRoute, "fileRoutes", "navigation edge"),
        // The backend HTTP boundary provider mirrors the nextjs-api/nuxt-api rows (Endpoint definition kind, a
        // client-request reference noun). ProviderParticipates therefore gates it on backend-http.routeFacts > 0,
        // which is correct: the client-request family is shared with dotnet-web/nextjs-api/nuxt-api, so gating on it
        // would make backend-http "participate" in a non-backend repo. Limitation: a niche Rails-only repo where
        // every route is expanded rather than a direct rails.route fact has routeFacts == 0, so this row does not
        // participate for it — the generic pooled route diagnostics still cover that case. We intentionally do NOT
        // OR composedRoutes/expandedResourceRoutes into the single-key participation gate.
        // TargetFactName is the fact SUBJECT ("route"), not "route fact": the diagnostic templates append " fact"/
        // " facts"/"s" to it (e.g. "no matching route fact", "observed routes:"), matching the existing rows'
        // "file route"/"route handler"/"server route" convention. DefinitionEvidenceName stays the "routeFacts" key.
        new("backend-http", "Backend", "client request", "route", BridgeNodeKind.Endpoint, "routeFacts", "route edge"),
    ];

    private static bool HasFileRouteProviderEvidence(BridgeGraph graph) =>
        FileRouteDiagnosticProviders.Any(provider => ProviderParticipates(graph, provider));

    // A navigation provider participates in route diagnostics on any of its evidence (its fact families are
    // provider-specific). An API provider participates only with definition-side (handler) evidence — its
    // client-request evidence is shared with the sibling API provider AND appears in repos that are neither
    // Next.js nor Nuxt (dotnet-web consumes the same facts), where the generic backend diagnostics stay honest.
    private static bool ProviderParticipates(BridgeGraph graph, FileRouteDiagnosticProvider provider) =>
        provider.DefinitionNodeKind == BridgeNodeKind.Endpoint
            ? FileRouteEvidenceCount(graph, provider.ProviderId, provider.DefinitionEvidenceName) > 0
            : HasFileRouteProviderEvidence(graph, provider.ProviderId);

    private static bool HasFileRouteProviderEvidence(BridgeGraph graph, string providerId) =>
        graph.CapabilityReport.ActiveProviders.Any(provider => string.Equals(provider, providerId, StringComparison.Ordinal)) ||
        graph.CapabilityReport.EvidenceCounts.Any(item =>
            item.Value > 0 && item.Key.StartsWith(providerId + ".", StringComparison.Ordinal));

    private static int FileRouteEvidenceCount(BridgeGraph graph, string providerId, string name) =>
        graph.CapabilityReport.EvidenceCounts.TryGetValue(providerId + "." + name, out int count) ? count : 0;

    private static bool RoutePresent(IReadOnlyList<(string Normalized, string Display)> routes, string targetRouteKey) =>
        routes.Any(route => string.Equals(route.Normalized, targetRouteKey, StringComparison.Ordinal));

    // True when any observed route matches the target exactly (normalized) or as a dynamic-segment file
    // route/handler template — the same match semantics the per-provider rows use for their definition side.
    private static bool AnyRouteMatches(
        IReadOnlyList<(string Normalized, string Display)> routes, string targetRoute, string targetRouteKey) =>
        RoutePresent(routes, targetRouteKey) ||
        routes.Any(route => Miller.Core.Resolver.FileRouteMatcher.Matches(targetRoute, route.Display));

    // Pooled when providerId is null; scoped to one provider's observation-node provenance otherwise.
    private static IEnumerable<(string Normalized, string Display)> ObservedRoutes(
        BridgeGraph graph, BridgeNodeKind kind, string? providerId = null) =>
        graph.Nodes.Values
            .Where(node => node.Kind == kind &&
                (providerId is null || graph.ObservationProviders(node.Id).Contains(providerId, StringComparer.Ordinal)))
            .Select(NodeRouteDisplay)
            .Where(route => route is not null)
            .Select(route => (Normalized: RouteNormalizer.FromClientCall("fetch", route!).Route, Display: NormalizeRouteDisplay(route!)))
            .Where(route => route.Normalized.Length > 0)
            .GroupBy(route => route.Normalized, StringComparer.Ordinal)
            .Select(group => group.OrderBy(route => route.Display, StringComparer.Ordinal).First())
            .OrderBy(route => route.Display, StringComparer.Ordinal);

    private static string? NodeRouteDisplay(BridgeNode node) =>
        node.Kind switch
        {
            BridgeNodeKind.TsType when node.Display.Contains('/', StringComparison.Ordinal) => node.Display,
            BridgeNodeKind.Endpoint => EndpointRouteDisplay(node.Display),
            BridgeNodeKind.FileRoute when node.Display.Contains('/', StringComparison.Ordinal) => node.Display,
            _ => null,
        };

    private static string? EndpointRouteDisplay(string display)
    {
        var trimmed = display.Trim();
        if (trimmed.Length == 0)
            return null;

        int space = trimmed.IndexOf(' ');
        if (space >= 0 && IsHttpVerb(trimmed[..space]))
            return trimmed[(space + 1)..].Trim();

        return trimmed.Contains('/', StringComparison.Ordinal) ? trimmed : null;
    }

    private static bool IsHttpVerb(string value) =>
        string.Equals(value, "GET", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "POST", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "PUT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "PATCH", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "DELETE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "HEAD", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "OPTIONS", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRouteDisplay(string route)
    {
        var normalized = RouteNormalizer.FromClientCall("fetch", route).Route;
        if (normalized.Length == 0)
            return route;
        return normalized.StartsWith("/", StringComparison.Ordinal) ? normalized : "/" + normalized;
    }

    private static string FormatObservedRoutes(IReadOnlyList<(string Normalized, string Display)> routes) =>
        routes.Count == 0
            ? "none"
            : string.Join(", ", routes.Take(5).Select(route => route.Display)) + (routes.Count > 5 ? $", +{routes.Count - 5} more" : string.Empty);

    private static bool ResolveBridgeStart(
        MillerRepositoryIndex index, SmartTargetResolver resolver, string target, string? scope,
        out string startId, out string? routeFilter, out string? note, out IReadOnlyList<TraceNextAction> nextActions)
    {
        startId = string.Empty;
        routeFilter = null;
        note = null;
        nextActions = Array.Empty<TraceNextAction>();

        if (string.IsNullOrWhiteSpace(target))
        {
            note = "trace: a target symbol is required.";
            return false;
        }

        if (TryResolveBridgeRouteTarget(index.BridgeGraph, target, out startId, out routeFilter, out note))
            return true;
        if (note is not null)
            return false;

        if (TryResolveSyntheticBridgeNode(index.BridgeGraph, target, out startId, out routeFilter))
            return true;

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
                nextActions = AmbiguousTargetNextActions(target, cands.Matches);
                note = RenderCandidatesNote(target, cands.Matches);
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

    private static bool TryResolveSyntheticBridgeNode(BridgeGraph graph, string target, out string startId, out string? routeFilter)
    {
        startId = string.Empty;
        routeFilter = null;

        var route = RouteNormalizer.FromClientCall("fetch", target).Route;
        if (route.Length > 0)
        {
            string routeId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, route);
            if (graph.Contains(routeId))
            {
                startId = routeId;
                routeFilter = route;
                return true;
            }

            if (TryResolveObservedRouteNode(graph, route, BridgeNodeKind.TsType, out startId))
            {
                routeFilter = route;
                return true;
            }

            if (TryResolveObservedRouteNode(graph, route, BridgeNodeKind.Endpoint, out startId))
            {
                routeFilter = route;
                return true;
            }

            if (TryResolveObservedRouteNode(graph, route, BridgeNodeKind.FileRoute, out startId))
            {
                routeFilter = route;
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

    private static bool TryResolveObservedRouteNode(
        BridgeGraph graph, string route, BridgeNodeKind kind, out string startId)
    {
        startId = graph.Nodes.Values
            .Where(node => node.Kind == kind)
            .Where(node =>
            {
                var nodeRoute = NodeRouteDisplay(node);
                return nodeRoute is not null &&
                       string.Equals(RouteNormalizer.FromClientCall("fetch", nodeRoute).Route, route, StringComparison.Ordinal);
            })
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .Select(node => node.Id)
            .FirstOrDefault() ?? string.Empty;
        return startId.Length > 0;
    }

    private static bool TryResolveBridgeRouteTarget(
        BridgeGraph graph, string target, out string startId, out string? routeFilter, out string? note)
    {
        startId = string.Empty;
        routeFilter = null;
        note = null;

        if (!target.Contains('/', StringComparison.Ordinal))
            return false;

        var route = RouteNormalizer.FromClientCall("fetch", target).Route;
        if (route.Length == 0)
            return false;

        var starts = graph.Edges
            .Where(e => IsRouteTargetEdge(e.Edge, route))
            .Select(e => BridgeGraph.NodeIdOf(e.Edge.SourceRef, e.Edge.Kind, EndpointSide.Source))
            .Where(id => id is not null && graph.Contains(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        if (starts.Count == 1)
        {
            startId = starts[0];
            routeFilter = route;
            return true;
        }

        if (starts.Count > 1)
            note = RenderBridgeRouteCandidatesNote(graph, route, starts);

        return false;
    }

    private static bool IsRouteTargetEdge(CandidateEdge edge, string route) =>
        IsRouteTargetKind(edge.Kind) &&
        (RouteDisplayMatches(edge.SourceRef.Display, route) || RouteDisplayMatches(edge.TargetRef.Display, route));

    private static bool IsRouteTargetKind(BridgeKind kind) =>
        kind is BridgeKind.Hits or BridgeKind.NavigatesTo;

    private static bool RouteDisplayMatches(string display, string route) =>
        string.Equals(display, route, StringComparison.Ordinal) ||
        string.Equals(RouteNormalizer.FromClientCall("fetch", display).Route, route, StringComparison.Ordinal);

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
        BridgeKind.NavigatesTo => "navigates_to",
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
        out string symbolId, out string? note, out IReadOnlyList<TraceNextAction> nextActions)
    {
        symbolId = string.Empty;
        note = null;
        nextActions = Array.Empty<TraceNextAction>();

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
                nextActions = AmbiguousTargetNextActions(target, cands.Matches);
                note = RenderCandidatesNote(target, cands.Matches);
                return false;

            case TargetResolution.NotFound nf:
                note = nf.RenderMessage();
                return false;

            default:
                note = "trace: unrecognized target resolution.";
                return false;
        }
    }

    private static string RenderCandidatesNote(string target, IReadOnlyList<IndexedSymbol> matches)
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
        CandidateOutput.AppendRerunExamples(sb, target, matches, supportsScope: true, command: "trace");
        return sb.ToString().TrimEnd('\n');
    }

    private static IReadOnlyList<TraceNextAction> AmbiguousTargetNextActions(string target, IReadOnlyList<IndexedSymbol> matches)
    {
        string[] paths = matches
            .Select(static match => match.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        if (paths.Length < 2)
        {
            return matches
                .Take(3)
                .Select(match => NextAction(
                    "trace",
                    $"retry with this symbol id for {match.Kind} in {match.FilePath}:{match.StartLine}",
                    ("target", match.SymbolId)))
                .ToArray();
        }

        return paths
            .Select(path => NextAction(
                "trace",
                "retry with this file scope to disambiguate the target",
                ("target", target),
                ("scope", path)))
            .ToArray();
    }

    private static IReadOnlyList<TraceNextAction> NoPathNextActions(
        string target,
        string to,
        int depth,
        string pathKind)
    {
        var actions = new List<TraceNextAction>(capacity: 4)
        {
            NextAction(
                "trace",
                "check extracted identifier references from the source endpoint",
                ("target", target),
                ("mode", ModeRefs)),
        };

        if (!string.Equals(target.Trim(), to.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            actions.Add(NextAction(
                "trace",
                "check extracted identifier references from the destination endpoint",
                ("target", to),
                ("mode", ModeRefs)));
        }

        if (depth < 3)
        {
            actions.Add(NextAction(
                "trace",
                "retry with a bounded depth bump; this does not prove a path exists",
                ("target", target),
                ("mode", ModePath),
                ("to", to),
                ("depth", (depth + 1).ToString(CultureInfo.InvariantCulture))));
        }

        if (pathKind == "call")
        {
            actions.Add(NextAction(
                "trace",
                "retry with broad dependency edges; the result is not a call path",
                ("target", target),
                ("mode", ModePath),
                ("to", to),
                ("path_kind", "dependency"),
                ("depth", depth.ToString(CultureInfo.InvariantCulture))));
        }

        actions.Add(NextAction(
            "search",
            "look for text links not represented in the graph",
            ("query", $"{target} {to}"),
            ("mode", "source")));

        return actions.Take(5).ToArray();
    }

    private static string LimitTruncatedNote(int reachableLimit) =>
        $"reference trace truncated by limit. {reachableLimit} reference(s) available — " +
        $"re-run with limit={reachableLimit.ToString(CultureInfo.InvariantCulture)} to see them all.";

    private static IReadOnlyList<TraceNextAction> RefsLimitTruncatedNextActions(
        string target,
        string? normalizedKind,
        int reachableLimit)
    {
        var args = new List<(string, string)>
        {
            ("target", target),
            ("mode", "refs"),
            ("limit", reachableLimit.ToString(CultureInfo.InvariantCulture)),
        };
        if (normalizedKind is not null)
            args.Add(("reference_kind", normalizedKind));

        return
        [
            NextAction(
                "trace",
                "raise the limit to return every available reference in one call",
                [.. args]),
            NextAction(
                "impact",
                "assess the full blast radius instead of enumerating references",
                ("target", target)),
        ];
    }

    private static IReadOnlyList<TraceNextAction> RefsEmptyNextActions(string target) =>
    [
        NextAction(
            "search",
            "look for text occurrences because extracted refs may be unavailable or incomplete",
            ("query", target),
            ("mode", "source")),
        NextAction(
            "inspect",
            "inspect callers and callees from the symbol graph",
            ("target", target),
            ("depth", "full")),
    ];

    private static IReadOnlyList<TraceNextAction> BridgeFallbackNextActions(string target, BridgeCapabilityReport? capabilityReport = null)
    {
        var actions = new List<TraceNextAction>
        {
            NextAction(
            "trace",
            "check ordinary extracted identifier references outside the bridge graph",
            ("target", target),
            ("mode", ModeRefs)),
            NextAction(
            "inspect",
            "inspect callers and callees from the symbol graph",
            ("target", target),
            ("depth", "full")),
            NextAction(
            "search",
            "look for source text links not represented in the bridge graph",
            ("query", target),
            ("mode", "source")),
        };

        if (capabilityReport is not null && HasRouteFactEvidence(capabilityReport))
        {
            actions.Add(NextAction(
                "patterns",
                "audit route structural facts consumed by bridge providers",
                ("operation", "search"),
                ("query", "route")));

            if (HasEvidence(capabilityReport, "dotnet-web.htmxCalls"))
            {
                actions.Add(NextAction(
                    "patterns",
                    "audit htmx route structural facts consumed by the dotnet-web bridge",
                    ("operation", "search"),
                    ("pattern_id", BridgeStructuralPatterns.HtmxAttribute)));
            }

            if (HasEvidence(capabilityReport, "dotnet-web.attributeRoutes"))
            {
                actions.Add(NextAction(
                    "patterns",
                    "audit ASP.NET attribute route structural facts consumed by the dotnet-web bridge",
                    ("operation", "search"),
                    ("pattern_id", BridgeStructuralPatterns.AspNetAttributeRoute)));
            }

            if (HasEvidence(capabilityReport, "dotnet-web.clientRequests") ||
                HasEvidence(capabilityReport, "nextjs-api.clientRequests") ||
                HasEvidence(capabilityReport, "nuxt-api.clientRequests") ||
                HasEvidence(capabilityReport, "backend-http.clientRequests"))
            {
                actions.Add(NextAction(
                    "patterns",
                    "audit HTTP client request structural facts consumed by bridge providers",
                    ("operation", "search"),
                    ("pattern_id", BridgeStructuralPatterns.HttpClientRequest)));
            }

            if (HasEvidence(capabilityReport, "dotnet-web.vueCalls"))
            {
                actions.Add(NextAction(
                    "patterns",
                    "audit Vue route structural facts consumed by the dotnet-web bridge",
                    ("operation", "search"),
                    ("pattern_id", BridgeStructuralPatterns.VueRouteReference)));
            }

            if (HasEvidence(capabilityReport, "dotnet-web.reactCalls"))
            {
                actions.Add(NextAction(
                    "patterns",
                    "audit React route structural facts consumed by the dotnet-web bridge",
                    ("operation", "search"),
                    ("pattern_id", BridgeStructuralPatterns.ReactRouteReference)));
            }

            if (HasEvidence(capabilityReport, "dotnet-web.nextjsCalls") ||
                HasEvidence(capabilityReport, "nextjs.routeReferences") ||
                HasEvidence(capabilityReport, "nextjs.fileRoutes") ||
                HasEvidence(capabilityReport, "nextjs-api.routeHandlers"))
            {
                actions.Add(NextAction(
                    "patterns",
                    "audit Next.js route structural facts consumed by bridge providers",
                    ("operation", "search"),
                    ("query", "nextjs")));
            }

            if (HasEvidence(capabilityReport, "dotnet-web.nuxtCalls") ||
                HasEvidence(capabilityReport, "nuxt.routeReferences") ||
                HasEvidence(capabilityReport, "nuxt.fileRoutes") ||
                HasEvidence(capabilityReport, "nuxt-api.serverRoutes"))
            {
                actions.Add(NextAction(
                    "patterns",
                    "audit Nuxt route structural facts consumed by bridge providers",
                    ("operation", "search"),
                    ("query", "nuxt")));
            }

            if (HasEvidence(capabilityReport, "backend-http.routeFacts") ||
                HasEvidence(capabilityReport, "backend-http.composedRoutes") ||
                HasEvidence(capabilityReport, "backend-http.expandedResourceRoutes"))
            {
                // The 10 backend route families don't share one pattern_id and enumerating all 16 would be noise;
                // query=route matches the *.route.* families and the generic route audit above covers the rest.
                actions.Add(NextAction(
                    "patterns",
                    "audit backend HTTP route structural facts consumed by the backend-http bridge",
                    ("operation", "search"),
                    ("query", "route")));
            }
        }

        return actions;
    }

    private static bool HasRouteFactEvidence(BridgeCapabilityReport report) =>
        HasEvidence(report, "bridge.structuralFacts") ||
        HasEvidence(report, "dotnet-web.structuralFacts") ||
        HasEvidence(report, "dotnet-web.aspnetMinimalRoutes") ||
        HasEvidence(report, "dotnet-web.attributeRoutes") ||
        HasEvidence(report, "dotnet-web.htmxCalls") ||
        HasEvidence(report, "dotnet-web.vueCalls") ||
        HasEvidence(report, "dotnet-web.reactCalls") ||
        HasEvidence(report, "dotnet-web.nextjsCalls") ||
        HasEvidence(report, "dotnet-web.nuxtCalls") ||
        HasEvidence(report, "dotnet-web.clientRequests") ||
        HasEvidence(report, "nextjs.routeReferences") ||
        HasEvidence(report, "nextjs.fileRoutes") ||
        HasEvidence(report, "nextjs-api.clientRequests") ||
        HasEvidence(report, "nextjs-api.routeHandlers") ||
        HasEvidence(report, "nuxt.routeReferences") ||
        HasEvidence(report, "nuxt.fileRoutes") ||
        HasEvidence(report, "nuxt-api.clientRequests") ||
        HasEvidence(report, "nuxt-api.serverRoutes") ||
        HasEvidence(report, "backend-http.routeFacts") ||
        HasEvidence(report, "backend-http.clientRequests") ||
        HasEvidence(report, "backend-http.composedRoutes") ||
        HasEvidence(report, "backend-http.expandedResourceRoutes");

    private static bool HasEvidence(BridgeCapabilityReport report, string key) =>
        report.EvidenceCounts.TryGetValue(key, out int value) && value > 0;

    private static TraceNextAction NextAction(string tool, string reason, params (string Key, string Value)[] args) =>
        new(tool, reason, args.Select(static arg => new KeyValuePair<string, string>(arg.Key, arg.Value)).ToArray());

    private static string AppendNextActions(string message, IReadOnlyList<TraceNextAction> actions)
    {
        if (actions.Count == 0)
            return message;

        var sb = new StringBuilder(message);
        AppendNextActions(sb, actions);
        return sb.ToString();
    }

    private static void AppendNextActions(StringBuilder sb, IReadOnlyList<TraceNextAction> actions)
    {
        if (actions.Count == 0)
            return;

        sb.Append('\n').Append("Next:");
        foreach (TraceNextAction action in actions.Take(MaxNextActions))
        {
            sb.Append('\n')
              .Append("  ")
              .Append(FormatNextActionCommand(action))
              .Append(" - ")
              .Append(action.Reason)
              .Append('.');
        }
    }

    private static string FormatNextActionCommand(TraceNextAction action)
    {
        var sb = new StringBuilder(action.Tool);
        foreach (KeyValuePair<string, string> arg in action.Args)
        {
            sb.Append(' ')
              .Append(arg.Key)
              .Append("=\"")
              .Append(EscapeShellishArgument(arg.Value))
              .Append('"');
        }

        return sb.ToString();
    }

    private static string EscapeShellishArgument(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string ReferencePopulationFingerprint(
        IReadOnlyList<ReferenceEvidence> exact,
        IReadOnlyList<ReferenceEvidence> fallback,
        ReferenceEvidenceCoverage coverage)
    {
        var fields = new List<string?>
        {
            coverage.ExactAvailable.ToString(CultureInfo.InvariantCulture),
            coverage.FallbackAvailable.ToString(CultureInfo.InvariantCulture),
            coverage.FallbackStatus.ToString(),
        };
        foreach (ReferenceEvidence reference in exact.Concat(fallback))
        {
            fields.Add(reference.TargetSymbolId);
            fields.Add(reference.ContainingSymbolId);
            fields.Add(reference.FilePath);
            fields.Add(reference.StartLine?.ToString(CultureInfo.InvariantCulture));
            fields.Add(reference.StartColumn?.ToString(CultureInfo.InvariantCulture));
            fields.Add(reference.EndLine?.ToString(CultureInfo.InvariantCulture));
            fields.Add(reference.EndColumn?.ToString(CultureInfo.InvariantCulture));
            fields.Add(reference.StartByte?.ToString(CultureInfo.InvariantCulture));
            fields.Add(reference.EndByte?.ToString(CultureInfo.InvariantCulture));
            fields.Add(reference.Kind.ToString());
            fields.Add(reference.SourceKind);
            fields.Add(reference.Source.ToString());
            fields.Add(reference.ResolutionTier?.ToString(CultureInfo.InvariantCulture));
            fields.Add(reference.Confidence.ToString("R", CultureInfo.InvariantCulture));
            fields.Add(reference.ResolutionStatus.ToString());
            fields.Add(reference.Language);
            fields.Add(reference.ReferenceSiteId);
            fields.Add(reference.IsExact ? "1" : "0");
            fields.Add(reference.SiteProvenance);
        }
        return FingerprintFields(fields.ToArray());
    }

    private static string FingerprintFields(params string?[] fields)
    {
        var canonical = new StringBuilder();
        foreach (string? field in fields)
        {
            string value = field ?? string.Empty;
            canonical.Append(Encoding.UTF8.GetByteCount(value))
                .Append(':')
                .Append(value)
                .Append(';');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static ToolDiagnosticException InvalidMode(string? mode) =>
        new(ToolDiagnostic.Refusal(
            "invalid_mode",
            $"Unknown trace mode '{mode}'. Use refs, path, or bridge."));

    // ---------- JSON rendering ----------

    private static string RenderPathJson(
        ISymbolLookupIndex index, string target, string? to, int depth, int limit, int emitted, int nodesVisited,
        string? fromId, string? toId, GraphPath? path, string pathKind, string? note, string? diagnosticCode,
        IReadOnlyList<TraceNextAction>? nextActions = null)
    {
        var ids = new List<string>();
        if (fromId is not null)
            ids.Add(fromId);
        if (toId is not null)
            ids.Add(toId);
        if (path is not null)
            ids.AddRange(path.Nodes);

        var symbolsById = SymbolLookupBatch.FindBySymbolIds(index, ids);
        symbolsById.TryGetValue(fromId ?? string.Empty, out IndexedSymbol? fromSymbol);
        symbolsById.TryGetValue(toId ?? string.Empty, out IndexedSymbol? toSymbol);
        int shownCount = path is null ? 0 : Math.Min(path.Nodes.Count, limit);

        return RenderTraceJson(ModePath, target, to, depth, limit, emitted, nodesVisited, note, diagnosticCode,
            nextActions: nextActions,
            writeExtraRootProperties: w =>
            {
                w.WriteString("path_kind", pathKind);
                if (path is null)
                    w.WriteNull("hops");
                else
                    w.WriteNumber("hops", path.Nodes.Count - 1);
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
                        if (symbolsById.TryGetValue(path.Nodes[i], out IndexedSymbol? symbol))
                            WriteSymbolNode(
                                w,
                                symbol,
                                i == 0 ? "target" : i == path.Nodes.Count - 1 ? "destination" : "path",
                                hop: i);
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
                        GraphPathEdge edge = path.Edges[i - 1];
                        w.WriteString("source", edge.From);
                        w.WriteString("target", edge.To);
                        w.WriteString("kind", "dependency_path");
                        w.WriteString("edge_kind", edge.Kind);
                        w.WriteNumber("confidence", edge.Confidence);
                        w.WriteString("provenance", edge.Source);
                        w.WriteNumber("hop", i);
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();
            });
    }

    private static string RenderRefsJson(
        ISymbolLookupIndex index,
        string target, int depth, int limit, int emitted, int nodesVisited, IndexedSymbol? targetSymbol,
        ReferenceEvidenceSet? evidence,
        IReadOnlyList<ReferenceEvidence> exact,
        IReadOnlyList<ReferenceEvidence> fallback,
        string? normalizedKind, bool includeDefinition,
        string? note, string? diagnosticCode, IReadOnlyList<TraceNextAction>? nextActions = null,
        string? continuation = null)
    {
        return RenderTraceJson(ModeRefs, target, to: null, depth, limit, emitted, nodesVisited, note, diagnosticCode,
            nextActions: nextActions,
            writeExtraRootProperties: w =>
            {
                if (normalizedKind is null) w.WriteNull("reference_kind"); else w.WriteString("reference_kind", normalizedKind);
                w.WriteBoolean("include_definition", includeDefinition);
                if (continuation is null) w.WriteNull("continuation"); else w.WriteString("continuation", continuation);
                w.WritePropertyName("exact_references");
                w.WriteStartArray();
                foreach (ReferenceEvidence reference in exact)
                    WriteReference(index, w, reference, targetSymbol?.Name);
                w.WriteEndArray();
                w.WritePropertyName("fallback_references");
                w.WriteStartArray();
                foreach (ReferenceEvidence reference in fallback)
                    WriteReference(index, w, reference, targetSymbol?.Name);
                w.WriteEndArray();
                WriteReferenceCoverage(w, evidence?.Coverage);
            },
            writeResolvedTarget: w => WriteSymbolOrNull(w, targetSymbol),
            writeNodes: w =>
            {
                w.WriteStartArray();
                if (includeDefinition && targetSymbol is not null)
                    WriteSymbolNode(w, targetSymbol, "target", hop: null);
                w.WriteEndArray();
            });
    }

    private static void WriteReferenceCoverage(Utf8JsonWriter w, ReferenceEvidenceCoverage? coverage)
    {
        w.WritePropertyName("reference_coverage");
        if (coverage is null)
        {
            w.WriteNullValue();
            return;
        }

        w.WriteStartObject();
        w.WriteNumber("exact_observed", coverage.ExactObserved);
        w.WriteNumber("exact_available", coverage.ExactAvailable);
        w.WriteNumber("exact_returned", coverage.ExactReturned);
        w.WriteNumber("fallback_available", coverage.FallbackAvailable);
        w.WriteNumber("fallback_returned", coverage.FallbackReturned);
        w.WriteNumber("same_name_definition_count", coverage.SameNameDefinitionCount);
        w.WriteBoolean("exact_truncated", coverage.ExactTruncated);
        w.WriteBoolean("fallback_truncated", coverage.FallbackTruncated);
        w.WriteString("fallback_status", FallbackStatusLabel(coverage.FallbackStatus));
        w.WriteEndObject();
    }

    private static string FallbackStatusLabel(ReferenceFallbackStatus status) => status switch
    {
        ReferenceFallbackStatus.NoCandidates => "no_candidates",
        ReferenceFallbackStatus.Available => "available",
        ReferenceFallbackStatus.SuppressedAmbiguousName => "suppressed_ambiguous_name",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static string RenderBridgeJson(
        BridgeGraph graph, string target, string? to, int depth, int limit, int emitted, int nodesVisited,
        string? startId, IReadOnlyList<ScoredEdge> edges, string? note, string? diagnosticCode,
        IReadOnlyList<TraceNextAction>? nextActions = null)
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
            nextActions: nextActions,
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
        IReadOnlyList<TraceNextAction>? nextActions = null,
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
            w.WriteNumber("schema_version", 2);
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
            w.WritePropertyName("next_actions");
            WriteNextActions(w, nextActions);
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

    private static void WriteNextActions(Utf8JsonWriter w, IReadOnlyList<TraceNextAction>? actions)
    {
        w.WriteStartArray();
        if (actions is not null)
        {
            foreach (TraceNextAction action in actions.Take(MaxNextActions))
            {
                w.WriteStartObject();
                w.WriteString("tool", action.Tool);
                w.WriteString("reason", action.Reason);
                w.WritePropertyName("args");
                w.WriteStartObject();
                foreach (KeyValuePair<string, string> arg in action.Args)
                    w.WriteString(arg.Key, arg.Value);
                w.WriteEndObject();
                w.WriteEndObject();
            }
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

    private static void WriteReference(
        ISymbolLookupIndex index,
        Utf8JsonWriter w,
        ReferenceEvidence reference,
        string? fallbackName)
    {
        string? name = reference.TargetSymbolId is null
            ? fallbackName
            : index.FindBySymbolId(reference.TargetSymbolId)?.Name ?? fallbackName;
        w.WriteStartObject();
        w.WriteString("reference_site_id", reference.ReferenceSiteId);
        w.WriteBoolean("is_exact", reference.IsExact);
        w.WriteString("site_provenance", reference.SiteProvenance);
        if (name is null) w.WriteNull("name"); else w.WriteString("name", name);
        w.WriteString("target_symbol_id", reference.TargetSymbolId);
        w.WriteString("kind", reference.SourceKind);
        w.WriteString("canonical_kind", reference.Kind.ToString());
        w.WriteString("file", reference.FilePath);
        if (reference.StartLine is null) w.WriteNull("line"); else w.WriteNumber("line", reference.StartLine.Value);
        if (reference.StartColumn is null) w.WriteNull("column"); else w.WriteNumber("column", reference.StartColumn.Value);
        if (reference.EndLine is null) w.WriteNull("end_line"); else w.WriteNumber("end_line", reference.EndLine.Value);
        if (reference.EndColumn is null) w.WriteNull("end_column"); else w.WriteNumber("end_column", reference.EndColumn.Value);
        if (reference.StartByte is null) w.WriteNull("start_byte"); else w.WriteNumber("start_byte", reference.StartByte.Value);
        if (reference.EndByte is null) w.WriteNull("end_byte"); else w.WriteNumber("end_byte", reference.EndByte.Value);
        if (reference.ContainingSymbolId is null)
            w.WriteNull("containing_symbol_id");
        else
            w.WriteString("containing_symbol_id", reference.ContainingSymbolId);
        if (reference.ContainingSymbolId is { } containingId &&
            index.FindBySymbolId(containingId) is { } containing)
            w.WriteString("containing_symbol_name", containing.Name);
        else
            w.WriteNull("containing_symbol_name");
        w.WriteString("resolution_status",
            reference.ResolutionStatus == ReferenceResolutionStatus.Exact ? "exact" : "fallback");
        w.WriteString("source", EvidenceSourceLabel(reference.Source));
        if (reference.ResolutionTier is null)
            w.WriteNull("resolution_tier");
        else
            w.WriteNumber("resolution_tier", reference.ResolutionTier.Value);
        w.WriteNumber("confidence", reference.Confidence);
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
        BridgeKind.NavigatesTo => "navigates_to",
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
        BridgeNodeKind.FileRoute => "file_route",
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
