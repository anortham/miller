using System.Text;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;

namespace Miller.Server.Tools;

internal static class ReadToolWorkspaceRouting
{
    /// <summary>
    /// Turn the caller's <c>ensure_fresh</c> answer into the refresh mode the provider runs.
    ///
    /// <para>An explicit <c>workspace_id</c> used to mean "refresh first and wait", which put a whole cross-workspace
    /// scan in front of every read (measured p50 ~2.9s, p95 20s+ against a current-workspace p50 of 757ms). The
    /// DEFAULT is now <see cref="WorkspaceRefreshMode.Background"/>: serve the pinned view now, refresh off the read
    /// path, and say so in the output. Both EXPLICIT answers are unchanged — <c>true</c> still waits, <c>false</c>
    /// still does zero refresh work.</para>
    /// </summary>
    public static WorkspaceRefreshMode ResolveRefreshMode(string? workspaceId, bool? ensureFresh) =>
        ensureFresh switch
        {
            true => WorkspaceRefreshMode.Blocking,
            false => WorkspaceRefreshMode.None,
            // No answer: only a named target has anything to refresh in the background; the current workspace is
            // already converged by its own leader/watcher and has never refreshed on a read.
            null => workspaceId is null ? WorkspaceRefreshMode.None : WorkspaceRefreshMode.Background,
        };

    public static string? CompactBanner(WorkspaceReadContext context, string? requestedWorkspaceId, bool json)
    {
        return CompactBanner(
            context.DisplayId,
            context.WorkspaceId,
            context.WorkspaceRoot,
            context.IndexFresh,
            context.FreshnessStatus,
            context.Revision,
            requestedWorkspaceId,
            json,
            context.WarningText,
            context.BackgroundOperation);
    }

    public static string? CompactBanner(WorkspaceArtifactContext context, string? requestedWorkspaceId, bool json)
    {
        return CompactBanner(
            context.DisplayId,
            context.WorkspaceId,
            context.WorkspaceRoot,
            context.IndexFresh,
            context.FreshnessStatus,
            context.Revision,
            requestedWorkspaceId,
            json,
            context.WarningText,
            context.BackgroundOperation);
    }

    public static string? CompactBanner(WorkspaceSymbolSearchContext context, string? requestedWorkspaceId, bool json)
    {
        return CompactBanner(
            context.DisplayId,
            context.WorkspaceId,
            context.WorkspaceRoot,
            context.IndexFresh,
            context.FreshnessStatus,
            context.Revision,
            requestedWorkspaceId,
            json,
            context.WarningText,
            context.BackgroundOperation);
    }

    public static string? CompactBanner(WorkspaceSymbolReadContext context, string? requestedWorkspaceId, bool json)
    {
        return CompactBanner(
            context.DisplayId,
            context.WorkspaceId,
            context.WorkspaceRoot,
            context.IndexFresh,
            context.FreshnessStatus,
            context.Revision,
            requestedWorkspaceId,
            json,
            context.WarningText,
            context.BackgroundOperation);
    }

    public static string? CompactBanner(WorkspaceContentSearchContext context, string? requestedWorkspaceId, bool json)
    {
        return CompactBanner(
            context.DisplayId,
            context.WorkspaceId,
            context.WorkspaceRoot,
            context.IndexFresh,
            context.FreshnessStatus,
            context.Revision,
            requestedWorkspaceId,
            json,
            context.WarningText,
            context.BackgroundOperation);
    }

    public static string? CompactBanner(WorkspaceRegionSearchContext context, string? requestedWorkspaceId, bool json)
    {
        return CompactBanner(
            context.DisplayId,
            context.WorkspaceId,
            context.WorkspaceRoot,
            context.IndexFresh,
            context.FreshnessStatus,
            context.Revision,
            requestedWorkspaceId,
            json,
            context.WarningText,
            context.BackgroundOperation);
    }

    public static string? CompactBanner(WorkspaceTextContentSearchContext context, string? requestedWorkspaceId, bool json)
    {
        return CompactBanner(
            context.DisplayId,
            context.WorkspaceId,
            context.WorkspaceRoot,
            context.IndexFresh,
            context.FreshnessStatus,
            context.Revision,
            requestedWorkspaceId,
            json,
            context.WarningText,
            context.BackgroundOperation);
    }

    internal static string? CompactBanner(
        string? displayId,
        string? workspaceId,
        string workspaceRoot,
        bool? indexFresh,
        string freshnessStatus,
        long revision,
        string? requestedWorkspaceId,
        bool json,
        string? warningText = null,
        BackgroundRefreshOperationSnapshot? backgroundOperation = null)
    {
        if (json)
            return null;

        freshnessStatus = WorkspaceFreshnessView.EvidenceStatusFor(indexFresh, freshnessStatus);
        bool showFreshness = ShouldShowFreshness(indexFresh, freshnessStatus);
        bool hasWarning = !string.IsNullOrWhiteSpace(warningText);
        if (string.IsNullOrWhiteSpace(requestedWorkspaceId) && !showFreshness && !hasWarning)
            return null;

        var sb = new StringBuilder();
        sb.Append("workspace: ")
          .Append(Display(displayId, workspaceId, requestedWorkspaceId));

        bool hasBackground = backgroundOperation is { State: not BackgroundRefreshActivityState.Unknown };
        string evidenceStatus = hasBackground && freshnessStatus == WorkspaceFreshnessView.RefreshPendingStatus
            ? "unconfirmed"
            : freshnessStatus;
        if (showFreshness)
            sb.Append('\n').Append("freshness: ").Append(evidenceStatus);

        if (hasBackground || showFreshness && freshnessStatus == WorkspaceFreshnessView.RefreshPendingStatus)
            sb.Append('\n').Append("revision: ").Append(revision);

        if (hasBackground)
            sb.Append('\n').Append("background: ").Append(backgroundOperation!.State.ToString().ToLowerInvariant());

        if (hasWarning)
        {
            sb.Append('\n').Append("warning: ").Append(warningText);
        }

        return sb.ToString();
    }

    public static string PrefixCompact(string output, string? compactBanner) =>
        string.IsNullOrWhiteSpace(compactBanner) ? output : compactBanner + '\n' + output;

    public static void ApplyTelemetry(TelemetryScope? telemetry, WorkspaceReadContext context)
    {
        ApplyTelemetry(telemetry, context.WorkspaceId, context.WorkspaceRoot, context.IndexFresh);
        ApplyReadTelemetry(telemetry, context.ReadTelemetry);
    }

    public static void ApplyTelemetry(TelemetryScope? telemetry, WorkspaceArtifactContext context)
    {
        ApplyTelemetry(telemetry, context.WorkspaceId, context.WorkspaceRoot, context.IndexFresh);
    }

    public static void ApplyTelemetry(TelemetryScope? telemetry, WorkspaceSymbolSearchContext context)
    {
        ApplyTelemetry(telemetry, context.WorkspaceId, context.WorkspaceRoot, context.IndexFresh);
    }

    public static void ApplyTelemetry(TelemetryScope? telemetry, WorkspaceSymbolReadContext context)
    {
        ApplyTelemetry(telemetry, context.WorkspaceId, context.WorkspaceRoot, context.IndexFresh);
        ApplyReadTelemetry(telemetry, context.ReadTelemetry);
    }

    public static void ApplyTelemetry(TelemetryScope? telemetry, WorkspaceContentSearchContext context)
    {
        ApplyTelemetry(telemetry, context.WorkspaceId, context.WorkspaceRoot, context.IndexFresh);
    }

    public static void ApplyTelemetry(TelemetryScope? telemetry, WorkspaceRegionSearchContext context)
    {
        ApplyTelemetry(telemetry, context.WorkspaceId, context.WorkspaceRoot, context.IndexFresh);
    }

    public static void ApplyTelemetry(TelemetryScope? telemetry, WorkspaceTextContentSearchContext context)
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

    private static void ApplyReadTelemetry(TelemetryScope? telemetry, ReadPhaseTelemetry? readTelemetry)
    {
        if (telemetry is null || readTelemetry is null)
            return;

        telemetry.SetMetadata("read_resolve_ms", readTelemetry.ResolveElapsedMilliseconds);
        telemetry.SetMetadata("read_lookup_count", readTelemetry.LookupCallCount);
        telemetry.SetMetadata("read_lookup_ms", readTelemetry.LookupElapsedMilliseconds);
        telemetry.SetMetadata(
            "read_lookup_backend",
            SymbolLookupBackends.Name(readTelemetry.LookupBackend));
        telemetry.SetMetadata("read_graph_count", readTelemetry.GraphCallCount);
        telemetry.SetMetadata("read_graph_ms", readTelemetry.GraphElapsedMilliseconds);
        telemetry.SetMetadata("read_provider_cache_entries", readTelemetry.ProviderCacheEntries);
    }

    private static bool ShouldShowFreshness(bool? indexFresh, string freshnessStatus)
    {
        if (string.IsNullOrWhiteSpace(freshnessStatus))
            return false;
        if (string.Equals(freshnessStatus, "current", StringComparison.OrdinalIgnoreCase))
            return false;

        return indexFresh != true ||
               freshnessStatus.StartsWith("unconfirmed", StringComparison.OrdinalIgnoreCase) ||
               freshnessStatus.Contains("stale", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(freshnessStatus, WorkspaceFreshnessView.RefreshPendingStatus, StringComparison.OrdinalIgnoreCase);
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
