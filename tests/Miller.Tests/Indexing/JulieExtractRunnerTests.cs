using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the subprocess wrapper WITHOUT spawning julie-server (the live path is the Scale suite). Three seams
/// are exercised in isolation: (1) the argv builder produces the exact verified <c>extract</c> command line;
/// (2) the report parser deserializes real-shaped scan/info JSON into the flat records — including serde's
/// <c>db</c>→<c>db_path</c> rename and the <c>*_total</c> info counts; (3) the exit-code→outcome mapping
/// (0 success / 1 status:failed report / 2 stderr-only usage error) throws the right typed exception.
/// </summary>
public sealed class JulieExtractRunnerTests
{
    private const string AbsDb = "/abs/work/.miller/symbols.db";
    private const string AbsRoot = "/abs/work/repo";

    // ---- (1) argv builder ----

    [Fact]
    public void BuildScanArgs_ProducesVerifiedArgv_WithAbsolutePathsAndMandatoryJson()
    {
        var args = JulieExtractRunner.BuildScanArgs(AbsDb, AbsRoot, force: false);

        // Verified contract: extract --db <ABS_DB> --root <ABS_ROOT> --json scan
        Assert.Equal(
            new[] { "extract", "--db", AbsDb, "--root", AbsRoot, "--json", "scan" },
            args);
    }

    [Fact]
    public void BuildScanArgs_Force_AppendsForceFlagAfterSubcommand()
    {
        var args = JulieExtractRunner.BuildScanArgs(AbsDb, AbsRoot, force: true);

        Assert.Equal(
            new[] { "extract", "--db", AbsDb, "--root", AbsRoot, "--json", "scan", "--force" },
            args);
    }

    [Fact]
    public void BuildInfoArgs_OmitsRoot_AndIsJson()
    {
        // info opens read-only, takes NO flock, needs NO --root (safe under a live writer).
        var args = JulieExtractRunner.BuildInfoArgs(AbsDb);

        Assert.Equal(new[] { "extract", "--db", AbsDb, "--json", "info" }, args);
        Assert.DoesNotContain("--root", args);
    }

    // ---- (2) report parser ----

    private const string ScanSuccessJson = """
        {
          "status": "scanned",
          "operation": "scan",
          "db_path": "/abs/work/.miller/symbols.db",
          "root": "/abs/work/repo",
          "schema_version": 28,
          "schema_state": "current",
          "extract_contract_version": 2,
          "analysis_state": "missing",
          "files_scanned": 12,
          "symbols_extracted": 134,
          "files_total": 12,
          "symbols_total": 134,
          "relationships_total": 40,
          "identifiers_total": 512,
          "types_total": 9,
          "errors": []
        }
        """;

    [Fact]
    public void ParseReport_ScanSuccess_DeserializesFlatFields_AndRenamesDbToDbPath()
    {
        var report = JulieExtractRunner.ParseReport(ScanSuccessJson);

        Assert.Equal("scanned", report.Status);
        Assert.Equal("scan", report.Operation);
        Assert.Equal("/abs/work/.miller/symbols.db", report.DbPath); // serde renamed `db` -> `db_path`
        Assert.Equal("/abs/work/repo", report.Root);
        Assert.Equal(28, report.SchemaVersion);
        Assert.Equal("current", report.SchemaState);
        Assert.Equal(2, report.ExtractContractVersion);
        Assert.Equal(12u, report.FilesScanned);
        Assert.Equal(134u, report.SymbolsExtracted);
        Assert.Empty(report.Errors);
    }

    private const string InfoJson = """
        {
          "status": "unchanged",
          "operation": "info",
          "db_path": "/abs/work/.miller/symbols.db",
          "root": null,
          "schema_version": 28,
          "schema_state": "current",
          "extract_contract_version": 2,
          "analysis_state": "missing",
          "files_scanned": 0,
          "symbols_extracted": 0,
          "files_total": 12,
          "symbols_total": 134,
          "relationships_total": 40,
          "identifiers_total": 512,
          "types_total": 9,
          "errors": []
        }
        """;

    [Fact]
    public void ParseReport_Info_CountsLandInTotals_NotScanned()
    {
        var report = JulieExtractRunner.ParseReport(InfoJson);

        // info reuses the same flat shape; counts come as *_total, scan counters are 0.
        Assert.Equal(0u, report.FilesScanned);
        Assert.Equal(0u, report.SymbolsExtracted);
        Assert.Equal(12u, report.FilesTotal);
        Assert.Equal(134u, report.SymbolsTotal);
        Assert.Equal(40u, report.RelationshipsTotal);
        Assert.Equal(512u, report.IdentifiersTotal);
        Assert.Equal(9u, report.TypesTotal);
        Assert.Null(report.Root);
    }

    private const string FailedJson = """
        {
          "status": "failed",
          "operation": "scan",
          "db_path": "/abs/work/.miller/symbols.db",
          "root": "/abs/work/repo",
          "schema_version": 28,
          "schema_state": "current",
          "extract_contract_version": 2,
          "analysis_state": "missing",
          "files_scanned": 0,
          "symbols_extracted": 0,
          "files_total": 0,
          "symbols_total": 0,
          "relationships_total": 0,
          "identifiers_total": 0,
          "types_total": 0,
          "errors": [
            { "code": "root_mismatch", "message": "root differs from bound workspace; use --force", "path": "/abs/work/repo" }
          ]
        }
        """;

    [Fact]
    public void ParseReport_Failed_DeserializesErrorsArray()
    {
        var report = JulieExtractRunner.ParseReport(FailedJson);

        Assert.Equal("failed", report.Status);
        var err = Assert.Single(report.Errors);
        Assert.Equal("root_mismatch", err.Code);
        Assert.Contains("--force", err.Message);
        Assert.Equal("/abs/work/repo", err.Path);
    }

    // ---- (3) exit-code -> outcome mapping (fake process result, no live process) ----

    [Fact]
    public void Interpret_Exit0_ReturnsParsedReport()
    {
        var report = JulieExtractRunner.Interpret(exitCode: 0, stdout: ScanSuccessJson, stderr: "");

        Assert.Equal("scanned", report.Status);
        Assert.Equal(134u, report.SymbolsExtracted);
    }

    [Fact]
    public void Interpret_Exit1_ThrowsFailed_WithReportErrorsAndStderr()
    {
        // exit 1 => stdout STILL holds an ExtractReport with status:"failed" + errors[]; stderr carried too.
        var ex = Assert.Throws<JulieExtractFailedException>(() =>
            JulieExtractRunner.Interpret(exitCode: 1, stdout: FailedJson, stderr: "extract: root mismatch"));

        var err = Assert.Single(ex.Errors);
        Assert.Equal("root_mismatch", err.Code);
        Assert.Equal("extract: root mismatch", ex.StandardError);
        Assert.Contains("root_mismatch", ex.Message); // message surfaces the error code for operators
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
        // It must be the BASE type, not one of the specific subclasses.
        Assert.IsType<JulieExtractException>(ex, exactMatch: true);
    }

    [Fact]
    public void Interpret_DeleteNotFound_Exit0_IsTolerated_NotAFailure()
    {
        // `delete` of an absent file → status "not_found", exit 0. Tolerant, NOT a failure.
        const string notFound = """
            { "status": "not_found", "operation": "delete", "db_path": "/abs/db", "root": "/abs/r",
              "schema_version": 28, "extract_contract_version": 2,
              "files_scanned": 0, "symbols_extracted": 0, "files_total": 0, "symbols_total": 0,
              "relationships_total": 0, "identifiers_total": 0, "types_total": 0, "errors": [] }
            """;

        var report = JulieExtractRunner.Interpret(exitCode: 0, stdout: notFound, stderr: "");
        Assert.Equal("not_found", report.Status);
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

    // ---- locator error path ----

    [Fact]
    public void Constructor_BinaryNotFound_ThrowsPointingAtRestoreScript()
    {
        string missing = Path.Combine(Path.GetTempPath(), "miller-no-julie-" + Guid.NewGuid().ToString("N"), "julie-server");
        var ex = Assert.Throws<FileNotFoundException>(() => new JulieExtractRunner(missing));
        Assert.Contains("restore-julie-server", ex.Message);
    }

    [Fact]
    public void ParseReport_BindsKeysCaseInsensitively_ProvingPropertyNameCaseInsensitive()
    {
        // Keys differ from the wire form ONLY by case (Schema_Version, Status, Extract_Contract_Version).
        // This binds iff PropertyNameCaseInsensitive is set on the runner's JsonSerializerOptions; drop that
        // flag and these fields would deserialize to their defaults instead.
        const string mixedCaseJson = """
            {
              "Status": "scanned",
              "operation": "scan",
              "DB_Path": "/abs/work/.miller/symbols.db",
              "root": "/abs/work/repo",
              "Schema_Version": 28,
              "Extract_Contract_Version": 2,
              "files_scanned": 0,
              "symbols_extracted": 0,
              "files_total": 0,
              "symbols_total": 0,
              "relationships_total": 0,
              "identifiers_total": 0,
              "types_total": 0,
              "errors": []
            }
            """;

        var report = JulieExtractRunner.ParseReport(mixedCaseJson);

        Assert.Equal("scanned", report.Status);                       // "Status" bound case-insensitively
        Assert.Equal("/abs/work/.miller/symbols.db", report.DbPath);  // "DB_Path" bound to db_path
        Assert.Equal(28, report.SchemaVersion);                       // "Schema_Version" bound to schema_version
        Assert.Equal(2, report.ExtractContractVersion);               // "Extract_Contract_Version" bound
    }

    // ---- (4) post-extract version cross-check (D5; julie self-rejects only a NEWER DB, so Miller gates) ----

    private static ExtractReport ReportWith(int? schemaVersion, int? contractVersion) => new(
        Status: "scanned", Operation: "scan", DbPath: AbsDb, Root: AbsRoot,
        SchemaVersion: schemaVersion, SchemaState: "current", ExtractContractVersion: contractVersion,
        AnalysisState: "missing", FilesScanned: 0, SymbolsExtracted: 0, FilesTotal: 0, SymbolsTotal: 0,
        RelationshipsTotal: 0, IdentifiersTotal: 0, TypesTotal: 0, Errors: Array.Empty<ExtractError>());

    [Fact]
    public void VerifyReport_AtPinnedSchemaAndContract_DoesNotThrow()
    {
        // No throw == compatible. A throw fails the test.
        ExtractVersionMismatch.VerifyReport(ReportWith((int)MillerExtractContract.ExpectedSchemaVersion, (int)MillerExtractContract.ExpectedExtractContractVersion));
    }

    [Fact]
    public void VerifyReport_NewerSchema_ThrowsNamingValueAndPointingAtUpgrade()
    {
        var ex = Assert.Throws<IncompatibleExtractException>(() =>
            ExtractVersionMismatch.VerifyReport(ReportWith((int)MillerExtractContract.ExpectedSchemaVersion + 1, (int)MillerExtractContract.ExpectedExtractContractVersion)));
        Assert.Contains(JulieDbFixture.SchemaText(1), ex.Message);
        Assert.Contains(JulieDbFixture.SchemaText(), ex.Message);
        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("upgrade Miller", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyReport_OlderContract_ThrowsNamingValueAndPointingAtRestore()
    {
        // Contract 0 < expected 1: julie would NOT self-reject this; Miller's cross-check must.
        var ex = Assert.Throws<IncompatibleExtractException>(() =>
            ExtractVersionMismatch.VerifyReport(ReportWith((int)MillerExtractContract.ExpectedSchemaVersion, (int)MillerExtractContract.ExpectedExtractContractVersion - 1)));
        Assert.Contains("extract_contract_version", ex.Message);
        Assert.Contains("restore", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyReport_NullVersions_DoesNotThrow_ReadPathGateEnforcesPresence()
    {
        // julie may omit the versions in a report; the cross-check skips nulls (the DB-read gate enforces
        // presence before any read). The point: the runner must not false-positive on an absent field.
        ExtractVersionMismatch.VerifyReport(ReportWith(null, null));
    }
}
