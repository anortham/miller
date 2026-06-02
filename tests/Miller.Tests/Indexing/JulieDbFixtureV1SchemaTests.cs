using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Locks JulieDbFixture to the v1 julie-extract artifact schema (schema.rs v1). This is the canonical
/// synthetic-schema guard the design (§10H) calls for: if the fixture drifts off v1, the readers it feeds
/// would silently test against the wrong contract. Asserts the v1 table set exists and the old-schema
/// tables/columns are GONE.
/// </summary>
public sealed class JulieDbFixtureV1SchemaTests
{
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
    public void Fixture_EmitsV1ArtifactTables_AndDropsOldSchemaTables()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);

        // Phase 3 = the non-revision v1 spine. The revision tables (extraction_revisions/revision_file_changes)
        // and the canonical_revisions DROP are H2's lock assertions (Phase 4): in Phase 3 canonical_revisions is
        // still present and untouched, so do NOT assert on revision tables here.
        foreach (var t in new[] { "artifact_metadata", "files", "symbols", "identifiers",
            "relationships", "type_argument_usages", "type_arguments", "literals", "symbol_annotations",
            "parse_diagnostics", "parser_inventory", "language_capabilities" })
            Assert.True(TableExists(c, t), $"v1 table '{t}' must exist");

        // Old-schema artifacts H1 removes are gone.
        Assert.False(TableExists(c, "schema_version"), "schema_version table is dropped in v1");
        // NOTE: external_extract_metadata is kept TRANSITIONALLY in Phase 3 (it still carries hash_algorithm for the
        // Subsystem-D ExtractFileHashReader/FreshnessGate). Phase 4 moves hash_algorithm fully onto artifact_metadata
        // and drops this table; the "external_extract_metadata is gone" assertion is Phase 4's lock, not here.
    }

    [Fact]
    public void Fixture_SymbolsAndFiles_UseV1ColumnNames()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);

        Assert.True(ColumnExists(c, "symbols", "symbol_id"));
        Assert.True(ColumnExists(c, "symbols", "path"));
        Assert.True(ColumnExists(c, "symbols", "parent_symbol_id"));
        Assert.True(ColumnExists(c, "symbols", "metadata_json"));
        Assert.True(ColumnExists(c, "symbols", "is_test"));
        Assert.False(ColumnExists(c, "symbols", "file_path"), "old column renamed to path");
        Assert.False(ColumnExists(c, "symbols", "parent_id"), "old column renamed to parent_symbol_id");
        Assert.False(ColumnExists(c, "symbols", "code_context"), "code_context moved to identifiers in v1");

        Assert.True(ColumnExists(c, "files", "content_hash"));
        Assert.True(ColumnExists(c, "files", "content_bytes"));
        // NOTE: files.hash is kept TRANSITIONALLY in Phase 3 (raw blake3 hex the Subsystem-D ExtractFileHashReader
        // still reads). Phase 4 flips the freshness path onto content_hash and drops this column; the
        // "files.hash renamed to content_hash" assertion is Phase 4's lock, not here.
        // The transitional `content` column is likewise still present in Phase 3 (OLD ReadBody reads it until
        // C3/Phase 5); the "files is content-free" assertion lives in H3's Phase-5 lock test, not here.

        Assert.True(ColumnExists(c, "artifact_metadata", "key"));
        Assert.True(ColumnExists(c, "artifact_metadata", "value"));
    }

    [Fact]
    public void Fixture_ArtifactMetadata_CarriesVersionKeys()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM artifact_metadata WHERE key='sqlite_schema_version';";
        Assert.Equal(MillerExtractContract.ExpectedSchemaVersion.ToString(
            System.Globalization.CultureInfo.InvariantCulture), cmd.ExecuteScalar()?.ToString());
    }

    [Fact]
    public void Fixture_ArtifactMetadata_FingerprintsCarrySha256Domain_NotBlake3()
    {
        // Hash-domain discipline (#9): the file CONTENT hash is blake3 (the gate's hash_algorithm), but the
        // parser/capability FINGERPRINTS are sha256-domain values Miller stores and never compares to a file hash.
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);

        foreach (var key in new[] { "parser_inventory_fingerprint", "capability_snapshot_fingerprint" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM artifact_metadata WHERE key=$k;";
            cmd.Parameters.AddWithValue("$k", key);
            var value = cmd.ExecuteScalar()?.ToString();
            Assert.NotNull(value);
            Assert.StartsWith("sha256:", value);
            Assert.DoesNotContain("blake3:", value);
        }
    }

    [Fact]
    public void Fixture_SymbolRow_SeedsTypedTestColumns()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
            {
                new JulieDbFixture.SymbolRow("a0000000000000000000000000000001", "T", "method", "csharp",
                    "Tests/T.cs", "public void T()", 3, null) { IsTest = true, TestContainer = true },
                new JulieDbFixture.SymbolRow("b0000000000000000000000000000001", "P", "method", "csharp",
                    "src/P.cs", "public void P()", 3, null), // defaults: all 0
            });
        using var c = Open(fx.DbPath);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT is_test, test_container, test_lifecycle FROM symbols WHERE symbol_id=$id;";
        cmd.Parameters.AddWithValue("$id", "a0000000000000000000000000000001");
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(1L, r.GetInt64(0));
        Assert.Equal(1L, r.GetInt64(1));
        Assert.Equal(0L, r.GetInt64(2));
    }

    [Fact]
    public void Fixture_BridgeTables_UseV1ShapesAndAnnotationsHaveNoOrdinal()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);

        Assert.True(TableExists(c, "type_argument_usages"));
        Assert.True(ColumnExists(c, "type_argument_usages", "usage_id"));
        Assert.True(ColumnExists(c, "type_argument_usages", "identifier_id"));

        Assert.True(ColumnExists(c, "type_arguments", "type_argument_id"));
        Assert.True(ColumnExists(c, "type_arguments", "usage_id"));
        Assert.True(ColumnExists(c, "type_arguments", "parent_type_argument_id"));
        Assert.False(ColumnExists(c, "type_arguments", "identifier_id"),
            "v1 moves identifier_id onto type_argument_usages");
        Assert.False(ColumnExists(c, "type_arguments", "file_path"), "v1 has no file_path here");

        Assert.True(ColumnExists(c, "literals", "literal_id"));
        Assert.True(ColumnExists(c, "literals", "path"));
        Assert.False(ColumnExists(c, "literals", "file_path"), "renamed to path");

        Assert.True(ColumnExists(c, "symbol_annotations", "annotation_id"));
        Assert.True(ColumnExists(c, "symbol_annotations", "annotation_key"));
        Assert.False(ColumnExists(c, "symbol_annotations", "ordinal"),
            "v1 drops ordinal; ordering re-keys to (symbol_id, annotation_id)");
    }

    [Fact]
    public void Fixture_EmitsV1OnlyTables()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);
        foreach (var t in new[] { "parser_inventory", "parse_diagnostics", "language_capabilities",
            "language_capability_fixtures", "language_capability_gaps",
            "pending_relationships", "type_facts" })
            Assert.True(TableExists(c, t), $"v1 artifact table '{t}' must exist");
    }
}
