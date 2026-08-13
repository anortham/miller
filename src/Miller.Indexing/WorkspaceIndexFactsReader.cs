using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

namespace Miller.Indexing;

/// <summary>
/// Cheap metadata reader for status/dashboard surfaces. It intentionally does not hydrate symbols, edges, bridge
/// data, or BM25 structures; read tools use <see cref="RepositoryIndexLoader"/> when they need the full index.
/// </summary>
public static class WorkspaceIndexFactsReader
{
    public static WorkspaceIndexFacts Read(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        using SqliteConnection connection = SqliteReadOnlyAccess.Open(dbPath);
        return ReadConnection(connection);
    }

    public static WorkspaceIndexFacts ReadSession(IWorkspaceReadSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Read(ReadConnection);
    }

    /// <summary>Bounded scalar counts for report/status surfaces; does not hydrate symbols.</summary>
    public static WorkspaceSymbolCounts ReadSymbolCounts(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        using SqliteConnection connection = SqliteReadOnlyAccess.Open(dbPath);
        return ReadSymbolCounts(connection);
    }

    public static WorkspaceSymbolCounts ReadSymbolCounts(IWorkspaceReadSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Read(ReadSymbolCounts);
    }

    private static WorkspaceSymbolCounts ReadSymbolCounts(SqliteConnection connection)
    {
        JulieSchemaGate.Verify(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COUNT(DISTINCT path), COUNT(DISTINCT language)
            FROM symbols WHERE name IS NOT NULL;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();
        return new WorkspaceSymbolCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    /// <summary>
    /// The distinct-extension count on its own, for a caller that already has the symbol counts and only needs
    /// to refresh this when the file set actually changed.
    /// </summary>
    /// <remarks>
    /// This is the expensive half of <see cref="ReadSession"/>: it streams every symbol-bearing path
    /// (92-97 ms against a 226k-symbol store) where the counts query is a single 35-38 ms statement. The
    /// freshness swap ran both on every poll, in every process — ~145 ms twice a second for a store that had
    /// not changed (2026-08-12 triage).
    /// </remarks>
    public static int ReadKnownExtensionsCount(IWorkspaceReadSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Read(ReadKnownExtensionsCount);
    }

    private static long ReadDocumentCount(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbols WHERE name IS NOT NULL;";
        object? result = command.ExecuteScalar();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private static WorkspaceIndexFacts ReadConnection(SqliteConnection connection)
    {
        JulieSchemaGate.Verify(connection);
        return new WorkspaceIndexFacts(
            ReadDocumentCount(connection),
            ReadKnownExtensionsCount(connection));
    }

    private static int ReadKnownExtensionsCount(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        // v1: symbols.file_path → path. By-name read (D6).
        command.CommandText = "SELECT DISTINCT path FROM symbols WHERE name IS NOT NULL;";

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using SqliteDataReader reader = command.ExecuteReader();
        int oPath = reader.GetOrdinal("path");
        while (reader.Read())
        {
            string fileName = LastPathSegment(reader.GetString(oPath));
            string extension = Path.GetExtension(fileName);
            if (extension.Length > 1)
                extensions.Add(extension);
        }

        return extensions.Count;
    }

    private static string LastPathSegment(string path)
    {
        int slash = path.LastIndexOfAny(SeparatorChars);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static readonly char[] SeparatorChars = { '/', '\\' };
}

public sealed record WorkspaceIndexFacts(long DocumentCount, int KnownExtensionsCount);

public sealed record WorkspaceSymbolCounts(long Symbols, long Files, long Languages);
