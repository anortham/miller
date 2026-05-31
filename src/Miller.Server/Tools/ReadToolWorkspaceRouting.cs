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
        if (json || string.IsNullOrWhiteSpace(requestedWorkspaceId))
            return null;

        var sb = new StringBuilder();
        sb.Append("workspace: ")
          .Append(Display(context, requestedWorkspaceId))
          .Append(' ')
          .Append(context.WorkspaceRoot);

        if (ShouldShowFreshness(context))
            sb.Append('\n').Append("freshness: ").Append(context.FreshnessStatus);

        return sb.ToString();
    }

    public static string PrefixCompact(string output, string? compactBanner) =>
        string.IsNullOrWhiteSpace(compactBanner) ? output : compactBanner + '\n' + output;

    public static void ApplyTelemetry(TelemetryScope? telemetry, WorkspaceReadContext context)
    {
        if (telemetry is null)
            return;

        if (!string.IsNullOrWhiteSpace(context.WorkspaceId))
            telemetry.SetWorkspace(context.WorkspaceId, context.WorkspaceRoot);

        telemetry.IndexFresh = context.IndexFresh;
    }

    private static bool ShouldShowFreshness(WorkspaceReadContext context)
    {
        if (string.IsNullOrWhiteSpace(context.FreshnessStatus))
            return false;

        return context.IndexFresh != true ||
               context.FreshnessStatus.StartsWith("unconfirmed", StringComparison.OrdinalIgnoreCase) ||
               context.FreshnessStatus.Contains("stale", StringComparison.OrdinalIgnoreCase);
    }

    private static string Display(WorkspaceReadContext context, string? requestedWorkspaceId)
    {
        if (!string.IsNullOrWhiteSpace(context.DisplayId))
            return context.DisplayId;
        if (!string.IsNullOrWhiteSpace(context.WorkspaceId))
            return context.WorkspaceId;
        return requestedWorkspaceId ?? "(unknown)";
    }
}
