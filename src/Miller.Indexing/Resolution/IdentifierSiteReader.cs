using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

namespace Miller.Indexing.Resolution;

internal sealed record IdentifierSite(
    long VersionId,
    long RowId,
    string IdentifierId,
    string Name,
    string Kind,
    string? Receiver,
    string? ReceiverQualifier,
    string? ReceiverType,
    string? ContainingSymbolId,
    double Confidence,
    long StartByte,
    long EndByte,
    long StartLine);

internal static class IdentifierSiteReader
{
    private const int IdChunkSize = 128;

    internal static IEnumerable<IdentifierSite> SitesNamed(
        SqliteConnection storeRead,
        StoreVisibility visibility,
        string name)
    {
        ArgumentNullException.ThrowIfNull(storeRead);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(name);
        using SqliteCommand command = storeRead.CreateCommand();
        command.CommandText =
            """
            SELECT i.version_id,i.rowid,i.identifier_id,i.name,i.kind,i.containing_symbol_id,
                   i.confidence,i.start_byte,i.end_byte,i.start_line,i.metadata_json
            FROM main.identifiers AS i
            JOIN main.manifest_entries AS e ON e.version_id=i.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation AND i.name=$name
            ORDER BY i.version_id,i.rowid
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$name", name);
        return ReadSites(command).ToArray();
    }

    internal static IEnumerable<IdentifierSite> SitesWithinSymbols(
        SqliteConnection storeRead,
        StoreVisibility visibility,
        IReadOnlyList<string> containingSymbolIds)
    {
        ArgumentNullException.ThrowIfNull(storeRead);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(containingSymbolIds);
        if (containingSymbolIds.Count == 0)
            return [];

        var rows = new List<IdentifierSite>();
        foreach (string[] chunk in Chunk(containingSymbolIds, IdChunkSize))
        {
            using SqliteCommand command = storeRead.CreateCommand();
            command.CommandText =
                $"""
                SELECT i.version_id,i.rowid,i.identifier_id,i.name,i.kind,i.containing_symbol_id,
                       i.confidence,i.start_byte,i.end_byte,i.start_line,i.metadata_json
                FROM main.identifiers AS i
                JOIN main.manifest_entries AS e ON e.version_id=i.version_id
                WHERE e.view_id=$view_id AND e.generation=$generation
                  AND i.containing_symbol_id IN ({Placeholders(chunk.Length)})
                ORDER BY i.version_id,i.rowid
                """;
            command.Parameters.AddWithValue("$view_id", visibility.ViewId);
            command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
            BindIds(command, chunk);
            rows.AddRange(ReadSites(command));
        }

        return rows;
    }

    internal static IEnumerable<IdentifierSite> SitesAll(
        SqliteConnection storeRead,
        StoreVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(storeRead);
        ArgumentNullException.ThrowIfNull(visibility);
        using SqliteCommand command = storeRead.CreateCommand();
        command.CommandText =
            """
            SELECT i.version_id,i.rowid,i.identifier_id,i.name,i.kind,i.containing_symbol_id,
                   i.confidence,i.start_byte,i.end_byte,i.start_line,i.metadata_json
            FROM main.identifiers AS i
            JOIN main.manifest_entries AS e ON e.version_id=i.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            ORDER BY i.version_id,i.rowid
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        return ReadSites(command).ToArray();
    }

    internal static IEnumerable<IdentifierSite> SitesNamed(SqliteConnection artifactRead, string name)
    {
        ArgumentNullException.ThrowIfNull(artifactRead);
        ArgumentNullException.ThrowIfNull(name);
        using SqliteCommand command = artifactRead.CreateCommand();
        command.CommandText =
            """
            SELECT f.rowid,i.rowid,i.identifier_id,i.name,i.kind,i.containing_symbol_id,
                   i.confidence,i.start_byte,i.end_byte,i.start_line,i.metadata_json
            FROM identifiers AS i
            JOIN files AS f ON f.file_id=i.file_id
            WHERE i.name=$name
            ORDER BY 1,i.rowid
            """;
        command.Parameters.AddWithValue("$name", name);
        return ReadSites(command).ToArray();
    }

    internal static IEnumerable<IdentifierSite> SitesAll(SqliteConnection artifactRead)
    {
        ArgumentNullException.ThrowIfNull(artifactRead);
        using SqliteCommand command = artifactRead.CreateCommand();
        command.CommandText =
            """
            SELECT f.rowid,i.rowid,i.identifier_id,i.name,i.kind,i.containing_symbol_id,
                   i.confidence,i.start_byte,i.end_byte,i.start_line,i.metadata_json
            FROM identifiers AS i
            JOIN files AS f ON f.file_id=i.file_id
            ORDER BY 1,i.rowid
            """;
        return ReadSites(command).ToArray();
    }

    internal static IEnumerable<IdentifierSite> SitesWithinSymbols(
        SqliteConnection artifactRead,
        IReadOnlyList<string> containingSymbolIds)
    {
        ArgumentNullException.ThrowIfNull(artifactRead);
        ArgumentNullException.ThrowIfNull(containingSymbolIds);
        if (containingSymbolIds.Count == 0)
            return [];

        var rows = new List<IdentifierSite>();
        foreach (string[] chunk in Chunk(containingSymbolIds, IdChunkSize))
        {
            using SqliteCommand command = artifactRead.CreateCommand();
            command.CommandText =
                $"""
                SELECT f.rowid,i.rowid,i.identifier_id,i.name,i.kind,i.containing_symbol_id,
                       i.confidence,i.start_byte,i.end_byte,i.start_line,i.metadata_json
                FROM identifiers AS i
                JOIN files AS f ON f.file_id=i.file_id
                WHERE i.containing_symbol_id IN ({Placeholders(chunk.Length)})
                ORDER BY 1,i.rowid
                """;
            BindIds(command, chunk);
            rows.AddRange(ReadSites(command));
        }

        return rows;
    }

    private static List<IdentifierSite> ReadSites(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<IdentifierSite>();
        while (reader.Read())
        {
            string? metadata = reader.IsDBNull(10) ? null : reader.GetString(10);
            (string? receiver, string? qualifier, string? receiverType) = FactMetadataParser.IdentifierReceivers(metadata);
            rows.Add(new IdentifierSite(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                receiver,
                qualifier,
                receiverType,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? 1.0 : reader.GetDouble(6),
                reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                reader.IsDBNull(9) ? 0 : reader.GetInt64(9)));
        }

        return rows;
    }

    private static IEnumerable<string[]> Chunk(IReadOnlyList<string> ids, int size)
    {
        for (int offset = 0; offset < ids.Count; offset += size)
        {
            int take = Math.Min(size, ids.Count - offset);
            var chunk = new string[take];
            for (int i = 0; i < take; i++)
                chunk[i] = ids[offset + i];
            yield return chunk;
        }
    }

    private static string Placeholders(int count)
    {
        var parts = new string[count];
        for (int i = 0; i < count; i++)
            parts[i] = "$id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(',', parts);
    }

    private static void BindIds(SqliteCommand command, string[] ids)
    {
        for (int i = 0; i < ids.Length; i++)
            command.Parameters.AddWithValue("$id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), ids[i]);
    }
}
