using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Analysis;
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

    /// <summary>Default snapshot window for <c>metrics history</c> — the most recent 20 snapshots.</summary>
    public const int DefaultHistoryLimit = 20;

    /// <summary>The <c>metrics history --json</c> envelope version (docs/contracts/metrics-history-v1.md).</summary>
    public const int HistorySchemaVersion = 1;

    /// <summary>
    /// The default metric set for <c>metrics history</c> when <c>--metric</c> is omitted: one cheap-arm rollup per
    /// signal family (symbols, complexity, clones, markers) plus the dead-code heavy-arm count. Names are the
    /// canonical producer consts so the read surface can never drift from what the write arms emit.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultHistoryMetrics =
    [
        MetricSnapshotAggregates.SymbolCount,
        MetricSnapshotAggregates.ComplexityP90,
        MetricSnapshotAggregates.CloneGroupCount,
        MetricSnapshotAggregates.MarkerTotal,
        MetricHistoryHeavyArm.DeadCodeCandidateCount,
    ];

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
        IGitHistoryReader? historyReader = null,
        bool nearDuplicates = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
        string op = NormalizeOperation(operation);
        return op switch
        {
            "clones" => RunClones(
                dbPath, boundedLimit, json, minCount, maxSymbolsPerGroup, workspaceRoot, nearDuplicates),
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

    /// <summary>
    /// Symbols examined by the opt-in Type-2 arm, in <c>(path, start_line, symbol_id)</c> order. Bounded because
    /// each candidate costs one disk-verified body read; a bigger sweep belongs in a background arm, not a CLI verb.
    /// </summary>
    private const int NearDuplicateCandidateCap = 2000;

    /// <summary>Body-span byte floor for a Type-2 candidate — below it a body cannot clear the analyzer's token floor.</summary>
    private const int NearDuplicateMinBodyBytes = 160;

    private static MetricsToolResult RunClones(
        string dbPath,
        int limit,
        bool json,
        int minCount,
        int maxSymbolsPerGroup,
        string? workspaceRoot,
        bool nearDuplicates)
    {
        int boundedSymbolLimit = Math.Clamp(maxSymbolsPerGroup, 1, CloneGroupReader.MaxSymbolsPerGroup);
        IReadOnlyList<CloneGroup> groups = CloneGroupReader.Read(dbPath, limit, minCount, boundedSymbolLimit);

        // Off by default: the Type-2 arm re-reads symbol bodies from disk (hash-verified per file), so it must
        // never ride along on a plain `metrics clones`. With it off the output below is byte-identical to v1.
        IReadOnlyList<NearDuplicateCloneGroup> near =
            nearDuplicates && !string.IsNullOrWhiteSpace(workspaceRoot)
                ? ReadNearDuplicateGroups(dbPath, workspaceRoot, limit, boundedSymbolLimit)
                : [];

        return new MetricsToolResult(
            json
                ? RenderClonesJson(groups, near, boundedSymbolLimit)
                : RenderClonesCompact(groups, near, boundedSymbolLimit),
            groups.Count + near.Count,
            SnapshotMetrics: null);
    }

    /// <summary>
    /// The Type-2 arm: read a bounded, deterministically ordered candidate set of symbol bodies from disk and
    /// hand them to the pure <see cref="NearDuplicateAnalyzer"/>. A body whose file drifted from the indexed
    /// content is skipped by <see cref="ExtractReader.ReadBody"/> rather than sliced stale, so a stale workspace
    /// yields fewer groups — never wrong ones.
    /// </summary>
    private static IReadOnlyList<NearDuplicateCloneGroup> ReadNearDuplicateGroups(
        string dbPath,
        string workspaceRoot,
        int limit,
        int symbolLimit)
    {
        IReadOnlyList<NearDuplicateCandidate> candidates = ReadNearDuplicateCandidates(dbPath);
        if (candidates.Count < 2)
            return [];

        var inputs = new List<NearDuplicateInput>(candidates.Count);
        var bySymbolId = new Dictionary<string, CloneSymbol>(StringComparer.Ordinal);
        foreach (NearDuplicateCandidate candidate in candidates)
        {
            ExtractReader.BodyReadResult body = ExtractReader.ReadBody(
                dbPath,
                workspaceRoot,
                candidate.Symbol.Path,
                candidate.BodyStartByte,
                candidate.BodyEndByte,
                candidate.BodyStartLine,
                candidate.BodyEndLine);
            if (body.Text is not { Length: > 0 } text)
                continue;

            inputs.Add(new NearDuplicateInput(candidate.Symbol.SymbolId, text));
            bySymbolId[candidate.Symbol.SymbolId] = candidate.Symbol;
        }

        IReadOnlyList<NearDuplicateGroup> analyzed =
            NearDuplicateAnalyzer.FindGroups(inputs, new NearDuplicateOptions { MaxGroups = limit });

        var results = new List<NearDuplicateCloneGroup>(analyzed.Count);
        foreach (NearDuplicateGroup group in analyzed)
        {
            List<CloneSymbol> symbols = group.MemberIds
                .Select(id => bySymbolId[id])
                .OrderBy(symbol => symbol.Path, StringComparer.Ordinal)
                .ThenBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
                .ToList();
            results.Add(new NearDuplicateCloneGroup(
                Math.Round(group.Similarity, 4, MidpointRounding.ToEven),
                symbols.Count,
                symbols.Take(symbolLimit).ToList()));
        }
        return results;
    }

    // Runs only AFTER CloneGroupReader.Read has already opened the same artifact through the D5 schema gate, so an
    // incompatible artifact fails there with the standard actionable message before this query is ever reached.
    private static IReadOnlyList<NearDuplicateCandidate> ReadNearDuplicateCandidates(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_id, name, kind, language, path, start_line, is_test,
                   body_start_byte, body_end_byte, body_start_line, body_end_line
            FROM symbols
            WHERE body_start_byte IS NOT NULL AND body_end_byte IS NOT NULL
              AND (body_end_byte - body_start_byte) >= $min_bytes
            ORDER BY path, start_line, symbol_id
            LIMIT $cap;
            """;
        command.Parameters.AddWithValue("$min_bytes", NearDuplicateMinBodyBytes);
        command.Parameters.AddWithValue("$cap", NearDuplicateCandidateCap);

        var candidates = new List<NearDuplicateCandidate>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            candidates.Add(new NearDuplicateCandidate(
                new CloneSymbol(
                    SymbolId: reader.GetString(0),
                    Name: reader.GetString(1),
                    Kind: reader.GetString(2),
                    Language: reader.GetString(3),
                    Path: reader.GetString(4),
                    Line: reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    IsTest: !reader.IsDBNull(6) && reader.GetInt64(6) != 0),
                BodyStartByte: reader.GetInt32(7),
                BodyEndByte: reader.GetInt32(8),
                BodyStartLine: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                BodyEndLine: reader.IsDBNull(10) ? null : reader.GetInt32(10)));
        }
        return candidates;
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

    /// <summary>
    /// Read the metric-history trend from a workspace <c>history.db</c> and render it (compact table or the stable
    /// <c>metrics-history-v1</c> JSON envelope). Pure and read-only — it records nothing. <paramref name="metrics"/>
    /// empty ⟹ <see cref="DefaultHistoryMetrics"/>; a snapshot window of the most recent <paramref name="limit"/>
    /// snapshots is read with NO downsampling (raw points — downsampling is a dashboard concern). An empty/missing
    /// history is a friendly exit-0 state, never an error: the compact form nudges to <c>miller report</c>, the JSON
    /// form emits <c>workspace_id</c> plus an empty <c>metrics</c> array.
    /// </summary>
    internal static MetricsToolResult RunHistory(
        string historyDbPath,
        string workspaceId,
        IReadOnlyList<string> metrics,
        int limit,
        bool json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyDbPath);

        IReadOnlyList<string> requested = metrics is { Count: > 0 } ? metrics : DefaultHistoryMetrics;
        string[] wanted = requested
            .Where(static m => !string.IsNullOrWhiteSpace(m))
            .Select(static m => m.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (wanted.Length == 0)
            wanted = DefaultHistoryMetrics.ToArray();

        int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
        // maxPoints <= 0 ⟹ no downsampling: the CLI shows every recorded point. Downsampling stays a dashboard concern.
        IReadOnlyList<MetricHistoryTrendPoint> points =
            MetricHistoryStore.ReadTrend(historyDbPath, wanted, boundedLimit, maxPoints: 0);

        return new MetricsToolResult(
            json ? RenderHistoryJson(workspaceId, wanted, points) : RenderHistoryCompact(wanted, points),
            points.Count,
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

    // The heavy-arm churn snapshot: churn_files_changed = the EXACT distinct changed-path count for the range
    // (ChurnReport.TotalFilesChanged, computed before row truncation), range+limit stamped in detail_json. Exactness
    // is load-bearing: the report arm records the same metric name at a different row limit, and ReadTrend flattens
    // by name — a row-bounded count here would mix non-comparable values into one series. A genuinely empty churn is
    // 0 files changed (git was available), which the recorder writes as a real value.
    private static IReadOnlyList<MetricHistoryPoint> ChurnSnapshotMetrics(ChurnReport report, int limit) =>
    [
        new MetricHistoryPoint(
            MetricHistoryHeavyArm.ChurnFilesChanged,
            report.TotalFilesChanged,
            MetricHistoryHeavyArm.RangeLimitDetail(report.Range, limit)),
    ];

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

    private static string RenderClonesCompact(
        IReadOnlyList<CloneGroup> groups,
        IReadOnlyList<NearDuplicateCloneGroup> nearDuplicates,
        int symbolLimit)
    {
        if (groups.Count == 0 && nearDuplicates.Count == 0)
            return "No clone groups.";

        var sb = new StringBuilder();
        if (groups.Count > 0)
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

        if (nearDuplicates.Count > 0)
        {
            sb.AppendLine("# near-duplicate groups");
            foreach (NearDuplicateCloneGroup group in nearDuplicates)
            {
                sb.Append("similarity=")
                  .Append(group.Similarity.ToString("0.####", CultureInfo.InvariantCulture))
                  .Append("  count=").Append(group.Count).AppendLine();
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

    private static string RenderClonesJson(
        IReadOnlyList<CloneGroup> groups,
        IReadOnlyList<NearDuplicateCloneGroup> nearDuplicates,
        int symbolLimit)
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
            // Additive per metrics-json-v1: near-duplicate groups are appended to the same array and are the ONLY
            // entries carrying `kind`/`similarity` (an absent `kind` means the v1 exact `body_hash` group). Nothing
            // is written when the Type-2 arm is off or finds nothing, so v1 output stays byte-identical.
            foreach (NearDuplicateCloneGroup group in nearDuplicates)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "near_duplicate");
                writer.WriteNumber("similarity", group.Similarity);
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

    // Compact trend: one line per snapshot, oldest FIRST / newest LAST so each metric column reads as a trend down
    // the page. `wanted` fixes the column order; a metric absent from a snapshot renders "-" (an absent row, never
    // a fabricated 0). Points arrive snapshot_id-ordered from ReadTrend; the pivot re-sorts by snapshot_id defensively.
    private static string RenderHistoryCompact(
        IReadOnlyList<string> wanted, IReadOnlyList<MetricHistoryTrendPoint> points)
    {
        if (points.Count == 0)
            return "no trend data yet — run `miller report`.";

        var order = new List<long>();
        var byId = new Dictionary<long, HistorySnapshotRow>();
        foreach (MetricHistoryTrendPoint p in points)
        {
            if (!byId.TryGetValue(p.SnapshotId, out HistorySnapshotRow? row))
            {
                row = new HistorySnapshotRow(p.RecordedAtUtc, p.Revision, p.Source);
                byId[p.SnapshotId] = row;
                order.Add(p.SnapshotId);
            }
            row.Values[p.Metric] = p.Value;
        }
        order.Sort();

        var sb = new StringBuilder();
        sb.AppendLine("# metric history");
        sb.Append("recorded_at_utc\trevision\tsource");
        foreach (string metric in wanted)
            sb.Append('\t').Append(metric);
        sb.AppendLine();

        foreach (long id in order)
        {
            HistorySnapshotRow row = byId[id];
            sb.Append(row.RecordedAtUtc).Append('\t').Append(row.Revision).Append('\t').Append(row.Source);
            foreach (string metric in wanted)
                sb.Append('\t').Append(row.Values.TryGetValue(metric, out double v) ? FormatMetricValue(v) : "-");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // The stable metrics-history-v1 envelope: { schema_version, workspace_id, metrics: [{ metric, points: [...] }] }.
    // Metric series are emitted in `wanted` order; a metric with no recorded points is omitted (so an empty/missing
    // history yields metrics: []). Points inside a series stay snapshot_id-ordered (newest last).
    private static string RenderHistoryJson(
        string workspaceId, IReadOnlyList<string> wanted, IReadOnlyList<MetricHistoryTrendPoint> points)
    {
        var byMetric = new Dictionary<string, List<MetricHistoryTrendPoint>>(StringComparer.Ordinal);
        foreach (MetricHistoryTrendPoint p in points)
        {
            if (!byMetric.TryGetValue(p.Metric, out List<MetricHistoryTrendPoint>? list))
            {
                list = new List<MetricHistoryTrendPoint>();
                byMetric[p.Metric] = list;
            }
            list.Add(p);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", HistorySchemaVersion);
            writer.WriteString("workspace_id", workspaceId);
            writer.WriteStartArray("metrics");
            foreach (string metric in wanted)
            {
                if (!byMetric.TryGetValue(metric, out List<MetricHistoryTrendPoint>? series) || series.Count == 0)
                    continue;
                writer.WriteStartObject();
                writer.WriteString("metric", metric);
                writer.WriteStartArray("points");
                foreach (MetricHistoryTrendPoint pt in series)
                {
                    writer.WriteStartObject();
                    writer.WriteString("recorded_at_utc", pt.RecordedAtUtc);
                    writer.WriteString("artifact_id", pt.ArtifactId);
                    writer.WriteNumber("revision", pt.Revision);
                    writer.WriteString("source", pt.Source);
                    writer.WriteNumber("value", pt.Value);
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

    // Integral values render without a decimal tail (symbol_count=1200, not 1200.0); a fractional metric such as an
    // interpolated complexity_p90 keeps up to three significant fractional digits.
    private static string FormatMetricValue(double value) =>
        !double.IsInfinity(value) && !double.IsNaN(value) && value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed class HistorySnapshotRow(string recordedAtUtc, long revision, string source)
    {
        public string RecordedAtUtc { get; } = recordedAtUtc;
        public long Revision { get; } = revision;
        public string Source { get; } = source;
        public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);
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

/// <summary>
/// A rendered Type-2 group: the analyzer's weakest-edge similarity plus the group's symbols in the same
/// <c>(path, line, symbol_id)</c> order the exact clone groups use. <see cref="Count"/> is the full membership;
/// <see cref="Symbols"/> is bounded by the caller's symbol limit.
/// </summary>
internal sealed record NearDuplicateCloneGroup(double Similarity, int Count, IReadOnlyList<CloneSymbol> Symbols);

/// <summary>One symbol eligible for Type-2 analysis, with the body span the disk re-source needs.</summary>
internal sealed record NearDuplicateCandidate(
    CloneSymbol Symbol,
    int BodyStartByte,
    int BodyEndByte,
    int? BodyStartLine,
    int? BodyEndLine);
