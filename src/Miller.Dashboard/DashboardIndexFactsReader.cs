using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;

namespace Miller.Dashboard;

public static class DashboardIndexFactsReader
{
    public static IReadOnlyList<DashboardWorkspaceFacts> Read(IReadOnlyList<DashboardWorkspaceRow> workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        var facts = new List<DashboardWorkspaceFacts>(workspaces.Count);
        foreach (DashboardWorkspaceRow workspace in workspaces)
        {
            facts.Add(Read(workspace));
        }

        return facts;
    }

    public static DashboardWorkspaceFacts Read(DashboardWorkspaceRow workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!File.Exists(workspace.IndexDbPath))
        {
            return Empty(
                workspace,
                "missing",
                $"Index DB not found: {workspace.IndexDbPath}",
                searchSidecarStatus: "unknown");
        }

        try
        {
            using SqliteConnection connection = OpenReadOnly(workspace.IndexDbPath);
            if (!TableExists(connection, "files") || !TableExists(connection, "symbols"))
            {
                return Empty(
                    workspace,
                    "unreadable",
                    "Index DB does not contain julie files and symbols tables.",
                    searchSidecarStatus: ReadSearchSidecarStatus(workspace));
            }

            FileFacts fileFacts = ReadFileFacts(connection);
            Dictionary<string, long> symbolCountsByLanguage = ReadSymbolCountsByLanguage(connection);
            IReadOnlyList<DashboardSymbolKindStat> symbolKinds = ReadSymbolKinds(connection);
            IReadOnlyList<DashboardLanguageStat> languages = BuildLanguageStats(
                fileFacts.Languages,
                symbolCountsByLanguage);
            long symbolCount = symbolCountsByLanguage.Values.Sum();

            return new DashboardWorkspaceFacts(
                workspace.WorkspaceId,
                workspace.DisplayId,
                workspace.CanonicalRoot,
                workspace.IndexDbPath,
                workspace.State,
                null,
                fileFacts.FileCount,
                symbolCount,
                languages.Count,
                fileFacts.ContentBytes,
                workspace.LastRevision,
                workspace.LastScanAt,
                ReadSearchSidecarStatus(workspace),
                languages,
                symbolKinds);
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
        {
            return Empty(workspace, "unreadable", ex.Message, searchSidecarStatus: "unknown");
        }
    }

    private static DashboardWorkspaceFacts Empty(
        DashboardWorkspaceRow workspace,
        string status,
        string? message,
        string searchSidecarStatus) =>
        new(
            workspace.WorkspaceId,
            workspace.DisplayId,
            workspace.CanonicalRoot,
            workspace.IndexDbPath,
            status,
            message,
            FileCount: 0,
            SymbolCount: 0,
            LanguageCount: 0,
            ContentBytes: 0,
            workspace.LastRevision,
            workspace.LastScanAt,
            searchSidecarStatus,
            Array.Empty<DashboardLanguageStat>(),
            Array.Empty<DashboardSymbolKindStat>());

    private static FileFacts ReadFileFacts(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(NULLIF(language, ''), 'unknown') AS language,
                   COUNT(*) AS files,
                   COALESCE(SUM(content_bytes), 0) AS content_bytes
            FROM files
            GROUP BY COALESCE(NULLIF(language, ''), 'unknown')
            ORDER BY files DESC, language COLLATE NOCASE, language;
            """;
        using SqliteDataReader reader = cmd.ExecuteReader();
        var languages = new Dictionary<string, FileLanguageFacts>(StringComparer.OrdinalIgnoreCase);
        long fileCount = 0;
        long contentBytes = 0;
        while (reader.Read())
        {
            string language = reader.GetString(0);
            long files = reader.GetInt64(1);
            long bytes = reader.GetInt64(2);
            languages[language] = new FileLanguageFacts(files, bytes);
            fileCount += files;
            contentBytes += bytes;
        }

        return new FileFacts(fileCount, contentBytes, languages);
    }

    private static Dictionary<string, long> ReadSymbolCountsByLanguage(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(NULLIF(language, ''), 'unknown') AS language,
                   COUNT(*) AS symbols
            FROM symbols
            WHERE name IS NOT NULL
            GROUP BY COALESCE(NULLIF(language, ''), 'unknown');
            """;
        using SqliteDataReader reader = cmd.ExecuteReader();
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return counts;
    }

    private static IReadOnlyList<DashboardSymbolKindStat> ReadSymbolKinds(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(NULLIF(kind, ''), 'unknown') AS kind,
                   COUNT(*) AS symbols
            FROM symbols
            WHERE name IS NOT NULL
            GROUP BY COALESCE(NULLIF(kind, ''), 'unknown')
            ORDER BY symbols DESC, kind COLLATE NOCASE, kind
            LIMIT 12;
            """;
        using SqliteDataReader reader = cmd.ExecuteReader();
        var kinds = new List<DashboardSymbolKindStat>();
        while (reader.Read())
        {
            kinds.Add(new DashboardSymbolKindStat(reader.GetString(0), reader.GetInt64(1)));
        }

        return kinds;
    }

    private static IReadOnlyList<DashboardLanguageStat> BuildLanguageStats(
        IReadOnlyDictionary<string, FileLanguageFacts> fileFacts,
        IReadOnlyDictionary<string, long> symbolCountsByLanguage)
    {
        var names = new SortedSet<string>(fileFacts.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string language in symbolCountsByLanguage.Keys)
        {
            names.Add(language);
        }

        return names
            .Select(language =>
            {
                fileFacts.TryGetValue(language, out FileLanguageFacts files);
                symbolCountsByLanguage.TryGetValue(language, out long symbols);
                return new DashboardLanguageStat(language, files.FileCount, symbols, files.ContentBytes);
            })
            .OrderByDescending(language => language.FileCount)
            .ThenByDescending(language => language.SymbolCount)
            .ThenBy(language => language.Language, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    private static string ReadSearchSidecarStatus(DashboardWorkspaceRow workspace)
    {
        string searchDbPath;
        try
        {
            searchDbPath = SymbolSearchSidecar.SearchDbPathFor(workspace.IndexDbPath);
        }
        catch (ArgumentException)
        {
            return "unknown";
        }

        if (!File.Exists(searchDbPath))
            return "missing";

        try
        {
            using SqliteConnection connection = OpenReadOnly(searchDbPath);
            if (!TableExists(connection, "meta"))
                return "unreadable";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT revision, schema_version FROM meta LIMIT 1;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
                return "unreadable";

            long revision = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            long schemaVersion = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (schemaVersion != SearchIndexWriter.SchemaVersion)
                return "stale_schema";
            if (workspace.LastRevision is { } expected && revision != expected)
                return "stale";

            return "fresh";
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or FormatException or OverflowException)
        {
            return "unreadable";
        }
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=3000;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private readonly record struct FileFacts(
        long FileCount,
        long ContentBytes,
        IReadOnlyDictionary<string, FileLanguageFacts> Languages);

    private readonly record struct FileLanguageFacts(long FileCount, long ContentBytes);
}
