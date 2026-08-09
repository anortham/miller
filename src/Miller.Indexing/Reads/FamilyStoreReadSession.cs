using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Miller.Indexing.Store;

namespace Miller.Indexing.Reads;

public enum FamilyStoreReadFailure
{
    BindingNotReady,
    CurrentMissing,
    CurrentMalformed,
    GenerationMissing,
    StoreMissing,
    CoordinatorMissing,
    SchemaIncompatible,
    ReaderFloorIncompatible,
    FamilyMismatch,
    ViewNotFound,
    ViewRootMismatch,
    ManifestMissing,
    Corrupt,
}

public sealed class FamilyStoreReadException(
    FamilyStoreReadFailure failure,
    string message,
    Exception? innerException = null) : IOException(message, innerException)
{
    public FamilyStoreReadFailure Failure { get; } = failure;
}

public sealed class FamilyStoreReadSession : IWorkspaceReadSession
{
    private const int StoreSchemaVersion = 2;
    private const int StoreFormatEpoch = 1;
    private static readonly Regex GenerationName = new(
        @"^gen-[0-9]{3,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    private FamilyStoreReadSession(
        SqliteConnection connection,
        StoreVisibility visibility,
        WorkspaceReadSnapshot snapshot)
    {
        _connection = connection;
        Visibility = visibility;
        Snapshot = snapshot;
    }

    public StoreVisibility Visibility { get; }

    public WorkspaceReadSnapshot Snapshot { get; }

    public static FamilyStoreReadSession Open(
        StoreFamilyBinding binding,
        string? workspaceId = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.State != StoreBindingState.Ready)
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.BindingNotReady,
                "The workspace family-store binding is not ready for reads.");

        try
        {
            string storeRoot = PathCanonicalizer.CanonicalizeRoot(binding.StoreRoot);
            string workspaceRoot = PathCanonicalizer.CanonicalizeRoot(binding.WorkspaceRoot);
            string currentPath = CanonicalizeContained(
                storeRoot,
                Path.Combine(storeRoot, "CURRENT"),
                "The family store CURRENT pointer escapes its root.");
            if (!File.Exists(currentPath))
                throw new FamilyStoreReadException(
                    FamilyStoreReadFailure.CurrentMissing,
                    "The family store is missing its CURRENT pointer.");

            string generationName = File.ReadAllText(currentPath).Trim();
            if (!GenerationName.IsMatch(generationName))
                throw new FamilyStoreReadException(
                    FamilyStoreReadFailure.CurrentMalformed,
                    $"The family store CURRENT pointer '{generationName}' is malformed.");

            string generationPath = CanonicalizeContained(
                storeRoot,
                Path.Combine(storeRoot, generationName),
                "The serving family-store generation escapes its root.");
            if (!Directory.Exists(generationPath))
                throw new FamilyStoreReadException(
                    FamilyStoreReadFailure.GenerationMissing,
                    $"The serving family-store generation '{generationName}' is missing.");

            string storeDatabasePath = CanonicalizeContained(
                generationPath,
                Path.Combine(generationPath, "store.db"),
                "The serving family-store database escapes its generation.");
            if (!File.Exists(storeDatabasePath))
                throw new FamilyStoreReadException(
                    FamilyStoreReadFailure.StoreMissing,
                    "The serving family-store generation has no store.db.");
            string coordinatorDatabasePath = CanonicalizeContained(
                storeRoot,
                Path.Combine(storeRoot, "coord.db"),
                "The family-store coordinator database escapes its root.");
            if (!File.Exists(coordinatorDatabasePath))
                throw new FamilyStoreReadException(
                    FamilyStoreReadFailure.CoordinatorMissing,
                    "The family store has no coordinator database.");

            SqliteConnection connection = OpenReadOnly(storeDatabasePath);
            try
            {
                Dictionary<string, string> metadata = ReadStoreMetadata(connection);
                ValidateStoreMetadata(metadata, binding);
                StoreVisibility visibility = ReadVisibility(
                    connection,
                    binding,
                    storeRoot,
                    generationName,
                    storeDatabasePath,
                    coordinatorDatabasePath,
                    workspaceRoot);
                bool resolutionAttached = AttachValidatedResolutionBase(connection, visibility);
                CreateCompatibilityProjection(connection, visibility, metadata, resolutionAttached);
                SetQueryOnly(connection);
                var freshness = new WorkspaceFreshnessToken(
                    visibility.FamilyId,
                    visibility.ManifestGeneration,
                    visibility.ManifestHash,
                    visibility.StoreLogSequence,
                    ResolutionStamp(visibility),
                    StoreInstanceId: visibility.StoreInstanceId,
                    ViewId: visibility.ViewId,
                    GenerationName: visibility.GenerationName,
                    ManifestGeneration: visibility.ManifestGeneration,
                    IndexLevel: visibility.IndexLevel,
                    LevelStampL1: visibility.LevelStampL1,
                    LevelStampL2: visibility.LevelStampL2,
                    LevelStampL3: visibility.LevelStampL3);
                var snapshot = new WorkspaceReadSnapshot(
                    visibility.WorkspaceRoot,
                    workspaceId,
                    visibility.FamilyId,
                    visibility.ViewId,
                    freshness,
                    visibility.IndexLevel,
                    WorkspaceReadMode.FamilyStore,
                    visibility.GenerationName,
                    visibility.ManifestGeneration,
                    visibility.ResolutionState,
                    visibility.ResolutionBaseId,
                    visibility.ResolutionDeltaGeneration,
                    visibility.ResolutionExactAt);
                return new FamilyStoreReadSession(connection, visibility, snapshot);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
        catch (FamilyStoreReadException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or FormatException)
        {
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The family store could not be opened as a validated read session.",
                ex);
        }
    }

    public TResult Read<TResult>(Func<SqliteConnection, TResult> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return query(_connection);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _connection.Dispose();
        }
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static Dictionary<string, string> ReadStoreMetadata(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT key,value FROM store_meta ORDER BY key";
        using SqliteDataReader reader = command.ExecuteReader();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            metadata.Add(reader.GetString(0), reader.GetString(1));
        return metadata;
    }

    private static void ValidateStoreMetadata(
        IReadOnlyDictionary<string, string> metadata,
        StoreFamilyBinding binding)
    {
        string familyId = Required(metadata, "family_id");
        if (!Guid.TryParse(familyId, out Guid actualFamily) || actualFamily != binding.FamilyId)
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.FamilyMismatch,
                $"The family store records family '{familyId}', not '{binding.FamilyId:D}'.");

        int schema = ParseInt(metadata, "store_sqlite_schema_version");
        int format = ParseInt(metadata, "store_format_epoch");
        if (schema != StoreSchemaVersion || format != StoreFormatEpoch ||
            !string.Equals(Required(metadata, "generation_state"), "serving", StringComparison.Ordinal))
        {
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.SchemaIncompatible,
                $"The family store has schema {schema}, format epoch {format}, or a non-serving generation; " +
                $"Miller requires schema {StoreSchemaVersion}, format epoch {StoreFormatEpoch}, serving state.");
        }

        string minimumReader = Required(metadata, "min_reader_version");
        try
        {
            if (LeadershipEligibility.CompareVersions(
                    MillerExtractContract.PinnedJulieExtractVersion,
                    minimumReader) < 0)
            {
                throw new FamilyStoreReadException(
                    FamilyStoreReadFailure.ReaderFloorIncompatible,
                    $"The family store requires reader {minimumReader}; Miller bundles " +
                    $"julie-extract {MillerExtractContract.PinnedJulieExtractVersion}.");
            }
        }
        catch (ArgumentException ex)
        {
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.ReaderFloorIncompatible,
                "The family store min_reader_version is malformed.",
                ex);
        }
    }

    private static StoreVisibility ReadVisibility(
        SqliteConnection connection,
        StoreFamilyBinding binding,
        string storeRoot,
        string generationName,
        string storeDatabasePath,
        string coordinatorDatabasePath,
        string workspaceRoot)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT v.root,v.current_generation,m.manifest_hash,
                   v.resolution_state,v.resolution_base_id,
                   v.resolution_delta_generation,v.resolution_exact_at
            FROM views AS v
            LEFT JOIN manifests AS m
              ON m.view_id=v.view_id AND m.generation=v.current_generation
            WHERE v.view_id=$view_id
            """;
        command.Parameters.AddWithValue("$view_id", binding.ViewId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.ViewNotFound,
                $"The family store has no view '{binding.ViewId}'.");
        string recordedRoot = reader.GetString(0);
        if (!ArtifactRootIdentity.Matches(recordedRoot, workspaceRoot))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.ViewRootMismatch,
                "The family-store view root does not match the workspace root.");
        if (reader.IsDBNull(1) || reader.IsDBNull(2))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.ManifestMissing,
                "The family-store view has no current manifest.");
        long manifestGeneration = reader.GetInt64(1);
        string manifestHash = reader.GetString(2);
        string resolutionState = reader.GetString(3);
        string? baseId = reader.IsDBNull(4) ? null : reader.GetString(4);
        long? deltaGeneration = reader.IsDBNull(5) ? null : reader.GetInt64(5);
        long? exactAt = reader.IsDBNull(6) ? null : reader.GetInt64(6);
        reader.Close();

        string indexLevel = ReadIndexLevel(connection, binding.ViewId, manifestGeneration);
        long sequence = ReadStoreLogSequence(connection, binding.ViewId, manifestGeneration);
        (string LevelStampL1, string LevelStampL2, string LevelStampL3) levelStamps = ReadLevelStamps(
            connection,
            binding.ViewId,
            manifestGeneration);
        return new StoreVisibility(
            binding.FamilyId.ToString("D", CultureInfo.InvariantCulture),
            storeRoot,
            generationName,
            storeDatabasePath,
            coordinatorDatabasePath,
            binding.ViewId,
            workspaceRoot,
            manifestGeneration,
            manifestHash,
            resolutionState,
            baseId,
            deltaGeneration,
            exactAt,
            sequence,
            indexLevel,
            StoreInstanceId(binding.FamilyId, generationName),
            levelStamps.LevelStampL1,
            levelStamps.LevelStampL2,
            levelStamps.LevelStampL3);
    }

    private static string ReadIndexLevel(
        SqliteConnection connection,
        string viewId,
        long manifestGeneration)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              COALESCE(SUM(CASE WHEN e.version_id IS NOT NULL AND v.complete_l1 IS NULL THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN e.version_id IS NOT NULL AND v.complete_l3 IS NULL THEN 1 ELSE 0 END),0)
            FROM manifest_entries AS e
            LEFT JOIN file_versions AS v ON v.version_id=e.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            """;
        command.Parameters.AddWithValue("$view_id", viewId);
        command.Parameters.AddWithValue("$generation", manifestGeneration);
        using SqliteDataReader reader = command.ExecuteReader();
        AssertRow(reader);
        if (reader.GetInt64(0) != 0)
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The current manifest includes a version without a complete L1 stamp.");
        return reader.GetInt64(1) == 0
            ? IndexLevels.FullMetadataValue
            : IndexLevels.SymbolsMetadataValue;
    }

    private static long ReadStoreLogSequence(
        SqliteConnection connection,
        string viewId,
        long manifestGeneration)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(MAX(log.sequence),0)
            FROM store_log AS log
            WHERE log.view_id=$view_id
               OR (log.view_id IS NULL AND log.version_id IS NULL)
               OR EXISTS (
                    SELECT 1
                    FROM manifest_entries AS entry
                    WHERE entry.view_id=$view_id
                      AND entry.generation=$generation
                      AND entry.version_id=log.version_id)
            """;
        command.Parameters.AddWithValue("$view_id", viewId);
        command.Parameters.AddWithValue("$generation", manifestGeneration);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static (string LevelStampL1, string LevelStampL2, string LevelStampL3) ReadLevelStamps(
        SqliteConnection connection,
        string viewId,
        long manifestGeneration)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.version_id,v.complete_l1,v.complete_l2,v.complete_l3
            FROM manifest_entries AS e
            LEFT JOIN file_versions AS v ON v.version_id=e.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            ORDER BY e.version_id,e.path
            """;
        command.Parameters.AddWithValue("$view_id", viewId);
        command.Parameters.AddWithValue("$generation", manifestGeneration);
        using SqliteDataReader reader = command.ExecuteReader();
        var l1 = new StringBuilder();
        var l2 = new StringBuilder();
        var l3 = new StringBuilder();
        while (reader.Read())
        {
            string versionId = reader.IsDBNull(0)
                ? "null"
                : reader.GetInt64(0).ToString(CultureInfo.InvariantCulture);
            AppendLevelStamp(l1, versionId, reader, 1);
            AppendLevelStamp(l2, versionId, reader, 2);
            AppendLevelStamp(l3, versionId, reader, 3);
        }

        return (HashText(l1), HashText(l2), HashText(l3));
    }

    private static void AppendLevelStamp(
        StringBuilder builder,
        string versionId,
        SqliteDataReader reader,
        int ordinal)
    {
        builder.Append(versionId).Append('=');
        if (reader.IsDBNull(ordinal))
            builder.Append("null");
        else
            builder.Append(reader.GetInt64(ordinal).ToString(CultureInfo.InvariantCulture));
        builder.Append('\n');
    }

    private static string HashText(StringBuilder value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));

    private static string StoreInstanceId(Guid familyId, string generationName) =>
        familyId.ToString("D", CultureInfo.InvariantCulture) + ":" + generationName;

    private static void CreateCompatibilityProjection(
        SqliteConnection connection,
        StoreVisibility visibility,
        IReadOnlyDictionary<string, string> metadata,
        bool resolutionAttached)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TEMP TABLE _miller_visible_entries AS
            SELECT e.path,e.language,e.version_id,e.status,e.observed_content_hash,e.indexed_at,
                   e.error_class,e.error_json
            FROM main.manifest_entries AS e
            WHERE e.view_id=$view_id AND e.generation=$generation
            ORDER BY e.path;
            CREATE UNIQUE INDEX temp._miller_visible_entries_path ON _miller_visible_entries(path);
            CREATE INDEX temp._miller_visible_entries_version ON _miller_visible_entries(version_id);

            CREATE TEMP TABLE _miller_session (
              generation INTEGER NOT NULL,
              binary_version TEXT NOT NULL,
              contract_version TEXT NOT NULL,
              legacy_schema TEXT NOT NULL,
              root TEXT NOT NULL,
              view_id TEXT NOT NULL,
              resolution_delta_generation INTEGER) STRICT;
            INSERT INTO _miller_session VALUES (
              $generation,$binary_version,$contract_version,$legacy_schema,$root,
              $view_id,$resolution_delta_generation);
            CREATE TEMP TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            CREATE TEMP VIEW extraction_revisions AS
            SELECT generation AS revision_id,NULL AS parent_revision_id,'store' AS operation,
                   'snapshot' AS mode,NULL AS started_at,NULL AS completed_at,
                   binary_version,contract_version AS extract_contract_version,
                   legacy_schema AS sqlite_schema_version,root AS input_root,'{}' AS counts_json
            FROM _miller_session;
            CREATE TEMP VIEW files AS
            SELECT COALESCE(e.version_id,-ROW_NUMBER() OVER (ORDER BY e.path)) AS file_id,
                   e.path,e.language,
                   COALESCE(v.content_hash,e.observed_content_hash,'') AS content_hash,
                   COALESCE(v.content_bytes,0) AS content_bytes,
                   v.line_count,e.indexed_at,
                   (SELECT generation FROM _miller_session) AS last_revision_id,e.status,v.metadata_json
            FROM _miller_visible_entries AS e
            LEFT JOIN main.file_versions AS v ON v.version_id=e.version_id;
            CREATE TEMP VIEW symbols AS
            SELECT s.symbol_id,
                   s.version_id AS file_id,s.path,s.language,s.name,s.kind,s.signature,s.doc_comment,
                   s.visibility,
                   s.parent_symbol_id,
                   s.start_line,s.start_column,s.end_line,s.end_column,s.start_byte,s.end_byte,
                   s.body_start_line,s.body_start_column,s.body_end_line,s.body_end_column,
                   s.body_start_byte,s.body_end_byte,s.body_hash,s.semantic_group,s.confidence,
                   s.content_type,s.is_test,s.test_container,s.test_lifecycle,s.metadata_json
            FROM main.symbols AS s
            JOIN _miller_visible_entries AS e ON e.version_id=s.version_id;
            CREATE TEMP VIEW symbol_annotations AS
            SELECT a.annotation_id,a.symbol_id,
                   a.annotation,a.annotation_key,a.raw_text,a.carrier,a.metadata_json
            FROM main.symbol_annotations AS a
            JOIN _miller_visible_entries AS e ON e.version_id=a.version_id;
            CREATE TEMP VIEW reference_sites AS
            SELECT r.reference_site_id,
                   r.version_id AS file_id,r.path,r.language,
                   r.containing_symbol_id,
                   r.start_line,r.start_column,r.end_line,r.end_column,r.start_byte,r.end_byte,
                   r.is_exact,r.provenance
            FROM main.reference_sites AS r
            JOIN _miller_visible_entries AS e ON e.version_id=r.version_id;
            CREATE TEMP VIEW identifiers AS
            SELECT i.identifier_id,i.reference_site_id,
                   i.version_id AS file_id,i.path,i.language,i.name,i.kind,
                   i.containing_symbol_id,
                   i.start_line,i.start_column,i.end_line,i.end_column,i.start_byte,i.end_byte,
                   i.confidence,i.code_context,i.metadata_json
            FROM main.identifiers AS i
            JOIN _miller_visible_entries AS e ON e.version_id=i.version_id;
            CREATE TEMP VIEW relationships AS
            SELECT r.relationship_id,r.reference_site_id,r.from_symbol_id,r.to_symbol_id,
                   r.version_id AS file_id,r.path,r.kind,r.start_line,r.start_column,r.end_line,
                   r.end_column,r.start_byte,r.end_byte,r.confidence,r.metadata_json
            FROM main.relationships AS r
            JOIN _miller_visible_entries AS e ON e.version_id=r.version_id;
            CREATE TEMP VIEW pending_relationships AS
            SELECT p.pending_relationship_id,p.reference_site_id,p.from_symbol_id,p.caller_scope_symbol_id,
                   p.version_id AS file_id,p.path,p.kind,p.target_display_name,p.target_terminal_name,
                   p.target_receiver,p.target_namespace_json,p.target_import_context,p.start_line,p.start_column,
                   p.end_line,p.end_column,p.start_byte,p.end_byte,p.confidence,p.metadata_json
            FROM main.pending_relationships AS p
            JOIN _miller_visible_entries AS e ON e.version_id=p.version_id;
            CREATE TEMP VIEW type_facts AS
            SELECT t.type_fact_id,t.symbol_id,t.language,t.resolved_type,
                   t.generic_params_json,t.constraints_json,t.is_inferred,t.metadata_json
            FROM main.type_facts AS t
            JOIN _miller_visible_entries AS e ON e.version_id=t.version_id;
            CREATE TEMP VIEW type_argument_usages AS
            SELECT t.usage_id,t.identifier_id,
                   t.version_id AS file_id,t.path,t.language,t.metadata_json
            FROM main.type_argument_usages AS t
            JOIN _miller_visible_entries AS e ON e.version_id=t.version_id;
            CREATE TEMP VIEW type_arguments AS
            SELECT t.type_argument_id,t.usage_id,t.parent_type_argument_id,
                   t.ordinal,t.type_name
            FROM main.type_arguments AS t
            JOIN _miller_visible_entries AS e ON e.version_id=t.version_id;
            CREATE TEMP VIEW literals AS
            SELECT l.literal_id,l.version_id AS file_id,
                   l.path,l.language,l.literal_text,l.kind,l.carrier,l.arg_position,
                   l.containing_symbol_id,
                   l.start_line,l.start_column,l.end_line,l.end_column,l.start_byte,l.end_byte,
                   l.confidence,l.metadata_json
            FROM main.literals AS l
            JOIN _miller_visible_entries AS e ON e.version_id=l.version_id;
            CREATE TEMP VIEW source_regions AS
            SELECT r.source_region_id,
                   r.version_id AS file_id,r.path,r.language,r.kind,
                   r.containing_symbol_id,
                   r.start_line,r.start_column,r.end_line,r.end_column,r.start_byte,r.end_byte,r.metadata_json
            FROM main.source_regions AS r
            JOIN _miller_visible_entries AS e ON e.version_id=r.version_id;
            CREATE TEMP VIEW complexity_metrics AS
            SELECT c.complexity_metric_id,
                   c.version_id AS file_id,c.path,c.language,c.scope,
                   c.symbol_id,
                   c.algorithm_id,c.covered_lines,c.covered_bytes,c.decision_count,c.loop_count,
                   c.max_nesting_depth,c.parameter_count,c.start_line,c.start_column,c.end_line,c.end_column,
                   c.start_byte,c.end_byte,c.metadata_json
            FROM main.complexity_metrics AS c
            JOIN _miller_visible_entries AS e ON e.version_id=c.version_id;
            CREATE TEMP VIEW structural_facts AS
            SELECT f.structural_fact_id,
                   f.version_id AS file_id,f.path,f.language,f.pattern_id,f.capture_name,f.node_kind,
                   f.containing_symbol_id,
                   f.start_line,f.start_column,f.end_line,f.end_column,f.start_byte,f.end_byte,
                   f.confidence,f.metadata_json
            FROM main.structural_facts AS f
            JOIN _miller_visible_entries AS e ON e.version_id=f.version_id;
            CREATE TEMP VIEW parse_diagnostics AS
            SELECT d.diagnostic_id,
                   d.version_id AS file_id,d.path,d.language,d.kind,d.message,d.start_line,d.start_column,
                   d.end_line,d.end_column,d.start_byte,d.end_byte,d.metadata_json
            FROM main.parse_diagnostics AS d
            JOIN _miller_visible_entries AS e ON e.version_id=d.version_id;
            CREATE TEMP VIEW parser_inventory AS
            SELECT language,parser_package,parser_version,grammar_version,source,metadata_json
            FROM main.parser_inventory
            WHERE extraction_epoch=(SELECT CAST(value AS INTEGER) FROM main.store_meta WHERE key='extraction_identity_epoch');
            CREATE TEMP VIEW language_capabilities AS
            SELECT language,parser_package,extensions_json,dependency_status,target_symbols,target_relationships,
                   target_pending_relationships,target_identifiers,target_types,actual_symbols,actual_relationships,
                   actual_pending_relationships,actual_identifiers,actual_types,kind_coverage_json
            FROM main.language_capabilities
            WHERE extraction_epoch=(SELECT CAST(value AS INTEGER) FROM main.store_meta WHERE key='extraction_identity_epoch');
            CREATE TEMP VIEW language_capability_fixtures AS
            SELECT language,fixture_name,source_path,expected_path
            FROM main.language_capability_fixtures
            WHERE extraction_epoch=(SELECT CAST(value AS INTEGER) FROM main.store_meta WHERE key='extraction_identity_epoch');
            CREATE TEMP VIEW language_capability_gaps AS
            SELECT gap_id,language,capability,status,reason,required_closure,evidence_json
            FROM main.language_capability_gaps
            WHERE extraction_epoch=(SELECT CAST(value AS INTEGER) FROM main.store_meta WHERE key='extraction_identity_epoch');
            CREATE TEMP VIEW revision_file_changes AS
            SELECT (SELECT generation FROM _miller_session) AS revision_id,
                   CAST(file_id AS TEXT) AS file_id,path,'upsert' AS change_kind
            FROM files;
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$binary_version", Required(metadata, "binary_version"));
        command.Parameters.AddWithValue(
            "$contract_version",
            MillerExtractContract.ExpectedExtractContractVersion.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$legacy_schema",
            MillerExtractContract.ExpectedSchemaVersion.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$root", visibility.WorkspaceRoot);
        command.Parameters.AddWithValue("$resolution_delta_generation", (object?)visibility.ResolutionDeltaGeneration ?? DBNull.Value);
        command.ExecuteNonQuery();

        CreateResolutionViews(connection, resolutionAttached);

        InsertMetadata(connection, "artifact_id", visibility.FamilyId);
        InsertMetadata(connection, "root_path", visibility.WorkspaceRoot);
        InsertMetadata(connection, "index_level", visibility.IndexLevel);
        InsertMetadata(
            connection,
            "sqlite_schema_version",
            MillerExtractContract.ExpectedSchemaVersion.ToString(CultureInfo.InvariantCulture));
        InsertMetadata(
            connection,
            "extract_contract_version",
            MillerExtractContract.ExpectedExtractContractVersion.ToString(CultureInfo.InvariantCulture));
        InsertMetadata(connection, "hash_algorithm", MillerExtractContract.ExpectedHashAlgorithm);
        InsertMetadata(connection, "binary_version", Required(metadata, "binary_version"));
    }

    private static bool AttachValidatedResolutionBase(
        SqliteConnection connection,
        StoreVisibility visibility)
    {
        if (visibility.ResolutionState != "exact" ||
            visibility.ResolutionExactAt != visibility.ManifestGeneration ||
            visibility.ResolutionBaseId is null ||
            visibility.ResolutionDeltaGeneration is null)
        {
            return false;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT b.manifest_hash,b.resolver_output_epoch,b.state,b.relative_path,
                   b.file_bytes,b.file_sha256,d.manifest_hash,d.resolver_output_epoch
            FROM resolution_bases AS b
            JOIN resolution_deltas AS d
              ON d.base_id=b.base_id
             AND d.view_id=$view_id
             AND d.delta_generation=$delta_generation
            WHERE b.base_id=$base_id
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$delta_generation", visibility.ResolutionDeltaGeneration.Value);
        command.Parameters.AddWithValue("$base_id", visibility.ResolutionBaseId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The exact resolution binding has no matching ready base and delta.");
        string baseManifestHash = reader.GetString(0);
        long baseEpoch = reader.GetInt64(1);
        string state = reader.GetString(2);
        string relativePath = reader.GetString(3);
        long fileBytes = reader.GetInt64(4);
        string fileSha256 = reader.GetString(5);
        string deltaManifestHash = reader.GetString(6);
        long deltaEpoch = reader.GetInt64(7);
        reader.Close();
        if (state != "ready" || deltaManifestHash != visibility.ManifestHash || baseEpoch != deltaEpoch)
        {
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The exact resolution base or delta does not match the current manifest.");
        }

        string generationDirectory = Path.GetDirectoryName(visibility.StoreDatabasePath)
            ?? throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The family-store generation has no directory.");
        if (Path.IsPathRooted(relativePath))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The exact resolution base path is absolute.");
        string basePath = CanonicalizeContained(
            generationDirectory,
            Path.Combine(generationDirectory, relativePath),
            "The exact resolution base file escapes its generation.");
        if (!File.Exists(basePath))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The exact resolution base file is missing.");
        var info = new FileInfo(basePath);
        if (info.Length != fileBytes || !StringComparer.OrdinalIgnoreCase.Equals(Sha256(basePath), fileSha256))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The exact resolution base file does not match its recorded identity.");

        using SqliteCommand attach = connection.CreateCommand();
        attach.CommandText = "ATTACH DATABASE $base_path AS resolution_base";
        attach.Parameters.AddWithValue("$base_path", basePath);
        attach.ExecuteNonQuery();
        ValidateAttachedBase(connection, baseManifestHash, baseEpoch);
        return true;
    }

    private static string CanonicalizeContained(string root, string path, string message)
    {
        string canonical = PathCanonicalizer.CanonicalizeFile(root, path);
        string relative = Path.GetRelativePath(root, canonical);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new FamilyStoreReadException(FamilyStoreReadFailure.Corrupt, message);
        }
        return canonical;
    }

    private static void ValidateAttachedBase(
        SqliteConnection connection,
        string baseManifestHash,
        long resolverOutputEpoch)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              (SELECT value FROM resolution_base.base_meta WHERE key='completed'),
              (SELECT value FROM resolution_base.base_meta WHERE key='manifest_hash'),
              (SELECT value FROM resolution_base.base_meta WHERE key='resolver_output_epoch')
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        AssertRow(reader);
        if (reader.IsDBNull(0) || reader.GetString(0) != "1" ||
            reader.IsDBNull(1) || reader.GetString(1) != baseManifestHash ||
            reader.IsDBNull(2) || reader.GetString(2) != resolverOutputEpoch.ToString(CultureInfo.InvariantCulture))
        {
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The attached resolution base metadata does not match the current binding.");
        }
    }

    private static void CreateResolutionViews(SqliteConnection connection, bool resolutionAttached)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = resolutionAttached
            ?
            """
            CREATE TEMP VIEW identifier_resolutions AS
            SELECT b.identifier_id,b.target_symbol_id,b.tier,b.confidence,b.method,b.outcome,b.candidates,
                   (SELECT generation FROM _miller_session) AS resolved_at_revision
            FROM resolution_base.identifier_resolutions AS b
            JOIN _miller_visible_entries AS e ON e.version_id=b.version_id
            WHERE NOT EXISTS (
              SELECT 1 FROM main.resolution_identifier_deltas AS d
              WHERE d.view_id=(SELECT view_id FROM _miller_session)
                AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                AND d.version_id=b.version_id AND d.identifier_id=b.identifier_id)
            UNION ALL
            SELECT d.identifier_id,d.target_symbol_id,d.tier,d.confidence,d.method,d.outcome,d.candidates,
                   (SELECT generation FROM _miller_session)
            FROM main.resolution_identifier_deltas AS d
            JOIN _miller_visible_entries AS e ON e.version_id=d.version_id
            WHERE d.view_id=(SELECT view_id FROM _miller_session)
              AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session);
            CREATE TEMP VIEW pending_resolutions AS
            SELECT b.pending_relationship_id,b.target_symbol_id,b.tier,b.confidence,b.method,
                   (SELECT generation FROM _miller_session) AS resolved_at_revision
            FROM resolution_base.pending_resolutions AS b
            JOIN _miller_visible_entries AS e ON e.version_id=b.version_id
            WHERE NOT EXISTS (
              SELECT 1 FROM main.resolution_pending_deltas AS d
              WHERE d.view_id=(SELECT view_id FROM _miller_session)
                AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
                AND d.version_id=b.version_id AND d.pending_relationship_id=b.pending_relationship_id)
            UNION ALL
            SELECT d.pending_relationship_id,d.target_symbol_id,d.tier,d.confidence,d.method,
                   (SELECT generation FROM _miller_session)
            FROM main.resolution_pending_deltas AS d
            JOIN _miller_visible_entries AS e ON e.version_id=d.version_id
            WHERE d.view_id=(SELECT view_id FROM _miller_session)
              AND d.delta_generation=(SELECT resolution_delta_generation FROM _miller_session)
              AND d.operation='replace';
            """
            :
            """
            CREATE TEMP VIEW identifier_resolutions AS
            SELECT CAST(NULL AS TEXT) AS identifier_id,CAST(NULL AS TEXT) AS target_symbol_id,
                   CAST(NULL AS INTEGER) AS tier,CAST(NULL AS REAL) AS confidence,
                   CAST(NULL AS TEXT) AS method,CAST(NULL AS TEXT) AS outcome,
                   CAST(NULL AS INTEGER) AS candidates,CAST(NULL AS INTEGER) AS resolved_at_revision
            WHERE 0;
            CREATE TEMP VIEW pending_resolutions AS
            SELECT CAST(NULL AS TEXT) AS pending_relationship_id,CAST(NULL AS TEXT) AS target_symbol_id,
                   CAST(NULL AS INTEGER) AS tier,CAST(NULL AS REAL) AS confidence,
                   CAST(NULL AS TEXT) AS method,CAST(NULL AS INTEGER) AS resolved_at_revision
            WHERE 0;
            """;
        command.ExecuteNonQuery();
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }


    private static void InsertMetadata(SqliteConnection connection, string key, string value)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO temp.artifact_metadata(key,value) VALUES ($key,$value)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void SetQueryOnly(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only=ON; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
    }

    private static string ResolutionStamp(StoreVisibility visibility) => string.Join(
        ':',
        visibility.ResolutionState,
        visibility.ResolutionBaseId ?? string.Empty,
        visibility.ResolutionDeltaGeneration?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        visibility.ResolutionExactAt?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static int ParseInt(IReadOnlyDictionary<string, string> metadata, string key)
    {
        string value = Required(metadata, key);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.SchemaIncompatible,
                $"The family store metadata '{key}' value '{value}' is not an integer.");
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                $"The family store is missing required metadata '{key}'.");

    private static void AssertRow(SqliteDataReader reader)
    {
        if (!reader.Read())
            throw new FamilyStoreReadException(
                FamilyStoreReadFailure.Corrupt,
                "The family store query returned no aggregate row.");
    }
}
