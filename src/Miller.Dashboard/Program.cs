using Miller.Dashboard;

var builder = WebApplication.CreateBuilder(args);
DashboardPaths paths = DashboardPaths.FromEnvironment(AppContext.BaseDirectory);
builder.WebHost.UseUrls(paths.Url);

var app = builder.Build();

app.MapGet("/", () => Results.Content(
    DashboardData.RenderIndexHtml(paths.RegistryDbPath),
    "text/html; charset=utf-8"));

app.MapGet("/workspaces.json", () => Results.Text(
    DashboardData.RenderWorkspacesJson(paths.RegistryDbPath),
    "application/json; charset=utf-8"));

app.MapGet("/telemetry.json", (string? workspace_id) => Results.Text(
    DashboardData.RenderTelemetryJson(paths.TelemetryDbPath, workspace_id),
    "application/json; charset=utf-8"));

app.MapPost("/workspaces/{workspace_id}/refresh", (string workspace_id) =>
{
    var result = DashboardData.RefreshWorkspace(paths.RegistryDbPath, paths.ToolsRoot, workspace_id);
    return Results.Json(result);
});

app.Run();

internal sealed record DashboardPaths(
    string RegistryDbPath,
    string TelemetryDbPath,
    string ToolsRoot,
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
        string port = Environment.GetEnvironmentVariable("MILLER_DASHBOARD_PORT") ?? "4977";
        if (!int.TryParse(port, out int parsedPort) || parsedPort is < 1 or > 65535)
            parsedPort = 4977;

        return new DashboardPaths(
            Path.GetFullPath(registry),
            Path.GetFullPath(telemetry),
            Path.GetFullPath(toolsRoot),
            $"http://127.0.0.1:{parsedPort}");
    }
}
