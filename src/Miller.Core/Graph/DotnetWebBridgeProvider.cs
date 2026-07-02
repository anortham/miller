using Miller.Core.Contracts;
using Miller.Core.Resolver;

namespace Miller.Core.Graph;

/// <summary>
/// The current ASP.NET / TypeScript bridge provider: controller routes (annotation-derived and
/// <c>aspnet.attribute_route.v1</c> structural facts), client URL literals and <c>http.client_request.v1</c>
/// fetch/axios facts, AutoMapper-style CreateMap pairs, and EF DbSet breadcrumbs.
/// </summary>
public sealed class DotnetWebBridgeProvider : IBridgeProvider
{
    public const string ProviderId = "dotnet-web";
    private const string AspNetMinimalApiRoutePattern = BridgeStructuralPatterns.AspNetMinimalApiRoute;
    private const string AspNetAttributeRoutePattern = BridgeStructuralPatterns.AspNetAttributeRoute;

    public static DotnetWebBridgeProvider Instance { get; } = new();

    private DotnetWebBridgeProvider()
    {
    }

    /// <summary>
    /// The structural client-call reduction: every call (route references + client requests) in
    /// <paramref name="Calls"/>, plus the <c>http.client_request.v1</c>-derived subset in
    /// <paramref name="ClientRequestCalls"/> — the covering set for <see cref="DedupeClientCalls"/> (only a
    /// structural client REQUEST describes the same fetch/axios call site a legacy url literal does; a
    /// route-reference fact never suppresses a literal).
    /// </summary>
    private sealed record StructuralClientCallReduction(
        IReadOnlyList<TsClientCall> Calls,
        IReadOnlyList<TsClientCall> ClientRequestCalls,
        (int Htmx, int Vue, int React, int NextJs, int Nuxt, int ClientRequests) Counts);

    /// <summary>
    /// The <c>aspnet.attribute_route.v1</c> reduction: the emitted endpoints plus the count of endpoint-shaped
    /// method-level facts consumed (<c>http_method</c> facts emitted as endpoints + <c>route</c> facts recognized
    /// but deferred — see <see cref="ReduceAttributeRouteEndpoints"/>). <c>controller_route</c> prefix facts are
    /// neither.
    /// </summary>
    private sealed record AttributeRouteReduction(
        IReadOnlyList<ControllerEndpoint> Endpoints,
        int EndpointFacts);

    public string Id => ProviderId;

    public BridgeProviderResult BuildCandidates(BridgeProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var createMaps = ReduceCreateMaps(context.TypeArguments);
        var annotationEndpoints = ReduceEndpoints(context.Symbols, context.Annotations);
        var minimalApiEndpoints = ReduceStructuralEndpoints(
            context.StructuralFacts, context.Symbols, context.Literals, context.SymbolsById, context.LiteralSites);
        var attributeRoutes = ReduceAttributeRouteEndpoints(context.StructuralFacts, context.SymbolsById);
        var structuralEndpoints = minimalApiEndpoints.Concat(attributeRoutes.Endpoints).ToList();
        var endpoints = DedupeEndpoints(annotationEndpoints, structuralEndpoints);
        var structuralClientCallReduction = ReduceStructuralClientCalls(context.StructuralFacts, context.SymbolsById);
        var structuralClientCalls = structuralClientCallReduction.Calls;
        var literalClientCalls = DedupeClientCalls(
            ReduceClientCalls(context.Literals, context.SymbolsById, context.LiteralSites),
            structuralClientCallReduction.ClientRequestCalls);
        var clientCalls = literalClientCalls.Concat(structuralClientCalls).ToList();
        var observationNodes = BuildStructuralRouteObservationNodes(structuralClientCalls, structuralEndpoints);
        var structuralCallCounts = structuralClientCallReduction.Counts;
        var serverTypeResolver = new SymbolResolver(context.Symbols.Where(IsCSharpUserType).ToArray());

        var evidenceCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["dotnet-web.createMaps"] = createMaps.Count,
            ["dotnet-web.endpoints"] = endpoints.Count,
            ["dotnet-web.clientCalls"] = clientCalls.Count,
            ["dotnet-web.structuralEndpoints"] = structuralEndpoints.Count,
            ["dotnet-web.structuralClientCalls"] = structuralClientCalls.Count,
            ["dotnet-web.structuralFacts"] = context.StructuralFacts.Count,
            ["dotnet-web.aspnetMinimalRoutes"] = minimalApiEndpoints.Count,
            ["dotnet-web.attributeRoutes"] = attributeRoutes.EndpointFacts,
            ["dotnet-web.htmxCalls"] = structuralCallCounts.Htmx,
            ["dotnet-web.vueCalls"] = structuralCallCounts.Vue,
            ["dotnet-web.reactCalls"] = structuralCallCounts.React,
            ["dotnet-web.nextjsCalls"] = structuralCallCounts.NextJs,
            ["dotnet-web.nuxtCalls"] = structuralCallCounts.Nuxt,
            ["dotnet-web.clientRequests"] = structuralCallCounts.ClientRequests,
            ["dotnet-web.dbsets"] = context.DbSetProperties.Count,
        };

        // The active gate is backend-evidence-based: client calls/requests alone never activate dotnet-web,
        // so a pure-frontend repo stays inactive (its client-request evidence counts are still reported).
        if (createMaps.Count == 0 &&
            endpoints.Count == 0 &&
            context.DbSetProperties.Count == 0)
        {
            return BridgeProviderResult.Skipped("no dotnet-web backend evidence", evidenceCounts);
        }

        var candidates = new List<CandidateEdge>();
        candidates.AddRange(EntityTableBridge.Resolve(
            new EntityTableInput(context.DbSetProperties, DapperFromCandidates: []), serverTypeResolver));
        candidates.AddRange(DtoEntityBridge.Resolve(
            new DtoEntityInput(createMaps, Projections: [], FieldSources: null), serverTypeResolver));
        candidates.AddRange(RouteBridge.Resolve(
            new RouteBridgeInput(clientCalls, endpoints), serverTypeResolver));

        evidenceCounts["dotnet-web.candidates"] = candidates.Count;
        return BridgeProviderResult.ActiveResult(candidates, evidenceCounts, observationNodes);
    }

    /// <summary>
    /// Group the <c>type_arguments</c> of each generic use-site into an ordinal 0/1 source/dest map candidate.
    /// </summary>
    private static IReadOnlyList<CreateMapCandidate> ReduceCreateMaps(IReadOnlyList<TypeArgument> typeArguments)
    {
        var groups = new Dictionary<string, CreateMapGroup>(StringComparer.Ordinal);

        foreach (var arg in typeArguments)
        {
            if (arg.ParentArgId is not null)
                continue;
            if (string.IsNullOrEmpty(arg.IdentifierId) || string.IsNullOrWhiteSpace(arg.TypeName))
                continue;

            if (!groups.TryGetValue(arg.IdentifierId, out var group))
            {
                group = new CreateMapGroup();
                groups[arg.IdentifierId] = group;
            }

            group.TopLevelArgCount++;
            if (arg.Ordinal == 0)
                group.Source ??= arg.TypeName;
            else if (arg.Ordinal == 1)
                group.Dest ??= arg.TypeName;
            group.FilePath ??= arg.FilePath;
        }

        var candidates = new List<CreateMapCandidate>();
        foreach (var identifierId in Sorted(groups.Keys))
        {
            var group = groups[identifierId];
            if (group.Source is null || group.Dest is null)
                continue;
            if (group.TopLevelArgCount != 2)
                continue;

            candidates.Add(new CreateMapCandidate(
                group.Source,
                group.Dest,
                group.FilePath ?? string.Empty,
                Line: 0,
                HasReverseMap: false));
        }
        return candidates;
    }

    private sealed class CreateMapGroup
    {
        public string? Source;
        public string? Dest;
        public string? FilePath;
        public int TopLevelArgCount;
    }

    private static IReadOnlyList<ControllerEndpoint> ReduceEndpoints(
        IReadOnlyList<SymbolDetail> symbols,
        IReadOnlyList<SymbolAnnotation> annotations)
    {
        var annotationsBySymbol = new Dictionary<string, List<SymbolAnnotation>>(StringComparer.Ordinal);
        foreach (var annotation in annotations)
        {
            if (!annotationsBySymbol.TryGetValue(annotation.SymbolId, out var list))
            {
                list = [];
                annotationsBySymbol[annotation.SymbolId] = list;
            }
            list.Add(annotation);
        }

        var classRouteByName = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var symbol in symbols.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (!IsClassKind(symbol.Kind))
                continue;
            if (classRouteByName.ContainsKey(symbol.Name))
                continue;
            classRouteByName[symbol.Name] =
                annotationsBySymbol.TryGetValue(symbol.Id, out var classAnnotations)
                    ? RouteArgOf(classAnnotations)
                    : null;
        }

        var endpoints = new List<ControllerEndpoint>();
        foreach (var method in symbols.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (!annotationsBySymbol.TryGetValue(method.Id, out var methodAnnotations))
                continue;

            var verbAnnotation = methodAnnotations.FirstOrDefault(a => IsHttpVerbKey(a.AnnotationKey));
            if (verbAnnotation is null)
                continue;

            var parentClassName = method.ParentClassName ?? string.Empty;
            if (parentClassName.Length == 0)
                continue;

            string? classRoute = classRouteByName.TryGetValue(parentClassName, out var route) ? route : null;

            var methodRoute = FirstStringArg(verbAnnotation.RawText);
            var (returnType, requestBodyType) = ParseSignatureTypes(method.Signature, verbAnnotation.AnnotationKey);

            endpoints.Add(new ControllerEndpoint(
                SymbolId: method.Id,
                VerbKey: verbAnnotation.AnnotationKey,
                ClassRoute: classRoute,
                MethodRoute: methodRoute,
                ParentClassName: parentClassName,
                MethodName: method.Name,
                ReturnType: returnType,
                RequestBodyType: requestBodyType,
                FilePath: method.FilePath,
                Line: 0));
        }
        return endpoints;
    }

    private static IReadOnlyList<ControllerEndpoint> ReduceStructuralEndpoints(
        IReadOnlyList<StructuralFactRecord> structuralFacts,
        IReadOnlyList<SymbolDetail> symbols,
        IReadOnlyList<LiteralRecord> literals,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        IReadOnlyDictionary<LiteralRecord, LiteralSite>? literalSites)
    {
        var endpoints = new List<ControllerEndpoint>();
        foreach (var fact in structuralFacts.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Span.StartByte))
        {
            if (!TryReduceStructuralEndpointFact(fact, symbols, literals, symbolsById, literalSites, out var endpoint))
                continue;

            endpoints.Add(endpoint);
        }
        return endpoints;
    }

    private static bool TryReduceStructuralEndpointFact(
        StructuralFactRecord fact,
        IReadOnlyList<SymbolDetail> symbols,
        IReadOnlyList<LiteralRecord> literals,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        IReadOnlyDictionary<LiteralRecord, LiteralSite>? literalSites,
        out ControllerEndpoint endpoint)
    {
        endpoint = null!;
        if (!string.Equals(fact.PatternId, AspNetMinimalApiRoutePattern, StringComparison.Ordinal))
            return false;

        var routeTemplate = StructuralRouteFactAdapter.MetadataString(fact, "route_template");
        var verb = StructuralRouteFactAdapter.MetadataString(fact, "verb");
        if (string.IsNullOrWhiteSpace(routeTemplate) || string.IsNullOrWhiteSpace(verb))
            return false;

        var handler = ResolveStructuralHandler(fact, symbols, symbolsById);
        var fullRoute = ComposeStructuralEndpointRoute(fact, routeTemplate, literals, symbolsById, literalSites);
        endpoint = new ControllerEndpoint(
            SymbolId: handler?.Id,
            VerbKey: ToHttpVerbKey(verb),
            ClassRoute: null,
            MethodRoute: fullRoute,
            ParentClassName: handler?.ParentClassName ?? string.Empty,
            MethodName: handler?.Name ?? SyntheticEndpointDisplay(verb, fullRoute),
            ReturnType: string.Empty,
            RequestBodyType: null,
            FilePath: handler?.FilePath ?? fact.Path,
            Line: fact.Span.StartLine);
        return true;
    }

    /// <summary>
    /// Reduce <c>aspnet.attribute_route.v1</c> facts (2.6.0) into controller endpoints beside the minimal-API arm.
    /// Only <c>attribute_kind="http_method"</c> facts become endpoints: the route is the extractor-composed
    /// <c>effective_route_template</c> (leading <c>/</c>, tokens pre-substituted) falling back to
    /// <c>route_template</c>, and the handler binds via <c>containing_symbol_id</c> (the action method).
    /// <c>controller_route</c> facts are class-level prefix facts, never endpoints. <c>route</c> facts (a method
    /// <c>[Route]</c> with no verb attribute) would be verb-unknown endpoints, but
    /// <c>RouteBridge.TryBuildHitsEdge</c> yields NO edge for a verb-known client against a verb-unknown endpoint
    /// (verified 2026-07-01), so they are counted as evidence only — endpoint emission is a noted follow-up.
    /// </summary>
    private static AttributeRouteReduction ReduceAttributeRouteEndpoints(
        IReadOnlyList<StructuralFactRecord> structuralFacts,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById)
    {
        var endpoints = new List<ControllerEndpoint>();
        int endpointFacts = 0;
        foreach (var fact in structuralFacts.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Span.StartByte))
        {
            if (!string.Equals(fact.PatternId, AspNetAttributeRoutePattern, StringComparison.Ordinal))
                continue;

            var attributeKind = StructuralRouteFactAdapter.MetadataString(fact, "attribute_kind");
            if (string.Equals(attributeKind, "route", StringComparison.Ordinal))
            {
                endpointFacts++;
                continue;
            }

            if (!string.Equals(attributeKind, "http_method", StringComparison.Ordinal))
                continue;

            var verb = StructuralRouteFactAdapter.MetadataString(fact, "verb");
            if (string.IsNullOrWhiteSpace(verb))
                continue;

            // route_template is ABSENT for a bare [HttpGet]; effective_route_template is absent only when
            // neither template exists (conventional routing) — then there is no route evidence to join on.
            var route = StructuralRouteFactAdapter.MetadataString(fact, "effective_route_template")
                ?? StructuralRouteFactAdapter.MetadataString(fact, "route_template");
            if (string.IsNullOrWhiteSpace(route))
                continue;

            endpointFacts++;

            SymbolDetail? handler = !string.IsNullOrWhiteSpace(fact.ContainingSymbolId) &&
                                    symbolsById.TryGetValue(fact.ContainingSymbolId, out var containing)
                ? containing
                : null;

            var verbKey = ToHttpVerbKey(verb);
            var (returnType, requestBodyType) = handler is null
                ? (string.Empty, (string?)null)
                : ParseSignatureTypes(handler.Signature, verbKey);

            endpoints.Add(new ControllerEndpoint(
                SymbolId: handler?.Id,
                VerbKey: verbKey,
                ClassRoute: null,
                MethodRoute: route,
                ParentClassName: handler?.ParentClassName ?? string.Empty,
                MethodName: handler?.Name ?? SyntheticEndpointDisplay(verb, route),
                ReturnType: returnType,
                RequestBodyType: requestBodyType,
                FilePath: handler?.FilePath ?? fact.Path,
                Line: fact.Span.StartLine));
        }
        return new AttributeRouteReduction(endpoints, endpointFacts);
    }

    /// <summary>
    /// Collapse overlapping endpoint evidence: a structural attribute-route endpoint and an annotation-derived
    /// endpoint for the same (method SymbolId, VerbKey) describe ONE endpoint — the structural fact wins (its
    /// extractor-composed template is richer: <c>[action]</c> substitution, absolute <c>/</c>/<c>~/</c>
    /// overrides, tokens pre-substituted).
    /// </summary>
    private static List<ControllerEndpoint> DedupeEndpoints(
        IReadOnlyList<ControllerEndpoint> annotationEndpoints,
        IReadOnlyList<ControllerEndpoint> structuralEndpoints)
    {
        var structuralKeys = new HashSet<(string SymbolId, string VerbKey)>(
            structuralEndpoints
                .Where(endpoint => !string.IsNullOrEmpty(endpoint.SymbolId))
                .Select(endpoint => (endpoint.SymbolId!, endpoint.VerbKey.ToLowerInvariant())));

        var endpoints = new List<ControllerEndpoint>();
        foreach (var endpoint in annotationEndpoints)
        {
            if (!string.IsNullOrEmpty(endpoint.SymbolId) &&
                structuralKeys.Contains((endpoint.SymbolId, endpoint.VerbKey.ToLowerInvariant())))
                continue;
            endpoints.Add(endpoint);
        }
        endpoints.AddRange(structuralEndpoints);
        return endpoints;
    }

    /// <summary>
    /// Collapse overlapping client-call evidence: a legacy url literal and an <c>http.client_request.v1</c>
    /// structural fact for the SAME call site describe ONE client call — the structural request wins (its verb
    /// is source-attested; the legacy literal is verb-unknown, and letting it through fabricates Medium
    /// route-only edges to endpoints whose verb is KNOWN to differ — a different target means a different edge
    /// signature, so graph dedupe cannot collapse them). Same site = same canonical route
    /// (<see cref="RouteNormalizer"/>) AND same containing symbol id; when either side has no containing
    /// symbol, same file path + same canonical route. A literal with no covering structural request
    /// (ky/got/$fetch/HttpClient wrappers, …) always survives — suppression is per-site, never global.
    /// </summary>
    private static List<TsClientCall> DedupeClientCalls(
        IReadOnlyList<TsClientCall> literalClientCalls,
        IReadOnlyList<TsClientCall> structuralClientRequests)
    {
        if (structuralClientRequests.Count == 0)
            return literalClientCalls.ToList();

        var symbolKeys = new HashSet<(string SymbolId, string Route)>();
        var fileKeys = new HashSet<(string FilePath, string Route)>();
        var symbollessFileKeys = new HashSet<(string FilePath, string Route)>();

        foreach (var request in structuralClientRequests)
        {
            var route = CanonicalClientRoute(request);
            if (route.Length == 0)
                continue;

            if (!string.IsNullOrEmpty(request.Literal.ContainingSymbolId))
                symbolKeys.Add((request.Literal.ContainingSymbolId, route));
            else if (request.FilePath.Length > 0)
                symbollessFileKeys.Add((request.FilePath, route));

            if (request.FilePath.Length > 0)
                fileKeys.Add((request.FilePath, route));
        }

        var calls = new List<TsClientCall>();
        foreach (var call in literalClientCalls)
        {
            if (IsCoveredByStructuralRequest(call, symbolKeys, fileKeys, symbollessFileKeys))
                continue;
            calls.Add(call);
        }
        return calls;
    }

    private static bool IsCoveredByStructuralRequest(
        TsClientCall call,
        HashSet<(string SymbolId, string Route)> symbolKeys,
        HashSet<(string FilePath, string Route)> fileKeys,
        HashSet<(string FilePath, string Route)> symbollessFileKeys)
    {
        var route = CanonicalClientRoute(call);
        if (route.Length == 0)
            return false;

        if (!string.IsNullOrEmpty(call.Literal.ContainingSymbolId))
        {
            if (symbolKeys.Contains((call.Literal.ContainingSymbolId, route)))
                return true;
            // The structural side has no containing symbol: fall back to file path + canonical route.
            return call.FilePath.Length > 0 && symbollessFileKeys.Contains((call.FilePath, route));
        }

        // The literal side has no containing symbol: fall back to file path + canonical route.
        return call.FilePath.Length > 0 && fileKeys.Contains((call.FilePath, route));
    }

    private static string CanonicalClientRoute(TsClientCall call) =>
        RouteNormalizer.FromClientCall(call.Literal.Carrier, call.Literal.LiteralText).Route;

    private static string SyntheticEndpointDisplay(string verb, string route)
    {
        var displayRoute = RouteNormalizer.FromClientCall("fetch", route).Route;
        if (displayRoute.Length > 0 && !displayRoute.StartsWith("/", StringComparison.Ordinal))
            displayRoute = "/" + displayRoute;
        return string.Concat(verb.Trim().ToUpperInvariant(), " ", displayRoute.Length == 0 ? route : displayRoute);
    }

    private static IReadOnlyDictionary<string, BridgeNode> BuildStructuralRouteObservationNodes(
        IReadOnlyList<TsClientCall> structuralClientCalls,
        IReadOnlyList<ControllerEndpoint> structuralEndpoints)
    {
        var nodes = new Dictionary<string, BridgeNode>(StringComparer.Ordinal);

        foreach (var call in structuralClientCalls)
        {
            var route = RouteNormalizer.FromClientCall(call.Literal.Carrier, call.Literal.LiteralText).Route;
            if (route.Length == 0)
                continue;

            var display = RouteDisplay(route);
            var id = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, display);
            nodes.TryAdd(id, new BridgeNode(id, BridgeNodeKind.TsType, display, call.FilePath, call.Line));
        }

        foreach (var endpoint in structuralEndpoints)
        {
            var route = RouteNormalizer.FromEndpoint(
                endpoint.VerbKey,
                endpoint.ClassRoute,
                endpoint.MethodRoute,
                endpoint.ParentClassName,
                endpoint.MethodName);
            if (route.Route.Length == 0)
                continue;

            var display = SyntheticEndpointDisplay(HttpVerbDisplay(endpoint.VerbKey), route.Route);
            var id = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, display);
            nodes.TryAdd(id, new BridgeNode(id, BridgeNodeKind.Endpoint, display, endpoint.FilePath, endpoint.Line));
        }

        return nodes;
    }

    private static string RouteDisplay(string route) =>
        route.StartsWith("/", StringComparison.Ordinal) ? route : "/" + route;

    private static string HttpVerbDisplay(string verbKey) =>
        verbKey.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? verbKey[4..].ToUpperInvariant()
            : verbKey.ToUpperInvariant();

    private static StructuralClientCallReduction ReduceStructuralClientCalls(
        IReadOnlyList<StructuralFactRecord> structuralFacts,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById)
    {
        var calls = new List<TsClientCall>();
        var clientRequestCalls = new List<TsClientCall>();
        int htmx = 0;
        int vue = 0;
        int react = 0;
        int nextjs = 0;
        int nuxt = 0;
        int clientRequests = 0;
        foreach (var fact in structuralFacts.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Span.StartByte))
        {
            if (!IsStructuralClientCallPattern(fact.PatternId))
                continue;

            if (string.Equals(fact.PatternId, BridgeStructuralPatterns.HttpClientRequest, StringComparison.Ordinal))
            {
                if (StructuralRouteFactAdapter.TryReadClientRequest(fact, symbolsById, out var request))
                {
                    clientRequests++;
                    var call = ToClientCall(request);
                    calls.Add(call);
                    clientRequestCalls.Add(call);
                }
                continue;
            }

            if (StructuralRouteFactAdapter.TryReadRouteReference(fact, symbolsById, out var reference))
            {
                CountStructuralRouteReferencePattern(fact.PatternId, ref htmx, ref vue, ref react, ref nextjs, ref nuxt);
                calls.Add(ToClientCall(reference));
            }
        }
        return new StructuralClientCallReduction(calls, clientRequestCalls, (htmx, vue, react, nextjs, nuxt, clientRequests));
    }

    private static bool IsStructuralClientCallPattern(string patternId) =>
        string.Equals(patternId, BridgeStructuralPatterns.HtmxAttribute, StringComparison.Ordinal) ||
        string.Equals(patternId, BridgeStructuralPatterns.VueRouteReference, StringComparison.Ordinal) ||
        string.Equals(patternId, BridgeStructuralPatterns.ReactRouteReference, StringComparison.Ordinal) ||
        string.Equals(patternId, BridgeStructuralPatterns.NextJsRouteReference, StringComparison.Ordinal) ||
        string.Equals(patternId, BridgeStructuralPatterns.NuxtRouteReference, StringComparison.Ordinal) ||
        string.Equals(patternId, BridgeStructuralPatterns.HttpClientRequest, StringComparison.Ordinal);

    private static void CountStructuralRouteReferencePattern(
        string patternId,
        ref int htmx,
        ref int vue,
        ref int react,
        ref int nextjs,
        ref int nuxt)
    {
        switch (patternId)
        {
            case BridgeStructuralPatterns.HtmxAttribute:
                htmx++;
                break;
            case BridgeStructuralPatterns.VueRouteReference:
                vue++;
                break;
            case BridgeStructuralPatterns.ReactRouteReference:
                react++;
                break;
            case BridgeStructuralPatterns.NextJsRouteReference:
                nextjs++;
                break;
            case BridgeStructuralPatterns.NuxtRouteReference:
                nuxt++;
                break;
        }
    }

    private static TsClientCall ToClientCall(StructuralClientRequest request)
    {
        var literal = new LiteralRecord(
            LiteralText: request.RoutePath,
            Kind: "url",
            Carrier: ClientRequestCarrier(request),
            ArgPosition: 0,
            Language: request.Fact.Language,
            ContainingSymbolId: request.ContainingSymbolId,
            Span: new SourceSpan(request.Fact.Span.StartByte, request.Fact.Span.EndByte));
        // The attested verb rides on the call directly: the synthesized carrier round-trip is lossy for
        // non-whitelist verbs (fetch(url, {method:"PURGE"}) attests PURGE, which VerbFromCarrier cannot carry).
        return new TsClientCall(literal, IsTest: false, request.FilePath, request.Line, AttestedVerb: request.Verb);
    }

    /// <summary>
    /// Carrier synthesis for an <c>http.client_request.v1</c> fact: <c>"&lt;client&gt;.&lt;lowerverb&gt;"</c>
    /// (<c>fetch.get</c>, <c>axios.post</c>, …), kept for display continuity — the verb itself is carried by
    /// <see cref="TsClientCall.AttestedVerb"/> so a non-whitelist attested verb (PURGE) never degrades to
    /// verb-unknown in the round-trip. Both <c>verb_source</c> values (<c>attested</c> and <c>default</c>) are
    /// verb-known per the verb-honesty doctrine — the extractor stays silent when the method option is
    /// non-literal, so <c>default</c> genuinely means the runtime verb is GET by spec.
    /// </summary>
    private static string ClientRequestCarrier(StructuralClientRequest request) =>
        request.Client.ToLowerInvariant() + "." + request.Verb.ToLowerInvariant();

    private static TsClientCall ToClientCall(StructuralRouteReference reference)
    {
        var literal = new LiteralRecord(
            LiteralText: reference.RoutePath,
            Kind: "url",
            Carrier: StructuralCarrier(reference.Fact.PatternId, reference.Verb),
            ArgPosition: 0,
            Language: reference.Fact.Language,
            ContainingSymbolId: reference.ContainingSymbolId,
            Span: new SourceSpan(reference.Fact.Span.StartByte, reference.Fact.Span.EndByte));
        return new TsClientCall(literal, IsTest: false, reference.FilePath, reference.Line);
    }

    private static string StructuralCarrier(string patternId, string? verb)
    {
        if (string.IsNullOrWhiteSpace(verb))
        {
            return patternId switch
            {
                BridgeStructuralPatterns.VueRouteReference => "vue",
                BridgeStructuralPatterns.ReactRouteReference => "react",
                BridgeStructuralPatterns.NextJsRouteReference => "nextjs",
                BridgeStructuralPatterns.NuxtRouteReference => "nuxt",
                _ => "route",
            };
        }

        var lowerVerb = verb.ToLowerInvariant();
        return patternId switch
        {
            BridgeStructuralPatterns.HtmxAttribute => "htmx." + lowerVerb,
            BridgeStructuralPatterns.VueRouteReference or BridgeStructuralPatterns.VueRouteDefinition => "vue." + lowerVerb,
            BridgeStructuralPatterns.ReactRouteReference or BridgeStructuralPatterns.ReactRouteDefinition => "react." + lowerVerb,
            BridgeStructuralPatterns.NextJsRouteReference => "nextjs." + lowerVerb,
            BridgeStructuralPatterns.NuxtRouteReference => "nuxt." + lowerVerb,
            _ => verb.ToUpperInvariant(),
        };
    }

    private static SymbolDetail? ResolveStructuralHandler(
        StructuralFactRecord fact,
        IReadOnlyList<SymbolDetail> symbols,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById)
    {
        var handlerName = StructuralRouteFactAdapter.MetadataString(fact, "handler_name");
        if (string.IsNullOrWhiteSpace(handlerName))
        {
            return !string.IsNullOrWhiteSpace(fact.ContainingSymbolId) &&
                   symbolsById.TryGetValue(fact.ContainingSymbolId, out var containing)
                ? containing
                : null;
        }

        return symbols
            .Where(symbol => string.Equals(symbol.Name, handlerName, StringComparison.Ordinal)
                             && string.Equals(symbol.FilePath, fact.Path, StringComparison.Ordinal)
                             && string.Equals(symbol.Kind, "method", StringComparison.OrdinalIgnoreCase))
            .OrderBy(symbol => symbol.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string ComposeStructuralEndpointRoute(
        StructuralFactRecord fact,
        string routeTemplate,
        IReadOnlyList<LiteralRecord> literals,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        IReadOnlyDictionary<LiteralRecord, LiteralSite>? literalSites)
    {
        var effectiveRoute = StructuralRouteFactAdapter.MetadataString(fact, "effective_route_template");
        if (!string.IsNullOrWhiteSpace(effectiveRoute))
            return effectiveRoute;

        var explicitPrefix = StructuralRouteFactAdapter.MetadataString(fact, "route_group_prefix")
            ?? StructuralRouteFactAdapter.MetadataString(fact, "group_prefix")
            ?? StructuralRouteFactAdapter.MetadataString(fact, "route_prefix");
        var prefix = string.IsNullOrWhiteSpace(explicitPrefix)
            ? NearestMapGroupPrefix(fact, literals, symbolsById, literalSites)
            : explicitPrefix;

        return string.IsNullOrWhiteSpace(prefix)
            ? routeTemplate
            : JoinRoute(prefix, routeTemplate);
    }

    private static string? NearestMapGroupPrefix(
        StructuralFactRecord fact,
        IReadOnlyList<LiteralRecord> literals,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        IReadOnlyDictionary<LiteralRecord, LiteralSite>? literalSites)
    {
        if (string.IsNullOrWhiteSpace(fact.ContainingSymbolId))
            return null;

        return literals
            .Where(literal => string.Equals(literal.ContainingSymbolId, fact.ContainingSymbolId, StringComparison.Ordinal)
                              && literal.Span.StartByte < fact.Span.StartByte
                              && string.Equals(literal.Language, "csharp", StringComparison.OrdinalIgnoreCase)
                              && literal.Carrier.Contains("MapGroup", StringComparison.Ordinal)
                              && string.Equals(SiteFor(literal, symbolsById, literalSites).FilePath, fact.Path, StringComparison.Ordinal))
            .OrderByDescending(literal => literal.Span.StartByte)
            .Select(literal => literal.LiteralText)
            .FirstOrDefault();
    }

    private static string ToHttpVerbKey(string verb) => "http" + verb.Trim().ToLowerInvariant();

    private static string JoinRoute(string prefix, string route)
    {
        var p = prefix.Trim('/');
        var r = route.Trim('/');
        if (p.Length == 0)
            return r;
        if (r.Length == 0)
            return p;
        return "/" + p + "/" + r;
    }

    private static bool IsClassKind(string kind) =>
        string.Equals(kind, "class", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "record", StringComparison.OrdinalIgnoreCase);

    private static bool IsCSharpUserType(SymbolDetail symbol) =>
        IsCSharpFile(symbol.FilePath) && IsUserTypeKind(symbol.Kind);

    private static bool IsCSharpFile(string filePath) =>
        filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsUserTypeKind(string kind) =>
        string.Equals(kind, "class", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "record", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "interface", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "struct", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "enum", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] HttpVerbKeys =
    [
        "httpget", "httppost", "httpput", "httpdelete", "httppatch", "httphead", "httpoptions",
    ];

    private static bool IsHttpVerbKey(string key)
    {
        foreach (var verb in HttpVerbKeys)
        {
            if (string.Equals(key, verb, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static readonly string[] BodyBearingVerbKeys = ["httppost", "httpput", "httppatch"];

    private static bool IsBodyBearingVerb(string verbKey)
    {
        foreach (var verb in BodyBearingVerbKeys)
        {
            if (string.Equals(verbKey, verb, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? RouteArgOf(List<SymbolAnnotation> classAnnotations)
    {
        foreach (var annotation in classAnnotations)
        {
            if (string.Equals(annotation.AnnotationKey, "route", StringComparison.OrdinalIgnoreCase))
                return FirstStringArg(annotation.RawText);
        }
        return null;
    }

    private static (string ReturnType, string? RequestBodyType) ParseSignatureTypes(string signature, string verbKey)
    {
        var returnType = ParseReturnType(signature);
        string? requestBodyType = null;

        if (IsBodyBearingVerb(verbKey))
            requestBodyType = ParseRequestBodyType(signature);

        return (returnType, requestBodyType);
    }

    private static string ParseReturnType(string signature)
    {
        var sig = (signature ?? string.Empty).Trim();
        int open = TopLevelChar(sig, '(');
        if (open < 0)
            return sig;

        var head = sig[..open].Trim();
        int lastSpace = LastTopLevelSpace(head);
        return lastSpace <= 0 ? string.Empty : head[..lastSpace].Trim();
    }

    private static string? ParseRequestBodyType(string signature)
    {
        var sig = signature ?? string.Empty;
        int open = TopLevelChar(sig, '(');
        if (open < 0)
            return null;
        var inner = BalancedInner(sig, open);
        if (inner is null || inner.Trim().Length == 0)
            return null;

        foreach (var param in SplitTopLevel(inner))
        {
            var type = ParamType(param);
            if (type is null)
                continue;
            if (IsPlausibleBodyType(type))
                return type;
        }
        return null;
    }

    private static string? ParamType(string param)
    {
        var p = param.Trim();
        if (p.Length == 0)
            return null;

        int eq = TopLevelIndexOf(p, '=');
        if (eq >= 0)
            p = p[..eq].Trim();

        int lastSpace = LastTopLevelSpace(p);
        if (lastSpace <= 0)
            return null;

        var type = p[..lastSpace].Trim();
        return type.Length == 0 ? null : type;
    }

    private static bool IsPlausibleBodyType(string type)
    {
        var t = type.TrimEnd('?').Trim();
        if (t.Length == 0)
            return false;
        if (t.IndexOfAny(['<', '>', '[', ']']) >= 0)
            return false;
        if (Primitives.Contains(t))
            return false;
        return char.IsUpper(t[0]);
    }

    private static readonly HashSet<string> Primitives = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short",
        "ushort", "string", "object", "void", "Guid", "DateTime", "DateTimeOffset", "TimeSpan", "Boolean", "Int32",
        "Int64", "Int16", "Double", "Single", "Decimal", "String", "Object", "Byte", "Char", "CancellationToken",
    };

    private static IReadOnlyList<TsClientCall> ReduceClientCalls(
        IReadOnlyList<LiteralRecord> literals,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        IReadOnlyDictionary<LiteralRecord, LiteralSite>? literalSites)
    {
        var calls = new List<TsClientCall>();
        var ordered = literals
            .Where(l => string.Equals(l.Kind, "url", StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.Span.StartByte)
            .ThenBy(l => l.ContainingSymbolId, StringComparer.Ordinal);

        foreach (var literal in ordered)
        {
            bool isTest = false;
            if (!string.IsNullOrEmpty(literal.ContainingSymbolId) &&
                symbolsById.TryGetValue(literal.ContainingSymbolId, out var container))
            {
                isTest = container.IsTest;
            }

            var site = SiteFor(literal, symbolsById, literalSites);
            calls.Add(new TsClientCall(literal, isTest, site.FilePath, site.Line));
        }
        return calls;
    }

    private static LiteralSite SiteFor(
        LiteralRecord literal,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        IReadOnlyDictionary<LiteralRecord, LiteralSite>? literalSites)
    {
        if (literalSites is not null && literalSites.TryGetValue(literal, out var site))
            return site;

        if (!string.IsNullOrEmpty(literal.ContainingSymbolId) &&
            symbolsById.TryGetValue(literal.ContainingSymbolId, out var container))
            return new LiteralSite(container.FilePath, 0);

        return new LiteralSite(string.Empty, 0);
    }

    private static IEnumerable<string> Sorted(IEnumerable<string> keys)
    {
        var list = keys.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static string? FirstStringArg(string rawText)
    {
        if (rawText is null)
            return null;
        int start = rawText.IndexOf('"');
        if (start < 0)
            return null;
        int end = rawText.IndexOf('"', start + 1);
        if (end <= start)
            return null;
        return rawText[(start + 1)..end];
    }

    private static int TopLevelChar(string s, char target)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is '<' or '[')
                depth++;
            else if (ch is '>' or ']')
                depth--;
            else if (ch == target && depth == 0)
                return i;
        }
        return -1;
    }

    private static string? BalancedInner(string s, int open)
    {
        char openCh = s[open];
        char closeCh = openCh switch { '<' => '>', '(' => ')', '[' => ']', '{' => '}', _ => '\0' };
        if (closeCh == '\0')
            return null;

        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == openCh)
                depth++;
            else if (s[i] == closeCh)
            {
                depth--;
                if (depth == 0)
                    return s[(open + 1)..i];
            }
        }
        return null;
    }

    private static IEnumerable<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        int depth = 0;
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '<' or '(' or '[':
                    depth++;
                    current.Append(ch);
                    break;
                case '>' or ')' or ']':
                    depth--;
                    current.Append(ch);
                    break;
                case ',' when depth == 0:
                    parts.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    private static int TopLevelIndexOf(string s, char target)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is '<' or '(' or '[')
                depth++;
            else if (ch is '>' or ')' or ']')
                depth--;
            else if (ch == target && depth == 0)
                return i;
        }
        return -1;
    }

    private static int LastTopLevelSpace(string s)
    {
        int depth = 0, last = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is '<' or '(' or '[')
                depth++;
            else if (ch is '>' or ')' or ']')
                depth--;
            else if ((ch == ' ' || ch == '\t') && depth == 0)
                last = i;
        }
        return last;
    }
}
