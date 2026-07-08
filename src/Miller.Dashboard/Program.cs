using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miller.Dashboard;
using Miller.Dashboard.Endpoints;
using Miller.Server;

DashboardPaths paths = DashboardPaths.FromEnvironment(AppContext.BaseDirectory);
string dashboardCssPath = Path.Combine(paths.WebRoot, "dashboard.css");
string htmxPath = Path.Combine(paths.WebRoot, "lib", "htmx", "htmx.min.js");
string alpinePath = Path.Combine(paths.WebRoot, "lib", "alpine", "cspalpine.min.js");
string themeInitPath = Path.Combine(paths.WebRoot, "js", "theme-init.js");
string siteJsPath = Path.Combine(paths.WebRoot, "js", "dashboard-site.js");
string alpineComponentsPath = Path.Combine(paths.WebRoot, "js", "alpine-components.js");
string archivoFontPath = Path.Combine(paths.WebRoot, "fonts", "archivo-latin.woff2");
string jetbrainsMonoFontPath = Path.Combine(paths.WebRoot, "fonts", "jetbrains-mono-latin.woff2");
string launchDirectory = Environment.GetEnvironmentVariable("MILLER_DASHBOARD_PREFERRED_ROOT")
    ?? Environment.CurrentDirectory;
const string FaviconSvg = """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">
  <rect width="32" height="32" fill="#f7f2e8"/>
  <path d="M7 8h18v3H7zM7 15h18v3H7zM7 22h12v3H7z" fill="#168276"/>
</svg>
""";

var host = new HostBuilder()
    .ConfigureWebHost(webBuilder =>
    {
        webBuilder
            .ConfigureLogging(logging =>
                logging.AddSimpleConsole()
                    .SetMinimumLevel(LogLevel.Information))
            .UseKestrel()
            .UseContentRoot(AppContext.BaseDirectory)
            .UseUrls(paths.Url)
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddRazorComponents();
            })
            .Configure(app =>
            {
                var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Miller.Dashboard");
                app.Use(async (context, next) =>
                {
                    try
                    {
                        await next(context);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Unhandled dashboard request exception");
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        context.Response.ContentType = "text/plain; charset=utf-8";
                        await context.Response.WriteAsync(
                            $"miller-dashboard error: {ex.GetType().Name}: {ex.Message}");
                    }
                });
                app.UseRouting();
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
            });
    })
    .Build();

host.Run();

static IResult StaticAsset(string path, string contentType) =>
    File.Exists(path) ? Results.File(path, contentType) : Results.NotFound();
