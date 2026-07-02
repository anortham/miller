using Miller.Core.Contracts;
using Miller.Core.Resolver;

namespace Miller.Core.Graph;

/// <summary>
/// Verb-aware backend HTTP boundary bridge provider (julie-extractors 2.7.0): joins
/// <c>http.client_request.v1</c> fetch/axios call sites to server route-template facts from the 10
/// <see cref="BridgeStructuralPatterns.BackendRoutePatternIds"/> families (Express/Fastify/FastAPI/Flask/
/// Django/Spring/Go net-http/gin/echo/Rails), emitting <see cref="BridgeKind.Hits"/> edges. It sits beside the
/// framework-specific verb-aware API arm (<see cref="ApiRouteBridgeProvider"/>) but is standalone rather than
/// descriptor-driven: it collects a broad route-family set plus the cross-file mount/include inputs, giving the
/// later enrichment passes (mount composition, Rails resource expansion) a single place to grow.
///
/// <para>All verb rules come free from <see cref="FileRouteBridge.ResolveClientRequests"/>: handler verb equal ⇒
/// High (<see cref="SignalRule.RouteVerbMatch"/>); handler verb different ⇒ no edge; handler verb null ⇒ Medium
/// <c>verb_unknown</c> (<see cref="SignalRule.RouteOnlyMatch"/>); a specificity tie between equally-specific
/// verb-exact routes is ambiguous and yields no edge.</para>
///
/// <para><b>Enrichment seam (Tasks 3–4).</b> <c>routeHandlers</c> starts as a copy of the directly-read backend
/// routes; mount-prefix composition and Rails resource expansion APPEND their synthesized handlers to that list
/// BEFORE the resolve call, and read the collected <c>mountFacts</c>. Those passes will add
/// <c>backend-http.composedRoutes</c> / <c>.unanchoredMounts</c> / <c>.expandedResourceRoutes</c> evidence keys;
/// Task 2 emits none of those.</para>
/// </summary>
public sealed class BackendHttpBridgeProvider : IBridgeProvider
{
    public const string ProviderId = "backend-http";

    public static BackendHttpBridgeProvider Instance { get; } = new();

    private BackendHttpBridgeProvider()
    {
    }

    public string Id => ProviderId;

    public BridgeProviderResult BuildCandidates(BridgeProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var clientRequests = new List<StructuralClientRequest>();
        var backendRoutes = new List<StructuralRouteHandler>();
        var mountFacts = new List<StructuralMountFact>();
        var railsMountCount = 0;
        foreach (var fact in context.StructuralFacts.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Span.StartByte))
        {
            if (StructuralRouteFactAdapter.TryReadClientRequest(fact, context.SymbolsById, out var request))
            {
                clientRequests.Add(request);
                continue;
            }

            if (StructuralRouteFactAdapter.TryReadBackendRoute(fact, context.SymbolsById, out var handler))
            {
                backendRoutes.Add(handler);
                continue;
            }

            if (StructuralRouteFactAdapter.TryReadMountFact(fact, context.SymbolsById, out var mount))
            {
                // Collected for the Task 3 mount-prefix composition pass; Task 2 only counts them as evidence.
                mountFacts.Add(mount);
                continue;
            }

            // rails.mount mounts a Rack app whose internal routes never reach the fact stream, so it composes
            // nothing — counted as mount evidence only (never read, never a join input).
            if (string.Equals(fact.PatternId, BridgeStructuralPatterns.RailsMount, StringComparison.Ordinal))
                railsMountCount++;
        }

        // The join pool. Tasks 3 (mount-prefix composition) and 4 (Rails resource expansion) APPEND their
        // synthesized handlers here before the resolve call — keep this as a distinct local so the insertion
        // point stays obvious.
        var routeHandlers = new List<StructuralRouteHandler>(backendRoutes);

        // Task 3: cross-file mount-prefix composition. Anchor each mount fact (deterministically, unambiguous-or-
        // nothing) to route facts in another file and APPEND composed prefixed variants — strictly additive, the
        // original backendRoutes entries are never removed.
        var composition = ComposeMountedRoutes(mountFacts, backendRoutes, context.Symbols);
        routeHandlers.AddRange(composition.Composed);

        var result = FileRouteBridge.ResolveClientRequests(clientRequests, routeHandlers);
        var evidenceCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["backend-http.clientRequests"] = clientRequests.Count,
            ["backend-http.routeFacts"] = backendRoutes.Count,
            ["backend-http.mounts"] = mountFacts.Count + railsMountCount,
            ["backend-http.composedRoutes"] = composition.Composed.Count,
            ["backend-http.unanchoredMounts"] = composition.UnanchoredMounts,
            ["backend-http.candidates"] = result.Edges.Count,
            ["backend-http.ambiguousMatches"] = result.AmbiguousMatches,
        };

        if (clientRequests.Count == 0 && backendRoutes.Count == 0 && mountFacts.Count == 0 && railsMountCount == 0)
            return BridgeProviderResult.Skipped("no backend-http bridge evidence", evidenceCounts);

        return BridgeProviderResult.ActiveResult(
            result.Edges,
            evidenceCounts,
            BuildObservationNodes(clientRequests, routeHandlers));
    }

    /// <summary>
    /// Route diagnostics need the unmatched sides too: every client request becomes a canonical-route
    /// <see cref="BridgeNodeKind.TsType"/> node and every entry in <paramref name="routeHandlers"/> a
    /// <see cref="BridgeNodeKind.Endpoint"/> node (matched ones collapse into the edge nodes via the builder's
    /// TryAdd). Building over <paramref name="routeHandlers"/> rather than the directly-read routes means the
    /// Task 3/4 composed/expanded handlers get observation nodes for free.
    /// </summary>
    private static IReadOnlyDictionary<string, BridgeNode> BuildObservationNodes(
        IReadOnlyList<StructuralClientRequest> clientRequests,
        IReadOnlyList<StructuralRouteHandler> routeHandlers)
    {
        var nodes = new Dictionary<string, BridgeNode>(StringComparer.Ordinal);

        foreach (var request in clientRequests)
        {
            var display = FileRouteBridgeProvider.RouteDisplay(request.RoutePath);
            var id = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, display);
            nodes.TryAdd(id, new BridgeNode(id, BridgeNodeKind.TsType, display, request.FilePath, request.Line));
        }

        foreach (var handler in routeHandlers)
        {
            var display = FileRouteBridge.HandlerDisplay(handler);
            var id = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, display);
            nodes.TryAdd(id, new BridgeNode(id, BridgeNodeKind.Endpoint, display, handler.FilePath, handler.Line));
        }

        return nodes;
    }

    // ============================ Task 3: cross-file mount-prefix composition ==================================
    // Doctrine (from the plan): AMBIGUITY POISONS, NEVER DEGRADES. Each mount fact is anchored to route facts in
    // ANOTHER file via one of two deterministic tiers; a zero-or-tied anchor composes NOTHING and is counted in
    // unanchoredMounts. Composition is STRICTLY ADDITIVE — it only APPENDS composed variants (RoutePath =
    // JoinRoute(mountPath, routePath); verb/symbol/file/line/Fact unchanged), never replacing an original route
    // fact, because route facts carry no receiver identity and Miller cannot prove a given route belongs to the
    // mounted router rather than a direct app.get in the same file.

    private readonly record struct MountComposition(
        IReadOnlyList<StructuralRouteHandler> Composed,
        int UnanchoredMounts);

    /// <summary>
    /// Compose cross-file mounted routes for every collected mount fact. Builds ONE name→defining-files lookup for
    /// the whole run (not an O(symbols) scan per mount), then anchors each mount by module path (django) or trailing
    /// identifier (express/fastapi/flask) and appends prefixed route variants. A mount that cannot anchor to exactly
    /// one file composes nothing and increments <see cref="MountComposition.UnanchoredMounts"/>.
    /// </summary>
    private static MountComposition ComposeMountedRoutes(
        IReadOnlyList<StructuralMountFact> mountFacts,
        IReadOnlyList<StructuralRouteHandler> backendRoutes,
        IReadOnlyList<SymbolDetail> symbols)
    {
        var composed = new List<StructuralRouteHandler>();
        if (mountFacts.Count == 0)
            return new MountComposition(composed, 0);

        var nameToFiles = BuildNameToFiles(symbols);
        var unanchored = 0;

        foreach (var mount in mountFacts)
        {
            var routeFamily = RouteFamilyForMount(mount.Fact.PatternId);
            if (routeFamily is null)
                continue; // Unknown/evidence-only family (rails.mount never reaches this list) — never composes.

            var anchorFile = string.Equals(mount.Fact.PatternId, BridgeStructuralPatterns.DjangoUrlInclude, StringComparison.Ordinal)
                ? AnchorByModulePath(mount, backendRoutes)
                : AnchorByIdentifier(mount, routeFamily, backendRoutes, nameToFiles);

            if (anchorFile is null)
            {
                unanchored++;
                continue;
            }

            // Anchored to exactly one file: compose every matching-family route in that file that is NOT already
            // same-file prefixed. A file that anchors but yields zero composable routes is anchored, not unanchored.
            foreach (var route in backendRoutes)
            {
                if (!string.Equals(route.Fact.PatternId, routeFamily, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(Normalize(route.Fact.Path), anchorFile, StringComparison.Ordinal))
                    continue;
                // effective_route_template means the framework already folded a same-file prefix (app.use /
                // router_prefix / url_prefix); composing again would double-prefix, so skip it.
                if (StructuralRouteFactAdapter.MetadataString(route.Fact, "effective_route_template") is not null)
                    continue;

                composed.Add(route with { RoutePath = JoinRoute(mount.MountPath, route.RoutePath) });
            }
        }

        return new MountComposition(composed, unanchored);
    }

    /// <summary>Map a mount/include family to the route-template family it prefixes. Null for anything else.</summary>
    private static string? RouteFamilyForMount(string mountPatternId) => mountPatternId switch
    {
        BridgeStructuralPatterns.ExpressRouterMount => BridgeStructuralPatterns.ExpressRoute,
        BridgeStructuralPatterns.FastApiIncludeRouter => BridgeStructuralPatterns.FastApiRoute,
        BridgeStructuralPatterns.FlaskBlueprintRegistration => BridgeStructuralPatterns.FlaskRoute,
        BridgeStructuralPatterns.DjangoUrlInclude => BridgeStructuralPatterns.DjangoUrlPattern,
        _ => null,
    };

    /// <summary>
    /// Tier 1 (django only): anchor by the included module path. <c>"shop.urls"</c> → suffix <c>"shop/urls.py"</c>;
    /// among django route facts, keep those whose (normalized) path ends at a SEGMENT boundary with that suffix.
    /// Exactly one distinct file anchors; zero or multiple compose nothing (returns null).
    /// </summary>
    private static string? AnchorByModulePath(
        StructuralMountFact mount,
        IReadOnlyList<StructuralRouteHandler> backendRoutes)
    {
        if (string.IsNullOrWhiteSpace(mount.IncludedModule))
            return null;

        var suffix = mount.IncludedModule.Replace('.', '/') + ".py";
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in backendRoutes)
        {
            if (!string.Equals(route.Fact.PatternId, BridgeStructuralPatterns.DjangoUrlPattern, StringComparison.Ordinal))
                continue;
            var path = Normalize(route.Fact.Path);
            // Segment-boundary endsWith: path == suffix or path ends with "/" + suffix — so "myshop/urls.py" is not
            // a false match for "shop/urls.py". A tighter anchor can only REDUCE false composes (poisons, not degrades).
            if (string.Equals(path, suffix, StringComparison.Ordinal) ||
                path.EndsWith("/" + suffix, StringComparison.Ordinal))
            {
                files.Add(path);
            }
        }

        return files.Count == 1 ? files.First() : null;
    }

    /// <summary>
    /// Tier 2 (express/fastapi/flask): anchor by the trailing identifier of the mount target. The anchor file must
    /// BOTH define a non-test symbol named <c>identifier</c> AND own ≥1 route fact of the matching family. Exactly
    /// one such file anchors; zero or ties compose nothing. A DOTTED fastapi target additionally requires the file
    /// stem to equal the module segment (<c>users.router</c> ⇒ file stem <c>users</c>).
    /// </summary>
    private static string? AnchorByIdentifier(
        StructuralMountFact mount,
        string routeFamily,
        IReadOnlyList<StructuralRouteHandler> backendRoutes,
        IReadOnlyDictionary<string, HashSet<string>> nameToFiles)
    {
        var (identifier, module) = ExtractIdentifier(mount.MountTarget);
        if (identifier is null || !nameToFiles.TryGetValue(identifier, out var definingFiles) || definingFiles.Count == 0)
            return null;

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in backendRoutes)
        {
            if (!string.Equals(route.Fact.PatternId, routeFamily, StringComparison.Ordinal))
                continue;
            var path = Normalize(route.Fact.Path);
            if (definingFiles.Contains(path))
                candidates.Add(path);
        }

        // Dotted fastapi target (users.router): the router's module segment must match the defining file's stem.
        if (module is not null &&
            string.Equals(mount.Fact.PatternId, BridgeStructuralPatterns.FastApiIncludeRouter, StringComparison.Ordinal))
        {
            candidates.RemoveWhere(path => !string.Equals(Stem(path), module, StringComparison.Ordinal));
        }

        return candidates.Count == 1 ? candidates.First() : null;
    }

    /// <summary>
    /// One name→distinct-non-test-defining-files lookup, built once per run. A symbol's file defines its name; test
    /// symbols never seed an anchor (route facts are already non-test).
    /// </summary>
    private static Dictionary<string, HashSet<string>> BuildNameToFiles(IReadOnlyList<SymbolDetail> symbols)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.IsTest || string.IsNullOrEmpty(symbol.Name) || string.IsNullOrEmpty(symbol.FilePath))
                continue;
            if (!map.TryGetValue(symbol.Name, out var files))
            {
                files = new HashSet<string>(StringComparer.Ordinal);
                map[symbol.Name] = files;
            }
            files.Add(Normalize(symbol.FilePath));
        }
        return map;
    }

    /// <summary>
    /// Extract the trailing identifier (and preceding module segment, when dotted) from a mount target's source
    /// text: drop any call arguments (<c>express.json()</c> → <c>express.json</c>), split on '.', take the last
    /// identifier-like segment as the identifier and the one before it as the module. Returns (null, null) when no
    /// identifier-like token remains (⇒ the mount composes nothing).
    /// </summary>
    private static (string? Identifier, string? Module) ExtractIdentifier(string mountTarget)
    {
        var text = mountTarget?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return (null, null);

        var call = text.IndexOf('(', StringComparison.Ordinal);
        if (call >= 0)
            text = text[..call].Trim();
        if (text.Length == 0)
            return (null, null);

        var segments = text.Split('.');
        var last = segments[^1].Trim();
        if (!IsIdentifierLike(last))
            return (null, null);

        string? module = null;
        if (segments.Length >= 2)
        {
            var candidate = segments[^2].Trim();
            module = IsIdentifierLike(candidate) ? candidate : null;
        }
        return (last, module);
    }

    private static bool IsIdentifierLike(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        if (!(char.IsLetter(token[0]) || token[0] == '_' || token[0] == '$'))
            return false;
        foreach (var ch in token)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '$'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Concatenate a mount prefix and a route path with a single separator, trimming redundant slashes. The
    /// resolver canonically folds params/slashes for matching, so this only needs sane concatenation:
    /// <c>("/users","/:id")</c> and <c>("/users/","/:id")</c> → <c>/users/:id</c>; <c>("/shop/","posts")</c> →
    /// <c>/shop/posts</c>; <c>("/","/:id")</c> → <c>/:id</c>.
    /// </summary>
    private static string JoinRoute(string prefix, string route)
    {
        var p = "/" + prefix.Trim().Trim('/');
        var r = route.Trim().Trim('/');
        if (r.Length == 0)
            return p;
        return (p == "/" ? string.Empty : p) + "/" + r;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>The file name without its extension: <c>app/users.py</c> → <c>users</c>.</summary>
    private static string Stem(string path)
    {
        var norm = Normalize(path);
        var slash = norm.LastIndexOf('/');
        var name = slash >= 0 ? norm[(slash + 1)..] : norm;
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
