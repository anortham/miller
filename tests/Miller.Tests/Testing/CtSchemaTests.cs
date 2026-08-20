using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing;

public sealed class CtSchemaTests : IDisposable
{
    private static readonly string[] FreshnessBearingTables =
    [
        "test_runs",
        "test_results",
        "ct_test_states",
        "ct_case_fresh_watermarks",
        "coverage_files",
        "coverage_spans",
        "confidence_snapshots",
        "ct_coverage_maps",
        "ct_coverage_map_files",
        "ct_coverage_delta_receipts",
        "ct_coverage_delta_map_applications",
    ];

    private readonly string _dir;
    private readonly string _dbPath;

    public CtSchemaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, CtSchema.DbFileName);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Apply_creates_self_contained_schema_with_composite_freshness_columns()
    {
        using var connection = OpenWrite();
        CtSchema.Apply(connection);

        Assert.Equal("2", Convert.ToString(
            Scalar(connection, "SELECT value FROM meta WHERE key='schema_version';"),
            CultureInfo.InvariantCulture));
        Assert.Equal(CtSchema.SchemaVersion, CtSchema.ReadSchemaVersion(connection));
        Assert.Equal(CtSchema.SchemaVersion, UserVersion(connection));

        foreach (string table in FreshnessBearingTables)
        {
            IReadOnlyList<string> columns = TableColumns(connection, table);
            Assert.Contains("index_identity", columns, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("revision", columns, StringComparer.OrdinalIgnoreCase);
        }

        IReadOnlyList<string> caseColumns = TableColumns(connection, "test_cases");
        Assert.Contains("file_path", caseColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("content_hash", caseColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("symbol_name", caseColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("symbol_path", caseColumns, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("file_id", caseColumns, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("symbol_id", caseColumns, StringComparer.OrdinalIgnoreCase);

        foreach (string sql in TableSql(connection))
        {
            Assert.DoesNotContain("references files(", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("references symbols(", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("references search_docs(", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("attach ", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Apply_stamps_schema_version_once_and_leaves_a_newer_version_untouched()
    {
        using (var connection = OpenWrite())
        {
            CtSchema.Apply(connection);
            CtSchema.Apply(connection);
            Assert.Equal(CtSchema.SchemaVersion, CtSchema.ReadSchemaVersion(connection));

            using var bump = connection.CreateCommand();
            bump.CommandText = "UPDATE meta SET value = $v WHERE key = 'schema_version';";
            bump.Parameters.AddWithValue("$v", (CtSchema.SchemaVersion + 7).ToString(CultureInfo.InvariantCulture));
            bump.ExecuteNonQuery();
        }

        using (var connection = OpenWrite())
        {
            CtSchema.Apply(connection);
            Assert.Equal(CtSchema.SchemaVersion + 7, CtSchema.ReadSchemaVersion(connection));
            Assert.True(CtSchema.IsNewerSchema(connection));
        }
    }

    /// <summary>
    /// The rebuild that drops the selector uniqueness must not take the referencing rows with it.
    /// SIX tables reference <c>test_cases(id)</c> and every one of them cascades on delete, so a
    /// <c>DROP TABLE test_cases</c> with foreign keys ON empties all six — silently, in a migration
    /// that reports success. Measured on the developer's live file that was 5,754 <c>test_results</c>
    /// rows and 6,108 <c>ct_test_states</c> rows.
    /// </summary>
    [Fact]
    public void Apply_migrates_a_legacy_db_and_keeps_every_row_that_references_a_test_case()
    {
        using (var connection = OpenWrite())
        {
            CreateLegacySchema(connection);
            SeedReferencingRows(connection);

            Assert.Equal(0L, UserVersion(connection));
            Assert.True(HasSelectorUniqueIndex(connection), "the legacy fixture must carry the constraint");
            foreach (string table in CascadingTables)
                Assert.Equal(RowsPerTable[table], RowCount(connection, table));
        }

        using (var connection = OpenWrite())
        {
            CtSchema.Apply(connection);

            foreach (string table in CascadingTables)
                Assert.Equal(RowsPerTable[table], RowCount(connection, table));

            Assert.Equal(2, RowCount(connection, "test_cases"));
            Assert.Empty(ForeignKeyViolations(connection));
            Assert.False(HasSelectorUniqueIndex(connection));
            Assert.Equal(CtSchema.SchemaVersion, UserVersion(connection));
            Assert.Equal(CtSchema.SchemaVersion, CtSchema.ReadSchemaVersion(connection));

            IReadOnlyList<string> indexes = IndexNames(connection, "test_cases");
            Assert.Contains("idx_test_cases_workspace_id", indexes);
            Assert.Contains("idx_test_cases_file_path", indexes);

            // The point of the migration: a second case may now claim the first one's selector.
            InsertCase(connection, "case:3", selector: "-method Suite.Theory");
            Assert.Equal(3, RowCount(connection, "test_cases"));
        }
    }

    /// <summary>
    /// A migrated file and a file the DDL created fresh must be the same database. If they diverge,
    /// a bug reproduces on one machine and not the other.
    /// </summary>
    [Fact]
    public void A_migrated_db_and_a_fresh_db_land_on_the_same_version_and_the_same_test_cases_shape()
    {
        using (var connection = OpenWrite())
        {
            CreateLegacySchema(connection);
            SeedReferencingRows(connection);
        }

        long migratedVersion;
        IReadOnlyList<string> migratedColumns;
        IReadOnlyList<string> migratedIndexes;
        using (var connection = OpenWrite())
        {
            CtSchema.Apply(connection);
            migratedVersion = UserVersion(connection);
            migratedColumns = TableColumns(connection, "test_cases");
            migratedIndexes = IndexNames(connection, "test_cases");
        }

        string freshPath = Path.Combine(_dir, "fresh.db");
        using (var connection = OpenWrite(freshPath))
        {
            CtSchema.Apply(connection);
            Assert.Equal(migratedVersion, UserVersion(connection));
            Assert.Equal(CtSchema.SchemaVersion, UserVersion(connection));
            Assert.Equal(migratedColumns, TableColumns(connection, "test_cases"));
            Assert.Equal(migratedIndexes, IndexNames(connection, "test_cases"));
            Assert.False(HasSelectorUniqueIndex(connection));
        }
    }

    /// <summary>
    /// The migration runs only when the file is behind. A second <c>Apply</c> must not rebuild the
    /// table again, and a re-run on an already current file must leave its rows alone.
    /// </summary>
    [Fact]
    public void Apply_is_idempotent_and_leaves_an_already_migrated_db_untouched()
    {
        using (var connection = OpenWrite())
        {
            CreateLegacySchema(connection);
            SeedReferencingRows(connection);
        }

        for (int pass = 0; pass < 3; pass++)
        {
            using var connection = OpenWrite();
            CtSchema.Apply(connection);
            Assert.Equal(CtSchema.SchemaVersion, UserVersion(connection));
            Assert.Equal(2, RowCount(connection, "test_cases"));
            foreach (string table in CascadingTables)
                Assert.Equal(RowsPerTable[table], RowCount(connection, table));
            Assert.Empty(ForeignKeyViolations(connection));
        }
    }

    /// <summary>
    /// The two version markers must land in ONE transaction. <c>PRAGMA user_version</c> tells THIS binary
    /// whether to re-run the migration; <c>meta.schema_version</c> is what an OLDER binary reads to decide
    /// whether it may write the file at all. Stamping the pragma at commit and <c>meta</c> afterwards left a
    /// window in which a schema-2 file still advertised schema 1, and an older Miller would then write the
    /// constraint-free shape instead of refusing it.
    ///
    /// <para>The trigger below makes the <c>meta</c> write fail at exactly that moment. With both stamps in
    /// one transaction the whole rebuild rolls back and the file stays wholly at version 1. With the stamps
    /// split, the rebuild commits first and the file is left at the mismatched state this test forbids.</para>
    /// </summary>
    [Fact]
    public void A_failed_schema_version_stamp_rolls_the_whole_migration_back()
    {
        using (var connection = OpenWrite())
        {
            CreateLegacySchema(connection);
            SeedReferencingRows(connection);
            Execute(
                connection,
                """
                CREATE TRIGGER refuse_schema_version BEFORE UPDATE ON meta
                WHEN NEW.key = 'schema_version'
                BEGIN
                    SELECT RAISE(ABORT, 'simulated failure while stamping schema_version');
                END;
                """);
        }

        using (var connection = OpenWrite())
        {
            Assert.Throws<SqliteException>(() => CtSchema.Apply(connection));
        }

        using (var connection = OpenWrite())
        {
            // Nothing may have moved: not the shape, not either stamp, not one row.
            Assert.True(HasSelectorUniqueIndex(connection), "the rebuild committed without its version stamp");
            Assert.Equal(0L, UserVersion(connection));
            Assert.Equal(1, CtSchema.ReadSchemaVersion(connection));
            foreach (string table in CascadingTables)
                Assert.Equal(RowsPerTable[table], RowCount(connection, table));
        }
    }

    /// <summary>
    /// The one way this migration could still destroy data: <c>PRAGMA foreign_keys=OFF</c> is a NO-OP
    /// inside a transaction, so a caller that opened one first would run the rebuild with cascades live.
    /// The migration reads the switch back and refuses. Proven here by opening a transaction and
    /// checking that every referencing row is still there after the refusal.
    /// </summary>
    [Fact]
    public void Apply_refuses_to_migrate_inside_a_transaction_instead_of_cascading_the_rows_away()
    {
        using (var connection = OpenWrite())
        {
            CreateLegacySchema(connection);
            SeedReferencingRows(connection);
        }

        using (var connection = OpenWrite())
        {
            Execute(connection, "PRAGMA foreign_keys=ON;");
            using SqliteTransaction transaction = connection.BeginTransaction();

            var error = Assert.Throws<InvalidOperationException>(() => CtSchema.Apply(connection));
            Assert.Contains("foreign keys", error.Message, StringComparison.Ordinal);

            transaction.Rollback();
        }

        using (var connection = OpenWrite())
        {
            foreach (string table in CascadingTables)
                Assert.Equal(RowsPerTable[table], RowCount(connection, table));
            Assert.Equal(2, RowCount(connection, "test_cases"));
            Assert.True(HasSelectorUniqueIndex(connection), "the refused migration must change nothing");
            Assert.Equal(0L, UserVersion(connection));
        }
    }

    [Fact]
    public void DbPathFor_places_ct_db_beside_the_control_plane_directory()
    {
        string root = Path.Combine(_dir, "workspace");
        Assert.Equal(Path.Combine(root, ".miller", "ct.db"), CtSchema.DbPathFor(root));
        Assert.Equal("ct.db", CtSchema.DbFileName);
    }

    private SqliteConnection OpenWrite() => OpenWrite(_dbPath);

    private static SqliteConnection OpenWrite(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Every table that references <c>test_cases(id)</c>. All six declare <c>ON DELETE CASCADE</c>.
    /// </summary>
    private static readonly string[] CascadingTables =
    [
        "test_results",
        "test_links",
        "test_quality_findings",
        "ct_test_states",
        "ct_case_fresh_watermarks",
        "ct_coverage_maps",
    ];

    private static readonly Dictionary<string, int> RowsPerTable = new(StringComparer.Ordinal)
    {
        ["test_results"] = 2,
        ["test_links"] = 2,
        ["test_quality_findings"] = 2,
        ["ct_test_states"] = 2,
        ["ct_case_fresh_watermarks"] = 2,
        ["ct_coverage_maps"] = 2,
    };

    /// <summary>
    /// The <c>test_cases</c> table exactly as schema version 1 wrote it, WITH
    /// <c>UNIQUE (workspace_id, selector, source)</c>. Frozen on purpose: a migration test must build
    /// the shape it migrates FROM, not the shape the current DDL produces.
    /// </summary>
    private const string LegacyTestCasesDdl = """
        CREATE TABLE test_cases (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            file_path TEXT,
            content_hash TEXT,
            symbol_name TEXT,
            symbol_path TEXT,
            suite_id TEXT REFERENCES test_suites(id) ON DELETE SET NULL,
            name TEXT NOT NULL,
            qualified_name TEXT NOT NULL,
            selector TEXT NOT NULL,
            framework TEXT,
            role TEXT NOT NULL,
            source TEXT NOT NULL,
            confidence REAL NOT NULL CHECK (confidence >= 0.0 AND confidence <= 1.0),
            metadata_json TEXT NOT NULL DEFAULT '{}',
            provenance_json TEXT NOT NULL DEFAULT '{}',
            UNIQUE (workspace_id, selector, source)
        );
        CREATE INDEX IF NOT EXISTS idx_test_cases_workspace_id ON test_cases(workspace_id);
        CREATE INDEX IF NOT EXISTS idx_test_cases_file_path ON test_cases(file_path);
        """;

    /// <summary>
    /// Builds a version 1 file: the current tables, then <c>test_cases</c> swapped back to its legacy
    /// shape, and <c>user_version</c> left at the 0 every field file carries.
    /// </summary>
    private static void CreateLegacySchema(SqliteConnection connection)
    {
        // A field ct.db is already in WAL, because version 1's Apply put it there. That matters:
        // Apply's own `PRAGMA journal_mode=WAL` errors inside a transaction on a non-WAL file, which
        // would mask the migration's foreign-key guard behind an unrelated failure.
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, CtSchema.Ddl);
        Execute(connection, "DROP TABLE test_cases;");
        Execute(connection, LegacyTestCasesDdl);
        Execute(connection, "PRAGMA user_version = 0;");
        Execute(
            connection,
            "INSERT OR REPLACE INTO meta(key, value) VALUES ('schema_version', '1');");
    }

    private static void SeedReferencingRows(SqliteConnection connection)
    {
        InsertCase(connection, "case:1", selector: "-method Suite.Theory");
        InsertCase(connection, "case:2", selector: "-method Suite.Other");
        Execute(connection, """
            INSERT INTO test_runs (id, workspace_id, index_identity, revision, status)
            VALUES ('run:1', 'ws:1', 'gen-1', 1, 'passed');

            INSERT INTO test_results (id, workspace_id, index_identity, revision, test_case_id, test_run_id, status)
            VALUES ('result:1', 'ws:1', 'gen-1', 1, 'case:1', 'run:1', 'passed'),
                   ('result:2', 'ws:1', 'gen-1', 1, 'case:2', 'run:1', 'failed');

            INSERT INTO test_links (id, workspace_id, test_case_id, tier, confidence, explanation)
            VALUES ('link:1', 'ws:1', 'case:1', 'symbol', 0.9, 'same name'),
                   ('link:2', 'ws:1', 'case:2', 'symbol', 0.8, 'same file');

            INSERT INTO test_quality_findings
                (id, workspace_id, test_case_id, finding_type, severity, confidence, explanation)
            VALUES ('finding:1', 'ws:1', 'case:1', 'no_assert', 'warn', 0.7, 'asserts nothing'),
                   ('finding:2', 'ws:1', 'case:2', 'no_assert', 'warn', 0.6, 'asserts nothing');

            INSERT INTO ct_test_states (test_case_id, workspace_id, index_identity, revision, state)
            VALUES ('case:1', 'ws:1', 'gen-1', 1, 'green'),
                   ('case:2', 'ws:1', 'gen-1', 1, 'red');

            INSERT INTO ct_case_fresh_watermarks (test_case_id, workspace_id, index_identity, revision)
            VALUES ('case:1', 'ws:1', 'gen-1', 1),
                   ('case:2', 'ws:1', 'gen-1', 1);

            INSERT INTO ct_coverage_maps
                (map_id, workspace_id, index_identity, revision, test_case_id, project_path, run_id,
                 generation_id, start_converged, end_converged, complete, granularity, recorded_at, source)
            VALUES ('map:1', 'ws:1', 'gen-1', 1, 'case:1', 'p.csproj', 'run:1', 'g1', 1, 1, 1, 'test', 'now', 'dotnet'),
                   ('map:2', 'ws:1', 'gen-1', 1, 'case:2', 'p.csproj', 'run:1', 'g1', 1, 1, 1, 'test', 'now', 'dotnet');
            """);
    }

    private static void InsertCase(SqliteConnection connection, string id, string selector)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO test_cases (id, workspace_id, name, qualified_name, selector, role, source, confidence)
            VALUES ($id, 'ws:1', $id, $id, $selector, 'testcase', 'ct-provider:dotnet', 1.0);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$selector", selector);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long UserVersion(SqliteConnection connection) =>
        Convert.ToInt64(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);

    private static int RowCount(SqliteConnection connection, string table) =>
        Convert.ToInt32(Scalar(connection, $"SELECT COUNT(*) FROM {table};"), CultureInfo.InvariantCulture);

    private static List<string> ForeignKeyViolations(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"{reader.GetValue(0)}/{reader.GetValue(1)}/{reader.GetValue(2)}");
        return rows;
    }

    private static List<string> IndexNames(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list({table});";
        using var reader = command.ExecuteReader();
        int nameOrdinal = reader.GetOrdinal("name");
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(nameOrdinal));
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// True when <c>test_cases</c> still carries a UNIQUE index over
    /// (<c>workspace_id</c>, <c>selector</c>, <c>source</c>). Read structurally rather than by matching
    /// the table's SQL text, because <c>ALTER TABLE ... RENAME</c> rewrites that text.
    /// </summary>
    private static bool HasSelectorUniqueIndex(SqliteConnection connection)
    {
        var uniqueIndexes = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "PRAGMA index_list(test_cases);";
            using var reader = list.ExecuteReader();
            int nameOrdinal = reader.GetOrdinal("name");
            int uniqueOrdinal = reader.GetOrdinal("unique");
            while (reader.Read())
            {
                if (reader.GetInt64(uniqueOrdinal) != 0)
                    uniqueIndexes.Add(reader.GetString(nameOrdinal));
            }
        }

        foreach (string indexName in uniqueIndexes)
        {
            using var info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info('{indexName}');";
            using var reader = info.ExecuteReader();
            int nameOrdinal = reader.GetOrdinal("name");
            var columns = new List<string>();
            while (reader.Read())
                columns.Add(reader.IsDBNull(nameOrdinal) ? "" : reader.GetString(nameOrdinal));
            if (columns is ["workspace_id", "selector", "source"])
                return true;
        }

        return false;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static List<string> TableColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        Assert.False(columns.Count == 0, $"missing table {table}");
        return columns;
    }

    private static List<string> TableSql(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL;";
        using var reader = command.ExecuteReader();
        var sql = new List<string>();
        while (reader.Read())
            sql.Add(reader.GetString(0));
        return sql;
    }
}
