using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;

namespace Miller.Tests.Indexing;

/// <summary>
/// Synthesizes a tiny SQLite file matching the julie-extractors v1 artifact schema
/// (<c>sqlite_schema_version = 1</c>, <c>extract_contract_version = 1</c>). This is Miller's READ-CONTRACT
/// harness — it is NOT a re-test of julie's extraction (julie owns that). The DDL is transcribed from
/// <c>julie-extractors/crates/julie-extract-artifact/src/schema.rs</c>, so the reader is exercised against the
/// real v1 column set, NULL discipline, and self-FK that a live extract produces.
///
/// <para>Remaining deviations from the strict v1 schema (called out where they apply):
/// <c>files.last_revision_id</c> is a plain column with NO FK to <c>extraction_revisions</c> (the synthetic
/// DB relaxes the FK so a <c>files</c> row can be seeded with no revision). The fixture is fully v1: <c>files</c>
/// is content-free (no <c>content</c> column — body text re-sources from DISK under <see cref="WorkspaceRoot"/>,
/// Phase 5/D2), the freshness cursor reads <c>extraction_revisions</c>/<c>revision_file_changes</c>, the file
/// hash reads <c>files.content_hash</c> (<c>blake3:</c>-prefixed), and <c>hash_algorithm</c> lives only in
/// <c>artifact_metadata</c> (Phase 4).</para>
///
/// Disposable: deletes the temp directory (and -wal/-shm sidecars) on <see cref="Dispose"/>.
/// </summary>
internal sealed class JulieDbFixture : IDisposable
{
    private readonly string _dir;

    /// <summary>
    /// The schema / extract_contract version this Miller build is pinned to, sourced from
    /// <see cref="MillerExtractContract"/>. Fixtures that just need a *valid* artifact pass these (NOT
    /// literals) so a julie re-pin needs no per-test edits — only the one constants file changes. In v1 the
    /// version is an <c>artifact_metadata</c> KEY, not a separate table row.
    /// </summary>
    public static readonly long PinnedSchema = MillerExtractContract.ExpectedSchemaVersion;

    /// <summary>The pinned contract version as the TEXT julie stores in artifact_metadata.</summary>
    public static readonly string PinnedContract =
        MillerExtractContract.ExpectedExtractContractVersion.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Pin-relative schema version as a string for "names the value" assertions: delta 0 == the pin,
    /// +1 == a future (newer) schema, -1 == an older one.
    /// </summary>
    public static string SchemaText(long delta = 0) =>
        (PinnedSchema + delta).ToString(CultureInfo.InvariantCulture);

    /// <summary>Absolute path to the synthesized julie extract <c>.db</c> file.</summary>
    public string DbPath { get; }

    /// <summary>Absolute path to the directory containing the DB (the WAL sidecars live here).</summary>
    public string Directory => _dir;

    /// <summary>
    /// The fixture-owned workspace root: every fixture file's exact UTF-8 bytes are materialized under this
    /// directory (parent dirs created) so the D2 disk-slice path (<see cref="ExtractReader.ReadBody"/>) can
    /// re-source body text from disk and verify it against the stored <c>files.content_hash</c>. The on-disk
    /// bytes are the SAME bytes the stored hash was computed from, so a fresh read matches by construction.
    /// </summary>
    public string WorkspaceRoot => _dir;

    /// <summary>
    /// The known rows inserted by <see cref="CreateDefault"/>, in INSERT order. Tests assert the reader's
    /// output against the subset/ordering these imply (the reader's SELECT re-orders by path,start_line,symbol_id).
    /// </summary>
    public IReadOnlyList<SymbolRow> Rows { get; }

    private JulieDbFixture(string dir, string dbPath, IReadOnlyList<SymbolRow> rows)
    {
        _dir = dir;
        DbPath = dbPath;
        Rows = rows;
    }

    public static byte[] Utf16LeBomBytes(string text)
    {
        byte[] encoded = System.Text.Encoding.Unicode.GetBytes(text);
        byte[] bytes = new byte[encoded.Length + 2];
        bytes[0] = 0xFF;
        bytes[1] = 0xFE;
        encoded.CopyTo(bytes, 2);
        return bytes;
    }

    public void ReplaceFileBytesAndRefreshHash(string relPath, byte[] bytes)
    {
        string abs = Path.Combine(WorkspaceRoot, relPath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllBytes(abs, bytes);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE files
            SET content_hash = $hash, content_bytes = $bytes
            WHERE path = $path;
            """;
        command.Parameters.AddWithValue("$hash", "blake3:" + ContentHasher.Blake3Hex(bytes));
        command.Parameters.AddWithValue("$bytes", bytes.Length);
        command.Parameters.AddWithValue("$path", relPath);
        int updated = command.ExecuteNonQuery();
        if (updated != 1)
            throw new InvalidOperationException($"Expected one files row for '{relPath}', updated {updated}.");
    }

    /// <summary>
    /// Run mutation SQL against the fixture DB over a fresh ReadWrite (Pooling=false) connection — the
    /// shared escape hatch for tests that reshape the synthesized artifact (status flips, extra rows,
    /// dropped tables/columns) beyond what the builders model.
    /// </summary>
    public void ExecuteWrite(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void SetArtifactMetadata(string key, string value) =>
        ExecuteWrite("""
            INSERT INTO artifact_metadata (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """, parameters =>
        {
            parameters.AddWithValue("$key", key);
            parameters.AddWithValue("$value", value);
        });

    /// <summary>
    /// The five-symbol test-role/currency evidence scenario proven identically by the artifact reader
    /// (<c>SqliteSymbolReaderTests</c>) and the symbols export feed (<c>SymbolExportReaderTests</c>): one
    /// symbol per currency rule — current, failed_preserved file status, parse diagnostics, both combined,
    /// and a deleted files row. One builder so the two surfaces can never silently diverge on the scenario.
    /// Symbol ids are <c><paramref name="idPrefix"/>-current</c> … <c><paramref name="idPrefix"/>-unavailable</c>.
    /// </summary>
    public static JulieDbFixture CreateTestRoleEvidenceScenario(string idPrefix)
    {
        var fx = Create(PinnedSchema, PinnedContract, new[]
        {
            new SymbolRow($"{idPrefix}-current", "Current", "method", "csharp",
                "a-current.cs", "void Current()", 1, null) { IsTest = true, TestContainer = true },
            new SymbolRow($"{idPrefix}-file-status", "FileStatus", "method", "csharp",
                "b-file-status.cs", "void FileStatus()", 1, null) { IsTest = true, TestLifecycle = true },
            new SymbolRow($"{idPrefix}-diagnostic", "Diagnostic", "method", "csharp",
                "c-diagnostic.cs", "void Diagnostic()", 1, null) { TestContainer = true },
            new SymbolRow($"{idPrefix}-combined", "Combined", "method", "csharp",
                "d-combined.cs", "void Combined()", 1, null)
                { IsTest = true, TestContainer = true, TestLifecycle = true },
            new SymbolRow($"{idPrefix}-unavailable", "Unavailable", "method", "csharp",
                "e-unavailable.cs", "void Unavailable()", 1, null) { IsTest = true },
        });
        fx.ExecuteWrite($"""
            UPDATE files
            SET status = 'failed_preserved'
            WHERE path IN ('b-file-status.cs', 'd-combined.cs');

            INSERT INTO parse_diagnostics
                (diagnostic_id, file_id, path, language, kind, message, start_line, start_column,
                 end_line, end_column, start_byte, end_byte, metadata_json)
            VALUES
                ('diag-{idPrefix}-1', 'file:c-diagnostic.cs', 'c-diagnostic.cs', 'csharp', 'parse_error',
                 'diagnostic-only', 1, 1, 1, 1, 0, 1, NULL),
                ('diag-{idPrefix}-2', 'file:d-combined.cs', 'd-combined.cs', 'csharp', 'parse_error',
                 'combined', 1, 1, 1, 1, 0, 1, NULL);

            DELETE FROM files WHERE path = 'e-unavailable.cs';
            """);
        return fx;
    }

    // ---- v4 reference-resolution + suppression-evidence builders --------------------------------------------
    // These mutate the already-created DB over a fresh ReadWrite (Pooling=false) connection — the same pattern as
    // ReplaceFileBytesAndRefreshHash. FK enforcement is OFF on the new connection (SQLite per-connection default),
    // so a row may reference a symbol/file the fixture did not seed; the identifier_resolutions CHECK is still
    // enforced (a 'resolved' outcome REQUIRES a non-null target). Consumed by the dead-code reader tests and Task 3.

    private void ExecuteWrite(string sql, Action<SqliteParameterCollection> bind)
    {
        // ForeignKeys=false mirrors Create's `PRAGMA foreign_keys=OFF` so a builder may reference a symbol/file
        // the fixture did not seed; the identifier_resolutions CHECK is enforced regardless of the FK pragma.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command.Parameters);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Insert an <c>identifier_resolutions</c> row: the resolved/ambiguous outcome for one identifier reference.
    /// A <c>resolved</c> outcome REQUIRES a non-null <paramref name="targetSymbolId"/> (the CHECK enforces it).
    /// </summary>
    public void AddIdentifierResolution(
        string identifierId, string? targetSymbolId, string outcome = "resolved",
        int tier = 1, double confidence = 1.0, string method = "exact", int candidates = 1,
        long resolvedAtRevision = 1)
    {
        ExecuteWrite("""
            INSERT INTO identifier_resolutions
                (identifier_id, target_symbol_id, tier, confidence, method, outcome, candidates, resolved_at_revision)
            VALUES ($id, $target, $tier, $conf, $method, $outcome, $cands, $rev);
            """, p =>
        {
            p.AddWithValue("$id", identifierId);
            p.AddWithValue("$target", (object?)targetSymbolId ?? DBNull.Value);
            p.AddWithValue("$tier", tier);
            p.AddWithValue("$conf", confidence);
            p.AddWithValue("$method", method);
            p.AddWithValue("$outcome", outcome);
            p.AddWithValue("$cands", candidates);
            p.AddWithValue("$rev", resolvedAtRevision);
        });
    }

    /// <summary>
    /// Insert a <c>pending_relationships</c> row (a deferred call/reference awaiting resolution). <c>file_id</c>
    /// is derived from <paramref name="filePath"/> via the shared helper; <paramref name="startByte"/>/
    /// <paramref name="endByte"/> are the (nullable) origin span the reader's inside-S test consults.
    /// </summary>
    public void AddPendingRelationship(
        string pendingRelationshipId, string fromSymbolId, string filePath,
        string? callerScopeSymbolId = null, int? startByte = null, int? endByte = null,
        string kind = "call", string targetDisplayName = "Target", string targetTerminalName = "Target",
        int startLine = 1, double confidence = 1.0)
    {
        ExecuteWrite("""
            INSERT INTO pending_relationships
                (pending_relationship_id, from_symbol_id, caller_scope_symbol_id, file_id, path, kind,
                 target_display_name, target_terminal_name, target_receiver, target_namespace_json,
                 target_import_context, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES ($id, $from, $caller, $fid, $path, $kind, $display, $terminal, NULL, '[]', NULL,
                    $sl, NULL, NULL, NULL, $sb, $eb, $conf, NULL);
            """, p =>
        {
            p.AddWithValue("$id", pendingRelationshipId);
            p.AddWithValue("$from", fromSymbolId);
            p.AddWithValue("$caller", (object?)callerScopeSymbolId ?? DBNull.Value);
            p.AddWithValue("$fid", FileId(filePath));
            p.AddWithValue("$path", filePath);
            p.AddWithValue("$kind", kind);
            p.AddWithValue("$display", targetDisplayName);
            p.AddWithValue("$terminal", targetTerminalName);
            p.AddWithValue("$sl", startLine);
            p.AddWithValue("$sb", (object?)startByte ?? DBNull.Value);
            p.AddWithValue("$eb", (object?)endByte ?? DBNull.Value);
            p.AddWithValue("$conf", confidence);
        });
    }

    /// <summary>
    /// Insert a <c>pending_resolutions</c> row: the resolved target for a pending relationship. Independent of
    /// <c>identifier_resolutions</c> — a pending relationship can resolve with NO identifier_resolutions row.
    /// </summary>
    public void AddPendingResolution(
        string pendingRelationshipId, string targetSymbolId,
        int tier = 1, double confidence = 1.0, string method = "exact", long resolvedAtRevision = 1)
    {
        ExecuteWrite("""
            INSERT INTO pending_resolutions
                (pending_relationship_id, target_symbol_id, tier, confidence, method, resolved_at_revision)
            VALUES ($id, $target, $tier, $conf, $method, $rev);
            """, p =>
        {
            p.AddWithValue("$id", pendingRelationshipId);
            p.AddWithValue("$target", targetSymbolId);
            p.AddWithValue("$tier", tier);
            p.AddWithValue("$conf", confidence);
            p.AddWithValue("$method", method);
            p.AddWithValue("$rev", resolvedAtRevision);
        });
    }

    /// <summary>
    /// Insert a <c>structural_facts</c> row bound to <paramref name="containingSymbolId"/> (the framework-bound
    /// source the reader treats as a suppression signal on the symbol OR any ancestor).
    /// </summary>
    public void AddStructuralFact(
        string structuralFactId, string? containingSymbolId, string path,
        string language = "csharp", string patternId = "custom.pattern.v1",
        string captureName = "attribute", string nodeKind = "attribute")
    {
        ExecuteWrite("""
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES ($id, $fid, $path, $lang, $pattern, $capture, $node, $symbol,
                    1, 1, 1, 2, 0, 1, 1.0, NULL);
            """, p =>
        {
            p.AddWithValue("$id", structuralFactId);
            p.AddWithValue("$fid", FileId(path));
            p.AddWithValue("$path", path);
            p.AddWithValue("$lang", language);
            p.AddWithValue("$pattern", patternId);
            p.AddWithValue("$capture", captureName);
            p.AddWithValue("$node", nodeKind);
            p.AddWithValue("$symbol", (object?)containingSymbolId ?? DBNull.Value);
        });
    }

    /// <summary>
    /// Insert a <c>symbol_annotations</c> row for <paramref name="symbolId"/> (the SELF-only annotated signal the
    /// reader reads via <c>SELECT DISTINCT symbol_id FROM symbol_annotations</c>).
    /// </summary>
    public void AddSymbolAnnotation(
        string annotationId, string symbolId, string annotation = "Obsolete", string annotationKey = "obsolete")
    {
        ExecuteWrite("""
            INSERT INTO symbol_annotations (annotation_id, symbol_id, annotation, annotation_key, raw_text, carrier)
            VALUES ($id, $sid, $ann, $key, NULL, NULL);
            """, p =>
        {
            p.AddWithValue("$id", annotationId);
            p.AddWithValue("$sid", symbolId);
            p.AddWithValue("$ann", annotation);
            p.AddWithValue("$key", annotationKey);
        });
    }

    /// <summary>
    /// A row as written into the synthesized v1 <c>symbols</c> table. The first eight fields are the M1 read
    /// projection; the remaining detail/body columns (M2 <c>ReadDetail</c>/<c>ReadBody</c>) and the typed test
    /// columns are optional init-properties that default to NULL/false, so every existing positional
    /// construction stays valid.
    /// </summary>
    internal sealed record SymbolRow(
        string Id,
        string Name,
        string Kind,
        string Language,
        string FilePath,
        string? Signature,
        int? StartLine,
        string? ParentId)
    {
        public string? DocComment { get; init; }
        public string? Visibility { get; init; }

        /// <summary>
        /// The symbol's WHOLE-span end line (julie's <c>end_line</c>, 1-based). NULL by default so M1/M2 rows
        /// are unaffected. M5's D7 reads this so the diff→symbol mapping can intersect <c>[start_line, end_line]</c>
        /// against a changed range; a NULL here reads as 0 (the same nullable-INTEGER discipline as start_line).
        /// </summary>
        public int? EndLine { get; init; }

        /// <summary>
        /// The symbol's WHOLE-span start/end byte offsets (julie's <c>start_byte</c>/<c>end_byte</c>). NULL by
        /// default so M1/M2 rows are unaffected. M6's <c>ReadEditSpan</c> reads these for signature/insert ops:
        /// signature span = <c>[start_byte, body_start_byte)</c>, insert_after at <c>end_byte</c>.
        /// </summary>
        public int? StartByte { get; init; }
        public int? EndByte { get; init; }

        public int? BodyStartByte { get; init; }
        public int? BodyEndByte { get; init; }
        public int? BodyStartLine { get; init; }
        public int? BodyEndLine { get; init; }

        /// <summary>
        /// Raw <c>symbols.metadata_json</c> (julie's per-language extractor output). NULL by default. Kept
        /// seedable for any consumer asserting on the JSON mirror; in v1 the test signal is the typed
        /// <see cref="IsTest"/>/<see cref="TestContainer"/>/<see cref="TestLifecycle"/> columns, NOT this JSON.
        /// </summary>
        public string? Metadata { get; init; }

        /// <summary>v1 typed <c>symbols.is_test</c> column (INTEGER NOT NULL DEFAULT 0). The cross-language signal.</summary>
        public bool IsTest { get; init; }

        /// <summary>v1 typed <c>symbols.test_container</c> column (INTEGER NOT NULL DEFAULT 0).</summary>
        public bool TestContainer { get; init; }

        /// <summary>v1 typed <c>symbols.test_lifecycle</c> column (INTEGER NOT NULL DEFAULT 0).</summary>
        public bool TestLifecycle { get; init; }
    }

    /// <summary>
    /// An extra <c>files</c>-manifest row with full control over status/language/on-disk bytes — for the
    /// content-search loader tests (docs-scope, freshness, size-cap, status, non-UTF-8). Independent of the
    /// symbol-derived files the fixture also writes. <see cref="DiskText"/>/<see cref="DiskBytes"/> are
    /// materialized under <see cref="WorkspaceRoot"/> (null => file NOT written, the missing-file case); the
    /// stored <c>content_hash</c> is BLAKE3 of those bytes unless <see cref="StaleHash"/> forces a mismatch;
    /// <see cref="ContentBytesOverride"/> sets <c>content_bytes</c> without writing a large file (size-cap case).
    /// </summary>
    internal sealed record FileSpec(string Path)
    {
        public string Language { get; init; } = "csharp";
        public string Status { get; init; } = "indexed";
        public string? DiskText { get; init; }
        public byte[]? DiskBytes { get; init; }
        public bool StaleHash { get; init; }
        public long? ContentBytesOverride { get; init; }
    }

    /// <summary>A row as written into the synthesized <c>identifiers</c> table.</summary>
    internal sealed record IdentifierRow(
        string Id,
        string Name,
        string Kind,             // 'call' | 'variable_ref' | 'type_usage' | 'member_access'
        string Language,
        string FilePath,
        int StartLine,
        string? ContainingSymbolId)
    {
        /// <summary>
        /// The exact per-occurrence byte token span (julie's <c>identifiers.start_byte</c>/<c>end_byte</c>),
        /// e.g. a 5-char <c>Total</c> call at <c>start_byte=120, end_byte=125</c>. NULL by default so the M2
        /// reference rows are unaffected; M6's <c>ReadIdentifierSites</c> reads these for exact-span rename.
        /// </summary>
        public int? StartByte { get; init; }
        public int? EndByte { get; init; }
        public string? TargetSymbolId { get; init; }
    }

    /// <summary>
    /// A row as written into the synthesized v1 <c>relationships</c> table (M5 D2 precise edge source).
    /// <see cref="FromSymbolId"/> → <see cref="ToSymbolId"/> are BOTH resolved symbol ids
    /// (julie's <c>from_symbol_id</c>/<c>to_symbol_id</c>, NOT NULL); <see cref="Kind"/> is the edge label
    /// (<c>calls</c>/<c>uses</c>/...). Sparse: only the directly-extracted edges (the analyze pass does not run
    /// under <c>extract scan</c>).
    /// </summary>
    internal sealed record RelationshipRow(
        string Id,
        string FromSymbolId,
        string ToSymbolId,
        string Kind)
    {
        public string FilePath { get; init; } = string.Empty;
        public int? StartLine { get; init; }
        public int? StartColumn { get; init; }
        public int? EndLine { get; init; }
        public int? EndColumn { get; init; }
        public int? StartByte { get; init; }
        public int? EndByte { get; init; }
        public double Confidence { get; init; } = 1.0;
    }

    /// <summary>
    /// A row as written into the synthesized v2 <c>source_regions</c> table. It carries the full julie column
    /// set; region text is not stored in the artifact and must be sliced from file bytes by consumers.
    /// </summary>
    internal sealed record SourceRegionRow(
        string SourceRegionId,
        string FileId,
        string Path,
        string Language,
        string Kind,
        string? ContainingSymbolId,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn,
        int StartByte,
        int EndByte,
        string? MetadataJson);

    /// <summary>
    /// A row as written into the v1 <c>extraction_revisions</c> table (the freshness cursor; schema.rs:28-41).
    /// <see cref="Revision"/> maps to the <c>revision_id</c> PK the FreshnessReader takes MAX of (one DB = one
    /// root, so there is no <c>workspace_id</c>); <see cref="Kind"/> maps to the <c>mode</c> column and defaults
    /// to <c>full</c> (a scan-produced revision is a full extraction). The other NOT-NULL columns are supplied by
    /// the INSERT from <see cref="MillerExtractContract"/> so the synthetic row is a faithful v1 shape.
    /// </summary>
    internal sealed record RevisionRow(long Revision, string Kind = "full")
    {
        public long CreatedAt { get; init; }
    }

    /// <summary>
    /// A row as written into the v1 <c>revision_file_changes</c> table (the changed-file delta; schema.rs:43-50).
    /// v1 has no <c>workspace_id</c> and no CHECK on <c>change_kind</c>. <see cref="Path"/> maps to the v1
    /// <c>path</c> column; <see cref="ChangeKind"/> is one of <c>inserted|updated|deleted|unsupported</c>. The
    /// NOT-NULL <c>file_id</c> PK component is DERIVED (via the shared <see cref="FileId"/> helper), not a field.
    /// </summary>
    internal sealed record RevisionFileChangeRow(long Revision, string Path, string ChangeKind);

    /// <summary>
    /// Build a fixture with the given schema/contract version values and the supplied symbol rows.
    /// In v1 the versions are <c>artifact_metadata</c> KEYS, not a separate table: <paramref name="schemaVersion"/>
    /// is written to the <c>sqlite_schema_version</c> (and mirrored <c>schema_version</c>) key,
    /// <paramref name="contractValue"/> to <c>extract_contract_version</c>, and <paramref name="hashAlgorithm"/>
    /// to <c>hash_algorithm</c> — all as TEXT. Passing <c>null</c> for schema/contract/hash omits that key;
    /// passing <c>createMetadataTable: false</c> omits the whole <c>artifact_metadata</c> table (a non-julie DB).
    /// <paramref name="createSchemaVersionTable"/> is retained for call-site API stability but no longer creates
    /// a separate table (v1 has none); it gates only whether the version keys are written.
    /// </summary>
    public static JulieDbFixture Create(
        long? schemaVersion,
        string? contractValue,
        IReadOnlyList<SymbolRow> rows,
        bool createSchemaVersionTable = true,
        bool createMetadataTable = true,
        IReadOnlyList<IdentifierRow>? identifiers = null,
        IReadOnlyDictionary<string, string>? fileContent = null,
        string? workspaceId = null,
        IReadOnlyList<RevisionRow>? revisions = null,
        IReadOnlyList<RevisionFileChangeRow>? fileChanges = null,
        IReadOnlyList<RelationshipRow>? relationships = null,
        string? hashAlgorithm = MillerExtractContract.ExpectedHashAlgorithm,
        IReadOnlyList<FileSpec>? extraFiles = null,
        IReadOnlyList<SourceRegionRow>? sourceRegions = null,
        string? referenceResolutionStatus = "partial",
        string? referenceResolutionVersion = "1")
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-julie-fixture-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "symbols.db");

        // Pooling=false on the write connection: it is disposed at the end of this using block, releasing the
        // file handle immediately WITHOUT a process-global SqliteConnection.ClearAllPools() (which races a
        // concurrently running test's live connection — xUnit parallelizes collections).
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        };
        using (var conn = new SqliteConnection(csb.ToString()))
        {
            conn.Open();
            // Match julie: WAL. FK enforcement is left OFF here so the FK-free files.last_revision_id and the
            // synthetic position-nullable columns can be seeded freely; the real artifact enforces FKs, but
            // Miller only READS.
            Exec(conn, "PRAGMA journal_mode=WAL;");
            Exec(conn, "PRAGMA foreign_keys=OFF;");
            // Throwaway test DB: skip per-statement fsync and batch the whole build into one commit so a
            // fixture is tens of ms, not one WAL-frame flush per DDL/INSERT. Raw BEGIN/COMMIT (not a
            // SqliteTransaction) keeps every CreateCommand call site below untouched.
            Exec(conn, "PRAGMA synchronous=OFF;");
            Exec(conn, "BEGIN;");

            Exec(conn, FilesDdl);
            Exec(conn, SymbolsDdl);
            Exec(conn, IdentifiersDdl);
            // The relationships table is always created so a SymbolGraphReader can open against any fixture.
            Exec(conn, RelationshipsDdl);
            Exec(conn, SourceRegionsDdl);
            Exec(conn, SourceRegionsIndexesDdl);
            Exec(conn, StructuralFactsDdl);
            Exec(conn, PatternCatalogDdl);
            Exec(conn, ComplexityMetricsDdl);
            // The v1 freshness tables (extraction_revisions/revision_file_changes) so the FreshnessReader can open.
            Exec(conn, ExtractionRevisionsDdl);
            Exec(conn, RevisionFileChangesDdl);
            // The M4 bridge tables are always created (empty by default) so the SqliteBridgeReader — on the single
            // production RepositoryIndexLoader.Load path (D9) — can open against ANY fixture.
            Exec(conn, TypeArgumentUsagesDdl);
            Exec(conn, TypeArgumentsDdl);
            Exec(conn, LiteralsDdl);
            Exec(conn, SymbolAnnotationsDdl);
            // Schema-versioned tables (created empty) so the synthetic artifact is a faithful current shape.
            Exec(conn, ParserInventoryDdl);
            Exec(conn, ParseDiagnosticsDdl);
            Exec(conn, LanguageCapabilitiesDdl);
            Exec(conn, LanguageCapabilityFixturesDdl);
            Exec(conn, LanguageCapabilityGapsDdl);
            Exec(conn, PendingRelationshipsDdl);
            Exec(conn, PendingRelationshipsIndexesDdl);
            // v4 reference-resolution overlay tables + their pinned indexes (created empty; seeded by the
            // AddIdentifierResolution / AddPendingResolution builders when a test needs resolution evidence).
            Exec(conn, IdentifierResolutionsDdl);
            Exec(conn, IdentifierResolutionsIndexDdl);
            Exec(conn, PendingResolutionsDdl);
            Exec(conn, PendingResolutionsIndexDdl);
            Exec(conn, TypeFactsDdl);
            if (createMetadataTable) Exec(conn, MetadataDdl);

            // files rows (v1: file_id PK, path UNIQUE, content_hash/content_bytes — content-free, no `content`).
            // identifiers also carry path, so union both sources of paths. The exact bytes hashed into
            // content_hash are ALSO materialized to disk under _dir (the WorkspaceRoot), so the D2 disk-slice
            // path reads a file whose blake3 matches the stored content_hash by construction (reconciliation #3).
            foreach (var path in DistinctPaths(rows, identifiers, sourceRegions))
            {
                string content = fileContent is not null && fileContent.TryGetValue(path, out var c) ? c : "";
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
                string contentHash = "blake3:" + ContentHasher.Blake3Hex(bytes); // v1 content_hash: domain-prefixed

                // Materialize the EXACT bytes to disk (parent dirs first) so disk-hash == stored content_hash.
                string abs = Path.Combine(dir, path);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                File.WriteAllBytes(abs, bytes);

                using var fcmd = conn.CreateCommand();
                fcmd.CommandText =
                    "INSERT INTO files (file_id, path, language, content_hash, content_bytes, line_count, " +
                    "indexed_at, last_revision_id, status, metadata_json) " +
                    "VALUES ($fid, $p, 'csharp', $chash, $bytes, 0, '1970-01-01T00:00:00Z', 0, 'indexed', NULL);";
                fcmd.Parameters.AddWithValue("$fid", FileId(path));
                fcmd.Parameters.AddWithValue("$p", path);
                fcmd.Parameters.AddWithValue("$chash", contentHash);
                fcmd.Parameters.AddWithValue("$bytes", bytes.Length);
                fcmd.ExecuteNonQuery();
            }

            // extra files-manifest rows for content-search loader tests: full control over status/language/
            // on-disk bytes/hash/size, independent of the symbol-derived files above. Paths must not collide.
            if (extraFiles is not null)
            {
                foreach (var f in extraFiles)
                {
                    byte[]? disk = f.DiskBytes
                        ?? (f.DiskText is null ? null : System.Text.Encoding.UTF8.GetBytes(f.DiskText));
                    if (disk is not null)
                    {
                        string abs = Path.Combine(dir, f.Path);
                        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                        File.WriteAllBytes(abs, disk);
                    }

                    // Stored content_hash matches the on-disk bytes unless StaleHash forces a drift.
                    byte[] hashedBytes = f.StaleHash
                        ? System.Text.Encoding.UTF8.GetBytes("STALE-" + f.Path)
                        : (disk ?? Array.Empty<byte>());
                    string contentHash = "blake3:" + ContentHasher.Blake3Hex(hashedBytes);
                    long contentBytes = f.ContentBytesOverride ?? (disk?.Length ?? 0);

                    using var fcmd = conn.CreateCommand();
                    fcmd.CommandText =
                        "INSERT INTO files (file_id, path, language, content_hash, content_bytes, line_count, " +
                        "indexed_at, last_revision_id, status, metadata_json) " +
                        "VALUES ($fid, $p, $lang, $chash, $bytes, 0, '1970-01-01T00:00:00Z', 0, $status, NULL);";
                    fcmd.Parameters.AddWithValue("$fid", FileId(f.Path));
                    fcmd.Parameters.AddWithValue("$p", f.Path);
                    fcmd.Parameters.AddWithValue("$lang", f.Language);
                    fcmd.Parameters.AddWithValue("$chash", contentHash);
                    fcmd.Parameters.AddWithValue("$bytes", contentBytes);
                    fcmd.Parameters.AddWithValue("$status", f.Status);
                    fcmd.ExecuteNonQuery();
                }
            }

            // symbols rows — parents first so self-FK parent_symbol_id resolves. The detail/body/typed-test
            // columns are written from the row's optional init-props (NULL/0 by default — M1 behavior preserved).
            foreach (var r in OrderParentsFirst(rows))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO symbols (symbol_id, file_id, path, language, name, kind, signature, " +
                    "start_line, start_column, end_line, end_column, start_byte, end_byte, parent_symbol_id, " +
                    "doc_comment, visibility, " +
                    "body_start_byte, body_end_byte, body_start_line, body_end_line, " +
                    "is_test, test_container, test_lifecycle, metadata_json) " +
                    "VALUES ($id, $fid, $fp, $lang, $name, $kind, $sig, " +
                    "$sl, 0, $el, 0, $sb, $eb, $pid, " +
                    "$doc, $vis, $bsb, $beb, $bsl, $bel, " +
                    "$istest, $tcont, $tlife, $meta);";
                cmd.Parameters.AddWithValue("$id", r.Id);
                cmd.Parameters.AddWithValue("$fid", FileId(r.FilePath));
                cmd.Parameters.AddWithValue("$fp", r.FilePath);
                cmd.Parameters.AddWithValue("$lang", r.Language);
                cmd.Parameters.AddWithValue("$name", r.Name);
                cmd.Parameters.AddWithValue("$kind", r.Kind);
                cmd.Parameters.AddWithValue("$sig", (object?)r.Signature ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sl", (object?)r.StartLine ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$el", (object?)r.EndLine ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sb", (object?)r.StartByte ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$eb", (object?)r.EndByte ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pid", (object?)r.ParentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$doc", (object?)r.DocComment ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$vis", (object?)r.Visibility ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bsb", (object?)r.BodyStartByte ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$beb", (object?)r.BodyEndByte ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bsl", (object?)r.BodyStartLine ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bel", (object?)r.BodyEndLine ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$istest", r.IsTest ? 1 : 0);
                cmd.Parameters.AddWithValue("$tcont", r.TestContainer ? 1 : 0);
                cmd.Parameters.AddWithValue("$tlife", r.TestLifecycle ? 1 : 0);
                cmd.Parameters.AddWithValue("$meta", (object?)r.Metadata ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            if (identifiers is not null)
            {
                foreach (var ident in identifiers)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "INSERT INTO identifiers (identifier_id, file_id, path, language, name, kind, " +
                        "start_line, start_column, end_line, end_column, start_byte, end_byte, confidence, " +
                        "containing_symbol_id, target_symbol_id) " +
                        "VALUES ($id, $fid, $fp, $lang, $name, $kind, $sl, 0, $sl, 0, $sb, $eb, 1.0, $cid, $target);";
                    cmd.Parameters.AddWithValue("$id", ident.Id);
                    cmd.Parameters.AddWithValue("$fid", FileId(ident.FilePath));
                    cmd.Parameters.AddWithValue("$fp", ident.FilePath);
                    cmd.Parameters.AddWithValue("$lang", ident.Language);
                    cmd.Parameters.AddWithValue("$name", ident.Name);
                    cmd.Parameters.AddWithValue("$kind", ident.Kind);
                    cmd.Parameters.AddWithValue("$sl", ident.StartLine);
                    cmd.Parameters.AddWithValue("$sb", (object?)ident.StartByte ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$eb", (object?)ident.EndByte ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$cid", (object?)ident.ContainingSymbolId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$target", (object?)ident.TargetSymbolId ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }

            if (relationships is not null)
            {
                foreach (var rel in relationships)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "INSERT INTO relationships (relationship_id, from_symbol_id, to_symbol_id, file_id, path, " +
                        "kind, start_line, start_column, end_line, end_column, start_byte, end_byte, confidence) " +
                        "VALUES ($id, $from, $to, $fid, $path, $kind, $sl, $sc, $el, $ec, $sb, $eb, $confidence);";
                    cmd.Parameters.AddWithValue("$id", rel.Id);
                    cmd.Parameters.AddWithValue("$from", rel.FromSymbolId);
                    cmd.Parameters.AddWithValue("$to", rel.ToSymbolId);
                    cmd.Parameters.AddWithValue("$fid", string.IsNullOrEmpty(rel.FilePath) ? string.Empty : FileId(rel.FilePath));
                    cmd.Parameters.AddWithValue("$path", rel.FilePath);
                    cmd.Parameters.AddWithValue("$kind", rel.Kind);
                    cmd.Parameters.AddWithValue("$sl", (object?)rel.StartLine ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$sc", (object?)rel.StartColumn ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$el", (object?)rel.EndLine ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$ec", (object?)rel.EndColumn ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$sb", (object?)rel.StartByte ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$eb", (object?)rel.EndByte ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$confidence", rel.Confidence);
                    cmd.ExecuteNonQuery();
                }
            }

            if (sourceRegions is not null)
            {
                foreach (var region in sourceRegions)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "INSERT INTO source_regions (source_region_id, file_id, path, language, kind, " +
                        "containing_symbol_id, start_line, start_column, end_line, end_column, " +
                        "start_byte, end_byte, metadata_json) " +
                        "VALUES ($id, $fid, $path, $lang, $kind, $symbol, $sl, $sc, $el, $ec, $sb, $eb, $meta);";
                    cmd.Parameters.AddWithValue("$id", region.SourceRegionId);
                    cmd.Parameters.AddWithValue("$fid", region.FileId);
                    cmd.Parameters.AddWithValue("$path", region.Path);
                    cmd.Parameters.AddWithValue("$lang", region.Language);
                    cmd.Parameters.AddWithValue("$kind", region.Kind);
                    cmd.Parameters.AddWithValue("$symbol", (object?)region.ContainingSymbolId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$sl", region.StartLine);
                    cmd.Parameters.AddWithValue("$sc", region.StartColumn);
                    cmd.Parameters.AddWithValue("$el", region.EndLine);
                    cmd.Parameters.AddWithValue("$ec", region.EndColumn);
                    cmd.Parameters.AddWithValue("$sb", region.StartByte);
                    cmd.Parameters.AddWithValue("$eb", region.EndByte);
                    cmd.Parameters.AddWithValue("$meta", (object?)region.MetadataJson ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }

            // extraction_revisions rows (v1 freshness cursor). revision_id is an explicit (non-autoincrement) PK;
            // the NOT-NULL columns v1 requires are supplied from the pinned contract constants for fidelity.
            if (revisions is not null)
            {
                foreach (var rev in revisions)
                {
                    string ts = rev.CreatedAt == 0
                        ? "1970-01-01T00:00:00Z"
                        : rev.CreatedAt.ToString(CultureInfo.InvariantCulture);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "INSERT INTO extraction_revisions " +
                        "(revision_id, parent_revision_id, operation, mode, started_at, completed_at, " +
                        "binary_version, extract_contract_version, sqlite_schema_version, input_root, counts_json) " +
                        "VALUES ($rev, NULL, 'scan', $mode, $started, $completed, $bin, $contract, $schema, NULL, '{}');";
                    cmd.Parameters.AddWithValue("$rev", rev.Revision);
                    cmd.Parameters.AddWithValue("$mode", rev.Kind);
                    cmd.Parameters.AddWithValue("$started", ts);
                    cmd.Parameters.AddWithValue("$completed", ts);
                    cmd.Parameters.AddWithValue("$bin", MillerExtractContract.PinnedJulieExtractVersion);
                    cmd.Parameters.AddWithValue(
                        "$contract",
                        MillerExtractContract.ExpectedExtractContractVersion.ToString(CultureInfo.InvariantCulture));
                    cmd.Parameters.AddWithValue(
                        "$schema",
                        MillerExtractContract.ExpectedSchemaVersion.ToString(CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                }
            }

            // revision_file_changes rows (v1 changed-file delta). file_id is DERIVED from the path (the shared
            // FileId helper); v1 has no workspace_id / old_hash / new_hash and no CHECK on change_kind.
            if (fileChanges is not null)
            {
                foreach (var fc in fileChanges)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "INSERT INTO revision_file_changes (revision_id, file_id, path, change_kind) " +
                        "VALUES ($rev, $fid, $p, $ck);";
                    cmd.Parameters.AddWithValue("$rev", fc.Revision);
                    cmd.Parameters.AddWithValue("$fid", FileId(fc.Path));
                    cmd.Parameters.AddWithValue("$p", fc.Path);
                    cmd.Parameters.AddWithValue("$ck", fc.ChangeKind);
                    cmd.ExecuteNonQuery();
                }
            }

            // v1 artifact_metadata: the full REQUIRED_METADATA_KEYS set so the synthetic fixture is a faithful
            // v1 artifact (metadata.rs). The gate reads sqlite_schema_version / schema_version /
            // extract_contract_version / hash_algorithm; the rest keep the fixture honest. Fingerprints carry the
            // sha256: domain prefix (NEVER blake3:) — Miller stores but never compares them (hash-domain split).
            if (createMetadataTable)
            {
                void Meta(string key, string value)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT INTO artifact_metadata (key, value) VALUES ($k, $v);";
                    cmd.Parameters.AddWithValue("$k", key);
                    cmd.Parameters.AddWithValue("$v", value);
                    cmd.ExecuteNonQuery();
                }

                Meta("artifact_id", "artifact-" + (workspaceId ?? "default"));
                Meta("root_path", "/work/repo");
                Meta("binary_version", MillerExtractContract.PinnedJulieExtractVersion);
                Meta("parser_inventory_fingerprint", "sha256:" + new string('a', 64));
                Meta("capability_snapshot_fingerprint", "sha256:" + new string('b', 64));
                Meta("created_at", "1970-01-01T00:00:00Z");
                Meta("updated_at", "1970-01-01T00:00:00Z");

                if (createSchemaVersionTable && schemaVersion is { } sv)
                {
                    string svText = sv.ToString(CultureInfo.InvariantCulture);
                    Meta("sqlite_schema_version", svText);
                    Meta("schema_version", svText);
                }

                if (contractValue is not null)
                    Meta("extract_contract_version", contractValue);

                if (hashAlgorithm is not null)
                    Meta("hash_algorithm", hashAlgorithm);

                // v4 reference-resolution metadata keys (schema 4 / product 2.9.0). Included by default for
                // fidelity; a test passes null for either to exercise the reader's unknown/null fallbacks.
                if (referenceResolutionStatus is not null)
                    Meta("reference_resolution_status", referenceResolutionStatus);
                if (referenceResolutionVersion is not null)
                    Meta("reference_resolution_version", referenceResolutionVersion);
            }

            Exec(conn, "COMMIT;");
        }

        // The write connection above was Pooling=false, so its handle is already released — no global
        // SqliteConnection.ClearAllPools() (which would race a parallel test's live connection).
        return new JulieDbFixture(dir, dbPath, rows);
    }

    /// <summary>The deterministic synthetic file_id for a path (v1 files PK; symbols/identifiers FK to it).</summary>
    private static string FileId(string path) => "file:" + path;

    /// <summary>
    /// The canonical fixture: a v1 artifact with ~12 realistic rows — mixed kinds/languages, some NULL
    /// signatures, at least one NULL start_line, parent/child pairs, distinct files.
    /// </summary>
    public static JulieDbFixture CreateDefault() => Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, DefaultRows);

    // ----- M2 inspect/ExtractReader fixture -----

    /// <summary>The byte content of <c>auth/UserService.cs</c> in <see cref="CreateForInspect"/>.</summary>
    public const string UserServiceContent =
        "public class UserService {\n" +   // bytes 0..26  (line 1)
        "  public User GetUser(int id) {\n" + // line 2
        "    return _repo.Find(id);\n" +    // line 3
        "  }\n" +                            // line 4
        "}\n";                               // line 5

    /// <summary>
    /// The id of <c>GetUser</c> — the symbol carrying full detail (doc_comment/visibility/body spans) and the
    /// one whose body slices out of <see cref="UserServiceContent"/> in <see cref="CreateForInspect"/>.
    /// </summary>
    public const string GetUserId = "b2c3d4e5f6001122334455667788990a";

    /// <summary>The id of <c>UserService</c> (the parent class of GetUser/DeleteUser).</summary>
    public const string UserServiceId = "a1b2c3d4e5f600112233445566778899";

    /// <summary>
    /// A fixture wired for the M2 inspect/ExtractReader tests: GetUser carries doc_comment + visibility +
    /// body byte/line spans into <see cref="UserServiceContent"/>; identifiers record two name-based refs to
    /// GetUser (in two enclosing symbols) and one call FROM GetUser to a helper (callee). DeleteUser carries
    /// NULL body spans (the graceful-degradation case). workspace_id drives the artifact identity keys.
    /// </summary>
    public static JulieDbFixture CreateForInspect()
    {
        // GetUser's body is the slice from just after "{" on line 1 to the closing "}" on line 4.
        // Byte offsets into UserServiceContent (computed against the literal above).
        int bodyStart = UserServiceContent.IndexOf("public User GetUser", StringComparison.Ordinal);
        int bodyEnd = UserServiceContent.IndexOf("  }\n", StringComparison.Ordinal) + 3; // include the '}'

        var rows = new[]
        {
            new SymbolRow(UserServiceId, "UserService", "class", "csharp",
                "auth/UserService.cs", "public class UserService", 1, null)
            { Visibility = "public", DocComment = "The user service." },

            new SymbolRow(GetUserId, "GetUser", "method", "csharp",
                "auth/UserService.cs", "public User GetUser(int id)", 2, UserServiceId)
            {
                Visibility = "public",
                DocComment = "Gets a user by id.",
                BodyStartByte = bodyStart, BodyEndByte = bodyEnd,
                BodyStartLine = 2, BodyEndLine = 4,
            },

            // DeleteUser: NULL body spans (graceful body degradation) + a NULL body line range.
            new SymbolRow("c3d4e5f6001122334455667788990a1b", "DeleteUser", "method", "csharp",
                "auth/UserService.cs", "public void DeleteUser(int id)", 6, UserServiceId)
            { Visibility = "public" },

            // A helper that GetUser calls (callee target by name).
            new SymbolRow("dd001122334455667788990a1b2c3d4e", "Find", "method", "csharp",
                "auth/Repo.cs", "public User Find(int id)", 3, null),

            // An unrelated caller in another file that references GetUser by name.
            new SymbolRow("ee001122334455667788990a1b2c3d4e", "Controller", "class", "csharp",
                "web/Controller.cs", "public class Controller", 1, null),
        };

        var identifiers = new[]
        {
            // Two name-based refs to "GetUser": one inside Controller, one inside Find's file (top-level).
            new IdentifierRow("f100000000000000000000000000000a", "GetUser", "call", "csharp",
                "web/Controller.cs", 4, "ee001122334455667788990a1b2c3d4e"),
            new IdentifierRow("f100000000000000000000000000000b", "GetUser", "call", "csharp",
                "auth/Repo.cs", 9, "dd001122334455667788990a1b2c3d4e"),
            // A call FROM GetUser to "Find" (callee one-hop): containing_symbol_id == GetUser, kind 'call'.
            new IdentifierRow("f100000000000000000000000000000c", "Find", "call", "csharp",
                "auth/UserService.cs", 3, GetUserId),
        };

        var content = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth/UserService.cs"] = UserServiceContent,
        };

        return Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows, identifiers: identifiers, fileContent: content, workspaceId: "ws-inspect-001");
    }

    // ----- M6 edit/ReadEditSpan + ReadIdentifierSites fixture -----
    //
    // Byte offsets below are computed against the ASCII literals (byte index == char index) and verified
    // (docs/m6-design.md verified-fact 1/2: symbols carry start_byte/end_byte AND body_start_byte/end_byte;
    // identifiers carry exact per-occurrence byte tokens). The one UTF-8 file (Café.cs) proves the reader
    // returns absolute UTF-8 byte offsets, not UTF-16 char indices.

    /// <summary>The ASCII content of <c>orders/OrderService.cs</c> in <see cref="CreateForEdit"/> (116 bytes).</summary>
    public const string OrderServiceContent =
        "public class OrderService {\n" +          // line 1  bytes 0..27
        "  public int Total() {\n" +               // line 2
        "    return _items.Sum(i => i.Total);\n" + // line 3
        "  }\n" +                                  // line 4
        "  private int _count;\n" +                // line 5
        "}\n";                                     // line 6

    /// <summary>The ASCII content of <c>billing/Invoice.cs</c> in <see cref="CreateForEdit"/> (a call to Total + a HOMONYM Total def).</summary>
    public const string InvoiceContent =
        "public class Invoice {\n" +               // line 1
        "  public int Sum(OrderService o) {\n" +   // line 2
        "    return o.Total();\n" +                // line 3  -> a genuine ref to OrderService.Total
        "  }\n" +                                  // line 4
        "  public int Total() { return 0; }\n" +   // line 5  -> a HOMONYM (unrelated same-named def)
        "}\n";                                     // line 6

    /// <summary>The UTF-8 content of <c>unicode/Café.cs</c> in <see cref="CreateForEdit"/> — the accent shifts byte vs char offsets.</summary>
    public const string CafeContent =
        "// café configuration\n" +           // line 1: 'é' is 2 UTF-8 bytes (byte 6..7)
        "var x = Total();\n";                      // line 2

    /// <summary>The id of <c>OrderService.Total</c> — the method carrying full byte + body spans in <see cref="CreateForEdit"/>.</summary>
    public const string TotalMethodId = "10ade1ade1ade1ade1ade1ade1ade100";

    /// <summary>The id of the <c>OrderService</c> class (whole-span 0..116, body 26..115).</summary>
    public const string OrderServiceId = "0c1a550c1a550c1a550c1a550c1a5500";

    /// <summary>The id of the <c>_count</c> field — NULL body spans (the body/signature-op-reject case).</summary>
    public const string CountFieldId = "f1e1df1e1df1e1df1e1df1e1df1e1d00";

    /// <summary>
    /// A fixture wired for the M6 edit read-layer tests (<c>ReadEditSpan</c> / <c>ReadIdentifierSites</c>).
    /// <c>OrderService.Total</c> carries the full whole-span + body byte offsets;
    /// the <c>_count</c> field carries NULL body spans (body/signature ops reject it). The name <c>Total</c>
    /// occurs at four identifier sites across three files: two in OrderService.cs (the method-header name token
    /// and the <c>i.Total</c> property access), one genuine call <c>o.Total()</c> in Invoice.cs, and one in the
    /// UTF-8 Café.cs (byte offset 31, NOT char offset 30 — proves UTF-8 byte addressing). Invoice.cs also
    /// defines a HOMONYM <c>Total</c> method; its def is a symbol, not an identifier, so it surfaces via
    /// <c>ReadEditSpan</c>, while the name-based identifier sites are what <c>ReadIdentifierSites</c> returns.
    /// </summary>
    public static JulieDbFixture CreateForEdit()
    {
        var rows = new[]
        {
            // OrderService class: whole span [0,116), body [26,115).
            new SymbolRow(OrderServiceId, "OrderService", "class", "csharp",
                "orders/OrderService.cs", "public class OrderService", 1, null)
            { Visibility = "public", StartByte = 0, EndByte = 116, BodyStartByte = 26, BodyEndByte = 115,
              BodyStartLine = 1, BodyEndLine = 6 },

            // Total method: signature span [30,49), body span [49,91). end_byte = body_end = 91.
            new SymbolRow(TotalMethodId, "Total", "method", "csharp",
                "orders/OrderService.cs", "public int Total()", 2, OrderServiceId)
            { Visibility = "public", StartByte = 30, EndByte = 91, BodyStartByte = 49, BodyEndByte = 91,
              BodyStartLine = 2, BodyEndLine = 4 },

            // _count field: whole span [94,113), NULL body spans (graceful reject for body/signature ops).
            new SymbolRow(CountFieldId, "_count", "field", "csharp",
                "orders/OrderService.cs", "private int _count;", 5, OrderServiceId)
            { Visibility = "private", StartByte = 94, EndByte = 113 /* body spans left NULL */ },

            // The HOMONYM Total def in another file — an unrelated symbol that happens to share the name.
            new SymbolRow("ab1ab1ab1ab1ab1ab1ab1ab1ab1ab100", "Total", "method", "csharp",
                "billing/Invoice.cs", "public int Total()", 5, null)
            { Visibility = "public", StartByte = 86, EndByte = 118, BodyStartByte = 105, BodyEndByte = 118,
              BodyStartLine = 5, BodyEndLine = 5 },

            // A symbol in Invoice.cs whose body holds the genuine o.Total() call site.
            new SymbolRow("5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c00", "Sum", "method", "csharp",
                "billing/Invoice.cs", "public int Sum(OrderService o)", 2, null)
            { Visibility = "public" },
        };

        // Four 'Total' identifier sites across three files. ReadIdentifierSites must return all of them,
        // ordered by path then start_byte. The Café.cs site's start_byte (31) differs from its char
        // index (30) — the UTF-8 proof. A homonym call site (Invoice.cs:3) is INCLUDED — name-based matching.
        var identifiers = new[]
        {
            // orders/OrderService.cs: the method-header name token [41,46) and the i.Total access [80,85).
            new IdentifierRow("d100000000000000000000000000000a", "Total", "member_access", "csharp",
                "orders/OrderService.cs", 2, TotalMethodId) { StartByte = 41, EndByte = 46 },
            new IdentifierRow("d100000000000000000000000000000b", "Total", "member_access", "csharp",
                "orders/OrderService.cs", 3, TotalMethodId) { StartByte = 80, EndByte = 85 },
            // billing/Invoice.cs: the genuine o.Total() call [71,76).
            new IdentifierRow("d100000000000000000000000000000c", "Total", "call", "csharp",
                "billing/Invoice.cs", 3, "5c5c5c5c5c5c5c5c5c5c5c5c5c5c5c00") { StartByte = 71, EndByte = 76 },
            // unicode/Café.cs: a call at BYTE offset 31 (char offset would be 30 — the é shifts it).
            new IdentifierRow("d100000000000000000000000000000d", "Total", "call", "csharp",
                "unicode/Café.cs", 2, null) { StartByte = 31, EndByte = 36 },
        };

        var content = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orders/OrderService.cs"] = OrderServiceContent,
            ["billing/Invoice.cs"] = InvoiceContent,
            ["unicode/Café.cs"] = CafeContent,
        };

        return Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows, identifiers: identifiers, fileContent: content, workspaceId: "ws-edit-001");
    }

    /// <summary>Realistic MD5-hex symbol ids (32 lowercase hex chars), per julie's id scheme (treated as opaque).</summary>
    public static IReadOnlyList<SymbolRow> DefaultRows { get; } = new[]
    {
        // auth/UserService.cs — a class with two child methods (parent/child via parent_symbol_id).
        new SymbolRow("a1b2c3d4e5f600112233445566778899", "UserService", "class", "csharp",
            "auth/UserService.cs", "public class UserService", 1, null),
        new SymbolRow("b2c3d4e5f6001122334455667788990a", "GetUser", "method", "csharp",
            "auth/UserService.cs", "public User GetUser(int id)", 5, "a1b2c3d4e5f600112233445566778899"),
        new SymbolRow("c3d4e5f6001122334455667788990a1b", "DeleteUser", "method", "csharp",
            "auth/UserService.cs", null /* NULL signature */, 12, "a1b2c3d4e5f600112233445566778899"),

        // auth/token.ts — a TS function + a const with a NULL start_line (the nullable-INTEGER trap).
        new SymbolRow("d4e5f6001122334455667788990a1b2c", "parseToken", "function", "typescript",
            "auth/token.ts", "function parseToken(raw: string): Token", 3, null),
        new SymbolRow("e5f6001122334455667788990a1b2c3d", "TOKEN_TTL", "constant", "typescript",
            "auth/token.ts", "const TOKEN_TTL = 3600", null /* NULL start_line -> 0 */, null),

        // core/math.rs — a Rust struct + impl method.
        new SymbolRow("f6001122334455667788990a1b2c3d4e", "Vector512", "struct", "rust",
            "core/math.rs", "pub struct Vector512", 8, null),
        new SymbolRow("001122334455667788990a1b2c3d4e5f", "dot", "method", "rust",
            "core/math.rs", "pub fn dot(&self, other: &Vector512) -> f32", 20, "f6001122334455667788990a1b2c3d4e"),

        // util/strings.py — python functions, one with NULL signature.
        new SymbolRow("1122334455667788990a1b2c3d4e5f60", "snake_to_camel", "function", "python",
            "util/strings.py", "def snake_to_camel(s)", 2, null),
        new SymbolRow("22334455667788990a1b2c3d4e5f6011", "EMPTY", "variable", "python",
            "util/strings.py", null /* NULL signature */, 1, null),

        // http/Server.go — go type + two methods.
        new SymbolRow("334455667788990a1b2c3d4e5f601122", "Server", "struct", "go",
            "http/Server.go", "type Server struct", 10, null),
        new SymbolRow("4455667788990a1b2c3d4e5f60112233", "getHTTPResponseCode", "method", "go",
            "http/Server.go", "func (s *Server) getHTTPResponseCode() int", 25, "334455667788990a1b2c3d4e5f601122"),
        new SymbolRow("55667788990a1b2c3d4e5f6011223344", "ServeHTTP", "method", "go",
            "http/Server.go", "func (s *Server) ServeHTTP(w ResponseWriter, r *Request)", 40, "334455667788990a1b2c3d4e5f601122"),
    };

    private static IEnumerable<string> DistinctPaths(
        IReadOnlyList<SymbolRow> rows,
        IReadOnlyList<IdentifierRow>? identifiers,
        IReadOnlyList<SourceRegionRow>? sourceRegions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
            if (seen.Add(r.FilePath))
                yield return r.FilePath;
        if (identifiers is not null)
            foreach (var i in identifiers)
                if (seen.Add(i.FilePath))
                    yield return i.FilePath;
        if (sourceRegions is not null)
            foreach (var r in sourceRegions)
                if (seen.Add(r.Path))
                    yield return r.Path;
    }

    // Parents (parent_id == null) before children so the self-referential FK never dangles at insert time.
    private static IEnumerable<SymbolRow> OrderParentsFirst(IReadOnlyList<SymbolRow> rows)
    {
        foreach (var r in rows) if (r.ParentId is null) yield return r;
        foreach (var r in rows) if (r.ParentId is not null) yield return r;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // Release THIS fixture's pooled reader handles so the temp dir can be deleted — but scope it to this
        // DB only (ClearPool, NOT the process-global ClearAllPools), so a concurrently running test's live
        // connection is never disposed out from under it (xUnit parallelizes collections).
        using (var c = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            SqliteConnection.ClearPool(c);
        }
        try
        {
            if (System.IO.Directory.Exists(_dir))
                System.IO.Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a held handle on a CI agent must not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a held handle on a CI agent must not fail the test.
        }
        _ = CultureInfo.InvariantCulture; // keep the using meaningful if trimmed later
    }

    // --- DDL transcribed from julie-extractors SQLite schema contract, with only fast-fixture relaxations. ---
    // Remaining deviations: files.last_revision_id has NO FK (so a files row can be seeded with no revision);
    // symbol/identifier position columns are nullable here (the synthetic DB relaxes julie's NOT NULL so the
    // existing NULL-discipline tests keep coverage). v1 files is content-free — body text re-sources from disk.

    private const string FilesDdl = """
        CREATE TABLE IF NOT EXISTS files (
            file_id TEXT PRIMARY KEY,
            path TEXT NOT NULL UNIQUE,
            language TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            content_bytes INTEGER NOT NULL,
            line_count INTEGER,
            indexed_at TEXT NOT NULL,
            last_revision_id INTEGER NOT NULL,
            status TEXT NOT NULL,
            metadata_json TEXT
        );
        """;

    private const string SymbolsDdl = """
        CREATE TABLE IF NOT EXISTS symbols (
            symbol_id TEXT PRIMARY KEY,
            file_id TEXT NOT NULL,
            path TEXT NOT NULL,
            language TEXT NOT NULL,
            name TEXT NOT NULL,
            kind TEXT NOT NULL,
            signature TEXT,
            doc_comment TEXT,
            visibility TEXT,
            parent_symbol_id TEXT REFERENCES symbols(symbol_id) ON DELETE SET NULL,
            start_line INTEGER, start_column INTEGER, end_line INTEGER, end_column INTEGER,
            start_byte INTEGER, end_byte INTEGER,
            body_start_line INTEGER, body_start_column INTEGER, body_end_line INTEGER, body_end_column INTEGER,
            body_start_byte INTEGER, body_end_byte INTEGER, body_hash TEXT,
            semantic_group TEXT,
            confidence REAL,
            content_type TEXT,
            is_test INTEGER NOT NULL DEFAULT 0,
            test_container INTEGER NOT NULL DEFAULT 0,
            test_lifecycle INTEGER NOT NULL DEFAULT 0,
            metadata_json TEXT
        );
        """;

    private const string IdentifiersDdl = """
        CREATE TABLE IF NOT EXISTS identifiers (
            identifier_id TEXT PRIMARY KEY,
            file_id TEXT NOT NULL,
            path TEXT NOT NULL,
            language TEXT NOT NULL,
            name TEXT NOT NULL,
            kind TEXT NOT NULL,
            containing_symbol_id TEXT,
            target_symbol_id TEXT,
            start_line INTEGER, start_column INTEGER, end_line INTEGER, end_column INTEGER,
            start_byte INTEGER, end_byte INTEGER,
            confidence REAL NOT NULL DEFAULT 1.0,
            code_context TEXT,
            metadata_json TEXT
        );
        """;

    private const string RelationshipsDdl = """
        CREATE TABLE IF NOT EXISTS relationships (
            relationship_id TEXT PRIMARY KEY,
            from_symbol_id TEXT NOT NULL,
            to_symbol_id TEXT NOT NULL,
            file_id TEXT NOT NULL,
            path TEXT NOT NULL,
            kind TEXT NOT NULL,
            start_line INTEGER, start_column INTEGER, end_line INTEGER, end_column INTEGER,
            start_byte INTEGER, end_byte INTEGER,
            confidence REAL NOT NULL DEFAULT 1.0,
            metadata_json TEXT
        );
        """;

    private const string SourceRegionsDdl = """
        CREATE TABLE IF NOT EXISTS source_regions (
            source_region_id TEXT PRIMARY KEY,
            file_id TEXT NOT NULL REFERENCES files(file_id) ON DELETE CASCADE,
            path TEXT NOT NULL,
            language TEXT NOT NULL,
            kind TEXT NOT NULL,
            containing_symbol_id TEXT REFERENCES symbols(symbol_id) ON DELETE SET NULL,
            start_line INTEGER NOT NULL,
            start_column INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            end_column INTEGER NOT NULL,
            start_byte INTEGER NOT NULL,
            end_byte INTEGER NOT NULL,
            metadata_json TEXT
        );
        """;

    private const string SourceRegionsIndexesDdl = """
        CREATE INDEX IF NOT EXISTS idx_source_regions_file_span ON source_regions(file_id, start_byte, end_byte);
        CREATE INDEX IF NOT EXISTS idx_source_regions_kind_file ON source_regions(kind, file_id, start_byte);
        CREATE INDEX IF NOT EXISTS idx_source_regions_symbol ON source_regions(containing_symbol_id);
        """;

    private const string PatternCatalogDdl = """
        CREATE TABLE IF NOT EXISTS pattern_catalog (
            pattern_id TEXT PRIMARY KEY,
            label TEXT NOT NULL,
            description TEXT,
            tags_json TEXT,
            expected_metadata_keys_json TEXT
        );
        """;

    private const string StructuralFactsDdl = """
        CREATE TABLE IF NOT EXISTS structural_facts (
            structural_fact_id TEXT PRIMARY KEY,
            file_id TEXT NOT NULL REFERENCES files(file_id) ON DELETE CASCADE,
            path TEXT NOT NULL,
            language TEXT NOT NULL,
            pattern_id TEXT NOT NULL,
            capture_name TEXT NOT NULL,
            node_kind TEXT NOT NULL,
            containing_symbol_id TEXT REFERENCES symbols(symbol_id) ON DELETE SET NULL,
            start_line INTEGER NOT NULL,
            start_column INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            end_column INTEGER NOT NULL,
            start_byte INTEGER NOT NULL,
            end_byte INTEGER NOT NULL,
            confidence REAL NOT NULL,
            metadata_json TEXT
        );
        """;

    private const string ComplexityMetricsDdl = """
        CREATE TABLE IF NOT EXISTS complexity_metrics (
            complexity_metric_id TEXT PRIMARY KEY,
            file_id TEXT NOT NULL REFERENCES files(file_id) ON DELETE CASCADE,
            path TEXT NOT NULL,
            language TEXT NOT NULL,
            scope TEXT NOT NULL,
            symbol_id TEXT REFERENCES symbols(symbol_id) ON DELETE SET NULL,
            algorithm_id TEXT NOT NULL,
            covered_lines INTEGER NOT NULL,
            covered_bytes INTEGER NOT NULL,
            decision_count INTEGER NOT NULL,
            loop_count INTEGER NOT NULL,
            max_nesting_depth INTEGER NOT NULL,
            parameter_count INTEGER,
            start_line INTEGER NOT NULL,
            start_column INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            end_column INTEGER NOT NULL,
            start_byte INTEGER NOT NULL,
            end_byte INTEGER NOT NULL,
            metadata_json TEXT
        );
        """;

    // ---- M4 bridge tables (v1 schema.rs:192-233) -----------------------------------------------------------
    // Created empty (the bridge rows are seeded by subsystem-B's SqliteBridgeReaderTests inline DBs, not here).
    // v1 split: type_argument_usages carries identifier_id/path/language; type_arguments carries usage_id +
    // ordinal + parent_type_argument_id + type_name. symbol_annotations has NO ordinal (re-keyed to annotation_id).

    private const string TypeArgumentUsagesDdl = """
        CREATE TABLE IF NOT EXISTS type_argument_usages (
            usage_id TEXT PRIMARY KEY,
            identifier_id TEXT NOT NULL,
            file_id TEXT NOT NULL,
            path TEXT NOT NULL,
            language TEXT NOT NULL,
            metadata_json TEXT
        );
        """;

    private const string TypeArgumentsDdl = """
        CREATE TABLE IF NOT EXISTS type_arguments (
            type_argument_id TEXT PRIMARY KEY,
            usage_id TEXT NOT NULL,
            parent_type_argument_id TEXT,
            ordinal INTEGER NOT NULL,
            type_name TEXT NOT NULL
        );
        """;

    private const string LiteralsDdl = """
        CREATE TABLE IF NOT EXISTS literals (
            literal_id TEXT PRIMARY KEY,
            file_id TEXT NOT NULL,
            path TEXT NOT NULL,
            language TEXT NOT NULL,
            literal_text TEXT NOT NULL,
            kind TEXT NOT NULL,
            carrier TEXT,
            arg_position INTEGER NOT NULL,
            containing_symbol_id TEXT,
            start_line INTEGER NOT NULL,
            start_column INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            end_column INTEGER NOT NULL,
            start_byte INTEGER NOT NULL,
            end_byte INTEGER NOT NULL,
            confidence REAL NOT NULL,
            metadata_json TEXT
        );
        """;

    private const string SymbolAnnotationsDdl = """
        CREATE TABLE IF NOT EXISTS symbol_annotations (
            annotation_id TEXT PRIMARY KEY,
            symbol_id TEXT NOT NULL,
            annotation TEXT NOT NULL,
            annotation_key TEXT NOT NULL,
            raw_text TEXT,
            carrier TEXT,
            metadata_json TEXT
        );
        """;

    // ---- v1-only tables (schema.rs:18-26, 155-190, 235-288) — created empty for artifact fidelity ----------

    private const string ParserInventoryDdl = """
        CREATE TABLE IF NOT EXISTS parser_inventory (
            language TEXT NOT NULL,
            parser_package TEXT NOT NULL,
            parser_version TEXT,
            grammar_version TEXT,
            source TEXT,
            metadata_json TEXT,
            PRIMARY KEY (language, parser_package)
        );
        """;

    private const string ParseDiagnosticsDdl = """
        CREATE TABLE IF NOT EXISTS parse_diagnostics (
            diagnostic_id TEXT PRIMARY KEY,
            file_id TEXT NOT NULL,
            path TEXT NOT NULL,
            language TEXT NOT NULL,
            kind TEXT NOT NULL,
            message TEXT,
            start_line INTEGER NOT NULL,
            start_column INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            end_column INTEGER NOT NULL,
            start_byte INTEGER NOT NULL,
            end_byte INTEGER NOT NULL,
            metadata_json TEXT
        );
        """;

    private const string LanguageCapabilitiesDdl = """
        CREATE TABLE IF NOT EXISTS language_capabilities (
            language TEXT PRIMARY KEY,
            parser_package TEXT NOT NULL,
            extensions_json TEXT NOT NULL,
            dependency_status TEXT NOT NULL,
            target_symbols INTEGER NOT NULL,
            target_relationships INTEGER NOT NULL,
            target_pending_relationships INTEGER NOT NULL,
            target_identifiers INTEGER NOT NULL,
            target_types INTEGER NOT NULL,
            actual_symbols INTEGER NOT NULL,
            actual_relationships INTEGER NOT NULL,
            actual_pending_relationships INTEGER NOT NULL,
            actual_identifiers INTEGER NOT NULL,
            actual_types INTEGER NOT NULL,
            kind_coverage_json TEXT NOT NULL
        );
        """;

    private const string LanguageCapabilityFixturesDdl = """
        CREATE TABLE IF NOT EXISTS language_capability_fixtures (
            language TEXT NOT NULL,
            fixture_name TEXT NOT NULL,
            source_path TEXT NOT NULL,
            expected_path TEXT NOT NULL,
            PRIMARY KEY (language, fixture_name)
        );
        """;

    private const string LanguageCapabilityGapsDdl = """
        CREATE TABLE IF NOT EXISTS language_capability_gaps (
            gap_id TEXT PRIMARY KEY,
            language TEXT NOT NULL,
            capability TEXT NOT NULL,
            status TEXT NOT NULL,
            reason TEXT NOT NULL,
            required_closure TEXT NOT NULL,
            evidence_json TEXT NOT NULL
        );
        """;

    // v4 pinned shape: the columns are unchanged from v1, but the FKs (from_symbol_id / caller_scope_symbol_id /
    // file_id) and the four lookup indexes are added so the synthetic artifact matches the reference-resolution
    // contract the dead-code reader depends on (schema v4 / product 2.9.0).
    private const string PendingRelationshipsDdl = """
        CREATE TABLE IF NOT EXISTS pending_relationships (
            pending_relationship_id TEXT PRIMARY KEY,
            from_symbol_id TEXT NOT NULL,
            caller_scope_symbol_id TEXT,
            file_id TEXT NOT NULL,
            path TEXT NOT NULL,
            kind TEXT NOT NULL,
            target_display_name TEXT NOT NULL,
            target_terminal_name TEXT NOT NULL,
            target_receiver TEXT,
            target_namespace_json TEXT NOT NULL,
            target_import_context TEXT,
            start_line INTEGER NOT NULL,
            start_column INTEGER,
            end_line INTEGER,
            end_column INTEGER,
            start_byte INTEGER,
            end_byte INTEGER,
            confidence REAL NOT NULL,
            metadata_json TEXT,
            FOREIGN KEY (from_symbol_id) REFERENCES symbols(symbol_id) ON DELETE CASCADE,
            FOREIGN KEY (caller_scope_symbol_id) REFERENCES symbols(symbol_id) ON DELETE SET NULL,
            FOREIGN KEY (file_id) REFERENCES files(file_id) ON DELETE CASCADE
        );
        """;

    private const string PendingRelationshipsIndexesDdl = """
        CREATE INDEX IF NOT EXISTS idx_pending_terminal ON pending_relationships(target_terminal_name);
        CREATE INDEX IF NOT EXISTS idx_pending_file ON pending_relationships(file_id);
        CREATE INDEX IF NOT EXISTS idx_pending_from ON pending_relationships(from_symbol_id);
        CREATE INDEX IF NOT EXISTS idx_pending_caller_scope ON pending_relationships(caller_scope_symbol_id);
        """;

    // v4 overlay: identifier_resolutions (the resolved/ambiguous outcome for a plain identifier reference). The
    // CHECK ties outcome='resolved' to a non-null target_symbol_id — a faithful copy of the pinned contract.
    private const string IdentifierResolutionsDdl = """
        CREATE TABLE IF NOT EXISTS identifier_resolutions (
            identifier_id TEXT PRIMARY KEY REFERENCES identifiers(identifier_id) ON DELETE CASCADE,
            target_symbol_id TEXT REFERENCES symbols(symbol_id) ON DELETE CASCADE,
            tier INTEGER,
            confidence REAL,
            method TEXT,
            outcome TEXT NOT NULL,
            candidates INTEGER,
            resolved_at_revision INTEGER NOT NULL,
            CHECK ((outcome = 'resolved') = (target_symbol_id IS NOT NULL))
        );
        """;

    private const string IdentifierResolutionsIndexDdl =
        "CREATE INDEX IF NOT EXISTS idx_identifier_resolutions_target ON identifier_resolutions(target_symbol_id);";

    // v4 overlay: pending_resolutions (the resolved target for a deferred/pending relationship). Independent of
    // identifier_resolutions — a pending relationship can resolve with no identifier_resolutions row.
    private const string PendingResolutionsDdl = """
        CREATE TABLE IF NOT EXISTS pending_resolutions (
            pending_relationship_id TEXT PRIMARY KEY
                REFERENCES pending_relationships(pending_relationship_id) ON DELETE CASCADE,
            target_symbol_id TEXT NOT NULL REFERENCES symbols(symbol_id) ON DELETE CASCADE,
            tier INTEGER NOT NULL,
            confidence REAL NOT NULL,
            method TEXT NOT NULL,
            resolved_at_revision INTEGER NOT NULL
        );
        """;

    private const string PendingResolutionsIndexDdl =
        "CREATE INDEX IF NOT EXISTS idx_pending_resolutions_target ON pending_resolutions(target_symbol_id);";

    private const string TypeFactsDdl = """
        CREATE TABLE IF NOT EXISTS type_facts (
            type_fact_id TEXT PRIMARY KEY,
            symbol_id TEXT NOT NULL,
            language TEXT NOT NULL,
            resolved_type TEXT NOT NULL,
            generic_params_json TEXT,
            constraints_json TEXT,
            is_inferred INTEGER NOT NULL,
            metadata_json TEXT
        );
        """;

    private const string MetadataDdl = """
        CREATE TABLE IF NOT EXISTS artifact_metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    // --- v1 freshness DDL (schema.rs:28-50). extraction_revisions has no workspace_id (one DB = one root) and
    //     revision_id is an explicit PRIMARY KEY (NOT autoincrement); revision_file_changes has no workspace_id
    //     and no CHECK on change_kind (Miller is the only guard — see FreshnessReader.ParseChangeKind). ---

    private const string ExtractionRevisionsDdl = """
        CREATE TABLE IF NOT EXISTS extraction_revisions (
            revision_id INTEGER PRIMARY KEY,
            parent_revision_id INTEGER,
            operation TEXT NOT NULL,
            mode TEXT,
            started_at TEXT NOT NULL,
            completed_at TEXT NOT NULL,
            binary_version TEXT NOT NULL,
            extract_contract_version TEXT NOT NULL,
            sqlite_schema_version TEXT NOT NULL,
            input_root TEXT,
            counts_json TEXT NOT NULL
        );
        """;

    private const string RevisionFileChangesDdl = """
        CREATE TABLE IF NOT EXISTS revision_file_changes (
            revision_id INTEGER NOT NULL,
            file_id TEXT NOT NULL,
            path TEXT NOT NULL,
            change_kind TEXT NOT NULL,
            PRIMARY KEY (revision_id, file_id)
        );
        """;
}
