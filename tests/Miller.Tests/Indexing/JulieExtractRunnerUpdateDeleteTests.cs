using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the M3 <c>update</c>/<c>delete</c> seams WITHOUT spawning julie-server (the live path is the Scale
/// suite). Two arg-builders produce the exact verified argv — <c>extract --db &lt;db&gt; --root &lt;root&gt;
/// --json update --file &lt;file&gt;</c> (and <c>delete</c>) — and reuse the same <see cref="JulieExtractRunner.Interpret"/>
/// exit-code contract as scan. All paths the builders receive are already canonical (see
/// <see cref="PathCanonicalizerTests"/>); these tests assert the builder passes them through verbatim, in order.
/// </summary>
public sealed class JulieExtractRunnerUpdateDeleteTests
{
    private const string AbsDb = "/abs/work/.miller/symbols.db";
    private const string AbsRoot = "/abs/work/repo";
    private const string AbsFile = "/abs/work/repo/src/a.cs";

    [Fact]
    public void BuildUpdateArgs_ProducesVerifiedArgv_WithFileAfterSubcommand()
    {
        var args = JulieExtractRunner.BuildUpdateArgs(AbsDb, AbsRoot, AbsFile);

        Assert.Equal(
            new[] { "extract", "--db", AbsDb, "--root", AbsRoot, "--json", "update", "--file", AbsFile },
            args);
    }

    [Fact]
    public void BuildDeleteArgs_ProducesVerifiedArgv_WithFileAfterSubcommand()
    {
        var args = JulieExtractRunner.BuildDeleteArgs(AbsDb, AbsRoot, AbsFile);

        Assert.Equal(
            new[] { "extract", "--db", AbsDb, "--root", AbsRoot, "--json", "delete", "--file", AbsFile },
            args);
    }

    [Fact]
    public void BuildUpdateArgs_PassesCanonicalPathsVerbatim_NoNormalizationInTheBuilder()
    {
        // The builder is a pure seam: it must NOT re-normalize. Canonicalization is PathCanonicalizer's job
        // (done upstream); a builder that "helpfully" rewrote the path would defeat verified-fact-4. Feed a
        // path with a redundant segment and prove it survives untouched into the argv.
        const string oddButCanonicalShaped = "/abs/work/repo/src/a.cs";
        var args = JulieExtractRunner.BuildUpdateArgs(AbsDb, AbsRoot, oddButCanonicalShaped);
        Assert.Equal(oddButCanonicalShaped, args[^1]);
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

    // ---- exit-code contract reuse: update/delete share scan's Interpret mapping (verified-fact 2/3). ----

    private const string ChangedJson = """
        { "status": "changed", "operation": "update", "workspace_id": "ws-1",
          "db_path": "/abs/db", "root": "/abs/r", "schema_version": 26, "extract_contract_version": 1,
          "revision": 5, "files_scanned": 0, "files_updated": 1, "files_deleted": 0,
          "symbols_extracted": 3, "files_total": 2, "symbols_total": 8,
          "relationships_total": 0, "identifiers_total": 0, "types_total": 0, "errors": [] }
        """;

    [Fact]
    public void Interpret_UpdateChanged_Exit0_ReturnsReport_WithBumpedRevision()
    {
        var report = JulieExtractRunner.Interpret(exitCode: 0, stdout: ChangedJson, stderr: "");

        Assert.Equal("changed", report.Status);
        Assert.Equal(1u, report.FilesUpdated);
        Assert.Equal(5L, report.Revision);
    }

    private const string FailedUpdateJson = """
        { "status": "failed", "operation": "update", "workspace_id": "ws-1",
          "db_path": "/abs/db", "root": "/abs/r", "schema_version": 26, "extract_contract_version": 1,
          "revision": 4, "files_scanned": 0, "files_updated": 0, "files_deleted": 0,
          "symbols_extracted": 0, "files_total": 0, "symbols_total": 0,
          "relationships_total": 0, "identifiers_total": 0, "types_total": 0,
          "errors": [ { "code": "outside_root", "message": "file is outside external extract root", "path": "/x" } ] }
        """;

    [Fact]
    public void Interpret_UpdateFailed_Exit1_ThrowsFailed_CarryingOutcomeAwareErrors()
    {
        // Decision-10: an update failure (e.g. the outside-root trap, the empty-reparse guard) surfaces as a
        // typed failure carrying the structured errors so the service can branch (keep-prior vs surface-loudly).
        var ex = Assert.Throws<JulieExtractFailedException>(() =>
            JulieExtractRunner.Interpret(exitCode: 1, stdout: FailedUpdateJson, stderr: "extract: outside root"));

        var err = Assert.Single(ex.Errors);
        Assert.Equal("outside_root", err.Code);
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
        // the pinned julie-server binary — we only exercise the pre-spawn db guard, which throws BEFORE exec.
        string self = typeof(JulieExtractRunnerUpdateDeleteTests).Assembly.Location;
        return new JulieExtractRunner(self);
    }

    [Theory]
    [InlineData("relative/symbols.db")]
    [InlineData(".miller/symbols.db")]
    [InlineData("symbols.db")]
    public void Update_RejectsARelativeDbPath_BeforeSpawning(string relativeDb)
    {
        // verified-fact 4: a relative --db would be resolved by julie against its ambient CWD, defeating the
        // canonicalization the bootstrap performed. The runner must reject it up front (no process spawned).
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
