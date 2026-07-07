using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Miller.Server;
using Miller.Server.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Miller.Server.Telemetry;

/// <summary>
/// Ensures the primary workspace is bound via MCP roots before any tool handler runs.
/// </summary>
public static class WorkspaceBindingCallToolFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        return next => async (request, cancellationToken) =>
        {
            var binding = request.Services?.GetService<IWorkspaceBindingService>();
            var server = request.Services?.GetService<McpServer>();
            if (binding is not null && server is not null)
            {
                await binding.EnsurePrimaryBoundAsync(server, cancellationToken).ConfigureAwait(false);

                var snapshot = binding.Snapshot;
                if (IsWorkspaceTool(request) && snapshot.Phase != BootstrapPhase.Bound)
                    return TextResult(RenderWorkspaceSnapshot(snapshot), isError: false);

                if (snapshot.Phase == BootstrapPhase.Bound)
                    return await next(request, cancellationToken).ConfigureAwait(false);

                if (snapshot.LastFailureMessage is { Length: > 0 } lastFailure)
                    return TextResult(FailedText(lastFailure), isError: true);

                if (snapshot.Phase == BootstrapPhase.Failed)
                    return TextResult(FailedText(snapshot.FailureMessage ?? "unknown error"), isError: true);

                if (snapshot.Phase == BootstrapPhase.Running)
                {
                    TimeSpan grace = BootstrapGraceTimeout();
                    if (grace > TimeSpan.Zero)
                    {
                        bool completed = await WaitForRunWithinGraceAsync(
                            binding, snapshot.RunGeneration, grace, cancellationToken).ConfigureAwait(false);
                        if (completed)
                        {
                            snapshot = binding.Snapshot;
                            if (snapshot.Phase == BootstrapPhase.Bound)
                                return await next(request, cancellationToken).ConfigureAwait(false);
                            if (snapshot.LastFailureMessage is { Length: > 0 } completedFailure)
                                return TextResult(FailedText(completedFailure), isError: true);
                            if (snapshot.Phase == BootstrapPhase.Failed)
                                return TextResult(
                                    FailedText(snapshot.FailureMessage ?? "unknown error"), isError: true);
                        }
                    }

                    return TextResult(NotReadyText(snapshot), isError: true);
                }
            }

            return await next(request, cancellationToken).ConfigureAwait(false);
        };
    }

    private static bool IsWorkspaceTool(RequestContext<CallToolRequestParams> request) =>
        string.Equals(request.Params?.Name, "workspace", StringComparison.Ordinal);

    private static async Task<bool> WaitForRunWithinGraceAsync(
        IWorkspaceBindingService binding,
        int runGeneration,
        TimeSpan grace,
        CancellationToken cancellationToken)
    {
        var waitTask = binding.WaitForRunAsync(runGeneration, cancellationToken);
        var timeoutTask = Task.Delay(grace, cancellationToken);
        var completed = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
        if (ReferenceEquals(completed, timeoutTask))
        {
            await timeoutTask.ConfigureAwait(false);
            return false;
        }

        await waitTask.ConfigureAwait(false);
        return true;
    }

    private static TimeSpan BootstrapGraceTimeout()
    {
        const double DefaultSeconds = 5;
        string? raw = Environment.GetEnvironmentVariable("MILLER_BOOTSTRAP_GRACE_SECONDS");
        if (string.IsNullOrWhiteSpace(raw))
            return TimeSpan.FromSeconds(DefaultSeconds);

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) &&
            seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(DefaultSeconds);
    }

    private static CallToolResult TextResult(string text, bool isError) =>
        new()
        {
            IsError = isError,
            Content = [new TextContentBlock { Text = text }],
        };

    private static string NotReadyText(BootstrapSnapshot snapshot) =>
        $"Miller is indexing this workspace for the first time: {snapshot.CanonicalRoot ?? "(unknown)"} " +
        $"(started {ElapsedSeconds(snapshot.StartedAtUtc)}s ago). Tool calls will work once indexing completes — " +
        "retry shortly, or run 'workspace status' for progress.";

    private static string FailedText(string message) =>
        $"bootstrap failed: {message}; retry started — call again shortly.";

    private static string RenderWorkspaceSnapshot(BootstrapSnapshot snapshot) =>
        snapshot.Phase switch
        {
            BootstrapPhase.Running =>
                $"bootstrap: running {snapshot.CanonicalRoot ?? "(unknown)"}, " +
                $"started {ElapsedSeconds(snapshot.StartedAtUtc)}s ago",
            BootstrapPhase.Failed =>
                $"bootstrap: failed — {snapshot.FailureMessage ?? snapshot.LastFailureMessage ?? "unknown error"}",
            BootstrapPhase.Idle => "bootstrap: idle",
            _ => "bootstrap: unavailable",
        };

    private static long ElapsedSeconds(DateTimeOffset? startedAtUtc)
    {
        if (startedAtUtc is null)
            return 0;

        var elapsed = DateTimeOffset.UtcNow - startedAtUtc.Value;
        return Math.Max(0, (long)elapsed.TotalSeconds);
    }
}
