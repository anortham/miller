using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Miller.Core.DeadCode;

namespace Miller.Indexing;

/// <summary>Literal-scan accounting for the two-phase dead-code pass: how many literal-bearing files were read
/// under the freshness guard, and how many were skipped because their on-disk bytes no longer match the artifact.</summary>
public sealed record DeadCodeLiteralScan(int FilesScanned, int FilesSkippedStale);

/// <summary>Artifact-identity block for a dead-code report: the artifact id, the max extraction revision, and the
/// reference-resolution status/version metadata (status falls back to <c>"unknown"</c> and version to <c>null</c>
/// when the artifact predates the v4 keys).</summary>
public sealed record DeadCodeArtifact(
    string? ArtifactId,
    long? Revision,
    string ReferenceResolutionStatus,
    string? ReferenceResolutionVersion);

/// <summary>The final dead-code report the CLI renders: the evaluated <see cref="DeadCodeResult"/> (two-phase
/// literal scan already applied), the per-language coverage rows, the literal-scan accounting, and the artifact
/// block. Produced by <see cref="DeadCodeCandidateReader.Read"/> — the reader owns all query-time computation.</summary>
public sealed record DeadCodeCandidateReport(
    DeadCodeResult Result,
    IReadOnlyList<LanguageCoverageRow> LanguageCoverage,
    DeadCodeLiteralScan LiteralScan,
    DeadCodeArtifact Artifact);

/// <summary>
/// Reads a julie-extract v4 artifact and produces the FINAL <see cref="DeadCodeCandidateReport"/> for
/// <c>miller references candidates</c>. The reader owns everything I/O-shaped: the schema gate, the required-table
/// validation, the ancestor-closure walks, the four inbound-evidence counts, the per-language coverage universe,
/// and the two-phase literal scan. <see cref="DeadCodeCandidates"/> (Miller.Core) owns the pure decision logic.
/// </summary>
public static class DeadCodeCandidateReader
{
    // JulieSchemaGate only gates metadata VALUES, not table presence. A v4-stamped artifact missing the resolution
    // overlay would otherwise throw a raw SqliteException (CLI exit 1); these three tables are hard requirements
    // for the analysis, so their absence is an incompatible-extract condition (CLI exit 3).
    private static readonly string[] RequiredResolutionTables =
        ["identifier_resolutions", "pending_resolutions", "pending_relationships"];

    /// <summary>
    /// Open <paramref name="symbolsDbPath"/> read-only, gate + validate it, gather the candidate rows and coverage,
    /// evaluate, run the literal scan over the survivors (re-reading source under <paramref name="workspaceRoot"/>
    /// with a blake3 freshness guard), and return the finished report.
    /// </summary>
    public static DeadCodeCandidateReport Read(string symbolsDbPath, string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        using SqliteConnection connection = SqliteReadOnlyAccess.Open(symbolsDbPath);
        JulieSchemaGate.Verify(connection);
        RequireResolutionTables(connection);

        // Closure inputs over ALL symbols (small): the parent map + the is_test / structural-fact / annotation sets.
        var parent = new Dictionary<string, string?>(StringComparer.Ordinal);
        var isTest = new HashSet<string>(StringComparer.Ordinal);
        LoadSymbolClosureInputs(connection, parent, isTest);

        var structuralFactSymbols = LoadDistinctIds(connection,
            "SELECT DISTINCT containing_symbol_id FROM structural_facts WHERE containing_symbol_id IS NOT NULL;");
        var annotatedSymbols = LoadDistinctIds(connection, "SELECT DISTINCT symbol_id FROM symbol_annotations;");

        var rows = LoadCandidateRows(connection, parent, isTest, structuralFactSymbols, annotatedSymbols);
        var coverage = LoadCoverage(connection);

        var result = DeadCodeCandidates.Evaluate(rows, coverage);
        var (finalResult, literalScan) = RunLiteralScan(connection, workspaceRoot, result);
        var artifact = ReadArtifact(connection);

        return new DeadCodeCandidateReport(finalResult, coverage, literalScan, artifact);
    }

    // ---- required-table validation ---------------------------------------------------------------------------

    private static void RequireResolutionTables(SqliteConnection connection)
    {
        foreach (var table in RequiredResolutionTables)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1;";
            cmd.Parameters.AddWithValue("$n", table);
            if (cmd.ExecuteScalar() is null)
                throw new IncompatibleExtractException(
                    $"DB has no '{table}' table; it is not a schema-{MillerExtractContract.ExpectedSchemaVersion} " +
                    $"julie-extract artifact with workspace reference resolution. Re-run restore + `scan` with the " +
                    $"pinned julie-extract (v{MillerExtractContract.PinnedJulieExtractVersion}).");
        }
    }

    // ---- closure inputs --------------------------------------------------------------------------------------

    private static void LoadSymbolClosureInputs(
        SqliteConnection connection, Dictionary<string, string?> parent, HashSet<string> isTest)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT symbol_id, parent_symbol_id, is_test FROM symbols;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string id = reader.GetString(0);
            parent[id] = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (!reader.IsDBNull(2) && reader.GetInt64(2) != 0)
                isTest.Add(id);
        }
    }

    private static HashSet<string> LoadDistinctIds(SqliteConnection connection, string sql)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0))
                set.Add(reader.GetString(0));
        return set;
    }

    /// <summary>Walk self → parent → … testing membership in <paramref name="set"/>; cycle-safe via a visited set.</summary>
    private static bool SelfOrAncestorInSet(
        string id, IReadOnlyDictionary<string, string?> parent, IReadOnlySet<string> set)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? current = id;
        while (current is not null && seen.Add(current))
        {
            if (set.Contains(current))
                return true;
            if (!parent.TryGetValue(current, out current))
                break;
        }

        return false;
    }

    // ---- candidate rows --------------------------------------------------------------------------------------

    private readonly record struct CandidateTuple(
        string SymbolId, string FileId, string Path, string Language, string Name, string Kind,
        string? Visibility, int StartLine, long? StartByte, long? EndByte, string? Signature);

    private static List<DeadCodeSymbolRow> LoadCandidateRows(
        SqliteConnection connection,
        IReadOnlyDictionary<string, string?> parent,
        IReadOnlySet<string> isTest,
        IReadOnlySet<string> structuralFactSymbols,
        IReadOnlySet<string> annotatedSymbols)
    {
        // Read every candidate-kind symbol into memory FIRST (closing the reader) so the per-symbol count
        // subqueries below can run on the same connection without a live reader open.
        var tuples = LoadCandidateTuples(connection);

        // Four reusable, parameterized count commands — per-symbol INDEXED subqueries (never materialize all
        // identifiers). The inside-S test treats a NULL span / NULL containing symbol as "not inside" (COALESCE→0).
        using var nameCmd = CreateCountCommand(connection, """
            SELECT COUNT(*) FROM identifiers i
            WHERE i.name = $name
              AND COALESCE(i.containing_symbol_id = $sid, 0) = 0
              AND COALESCE((i.file_id = $fid AND i.start_byte >= $sb AND i.end_byte <= $eb), 0) = 0;
            """, includeName: true);
        using var resolvedCmd = CreateCountCommand(connection, """
            SELECT COUNT(*) FROM identifier_resolutions ir
            JOIN identifiers i ON i.identifier_id = ir.identifier_id
            WHERE ir.target_symbol_id = $sid
              AND COALESCE(i.containing_symbol_id = $sid, 0) = 0
              AND COALESCE((i.file_id = $fid AND i.start_byte >= $sb AND i.end_byte <= $eb), 0) = 0;
            """, includeName: false);
        using var pendingCmd = CreateCountCommand(connection, """
            SELECT COUNT(*) FROM pending_resolutions pr
            JOIN pending_relationships p ON p.pending_relationship_id = pr.pending_relationship_id
            WHERE pr.target_symbol_id = $sid
              AND COALESCE(p.caller_scope_symbol_id = $sid, 0) = 0
              AND COALESCE((p.file_id = $fid AND p.start_byte >= $sb AND p.end_byte <= $eb), 0) = 0;
            """, includeName: false);
        using var callsCmd = CreateCountCommand(connection, """
            SELECT COUNT(*) FROM relationships r
            WHERE r.to_symbol_id = $sid AND r.from_symbol_id <> $sid;
            """, includeName: false);

        var rows = new List<DeadCodeSymbolRow>(tuples.Count);
        foreach (var t in tuples)
        {
            object sb = (object?)t.StartByte ?? DBNull.Value;
            object eb = (object?)t.EndByte ?? DBNull.Value;

            BindCountParams(nameCmd, t.SymbolId, t.FileId, sb, eb, t.Name);
            BindCountParams(resolvedCmd, t.SymbolId, t.FileId, sb, eb, null);
            BindCountParams(pendingCmd, t.SymbolId, t.FileId, sb, eb, null);
            BindCountParams(callsCmd, t.SymbolId, t.FileId, sb, eb, null);

            parent.TryGetValue(t.SymbolId, out string? parentSymbolId);

            rows.Add(new DeadCodeSymbolRow(
                SymbolId: t.SymbolId,
                Name: t.Name,
                Kind: t.Kind,
                Language: t.Language,
                Path: t.Path,
                StartLine: t.StartLine,
                StartByte: t.StartByte ?? 0L,
                EndByte: t.EndByte ?? 0L,
                Visibility: t.Visibility,
                IsTestSelfOrAncestor: SelfOrAncestorInSet(t.SymbolId, parent, isTest),
                ParentSymbolId: parentSymbolId,
                HasAnnotation: annotatedSymbols.Contains(t.SymbolId),
                HasStructuralFactSelfOrAncestor: SelfOrAncestorInSet(t.SymbolId, parent, structuralFactSymbols),
                IsOverrideMember: DeadCodeCandidates.IsOverrideSignature(t.Signature),
                NameMatchesOutside: ExecCount(nameCmd),
                ResolvedInbound: ExecCount(resolvedCmd),
                PendingResolvedInbound: ExecCount(pendingCmd),
                CallsInbound: ExecCount(callsCmd),
                LiteralMatch: null));
        }

        return rows;
    }

    private static List<CandidateTuple> LoadCandidateTuples(SqliteConnection connection)
    {
        var kinds = DeadCodeCandidates.CandidateKinds.ToArray();
        var placeholders = new string[kinds.Length];

        using var cmd = connection.CreateCommand();
        for (int i = 0; i < kinds.Length; i++)
        {
            placeholders[i] = "$k" + i.ToString(CultureInfo.InvariantCulture);
            cmd.Parameters.AddWithValue(placeholders[i], kinds[i]);
        }

        cmd.CommandText = $"""
            SELECT symbol_id, file_id, path, language, name, kind, visibility, start_line, start_byte, end_byte,
                   signature
            FROM symbols
            WHERE kind IN ({string.Join(", ", placeholders)})
            ORDER BY path, start_line, symbol_id;
            """;

        var tuples = new List<CandidateTuple>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tuples.Add(new CandidateTuple(
                SymbolId: reader.GetString(0),
                FileId: reader.GetString(1),
                Path: reader.GetString(2),
                Language: reader.GetString(3),
                Name: reader.GetString(4),
                Kind: reader.GetString(5),
                Visibility: reader.IsDBNull(6) ? null : reader.GetString(6),
                StartLine: reader.IsDBNull(7) ? 0 : (int)reader.GetInt64(7),
                StartByte: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                EndByte: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                Signature: reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return tuples;
    }

    private static SqliteCommand CreateCountCommand(SqliteConnection connection, string sql, bool includeName)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add("$sid", SqliteType.Text);
        cmd.Parameters.Add("$fid", SqliteType.Text);
        cmd.Parameters.Add("$sb", SqliteType.Integer);
        cmd.Parameters.Add("$eb", SqliteType.Integer);
        if (includeName)
            cmd.Parameters.Add("$name", SqliteType.Text);
        return cmd;
    }

    private static void BindCountParams(
        SqliteCommand cmd, string symbolId, string fileId, object startByte, object endByte, string? name)
    {
        cmd.Parameters["$sid"].Value = symbolId;
        cmd.Parameters["$fid"].Value = fileId;
        cmd.Parameters["$sb"].Value = startByte;
        cmd.Parameters["$eb"].Value = endByte;
        if (name is not null)
            cmd.Parameters["$name"].Value = name;
    }

    private static int ExecCount(SqliteCommand cmd) =>
        Convert.ToInt32(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);

    // ---- coverage universe -----------------------------------------------------------------------------------

    private static List<LanguageCoverageRow> LoadCoverage(SqliteConnection connection)
    {
        // Universe = UNION of languages in symbols AND files (NOT the identifiers table alone) — a language with
        // symbols but zero identifiers (css/html) MUST appear so Core's low_evidence_language rule can fire.
        var languages = new SortedSet<string>(StringComparer.Ordinal);
        AddLanguages(connection, "SELECT DISTINCT language FROM symbols;", languages);
        AddLanguages(connection, "SELECT DISTINCT language FROM files;", languages);

        var identifierCounts = LoadCountsByLanguage(connection,
            "SELECT language, COUNT(*) FROM identifiers GROUP BY language;");
        var resolvedCounts = LoadCountsByLanguage(connection, """
            SELECT i.language, COUNT(*)
            FROM identifier_resolutions ir
            JOIN identifiers i ON i.identifier_id = ir.identifier_id
            WHERE ir.outcome = 'resolved'
            GROUP BY i.language;
            """);

        var rows = new List<LanguageCoverageRow>(languages.Count);
        foreach (var language in languages)
            rows.Add(new LanguageCoverageRow(
                language,
                identifierCounts.GetValueOrDefault(language),
                resolvedCounts.GetValueOrDefault(language)));

        return rows;
    }

    private static void AddLanguages(SqliteConnection connection, string sql, SortedSet<string> languages)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0))
                languages.Add(reader.GetString(0));
    }

    private static Dictionary<string, int> LoadCountsByLanguage(SqliteConnection connection, string sql)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0))
                counts[reader.GetString(0)] = (int)reader.GetInt64(1);
        return counts;
    }

    // ---- literal scan (phase 2) ------------------------------------------------------------------------------

    private readonly record struct LiteralRegion(
        string Path, int StartByte, int EndByte, string ContentHash, long ContentBytes);

    private static (DeadCodeResult Result, DeadCodeLiteralScan Scan) RunLiteralScan(
        SqliteConnection connection, string workspaceRoot, DeadCodeResult result)
    {
        // Only runs when provisional candidates survive; skip the disk reads entirely otherwise.
        if (result.NeedsLiteralScan.Count == 0)
            return (result, new DeadCodeLiteralScan(0, 0));

        var nameToSymbolIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in result.NeedsLiteralScan)
        {
            if (!nameToSymbolIds.TryGetValue(row.Name, out var ids))
                nameToSymbolIds[row.Name] = ids = [];
            ids.Add(row.SymbolId);
        }

        var regions = ReadStringLiteralRegions(connection);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        int filesScanned = 0;
        int filesSkippedStale = 0;
        // path → verified raw bytes (read + hashed ONCE per file). Regions slice these bytes directly: the
        // artifact's span offsets are byte offsets into exactly the bytes the hash covered, so slicing raw bytes
        // avoids the per-region full-file UTF-8 re-encode the old text-based slice paid (regions × file-size CPU;
        // adversarial-review finding on PR #5).
        var fileBytes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);

        foreach (var region in regions)
        {
            if (!fileBytes.TryGetValue(region.Path, out byte[]? bytes))
            {
                bytes = ReadVerifiedFileBytes(workspaceRoot, region);
                fileBytes[region.Path] = bytes;
                if (bytes is null)
                    filesSkippedStale++;
                else
                    filesScanned++;
            }

            if (bytes is null)
                continue;

            if (region.StartByte < 0 || region.EndByte <= region.StartByte || region.StartByte >= bytes.Length)
                continue;

            int end = Math.Min(region.EndByte, bytes.Length);
            // Invalid UTF-8 inside the slice decodes to U+FFFD, which can never match a candidate name — same
            // conservative outcome as the old skip-undecodable-file behavior, scoped per region.
            string literal = Encoding.UTF8.GetString(bytes, region.StartByte, end - region.StartByte);

            foreach (var (name, ids) in nameToSymbolIds)
                if (literal.Contains(name, StringComparison.Ordinal))
                    foreach (var id in ids)
                        matched.Add(id);
        }

        return (DeadCodeCandidates.ApplyLiteralScan(result, matched),
            new DeadCodeLiteralScan(filesScanned, filesSkippedStale));
    }

    private static List<LiteralRegion> ReadStringLiteralRegions(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        // ORDER BY path so every literal-bearing file's regions are contiguous (read once, per the fileText cache).
        cmd.CommandText = """
            SELECT sr.path, sr.start_byte, sr.end_byte, f.content_hash, f.content_bytes
            FROM source_regions sr
            JOIN files f ON f.file_id = sr.file_id
            WHERE sr.kind = 'string_literal'
            ORDER BY sr.path, sr.start_byte, sr.source_region_id;
            """;

        var regions = new List<LiteralRegion>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            regions.Add(new LiteralRegion(
                Path: reader.GetString(0),
                StartByte: (int)reader.GetInt64(1),
                EndByte: (int)reader.GetInt64(2),
                ContentHash: reader.GetString(3),
                ContentBytes: reader.GetInt64(4)));
        }

        return regions;
    }

    /// <summary>
    /// The freshness-guarded source re-read mirrored from <c>SearchIndexWriter.ReadVerifiedFileText</c>: resolve
    /// under the workspace root, require the on-disk byte length AND blake3 to match the artifact's stored facts.
    /// Returns the RAW verified bytes (the artifact's span offsets index these bytes exactly); null (⇒ STALE /
    /// missing) on any mismatch — a stale file never suppresses a candidate.
    /// </summary>
    private static byte[]? ReadVerifiedFileBytes(string workspaceRoot, LiteralRegion region)
    {
        try
        {
            string? abs = WorkspaceRelativePath.ResolveUnderRoot(workspaceRoot, region.Path);
            if (abs is null || !File.Exists(abs))
                return null;

            byte[] bytes = File.ReadAllBytes(abs);
            if (bytes.LongLength != region.ContentBytes)
                return null;
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    ContentHasher.Blake3Hex(bytes), ContentHasher.NormalizeHash(region.ContentHash)))
                return null;

            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // ---- artifact block --------------------------------------------------------------------------------------

    private static DeadCodeArtifact ReadArtifact(SqliteConnection connection) => new(
        ArtifactId: ReadMetadataValue(connection, "artifact_id"),
        Revision: ReadMaxRevision(connection),
        ReferenceResolutionStatus: ReadMetadataValue(connection, "reference_resolution_status") ?? "unknown",
        ReferenceResolutionVersion: ReadMetadataValue(connection, "reference_resolution_version"));

    private static string? ReadMetadataValue(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM artifact_metadata WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);
        object? value = cmd.ExecuteScalar();
        return value is string text ? text : null;
    }

    private static long? ReadMaxRevision(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(revision_id) FROM extraction_revisions;";
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
