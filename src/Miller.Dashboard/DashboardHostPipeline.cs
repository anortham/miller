using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Miller.Dashboard.Endpoints;

namespace Miller.Dashboard;

/// <summary>
/// The dashboard host's service, middleware, and endpoint composition, extracted from
/// <c>Program.cs</c> so HTTP-level tests can stand up the EXACT production pipeline on an
/// in-memory TestServer — antiforgery validation on the mutation form posts (ADR-0002), the
/// exception wrapper, and the endpoint wiring — against per-test temp registry paths.
/// <c>Program.cs</c> keeps only what cannot run under TestServer: Kestrel, URLs, and logging.
/// </summary>
internal static partial class DashboardHostPipeline
{
    private const string AntiforgeryCookiePrefix = ".AspNetCore.Antiforgery.";
    private const string AntiforgeryTokenMask = "__antiforgery-token-masked-for-hashing__";

    /// <summary>
    /// The rendered antiforgery form token. ASP.NET Data Protection encrypts it with a fresh random IV on
    /// every <c>Protect()</c> call, so the value differs on every render even for one cookie — hashing it
    /// would make each poll's ETag unique and defeat 304s entirely. Verified name-then-value attribute order.
    /// </summary>
    [GeneratedRegex("""(name="__RequestVerificationToken"\s+value=")[^"]*(")""")]
    private static partial Regex AntiforgeryTokenValue();

    private const string FaviconSvg = """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">
  <rect width="32" height="32" fill="#f7f2e8"/>
  <path d="M7 8h18v3H7zM7 15h18v3H7zM7 22h12v3H7z" fill="#168276"/>
</svg>
""";

    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddRouting();
        services.AddRazorComponents();
        // The remove/prune form posts are antiforgery-validated (ADR-0002); the <AntiforgeryToken/>
        // component and the form-binding validation both need the antiforgery services + middleware.
        services.AddAntiforgery();
    }

    public static void Configure(IApplicationBuilder app, DashboardPaths paths, string launchDirectory)
    {
        string dashboardCssPath = Path.Combine(paths.WebRoot, "dashboard.css");
        string htmxPath = Path.Combine(paths.WebRoot, "lib", "htmx", "htmx.min.js");
        string alpinePath = Path.Combine(paths.WebRoot, "lib", "alpine", "cspalpine.min.js");
        string themeInitPath = Path.Combine(paths.WebRoot, "js", "theme-init.js");
        string siteJsPath = Path.Combine(paths.WebRoot, "js", "dashboard-site.js");
        string alpineComponentsPath = Path.Combine(paths.WebRoot, "js", "alpine-components.js");
        string idiomorphPath = Path.Combine(paths.WebRoot, "lib", "idiomorph", "idiomorph-ext.min.js");
        string archivoFontPath = Path.Combine(paths.WebRoot, "fonts", "archivo-latin.woff2");
        string jetbrainsMonoFontPath = Path.Combine(paths.WebRoot, "fonts", "jetbrains-mono-latin.woff2");

        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Miller.Dashboard");
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception ex) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client disconnects are routine, not failures; don't pollute the error log.
                logger.LogDebug(ex, "Dashboard request aborted by client");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled dashboard request exception");
                if (context.Response.HasStarted)
                    throw; // Headers are gone; rewriting the response would itself throw.
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(
                    $"miller-dashboard error: {ex.GetType().Name}: {ex.Message}");
            }
        });
        app.Use(FragmentETagAsync);
        app.UseRouting();
        app.UseAntiforgery();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapMethods(
                "/dashboard.css",
                ["GET", "HEAD"],
                () => StaticAsset(dashboardCssPath, "text/css; charset=utf-8"));
            endpoints.MapMethods(
                "/lib/htmx/htmx.min.js",
                ["GET", "HEAD"],
                () => StaticAsset(htmxPath, "text/javascript; charset=utf-8"));
            endpoints.MapMethods(
                "/lib/alpine/cspalpine.min.js",
                ["GET", "HEAD"],
                () => StaticAsset(alpinePath, "text/javascript; charset=utf-8"));
            endpoints.MapMethods(
                "/js/theme-init.js",
                ["GET", "HEAD"],
                () => StaticAsset(themeInitPath, "text/javascript; charset=utf-8"));
            endpoints.MapMethods(
                "/js/dashboard-site.js",
                ["GET", "HEAD"],
                () => StaticAsset(siteJsPath, "text/javascript; charset=utf-8"));
            endpoints.MapMethods(
                "/js/alpine-components.js",
                ["GET", "HEAD"],
                () => StaticAsset(alpineComponentsPath, "text/javascript; charset=utf-8"));
            endpoints.MapMethods(
                "/lib/idiomorph/idiomorph-ext.min.js",
                ["GET", "HEAD"],
                () => StaticAsset(idiomorphPath, "text/javascript; charset=utf-8"));
            endpoints.MapMethods(
                "/fonts/archivo-latin.woff2",
                ["GET", "HEAD"],
                () => StaticAsset(archivoFontPath, "font/woff2"));
            endpoints.MapMethods(
                "/fonts/jetbrains-mono-latin.woff2",
                ["GET", "HEAD"],
                () => StaticAsset(jetbrainsMonoFontPath, "font/woff2"));
            endpoints.MapMethods("/favicon.ico", ["GET", "HEAD"], () =>
                Results.Text(FaviconSvg, "image/svg+xml; charset=utf-8"));
            endpoints.MapGet("/healthz", () => Results.Text(
                "miller-dashboard ok",
                "text/plain; charset=utf-8"));

            DashboardEndpoints.MapDashboardEndpoints(endpoints, paths, launchDirectory);
            DashboardEndpoints.MapDashboardJsonEndpoints(endpoints, paths, launchDirectory);
        });
    }

    /// <summary>
    /// Conditional-GET support for the polled fragments: hash each rendered fragment into a strong
    /// <c>ETag</c> and answer a matching <c>If-None-Match</c> with <c>304</c>, so an unchanged poll
    /// transfers and re-renders nothing. Pairs with the client's morph swaps (dashboard-site.js):
    /// a 304 is turned into a no-swap, leaving the live DOM — and its focus/selection — untouched.
    /// </summary>
    private static async Task FragmentETagAsync(HttpContext context, RequestDelegate next)
    {
        if (!HttpMethods.IsGet(context.Request.Method) ||
            !context.Request.Path.StartsWithSegments("/fragments"))
        {
            await next(context);
            return;
        }

        Stream originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        try
        {
            context.Response.Body = buffer;
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        // Only a clean, fully-buffered 200 is safe to hash; anything else replays untouched.
        if (context.Response.StatusCode != StatusCodes.Status200OK)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted);
            return;
        }

        byte[] payload = buffer.ToArray();
        string etag = ComputeFragmentETag(payload, context.Request);
        context.Response.Headers.ETag = etag;

        if (RequestMatchesETag(context.Request, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.ContentLength = null;
            context.Response.Headers.Remove(HeaderNames.ContentType);
            return;
        }

        context.Response.ContentLength = payload.Length;
        await originalBody.WriteAsync(payload, context.RequestAborted);
    }

    /// <summary>
    /// Hashes what actually varies with content. The token values are masked out first (see
    /// <see cref="AntiforgeryTokenValue"/>) and the client's antiforgery cookie is folded in as a salt —
    /// the served 200 body is always the ORIGINAL bytes, tokens intact. Masking alone would be unsafe: a
    /// 304 leaves the previously delivered token in the live DOM, and that token is only valid for the
    /// cookie it was minted against. Salting means a rotated cookie changes the ETag, forcing a 200 with a
    /// freshly matched token rather than stranding a stale one. (Antiforgery tokens are not single-use, so
    /// a retained token keeps working as long as its cookie does.)
    /// </summary>
    private static string ComputeFragmentETag(byte[] payload, HttpRequest request)
    {
        string masked = AntiforgeryTokenValue().Replace(
            Encoding.UTF8.GetString(payload), $"$1{AntiforgeryTokenMask}$2");
        string salt = AntiforgerySalt(request);
        // NUL separates content from salt: it occurs in neither, so no two inputs can collide.
        byte[] hashInput = Encoding.UTF8.GetBytes($"{masked}\u0000{salt}");
        return $"\"{Convert.ToHexString(SHA256.HashData(hashInput)).ToLowerInvariant()}\"";
    }

    private static string AntiforgerySalt(HttpRequest request)
    {
        foreach (KeyValuePair<string, string> cookie in request.Cookies)
        {
            if (cookie.Key.StartsWith(AntiforgeryCookiePrefix, StringComparison.Ordinal))
            {
                return $"{cookie.Key}={cookie.Value}";
            }
        }
        return string.Empty;
    }

    private static bool RequestMatchesETag(HttpRequest request, string etag)
    {
        if (!request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var candidates))
        {
            return false;
        }

        foreach (string? candidate in candidates)
        {
            if (candidate is null)
            {
                continue;
            }
            foreach (string part in candidate.Split(','))
            {
                string trimmed = part.Trim();
                if (trimmed == "*" || string.Equals(trimmed, etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static IResult StaticAsset(string path, string contentType) =>
        File.Exists(path) ? Results.File(path, contentType) : Results.NotFound();
}
