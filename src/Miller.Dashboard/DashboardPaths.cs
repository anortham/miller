namespace Miller.Dashboard;

internal sealed record DashboardPaths(
    string RegistryDbPath,
    string TelemetryDbPath,
    string ToolsRoot,
    string WebRoot,
    string Url)
{
    public static DashboardPaths FromEnvironment(string appBaseDirectory)
    {
        string millerHome = Miller.Indexing.MillerHome.ResolveMillerDirectory();
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
