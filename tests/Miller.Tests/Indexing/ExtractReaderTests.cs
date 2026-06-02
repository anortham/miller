using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the M2 on-demand read layer (<see cref="ExtractReader"/>): per-inspect detail (doc/visibility/body
/// spans), name-based references (identifiers — <c>target_symbol_id</c> is always NULL), and the body slice
/// re-sourced from DISK under the fixture's <c>WorkspaceRoot</c> with the hard content_hash freshness invariant
/// (a drifted file is never sliced) and graceful NULL-span degradation. Driven against the inspect fixture;
/// opens the DB Mode=ReadOnly like the M1 reader. Fast suite (no julie-extract binary).
/// </summary>
public sealed class ExtractReaderTests
{
    // ---- ReadDetail ----

    [Fact]
    public void ReadDetail_ReturnsDocVisibilityAndBodySpans()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var detail = ExtractReader.ReadDetail(fx.DbPath, JulieDbFixture.GetUserId);

        Assert.NotNull(detail);
        Assert.Equal("Gets a user by id.", detail!.DocComment);
        Assert.Equal("public", detail.Visibility);
        Assert.NotNull(detail.BodyStartByte);
        Assert.NotNull(detail.BodyEndByte);
        Assert.Equal(2, detail.BodyStartLine);
        Assert.Equal(4, detail.BodyEndLine);
    }

    [Fact]
    public void ReadDetail_NullColumns_MapToNull()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        // DeleteUser has NULL doc_comment and NULL body spans (code_context is gone from v1 symbols).
        var detail = ExtractReader.ReadDetail(fx.DbPath, "c3d4e5f6001122334455667788990a1b");

        Assert.NotNull(detail);
        Assert.Null(detail!.DocComment);
        Assert.Null(detail.BodyStartByte);
        Assert.Null(detail.BodyEndByte);
        Assert.Null(detail.BodyStartLine);
        Assert.Equal("public", detail.Visibility);
    }

    [Fact]
    public void ReadDetail_UnknownId_ReturnsNull()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        Assert.Null(ExtractReader.ReadDetail(fx.DbPath, "ffffffffffffffffffffffffffffffff"));
    }

    // ---- ReadReferences (name-based) ----

    [Fact]
    public void ReadReferences_ByName_ReturnsEveryIdentifierWithThatName()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        var refs = ExtractReader.ReadReferences(fx.DbPath, "GetUser");

        // Two refs to GetUser were recorded (Controller.cs:4 and Repo.cs:9).
        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.FilePath == "web/Controller.cs" && r.StartLine == 4);
        Assert.Contains(refs, r => r.FilePath == "auth/Repo.cs" && r.StartLine == 9);
        // Each carries its enclosing (containing) symbol id — the callers source.
        Assert.All(refs, r => Assert.False(string.IsNullOrEmpty(r.ContainingSymbolId)));
    }

    [Fact]
    public void ReadReferences_CallsFromASymbol_AreFoundByContainingId()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        // Callees one-hop: identifiers with containing_symbol_id == GetUser AND kind == 'call'.
        var callees = ExtractReader.ReadCallees(fx.DbPath, JulieDbFixture.GetUserId);

        var callee = Assert.Single(callees);
        Assert.Equal("Find", callee.Name);
        Assert.Equal("auth/UserService.cs", callee.FilePath);
        Assert.Equal(3, callee.StartLine);
    }

    [Fact]
    public void ReadReferences_UnknownName_ReturnsEmpty()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        Assert.Empty(ExtractReader.ReadReferences(fx.DbPath, "NoSuchIdentifier"));
    }

    // ---- ReadBody (disk re-source with the hard content_hash freshness invariant) ----

    [Fact]
    public void ReadBody_FreshFile_SlicesByteRangeFromDisk()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var detail = ExtractReader.ReadDetail(fx.DbPath, JulieDbFixture.GetUserId)!;

        string? body = ExtractReader.ReadBody(
            fx.DbPath, fx.WorkspaceRoot, "auth/UserService.cs",
            detail.BodyStartByte, detail.BodyEndByte, detail.BodyStartLine, detail.BodyEndLine);

        Assert.NotNull(body);
        Assert.StartsWith("public User GetUser(int id)", body);
        Assert.Contains("return _repo.Find(id);", body!);
        Assert.EndsWith("}", body.TrimEnd());
    }

    [Fact]
    public void ReadBody_DriftedFile_ReturnsNull_NeverSlicesStaleBytes()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var detail = ExtractReader.ReadDetail(fx.DbPath, JulieDbFixture.GetUserId)!;

        // Mutate the on-disk file so its blake3 no longer matches the stored content_hash.
        string abs = Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs");
        File.WriteAllText(abs, "// completely different file\nclass X {}\n");

        string? body = ExtractReader.ReadBody(
            fx.DbPath, fx.WorkspaceRoot, "auth/UserService.cs",
            detail.BodyStartByte, detail.BodyEndByte, detail.BodyStartLine, detail.BodyEndLine);

        // Hard invariant (design §7): the stored byte offsets address the INDEXED content; slicing them out of
        // the drifted file would return the WRONG bytes. The reader must refuse and signal staleness.
        Assert.Null(body);
    }

    [Fact]
    public void ReadBody_MissingDiskFile_ReturnsNull()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        File.Delete(Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs"));

        string? body = ExtractReader.ReadBody(
            fx.DbPath, fx.WorkspaceRoot, "auth/UserService.cs",
            startByte: 0, endByte: 10, startLine: 1, endLine: 1);

        Assert.Null(body);
    }

    [Fact]
    public void ReadBody_NullByteSpans_FallsBackToLineSlice_FromDisk()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        // No byte spans, but line range 2..4 → line-based fallback slice of the FRESH on-disk content.
        string? body = ExtractReader.ReadBody(
            fx.DbPath, fx.WorkspaceRoot, "auth/UserService.cs",
            startByte: null, endByte: null, startLine: 2, endLine: 4);

        Assert.NotNull(body);
        Assert.Contains("GetUser", body!);
        Assert.Contains("return _repo.Find(id);", body);
    }

    [Fact]
    public void ReadBody_NoByteAndNoLineSpans_ReturnsNull()
    {
        using var fx = JulieDbFixture.CreateForInspect();

        string? body = ExtractReader.ReadBody(
            fx.DbPath, fx.WorkspaceRoot, "auth/UserService.cs",
            startByte: null, endByte: null, startLine: null, endLine: null);

        Assert.Null(body);
    }

    // ---- ReadRootPath (v1 artifact identity — reconciliation #14) ----

    [Fact]
    public void ReadRootPath_ReturnsTheArtifactMetadataValue()
    {
        // v1 records the canonical root in artifact_metadata.root_path (the fixture seeds '/work/repo').
        using var fx = JulieDbFixture.CreateForInspect();
        Assert.Equal("/work/repo", ExtractReader.ReadRootPath(fx.DbPath));
    }

    [Fact]
    public void ReadRootPath_LegacyDbWithoutArtifactMetadataTable_ReturnsNull_NotThrows()
    {
        // A pre-v1 julie-server DB (schema 28) has NO artifact_metadata table. ReadRootPath must treat the
        // missing table as "unknown root" (null) — NOT throw — so the bootstrap's DecideBootstrapScan force-
        // rescans and julie-extract rebuilds it as a v1 artifact (the documented self-healing upgrade,
        // reconciliation #14). Throwing here propagates out of bootstrap StartAsync and crashes server startup
        // for every user upgrading with an existing .miller/symbols.db. Mirrors the missing-table tolerance
        // ExtractFileHashReader.ReadHashAlgorithm already has.
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract,
            JulieDbFixture.DefaultRows, createMetadataTable: false);

        Assert.Null(ExtractReader.ReadRootPath(fx.DbPath));
    }

    // ---- ReadBody workspace-containment trust boundary (Finding 3) ----
    // v1 re-sources body text from DISK keyed on the artifact's files.path. A corrupt/tampered artifact could
    // record a path that escapes the workspace root (absolute, or via '..') with a content_hash matching the
    // external file's bytes; the inspect surface must NEVER disclose a file outside the root. These prove the
    // reader fails CLOSED (null) on an out-of-root path even when the hash would otherwise match.

    [Fact]
    public void ReadBody_AbsoluteSymbolPath_ReturnsNull_EvenWhenHashMatches()
    {
        string root = NewTempDir();
        string secretAbs = Path.Combine(NewTempDir(), "secret.txt");
        byte[] secret = System.Text.Encoding.UTF8.GetBytes("TOP SECRET (absolute path)\n");
        File.WriteAllBytes(secretAbs, secret);

        // Artifact row: an ABSOLUTE path pointing outside the workspace, hash matching the secret's real bytes.
        string db = Path.Combine(root, "symbols.db");
        BuildFilesOnlyDb(db, secretAbs, "blake3:" + ContentHasher.Blake3Hex(secret));

        string? body = ExtractReader.ReadBody(
            db, workspaceRoot: root, filePath: secretAbs,
            startByte: 0, endByte: secret.Length, startLine: 1, endLine: 1);

        Assert.Null(body);
    }

    [Fact]
    public void ReadBody_ParentEscapingSymbolPath_ReturnsNull_EvenWhenHashMatches()
    {
        string parent = NewTempDir();
        string root = Path.Combine(parent, "ws");
        Directory.CreateDirectory(root);
        byte[] secret = System.Text.Encoding.UTF8.GetBytes("TOP SECRET (.. escape)\n");
        File.WriteAllBytes(Path.Combine(parent, "secret.txt"), secret);

        // Artifact row: a RELATIVE path that escapes the root via '..', hash matching the secret's real bytes.
        string escaping = Path.Combine("..", "secret.txt");
        string db = Path.Combine(root, "symbols.db");
        BuildFilesOnlyDb(db, escaping, "blake3:" + ContentHasher.Blake3Hex(secret));

        string? body = ExtractReader.ReadBody(
            db, workspaceRoot: root, filePath: escaping,
            startByte: 0, endByte: secret.Length, startLine: 1, endLine: 1);

        Assert.Null(body);
    }

    // A fresh temp directory (left for the OS temp reaper — tests must not depend on cleanup ordering).
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-extractreader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A minimal v1-shaped DB carrying ONE files row (path + content_hash) — all ReadBody's disk re-source needs
    // to look up the stored hash. Deliberately does NOT materialize the file under any root (the path is meant to
    // point outside it), so it exercises the containment guard, not the manifest-miss branch.
    private static void BuildFilesOnlyDb(string dbPath, string filePath, string contentHash)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        };
        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE files (path TEXT PRIMARY KEY, content_hash TEXT NOT NULL);";
            ddl.ExecuteNonQuery();
        }
        using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO files (path, content_hash) VALUES ($p, $h);";
        ins.Parameters.AddWithValue("$p", filePath);
        ins.Parameters.AddWithValue("$h", contentHash);
        ins.ExecuteNonQuery();
    }

    // ---- D6 by-name read discipline: a value comes from its NAMED column, not a fixed ordinal ----

    [Fact]
    public void ReadDetail_ReadsByColumnName_NotOrdinal_AcrossTheFullV1ColumnLayout()
    {
        // The v1 symbols table interleaves many position/test/metadata columns; ReadDetail SELECTs a SUBSET
        // (doc_comment, visibility, body spans) and reads each by GetOrdinal(name). This pins that the doc text
        // lands in DocComment and the visibility in Visibility — i.e. the by-name reads are wired to the right
        // columns and never silently shift a value into the wrong field if julie reorders the table (D6).
        using var fx = JulieDbFixture.CreateForInspect();

        var detail = ExtractReader.ReadDetail(fx.DbPath, JulieDbFixture.GetUserId)!;

        // GetUser's doc_comment and visibility are distinct strings — a column-order bug would cross them.
        Assert.Equal("Gets a user by id.", detail.DocComment);
        Assert.Equal("public", detail.Visibility);
        Assert.NotEqual(detail.DocComment, detail.Visibility);
    }

    // ---- D4 read discipline (finding-2): ExtractReader shares SqliteReadOnlyAccess's guards ----

    [Fact]
    public void ReadDetail_MissingDbFile_ThrowsFileNotFound()
    {
        // The DB file does not exist → a clear FileNotFoundException (the shared D4 read discipline), not a
        // cryptic SQLite open error.
        string missing = Path.Combine(
            Path.GetTempPath(), "miller-extractreader-missing-" + Guid.NewGuid().ToString("N"), "symbols.db");
        Assert.Throws<FileNotFoundException>(() => ExtractReader.ReadDetail(missing, "anyid"));
    }

    [Fact]
    public void ReadDetail_NonWritableDbDirectory_ThrowsActionableError()
    {
        // The WAL -shm/-wal sidecar trap (D4): under Mode=ReadOnly SQLite still needs to write the wal-index
        // into the DB's directory. ExtractReader must share SqliteReadOnlyAccess's up-front writable-dir probe
        // (consistent with SqliteSymbolReader/FreshnessReader) and surface a clear InvalidOperationException
        // instead of a cryptic SQLITE_READONLY mid-read. Simulate by chmod 0o555 on the dir (POSIX only).
        if (OperatingSystem.IsWindows())
            return; // POSIX dir-permission semantics don't apply; the live probe is exercised on Unix CI.

        using var fx = JulieDbFixture.CreateForInspect();
        string dir = fx.Directory;
        var original = File.GetUnixFileMode(dir);
        try
        {
            // r-x r-x r-x: traversable + readable, but NOT writable → the temp-file probe must fail.
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var ex = Assert.Throws<InvalidOperationException>(
                () => ExtractReader.ReadDetail(fx.DbPath, JulieDbFixture.GetUserId));
            Assert.Contains("writable", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(dir, ex.Message);
        }
        finally
        {
            File.SetUnixFileMode(dir, original); // restore so the fixture can clean up
        }
    }
}
