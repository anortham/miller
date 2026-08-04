using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Tests.Indexing;

namespace Miller.Tests.Support;

/// <summary>
/// Synthesizes a julie-extract artifact at a chosen index level, without spawning <c>julie-extract</c> (it writes
/// SQLite directly, so it stays in the fast suite).
///
/// <para>The row shape is the one a REAL symbols-level artifact carries: <c>symbols</c>, <c>files</c>,
/// <c>relationships</c>, <c>reference_sites</c>, <c>complexity_metrics</c> and <c>type_facts</c> are populated
/// while <c>identifiers</c>, <c>identifier_resolutions</c>, <c>source_regions</c> and <c>structural_facts</c> are
/// EMPTY. Zeroing every table instead would make a symbols-level artifact look like an artifact with no code in
/// it, and any consumer built on that picture would be built on a false one.</para>
/// </summary>
internal static class SymbolsLevelArtifact
{
    /// <summary>Write a symbols-level artifact into <paramref name="dir"/> and return its path.</summary>
    internal static string Create(string dir) => Write(dir, IndexLevels.SymbolsMetadataValue);

    /// <summary>
    /// Write a full-level artifact into <paramref name="dir"/> and return its path — the negative case, where the
    /// reference/region/facts layers carry rows and no guard may fire.
    /// </summary>
    internal static string CreateFull(string dir) => Write(dir, IndexLevels.FullMetadataValue);

    private const string AlphaPath = "src/Alpha.cs";
    private const string BetaPath = "src/Beta.cs";
    private const string AlphaText = "public class Alpha { public void Run() { } }\n";
    private const string BetaText = "public class Beta { public void Call() { new Alpha().Run(); } }\n";

    private static string Write(string dir, string indexLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "symbols.db");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            Exec(connection, "PRAGMA journal_mode=WAL;");
            Exec(connection, "PRAGMA foreign_keys=OFF;");
            Exec(connection, "PRAGMA synchronous=OFF;");
            Exec(connection, "BEGIN;");

            JulieDbFixture.EnsureCurrentSchema(connection);

            WriteFile(connection, dir, AlphaPath, AlphaText);
            WriteFile(connection, dir, BetaPath, BetaText);
            WriteSymbols(connection);
            WriteRelationship(connection);
            WriteReferenceSites(connection);
            WriteComplexityMetric(connection);
            WriteTypeFact(connection);
            WriteRevision(connection);

            if (!string.Equals(indexLevel, IndexLevels.SymbolsMetadataValue, StringComparison.Ordinal))
                WriteReferenceLayer(connection);

            WriteMetadata(connection, indexLevel);

            Exec(connection, "COMMIT;");
        }

        return dbPath;
    }

    private static void WriteFile(SqliteConnection connection, string dir, string path, string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        string absolute = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, bytes);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO files
                (file_id, path, language, content_hash, content_bytes, line_count,
                 indexed_at, last_revision_id, status, metadata_json)
            VALUES ($fid, $path, 'csharp', $hash, $bytes, 1, '1970-01-01T00:00:00Z', 1, 'indexed', NULL);
            """;
        command.Parameters.AddWithValue("$fid", FileId(path));
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$hash", "blake3:" + ContentHasher.Blake3Hex(bytes));
        command.Parameters.AddWithValue("$bytes", bytes.Length);
        command.ExecuteNonQuery();
    }

    private static void WriteSymbols(SqliteConnection connection)
    {
        InsertSymbol(connection, "sym-alpha", AlphaPath, "Alpha", "class", "public class Alpha", parentId: null, 1, 0, 1, 44);
        InsertSymbol(connection, "sym-alpha-run", AlphaPath, "Run", "method", "public void Run()", "sym-alpha", 1, 21, 1, 42);
        InsertSymbol(connection, "sym-beta", BetaPath, "Beta", "class", "public class Beta", parentId: null, 1, 0, 1, 60);
    }

    private static void InsertSymbol(
        SqliteConnection connection,
        string symbolId,
        string path,
        string name,
        string kind,
        string signature,
        string? parentId,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO symbols
                (symbol_id, file_id, path, language, name, kind, signature, doc_comment, visibility,
                 parent_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, is_test, test_container, test_lifecycle)
            VALUES ($id, $fid, $path, 'csharp', $name, $kind, $signature, NULL, 'public',
                    $parent, $startLine, $startColumn, $endLine, $endColumn, $startColumn, $endColumn,
                    1.0, 0, 0, 0);
            """;
        command.Parameters.AddWithValue("$id", symbolId);
        command.Parameters.AddWithValue("$fid", FileId(path));
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$signature", signature);
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$startLine", startLine);
        command.Parameters.AddWithValue("$startColumn", startColumn);
        command.Parameters.AddWithValue("$endLine", endLine);
        command.Parameters.AddWithValue("$endColumn", endColumn);
        command.ExecuteNonQuery();
    }

    private static void WriteRelationship(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO relationships
                (relationship_id, reference_site_id, from_symbol_id, to_symbol_id, file_id, path, kind,
                 start_line, start_column, end_line, end_column, start_byte, end_byte, confidence)
            VALUES ('rel-beta-alpha', 'site-beta-alpha', 'sym-beta', 'sym-alpha', $fid, $path, 'calls',
                    1, 38, 1, 43, 38, 43, 1.0);
            """;
        command.Parameters.AddWithValue("$fid", FileId(BetaPath));
        command.Parameters.AddWithValue("$path", BetaPath);
        command.ExecuteNonQuery();
    }

    private static void WriteReferenceSites(SqliteConnection connection)
    {
        InsertReferenceSite(connection, "site-beta-alpha", BetaPath, "sym-beta", 38, 43);
        InsertReferenceSite(connection, "site-beta-run", BetaPath, "sym-beta", 46, 49);
    }

    private static void InsertReferenceSite(
        SqliteConnection connection,
        string referenceSiteId,
        string path,
        string containingSymbolId,
        int startByte,
        int endByte)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_sites
                (reference_site_id, file_id, path, language, containing_symbol_id,
                 start_line, start_column, end_line, end_column, start_byte, end_byte, is_exact, provenance)
            VALUES ($site, $fid, $path, 'csharp', $containing,
                    1, $startByte, 1, $endByte, $startByte, $endByte, 1, 'target_token');
            """;
        command.Parameters.AddWithValue("$site", referenceSiteId);
        command.Parameters.AddWithValue("$fid", FileId(path));
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$containing", containingSymbolId);
        command.Parameters.AddWithValue("$startByte", startByte);
        command.Parameters.AddWithValue("$endByte", endByte);
        command.ExecuteNonQuery();
    }

    private static void WriteComplexityMetric(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO complexity_metrics
                (complexity_metric_id, file_id, path, language, scope, symbol_id, algorithm_id,
                 covered_lines, covered_bytes, decision_count, loop_count, max_nesting_depth, parameter_count,
                 start_line, start_column, end_line, end_column, start_byte, end_byte)
            VALUES ('cx-alpha-run', $fid, $path, 'csharp', 'symbol', 'sym-alpha-run', 'cyclomatic.v1',
                    1, 21, 1, 0, 1, 0,
                    1, 21, 1, 42, 21, 42);
            """;
        command.Parameters.AddWithValue("$fid", FileId(AlphaPath));
        command.Parameters.AddWithValue("$path", AlphaPath);
        command.ExecuteNonQuery();
    }

    private static void WriteTypeFact(SqliteConnection connection) =>
        Exec(
            connection,
            """
            INSERT INTO type_facts
                (type_fact_id, symbol_id, language, resolved_type, generic_params_json, constraints_json,
                 is_inferred, metadata_json)
            VALUES ('type-alpha-run', 'sym-alpha-run', 'csharp', 'void', NULL, NULL, 0, NULL);
            """);

    /// <summary>The layers a symbols-level scan has not extracted yet; only a full-level artifact carries them.</summary>
    private static void WriteReferenceLayer(SqliteConnection connection)
    {
        using (var identifier = connection.CreateCommand())
        {
            identifier.CommandText = """
                INSERT INTO identifiers
                    (identifier_id, reference_site_id, file_id, path, language, name, kind,
                     containing_symbol_id, target_symbol_id,
                     start_line, start_column, end_line, end_column, start_byte, end_byte, confidence)
                VALUES ('ident-beta-alpha', 'site-beta-alpha', $fid, $path, 'csharp', 'Alpha', 'type_usage',
                        'sym-beta', 'sym-alpha', 1, 38, 1, 43, 38, 43, 1.0);
                """;
            identifier.Parameters.AddWithValue("$fid", FileId(BetaPath));
            identifier.Parameters.AddWithValue("$path", BetaPath);
            identifier.ExecuteNonQuery();
        }

        Exec(
            connection,
            """
            INSERT INTO identifier_resolutions
                (identifier_id, target_symbol_id, tier, confidence, method, outcome, candidates,
                 resolved_at_revision)
            VALUES ('ident-beta-alpha', 'sym-alpha', 1, 1.0, 'exact', 'resolved', 1, 1);
            """);

        using (var region = connection.CreateCommand())
        {
            region.CommandText = """
                INSERT INTO source_regions
                    (source_region_id, file_id, path, language, kind, containing_symbol_id,
                     start_line, start_column, end_line, end_column, start_byte, end_byte)
                VALUES ('region-alpha-doc', $fid, $path, 'csharp', 'comment', 'sym-alpha',
                        1, 0, 1, 18, 0, 18);
                """;
            region.Parameters.AddWithValue("$fid", FileId(AlphaPath));
            region.Parameters.AddWithValue("$path", AlphaPath);
            region.ExecuteNonQuery();
        }

        using (var fact = connection.CreateCommand())
        {
            fact.CommandText = """
                INSERT INTO structural_facts
                    (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                     containing_symbol_id, start_line, start_column, end_line, end_column,
                     start_byte, end_byte, confidence)
                VALUES ('fact-alpha-class', $fid, $path, 'csharp', 'csharp.class.v1', 'name', 'class_declaration',
                        'sym-alpha', 1, 0, 1, 44, 0, 44, 1.0);
                """;
            fact.Parameters.AddWithValue("$fid", FileId(AlphaPath));
            fact.Parameters.AddWithValue("$path", AlphaPath);
            fact.ExecuteNonQuery();
        }
    }

    private static void WriteRevision(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO extraction_revisions
                (revision_id, parent_revision_id, operation, mode, started_at, completed_at,
                 binary_version, extract_contract_version, sqlite_schema_version, input_root, counts_json)
            VALUES (1, NULL, 'scan', 'fresh', '1970-01-01T00:00:00Z', '1970-01-01T00:00:00Z',
                    $binary, $contract, $schema, NULL, '{}');
            """;
        command.Parameters.AddWithValue("$binary", MillerExtractContract.PinnedJulieExtractVersion);
        command.Parameters.AddWithValue("$contract", ContractText);
        command.Parameters.AddWithValue("$schema", SchemaText);
        command.ExecuteNonQuery();
    }

    private static void WriteMetadata(SqliteConnection connection, string indexLevel)
    {
        void Meta(string key, string value)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO artifact_metadata (key, value) VALUES ($key, $value);";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        Meta("artifact_id", "artifact-" + indexLevel);
        Meta("root_path", "/work/repo");
        Meta("binary_version", MillerExtractContract.PinnedJulieExtractVersion);
        Meta("parser_inventory_fingerprint", "sha256:" + new string('a', 64));
        Meta("capability_snapshot_fingerprint", "sha256:" + new string('b', 64));
        Meta("created_at", "1970-01-01T00:00:00Z");
        Meta("updated_at", "1970-01-01T00:00:00Z");
        Meta("sqlite_schema_version", SchemaText);
        Meta("schema_version", SchemaText);
        Meta("extract_contract_version", ContractText);
        Meta("hash_algorithm", MillerExtractContract.ExpectedHashAlgorithm);
        Meta("reference_resolution_status", "partial");
        Meta("reference_resolution_version", "6");
        Meta("index_level", indexLevel);
    }

    private static string SchemaText =>
        MillerExtractContract.ExpectedSchemaVersion.ToString(CultureInfo.InvariantCulture);

    private static string ContractText =>
        MillerExtractContract.ExpectedExtractContractVersion.ToString(CultureInfo.InvariantCulture);

    private static string FileId(string path) => "file:" + path;

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
