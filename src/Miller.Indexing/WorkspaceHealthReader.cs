using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Cheap aggregate reader for workspace health surfaces. It intentionally avoids hydrating symbols,
/// relationships, identifiers, or source text.
/// </summary>
public static class WorkspaceHealthReader
{
    public static WorkspaceExtractionHealthFacts Read(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        using SqliteConnection connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);

        return new WorkspaceExtractionHealthFacts(
            ParseDiagnostics: ReadSection(connection, "parse_diagnostics", ReadParseDiagnostics),
            CapabilityGaps: ReadSection(connection, "language_capability_gaps", ReadCapabilityGaps),
            LanguageCapabilities: ReadSection(connection, "language_capabilities", ReadLanguageCapabilities),
            StructuralFacts: ReadSection(connection, "structural_facts", ReadStructuralFacts),
            ComplexityMetrics: ReadSection(connection, "complexity_metrics", ReadComplexityMetrics),
            Files: ReadSection(connection, "files", ReadFileStatuses));
    }

    private static HealthFactSection<T> ReadSection<T>(
        SqliteConnection connection,
        string tableName,
        Func<SqliteConnection, IReadOnlyList<T>> read)
    {
        if (!TableExists(connection, tableName))
            return HealthFactSection<T>.Unavailable($"table '{tableName}' is missing");

        return HealthFactSection<T>.FromRows(read(connection));
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        object? result = command.ExecuteScalar();
        return result is not null and not DBNull;
    }

    private static IReadOnlyList<ParseDiagnosticGroup> ReadParseDiagnostics(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT language, kind, COUNT(*) AS count
            FROM parse_diagnostics
            GROUP BY language, kind
            ORDER BY language, kind;
            """;
        var rows = new List<ParseDiagnosticGroup>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ParseDiagnosticGroup(
                Language: reader.GetString(0),
                Kind: reader.GetString(1),
                Count: reader.GetInt64(2)));
        }

        return rows;
    }

    private static IReadOnlyList<CapabilityGapGroup> ReadCapabilityGaps(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT language, capability, status, COUNT(*) AS count
            FROM language_capability_gaps
            GROUP BY language, capability, status
            ORDER BY language, capability, status;
            """;
        var rows = new List<CapabilityGapGroup>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CapabilityGapGroup(
                Language: reader.GetString(0),
                Capability: reader.GetString(1),
                Status: reader.GetString(2),
                Count: reader.GetInt64(3)));
        }

        return rows;
    }

    private static IReadOnlyList<LanguageCapabilitySummary> ReadLanguageCapabilities(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT language,
                   target_symbols, actual_symbols,
                   target_relationships, actual_relationships,
                   target_pending_relationships, actual_pending_relationships,
                   target_identifiers, actual_identifiers,
                   target_types, actual_types,
                   kind_coverage_json
            FROM language_capabilities
            ORDER BY language;
            """;
        var rows = new List<LanguageCapabilitySummary>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new LanguageCapabilitySummary(
                Language: reader.GetString(0),
                TargetSymbols: reader.GetInt64(1),
                ActualSymbols: reader.GetInt64(2),
                TargetRelationships: reader.GetInt64(3),
                ActualRelationships: reader.GetInt64(4),
                TargetPendingRelationships: reader.GetInt64(5),
                ActualPendingRelationships: reader.GetInt64(6),
                TargetIdentifiers: reader.GetInt64(7),
                ActualIdentifiers: reader.GetInt64(8),
                TargetTypes: reader.GetInt64(9),
                ActualTypes: reader.GetInt64(10),
                KindCoverage: ParseKindCoverage(reader.IsDBNull(11) ? null : reader.GetString(11))));
        }

        return rows;
    }

    // The artifact contract guards kind_coverage_json shape upstream (julie golden contract); an
    // unparseable cell degrades to "no depth facts" here rather than failing the whole health surface.
    private static IReadOnlyList<KindCoverageDomain> ParseKindCoverage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<KindCoverageDomain>();

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<KindCoverageDomain>();

            var domains = new List<KindCoverageDomain>();
            foreach (JsonProperty domain in document.RootElement.EnumerateObject())
            {
                if (domain.Value.ValueKind != JsonValueKind.Object)
                    continue;

                domains.Add(new KindCoverageDomain(
                    Domain: domain.Name,
                    Supported: ReadKindArray(domain.Value, "supported"),
                    OpenGaps: ReadKindArray(domain.Value, "open_gaps"),
                    NotApplicable: ReadKindArray(domain.Value, "not_applicable")));
            }

            domains.Sort(static (a, b) => string.CompareOrdinal(a.Domain, b.Domain));
            return domains;
        }
        catch (JsonException)
        {
            return Array.Empty<KindCoverageDomain>();
        }
    }

    private static IReadOnlyList<string> ReadKindArray(JsonElement domain, string propertyName)
    {
        if (!domain.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var values = new List<string>();
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
                values.Add(element.GetString()!);
        }

        return values;
    }

    private static IReadOnlyList<FileStatusGroup> ReadFileStatuses(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(language, ''), status, COUNT(*) AS count
            FROM files
            GROUP BY COALESCE(language, ''), status
            ORDER BY COALESCE(language, ''), status;
            """;
        var rows = new List<FileStatusGroup>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new FileStatusGroup(
                Language: reader.GetString(0),
                Status: reader.GetString(1),
                Count: Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    private static IReadOnlyList<StructuralFactGroup> ReadStructuralFacts(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT language, pattern_id, capture_name, COUNT(*) AS count
            FROM structural_facts
            GROUP BY language, pattern_id, capture_name
            ORDER BY language, pattern_id, capture_name;
            """;
        var rows = new List<StructuralFactGroup>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StructuralFactGroup(
                Language: reader.GetString(0),
                PatternId: reader.GetString(1),
                CaptureName: reader.GetString(2),
                Count: reader.GetInt64(3)));
        }

        return rows;
    }

    private static IReadOnlyList<ComplexityMetricGroup> ReadComplexityMetrics(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT language,
                   scope,
                   algorithm_id,
                   COUNT(*) AS count,
                   MAX(decision_count) AS max_decision_count,
                   MAX(loop_count) AS max_loop_count,
                   MAX(max_nesting_depth) AS max_nesting_depth,
                   MAX(parameter_count) AS max_parameter_count
            FROM complexity_metrics
            GROUP BY language, scope, algorithm_id
            ORDER BY language, scope, algorithm_id;
            """;
        var rows = new List<ComplexityMetricGroup>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ComplexityMetricGroup(
                Language: reader.GetString(0),
                Scope: reader.GetString(1),
                AlgorithmId: reader.GetString(2),
                Count: reader.GetInt64(3),
                MaxDecisionCount: reader.GetInt64(4),
                MaxLoopCount: reader.GetInt64(5),
                MaxNestingDepth: reader.GetInt64(6),
                MaxParameterCount: reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }

        return rows;
    }
}

public sealed record WorkspaceExtractionHealthFacts(
    HealthFactSection<ParseDiagnosticGroup> ParseDiagnostics,
    HealthFactSection<CapabilityGapGroup> CapabilityGaps,
    HealthFactSection<LanguageCapabilitySummary> LanguageCapabilities,
    HealthFactSection<StructuralFactGroup> StructuralFacts,
    HealthFactSection<ComplexityMetricGroup> ComplexityMetrics,
    HealthFactSection<FileStatusGroup> Files);

public sealed record HealthFactSection<T>(bool Available, IReadOnlyList<T> Rows, string? Error)
{
    public static HealthFactSection<T> FromRows(IReadOnlyList<T> rows) => new(true, rows, Error: null);

    public static HealthFactSection<T> Unavailable(string error) => new(false, Array.Empty<T>(), error);
}

public sealed record ParseDiagnosticGroup(string Language, string Kind, long Count);

public sealed record CapabilityGapGroup(string Language, string Capability, string Status, long Count);

public sealed record LanguageCapabilitySummary(
    string Language,
    long TargetSymbols,
    long ActualSymbols,
    long TargetRelationships,
    long ActualRelationships,
    long TargetPendingRelationships,
    long ActualPendingRelationships,
    long TargetIdentifiers,
    long ActualIdentifiers,
    long TargetTypes,
    long ActualTypes,
    IReadOnlyList<KindCoverageDomain> KindCoverage);

/// <summary>
/// One extraction domain from the artifact's per-language kind_coverage depth contract
/// (v2.3.0 carries ten domains: symbols, relationships, identifiers, body_spans, annotations,
/// doc_comments, literals, source_regions, structural_facts, complexity_metrics).
/// </summary>
public sealed record KindCoverageDomain(
    string Domain,
    IReadOnlyList<string> Supported,
    IReadOnlyList<string> OpenGaps,
    IReadOnlyList<string> NotApplicable);

public sealed record FileStatusGroup(string Language, string Status, long Count);

public sealed record StructuralFactGroup(string Language, string PatternId, string CaptureName, long Count);

public sealed record ComplexityMetricGroup(
    string Language,
    string Scope,
    string AlgorithmId,
    long Count,
    long MaxDecisionCount,
    long MaxLoopCount,
    long MaxNestingDepth,
    long? MaxParameterCount);
