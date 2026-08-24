using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Locks JulieDbFixture to the pinned julie-extract artifact contract. This is the canonical synthetic
/// schema guard the readers rely on: if the fixture drifts off the pinned contract, tests would silently
/// exercise the wrong SQLite shape.
/// </summary>
public sealed class JulieDbFixtureCurrentSchemaTests
{
    private const string QmlFixtureArtifactSha256 =
        "b30424877ae1d7e04e2ed7659e190d75578bb20882046c0a80d6144f92f6cb4a";

    private static string QmlFixtureArtifactPath => Path.Combine(
        ScaleTestSupport.RepoRoot(), "tests", "Miller.Tests", "Fixtures", "QmlFirstClass", "symbols.db");

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

    private static bool IndexExists(SqliteConnection c, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    [Fact]
    public void Fixture_EmitsCurrentArtifactTables_AndDropsOldSchemaTables()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);

        foreach (var t in new[] { "artifact_metadata", "files", "symbols", "identifiers",
            "relationships", "type_argument_usages", "type_arguments", "literals", "symbol_annotations",
            "parse_diagnostics", "parser_inventory", "language_capabilities",
            "extraction_revisions", "revision_file_changes", "source_regions",
            "structural_facts", "complexity_metrics" })
            Assert.True(TableExists(c, t), $"pinned schema table '{t}' must exist");

        // Old-schema artifacts removed in v1 are gone.
        Assert.False(TableExists(c, "schema_version"), "schema_version table is dropped in v1");
        Assert.False(TableExists(c, "external_extract_metadata"),
            "external_extract_metadata is dropped in v1 (hash_algorithm moved onto artifact_metadata)");
    }

    [Fact]
    public void QmlFixture_ContainsReleasedJulieFamilyFacts()
    {
        Assert.Equal(
            QmlFixtureArtifactSha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(QmlFixtureArtifactPath))).ToLowerInvariant());

        using var c = Open(QmlFixtureArtifactPath);

        Assert.Equal("2.35.1", Metadata(c, "binary_version"));
        Assert.Equal("7", Metadata(c, "schema_version"));
        Assert.Equal("7", Metadata(c, "sqlite_schema_version"));
        Assert.Equal("4", Metadata(c, "extract_contract_version"));
        Assert.Equal("blake3", Metadata(c, "hash_algorithm"));

        AssertMetadata(c, "source.qml", "components", "import_kind", "directory");
        AssertMetadata(c, "source.qml", "components", "alias", "Components");
        AssertMetadata(c, "source.qml", "QtQuick.Controls", "import_kind", "module");
        AssertMetadata(c, "source.qml", "./js/helpers.js", "import_kind", "javascript");

        foreach (string pattern in new[]
        {
            "qmldir.module.v1", "qmldir.object_type.v1", "qmldir.singleton_type.v1",
            "qmldir.internal_type.v1", "qmldir.typeinfo.v1",
        })
            Assert.Equal(1L, Count(c, "SELECT COUNT(*) FROM structural_facts WHERE path='components/qmldir' AND pattern_id=$p;", ("$p", pattern)));

        using (var command = c.CreateCommand())
        {
            command.CommandText = """
                SELECT metadata_json
                FROM symbols
                WHERE path='components/Module.qmltypes' AND name='RemoteCard' AND kind='class';
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            using JsonDocument metadata = JsonDocument.Parse(reader.GetString(0));
            Assert.Equal("type", metadata.RootElement.GetProperty("typeinfo_kind").GetString());
            Assert.Contains(
                "Example/Components 1.0",
                metadata.RootElement.GetProperty("exports").EnumerateArray().Select(value => value.GetString()),
                StringComparer.Ordinal);
        }

        AssertMetadata(c, "components/qmldir", "RemoteCard", "file", "RemoteCard.qml");
        Assert.Equal(1L, Count(c, "SELECT COUNT(*) FROM files WHERE path='components/RemoteCard.qml';"));

        using (var command = c.CreateCommand())
        {
            command.CommandText = """
                SELECT target_display_name, target_terminal_name
                FROM pending_relationships
                WHERE path='source.qml' AND kind='instantiates'
                ORDER BY target_terminal_name;
                """;
            using var reader = command.ExecuteReader();
            var rows = new List<(string Display, string Terminal)>();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1)));

            Assert.Equal(
                new[] { ("LocalCard", "LocalCard"), ("Components.RemoteCard", "RemoteCard") },
                rows);
        }
    }

    [Fact]
    public void Fixture_SourceRegions_UseCurrentColumnSetAndIndexes()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);

        foreach (var column in new[]
        {
            "source_region_id", "file_id", "path", "language", "kind", "containing_symbol_id",
            "start_line", "start_column", "end_line", "end_column", "start_byte", "end_byte",
            "metadata_json",
        })
            Assert.True(ColumnExists(c, "source_regions", column), $"source_regions.{column} must exist");

        Assert.True(IndexExists(c, "idx_source_regions_file_span"));
        Assert.True(IndexExists(c, "idx_source_regions_kind_file"));
        Assert.True(IndexExists(c, "idx_source_regions_symbol"));
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

    // ---- v4 reference-resolution tables (identifier_resolutions / pending_resolutions) + pinned indexes ----

    [Fact]
    public void Fixture_EmitsV4ResolutionTables()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);
        foreach (var t in new[] { "identifier_resolutions", "pending_resolutions", "pending_relationships" })
            Assert.True(TableExists(c, t), $"v4 resolution table '{t}' must exist");
    }

    [Fact]
    public void Fixture_IdentifierResolutions_UseCurrentColumnSet()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);
        foreach (var column in new[]
        {
            "identifier_id", "target_symbol_id", "tier", "confidence", "method",
            "outcome", "candidates", "resolved_at_revision",
        })
            Assert.True(ColumnExists(c, "identifier_resolutions", column),
                $"identifier_resolutions.{column} must exist");
    }

    [Fact]
    public void Fixture_IdentifierResolutions_EnforceOutcomeTargetCheckConstraint()
    {
        using var fx = JulieDbFixture.CreateDefault();

        // A 'resolved' outcome REQUIRES a non-null target_symbol_id (CHECK ((outcome='resolved') = (target IS NOT NULL))).
        var resolvedWithoutTarget = Assert.Throws<SqliteException>(() => ExecWrite(fx.DbPath, """
            INSERT INTO identifier_resolutions
                (identifier_id, target_symbol_id, tier, confidence, method, outcome, candidates, resolved_at_revision)
            VALUES ('ir-bad-1', NULL, 1, 1.0, 'exact', 'resolved', 1, 1);
            """));
        Assert.Contains("CHECK", resolvedWithoutTarget.Message, StringComparison.OrdinalIgnoreCase);

        // A non-'resolved' outcome REQUIRES a null target_symbol_id.
        Assert.Throws<SqliteException>(() => ExecWrite(fx.DbPath, """
            INSERT INTO identifier_resolutions
                (identifier_id, target_symbol_id, tier, confidence, method, outcome, candidates, resolved_at_revision)
            VALUES ('ir-bad-2', 'sym-x', 1, 1.0, 'exact', 'ambiguous', 3, 1);
            """));

        // Both consistent shapes insert cleanly.
        ExecWrite(fx.DbPath, """
            INSERT INTO identifier_resolutions
                (identifier_id, target_symbol_id, tier, confidence, method, outcome, candidates, resolved_at_revision)
            VALUES ('ir-ok-resolved', 'sym-x', 1, 1.0, 'exact', 'resolved', 1, 1),
                   ('ir-ok-unresolved', NULL, 1, 1.0, 'exact', 'ambiguous', 3, 1);
            """);
        using var c = Open(fx.DbPath);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM identifier_resolutions;";
        Assert.Equal(2L, System.Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void Fixture_PendingResolutions_UseCurrentColumnSet()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);
        foreach (var column in new[]
        {
            "pending_relationship_id", "target_symbol_id", "tier", "confidence", "method", "resolved_at_revision",
        })
            Assert.True(ColumnExists(c, "pending_resolutions", column),
                $"pending_resolutions.{column} must exist");
    }

    [Fact]
    public void Fixture_PendingRelationships_UseCurrentColumnSet()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);
        foreach (var column in new[]
        {
            "pending_relationship_id", "from_symbol_id", "caller_scope_symbol_id", "file_id", "path", "kind",
            "target_display_name", "target_terminal_name", "target_receiver", "target_namespace_json",
            "target_import_context", "start_byte", "end_byte", "confidence",
        })
            Assert.True(ColumnExists(c, "pending_relationships", column),
                $"pending_relationships.{column} must exist");
    }

    [Fact]
    public void Fixture_V4ResolutionIndexes_Exist()
    {
        using var fx = JulieDbFixture.CreateDefault();
        using var c = Open(fx.DbPath);
        foreach (var index in new[]
        {
            "idx_identifier_resolutions_target", "idx_pending_terminal", "idx_pending_file",
            "idx_pending_from", "idx_pending_caller_scope", "idx_pending_resolutions_target",
        })
            Assert.True(IndexExists(c, index), $"pinned v4 index '{index}' must exist");
    }

    private static void ExecWrite(string dbPath, string sql)
    {
        // ForeignKeys=false matches the fixture's relaxed-FK philosophy so the CHECK-constraint assertions do not
        // trip on unseeded target_symbol_id references; the CHECK itself is enforced independent of the FK pragma.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Metadata(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM artifact_metadata WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static void AssertMetadata(
        SqliteConnection connection, string path, string name, string key, string expected)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT metadata_json FROM symbols WHERE path=$path AND name=$name;";
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$name", name);
        using JsonDocument metadata = JsonDocument.Parse(Assert.IsType<string>(command.ExecuteScalar()));
        Assert.Equal(expected, metadata.RootElement.GetProperty(key).GetString());
    }

    private static long Count(
        SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return System.Convert.ToInt64(command.ExecuteScalar());
    }
}
