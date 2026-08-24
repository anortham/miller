using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Testing;

/// <summary>
/// Thrown when a present <c>ct.db</c> has a <c>schema_version</c> newer than this binary. The file
/// is left untouched; callers must fail visibly rather than apply DDL as a healer.
/// </summary>
public sealed class ContinuousTestStoreSchemaException : Exception
{
    public string Path { get; }
    public int FileSchemaVersion { get; }
    public int SupportedSchemaVersion { get; }

    public ContinuousTestStoreSchemaException(string path, int fileSchemaVersion, int supportedSchemaVersion)
        : base(
            $"ct.db at '{path}' has schema version {fileSchemaVersion}, newer than this Miller binary supports ({supportedSchemaVersion}). Upgrade Miller. The file was not modified.")
    {
        Path = path;
        FileSchemaVersion = fileSchemaVersion;
        SupportedSchemaVersion = supportedSchemaVersion;
    }
}

/// <summary>
/// Thrown when a present <c>ct.db</c> cannot be opened or read (corrupt / not a database). The file
/// is left in place; this binary will not overwrite or rename it aside.
/// </summary>
public sealed class ContinuousTestStoreUnreadableException : Exception
{
    public string Path { get; }

    public ContinuousTestStoreUnreadableException(string path, Exception innerException)
        : base(
            $"ct.db at '{path}' is present but could not be read: {innerException.Message}. Repair or delete the sidecar; this Miller binary will not overwrite it.",
            innerException)
    {
        Path = path;
    }
}

/// <summary>
/// Core owner of <c>&lt;workspace&gt;/.miller/ct.db</c>. Reads of a missing file return empty and
/// do not create it. Writes that need a database may create it and apply <see cref="CtSchema"/>.
/// Newer-schema and corrupt files fail visibly. The SQLite connection is never part of the public
/// surface.
/// </summary>
public sealed partial class ContinuousTestStore : IDisposable
{
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADb = 26;
    private const int WriteBusyTimeoutSeconds = 5;

    internal const string AggregateContinuousTestStatusesNoCursorSql = """
        SELECT COUNT(*),
               COALESCE(SUM(CASE WHEN state IN ('unknown', 'running') THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN state = 'stale' THEN 1 ELSE 0 END), 0),
               0
        FROM ct_test_states s INDEXED BY idx_ct_test_states_workspace_state
        JOIN test_cases tc
          ON tc.workspace_id = s.workspace_id
         AND tc.id = s.test_case_id
        LEFT JOIN ct_test_projects p
          ON p.workspace_id = tc.workspace_id
         AND p.project_path = tc.project_path
        WHERE s.workspace_id = $workspace
          AND (tc.source <> 'ct-project-status' OR p.enabled IS NULL OR p.enabled = 1);
        """;

    internal const string AggregateContinuousTestStatusesSelectedSql = """
        SELECT COUNT(*),
               COALESCE(SUM(CASE WHEN s.state IN ('unknown', 'running') THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN s.state NOT IN ('unknown', 'running')
                   AND NOT (
                       (s.state IN ('green', 'red', 'skipped')
                           AND s.index_identity = $identity AND s.revision = $revision)
                       OR (s.state = 'green' AND w.revision IS NOT NULL AND w.revision >= $revision)
                   ) THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN s.state = 'red'
                   AND s.index_identity = $identity AND s.revision = $revision
                   THEN 1 ELSE 0 END), 0)
        FROM ct_test_states s INDEXED BY idx_ct_test_states_workspace_state
        JOIN test_cases tc
          ON tc.workspace_id = s.workspace_id
         AND tc.id = s.test_case_id
        LEFT JOIN ct_test_projects p
          ON p.workspace_id = tc.workspace_id
         AND p.project_path = tc.project_path
        LEFT JOIN ct_case_fresh_watermarks w
            ON w.test_case_id = s.test_case_id
           AND w.workspace_id = s.workspace_id
           AND w.index_identity = $identity
        WHERE s.workspace_id = $workspace
          AND (tc.source <> 'ct-project-status' OR p.enabled IS NULL OR p.enabled = 1);
        """;

    private readonly object _gate = new();
    private SqliteConnection? _write;
    private SqliteTransaction? _transaction;
    private int _transactionDepth;
    private bool _rollbackRequested;

    public string DbPath { get; }

    public ContinuousTestStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        DbPath = Path.GetFullPath(dbPath);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _transaction?.Dispose();
            _transaction = null;
            _write?.Dispose();
            _write = null;
            _transactionDepth = 0;
            _rollbackRequested = false;
        }
    }

    public void Transaction(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        lock (_gate)
        {
            if (_transactionDepth > 0)
            {
                _transactionDepth++;
                try
                {
                    body();
                }
                catch
                {
                    _rollbackRequested = true;
                    _transactionDepth--;
                    throw;
                }

                _transactionDepth--;
                return;
            }

            using CtWriteLock lease = CtWriteLock.AcquireFor(DbPath);
            using SqliteConnection connection = OpenForWrite();
            try
            {
                GuardSchema(connection, apply: true);
            }
            catch (SqliteException ex) when (IsCorruption(ex))
            {
                throw new ContinuousTestStoreUnreadableException(DbPath, ex);
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            _write = connection;
            _transaction = transaction;
            _transactionDepth = 1;
            _rollbackRequested = false;
            try
            {
                body();
                if (_rollbackRequested)
                    transaction.Rollback();
                else
                    transaction.Commit();
            }
            catch
            {
                try { transaction.Rollback(); }
                catch (SqliteException) { }
                throw;
            }
            finally
            {
                _transaction = null;
                _write = null;
                _transactionDepth = 0;
                _rollbackRequested = false;
            }
        }
    }

    public IReadOnlyList<ContinuousTestCase> ListTestCases(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<IReadOnlyList<ContinuousTestCase>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, workspace_id, file_path, content_hash, symbol_name, symbol_path,
                           suite_id, name, qualified_name, selector, framework, role, source,
                           confidence, metadata_json, provenance_json
                    FROM test_cases
                    WHERE workspace_id = $ws
                    ORDER BY selector, id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);

                var rows = new List<ContinuousTestCase>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new ContinuousTestCase(
                        Id: reader.GetString(0),
                        WorkspaceId: reader.GetString(1),
                        FilePath: NullableString(reader, 2),
                        ContentHash: NullableString(reader, 3),
                        SymbolName: NullableString(reader, 4),
                        SymbolPath: NullableString(reader, 5),
                        SuiteId: NullableString(reader, 6),
                        Name: reader.GetString(7),
                        QualifiedName: reader.GetString(8),
                        Selector: reader.GetString(9),
                        Framework: NullableString(reader, 10),
                        Role: RoleFromValue(reader.GetString(11)),
                        Source: reader.GetString(12),
                        Confidence: reader.GetDouble(13),
                        Metadata: MetadataFromJson(reader.GetString(14)),
                        Provenance: MetadataFromJson(reader.GetString(15))));
                }

                return rows;
            });
    }

    public ContinuousTestCase? GetTestCase(string workspaceId, string testCaseId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        if (string.IsNullOrEmpty(testCaseId))
            throw new ArgumentException("must not be empty", nameof(testCaseId));

        return ListTestCases(workspaceId).FirstOrDefault(row =>
            string.Equals(row.Id, testCaseId, StringComparison.Ordinal));
    }

    public void PutTestCase(ContinuousTestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO test_cases (
                    id, workspace_id, file_path, content_hash, symbol_name, symbol_path,
                    suite_id, name, qualified_name, selector, framework, role, source,
                    confidence, metadata_json, provenance_json, project_path
                )
                VALUES (
                    $id, $ws, $file, $hash, $symbolName, $symbolPath, $suite, $name, $qualified,
                    $selector, $framework, $role, $source, $confidence, $metadata, $provenance, $project
                )
                ON CONFLICT(id) DO UPDATE SET
                    workspace_id = excluded.workspace_id,
                    file_path = excluded.file_path,
                    content_hash = excluded.content_hash,
                    symbol_name = excluded.symbol_name,
                    symbol_path = excluded.symbol_path,
                    suite_id = excluded.suite_id,
                    name = excluded.name,
                    qualified_name = excluded.qualified_name,
                    selector = excluded.selector,
                    framework = excluded.framework,
                    role = excluded.role,
                    source = excluded.source,
                    confidence = excluded.confidence,
                    metadata_json = excluded.metadata_json,
                    provenance_json = excluded.provenance_json,
                    project_path = excluded.project_path;
                """;
            command.Parameters.AddWithValue("$id", testCase.Id);
            command.Parameters.AddWithValue("$ws", testCase.WorkspaceId);
            command.Parameters.AddWithValue("$file", (object?)testCase.FilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", (object?)testCase.ContentHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$symbolName", (object?)testCase.SymbolName ?? DBNull.Value);
            command.Parameters.AddWithValue("$symbolPath", (object?)testCase.SymbolPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$suite", (object?)testCase.SuiteId ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", testCase.Name);
            command.Parameters.AddWithValue("$qualified", testCase.QualifiedName);
            command.Parameters.AddWithValue("$selector", testCase.Selector);
            command.Parameters.AddWithValue("$framework", (object?)testCase.Framework ?? DBNull.Value);
            command.Parameters.AddWithValue("$role", RoleValue(testCase.Role));
            command.Parameters.AddWithValue("$source", testCase.Source);
            command.Parameters.AddWithValue("$confidence", testCase.Confidence);
            command.Parameters.AddWithValue("$metadata", JsonText(testCase.Metadata));
            command.Parameters.AddWithValue("$provenance", JsonText(testCase.Provenance));
            command.Parameters.AddWithValue("$project", (object?)NormalizedProjectPath(testCase.Metadata) ?? DBNull.Value);
            command.ExecuteNonQuery();
        });
    }

    public int DeleteTestCase(string workspaceId, string testCaseId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(testCaseId))
            throw new ArgumentException("must not be empty", nameof(testCaseId));
        if (!CanWriteExistingFile())
            return 0;

        int deleted = 0;
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM test_cases WHERE workspace_id = $ws AND id = $id;";
            command.Parameters.AddWithValue("$ws", workspaceId);
            command.Parameters.AddWithValue("$id", testCaseId);
            deleted = command.ExecuteNonQuery();
        });
        return deleted;
    }

    public IReadOnlyList<ContinuousTestStatus> ListContinuousTestStatuses(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<IReadOnlyList<ContinuousTestStatus>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT s.workspace_id, s.test_case_id, s.state, s.index_identity, s.revision,
                           s.last_run_revision, s.stale_since_revision, s.running_run_id, s.running_revision,
                           s.last_result_status, s.last_result_at, s.failure_summary, s.flakiness_score
                    FROM ct_test_states s INDEXED BY idx_ct_test_states_workspace_state
                    JOIN test_cases tc
                      ON tc.workspace_id = s.workspace_id
                     AND tc.id = s.test_case_id
                    LEFT JOIN ct_test_projects p
                      ON p.workspace_id = tc.workspace_id
                     AND p.project_path = tc.project_path
                    WHERE s.workspace_id = $ws
                      AND (tc.source <> 'ct-project-status' OR p.enabled IS NULL OR p.enabled = 1)
                    ORDER BY s.state, s.test_case_id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);

                var rows = new List<ContinuousTestStatus>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ContinuousTestState state = StateFromValue(reader.GetString(2));
                    string indexIdentity = reader.GetString(3);
                    long revision = reader.GetInt64(4);
                    CtFreshnessKey? proven = state is ContinuousTestState.Green
                        or ContinuousTestState.Red
                        or ContinuousTestState.Skipped
                        ? new CtFreshnessKey(indexIdentity, revision)
                        : null;
                    rows.Add(new ContinuousTestStatus(
                        WorkspaceId: reader.GetString(0),
                        TestCaseId: reader.GetString(1),
                        State: state,
                        IndexIdentity: indexIdentity,
                        Revision: revision,
                        LastRunRevision: NullableString(reader, 5),
                        StaleSinceRevision: NullableString(reader, 6),
                        RunningRunId: NullableString(reader, 7),
                        RunningRevision: NullableString(reader, 8),
                        LastResultStatus: NullableString(reader, 9),
                        LastResultAt: NullableDateTimeOffset(reader, 10),
                        FailureSummary: NullableString(reader, 11),
                        FlakinessScore: reader.GetDouble(12),
                        ProvenFreshKey: proven));
                }

                return rows;
            });
    }

    public ContinuousTestStatusAggregate AggregateContinuousTestStatuses(
        string workspaceId,
        CtFreshnessKey? selectedKey)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead(
            static () => new ContinuousTestStatusAggregate(0, 0, 0, 0),
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = selectedKey is null
                    ? AggregateContinuousTestStatusesNoCursorSql
                    : AggregateContinuousTestStatusesSelectedSql;
                command.Parameters.AddWithValue("$workspace", workspaceId);
                if (selectedKey is { } selected)
                {
                    command.Parameters.AddWithValue("$identity", selected.IndexIdentity);
                    command.Parameters.AddWithValue("$revision", selected.Revision);
                }

                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    return new ContinuousTestStatusAggregate(0, 0, 0, 0);

                return new ContinuousTestStatusAggregate(
                    Total: checked((int)reader.GetInt64(0)),
                    Pending: checked((int)reader.GetInt64(1)),
                    Stale: checked((int)reader.GetInt64(2)),
                    FreshRed: checked((int)reader.GetInt64(3)));
            });
    }

    public void MarkContinuousTestsStale(
        string workspaceId,
        IReadOnlyList<string> testCaseIds,
        CtFreshnessKey staleAt)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        ArgumentNullException.ThrowIfNull(testCaseIds);
        if (testCaseIds.Count == 0)
            return;
        if (!CanWriteExistingFile())
            return;

        string staleSince = staleAt.Revision.ToString(CultureInfo.InvariantCulture);
        Transaction(() =>
        {
            foreach (string testCaseId in testCaseIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                using (var command = _write!.CreateCommand())
                {
                    command.CommandText = """
                        INSERT INTO ct_test_states (
                            test_case_id, workspace_id, index_identity, revision, state,
                            stale_since_revision, running_run_id, running_revision, updated_at
                        )
                        SELECT id, workspace_id, $identity, $revision, 'stale', $staleSince, NULL, NULL,
                               strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                        FROM test_cases
                        WHERE workspace_id = $ws AND id = $id
                        ON CONFLICT(test_case_id) DO UPDATE SET
                            workspace_id = excluded.workspace_id,
                            state = CASE
                                WHEN ct_test_states.running_run_id IS NOT NULL
                                     AND EXISTS (
                                         SELECT 1 FROM test_runs tr
                                         WHERE tr.id = ct_test_states.running_run_id
                                           AND tr.status = 'running')
                                THEN ct_test_states.state
                                ELSE 'stale'
                            END,
                            stale_since_revision = CASE
                                WHEN ct_test_states.running_run_id IS NOT NULL
                                     AND EXISTS (
                                         SELECT 1 FROM test_runs tr
                                         WHERE tr.id = ct_test_states.running_run_id
                                           AND tr.status = 'running')
                                THEN coalesce(ct_test_states.stale_since_revision, $staleSince)
                                ELSE $staleSince
                            END,
                            running_run_id = CASE
                                WHEN ct_test_states.running_run_id IS NOT NULL
                                     AND EXISTS (
                                         SELECT 1 FROM test_runs tr
                                         WHERE tr.id = ct_test_states.running_run_id
                                           AND tr.status = 'running')
                                THEN ct_test_states.running_run_id
                                ELSE NULL
                            END,
                            running_revision = CASE
                                WHEN ct_test_states.running_run_id IS NOT NULL
                                     AND EXISTS (
                                         SELECT 1 FROM test_runs tr
                                         WHERE tr.id = ct_test_states.running_run_id
                                           AND tr.status = 'running')
                                THEN ct_test_states.running_revision
                                ELSE NULL
                            END,
                            index_identity = CASE
                                WHEN ct_test_states.running_run_id IS NOT NULL
                                     AND EXISTS (
                                         SELECT 1 FROM test_runs tr
                                         WHERE tr.id = ct_test_states.running_run_id
                                           AND tr.status = 'running')
                                THEN ct_test_states.index_identity
                                ELSE excluded.index_identity
                            END,
                            revision = CASE
                                WHEN ct_test_states.running_run_id IS NOT NULL
                                     AND EXISTS (
                                         SELECT 1 FROM test_runs tr
                                         WHERE tr.id = ct_test_states.running_run_id
                                           AND tr.status = 'running')
                                THEN ct_test_states.revision
                                ELSE excluded.revision
                            END,
                            updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
                        """;
                    command.Parameters.AddWithValue("$ws", workspaceId);
                    command.Parameters.AddWithValue("$id", testCaseId);
                    command.Parameters.AddWithValue("$identity", staleAt.IndexIdentity);
                    command.Parameters.AddWithValue("$revision", staleAt.Revision);
                    command.Parameters.AddWithValue("$staleSince", staleSince);
                    command.ExecuteNonQuery();
                }

                using var invalidate = _write!.CreateCommand();
                invalidate.CommandText = """
                    DELETE FROM ct_case_fresh_watermarks
                    WHERE workspace_id = $ws AND test_case_id = $id;
                    """;
                invalidate.Parameters.AddWithValue("$ws", workspaceId);
                invalidate.Parameters.AddWithValue("$id", testCaseId);
                invalidate.ExecuteNonQuery();
            }
        });
    }

    private bool CanWriteExistingFile()
    {
        lock (_gate)
            return _write is not null || File.Exists(DbPath);
    }

    private void WithWrite(Action<SqliteConnection> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (_write is not null)
            {
                action(_write);
                return;
            }

            using CtWriteLock lease = CtWriteLock.AcquireFor(DbPath);
            using SqliteConnection connection = OpenForWrite();
            try
            {
                GuardSchema(connection, apply: true);
                action(connection);
            }
            catch (SqliteException ex) when (IsCorruption(ex))
            {
                throw new ContinuousTestStoreUnreadableException(DbPath, ex);
            }
        }
    }

    private T WithRead<T>(Func<T> missing, Func<SqliteConnection, T> present)
    {
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(present);
        lock (_gate)
        {
            if (_write is not null)
                return present(_write);
            if (!File.Exists(DbPath))
                return missing();

            try
            {
                using SqliteConnection connection = OpenForRead();
                GuardSchema(connection, apply: false);
                return present(connection);
            }
            catch (FileNotFoundException)
            {
                return missing();
            }
            catch (SqliteException ex)
            {
                throw new ContinuousTestStoreUnreadableException(DbPath, ex);
            }
        }
    }

    private SqliteConnection OpenForWrite()
    {
        string dir = Path.GetDirectoryName(DbPath)
            ?? throw new ArgumentException($"Path has no directory: {DbPath}");
        Directory.CreateDirectory(dir);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = WriteBusyTimeoutSeconds,
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            connection.Dispose();
            throw new ContinuousTestStoreUnreadableException(DbPath, ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private SqliteConnection OpenForRead()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            connection.Dispose();
            throw new ContinuousTestStoreUnreadableException(DbPath, ex);
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new ContinuousTestStoreUnreadableException(DbPath, ex);
        }
        catch (InvalidOperationException ex)
        {
            connection.Dispose();
            throw new ContinuousTestStoreUnreadableException(DbPath, ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void GuardSchema(SqliteConnection connection, bool apply)
    {
        try
        {
            if (CtSchema.IsNewerSchema(connection)
                && CtSchema.ReadSchemaVersion(connection) is int fileVersion)
            {
                throw new ContinuousTestStoreSchemaException(DbPath, fileVersion, CtSchema.SchemaVersion);
            }
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            throw new ContinuousTestStoreUnreadableException(DbPath, ex);
        }

        if (apply)
            CtSchema.Apply(connection);
    }

    private static bool IsCorruption(SqliteException ex) =>
        ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADb;

    private static string JsonText(object? value) => TestingJson.Value(value);

    private static string? NormalizedProjectPath(IReadOnlyDictionary<string, object?> metadata)
    {
        if (!metadata.TryGetValue("ct_project_path", out object? raw)
            || raw is null)
        {
            return null;
        }

        string? path = raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, object?> MetadataFromJson(string json) =>
        TestingJson.Object(json);

    private static object DateTimeText(DateTimeOffset? value) =>
        value is null
            ? DBNull.Value
            : value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? NullableDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static string RoleValue(ContinuousTestRole role) => role.ToString().ToLowerInvariant();

    private static ContinuousTestRole RoleFromValue(string value) => value switch
    {
        "testcase" => ContinuousTestRole.TestCase,
        "parameterizedtest" => ContinuousTestRole.ParameterizedTest,
        "fixturesetup" => ContinuousTestRole.FixtureSetup,
        "fixtureteardown" => ContinuousTestRole.FixtureTeardown,
        "testcontainer" => ContinuousTestRole.TestContainer,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown continuous test role"),
    };

    private static string StateValue(ContinuousTestState state) => state switch
    {
        ContinuousTestState.Unknown => "unknown",
        ContinuousTestState.Green => "green",
        ContinuousTestState.Red => "red",
        ContinuousTestState.Skipped => "skipped",
        ContinuousTestState.Running => "running",
        ContinuousTestState.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown continuous test state"),
    };

    private static ContinuousTestState StateFromValue(string value) => value switch
    {
        "unknown" => ContinuousTestState.Unknown,
        "green" => ContinuousTestState.Green,
        "red" => ContinuousTestState.Red,
        "skipped" => ContinuousTestState.Skipped,
        "running" => ContinuousTestState.Running,
        "stale" => ContinuousTestState.Stale,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown continuous test state"),
    };

    private static ContinuousTestState StateForResult(string status) =>
        status.ToLowerInvariant() switch
        {
            "passed" or "pass" or "green" => ContinuousTestState.Green,
            "skipped" or "skip" => ContinuousTestState.Skipped,
            _ => ContinuousTestState.Red,
        };

    private static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }
}
