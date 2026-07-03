using Miller.Core.Contracts;
using Miller.Core.Resolver;
using System.Text.Json;

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
        var resourceFacts = new List<StructuralFactRecord>();
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

            // Task 4: rails.resource_route.v1 matches none of the reads above (it is NOT in BackendRoutePatternIds).
            // Collect the raw fact and expand it into concrete verb-known route handlers below. The IsTestFact filter
            // mirrors the route/mount reads so a resources declaration in a test-scoped routes file never expands.
            if (string.Equals(fact.PatternId, BridgeStructuralPatterns.RailsResourceRoute, StringComparison.Ordinal))
            {
                if (!StructuralRouteFactAdapter.IsTestFact(fact, context.SymbolsById))
                    resourceFacts.Add(fact);
                continue;
            }

            // rails.mount mounts a Rack app whose internal routes never reach the fact stream, so it composes
            // nothing — counted as mount evidence only (never read, never a join input).
            if (string.Equals(fact.PatternId, BridgeStructuralPatterns.RailsMount, StringComparison.Ordinal))
                railsMountCount++;
        }

        // Task 4: build the (controllerClass, action) → unique-method-id lookup ONCE per run (mirrors Task 3's
        // BuildNameToFiles — no O(symbols) scan per route). Rails controller binding is unambiguous-or-nothing:
        // exactly one non-test method match binds; zero or many falls back to the fact's containing symbol id.
        var controllerMethods = BuildControllerMethodIndex(context.Symbols);

        // The join pool. Task 3 (mount-prefix composition) and Task 4 (Rails resource expansion) APPEND their
        // synthesized handlers here before the resolve call — keep this as a distinct local so the insertion point
        // stays obvious. Rails route handlers carrying controller_action are REBOUND to their resolved controller
        // method (an honest rebind — controller_action is receiver identity); every other family is copied verbatim.
        var routeHandlers = new List<StructuralRouteHandler>(backendRoutes.Count);
        foreach (var route in backendRoutes)
            routeHandlers.Add(BindRailsRouteController(route, controllerMethods));

        // Task 3: cross-file mount-prefix composition reads the PRISTINE backendRoutes (so the rebind above and the
        // route-fact count stay decoupled). Anchor each mount fact (deterministically, unambiguous-or-nothing) to
        // route facts in another file and APPEND composed prefixed variants — strictly additive, originals kept.
        var composition = ComposeMountedRoutes(mountFacts, backendRoutes, context.Symbols);
        routeHandlers.AddRange(composition.Composed);

        // Task 4: expand rails.resource_route.v1 facts into concrete verb-known handlers and APPEND them.
        var expandedResourceRoutes = ExpandResourceRoutes(resourceFacts, controllerMethods);
        routeHandlers.AddRange(expandedResourceRoutes);

        var result = FileRouteBridge.ResolveClientRequests(clientRequests, routeHandlers);
        var evidenceCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["backend-http.clientRequests"] = clientRequests.Count,
            ["backend-http.routeFacts"] = backendRoutes.Count,
            ["backend-http.mounts"] = mountFacts.Count + railsMountCount,
            ["backend-http.composedRoutes"] = composition.Composed.Count,
            ["backend-http.unanchoredMounts"] = composition.UnanchoredMounts,
            ["backend-http.expandedResourceRoutes"] = expandedResourceRoutes.Count,
            ["backend-http.candidates"] = result.Edges.Count,
            ["backend-http.ambiguousMatches"] = result.AmbiguousMatches,
        };

        if (clientRequests.Count == 0 && backendRoutes.Count == 0 && mountFacts.Count == 0 &&
            railsMountCount == 0 && resourceFacts.Count == 0)
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

    // ================================ Task 4: Rails semantics ================================================
    // Rails is Miller's job (julie handoff). rails.resource_route.v1 facts are expanded into concrete verb-known
    // route handlers by deterministic Rails doctrine, and rails route handlers bind to their controller-action
    // method symbol UNAMBIGUOUSLY-OR-NOTHING. Every expanded handler carries the resource fact's routes.rb
    // file/line so trace points at the declaring DSL line; the resolved controller method id (when a unique
    // non-test match exists) becomes the edge target, else the endpoint falls back to a synthesized node.

    // The 7 conventional Rails actions — the only/except filter domain. `update` maps to two verb entries.
    private static readonly HashSet<string> ConventionalActions = new(StringComparer.Ordinal)
    {
        "index", "create", "new", "edit", "show", "update", "destroy",
    };

    // resources :x → 8 handler entries (collection). Suffix is appended to "/{resource_name}".
    private static readonly (string Action, string Verb, string Suffix)[] CollectionRoutes =
    [
        ("index",   "GET",    ""),
        ("create",  "POST",   ""),
        ("new",     "GET",    "/new"),
        ("edit",    "GET",    "/:id/edit"),
        ("show",    "GET",    "/:id"),
        ("update",  "PATCH",  "/:id"),
        ("update",  "PUT",    "/:id"),
        ("destroy", "DELETE", "/:id"),
    ];

    // resource :x → 7 handler entries (singular): no index, no :id member routes.
    private static readonly (string Action, string Verb, string Suffix)[] SingularRoutes =
    [
        ("show",    "GET",    ""),
        ("create",  "POST",   ""),
        ("new",     "GET",    "/new"),
        ("edit",    "GET",    "/edit"),
        ("update",  "PATCH",  ""),
        ("update",  "PUT",    ""),
        ("destroy", "DELETE", ""),
    ];

    /// <summary>
    /// Expand every collected <c>rails.resource_route.v1</c> fact into concrete verb-known route handlers by Rails
    /// doctrine. The base path segment is <c>resource_name</c> (leading <c>:</c> stripped); <c>only</c>/<c>except</c>
    /// (JSON string arrays, tolerant of a leading <c>:</c> and a raw ruby form) filter the ACTION set;
    /// <c>scope_path</c> prefixes every path via <see cref="JoinRoute"/>. Both kinds map to a PLURAL controller
    /// (collection <c>resource_name</c> is already plural; a singular one is pluralized first); each entry binds to
    /// the conventional action method when a unique non-test match exists, else to the fact's containing symbol id.
    /// </summary>
    private static IReadOnlyList<StructuralRouteHandler> ExpandResourceRoutes(
        IReadOnlyList<StructuralFactRecord> resourceFacts,
        IReadOnlyDictionary<string, string?> controllerMethods)
    {
        var expanded = new List<StructuralRouteHandler>();
        if (resourceFacts.Count == 0)
            return expanded;

        foreach (var fact in resourceFacts)
        {
            var rawName = StructuralRouteFactAdapter.MetadataString(fact, "resource_name");
            if (rawName is null)
                continue;
            var resourceName = StripLeadingColon(rawName.Trim());
            if (resourceName.Length == 0)
                continue;

            var kind = StructuralRouteFactAdapter.MetadataString(fact, "resource_kind")?.Trim() ?? string.Empty;
            var singular = string.Equals(kind, "singular", StringComparison.OrdinalIgnoreCase);
            var collection = string.Equals(kind, "collection", StringComparison.OrdinalIgnoreCase);
            if (!singular && !collection)
                continue; // Unknown resource_kind → expand nothing (honest: never fabricate a route shape).

            // Rails maps BOTH kinds to a PLURAL controller: `resources :users` → UsersController; `resource :profile`
            // → ProfilesController (pluralize, then CamelCase + "Controller"). A singular ProfileController never binds.
            var controllerClass = CamelCase(singular ? Pluralize(resourceName) : resourceName) + "Controller";

            var allowed = ComputeAllowedActions(fact); // null ⇒ every conventional action.
            var scopePath = StructuralRouteFactAdapter.MetadataString(fact, "scope_path");
            var table = singular ? SingularRoutes : CollectionRoutes;

            foreach (var (action, verb, suffix) in table)
            {
                if (allowed is not null && !allowed.Contains(action))
                    continue;

                var path = "/" + resourceName + suffix;
                if (!string.IsNullOrWhiteSpace(scopePath))
                    path = JoinRoute(scopePath, path);

                var boundId = ResolveControllerMethod(controllerMethods, controllerClass, action)
                    ?? (fact.ContainingSymbolId ?? string.Empty);

                expanded.Add(new StructuralRouteHandler(fact, path, verb, boundId, fact.Path, fact.Span.StartLine));
            }
        }

        return expanded;
    }

    /// <summary>
    /// Rebind a <c>rails.route.v1</c> handler carrying <c>controller_action</c> (<c>"users#show"</c>) to the resolved
    /// controller method: controller class = <c>CamelCase(controller) + "Controller"</c> (already in Rails' controller
    /// form, no inflection), action = the method name. An honest rebind because controller_action IS receiver
    /// identity. Any non-rails handler, a missing/blank controller_action, or an unresolved lookup returns the handler
    /// unchanged (⇒ its original containing symbol id, or a synthesized endpoint node when that is blank).
    /// </summary>
    private static StructuralRouteHandler BindRailsRouteController(
        StructuralRouteHandler route,
        IReadOnlyDictionary<string, string?> controllerMethods)
    {
        if (!string.Equals(route.Fact.PatternId, BridgeStructuralPatterns.RailsRoute, StringComparison.Ordinal))
            return route;

        var controllerAction = StructuralRouteFactAdapter.MetadataString(route.Fact, "controller_action");
        if (controllerAction is null)
            return route;

        var hash = controllerAction.IndexOf('#', StringComparison.Ordinal);
        if (hash <= 0 || hash >= controllerAction.Length - 1)
            return route; // Need a "controller#action" shape; anything else is not a bindable literal.

        var controllerClass = CamelCase(controllerAction[..hash].Trim()) + "Controller";
        var action = controllerAction[(hash + 1)..].Trim();
        if (action.Length == 0)
            return route;

        var boundId = ResolveControllerMethod(controllerMethods, controllerClass, action);
        return boundId is null ? route : route with { ContainingSymbolId = boundId };
    }

    /// <summary>
    /// Build the <c>(controllerClass, action) → unique-method-id</c> lookup once per run. A key maps to the ONE
    /// non-test method symbol whose <see cref="SymbolDetail.ParentClassName"/> and <see cref="SymbolDetail.Name"/>
    /// match; a second collision POISONS the key to null (ambiguous → never binds). Unambiguous-or-nothing.
    /// </summary>
    private static Dictionary<string, string?> BuildControllerMethodIndex(IReadOnlyList<SymbolDetail> symbols)
    {
        var index = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.IsTest ||
                !string.Equals(symbol.Kind, "method", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(symbol.Name) ||
                string.IsNullOrEmpty(symbol.ParentClassName) ||
                string.IsNullOrEmpty(symbol.Id))
            {
                continue;
            }

            var key = symbol.ParentClassName + ' ' + symbol.Name;
            index[key] = index.ContainsKey(key) ? null : symbol.Id; // second match ⇒ ambiguous (null), never binds.
        }
        return index;
    }

    /// <summary>Look up the unique method id for a controller action; null when absent OR ambiguous (poisoned).</summary>
    private static string? ResolveControllerMethod(
        IReadOnlyDictionary<string, string?> controllerMethods,
        string controllerClass,
        string action) =>
        controllerMethods.TryGetValue(controllerClass + ' ' + action, out var id) ? id : null;

    /// <summary>
    /// The allowed action set from <c>only</c>/<c>except</c> (null ⇒ every conventional action). <c>only</c> keeps
    /// only the listed conventional actions; <c>except</c> drops the listed ones. Elements are normalized (trimmed,
    /// leading <c>:</c> stripped, lowercased) before comparison against the conventional action names.
    /// </summary>
    private static HashSet<string>? ComputeAllowedActions(StructuralFactRecord fact)
    {
        var onlyRaw = StructuralRouteFactAdapter.MetadataString(fact, "only");
        if (onlyRaw is not null)
        {
            var only = ParseActionList(onlyRaw);
            return ConventionalActions.Where(only.Contains).ToHashSet(StringComparer.Ordinal);
        }

        var exceptRaw = StructuralRouteFactAdapter.MetadataString(fact, "except");
        if (exceptRaw is not null)
        {
            var except = ParseActionList(exceptRaw);
            return ConventionalActions.Where(action => !except.Contains(action)).ToHashSet(StringComparer.Ordinal);
        }

        return null;
    }

    /// <summary>
    /// Parse a Rails action list. The 2.7.0 contract types <c>only</c>/<c>except</c> as JSON string arrays, so parse
    /// with <see cref="JsonDocument"/> first (AOT-safe; reflection-based deserialize fails the Native AOT publish);
    /// on failure fall back to a bracket/comma split so a raw ruby array (<c>[:index, :show]</c>) still works — the
    /// task asks for leading-<c>:</c> tolerance so Task 7's live extract cannot surprise the filter. Each element is
    /// normalized to a lowercased action name.
    /// </summary>
    private static HashSet<string> ParseActionList(string raw)
    {
        var trimmed = raw.Trim();
        var actions = new HashSet<string>(StringComparer.Ordinal);
        if (trimmed.Length == 0)
            return actions;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                        continue;
                    var action = NormalizeAction(element.GetString()!);
                    if (action.Length > 0)
                        actions.Add(action);
                }
                return actions;
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the tolerant bracket/comma split (raw ruby `[:index, :show]`).
        }

        var inner = trimmed;
        if (inner.StartsWith('['))
            inner = inner[1..];
        if (inner.EndsWith(']'))
            inner = inner[..^1];
        foreach (var element in inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var action = NormalizeAction(element);
            if (action.Length > 0)
                actions.Add(action);
        }
        return actions;
    }

    /// <summary>Normalize one action token: strip surrounding quotes, a leading <c>:</c>, then lowercase.</summary>
    private static string NormalizeAction(string raw)
    {
        var token = raw.Trim();
        if (token.Length >= 2 && token[0] == '"' && token[^1] == '"')
            token = token[1..^1].Trim();
        token = StripLeadingColon(token);
        return token.Trim().ToLowerInvariant();
    }

    private static string StripLeadingColon(string token) =>
        token.StartsWith(':') ? token[1..] : token;

    /// <summary>snake_case → PascalCase: split on '_', capitalize each segment (<c>admin_user</c> → <c>AdminUser</c>).</summary>
    private static string CamelCase(string snake) =>
        string.Concat(snake
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..]));

    /// <summary>
    /// Minimal English pluralization for singular resource controllers: <c>s/x/z/ch/sh</c> → append <c>es</c>;
    /// consonant + <c>y</c> → <c>ies</c>; otherwise append <c>s</c>. Irregulars (<c>person</c> → <c>people</c>)
    /// simply fail the controller lookup — unambiguous-or-nothing already guards, and the endpoint fallback is honest.
    /// </summary>
    private static string Pluralize(string word)
    {
        if (word.Length == 0)
            return word;

        if (word.EndsWith("s", StringComparison.OrdinalIgnoreCase) ||
            word.EndsWith("x", StringComparison.OrdinalIgnoreCase) ||
            word.EndsWith("z", StringComparison.OrdinalIgnoreCase) ||
            word.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
            word.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
        {
            return word + "es";
        }

        if (word.Length >= 2 && (word[^1] == 'y' || word[^1] == 'Y') && !IsVowel(word[^2]))
            return word[..^1] + "ies";

        return word + "s";
    }

    private static bool IsVowel(char c) =>
        c is 'a' or 'e' or 'i' or 'o' or 'u' or 'A' or 'E' or 'I' or 'O' or 'U';
}
