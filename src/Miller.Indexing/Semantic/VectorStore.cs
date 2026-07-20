using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing.Semantic;

/// <summary>One KNN hit: the vec0 rowid and its distance, plus the mapping row's unit id.</summary>
public sealed record VectorMatch(long RowId, double Distance, string UnitId, string Path);

/// <summary>One mapping row: the unit a vec0 rowid stands for, and the hash that gates its re-embedding.</summary>
public sealed record VectorMapEntry(string UnitId, string Path, string EmbedTextHash);

/// <summary>One unit staged for <see cref="VectorStore.CommitBatch"/>: its vec0 metadata, its vector, and the
/// hash of the constructed text the vector came from.</summary>
public sealed record VectorBatchEntry(
    string UnitId,
    string Path,
    string SymbolKind,
    bool IsTest,
    sbyte[] Embedding,
    string EmbedTextHash);

/// <summary>The two corpora that get their own vec0 table and mapping table.</summary>
public enum VectorUnitKind
{
    Symbol,
    Chunk,
}

/// <summary>
/// The physical <c>vectors.db</c> artifact of vectors-v1 §Storage schema: the <c>vectors_meta</c> key/value
/// table, the <c>symbol_vectors</c>/<c>chunk_vectors</c> vec0 tables whose element type and dims are derived
/// from the <c>storage_schema</c> lane, and the mapping tables that carry the glob-resolution <c>path</c>.
/// </summary>
/// <remarks>
/// Every connection loads the pinned sqlite-vec loadable extension from an absolute path and verifies
/// <c>vec_version()</c> against <see cref="PinnedVecVersion"/> at open — a mismatch is a stated failure, never
/// a silent downgrade. Connections open with pooling disabled, exactly as extract-DB opens do, so wholesale
/// file replacement during a promote stays safe.
/// </remarks>
public sealed class VectorStore : IDisposable
{
    /// <summary>The sqlite-vec version pinned per-RID in <c>scripts/spike-pins.json</c>.</summary>
    public const string PinnedVecVersion = "0.1.9";

    /// <summary>Overrides the packaged extension path for development and tests.</summary>
    public const string ExtensionPathEnvVar = "MILLER_SQLITE_VEC_PATH";

    private static readonly string[] RequiredMetaKeys =
    [
        "contract_version",
        "encoder_fingerprint",
        "storage_schema",
        "corpus_generation",
    ];

    private readonly SqliteConnection _connection;

    private VectorStore(SqliteConnection connection, SemanticGenerationIdentity identity, SemanticStorageLane lane, string vecVersion)
    {
        _connection = connection;
        Identity = identity;
        Lane = lane;
        VecVersion = vecVersion;
    }

    public SemanticGenerationIdentity Identity { get; }

    public SemanticStorageLane Lane { get; }

    public string VecVersion { get; }

    /// <summary>The packaged loadable-extension file name for the host platform.</summary>
    public static string PackagedExtensionFileName =>
        OperatingSystem.IsWindows() ? "vec0.dll"
        : OperatingSystem.IsMacOS() ? "vec0.dylib"
        : "vec0.so";

    /// <summary>
    /// Resolves the loadable extension from the running app's base directory: <see cref="ExtensionPathEnvVar"/>
    /// when set, otherwise the packaged <c>.tools/vec0.*</c> a release archive carries, otherwise none — an
    /// unrestored build gets a stated reason rather than a silent lexical downgrade.
    /// </summary>
    public static string? ResolveExtensionPath() => ResolveExtensionPath(AppContext.BaseDirectory);

    /// <summary>
    /// Resolves the loadable extension relative to <paramref name="baseDirectory"/>, the packaged layout's
    /// root (<c>&lt;baseDirectory&gt;/.tools/vec0.&lt;ext&gt;</c>). The environment override keeps absolute
    /// precedence over whatever is packaged.
    /// </summary>
    public static string? ResolveExtensionPath(string baseDirectory)
    {
        if (Environment.GetEnvironmentVariable(ExtensionPathEnvVar) is { Length: > 0 } configured)
        {
            return configured;
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        string packaged = Path.Combine(baseDirectory, ".tools", PackagedExtensionFileName);
        return File.Exists(packaged) ? packaged : null;
    }

    /// <summary>Creates a fresh artifact at <paramref name="path"/> stamped with <paramref name="identity"/>.</summary>
    public static VectorStore Create(
        string path,
        SemanticGenerationIdentity identity,
        string artifactId,
        string extensionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);

        SemanticStorageLane lane = MillerSemanticContract.ParseStorageSchema(identity.StorageSchema);
        SqliteConnection connection = OpenConnection(path, SqliteOpenMode.ReadWriteCreate, extensionPath, out string vecVersion);

        try
        {
            Guard($"create the schema at '{path}'", () =>
            {
                Execute(connection, SchemaDdl(lane));
                WriteMeta(connection, InitialMeta(identity, artifactId));
            });

            return new VectorStore(connection, identity, lane, vecVersion);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an existing artifact, verifying <c>vec_version()</c> against the pin and that
    /// <c>vectors_meta</c> carries every key a reader must have. A missing or unreadable meta table is
    /// corruption-shaped and surfaces as <see cref="VectorStoreException"/>.
    /// </summary>
    public static VectorStore Open(string path, string extensionPath, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        SqliteConnection connection = OpenConnection(
            path,
            readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            extensionPath,
            out string vecVersion);

        try
        {
            IReadOnlyDictionary<string, string> meta = ReadMeta(connection);
            SemanticGenerationIdentity identity = IdentityFrom(meta);
            SemanticStorageLane lane = MillerSemanticContract.ParseStorageSchema(identity.StorageSchema);
            return new VectorStore(connection, identity, lane, vecVersion);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads <c>vectors_meta</c> without constructing a store — the probe the sidecar's open path uses to
    /// classify an artifact it may turn out to be unable to serve.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadMetaAt(string path, string extensionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using SqliteConnection connection = OpenConnection(path, SqliteOpenMode.ReadOnly, extensionPath, out _);
        return ReadMeta(connection);
    }

    public string? Meta(string key) => Guard("read vectors_meta", () =>
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM vectors_meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    });

    public void SetMeta(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        Guard("write vectors_meta", () =>
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO vectors_meta(key, value) VALUES($key, $value) " +
                                  "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Writes one unit's vector and its mapping row in a single transaction, so no observable state has a
    /// vec0 row without the mapping row that explains it.
    /// </summary>
    public void Upsert(
        VectorUnitKind kind,
        long rowId,
        string unitId,
        string path,
        ReadOnlySpan<sbyte> embedding,
        string embedTextHash,
        long revision,
        string symbolKind,
        bool isTest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(embedTextHash);

        if (embedding.Length != Lane.Dims)
        {
            throw new VectorStoreException(
                $"embedding has {embedding.Length} dims but lane '{Lane.Lane}' declares {Lane.Dims}.");
        }

        byte[] blob = QuantizedBlob(embedding);
        string vectors = VectorTable(kind);
        string map = MapTable(kind);
        string idColumn = IdColumn(kind);

        string vectorLiteral = VectorLiteral();

        Guard($"upsert into {vectors}", () =>
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();

            Execute(_connection, transaction, $"DELETE FROM {vectors} WHERE rowid = $rowid", ("$rowid", rowId));
            Execute(_connection, transaction, $"DELETE FROM {map} WHERE rowid_ref = $rowid", ("$rowid", rowId));

            Execute(
                _connection,
                transaction,
                $"INSERT INTO {vectors}(rowid, embedding, path, kind, is_test) " +
                $"VALUES($rowid, {vectorLiteral}, $path, $kind, $is_test)",
                ("$rowid", rowId),
                ("$embedding", blob),
                ("$path", path),
                ("$kind", symbolKind),
                ("$is_test", isTest ? 1L : 0L));

            Execute(
                _connection,
                transaction,
                $"INSERT INTO {map}(rowid_ref, {idColumn}, path, embed_text_hash, revision) " +
                "VALUES($rowid, $unit_id, $path, $hash, $revision)",
                ("$rowid", rowId),
                ("$unit_id", unitId),
                ("$path", path),
                ("$hash", embedTextHash),
                ("$revision", revision));

            transaction.Commit();
        });
    }

    /// <summary>Every <c>vectors_meta</c> key/value, re-read from the artifact.</summary>
    public IReadOnlyDictionary<string, string> AllMeta() => ReadMeta(_connection);

    /// <summary>
    /// The five identity fields as the artifact currently records them. Unlike <see cref="Identity"/>, which is
    /// the snapshot taken at open, this re-reads — the writer uses it to prove the generation it embedded for is
    /// still the generation it is about to commit to.
    /// </summary>
    public SemanticGenerationIdentity ReadIdentity() => IdentityFrom(AllMeta());

    /// <summary>The mapping rows for <paramref name="paths"/>, or the whole corpus when it is null — the
    /// hash-gate input that makes a re-embed targeted and a replay idempotent.</summary>
    public IReadOnlyList<VectorMapEntry> MappedUnits(VectorUnitKind kind, IReadOnlyCollection<string>? paths)
    {
        string table = MapTable(kind);
        string idColumn = IdColumn(kind);

        return Guard($"read {table}", () =>
        {
            var entries = new List<VectorMapEntry>();
            foreach (IReadOnlyList<string>? batch in Batched(paths))
            {
                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = batch is null
                    ? $"SELECT {idColumn}, path, embed_text_hash FROM {table}"
                    : $"SELECT {idColumn}, path, embed_text_hash FROM {table} WHERE path IN ({Placeholders(batch, command)})";

                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    entries.Add(new VectorMapEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            return (IReadOnlyList<VectorMapEntry>)entries;
        });
    }

    public int MappedCount(VectorUnitKind kind) => Guard($"count {MapTable(kind)}", () =>
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {MapTable(kind)}";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    });

    /// <summary>
    /// The one short transaction of vectors-v1 §Cursors: vec0 deletes, vec0 inserts, mapping-table updates and
    /// the cursor advance commit together, so no observable state has a cursor ahead of its staged batch or a
    /// vec0 row without the mapping row that explains it.
    /// </summary>
    public void CommitBatch(
        VectorUnitKind kind,
        IReadOnlyList<VectorBatchEntry> vectors,
        IReadOnlyList<string> deletes,
        IReadOnlyDictionary<string, string> metaUpdates,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(deletes);
        ArgumentNullException.ThrowIfNull(metaUpdates);

        foreach (VectorBatchEntry entry in vectors)
        {
            if (entry.Embedding.Length != Lane.Dims)
            {
                throw new VectorStoreException(
                    $"embedding has {entry.Embedding.Length} dims but lane '{Lane.Lane}' declares {Lane.Dims}.");
            }
        }

        string vectorTable = VectorTable(kind);
        string mapTable = MapTable(kind);
        string idColumn = IdColumn(kind);

        string vectorLiteral = VectorLiteral();

        Guard($"commit batch into {vectorTable}", () =>
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();

            long nextRowId = NextRowId(transaction, mapTable);
            foreach (string unitId in deletes.Concat(vectors.Select(static entry => entry.UnitId)))
            {
                if (ResolveRowId(transaction, mapTable, idColumn, unitId) is not { } rowId)
                    continue;

                Execute(_connection, transaction, $"DELETE FROM {vectorTable} WHERE rowid = $rowid", ("$rowid", rowId));
                Execute(_connection, transaction, $"DELETE FROM {mapTable} WHERE rowid_ref = $rowid", ("$rowid", rowId));
            }

            foreach (VectorBatchEntry entry in vectors)
            {
                long rowId = nextRowId++;
                Execute(
                    _connection,
                    transaction,
                    $"INSERT INTO {vectorTable}(rowid, embedding, path, kind, is_test) " +
                    $"VALUES($rowid, {vectorLiteral}, $path, $kind, $is_test)",
                    ("$rowid", rowId),
                    ("$embedding", QuantizedBlob(entry.Embedding)),
                    ("$path", entry.Path),
                    ("$kind", entry.SymbolKind),
                    ("$is_test", entry.IsTest ? 1L : 0L));

                Execute(
                    _connection,
                    transaction,
                    $"INSERT INTO {mapTable}(rowid_ref, {idColumn}, path, embed_text_hash, revision) " +
                    "VALUES($rowid, $unit_id, $path, $hash, $revision)",
                    ("$rowid", rowId),
                    ("$unit_id", entry.UnitId),
                    ("$path", entry.Path),
                    ("$hash", entry.EmbedTextHash),
                    ("$revision", revision));
            }

            foreach ((string key, string value) in metaUpdates)
            {
                Execute(
                    _connection,
                    transaction,
                    "INSERT INTO vectors_meta(key, value) VALUES($key, $value) " +
                    "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                    ("$key", key),
                    ("$value", value));
            }

            transaction.Commit();
        });
    }

    /// <summary>
    /// KNN over one corpus. Results are ordered by distance then by integer vec0 rowid, so ties never resolve
    /// arbitrarily (vectors-v1 §Query rules, Determinism).
    /// </summary>
    public IReadOnlyList<VectorMatch> Search(VectorUnitKind kind, ReadOnlySpan<sbyte> query, int k)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (query.Length != Lane.Dims)
        {
            throw new VectorStoreException(
                $"query has {query.Length} dims but lane '{Lane.Lane}' declares {Lane.Dims}.");
        }

        string sql =
            $"SELECT v.rowid, v.distance, m.{IdColumn(kind)}, m.path " +
            $"FROM {VectorTable(kind)} v JOIN {MapTable(kind)} m ON m.rowid_ref = v.rowid " +
            $"WHERE v.embedding MATCH {VectorLiteral("$query")} AND k = $k";
        byte[] blob = QuantizedBlob(query);

        return Guard($"KNN over {VectorTable(kind)}", () =>
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$query", blob);
            command.Parameters.AddWithValue("$k", k);

            var matches = new List<VectorMatch>();
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    matches.Add(new VectorMatch(
                        reader.GetInt64(0),
                        reader.GetDouble(1),
                        reader.GetString(2),
                        reader.GetString(3)));
                }
            }

            matches.Sort(static (left, right) =>
            {
                int byDistance = left.Distance.CompareTo(right.Distance);
                return byDistance != 0 ? byDistance : left.RowId.CompareTo(right.RowId);
            });
            return (IReadOnlyList<VectorMatch>)matches;
        });
    }

    /// <summary>
    /// The <c>rowid_ref</c> set whose mapping-table <c>path</c> matches a glob. vec0 metadata columns support
    /// no <c>LIKE</c>/<c>GLOB</c>, so glob scoping resolves here first and brute-forces distance over the
    /// subset rather than approximating with oversampled KNN.
    /// </summary>
    public IReadOnlyList<long> ResolveGlob(VectorUnitKind kind, string glob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(glob);

        return Guard($"glob-resolve {MapTable(kind)}", () =>
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = $"SELECT rowid_ref FROM {MapTable(kind)} WHERE path GLOB $glob ORDER BY rowid_ref";
            command.Parameters.AddWithValue("$glob", glob);

            var rowIds = new List<long>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                rowIds.Add(reader.GetInt64(0));
            return (IReadOnlyList<long>)rowIds;
        });
    }

    public IReadOnlyList<string> TableColumns(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        return Guard($"read the columns of {table}", () =>
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid";

            var columns = new List<string>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(0));
            return (IReadOnlyList<string>)columns;
        });
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// The storage boundary: every SQLite fault a public member can hit — a dropped or corrupt table, an
    /// unreadable file, a locked database — leaves here as <see cref="VectorStoreException"/>. Callers such as
    /// the fail-open retrieval arm catch that one type; a raw <see cref="SqliteException"/> escaping instead
    /// would propagate past them and break a lexical result the semantic arm is only supposed to augment.
    /// </summary>
    private static T Guard<T>(string operation, Func<T> work)
    {
        try
        {
            return work();
        }
        catch (SqliteException ex)
        {
            throw new VectorStoreException($"the vector artifact could not {operation}: {ex.Message}", ex);
        }
    }

    private static void Guard(string operation, Action work) =>
        Guard(operation, () =>
        {
            work();
            return true;
        });

    internal static string SchemaDdl(SemanticStorageLane lane)
    {
        string declaration = $"{lane.Element}[{lane.Dims.ToString(CultureInfo.InvariantCulture)}] distance_metric={lane.Metric}";

        return $"""
            CREATE TABLE vectors_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            ) STRICT;

            CREATE VIRTUAL TABLE symbol_vectors USING vec0(
                embedding {declaration},
                path      TEXT,
                kind      TEXT,
                is_test   INTEGER
            );

            CREATE VIRTUAL TABLE chunk_vectors USING vec0(
                embedding {declaration},
                path      TEXT,
                kind      TEXT,
                is_test   INTEGER
            );

            CREATE TABLE symbol_vector_map (
                rowid_ref       INTEGER PRIMARY KEY,
                symbol_id       TEXT NOT NULL UNIQUE,
                path            TEXT NOT NULL,
                embed_text_hash TEXT NOT NULL,
                revision        INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE chunk_vector_map (
                rowid_ref       INTEGER PRIMARY KEY,
                chunk_id        TEXT NOT NULL UNIQUE,
                path            TEXT NOT NULL,
                embed_text_hash TEXT NOT NULL,
                revision        INTEGER NOT NULL
            ) STRICT;

            CREATE INDEX symbol_vector_map_path     ON symbol_vector_map(path);
            CREATE INDEX symbol_vector_map_revision ON symbol_vector_map(revision);
            CREATE INDEX chunk_vector_map_path      ON chunk_vector_map(path);
            CREATE INDEX chunk_vector_map_revision  ON chunk_vector_map(revision);
            """;
    }

    internal static SemanticGenerationIdentity IdentityFrom(IReadOnlyDictionary<string, string> meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        List<string> missing = [.. RequiredMetaKeys.Where(key => !meta.ContainsKey(key))];
        if (missing.Count > 0)
        {
            throw new VectorStoreException(
                $"vectors_meta is missing required key(s): {string.Join(", ", missing)}.");
        }

        return new SemanticGenerationIdentity(
            meta["encoder_fingerprint"],
            meta["storage_schema"],
            meta["corpus_generation"],
            meta.GetValueOrDefault("writer_version", string.Empty),
            meta.GetValueOrDefault("min_reader_version", string.Empty),
            meta.GetValueOrDefault("fusion_profile", string.Empty));
    }

    private static IReadOnlyDictionary<string, string> InitialMeta(SemanticGenerationIdentity identity, string artifactId) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contract_version"] = MillerSemanticContract.ContractVersion,
            ["encoder_fingerprint"] = identity.EncoderFingerprint,
            ["storage_schema"] = identity.StorageSchema,
            ["corpus_generation"] = identity.CorpusGeneration,
            ["writer_version"] = identity.WriterVersion,
            ["min_reader_version"] = identity.MinReaderVersion,
            ["fusion_profile"] = identity.FusionProfile,
            ["artifact_id"] = artifactId,
            ["hash_algorithm"] = MillerSemanticContract.HashAlgorithm,
            ["symbol_completed_revision"] = "0",
            ["symbol_target_revision"] = "0",
            ["chunk_completed_revision"] = "0",
            ["chunk_target_revision"] = "0",
            ["chunk_content_schema_version"] = ContentCorpusSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            ["chunk_source_artifact_id"] = artifactId,
            ["build_state"] = "building",
            ["build_progress_percent"] = "0",
        };

    private static SqliteConnection OpenConnection(
        string path,
        SqliteOpenMode mode,
        string extensionPath,
        out string vecVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionPath);

        if (!Path.IsPathRooted(extensionPath))
            throw new VectorStoreException($"sqlite-vec extension path must be absolute: '{extensionPath}'.");
        if (!File.Exists(extensionPath))
            throw new VectorStoreException($"sqlite-vec extension not found at '{extensionPath}'.");

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());

        try
        {
            connection.Open();
            connection.EnableExtensions(true);
            connection.LoadExtension(extensionPath);
            connection.EnableExtensions(false);

            string reported = ScalarString(connection, "SELECT vec_version()")
                ?? throw new VectorStoreException("vec_version() returned NULL after loading the sqlite-vec extension.");

            vecVersion = NormalizeVecVersion(reported);
            if (!string.Equals(vecVersion, PinnedVecVersion, StringComparison.Ordinal))
            {
                throw new VectorStoreException(
                    $"sqlite-vec {reported} != pinned {PinnedVecVersion}.");
            }

            if (mode != SqliteOpenMode.ReadOnly)
                Execute(connection, "PRAGMA journal_mode=WAL;");

            return connection;
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new VectorStoreException($"the vector artifact at '{path}' could not be opened: {ex.Message}", ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>sqlite-vec reports its version as <c>v0.1.9</c>; the pin file records it without the tag.</summary>
    internal static string NormalizeVecVersion(string reported) =>
        reported.StartsWith('v') ? reported[1..] : reported;

    private static IReadOnlyDictionary<string, string> ReadMeta(SqliteConnection connection)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM vectors_meta";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                meta[reader.GetString(0)] = reader.GetString(1);
        }
        catch (SqliteException ex)
        {
            throw new VectorStoreException($"vectors_meta is unreadable: {ex.Message}", ex);
        }

        if (meta.Count == 0)
            throw new VectorStoreException("vectors_meta is missing or empty.");

        return meta;
    }

    private static void WriteMeta(SqliteConnection connection, IReadOnlyDictionary<string, string> meta)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach ((string key, string value) in meta)
        {
            Execute(
                connection,
                transaction,
                "INSERT INTO vectors_meta(key, value) VALUES($key, $value)",
                ("$key", key),
                ("$value", value));
        }

        transaction.Commit();
    }

    /// <summary>
    /// Tags a bound BLOB with the lane's element type. sqlite-vec reads an untagged BLOB as float32, so an
    /// int8 lane silently rejects (or worse, misreads) a raw byte blob without this constructor.
    /// </summary>
    private string VectorLiteral(string parameter = "$embedding") => Lane.Element switch
    {
        "int8" => $"vec_int8({parameter})",
        "float" or "float32" => $"vec_f32({parameter})",
        "bit" => $"vec_bit({parameter})",
        _ => throw new VectorStoreException($"lane '{Lane.Lane}' declares unsupported element type '{Lane.Element}'."),
    };

    private static byte[] QuantizedBlob(ReadOnlySpan<sbyte> embedding)
    {
        var blob = new byte[embedding.Length];
        for (int i = 0; i < embedding.Length; i++)
            blob[i] = unchecked((byte)embedding[i]);
        return blob;
    }

    private long NextRowId(SqliteTransaction transaction, string mapTable)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COALESCE(MAX(rowid_ref), 0) + 1 FROM {mapTable}";
        return Convert.ToInt64(command.ExecuteScalar() ?? (object)1L, CultureInfo.InvariantCulture);
    }

    private long? ResolveRowId(SqliteTransaction transaction, string mapTable, string idColumn, string unitId)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT rowid_ref FROM {mapTable} WHERE {idColumn} = $id";
        command.Parameters.AddWithValue("$id", unitId);
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    // A null batch means "no path filter"; otherwise SQLite's parameter ceiling is respected by chunking.
    private static IEnumerable<IReadOnlyList<string>?> Batched(IReadOnlyCollection<string>? paths)
    {
        if (paths is null)
        {
            yield return null;
            yield break;
        }

        const int batchSize = 400;
        string[] ordered = [.. paths];
        for (int offset = 0; offset < ordered.Length; offset += batchSize)
            yield return ordered[offset..Math.Min(offset + batchSize, ordered.Length)];
    }

    private static string Placeholders(IReadOnlyList<string> values, SqliteCommand command)
    {
        var names = new List<string>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            string name = $"$p{i.ToString(CultureInfo.InvariantCulture)}";
            command.Parameters.AddWithValue(name, values[i]);
            names.Add(name);
        }

        return names.Count == 0 ? "NULL" : string.Join(", ", names);
    }

    private static string VectorTable(VectorUnitKind kind) =>
        kind is VectorUnitKind.Symbol ? "symbol_vectors" : "chunk_vectors";

    private static string MapTable(VectorUnitKind kind) =>
        kind is VectorUnitKind.Symbol ? "symbol_vector_map" : "chunk_vector_map";

    private static string IdColumn(VectorUnitKind kind) =>
        kind is VectorUnitKind.Symbol ? "symbol_id" : "chunk_id";

    private static string? ScalarString(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}

/// <summary>A stated failure to create, open, or use the vector artifact — never a silent downgrade.</summary>
public sealed class VectorStoreException : Exception
{
    public VectorStoreException(string message)
        : base(message)
    {
    }

    public VectorStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
