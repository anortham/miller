using Microsoft.Data.Sqlite;

namespace Miller.Indexing.Reads;

public sealed class LegacyArtifactReadSession : IWorkspaceReadSession
{
    private readonly string _databasePath;
    private bool _disposed;

    private LegacyArtifactReadSession(string databasePath, WorkspaceReadSnapshot snapshot)
    {
        _databasePath = databasePath;
        Snapshot = snapshot;
    }

    public WorkspaceReadSnapshot Snapshot { get; }

    internal string DatabasePath => _databasePath;

    public static LegacyArtifactReadSession Open(
        string databasePath,
        string? workspaceRoot = null,
        string? workspaceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string absolutePath = Path.GetFullPath(databasePath);
        using SqliteConnection connection = SqliteReadOnlyAccess.Open(absolutePath);

        Dictionary<string, string> metadata = ReadMetadata(connection);
        string artifactId = Optional(metadata, "artifact_id") ?? $"legacy:{absolutePath}";
        string root = workspaceRoot
            ?? Optional(metadata, "root_path")
            ?? string.Empty;
        if (root.Length > 0)
            root = Path.GetFullPath(root);
        long revision = ReadRevision(connection);
        string indexLevel = ExtractIndexLevelReader.Read(connection);
        var freshness = new WorkspaceFreshnessToken(artifactId, revision);
        var snapshot = new WorkspaceReadSnapshot(
            root,
            workspaceId,
            artifactId,
            "legacy",
            freshness,
            indexLevel,
            WorkspaceReadMode.LegacyArtifact);
        return new LegacyArtifactReadSession(absolutePath, snapshot);
    }

    internal static LegacyArtifactReadSession CreateDeferred(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string absolutePath = Path.GetFullPath(databasePath);
        var snapshot = new WorkspaceReadSnapshot(
            string.Empty,
            WorkspaceId: null,
            $"legacy:{absolutePath}",
            "legacy",
            new WorkspaceFreshnessToken($"legacy:{absolutePath}", Revision: 0),
            IndexLevels.FullMetadataValue,
            WorkspaceReadMode.LegacyArtifact);
        return new LegacyArtifactReadSession(absolutePath, snapshot);
    }

    public TResult Read<TResult>(Func<SqliteConnection, TResult> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using SqliteConnection connection = SqliteReadOnlyAccess.Open(_databasePath);
        return query(connection);
    }

    public void Dispose() => _disposed = true;

    private static Dictionary<string, string> ReadMetadata(SqliteConnection connection)
    {
        if (!TableExists(connection, "artifact_metadata"))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM artifact_metadata ORDER BY key";
        using SqliteDataReader reader = command.ExecuteReader();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            values.Add(reader.GetString(0), reader.GetString(1));
        return values;
    }

    private static string? Optional(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static long ReadRevision(SqliteConnection connection)
    {
        if (!TableExists(connection, "extraction_revisions"))
            return 0;

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(revision_id), 0) FROM extraction_revisions";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }
}
