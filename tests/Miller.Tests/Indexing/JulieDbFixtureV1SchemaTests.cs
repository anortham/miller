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

        foreach (var t in new[] { "artifact_metadata", "files", "symbols", "identifiers",
            "relationships", "type_argument_usages", "type_arguments", "literals", "symbol_annotations",
            "parse_diagnostics", "parser_inventory", "language_capabilities",
            "extraction_revisions", "revision_file_changes" })
            Assert.True(TableExists(c, t), $"v1 table '{t}' must exist");

        // Old-schema artifacts removed in v1 are gone.
        Assert.False(TableExists(c, "schema_version"), "schema_version table is dropped in v1");
        Assert.False(TableExists(c, "external_extract_metadata"),
            "external_extract_metadata is dropped in v1 (hash_algorithm moved onto artifact_metadata)");
    }

    // ---- H2: v1 revision tables (extraction_revisions / revision_file_changes) ----

    [Fact]
    public void Fixture_RevisionTables_AreV1_CanonicalRevisionsGone()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);
        Assert.True(TableExists(c, "extraction_revisions"), "v1 revision table present");
        Assert.True(TableExists(c, "revision_file_changes"), "v1 per-file change table present");
        Assert.False(TableExists(c, "canonical_revisions"), "old revision table renamed to extraction_revisions in v1");
    }

    [Fact]
    public void Fixture_ExtractionRevisions_AreWorkspaceIdFree_AndKeyedByRevisionId()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>(),
            revisions: new[] { new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2) });
        using var c = Open(fx.DbPath);

        Assert.False(ColumnExists(c, "extraction_revisions", "workspace_id"),
            "v1 extraction_revisions has no workspace_id (one DB = one root)");
        Assert.True(ColumnExists(c, "extraction_revisions", "revision_id"));

        using var max = c.CreateCommand();
        max.CommandText = "SELECT MAX(revision_id) FROM extraction_revisions;";
        Assert.Equal(2L, System.Convert.ToInt64(max.ExecuteScalar()));
    }

    [Fact]
    public void Fixture_RevisionFileChanges_UseV1VocabularyWithoutCheckConstraint()
    {
        // v1 has NO CHECK on change_kind; 'unsupported' (a v1-only value) must insert without error.
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>(),
            revisions: new[] { new JulieDbFixture.RevisionRow(1) },
            fileChanges: new[]
            {
                new JulieDbFixture.RevisionFileChangeRow(1, "a.cs", "inserted"),
                new JulieDbFixture.RevisionFileChangeRow(1, "b.cs", "unsupported"),
            });
        using var c = Open(fx.DbPath);
        Assert.False(ColumnExists(c, "revision_file_changes", "workspace_id"));
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM revision_file_changes;";
        Assert.Equal(2L, System.Convert.ToInt64(cmd.ExecuteScalar()));
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
        Assert.False(ColumnExists(c, "files", "hash"),
            "the transitional raw-hex files.hash column is dropped in v1; freshness reads content_hash");
        Assert.False(ColumnExists(c, "files", "content"),
            "v1 files is content-free (H3/Phase 5); body text re-sources from disk under WorkspaceRoot");

        Assert.True(ColumnExists(c, "artifact_metadata", "key"));
        Assert.True(ColumnExists(c, "artifact_metadata", "value"));
    }

    // ---- H3: v1 files are content-free; body text re-sources from disk under WorkspaceRoot ----

    [Fact]
    public void Fixture_FilesStoreContentHashPrefixedAndByteCount_NotContent()
    {
        using var fx = JulieDbFixture.CreateForInspect(); // writes UserServiceContent
        using var c = Open(fx.DbPath);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT content_hash, content_bytes FROM files WHERE path='auth/UserService.cs';";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        string hash = r.GetString(0);
        long bytes = r.GetInt64(1);

        Assert.StartsWith("blake3:", hash);
        var expected = System.Text.Encoding.UTF8.GetBytes(JulieDbFixture.UserServiceContent);
        Assert.Equal(expected.Length, bytes);
        Assert.Equal("blake3:" + Miller.Indexing.ContentHasher.Blake3Hex(expected), hash);

        // H3 drops H1's transitional `content` column in Phase 5 — v1 is content-free (D2). (Moved here from
        // H1's lock test, which keeps `content` through Phases 3-4 for the OLD ReadBody path.)
        Assert.False(ColumnExists(c, "files", "content"), "v1 files has no content column after H3");
    }

    [Fact]
    public void Fixture_MaterializesFilesUnderWorkspaceRoot_MatchingStoredHash()
    {
        using var fx = JulieDbFixture.CreateForEdit();

        // Bytes are on disk under WorkspaceRoot (no test-side write), and their blake3 equals the stored content_hash.
        foreach (var (rel, content) in new[]
        {
            ("orders/OrderService.cs", JulieDbFixture.OrderServiceContent),
            ("unicode/Café.cs", JulieDbFixture.CafeContent),
        })
        {
            string abs = Path.Combine(fx.WorkspaceRoot, rel);
            Assert.True(File.Exists(abs), $"{rel} must be materialized under WorkspaceRoot");
            var bytes = File.ReadAllBytes(abs);
            Assert.Equal(content, System.Text.Encoding.UTF8.GetString(bytes));

            using var c = Open(fx.DbPath);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT content_hash FROM files WHERE path=$p;";
            cmd.Parameters.AddWithValue("$p", rel);
            Assert.Equal("blake3:" + Miller.Indexing.ContentHasher.Blake3Hex(bytes), (string)cmd.ExecuteScalar()!);
        }
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
