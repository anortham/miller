using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog.Context;

namespace Miller.Server.Telemetry;

/// <summary>
/// The ONE central tool-call interceptor (M2 decision-1). Registered via
/// <c>builder.WithRequestFilters(f => f.AddCallToolFilter(TelemetryCallToolFilter.Create()))</c>, it wraps
/// every explicitly registered tool, so telemetry is captured once, centrally, with no per-tool boilerplate. For
/// each call it opens a <see cref="TelemetryLedger.Measure"/> scope (published as the ambient
/// <see cref="TelemetryContext.Current"/> the tool body may enrich), runs the inner handler, then stamps the
/// outcome (IsError → error; else, only when the tool did NOT classify it, empty if zero results else ok),
/// bytes_returned, and est_tokens before the scope disposes and persists the row. index_fresh is left null
/// (unknown) for M2 — there is no per-call mtime comparison until M3 (spec L238-239).
/// <para>
/// M7 decision-4 (soft budgets): after the inner handler the filter also evaluates the call against
/// <see cref="SoftBudgets"/> (resolved optionally from DI) and logs a Serilog/ILogger WARN per breach —
/// WARN-ONLY: it never blocks, never turns the call into an error, never throws. The budget check times the
/// call with its OWN local <see cref="Stopwatch.GetTimestamp"/> taken at filter entry (independent of the
/// <see cref="TelemetryScope"/>'s own dispose-timing, which remains the ledger's record of truth); est_tokens
/// is read from the scope. If <see cref="SoftBudgets"/> or a logger is absent (a bare test harness) the check
/// is skipped silently.
/// </para>
/// </summary>
public static class TelemetryCallToolFilter
{
    /// <summary>The ILogger category for soft-budget WARN lines.</summary>
    private const string BudgetLoggerCategory = "Miller.Server.Telemetry.SoftBudgets";

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

            // Correlation id (M8 decision-2): ONE id per tools/call, generated here at the single choke point.
            // It is BOTH the telemetry row id (passed to the scope, which hands it to Record) AND the Serilog
            // 'cid' log property pushed for the duration of the inner handler — so every log line emitted on this
            // call's async flow (the tool body and the readers it calls) carries the same id as the ledger row.
            string cid = Guid.CreateVersion7().ToString();
            using var scope = ledger.Measure(tool, op: null, correlationId: cid);
            using var cidContext = LogContext.PushProperty("cid", cid);

            // Soft budgets (M7 decision-4): time the call with the filter's OWN timestamp, independent of the
            // scope's dispose-timing. The budget WARN is diagnostic; the ledger row is the record of truth.
            // Both deps are resolved optionally — a bare harness without them simply skips the check.
            long budgetStart = Stopwatch.GetTimestamp();
            var budgets = request.Services?.GetService<SoftBudgets>();
            var loggerFactory = request.Services?.GetService<ILoggerFactory>();

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

                EvaluateBudgets(budgets, loggerFactory, tool, budgetStart, scope.EstTokens ?? 0);
                return result;
            }
            catch (ArgumentException ex) when (TryGetMissingRequiredParameter(ex, out string parameterName))
            {
                // The Microsoft.Extensions.AI argument marshaller throws this exact shape when a tools/call
                // arrives without a required parameter ("The arguments dictionary is missing a value for the
                // required parameter 'X'", ParamName = "arguments"). Rethrowing it surfaces an opaque
                // protocol-level error the agent retry-loops on (seen live in a Windows dogfood session), so
                // THIS one shape is shaped here — at the single choke point covering every tool — into a
                // friendly IsError tool result naming the missing parameter. Telemetry still records an
                // error row; everything else about the call is unchanged.
                scope.Outcome = TelemetryOutcome.Error;
                scope.ErrorKind = ex.GetType().Name;

                var result = new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = MissingParameterHint(tool, parameterName) }],
                };

                long bytes = MeasureBytes(result);
                scope.BytesReturned = bytes;
                scope.EstTokens = TokenEstimator.CountFromBytes(bytes);

                EvaluateBudgets(budgets, loggerFactory, tool, budgetStart, scope.EstTokens ?? 0);
                return result;
            }
            catch (Exception ex)
            {
                // A tool threw past its own catch. Record an error row, then rethrow so the SDK does its
                // standard redaction to the client. Still run the budget check on the throw path: a slow call
                // that ALSO threw is exactly the kind of pathology a latency WARN should surface. est_tokens is
                // 0 here (no result payload was produced), so only the latency dimension can breach.
                scope.Outcome = TelemetryOutcome.Error;
                scope.ErrorKind = ex.GetType().Name;
                EvaluateBudgets(budgets, loggerFactory, tool, budgetStart, scope.EstTokens ?? 0);
                throw;
            }
        };
    }

    /// <summary>
    /// The marker the Microsoft.Extensions.AI marshaller puts in its missing-required-parameter
    /// <see cref="ArgumentException"/>; the missing parameter's name follows in single quotes.
    /// </summary>
    private const string MissingParameterMarker = "missing a value for the required parameter '";

    /// <summary>
    /// One-line example calls for the tools whose required parameter has no sensible default. Unmapped tools
    /// fall back to a generic "missing required parameter" hint, so a future tool is covered automatically.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ToolUsageExamples =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["inspect"] = "inspect(target=\"WorkspaceTool.ResolveTarget\")",
            ["search"] = "search(query=\"TelemetryLedger\")",
            ["context"] = "context(query=\"how does workspace refresh converge\")",
            ["trace"] = "trace(target=\"SearchTool.Run\")",
            ["edit"] = "edit(operation=\"replace_text\", target=\"src/File.cs\", old_text=\"...\", new_text=\"...\")",
        };

    /// <summary>
    /// True when <paramref name="ex"/> is the argument marshaller's missing-required-parameter shape:
    /// an <see cref="ArgumentException"/> with <c>ParamName == "arguments"</c> whose message carries
    /// "missing a value for the required parameter '&lt;name&gt;'". Extracts the parameter name on match.
    /// </summary>
    private static bool TryGetMissingRequiredParameter(ArgumentException ex, out string parameterName)
    {
        parameterName = string.Empty;
        if (!string.Equals(ex.ParamName, "arguments", StringComparison.Ordinal))
            return false;

        string message = ex.Message;
        int start = message.IndexOf(MissingParameterMarker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;
        start += MissingParameterMarker.Length;
        int end = message.IndexOf('\'', start);
        if (end <= start)
            return false;

        parameterName = message[start..end];
        return true;
    }

    /// <summary>Build the friendly one-line usage hint for a missing required parameter.</summary>
    private static string MissingParameterHint(string tool, string parameterName) =>
        ToolUsageExamples.TryGetValue(tool, out string? example)
            ? $"{tool} requires '{parameterName}'. Example: {example}"
            : $"{tool}: missing required parameter '{parameterName}'.";

    /// <summary>
    /// Evaluate the just-completed call against its soft budget and log a WARN per breach. WARN-ONLY and
    /// fully best-effort: any failure (a logging fault, etc.) is swallowed so the budget check can NEVER affect
    /// the call. Skips silently when budgets or a logger are not registered.
    /// </summary>
    private static void EvaluateBudgets(
        SoftBudgets? budgets, ILoggerFactory? loggerFactory, string tool, long budgetStart, long estTokens)
    {
        if (budgets is null || loggerFactory is null)
            return;

        try
        {
            long durationMs = Math.Max(0, (long)Stopwatch.GetElapsedTime(budgetStart).TotalMilliseconds);
            var breaches = budgets.Evaluate(tool, durationMs, estTokens);
            if (breaches.Count == 0)
                return;

            var logger = loggerFactory.CreateLogger(BudgetLoggerCategory);
            foreach (var breach in breaches)
            {
                string dimension = breach.Dimension switch
                {
                    BudgetDimension.Latency => "latency",
                    BudgetDimension.EstTokens => "est_tokens",
                    _ => breach.Dimension.ToString(),
                };
                string units = breach.Dimension == BudgetDimension.Latency ? "ms" : " tokens";
                // e.g. "tool 'context' exceeded latency budget: 820ms > 500ms".
                logger.LogWarning(
                    "tool '{Tool}' exceeded {Dimension} budget: {Actual}{Units} > {Limit}{Units}",
                    tool, dimension,
                    breach.Actual.ToString(CultureInfo.InvariantCulture), units,
                    breach.Limit.ToString(CultureInfo.InvariantCulture), units);
            }
        }
        catch (Exception)
        {
            // The budget WARN is purely diagnostic; a fault here must never surface to the agent or the call.
        }
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
