using Microsoft.Data.Sqlite;

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
        JulieSchemaGate.Verify(connection);

        long documentCount = ReadDocumentCount(connection);
        int knownExtensionsCount = ReadKnownExtensionsCount(connection);
        return new WorkspaceIndexFacts(documentCount, knownExtensionsCount);
    }

    private static long ReadDocumentCount(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbols WHERE name IS NOT NULL;";
        object? result = command.ExecuteScalar();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private static int ReadKnownExtensionsCount(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT file_path FROM symbols WHERE name IS NOT NULL;";

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string fileName = LastPathSegment(reader.GetString(0));
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
