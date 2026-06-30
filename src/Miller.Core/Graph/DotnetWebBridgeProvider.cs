using Miller.Core.Contracts;
using Miller.Core.Resolver;

namespace Miller.Core.Graph;

/// <summary>
/// The current ASP.NET / TypeScript bridge provider: controller routes, client URL literals, AutoMapper-style
/// CreateMap pairs, and EF DbSet breadcrumbs.
/// </summary>
public sealed class DotnetWebBridgeProvider : IBridgeProvider
{
    public const string ProviderId = "dotnet-web";

    public static DotnetWebBridgeProvider Instance { get; } = new();

    private DotnetWebBridgeProvider()
    {
    }

    public string Id => ProviderId;

    public BridgeProviderResult BuildCandidates(BridgeProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var createMaps = ReduceCreateMaps(context.TypeArguments);
        var structuralReduction = WebStackStructuralFactReducer.Reduce(context.StructuralFacts, context.SymbolsById);
        var endpoints = ReduceEndpoints(context.Symbols, context.Annotations)
            .Concat(structuralReduction.AspNetMinimalRoutes)
            .ToList();
        var clientCalls = ReduceClientCalls(context.Literals, context.SymbolsById, context.LiteralSites)
            .Concat(structuralReduction.HtmxCalls)
            .ToList();
        var serverTypeResolver = new SymbolResolver(context.Symbols.Where(IsCSharpUserType).ToArray());

        var evidenceCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["dotnet-web.createMaps"] = createMaps.Count,
            ["dotnet-web.endpoints"] = endpoints.Count,
            ["dotnet-web.clientCalls"] = clientCalls.Count,
            ["dotnet-web.dbsets"] = context.DbSetProperties.Count,
            ["dotnet-web.structuralFacts"] = context.StructuralFacts.Count,
            ["dotnet-web.aspnetMinimalRoutes"] = structuralReduction.AspNetMinimalRoutes.Count,
            ["dotnet-web.htmxCalls"] = structuralReduction.HtmxCalls.Count,
        };

        if (createMaps.Count == 0 &&
            endpoints.Count == 0 &&
            clientCalls.Count == 0 &&
            context.DbSetProperties.Count == 0)
        {
            return BridgeProviderResult.Skipped("no dotnet-web bridge evidence", evidenceCounts);
        }

        var candidates = new List<CandidateEdge>();
        candidates.AddRange(EntityTableBridge.Resolve(
            new EntityTableInput(context.DbSetProperties, DapperFromCandidates: []), serverTypeResolver));
        candidates.AddRange(DtoEntityBridge.Resolve(
            new DtoEntityInput(createMaps, Projections: [], FieldSources: null), serverTypeResolver));
        candidates.AddRange(RouteBridge.Resolve(
            new RouteBridgeInput(clientCalls, endpoints), serverTypeResolver));

        evidenceCounts["dotnet-web.candidates"] = candidates.Count;
        return BridgeProviderResult.ActiveResult(candidates, evidenceCounts);
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
