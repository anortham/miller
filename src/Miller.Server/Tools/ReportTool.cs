using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Git;

namespace Miller.Server.Tools;

/// <summary>
/// One composed repo-quality report over facts Miller already extracts: index counts, extraction
/// health, marker counts, complexity hotspots, clone groups, churn, and churn×complexity risk.
/// Pure composition — no new extraction, no recommendations, and no dead-code section until
/// reference resolution earns confidence (see the 2026-07-06 bolstering assessment consensus).
/// Sections that cannot be served (no git history, region search disabled) render as unavailable
/// with a reason instead of failing the report.
/// </summary>
public static class ReportTool
{
    public const int DefaultSectionLimit = 10;
    public const int MaxSectionLimit = 100;

    internal static ReportToolResult Run(
        string dbPath,
        string? workspaceRoot,
        string? range,
        int sectionLimit,
        bool json,
        bool includeTests,
        IGitHistoryReader? historyReader,
        IRegionSearchIndex? regionIndex,
        bool nearDuplicates = false,
        int nearDuplicateCandidateCap = MetricsTool.NearDuplicateCandidateCap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        int limit = Math.Clamp(sectionLimit, 1, MaxSectionLimit);
        string effectiveRange = string.IsNullOrWhiteSpace(range) ? "HEAD~20..HEAD" : range;

        IndexSection index = ReadIndexSection(dbPath);
        WorkspaceExtractionHealthFacts extraction = WorkspaceHealthReader.Read(dbPath);
        MarkerSection markers = ReadMarkerSection(dbPath, includeTests);
        IReadOnlyList<ComplexityHotspot> complexity = ComplexityRankingReader.Read(
            dbPath, limit, ComplexitySeverity.Moderate, includeTests);
        IReadOnlyList<CloneGroup> clones = CloneGroupReader.Read(
            dbPath, limit, minCount: 2, MetricsTool.DefaultCloneSymbolsPerGroup);
        GitSections git = ReadGitSections(dbPath, workspaceRoot, effectiveRange, limit, includeTests, historyReader);

        // Opt-in for the same reason `metrics clones --near-duplicates` is: the Type-2 arm re-reads symbol bodies
        // from disk. Off ⟹ the rollup is byte-identical to the version before this section existed.
        NearDuplicateScan? nearDuplicateScan = nearDuplicates && !string.IsNullOrWhiteSpace(workspaceRoot)
            ? MetricsTool.ScanNearDuplicates(
                dbPath, workspaceRoot, limit, MetricsTool.DefaultCloneSymbolsPerGroup, nearDuplicateCandidateCap)
            : null;

        var report = new ReportFacts(
            effectiveRange, limit, index, extraction, markers, complexity, clones, git, nearDuplicateScan);
        return new ReportToolResult(
            json ? RenderJson(report) : RenderCompact(report),
            BuildSnapshotMetrics(report));
    }

    // The heavy-arm `source='report'` snapshot: only facts the report composes EXACTLY, keyed on the canonical
    // metric names Task 4/6 read. Pure — it projects `report`, never recomputes. Cheap-arm names reuse
    // MetricSnapshotAggregates so the report and the leader converge arm never drift; the git scalars reuse the
    // shared heavy-arm vocabulary.
    //
    // clone_group_count AND marker_total are deliberately NOT recorded here. MetricHistoryStore.ReadTrend flattens
    // points by metric name across ALL sources (it never inspects detail_json), so any value the report records
    // must be exactly comparable with the leader converge arm's value for the same name — the design rule is a
    // metric is exact or absent, never misleading. Both fail that bar from the report side:
    //   clone_group_count — the report composes only a top-SectionLimit clone list, while converge records the
    //     EXACT GROUP BY count every revision; a truncated report value would poison the trend into a sawtooth.
    //   marker_total — the report composes only a bounded marker display, while converge records the exact producer
    //     fact count every revision. A bounded report value would poison the exact series. Converge owns it.
    //
    // The metrics that ARE recorded are exact and limit-insensitive across every producer:
    //   symbol/file/language — same WorkspaceIndexFactsReader.ReadSymbolCounts COUNT shape as the converge arm;
    //   churn_files_changed — ChurnReport.TotalFilesChanged, the exact pre-truncation distinct-path count, the
    //     same value `metrics churn` records regardless of either command's row limit;
    //   risk_top_score — the global max (risk rows are score-desc, so it is limit-insensitive).
    // Absent-vs-zero holds: git unavailable ⟹ no churn/risk rows; risk available but empty ⟹ risk_top_score
    // absent (a max over nothing is undefined).
    private static IReadOnlyList<MetricHistoryPoint> BuildSnapshotMetrics(ReportFacts report)
    {
        var points = new List<MetricHistoryPoint>
        {
            new(MetricSnapshotAggregates.SymbolCount, report.Index.Symbols, null),
            new(MetricSnapshotAggregates.FileCount, report.Index.Files, null),
            new(MetricSnapshotAggregates.LanguageCount, report.Index.Languages, null),
        };

        if (report.Git.Available)
        {
            string detail = MetricHistoryHeavyArm.RangeLimitDetail(report.Range, report.SectionLimit);
            points.Add(new MetricHistoryPoint(
                MetricHistoryHeavyArm.ChurnFilesChanged, report.Git.Churn!.TotalFilesChanged, detail));
            if (report.Git.Risk!.Rows.Count > 0)
                points.Add(new MetricHistoryPoint(
                    MetricHistoryHeavyArm.RiskTopScore, report.Git.Risk.Rows.Max(row => row.Score), detail));
        }

        // near_duplicate_group_count IS recordable from here, unlike clone_group_count: the scan's bounds are fixed
        // constants rather than the report's SectionLimit, so `miller report --near-duplicates` and
        // `metrics clones --near-duplicates` record the identical value for the same artifact. A truncated scan
        // records nothing (MetricsTool suppresses it) rather than mixing a floor into the series.
        if (MetricsTool.NearDuplicateSnapshotMetrics(report.NearDuplicates) is { } nearDuplicatePoints)
            points.AddRange(nearDuplicatePoints);

        return points;
    }

    private static IndexSection ReadIndexSection(string dbPath)
    {
        WorkspaceSymbolCounts counts = WorkspaceIndexFactsReader.ReadSymbolCounts(dbPath);
        return new IndexSection(counts.Symbols, counts.Files, counts.Languages);
    }

    private static MarkerSection ReadMarkerSection(string dbPath, bool includeTests)
    {
        IReadOnlyList<string> markerNames = MarkerSearch.ParseMarkers(null);
        IReadOnlyList<MarkerSearchHit> hits = MarkerSearch.FindMarkers(
            dbPath,
            markerNames,
            MarkerSearch.MaxLimit,
            excludeTests: !includeTests,
            filePattern: null,
            language: null);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string marker in markerNames)
            counts[marker] = 0;
        foreach (MarkerSearchHit hit in hits)
        {
            foreach (string marker in hit.Markers)
                counts[marker] = counts.GetValueOrDefault(marker) + 1;
        }

        return new MarkerSection(
            Available: true,
            Reason: null,
            BoundedAt: MarkerSearch.MaxLimit,
            Counts: markerNames.Select(marker => new MarkerCount(marker, counts[marker])).ToArray(),
            Total: hits.Count);
    }

    private static GitSections ReadGitSections(
        string dbPath,
        string? workspaceRoot,
        string range,
        int limit,
        bool includeTests,
        IGitHistoryReader? historyReader)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || historyReader is null)
            return GitSections.Unavailable("git history unavailable (no workspace root or history reader)");

        try
        {
            // One git history parse feeds both sections: full churn for the risk join, top-N for display.
            ChurnReport fullChurn = GitChurnAnalyzer.Read(
                dbPath, workspaceRoot, range, limit: int.MaxValue, includeCommits: false, historyReader);
            var churn = new ChurnReport(fullChurn.Range, fullChurn.Rows.Take(limit).ToArray(), fullChurn.TotalFilesChanged);
            RiskReport risk = RiskRanking.FromChurn(dbPath, fullChurn, limit, includeTests);
            return new GitSections(Available: true, Reason: null, churn, risk);
        }
        catch (InvalidOperationException ex)
        {
            return GitSections.Unavailable(ex.Message);
        }
    }

    private static string RenderCompact(ReportFacts report)
    {
        var sb = new StringBuilder();
        sb.Append("# miller report  range=").AppendLine(report.Range);

        sb.AppendLine();
        sb.AppendLine("## index");
        sb.Append("symbols ").Append(report.Index.Symbols)
          .Append("  files ").Append(report.Index.Files)
          .Append("  languages ").Append(report.Index.Languages).AppendLine();

        sb.AppendLine();
        sb.AppendLine("## extraction health");
        AppendExtractionCompact(sb, report.Extraction);

        sb.AppendLine();
        sb.AppendLine("## markers");
        if (!report.Markers.Available)
        {
            sb.Append("markers: unavailable — ").AppendLine(report.Markers.Reason);
        }
        else
        {
            foreach (MarkerCount count in report.Markers.Counts)
                sb.Append(count.Marker).Append(' ').Append(count.Count).Append("  ");
            sb.AppendLine();
            if (report.Markers.Total >= report.Markers.BoundedAt)
                sb.Append("(counts bounded at ").Append(report.Markers.BoundedAt).AppendLine(")");
        }

        sb.AppendLine();
        sb.Append("## complexity (top ").Append(report.SectionLimit).AppendLine(", severity >= moderate)");
        if (report.Complexity.Count == 0)
            sb.AppendLine("none");
        foreach (ComplexityHotspot hotspot in report.Complexity)
        {
            sb.Append(ComplexityRankingReader.SeverityName(hotspot.Severity)).Append('\t')
              .Append(hotspot.DecisionCount).Append('\t')
              .Append(hotspot.MaxNestingDepth).Append('\t')
              .Append(hotspot.Path).Append(':').Append(hotspot.StartLine).Append('\t')
              .AppendLine(hotspot.SymbolName ?? hotspot.Scope);
        }

        sb.AppendLine();
        sb.Append("## clones (top ").Append(report.SectionLimit).AppendLine(")");
        if (report.Clones.Count == 0)
            sb.AppendLine("none");
        foreach (CloneGroup group in report.Clones)
        {
            sb.Append(group.BodyHash).Append("  count=").Append(group.Count);
            if (group.Symbols.Count > 0)
                sb.Append("  e.g. ").Append(group.Symbols[0].Path).Append(':').Append(group.Symbols[0].Line);
            sb.AppendLine();
        }
        if (report.NearDuplicates is { } scan)
        {
            sb.Append("near-duplicate groups: ").Append(scan.GroupCount).AppendLine();
            if (scan.CandidatesTruncated)
                sb.AppendLine(MetricsTool.NearDuplicateTruncationNote(scan));
        }

        sb.AppendLine();
        sb.Append("## churn (top ").Append(report.SectionLimit).AppendLine(")");
        if (!report.Git.Available)
        {
            sb.Append("churn: unavailable — ").AppendLine(report.Git.Reason);
        }
        else
        {
            foreach (ChurnRow row in report.Git.Churn!.Rows)
            {
                sb.Append(row.CommitCount).Append('\t')
                  .Append(row.ChangedLines).Append('\t')
                  .Append(row.Path);
                if (row.Line is { } line)
                    sb.Append(':').Append(line);
                sb.Append('\t').AppendLine(row.SymbolName ?? "");
            }
        }

        sb.AppendLine();
        sb.Append("## risk (top ").Append(report.SectionLimit).AppendLine(")");
        if (!report.Git.Available)
        {
            sb.Append("risk: unavailable — ").AppendLine(report.Git.Reason);
        }
        else
        {
            sb.Append("score = ").AppendLine(RiskRanking.ScoreFormula);
            foreach (RiskRow row in report.Git.Risk!.Rows)
            {
                sb.Append(row.Score).Append('\t')
                  .Append(ComplexityRankingReader.SeverityName(row.Severity)).Append('\t')
                  .Append(row.Path);
                if (row.Line is { } line)
                    sb.Append(':').Append(line);
                sb.Append('\t').AppendLine(row.SymbolName ?? "");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendExtractionCompact(StringBuilder sb, WorkspaceExtractionHealthFacts extraction)
    {
        if (extraction.ParseDiagnostics.Available)
            sb.Append("parse diagnostics: ")
              .Append(extraction.ParseDiagnostics.Rows.Sum(static row => row.Count)).AppendLine();
        else
            sb.Append("parse diagnostics: unavailable — ").AppendLine(extraction.ParseDiagnostics.Error);
        if (extraction.CapabilityGaps.Available)
            sb.Append("capability gaps: ")
              .Append(extraction.CapabilityGaps.Rows.Sum(static row => row.Count)).AppendLine();
        else
            sb.Append("capability gaps: unavailable — ").AppendLine(extraction.CapabilityGaps.Error);
    }

    private static string RenderJson(ReportFacts report)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("operation", "report");
            writer.WriteString("range", report.Range);
            writer.WriteNumber("section_limit", report.SectionLimit);

            writer.WriteStartObject("index");
            writer.WriteBoolean("available", true);
            writer.WriteNumber("symbols", report.Index.Symbols);
            writer.WriteNumber("files", report.Index.Files);
            writer.WriteNumber("languages", report.Index.Languages);
            writer.WriteEndObject();

            writer.WriteStartObject("extraction_health");
            writer.WriteBoolean("available", report.Extraction.ParseDiagnostics.Available);
            if (report.Extraction.ParseDiagnostics.Available)
                writer.WriteNumber(
                    "parse_diagnostic_count",
                    report.Extraction.ParseDiagnostics.Rows.Sum(static row => row.Count));
            else
                writer.WriteString("reason", report.Extraction.ParseDiagnostics.Error);
            if (report.Extraction.CapabilityGaps.Available)
                writer.WriteNumber(
                    "capability_gap_count",
                    report.Extraction.CapabilityGaps.Rows.Sum(static row => row.Count));
            writer.WriteEndObject();

            writer.WriteStartObject("markers");
            writer.WriteBoolean("available", report.Markers.Available);
            if (!report.Markers.Available)
            {
                writer.WriteString("reason", report.Markers.Reason);
            }
            else
            {
                writer.WriteNumber("bounded_at", report.Markers.BoundedAt);
                writer.WriteBoolean("truncated", report.Markers.Total >= report.Markers.BoundedAt);
                writer.WriteStartArray("counts");
                foreach (MarkerCount count in report.Markers.Counts)
                {
                    writer.WriteStartObject();
                    writer.WriteString("marker", count.Marker);
                    writer.WriteNumber("count", count.Count);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteNumber("total", report.Markers.Total);
            }
            writer.WriteEndObject();

            writer.WriteStartObject("complexity");
            writer.WriteBoolean("available", true);
            writer.WriteString("min_severity", "moderate");
            writer.WriteStartArray("hotspots");
            foreach (ComplexityHotspot hotspot in report.Complexity)
            {
                writer.WriteStartObject();
                writer.WriteString("severity", ComplexityRankingReader.SeverityName(hotspot.Severity));
                writer.WriteString("path", hotspot.Path);
                if (hotspot.SymbolName is null) writer.WriteNull("symbol_name"); else writer.WriteString("symbol_name", hotspot.SymbolName);
                writer.WriteNumber("decision_count", hotspot.DecisionCount);
                writer.WriteNumber("loop_count", hotspot.LoopCount);
                writer.WriteNumber("max_nesting_depth", hotspot.MaxNestingDepth);
                writer.WriteNumber("start_line", hotspot.StartLine);
                writer.WriteBoolean("is_test", hotspot.IsTest);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("clones");
            writer.WriteBoolean("available", true);
            writer.WriteStartArray("groups");
            foreach (CloneGroup group in report.Clones)
            {
                writer.WriteStartObject();
                writer.WriteString("body_hash", group.BodyHash);
                writer.WriteNumber("count", group.Count);
                writer.WriteStartArray("sample");
                foreach (CloneSymbol symbol in group.Symbols.Take(3))
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", symbol.Path);
                    writer.WriteNumber("line", symbol.Line);
                    writer.WriteString("name", symbol.Name);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (report.NearDuplicates is { } scan)
            {
                writer.WriteNumber("near_duplicate_groups", scan.GroupCount);
                writer.WriteBoolean("near_duplicate_truncated", scan.CandidatesTruncated);
            }
            writer.WriteEndObject();

            WriteChurnSection(writer, report.Git);
            WriteRiskSection(writer, report.Git);

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteChurnSection(Utf8JsonWriter writer, GitSections git)
    {
        writer.WriteStartObject("churn");
        writer.WriteBoolean("available", git.Available);
        if (!git.Available)
        {
            writer.WriteString("reason", git.Reason);
            writer.WriteEndObject();
            return;
        }
        writer.WriteStartArray("rows");
        foreach (ChurnRow row in git.Churn!.Rows)
        {
            writer.WriteStartObject();
            writer.WriteString("mapping_basis", row.MappingBasis);
            if (row.SymbolName is null) writer.WriteNull("symbol_name"); else writer.WriteString("symbol_name", row.SymbolName);
            writer.WriteString("path", row.Path);
            if (row.Line is null) writer.WriteNull("line"); else writer.WriteNumber("line", row.Line.Value);
            writer.WriteNumber("commit_count", row.CommitCount);
            writer.WriteNumber("changed_lines", row.ChangedLines);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRiskSection(Utf8JsonWriter writer, GitSections git)
    {
        writer.WriteStartObject("risk");
        writer.WriteBoolean("available", git.Available);
        if (!git.Available)
        {
            writer.WriteString("reason", git.Reason);
            writer.WriteEndObject();
            return;
        }
        writer.WriteString("score_formula", RiskRanking.ScoreFormula);
        writer.WriteStartArray("rows");
        foreach (RiskRow row in git.Risk!.Rows)
        {
            writer.WriteStartObject();
            writer.WriteString("basis", row.Basis);
            if (row.SymbolName is null) writer.WriteNull("symbol_name"); else writer.WriteString("symbol_name", row.SymbolName);
            writer.WriteString("path", row.Path);
            if (row.Line is null) writer.WriteNull("line"); else writer.WriteNumber("line", row.Line.Value);
            writer.WriteNumber("commit_count", row.CommitCount);
            writer.WriteNumber("changed_lines", row.ChangedLines);
            writer.WriteNumber("decision_count", row.DecisionCount);
            writer.WriteNumber("max_nesting_depth", row.MaxNestingDepth);
            writer.WriteString("severity", ComplexityRankingReader.SeverityName(row.Severity));
            writer.WriteNumber("score", row.Score);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private sealed record ReportFacts(
        string Range,
        int SectionLimit,
        IndexSection Index,
        WorkspaceExtractionHealthFacts Extraction,
        MarkerSection Markers,
        IReadOnlyList<ComplexityHotspot> Complexity,
        IReadOnlyList<CloneGroup> Clones,
        GitSections Git,
        NearDuplicateScan? NearDuplicates);

    private sealed record IndexSection(long Symbols, long Files, long Languages);

    private sealed record MarkerCount(string Marker, int Count);

    private sealed record MarkerSection(
        bool Available,
        string? Reason,
        int BoundedAt,
        IReadOnlyList<MarkerCount> Counts,
        int Total);

    private sealed record GitSections(
        bool Available,
        string? Reason,
        ChurnReport? Churn = null,
        RiskReport? Risk = null)
    {
        public static GitSections Unavailable(string reason) => new(false, reason);
    }
}

/// <param name="SnapshotMetrics">
/// The heavy-arm <c>source='report'</c> metric-history points the CLI recorder appends for a canonical run. The
/// tool core stays side-effect-free: it only COMPUTES the points from the facts it composed; the actual
/// <c>history.db</c> write happens in the CLI handler.
/// </param>
internal readonly record struct ReportToolResult(
    string Output, IReadOnlyList<MetricHistoryPoint> SnapshotMetrics);
