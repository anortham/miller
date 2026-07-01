using Miller.Core.Contracts;

namespace Miller.Core.Graph;

internal static class StructuralRouteFactAdapter
{
    private const string HtmxAttributePattern = "htmx.attribute.v1";
    private const string VueRouteReferencePattern = "vue.route_reference.v1";
    private const string VueRouteDefinitionPattern = "vue.route_definition.v1";
    private const string ReactRouteReferencePattern = "react.route_reference.v1";
    private const string ReactRouteDefinitionPattern = "react.route_definition.v1";
    private const string NextJsRouteReferencePattern = "nextjs.route_reference.v1";
    private const string NextJsFileRoutePattern = "nextjs.file_route.v1";
    private const string NuxtRouteReferencePattern = "nuxt.route_reference.v1";
    private const string NuxtFileRoutePattern = "nuxt.file_route.v1";

    public static bool TryReadRouteReference(
        StructuralFactRecord fact,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        out StructuralRouteReference reference)
    {
        reference = null!;
        if (!IsRouteReferencePattern(fact.PatternId))
            return false;

        var routePath = RoutePath(fact);
        if (string.IsNullOrWhiteSpace(routePath))
            return false;

        var verb = string.Equals(fact.PatternId, HtmxAttributePattern, StringComparison.Ordinal)
            ? HtmxVerb(fact)
            : MetadataString(fact, "verb") ?? DefaultVerbForRouteReferenceFact(fact.PatternId);
        if (string.IsNullOrWhiteSpace(verb))
            return false;

        if (IsTestFact(fact, symbolsById))
            return false;

        reference = new StructuralRouteReference(
            fact,
            routePath,
            verb,
            fact.ContainingSymbolId ?? string.Empty,
            fact.Path,
            fact.Span.StartLine);
        return true;
    }

    public static bool TryReadFileRoute(
        StructuralFactRecord fact,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        out StructuralFileRoute route)
    {
        route = null!;
        if (!IsFileRoutePattern(fact.PatternId))
            return false;

        var routePath = RoutePath(fact);
        if (string.IsNullOrWhiteSpace(routePath))
            return false;

        if (IsTestFact(fact, symbolsById))
            return false;

        route = new StructuralFileRoute(
            fact,
            routePath,
            "GET",
            fact.ContainingSymbolId ?? string.Empty,
            fact.Path,
            fact.Span.StartLine);
        return true;
    }

    public static bool IsTestFact(
        StructuralFactRecord fact,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById)
    {
        if (!string.IsNullOrEmpty(fact.ContainingSymbolId) &&
            symbolsById.TryGetValue(fact.ContainingSymbolId, out var container) &&
            container.IsTest)
        {
            return true;
        }

        return IsTestPath(fact.Path);
    }

    private static bool IsRouteReferencePattern(string patternId) =>
        string.Equals(patternId, HtmxAttributePattern, StringComparison.Ordinal) ||
        string.Equals(patternId, VueRouteReferencePattern, StringComparison.Ordinal) ||
        string.Equals(patternId, VueRouteDefinitionPattern, StringComparison.Ordinal) ||
        string.Equals(patternId, ReactRouteReferencePattern, StringComparison.Ordinal) ||
        string.Equals(patternId, ReactRouteDefinitionPattern, StringComparison.Ordinal) ||
        string.Equals(patternId, NextJsRouteReferencePattern, StringComparison.Ordinal) ||
        string.Equals(patternId, NuxtRouteReferencePattern, StringComparison.Ordinal);

    private static bool IsFileRoutePattern(string patternId) =>
        string.Equals(patternId, NextJsFileRoutePattern, StringComparison.Ordinal) ||
        string.Equals(patternId, NuxtFileRoutePattern, StringComparison.Ordinal);

    private static string? RoutePath(StructuralFactRecord fact) =>
        MetadataString(fact, "target_path")
        ?? MetadataString(fact, "attribute_value")
        ?? MetadataString(fact, "normalized_route_template")
        ?? MetadataString(fact, "route_path");

    private static string? DefaultVerbForRouteReferenceFact(string patternId) =>
        string.Equals(patternId, VueRouteReferencePattern, StringComparison.Ordinal) ||
        string.Equals(patternId, VueRouteDefinitionPattern, StringComparison.Ordinal) ||
        string.Equals(patternId, ReactRouteReferencePattern, StringComparison.Ordinal) ||
        string.Equals(patternId, ReactRouteDefinitionPattern, StringComparison.Ordinal) ||
        string.Equals(patternId, NextJsRouteReferencePattern, StringComparison.Ordinal) ||
        string.Equals(patternId, NuxtRouteReferencePattern, StringComparison.Ordinal)
            ? "GET"
            : null;

    private static string? HtmxVerb(StructuralFactRecord fact)
    {
        var attributeName = MetadataString(fact, "attribute_name")
            ?? MetadataString(fact, "attribute");
        if (string.IsNullOrWhiteSpace(attributeName))
            return MetadataString(fact, "verb");

        return attributeName?.ToLowerInvariant() switch
        {
            "hx-get" => "GET",
            "hx-post" => "POST",
            "hx-put" => "PUT",
            "hx-patch" => "PATCH",
            "hx-delete" => "DELETE",
            _ => null,
        };
    }

    private static string? MetadataString(StructuralFactRecord fact, string key)
    {
        return fact.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static bool IsTestPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/__tests__/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(".test.", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(".spec.", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record StructuralRouteReference(
    StructuralFactRecord Fact,
    string RoutePath,
    string Verb,
    string ContainingSymbolId,
    string FilePath,
    int Line);

internal sealed record StructuralFileRoute(
    StructuralFactRecord Fact,
    string RoutePath,
    string Verb,
    string ContainingSymbolId,
    string FilePath,
    int Line);
