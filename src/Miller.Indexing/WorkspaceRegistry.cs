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
            level_policy TEXT,
            git_common_dir TEXT,
            git_is_linked INTEGER,
            git_dir TEXT,
            git_dir_created_at TEXT
        ) STRICT;
        """;

    private const string RowColumns =
        "workspace_id, display_id, canonical_root, index_db_path, last_seen_at, last_scan_at, " +
        "last_revision, state, last_error, level_policy, git_common_dir, git_is_linked, git_dir, " +
        "git_dir_created_at";

    private static readonly (string Name, string Type)[] AdditiveColumns =
    [
        ("level_policy", "TEXT"),
        ("git_common_dir", "TEXT"),
        ("git_is_linked", "INTEGER"),
        ("git_dir", "TEXT"),
        ("git_dir_created_at", "TEXT"),
    ];

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

            EnsureAdditiveColumns(connection);

            return new WorkspaceRegistry(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Record that a workspace was seen, inserting its row or refreshing the mutable facts of an existing one.
    /// </summary>
    /// <param name="lineage">
    /// The repository lineage observed for this root, or null when the caller resolved no git layout. Null leaves
    /// any lineage another process already stored UNTOUCHED — an upsert from a context without git resolution must
    /// not erase a persisted checkout generation. A non-null lineage replaces all four stored values together, so a
    /// half-known lineage cannot inherit the other half from a previous generation.
    /// <see cref="WorkspaceLineage.GitCommonDir"/> is canonicalized here through
    /// <see cref="PathCanonicalizer"/> — callers pass the raw <see cref="GitWorktreeLayout.CommonDir"/>, and
    /// <see cref="FindMainCheckoutByCommonDir"/> expects the same canonical spelling.
    /// </param>
    public WorkspaceRegistryRow UpsertSeen(
        string workspaceId,
        string displayId,
        string canonicalRoot,
        string indexDbPath,
        WorkspaceRegistryState state = WorkspaceRegistryState.Ready,
        DateTimeOffset? seenAtUtc = null,
        WorkspaceLineage? lineage = null)
    {
        ThrowIfDisposed();
        ValidateRequired(workspaceId, nameof(workspaceId));
        ValidateRequired(displayId, nameof(displayId));
        ValidateRequired(canonicalRoot, nameof(canonicalRoot));
        ValidateRequired(indexDbPath, nameof(indexDbPath));
        if (lineage is not null)
            ValidateRequired(lineage.GitCommonDir, nameof(lineage));

        DateTimeOffset seen = NormalizeUtc(seenAtUtc ?? DateTimeOffset.UtcNow);
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO workspaces
                    (workspace_id, display_id, canonical_root, index_db_path, last_seen_at, state, last_error,
                     git_common_dir, git_is_linked, git_dir, git_dir_created_at)
                VALUES
                    ($workspace_id, $display_id, $canonical_root, $index_db_path, $last_seen_at, $state, NULL,
                     $git_common_dir, $git_is_linked, $git_dir, $git_dir_created_at)
                ON CONFLICT(workspace_id) DO UPDATE SET
                    display_id = excluded.display_id,
                    canonical_root = excluded.canonical_root,
                    index_db_path = excluded.index_db_path,
                    last_seen_at = excluded.last_seen_at,
                    state = excluded.state,
                    last_error = NULL,
                    git_common_dir = CASE WHEN $has_lineage = 1
                        THEN excluded.git_common_dir ELSE workspaces.git_common_dir END,
                    git_is_linked = CASE WHEN $has_lineage = 1
                        THEN excluded.git_is_linked ELSE workspaces.git_is_linked END,
                    git_dir = CASE WHEN $has_lineage = 1
                        THEN excluded.git_dir ELSE workspaces.git_dir END,
                    git_dir_created_at = CASE WHEN $has_lineage = 1
                        THEN excluded.git_dir_created_at ELSE workspaces.git_dir_created_at END;
                """;
            cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
            cmd.Parameters.AddWithValue("$display_id", displayId);
            cmd.Parameters.AddWithValue("$canonical_root", canonicalRoot);
            cmd.Parameters.AddWithValue("$index_db_path", indexDbPath);
            cmd.Parameters.AddWithValue("$last_seen_at", FormatTimestamp(seen));
            cmd.Parameters.AddWithValue("$state", state.ToStorageString());
            cmd.Parameters.AddWithValue("$has_lineage", lineage is null ? 0 : 1);
            cmd.Parameters.AddWithValue(
                "$git_common_dir",
                lineage is null
                    ? DBNull.Value
                    : WorkspaceLineage.CanonicalizeCommonDir(lineage.GitCommonDir));
            cmd.Parameters.AddWithValue("$git_is_linked", lineage is null ? DBNull.Value : lineage.IsLinkedWorktree ? 1 : 0);
            cmd.Parameters.AddWithValue("$git_dir", (object?)lineage?.GitDir ?? DBNull.Value);
            cmd.Parameters.AddWithValue(
                "$git_dir_created_at",
                lineage?.GitDirCreatedAtUtc is { } created ? FormatTimestamp(created) : DBNull.Value);
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
            cmd.CommandText = $"""
                SELECT {RowColumns}
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

    /// <summary>
    /// The registered MAIN CHECKOUT of the repository whose shared git directory is
    /// <paramref name="canonicalCommonDir"/>, or null when no such row is registered. Linked worktrees of the same
    /// repository are skipped: a rebind sources from the main checkout only, never worktree-to-worktree.
    ///
    /// <para>Pass the same canonical spelling <see cref="UpsertSeen"/> stores —
    /// <see cref="WorkspaceLineage.CanonicalizeCommonDir"/> output. Rows are compared with
    /// <see cref="ArtifactRootIdentity.Matches"/>, so path case follows the platform rather than SQLite's
    /// ASCII-only <c>NOCASE</c> collation.</para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="canonicalCommonDir"/> is null or blank.</exception>
    public WorkspaceRegistryRow? FindMainCheckoutByCommonDir(string canonicalCommonDir)
    {
        ThrowIfDisposed();
        ValidateRequired(canonicalCommonDir, nameof(canonicalCommonDir));

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {RowColumns}
                FROM workspaces
                WHERE git_common_dir IS NOT NULL AND git_is_linked = 0
                ORDER BY workspace_id;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                WorkspaceRegistryRow row = ReadRow(reader);
                if (ArtifactRootIdentity.Matches(row.GitCommonDir, canonicalCommonDir))
                    return row;
            }

            return null;
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

    // Registries created before the levels and lineage work lack those columns, and the registry has no
    // migration machinery -- each additive nullable column rides the same pragma_table_info + ALTER pattern
    // the telemetry ledger uses. STRICT tables accept ALTER TABLE ... ADD COLUMN for nullable columns.
    private static void EnsureAdditiveColumns(SqliteConnection connection)
    {
        foreach ((string name, string type) in AdditiveColumns)
        {
            using (var probe = connection.CreateCommand())
            {
                probe.CommandText =
                    "SELECT COUNT(*) FROM pragma_table_info('workspaces') WHERE name = $name;";
                probe.Parameters.AddWithValue("$name", name);
                if (Convert.ToInt64(probe.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                    continue;
            }

            AddColumnToleratingConcurrentAdder(connection, name, type);
        }
    }

    /// <summary>
    /// The ALTER half of <see cref="EnsureAdditiveColumns"/>. The pragma probe above it is only a fast
    /// path: the registry is machine-global, so another Miller process can add the column between that
    /// check and this statement. A duplicate-column failure means the intended end state already holds.
    /// </summary>
    internal static void AddColumnToleratingConcurrentAdder(
        SqliteConnection connection,
        string column,
        string type)
    {
        using var alter = connection.CreateCommand();
        alter.CommandText = string.Create(
            CultureInfo.InvariantCulture,
            $"ALTER TABLE workspaces ADD COLUMN {column} {type};");
        try
        {
            alter.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (
            ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private WorkspaceRegistryRow GetRequiredUnderLock(string workspaceId) =>
        GetUnderLock(workspaceId) ?? throw new KeyNotFoundException(
            string.Create(CultureInfo.InvariantCulture, $"Workspace registry row '{workspaceId}' was not found."));

    private WorkspaceRegistryRow? GetUnderLock(string workspaceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {RowColumns}
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
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11) != 0,
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : ParseTimestamp(reader.GetString(13)));

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

/// <summary>
/// The repository lineage of a workspace root, persisted so a later process can answer two questions the root
/// path alone cannot: which repository family this workspace belongs to (for sibling lookup), and which checkout
/// generation occupied the root when the row was written (for path-reuse detection across restarts).
/// </summary>
/// <param name="GitCommonDir">
/// <see cref="GitWorktreeLayout.CommonDir"/> — the repository's shared git directory, which every worktree of one
/// repository has in common. Pass the raw layout value; <see cref="WorkspaceRegistry.UpsertSeen"/> canonicalizes it.
/// </param>
/// <param name="IsLinkedWorktree">
/// <see cref="GitWorktreeLayout.IsLinkedWorktree"/> — false for the main checkout (a rebind source), true for a
/// linked worktree (a rebind target).
/// </param>
/// <param name="GitDir">The <see cref="WorkspaceRootIdentity.GitDir"/> half of the checkout generation.</param>
/// <param name="GitDirCreatedAtUtc">
/// The <see cref="WorkspaceRootIdentity.GitDirCreatedAtUtc"/> half. Null when the platform reported no usable
/// creation time, which is what <see cref="WorkspaceRootIdentity.IsKnown"/> reads as an unknown generation.
/// </param>
public sealed record WorkspaceLineage(
    string GitCommonDir,
    bool IsLinkedWorktree,
    string? GitDir,
    DateTimeOffset? GitDirCreatedAtUtc)
{
    /// <summary>
    /// The canonical spelling used for both the stored <c>git_common_dir</c> and
    /// <see cref="WorkspaceRegistry.FindMainCheckoutByCommonDir"/> lookups.
    ///
    /// <para><see cref="GitWorktreeLayout"/> only normalizes lexically while registry roots are symlink-resolved,
    /// so a raw layout path would silently miss an eligible main checkout on a macOS <c>/var</c>→<c>/private/var</c>
    /// layout. This resolves as far as the path exists instead of throwing on a vanished directory, because
    /// registering a workspace must not fail when a git directory disappears mid-flight.</para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="commonDir"/> is null or blank.</exception>
    public static string CanonicalizeCommonDir(string commonDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonDir);

        string absolute = Path.GetFullPath(commonDir);
        return PathCanonicalizer.CanonicalizeFile(canonicalRoot: absolute, path: absolute);
    }
}
