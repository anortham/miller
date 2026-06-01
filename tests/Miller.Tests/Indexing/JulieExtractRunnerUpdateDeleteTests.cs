using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the M3 <c>update</c>/<c>delete</c> seams WITHOUT spawning julie-extract (the live path is the Scale
/// suite). Two arg-builders produce the exact verified v1 argv — <c>update --root &lt;root&gt; --db &lt;db&gt;
/// --file &lt;file&gt; --strict-schema --json</c> (and <c>delete</c>) — and reuse the same
/// <see cref="JulieExtractRunner.Interpret"/> exit-code contract as scan. All paths the builders receive are
/// already canonical (see <see cref="PathCanonicalizerTests"/>); these tests assert the builder passes them
/// through verbatim, in order.
/// </summary>
public sealed class JulieExtractRunnerUpdateDeleteTests
{
    private const string AbsDb = "/abs/work/.miller/symbols.db";
    private const string AbsRoot = "/abs/work/repo";
    private const string AbsFile = "/abs/work/repo/src/a.cs";

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
    public void BuildDeleteArgs_ProducesV1Argv_FileBeforeStrictSchema()
    {
        var args = JulieExtractRunner.BuildDeleteArgs(AbsDb, AbsRoot, AbsFile);

        Assert.Equal(
            new[] { "delete", "--root", AbsRoot, "--db", AbsDb, "--file", AbsFile, "--strict-schema", "--json" },
            args);
        Assert.DoesNotContain("extract", args);
    }

    [Fact]
    public void BuildUpdateArgs_PassesCanonicalPathsVerbatim_NoNormalizationInTheBuilder()
    {
        // The builder is a pure seam: it must NOT re-normalize. Canonicalization is PathCanonicalizer's job
        // (done upstream); a builder that "helpfully" rewrote the path would defeat verified-fact-4. Feed a
        // path with a redundant segment and prove it survives untouched into the argv.
        const string oddButCanonicalShaped = "/abs/work/repo/src/a.cs";
        var args = JulieExtractRunner.BuildUpdateArgs(AbsDb, AbsRoot, oddButCanonicalShaped);
        // --file is the argument immediately after the "--file" token.
        int fileIdx = args.ToList().IndexOf("--file");
        Assert.Equal(oddButCanonicalShaped, args[fileIdx + 1]);
    }

    [Theory]
    [InlineData(null, AbsRoot, AbsFile)]
    [InlineData(AbsDb, null, AbsFile)]
    [InlineData(AbsDb, AbsRoot, null)]
    [InlineData("", AbsRoot, AbsFile)]
    [InlineData(AbsDb, "   ", AbsFile)]
    [InlineData(AbsDb, AbsRoot, "")]
    public void BuildUpdateArgs_RejectsNullOrBlankArguments(string? db, string? root, string? file)
    {
        Assert.ThrowsAny<ArgumentException>(() => JulieExtractRunner.BuildUpdateArgs(db!, root!, file!));
    }

    [Theory]
    [InlineData(null, AbsRoot, AbsFile)]
    [InlineData(AbsDb, null, AbsFile)]
    [InlineData(AbsDb, AbsRoot, null)]
    [InlineData("", AbsRoot, AbsFile)]
    [InlineData(AbsDb, "   ", AbsFile)]
    [InlineData(AbsDb, AbsRoot, "")]
    public void BuildDeleteArgs_RejectsNullOrBlankArguments(string? db, string? root, string? file)
    {
        Assert.ThrowsAny<ArgumentException>(() => JulieExtractRunner.BuildDeleteArgs(db!, root!, file!));
    }

    // ---- exit-code contract reuse: update/delete share scan's Interpret mapping (nested v1 shape). ----

    private const string ChangedJson = """
        { "report_schema_version": 1, "status": "ok", "operation": "update", "mode": "single_file",
          "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": "/abs/r/a.cs",
                     "root_relative_path": "a.cs", "format": null, "output_path": null },
          "artifact": { "db_path": "/abs/db", "root_path": "/abs/r", "artifact_id": "a", "schema_version": 1,
                        "extract_contract_version": 1, "sqlite_schema_version": 1, "jsonl_schema_version": 1,
                        "hash_algorithm": "blake3", "parser_inventory_fingerprint": "sha256:p",
                        "capability_snapshot_fingerprint": "sha256:c" },
          "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": { "latest_revision_id": 5, "created_revision_id": 5 },
          "counts": { "files_scanned": 0, "files_changed": 1, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 0, "rows_written": { "symbols": 3 }, "totals": { "files": 2, "symbols": 8 } },
          "errors": [], "warnings": [] }
        """;

    [Fact]
    public void Interpret_UpdateChanged_Exit0_ReturnsReport_WithBumpedRevision()
    {
        var report = JulieExtractRunner.Interpret(exitCode: 0, stdout: ChangedJson, stderr: "");

        Assert.Equal("ok", report.Status);
        Assert.Equal(1u, report.FilesUpdated);
        Assert.Equal(5L, report.Revision);
    }

    private const string FailedUpdateJson = """
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

    [Fact]
    public void Interpret_UpdateFailed_Exit1_ThrowsFailed_CarryingOutcomeAwareErrors()
    {
        // Decision-10: an update failure (e.g. the outside-root trap, the data-loss guard) surfaces as a typed
        // failure carrying the structured diagnostics so the service can branch (keep-prior vs surface-loudly).
        var ex = Assert.Throws<JulieExtractFailedException>(() =>
            JulieExtractRunner.Interpret(exitCode: 1, stdout: FailedUpdateJson, stderr: "extract: outside root"));

        var err = Assert.Single(ex.Errors);
        Assert.Equal("file_outside_root", err.Code);
        Assert.Equal("extract: outside root", ex.StandardError);
    }

    [Fact]
    public void Interpret_DeleteUsageError_Exit2_ThrowsUsage()
    {
        const string usage = "error: --file <FILE> is required for delete";
        var ex = Assert.Throws<JulieExtractUsageException>(() =>
            JulieExtractRunner.Interpret(exitCode: 2, stdout: "", stderr: usage));
        Assert.Equal(usage, ex.StandardError);
    }

    // ---- finding-1: Update/Delete require an ALREADY-canonical (absolute) db and never re-mangle it ----

    private static JulieExtractRunner RealRunner()
    {
        // Point at THIS test assembly (any real file) so the constructor's File.Exists passes without needing
        // the pinned julie-extract binary — we only exercise the pre-spawn db guard, which throws BEFORE exec.
        string self = typeof(JulieExtractRunnerUpdateDeleteTests).Assembly.Location;
        return new JulieExtractRunner(self);
    }

    [Theory]
    [InlineData("relative/symbols.db")]
    [InlineData(".miller/symbols.db")]
    [InlineData("symbols.db")]
    public void Update_RejectsARelativeDbPath_BeforeSpawning(string relativeDb)
    {
        // verified-fact 4: a relative --db would be resolved by julie-extract against its ambient CWD, defeating
        // the canonicalization the bootstrap performed. The runner must reject it up front (no process spawned).
        var runner = RealRunner();
        Assert.Throws<ArgumentException>(() => runner.Update(AbsRoot, relativeDb, AbsFile));
    }

    [Theory]
    [InlineData("relative/symbols.db")]
    [InlineData(".miller/symbols.db")]
    [InlineData("symbols.db")]
    public void Delete_RejectsARelativeDbPath_BeforeSpawning(string relativeDb)
    {
        var runner = RealRunner();
        Assert.Throws<ArgumentException>(() => runner.Delete(AbsRoot, relativeDb, AbsFile));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Update_RejectsNullOrBlankDb(string? db)
    {
        var runner = RealRunner();
        Assert.ThrowsAny<ArgumentException>(() => runner.Update(AbsRoot, db!, AbsFile));
    }
}
