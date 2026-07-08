using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Miller.Dashboard.Components;

namespace Miller.Dashboard.Endpoints;

internal static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(
        IEndpointRouteBuilder endpoints,
        DashboardPaths paths,
        string launchDirectory)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchDirectory);

        endpoints.MapGet("/", () =>
            new RazorComponentResult<WorkspacesShell>(new
            {
                Index = DashboardData.ReadIndex(paths.RegistryDbPath),
                Activity = DashboardData.ReadRecentActivity(
                    paths.TelemetryDbPath,
                    paths.RegistryDbPath,
                    workspaceId: null),
                Telemetry = DashboardData.ReadTelemetrySummary(paths.TelemetryDbPath, "all", paths.RegistryDbPath),
            })
            {
                PreventStreamingRendering = true,
            });

        endpoints.MapGet("/workspace", (string? workspace_id) =>
        {
            DashboardSnapshot snapshot = DashboardData.ReadSnapshot(
                paths.RegistryDbPath,
                paths.TelemetryDbPath,
                workspace_id,
                launchDirectory);
            // A requested id that did not resolve must not silently render the fallback workspace.
            if (!string.IsNullOrWhiteSpace(workspace_id) &&
                snapshot.Workspaces.Count > 0 &&
                !string.Equals(snapshot.SelectedWorkspaceId, workspace_id, StringComparison.Ordinal))
            {
                return Results.NotFound(
                    $"workspace_id '{workspace_id}' is not registered — open / for the workspace list.");
            }

            return (IResult)new RazorComponentResult<WorkspaceShell>(new
            {
                Snapshot = snapshot,
                Activity = DashboardData.ReadRecentActivity(
                    paths.TelemetryDbPath,
                    paths.RegistryDbPath,
                    snapshot.SelectedWorkspaceId),
            })
            {
                PreventStreamingRendering = true,
            };
        });

        endpoints.MapGet("/fragments/activity", (string? workspace_id) =>
            new RazorComponentResult<ActivityFeedPanel>(new
            {
                Feed = DashboardData.ReadRecentActivity(
                    paths.TelemetryDbPath,
                    paths.RegistryDbPath,
                    workspace_id),
            })
            {
                PreventStreamingRendering = true,
            });

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
                Telemetry = DashboardData.ReadTelemetrySummary(paths.TelemetryDbPath, workspace_id, paths.RegistryDbPath),
                SelectedWorkspaceId = workspace_id,
            })
            {
                PreventStreamingRendering = true,
            });

        endpoints.MapPost("/fragments/refresh", (string workspace_id) =>
        {
            var result = DashboardData.TryRefreshWorkspace(
                paths.RegistryDbPath,
                paths.ToolsRoot,
                workspace_id);
            DashboardIndexFactsCache.Clear();
            DashboardSnapshot snapshot = DashboardData.ReadSnapshot(
                paths.RegistryDbPath,
                paths.TelemetryDbPath,
                workspace_id,
                launchDirectory);
            return new RazorComponentResult<WorkspaceDetailStack>(new
            {
                Snapshot = snapshot,
                Activity = DashboardData.ReadRecentActivity(
                    paths.TelemetryDbPath,
                    paths.RegistryDbPath,
                    snapshot.SelectedWorkspaceId),
                RefreshResult = result,
            })
            {
                PreventStreamingRendering = true,
            };
        });

        endpoints.MapPost("/workspaces/{workspace_id}/open-folder", (string workspace_id) =>
        {
            var workspaces = DashboardData.ReadWorkspaces(paths.RegistryDbPath);
            var workspace = workspaces.FirstOrDefault(w => string.Equals(w.WorkspaceId, workspace_id, StringComparison.Ordinal));
            if (workspace is null)
            {
                return Results.NotFound("Workspace not found in registry.");
            }

            if (!Directory.Exists(workspace.CanonicalRoot))
            {
                return Results.BadRequest($"Directory does not exist: {workspace.CanonicalRoot}");
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = workspace.CanonicalRoot,
                    UseShellExecute = true
                });
                return Results.Ok();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });
    }

    public static void MapDashboardJsonEndpoints(
        IEndpointRouteBuilder endpoints,
        DashboardPaths paths,
        string launchDirectory)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchDirectory);

        endpoints.MapGet("/workspaces.json", () => Results.Text(
            DashboardData.RenderWorkspacesJson(paths.RegistryDbPath),
            "application/json; charset=utf-8"));

        endpoints.MapGet("/index.json", () => Results.Text(
            DashboardData.RenderIndexJson(paths.RegistryDbPath),
            "application/json; charset=utf-8"));

        endpoints.MapGet("/activity.json", (string? workspace_id) => Results.Text(
            DashboardData.RenderActivityJson(paths.TelemetryDbPath, paths.RegistryDbPath, workspace_id),
            "application/json; charset=utf-8"));

        endpoints.MapGet("/telemetry.json", (string? workspace_id) => Results.Text(
            DashboardData.RenderTelemetryJson(paths.TelemetryDbPath, workspace_id, paths.RegistryDbPath),
            "application/json; charset=utf-8"));

        endpoints.MapGet("/snapshot.json", (string? workspace_id) => Results.Text(
            DashboardData.RenderSnapshotJson(paths.RegistryDbPath, paths.TelemetryDbPath, workspace_id, launchDirectory),
            "application/json; charset=utf-8"));

        endpoints.MapGet("/diagnostics.json", () => Results.Text(
            DashboardData.RenderDiagnosticsJson(BuildRuntimeInfo(paths, launchDirectory)),
            "application/json; charset=utf-8"));

        endpoints.MapPost("/workspaces/{workspace_id}/refresh", (string workspace_id) =>
        {
            // Parity with the htmx /fragments/refresh route: any failure (unregistered id, missing extractor,
            // scan fault) renders as a Failed result body instead of a 500 with an empty body.
            var result = DashboardData.TryRefreshWorkspace(paths.RegistryDbPath, paths.ToolsRoot, workspace_id);
            return Results.Text(
                DashboardData.RenderRefreshJson(result),
                "application/json; charset=utf-8");
        });
    }

    private static DashboardRuntimeInfo BuildRuntimeInfo(DashboardPaths paths, string launchDirectory)
    {
        string machineMillerDir = Path.GetDirectoryName(paths.RegistryDbPath)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new DashboardRuntimeInfo(
            paths.RegistryDbPath,
            paths.TelemetryDbPath,
            paths.ToolsRoot,
            paths.WebRoot,
            paths.Url,
            Path.GetFullPath(launchDirectory),
            Environment.ProcessId,
            Miller.Server.MillerVersion.Current,
            Environment.ProcessPath,
            Path.Combine(machineMillerDir, "dashboard.out.log"),
            Path.Combine(machineMillerDir, "dashboard.err.log"));
    }
}
