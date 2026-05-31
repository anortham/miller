using Miller.Core.Contracts;
using Miller.Core.Resolver;

namespace Miller.Core.Graph;

/// <summary>
/// Assembles the in-memory <see cref="BridgeGraph"/> from the raw julie-derived contract collections (plan Task 8;
/// design §3/§4). PURE Miller.Core — it takes already-loaded value records (the DB reader, plan Task 9, supplies them)
/// and performs the per-leg REDUCTIONS the legs explicitly delegate to the builder, then runs the three legs, scores
/// every candidate, and builds the graph. No DB, no I/O.
///
/// <para>The reductions the builder owns (each is named in the corresponding leg's doc-comment as "the graph builder's
/// job, out of this leg's scope"):
/// <list type="bullet">
/// <item><b>CreateMap grouping</b> — group <c>type_arguments</c> by <c>identifier_id</c> into A/B pairs (ordinal 0 =
/// copy-source, ordinal 1 = copy-dest into a <see cref="CreateMapCandidate"/>). The ordinal IS the copy direction — the
/// builder NEVER classifies entity-vs-DTO. Only un-nested (no <c>parent_arg_id</c>) two-arg groups are taken; the leg +
/// scorer + name-resolution gate then filter (an unresolvable pair yields no edge).</item>
/// <item><b>Controller endpoint reduction</b> — for method symbols carrying an http-verb annotation, find the parent
/// class by <see cref="SymbolDetail.ParentClassName"/> for the class <c>[Route]</c>, parse the method route from the
/// verb annotation <c>raw_text</c>, and parse the return + request-body types from the method <c>signature</c> into a
/// <see cref="ControllerEndpoint"/>.</item>
/// <item><b>TsClientCall reduction</b> — for <c>kind='url'</c> literals, attach the containing symbol's
/// <c>test_role</c> and the use-site file:line into a <see cref="TsClientCall"/>.</item>
/// </list></para>
///
/// <para><b>Contract gap (flagged, not silently worked around).</b> The Dapper-FROM secondary anchor (Leg 3's
/// <see cref="DapperFromCandidate"/>) cannot be produced here: it requires pairing a <c>kind='sql'</c> literal to a
/// co-located entity <c>type_argument</c> by span proximity within the same containing symbol, but the verified
/// <see cref="TypeArgument"/> contract carries NEITHER a <c>containing_symbol_id</c> NOR a span — so there is no key or
/// proximity signal to pair on (and <see cref="LiteralRecord"/> carries no <c>identifier_id</c> for a join — findings
/// 28-2). The builder therefore emits no Dapper candidates; the DbSet&lt;T&gt; property remains Leg 3's PRIMARY (and on
/// the verified MyraNext shape, 13/15 sql literals have no FROM clause anyway, so the Dapper path is opportunistic).
/// Task 9 / a contract revision can add the co-location fields to <see cref="TypeArgument"/> to re-enable it.</para>
///
/// <para><b>Literal evidence seam (Task 9 must match this).</b> <see cref="LiteralRecord"/> surfaces only a byte
/// <c>span</c> + <c>containing_symbol_id</c>; it does not re-expose the <c>literals</c> row's own file/line columns. So
/// the builder takes a reader-supplied <c>literal → (file, line)</c> lookup (<paramref name="literalSites"/> on
/// <see cref="Build"/>) rather than extending <see cref="LiteralRecord"/> — keeping Miller.Core free of julie row-shape
/// leakage. A literal absent from the lookup falls back to its containing symbol's file:line.</para>
/// </summary>
public static class BridgeGraphBuilder
{
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
        IReadOnlyDictionary<LiteralRecord, LiteralSite>? literalSites = null)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(typeArguments);
        ArgumentNullException.ThrowIfNull(literals);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(dbSetProperties);

        var symbolsById = BuildSymbolIndex(symbols);
        var resolver = new SymbolResolver(symbols);

        // --- the reductions (Task 8's job per the leg doc-comments) ---------------------------------------------
        var createMaps = ReduceCreateMaps(typeArguments);
        var endpoints = ReduceEndpoints(symbols, annotations);
        var clientCalls = ReduceClientCalls(literals, symbolsById, literalSites);

        // --- run the three legs (each emits candidates; the scorer scores) --------------------------------------
        var candidates = new List<CandidateEdge>();
        candidates.AddRange(EntityTableBridge.Resolve(
            new EntityTableInput(dbSetProperties, DapperFromCandidates: []), resolver));
        candidates.AddRange(DtoEntityBridge.Resolve(
            new DtoEntityInput(createMaps, Projections: [], FieldSources: null), resolver));
        candidates.AddRange(RouteBridge.Resolve(
            new RouteBridgeInput(clientCalls, endpoints), resolver));

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
        return BridgeGraph.Build(scored, nodes);
    }

    // ============================ CreateMap grouping (Leg 2 PRIMARY reduction) ==================================

    /// <summary>
    /// Group the <c>type_arguments</c> of each generic use-site (one group per <c>identifier_id</c>) into the A/B pair
    /// of a candidate map: ordinal 0 = copy-source, ordinal 1 = copy-dest into a <see cref="CreateMapCandidate"/>. ONLY
    /// the two top-level args of a use-site are read; nested generic args (<c>parent_arg_id</c> set) are skipped — they
    /// are the components of a generic type arg, not the map's own A/B. The ordinal encodes the COPY direction; the
    /// builder NEVER reorders to entity-vs-DTO (the design §8 trap). The leg + scorer + name-resolution gate filter the
    /// over-produced set: an unresolvable pair yields no edge.
    ///
    /// <para><b>Contract limit.</b> The verified <see cref="TypeArgument"/> carries no use-site name or kind, so the
    /// builder cannot restrict to literal <c>CreateMap</c> calls, and no <c>.ReverseMap()</c> sibling is observable —
    /// <see cref="CreateMapCandidate.HasReverseMap"/> is therefore always false (a contract gap, see the type remarks).
    /// Only un-nested, exactly-two-arg groups are admitted, which keeps over-production tight.</para>
    /// </summary>
    private static IReadOnlyList<CreateMapCandidate> ReduceCreateMaps(IReadOnlyList<TypeArgument> typeArguments)
    {
        // identifier_id -> the two top-level args (ordinal -> type name + a use-site file), plus a count of how many
        // distinct top-level ordinals appeared (to reject groups that are not a clean A/B pair).
        var groups = new Dictionary<string, CreateMapGroup>(StringComparer.Ordinal);

        foreach (var arg in typeArguments)
        {
            if (arg.ParentArgId is not null)
                continue; // a nested generic component, not one of the map's own A/B
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

        // Deterministic order: by identifier_id.
        var candidates = new List<CreateMapCandidate>();
        foreach (var identifierId in Sorted(groups.Keys))
        {
            var group = groups[identifierId];
            if (group.Source is null || group.Dest is null)
                continue; // not a clean ordinal 0 + 1 pair
            if (group.TopLevelArgCount != 2)
                continue; // a 1-arg or 3+-arg generic use-site is not a 2-type map (e.g. Dictionary<,>, single T)

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

    // ============================ Controller endpoint reduction (Leg 1 C# side) =================================

    /// <summary>
    /// Reduce each method symbol carrying an http-verb annotation into a <see cref="ControllerEndpoint"/>: find the
    /// parent class by <see cref="SymbolDetail.ParentClassName"/> for the class <c>[Route]</c>, parse the method route
    /// from the verb annotation <c>raw_text</c>, and parse the return type + a conservative request-body type from the
    /// method <c>signature</c>. Deterministic: endpoints ordered by symbol id.
    ///
    /// <para><b>Contract note.</b> <see cref="SymbolDetail"/> carries <see cref="SymbolDetail.ParentClassName"/> but no
    /// parent symbol id, so the class is found by name. When several classes share a name, the class <c>[Route]</c> may
    /// be wrong; the route normalizer + scorer tolerate a missing/loose class route, and the verb-known route match
    /// still anchors the edge.</para>
    /// </summary>
    private static IReadOnlyList<ControllerEndpoint> ReduceEndpoints(
        IReadOnlyList<SymbolDetail> symbols,
        IReadOnlyList<SymbolAnnotation> annotations)
    {
        // symbol id -> its annotations, for the verb + class [Route] lookups.
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

        // class name -> the class symbol's annotations (for the [Route] lookup by ParentClassName). First class with a
        // [Route] wins for a duplicated name; deterministic by symbol id.
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
                continue; // cannot expand [controller] without a parent class name

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

    private static bool IsClassKind(string kind) =>
        string.Equals(kind, "class", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "record", StringComparison.OrdinalIgnoreCase);

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

    // Body-bearing verbs whose request parameter MAY be a request body (findings 28-2: [FromBody] is NOT persisted).
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

    /// <summary>The route arg of a class's <c>[Route("...")]</c> annotation, or null when the class declares none.</summary>
    private static string? RouteArgOf(List<SymbolAnnotation> classAnnotations)
    {
        foreach (var annotation in classAnnotations)
        {
            if (string.Equals(annotation.AnnotationKey, "route", StringComparison.OrdinalIgnoreCase))
                return FirstStringArg(annotation.RawText);
        }
        return null;
    }

    /// <summary>
    /// Parse the method signature into (returnType, requestBodyType). The return type is the leading token run before
    /// the method-name "(". The request-body type is populated CONSERVATIVELY (findings 28-2: <c>[FromBody]</c> is NOT
    /// persisted by julie 28/2 — a param attribute degrades to the declared parameter type in the signature): ONLY a
    /// plausible request-body parameter (a complex, non-primitive, non-route-bound type) on a body-bearing verb
    /// (POST/PUT/PATCH) is promoted; otherwise null. An arbitrary first parameter is NEVER promoted to a Consumes edge.
    /// </summary>
    private static (string ReturnType, string? RequestBodyType) ParseSignatureTypes(string signature, string verbKey)
    {
        var returnType = ParseReturnType(signature);
        string? requestBodyType = null;

        if (IsBodyBearingVerb(verbKey))
            requestBodyType = ParseRequestBodyType(signature);

        return (returnType, requestBodyType);
    }

    /// <summary>The return type = the balanced-aware leading token run before the parameter-list <c>(</c>.</summary>
    private static string ParseReturnType(string signature)
    {
        var sig = (signature ?? string.Empty).Trim();
        int open = TopLevelChar(sig, '(');
        if (open < 0)
            return sig; // no parameter list visible — treat the whole thing as the return type

        var head = sig[..open].Trim();
        // head is "...modifiers Return MethodName" — the return type is the second-to-last top-level token run.
        int lastSpace = LastTopLevelSpace(head);
        return lastSpace <= 0 ? string.Empty : head[..lastSpace].Trim();
    }

    /// <summary>
    /// The first parameter whose type is a complex (non-primitive) user type, on a body-bearing verb — the
    /// conservative request-body candidate. A route-bound primitive (<c>int id</c>, <c>string name</c>) is never a body.
    /// </summary>
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

    /// <summary>A parameter's declared type = everything before its last top-level token (the name); null if malformed.</summary>
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

    /// <summary>True when a parameter type is a complex named type plausibly carrying a request body (not a primitive).</summary>
    private static bool IsPlausibleBodyType(string type)
    {
        var t = type.TrimEnd('?').Trim();
        if (t.Length == 0)
            return false;
        // A generic/collection or array param is not a single request DTO; treat only a bare named type as a body.
        if (t.IndexOfAny(['<', '>', '[', ']']) >= 0)
            return false;
        if (Primitives.Contains(t))
            return false;
        // Must look like a user type (an upper-case leading char, or interface 'I' prefix).
        return char.IsUpper(t[0]);
    }

    private static readonly HashSet<string> Primitives = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short",
        "ushort", "string", "object", "void", "Guid", "DateTime", "DateTimeOffset", "TimeSpan", "Boolean", "Int32",
        "Int64", "Int16", "Double", "Single", "Decimal", "String", "Object", "Byte", "Char", "CancellationToken",
    };

    // ============================ TsClientCall reduction (Leg 1 TS side) ========================================

    /// <summary>
    /// Reduce each <c>kind='url'</c> literal into a <see cref="TsClientCall"/>, attaching the containing symbol's
    /// <c>test_role</c> and the use-site file:line. The leg itself does the language + test filter; the builder only
    /// supplies the located rows. Deterministic: ordered by literal span.
    /// </summary>
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
            TestRole? testRole = null;
            if (!string.IsNullOrEmpty(literal.ContainingSymbolId) &&
                symbolsById.TryGetValue(literal.ContainingSymbolId, out var container))
            {
                testRole = container.TestRole;
            }

            var site = SiteFor(literal, symbolsById, literalSites);
            calls.Add(new TsClientCall(literal, testRole, site.FilePath, site.Line));
        }
        return calls;
    }

    // ============================ node construction ============================================================

    /// <summary>
    /// Build a <see cref="BridgeNode"/> for every endpoint of a surviving scored edge, keyed by the same node id
    /// <see cref="BridgeGraph"/> uses (resolved symbol id, or a kind+display synthesis). A symbol-backed node renders
    /// with the resolved symbol's NAME (so a route endpoint shows its action method, e.g. GetById, not the route text
    /// its <see cref="EdgeRef.Display"/> carries) and is enriched with the symbol's file:line; a non-symbol node
    /// (table / route) carries the edge ref's display + file.
    /// </summary>
    private static IReadOnlyDictionary<string, BridgeNode> BuildNodes(
        IReadOnlyList<ScoredEdge> scored, IReadOnlyDictionary<string, SymbolDetail> symbolsById)
    {
        var nodes = new Dictionary<string, BridgeNode>(StringComparer.Ordinal);
        foreach (var edge in scored)
        {
            AddNode(nodes, edge.Edge.SourceRef, edge.Edge.Kind, EndpointSide.Source, symbolsById);
            AddNode(nodes, edge.Edge.TargetRef, edge.Edge.Kind, EndpointSide.Target, symbolsById);
        }
        return nodes;
    }

    private static void AddNode(
        Dictionary<string, BridgeNode> nodes,
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

        nodes[id] = new BridgeNode(id, kind, edgeRef.Display, edgeRef.FilePath, Line: 0);
    }

    // ============================ shared helpers ===============================================================

    private static Dictionary<string, SymbolDetail> BuildSymbolIndex(IReadOnlyList<SymbolDetail> symbols)
    {
        var byId = new Dictionary<string, SymbolDetail>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
            byId[symbol.Id] = symbol; // last write wins for a duplicated id
        return byId;
    }

    /// <summary>
    /// Resolve a literal's use-site file:line: the reader-supplied lookup first (the seam), else the containing
    /// symbol's file (line 0, unknown). A literal with neither yields an empty file + line 0.
    /// </summary>
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

    /// <summary>The first double-quoted string argument in a raw attribute text, or null.</summary>
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

    // ---- balanced-bracket helpers (treat <...>/(...)/[...] by depth) -------------------------------------------

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

/// <summary>
/// A literal's resolved use-site file:line (the literal-evidence seam — see <see cref="BridgeGraphBuilder"/> remarks).
/// The DB reader (plan Task 9) supplies the lookup; Miller.Core stays free of julie's row shape.
/// </summary>
/// <param name="FilePath">The workspace-relative file the literal lives in.</param>
/// <param name="Line">The 1-based line of the literal, or 0 when unknown.</param>
public readonly record struct LiteralSite(string FilePath, int Line);
