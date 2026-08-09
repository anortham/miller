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
    string? ResolutionStamp)
{
    public static StoreSidecarStamp FromSnapshot(StoreSidecarKind kind, WorkspaceReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Mode != WorkspaceReadMode.FamilyStore)
            throw new ArgumentException("A family-store sidecar stamp requires a family-store snapshot.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.Freshness.ManifestHash) ||
            snapshot.Freshness.StoreLogSequence is null)
        {
            throw new ArgumentException("The family-store snapshot has no complete manifest/log freshness token.", nameof(snapshot));
        }

        return new StoreSidecarStamp(
            kind,
            snapshot.ArtifactOrStoreId,
            snapshot.ViewId,
            snapshot.Freshness.ManifestHash,
            snapshot.Freshness.StoreLogSequence.Value,
            snapshot.Freshness.ResolutionStamp);
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
            resolution_stamp TEXT
        ) STRICT;
        """;

    public static string PathFor(string storeRoot, StoreSidecarKind kind, string viewId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(storeRoot);
        string viewKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(viewId)));
        return Path.Combine(canonicalRoot, "sidecars", $"{KindName(kind)}-{viewKey}.db");
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
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = StampSchema + """
            INSERT INTO store_sidecar_stamp(
                singleton,kind,family_id,view_id,manifest_hash,store_log_sequence,resolution_stamp)
            VALUES (1,$kind,$family,$view,$manifest,$sequence,$resolution)
            ON CONFLICT(singleton) DO UPDATE SET
                kind=excluded.kind,
                family_id=excluded.family_id,
                view_id=excluded.view_id,
                manifest_hash=excluded.manifest_hash,
                store_log_sequence=excluded.store_log_sequence,
                resolution_stamp=excluded.resolution_stamp;
            """;
        command.Parameters.AddWithValue("$kind", KindName(stamp.Kind));
        command.Parameters.AddWithValue("$family", stamp.FamilyId);
        command.Parameters.AddWithValue("$view", stamp.ViewId);
        command.Parameters.AddWithValue("$manifest", stamp.ManifestHash);
        command.Parameters.AddWithValue("$sequence", stamp.StoreLogSequence);
        command.Parameters.AddWithValue("$resolution", (object?)stamp.ResolutionStamp ?? DBNull.Value);
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
                SELECT kind,family_id,view_id,manifest_hash,store_log_sequence,resolution_stamp
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
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public static bool IsCurrent(string databasePath, StoreSidecarStamp expected) =>
        TryRead(databasePath) == expected;

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
