using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Locks the Scale-only bulk writer behind <see cref="RebuildLatencyTests"/> to the current julie-extract schema.
/// The fixture must pass the same gate and production readers as a live artifact.
///
/// <para><c>[Trait("Category","Scale")]</c>: this builds a (small here) bulk DB via the same writer the 50k-symbol
/// latency test uses; it is grouped with the Scale suite so the fast suite stays pure logic.</para>
/// </summary>
[Trait("Category", "Scale")]
public sealed class LargeDbWriterCurrentSchemaTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public LargeDbWriterCurrentSchemaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-largedb-current-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "symbols.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static IReadOnlyList<IndexedSymbol> Sample() => new[]
    {
        new IndexedSymbol(0, "a0000000000000000000000000000001", "Parent", "public class Parent",
            "class", "csharp", "src/Parent.cs", 1, 20, null, IsTest: false, Visibility: "public"),
        new IndexedSymbol(1, "a0000000000000000000000000000002", "DoWork", "public void DoWork()",
            "method", "csharp", "src/Parent.cs", 5, 9, "a0000000000000000000000000000001", IsTest: false),
        new IndexedSymbol(2, "a0000000000000000000000000000003", "DoWork_Smoke", "public void DoWork_Smoke()",
            "method", "csharp", "src/Helper.cs", 3, 7, null, IsTest: true),
    };

    private static SqliteConnection Open(string dbPath)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        return c;
    }

    private static bool TableExists(SqliteConnection c, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection c, string table, string column)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$c;";
        cmd.Parameters.AddWithValue("$c", column);
        return cmd.ExecuteScalar() is not null;
    }

    [Fact]
    public void Write_EmitsCurrentSchemaTables()
    {
        LargeDbWriter.Write(_dbPath, Sample());
        using var c = Open(_dbPath);

        foreach (var t in new[]
        {
            "artifact_metadata", "files", "symbols", "reference_sites", "identifiers", "relationships",
            "identifier_resolutions", "pending_relationships", "pending_resolutions", "structural_facts",
            "language_capability_gaps", "type_argument_usages", "type_arguments", "literals",
            "symbol_annotations", "parse_diagnostics",
        })
            Assert.True(TableExists(c, t), $"current table '{t}' must exist");

        Assert.False(TableExists(c, "schema_version"));
        Assert.False(TableExists(c, "external_extract_metadata"));
    }

    [Fact]
    public void Write_UsesCurrentReferenceSiteColumns()
    {
        LargeDbWriter.Write(_dbPath, Sample());
        using var c = Open(_dbPath);

        Assert.True(ColumnExists(c, "symbols", "symbol_id"));
        Assert.True(ColumnExists(c, "symbols", "path"));
        Assert.True(ColumnExists(c, "symbols", "parent_symbol_id"));
        Assert.True(ColumnExists(c, "symbols", "is_test"));
        Assert.True(ColumnExists(c, "symbols", "test_container"));
        Assert.True(ColumnExists(c, "symbols", "test_lifecycle"));
        Assert.True(ColumnExists(c, "symbols", "visibility"));
        Assert.False(ColumnExists(c, "symbols", "file_path"), "renamed to path");
        Assert.False(ColumnExists(c, "symbols", "parent_id"), "renamed to parent_symbol_id");

        Assert.True(ColumnExists(c, "relationships", "relationship_id"));
        Assert.True(ColumnExists(c, "relationships", "reference_site_id"));
        Assert.True(ColumnExists(c, "relationships", "file_id"));
        Assert.True(ColumnExists(c, "relationships", "confidence"));
        Assert.True(ColumnExists(c, "identifiers", "reference_site_id"));
        Assert.True(ColumnExists(c, "identifiers", "file_id"));
        Assert.True(ColumnExists(c, "identifiers", "confidence"));
        Assert.True(ColumnExists(c, "reference_sites", "is_exact"));
        Assert.True(ColumnExists(c, "reference_sites", "provenance"));
    }

    [Fact]
    public void Write_OutputPassesTheSchemaGate_AndReadsBackVerbatim()
    {
        LargeDbWriter.Write(_dbPath, Sample());

        var symbols = SqliteSymbolReader.Read(_dbPath);

        Assert.Equal(3, symbols.Count);
        var parent = symbols.Single(s => s.Name == "Parent");
        Assert.Equal("src/Parent.cs", parent.FilePath);
        Assert.Equal(1, parent.StartLine);
        Assert.Equal("public", parent.Visibility);
        Assert.False(parent.IsTest);

        var child = symbols.Single(s => s.Name == "DoWork");
        Assert.Equal("a0000000000000000000000000000001", child.ParentId);

        var smoke = symbols.Single(s => s.Name == "DoWork_Smoke");
        Assert.True(smoke.IsTest);
    }

    [Fact]
    public void Write_OutputBuildsThroughTheProductionLoader_WithEdges()
    {
        LargeDbWriter.Write(_dbPath, Sample());

        var index = RepositoryIndexLoader.Load(_dbPath);

        Assert.Equal(3, index.DocumentCount);
        Assert.True(index.Graph.Contains("a0000000000000000000000000000001"));
    }
}
