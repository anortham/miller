using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

public sealed class WorkspaceRegistry : IDisposable
{
    private const string CreateTableDdl = """
        CREATE TABLE IF NOT EXISTS workspaces (
            workspace_id TEXT NOT NULL PRIMARY KEY,
            display_id TEXT NOT NULL,
            canonical_root TEXT NOT NULL,
            index_db_path TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            last_scan_at TEXT,
            last_revision INTEGER CHECK (last_revision IS NULL OR last_revision >= 0),
            state TEXT NOT NULL CHECK (state IN ('current','ready','loaded_existing','stale','refreshing','missing','error')),
            last_error TEXT,
            level_policy TEXT
        ) STRICT;
        """;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    private WorkspaceRegistry(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static WorkspaceRegistry Open(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        string absDbPath = Path.GetFullPath(dbPath);
        string? dir = Path.GetDirectoryName(absDbPath);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException($"Cannot determine the directory of registry DB path '{absDbPath}'.");
        Directory.CreateDirectory(dir);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = absDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText =
                    "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000;";
                pragma.ExecuteNonQuery();
            }

            using (var ddl = connection.CreateCommand())
            {
                ddl.CommandText = CreateTableDdl;
                ddl.ExecuteNonQuery();
            }

            EnsureLevelPolicyColumn(connection);

            return new WorkspaceRegistry(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public WorkspaceRegistryRow UpsertSeen(
        string workspaceId,
        string displayId,
        string canonicalRoot,
        string indexDbPath,
        WorkspaceRegistryState state = WorkspaceRegistryState.Ready,
        DateTimeOffset? seenAtUtc = null)
    {
        ThrowIfDisposed();
        ValidateRequired(workspaceId, nameof(workspaceId));
        ValidateRequired(displayId, nameof(displayId));
        ValidateRequired(canonicalRoot, nameof(canonicalRoot));
        ValidateRequired(indexDbPath, nameof(indexDbPath));

        DateTimeOffset seen = NormalizeUtc(seenAtUtc ?? DateTimeOffset.UtcNow);
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO workspaces
                    (workspace_id, display_id, canonical_root, index_db_path, last_seen_at, state, last_error)
                VALUES
                    ($workspace_id, $display_id, $canonical_root, $index_db_path, $last_seen_at, $state, NULL)
                ON CONFLICT(workspace_id) DO UPDATE SET
                    display_id = excluded.display_id,
                    canonical_root = excluded.canonical_root,
                    index_db_path = excluded.index_db_path,
                    last_seen_at = excluded.last_seen_at,
                    state = excluded.state,
                    last_error = NULL;
                """;
            cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
            cmd.Parameters.AddWithValue("$display_id", displayId);
            cmd.Parameters.AddWithValue("$canonical_root", canonicalRoot);
            cmd.Parameters.AddWithValue("$index_db_path", indexDbPath);
            cmd.Parameters.AddWithValue("$last_seen_at", FormatTimestamp(seen));
            cmd.Parameters.AddWithValue("$state", state.ToStorageString());
            cmd.ExecuteNonQuery();

            PruneDuplicatePathRowsUnderLock(tx, workspaceId, canonicalRoot, indexDbPath);
            tx.Commit();

            return GetRequiredUnderLock(workspaceId);
        }
    }

    public WorkspaceRegistryRow MarkScanned(
        string workspaceId,
        long revision,
        DateTimeOffset? scannedAtUtc = null)
    {
        ThrowIfDisposed();
        ValidateRequired(workspaceId, nameof(workspaceId));
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "Revision must be non-negative.");

        DateTimeOffset scanned = NormalizeUtc(scannedAtUtc ?? DateTimeOffset.UtcNow);
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE workspaces
                SET last_seen_at = $last_seen_at,
                    last_scan_at = $last_scan_at,
                    last_revision = $last_revision,
                    state = 'ready',
                    last_error = NULL
                WHERE workspace_id = $workspace_id;
                """;
            cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
            cmd.Parameters.AddWithValue("$last_seen_at", FormatTimestamp(scanned));
            cmd.Parameters.AddWithValue("$last_scan_at", FormatTimestamp(scanned));
            cmd.Parameters.AddWithValue("$last_revision", revision);
            ExecuteExistingRowUpdate(cmd, workspaceId);
            return GetRequiredUnderLock(workspaceId);
        }
    }

    public WorkspaceRegistryRow MarkLoadedExisting(
        string workspaceId,
        long revision,
        DateTimeOffset? seenAtUtc = null)
    {
        ThrowIfDisposed();
        ValidateRequired(workspaceId, nameof(workspaceId));
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "Revision must be non-negative.");

        DateTimeOffset seen = NormalizeUtc(seenAtUtc ?? DateTimeOffset.UtcNow);
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE workspaces
                SET last_seen_at = $last_seen_at,
                    last_revision = $last_revision,
                    state = 'loaded_existing',
                    last_error = NULL
                WHERE workspace_id = $workspace_id;
                """;
            cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
            cmd.Parameters.AddWithValue("$last_seen_at", FormatTimestamp(seen));
            cmd.Parameters.AddWithValue("$last_revision", revision);
            ExecuteExistingRowUpdate(cmd, workspaceId);
            return GetRequiredUnderLock(workspaceId);
        }
    }

    public WorkspaceRegistryRow MarkMissing(
        string workspaceId,
        string? error = null,
        DateTimeOffset? seenAtUtc = null)
    {
        ThrowIfDisposed();
        ValidateRequired(workspaceId, nameof(workspaceId));

        DateTimeOffset seen = NormalizeUtc(seenAtUtc ?? DateTimeOffset.UtcNow);
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE workspaces
                SET last_seen_at = $last_seen_at,
                    state = 'missing',
                    last_error = $last_error
                WHERE workspace_id = $workspace_id;
                """;
            cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
            cmd.Parameters.AddWithValue("$last_seen_at", FormatTimestamp(seen));
            cmd.Parameters.AddWithValue("$last_error", (object?)error ?? DBNull.Value);
            ExecuteExistingRowUpdate(cmd, workspaceId);
            return GetRequiredUnderLock(workspaceId);
        }
    }

    public WorkspaceRegistryRow MarkError(
        string workspaceId,
        string error,
        DateTimeOffset? seenAtUtc = null)
    {
        ThrowIfDisposed();
        ValidateRequired(workspaceId, nameof(workspaceId));
        ValidateRequired(error, nameof(error));

        DateTimeOffset seen = NormalizeUtc(seenAtUtc ?? DateTimeOffset.UtcNow);
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE workspaces
                SET last_seen_at = $last_seen_at,
                    state = 'error',
                    last_error = $last_error
                WHERE workspace_id = $workspace_id;
                """;
            cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
            cmd.Parameters.AddWithValue("$last_seen_at", FormatTimestamp(seen));
            cmd.Parameters.AddWithValue("$last_error", error);
            ExecuteExistingRowUpdate(cmd, workspaceId);
            return GetRequiredUnderLock(workspaceId);
        }
    }

    /// <summary>
    /// Set (or with null, clear) the workspace's per-workspace index-level policy. The stored string uses the
    /// same tokens <c>MILLER_INDEX_LEVELS</c> accepts; resolution order is env &gt; this column &gt; the
    /// progressive default, so the environment always wins over a stored policy.
    /// </summary>
    public WorkspaceRegistryRow SetLevelPolicy(string workspaceId, string? levelPolicy)
    {
        ThrowIfDisposed();
        ValidateRequired(workspaceId, nameof(workspaceId));

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE workspaces
                SET level_policy = $level_policy
                WHERE workspace_id = $workspace_id;
                """;
            cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
            cmd.Parameters.AddWithValue("$level_policy", (object?)levelPolicy ?? DBNull.Value);
            ExecuteExistingRowUpdate(cmd, workspaceId);
            return GetRequiredUnderLock(workspaceId);
        }
    }

    public bool Remove(string workspaceId)
    {
        ThrowIfDisposed();
        ValidateRequired(workspaceId, nameof(workspaceId));

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM workspaces WHERE workspace_id = $workspace_id;";
            cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public WorkspaceRegistryRow? Get(string workspaceId)
    {
        ThrowIfDisposed();
        ValidateRequired(workspaceId, nameof(workspaceId));

        lock (_gate)
        {
            return GetUnderLock(workspaceId);
        }
    }

    public IReadOnlyList<WorkspaceRegistryRow> List()
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT workspace_id, display_id, canonical_root, index_db_path, last_seen_at, last_scan_at,
                       last_revision, state, last_error, level_policy
                FROM workspaces
                ORDER BY CASE WHEN state IN ('current','ready','loaded_existing') THEN 0 ELSE 1 END,
                         display_id COLLATE NOCASE,
                         display_id,
                         workspace_id;
                """;
            using var reader = cmd.ExecuteReader();
            var rows = new List<WorkspaceRegistryRow>();
            while (reader.Read())
                rows.Add(ReadRow(reader));
            return rows;
        }
    }

    internal (string JournalMode, int Synchronous, int BusyTimeout) ReadPragmasForTest()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            return (
                ReadTextPragmaUnderLock("journal_mode"),
                ReadIntPragmaUnderLock("synchronous"),
                ReadIntPragmaUnderLock("busy_timeout"));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _connection.Dispose();
        _disposed = true;
    }

    // Registries created before the levels work lack the column, and the registry has no migration
    // machinery -- one additive nullable column rides the same pragma_table_info + ALTER pattern the
    // telemetry ledger uses. STRICT tables accept ALTER TABLE ... ADD COLUMN for nullable TEXT.
    private static void EnsureLevelPolicyColumn(SqliteConnection connection)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('workspaces') WHERE name = 'level_policy';";
            if (Convert.ToInt64(probe.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE workspaces ADD COLUMN level_policy TEXT;";
        alter.ExecuteNonQuery();
    }

    private WorkspaceRegistryRow GetRequiredUnderLock(string workspaceId) =>
        GetUnderLock(workspaceId) ?? throw new KeyNotFoundException(
            string.Create(CultureInfo.InvariantCulture, $"Workspace registry row '{workspaceId}' was not found."));

    private WorkspaceRegistryRow? GetUnderLock(string workspaceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT workspace_id, display_id, canonical_root, index_db_path, last_seen_at, last_scan_at,
                   last_revision, state, last_error, level_policy
            FROM workspaces
            WHERE workspace_id = $workspace_id;
            """;
        cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    private void PruneDuplicatePathRowsUnderLock(
        SqliteTransaction transaction,
        string workspaceId,
        string canonicalRoot,
        string indexDbPath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        string pathPredicate = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? """
              canonical_root COLLATE NOCASE = $canonical_root COLLATE NOCASE
              AND index_db_path COLLATE NOCASE = $index_db_path COLLATE NOCASE
              """
            : """
              canonical_root = $canonical_root
              AND index_db_path = $index_db_path
              """;
        cmd.CommandText = $$"""
            DELETE FROM workspaces
            WHERE workspace_id <> $workspace_id
              AND {{pathPredicate}};
            """;
        cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
        cmd.Parameters.AddWithValue("$canonical_root", canonicalRoot);
        cmd.Parameters.AddWithValue("$index_db_path", indexDbPath);
        cmd.ExecuteNonQuery();
    }

    private static WorkspaceRegistryRow ReadRow(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseTimestamp(reader.GetString(4)),
            reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            WorkspaceRegistryStateExtensions.FromStorage(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    private static void ExecuteExistingRowUpdate(SqliteCommand cmd, string workspaceId)
    {
        if (cmd.ExecuteNonQuery() == 0)
            throw new KeyNotFoundException(
                string.Create(CultureInfo.InvariantCulture, $"Workspace registry row '{workspaceId}' was not found."));
    }

    private string ReadTextPragmaUnderLock(string pragma)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA {pragma};");
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private int ReadIntPragmaUnderLock(string pragma)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA {pragma};");
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ValidateRequired(string value, string paramName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value) =>
        value.ToUniversalTime();

    private static string FormatTimestamp(DateTimeOffset value) =>
        NormalizeUtc(value).ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
