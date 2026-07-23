using Microsoft.Data.Sqlite;
using Miller.Core.References;

namespace Miller.Indexing;

/// <summary>Reads bounded, normalized inbound reference evidence for a resolved symbol ID.</summary>
public static class ReferenceEvidenceReader
{
    private static readonly string[] RequiredResolutionTables =
        ["identifier_resolutions", "pending_resolutions", "pending_relationships"];

    /// <summary>Read exact inbound sites and separately typed fallback candidates for one symbol.</summary>
    public static ReferenceEvidenceSet Read(
        string dbPath,
        string targetSymbolId,
        ReferenceEvidenceBounds bounds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSymbolId);
        bounds.Validate();

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);

        string targetName = ReadTargetName(connection, targetSymbolId);
        var exactRows = ReadExact(connection, targetSymbolId);
        var exact = Deduplicate(exactRows);
        int exactAvailable = exact.Count;
        var boundedExact = exact.Take(bounds.ExactLimit).ToArray();

        int sameNameDefinitionCount = CountDefinitions(connection, targetName);
        var fallbackCandidates = Deduplicate(ReadFallback(connection, targetSymbolId, targetName));
        IReadOnlyList<ReferenceEvidence> fallback;
        int fallbackAvailable = fallbackCandidates.Count;
        ReferenceFallbackStatus fallbackStatus;
        if (sameNameDefinitionCount > 1)
        {
            fallback = Array.Empty<ReferenceEvidence>();
            fallbackStatus = ReferenceFallbackStatus.SuppressedAmbiguousName;
        }
        else
        {
            fallback = fallbackCandidates.Take(bounds.FallbackLimit).ToArray();
            fallbackStatus = fallbackCandidates.Count == 0
                ? ReferenceFallbackStatus.NoCandidates
                : ReferenceFallbackStatus.Available;
        }

        return new ReferenceEvidenceSet(
            boundedExact,
            fallback,
            new ReferenceEvidenceCoverage(
                exactRows.Count,
                exactAvailable,
                boundedExact.Length,
                fallbackAvailable,
                fallback.Count,
                sameNameDefinitionCount,
                exactAvailable > boundedExact.Length,
                fallbackStatus == ReferenceFallbackStatus.Available && fallbackAvailable > fallback.Count,
                fallbackStatus));
    }

    private static string ReadTargetName(SqliteConnection connection, string targetSymbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM symbols WHERE symbol_id = $target;";
        command.Parameters.AddWithValue("$target", targetSymbolId);
        return command.ExecuteScalar() as string
            ?? throw new ArgumentException($"Unknown symbol ID '{targetSymbolId}'.", nameof(targetSymbolId));
    }

    private static void RequireResolutionTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name = $name;
            """;
        var name = command.Parameters.Add("$name", SqliteType.Text);
        foreach (string table in RequiredResolutionTables)
        {
            name.Value = table;
            if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
                throw new IncompatibleExtractException(
                    $"Reference evidence requires the '{table}' table. Restore the pinned julie-extract artifact.");
        }
    }

    private static int CountDefinitions(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbols WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<ReferenceEvidence> ReadExact(SqliteConnection connection, string targetSymbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.containing_symbol_id, i.path, i.start_line, i.start_column, i.end_line, i.end_column,
                   i.start_byte, i.end_byte, i.kind, i.confidence, 'identifier_direct' AS source,
                   NULL AS tier
            FROM identifiers i
            WHERE i.target_symbol_id = $target
            UNION ALL
            SELECT i.containing_symbol_id, i.path, i.start_line, i.start_column, i.end_line, i.end_column,
                   i.start_byte, i.end_byte, i.kind, COALESCE(ir.confidence, i.confidence),
                   'identifier_resolution' AS source, ir.tier
            FROM identifier_resolutions ir
            JOIN identifiers i ON i.identifier_id = ir.identifier_id
            WHERE ir.target_symbol_id = $target
              AND (i.target_symbol_id IS NULL OR i.target_symbol_id = ir.target_symbol_id)
            UNION ALL
            SELECT r.from_symbol_id, r.path, r.start_line, r.start_column, r.end_line, r.end_column,
                   r.start_byte, r.end_byte, r.kind, r.confidence, 'relationship' AS source,
                   NULL AS tier
            FROM relationships r
            WHERE r.to_symbol_id = $target
            UNION ALL
            SELECT COALESCE(p.caller_scope_symbol_id, p.from_symbol_id), p.path,
                   p.start_line, p.start_column, p.end_line, p.end_column,
                   p.start_byte, p.end_byte, p.kind, MIN(p.confidence, pr.confidence),
                   'pending_resolution' AS source, pr.tier
            FROM pending_resolutions pr
            JOIN pending_relationships p ON p.pending_relationship_id = pr.pending_relationship_id
            WHERE pr.target_symbol_id = $target
            ORDER BY 2, 7, 3, 9, 11;
            """;
        command.Parameters.AddWithValue("$target", targetSymbolId);
        return ReadRows(command, targetSymbolId, ReferenceResolutionStatus.Exact);
    }

    private static List<ReferenceEvidence> ReadFallback(
        SqliteConnection connection,
        string targetSymbolId,
        string targetName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.containing_symbol_id, i.path, i.start_line, i.start_column, i.end_line, i.end_column,
                   i.start_byte, i.end_byte, i.kind, MIN(i.confidence, 0.5), 'name_fallback' AS source,
                   NULL AS tier
            FROM identifiers i
            WHERE i.name = $name
              AND i.target_symbol_id IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM identifier_resolutions ir
                  WHERE ir.identifier_id = i.identifier_id
                    AND ir.target_symbol_id IS NOT NULL)
            ORDER BY i.path, i.start_byte, i.start_line, i.kind, i.identifier_id;
            """;
        command.Parameters.AddWithValue("$name", targetName);
        return ReadRows(command, targetSymbolId, ReferenceResolutionStatus.Fallback);
    }

    private static List<ReferenceEvidence> Deduplicate(IEnumerable<ReferenceEvidence> rows) =>
        rows.GroupBy(SiteKey)
            .Select(group => group
                .OrderBy(row => SourcePrecedence(row.Source))
                .ThenByDescending(row => row.Confidence)
                .First())
            .OrderBy(row => row.FilePath, StringComparer.Ordinal)
            .ThenBy(row => row.StartByte ?? long.MaxValue)
            .ThenBy(row => row.StartLine ?? int.MaxValue)
            .ThenBy(row => row.StartColumn ?? int.MaxValue)
            .ThenBy(row => row.Kind)
            .ToList();

    private static ReferenceSiteKey SiteKey(ReferenceEvidence row) =>
        row.StartByte is not null
            ? new(row.FilePath, null, row.StartByte, row.EndByte, null, null, null, null, row.Kind)
            : new(
                row.FilePath,
                row.ContainingSymbolId,
                null,
                null,
                row.StartLine,
                row.StartColumn.GetValueOrDefault(),
                null,
                null,
                row.Kind);

    private static int SourcePrecedence(ReferenceEvidenceSource source) => source switch
    {
        ReferenceEvidenceSource.IdentifierDirect => 0,
        ReferenceEvidenceSource.IdentifierResolution => 1,
        ReferenceEvidenceSource.Relationship => 2,
        ReferenceEvidenceSource.PendingResolution => 3,
        ReferenceEvidenceSource.NameFallback => 4,
        _ => int.MaxValue,
    };

    private static List<ReferenceEvidence> ReadRows(
        SqliteCommand command,
        string targetSymbolId,
        ReferenceResolutionStatus resolutionStatus)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<ReferenceEvidence>();
        while (reader.Read())
        {
            string sourceKind = reader.GetString(8);
            string source = reader.GetString(10);
            rows.Add(new ReferenceEvidence(
                targetSymbolId,
                ReadString(reader, 0),
                reader.GetString(1),
                ReadInt32(reader, 2),
                ReadInt32(reader, 3),
                ReadInt32(reader, 4),
                ReadInt32(reader, 5),
                ReadInt64(reader, 6),
                ReadInt64(reader, 7),
                NormalizeKind(sourceKind),
                sourceKind,
                ParseSource(source),
                ReadInt32(reader, 11),
                reader.GetDouble(9),
                resolutionStatus));
        }

        return rows;
    }

    private static ReferenceEvidenceSource ParseSource(string source) => source switch
    {
        "identifier_direct" => ReferenceEvidenceSource.IdentifierDirect,
        "identifier_resolution" => ReferenceEvidenceSource.IdentifierResolution,
        "relationship" => ReferenceEvidenceSource.Relationship,
        "pending_resolution" => ReferenceEvidenceSource.PendingResolution,
        "name_fallback" => ReferenceEvidenceSource.NameFallback,
        _ => throw new InvalidOperationException($"Unknown reference evidence source '{source}'."),
    };

    internal static ReferenceKind NormalizeKind(string kind) => kind switch
    {
        "call" or "calls" => ReferenceKind.Call,
        "type_usage" => ReferenceKind.TypeUsage,
        "member_access" => ReferenceKind.MemberAccess,
        "variable_ref" => ReferenceKind.VariableReference,
        "instantiates" => ReferenceKind.Instantiation,
        "extends" => ReferenceKind.Inheritance,
        "implements" => ReferenceKind.Implementation,
        "imports" => ReferenceKind.Import,
        "references" => ReferenceKind.Reference,
        "uses" => ReferenceKind.Usage,
        _ => ReferenceKind.Unknown,
    };

    private static string? ReadString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? ReadInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long? ReadInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private readonly record struct ReferenceSiteKey(
        string FilePath,
        string? ContainingSymbolId,
        long? StartByte,
        long? EndByte,
        int? StartLine,
        int? StartColumn,
        int? EndLine,
        int? EndColumn,
        ReferenceKind Kind);
}
