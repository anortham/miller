using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the D4 read layer against the synthesized julie schema. These are Miller's read-CONTRACT tests, NOT
/// a re-test of julie extraction. They assert the exact <see cref="IndexedSymbol"/> projection: deterministic
/// DocId ordinals (0..n-1 over the file_path,start_line,id ordering), opaque ids retained verbatim, and the
/// NULL discipline (NULL signature/parent_id → null; NULL start_line → 0). Error paths covered: the gate
/// fires before any read, and a non-writable DB directory surfaces a clear error (the WAL -shm/-wal trap).
/// </summary>
public sealed class SqliteSymbolReaderTests
{
    [Fact]
    public void Read_ProjectsRowsInDeterministicFilePathStartLineIdOrder_WithContiguousDocIds()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var symbols = SqliteSymbolReader.Read(fx.DbPath);

        // DocId is the row ordinal of the SELECT order (file_path, start_line, id), 0-based and contiguous.
        for (int i = 0; i < symbols.Count; i++)
            Assert.Equal(i, symbols[i].DocId);

        // The ordering the SELECT imposes, computed independently from the fixture's known rows.
        var expectedOrder = JulieDbFixture.DefaultRows
            .OrderBy(r => r.FilePath, StringComparer.Ordinal)
            .ThenBy(r => r.StartLine ?? 0)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => r.Id)
            .ToArray();

        Assert.Equal(expectedOrder, symbols.Select(s => s.SymbolId).ToArray());
        Assert.Equal(JulieDbFixture.DefaultRows.Count, symbols.Count);
    }

    [Fact]
    public void Read_RetainsOpaqueJulieIdVerbatim()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var symbols = SqliteSymbolReader.Read(fx.DbPath);

        // The 32-char MD5-hex id is the M4 join key — it must round-trip exactly (no trimming/casing).
        var userService = symbols.Single(s => s.Name == "UserService");
        Assert.Equal("a1b2c3d4e5f600112233445566778899", userService.SymbolId);
        Assert.All(symbols, s => Assert.Equal(32, s.SymbolId.Length));
    }

    [Fact]
    public void Read_NullSignature_MapsToNull()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var symbols = SqliteSymbolReader.Read(fx.DbPath);

        // DeleteUser and EMPTY were inserted with NULL signature.
        Assert.Null(symbols.Single(s => s.Name == "DeleteUser").Signature);
        Assert.Null(symbols.Single(s => s.Name == "EMPTY").Signature);
        // A non-null signature must come through verbatim.
        Assert.Equal("public User GetUser(int id)", symbols.Single(s => s.Name == "GetUser").Signature);
    }

    [Fact]
    public void Read_NullStartLine_MapsToZero()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var symbols = SqliteSymbolReader.Read(fx.DbPath);

        // TOKEN_TTL was inserted with NULL start_line (the nullable-INTEGER trap) → must read as 0, not throw.
        Assert.Equal(0, symbols.Single(s => s.Name == "TOKEN_TTL").StartLine);
        // A populated 1-based start_line comes through unchanged.
        Assert.Equal(5, symbols.Single(s => s.Name == "GetUser").StartLine);
    }

    [Fact]
    public void Read_NullParentId_MapsToNull_PopulatedParentRetained()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var symbols = SqliteSymbolReader.Read(fx.DbPath);

        // UserService is a top-level symbol: parent_id NULL.
        Assert.Null(symbols.Single(s => s.Name == "UserService").ParentId);
        // GetUser is a child of UserService: parent_id is the class's opaque id, retained verbatim.
        Assert.Equal("a1b2c3d4e5f600112233445566778899",
            symbols.Single(s => s.Name == "GetUser").ParentId);
    }

    [Fact]
    public void Read_CarriesKindLanguageAndRelativeFilePath()
    {
        using var fx = JulieDbFixture.CreateDefault();

        var symbols = SqliteSymbolReader.Read(fx.DbPath);

        var parseToken = symbols.Single(s => s.Name == "parseToken");
        Assert.Equal("function", parseToken.Kind);
        Assert.Equal("typescript", parseToken.Language);
        Assert.Equal("auth/token.ts", parseToken.FilePath); // relative-unix to root, unchanged
    }

    [Fact]
    public void ToSearchableDocument_ProjectsTheScoringFields_DroppingJoinKeys()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var getUser = SqliteSymbolReader.Read(fx.DbPath).Single(s => s.Name == "GetUser");

        var doc = getUser.ToSearchableDocument();

        Assert.Equal(getUser.DocId, doc.DocId);
        Assert.Equal("GetUser", doc.Name);
        Assert.Equal("public User GetUser(int id)", doc.Signature);
        Assert.Equal("method", doc.Kind);
        Assert.Equal("csharp", doc.Language);
        Assert.Equal("auth/UserService.cs", doc.FilePath);
        Assert.Equal(5, doc.StartLine);
        // SearchableDocument deliberately carries no julie id (that lives only on IndexedSymbol).
    }

    [Fact]
    public void Read_IncompatibleSchema_ThrowsBeforeReadingRows()
    {
        // The gate must run before the SELECT: a schema-27 DB (with rows) throws the typed gate error,
        // it does not return rows.
        using var fx = JulieDbFixture.Create(27, "1", JulieDbFixture.DefaultRows);

        var ex = Assert.Throws<IncompatibleExtractException>(() => SqliteSymbolReader.Read(fx.DbPath));
        Assert.Contains("27", ex.Message);
    }

    [Fact]
    public void Read_NonWritableDbDirectory_ThrowsActionableError()
    {
        // The WAL -shm/-wal sidecar trap (D4): under Mode=ReadOnly SQLite still needs to write the wal-index
        // into the DB's directory. Miller probes dir-writability up front and throws a clear error instead of
        // a cryptic SQLITE_READONLY mid-read. Simulate by chmod 0o555 on the dir (POSIX only).
        if (OperatingSystem.IsWindows())
            return; // POSIX dir-permission semantics don't apply; the live probe is exercised on Unix CI.

        using var fx = JulieDbFixture.CreateDefault();
        string dir = fx.Directory;
        var original = File.GetUnixFileMode(dir);
        try
        {
            // r-x r-x r-x: traversable + readable, but NOT writable → temp-file probe must fail.
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var ex = Assert.Throws<InvalidOperationException>(() => SqliteSymbolReader.Read(fx.DbPath));
            Assert.Contains("writable", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(dir, ex.Message);
        }
        finally
        {
            File.SetUnixFileMode(dir, original); // restore so the fixture can clean up
        }
    }

    [Fact]
    public void Read_MissingDbFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(Path.GetTempPath(), "miller-nope-" + Guid.NewGuid().ToString("N"), "symbols.db");

        // A typed, named error beats a cryptic SQLITE_CANTOPEN — the dir doesn't even exist.
        var ex = Assert.Throws<FileNotFoundException>(() => SqliteSymbolReader.Read(missing));
        Assert.Contains(missing, ex.Message);
    }
}
