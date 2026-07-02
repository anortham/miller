using Miller.Core.Contracts;
using System.Linq;

namespace Miller.Core.Graph;

internal static class StructuralRouteFactAdapter
{
    private const string HtmxAttributePattern = BridgeStructuralPatterns.HtmxAttribute;
    private const string VueRouteReferencePattern = BridgeStructuralPatterns.VueRouteReference;
    private const string VueRouteDefinitionPattern = BridgeStructuralPatterns.VueRouteDefinition;
    private const string ReactRouteReferencePattern = BridgeStructuralPatterns.ReactRouteReference;
    private const string ReactRouteDefinitionPattern = BridgeStructuralPatterns.ReactRouteDefinition;
    private const string NextJsRouteReferencePattern = BridgeStructuralPatterns.NextJsRouteReference;
    private const string NextJsFileRoutePattern = BridgeStructuralPatterns.NextJsFileRoute;
    private const string NuxtRouteReferencePattern = BridgeStructuralPatterns.NuxtRouteReference;
    private const string NuxtFileRoutePattern = BridgeStructuralPatterns.NuxtFileRoute;
    private const string HttpClientRequestPattern = BridgeStructuralPatterns.HttpClientRequest;
    private const string NextJsRouteHandlerPattern = BridgeStructuralPatterns.NextJsRouteHandler;
    private const string NuxtServerRoutePattern = BridgeStructuralPatterns.NuxtServerRoute;
    private const string SpringRequestMappingPattern = BridgeStructuralPatterns.SpringRequestMapping;
    private const string ExpressRouterMountPattern = BridgeStructuralPatterns.ExpressRouterMount;
    private const string FastApiIncludeRouterPattern = BridgeStructuralPatterns.FastApiIncludeRouter;
    private const string FlaskBlueprintRegistrationPattern = BridgeStructuralPatterns.FlaskBlueprintRegistration;
    private const string DjangoUrlIncludePattern = BridgeStructuralPatterns.DjangoUrlInclude;

    public static bool TryReadRouteReference(
        StructuralFactRecord fact,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        out StructuralRouteReference reference)
    {
        reference = null!;
        if (!IsRouteReferencePattern(fact.PatternId))
            return false;

        var routePath = RouteReferencePath(fact);
        if (string.IsNullOrWhiteSpace(routePath))
            return false;

        var verb = string.Equals(fact.PatternId, HtmxAttributePattern, StringComparison.Ordinal)
            ? HtmxVerb(fact)
            : null;
        if (string.Equals(fact.PatternId, HtmxAttributePattern, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(verb))
        {
            return false;
        }

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

        var routePath = FileRoutePath(fact);
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

    /// <summary>
    /// Read an <c>http.client_request.v1</c> fact (2.6.0: a fetch/axios call site). Only <c>url_kind="path"</c>
    /// requests are bridge candidates — <c>relative</c> resolution depends on the current page URL and
    /// <c>absolute</c> targets an external host, so neither can honestly join a workspace route. The verb is
    /// always present in the 2.6.0 contract and is verb-known for BOTH <c>verb_source</c> values
    /// (<c>attested</c> and <c>default</c> — fetch/axios spec-default GET); a missing verb is a malformed fact
    /// and is rejected rather than guessed.
    /// </summary>
    public static bool TryReadClientRequest(
        StructuralFactRecord fact,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        out StructuralClientRequest request)
    {
        request = null!;
        if (!string.Equals(fact.PatternId, HttpClientRequestPattern, StringComparison.Ordinal))
            return false;

        var urlKind = MetadataString(fact, "url_kind");
        if (!string.Equals(urlKind, "path", StringComparison.Ordinal))
            return false;

        var routePath = MetadataString(fact, "target_path");
        if (string.IsNullOrWhiteSpace(routePath))
            return false;

        var verb = MetadataString(fact, "verb");
        if (string.IsNullOrWhiteSpace(verb))
            return false;

        if (IsTestFact(fact, symbolsById))
            return false;

        request = new StructuralClientRequest(
            fact,
            routePath,
            verb.Trim().ToUpperInvariant(),
            MetadataString(fact, "verb_source"),
            MetadataString(fact, "client") ?? "http",
            fact.ContainingSymbolId ?? string.Empty,
            fact.Path,
            fact.Span.StartLine);
        return true;
    }

    /// <summary>
    /// Read a server route-handler definition fact (2.6.0: <c>nextjs.route_handler.v1</c> /
    /// <c>nuxt.server_route.v1</c>). The route path prefers the bracket-form <c>route_path</c> — the same
    /// precedence as navigation file routes. The verb is NULLABLE: a suffix-less Nuxt server route answers
    /// every method, so its accepted verb set is not source-attested and stays null (never assumed GET).
    /// Navigation file routes (<see cref="TryReadFileRoute"/>) keep their verb-blind semantics.
    /// </summary>
    public static bool TryReadRouteHandler(
        StructuralFactRecord fact,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        out StructuralRouteHandler handler)
    {
        handler = null!;
        if (!IsRouteHandlerPattern(fact.PatternId))
            return false;

        var routePath = MetadataString(fact, "route_path")
            ?? MetadataString(fact, "normalized_route_template");
        if (string.IsNullOrWhiteSpace(routePath))
            return false;

        if (IsTestFact(fact, symbolsById))
            return false;

        var verb = MetadataString(fact, "verb");
        handler = new StructuralRouteHandler(
            fact,
            routePath,
            string.IsNullOrWhiteSpace(verb) ? null : verb.Trim().ToUpperInvariant(),
            fact.ContainingSymbolId ?? string.Empty,
            fact.Path,
            fact.Span.StartLine);
        return true;
    }

    /// <summary>
    /// Read a backend HTTP route-template fact (2.7.0: the 10 <see cref="BridgeStructuralPatterns.BackendRoutePatternIds"/>
    /// families — Express/Fastify/FastAPI/Flask/Django/Spring/Go net-http/gin/echo/Rails). Sibling to
    /// <see cref="TryReadRouteHandler"/>, but the route-path precedence differs: backend families carry NO
    /// bracket-form <c>route_path</c>, so the join key is <c>effective_route_template</c> (same-file prefix folded)
    /// preferred over <c>normalized_route_template</c>. A blank route is rejected — this honestly excludes Django
    /// <c>route_syntax="regex"</c> facts (no <c>normalized_route_template</c>), never synthesizing a route from a regex.
    /// A Spring <c>attribute_kind="class_route"</c> fact is a controller prefix, never an endpoint (mirrors ASP.NET
    /// <c>controller_route</c>), and is rejected. The verb is NULLABLE UPPERCASE: verbless facts (Express <c>app.all</c>,
    /// gin/echo <c>Any</c>, method-less <c>@RequestMapping</c>, Django URLconf) yield a null verb → downstream Medium
    /// <c>verb_unknown</c>, never an assumed GET. Reuses <see cref="StructuralRouteHandler"/> so
    /// <c>FileRouteBridge.ResolveClientRequests</c> consumes backend routes with no resolver change.
    /// </summary>
    public static bool TryReadBackendRoute(
        StructuralFactRecord fact,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        out StructuralRouteHandler handler)
    {
        handler = null!;
        if (!IsBackendRoutePattern(fact.PatternId))
            return false;

        // A Spring class-level @RequestMapping is a controller prefix, not an endpoint (evidence only).
        if (string.Equals(fact.PatternId, SpringRequestMappingPattern, StringComparison.Ordinal) &&
            string.Equals(MetadataString(fact, "attribute_kind"), "class_route", StringComparison.Ordinal))
        {
            return false;
        }

        var routePath = MetadataString(fact, "effective_route_template")
            ?? MetadataString(fact, "normalized_route_template");
        if (string.IsNullOrWhiteSpace(routePath))
            return false;

        if (IsTestFact(fact, symbolsById))
            return false;

        var verb = MetadataString(fact, "verb");
        handler = new StructuralRouteHandler(
            fact,
            routePath,
            string.IsNullOrWhiteSpace(verb) ? null : verb.Trim().ToUpperInvariant(),
            fact.ContainingSymbolId ?? string.Empty,
            fact.Path,
            fact.Span.StartLine);
        return true;
    }

    /// <summary>
    /// Read a cross-file route-mount/include fact (2.7.0: <c>express.router_mount.v1</c>,
    /// <c>fastapi.include_router.v1</c>, <c>flask.blueprint_registration.v1</c>, <c>django.url_include.v1</c>).
    /// <c>rails.mount.v1</c> is deliberately NOT read here — it mounts Rack apps whose internal routes are not
    /// in the fact stream, so it is evidence-only. The mount prefix is <c>normalized_mount_path</c> preferred
    /// over <c>mount_path</c>; a fact with NEITHER is rejected — an un-prefixed <c>include_router</c>/
    /// <c>register_blueprint</c> composes nothing (fastapi/flask mount paths are optional literal-only). The
    /// <see cref="IsTestFact"/> filter mirrors the route reads so a mount fact in a test file never seeds
    /// composed edges.
    /// </summary>
    public static bool TryReadMountFact(
        StructuralFactRecord fact,
        IReadOnlyDictionary<string, SymbolDetail> symbolsById,
        out StructuralMountFact mount)
    {
        mount = null!;
        if (!IsMountFactPattern(fact.PatternId))
            return false;

        var mountPath = MetadataString(fact, "normalized_mount_path")
            ?? MetadataString(fact, "mount_path");
        if (string.IsNullOrWhiteSpace(mountPath))
            return false;

        if (IsTestFact(fact, symbolsById))
            return false;

        mount = new StructuralMountFact(
            fact,
            mountPath,
            MetadataString(fact, "mount_target") ?? string.Empty,
            MetadataString(fact, "included_module"),
            fact.Path);
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
        string.Equals(patternId, ReactRouteReferencePattern, StringComparison.Ordinal) ||
        string.Equals(patternId, NextJsRouteReferencePattern, StringComparison.Ordinal) ||
        string.Equals(patternId, NuxtRouteReferencePattern, StringComparison.Ordinal);

    private static bool IsFileRoutePattern(string patternId) =>
        string.Equals(patternId, VueRouteDefinitionPattern, StringComparison.Ordinal) ||
        string.Equals(patternId, ReactRouteDefinitionPattern, StringComparison.Ordinal) ||
        string.Equals(patternId, NextJsFileRoutePattern, StringComparison.Ordinal) ||
        string.Equals(patternId, NuxtFileRoutePattern, StringComparison.Ordinal);

    private static bool IsRouteHandlerPattern(string patternId) =>
        string.Equals(patternId, NextJsRouteHandlerPattern, StringComparison.Ordinal) ||
        string.Equals(patternId, NuxtServerRoutePattern, StringComparison.Ordinal);

    // The 10 backend route-template families are the single source of truth in BridgeStructuralPatterns —
    // gate against that list so the adapter and the provider can never drift.
    private static bool IsBackendRoutePattern(string patternId) =>
        BridgeStructuralPatterns.BackendRoutePatternIds.Contains(patternId, StringComparer.Ordinal);

    private static bool IsMountFactPattern(string patternId) =>
        string.Equals(patternId, ExpressRouterMountPattern, StringComparison.Ordinal) ||
        string.Equals(patternId, FastApiIncludeRouterPattern, StringComparison.Ordinal) ||
        string.Equals(patternId, FlaskBlueprintRegistrationPattern, StringComparison.Ordinal) ||
        string.Equals(patternId, DjangoUrlIncludePattern, StringComparison.Ordinal);

    private static string? RouteReferencePath(StructuralFactRecord fact) =>
        MetadataString(fact, "target_path")
        ?? MetadataString(fact, "attribute_value")
        ?? MetadataString(fact, "normalized_route_template")
        ?? MetadataString(fact, "route_path");

    private static string? FileRoutePath(StructuralFactRecord fact) =>
        MetadataString(fact, "route_path")
        ?? MetadataString(fact, "normalized_route_template")
        ?? MetadataString(fact, "target_path")
        ?? MetadataString(fact, "attribute_value");

    private static string? HtmxVerb(StructuralFactRecord fact)
    {
        var attributeName = MetadataString(fact, "attribute_name")
            ?? MetadataString(fact, "attribute");
        if (string.IsNullOrWhiteSpace(attributeName))
            return MetadataString(fact, "verb");

        var normalizedAttributeName = attributeName.Trim().ToLowerInvariant();
        if (normalizedAttributeName.StartsWith("data-", StringComparison.Ordinal))
            normalizedAttributeName = normalizedAttributeName["data-".Length..];

        return normalizedAttributeName switch
        {
            "hx-get" => "GET",
            "hx-post" => "POST",
            "hx-put" => "PUT",
            "hx-patch" => "PATCH",
            "hx-delete" => "DELETE",
            _ => null,
        };
    }

    public static string? MetadataString(StructuralFactRecord fact, string key)
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
    string? Verb,
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

/// <summary>
/// An <c>http.client_request.v1</c> read: a fetch/axios call site with a path-kind URL. <paramref name="Verb"/> is
/// always known (UPPERCASE); <paramref name="VerbSource"/> is <c>attested</c> or <c>default</c> — both verb-known.
/// <paramref name="Client"/> is the calling library (<c>fetch</c>/<c>axios</c>), used to synthesize the carrier.
/// </summary>
internal sealed record StructuralClientRequest(
    StructuralFactRecord Fact,
    string RoutePath,
    string Verb,
    string? VerbSource,
    string Client,
    string ContainingSymbolId,
    string FilePath,
    int Line);

/// <summary>
/// A server route-handler definition (<c>nextjs.route_handler.v1</c> / <c>nuxt.server_route.v1</c>).
/// <paramref name="Verb"/> is NULL when the handler's accepted method set is not source-attested
/// (suffix-less Nuxt server routes) — a verb-aware consumer must keep that arm honest-Medium, never assume GET.
/// </summary>
internal sealed record StructuralRouteHandler(
    StructuralFactRecord Fact,
    string RoutePath,
    string? Verb,
    string ContainingSymbolId,
    string FilePath,
    int Line);

/// <summary>
/// A cross-file route-mount/include fact (2.7.0: <c>express.router_mount.v1</c> / <c>fastapi.include_router.v1</c> /
/// <c>flask.blueprint_registration.v1</c> / <c>django.url_include.v1</c>). <paramref name="MountPath"/> is the
/// mount prefix (<c>normalized_mount_path</c> preferred over <c>mount_path</c>). <paramref name="MountTarget"/> is
/// the mounted expression's source text (the identifier anchor for express/fastapi/flask); it is empty when the
/// family carries no <c>mount_target</c> (Django anchors by module instead). <paramref name="IncludedModule"/> is
/// the Django module literal (e.g. <c>"users.urls"</c>) and null for the others.
/// </summary>
internal sealed record StructuralMountFact(
    StructuralFactRecord Fact,
    string MountPath,
    string MountTarget,
    string? IncludedModule,
    string FilePath);
