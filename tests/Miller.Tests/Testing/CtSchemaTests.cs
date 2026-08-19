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

        Assert.Equal("1", Convert.ToString(
            Scalar(connection, "SELECT value FROM meta WHERE key='schema_version';"),
            CultureInfo.InvariantCulture));
        Assert.Equal(CtSchema.SchemaVersion, CtSchema.ReadSchemaVersion(connection));

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

    [Fact]
    public void DbPathFor_places_ct_db_beside_the_control_plane_directory()
    {
        string root = Path.Combine(_dir, "workspace");
        Assert.Equal(Path.Combine(root, ".miller", "ct.db"), CtSchema.DbPathFor(root));
        Assert.Equal("ct.db", CtSchema.DbFileName);
    }

    private SqliteConnection OpenWrite()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
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
