using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;

namespace Miller.Dashboard;

public static class DashboardIndexFactsReader
{
    private static readonly ContentCorpusSidecar ContentSidecar = new();

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
                searchSidecarStatus: "unknown",
                contentSidecarStatus: "unknown",
                indexRevision: null,
                artifactId: null);
        }

        try
        {
            using SqliteConnection connection = OpenReadOnly(workspace.IndexDbPath);
            if (!TableExists(connection, "files") || !TableExists(connection, "symbols"))
            {
                string searchStatus = ReadSearchSidecarStatus(workspace);
                long? unreadableRevision = TryReadIndexRevision(connection);
                string contentStatus = ReadContentSidecarStatus(
                    workspace,
                    unreadableRevision ?? workspace.LastRevision ?? 0L);
                return Empty(
                    workspace,
                    "unreadable",
                    "Index DB does not contain julie files and symbols tables.",
                    searchSidecarStatus: searchStatus,
                    contentSidecarStatus: contentStatus,
                    indexRevision: unreadableRevision,
                    artifactId: TryReadArtifactId(connection));
            }

            FileFacts fileFacts = ReadFileFacts(connection);
            Dictionary<string, long> symbolCountsByLanguage = ReadSymbolCountsByLanguage(connection);
            (IReadOnlyList<DashboardSymbolKindStat> symbolKinds, int symbolKindCount) = ReadSymbolKinds(connection);
            int languageCount = CountLanguages(fileFacts.Languages, symbolCountsByLanguage);
            IReadOnlyList<DashboardLanguageStat> languages = BuildLanguageStats(
                fileFacts.Languages,
                symbolCountsByLanguage);
            long symbolCount = symbolCountsByLanguage.Values.Sum();
            long? indexRevision = TryReadIndexRevision(connection);
            string? artifactId = TryReadArtifactId(connection);
            string? extractorVersion = ExtractBinaryVersionReader.TryRead(connection);
            string searchSidecarStatus = ReadSearchSidecarStatus(workspace);
            string contentSidecarStatus = ReadContentSidecarStatus(
                workspace,
                indexRevision ?? workspace.LastRevision ?? 0L);
            string freshnessStatus = ComputeFreshnessStatus(
                workspace,
                workspace.State,
                indexRevision,
                searchSidecarStatus,
                contentSidecarStatus);

            return new DashboardWorkspaceFacts(
                workspace.WorkspaceId,
                workspace.DisplayId,
                workspace.CanonicalRoot,
                workspace.IndexDbPath,
                workspace.State,
                null,
                fileFacts.FileCount,
                symbolCount,
                languageCount,
                fileFacts.ContentBytes,
                workspace.LastRevision,
                workspace.LastScanAt,
                searchSidecarStatus,
                languages,
                symbolKinds,
                contentSidecarStatus,
                symbolKindCount,
                workspace.LastError,
                extractorVersion,
                artifactId,
                indexRevision,
                freshnessStatus);
        }
        catch (Exception ex) when (
            ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
        {
            return Empty(
                workspace,
                "unreadable",
                ex.Message,
                searchSidecarStatus: "unknown",
                contentSidecarStatus: "unknown",
                indexRevision: null,
                artifactId: null);
        }
    }

    private static DashboardWorkspaceFacts Empty(
        DashboardWorkspaceRow workspace,
        string status,
        string? message,
        string searchSidecarStatus,
        string contentSidecarStatus,
        long? indexRevision,
        string? artifactId) =>
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
            Array.Empty<DashboardSymbolKindStat>(),
            contentSidecarStatus,
            SymbolKindCount: 0,
            RegistryLastError: workspace.LastError,
            ExtractorVersion: ExtractBinaryVersionReader.TryRead(workspace.IndexDbPath),
            ArtifactId: artifactId,
            IndexRevision: indexRevision,
            FreshnessStatus: ComputeFreshnessStatus(
                workspace,
                status,
                indexRevision,
                searchSidecarStatus,
                contentSidecarStatus));

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

    private static (IReadOnlyList<DashboardSymbolKindStat> Kinds, int TotalCount) ReadSymbolKinds(SqliteConnection connection)
    {
        int totalCount = CountSymbolKinds(connection);
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

        return (kinds, totalCount);
    }

    private static int CountSymbolKinds(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM (
                SELECT 1
                FROM symbols
                WHERE name IS NOT NULL
                GROUP BY COALESCE(NULLIF(kind, ''), 'unknown')
            );
            """;
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static long? TryReadIndexRevision(SqliteConnection connection)
    {
        if (!TableExists(connection, "extraction_revisions"))
            return null;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(revision_id) FROM extraction_revisions;";
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string? TryReadArtifactId(SqliteConnection connection)
    {
        if (!TableExists(connection, "artifact_metadata"))
            return null;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM artifact_metadata WHERE key = 'artifact_id' LIMIT 1;";
        object? value = cmd.ExecuteScalar();
        return value is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
    }

    private static string ReadContentSidecarStatus(DashboardWorkspaceRow workspace, long expectedRevision)
    {
        try
        {
            ContentCorpusFacts facts = ContentSidecar.Inspect(workspace.IndexDbPath, expectedRevision);
            return MapContentSidecarStatus(facts.State);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return "unknown";
        }
    }

    private static string MapContentSidecarStatus(string state) =>
        state switch
        {
            "current" => "fresh",
            "stale" => "stale",
            "missing" => "missing",
            "unreadable" => "unreadable",
            _ => "unknown",
        };

    private static string ComputeFreshnessStatus(
        DashboardWorkspaceRow workspace,
        string indexStatus,
        long? indexRevision,
        string searchSidecarStatus,
        string contentSidecarStatus)
    {
        if (!string.IsNullOrWhiteSpace(workspace.LastError))
            return "registry_error";

        if (indexStatus is "missing" or "unreadable")
            return indexStatus;

        if (searchSidecarStatus is "stale" or "stale_schema" or "unreadable")
            return "stale_sidecar";

        if (contentSidecarStatus is "stale" or "unreadable")
            return "stale_sidecar";

        if (searchSidecarStatus is "missing" || contentSidecarStatus is "missing")
            return "stale_sidecar";

        if (indexRevision is { } revision &&
            workspace.LastRevision is { } registryRevision &&
            revision > 0 &&
            registryRevision != revision)
            return "revision_mismatch";

        if (string.Equals(workspace.State, "error", StringComparison.Ordinal))
            return "error";

        if (searchSidecarStatus == "fresh" && contentSidecarStatus == "fresh")
            return "current";

        return "unknown";
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

    private static int CountLanguages(
        IReadOnlyDictionary<string, FileLanguageFacts> fileFacts,
        IReadOnlyDictionary<string, long> symbolCountsByLanguage)
    {
        var names = new HashSet<string>(fileFacts.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string language in symbolCountsByLanguage.Keys)
        {
            names.Add(language);
        }

        return names.Count;
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
