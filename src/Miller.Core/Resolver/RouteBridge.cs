using Miller.Core.Contracts;

namespace Miller.Core.Resolver;

/// <summary>
/// A TS/JS client HTTP call, already located by the graph builder (design §4 Leg 1; findings 28-2). The verified
/// <c>literals</c> row carries the route text, the kind, the verbatim <c>carrier</c>, the language, the
/// <c>containing_symbol_id</c> and a byte span — but NO file/line columns and no <c>identifier_id</c>. The graph
/// builder (plan Task 8) is what selects the <c>kind=url</c> literals, resolves each one's use-site file:line for
/// evidence, and reads the containing TS function's <c>is_test</c> flag; this record is the already-located input the
/// pure leg consumes.
///
/// <para><b>The leg filters this to a real client call</b> (design §4 Leg 1): the literal must be <c>kind=url</c>,
/// its <see cref="LiteralRecord.Language"/> must be a frontend/client language (NOT the endpoint language —
/// julie stores FULL strings like <c>typescript</c>, so a short-code filter matches 0 rows), and its containing
/// symbol must NOT be a <c>test_role</c> HttpClient call (on real data the 57 <c>csharp</c> url literals are all test
/// HttpClient; only the 39 <c>typescript</c> ones are real calls). The verb is then derived from the carrier by
/// <see cref="RouteNormalizer.FromClientCall"/> — verb-known for <c>axios.&lt;verb&gt;</c>/<c>&lt;Verb&gt;Async</c>,
/// verb-unknown for <c>fetch</c>/<c>$fetch</c>/bare <c>axios</c>/etc. (never assumed GET).</para>
/// </summary>
/// <param name="Literal">
/// The url literal: <see cref="LiteralRecord.LiteralText"/> is the route ({}-folded), <see cref="LiteralRecord.Carrier"/>
/// is the verb source, <see cref="LiteralRecord.Language"/> drives the frontend-language filter.
/// </param>
/// <param name="IsTest">
/// The containing TS function's julie-extractors v1 typed <c>is_test</c> flag. A present test flag excludes the
/// literal from the route bridge — it is a test HttpClient call, not a real client call.
/// </param>
/// <param name="FilePath">The call's use-site file (workspace-relative), for the edge evidence.</param>
/// <param name="Line">The 1-based use-site line, for the edge evidence (file:line).</param>
public sealed record TsClientCall(
    LiteralRecord Literal,
    bool IsTest,
    string FilePath,
    int Line);

/// <summary>
/// A C# controller action endpoint, already reduced from julie's <c>symbol_annotations</c> + the parent class
/// <c>[Route]</c> + the method <c>signature</c> (design §4 Leg 1; findings 28-2). The verb comes from the lowercased
/// annotation key (<c>httpget</c>/<c>httppost</c>/…), the route arg from the annotation <c>raw_text</c>, and the
/// <c>[controller]</c>/<c>[action]</c>/<c>[area]</c> tokens are expanded by <see cref="RouteNormalizer.FromEndpoint"/>
/// using <see cref="ParentClassName"/>/<see cref="MethodName"/> BEFORE the class prefix is concatenated — the
/// load-bearing token expansion (design §8 risk 3); without it every endpoint normalizes to <c>api/[controller]/…</c>
/// and matches zero client routes, and a wrong expansion collides two controllers' <c>{id}</c> templates.
///
/// <para>The annotation-arg parsing (route out of <c>HttpGet("{id}")</c>), the <c>parent_id</c>→class join (71/71 on
/// real data) for the class <c>[Route]</c> and the parent class name, and the <c>[FromBody]</c>/parameter-type read out
/// of the method <c>signature</c> are the graph builder's job — plan Task 8, out of this leg's scope. This is the
/// already-reduced input the pure leg consumes.</para>
/// </summary>
/// <param name="SymbolId">
/// The endpoint method symbol's id (the resolved <c>—hits→</c> target), or null for a structural endpoint such as a
/// lambda minimal API route where the extractor observed the route but no handler symbol exists.
/// </param>
/// <param name="VerbKey">The lowercased annotation key (<c>httpget</c>/<c>httppost</c>/…); the verb is read from it.</param>
/// <param name="ClassRoute">The class <c>[Route(...)]</c> arg (e.g. <c>api/[controller]</c>), or null when absent.</param>
/// <param name="MethodRoute">The method route arg (e.g. <c>{id}</c>), or null when the method declares none.</param>
/// <param name="ParentClassName">The controller class name (e.g. <c>AppSettingsController</c>) — expands <c>[controller]</c>.</param>
/// <param name="MethodName">The action method name (e.g. <c>GetById</c>) — expands <c>[action]</c> and is the endpoint display.</param>
/// <param name="ReturnType">
/// The method <c>signature</c> return type (e.g. <c>Task&lt;ActionResult&lt;AppSetting&gt;&gt;</c>); unwrapped by
/// <see cref="FieldSetExtractor.UnwrapReturnType"/> to the response DTO for the <c>responds→</c> edge. May be empty.
/// </param>
/// <param name="RequestBodyType">
/// The <c>[FromBody]</c>/request parameter type from the method <c>signature</c> (e.g. <c>CreateAppSettingRequest</c>)
/// for the <c>consumes→</c> edge, or null when the method takes no request body.
/// </param>
/// <param name="FilePath">The endpoint's file (workspace-relative), for the edge evidence.</param>
/// <param name="Line">The 1-based line of the endpoint declaration, for the edge evidence (file:line).</param>
public sealed record ControllerEndpoint(
    string? SymbolId,
    string VerbKey,
    string? ClassRoute,
    string? MethodRoute,
    string ParentClassName,
    string MethodName,
    string ReturnType,
    string? RequestBodyType,
    string FilePath,
    int Line);

/// <summary>
/// The in-memory contract collections <see cref="RouteBridge"/> consumes (design §4 Leg 1; plan Task 7). Pure value
/// input — no DB, no I/O. The DB loader (plan Task 9) / graph builder (Task 8) builds these from julie rows; the leg
/// never reads SQLite.
/// </summary>
/// <param name="ClientCalls">
/// The TS/JS client HTTP calls (url literals already located by the builder): each that survives the frontend-language
/// + non-test filter and matches an endpoint's (verb, route) yields a <see cref="BridgeKind.Hits"/> edge.
/// </param>
/// <param name="Endpoints">
/// The C# controller endpoints: each yields a <see cref="BridgeKind.Hits"/> edge per matching client, plus a
/// <see cref="BridgeKind.Responds"/> edge when its return type unwraps to a named DTO and a
/// <see cref="BridgeKind.Consumes"/> edge when it declares a request body type.
/// </param>
public sealed record RouteBridgeInput(
    IReadOnlyList<TsClientCall> ClientCalls,
    IReadOnlyList<ControllerEndpoint> Endpoints);

/// <summary>
/// Leg 1 of the cross-language resolver (design §4): builds candidate edges linking a TS client HTTP call to the C#
/// controller endpoint it hits (<see cref="BridgeKind.Hits"/>), and linking that endpoint to its response DTO
/// (<see cref="BridgeKind.Responds"/>) and request DTO (<see cref="BridgeKind.Consumes"/>). PURE Miller.Core — it
/// operates over the in-memory <see cref="RouteBridgeInput"/>, normalizes routes via <see cref="RouteNormalizer"/>,
/// resolves DTO/endpoint names via <see cref="SymbolResolver"/>, unwraps return types via
/// <see cref="FieldSetExtractor.UnwrapReturnType"/>, and emits typed <see cref="CandidateEdge"/>s. It NEVER scores,
/// bands, or re-implements confidence logic; every signal it emits is decidable by <see cref="BridgeScorer"/> from the
/// candidate payload alone (the trust contract, design §5).
///
/// <para><b>Hits (TS call ⇄ endpoint).</b> Both sides are normalized with <see cref="RouteNormalizer"/> — the client
/// side via <see cref="RouteNormalizer.FromClientCall"/> (verb from the carrier, or verb-unknown), the endpoint side
/// via <see cref="RouteNormalizer.FromEndpoint"/> (which expands <c>[controller]</c>/<c>[action]</c>/<c>[area]</c> using
/// the parent class + method name BEFORE prefix concat). When the canonical routes are equal:
/// <list type="bullet">
/// <item>verb KNOWN on the client AND the verbs match ⇒ <see cref="SignalRule.RouteVerbMatch"/> (High-eligible);</item>
/// <item>verb UNKNOWN on the client (fetch/$fetch/bare axios/etc.) ⇒ <see cref="SignalRule.RouteOnlyMatch"/> on route
///   alone (Medium, never High; the scorer sets <c>IsVerbUnknown</c>). The verb is NEVER assumed GET;</item>
/// <item>verb KNOWN on both but the verbs differ ⇒ NO edge (a real verb distinction, not a route-only fallback).</item>
/// </list>
/// The endpoint is a resolved symbol, so its <see cref="EdgeRef"/> is trivially <see cref="ResolutionStatus.Resolved"/>;
/// the client side is a route node (no symbol), likewise trivially resolved. The collision guard (design §8 risk 3) is
/// inherited from <see cref="RouteNormalizer"/>: two controllers' <c>{id}</c> templates normalize to distinct routes, so
/// a client matches only its own controller — no false merge.</para>
///
/// <para><b>Responds (endpoint ⇄ response DTO) — PARTIAL.</b> The endpoint return type is unwrapped by balanced bracket
/// depth (<see cref="FieldSetExtractor.UnwrapReturnType"/>) to a named user type (class/interface/record, incl. a
/// collection element like <c>IProject</c>). A bare <c>ActionResult</c>/<c>IActionResult</c> or a primitive
/// (<c>Task&lt;bool&gt;</c>) unwraps to null ⇒ NO Responds edge (and no penalty to the Hits match). When a name is
/// recovered it is resolved by <see cref="SymbolResolver"/> and emitted as <see cref="SignalRule.ReturnTypeDto"/>.</para>
///
/// <para><b>Consumes (endpoint ⇄ request DTO).</b> The method's <c>[FromBody]</c>/request parameter type, when present,
/// is resolved by name and emitted as <see cref="SignalRule.FromBodyDto"/>. No request type ⇒ no Consumes edge.</para>
///
/// <para>The responds/consumes edges describe the endpoint's shape and do NOT depend on any client matching it — an
/// endpoint with no caller still yields them. An unresolved/ambiguous DTO name is reflected in the candidate's
/// <see cref="EdgeRef.Resolution"/> + a <see cref="NameResolutionSignal"/> so the scorer (not the leg) applies the §5
/// drop/cap rules.</para>
/// </summary>
public static class RouteBridge
{
    /// <summary>
    /// Build the route-bridge candidate edges from <paramref name="input"/>, normalizing routes and resolving DTO names
    /// through <paramref name="resolver"/>. Returns one <see cref="BridgeKind.Hits"/> edge per matching
    /// (client, endpoint) pair, one <see cref="BridgeKind.Responds"/> edge per endpoint whose return type unwraps to a
    /// named DTO, and one <see cref="BridgeKind.Consumes"/> edge per endpoint that declares a request body type. The leg
    /// does NOT score; it never returns a band.
    /// </summary>
    /// <param name="input">The in-memory client calls + controller endpoints.</param>
    /// <param name="resolver">The name resolver over the workspace's symbols.</param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="resolver"/> is null.</exception>
    public static IReadOnlyList<CandidateEdge> Resolve(RouteBridgeInput input, SymbolResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(resolver);

        var edges = new List<CandidateEdge>();

        // Normalize every real client call once (filter to frontend-language + non-test url literals first).
        var normalizedCalls = new List<(TsClientCall Call, NormalizedRoute Route)>();
        foreach (var call in input.ClientCalls)
        {
            if (!IsRealClientCall(call))
                continue;
            var route = RouteNormalizer.FromClientCall(call.Literal.Carrier, call.Literal.LiteralText);
            normalizedCalls.Add((call, route));
        }

        foreach (var endpoint in input.Endpoints)
        {
            var endpointRoute = RouteNormalizer.FromEndpoint(
                endpoint.VerbKey,
                endpoint.ClassRoute,
                endpoint.MethodRoute,
                endpoint.ParentClassName,
                endpoint.MethodName);

            // Hits: each client whose normalized (verb, route) matches this endpoint.
            foreach (var (call, callRoute) in normalizedCalls)
            {
                var hits = TryBuildHitsEdge(call, callRoute, endpoint, endpointRoute);
                if (hits is not null)
                    edges.Add(hits);
            }

            // Responds: the unwrapped return DTO (endpoint shape — independent of any client).
            var responds = TryBuildRespondsEdge(endpoint, resolver);
            if (responds is not null)
                edges.Add(responds);

            // Consumes: the request body DTO (endpoint shape — independent of any client).
            var consumes = TryBuildConsumesEdge(endpoint, resolver);
            if (consumes is not null)
                edges.Add(consumes);
        }

        return edges;
    }

    /// <summary>
    /// A real client call is a <c>kind=url</c> literal in a frontend/client language (NOT the C# endpoint language) and
    /// NOT an <c>is_test</c> HttpClient call (design §4 Leg 1; findings 28-2). The language-agnostic phrasing of the
    /// design's filter ("literal.language != the endpoint language") is implemented as "not the C# endpoint language":
    /// the route bridge's endpoints are C#, so a <c>csharp</c> url literal is a test HttpClient call, never a client call.
    /// </summary>
    private static bool IsRealClientCall(TsClientCall call)
    {
        if (!string.Equals(call.Literal.Kind, "url", StringComparison.OrdinalIgnoreCase))
            return false;
        if (call.IsTest)
            return false;
        // The endpoint side is C#; a url literal in the endpoint language is a (test) HttpClient call, not a client call.
        if (string.Equals(call.Literal.Language, "csharp", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>
    /// Build a <see cref="BridgeKind.Hits"/> edge when the client and endpoint canonical routes are equal, or null when
    /// they differ or a known client verb mismatches the endpoint verb. A verb-known match emits
    /// <see cref="SignalRule.RouteVerbMatch"/>; a verb-unknown client matches on route alone and emits
    /// <see cref="SignalRule.RouteOnlyMatch"/> (the verb is never assumed GET).
    /// </summary>
    private static CandidateEdge? TryBuildHitsEdge(
        TsClientCall call, NormalizedRoute callRoute, ControllerEndpoint endpoint, NormalizedRoute endpointRoute)
    {
        if (!string.Equals(callRoute.Route, endpointRoute.Route, StringComparison.Ordinal))
            return null;

        SignalRule rule;
        if (callRoute.VerbKnown)
        {
            // Verb known on the client: it must match the endpoint verb (and the endpoint verb must itself be known).
            if (!endpointRoute.VerbKnown ||
                !string.Equals(callRoute.Verb, endpointRoute.Verb, StringComparison.Ordinal))
                return null;
            rule = SignalRule.RouteVerbMatch;
        }
        else
        {
            // Verb unknown on the client: match on route alone (Medium, never High; never assumed GET).
            rule = SignalRule.RouteOnlyMatch;
        }

        var callEvidence = new Evidence(call.FilePath, call.Line);
        var endpointEvidence = new Evidence(endpoint.FilePath, endpoint.Line);

        // The client side is the containing frontend function when julie supplied one, falling back to a route node for
        // legacy or malformed rows. This makes trace bridge useful from the symbol agents naturally start with.
        var clientRef = ClientCallRef(call, callRoute.Route);
        var endpointDisplay = endpoint.SymbolId is null ? endpoint.MethodName : endpointRoute.Route;
        var endpointRef = new EdgeRef(
            Display: endpointDisplay,
            SymbolId: endpoint.SymbolId,
            FilePath: endpoint.FilePath,
            Resolution: new NameResolution(ResolutionStatus.Resolved, endpoint.SymbolId, 1));

        var signals = new List<Signal>
        {
            new StructuralSignal(rule, Present: true, endpointEvidence),
        };

        return new CandidateEdge(
            BridgeKind.Hits,
            clientRef,
            endpointRef,
            [callEvidence, endpointEvidence],
            signals);
    }

    /// <summary>
    /// Build a <see cref="BridgeKind.Responds"/> edge when the endpoint return type unwraps (balanced bracket depth) to
    /// a named user DTO, or null when it unwraps to a bare wrapper / primitive (no edge, no penalty). The recovered name
    /// is resolved by <paramref name="resolver"/>; an unresolved/ambiguous name rides in the candidate for the scorer.
    /// </summary>
    private static CandidateEdge? TryBuildRespondsEdge(ControllerEndpoint endpoint, SymbolResolver resolver)
    {
        var dtoName = FieldSetExtractor.UnwrapReturnType(endpoint.ReturnType);
        if (dtoName is null)
            return null;

        return BuildEndpointToDtoEdge(
            BridgeKind.Responds, SignalRule.ReturnTypeDto, endpoint, dtoName, resolver);
    }

    /// <summary>
    /// Build a <see cref="BridgeKind.Consumes"/> edge from the endpoint's <c>[FromBody]</c>/request parameter type, or
    /// null when the endpoint declares no request body. The type is resolved by <paramref name="resolver"/>; an
    /// unresolved/ambiguous name rides in the candidate for the scorer.
    /// </summary>
    private static CandidateEdge? TryBuildConsumesEdge(ControllerEndpoint endpoint, SymbolResolver resolver)
    {
        if (string.IsNullOrWhiteSpace(endpoint.RequestBodyType))
            return null;

        return BuildEndpointToDtoEdge(
            BridgeKind.Consumes, SignalRule.FromBodyDto, endpoint, endpoint.RequestBodyType, resolver);
    }

    /// <summary>
    /// Build an endpoint→DTO edge (the shared shape of the responds/consumes edges): the endpoint is the resolved
    /// source symbol; the DTO name is resolved by name into the target ref. Emits the structural breadcrumb
    /// <paramref name="rule"/> plus the per-side <see cref="NameResolutionSignal"/> for the DTO so the scorer applies
    /// the §5 unresolved/ambiguous rules from the payload alone.
    /// </summary>
    private static CandidateEdge BuildEndpointToDtoEdge(
        BridgeKind kind, SignalRule rule, ControllerEndpoint endpoint, string dtoName, SymbolResolver resolver)
    {
        var evidence = new Evidence(endpoint.FilePath, endpoint.Line);

        var endpointRef = new EdgeRef(
            Display: endpoint.MethodName,
            SymbolId: endpoint.SymbolId,
            FilePath: endpoint.FilePath,
            Resolution: new NameResolution(ResolutionStatus.Resolved, endpoint.SymbolId, 1));

        var dtoResolution = resolver.Resolve(dtoName, preferFile: endpoint.FilePath);
        var dtoRef = new EdgeRef(
            Display: LeafName(dtoName),
            SymbolId: dtoResolution.SymbolId,
            FilePath: dtoResolution.SymbolId is not null ? endpoint.FilePath : null,
            Resolution: dtoResolution);

        var signals = new List<Signal>
        {
            new StructuralSignal(rule, Present: true, evidence),
            new NameResolutionSignal(EndpointSide.Target, dtoResolution.Status, dtoResolution.MatchCount, evidence),
        };

        return new CandidateEdge(kind, endpointRef, dtoRef, [evidence], signals);
    }

    /// <summary>The client endpoint of a Hits edge: the containing frontend symbol when known, or a route node fallback.</summary>
    private static EdgeRef ClientCallRef(TsClientCall call, string route)
    {
        var symbolId = string.IsNullOrWhiteSpace(call.Literal.ContainingSymbolId)
            ? null
            : call.Literal.ContainingSymbolId;

        if (symbolId is null)
            return RouteNode(route, call.FilePath);

        return new EdgeRef(
            route,
            symbolId,
            call.FilePath,
            new NameResolution(ResolutionStatus.Resolved, symbolId, 1));
    }

    /// <summary>
    /// A route endpoint fallback of a Hits edge: a normalized route string, not a code symbol, so the ref is trivially
    /// <see cref="ResolutionStatus.Resolved"/> with no symbol id.
    /// </summary>
    private static EdgeRef RouteNode(string route, string filePath) =>
        new(route, SymbolId: null, FilePath: filePath, new NameResolution(ResolutionStatus.Resolved, null, 1));

    /// <summary>The leaf (simple) name of a possibly-qualified type name (<c>Api.Dtos.AppSetting</c> → <c>AppSetting</c>).</summary>
    private static string LeafName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return typeName;
        int dot = typeName.LastIndexOf('.');
        return (dot >= 0 && dot < typeName.Length - 1) ? typeName[(dot + 1)..] : typeName;
    }
}
