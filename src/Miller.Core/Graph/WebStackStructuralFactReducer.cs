using System.Text.Json;
using Miller.Core.Contracts;
using Miller.Core.Resolver;

namespace Miller.Core.Graph;

internal sealed record WebStackStructuralFactReduction(
    IReadOnlyList<ControllerEndpoint> AspNetMinimalRoutes,
    IReadOnlyList<TsClientCall> HtmxCalls,
    IReadOnlyList<TsClientCall> VueCalls);

internal static class WebStackStructuralFactReducer
{
    private const string AspNetMinimalRoutePatternId = "aspnet.minimal_api.route.v1";
    private const string HtmxAttributePatternId = "htmx.attribute.v1";
    private const string VueRouteReferencePatternId = "vue.route_reference.v1";

    private static readonly string[] HtmxRouteAttributes =
    [
        "hx-get", "hx-post", "hx-put", "hx-patch", "hx-delete",
    ];

    public static WebStackStructuralFactReduction Reduce(
        IReadOnlyList<StructuralFactRecord> structuralFacts,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById)
    {
        ArgumentNullException.ThrowIfNull(structuralFacts);
        ArgumentNullException.ThrowIfNull(symbolsById);

        var aspNetRoutes = new List<ControllerEndpoint>();
        var htmxCalls = new List<TsClientCall>();
        var vueCalls = new List<TsClientCall>();

        foreach (var fact in structuralFacts)
        {
            if (!TryParseMetadata(fact.MetadataJson, out var metadata))
                continue;

            if (string.Equals(fact.PatternId, AspNetMinimalRoutePatternId, StringComparison.Ordinal))
            {
                if (TryReduceAspNetMinimalRoute(fact, metadata, symbolsById, out var endpoint))
                    aspNetRoutes.Add(endpoint);
            }
            else if (string.Equals(fact.PatternId, HtmxAttributePatternId, StringComparison.Ordinal))
            {
                if (TryReduceHtmxCall(fact, metadata, symbolsById, out var call))
                    htmxCalls.Add(call);
            }
            else if (string.Equals(fact.PatternId, VueRouteReferencePatternId, StringComparison.Ordinal))
            {
                if (TryReduceVueCall(fact, metadata, symbolsById, out var call))
                    vueCalls.Add(call);
            }
        }

        return new WebStackStructuralFactReduction(aspNetRoutes, htmxCalls, vueCalls);
    }

    private static bool TryReduceAspNetMinimalRoute(
        StructuralFactRecord fact,
        JsonElement metadata,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        out ControllerEndpoint endpoint)
    {
        endpoint = null!;

        var routeTemplate = StringProperty(metadata, "route_template");
        var verb = StringProperty(metadata, "verb");
        var verbKey = VerbKey(verb);
        if (string.IsNullOrWhiteSpace(routeTemplate) ||
            verbKey is null ||
            string.IsNullOrWhiteSpace(fact.ContainingSymbolId))
        {
            return false;
        }

        symbolsById.TryGetValue(fact.ContainingSymbolId, out var symbol);

        endpoint = new ControllerEndpoint(
            SymbolId: fact.ContainingSymbolId,
            VerbKey: verbKey,
            ClassRoute: null,
            MethodRoute: routeTemplate,
            ParentClassName: symbol?.ParentClassName ?? string.Empty,
            MethodName: symbol?.Name ?? fact.CaptureName,
            ReturnType: string.Empty,
            RequestBodyType: null,
            FilePath: fact.Path,
            Line: fact.StartLine);
        return true;
    }

    private static bool TryReduceHtmxCall(
        StructuralFactRecord fact,
        JsonElement metadata,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        out TsClientCall call)
    {
        call = null!;

        var targetPath = StringProperty(metadata, "target_path");
        var carrier = HtmxCarrier(metadata);
        if (string.IsNullOrWhiteSpace(targetPath) || carrier is null)
            return false;

        var literal = new LiteralRecord(
            LiteralText: targetPath,
            Kind: "url",
            Carrier: carrier,
            ArgPosition: 0,
            Language: fact.Language,
            ContainingSymbolId: fact.ContainingSymbolId ?? string.Empty,
            Span: fact.Span);

        bool isTest = false;
        if (!string.IsNullOrEmpty(fact.ContainingSymbolId) &&
            symbolsById.TryGetValue(fact.ContainingSymbolId, out var container))
        {
            isTest = container.IsTest;
        }

        call = new TsClientCall(literal, isTest, fact.Path, fact.StartLine);
        return true;
    }

    private static bool TryReduceVueCall(
        StructuralFactRecord fact,
        JsonElement metadata,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        out TsClientCall call)
    {
        call = null!;

        var targetPath = StringProperty(metadata, "target_path");
        var verb = CanonicalVerb(StringProperty(metadata, "verb"));
        if (string.IsNullOrWhiteSpace(targetPath) || !string.Equals(verb, "GET", StringComparison.Ordinal))
            return false;

        var literal = new LiteralRecord(
            LiteralText: targetPath,
            Kind: "url",
            Carrier: "vue.get",
            ArgPosition: 0,
            Language: "vue",
            ContainingSymbolId: fact.ContainingSymbolId ?? string.Empty,
            Span: fact.Span);

        bool isTest = false;
        if (!string.IsNullOrEmpty(fact.ContainingSymbolId) &&
            symbolsById.TryGetValue(fact.ContainingSymbolId, out var container))
        {
            isTest = container.IsTest;
        }

        call = new TsClientCall(literal, isTest, fact.Path, fact.StartLine);
        return true;
    }

    private static string? HtmxCarrier(JsonElement metadata)
    {
        var attributeName = StringProperty(metadata, "attribute_name", "attribute");
        var verbFromAttribute = HtmxVerbFromAttribute(attributeName);
        if (verbFromAttribute is not null)
            return "htmx." + verbFromAttribute.ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(attributeName))
            return null;

        var verb = CanonicalVerb(StringProperty(metadata, "verb"));
        return verb is null ? null : "htmx." + verb.ToLowerInvariant();
    }

    private static string? HtmxVerbFromAttribute(string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName))
            return null;

        foreach (var routeAttribute in HtmxRouteAttributes)
        {
            if (string.Equals(attributeName, routeAttribute, StringComparison.OrdinalIgnoreCase))
                return routeAttribute["hx-".Length..].ToUpperInvariant();
        }
        return null;
    }

    private static string? VerbKey(string? verb)
    {
        var canonical = CanonicalVerb(verb);
        return canonical is null ? null : "http" + canonical.ToLowerInvariant();
    }

    private static string? CanonicalVerb(string? verb)
    {
        if (string.IsNullOrWhiteSpace(verb))
            return null;

        return verb.ToUpperInvariant() switch
        {
            "GET" => "GET",
            "POST" => "POST",
            "PUT" => "PUT",
            "PATCH" => "PATCH",
            "DELETE" => "DELETE",
            "HEAD" => "HEAD",
            "OPTIONS" => "OPTIONS",
            _ => null,
        };
    }

    private static bool TryParseMetadata(string? metadataJson, out JsonElement metadata)
    {
        metadata = default;
        if (string.IsNullOrWhiteSpace(metadataJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            metadata = document.RootElement.Clone();
            return metadata.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? StringProperty(JsonElement metadata, params string[] names)
    {
        foreach (var property in metadata.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }

        return null;
    }
}
