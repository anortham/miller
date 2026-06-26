using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Git;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

[McpServerToolType]
public sealed class MetricsTool
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int DefaultCloneSymbolsPerGroup = CloneGroupReader.DefaultSymbolsPerGroup;

    private readonly IWorkspaceArtifactProvider _workspaceProvider;
    private readonly IGitHistoryReader _historyReader;

    public MetricsTool(IWorkspaceArtifactProvider workspaceProvider, IGitHistoryReader historyReader)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        ArgumentNullException.ThrowIfNull(historyReader);
        _workspaceProvider = workspaceProvider;
        _historyReader = historyReader;
    }

    [McpServerTool(Name = "metrics")]
    [Description(
        "Report deterministic local metrics from the current workspace. Operations: churn, clones, complexity. " +
        "These are raw local facts, not semantic ranking, cleanup advice, or history orchestration.")]
    public string Metrics(
        [Description("churn|clones|complexity. Default complexity.")] string operation = "complexity",
        [Description("Max rows/groups. Default 50, maximum 500.")] int limit = DefaultLimit,
        [Description("Clone minimum group size. Used by operation=clones. Default 2.")] int min_count = 2,
        [Description("Max symbols listed per clone group. Used by operation=clones. Default 25, maximum 500. Count remains the full group size.")] int max_symbols_per_group = DefaultCloneSymbolsPerGroup,
        [Description("Complexity minimum severity: low|moderate|high. Used by operation=complexity. Default moderate.")] string min_severity = "moderate",
        [Description("Include test symbols/paths in complexity results. Default true.")] bool include_tests = true,
        [Description("Git commit range for operation=churn. Default HEAD~20..HEAD.")] string range = "HEAD~20..HEAD",
        [Description("Include commit ids in operation=churn JSON rows. Default false.")] bool include_commits = false,
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")] string? workspace_id = null,
        [Description("Refresh selected workspace before reading. Defaults true when workspace_id is supplied.")] bool? ensure_fresh = null,
        [Description("Output format: compact|json. Default compact.")] string format = "compact")
    {
        var telemetry = TelemetryContext.Current;
        try
        {
            bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            bool refresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            WorkspaceArtifactContext context = _workspaceProvider.ResolveArtifact(workspace_id, refresh);

            MetricsToolResult result = Run(
                context.IndexDbPath,
                operation,
                limit,
                json,
                min_count,
                max_symbols_per_group,
                min_severity,
                include_tests,
                context.WorkspaceRoot,
                range,
                include_commits,
                _historyReader);

            if (telemetry is not null)
            {
                ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
                telemetry.Op = NormalizeOperation(operation);
                telemetry.SetTarget(telemetry.Op);
                telemetry.ResultCount = result.ResultCount;
                telemetry.Outcome = result.ResultCount == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
                telemetry.SetMetadata("limit_bucket", LimitBucket(limit));
                telemetry.SetMetadata("include_tests", include_tests);
                if (result.ResultCount == 0)
                    telemetry.SetEmptyReason("no_metrics");
            }

            string? banner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            return ReadToolWorkspaceRouting.PrefixCompact(result.Output, banner);
        }
        catch (Exception ex)
        {
            if (telemetry is not null)
            {
                telemetry.Outcome = TelemetryOutcome.Error;
                telemetry.SetError(ex);
            }
            return $"metrics failed: {ex.Message}";
        }
    }

    internal static MetricsToolResult Run(
        string dbPath,
        string? operation,
        int limit,
        bool json,
        int minCount,
        int maxSymbolsPerGroup,
        string? minSeverity,
        bool includeTests,
        string? workspaceRoot = null,
        string? range = null,
        bool includeCommits = false,
        IGitHistoryReader? historyReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
        string op = NormalizeOperation(operation);
        return op switch
        {
            "clones" => RunClones(dbPath, boundedLimit, json, minCount, maxSymbolsPerGroup),
            "complexity" => RunComplexity(dbPath, boundedLimit, json, minSeverity, includeTests),
            "churn" => RunChurn(
                dbPath,
                workspaceRoot,
                string.IsNullOrWhiteSpace(range) ? "HEAD~20..HEAD" : range,
                boundedLimit,
                json,
                includeCommits,
                historyReader),
            _ => throw new InvalidOperationException("metrics operation must be churn, clones, or complexity."),
        };
    }

    private static MetricsToolResult RunClones(
        string dbPath,
        int limit,
        bool json,
        int minCount,
        int maxSymbolsPerGroup)
    {
        int boundedSymbolLimit = Math.Clamp(maxSymbolsPerGroup, 1, CloneGroupReader.MaxSymbolsPerGroup);
        IReadOnlyList<CloneGroup> groups = CloneGroupReader.Read(dbPath, limit, minCount, boundedSymbolLimit);
        return new MetricsToolResult(
            json ? RenderClonesJson(groups, boundedSymbolLimit) : RenderClonesCompact(groups, boundedSymbolLimit),
            groups.Count);
    }

    private static MetricsToolResult RunComplexity(
        string dbPath,
        int limit,
        bool json,
        string? minSeverity,
        bool includeTests)
    {
        if (!ComplexityRankingReader.TryParseSeverity(minSeverity, out ComplexitySeverity severity))
            throw new InvalidOperationException("metrics complexity min_severity must be low, moderate, or high.");

        IReadOnlyList<ComplexityHotspot> hotspots = ComplexityRankingReader.Read(
            dbPath,
            limit,
            severity,
            includeTests);
        return new MetricsToolResult(
            json ? RenderComplexityJson(hotspots, severity) : RenderComplexityCompact(hotspots),
            hotspots.Count);
    }

    private static MetricsToolResult RunChurn(
        string dbPath,
        string? workspaceRoot,
        string range,
        int limit,
        bool json,
        bool includeCommits,
        IGitHistoryReader? historyReader)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new InvalidOperationException("metrics churn requires a workspace root.");
        if (historyReader is null)
            throw new InvalidOperationException("metrics churn requires a git history reader.");

        ChurnReport report = GitChurnAnalyzer.Read(
            dbPath,
            workspaceRoot,
            range,
            limit,
            includeCommits,
            historyReader);
        return new MetricsToolResult(
            json ? RenderChurnJson(report) : RenderChurnCompact(report),
            report.Rows.Count);
    }

    private static string RenderClonesCompact(IReadOnlyList<CloneGroup> groups, int symbolLimit)
    {
        if (groups.Count == 0)
            return "No clone groups.";

        var sb = new StringBuilder();
        sb.AppendLine("# clone groups");
        foreach (CloneGroup group in groups)
        {
            sb.Append(group.BodyHash).Append("  count=").Append(group.Count).AppendLine();
            foreach (CloneSymbol symbol in group.Symbols)
            {
                sb.Append("  ")
                  .Append(symbol.Path).Append(':').Append(symbol.Line)
                  .Append(' ').Append(symbol.Name)
                  .Append(' ').Append(symbol.Kind);
                if (symbol.IsTest)
                    sb.Append(" test");
                sb.AppendLine();
            }
            if (group.Symbols.Count < group.Count)
            {
                sb.Append("  ... ")
                  .Append(group.Count - group.Symbols.Count)
                  .Append(" more symbols hidden (use max_symbols_per_group / --max-symbols-per-group)")
                  .AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderComplexityCompact(IReadOnlyList<ComplexityHotspot> hotspots)
    {
        if (hotspots.Count == 0)
            return "No complexity hotspots.";

        var sb = new StringBuilder();
        sb.AppendLine("# complexity hotspots");
        sb.AppendLine("severity\tdecisions\tnesting\tlines\tpath\tsymbol");
        foreach (ComplexityHotspot hotspot in hotspots)
        {
            sb.Append(ComplexityRankingReader.SeverityName(hotspot.Severity)).Append('\t')
              .Append(hotspot.DecisionCount).Append('\t')
              .Append(hotspot.MaxNestingDepth).Append('\t')
              .Append(hotspot.CoveredLines).Append('\t')
              .Append(hotspot.Path).Append(':').Append(hotspot.StartLine).Append('\t')
              .Append(hotspot.SymbolName ?? hotspot.Scope);
            if (hotspot.IsTest)
                sb.Append("\ttest");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderChurnCompact(ChurnReport report)
    {
        if (report.Rows.Count == 0)
            return "No churn rows.";

        var sb = new StringBuilder();
        sb.Append("# churn ").AppendLine(report.Range);
        sb.AppendLine("commits\tlines\tbasis\tpath\tsymbol");
        foreach (ChurnRow row in report.Rows)
        {
            sb.Append(row.CommitCount).Append('\t')
              .Append(row.ChangedLines).Append('\t')
              .Append(row.MappingBasis).Append('\t')
              .Append(row.Path);
            if (row.Line is { } line)
                sb.Append(':').Append(line);
            sb.Append('\t')
              .Append(row.SymbolName ?? "");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderClonesJson(IReadOnlyList<CloneGroup> groups, int symbolLimit)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("operation", "clones");
            writer.WriteStartArray("groups");
            foreach (CloneGroup group in groups)
            {
                writer.WriteStartObject();
                writer.WriteString("body_hash", group.BodyHash);
                writer.WriteNumber("count", group.Count);
                writer.WriteNumber("symbol_limit", symbolLimit);
                writer.WriteBoolean("symbols_truncated", group.Symbols.Count < group.Count);
                writer.WriteStartArray("symbols");
                foreach (CloneSymbol symbol in group.Symbols)
                {
                    writer.WriteStartObject();
                    writer.WriteString("symbol_id", symbol.SymbolId);
                    writer.WriteString("name", symbol.Name);
                    writer.WriteString("kind", symbol.Kind);
                    writer.WriteString("language", symbol.Language);
                    writer.WriteString("path", symbol.Path);
                    writer.WriteNumber("line", symbol.Line);
                    writer.WriteBoolean("is_test", symbol.IsTest);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderComplexityJson(
        IReadOnlyList<ComplexityHotspot> hotspots,
        ComplexitySeverity minSeverity)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("operation", "complexity");
            writer.WriteString("min_severity", ComplexityRankingReader.SeverityName(minSeverity));
            writer.WriteStartObject("thresholds");
            writer.WriteNumber("moderate_decision_count", ComplexityRankingReader.ModerateDecisionThreshold);
            writer.WriteNumber("moderate_max_nesting_depth", ComplexityRankingReader.ModerateNestingThreshold);
            writer.WriteNumber("high_decision_count", ComplexityRankingReader.HighDecisionThreshold);
            writer.WriteNumber("high_max_nesting_depth", ComplexityRankingReader.HighNestingThreshold);
            writer.WriteEndObject();
            writer.WriteStartArray("hotspots");
            foreach (ComplexityHotspot hotspot in hotspots)
            {
                writer.WriteStartObject();
                writer.WriteString("severity", ComplexityRankingReader.SeverityName(hotspot.Severity));
                writer.WriteString("complexity_metric_id", hotspot.ComplexityMetricId);
                writer.WriteString("path", hotspot.Path);
                writer.WriteString("language", hotspot.Language);
                writer.WriteString("scope", hotspot.Scope);
                if (hotspot.SymbolId is null) writer.WriteNull("symbol_id"); else writer.WriteString("symbol_id", hotspot.SymbolId);
                if (hotspot.SymbolName is null) writer.WriteNull("symbol_name"); else writer.WriteString("symbol_name", hotspot.SymbolName);
                if (hotspot.SymbolKind is null) writer.WriteNull("symbol_kind"); else writer.WriteString("symbol_kind", hotspot.SymbolKind);
                writer.WriteString("algorithm_id", hotspot.AlgorithmId);
                writer.WriteNumber("covered_lines", hotspot.CoveredLines);
                writer.WriteNumber("covered_bytes", hotspot.CoveredBytes);
                writer.WriteNumber("decision_count", hotspot.DecisionCount);
                writer.WriteNumber("loop_count", hotspot.LoopCount);
                writer.WriteNumber("max_nesting_depth", hotspot.MaxNestingDepth);
                if (hotspot.ParameterCount is null) writer.WriteNull("parameter_count"); else writer.WriteNumber("parameter_count", hotspot.ParameterCount.Value);
                writer.WriteNumber("start_line", hotspot.StartLine);
                writer.WriteNumber("end_line", hotspot.EndLine);
                writer.WriteNumber("start_byte", hotspot.StartByte);
                writer.WriteNumber("end_byte", hotspot.EndByte);
                writer.WriteBoolean("is_test", hotspot.IsTest);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderChurnJson(ChurnReport report)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("operation", "churn");
            writer.WriteString("range", report.Range);
            writer.WriteString("mapping_note", "changed hunks are mapped to the current index");
            writer.WriteStartArray("rows");
            foreach (ChurnRow row in report.Rows)
            {
                writer.WriteStartObject();
                writer.WriteString("mapping_basis", row.MappingBasis);
                if (row.SymbolId is null) writer.WriteNull("symbol_id"); else writer.WriteString("symbol_id", row.SymbolId);
                if (row.SymbolName is null) writer.WriteNull("symbol_name"); else writer.WriteString("symbol_name", row.SymbolName);
                if (row.SymbolKind is null) writer.WriteNull("symbol_kind"); else writer.WriteString("symbol_kind", row.SymbolKind);
                writer.WriteString("path", row.Path);
                if (row.Line is null) writer.WriteNull("line"); else writer.WriteNumber("line", row.Line.Value);
                writer.WriteNumber("commit_count", row.CommitCount);
                writer.WriteNumber("changed_lines", row.ChangedLines);
                writer.WriteString("last_commit_at_utc", row.LastCommitAtUtc);
                writer.WriteStartArray("commits");
                foreach (string commit in row.Commits)
                    writer.WriteStringValue(commit);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string NormalizeOperation(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            return "complexity";
        return operation.Trim().ToLowerInvariant() switch
        {
            "clone" => "clones",
            "duplicate" or "duplicates" => "clones",
            "hotspots" => "complexity",
            var normalized => normalized,
        };
    }

    private static string LimitBucket(int limit) => limit switch
    {
        <= 0 => "0",
        <= 5 => "1-5",
        <= 10 => "6-10",
        <= 25 => "11-25",
        <= 50 => "26-50",
        _ => "51+",
    };

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

internal readonly record struct MetricsToolResult(string Output, int ResultCount);
