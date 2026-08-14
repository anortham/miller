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

    private const string CreateStoreTablesDdl = """
        CREATE TABLE IF NOT EXISTS store_families (
            family_id TEXT NOT NULL PRIMARY KEY,
            lineage_key TEXT NOT NULL UNIQUE,
            canonical_common_dir TEXT,
            common_dir_created_at TEXT,
            store_root TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        ) STRICT;

        CREATE TABLE IF NOT EXISTS store_members (
            workspace_id TEXT NOT NULL PRIMARY KEY,
            family_id TEXT NOT NULL,
            view_id TEXT NOT NULL,
            workspace_root TEXT NOT NULL,
            root_git_dir TEXT,
            root_git_dir_created_at TEXT,
            updated_at TEXT NOT NULL,
            UNIQUE(family_id, view_id),
            FOREIGN KEY (workspace_id) REFERENCES workspaces(workspace_id) ON DELETE CASCADE,
            FOREIGN KEY (family_id) REFERENCES store_families(family_id)
                ON UPDATE CASCADE ON DELETE CASCADE
        ) STRICT;
        """;

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

    private WorkspaceRegistry(SqliteConnection connection, string databasePath)
    {
        _connection = connection;
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

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
                    "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000; " +
                    "PRAGMA foreign_keys=ON;";
                pragma.ExecuteNonQuery();
            }

            using (var ddl = connection.CreateCommand())
            {
                ddl.CommandText = CreateTableDdl;
                ddl.ExecuteNonQuery();
            }

            EnsureAdditiveColumns(connection);

            using (var storeDdl = connection.CreateCommand())
            {
                storeDdl.CommandText = CreateStoreTablesDdl;
                storeDdl.ExecuteNonQuery();
            }

            return new WorkspaceRegistry(connection, absDbPath);
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

    public StoreFamilyRegistryRow GetOrCreateStoreFamily(
        string lineageKey,
        string? canonicalCommonDir,
        DateTimeOffset? commonDirCreatedAtUtc,
        string storesRoot,
        Func<Guid>? mintFamilyId = null,
        DateTimeOffset? nowUtc = null)
    {
        ThrowIfDisposed();
        ValidateRequired(lineageKey, nameof(lineageKey));
        ValidateRequired(storesRoot, nameof(storesRoot));
        DateTimeOffset now = NormalizeUtc(nowUtc ?? DateTimeOffset.UtcNow);

        lock (_gate)
        {
            StoreFamilyRegistryRow? existing = GetStoreFamilyByLineageUnderLock(lineageKey);
            if (existing is not null)
                return existing;

            Guid familyId = (mintFamilyId ?? Guid.NewGuid)();
            if (familyId == Guid.Empty)
                throw new InvalidOperationException("The store family id factory returned an empty UUID.");
            string storeRoot = Path.Combine(Path.GetFullPath(storesRoot), familyId.ToString("D"));
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO store_families(
                    family_id, lineage_key, canonical_common_dir, common_dir_created_at,
                    store_root, created_at, updated_at)
                VALUES(
                    $family_id, $lineage_key, $canonical_common_dir, $common_dir_created_at,
                    $store_root, $created_at, $updated_at)
                """;
            command.Parameters.AddWithValue("$family_id", familyId.ToString("D"));
            command.Parameters.AddWithValue("$lineage_key", lineageKey);
            command.Parameters.AddWithValue("$canonical_common_dir", (object?)canonicalCommonDir ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$common_dir_created_at",
                commonDirCreatedAtUtc is { } created ? FormatTimestamp(created) : DBNull.Value);
            command.Parameters.AddWithValue("$store_root", storeRoot);
            command.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
            command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
            command.ExecuteNonQuery();
            return GetStoreFamilyByLineageUnderLock(lineageKey) ?? throw new InvalidOperationException(
                $"Store family lineage '{lineageKey}' was not persisted.");
        }
    }

    internal StoreFamilyRegistryRow AdoptStoreFamily(
        Guid familyId,
        string lineageKey,
        string? canonicalCommonDir,
        DateTimeOffset? commonDirCreatedAtUtc,
        string storeRoot,
        DateTimeOffset? nowUtc = null)
    {
        ThrowIfDisposed();
        if (familyId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(familyId));
        ValidateRequired(lineageKey, nameof(lineageKey));
        ValidateRequired(storeRoot, nameof(storeRoot));
        DateTimeOffset now = NormalizeUtc(nowUtc ?? DateTimeOffset.UtcNow);
        string absoluteStoreRoot = Path.GetFullPath(storeRoot);

        lock (_gate)
        {
            StoreFamilyRegistryRow? byLineage = GetStoreFamilyByLineageUnderLock(lineageKey);
            if (byLineage is not null)
            {
                if (byLineage.FamilyId != familyId ||
                    !ArtifactRootIdentity.Matches(byLineage.StoreRoot, absoluteStoreRoot))
                {
                    throw new InvalidOperationException(
                        $"Store lineage '{lineageKey}' is already bound to another family or root.");
                }
                return byLineage;
            }

            StoreFamilyRegistryRow? byId = GetStoreFamilyUnderLock(familyId);
            if (byId is not null)
            {
                bool sameLineage = string.Equals(byId.LineageKey, lineageKey, StringComparison.Ordinal) ||
                    (canonicalCommonDir is not null &&
                     byId.CanonicalCommonDir is not null &&
                     ArtifactRootIdentity.Matches(byId.CanonicalCommonDir, canonicalCommonDir) &&
                     byId.CommonDirCreatedAtUtc == commonDirCreatedAtUtc?.ToUniversalTime());
                if (!ArtifactRootIdentity.Matches(byId.StoreRoot, absoluteStoreRoot) || !sameLineage)
                {
                    throw new InvalidOperationException(
                        $"Store family '{familyId:D}' is already bound to another lineage or root.");
                }
                return byId;
            }

            StoreFamilyRegistryRow? byRoot = GetStoreFamilyByRootUnderLock(absoluteStoreRoot);
            if (byRoot is not null)
                throw new InvalidOperationException(
                    $"Store root '{absoluteStoreRoot}' is already bound to family '{byRoot.FamilyId:D}'.");

            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO store_families(
                    family_id, lineage_key, canonical_common_dir, common_dir_created_at,
                    store_root, created_at, updated_at)
                VALUES(
                    $family_id, $lineage_key, $canonical_common_dir, $common_dir_created_at,
                    $store_root, $created_at, $updated_at)
                """;
            command.Parameters.AddWithValue("$family_id", familyId.ToString("D"));
            command.Parameters.AddWithValue("$lineage_key", lineageKey);
            command.Parameters.AddWithValue("$canonical_common_dir", (object?)canonicalCommonDir ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$common_dir_created_at",
                commonDirCreatedAtUtc is { } created ? FormatTimestamp(created) : DBNull.Value);
            command.Parameters.AddWithValue("$store_root", absoluteStoreRoot);
            command.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
            command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
            command.ExecuteNonQuery();
            return GetStoreFamilyUnderLock(familyId) ?? throw new InvalidOperationException(
                $"Store family '{familyId:D}' was not persisted.");
        }
    }

    public StoreFamilyRegistryRow? GetStoreFamily(Guid familyId)
    {
        ThrowIfDisposed();
        lock (_gate)
            return GetStoreFamilyUnderLock(familyId);
    }

    public StoreFamilyRegistryRow? GetStoreFamilyByLineage(string lineageKey)
    {
        ThrowIfDisposed();
        ValidateRequired(lineageKey, nameof(lineageKey));
        lock (_gate)
            return GetStoreFamilyByLineageUnderLock(lineageKey);
    }

    public StoreFamilyRegistryRow? FindStoreFamilyByCommonDir(string canonicalCommonDir)
    {
        ThrowIfDisposed();
        ValidateRequired(canonicalCommonDir, nameof(canonicalCommonDir));
        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                SELECT family_id, lineage_key, canonical_common_dir, common_dir_created_at,
                       store_root, created_at, updated_at
                FROM store_families
                WHERE canonical_common_dir IS NOT NULL
                ORDER BY CASE WHEN common_dir_created_at IS NULL THEN 0 ELSE 1 END,
                         family_id
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                StoreFamilyRegistryRow row = ReadStoreFamily(reader);
                if (ArtifactRootIdentity.Matches(row.CanonicalCommonDir, canonicalCommonDir))
                    return row;
            }
            return null;
        }
    }

    public StoreFamilyRegistryRow PromoteStoreFamilyLineage(
        Guid familyId,
        string lineageKey,
        string canonicalCommonDir,
        DateTimeOffset commonDirCreatedAtUtc,
        DateTimeOffset? nowUtc = null)
    {
        ThrowIfDisposed();
        if (familyId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(familyId));
        ValidateRequired(lineageKey, nameof(lineageKey));
        ValidateRequired(canonicalCommonDir, nameof(canonicalCommonDir));
        DateTimeOffset now = NormalizeUtc(nowUtc ?? DateTimeOffset.UtcNow);
        lock (_gate)
        {
            StoreFamilyRegistryRow current = GetStoreFamilyUnderLock(familyId) ??
                throw new KeyNotFoundException($"Store family '{familyId:D}' was not found.");
            if (current.CommonDirCreatedAtUtc is not null ||
                !ArtifactRootIdentity.Matches(current.CanonicalCommonDir, canonicalCommonDir))
            {
                throw new InvalidOperationException("Only an unknown matching lineage can be promoted.");
            }
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE store_families
                SET lineage_key = $lineage_key,
                    canonical_common_dir = $canonical_common_dir,
                    common_dir_created_at = $common_dir_created_at,
                    updated_at = $updated_at
                WHERE family_id = $family_id
                """;
            command.Parameters.AddWithValue("$lineage_key", lineageKey);
            command.Parameters.AddWithValue("$canonical_common_dir", canonicalCommonDir);
            command.Parameters.AddWithValue("$common_dir_created_at", FormatTimestamp(commonDirCreatedAtUtc));
            command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
            command.Parameters.AddWithValue("$family_id", familyId.ToString("D"));
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Store family lineage promotion lost its registry row.");
            return GetStoreFamilyUnderLock(familyId) ?? throw new InvalidOperationException(
                "Store family lineage promotion did not persist.");
        }
    }

    public IReadOnlyList<StoreFamilyRegistryRow> ListStoreFamilies()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                SELECT family_id, lineage_key, canonical_common_dir, common_dir_created_at,
                       store_root, created_at, updated_at
                FROM store_families
                ORDER BY family_id
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            var rows = new List<StoreFamilyRegistryRow>();
            while (reader.Read())
                rows.Add(ReadStoreFamily(reader));
            return rows;
        }
    }

    public StoreMemberRegistryRow UpsertStoreMember(
        string workspaceId,
        Guid familyId,
        string viewId,
        string workspaceRoot,
        WorkspaceRootIdentity rootIdentity,
        DateTimeOffset? nowUtc = null)
    {
        ThrowIfDisposed();
        ValidateRequired(workspaceId, nameof(workspaceId));
        ValidateRequired(viewId, nameof(viewId));
        ValidateRequired(workspaceRoot, nameof(workspaceRoot));
        if (familyId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(familyId));
        DateTimeOffset now = NormalizeUtc(nowUtc ?? DateTimeOffset.UtcNow);

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO store_members(
                    workspace_id, family_id, view_id, workspace_root,
                    root_git_dir, root_git_dir_created_at, updated_at)
                VALUES(
                    $workspace_id, $family_id, $view_id, $workspace_root,
                    $root_git_dir, $root_git_dir_created_at, $updated_at)
                ON CONFLICT(workspace_id) DO UPDATE SET
                    family_id = excluded.family_id,
                    view_id = excluded.view_id,
                    workspace_root = excluded.workspace_root,
                    root_git_dir = excluded.root_git_dir,
                    root_git_dir_created_at = excluded.root_git_dir_created_at,
                    updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$workspace_id", workspaceId);
            command.Parameters.AddWithValue("$family_id", familyId.ToString("D"));
            command.Parameters.AddWithValue("$view_id", viewId);
            command.Parameters.AddWithValue("$workspace_root", workspaceRoot);
            command.Parameters.AddWithValue("$root_git_dir", (object?)rootIdentity.GitDir ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$root_git_dir_created_at",
                rootIdentity.GitDirCreatedAtUtc is { } created ? FormatTimestamp(created) : DBNull.Value);
            command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
            command.ExecuteNonQuery();
            return GetStoreMemberUnderLock(workspaceId) ?? throw new InvalidOperationException(
                $"Store member '{workspaceId}' was not persisted.");
        }
    }

    public StoreMemberRegistryRow? GetStoreMember(string workspaceId)
    {
        ThrowIfDisposed();
        ValidateRequired(workspaceId, nameof(workspaceId));
        lock (_gate)
            return GetStoreMemberUnderLock(workspaceId);
    }

    public IReadOnlyList<StoreMemberRegistryRow> ListStoreMembers()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                SELECT workspace_id, family_id, view_id, workspace_root,
                       root_git_dir, root_git_dir_created_at, updated_at
                FROM store_members
                ORDER BY workspace_id
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            var rows = new List<StoreMemberRegistryRow>();
            while (reader.Read())
                rows.Add(ReadStoreMember(reader));
            return rows;
        }
    }

    public StoreFamilyRegistryRow ReplaceStoreFamilyIdentity(
        Guid currentFamilyId,
        Guid catalogFamilyId,
        string storeRoot,
        DateTimeOffset? nowUtc = null)
    {
        ThrowIfDisposed();
        if (currentFamilyId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(currentFamilyId));
        if (catalogFamilyId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(catalogFamilyId));
        ValidateRequired(storeRoot, nameof(storeRoot));
        DateTimeOffset now = NormalizeUtc(nowUtc ?? DateTimeOffset.UtcNow);

        lock (_gate)
        {
            StoreFamilyRegistryRow current = GetStoreFamilyUnderLock(currentFamilyId) ??
                throw new KeyNotFoundException($"Store family '{currentFamilyId:D}' was not found.");
            if (!ArtifactRootIdentity.Matches(current.StoreRoot, storeRoot))
                throw new InvalidOperationException("Store family root changed during catalog reconciliation.");
            if (currentFamilyId == catalogFamilyId)
                return current;
            if (GetStoreFamilyUnderLock(catalogFamilyId) is not null)
                throw new InvalidOperationException($"Store family '{catalogFamilyId:D}' is already registered.");

            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE store_families
                SET family_id = $catalog_family_id,
                    updated_at = $updated_at
                WHERE family_id = $current_family_id AND store_root = $store_root
                """;
            command.Parameters.AddWithValue("$catalog_family_id", catalogFamilyId.ToString("D"));
            command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
            command.Parameters.AddWithValue("$current_family_id", currentFamilyId.ToString("D"));
            command.Parameters.AddWithValue("$store_root", current.StoreRoot);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Store family catalog reconciliation lost its registry row.");
            return GetStoreFamilyUnderLock(catalogFamilyId) ?? throw new InvalidOperationException(
                "Store family catalog reconciliation did not persist the catalog identity.");
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

    private StoreFamilyRegistryRow? GetStoreFamilyByLineageUnderLock(string lineageKey)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT family_id, lineage_key, canonical_common_dir, common_dir_created_at,
                   store_root, created_at, updated_at
            FROM store_families
            WHERE lineage_key = $lineage_key
            """;
        command.Parameters.AddWithValue("$lineage_key", lineageKey);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadStoreFamily(reader) : null;
    }

    private StoreFamilyRegistryRow? GetStoreFamilyUnderLock(Guid familyId)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT family_id, lineage_key, canonical_common_dir, common_dir_created_at,
                   store_root, created_at, updated_at
            FROM store_families
            WHERE family_id = $family_id
            """;
        command.Parameters.AddWithValue("$family_id", familyId.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadStoreFamily(reader) : null;
    }

    private StoreFamilyRegistryRow? GetStoreFamilyByRootUnderLock(string storeRoot)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? """
              SELECT family_id, lineage_key, canonical_common_dir, common_dir_created_at,
                     store_root, created_at, updated_at
              FROM store_families
              WHERE store_root COLLATE NOCASE = $store_root COLLATE NOCASE
              """
            : """
            SELECT family_id, lineage_key, canonical_common_dir, common_dir_created_at,
                   store_root, created_at, updated_at
            FROM store_families
            WHERE store_root = $store_root
            """;
        command.Parameters.AddWithValue("$store_root", storeRoot);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadStoreFamily(reader) : null;
    }

    private StoreMemberRegistryRow? GetStoreMemberUnderLock(string workspaceId)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT workspace_id, family_id, view_id, workspace_root,
                   root_git_dir, root_git_dir_created_at, updated_at
            FROM store_members
            WHERE workspace_id = $workspace_id
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadStoreMember(reader) : null;
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

    private static StoreFamilyRegistryRow ReadStoreFamily(SqliteDataReader reader) =>
        new(
            Guid.ParseExact(reader.GetString(0), "D"),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : ParseTimestamp(reader.GetString(3)),
            reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)));

    private static StoreMemberRegistryRow ReadStoreMember(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            Guid.ParseExact(reader.GetString(1), "D"),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)));

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
