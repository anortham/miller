using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Store.Core;

public sealed class ContinuousTestStoreSchemaTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public ContinuousTestStoreSchemaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-store-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, CtSchema.DbFileName);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Status_and_list_reads_on_a_missing_db_return_empty_and_do_not_create_the_file_or_miller_dir()
    {
        string root = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(root);
        string dbPath = CtSchema.DbPathFor(root);
        using var store = new ContinuousTestStore(dbPath);

        Assert.Empty(store.ListContinuousTestStatuses("ws:1"));
        Assert.Empty(store.ListTestCases("ws:1"));
        ContinuousTestFlakinessScore score = store.ScoreContinuousTestFlakiness("ws:1", "test:1");
        Assert.Equal(ContinuousTestFlakinessState.Unknown, score.State);
        Assert.Equal(0, score.Samples);

        Assert.False(File.Exists(dbPath));
        Assert.False(Directory.Exists(Path.Combine(root, CtSchema.MillerDirectoryName)));
        Assert.False(File.Exists(CtWriteLock.LockFilePathFor(dbPath)));
    }

    [Fact]
    public void Mark_stale_and_delete_on_a_missing_db_are_noops_and_do_not_create_the_file()
    {
        using var store = new ContinuousTestStore(_dbPath);

        store.MarkContinuousTestsStale("ws:1", ["test:1"], new CtFreshnessKey("gen-1", 2));
        Assert.Equal(0, store.DeleteTestCase("ws:1", "test:1"));

        Assert.False(File.Exists(_dbPath));
    }

    [Fact]
    public void PutTestCase_creates_the_db_and_applies_schema()
    {
        using var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(SampleCase("test:1"));

        Assert.True(File.Exists(_dbPath));
        using var connection = OpenRead(_dbPath);
        Assert.Equal(CtSchema.SchemaVersion, CtSchema.ReadSchemaVersion(connection));
        Assert.False(CtSchema.IsNewerSchema(connection));
    }

    [Fact]
    public void Newer_schema_reads_and_writes_throw_and_leave_the_file_untouched()
    {
        SeedNewerSchema(schemaVersion: CtSchema.SchemaVersion + 9);
        long sizeBefore = new FileInfo(_dbPath).Length;

        using var store = new ContinuousTestStore(_dbPath);
        ContinuousTestStoreSchemaException read = Assert.Throws<ContinuousTestStoreSchemaException>(
            () => store.ListContinuousTestStatuses("ws:1"));
        Assert.Contains("newer", read.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_dbPath, read.Message, StringComparison.Ordinal);
        Assert.Equal(CtSchema.SchemaVersion + 9, read.FileSchemaVersion);
        Assert.Equal(CtSchema.SchemaVersion, read.SupportedSchemaVersion);

        Assert.Throws<ContinuousTestStoreSchemaException>(() => store.ListTestCases("ws:1"));
        Assert.Throws<ContinuousTestStoreSchemaException>(() => store.PutTestCase(SampleCase("test:1")));
        Assert.Throws<ContinuousTestStoreSchemaException>(
            () => store.ScoreContinuousTestFlakiness("ws:1", "test:1"));

        using (var connection = OpenRead(_dbPath))
        {
            Assert.Equal(
                (CtSchema.SchemaVersion + 9).ToString(CultureInfo.InvariantCulture),
                Convert.ToString(
                    Scalar(connection, "SELECT value FROM meta WHERE key='schema_version';"),
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='test_cases';"),
                    CultureInfo.InvariantCulture));
        }

        Assert.Equal(sizeBefore, new FileInfo(_dbPath).Length);
        Assert.Empty(Directory.EnumerateFiles(_dir, CtSchema.DbFileName + ".corrupt-*"));
    }

    [Fact]
    public void Corrupt_file_reads_and_writes_throw_and_leave_the_bytes_in_place()
    {
        File.WriteAllText(_dbPath, "this is not a sqlite database, just garbage");
        byte[] before = File.ReadAllBytes(_dbPath);

        using var store = new ContinuousTestStore(_dbPath);
        ContinuousTestStoreUnreadableException read = Assert.Throws<ContinuousTestStoreUnreadableException>(
            () => store.ListContinuousTestStatuses("ws:1"));
        Assert.Contains("could not be read", read.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_dbPath, read.Message, StringComparison.Ordinal);

        Assert.Throws<ContinuousTestStoreUnreadableException>(() => store.ListTestCases("ws:1"));
        Assert.Throws<ContinuousTestStoreUnreadableException>(() => store.PutTestCase(SampleCase("test:1")));

        Assert.Equal(before, File.ReadAllBytes(_dbPath));
        Assert.Empty(Directory.EnumerateFiles(_dir, CtSchema.DbFileName + ".corrupt-*"));
    }

    [Fact]
    public void Store_never_exposes_a_sqlite_connection()
    {
        BindingFlags published = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public;
        foreach (Type type in new[] { typeof(ContinuousTestStore), typeof(CtWriteLock) })
        {
            foreach (MemberInfo member in type.GetMembers(published))
            {
                Assert.False(
                    ExposesSqliteConnection(member),
                    $"{type.Name}.{member.Name} exposes SqliteConnection");
            }
        }
    }

    private static bool ExposesSqliteConnection(MemberInfo member)
    {
        if (member is PropertyInfo property && IsSqliteConnection(property.PropertyType))
            return true;
        if (member is FieldInfo field && IsSqliteConnection(field.FieldType))
            return true;
        if (member is MethodInfo method)
        {
            if (IsSqliteConnection(method.ReturnType))
                return true;
            if (method.GetParameters().Any(parameter => IsSqliteConnection(parameter.ParameterType)))
                return true;
        }
        return false;
    }

    private static bool IsSqliteConnection(Type type) =>
        type == typeof(SqliteConnection)
        || type == typeof(SqliteConnection).MakeByRefType()
        || (type.IsGenericType && type.GetGenericArguments().Any(IsSqliteConnection));

    private void SeedNewerSchema(int schemaVersion)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO meta(key, value) VALUES ('schema_version', $v);
            """;
        command.Parameters.AddWithValue("$v", schemaVersion.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static SqliteConnection OpenRead(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
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

    private static ContinuousTestCase SampleCase(string id) =>
        new(
            Id: id,
            WorkspaceId: "ws:1",
            Name: id,
            QualifiedName: id,
            Selector: id + ".selector");
}
