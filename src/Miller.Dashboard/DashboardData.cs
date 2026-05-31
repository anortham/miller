using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Workspaces;

namespace Miller.Dashboard;

public sealed record DashboardWorkspaceRow(
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("display_id")] string DisplayId,
    [property: JsonPropertyName("canonical_root")] string CanonicalRoot,
    [property: JsonPropertyName("index_db_path")] string IndexDbPath,
    [property: JsonPropertyName("last_seen_at")] string LastSeenAt,
    [property: JsonPropertyName("last_scan_at")] string? LastScanAt,
    [property: JsonPropertyName("last_revision")] long? LastRevision,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("last_error")] string? LastError);

public sealed record DashboardTelemetrySummary(
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId,
    [property: JsonPropertyName("tools")] IReadOnlyList<DashboardToolStat> Tools,
    [property: JsonPropertyName("total_calls")] long TotalCalls,
    [property: JsonPropertyName("window_start_ts")] string? WindowStartTs,
    [property: JsonPropertyName("window_end_ts")] string? WindowEndTs);

public sealed record DashboardToolStat(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("calls")] long Calls,
    [property: JsonPropertyName("avg_ms")] double AvgMs,
    [property: JsonPropertyName("p95_ms")] long P95Ms,
    [property: JsonPropertyName("max_ms")] long MaxMs,
    [property: JsonPropertyName("error_count")] long ErrorCount,
    [property: JsonPropertyName("sum_est_tokens")] long SumEstTokens);

public static class DashboardData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static IReadOnlyList<DashboardWorkspaceRow> ReadWorkspaces(string registryDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryDbPath);
        if (!File.Exists(registryDbPath))
            return Array.Empty<DashboardWorkspaceRow>();

        using var connection = OpenReadOnly(registryDbPath);
        if (!TableExists(connection, "workspaces"))
            return Array.Empty<DashboardWorkspaceRow>();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT workspace_id, display_id, canonical_root, index_db_path, last_seen_at, last_scan_at,
                   last_revision, state, last_error
            FROM workspaces
            ORDER BY CASE WHEN state IN ('current','ready','loaded_existing') THEN 0 ELSE 1 END,
                     display_id COLLATE NOCASE,
                     display_id,
                     workspace_id;
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<DashboardWorkspaceRow>();
        while (reader.Read())
        {
            rows.Add(new DashboardWorkspaceRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return rows;
    }

    public static DashboardTelemetrySummary ReadTelemetrySummary(string telemetryDbPath, string? workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(telemetryDbPath);
        if (!File.Exists(telemetryDbPath))
            return new DashboardTelemetrySummary(workspaceId, Array.Empty<DashboardToolStat>(), 0, null, null);

        using var connection = OpenReadOnly(telemetryDbPath);
        if (!TableExists(connection, "tool_telemetry"))
            return new DashboardTelemetrySummary(workspaceId, Array.Empty<DashboardToolStat>(), 0, null, null);

        var tools = ReadToolStats(connection, workspaceId);
        (long totalCalls, string? windowStart, string? windowEnd) = ReadTotals(connection, workspaceId);
        return new DashboardTelemetrySummary(workspaceId, tools, totalCalls, windowStart, windowEnd);
    }

    public static string RenderWorkspacesJson(string registryDbPath) =>
        JsonSerializer.Serialize(ReadWorkspaces(registryDbPath), JsonOptions);

    public static string RenderTelemetryJson(string telemetryDbPath, string? workspaceId) =>
        JsonSerializer.Serialize(ReadTelemetrySummary(telemetryDbPath, workspaceId), JsonOptions);

    public static string RenderIndexHtml(string registryDbPath)
    {
        IReadOnlyList<DashboardWorkspaceRow> rows = ReadWorkspaces(registryDbPath);
        var sb = new StringBuilder("""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Miller Dashboard</title>
              <style>
                body { font: 14px system-ui, sans-serif; margin: 24px; color: #1f2933; }
                table { border-collapse: collapse; width: 100%; }
                th, td { border-bottom: 1px solid #d8dee4; padding: 8px; text-align: left; vertical-align: top; }
                th { font-size: 12px; text-transform: uppercase; color: #52616b; }
                code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
              </style>
            </head>
            <body>
              <h1>Miller Dashboard</h1>
              <table>
                <thead><tr><th>Workspace</th><th>State</th><th>Revision</th><th>Root</th><th>Index</th><th>Error</th></tr></thead>
                <tbody>
            """);

        foreach (DashboardWorkspaceRow row in rows)
        {
            sb.Append("<tr><td><code>").Append(Html(row.DisplayId)).Append("</code><br><small>")
              .Append(Html(row.WorkspaceId)).Append("</small></td><td>").Append(Html(row.State))
              .Append("</td><td>").Append(row.LastRevision?.ToString(CultureInfo.InvariantCulture) ?? "")
              .Append("</td><td><code>").Append(Html(row.CanonicalRoot)).Append("</code></td><td><code>")
              .Append(Html(row.IndexDbPath)).Append("</code></td><td>")
              .Append(Html(row.LastError ?? "")).Append("</td></tr>");
        }

        sb.Append("""
                </tbody>
              </table>
            </body>
            </html>
            """);
        return sb.ToString();
    }

    public static WorkspaceRefreshResult RefreshWorkspace(string registryDbPath, string toolsRoot, string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        using WorkspaceRegistry registry = WorkspaceRegistry.Open(registryDbPath);
        var runner = JulieExtractRunner.Locate(toolsRoot);
        var refresh = new CrossWorkspaceRefreshService(registry, runner);
        return refresh.Refresh(workspaceId);
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=3000;";
            pragma.ExecuteNonQuery();
        }
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static IReadOnlyList<DashboardToolStat> ReadToolStats(SqliteConnection connection, string? workspaceId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT tool,
                   COUNT(*) AS calls,
                   AVG(duration_ms) AS avg_ms,
                   MAX(duration_ms) AS max_ms,
                   SUM(CASE WHEN outcome = 'error' THEN 1 ELSE 0 END) AS errors,
                   COALESCE(SUM(est_tokens), 0) AS sum_tokens
            FROM tool_telemetry
            WHERE workspace_id IS $ws
            GROUP BY tool
            ORDER BY tool;
            """;
        cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var stats = new List<DashboardToolStat>();
        while (reader.Read())
        {
            string tool = reader.GetString(0);
            long calls = reader.GetInt64(1);
            stats.Add(new DashboardToolStat(
                tool,
                calls,
                reader.GetDouble(2),
                ComputeP95(connection, workspaceId, tool, calls),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return stats;
    }

    private static (long TotalCalls, string? WindowStart, string? WindowEnd) ReadTotals(
        SqliteConnection connection,
        string? workspaceId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MIN(ts), MAX(ts) FROM tool_telemetry WHERE workspace_id IS $ws;";
        cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (0, null, null);

        return (
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static long ComputeP95(SqliteConnection connection, string? workspaceId, string tool, long count)
    {
        long offset = (long)Math.Floor((count - 1) * 0.95);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT duration_ms FROM tool_telemetry WHERE tool = $tool AND workspace_id IS $ws " +
            "ORDER BY duration_ms ASC LIMIT 1 OFFSET $offset;";
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$ws", (object?)workspaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$offset", offset);
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);
}
