using System.Text.RegularExpressions;

namespace Miller.Core.Resolver;

/// <summary>
/// The normalized form of one HTTP call/endpoint route: a canonical route plus the HTTP verb when it is known. The
/// route bridge matches a TS client call to a C# endpoint by comparing <see cref="Verb"/> + <see cref="Route"/>; a
/// verb-unknown client call (<see cref="VerbKnown"/> false) matches on <see cref="Route"/> alone at reduced
/// confidence — it is NEVER silently assumed to be GET.
/// </summary>
/// <param name="Verb">The canonical upper-case HTTP verb (e.g. <c>GET</c>), or null when the verb is unknown.</param>
/// <param name="Route">The canonical route: lowercased, params folded to <c>{}</c>, no leading/trailing slash, no query.</param>
/// <param name="VerbKnown">True when <see cref="Verb"/> was derivable; false for a verb-less carrier (route-only match).</param>
public sealed record NormalizedRoute(string? Verb, string Route, bool VerbKnown);

/// <summary>
/// Canonicalizes HTTP routes on both sides of the call bridge (design §4 Leg 1). Two entry points:
/// <see cref="FromClientCall"/> (TS/JS: verb from the carrier tail, or verb-unknown) and <see cref="FromEndpoint"/>
/// (C#: ASP.NET <c>[controller]</c>/<c>[action]</c>/<c>[area]</c> token expansion BEFORE prefix concatenation, then
/// the same canonicalization). Pure and deterministic.
///
/// <para><b>Token expansion is on the critical path</b> (design §8 risk 3): 21/23 MyraNext controllers declare
/// <c>Route("api/[controller]")</c> literally. Without expansion every endpoint normalizes to <c>api/[controller]/…</c>
/// and matches zero client routes; expanding it WRONG (dropping the controller segment) collides two controllers'
/// <c>{id}</c> templates into one route. So <see cref="FromEndpoint"/> takes the parent class name and substitutes
/// <c>[controller]</c> → class name minus trailing "Controller" (lowercased) before concatenating.</para>
/// </summary>
public static class RouteNormalizer
{
    // HTTP verbs we recognize as a carrier tail token or an annotation_key suffix.
    private static readonly string[] HttpVerbs =
    [
        "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS",
    ];

    // [[...param]] | [...param] | [param] | ${param} | {param} | :param  ->  {}. Compiled once; each
    // alternative consumes one path segment placeholder.
    // The :param alternative is bounded to an identifier run (not "everything up to the next /") so a trailing literal
    // extension/suffix is preserved — "/files/:id.json" folds to "files/{}.json", matching the C# "{id}.json" side,
    // which stops folding at the '}'. The over-broad ":[^/]+" form folded the extension away on only the client side.
    private static readonly Regex ParamPattern = new(
        @"\[\[\.\.\.[^\]/]+\]\]|\[\.\.\.[^\]/]+\]|\[[^\]/]+\]|\$\{[^}]*\}|\{[^}]*\}|:[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Normalize a TS/JS client HTTP call. The verb is read from <paramref name="carrier"/> ONLY when its tail token
    /// is an HTTP verb (<c>axios.post</c> → POST) or a <c>&lt;Verb&gt;Async</c> method (<c>PostAsync</c> → POST).
    /// Verb-less carriers (<c>fetch</c>, <c>$fetch</c>, <c>ofetch</c>, bare <c>axios</c>, <c>request</c>, <c>ky</c>,
    /// <c>got</c>, <c>sendasync</c>) yield a verb-unknown result — never an assumed GET.
    /// </summary>
    /// <param name="carrier">The verbatim callee (<c>literals.carrier</c>), e.g. <c>axios.post</c> or <c>fetch</c>.</param>
    /// <param name="literalText">The route literal (<c>literals.literal_text</c>), already interpolation-folded by julie.</param>
    public static NormalizedRoute FromClientCall(string carrier, string literalText)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(literalText);

        var verb = VerbFromCarrier(carrier);
        var route = Canonicalize(literalText);
        return new NormalizedRoute(verb, route, verb is not null);
    }

    /// <summary>
    /// Normalize a C# controller endpoint. Expands ASP.NET route tokens using the controller's identity BEFORE
    /// concatenating the class prefix with the method route, then canonicalizes. An absolute method route (leading
    /// <c>/</c>) overrides the class prefix.
    /// </summary>
    /// <param name="verbKey">The lowercased annotation key (<c>httpget</c>/<c>httppost</c>/…); the verb suffix is read from it.</param>
    /// <param name="classRoute">The class <c>[Route(...)]</c> argument (may be null/empty), e.g. <c>api/[controller]</c>.</param>
    /// <param name="methodRoute">The method route arg (e.g. <c>{id}</c>), null/empty when the method has none.</param>
    /// <param name="parentClassName">The controller class name (e.g. <c>AppSettingsController</c>) — expands <c>[controller]</c>.</param>
    /// <param name="methodName">The action method name (e.g. <c>GetById</c>) — expands <c>[action]</c>.</param>
    /// <param name="area">The MVC area name when present — expands <c>[area]</c>; defaults to empty.</param>
    public static NormalizedRoute FromEndpoint(
        string verbKey,
        string? classRoute,
        string? methodRoute,
        string parentClassName,
        string methodName,
        string area = "")
    {
        ArgumentNullException.ThrowIfNull(verbKey);
        ArgumentNullException.ThrowIfNull(parentClassName);
        ArgumentNullException.ThrowIfNull(methodName);

        var verb = VerbFromAnnotationKey(verbKey);

        var controllerToken = ControllerToken(parentClassName);
        var actionToken = methodName.ToLowerInvariant();
        var areaToken = area.ToLowerInvariant();

        string ExpandTokens(string route) => route
            .Replace("[controller]", controllerToken, StringComparison.OrdinalIgnoreCase)
            .Replace("[action]", actionToken, StringComparison.OrdinalIgnoreCase)
            .Replace("[area]", areaToken, StringComparison.OrdinalIgnoreCase);

        var method = methodRoute ?? string.Empty;

        // Absolute override: a method route starting with '/' ignores the class prefix entirely.
        string combined;
        if (method.StartsWith('/'))
        {
            combined = ExpandTokens(method);
        }
        else
        {
            var prefix = ExpandTokens(classRoute ?? string.Empty);
            var tail = ExpandTokens(method);
            combined = Join(prefix, tail);
        }

        return new NormalizedRoute(verb, Canonicalize(combined), verb is not null);
    }

    /// <summary>Map an annotation key like <c>httpget</c> to <c>GET</c>, or null when it carries no verb.</summary>
    private static string? VerbFromAnnotationKey(string verbKey)
    {
        var lower = verbKey.ToLowerInvariant();
        foreach (var verb in HttpVerbs)
        {
            // "httpget" ends with "get"; a bare "get" matches too.
            if (lower.EndsWith(verb, StringComparison.OrdinalIgnoreCase))
                return verb;
        }
        return null;
    }

    /// <summary>
    /// Read the verb from a client carrier's tail token: the segment after the last '.' (axios.post → post) with a
    /// trailing "Async" trimmed (PostAsync → post). Returns the canonical verb, or null when the tail is not a verb.
    /// </summary>
    private static string? VerbFromCarrier(string carrier)
    {
        var tail = carrier;
        int dot = tail.LastIndexOf('.');
        if (dot >= 0 && dot < tail.Length - 1)
            tail = tail[(dot + 1)..];

        if (tail.EndsWith("Async", StringComparison.OrdinalIgnoreCase) && tail.Length > "Async".Length)
            tail = tail[..^"Async".Length];

        foreach (var verb in HttpVerbs)
        {
            if (string.Equals(tail, verb, StringComparison.OrdinalIgnoreCase))
                return verb;
        }
        return null;
    }

    /// <summary>The <c>[controller]</c> expansion: the class name minus a trailing "Controller", lowercased.</summary>
    private static string ControllerToken(string parentClassName)
    {
        var name = parentClassName;
        const string suffix = "Controller";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
            name = name[..^suffix.Length];
        return name.ToLowerInvariant();
    }

    /// <summary>Join a prefix and tail with exactly one '/', tolerating either side being empty or slash-padded.</summary>
    private static string Join(string prefix, string tail)
    {
        var p = prefix.Trim('/');
        var t = tail.Trim('/');
        if (p.Length == 0)
            return t;
        if (t.Length == 0)
            return p;
        return p + "/" + t;
    }

    /// <summary>
    /// The shared canonicalization: strip a query string, fold <c>${p}</c>/<c>{p}</c>/<c>:p</c> params to <c>{}</c>,
    /// lowercase, and trim leading/trailing slashes. Order matters — params are folded before slash trimming so a
    /// route that is only a param is preserved.
    /// </summary>
    private static string Canonicalize(string route)
    {
        var s = route.Trim();

        // Strip query (and any fragment) — everything from the first '?' or '#'.
        int cut = s.IndexOfAny(['?', '#']);
        if (cut >= 0)
            s = s[..cut];

        s = ParamPattern.Replace(s, "{}");
        s = s.ToLowerInvariant();
        s = s.Trim('/');
        return s;
    }
}
