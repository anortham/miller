using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miller.Dashboard.Endpoints;

namespace Miller.Dashboard;

/// <summary>
/// The dashboard host's service, middleware, and endpoint composition, extracted from
/// <c>Program.cs</c> so HTTP-level tests can stand up the EXACT production pipeline on an
/// in-memory TestServer — antiforgery validation on the mutation form posts (ADR-0002), the
/// exception wrapper, and the endpoint wiring — against per-test temp registry paths.
/// <c>Program.cs</c> keeps only what cannot run under TestServer: Kestrel, URLs, and logging.
/// </summary>
internal static class DashboardHostPipeline
{
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

    private static IResult StaticAsset(string path, string contentType) =>
        File.Exists(path) ? Results.File(path, contentType) : Results.NotFound();
}
