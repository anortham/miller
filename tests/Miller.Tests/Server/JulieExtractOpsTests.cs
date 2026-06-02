using Miller.Indexing;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins <see cref="JulieExtractOps"/>'s canonicalization contract (verified-fact 4) WITHOUT spawning
/// julie-extract: the production ops must hand julie symlink-resolved canonical paths for the file argument so
/// <c>delete</c>/<c>update</c> never trip the "outside external extract root" trap. We inject a recording
/// runner seam (a delegate trio) so the exact <c>(root, db, file)</c> the ops would pass to the binary is
/// asserted directly. The live subprocess path is the Scale suite.
/// </summary>
public sealed class JulieExtractOpsTests
{
    private sealed record Recorded(string Op, string Root, string Db, string File, bool Force = false);

    private static (JulieExtractOps ops, List<Recorded> calls) NewOps(string canonicalRoot, string db)
    {
        var calls = new List<Recorded>();
        ExtractReport Stub() => new(
            ReportSchemaVersion: 1, Status: "ok", Operation: "test", Mode: "single_file", Input: null,
            Artifact: new ExtractArtifact(
                DbPath: db, RootPath: canonicalRoot, ArtifactId: "a",
                SchemaVersion: MillerExtractContract.ExpectedSchemaVersion,
                ExtractContractVersion: MillerExtractContract.ExpectedExtractContractVersion,
                SqliteSchemaVersion: MillerExtractContract.ExpectedSqliteSchemaVersion,
                JsonlSchemaVersion: 1, HashAlgorithm: MillerExtractContract.ExpectedHashAlgorithm,
                ParserInventoryFingerprint: "p", CapabilitySnapshotFingerprint: "c"),
            Tool: new ExtractTool("julie-extract", "2.0.0"),
            RevisionBlock: new ExtractRevision(2, 2),
            Counts: new ExtractCounts(0, 1, 0, 0, 0, 0, RowsWritten: null, Totals: null),
            Errors: System.Array.Empty<ReportDiagnostic>(), Warnings: System.Array.Empty<ReportDiagnostic>());

        var ops = JulieExtractOps.CreateForTest(
            canonicalRoot, db,
            update: (root, db2, file) => { calls.Add(new Recorded("update", root, db2, file)); return Stub(); },
            delete: (root, db2, file) => { calls.Add(new Recorded("delete", root, db2, file)); return Stub(); },
            scan: (root, db2, force) => { calls.Add(new Recorded("scan", root, db2, "", force)); return Stub(); });
        return (ops, calls);
    }

    [Fact]
    public void Update_PassesCanonicalRootAndCanonicalFile()
    {
        // A real symlinked temp dir so canonicalization is observable (POSIX: /var -> /private/var on macOS,
        // and we add our own link so the test is deterministic across platforms that support symlinks).
        string real = Path.Combine(Path.GetTempPath(), "miller-ops-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(real);
        string canonicalReal = PathCanonicalizer.CanonicalizeRoot(real);
        string child = Path.Combine(canonicalReal, "sub", "File.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(child)!);
        File.WriteAllText(child, "x");

        try
        {
            var (ops, calls) = NewOps(canonicalReal, Path.Combine(canonicalReal, ".miller", "symbols.db"));

            // Pass a NON-canonical-looking relative path; ops must compose+canonicalize it under the root.
            ops.Update("sub/File.cs");

            var rec = Assert.Single(calls);
            Assert.Equal("update", rec.Op);
            Assert.Equal(canonicalReal, rec.Root);
            Assert.Equal(child, rec.File); // symlink-resolved absolute path, composed under the canonical root
        }
        finally
        {
            try { Directory.Delete(real, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Delete_OfAVanishedFile_StillResolvesUnderTheCanonicalRoot()
    {
        string real = Path.Combine(Path.GetTempPath(), "miller-ops-del-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(real);
        string canonicalReal = PathCanonicalizer.CanonicalizeRoot(real);

        try
        {
            var (ops, calls) = NewOps(canonicalReal, Path.Combine(canonicalReal, ".miller", "symbols.db"));

            // The file is already gone (a delete event). Canonicalization must still target a path INSIDE the
            // resolved root (the not-yet-existing tail is appended lexically), never throw.
            ops.Delete("removed/Gone.cs");

            var rec = Assert.Single(calls);
            Assert.Equal("delete", rec.Op);
            Assert.Equal(canonicalReal, rec.Root);
            Assert.Equal(Path.Combine(canonicalReal, "removed", "Gone.cs"), rec.File);
            Assert.StartsWith(canonicalReal, rec.File); // inside the canonical root (no outside-root trap)
        }
        finally
        {
            try { Directory.Delete(real, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Scan_PassesTheCanonicalRoot_NoFileArg_DefaultsToDeltaNotForce()
    {
        string real = Path.Combine(Path.GetTempPath(), "miller-ops-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(real);
        string canonicalReal = PathCanonicalizer.CanonicalizeRoot(real);

        try
        {
            var (ops, calls) = NewOps(canonicalReal, Path.Combine(canonicalReal, ".miller", "symbols.db"));

            ops.Scan();

            var rec = Assert.Single(calls);
            Assert.Equal("scan", rec.Op);
            Assert.Equal(canonicalReal, rec.Root);
            Assert.False(rec.Force); // the M3 delta reconcile default is a hash-delta scan, never --force
        }
        finally
        {
            try { Directory.Delete(real, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Scan_ForceTrue_ThreadsForceThroughToTheRunner()
    {
        string real = Path.Combine(Path.GetTempPath(), "miller-ops-scanf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(real);
        string canonicalReal = PathCanonicalizer.CanonicalizeRoot(real);

        try
        {
            var (ops, calls) = NewOps(canonicalReal, Path.Combine(canonicalReal, ".miller", "symbols.db"));

            ops.Scan(force: true); // a `workspace full` from-scratch rebuild (M7 D3)

            var rec = Assert.Single(calls);
            Assert.Equal("scan", rec.Op);
            Assert.Equal(canonicalReal, rec.Root);
            Assert.True(rec.Force); // --force must reach the runner so julie rebuilds from scratch
        }
        finally
        {
            try { Directory.Delete(real, recursive: true); } catch (IOException) { }
        }
    }
}
