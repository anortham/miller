using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the subprocess wrapper WITHOUT spawning julie-extract (the live path is the Scale suite). Four seams
/// are exercised in isolation: (1) the argv builders produce the exact verified v1 command line (top-level
/// subcommand, no <c>extract</c> parent token, no <c>--workspace-id</c>, with <c>--strict-schema --json</c>,
/// and a mandatory <c>--jobs</c> cap on every scan);
/// (2) the report parser deserializes real-shaped nested v1 JSON onto the convenience accessors; (3) the
/// exit-code→outcome mapping (0 success / 1 partial-or-failed / 2 usage / 3 incompatible / else base) throws
/// the right typed exception; (4) the post-extract version cross-check gates on <c>report.artifact.*</c>.
/// </summary>
public sealed class JulieExtractRunnerTests
{
    private const string AbsDb = "/abs/work/.miller/symbols.db";
    private const string AbsRoot = "/abs/work/repo";
    private const string AbsFile = "/abs/work/repo/src/a.cs";

    // ---- (1) argv builders (v1 shape) ----

    [Fact]
    public void BuildScanArgs_ProducesV1Argv_NoExtractToken_NoWorkspaceId_StrictSchema()
    {
        var args = JulieExtractRunner.BuildScanArgs(AbsDb, AbsRoot, force: false, jobs: 4);
        Assert.Equal(
            new[] { "scan", "--root", AbsRoot, "--db", AbsDb, "--strict-schema", "--json", "--jobs", "4" },
            args);
        Assert.DoesNotContain("extract", args);
        Assert.DoesNotContain("--workspace-id", args);
    }

    [Fact]
    public void BuildScanArgs_Force_AppendsForceFlag()
    {
        var args = JulieExtractRunner.BuildScanArgs(AbsDb, AbsRoot, force: true, jobs: 4);
        Assert.Equal(
            new[] { "scan", "--root", AbsRoot, "--db", AbsDb, "--strict-schema", "--json", "--jobs", "4", "--force" },
            args);
    }

    [Fact]
    public void BuildScanArgs_SymbolsLevel_AppendsTheLevelFlag()
    {
        var args = JulieExtractRunner.BuildScanArgs(
            AbsDb, AbsRoot, force: false, jobs: 4, level: ExtractIndexLevel.Symbols);
        Assert.Equal(
            new[]
            {
                "scan", "--root", AbsRoot, "--db", AbsDb, "--strict-schema", "--json", "--jobs", "4",
                "--level", "symbols",
            },
            args);
    }

    [Fact]
    public void BuildScanArgs_FullLevel_EmitsNoLevelFlag_ArgvStaysByteIdenticalToPreLevels()
    {
        var args = JulieExtractRunner.BuildScanArgs(
            AbsDb, AbsRoot, force: true, jobs: 4, level: ExtractIndexLevel.Full);
        Assert.DoesNotContain("--level", args);
        Assert.Equal(
            JulieExtractRunner.BuildScanArgs(AbsDb, AbsRoot, force: true, jobs: 4),
            args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildScanArgs_AlwaysCarriesJobs(bool force)
    {
        var args = JulieExtractRunner.BuildScanArgs(AbsDb, AbsRoot, force, jobs: 2);
        Assert.Contains("--jobs", args);
    }

    [Fact]
    public void BuildScanArgs_ZeroJobs_PassesRayonAutoThrough()
    {
        var args = JulieExtractRunner.BuildScanArgs(AbsDb, AbsRoot, force: false, jobs: ExtractJobsPolicy.RayonAuto);
        Assert.Equal(
            new[] { "scan", "--root", AbsRoot, "--db", AbsDb, "--strict-schema", "--json", "--jobs", "0" },
            args);
    }

    [Fact]
    public void BuildScanArgs_NegativeJobs_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => JulieExtractRunner.BuildScanArgs(AbsDb, AbsRoot, force: false, jobs: -1));

    [Fact]
    public void BuildScanArgs_EmitsIgnoreFilesInOrder_SoTheLastOnePassedWins()
    {
        var args = JulieExtractRunner.BuildScanArgs(
            AbsDb, AbsRoot, force: false, jobs: 4,
            ignoreFiles: new[] { "/abs/main/.julieignore", "/abs/work/.miller/invariant.julieignore" });

        Assert.Equal(
            new[]
            {
                "scan", "--root", AbsRoot, "--db", AbsDb, "--strict-schema", "--json", "--jobs", "4",
                "--ignore-file", "/abs/main/.julieignore",
                "--ignore-file", "/abs/work/.miller/invariant.julieignore",
            },
            args);
    }

    [Fact]
    public void BuildScanArgs_ForceStillTrailsTheIgnoreFiles()
    {
        var args = JulieExtractRunner.BuildScanArgs(
            AbsDb, AbsRoot, force: true, jobs: 4,
            ignoreFiles: new[] { "/abs/work/.miller/invariant.julieignore" });

        Assert.Equal("--force", args[^1]);
        Assert.Equal("/abs/work/.miller/invariant.julieignore", args[^2]);
    }

    [Fact]
    public void BuildScanArgs_NoIgnoreFiles_EmitsNoIgnoreFileFlag()
    {
        var args = JulieExtractRunner.BuildScanArgs(
            AbsDb, AbsRoot, force: false, jobs: 4, ignoreFiles: Array.Empty<string>());

        Assert.DoesNotContain("--ignore-file", args);
    }

    [Fact]
    public void BuildScanArgs_BlankIgnoreFile_Throws() =>
        Assert.Throws<ArgumentException>(
            () => JulieExtractRunner.BuildScanArgs(
                AbsDb, AbsRoot, force: false, jobs: 4, ignoreFiles: new[] { "  " }));

    [Fact]
    public void BuildInfoArgs_TopLevel_NoRoot_StrictSchema()
    {
        var args = JulieExtractRunner.BuildInfoArgs(AbsDb);
        Assert.Equal(new[] { "info", "--db", AbsDb, "--strict-schema", "--json" }, args);
        Assert.DoesNotContain("--root", args);
        Assert.DoesNotContain("extract", args);
    }

    [Fact]
    public void BuildUpdateArgs_ProducesV1Argv_FileBeforeStrictSchema()
    {
        var args = JulieExtractRunner.BuildUpdateArgs(AbsDb, AbsRoot, AbsFile);
        Assert.Equal(
            new[] { "update", "--root", AbsRoot, "--db", AbsDb, "--file", AbsFile, "--strict-schema", "--json" },
            args);
        Assert.DoesNotContain("extract", args);
    }

    [Fact]
    public void BuildLanguagesArgs_CapabilitySnapshot_NoDbNoRoot()
    {
        var args = JulieExtractRunner.BuildLanguagesArgs();
        Assert.Equal(new[] { "languages", "--json" }, args);
    }

    // ---- supported-extension parsing (the watcher gate's catalog; pure — the live probe is Scale) ----

    private const string LanguagesJson = """
        { "report_schema_version": 3, "status": "ok", "operation": "languages", "mode": "capability_snapshot",
          "languages": { "languages": [
            { "language": "rust",   "extensions": ["rs"] },
            { "language": "c",      "extensions": ["c", "h"] },
            { "language": "r",      "extensions": ["r", "R"] },
            { "language": "dotted", "extensions": [".tsx", ""] }
          ] } }
        """;

    [Fact]
    public void ParseSupportedExtensions_FlattensAllLanguages_LowercaseDotless_CaseInsensitiveSet()
    {
        IReadOnlySet<string> set = JulieExtractRunner.ParseSupportedExtensions(LanguagesJson);

        // Derived purely from the JSON: every claimed extension, normalized (leading dot stripped,
        // lowercased), under a case-insensitive comparer; the empty entry is dropped, "R"/"r" collapse.
        Assert.Equal(5, set.Count);
        Assert.Contains("rs", set);
        Assert.Contains("c", set);
        Assert.Contains("h", set);
        Assert.Contains("r", set);
        Assert.Contains("tsx", set);
        Assert.Contains("TSX", set); // comparer, not data
    }

    [Fact]
    public void ParseSupportedExtensions_MissingShape_ReturnsEmpty_NotThrow()
    {
        // A report without the languages block (or non-object/array shapes) parses to an EMPTY set; the live
        // caller maps empty → null so the gate fails soft rather than dropping everything.
        Assert.Empty(JulieExtractRunner.ParseSupportedExtensions("""{ "status": "ok" }"""));
        Assert.Empty(JulieExtractRunner.ParseSupportedExtensions("""{ "languages": { "languages": [] } }"""));
        Assert.Empty(JulieExtractRunner.ParseSupportedExtensions("""{ "languages": 42 }"""));
    }

    [Fact]
    public void ParseSupportedExtensions_InvalidJson_Throws()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => JulieExtractRunner.ParseSupportedExtensions("not json"));
    }

    // ---- (2) report parser (nested v1) ----

    private const string ScanSuccessJson = """
        { "report_schema_version": 1, "status": "ok", "operation": "scan", "mode": "incremental",
          "input": { "db_path": "/abs/work/.miller/symbols.db", "root_path": "/abs/work/repo", "file_path": null,
                     "root_relative_path": null, "format": null, "output_path": null },
          "artifact": { "db_path": "/abs/work/.miller/symbols.db", "root_path": "/abs/work/repo", "artifact_id": "art-1",
                        "schema_version": 1, "extract_contract_version": 1, "sqlite_schema_version": 1,
                        "jsonl_schema_version": 1, "hash_algorithm": "blake3",
                        "parser_inventory_fingerprint": "sha256:pi", "capability_snapshot_fingerprint": "sha256:cs" },
          "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": { "latest_revision_id": 3, "created_revision_id": 3 },
          "counts": { "files_scanned": 12, "files_changed": 12, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 0,
                      "rows_written": { "symbols": 134 },
                      "totals": { "files": 12, "symbols": 134, "relationships": 40, "identifiers": 512 } },
          "errors": [], "warnings": [] }
        """;

    [Fact]
    public void ParseReport_ScanSuccess_MapsAccessorsFromNestedModel()
    {
        var report = JulieExtractRunner.ParseReport(ScanSuccessJson);

        Assert.Equal("ok", report.Status);
        Assert.Equal("scan", report.Operation);
        Assert.Equal("blake3", report.HashAlgorithm);          // artifact.hash_algorithm
        Assert.Equal(12u, report.FilesScanned);                // counts.files_scanned
        Assert.Equal(134u, report.SymbolsExtracted);           // counts.rows_written.symbols
        Assert.Equal(3L, report.Revision);                     // revision.latest_revision_id
        Assert.Empty(report.Errors);
    }

    private const string InfoJson = """
        { "report_schema_version": 1, "status": "ok", "operation": "info", "mode": "read_only",
          "input": { "db_path": "/abs/work/.miller/symbols.db", "root_path": null, "file_path": null,
                     "root_relative_path": null, "format": null, "output_path": null },
          "artifact": { "db_path": "/abs/work/.miller/symbols.db", "root_path": "/abs/work/repo", "artifact_id": "art-1",
                        "schema_version": 1, "extract_contract_version": 1, "sqlite_schema_version": 1,
                        "jsonl_schema_version": 1, "hash_algorithm": "blake3",
                        "parser_inventory_fingerprint": "sha256:pi", "capability_snapshot_fingerprint": "sha256:cs" },
          "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": { "latest_revision_id": 3, "created_revision_id": null },
          "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 0, "rows_written": {},
                      "totals": { "files": 12, "symbols": 134, "relationships": 40, "identifiers": 512 } },
          "errors": [], "warnings": [] }
        """;

    [Fact]
    public void ParseReport_Info_CountsLandInTotals_NotPerOp()
    {
        var report = JulieExtractRunner.ParseReport(InfoJson);

        // info reuses the same shape; the per-op counters are 0, the inventory lives in counts.totals.
        Assert.Equal(0u, report.FilesScanned);
        Assert.Equal(0u, report.SymbolsExtracted);
        Assert.Equal(12u, report.FilesTotal);
        Assert.Equal(134u, report.SymbolsTotal);
        Assert.Equal(40u, report.RelationshipsTotal);
        Assert.Equal(512u, report.IdentifiersTotal);
    }

    private const string FailedJson = """
        { "report_schema_version": 1, "status": "failed", "operation": "scan", "mode": "incremental",
          "input": { "db_path": "/abs/work/.miller/symbols.db", "root_path": "/abs/work/repo", "file_path": null,
                     "root_relative_path": null, "format": null, "output_path": null },
          "artifact": null, "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": null,
          "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 1, "rows_written": {}, "totals": {} },
          "errors": [ { "code": "root_mismatch", "message": "root differs from the bound artifact root; use --force",
                        "path": "/abs/work/repo", "root_relative_path": null, "recoverable": false, "details": {} } ],
          "warnings": [] }
        """;

    [Fact]
    public void ParseReport_Failed_DeserializesDiagnostics()
    {
        var report = JulieExtractRunner.ParseReport(FailedJson);

        Assert.Equal("failed", report.Status);
        var err = Assert.Single(report.Errors);
        Assert.Equal("root_mismatch", err.Code);
        Assert.Contains("--force", err.Message);
        Assert.Equal("/abs/work/repo", err.Path);
        Assert.False(err.Recoverable);
    }

    [Fact]
    public void ParseReport_BindsKeysCaseInsensitively_ProvingPropertyNameCaseInsensitive()
    {
        // Keys differ from the wire form ONLY by case. They bind iff PropertyNameCaseInsensitive is set on the
        // runner's JsonSerializerOptions; drop that flag and these fields would deserialize to their defaults.
        const string mixedCaseJson = """
            { "Report_Schema_Version": 1, "Status": "ok", "operation": "scan", "Mode": "incremental",
              "Artifact": { "Db_Path": "/abs/db", "Root_Path": "/abs/r", "Artifact_Id": "a",
                            "Schema_Version": 1, "Extract_Contract_Version": 1, "Sqlite_Schema_Version": 1,
                            "Jsonl_Schema_Version": 1, "Hash_Algorithm": "blake3",
                            "Parser_Inventory_Fingerprint": "sha256:p", "Capability_Snapshot_Fingerprint": "sha256:c" },
              "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
              "Revision": { "Latest_Revision_Id": 4, "Created_Revision_Id": 4 },
              "errors": [], "warnings": [] }
            """;

        var report = JulieExtractRunner.ParseReport(mixedCaseJson);

        Assert.Equal("ok", report.Status);            // "Status" bound case-insensitively
        Assert.Equal("blake3", report.HashAlgorithm); // "Hash_Algorithm" bound to artifact.hash_algorithm
        Assert.Equal(4L, report.Revision);            // "Latest_Revision_Id" bound
        Assert.Equal(1, report.ReportSchemaVersion);  // "Report_Schema_Version" bound
    }

    // ---- (3) exit-code -> outcome mapping (fake process result, no live process) ----

    [Fact]
    public void Interpret_Exit0_ReturnsParsedReport()
    {
        var report = JulieExtractRunner.Interpret(exitCode: 0, stdout: ScanSuccessJson, stderr: "");

        Assert.Equal("ok", report.Status);
        Assert.Equal(134u, report.SymbolsExtracted);
    }

    [Fact]
    public void Interpret_Exit3_SchemaIncompatible_ThrowsIncompatibleExtract_FromErrorCode()
    {
        const string incompatible = """
            { "report_schema_version": 1, "status": "failed", "operation": "info", "mode": "read_only",
              "input": { "db_path": "/abs/db", "root_path": null, "file_path": null,
                         "root_relative_path": null, "format": null, "output_path": null },
              "artifact": null, "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
              "revision": null,
              "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                          "files_deleted": 0, "files_failed": 0, "rows_written": {}, "totals": {} },
              "errors": [ { "code": "schema_incompatible", "message": "artifact schema version is newer than this binary supports",
                            "path": null, "root_relative_path": null, "recoverable": false, "details": {} } ],
              "warnings": [] }
            """;
        var ex = Assert.Throws<IncompatibleExtractException>(() =>
            JulieExtractRunner.Interpret(exitCode: 3, stdout: incompatible, stderr: ""));
        Assert.Contains("schema_incompatible", ex.Message);
    }

    [Fact]
    public void Interpret_Exit3_RootMismatch_ThrowsIncompatibleExtract()
    {
        const string rootMismatch = """
            { "report_schema_version": 1, "status": "failed", "operation": "scan", "mode": "incremental",
              "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": null,
                         "root_relative_path": null, "format": null, "output_path": null },
              "artifact": null, "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
              "revision": null,
              "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                          "files_deleted": 0, "files_failed": 0, "rows_written": {}, "totals": {} },
              "errors": [ { "code": "root_mismatch", "message": "artifact root does not match requested root",
                            "path": "/abs/db", "root_relative_path": null, "recoverable": false, "details": {} } ],
              "warnings": [] }
            """;
        var ex = Assert.Throws<IncompatibleExtractException>(() =>
            JulieExtractRunner.Interpret(exitCode: 3, stdout: rootMismatch, stderr: ""));
        Assert.Contains("root_mismatch", ex.Message);
    }

    [Fact]
    public void Interpret_Exit3_UnparseableStdout_StillThrowsIncompatible_CarryingStderr()
    {
        var ex = Assert.Throws<IncompatibleExtractException>(() =>
            JulieExtractRunner.Interpret(exitCode: 3, stdout: "not json", stderr: "boom"));
        Assert.Contains("boom", ex.Message);  // never a silent pass
    }

    [Fact]
    public void Interpret_Exit1_PathError_StaysOperationFailure_NotIncompatible()
    {
        // FileOutsideRoot is exit 1 in v1 (commands.rs path policy), so it surfaces as a FAILED op, not an
        // incompatible-schema gate. Branch on errors[].code semantics, not the exit code alone.
        const string outsideRoot = """
            { "report_schema_version": 1, "status": "failed", "operation": "update", "mode": "single_file",
              "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": "/x",
                         "root_relative_path": null, "format": null, "output_path": null },
              "artifact": null, "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
              "revision": null,
              "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                          "files_deleted": 0, "files_failed": 1, "rows_written": {}, "totals": {} },
              "errors": [ { "code": "file_outside_root", "message": "file is outside external extract root",
                            "path": "/x", "root_relative_path": null, "recoverable": false, "details": {} } ],
              "warnings": [] }
            """;
        var ex = Assert.Throws<JulieExtractFailedException>(() =>
            JulieExtractRunner.Interpret(exitCode: 1, stdout: outsideRoot, stderr: ""));
        Assert.Equal("file_outside_root", Assert.Single(ex.Errors).Code);
    }

    [Fact]
    public void Interpret_Exit1_Partial_ReturnsReport_ConsistentArtifactPreserved()
    {
        // A partial scan (some files failed to parse) carries a CONSISTENT artifact and exits 1; bootstrap must
        // load the usable rows, so Interpret RETURNS the report (the caller WARN-logs files_failed + errors[]).
        const string partial = """
            { "report_schema_version": 1, "status": "partial", "operation": "scan", "mode": "incremental",
              "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": null,
                         "root_relative_path": null, "format": null, "output_path": null },
              "artifact": { "db_path": "/abs/db", "root_path": "/abs/r", "artifact_id": "a", "schema_version": 1,
                            "extract_contract_version": 1, "sqlite_schema_version": 1, "jsonl_schema_version": 1,
                            "hash_algorithm": "blake3", "parser_inventory_fingerprint": "sha256:p",
                            "capability_snapshot_fingerprint": "sha256:c" },
              "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
              "revision": { "latest_revision_id": 5, "created_revision_id": 5 },
              "counts": { "files_scanned": 10, "files_changed": 9, "files_unchanged": 0, "files_unsupported": 0,
                          "files_deleted": 0, "files_failed": 1, "rows_written": { "symbols": 80 }, "totals": { "files": 10 } },
              "errors": [ { "code": "parse_failed", "message": "tree-sitter could not parse src/broken.cs",
                            "path": "/abs/r/src/broken.cs", "root_relative_path": "src/broken.cs", "recoverable": true, "details": {} } ],
              "warnings": [] }
            """;
        var report = JulieExtractRunner.Interpret(exitCode: 1, stdout: partial, stderr: "");
        Assert.Equal("partial", report.Status);
        Assert.Equal(5L, report.Revision);            // the consistent artifact's cursor
        Assert.Equal(1, report.Counts!.FilesFailed);  // the caller WARN-logs this
        Assert.Equal("parse_failed", Assert.Single(report.Errors).Code);
    }

    [Fact]
    public void Interpret_Exit1_FailedStatus_Throws_WithStructuredDiagnostics()
    {
        var ex = Assert.Throws<JulieExtractFailedException>(() =>
            JulieExtractRunner.Interpret(exitCode: 1, stdout: FailedJson, stderr: "extract: root mismatch"));

        var err = Assert.Single(ex.Errors);
        Assert.Equal("root_mismatch", err.Code);
        Assert.Equal("extract: root mismatch", ex.StandardError);
        Assert.Contains("root_mismatch", ex.Message); // message surfaces the error code for operators
    }

    [Fact]
    public void Interpret_Exit1_WithUnparseableStdout_StillThrowsFailed_CarryingStderr()
    {
        // Defensive: if exit is 1 but stdout isn't valid JSON, we still surface a failure (with stderr),
        // never a silent success.
        var ex = Assert.Throws<JulieExtractFailedException>(() =>
            JulieExtractRunner.Interpret(exitCode: 1, stdout: "not json", stderr: "boom"));
        Assert.Equal("boom", ex.StandardError);
    }

    [Fact]
    public void Interpret_Exit2_ThrowsUsage_FromStderrOnly_NoStdoutParse()
    {
        // exit 2 => clap usage text on STDERR, NO JSON on stdout. Must NOT attempt to parse stdout.
        const string usage = "error: the following required arguments were not provided:\n  --db <DB>";
        var ex = Assert.Throws<JulieExtractUsageException>(() =>
            JulieExtractRunner.Interpret(exitCode: 2, stdout: "", stderr: usage));

        Assert.Equal(usage, ex.StandardError);
        Assert.Contains("--db", ex.Message);
    }

    [Fact]
    public void Interpret_UnexpectedExitCode_ThrowsBaseExtractException()
    {
        var ex = Assert.Throws<JulieExtractException>(() =>
            JulieExtractRunner.Interpret(exitCode: 137, stdout: "", stderr: "killed"));
        Assert.Contains("137", ex.Message);
        Assert.IsType<JulieExtractException>(ex, exactMatch: true);  // base type, not a subclass
    }

    [Fact]
    public void Interpret_Exit135_PreservesTheUnknownCodeWithoutInferringOom()
    {
        var ex = Assert.Throws<JulieExtractException>(() =>
            JulieExtractRunner.Interpret(exitCode: 135, stdout: "", stderr: "terminated"));

        Assert.Equal(135, ex.ExitCode);
        Assert.Equal("terminated", ex.StandardError);
        Assert.Contains("135", ex.Message);
        Assert.DoesNotContain("OOM", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Interpret_DeleteNotFound_Exit0_IsTolerated_NotAFailure()
    {
        // `delete` of an absent file -> status "not_found", exit 0. Tolerant, NOT a failure.
        const string notFound = """
            { "report_schema_version": 1, "status": "not_found", "operation": "delete", "mode": "single_file",
              "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": "/abs/r/a.cs",
                         "root_relative_path": "a.cs", "format": null, "output_path": null },
              "artifact": { "db_path": "/abs/db", "root_path": "/abs/r", "artifact_id": "a", "schema_version": 1,
                            "extract_contract_version": 1, "sqlite_schema_version": 1, "jsonl_schema_version": 1,
                            "hash_algorithm": "blake3", "parser_inventory_fingerprint": "sha256:p",
                            "capability_snapshot_fingerprint": "sha256:c" },
              "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
              "revision": { "latest_revision_id": 8, "created_revision_id": null },
              "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                          "files_deleted": 0, "files_failed": 0, "rows_written": {}, "totals": {} },
              "errors": [], "warnings": [] }
            """;

        var report = JulieExtractRunner.Interpret(exitCode: 0, stdout: notFound, stderr: "");
        Assert.Equal("not_found", report.Status);
    }

    // ---- locator error path ----

    [Fact]
    public void Constructor_BinaryNotFound_ThrowsPointingAtRestoreScript()
    {
        string missing = Path.Combine(Path.GetTempPath(), "miller-no-julie-" + Guid.NewGuid().ToString("N"), "julie-extract");
        var ex = Assert.Throws<FileNotFoundException>(() => new JulieExtractRunner(missing));
        Assert.Contains("restore-julie-extract", ex.Message);
    }

    // ---- (4) post-extract version cross-check (D5/D7; gate on report.artifact.*) ----

    private static ExtractReport ReportWith(
        long? sqliteSchema, long? contract, string? hashAlgorithm = MillerExtractContract.ExpectedHashAlgorithm,
        bool withArtifact = true, int? reportSchemaVersion = (int)MillerExtractContract.ExpectedReportSchemaVersion)
    {
        // reportSchemaVersion defaults to the pinned-current envelope (so a julie re-pin needs no edits here);
        // pass an explicit value — including null — to force a mismatch (a null models an ABSENT version).
        ExtractArtifact? artifact = withArtifact
            ? new ExtractArtifact(
                DbPath: "/abs/db", RootPath: "/abs/r", ArtifactId: "a",
                SchemaVersion: sqliteSchema ?? MillerExtractContract.ExpectedSchemaVersion,
                ExtractContractVersion: contract ?? MillerExtractContract.ExpectedExtractContractVersion,
                SqliteSchemaVersion: sqliteSchema ?? MillerExtractContract.ExpectedSqliteSchemaVersion,
                JsonlSchemaVersion: 2, HashAlgorithm: hashAlgorithm!,
                ParserInventoryFingerprint: "p", CapabilitySnapshotFingerprint: "c")
            : null;
        return new ExtractReport(
            ReportSchemaVersion: reportSchemaVersion, Status: "ok", Operation: "scan", Mode: "force",
            Input: null, Artifact: artifact,
            Tool: new ExtractTool("julie-extract", "2.0.0"),
            RevisionBlock: new ExtractRevision(1, 1),
            Counts: null,
            Errors: Array.Empty<ReportDiagnostic>(), Warnings: Array.Empty<ReportDiagnostic>());
    }

    [Fact]
    public void VerifyReport_AtPinnedSchemaAndContract_DoesNotThrow() =>
        ExtractVersionMismatch.VerifyReport(ReportWith(
            MillerExtractContract.ExpectedSqliteSchemaVersion, MillerExtractContract.ExpectedExtractContractVersion));

    [Fact]
    public void VerifyReport_NewerSchema_ThrowsNamingValueAndPointingAtUpgrade()
    {
        var ex = Assert.Throws<IncompatibleExtractException>(() =>
            ExtractVersionMismatch.VerifyReport(ReportWith(
                MillerExtractContract.ExpectedSqliteSchemaVersion + 1, MillerExtractContract.ExpectedExtractContractVersion)));
        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("upgrade Miller", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyReport_OlderContract_ThrowsNamingValueAndPointingAtRebuild()
    {
        var ex = Assert.Throws<IncompatibleExtractException>(() =>
            ExtractVersionMismatch.VerifyReport(ReportWith(
                MillerExtractContract.ExpectedSqliteSchemaVersion, MillerExtractContract.ExpectedExtractContractVersion - 1)));
        Assert.Contains("extract_contract_version", ex.Message);
        Assert.Contains("workspace full", ex.Message, StringComparison.OrdinalIgnoreCase); // force-rebuild remedy
        Assert.Contains("restore", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The remedy must cover a stale-but-PRESENT binary (older than the pin), not only a missing one — the
        // "only if missing" wording misled a real diagnosis (a 2.1.x binary left in .tools after the 2.2.1 pin bump).
        Assert.Contains("older", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyReport_NullArtifact_FailsTheGate_NotASilentPass()
    {
        // An artifact-producing op MUST carry the artifact block; its absence is a contract failure.
        var ex = Assert.Throws<IncompatibleExtractException>(() =>
            ExtractVersionMismatch.VerifyReport(ReportWith(null, null, withArtifact: false)));
        Assert.Contains("artifact", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyReport_WrongHashAlgorithm_ThrowsNamingValueAndExpectedValue()
    {
        // null,null == pinned-current schema/contract, so the gate reaches the hash check (not an earlier mismatch).
        var ex = Assert.Throws<IncompatibleExtractException>(() =>
            ExtractVersionMismatch.VerifyReport(ReportWith(null, null, hashAlgorithm: "sha256")));
        Assert.Contains("hash_algorithm", ex.Message);
        Assert.Contains("sha256", ex.Message);
        Assert.Contains("blake3", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(2)]      // an older/incompatible report envelope (< the pinned-current 3)
    [InlineData(null)]   // absent report_schema_version
    public void VerifyReport_WrongOrMissingReportSchemaVersion_Throws(int? reportSchemaVersion)
    {
        // null,null == pinned-current schema/contract so the report-envelope check is what fails.
        var ex = Assert.Throws<IncompatibleExtractException>(() =>
            ExtractVersionMismatch.VerifyReport(ReportWith(null, null, reportSchemaVersion: reportSchemaVersion)));
        Assert.Contains("report_schema_version", ex.Message);
    }
}
