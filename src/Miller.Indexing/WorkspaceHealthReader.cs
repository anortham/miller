using System.Globalization;
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
                   target_types, actual_types
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
                ActualTypes: reader.GetInt64(10)));
        }

        return rows;
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
}

public sealed record WorkspaceExtractionHealthFacts(
    HealthFactSection<ParseDiagnosticGroup> ParseDiagnostics,
    HealthFactSection<CapabilityGapGroup> CapabilityGaps,
    HealthFactSection<LanguageCapabilitySummary> LanguageCapabilities,
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
    long ActualTypes);

public sealed record FileStatusGroup(string Language, string Status, long Count);
