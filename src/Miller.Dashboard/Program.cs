using Microsoft.AspNetCore.Http.HttpResults;
using Miller.Dashboard;
using Miller.Dashboard.Components;

DashboardPaths paths = DashboardPaths.FromEnvironment(AppContext.BaseDirectory);
string dashboardCssPath = Path.Combine(paths.WebRoot, "dashboard.css");
string htmxPath = Path.Combine(paths.WebRoot, "lib", "htmx", "htmx.min.js");
string launchDirectory = Environment.GetEnvironmentVariable("MILLER_DASHBOARD_PREFERRED_ROOT")
    ?? Environment.CurrentDirectory;

var host = new HostBuilder()
    .ConfigureWebHost(webBuilder =>
    {
        webBuilder
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
                    endpoints.MapGet("/healthz", () => Results.Text(
                        "miller-dashboard ok",
                        "text/plain; charset=utf-8"));

                    // Landing page: full-width index of every registered workspace with stats.
                    endpoints.MapGet("/", () =>
                        new RazorComponentResult<WorkspacesShell>(new
                        {
                            Index = DashboardData.ReadIndex(paths.RegistryDbPath),
                        })
                        {
                            PreventStreamingRendering = true,
                        });

                    // Per-workspace detail page: full-width index facts, context savings, telemetry.
                    endpoints.MapGet("/workspace", (string? workspace_id) =>
                        new RazorComponentResult<WorkspaceShell>(new
                        {
                            Snapshot = DashboardData.ReadSnapshot(
                                paths.RegistryDbPath,
                                paths.TelemetryDbPath,
                                workspace_id,
                                launchDirectory),
                        })
                        {
                            PreventStreamingRendering = true,
                        });

                    // Legacy HTMX fragment routes retained for older dashboard links and dogfood evidence.
                    endpoints.MapGet("/fragments/dashboard", (string? workspace_id) =>
                        new RazorComponentResult<DashboardContent>(new
                        {
                            Snapshot = DashboardData.ReadSnapshot(
                                paths.RegistryDbPath,
                                paths.TelemetryDbPath,
                                workspace_id,
                                launchDirectory),
                        })
                        {
                            PreventStreamingRendering = true,
                        });

                    endpoints.MapGet("/fragments/workspaces", () =>
                        new RazorComponentResult<WorkspaceIndex>(new
                        {
                            Index = DashboardData.ReadIndex(paths.RegistryDbPath),
                        })
                        {
                            PreventStreamingRendering = true,
                        });

                    endpoints.MapGet("/fragments/telemetry", (string? workspace_id) =>
                        new RazorComponentResult<TelemetryPanel>(new
                        {
                            Telemetry = DashboardData.ReadTelemetrySummary(paths.TelemetryDbPath, workspace_id),
                            SelectedWorkspaceId = workspace_id,
                        })
                        {
                            PreventStreamingRendering = true,
                        });

                    endpoints.MapGet("/workspaces.json", () => Results.Text(
                        DashboardData.RenderWorkspacesJson(paths.RegistryDbPath),
                        "application/json; charset=utf-8"));

                    endpoints.MapGet("/index.json", () => Results.Text(
                        DashboardData.RenderIndexJson(paths.RegistryDbPath),
                        "application/json; charset=utf-8"));

                    endpoints.MapGet("/telemetry.json", (string? workspace_id) => Results.Text(
                        DashboardData.RenderTelemetryJson(paths.TelemetryDbPath, workspace_id),
                        "application/json; charset=utf-8"));

                    endpoints.MapGet("/snapshot.json", (string? workspace_id) => Results.Text(
                        DashboardData.RenderSnapshotJson(paths.RegistryDbPath, paths.TelemetryDbPath, workspace_id),
                        "application/json; charset=utf-8"));

                    endpoints.MapPost("/workspaces/{workspace_id}/refresh", (string workspace_id) =>
                    {
                        var result = DashboardData.RefreshWorkspace(paths.RegistryDbPath, paths.ToolsRoot, workspace_id);
                        return Results.Json(result);
                    });
                });
            });
    })
    .Build();

host.Run();

static IResult StaticAsset(string path, string contentType) =>
    File.Exists(path) ? Results.File(path, contentType) : Results.NotFound();

internal sealed record DashboardPaths(
    string RegistryDbPath,
    string TelemetryDbPath,
    string ToolsRoot,
    string WebRoot,
    string Url)
{
    public static DashboardPaths FromEnvironment(string appBaseDirectory)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string millerHome = Path.Combine(home, ".miller");
        string registry = Environment.GetEnvironmentVariable("MILLER_REGISTRY_DB")
            ?? Path.Combine(millerHome, "workspaces.db");
        string telemetry = Environment.GetEnvironmentVariable("MILLER_TELEMETRY_DB")
            ?? Path.Combine(millerHome, "telemetry.db");
        string toolsRoot = Environment.GetEnvironmentVariable("MILLER_TOOLS_ROOT")
            ?? Path.Combine(Path.GetFullPath(appBaseDirectory), ".tools");
        string webRoot = Environment.GetEnvironmentVariable("MILLER_DASHBOARD_WEBROOT")
            ?? Path.Combine(Path.GetFullPath(appBaseDirectory), "wwwroot");
        string port = Environment.GetEnvironmentVariable("MILLER_DASHBOARD_PORT") ?? "4977";
        if (!int.TryParse(port, out int parsedPort) || parsedPort is < 1 or > 65535)
            parsedPort = 4977;

        return new DashboardPaths(
            Path.GetFullPath(registry),
            Path.GetFullPath(telemetry),
            Path.GetFullPath(toolsRoot),
            Path.GetFullPath(webRoot),
            $"http://127.0.0.1:{parsedPort}");
    }
}
