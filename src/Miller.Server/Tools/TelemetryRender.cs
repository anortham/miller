using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Server.Telemetry;

namespace Miller.Server.Tools;

/// <summary>
/// The PURE renderer for a <see cref="TelemetrySummary"/> (M7 decision-5/6) — the tool-breakdown the
/// <c>workspace status</c> surface shows (julie's tool-breakdown screen). Deterministic, no I/O: given the
/// already-aggregated summary it produces a compact text table or a JSON document, keeping the SQL/aggregation
/// (<see cref="TelemetryLedger.Summarize"/>) cleanly separated from formatting. Mirrors the JSON-writer style of
/// the other tools (<c>ArrayBufferWriter</c> + <c>Utf8JsonWriter</c> with relaxed escaping).
/// </summary>
public static class TelemetryRender
{
    /// <summary>
    /// Render the summary as a compact text table: a header line (total calls, window, dropped writes) then one
    /// row per tool. An empty summary renders a single "no telemetry" line.
    /// </summary>
    public static string Compact(TelemetrySummary summary)
    {
        if (summary.Tools.Count == 0)
            return "no telemetry recorded yet";

        var sb = new StringBuilder();
        sb.Append("# telemetry — ").Append(summary.TotalCalls).Append(" calls");
        if (summary.WindowStartTs is not null && summary.WindowEndTs is not null)
            sb.Append("  [").Append(summary.WindowStartTs).Append(" .. ").Append(summary.WindowEndTs).Append(']');
        sb.Append("  dropped=").Append(summary.DroppedWrites).Append('\n');

        // Columns: tool / calls / avg ms / p95 ms / max ms / errors / est tokens.
        sb.Append("tool             calls   avg_ms   p95_ms   max_ms   errors   est_tokens\n");
        foreach (var t in summary.Tools)
        {
            sb.Append(Pad(t.Tool, 16)).Append(' ')
              .Append(PadLeft(t.Calls.ToString(CultureInfo.InvariantCulture), 5)).Append("   ")
              .Append(PadLeft(t.AvgMs.ToString("0.#", CultureInfo.InvariantCulture), 6)).Append("   ")
              .Append(PadLeft(t.P95Ms.ToString(CultureInfo.InvariantCulture), 6)).Append("   ")
              .Append(PadLeft(t.MaxMs.ToString(CultureInfo.InvariantCulture), 6)).Append("   ")
              .Append(PadLeft(t.ErrorCount.ToString(CultureInfo.InvariantCulture), 6)).Append("   ")
              .Append(PadLeft(t.SumEstTokens.ToString(CultureInfo.InvariantCulture), 10))
              .Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Render the summary as a JSON object: <c>{ total_calls, window_start, window_end, window_days,
    /// dropped_writes, tools:[{ tool, calls, avg_ms, p95_ms, max_ms, error_count, est_tokens }] }</c>.
    /// <c>window_start</c> / <c>window_end</c> are JSON null when the ledger is empty; <c>window_days</c> is
    /// JSON null when the summary covers every retained row rather than a rolling window.
    /// </summary>
    public static string Json(TelemetrySummary summary)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("total_calls", summary.TotalCalls);
            if (summary.WindowStartTs is null) w.WriteNull("window_start");
            else w.WriteString("window_start", summary.WindowStartTs);
            if (summary.WindowEndTs is null) w.WriteNull("window_end");
            else w.WriteString("window_end", summary.WindowEndTs);
            if (summary.WindowDays is { } windowDays) w.WriteNumber("window_days", windowDays);
            else w.WriteNull("window_days");
            w.WriteNumber("dropped_writes", summary.DroppedWrites);

            w.WritePropertyName("tools");
            w.WriteStartArray();
            foreach (var t in summary.Tools)
            {
                w.WriteStartObject();
                w.WriteString("tool", t.Tool);
                w.WriteNumber("calls", t.Calls);
                w.WriteNumber("avg_ms", t.AvgMs);
                w.WriteNumber("p95_ms", t.P95Ms);
                w.WriteNumber("max_ms", t.MaxMs);
                w.WriteNumber("error_count", t.ErrorCount);
                w.WriteNumber("est_tokens", t.SumEstTokens);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // Right-pad (truncate over-long values with an ellipsis so columns stay aligned).
    private static string Pad(string value, int width) =>
        value.Length >= width
            ? value[..(width - 1)] + "…"
            : value + new string(' ', width - value.Length);

    private static string PadLeft(string value, int width) =>
        value.Length >= width ? value : new string(' ', width - value.Length) + value;
}
