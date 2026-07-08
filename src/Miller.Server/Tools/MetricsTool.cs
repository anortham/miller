using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Git;

namespace Miller.Server.Tools;

/// <summary>
/// The heavy-arm metric-history vocabulary shared across the commands that record snapshots
/// (<c>miller report</c>, <c>metrics churn|risk</c>, <c>references candidates</c>): the <c>snapshots.source</c>
/// values and the heavy-only metric names, plus the tiny <c>detail_json</c> params builder those producers stamp.
/// The cheap-arm names (<c>symbol_count</c>, <c>clone_group_count</c>, …) stay single-sourced on
/// <see cref="MetricSnapshotAggregates"/>; only names the cheap arm does NOT emit live here so producer and the
/// Task 4/6 read surfaces never drift. Design: docs/plans/2026-07-07-metric-history-design.md ("Heavy arm").
/// </summary>
internal static class MetricHistoryHeavyArm
{
    // snapshots.source values (the computing operation — one command run = one coherent snapshot).
    public const string ReportSource = "report";
    public const string ChurnSource = "churn";
    public const string RiskSource = "risk";
    public const string CandidatesSource = "candidates";

    // Heavy-only metric names (the cheap arm never emits these; the Task 4/6 read surfaces key off them).
    public const string ChurnFilesChanged = "churn_files_changed";
    public const string RiskTopScore = "risk_top_score";
    public const string RiskRows = "risk_rows";
    public const string DeadCodeCandidateCount = "dead_code_candidate_count";
    public const string DeadCodeSuppressedTotal = "dead_code_suppressed_total";

    /// <summary>The canonical params stamped in <c>detail_json</c> so a churn/risk trend point is self-describing.</summary>
    public static string RangeLimitDetail(string range, int limit)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(
            buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("range", range);
            w.WriteNumber("limit", limit);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

public static class MetricsTool
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int DefaultCloneSymbolsPerGroup = CloneGroupReader.DefaultSymbolsPerGroup;

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
            // NOTE: churn/risk carry SnapshotMetrics for the CLI heavy-arm history recorder; clones/complexity
            // leave it null (the leader converge arm already records clone_group_count from symbols.db).
            "churn" => RunChurn(
                dbPath,
                workspaceRoot,
                string.IsNullOrWhiteSpace(range) ? "HEAD~20..HEAD" : range,
                boundedLimit,
                json,
                includeCommits,
                historyReader),
            "risk" => RunRisk(
                dbPath,
                workspaceRoot,
                string.IsNullOrWhiteSpace(range) ? "HEAD~20..HEAD" : range,
                boundedLimit,
                json,
                includeTests,
                historyReader),
            _ => throw new InvalidOperationException("metrics operation must be churn, clones, complexity, or risk."),
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
            groups.Count,
            SnapshotMetrics: null);
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
            hotspots.Count,
            SnapshotMetrics: null);
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
            report.Rows.Count,
            ChurnSnapshotMetrics(report, limit));
    }

    // The heavy-arm churn snapshot: churn_files_changed = distinct changed paths among the (bounded) churn rows,
    // range+limit stamped in detail_json. Reuses the already-composed rows — no second git parse. A genuinely
    // empty churn is 0 files changed (git was available), which the recorder writes as a real value.
    private static IReadOnlyList<MetricHistoryPoint> ChurnSnapshotMetrics(ChurnReport report, int limit)
    {
        int filesChanged = report.Rows.Select(row => row.Path).Distinct(StringComparer.Ordinal).Count();
        return
        [
            new MetricHistoryPoint(
                MetricHistoryHeavyArm.ChurnFilesChanged,
                filesChanged,
                MetricHistoryHeavyArm.RangeLimitDetail(report.Range, limit)),
        ];
    }

    private static MetricsToolResult RunRisk(
        string dbPath,
        string? workspaceRoot,
        string range,
        int limit,
        bool json,
        bool includeTests,
        IGitHistoryReader? historyReader)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new InvalidOperationException("metrics risk requires a workspace root.");
        if (historyReader is null)
            throw new InvalidOperationException("metrics risk requires a git history reader.");

        RiskReport report = RiskRanking.Read(dbPath, workspaceRoot, range, limit, includeTests, historyReader);
        return new MetricsToolResult(
            json ? RenderRiskJson(report) : RenderRiskCompact(report),
            report.Rows.Count,
            RiskSnapshotMetrics(report, limit));
    }

    // The heavy-arm risk snapshot: risk_rows = number of risk rows (a real value, recorded even when 0 because git
    // was available), and risk_top_score = the highest score — ABSENT when there are no rows (a max over nothing is
    // undefined, per the absent-vs-zero rule), never a fabricated 0. range+limit stamped in detail_json.
    private static IReadOnlyList<MetricHistoryPoint> RiskSnapshotMetrics(RiskReport report, int limit)
    {
        string detail = MetricHistoryHeavyArm.RangeLimitDetail(report.Range, limit);
        var points = new List<MetricHistoryPoint>(2);
        if (report.Rows.Count > 0)
            points.Add(new MetricHistoryPoint(
                MetricHistoryHeavyArm.RiskTopScore, report.Rows.Max(row => row.Score), detail));
        points.Add(new MetricHistoryPoint(MetricHistoryHeavyArm.RiskRows, report.Rows.Count, detail));
        return points;
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

    private static string RenderRiskCompact(RiskReport report)
    {
        if (report.Rows.Count == 0)
            return "No risk rows.";

        var sb = new StringBuilder();
        sb.Append("# risk ").AppendLine(report.Range);
        sb.Append("score = ").AppendLine(RiskRanking.ScoreFormula);
        sb.AppendLine("score\tcommits\tlines\tseverity\tbasis\tpath\tsymbol");
        foreach (RiskRow row in report.Rows)
        {
            sb.Append(row.Score).Append('\t')
              .Append(row.CommitCount).Append('\t')
              .Append(row.ChangedLines).Append('\t')
              .Append(ComplexityRankingReader.SeverityName(row.Severity)).Append('\t')
              .Append(row.Basis).Append('\t')
              .Append(row.Path);
            if (row.Line is { } line)
                sb.Append(':').Append(line);
            sb.Append('\t')
              .Append(row.SymbolName ?? "");
            if (row.IsTest)
                sb.Append("\ttest");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderRiskJson(RiskReport report)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("operation", "risk");
            writer.WriteString("range", report.Range);
            writer.WriteString("score_formula", RiskRanking.ScoreFormula);
            writer.WriteString(
                "mapping_note",
                "risk rows are the intersection of churn and complexity evidence mapped to the current index; churn-only and complexity-only rows are omitted");
            writer.WriteStartArray("rows");
            foreach (RiskRow row in report.Rows)
            {
                writer.WriteStartObject();
                writer.WriteString("basis", row.Basis);
                if (row.SymbolId is null) writer.WriteNull("symbol_id"); else writer.WriteString("symbol_id", row.SymbolId);
                if (row.SymbolName is null) writer.WriteNull("symbol_name"); else writer.WriteString("symbol_name", row.SymbolName);
                if (row.SymbolKind is null) writer.WriteNull("symbol_kind"); else writer.WriteString("symbol_kind", row.SymbolKind);
                writer.WriteString("path", row.Path);
                if (row.Line is null) writer.WriteNull("line"); else writer.WriteNumber("line", row.Line.Value);
                writer.WriteNumber("commit_count", row.CommitCount);
                writer.WriteNumber("changed_lines", row.ChangedLines);
                writer.WriteString("last_commit_at_utc", row.LastCommitAtUtc);
                writer.WriteNumber("decision_count", row.DecisionCount);
                writer.WriteNumber("loop_count", row.LoopCount);
                writer.WriteNumber("max_nesting_depth", row.MaxNestingDepth);
                writer.WriteString("severity", ComplexityRankingReader.SeverityName(row.Severity));
                writer.WriteBoolean("is_test", row.IsTest);
                writer.WriteNumber("score", row.Score);
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

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

/// <param name="SnapshotMetrics">
/// The heavy-arm metric-history points the CLI recorder appends for a canonical churn/risk run, or <c>null</c> for
/// operations that do not record (clones/complexity). The tool core stays side-effect-free: it only COMPUTES the
/// points from the facts it already produced; the actual <c>history.db</c> write happens in the CLI handler.
/// </param>
internal readonly record struct MetricsToolResult(
    string Output, int ResultCount, IReadOnlyList<MetricHistoryPoint>? SnapshotMetrics = null);
