using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Miller.Server.Telemetry;

/// <summary>
/// The ONE central tool-call interceptor (M2 decision-1). Registered via
/// <c>builder.WithRequestFilters(f => f.AddCallToolFilter(TelemetryCallToolFilter.Create()))</c>, it wraps
/// EVERY <c>tools/call</c> — including reflection-discovered (<c>WithToolsFromAssembly</c>) tools, verified
/// empirically — so telemetry is captured once, centrally, with no per-tool boilerplate. For each call it
/// opens a <see cref="TelemetryLedger.Measure"/> scope (published as the ambient
/// <see cref="TelemetryContext.Current"/> the tool body may enrich), runs the inner handler, then stamps the
/// outcome (IsError → error; else, only when the tool did NOT classify it, empty if zero results else ok),
/// bytes_returned, and est_tokens before the scope disposes and persists the row. index_fresh is left null
/// (unknown) for M2 — there is no per-call mtime comparison until M3 (spec L238-239).
/// </summary>
public static class TelemetryCallToolFilter
{
    /// <summary>
    /// Build the filter delegate. The <see cref="TelemetryLedger"/> is resolved per-call from the request's
    /// service provider, so the filter works regardless of registration order. If no ledger is registered
    /// (it always is in production) the call passes through untouched.
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        return next => async (request, cancellationToken) =>
        {
            var ledger = request.Services?.GetService<TelemetryLedger>();
            if (ledger is null)
                return await next(request, cancellationToken);

            string tool = request.Params?.Name ?? "(unknown)";
            using var scope = ledger.Measure(tool, op: null);

            // index_fresh (M3, decision-8): the coarse boolean "the held index is at the latest revision AND the
            // indexer queue is empty", computed by the IndexFreshProbe singleton when registered. It is resolved
            // optionally — when absent (a test harness with no freshness wiring) index_fresh stays NULL, honestly
            // recording "not measured" rather than a fabricated value. Set once up front so every outcome branch
            // (ok/empty/error/throw) carries it.
            var freshProbe = request.Services?.GetService<Hosting.IndexFreshProbe>();
            if (freshProbe is not null)
                scope.IndexFresh = freshProbe.Compute();

            try
            {
                CallToolResult result = await next(request, cancellationToken);

                bool isError = result.IsError == true;
                // The tool body may already have set ResultCount via TelemetryContext.Current; fall back to
                // the content-block count when it did not (e.g. a non-Miller discovered tool in the pin test).
                int resultCount = scope.ResultCount ?? (result.Content?.Count ?? 0);

                // An MCP error result always wins. Otherwise only the filter fills the outcome when the tool did
                // NOT classify it itself. Gating on OutcomeExplicitlySet (not ResultCount) is load-bearing: a
                // tool that catches internally sets Outcome=Error but returns a clean string (so IsError is
                // false) and leaves ResultCount null — keying on ResultCount would rewrite that Error back to Ok.
                if (isError)
                {
                    scope.Outcome = TelemetryOutcome.Error;
                }
                else if (!scope.OutcomeExplicitlySet)
                {
                    scope.Outcome = resultCount == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
                }

                long bytes = MeasureBytes(result);
                scope.BytesReturned = bytes;
                scope.EstTokens = TokenEstimator.CountFromBytes(bytes);
                return result;
            }
            catch (Exception ex)
            {
                // A tool threw past its own catch. Record an error row, then rethrow so the SDK does its
                // standard redaction to the client.
                scope.Outcome = TelemetryOutcome.Error;
                scope.ErrorKind = ex.GetType().Name;
                throw;
            }
        };
    }

    private static long MeasureBytes(CallToolResult result)
    {
        if (result.Content is null)
            return 0;
        long total = 0;
        foreach (var block in result.Content)
            if (block is TextContentBlock text)
                total += TokenEstimator.ByteLength(text.Text);
        return total;
    }
}
