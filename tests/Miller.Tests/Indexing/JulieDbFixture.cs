using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;

namespace Miller.Tests.Indexing;

/// <summary>
/// Synthesizes a tiny SQLite file matching julie v7.13.1's verified extract schema (schema_version 28,
/// extract_contract_version 3). This is Miller's READ-CONTRACT harness — it is NOT a re-test of julie's
/// extraction (julie owns that). The DDL is transcribed verbatim from julie's <c>src/database/schema.rs</c>
/// (see docs/findings/julie-contract-verified.md §1), so the reader is exercised against the real column
/// set, NULL discipline, and self-FK that a live extract produces.
///
/// Disposable: deletes the temp directory (and -wal/-shm sidecars) on <see cref="Dispose"/>.
/// </summary>
internal sealed class JulieDbFixture : IDisposable
{
    private readonly string _dir;

    /// <summary>
    /// The schema_version / extract_contract_version this Miller build is pinned to, sourced from
    /// <see cref="MillerExtractContract"/>. Fixtures that just need a *valid* extract pass these (NOT
    /// literals) so a julie re-pin needs no per-test edits — only the one constants file changes.
    /// </summary>
    public static readonly long PinnedSchema = MillerExtractContract.ExpectedSchemaVersion;

    /// <summary>The pinned contract version as the TEXT julie stores in external_extract_metadata.</summary>
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
    /// The known rows inserted by <see cref="CreateDefault"/>, in INSERT order. Tests assert the reader's
    /// output against the subset/ordering these imply (the reader's SELECT re-orders by file_path,start_line,id).
    /// </summary>
    public IReadOnlyList<SymbolRow> Rows { get; }

    private JulieDbFixture(string dir, string dbPath, IReadOnlyList<SymbolRow> rows)
    {
        _dir = dir;
        DbPath = dbPath;
        Rows = rows;
    }

    /// <summary>
    /// A row as written into the synthesized <c>symbols</c> table. The first eight fields are the M1 read
    /// projection; the remaining detail/body columns (M2 <c>ReadDetail</c>/<c>ReadBody</c>) are optional
    /// init-properties that default to NULL, so every existing positional construction stays valid and the
    /// 90 M1 tests are unaffected.
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
        public string? CodeContext { get; init; }

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
        /// Raw <c>symbols.metadata</c> JSON (julie's per-language extractor output). NULL by default so
        /// existing rows are unaffected. Seed e.g. <c>{"is_test":true}</c> to exercise the cross-language
        /// <c>is_test</c> read path (M2 decision-4).
        /// </summary>
        public string? Metadata { get; init; }
    }

    /// <summary>A row as written into the synthesized <c>identifiers</c> table (M2 <c>ReadReferences</c>).</summary>
    internal sealed record IdentifierRow(
        string Id,
        string Name,
        string Kind,             // 'call' | 'variable_ref' | 'type_usage' | 'member_access'
        string Language,
        string FilePath,
        int StartLine,
        string? ContainingSymbolId) // POPULATED (enclosing symbol). target_symbol_id is ALWAYS NULL.
    {
        /// <summary>
        /// The exact per-occurrence byte token span (julie's <c>identifiers.start_byte</c>/<c>end_byte</c>),
        /// e.g. a 5-char <c>Total</c> call at <c>start_byte=120, end_byte=125</c>. NULL by default so the M2
        /// reference rows are unaffected; M6's <c>ReadIdentifierSites</c> reads these for exact-span rename.
        /// </summary>
        public int? StartByte { get; init; }
        public int? EndByte { get; init; }
    }

    /// <summary>
    /// A row as written into the synthesized <c>relationships</c> table (M5 D2 precise edge source,
    /// verified-fact 1). <see cref="FromSymbolId"/> → <see cref="ToSymbolId"/> are BOTH resolved symbol ids
    /// (julie's <c>from_symbol_id</c>/<c>to_symbol_id</c>, NOT NULL); <see cref="Kind"/> is the edge label
    /// (<c>calls</c>/<c>uses</c>/...). Sparse: only the directly-extracted edges (the analyze pass does not run
    /// under <c>extract scan</c>).
    /// </summary>
    internal sealed record RelationshipRow(
        string Id,
        string FromSymbolId,
        string ToSymbolId,
        string Kind);

    /// <summary>
    /// A row as written into the synthesized <c>canonical_revisions</c> table (M3 freshness cursor,
    /// verified-fact 1). <see cref="Revision"/> is the PK the <see cref="FreshnessReader"/> takes MAX of per
    /// workspace; <see cref="Kind"/> is <c>fresh|incremental</c> (CHECK-constrained, like julie's schema 26).
    /// </summary>
    internal sealed record RevisionRow(long Revision, string WorkspaceId, string Kind = "incremental")
    {
        public long CreatedAt { get; init; }
    }

    /// <summary>
    /// A row as written into the synthesized <c>revision_file_changes</c> table (M3 changed-file delta,
    /// verified-fact 5). <see cref="ChangeKind"/> is <c>added|modified|deleted</c> (CHECK-constrained).
    /// </summary>
    internal sealed record RevisionFileChangeRow(
        long Revision,
        string WorkspaceId,
        string FilePath,
        string ChangeKind)
    {
        public string? OldHash { get; init; }
        public string? NewHash { get; init; }
    }

    /// <summary>
    /// Build a fixture with the given schema/contract version rows and the supplied symbol rows.
    /// <paramref name="schemaVersion"/> is written to <c>schema_version</c>; <paramref name="contractValue"/>
    /// and <paramref name="hashAlgorithm"/> are written to <c>external_extract_metadata</c> as TEXT (julie
    /// stores all metadata values as strings). Passing <c>null</c> for schema/contract skips that row or table
    /// as before; passing <c>null</c> for hashAlgorithm omits only that metadata key.
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
        string? hashAlgorithm = MillerExtractContract.ExpectedHashAlgorithm)
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
            // Match julie: WAL + FK enforcement ON. Exercises the WAL sidecar path the reader must tolerate.
            Exec(conn, "PRAGMA journal_mode=WAL;");
            Exec(conn, "PRAGMA foreign_keys=ON;");

            Exec(conn, FilesDdl);
            Exec(conn, SymbolsDdl);
            Exec(conn, IdentifiersDdl);
            // The relationships table is always created (harmless to existing tests; they query
            // symbols/identifiers only) so a SymbolGraphReader can open against any fixture.
            Exec(conn, RelationshipsDdl);
            // The M3 freshness tables are always created (harmless to existing tests; they query
            // symbols/identifiers only) so a FreshnessReader can open against any fixture.
            Exec(conn, CanonicalRevisionsDdl);
            Exec(conn, RevisionFileChangesDdl);
            // The M4 bridge tables are always created so the SqliteBridgeReader — now on the single production
            // RepositoryIndexLoader.Load path (D9) — can open against ANY fixture. Without them every loader /
            // rebuilder / freshness-swap test that routes through Load crashes on "no such table: type_arguments".
            // Empty by default (no bridge breadcrumbs) → an empty bridge graph, exactly like a scan-only extract.
            Exec(conn, TypeArgumentsDdl);
            Exec(conn, LiteralsDdl);
            Exec(conn, SymbolAnnotationsDdl);
            if (createSchemaVersionTable) Exec(conn, SchemaVersionDdl);
            if (createMetadataTable) Exec(conn, MetadataDdl);

            // files rows (symbols.file_path REFERENCES files(path) — FK is ON, so parents must exist).
            // identifiers also FK to files(path), so union both sources of paths.
            foreach (var path in DistinctPaths(rows, identifiers))
            {
                string content = fileContent is not null && fileContent.TryGetValue(path, out var c) ? c : "";
                string hash = ContentHasher.Blake3Hex(System.Text.Encoding.UTF8.GetBytes(content));
                using var fcmd = conn.CreateCommand();
                fcmd.CommandText =
                    "INSERT INTO files (path, language, hash, size, last_modified, content, line_count) " +
                    "VALUES ($p, 'csharp', $hash, 100, 0, $content, 0);";
                fcmd.Parameters.AddWithValue("$p", path);
                fcmd.Parameters.AddWithValue("$hash", hash);
                fcmd.Parameters.AddWithValue("$content", content);
                fcmd.ExecuteNonQuery();
            }

            // symbols rows — parents first so self-FK parent_id resolves under FK enforcement. The detail/body
            // columns are written from the row's optional init-props (NULL by default — M1 behavior preserved).
            foreach (var r in OrderParentsFirst(rows))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO symbols (id, name, kind, language, file_path, signature, start_line, end_line, parent_id, " +
                    "metadata, doc_comment, visibility, code_context, " +
                    "start_byte, end_byte, " +
                    "body_start_byte, body_end_byte, body_start_line, body_end_line) " +
                    "VALUES ($id, $name, $kind, $lang, $fp, $sig, $sl, $el, $pid, " +
                    "$meta, $doc, $vis, $ctx, $sb, $eb, $bsb, $beb, $bsl, $bel);";
                cmd.Parameters.AddWithValue("$id", r.Id);
                cmd.Parameters.AddWithValue("$name", r.Name);
                cmd.Parameters.AddWithValue("$kind", r.Kind);
                cmd.Parameters.AddWithValue("$lang", r.Language);
                cmd.Parameters.AddWithValue("$fp", r.FilePath);
                cmd.Parameters.AddWithValue("$sig", (object?)r.Signature ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sl", (object?)r.StartLine ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$el", (object?)r.EndLine ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pid", (object?)r.ParentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$meta", (object?)r.Metadata ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$doc", (object?)r.DocComment ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$vis", (object?)r.Visibility ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ctx", (object?)r.CodeContext ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sb", (object?)r.StartByte ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$eb", (object?)r.EndByte ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bsb", (object?)r.BodyStartByte ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$beb", (object?)r.BodyEndByte ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bsl", (object?)r.BodyStartLine ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bel", (object?)r.BodyEndLine ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            // identifiers rows — target_symbol_id is ALWAYS NULL from extract (not written here).
            if (identifiers is not null)
            {
                foreach (var ident in identifiers)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "INSERT INTO identifiers (id, name, kind, language, file_path, " +
                        "start_line, start_col, end_line, end_col, start_byte, end_byte, " +
                        "containing_symbol_id, target_symbol_id) " +
                        "VALUES ($id, $name, $kind, $lang, $fp, $sl, 0, $sl, 0, $sb, $eb, $cid, NULL);";
                    cmd.Parameters.AddWithValue("$id", ident.Id);
                    cmd.Parameters.AddWithValue("$name", ident.Name);
                    cmd.Parameters.AddWithValue("$kind", ident.Kind);
                    cmd.Parameters.AddWithValue("$lang", ident.Language);
                    cmd.Parameters.AddWithValue("$fp", ident.FilePath);
                    cmd.Parameters.AddWithValue("$sl", ident.StartLine);
                    cmd.Parameters.AddWithValue("$sb", (object?)ident.StartByte ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$eb", (object?)ident.EndByte ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$cid", (object?)ident.ContainingSymbolId ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }

            // relationships rows (M5 D2 precise edges). from_symbol_id/to_symbol_id FK to symbols(id), which
            // are already inserted above, so FK enforcement is satisfied.
            if (relationships is not null)
            {
                foreach (var rel in relationships)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "INSERT INTO relationships (id, from_symbol_id, to_symbol_id, kind) " +
                        "VALUES ($id, $from, $to, $kind);";
                    cmd.Parameters.AddWithValue("$id", rel.Id);
                    cmd.Parameters.AddWithValue("$from", rel.FromSymbolId);
                    cmd.Parameters.AddWithValue("$to", rel.ToSymbolId);
                    cmd.Parameters.AddWithValue("$kind", rel.Kind);
                    cmd.ExecuteNonQuery();
                }
            }

            // canonical_revisions rows (M3 freshness cursor). revision is an explicit PK here (the test
            // controls the values), so we insert the column directly rather than relying on AUTOINCREMENT.
            if (revisions is not null)
            {
                foreach (var rev in revisions)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "INSERT INTO canonical_revisions " +
                        "(revision, workspace_id, kind, cleaned_file_count, file_count, symbol_count, " +
                        "relationship_count, identifier_count, type_count, created_at) " +
                        "VALUES ($rev, $ws, $kind, 0, 0, 0, 0, 0, 0, $created);";
                    cmd.Parameters.AddWithValue("$rev", rev.Revision);
                    cmd.Parameters.AddWithValue("$ws", rev.WorkspaceId);
                    cmd.Parameters.AddWithValue("$kind", rev.Kind);
                    cmd.Parameters.AddWithValue("$created", rev.CreatedAt);
                    cmd.ExecuteNonQuery();
                }
            }

            // revision_file_changes rows (M3 changed-file delta).
            if (fileChanges is not null)
            {
                foreach (var fc in fileChanges)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "INSERT INTO revision_file_changes " +
                        "(revision, workspace_id, file_path, change_kind, old_hash, new_hash) " +
                        "VALUES ($rev, $ws, $fp, $ck, $oh, $nh);";
                    cmd.Parameters.AddWithValue("$rev", fc.Revision);
                    cmd.Parameters.AddWithValue("$ws", fc.WorkspaceId);
                    cmd.Parameters.AddWithValue("$fp", fc.FilePath);
                    cmd.Parameters.AddWithValue("$ck", fc.ChangeKind);
                    cmd.Parameters.AddWithValue("$oh", (object?)fc.OldHash ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$nh", (object?)fc.NewHash ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }

            if (createSchemaVersionTable && schemaVersion is { } sv)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO schema_version (version, applied_at, description) VALUES ($v, 0, 'test');";
                cmd.Parameters.AddWithValue("$v", sv);
                cmd.ExecuteNonQuery();
            }

            if (createMetadataTable && contractValue is not null)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO external_extract_metadata (key, value, updated_at) " +
                    "VALUES ('extract_contract_version', $val, 0);";
                cmd.Parameters.AddWithValue("$val", contractValue);
                cmd.ExecuteNonQuery();
            }

            if (createMetadataTable && hashAlgorithm is not null)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO external_extract_metadata (key, value, updated_at) " +
                    "VALUES ('hash_algorithm', $val, 0);";
                cmd.Parameters.AddWithValue("$val", hashAlgorithm);
                cmd.ExecuteNonQuery();
            }

            if (createMetadataTable && workspaceId is not null)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO external_extract_metadata (key, value, updated_at) " +
                    "VALUES ('workspace_id', $val, 0);";
                cmd.Parameters.AddWithValue("$val", workspaceId);
                cmd.ExecuteNonQuery();
            }
        }

        // The write connection above was Pooling=false, so its handle is already released — no global
        // SqliteConnection.ClearAllPools() (which would race a parallel test's live connection).
        return new JulieDbFixture(dir, dbPath, rows);
    }

    /// <summary>
    /// The canonical fixture: schema 28 / contract '3' with ~12 realistic rows — mixed kinds/languages,
    /// some NULL signatures, at least one NULL start_line, parent/child pairs via parent_id, distinct files.
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
    /// NULL body spans (the graceful-degradation case). workspace_id is set so startup can read it.
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
                CodeContext = "public User GetUser(int id) { ... }",
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
    /// A fixture wired for the M6 edit read-layer tests (<c>ReadEditSpan</c> / <c>ReadIdentifierSites</c> /
    /// <c>ReadIndexedFileText</c>). <c>OrderService.Total</c> carries the full whole-span + body byte offsets;
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
        // ordered by file_path then start_byte. The Café.cs site's start_byte (31) differs from its char
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
        // auth/UserService.cs — a class with two child methods (parent/child via parent_id).
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
        IReadOnlyList<SymbolRow> rows, IReadOnlyList<IdentifierRow>? identifiers)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
            if (seen.Add(r.FilePath))
                yield return r.FilePath;
        if (identifiers is not null)
            foreach (var i in identifiers)
                if (seen.Add(i.FilePath))
                    yield return i.FilePath;
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
        _ = CultureInfo.InvariantCulture; // keep the using meaningful if trimmed later
    }

    // --- DDL transcribed verbatim from julie src/database/schema.rs (contract-verified §1) ---

    private const string FilesDdl = """
        CREATE TABLE IF NOT EXISTS files (
            path TEXT PRIMARY KEY,
            language TEXT NOT NULL,
            hash TEXT NOT NULL,
            size INTEGER NOT NULL,
            last_modified INTEGER NOT NULL,
            last_indexed INTEGER DEFAULT 0,
            parse_cache BLOB,
            symbol_count INTEGER DEFAULT 0,
            content TEXT,
            line_count INTEGER DEFAULT 0
        );
        """;

    private const string SymbolsDdl = """
        CREATE TABLE IF NOT EXISTS symbols (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL,
            language TEXT NOT NULL,
            file_path TEXT NOT NULL REFERENCES files(path) ON DELETE CASCADE,
            signature TEXT,
            start_line INTEGER, start_col INTEGER, end_line INTEGER, end_col INTEGER,
            start_byte INTEGER, end_byte INTEGER,
            doc_comment TEXT,
            visibility TEXT,
            code_context TEXT,
            parent_id TEXT REFERENCES symbols(id),
            metadata TEXT,
            file_hash TEXT,
            last_indexed INTEGER DEFAULT 0,
            semantic_group TEXT,
            confidence REAL DEFAULT 1.0,
            content_type TEXT DEFAULT NULL,
            body_start_line INTEGER, body_start_col INTEGER, body_end_line INTEGER, body_end_col INTEGER,
            body_start_byte INTEGER, body_end_byte INTEGER, body_hash TEXT,
            reference_score REAL NOT NULL DEFAULT 0.0
        );
        """;

    private const string IdentifiersDdl = """
        CREATE TABLE IF NOT EXISTS identifiers (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL,
            language TEXT NOT NULL,
            file_path TEXT NOT NULL REFERENCES files(path) ON DELETE CASCADE,
            start_line INTEGER NOT NULL, start_col INTEGER NOT NULL, end_line INTEGER NOT NULL, end_col INTEGER NOT NULL,
            start_byte INTEGER, end_byte INTEGER,
            containing_symbol_id TEXT REFERENCES symbols(id) ON DELETE CASCADE,
            target_symbol_id TEXT REFERENCES symbols(id) ON DELETE SET NULL,
            confidence REAL DEFAULT 1.0,
            code_context TEXT,
            last_indexed INTEGER DEFAULT 0
        );
        """;

    private const string RelationshipsDdl = """
        CREATE TABLE IF NOT EXISTS relationships (
            id TEXT PRIMARY KEY,
            from_symbol_id TEXT NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
            to_symbol_id TEXT NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
            kind TEXT NOT NULL,
            file_path TEXT NOT NULL DEFAULT '',
            line_number INTEGER NOT NULL DEFAULT 0,
            confidence REAL DEFAULT 1.0,
            metadata TEXT,
            created_at INTEGER DEFAULT 0
        );
        """;

    // ---- M4 bridge tables (verbatim from julie v7.13.1 schema.rs; findings 28-3) ------------------------
    // The SqliteBridgeReader is now on the single production RepositoryIndexLoader.Load path (D9), so these are
    // always created — empty by default — and every loader/rebuilder/freshness-swap test that routes through Load
    // can open against ANY fixture (without them: "no such table: type_arguments"). The reader selects:
    //   type_arguments(identifier_id, ordinal, parent_arg_id, type_name, file_path, id);
    //   literals(literal_text, kind, carrier, arg_position, language, containing_symbol_id, start_byte, end_byte, file_path, start_line, id);
    //   symbol_annotations(symbol_id, ordinal, annotation, annotation_key, raw_text, carrier, id).

    private const string TypeArgumentsDdl = """
        CREATE TABLE IF NOT EXISTS type_arguments (
            id TEXT PRIMARY KEY,
            identifier_id TEXT NOT NULL,
            parent_arg_id TEXT,
            ordinal INTEGER NOT NULL,
            type_name TEXT NOT NULL,
            target_symbol_id TEXT,
            file_path TEXT NOT NULL,
            language TEXT NOT NULL,
            last_indexed INTEGER
        );
        """;

    private const string LiteralsDdl = """
        CREATE TABLE IF NOT EXISTS literals (
            id TEXT PRIMARY KEY,
            literal_text TEXT NOT NULL,
            kind TEXT NOT NULL,
            carrier TEXT,
            arg_position INTEGER NOT NULL,
            language TEXT NOT NULL,
            file_path TEXT NOT NULL,
            start_line INTEGER,
            end_line INTEGER,
            start_byte INTEGER,
            end_byte INTEGER,
            containing_symbol_id TEXT,
            confidence REAL
        );
        """;

    private const string SymbolAnnotationsDdl = """
        CREATE TABLE IF NOT EXISTS symbol_annotations (
            id TEXT PRIMARY KEY,
            symbol_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            annotation TEXT NOT NULL,
            annotation_key TEXT,
            raw_text TEXT,
            carrier TEXT,
            UNIQUE (symbol_id, ordinal)
        );
        """;

    private const string SchemaVersionDdl = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER PRIMARY KEY,
            applied_at INTEGER NOT NULL,
            description TEXT NOT NULL
        );
        """;

    private const string MetadataDdl = """
        CREATE TABLE IF NOT EXISTS external_extract_metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at INTEGER NOT NULL
        );
        """;

    // --- M3 freshness DDL transcribed verbatim from the PINNED julie-server v7.13.1 (schema 28) live DB ---
    // (dumped via `.schema` against a real `extract scan` output; see m3-design.md verified-fact 1/5).

    private const string CanonicalRevisionsDdl = """
        CREATE TABLE IF NOT EXISTS canonical_revisions (
            revision INTEGER PRIMARY KEY AUTOINCREMENT,
            workspace_id TEXT NOT NULL,
            kind TEXT NOT NULL CHECK(kind IN ('fresh', 'incremental')),
            cleaned_file_count INTEGER NOT NULL DEFAULT 0,
            file_count INTEGER NOT NULL DEFAULT 0,
            symbol_count INTEGER NOT NULL DEFAULT 0,
            relationship_count INTEGER NOT NULL DEFAULT 0,
            identifier_count INTEGER NOT NULL DEFAULT 0,
            type_count INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL
        );
        """;

    private const string RevisionFileChangesDdl = """
        CREATE TABLE IF NOT EXISTS revision_file_changes (
            revision INTEGER NOT NULL,
            workspace_id TEXT NOT NULL,
            file_path TEXT NOT NULL,
            change_kind TEXT NOT NULL CHECK(change_kind IN ('added', 'modified', 'deleted')),
            old_hash TEXT,
            new_hash TEXT,
            PRIMARY KEY (revision, workspace_id, file_path)
        );
        """;
}
