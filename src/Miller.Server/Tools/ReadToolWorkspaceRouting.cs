using System.Text;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;

namespace Miller.Server.Tools;

internal static class ReadToolWorkspaceRouting
{
    public static bool ResolveEnsureFresh(string? workspaceId, bool? ensureFresh) =>
        workspaceId is null ? ensureFresh ?? false : ensureFresh ?? true;

    public static string? CompactBanner(WorkspaceReadContext context, string? requestedWorkspaceId, bool json)
    {
        return CompactBanner(
            context.DisplayId,
            context.WorkspaceId,
            context.WorkspaceRoot,
            context.IndexFresh,
            context.FreshnessStatus,
            requestedWorkspaceId,
            json);
    }

    public static string? CompactBanner(WorkspaceSymbolSearchContext context, string? requestedWorkspaceId, bool json)
    {
        return CompactBanner(
            context.DisplayId,
            context.WorkspaceId,
            context.WorkspaceRoot,
            context.IndexFresh,
            context.FreshnessStatus,
            requestedWorkspaceId,
            json);
    }

    private static string? CompactBanner(
        string? displayId,
        string? workspaceId,
        string workspaceRoot,
        bool? indexFresh,
        string freshnessStatus,
        string? requestedWorkspaceId,
        bool json)
    {
        if (json || string.IsNullOrWhiteSpace(requestedWorkspaceId))
            return null;

        var sb = new StringBuilder();
        sb.Append("workspace: ")
          .Append(Display(displayId, workspaceId, requestedWorkspaceId))
          .Append(' ')
          .Append(workspaceRoot);

        if (ShouldShowFreshness(indexFresh, freshnessStatus))
            sb.Append('\n').Append("freshness: ").Append(freshnessStatus);

        return sb.ToString();
    }

    public static string PrefixCompact(string output, string? compactBanner) =>
        string.IsNullOrWhiteSpace(compactBanner) ? output : compactBanner + '\n' + output;

    public static void ApplyTelemetry(TelemetryScope? telemetry, WorkspaceReadContext context)
    {
        ApplyTelemetry(telemetry, context.WorkspaceId, context.WorkspaceRoot, context.IndexFresh);
    }

    public static void ApplyTelemetry(TelemetryScope? telemetry, WorkspaceSymbolSearchContext context)
    {
        ApplyTelemetry(telemetry, context.WorkspaceId, context.WorkspaceRoot, context.IndexFresh);
    }

    private static void ApplyTelemetry(
        TelemetryScope? telemetry, string? workspaceId, string workspaceRoot, bool? indexFresh)
    {
        if (telemetry is null)
            return;

        if (!string.IsNullOrWhiteSpace(workspaceId))
            telemetry.SetWorkspace(workspaceId, workspaceRoot);

        telemetry.IndexFresh = indexFresh;
    }

    private static bool ShouldShowFreshness(bool? indexFresh, string freshnessStatus)
    {
        if (string.IsNullOrWhiteSpace(freshnessStatus))
            return false;

        return indexFresh != true ||
               freshnessStatus.StartsWith("unconfirmed", StringComparison.OrdinalIgnoreCase) ||
               freshnessStatus.Contains("stale", StringComparison.OrdinalIgnoreCase);
    }

    private static string Display(string? displayId, string? workspaceId, string? requestedWorkspaceId)
    {
        if (!string.IsNullOrWhiteSpace(displayId))
            return displayId;
        if (!string.IsNullOrWhiteSpace(workspaceId))
            return workspaceId;
        return requestedWorkspaceId ?? "(unknown)";
    }
}
