using Microsoft.Data.Sqlite;
using Miller.Core.References;

namespace Miller.Indexing;

/// <summary>Reads bounded, normalized reference evidence keyed by resolved symbol IDs.</summary>
public static class ReferenceEvidenceReader
{
    private static readonly string[] RequiredResolutionTables =
        ["identifier_resolutions", "pending_resolutions", "pending_relationships"];

    /// <summary>Read exact inbound sites and separately typed fallback candidates for one symbol.</summary>
    public static ReferenceEvidenceSet Read(
        string dbPath,
        string targetSymbolId,
        ReferenceEvidenceBounds bounds) =>
        Read(dbPath, targetSymbolId, new ReferenceEvidenceQuery(bounds));

    /// <summary>Read one filtered, stateless inbound evidence page.</summary>
    public static ReferenceEvidenceSet Read(
        string dbPath,
        string targetSymbolId,
        ReferenceEvidenceQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSymbolId);
        query.Validate();

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);

        string targetName = ReadTargetName(connection, targetSymbolId);
        var exactRows = FilterKind(ReadExact(connection, targetSymbolId), query.Kind);
        var exact = Deduplicate(exactRows);
        int exactAvailable = exact.Count;
        var boundedExact = exact.Skip(query.ExactOffset).Take(query.Bounds.ExactLimit).ToArray();

        int sameNameDefinitionCount = CountDefinitions(connection, targetName);
        var fallbackRows = FilterKind(ReadFallback(connection, targetSymbolId, targetName), query.Kind);
        var fallbackCandidates = Deduplicate(fallbackRows);
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
            fallback = fallbackCandidates
                .Skip(query.FallbackOffset)
                .Take(query.Bounds.FallbackLimit)
                .ToArray();
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
                exactAvailable > query.ExactOffset + boundedExact.Length,
                fallbackStatus == ReferenceFallbackStatus.Available &&
                fallbackAvailable > query.FallbackOffset + fallback.Count,
                fallbackStatus),
            ReadSnapshot(connection))
        {
            ExactCallerSymbolIds = ExactContainingSymbolIds(exact, callLike: true),
            ExactReferencedBySymbolIds = ExactContainingSymbolIds(exact, callLike: false),
        };
    }

    /// <summary>Read resolved outgoing sites and separately typed unresolved fallbacks for one symbol.</summary>
    public static OutgoingReferenceEvidenceSet ReadOutgoing(
        string dbPath,
        string containingSymbolId,
        ReferenceEvidenceBounds bounds) =>
        ReadOutgoing(dbPath, containingSymbolId, new ReferenceEvidenceQuery(bounds));

    /// <summary>Read one filtered, stateless outgoing evidence page.</summary>
    public static OutgoingReferenceEvidenceSet ReadOutgoing(
        string dbPath,
        string containingSymbolId,
        ReferenceEvidenceQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(containingSymbolId);
        query.Validate();

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);
        RequireSymbol(connection, containingSymbolId);

        var exactRows = FilterOutgoingKind(ReadOutgoingExact(connection, containingSymbolId), query.Kind);
        var exact = DeduplicateOutgoing(exactRows);
        var fallbackRows = FilterOutgoingKind(ReadOutgoingFallback(connection, containingSymbolId), query.Kind);
        var fallback = DeduplicateOutgoing(fallbackRows);
        var boundedExact = exact.Skip(query.ExactOffset).Take(query.Bounds.ExactLimit).ToArray();
        var boundedFallback = fallback
            .Skip(query.FallbackOffset)
            .Take(query.Bounds.FallbackLimit)
            .ToArray();

        return new OutgoingReferenceEvidenceSet(
            boundedExact,
            boundedFallback,
            new OutgoingReferenceEvidenceCoverage(
                exactRows.Count,
                exact.Count,
                boundedExact.Length,
                fallback.Count,
                boundedFallback.Length,
                exact.Count > query.ExactOffset + boundedExact.Length,
                fallback.Count > query.FallbackOffset + boundedFallback.Length),
            ReadSnapshot(connection));
    }

    /// <summary>Read the current extractor artifact identity used by stateless reference continuations.</summary>
    public static ReferenceEvidenceSnapshot ReadSnapshot(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);
        return ReadSnapshot(connection);
    }

    private static ReferenceEvidenceSnapshot ReadSnapshot(SqliteConnection connection)
    {
        using var artifact = connection.CreateCommand();
        artifact.CommandText =
            "SELECT value FROM artifact_metadata WHERE key = 'artifact_id' LIMIT 1;";
        string artifactId = artifact.ExecuteScalar() as string
            ?? throw new IncompatibleExtractException(
                "Reference evidence requires artifact_metadata.artifact_id.");

        using var revision = connection.CreateCommand();
        revision.CommandText = "SELECT COALESCE(MAX(revision_id), 0) FROM extraction_revisions;";
        long revisionId = Convert.ToInt64(
            revision.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
        return new ReferenceEvidenceSnapshot(artifactId, revisionId);
    }

    private static string ReadTargetName(SqliteConnection connection, string targetSymbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM symbols WHERE symbol_id = $target;";
        command.Parameters.AddWithValue("$target", targetSymbolId);
        return command.ExecuteScalar() as string
            ?? throw new ArgumentException($"Unknown symbol ID '{targetSymbolId}'.", nameof(targetSymbolId));
    }

    private static void RequireSymbol(SqliteConnection connection, string symbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM symbols WHERE symbol_id = $symbol;";
        command.Parameters.AddWithValue("$symbol", symbolId);
        if (command.ExecuteScalar() is null)
            throw new ArgumentException($"Unknown symbol ID '{symbolId}'.", nameof(symbolId));
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
        string relationshipArm = HasTable(connection, "relationships")
            ? """
              UNION ALL
              SELECT r.from_symbol_id, r.path, r.start_line, r.start_column, r.end_line, r.end_column,
                     r.start_byte, r.end_byte, r.kind, r.confidence, 'relationship' AS source,
                     NULL AS tier, f.language
              FROM relationships r
              LEFT JOIN files f ON f.file_id = r.file_id
              WHERE r.to_symbol_id = $target
              """
            : string.Empty;
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.containing_symbol_id, i.path, i.start_line, i.start_column, i.end_line, i.end_column,
                   i.start_byte, i.end_byte, i.kind, i.confidence, 'identifier_direct' AS source,
                   NULL AS tier, i.language
            FROM identifiers i
            WHERE i.target_symbol_id = $target
            UNION ALL
            SELECT i.containing_symbol_id, i.path, i.start_line, i.start_column, i.end_line, i.end_column,
                   i.start_byte, i.end_byte, i.kind, COALESCE(ir.confidence, i.confidence),
                   'identifier_resolution' AS source, ir.tier, i.language
            FROM identifier_resolutions ir
            JOIN identifiers i ON i.identifier_id = ir.identifier_id
            WHERE ir.target_symbol_id = $target
              AND (i.target_symbol_id IS NULL OR i.target_symbol_id = ir.target_symbol_id)
            {relationshipArm}
            UNION ALL
            SELECT COALESCE(p.caller_scope_symbol_id, p.from_symbol_id), p.path,
                   p.start_line, p.start_column, p.end_line, p.end_column,
                   p.start_byte, p.end_byte, p.kind, MIN(p.confidence, pr.confidence),
                   'pending_resolution' AS source, pr.tier, f.language
            FROM pending_resolutions pr
            JOIN pending_relationships p ON p.pending_relationship_id = pr.pending_relationship_id
            JOIN files f ON f.file_id = p.file_id
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
                   NULL AS tier, i.language
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

    private static List<OutgoingReferenceEvidence> ReadOutgoingExact(
        SqliteConnection connection,
        string containingSymbolId)
    {
        string relationshipArm = HasTable(connection, "relationships")
            ? """
              UNION ALL
              SELECT r.to_symbol_id, target.name, r.path,
                     r.start_line, r.start_column, r.end_line, r.end_column,
                     r.start_byte, r.end_byte, r.kind, r.confidence,
                     'relationship' AS source, NULL AS tier, f.language
              FROM relationships r
              JOIN symbols target ON target.symbol_id = r.to_symbol_id
              LEFT JOIN files f ON f.file_id = r.file_id
              WHERE r.from_symbol_id = $containing
              """
            : string.Empty;
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.target_symbol_id, target.name, i.path,
                   i.start_line, i.start_column, i.end_line, i.end_column,
                   i.start_byte, i.end_byte, i.kind, i.confidence,
                   'identifier_direct' AS source, NULL AS tier, i.language
            FROM identifiers i
            JOIN symbols target ON target.symbol_id = i.target_symbol_id
            WHERE i.containing_symbol_id = $containing
            UNION ALL
            SELECT ir.target_symbol_id, target.name, i.path,
                   i.start_line, i.start_column, i.end_line, i.end_column,
                   i.start_byte, i.end_byte, i.kind, COALESCE(ir.confidence, i.confidence),
                   'identifier_resolution' AS source, ir.tier, i.language
            FROM identifier_resolutions ir
            JOIN identifiers i ON i.identifier_id = ir.identifier_id
            JOIN symbols target ON target.symbol_id = ir.target_symbol_id
            WHERE i.containing_symbol_id = $containing
              AND (i.target_symbol_id IS NULL OR i.target_symbol_id = ir.target_symbol_id)
            {relationshipArm}
            UNION ALL
            SELECT pr.target_symbol_id, target.name, p.path,
                   p.start_line, p.start_column, p.end_line, p.end_column,
                   p.start_byte, p.end_byte, p.kind, MIN(p.confidence, pr.confidence),
                   'pending_resolution' AS source, pr.tier, f.language
            FROM pending_resolutions pr
            JOIN pending_relationships p ON p.pending_relationship_id = pr.pending_relationship_id
            JOIN symbols target ON target.symbol_id = pr.target_symbol_id
            JOIN files f ON f.file_id = p.file_id
            WHERE COALESCE(p.caller_scope_symbol_id, p.from_symbol_id) = $containing
            ORDER BY 3, 8, 4, 10, 12, 2, 1;
            """;
        command.Parameters.AddWithValue("$containing", containingSymbolId);
        return ReadOutgoingRows(command, containingSymbolId, ReferenceResolutionStatus.Exact);
    }

    private static List<OutgoingReferenceEvidence> ReadOutgoingFallback(
        SqliteConnection connection,
        string containingSymbolId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT NULL AS target_symbol_id, i.name, i.path,
                   i.start_line, i.start_column, i.end_line, i.end_column,
                   i.start_byte, i.end_byte, i.kind, MIN(i.confidence, 0.5),
                   'name_fallback' AS source, NULL AS tier, i.language
            FROM identifiers i
            WHERE i.containing_symbol_id = $containing
              AND i.target_symbol_id IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM identifier_resolutions ir
                  WHERE ir.identifier_id = i.identifier_id
                    AND ir.target_symbol_id IS NOT NULL)
            UNION ALL
            SELECT NULL AS target_symbol_id, p.target_display_name, p.path,
                   p.start_line, p.start_column, p.end_line, p.end_column,
                   p.start_byte, p.end_byte, p.kind, MIN(p.confidence, 0.5),
                   'name_fallback' AS source, NULL AS tier, f.language
            FROM pending_relationships p
            JOIN files f ON f.file_id = p.file_id
            WHERE COALESCE(p.caller_scope_symbol_id, p.from_symbol_id) = $containing
              AND NOT EXISTS (
                  SELECT 1 FROM pending_resolutions pr
                  WHERE pr.pending_relationship_id = p.pending_relationship_id)
            ORDER BY 3, 8, 4, 10, 2;
            """;
        command.Parameters.AddWithValue("$containing", containingSymbolId);
        return ReadOutgoingRows(command, containingSymbolId, ReferenceResolutionStatus.Fallback);
    }

    private static bool HasTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table LIMIT 1;";
        command.Parameters.AddWithValue("$table", table);
        return command.ExecuteScalar() is not null;
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
            .ThenBy(row => row.ContainingSymbolId, StringComparer.Ordinal)
            .ThenBy(row => row.Kind)
            .ToList();

    private static List<ReferenceEvidence> FilterKind(
        List<ReferenceEvidence> rows,
        ReferenceKind? kind) =>
        kind is null ? rows : rows.Where(row => row.Kind == kind.Value).ToList();

    private static IReadOnlyList<string> ExactContainingSymbolIds(
        IReadOnlyList<ReferenceEvidence> rows,
        bool callLike) =>
        rows.Where(row =>
                row.ContainingSymbolId is not null &&
                IsCallLike(row.Kind) == callLike)
            .Select(row => row.ContainingSymbolId!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbolId => symbolId, StringComparer.Ordinal)
            .ToArray();

    private static bool IsCallLike(ReferenceKind kind) =>
        kind is ReferenceKind.Call or ReferenceKind.Instantiation;

    private static List<OutgoingReferenceEvidence> DeduplicateOutgoing(
        IEnumerable<OutgoingReferenceEvidence> rows) =>
        rows.GroupBy(OutgoingSiteKey)
            .Select(group => group
                .OrderBy(row => SourcePrecedence(row.Source))
                .ThenByDescending(row => row.Confidence)
                .First())
            .OrderBy(row => row.FilePath, StringComparer.Ordinal)
            .ThenBy(row => row.StartByte ?? long.MaxValue)
            .ThenBy(row => row.StartLine ?? int.MaxValue)
            .ThenBy(row => row.StartColumn ?? int.MaxValue)
            .ThenBy(row => row.Kind)
            .ThenBy(row => row.TargetName, StringComparer.Ordinal)
            .ThenBy(row => row.TargetSymbolId, StringComparer.Ordinal)
            .ToList();

    private static List<OutgoingReferenceEvidence> FilterOutgoingKind(
        List<OutgoingReferenceEvidence> rows,
        ReferenceKind? kind) =>
        kind is null ? rows : rows.Where(row => row.Kind == kind.Value).ToList();

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

    private static OutgoingReferenceSiteKey OutgoingSiteKey(OutgoingReferenceEvidence row) =>
        row.StartByte is not null
            ? new(
                row.FilePath,
                row.StartByte,
                row.EndByte,
                null,
                null,
                row.Kind,
                row.TargetSymbolId,
                row.TargetName)
            : new(
                row.FilePath,
                null,
                null,
                row.StartLine,
                row.StartColumn.GetValueOrDefault(),
                row.Kind,
                row.TargetSymbolId,
                row.TargetName);

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
                resolutionStatus == ReferenceResolutionStatus.Exact ? targetSymbolId : null,
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
                resolutionStatus,
                ReadString(reader, 12)));
        }

        return rows;
    }

    private static List<OutgoingReferenceEvidence> ReadOutgoingRows(
        SqliteCommand command,
        string containingSymbolId,
        ReferenceResolutionStatus resolutionStatus)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<OutgoingReferenceEvidence>();
        while (reader.Read())
        {
            string sourceKind = reader.GetString(9);
            rows.Add(new OutgoingReferenceEvidence(
                containingSymbolId,
                ReadString(reader, 0),
                reader.GetString(1),
                reader.GetString(2),
                ReadInt32(reader, 3),
                ReadInt32(reader, 4),
                ReadInt32(reader, 5),
                ReadInt32(reader, 6),
                ReadInt64(reader, 7),
                ReadInt64(reader, 8),
                NormalizeKind(sourceKind),
                sourceKind,
                ParseSource(reader.GetString(11)),
                ReadInt32(reader, 12),
                reader.GetDouble(10),
                resolutionStatus,
                ReadString(reader, 13)));
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

    public static ReferenceKind NormalizeKind(string kind) => kind switch
    {
        "call" or "calls" => ReferenceKind.Call,
        "type_usage" => ReferenceKind.TypeUsage,
        "member_access" => ReferenceKind.MemberAccess,
        "variable_ref" => ReferenceKind.VariableReference,
        "instantiates" => ReferenceKind.Instantiation,
        "extends" => ReferenceKind.Inheritance,
        "implements" => ReferenceKind.Implementation,
        "import" or "imports" => ReferenceKind.Import,
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

    private readonly record struct OutgoingReferenceSiteKey(
        string FilePath,
        long? StartByte,
        long? EndByte,
        int? StartLine,
        int? StartColumn,
        ReferenceKind Kind,
        string? TargetSymbolId,
        string TargetName);
}
