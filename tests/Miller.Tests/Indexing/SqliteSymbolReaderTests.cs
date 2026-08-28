using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the D4 read layer against the synthesized julie schema. These are Miller's read-CONTRACT tests, NOT
/// a re-test of julie extraction. They assert the exact <see cref="IndexedSymbol"/> projection: deterministic
/// DocId ordinals (0..n-1 over the path,start_line,symbol_id ordering), opaque ids retained verbatim, and the
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
    public void Read_EndLine_PopulatedRetained_NullMapsToZero()
    {
        // D7: the reader projects symbols.end_line (the whole-symbol span end) so the diff→symbol line-precise
        // mapping (D5) can intersect [StartLine, EndLine] against a changed range — without a per-call DB hop.
        // NULL end_line (the same nullable-INTEGER trap as start_line) must read as 0, not throw.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("c0000000000000000000000000000001", "Spanned", "method", "csharp",
                "src/Spanned.cs", "public void Spanned()", 10, null) { EndLine = 42 },
            // NULL end_line → 0 (no whole-span recorded; the diff path degrades to whole-file at the caller).
            new JulieDbFixture.SymbolRow("c0000000000000000000000000000002", "NoEnd", "method", "csharp",
                "src/NoEnd.cs", "public void NoEnd()", 3, null),
        });

        var symbols = SqliteSymbolReader.Read(fx.DbPath);

        var spanned = symbols.Single(s => s.Name == "Spanned");
        Assert.Equal(10, spanned.StartLine);
        Assert.Equal(42, spanned.EndLine);
        // NULL end_line maps to 0 (the nullable-INTEGER discipline, identical to start_line).
        Assert.Equal(0, symbols.Single(s => s.Name == "NoEnd").EndLine);
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
    public void Read_IsTest_FromTypedColumn_IsCrossLanguage()
    {
        // v1 promotes the cross-language test signal to a typed symbols.is_test column (INTEGER NOT NULL).
        // Miller reads it directly (no metadata JSON parse). Verified across go/python/csharp/rust positives +
        // negatives; the old JSON substring-trap/malformed cases are gone with ParseTestSignals (D4). The rust
        // rows pin CT revision-delta design §2: julie's test_detection.rs already flags #[test]/#[tokio::test]
        // attributed functions is_test=1 (confirmed live against julie's own self-extract), and Miller's read
        // layer is a verbatim, language-agnostic column read — no per-language branch exists to miss rust.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000001", "TestAdd", "function", "go",
                "math/add_test.go", "func TestAdd(t *testing.T)", 3, null) { IsTest = true },
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000002", "test_add", "function", "python",
                "calc/test_calc.py", "def test_add()", 2, null) { IsTest = true },
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000003", "Adds", "method", "csharp",
                "Tests/CalcTests.cs", "public void Adds()", 9, null) { IsTest = true },
            // rust: a plain #[test] fn and a #[tokio::test] async fn, both julie-flagged is_test=1.
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000004", "test_add_rejects_overflow",
                "function", "rust", "crates/calc/src/tests/add_tests.rs",
                "fn test_add_rejects_overflow()", 4, null) { IsTest = true },
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000005", "test_add_async_commits",
                "function", "rust", "crates/calc/src/tests/add_tests.rs",
                "async fn test_add_async_commits()", 11, null) { IsTest = true },
            // Non-test: column defaults to 0/false.
            new JulieDbFixture.SymbolRow("b0000000000000000000000000000001", "Add", "function", "go",
                "math/add.go", "func Add(a, b int) int", 5, null),
            new JulieDbFixture.SymbolRow("b0000000000000000000000000000002", "helper", "function", "python",
                "calc/util.py", "def helper()", 1, null) { IsTest = false },
            // rust: a non-attributed helper living in the same test module — must stay false (not over-matched).
            new JulieDbFixture.SymbolRow("b0000000000000000000000000000003", "make_fixture", "function", "rust",
                "crates/calc/src/tests/add_tests.rs", "fn make_fixture() -> Calc", 20, null) { IsTest = false },
        });

        var symbols = SqliteSymbolReader.Read(fx.DbPath);
        bool IsTestOf(string name) => symbols.Single(s => s.Name == name).IsTest;

        Assert.True(IsTestOf("TestAdd"), "go test → is_test column true");
        Assert.True(IsTestOf("test_add"), "python test → is_test column true");
        Assert.True(IsTestOf("Adds"), "csharp test → is_test column true");
        Assert.True(IsTestOf("test_add_rejects_overflow"), "rust #[test] fn → is_test column true");
        Assert.True(IsTestOf("test_add_async_commits"), "rust #[tokio::test] fn → is_test column true");
        Assert.False(IsTestOf("Add"), "default column 0 → not a test");
        Assert.False(IsTestOf("helper"), "explicit is_test=0 → not a test");
        Assert.False(IsTestOf("make_fixture"), "rust non-attributed helper in a test module → not a test");
    }

    [Fact]
    public void ReadAndReadForPaths_DeriveIdenticalRoleAndCurrencyEvidence()
    {
        using var fx = JulieDbFixture.CreateTestRoleEvidenceScenario("role");

        IReadOnlyList<IndexedSymbol> all = SqliteSymbolReader.Read(fx.DbPath);
        IReadOnlyList<IndexedSymbol> selected = SqliteSymbolReader.ReadForPaths(
            fx.DbPath,
            all.Select(static symbol => symbol.FilePath).Reverse().Append("a-current.cs").ToArray());

        Assert.Equal(all, selected);
        AssertRole(all, "Current", isTest: true, isCase: true, isContainer: true, isLifecycle: false,
            status: "current", reason: null);
        AssertRole(all, "FileStatus", isTest: true, isCase: false, isContainer: false, isLifecycle: true,
            status: "unknown", reason: "file_status");
        AssertRole(all, "Diagnostic", isTest: false, isCase: false, isContainer: true, isLifecycle: false,
            status: "unknown", reason: "parse_diagnostics");
        AssertRole(all, "Combined", isTest: true, isCase: false, isContainer: true, isLifecycle: true,
            status: "unknown", reason: "file_status_and_parse_diagnostics");
        AssertRole(all, "Unavailable", isTest: true, isCase: true, isContainer: false, isLifecycle: false,
            status: "unknown", reason: "file_evidence_unavailable");
    }

    [Fact]
    public void ReadAndReadForPaths_MissingOptionalEvidenceTables_DefaultRoleEvidenceToUnknown()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("minimal-role", "MinimalRole", "method", "csharp",
                "Minimal.cs", "void MinimalRole()", 1, null) { IsTest = true },
        });
        fx.ExecuteWrite("DROP TABLE parse_diagnostics; DROP TABLE files;");

        IReadOnlyList<IndexedSymbol> all = SqliteSymbolReader.Read(fx.DbPath);
        IReadOnlyList<IndexedSymbol> selected = SqliteSymbolReader.ReadForPaths(fx.DbPath, ["Minimal.cs"]);

        Assert.Equal(all, selected);
        AssertRole(all, "MinimalRole", isTest: true, isCase: true, isContainer: false, isLifecycle: false,
            status: "unknown", reason: "file_evidence_unavailable");
    }

    [Fact]
    public void IndexedSymbol_ManualConstruction_DefaultsNewEvidenceWithoutChangingLegacyIsTest()
    {
        var symbol = new IndexedSymbol(
            DocId: 0,
            SymbolId: "manual",
            Name: "Manual",
            Signature: null,
            Kind: "method",
            Language: "csharp",
            FilePath: "Manual.cs",
            StartLine: 1,
            EndLine: 1,
            ParentId: null,
            IsTest: true);

        Assert.True(symbol.IsTest);
        Assert.True(symbol.TestEvidence.IsTest);
        Assert.True(symbol.TestEvidence.IsCase);
        Assert.False(symbol.TestEvidence.IsContainer);
        Assert.False(symbol.TestEvidence.IsLifecycle);
        Assert.Equal("unknown", symbol.TestEvidence.Status);
        Assert.Equal("file_evidence_unavailable", symbol.TestEvidence.Reason);
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
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema + 1, JulieDbFixture.PinnedContract, JulieDbFixture.DefaultRows);

        var ex = Assert.Throws<IncompatibleExtractException>(() => SqliteSymbolReader.Read(fx.DbPath));
        Assert.Contains(JulieDbFixture.SchemaText(1), ex.Message);
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

    [Fact]
    public void ReadForPaths_FiltersBeforeOrdering_AndReturnsRequestedRowsWithContiguousDocIds()
    {
        using var fx = JulieDbFixture.CreateDefault();

        IReadOnlyList<IndexedSymbol> all = SqliteSymbolReader.Read(fx.DbPath);
        IReadOnlyList<IndexedSymbol> selected = SqliteSymbolReader.ReadForPaths(
            fx.DbPath,
            ["auth/token.ts", "core/math.rs", "missing.cs"]);

        IndexedSymbol[] expected = all
            .Where(symbol => symbol.FilePath is "auth/token.ts" or "core/math.rs")
            .Select((symbol, index) => symbol with { DocId = index })
            .ToArray();

        Assert.Equal(expected, selected);
        Assert.Equal(Enumerable.Range(0, selected.Count), selected.Select(symbol => symbol.DocId));
    }

    [Fact]
    public void ReadForPaths_UsesTwoBatchesFor501UniquePaths_DeduplicatesDuplicates_AndRenumbersResults()
    {
        var selectedRows = Enumerable.Range(0, 501)
            .Select(index => new JulieDbFixture.SymbolRow(
                index.ToString("x32"),
                "Needle" + index,
                "method",
                "csharp",
                $"selected/{index:D3}.cs",
                "void Needle()",
                index + 1,
                null))
            .ToArray();
        var rows = selectedRows
            .Prepend(new JulieDbFixture.SymbolRow(
                "ffffffffffffffffffffffffffffffff",
                "Outside",
                "method",
                "csharp",
                "aaa.cs",
                "void Outside()",
                1,
                null))
            .ToArray();
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows);
        using var snapshotSession = LegacyArtifactReadSession.Open(fx.DbPath, fx.WorkspaceRoot);

        string[] paths = selectedRows
            .Select(row => row.FilePath)
            .Reverse()
            .Concat(selectedRows.Take(7).Select(row => row.FilePath))
            .ToArray();

        using var onePathSession = new CountingReadSession(fx.DbPath, snapshotSession.Snapshot);
        Assert.Single(SqliteSymbolReader.ReadForPaths(onePathSession, [paths[0]]));
        int onePathCommandCount = onePathSession.Connection.CommandCount;

        using var selectedPathSession = new CountingReadSession(fx.DbPath, snapshotSession.Snapshot);
        IReadOnlyList<IndexedSymbol> selected = SqliteSymbolReader.ReadForPaths(selectedPathSession, paths);

        Assert.Equal(501, selected.Count);
        Assert.Equal(
            selectedRows.OrderBy(row => row.FilePath, StringComparer.Ordinal).Select(row => row.Id),
            selected.Select(symbol => symbol.SymbolId));
        Assert.Equal(Enumerable.Range(0, selected.Count), selected.Select(symbol => symbol.DocId));
        Assert.Equal(onePathCommandCount + 1, selectedPathSession.Connection.CommandCount);
    }

    private sealed class CountingReadSession : IWorkspaceReadSession
    {
        public CountingReadSession(string dbPath, WorkspaceReadSnapshot snapshot)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(dbPath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            Connection = new CountingSqliteConnection(connectionString);
            Connection.Open();
            Connection.ResetCommandCount();
            Snapshot = snapshot;
        }

        public CountingSqliteConnection Connection { get; }

        public WorkspaceReadSnapshot Snapshot { get; }

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) => query(Connection);

        public void Dispose() => Connection.Dispose();
    }

    private sealed class CountingSqliteConnection : SqliteConnection
    {
        public CountingSqliteConnection(string connectionString)
            : base(connectionString)
        {
        }

        public int CommandCount { get; private set; }

        public override SqliteCommand CreateCommand()
        {
            CommandCount++;
            return base.CreateCommand();
        }

        public void ResetCommandCount() => CommandCount = 0;
    }

    private static void AssertRole(
        IReadOnlyList<IndexedSymbol> symbols,
        string name,
        bool isTest,
        bool isCase,
        bool isContainer,
        bool isLifecycle,
        string status,
        string? reason)
    {
        IndexedSymbol symbol = symbols.Single(candidate => candidate.Name == name);
        Assert.Equal(isTest, symbol.IsTest);
        Assert.Equal(isTest, symbol.TestEvidence.IsTest);
        Assert.Equal(isCase, symbol.TestEvidence.IsCase);
        Assert.Equal(isContainer, symbol.TestEvidence.IsContainer);
        Assert.Equal(isLifecycle, symbol.TestEvidence.IsLifecycle);
        Assert.Equal(status, symbol.TestEvidence.Status);
        Assert.Equal(reason, symbol.TestEvidence.Reason);
    }
}
