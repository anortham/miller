using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

public static class ComplexityRankingReader
{
    public const int ModerateDecisionThreshold = 8;
    public const int ModerateNestingThreshold = 4;
    public const int HighDecisionThreshold = 15;
    public const int HighNestingThreshold = 6;

    public static IReadOnlyList<ComplexityHotspot> Read(
        string symbolsDbPath,
        int limit = 50,
        ComplexitySeverity minSeverity = ComplexitySeverity.Moderate,
        bool includeTests = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        if (limit < 1)
            limit = 1;

        using SqliteConnection connection = SqliteReadOnlyAccess.Open(symbolsDbPath);
        JulieSchemaGate.Verify(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT cm.complexity_metric_id, cm.path, cm.language, cm.scope, cm.symbol_id, cm.algorithm_id,
                   cm.covered_lines, cm.covered_bytes, cm.decision_count, cm.loop_count, cm.max_nesting_depth,
                   cm.parameter_count, cm.start_line, cm.end_line, cm.start_byte, cm.end_byte,
                   s.name, s.kind, COALESCE(s.is_test, 0) AS is_test
            FROM complexity_metrics cm
            LEFT JOIN symbols s ON s.symbol_id = cm.symbol_id
            WHERE ($include_tests = 1 OR COALESCE(s.is_test, 0) = 0)
              AND (
                  $min_severity = 'low'
                  OR ($min_severity = 'moderate'
                      AND (cm.decision_count >= $moderate_decision OR cm.max_nesting_depth >= $moderate_nesting))
                  OR ($min_severity = 'high'
                      AND (cm.decision_count >= $high_decision OR cm.max_nesting_depth >= $high_nesting))
              )
            ORDER BY
                CASE
                    WHEN cm.decision_count >= $high_decision OR cm.max_nesting_depth >= $high_nesting THEN 0
                    WHEN cm.decision_count >= $moderate_decision OR cm.max_nesting_depth >= $moderate_nesting THEN 1
                    ELSE 2
                END,
                cm.decision_count DESC,
                cm.max_nesting_depth DESC,
                cm.covered_lines DESC,
                cm.path,
                cm.start_line,
                cm.complexity_metric_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$include_tests", includeTests ? 1 : 0);
        command.Parameters.AddWithValue("$min_severity", SeverityName(minSeverity));
        command.Parameters.AddWithValue("$moderate_decision", ModerateDecisionThreshold);
        command.Parameters.AddWithValue("$moderate_nesting", ModerateNestingThreshold);
        command.Parameters.AddWithValue("$high_decision", HighDecisionThreshold);
        command.Parameters.AddWithValue("$high_nesting", HighNestingThreshold);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<ComplexityHotspot>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            int decisionCount = reader.GetInt32(8);
            int nesting = reader.GetInt32(10);
            results.Add(new ComplexityHotspot(
                Severity: Classify(decisionCount, nesting),
                ComplexityMetricId: reader.GetString(0),
                Path: reader.GetString(1),
                Language: reader.GetString(2),
                Scope: reader.GetString(3),
                SymbolId: reader.IsDBNull(4) ? null : reader.GetString(4),
                AlgorithmId: reader.GetString(5),
                CoveredLines: reader.GetInt32(6),
                CoveredBytes: reader.GetInt32(7),
                DecisionCount: decisionCount,
                LoopCount: reader.GetInt32(9),
                MaxNestingDepth: nesting,
                ParameterCount: reader.IsDBNull(11) ? null : reader.GetInt32(11),
                StartLine: reader.GetInt32(12),
                EndLine: reader.GetInt32(13),
                StartByte: reader.GetInt32(14),
                EndByte: reader.GetInt32(15),
                SymbolName: reader.IsDBNull(16) ? null : reader.GetString(16),
                SymbolKind: reader.IsDBNull(17) ? null : reader.GetString(17),
                IsTest: reader.GetInt64(18) != 0));
        }

        return results;
    }

    public static bool TryParseSeverity(string? value, out ComplexitySeverity severity)
    {
        severity = ComplexitySeverity.Moderate;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "low":
                severity = ComplexitySeverity.Low;
                return true;
            case "moderate":
            case "medium":
                severity = ComplexitySeverity.Moderate;
                return true;
            case "high":
                severity = ComplexitySeverity.High;
                return true;
            default:
                return false;
        }
    }

    public static ComplexitySeverity Classify(int decisionCount, int maxNestingDepth)
    {
        if (decisionCount >= HighDecisionThreshold || maxNestingDepth >= HighNestingThreshold)
            return ComplexitySeverity.High;
        if (decisionCount >= ModerateDecisionThreshold || maxNestingDepth >= ModerateNestingThreshold)
            return ComplexitySeverity.Moderate;
        return ComplexitySeverity.Low;
    }

    public static string SeverityName(ComplexitySeverity severity) => severity switch
    {
        ComplexitySeverity.Low => "low",
        ComplexitySeverity.Moderate => "moderate",
        ComplexitySeverity.High => "high",
        _ => "moderate",
    };
}

public enum ComplexitySeverity
{
    Low = 0,
    Moderate = 1,
    High = 2,
}

public sealed record ComplexityHotspot(
    ComplexitySeverity Severity,
    string ComplexityMetricId,
    string Path,
    string Language,
    string Scope,
    string? SymbolId,
    string AlgorithmId,
    int CoveredLines,
    int CoveredBytes,
    int DecisionCount,
    int LoopCount,
    int MaxNestingDepth,
    int? ParameterCount,
    int StartLine,
    int EndLine,
    int StartByte,
    int EndByte,
    string? SymbolName,
    string? SymbolKind,
    bool IsTest);
