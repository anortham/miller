using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

namespace Miller.Indexing;

/// <summary>
/// Read layer for julie's <c>source_regions</c> table. It keeps region text out of SQLite: callers slice bytes
/// from disk using the returned UTF-8 spans and joined file freshness facts.
/// </summary>
public static class SqliteSourceRegionReader
{
    /// <summary>
    /// Bulk-read source regions that are eligible for v1 region text indexing. Embedded-language regions are
    /// intentionally skipped for now because their spans can be large and need separate sizing policy.
    /// </summary>
    public static IReadOnlyList<SourceRegionRow> ReadIndexedRegions(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sr.source_region_id, sr.file_id, sr.path, sr.language, sr.kind,
                   sr.containing_symbol_id, sr.start_line, sr.start_column, sr.end_line, sr.end_column,
                   sr.start_byte, sr.end_byte, sr.metadata_json,
                   f.content_hash, f.content_bytes, f.status
            FROM source_regions sr
            JOIN files f ON f.file_id = sr.file_id
            WHERE sr.kind IN ('comment', 'doc_comment', 'string_literal')
            ORDER BY sr.path, sr.start_byte, sr.source_region_id;
            """;

        var results = new List<SourceRegionRow>();
        using var reader = command.ExecuteReader();
        int oSourceRegionId = reader.GetOrdinal("source_region_id");
        int oFileId = reader.GetOrdinal("file_id");
        int oPath = reader.GetOrdinal("path");
        int oLanguage = reader.GetOrdinal("language");
        int oKind = reader.GetOrdinal("kind");
        int oContainingSymbolId = reader.GetOrdinal("containing_symbol_id");
        int oStartLine = reader.GetOrdinal("start_line");
        int oStartColumn = reader.GetOrdinal("start_column");
        int oEndLine = reader.GetOrdinal("end_line");
        int oEndColumn = reader.GetOrdinal("end_column");
        int oStartByte = reader.GetOrdinal("start_byte");
        int oEndByte = reader.GetOrdinal("end_byte");
        int oMetadataJson = reader.GetOrdinal("metadata_json");
        int oContentHash = reader.GetOrdinal("content_hash");
        int oContentBytes = reader.GetOrdinal("content_bytes");
        int oStatus = reader.GetOrdinal("status");

        while (reader.Read())
        {
            results.Add(new SourceRegionRow(
                SourceRegionId: reader.GetString(oSourceRegionId),
                FileId: reader.GetString(oFileId),
                Path: reader.GetString(oPath),
                Language: reader.GetString(oLanguage),
                Kind: reader.GetString(oKind),
                ContainingSymbolId: reader.IsDBNull(oContainingSymbolId) ? null : reader.GetString(oContainingSymbolId),
                StartLine: reader.GetInt32(oStartLine),
                StartColumn: reader.GetInt32(oStartColumn),
                EndLine: reader.GetInt32(oEndLine),
                EndColumn: reader.GetInt32(oEndColumn),
                StartByte: reader.GetInt32(oStartByte),
                EndByte: reader.GetInt32(oEndByte),
                MetadataJson: reader.IsDBNull(oMetadataJson) ? null : reader.GetString(oMetadataJson),
                ContentHash: reader.GetString(oContentHash),
                ContentBytes: reader.GetInt64(oContentBytes),
                Status: reader.GetString(oStatus)));
        }

        return results;
    }

    /// <summary>
    /// Return the requested symbol ids whose <c>symbols.doc_comment</c> is populated. This deliberately does not
    /// read <c>source_regions</c>; it is the cheap symbol-search annotation path.
    /// </summary>
    public static IReadOnlySet<string> ReadHasDocComment(string dbPath, IReadOnlyCollection<string> symbolIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(symbolIds);

        var requestedIds = symbolIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestedIds.Length == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        return ReadHasDocComment(connection, requestedIds);
    }

    public static IReadOnlySet<string> ReadHasDocComment(
        IWorkspaceReadSession session,
        IReadOnlyCollection<string> symbolIds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(symbolIds);
        string[] requestedIds = symbolIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestedIds.Length == 0)
            return new HashSet<string>(StringComparer.Ordinal);
        return session.Read(connection => ReadHasDocComment(connection, requestedIds));
    }

    private static IReadOnlySet<string> ReadHasDocComment(SqliteConnection connection, string[] requestedIds)
    {
        JulieSchemaGate.Verify(connection);

        using var command = connection.CreateCommand();
        var parameters = new string[requestedIds.Length];
        for (int i = 0; i < requestedIds.Length; i++)
        {
            parameters[i] = "$id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(parameters[i], requestedIds[i]);
        }

        command.CommandText = $"""
            SELECT symbol_id
            FROM symbols
            WHERE doc_comment IS NOT NULL
              AND symbol_id IN ({string.Join(", ", parameters)})
            ORDER BY symbol_id;
            """;

        var results = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));

        return results;
    }
}
