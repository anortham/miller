using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

namespace Miller.Indexing;

public enum StoreSidecarKind
{
    Search,
    Content,
    Vector,
}

public sealed record StoreSidecarStamp(
    StoreSidecarKind Kind,
    string FamilyId,
    string ViewId,
    string ManifestHash,
    long StoreLogSequence,
    // razorback: resolution_stamp can drop in a future stamp-schema bump; read-normalization keeps pre-change stamps current.
    string? ResolutionStamp,
    string StoreInstanceId,
    string GenerationName,
    long ManifestGeneration,
    string IndexLevel,
    string LevelStampL1,
    string LevelStampL2,
    string LevelStampL3)
{
    internal const string RetiredResolutionStamp = "retired";

    internal static string NormalizeResolutionStamp(string? persisted)
    {
        _ = persisted;
        return RetiredResolutionStamp;
    }

    public string ScopeToken =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ScopeMaterial(this))));

    public static StoreSidecarStamp FromSnapshot(StoreSidecarKind kind, WorkspaceReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Mode != WorkspaceReadMode.FamilyStore)
            throw new ArgumentException("A family-store sidecar stamp requires a family-store snapshot.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.Freshness.ManifestHash) ||
            snapshot.Freshness.StoreLogSequence is null ||
            string.IsNullOrWhiteSpace(snapshot.Freshness.StoreInstanceId) ||
            string.IsNullOrWhiteSpace(snapshot.Freshness.ViewId) ||
            string.IsNullOrWhiteSpace(snapshot.Freshness.GenerationName) ||
            snapshot.Freshness.ManifestGeneration is null ||
            string.IsNullOrWhiteSpace(snapshot.Freshness.IndexLevel) ||
            string.IsNullOrWhiteSpace(snapshot.Freshness.LevelStampL1) ||
            string.IsNullOrWhiteSpace(snapshot.Freshness.LevelStampL2) ||
            string.IsNullOrWhiteSpace(snapshot.Freshness.LevelStampL3))
        {
            throw new ArgumentException("The family-store snapshot has no complete freshness token.", nameof(snapshot));
        }

        return new StoreSidecarStamp(
            kind,
            snapshot.ArtifactOrStoreId,
            snapshot.Freshness.ViewId,
            snapshot.Freshness.ManifestHash,
            snapshot.Freshness.StoreLogSequence.Value,
            RetiredResolutionStamp,
            snapshot.Freshness.StoreInstanceId,
            snapshot.Freshness.GenerationName,
            snapshot.Freshness.ManifestGeneration.Value,
            snapshot.Freshness.IndexLevel,
            snapshot.Freshness.LevelStampL1,
            snapshot.Freshness.LevelStampL2,
            snapshot.Freshness.LevelStampL3);
    }

    private static string ScopeMaterial(StoreSidecarStamp stamp)
    {
        var material = new System.Text.StringBuilder();
        AppendScopeField(material, ((int)stamp.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendScopeField(material, stamp.FamilyId);
        AppendScopeField(material, stamp.ViewId);
        AppendScopeField(material, stamp.ManifestHash);
        AppendScopeField(
            material,
            stamp.StoreLogSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendScopeField(material, stamp.ResolutionStamp);
        AppendScopeField(material, stamp.StoreInstanceId);
        AppendScopeField(material, stamp.GenerationName);
        AppendScopeField(
            material,
            stamp.ManifestGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendScopeField(material, stamp.IndexLevel);
        AppendScopeField(material, stamp.LevelStampL1);
        AppendScopeField(material, stamp.LevelStampL2);
        AppendScopeField(material, stamp.LevelStampL3);
        return material.ToString();
    }

    private static void AppendScopeField(System.Text.StringBuilder material, string? value)
    {
        if (value is null)
        {
            material.Append("-1:");
            return;
        }

        material.Append(value.Length).Append(':').Append(value);
    }
}

public static class StoreSidecarCatalog
{
    private const string StampSchema = """
        CREATE TABLE IF NOT EXISTS store_sidecar_stamp(
            singleton INTEGER PRIMARY KEY CHECK(singleton=1),
            kind TEXT NOT NULL,
            family_id TEXT NOT NULL,
            view_id TEXT NOT NULL,
            manifest_hash TEXT NOT NULL,
            store_log_sequence INTEGER NOT NULL CHECK(store_log_sequence>=0),
            resolution_stamp TEXT,
            store_instance_id TEXT NOT NULL,
            generation_name TEXT NOT NULL,
            manifest_generation INTEGER NOT NULL,
            index_level TEXT NOT NULL,
            level_stamp_l1 TEXT NOT NULL,
            level_stamp_l2 TEXT NOT NULL,
            level_stamp_l3 TEXT NOT NULL
        ) STRICT;
        """;

    /// <summary>
    /// The one directory every Miller-owned per-view sidecar of <paramref name="storeRoot"/> lives in. Anything
    /// that ENUMERATES the sidecars must resolve the directory through this, so it lists the same directory
    /// <see cref="PathFor"/> writes into.
    /// </summary>
    public static string DirectoryFor(string storeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        return Path.Combine(PathCanonicalizer.CanonicalizeRoot(storeRoot), "sidecars");
    }

    public static string PathFor(string storeRoot, StoreSidecarKind kind, string viewId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);
        string viewKey = ViewKey(viewId);
        return Path.Combine(DirectoryFor(storeRoot), $"{KindName(kind)}-{viewKey}.db");
    }

    /// <summary>The file-name key a view id maps to — the SHA-256 that names every sidecar of that view.</summary>
    public static string ViewKey(string viewId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(viewId)));
    }

    public static void Stamp(string databasePath, StoreSidecarStamp stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(stamp);
        string fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The sidecar must commit before its completeness stamp is published.", fullPath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        Stamp(connection, transaction, stamp);
        transaction.Commit();
    }

    internal static void Stamp(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoreSidecarStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(stamp);
        EnsureStampSchema(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO store_sidecar_stamp(
                singleton,kind,family_id,view_id,manifest_hash,store_log_sequence,resolution_stamp,
                store_instance_id,generation_name,manifest_generation,index_level,
                level_stamp_l1,level_stamp_l2,level_stamp_l3)
            VALUES (
                1,$kind,$family,$view,$manifest,$sequence,$resolution,
                $instance,$generation,$generation_number,$level,$level_l1,$level_l2,$level_l3)
            ON CONFLICT(singleton) DO UPDATE SET
                kind=excluded.kind,
                family_id=excluded.family_id,
                view_id=excluded.view_id,
                manifest_hash=excluded.manifest_hash,
                store_log_sequence=excluded.store_log_sequence,
                resolution_stamp=excluded.resolution_stamp,
                store_instance_id=excluded.store_instance_id,
                generation_name=excluded.generation_name,
                manifest_generation=excluded.manifest_generation,
                index_level=excluded.index_level,
                level_stamp_l1=excluded.level_stamp_l1,
                level_stamp_l2=excluded.level_stamp_l2,
                level_stamp_l3=excluded.level_stamp_l3;
            """;
        command.Parameters.AddWithValue("$kind", KindName(stamp.Kind));
        command.Parameters.AddWithValue("$family", stamp.FamilyId);
        command.Parameters.AddWithValue("$view", stamp.ViewId);
        command.Parameters.AddWithValue("$manifest", stamp.ManifestHash);
        command.Parameters.AddWithValue("$sequence", stamp.StoreLogSequence);
        command.Parameters.AddWithValue("$resolution", (object?)stamp.ResolutionStamp ?? DBNull.Value);
        command.Parameters.AddWithValue("$instance", stamp.StoreInstanceId);
        command.Parameters.AddWithValue("$generation", stamp.GenerationName);
        command.Parameters.AddWithValue("$generation_number", stamp.ManifestGeneration);
        command.Parameters.AddWithValue("$level", stamp.IndexLevel);
        command.Parameters.AddWithValue("$level_l1", stamp.LevelStampL1);
        command.Parameters.AddWithValue("$level_l2", stamp.LevelStampL2);
        command.Parameters.AddWithValue("$level_l3", stamp.LevelStampL3);
        command.ExecuteNonQuery();
    }

    public static StoreSidecarStamp? TryRead(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
            return null;
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT kind,family_id,view_id,manifest_hash,store_log_sequence,resolution_stamp,
                       store_instance_id,generation_name,manifest_generation,index_level,
                       level_stamp_l1,level_stamp_l2,level_stamp_l3
                FROM store_sidecar_stamp WHERE singleton=1;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return new StoreSidecarStamp(
                ParseKind(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                StoreSidecarStamp.NormalizeResolutionStamp(reader.IsDBNull(5) ? null : reader.GetString(5)),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt64(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12));
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException)
        {
            return null;
        }
    }

    public static bool IsCurrent(string databasePath, StoreSidecarStamp expected) =>
        TryRead(databasePath) == expected;

    internal const int ReadableOpenAttempts = 4;

    /// <summary>
    /// The newest stamp on <paramref name="databasePath"/> that matches <paramref name="live"/> kind, family,
    /// view, store instance, and GENERATION at an earlier store sequence. A different family, view, store
    /// instance, or generation is never last-good.
    ///
    /// <para>The generation test is load-bearing, not belt and braces. A generation promotion re-mints every
    /// symbol id, and the store log does NOT restart with it: the v4 store contract §13 opens the new
    /// generation's log at the predecessor's last sequence + 1 so sidecar cursors survive the flip. Family, view,
    /// and "strictly lower sequence" therefore all still hold across a promotion, and without this test the
    /// previous generation's sidecar would pass as last-good and serve ids no live row carries.</para>
    /// </summary>
    public static StoreSidecarStamp? TryLastGood(string databasePath, StoreSidecarStamp live)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(live);
        return LastGoodOrNull(TryRead(databasePath), live);
    }

    private static StoreSidecarStamp? LastGoodOrNull(StoreSidecarStamp? stamp, StoreSidecarStamp live)
    {
        if (stamp is null)
            return null;
        if (stamp.Kind != live.Kind)
            return null;
        if (!string.Equals(stamp.FamilyId, live.FamilyId, StringComparison.Ordinal))
            return null;
        if (!string.Equals(stamp.ViewId, live.ViewId, StringComparison.Ordinal))
            return null;
        if (!string.Equals(stamp.StoreInstanceId, live.StoreInstanceId, StringComparison.Ordinal))
            return null;
        if (!string.Equals(stamp.GenerationName, live.GenerationName, StringComparison.Ordinal))
            return null;
        if (stamp.StoreLogSequence >= live.StoreLogSequence)
            return null;
        return stamp;
    }

    /// <summary>
    /// Last-good is allowed whenever the live snapshot is a readable family-store cursor, including
    /// <c>exact</c>. Sidecar rebuild can lag after resolution becomes exact.
    /// </summary>
    internal static bool AllowsLastGoodServe(WorkspaceReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Mode == WorkspaceReadMode.FamilyStore
            && snapshot.Freshness.StoreLogSequence is not null;
    }

    /// <summary>
    /// The stamp table is read ONCE here and compared against both rules. The lagging case is the common case on
    /// a converging workspace, so a second <see cref="TryRead"/> would open a second unpooled connection on every
    /// named read.
    /// </summary>
    internal static StoreSidecarStamp? TryResolveReadable(
        string databasePath,
        StoreSidecarStamp expected,
        WorkspaceReadSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(snapshot);
        StoreSidecarStamp? stamp = TryRead(databasePath);
        if (stamp == expected)
            return expected;
        if (!AllowsLastGoodServe(snapshot))
            return null;
        return LastGoodOrNull(stamp, expected);
    }

    internal static bool TryFastForwardEmptyDelta(
        string databasePath,
        StoreSidecarStamp expected,
        IWorkspaceReadSession session,
        Func<SqliteConnection, SqliteTransaction, long, bool> updateMetadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(updateMetadata);

        StoreSidecarStamp? previous = TryRead(databasePath);
        if (previous is null ||
            previous.StoreLogSequence >= expected.StoreLogSequence ||
            previous with
            {
                StoreLogSequence = expected.StoreLogSequence,
                ResolutionStamp = expected.ResolutionStamp,
            } != expected)
        {
            return false;
        }

        RevisionDeltaResult delta = RevisionDeltaReader.Read(
            session,
            previous.StoreLogSequence,
            previous.FamilyId);
        if (delta.Status != RevisionDeltaStatus.Complete ||
            delta.ToRevision != expected.StoreLogSequence ||
            delta.ChangedPaths.Count != 0 ||
            delta.DeletedPaths is not { Count: 0 })
        {
            return false;
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (Read(connection, transaction) != previous ||
                !updateMetadata(connection, transaction, expected.StoreLogSequence))
            {
                return false;
            }

            Stamp(connection, transaction, expected);
            transaction.Commit();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static void EnsureStampSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = StampSchema;
            create.ExecuteNonQuery();
        }

        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var columnsCommand = connection.CreateCommand())
        {
            columnsCommand.Transaction = transaction;
            columnsCommand.CommandText = "PRAGMA table_info(store_sidecar_stamp);";
            using SqliteDataReader reader = columnsCommand.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }

        AddColumn(connection, transaction, columns, "store_instance_id", "TEXT NOT NULL DEFAULT ''");
        AddColumn(connection, transaction, columns, "generation_name", "TEXT NOT NULL DEFAULT ''");
        AddColumn(connection, transaction, columns, "manifest_generation", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(connection, transaction, columns, "index_level", "TEXT NOT NULL DEFAULT ''");
        AddColumn(connection, transaction, columns, "level_stamp_l1", "TEXT NOT NULL DEFAULT ''");
        AddColumn(connection, transaction, columns, "level_stamp_l2", "TEXT NOT NULL DEFAULT ''");
        AddColumn(connection, transaction, columns, "level_stamp_l3", "TEXT NOT NULL DEFAULT ''");
    }

    private static StoreSidecarStamp? Read(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT kind,family_id,view_id,manifest_hash,store_log_sequence,resolution_stamp,
                   store_instance_id,generation_name,manifest_generation,index_level,
                   level_stamp_l1,level_stamp_l2,level_stamp_l3
            FROM store_sidecar_stamp WHERE singleton=1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new StoreSidecarStamp(
            ParseKind(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            StoreSidecarStamp.NormalizeResolutionStamp(reader.IsDBNull(5) ? null : reader.GetString(5)),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12));
    }

    private static void AddColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ISet<string> columns,
        string name,
        string definition)
    {
        if (!columns.Add(name))
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"ALTER TABLE store_sidecar_stamp ADD COLUMN {name} {definition};";
        command.ExecuteNonQuery();
    }

    private static string KindName(StoreSidecarKind kind) => kind switch
    {
        StoreSidecarKind.Search => "search",
        StoreSidecarKind.Content => "content",
        StoreSidecarKind.Vector => "vector",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static StoreSidecarKind ParseKind(string value) => value switch
    {
        "search" => StoreSidecarKind.Search,
        "content" => StoreSidecarKind.Content,
        "vector" => StoreSidecarKind.Vector,
        _ => throw new InvalidDataException($"Unknown store sidecar kind '{value}'."),
    };
}
