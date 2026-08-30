using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Primitives;
using Miller.Dashboard.Components;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server.Tools;
using Miller.Server.Workspaces;

namespace Miller.Dashboard.Endpoints;

internal static class DashboardEndpoints
{
    private const string DashboardRequestHeader = "X-Miller-Dashboard";

    /// <summary>
    /// htmx 2.0.4 cancels the REQUESTING element's polling on a 286 and still swaps the body. It is the
    /// second belt behind the self-targeted swap, and the only thing that stops a SECOND browser tab:
    /// refresh jobs are process-global, so only the first tab to poll ever observes the outcome.
    /// </summary>
    private const int PollingStoppedStatusCode = 286;

    /// <summary>
    /// Fired by the terminal refresh-status response's <c>HX-Trigger</c> header, which htmx dispatches on
    /// the requesting span before the swap. <c>#workspace-detail-stack</c> listens for it on <c>body</c>
    /// and refetches the ten panels once, so the 2-second poll never carries them again.
    /// </summary>
    private const string RefreshFinishedEvent = "miller:refresh-finished";

    /// <summary>
    /// CSRF guard for the POSTs that carry no antiforgery token (they are htmx triggers, not forms, and
    /// the loopback dashboard has no cookie session to bind a token to). A cross-origin <c>&lt;form&gt;</c>
    /// cannot set a custom request header at all, and a cross-origin <c>fetch</c> that sets one turns the
    /// request into a CORS preflight this server never answers — so the header's presence is proof the
    /// caller is the dashboard's own page. Returns null when the request may proceed.
    /// </summary>
    private static IResult? RequireDashboardRequestHeader(HttpContext context) =>
        context.Request.Headers.TryGetValue(DashboardRequestHeader, out StringValues values) &&
        values.Contains("1")
            ? null
            : Results.BadRequest($"Missing required {DashboardRequestHeader}: 1 header.");

    public static void MapDashboardEndpoints(
        IEndpointRouteBuilder endpoints,
        DashboardPaths paths,
        string launchDirectory)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchDirectory);

        endpoints.MapGet("/", (string? notice, string? detail) =>
            new RazorComponentResult<WorkspacesShell>(new
            {
                Index = DashboardData.ReadIndex(paths.RegistryDbPath, paths.TelemetryDbPath),
                Activity = DashboardData.ReadRecentActivity(
                    paths.TelemetryDbPath,
                    paths.RegistryDbPath,
                    workspaceId: null),
                Telemetry = DashboardData.ReadTelemetrySummary(paths.TelemetryDbPath, "all", paths.RegistryDbPath),
                // Remove/prune outcome from the post-redirect-get below; an unknown code renders nothing.
                Notice = notice,
                NoticeDetail = detail,
            })
            {
                PreventStreamingRendering = true,
            });

        // Registry-lifecycle mutations (ADR-0002). Both are antiforgery-validated form posts (form binding opts
        // the endpoint into validation once UseAntiforgery runs) and follow post-redirect-get back to the
        // all-workspaces view with an outcome notice — never a 500, same degrade discipline as the panel readers.
        endpoints.MapPost("/workspace/remove", ([FromForm] string? workspace_id) =>
            Results.Redirect(RemoveWorkspaceRedirect(paths.RegistryDbPath, paths.ToolsRoot, workspace_id)));

        endpoints.MapPost("/workspaces/prune", (IFormCollection form) =>
            Results.Redirect(PruneRedirect(paths.RegistryDbPath, paths.ToolsRoot)));

        endpoints.MapGet("/workspace", (string? workspace_id) =>
        {
            DashboardSnapshot snapshot = DashboardData.ReadSnapshot(
                paths.RegistryDbPath,
                paths.TelemetryDbPath,
                workspace_id,
                launchDirectory);
            // A requested id that did not resolve must not silently render the fallback workspace —
            // or, on an empty registry, the empty workspace shell.
            if (!string.IsNullOrWhiteSpace(workspace_id) &&
                !string.Equals(snapshot.SelectedWorkspaceId, workspace_id, StringComparison.Ordinal))
            {
                return new RazorComponentResult<NotFoundPage>(new
                {
                    Message = $"workspace_id '{workspace_id}' is not registered — open / for the workspace list.",
                })
                {
                    PreventStreamingRendering = true,
                    StatusCode = StatusCodes.Status404NotFound,
                };
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

        // The Tests section polls this while a daemon can move; it reads the CT sidecar through the
        // tests-status core and creates nothing.
        endpoints.MapGet("/fragments/tests", (string? workspace_id) =>
            new RazorComponentResult<WorkspaceTestsPanel>(new
            {
                Tests = DashboardData.ReadTests(paths.RegistryDbPath, workspace_id),
            })
            {
                PreventStreamingRendering = true,
            });

        endpoints.MapGet("/fragments/workspaces", () =>
            new RazorComponentResult<WorkspaceIndex>(new
            {
                Index = DashboardData.ReadIndex(paths.RegistryDbPath, paths.TelemetryDbPath),
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

        // A converge can run for minutes: start a background job and answer with the in-progress stack, which
        // polls /fragments/refresh-status until a terminal result renders. Blocking here would hold the request
        // open past the browser's patience and lose the outcome entirely.
        endpoints.MapPost("/fragments/refresh", (string workspace_id, HttpContext context) =>
        {
            if (RequireDashboardRequestHeader(context) is IResult rejected)
            {
                return rejected;
            }

            DashboardRefreshJobStatus job = DashboardRefreshJobs.Start(
                workspace_id,
                () => RefreshAndInvalidateFacts(paths, workspace_id));
            return DetailStackResult(paths, launchDirectory, workspace_id, job);
        });

        // The status span alone — the ten-panel stack used to ride along on every 2-second poll. The running
        // render self-targets, so htmx re-inits the swapped-in span and clears its own timer as soon as the
        // terminal render drops the poll attributes.
        endpoints.MapGet("/fragments/refresh-status", (string workspace_id, HttpContext context) =>
        {
            DashboardRefreshJobStatus? job = DashboardRefreshJobs.Peek(workspace_id);
            bool running = job is { State: DashboardRefreshJobState.Running };
            if (!running)
            {
                context.Response.Headers["HX-Trigger"] = RefreshFinishedEvent;
            }

            return new RazorComponentResult<RefreshStatusPanel>(new
            {
                Job = job,
                WorkspaceId = workspace_id,
            })
            {
                PreventStreamingRendering = true,
                StatusCode = running ? StatusCodes.Status200OK : PollingStoppedStatusCode,
            };
        });

        // The one refetch the terminal refresh-status response triggers. Peek has already consumed the job by
        // the time this lands, so the retained outcome is what keeps the status span readable.
        endpoints.MapGet("/fragments/detail-stack", (string workspace_id) =>
            DetailStackResult(
                paths,
                launchDirectory,
                workspace_id,
                DashboardRefreshJobs.PeekLastOutcome(workspace_id)));

        // Tests-panel lifecycle triggers. Same CSRF proof as the other htmx POSTs; the action itself
        // runs the public `miller tests` verb so the dashboard reuses the CLI's refusal, anchoring,
        // and daemon-replacement rules instead of growing CT logic of its own.
        endpoints.MapPost("/workspaces/{workspace_id}/tests/{action}", (string workspace_id, string action, HttpContext context) =>
        {
            if (RequireDashboardRequestHeader(context) is IResult rejected)
            {
                return rejected;
            }

            if (!DashboardTestsActions.IsAllowed(action))
            {
                return Results.NotFound($"Unknown tests action '{action}'.");
            }

            var workspace = DashboardData.ReadWorkspaces(paths.RegistryDbPath)
                .FirstOrDefault(w => string.Equals(w.WorkspaceId, workspace_id, StringComparison.Ordinal));
            if (workspace is null)
            {
                return Results.NotFound("Workspace not found in registry.");
            }

            DashboardTestsActionOutcome outcome = DashboardTestsActions.Run(
                paths.ToolsRoot,
                workspace.CanonicalRoot,
                action);
            return (IResult)new RazorComponentResult<WorkspaceTestsPanel>(new
            {
                Tests = DashboardData.ReadTests(paths.RegistryDbPath, workspace_id),
                ActionNotice = outcome.Message,
                ActionFailed = !outcome.Success,
            })
            {
                PreventStreamingRendering = true,
            };
        });

        endpoints.MapPost("/workspaces/{workspace_id}/open-folder", (string workspace_id, HttpContext context) =>
        {
            if (RequireDashboardRequestHeader(context) is IResult rejected)
            {
                return rejected;
            }

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

    private static IResult DetailStackResult(
        DashboardPaths paths,
        string launchDirectory,
        string workspaceId,
        DashboardRefreshJobStatus? job)
    {
        DashboardSnapshot snapshot = DashboardData.ReadSnapshot(
            paths.RegistryDbPath,
            paths.TelemetryDbPath,
            workspaceId,
            launchDirectory);
        return new RazorComponentResult<WorkspaceDetailStack>(new
        {
            Snapshot = snapshot,
            Activity = DashboardData.ReadRecentActivity(
                paths.TelemetryDbPath,
                paths.RegistryDbPath,
                snapshot.SelectedWorkspaceId),
            RefreshJob = job,
        })
        {
            PreventStreamingRendering = true,
        };
    }

    // Runs on the job's background thread. The cached facts describe the index this refresh just rewrote, so
    // they are dropped as the job ends — even for a failed scan, which may still have replaced the artifact.
    private static WorkspaceRefreshResult RefreshAndInvalidateFacts(DashboardPaths paths, string workspaceId)
    {
        try
        {
            return DashboardData.TryRefreshWorkspace(paths.RegistryDbPath, paths.ToolsRoot, workspaceId);
        }
        finally
        {
            DashboardIndexFactsCache.Clear();
        }
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
            DashboardData.RenderIndexJson(paths.RegistryDbPath, paths.TelemetryDbPath),
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

        endpoints.MapPost("/workspaces/{workspace_id}/refresh", (string workspace_id, HttpContext context) =>
        {
            if (RequireDashboardRequestHeader(context) is IResult rejected)
            {
                return rejected;
            }

            // Parity with the htmx /fragments/refresh route: any failure (unregistered id, missing extractor,
            // scan fault) renders as a Failed result body instead of a 500 with an empty body.
            var result = DashboardData.TryRefreshWorkspace(paths.RegistryDbPath, paths.ToolsRoot, workspace_id);
            return Results.Text(
                DashboardData.RenderRefreshJson(result),
                "application/json; charset=utf-8");
        });
    }

    // The workspace_id arrives from the per-row confirm form (any registry selector works, but the form sends
    // the full id). liveRoot is null: the dashboard process serves no workspace in-process. Active WRITERS are
    // refused by WorkspaceRemoval's in-use lease check; pure READERS hold no lease and are not blocked — same
    // as CLI remove — they fail loudly on their next reopen and the index is rebuildable via workspace open.
    private static string RemoveWorkspaceRedirect(string registryDbPath, string toolsRoot, string? workspaceId)
    {
        string code;
        string? detail;
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            (code, detail) = ("remove-not-found", null);
        }
        else
        {
            try
            {
                using WorkspaceRegistry registry = WorkspaceRegistry.Open(registryDbPath);
                WorkspaceRemoveResult result = WorkspaceRemoval.RemoveById(
                    registry,
                    workspaceId,
                    liveRoot: null,
                    protectedMillerDir: Path.GetDirectoryName(registryDbPath),
                    retireView: StoreViewRetirementRunner.ForToolsRoot(toolsRoot));
                code = result.Result switch
                {
                    WorkspaceRemoveResult.Outcome.Removed when result.IndexDirDeleted => "removed",
                    WorkspaceRemoveResult.Outcome.Removed => "removed-registration",
                    WorkspaceRemoveResult.Outcome.RefusedLive => "remove-refused-live",
                    WorkspaceRemoveResult.Outcome.RefusedInUse => "remove-refused-in-use",
                    WorkspaceRemoveResult.Outcome.RefusedSensitive => "remove-refused-sensitive",
                    WorkspaceRemoveResult.Outcome.RefusedInvalidRegistration => "remove-refused-invalid-registration",
                    WorkspaceRemoveResult.Outcome.RefusedRetirement => "remove-error",
                    _ => "remove-not-found",
                };
                detail = result.Result == WorkspaceRemoveResult.Outcome.RefusedRetirement
                    ? $"Producer view retirement failed for {result.Root ?? workspaceId}. {result.ViewRetirement?.Error ?? "Unknown producer retirement failure"} The registry entry was kept for retry."
                    : result.Root ?? workspaceId;
            }
            catch (KeyNotFoundException)
            {
                (code, detail) = ("remove-not-found", workspaceId);
            }
            catch (Exception ex) when (
                ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // A bad registry/index state degrades to a notice on the list, never a 500.
                (code, detail) = ("remove-error", ex.Message);
            }
            finally
            {
                // Even a FAILED remove may have deleted some index files (the delete is not atomic), so drop
                // cached facts unconditionally — the list must never render pre-remove facts over partial state.
                DashboardIndexFactsCache.Clear();
            }
        }

        return NoticeRedirect(code, detail);
    }

    private static string PruneRedirect(string registryDbPath, string toolsRoot)
    {
        try
        {
            using WorkspaceRegistry registry = WorkspaceRegistry.Open(registryDbPath);
            WorkspaceRegistryPrune.Result result =
                WorkspaceRegistryPrune.Run(
                    registry,
                    protectedWorkspaceId: null,
                    dryRun: false,
                    retireView: StoreViewRetirementRunner.ForToolsRoot(toolsRoot));
            // A kept row is the normal outcome, not a failure: a run that hits the per-run retirement cap, or a
            // row whose removal it cannot confirm, still prunes everything else. Reporting an error whenever any
            // row was kept would show one on every registry that has a backlog — which is every registry that
            // needs pruning at all. The panel names what stayed behind.
            if (result.Pruned.Count > 0)
                return NoticeRedirect("pruned", result.Pruned.Count.ToString(CultureInfo.InvariantCulture));

            if (result.RetirementFailures.Count > 0)
            {
                WorkspaceRegistryPrune.RetirementFailure failure = result.RetirementFailures[0];
                string detail = result.RetirementFailures.Count == 1
                    ? $"Producer view retirement failed for {failure.DisplayId}. {failure.Outcome.Error ?? "Unknown producer retirement failure"} The registry entry was kept for retry."
                    : $"Producer view retirement failed for {result.RetirementFailures.Count} workspaces. The registry entries were kept for retry. First failure: {failure.Outcome.Error ?? "Unknown producer retirement failure"}";
                return NoticeRedirect("remove-error", detail);
            }

            return NoticeRedirect("pruned", "0");
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return NoticeRedirect("remove-error", ex.Message);
        }
        finally
        {
            // A failed prune may still have removed some rows mid-loop; never serve pre-prune cached facts.
            DashboardIndexFactsCache.Clear();
        }
    }

    private static string NoticeRedirect(string code, string? detail) =>
        detail is null ? $"/?notice={code}" : $"/?notice={code}&detail={Uri.EscapeDataString(detail)}";

    private static DashboardRuntimeInfo BuildRuntimeInfo(DashboardPaths paths, string launchDirectory)
    {
        string machineMillerDir = Path.GetDirectoryName(paths.RegistryDbPath)
            ?? Miller.Indexing.MillerHome.Resolve();
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
