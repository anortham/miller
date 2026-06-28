using System.Text.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the <c>search</c> tool's behavior (M2 §4) against the M1 synthesized fixture index: compact + json
/// rendering, <c>limit</c> + the <c>… N more</c> overflow note (never silently drop), the <c>exclude_tests</c>
/// tri-state (null/true/false), empty → a compact one-line hint, and ordering preserved (the renderer must NOT
/// re-sort — Core's score-DESC ordering is authoritative). Exercises <see cref="SearchTool.Run"/> directly
/// (the pure core the MCP method delegates to), so it stays in the fast suite.
/// </summary>
public sealed class SearchToolTests
{
    private static MillerRepositoryIndex BuildIndex(JulieDbFixture fx) =>
        MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));

    private static IContentSearchIndex ContentIndex(params (string Path, string Text)[] docs) =>
        ContentSearchProjection.Build(
            docs.Select((d, i) => new ContentDocument(i, d.Path, d.Text)).ToList());

    private static ITextContentSearchIndex TextContentIndex(params TextContentSearchHit[] hits) =>
        new StubTextContentSearchIndex(hits);

    private static TextContentSearchHit CorpusHit(
        string path,
        string contentKind,
        int line,
        string snippet,
        string language = "markdown",
        string? sourceId = null,
        string? chunkId = null,
        double score = 2.0,
        long sourceBytes = 128,
        string? containingSymbolId = null,
        string? containingSymbolName = null) =>
        new(
            sourceId ?? contentKind + ":" + path,
            chunkId ?? contentKind + ":" + path + ":1",
            contentKind,
            path,
            Url: null,
            DisplayPath: path,
            language,
            score,
            line,
            LineStart: Math.Max(1, line - 1),
            LineEnd: line + 1,
            ByteStart: 24,
            ByteEnd: 88,
            snippet,
            sourceBytes,
            containingSymbolId,
            containingSymbolName);

    private static TextContentSearchHit SourceHit(
        string path,
        int line,
        string snippet,
        string language = "csharp",
        string sourceId = "source-a",
        string chunkId = "chunk-a",
        double score = 2.0,
        long sourceBytes = 128,
        string? containingSymbolId = "sym-api",
        string? containingSymbolName = "Api.Handle") =>
        CorpusHit(
            path,
            TextContentKind.WorkspaceSource,
            line,
            snippet,
            language: language,
            sourceId: sourceId,
            chunkId: chunkId,
            score: score,
            sourceBytes: sourceBytes,
            containingSymbolId: containingSymbolId,
            containingSymbolName: containingSymbolName);

    private static IndexedSymbol Symbol(
        int docId, string symbolId, string name, string kind, string filePath, int line, string? signature = null,
        string language = "csharp") =>
        new(docId, symbolId, name, signature, kind, language, filePath, line, EndLine: line, ParentId: null, IsTest: false);

    private static JulieDbFixture FixtureWithSymbol(string workspaceId, string symbolName) =>
        JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow(
                Guid.NewGuid().ToString("N"),
                symbolName,
                "class",
                "csharp",
                $"src/{symbolName}.cs",
                $"public class {symbolName}",
                1,
                ParentId: null),
        }, workspaceId: workspaceId);

    private static JulieDbFixture FixtureWithDocCommentSymbol() =>
        JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "HasDoc", "method", "csharp",
                "src/Docs.cs", "void HasDoc()", 10, ParentId: null) { DocComment = "/// Has documentation." },
            new JulieDbFixture.SymbolRow("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "NoDoc", "method", "csharp",
                "src/Docs.cs", "void NoDoc()", 20, ParentId: null),
        });

    private static (string? WorkspaceId, string? WorkspaceRoot, bool? IndexFresh) ReadTelemetryRow(string dbPath)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT workspace_id, workspace_root, index_fresh FROM tool_telemetry LIMIT 1;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), "expected one telemetry row");
        return (
            r.IsDBNull(0) ? null : r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetInt64(2) == 1);
    }

    private static string ReadTelemetryMetadata(string dbPath)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT metadata_json FROM tool_telemetry LIMIT 1;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), "expected one telemetry row");
        return r.GetString(0);
    }

    private static (string? Op, string MetadataJson, string Outcome) ReadTelemetryOpMetadata(string dbPath)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT op, metadata_json, outcome FROM tool_telemetry LIMIT 1;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), "expected one telemetry row");
        return (r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), r.GetString(2));
    }

    private static long ReadTelemetrySourceBytes(string dbPath)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT source_bytes FROM tool_telemetry LIMIT 1;";
        object? value = cmd.ExecuteScalar();
        Assert.NotNull(value);
        return Convert.ToInt64(value);
    }

    // A search-symbol provider returning a fixed context over ANY ISymbolLookupIndex — used to drive the
    // backend-tagging telemetry path with either the on-disk FtsSymbolSearchIndex or an in-memory index
    // (the production RecordingWorkspaceIndexProvider only ever yields a MillerRepositoryIndex-backed context).
    private sealed class FixedSymbolSearchProvider
        : IWorkspaceSearchProvider, IWorkspaceContentSearchProvider, IWorkspaceTextContentSearchProvider
    {
        private readonly WorkspaceSymbolSearchContext _context;
        private readonly WorkspaceTextContentSearchContext? _textContentContext;

        public FixedSymbolSearchProvider(ISymbolLookupIndex index, string root, ITextContentSearchIndex? textContentIndex = null)
        {
            _context = new WorkspaceSymbolSearchContext(
                index, "symbols.db", "current-ws", root,
                Revision: 1, IndexFresh: true, "current", WarningText: null, DisplayId: "current-ws");
            _textContentContext = textContentIndex is null
                ? null
                : new WorkspaceTextContentSearchContext(
                    textContentIndex,
                    "content.db",
                    "current-ws",
                    root,
                    Revision: 1,
                    IndexFresh: true,
                    "current",
                    WarningText: null,
                    DisplayId: "current-ws");
        }

        public int TextContentSearchResolveCount { get; private set; }

        public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, bool ensureFresh) => _context;

        public WorkspaceContentSearchContext ResolveContentSearch(string? workspaceId, bool ensureFresh) =>
            throw new NotSupportedException("FixedSymbolSearchProvider serves symbol search only.");

        public WorkspaceTextContentSearchContext ResolveTextContentSearch(string? workspaceId, bool ensureFresh)
        {
            TextContentSearchResolveCount++;
            return _textContentContext
                ?? throw new NotSupportedException("FixedSymbolSearchProvider has no text content context.");
        }
    }

    [Theory]
    [InlineData(true, "disk")]
    [InlineData(false, "memory")]
    public void Search_RecordsServingBackend_InTelemetryMetadata(bool onDisk, string expectedBackend)
    {
        // The "disk path taken" telemetry counter (Phase 5): every symbol search stamps which backend served it
        // into the row's metadata_json, so a silent self-heal from the on-disk sidecar to the in-memory index is
        // observable (otherwise the slow path hides). disk == FtsSymbolSearchIndex, memory == any in-memory index.
        string dir = Path.Combine(Path.GetTempPath(), "miller-search-backend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string telemetryDb = Path.Combine(dir, "telemetry.db");
        var sym = Symbol(0, "probe-id", "TelemetryProbe", "class", "src/Probe.cs", 1, "public class TelemetryProbe");

        ISymbolLookupIndex index;
        if (onDisk)
        {
            string searchDb = Path.Combine(dir, "search.db");
            SearchIndexWriter.Write(searchDb, new[] { sym }, revision: 1);
            index = FtsSymbolSearchIndex.Open(searchDb);
        }
        else
        {
            index = SymbolSearchProjection.Build(new[] { sym });
        }

        var provider = new FixedSymbolSearchProvider(index, Path.Combine(dir, "root"));
        var tool = new SearchTool(provider, provider);

        try
        {
            using (var ledger = TelemetryLedger.Open(telemetryDb, "current-ws", Path.Combine(dir, "root")))
            {
                using var scope = ledger.Measure("search", op: "auto");
                string output = tool.Search("TelemetryProbe");
                Assert.Contains("TelemetryProbe", output);
            }

            using var doc = JsonDocument.Parse(ReadTelemetryMetadata(telemetryDb));
            Assert.Equal(expectedBackend, doc.RootElement.GetProperty("search_backend").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Search_RecordsModeRouteShapeAndEmptyReason_InTelemetry()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-search-shape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string telemetryDb = Path.Combine(dir, "telemetry.db");
        var provider = new FixedSymbolSearchProvider(
            SymbolSearchProjection.Build(Array.Empty<IndexedSymbol>()),
            Path.Combine(dir, "root"));
        var tool = new SearchTool(provider, provider);

        try
        {
            using (var ledger = TelemetryLedger.Open(telemetryDb, "current-ws", Path.Combine(dir, "root")))
            {
                using var scope = ledger.Measure("search", op: null);
                string output = tool.Search(
                    "NoSuchSymbol",
                    mode: "auto",
                    limit: 3,
                    file_pattern: "src/**",
                    language: "csharp");
                Assert.Contains("No results", output);
            }

            var row = ReadTelemetryOpMetadata(telemetryDb);
            Assert.Equal("auto", row.Op);
            Assert.Equal("empty", row.Outcome);
            using JsonDocument doc = JsonDocument.Parse(row.MetadataJson);
            Assert.Equal("symbols", doc.RootElement.GetProperty("route").GetString());
            Assert.Equal("compact", doc.RootElement.GetProperty("format").GetString());
            Assert.Equal("1-5", doc.RootElement.GetProperty("limit_bucket").GetString());
            Assert.False(doc.RootElement.GetProperty("has_regions").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("has_file_pattern").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("has_language").GetBoolean());
            Assert.Equal("no_symbol_hits", doc.RootElement.GetProperty("empty_reason").GetString());
            Assert.False(row.MetadataJson.Contains("NoSuchSymbol", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // A fixture proving the FULL cross-language predicate (decision-4): exclude_tests must hide BOTH a
    // path-flagged test row (tests/auth/AuthServiceTests.cs — not is_test) AND a julie-is_test row whose path
    // is NOT test-shaped (src/auth/AuthHelper.cs with the typed is_test column set, a [Fact] method julie flagged).
    // The third row is the one the path-only filter would miss; it pins the sym.IsTest branch of the predicate.
    private static JulieDbFixture FixtureWithTestPaths() => JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
    {
        new JulieDbFixture.SymbolRow("a0001122334455667788990a1b2c3d4e", "AuthService", "class", "csharp",
            "src/auth/AuthService.cs", "public class AuthService", 1, null),
        new JulieDbFixture.SymbolRow("b0001122334455667788990a1b2c3d4e", "AuthServiceTests", "class", "csharp",
            "tests/auth/AuthServiceTests.cs", "public class AuthServiceTests", 1, null),
        // julie-flagged test method in a PRODUCTION-named path: only sym.IsTest can hide this, not the path rule.
        // v1 carries the test signal in the typed is_test column (NOT a metadata-JSON parse).
        new JulieDbFixture.SymbolRow("c0001122334455667788990a1b2c3d4e", "AuthService_Smoke", "method", "csharp",
            "src/auth/AuthHelper.cs", "public void AuthService_Smoke()", 1, null)
        { IsTest = true },
    });

    [Theory]
    [InlineData(SearchToolMode.Auto)]
    [InlineData(SearchToolMode.Text)]
    [InlineData(SearchToolMode.Symbol)]
    public void Run_SymbolModes_RenderExactCompactShape(SearchToolMode mode)
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "sym-alpha", "Alpha", "method", "src/Alpha.cs", 7, "public void Alpha()"), 1.25),
            (Symbol(1, "sym-beta", "Beta", "class", "src/Beta.cs", 3), 0.5),
            (Symbol(2, "sym-gamma", "Gamma", "function", "src/Gamma.cs", 11, "Gamma()"), 0.25));

        string output = SearchTool.Run(index, "Alpha", mode, limit: 2,
            excludeTests: null, json: false, out int count, compactBanner: "workspace: target-ws");

        Assert.Equal(2, count);
        Assert.Equal(
            "workspace: target-ws\n" +
            "Definition found: Alpha\n" +
            "  src/Alpha.cs:7 (method)\n" +
            "  public void Alpha()\n" +
            "\n" +
            "Other matches:\n" +
            "\n" +
            "src/Beta.cs:3 (class)\n" +
            "… 1 more (raise limit)",
            output);
    }

    [Fact]
    public void Run_FileMode_SearchesFilePathFragments()
    {
        var index = SymbolSearchProjection.Build([
            Symbol(0, "sym-file-hit", "ActualFileSymbol", "class",
                "src/Miller.Server/Tools/SearchTool.cs", 9),
            Symbol(1, "sym-symbol-decoy", "SearchTool", "class",
                "src/NotTheFile.cs", 3),
        ]);

        string output = SearchTool.Run(index, "SearchTool.cs", SearchToolMode.File, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.Equal(1, count);
        Assert.Equal(
            "File match: src/Miller.Server/Tools/SearchTool.cs\n" +
            "  :9 ActualFileSymbol class",
            output);
    }

    [Fact]
    public void Run_FileMode_Empty_ReturnsFileRecoveryHint()
    {
        var index = SymbolSearchProjection.Build([
            Symbol(0, "sym-file-hit", "ActualFileSymbol", "class",
                "src/Miller.Server/Tools/SearchTool.cs", 9),
        ]);

        string output = SearchTool.Run(index, "does/not/exist.cs", SearchToolMode.File, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.Equal(0, count);
        Assert.StartsWith("No indexed file matches 'does/not/exist.cs'.", output.Trim());
        Assert.Contains("mode=auto", output, StringComparison.Ordinal);
        Assert.Contains("`search does/not/exist.cs` for symbols", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_FileMode_FilteredMiss_ShowsOutsideScopeHintInCompact()
    {
        var index = SymbolSearchProjection.Build([
            Symbol(0, "sym-api", "SearchWidget", "class", "src/api/SearchWidget.cs", 8, "public class SearchWidget"),
            Symbol(1, "sym-domain", "SearchWidget", "class", "src/domain/SearchWidget.cs", 9, "public class SearchWidget"),
        ]);

        string output = SearchTool.Run(index, "SearchWidget.cs", SearchToolMode.File, limit: 10,
            excludeTests: null, json: false, out int count, filePattern: "src/ui/**");

        Assert.Equal(0, count);
        // File-mode empty with out-of-scope hits reuses the filtered-miss recovery, not the bare file-miss hint.
        Assert.Contains("No results within file_pattern=src/ui/**.", output);
        Assert.Contains("Outside scope:", output);
        Assert.Contains("src/api/SearchWidget.cs", output);
        Assert.Contains("src/domain/SearchWidget.cs", output);
        Assert.DoesNotContain("No indexed file matches 'SearchWidget.cs'.", output);
    }

    [Fact]
    public void Run_FileMode_HidesImportAndModuleNoise()
    {
        var index = SymbolSearchProjection.Build([
            Symbol(0, "sym-import", "Text", "import",
                "src/Miller.Server/Tools/SearchTool.cs", 1),
            Symbol(1, "sym-module", "Tools", "module",
                "src/Miller.Server/Tools/SearchTool.cs", 2),
            Symbol(2, "sym-file-hit", "SearchTool", "class",
                "src/Miller.Server/Tools/SearchTool.cs", 42),
        ]);

        string output = SearchTool.Run(index, "SearchTool.cs", SearchToolMode.File, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.Equal(1, count);
        Assert.Equal(
            "File match: src/Miller.Server/Tools/SearchTool.cs\n" +
            "  :42 SearchTool class",
            output);
    }

    [Fact]
    public void Run_AutoMode_RoutesPathLikeQueryToFileSearch()
    {
        var index = SymbolSearchProjection.Build([
            Symbol(0, "sym-file-hit", "ActualFileSymbol", "class",
                "src/Miller.Server/Tools/SearchTool.cs", 9),
            Symbol(1, "sym-symbol-decoy", "SearchTool", "class",
                "src/NotTheFile.cs", 3),
        ]);

        string output = SearchTool.Run(index, "src/Miller.Server/Tools/SearchTool.cs", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.Equal(1, count);
        Assert.Equal(
            "File match: src/Miller.Server/Tools/SearchTool.cs\n" +
            "  :9 ActualFileSymbol class",
            output);
    }

    [Fact]
    public void Run_FileMode_GroupsMultipleFileMatches()
    {
        var index = SymbolSearchProjection.Build([
            Symbol(0, "sym-backend", "backend", "namespace",
                "src/tools/search/mod.rs", 14, "mod backend"),
            Symbol(1, "sym-content", "content_scoring_tests", "namespace",
                "src/tests/tools/search/mod.rs", 15, "mod content_scoring_tests"),
            Symbol(2, "sym-other", "Other", "class",
                "src/Other.rs", 3),
        ]);

        string output = SearchTool.Run(index, "search/mod.rs", SearchToolMode.File, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.Equal(2, count);
        Assert.Equal(
            "File matches:\n" +
            "src/tools/search/mod.rs:\n" +
            "  :14 backend namespace\n" +
            "src/tests/tools/search/mod.rs:\n" +
            "  :15 content_scoring_tests namespace",
            output);
    }

    [Fact]
    public void Run_FileMode_Json_KeepsSymbolRows()
    {
        var index = SymbolSearchProjection.Build([
            Symbol(0, "sym-file-hit", "ActualFileSymbol", "class",
                "src/Miller.Server/Tools/SearchTool.cs", 9),
        ]);

        string output = SearchTool.Run(index, "SearchTool.cs", SearchToolMode.File, limit: 10,
            excludeTests: null, json: true, out int count);

        Assert.Equal(1, count);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal("ActualFileSymbol", doc.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("src/Miller.Server/Tools/SearchTool.cs", doc.RootElement[0].GetProperty("file").GetString());
    }

    [Fact]
    public void Run_SymbolSearch_FilePatternFiltersByGlob()
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "sym-ui", "SearchWidget", "class", "src/ui/SearchWidget.cs", 7), 10.0),
            (Symbol(1, "sym-api", "SearchWidget", "class", "src/api/SearchWidget.cs", 8), 9.0));

        string output = SearchTool.Run(index, "SearchWidget", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count, filePattern: "src/ui/**");

        Assert.Equal(1, count);
        Assert.Contains("src/ui/SearchWidget.cs", output);
        Assert.DoesNotContain("src/api/SearchWidget.cs", output);
    }

    [Fact]
    public void Run_SymbolSearch_LanguageFilters()
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "sym-cs", "SearchWidget", "class", "src/SearchWidget.cs", 7, language: "csharp"), 10.0),
            (Symbol(1, "sym-ts", "SearchWidget", "interface", "src/SearchWidget.ts", 8, language: "typescript"), 9.0));

        string output = SearchTool.Run(index, "SearchWidget", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count, language: "typescript");

        Assert.Equal(1, count);
        Assert.Contains("src/SearchWidget.ts", output);
        Assert.DoesNotContain("src/SearchWidget.cs", output);
    }

    [Fact]
    public void Run_SymbolSearch_FilteredMiss_ShowsOutsideScopeHintInCompact()
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "sym-api", "SearchWidget", "class", "src/api/SearchWidget.cs", 8,
                "public class SearchWidget"), 10.0),
            (Symbol(1, "sym-domain", "SearchWidget", "class", "src/domain/SearchWidget.cs", 9,
                "public class SearchWidget"), 9.0));

        string output = SearchTool.Run(index, "SearchWidget", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count, filePattern: "src/ui/**");

        Assert.Equal(0, count);
        Assert.Contains("No results within file_pattern=src/ui/**.", output);
        Assert.Contains("Outside scope:", output);
        Assert.Contains("SearchWidget  class  src/api/SearchWidget.cs:8  public class SearchWidget", output);
        Assert.Contains("SearchWidget  class  src/domain/SearchWidget.cs:9  public class SearchWidget", output);
    }

    [Fact]
    public void Run_SymbolSearch_FilteredMiss_SuggestsNestedFilePatternWhenOutsideHitContainsScopedSegment()
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "sym-card", ".iq-card", "property", "src/AccessIQ/wwwroot/css/site.css", 205,
                ".iq-card { background: var(--surface); }", language: "css"), 10.0));

        string output = SearchTool.Run(index, ".iq-card", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count, filePattern: "wwwroot/css/**");

        Assert.Equal(0, count);
        Assert.Contains("No results within file_pattern=wwwroot/css/**.", output);
        Assert.Contains("file_pattern values match repo-relative paths", output);
        Assert.Contains("try file_pattern=**/wwwroot/css/**", output);
        Assert.Contains("src/AccessIQ/wwwroot/css/site.css", output);
        Assert.Contains("Outside scope:", output);
    }

    [Fact]
    public void Run_SymbolSearch_FilteredMissJson_RemainsEmptyArray()
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "sym-api", "SearchWidget", "class", "src/api/SearchWidget.cs", 8), 10.0));

        string output = SearchTool.Run(index, "SearchWidget", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: true, out int count, filePattern: "src/ui/**");

        Assert.Equal(0, count);
        Assert.Equal("[]", output);
    }

    [Fact]
    public void Run_SymbolJson_RendersExactCompatibilityShape()
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "sym-alpha", "Alpha", "method", "src/Alpha.cs", 7, "public void Alpha()"), 1.25),
            (Symbol(1, "sym-beta", "Beta", "class", "src/Beta.cs", 3), 0.5));

        string output = SearchTool.Run(index, "Alpha", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: true, out int count);

        Assert.Equal(2, count);
        Assert.Equal(
            "[{\"name\":\"Alpha\",\"kind\":\"method\",\"file\":\"src/Alpha.cs\",\"line\":7," +
            "\"signature\":\"public void Alpha()\",\"score\":1.25,\"symbol_id\":\"sym-alpha\"}," +
            "{\"name\":\"Beta\",\"kind\":\"class\",\"file\":\"src/Beta.cs\",\"line\":3," +
            "\"signature\":null,\"score\":0.5,\"symbol_id\":\"sym-beta\"}]",
            output);
    }

    [Fact]
    public void Run_Compact_RendersOneLinePerHit_WithNameKindFileLine_ForNonExactSearch()
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "sym-alpha", "AlphaParser", "method", "src/Alpha.cs", 7, "public void AlphaParser()"), 1.25),
            (Symbol(1, "sym-beta", "BetaParser", "class", "src/Beta.cs", 3), 0.5));

        string output = SearchTool.Run(index, "Parser", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.Equal(2, count);
        var first = output.Split('\n')[0];
        Assert.Contains("AlphaParser", first);
        Assert.Contains("method", first);
        Assert.Contains("src/Alpha.cs:7", first);
        // Compact output has no blank lines.
        Assert.DoesNotContain("\n\n", output);
    }

    [Fact]
    public void Search_DefaultLimit_RendersSixActionableRows_WithOverflowNote()
    {
        var rows = Enumerable.Range(0, 8)
            .Select(i => (
                Symbol: Symbol(i, $"sym-widget-{i}", $"Widget{i}", "class", $"src/Widget{i}.cs", i + 1,
                    $"public class Widget{i}"),
                Score: 10.0 - i))
            .ToArray();
        var index = new StubSymbolSearchIndex(rows);
        var provider = new FixedSymbolSearchProvider(index, Path.Combine(Path.GetTempPath(), "miller-search-root"));
        var tool = new SearchTool(provider, provider);

        string output = tool.Search("Widget");

        Assert.Contains("Widget5  class  src/Widget5.cs:6", output);
        Assert.DoesNotContain("Widget6", output);
        Assert.Contains("… 2 more (raise limit)", output);
    }

    [Fact]
    public void Run_Empty_ReturnsNoResultsSentinel()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "ZZTopNothingMatches", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.Equal(0, count);
        Assert.Equal(
            "No results. Try a shorter symbol query, mode=source for code text, or mode=content for docs/config.",
            output.Trim());
    }

    [Fact]
    public void Run_Empty_WithNearMiss_RendersCompactSuggestions()
    {
        var candidate = Symbol(
            0,
            "aa11223344556677889900aabbccdd91",
            "ReadReferencesAsync",
            "method",
            "src/References/Reader.cs",
            12,
            "Task ReadReferencesAsync()");
        var index = StubSymbolSearchIndex.WithSymbolsOnly(candidate);

        string output = SearchTool.Run(index, "ReadReferencesAsyncc", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.Equal(0, count);
        Assert.Contains("No results.", output);
        Assert.Contains("Try: ReadReferencesAsync (src/References/Reader.cs:12)", output);
    }

    [Fact]
    public void Run_EmptyJson_WithNearMiss_ReturnsSuggestionsObject()
    {
        var candidate = Symbol(
            0,
            "aa11223344556677889900aabbccdd91",
            "ReadReferencesAsync",
            "method",
            "src/References/Reader.cs",
            12,
            "Task ReadReferencesAsync()");
        var index = StubSymbolSearchIndex.WithSymbolsOnly(candidate);

        string output = SearchTool.Run(index, "ReadReferencesAsyncc", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: true, out int count);

        Assert.Equal(0, count);
        using var doc = JsonDocument.Parse(output);
        JsonElement root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(0, root.GetProperty("results").GetArrayLength());
        JsonElement suggestion = root.GetProperty("suggestions")[0];
        Assert.Equal("ReadReferencesAsync", suggestion.GetProperty("name").GetString());
        Assert.Equal("src/References/Reader.cs", suggestion.GetProperty("file").GetString());
    }

    [Fact]
    public void Run_Json_IsAParseableArrayWithTheExpectedShape()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "GetUser", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: true, out int count);

        Assert.True(count >= 1);
        using var doc = JsonDocument.Parse(output);
        var arr = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        var first = arr[0];
        Assert.Equal("GetUser", first.GetProperty("name").GetString());
        Assert.Equal("method", first.GetProperty("kind").GetString());
        Assert.Equal("auth/UserService.cs", first.GetProperty("file").GetString());
        Assert.Equal(5, first.GetProperty("line").GetInt32());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("symbol_id").GetString()));
        Assert.True(first.TryGetProperty("score", out _));
    }

    [Fact]
    public void Run_OverLimit_AppendsMoreNote_AndDoesNotDrop()
    {
        // 5 symbols all share a token; limit=2 must show 2 rows + a "… N more" note.
        var rows = Enumerable.Range(0, 5).Select(i => new JulieDbFixture.SymbolRow(
            $"{i:x32}".PadLeft(32, '0')[..32], $"Widget{i}", "class", "csharp",
            $"src/Widget{i}.cs", $"public class Widget{i}", 1, null)).ToArray();
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows);
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "widget", SearchToolMode.Auto, limit: 2,
            excludeTests: false, json: false, out int count);

        // count is the number actually rendered (the page), the note reports the remainder.
        Assert.Equal(2, count);
        Assert.Contains("more", output);
        Assert.Contains("raise limit", output);
    }

    [Fact]
    public void Run_ExcludeTestsTrue_AlwaysHidesTestPaths()
    {
        using var fx = FixtureWithTestPaths();
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "AuthService", SearchToolMode.Auto, limit: 10,
            excludeTests: true, json: false, out int count);

        Assert.Contains("src/auth/AuthService.cs", output);
        Assert.DoesNotContain("tests/auth/AuthServiceTests.cs", output);
    }

    [Fact]
    public void Run_ExcludeTestsTrue_HidesJulieIsTestRow_InNonTestPath()
    {
        // The sym.IsTest branch of the predicate: a [Fact]-style method julie flagged is_test, living in a
        // PRODUCTION-named file (src/auth/AuthHelper.cs). The path rule would keep it; sym.IsTest must hide it.
        // This case fails if SearchTool consults only IsTestPath.Check and ignores the persisted is_test signal.
        using var fx = FixtureWithTestPaths();
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "AuthService", SearchToolMode.Auto, limit: 10,
            excludeTests: true, json: false, out _);

        Assert.DoesNotContain("AuthService_Smoke", output);
        Assert.DoesNotContain("src/auth/AuthHelper.cs", output);
        // The non-test production symbol is still present.
        Assert.Contains("src/auth/AuthService.cs", output);
    }

    [Fact]
    public void Run_ExcludeTestsFalse_AlwaysIncludesTestPaths()
    {
        using var fx = FixtureWithTestPaths();
        var index = BuildIndex(fx);

        string output = SearchTool.Run(index, "AuthService", SearchToolMode.Auto, limit: 10,
            excludeTests: false, json: false, out int count);

        Assert.Contains("src/auth/AuthService.cs", output);
        Assert.Contains("tests/auth/AuthServiceTests.cs", output);
        // exclude_tests=false includes even the julie-is_test row.
        Assert.Contains("AuthService_Smoke", output);
    }

    [Fact]
    public void Run_ExcludeTestsNull_HidesTestPaths_ForNaturalLanguagePhrase()
    {
        using var fx = FixtureWithTestPaths();
        var index = BuildIndex(fx);

        // Multi-word NL phrase, no test/def intent → default hides test paths.
        string output = SearchTool.Run(index, "auth service", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out _);

        Assert.Contains("src/auth/AuthService.cs", output);
        Assert.DoesNotContain("tests/auth/AuthServiceTests.cs", output);
    }

    [Fact]
    public void Run_ExcludeTestsNull_KeepsTestPaths_ForSingleIdentifierQuery()
    {
        using var fx = FixtureWithTestPaths();
        var index = BuildIndex(fx);

        // A single identifier-ish token is NOT an NL phrase → null defaults to include (don't auto-hide).
        string output = SearchTool.Run(index, "AuthService", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out _);

        Assert.Contains("src/auth/AuthService.cs", output);
        Assert.Contains("tests/auth/AuthServiceTests.cs", output);
    }

    [Fact]
    public void Run_ExcludeTestsNull_KeepsTestPaths_WhenPhraseHasTestIntent()
    {
        using var fx = FixtureWithTestPaths();
        var index = BuildIndex(fx);

        // An NL phrase that explicitly mentions "test" intent → do not auto-hide test paths.
        string output = SearchTool.Run(index, "auth service test", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out _);

        Assert.Contains("tests/auth/AuthServiceTests.cs", output);
    }

    [Fact]
    public void Run_NaturalLanguagePhrase_HidesImportAndModuleNoise()
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "import-row", "collapsed-trigram", "import", "src/Imports.cs", 1), 10.0),
            (Symbol(1, "module-row", "collapsed-trigram", "module", "src/Module.cs", 1), 9.0),
            (Symbol(2, "class-row", "CollapsedTrigramDesign", "class", "src/SearchDesign.cs", 12), 1.0));

        string output = SearchTool.Run(index, "collapsed trigram", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.Equal(1, count);
        Assert.Contains("CollapsedTrigramDesign  class  src/SearchDesign.cs:12", output);
        Assert.DoesNotContain("src/Imports.cs", output);
        Assert.DoesNotContain("src/Module.cs", output);
    }

    [Fact]
    public void Run_NaturalLanguagePhrase_OverfetchesPastImportAndModuleNoise()
    {
        var rows = Enumerable.Range(0, 75)
            .Select(i => (
                Symbol(i, $"import-row-{i}", "collapsed-trigram", "import", $"src/Imports{i}.cs", 1),
                Score: 100.0 - i))
            .Append((
                Symbol(75, "class-row", "CollapsedTrigramDesign", "class", "src/SearchDesign.cs", 12),
                Score: 1.0))
            .ToArray();
        var index = new StubSymbolSearchIndex(rows);

        string output = SearchTool.Run(index, "collapsed trigram", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.Equal(1, count);
        Assert.Contains("CollapsedTrigramDesign  class  src/SearchDesign.cs:12", output);
    }

    [Fact]
    public void Run_SingleIdentifierQuery_KeepsImportAndModuleRows()
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "import-row", "React", "import", "src/App.tsx", 1), 10.0),
            (Symbol(1, "module-row", "React", "module", "src/react.ts", 1), 9.0),
            (Symbol(2, "class-row", "ReactWidget", "class", "src/ReactWidget.cs", 12), 1.0));

        string output = SearchTool.Run(index, "React", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int count);

        Assert.Equal(3, count);
        Assert.Contains("React  import  src/App.tsx:1", output);
        Assert.Contains("React  module  src/react.ts:1", output);
        Assert.Contains("ReactWidget  class  src/ReactWidget.cs:12", output);
    }

    [Fact]
    public void Run_Compact_PromotesExactDefinitionAndGroupsOtherMatches()
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "struct-row", "FastSearchTool", "struct", "crates/julie-tools/src/search/mod.rs", 58,
                "pub struct FastSearchTool"), 30.0),
            (Symbol(1, "import-row", "FastSearchTool", "import", "crates/julie-tools/src/lib.rs", 26,
                "pub use search::FastSearchTool;"), 20.0),
            (Symbol(2, "import-row-two", "FastSearchTool", "import", "src/tools/mod.rs", 31,
                "pub use search::FastSearchTool;"), 20.0),
            (Symbol(3, "module-row", "search", "module", "src/tools/mod.rs", 1,
                "pub mod search;"), 10.0));

        string compact = SearchTool.Run(index, "FastSearchTool", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: false, out int compactCount);
        string json = SearchTool.Run(index, "FastSearchTool", SearchToolMode.Auto, limit: 10,
            excludeTests: null, json: true, out int jsonCount);

        Assert.Equal(4, compactCount);
        Assert.Equal(4, jsonCount);
        Assert.Contains(
            "Definition found: FastSearchTool\n" +
            "  crates/julie-tools/src/search/mod.rs:58 (struct)\n" +
            "  pub struct FastSearchTool",
            compact);
        Assert.Contains("Other matches:", compact);
        Assert.Contains("crates/julie-tools/src/lib.rs:26 (import) low_signal", compact);
        Assert.Contains(
            "src/tools/mod.rs:\n" +
            "  :31 (import) low_signal\n" +
            "  :1 (module) low_signal",
            compact);
        Assert.DoesNotContain("FastSearchTool  import", compact);
        Assert.DoesNotContain("FastSearchTool  module", compact);
        Assert.DoesNotContain("pub use search::FastSearchTool;", compact);
        Assert.DoesNotContain("pub mod search;", compact);
        Assert.Contains("\"signature\":\"pub use search::FastSearchTool;\"", json);
        Assert.Contains("\"signature\":\"pub mod search;\"", json);
    }

    [Fact]
    public void Run_Compact_GroupsRepeatedFiles_AndKeepsDistinctFilesFlat()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("c0000000000000000000000000000001", "ParseHeader", "method", "csharp",
                "src/Parser.cs", "string ParseHeader()", 10, ParentId: null),
            new JulieDbFixture.SymbolRow("c0000000000000000000000000000002", "ParseBody", "method", "csharp",
                "src/Parser.cs", "string ParseBody()", 20, ParentId: null),
            new JulieDbFixture.SymbolRow("c0000000000000000000000000000003", "ParseFooter", "method", "csharp",
                "src/Render.cs", "string ParseFooter()", 30, ParentId: null),
        });
        var index = BuildIndex(fx);

        string grouped = SearchTool.Run(index, "Parse", SearchToolMode.Symbol, limit: 10,
            excludeTests: false, json: false, out _);

        // src/Parser.cs repeats, so its path renders once with rank-ordered rows under it.
        Assert.Contains("src/Parser.cs:\n", grouped);
        Assert.Contains(":10 ParseHeader method  string ParseHeader()", grouped);
        Assert.Contains(":20 ParseBody method  string ParseBody()", grouped);
        Assert.Contains("src/Render.cs:\n", grouped);
        Assert.Equal(1, grouped.Split("src/Parser.cs").Length - 1);

        string flat = SearchTool.Run(index, "ParseFooter", SearchToolMode.Symbol, limit: 10,
            excludeTests: false, json: false, out _);

        // A page of all-distinct files stays one-line-per-hit (no group header).
        Assert.DoesNotContain("src/Render.cs:\n", flat);
        Assert.Contains("src/Render.cs:30", flat);
    }

    [Fact]
    public void Run_PreservesIndexOrdering_DoesNotReSort()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);

        // Compare the tool's rendered order against the raw index order for the same query. Compact output
        // groups repeated files (path printed once), so the pinned invariant is: group order follows each
        // file's best hit, and rows inside a group keep index order — still filter-only, never re-scored.
        var rawHits = index.Search("http", limit: 20)
            .Select(h => index.Resolve(h.Document.DocId))
            .ToList();
        var expectedNames = rawHits
            .GroupBy(s => s.FilePath) // LINQ GroupBy preserves first-appearance key order
            .SelectMany(g => g)
            .Select(s => s.Name)
            .ToList();

        string output = SearchTool.Run(index, "http", SearchToolMode.Auto, limit: 20,
            excludeTests: false, json: false, out _);

        var renderedNames = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('…') && !l.Contains("more") && !l.EndsWith(':'))
            .Select(l => l.StartsWith("  :")
                ? l.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries)[1] // grouped row: ":line Name kind…"
                : l.Split("  ", StringSplitOptions.RemoveEmptyEntries)[0].Trim())    // flat row: "Name  kind  path:line…"
            .ToList();

        Assert.Equal(expectedNames, renderedNames);
    }

    [Fact]
    public void Search_ExplicitWorkspaceId_DefaultsEnsureFreshTrue_AndRoutesToTargetIndex()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        using var target = FixtureWithSymbol("target-ws", "TargetOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(
                BuildIndex(target),
                target.DbPath,
                "target-ws",
                targetRoot,
                displayId: "target-111111111111")));
        var tool = new SearchTool(provider, provider);

        string output = tool.Search("TargetOnly", workspace_id: "target-ws");

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.True(provider.LastEnsureFresh);
        Assert.StartsWith("workspace: target-111111111111\n", output);
        Assert.DoesNotContain(targetRoot, output);
        Assert.Contains("TargetOnly", output);
    }

    [Fact]
    public void Search_EnsureFreshFalse_PassesThrough_AndTelemetryUsesProviderWorkspaceAndFreshness()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        using var target = FixtureWithSymbol("target-ws", "TargetOnly");
        string dir = Path.Combine(Path.GetTempPath(), "miller-search-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string telemetryDb = Path.Combine(dir, "telemetry.db");
        string currentRoot = Path.Combine(dir, "current");
        string targetRoot = Path.Combine(dir, "target");
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(
                BuildIndex(target),
                target.DbPath,
                "target-ws",
                targetRoot,
                indexFresh: false,
                freshnessStatus: "loaded_existing",
                displayId: "target-111111111111")));
        var tool = new SearchTool(provider, provider);

        try
        {
            using (var ledger = TelemetryLedger.Open(telemetryDb, workspaceId: "current-ws", currentRoot))
            {
                using var scope = ledger.Measure("search", op: "auto");
                string output = tool.Search("TargetOnly", workspace_id: "target-ws", ensure_fresh: false);

                Assert.Equal("target-ws", provider.LastWorkspaceId);
                Assert.False(provider.LastEnsureFresh);
                Assert.StartsWith("workspace: target-111111111111\n", output);
                Assert.Contains("freshness: loaded_existing", output);
                Assert.DoesNotContain(targetRoot, output);
                Assert.Contains("TargetOnly", output);
            }

            var row = ReadTelemetryRow(telemetryDb);
            Assert.Equal("target-ws", row.WorkspaceId);
            Assert.Equal(targetRoot, row.WorkspaceRoot);
            Assert.False(row.IndexFresh);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ----- mode=content (phase 3) -----

    [Fact]
    public void RunContent_Compact_RendersPathLineAndSnippet()
    {
        var index = ContentIndex(
            ("docs/guide.md", "# Guide\nThe freshness gate verifies blake3 before reading.\nMore text."));

        string output = SearchTool.RunContent(index, "freshness", limit: 10, json: false, out int count);

        Assert.Equal(1, count);
        Assert.Equal(
            "docs/guide.md:2\n" +
            "    # Guide\n" +
            "    The freshness gate verifies blake3 before reading.\n" +
            "    More text.",
            output);
    }

    [Fact]
    public void RunContent_Empty_ReturnsTextContentRecoveryHint()
    {
        var index = ContentIndex(("docs/guide.md", "nothing relevant on this page"));

        string output = SearchTool.RunContent(index, "zzzznotpresent", limit: 10, json: false, out int count);

        Assert.Equal(0, count);
        Assert.StartsWith("No text hits.", output.Trim());
        Assert.Contains("workspace refresh", output, StringComparison.Ordinal);
        Assert.Contains("`search zzzznotpresent` for symbols", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunContent_Json_HasContentShape_NeverFakeSymbols()
    {
        var index = ContentIndex(("docs/guide.md", "alpha freshness beta\n"));

        string output = SearchTool.RunContent(index, "freshness", limit: 10, json: true, out int count);

        Assert.True(count >= 1);
        using var doc = JsonDocument.Parse(output);
        var first = doc.RootElement[0];
        Assert.Equal("docs/guide.md", first.GetProperty("file").GetString());
        Assert.Equal(1, first.GetProperty("line").GetInt32());
        Assert.True(first.TryGetProperty("score", out _));
        Assert.False(string.IsNullOrEmpty(first.GetProperty("snippet").GetString()));
        // Content hits are a distinct result kind — NOT fake symbols.
        Assert.False(first.TryGetProperty("symbol_id", out _));
        Assert.False(first.TryGetProperty("kind", out _));
        Assert.False(first.TryGetProperty("name", out _));
    }

    [Fact]
    public void RunContentCorpus_Json_HasLegacyContentShape_NeverCorpusShape()
    {
        var index = TextContentIndex(CorpusHit(
            "docs/guide.md",
            TextContentKind.WorkspaceDocs,
            line: 2,
            snippet: "# Guide\nalpha freshness beta"));

        string output = SearchTool.RunContentCorpus(index, "freshness", limit: 10, json: true, out int count);

        Assert.Equal(1, count);
        using var doc = JsonDocument.Parse(output);
        var first = doc.RootElement[0];
        Assert.Equal("docs/guide.md", first.GetProperty("file").GetString());
        Assert.Equal(2, first.GetProperty("line").GetInt32());
        Assert.True(first.TryGetProperty("score", out _));
        Assert.False(string.IsNullOrEmpty(first.GetProperty("snippet").GetString()));
        Assert.False(first.TryGetProperty("source_id", out _));
        Assert.False(first.TryGetProperty("chunk_id", out _));
        Assert.False(first.TryGetProperty("content_kind", out _));
        Assert.False(first.TryGetProperty("symbol_id", out _));
        Assert.False(first.TryGetProperty("kind", out _));
        Assert.False(first.TryGetProperty("name", out _));
    }

    [Fact]
    public void RunContent_OverLimit_AppendsMoreNote_AndDoesNotDrop()
    {
        var docs = Enumerable.Range(0, 5)
            .Select(i => ($"docs/d{i}.md", $"widget content number {i}"))
            .ToArray();
        var index = ContentIndex(docs);

        string output = SearchTool.RunContent(index, "widget", limit: 2, json: false, out int count);

        Assert.Equal(2, count);
        Assert.Contains("more", output);
        Assert.Contains("raise limit", output);
    }

    [Fact]
    public void RunContent_FilePatternAndLanguageFilters()
    {
        var index = ContentSearchProjection.Build([
            new ContentDocument(0, "docs/guide.md", "alpha scoped content", "markdown"),
            new ContentDocument(1, "notes/guide.txt", "alpha scoped content", "text"),
        ]);

        string output = SearchTool.RunContent(index, "alpha", limit: 10, json: false, out int count,
            filePattern: "docs/**", language: "markdown");

        Assert.Equal(1, count);
        Assert.Contains("docs/guide.md", output);
        Assert.DoesNotContain("notes/guide.txt", output);
    }

    [Fact]
    public void RunContent_FilteredMiss_ShowsOutsideScopeHintInCompact()
    {
        var index = ContentSearchProjection.Build([
            new ContentDocument(0, "notes/guide.txt", "alpha scoped content", "text"),
            new ContentDocument(1, "notes/reference.txt", "alpha scoped reference", "text"),
        ]);

        string output = SearchTool.RunContent(index, "alpha", limit: 10, json: false, out int count,
            filePattern: "docs/**");

        Assert.Equal(0, count);
        Assert.Contains("No results within file_pattern=docs/**.", output);
        Assert.Contains("Outside scope:", output);
        Assert.Contains("notes/guide.txt:1", output);
        Assert.Contains("alpha scoped content", output);
        Assert.Contains("notes/reference.txt:1", output);
    }

    [Fact]
    public void Search_ModeContent_RoutesToTextContentProvider_AndRendersLegacyContentHits()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            currentTextContent: ReadToolRoutingTestSupport.TextContentContextFor(
                TextContentIndex(CorpusHit("docs/none.md", TextContentKind.WorkspaceDocs, 1, "irrelevant")),
                current.DbPath,
                "current-ws",
                currentRoot),
            textContentTargets: new[]
            {
                ("target-ws", ReadToolRoutingTestSupport.TextContentContextFor(
                    TextContentIndex(CorpusHit(
                        "docs/guide.md",
                        TextContentKind.WorkspaceDocs,
                        line: 2,
                        snippet: "# Guide\nThe freshness gate verifies blake3.\n")),
                    "target.db", "target-ws", targetRoot)),
            });
        var tool = new SearchTool(provider, provider, provider, provider);

        string output = tool.Search("freshness", mode: "content", workspace_id: "target-ws");

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.True(provider.LastEnsureFresh); // explicit workspace_id defaults ensure_fresh=true
        Assert.Equal(1, provider.TextContentSearchResolveCount);
        Assert.Equal(0, provider.ContentSearchResolveCount);
        Assert.Equal(0, provider.SymbolSearchResolveCount); // content mode never touches the symbol provider
        Assert.StartsWith("workspace: target-ws\n", output);
        Assert.DoesNotContain(targetRoot, output);
        Assert.Contains("docs/guide.md:2", output);
        Assert.Contains("The freshness gate verifies blake3", output);
        Assert.DoesNotContain(TextContentKind.WorkspaceDocs, output);
    }

    [Fact]
    public void Search_ModeContent_RecordsRealSourceBytesFromContentCorpusIndex()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string dir = Path.Combine(Path.GetTempPath(), "miller-content-source-bytes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string telemetryDb = Path.Combine(dir, "telemetry.db");
        string currentRoot = Path.Combine(dir, "current");
        const string guideText = "# Guide\nThe freshness gate verifies blake3.\n";
        const string apiText = "# API\nThe context bundle stays compact.\n";
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            currentTextContent: ReadToolRoutingTestSupport.TextContentContextFor(
                TextContentIndex(
                    CorpusHit("docs/guide.md", TextContentKind.WorkspaceDocs, 2, guideText,
                        sourceBytes: Encoding.UTF8.GetByteCount(guideText)),
                    CorpusHit("src/api.md", TextContentKind.WorkspaceSource, 2, apiText,
                        sourceBytes: Encoding.UTF8.GetByteCount(apiText))),
                current.DbPath,
                "current-ws",
                currentRoot),
            textContentTargets: Array.Empty<(string, WorkspaceTextContentSearchContext)>());
        var tool = new SearchTool(provider, provider, provider, provider);

        try
        {
            using (var ledger = TelemetryLedger.Open(telemetryDb, workspaceId: "current-ws", currentRoot))
            {
                using var scope = ledger.Measure("search", op: "content");
                string output = tool.Search("freshness", mode: "content");
                Assert.Contains("docs/guide.md", output);
            }

            long expectedSourceBytes = Encoding.UTF8.GetByteCount(guideText);
            Assert.Equal(expectedSourceBytes, ReadTelemetrySourceBytes(telemetryDb));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Search_ModeDocs_AliasesContent()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            currentTextContent: ReadToolRoutingTestSupport.TextContentContextFor(
                TextContentIndex(CorpusHit("docs/readme.md", TextContentKind.WorkspaceDocs, 1, "alpha docsalias beta")),
                current.DbPath,
                "current-ws",
                currentRoot),
            textContentTargets: Array.Empty<(string, WorkspaceTextContentSearchContext)>());
        var tool = new SearchTool(provider, provider, provider, provider);

        string output = tool.Search("docsalias", mode: "docs");

        Assert.Equal(1, provider.TextContentSearchResolveCount);
        Assert.Equal(0, provider.ContentSearchResolveCount);
        Assert.Equal(0, provider.SymbolSearchResolveCount);
        Assert.Contains("docs/readme.md", output);
    }

    [Fact]
    public void Search_ModeContent_ExcludeTestsIsNoOp()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            currentTextContent: ReadToolRoutingTestSupport.TextContentContextFor(
                TextContentIndex(CorpusHit("tests/guide.md", TextContentKind.WorkspaceDocs, 1, "alpha freshness beta")),
                current.DbPath,
                "current-ws",
                currentRoot),
            textContentTargets: Array.Empty<(string, WorkspaceTextContentSearchContext)>());
        var tool = new SearchTool(provider, provider, provider, provider);

        string withExclude = tool.Search("freshness", mode: "content", exclude_tests: true);
        string withoutExclude = tool.Search("freshness", mode: "content", exclude_tests: false);

        Assert.Equal(withExclude, withoutExclude); // exclude_tests does not filter content results
        Assert.Contains("tests/guide.md", withExclude);
    }

    // ----- mode=source (content corpus source-body search) -----

    [Fact]
    public void ParseMode_Source_UsesExplicitSourceBodySearchMode()
    {
        Assert.Equal(SearchToolMode.Source, SearchTool.ParseMode("source"));
    }

    [Theory]
    [InlineData("external", SearchToolMode.External)]
    [InlineData("web", SearchToolMode.Web)]
    [InlineData("all-text", SearchToolMode.AllText)]
    public void ParseMode_TextCorpusModes_AreExplicitOnly(string mode, SearchToolMode expected)
    {
        Assert.Equal(expected, SearchTool.ParseMode(mode));
    }

    [Fact]
    public void ParseMode_Markers_UsesMarkerAuditMode()
    {
        Assert.Equal("Markers", SearchTool.ParseMode("markers").ToString());
    }

    [Fact]
    public void RunTextContent_Compact_RendersSourceHitMetadataAndSnippet()
    {
        var index = TextContentIndex(SourceHit(
            "src/Api.cs",
            line: 42,
            snippet: "public void Handle()\nthrow new InvalidOperationException(\"KnownSourceError\");",
            containingSymbolName: "Api.Handle"));

        string output = SearchTool.RunTextContent(
            index,
            "KnownSourceError",
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: false,
            json: false,
            out int count);

        Assert.Equal(1, count);
        Assert.Equal(
            "src/Api.cs:42  workspace_source  Api.Handle\n" +
            "    public void Handle()\n" +
            "    throw new InvalidOperationException(\"KnownSourceError\");",
            output);
    }

    [Fact]
    public void RunTextContent_AllText_CanSearchMultipleContentKinds()
    {
        var index = TextContentIndex(
            CorpusHit("src/Api.cs", TextContentKind.WorkspaceSource, 5, "Needle in source"),
            CorpusHit("docs/guide.md", TextContentKind.WorkspaceDocs, 7, "Needle in docs"),
            CorpusHit("ci.log", TextContentKind.ExternalFile, 2, "Needle in log"));

        string output = SearchTool.RunTextContent(
            index,
            "Needle",
            [TextContentKind.WorkspaceSource, TextContentKind.WorkspaceDocs, TextContentKind.ExternalFile],
            limit: 10,
            excludeTests: false,
            json: false,
            out int count,
            out _);

        Assert.Equal(3, count);
        Assert.Contains("src/Api.cs:5  workspace_source", output);
        Assert.Contains("docs/guide.md:7  workspace_docs", output);
        Assert.Contains("ci.log:2  external_file", output);
    }

    [Fact]
    public void RunTextContent_Empty_WorkspaceKind_PointsAtWorkspaceRefresh()
    {
        var index = TextContentIndex();

        string output = SearchTool.RunTextContent(
            index,
            "zzzznotpresent",
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: false,
            json: false,
            out int count);

        Assert.Equal(0, count);
        Assert.StartsWith("No text hits.", output.Trim());
        Assert.Contains("workspace refresh", output, StringComparison.Ordinal);
        Assert.Contains("`search zzzznotpresent` for symbols", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunTextContent_Empty_ImportedKind_PointsAtContentList()
    {
        var index = TextContentIndex();

        string output = SearchTool.RunTextContent(
            index,
            "zzzznotpresent",
            TextContentKind.ExternalFile,
            limit: 10,
            excludeTests: false,
            json: false,
            out int count,
            out long _);

        Assert.Equal(0, count);
        Assert.StartsWith("No text hits.", output.Trim());
        Assert.Contains("content list", output, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace refresh", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunTextContent_Empty_AllText_ListsBothRecoveryPaths()
    {
        var index = TextContentIndex();

        string output = SearchTool.RunTextContent(
            index,
            "zzzznotpresent",
            new[] { TextContentKind.WorkspaceSource, TextContentKind.ExternalFile },
            limit: 10,
            excludeTests: false,
            json: false,
            out int count,
            out long _);

        Assert.Equal(0, count);
        Assert.Contains("workspace refresh", output, StringComparison.Ordinal);
        Assert.Contains("content list", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RunTextContent_Json_HasSourceCorpusShape_NotSymbolShape()
    {
        var index = TextContentIndex(SourceHit(
            "src/Api.cs",
            line: 42,
            snippet: "throw new InvalidOperationException(\"KnownSourceError\");",
            sourceId: "src:api",
            chunkId: "src:api:0003",
            sourceBytes: 4096,
            containingSymbolId: "sym-handle",
            containingSymbolName: "Api.Handle"));

        string output = SearchTool.RunTextContent(
            index,
            "KnownSourceError",
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: false,
            json: true,
            out int count);

        Assert.Equal(1, count);
        using var doc = JsonDocument.Parse(output);
        JsonElement first = doc.RootElement[0];
        Assert.Equal("src:api", first.GetProperty("source_id").GetString());
        Assert.Equal("src:api:0003", first.GetProperty("chunk_id").GetString());
        Assert.Equal("workspace_source", first.GetProperty("content_kind").GetString());
        Assert.Equal("src/Api.cs", first.GetProperty("path").GetString());
        Assert.Equal("csharp", first.GetProperty("language").GetString());
        Assert.Equal(42, first.GetProperty("line").GetInt32());
        Assert.Equal(41, first.GetProperty("line_start").GetInt32());
        Assert.Equal(43, first.GetProperty("line_end").GetInt32());
        Assert.Equal(24, first.GetProperty("byte_start").GetInt64());
        Assert.Equal(88, first.GetProperty("byte_end").GetInt64());
        Assert.Equal(4096, first.GetProperty("source_bytes").GetInt64());
        Assert.Equal("sym-handle", first.GetProperty("containing_symbol_id").GetString());
        Assert.Equal("Api.Handle", first.GetProperty("containing_symbol_name").GetString());
        Assert.True(first.TryGetProperty("score", out _));
        Assert.False(first.TryGetProperty("symbol_id", out _));
        Assert.False(first.TryGetProperty("name", out _));
    }

    [Fact]
    public void RunTextContent_FilePatternLanguageAndExcludeTestsFilterSourceHits()
    {
        var index = TextContentIndex(
            SourceHit("src/ui/Panel.ts", 12, "KnownSourceError", language: "typescript", sourceId: "prod"),
            SourceHit("src/api/Panel.cs", 12, "KnownSourceError", language: "csharp", sourceId: "api"),
            SourceHit("tests/ui/PanelTests.ts", 12, "KnownSourceError", language: "typescript", sourceId: "test"));

        string output = SearchTool.RunTextContent(
            index,
            "KnownSourceError",
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: true,
            json: false,
            out int count,
            filePattern: "src/ui/**",
            language: "typescript");

        Assert.Equal(1, count);
        Assert.Contains("src/ui/Panel.ts", output);
        Assert.DoesNotContain("src/api/Panel.cs", output);
        Assert.DoesNotContain("tests/ui/PanelTests.ts", output);
    }

    [Fact]
    public void RunTextContent_FilteredMiss_SuggestsNestedFilePatternWhenOutsideHitContainsScopedSegment()
    {
        var index = TextContentIndex(
            SourceHit("src/AccessIQ/wwwroot/css/site.css", 205, ".iq-card { background: var(--surface); }",
                language: "css", containingSymbolName: ".iq-card"));

        string output = SearchTool.RunTextContent(
            index,
            ".iq-card",
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: false,
            json: false,
            out int count,
            filePattern: "wwwroot/css/**");

        Assert.Equal(0, count);
        Assert.Contains("No results within file_pattern=wwwroot/css/**.", output);
        Assert.Contains("file_pattern values match repo-relative paths", output);
        Assert.Contains("try file_pattern=**/wwwroot/css/**", output);
        Assert.Contains("src/AccessIQ/wwwroot/css/site.css", output);
        Assert.Contains("Outside scope:", output);
    }

    [Fact]
    public void Search_ModeSource_RoutesToTextContentProvider_AndRendersSourceHits()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            currentTextContent: ReadToolRoutingTestSupport.TextContentContextFor(
                TextContentIndex(SourceHit("src/none.cs", 1, "irrelevant")),
                current.DbPath,
                "current-ws",
                currentRoot),
            textContentTargets: new[]
            {
                ("target-ws", ReadToolRoutingTestSupport.TextContentContextFor(
                    TextContentIndex(SourceHit(
                        "src/Api.cs",
                        line: 42,
                        snippet: "throw new InvalidOperationException(\"KnownSourceError\");")),
                    "target.db",
                    "target-ws",
                    targetRoot)),
            });
        var tool = new SearchTool(provider, provider, provider, provider);

        string output = tool.Search("KnownSourceError", mode: "source", workspace_id: "target-ws");

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.True(provider.LastEnsureFresh);
        Assert.Equal(1, provider.TextContentSearchResolveCount);
        Assert.Equal(0, provider.SymbolSearchResolveCount);
        Assert.Equal(0, provider.ContentSearchResolveCount);
        Assert.StartsWith("workspace: target-ws\n", output);
        Assert.DoesNotContain(targetRoot, output);
        Assert.Contains("src/Api.cs:42  workspace_source  Api.Handle", output);
        Assert.Contains("KnownSourceError", output);
    }

    [Fact]
    public void Search_ModeSource_RecordsRealSourceBytesFromTextContentIndex()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string dir = Path.Combine(Path.GetTempPath(), "miller-source-source-bytes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string telemetryDb = Path.Combine(dir, "telemetry.db");
        string currentRoot = Path.Combine(dir, "current");
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            currentTextContent: ReadToolRoutingTestSupport.TextContentContextFor(
                TextContentIndex(SourceHit(
                    "src/Api.cs",
                    line: 42,
                    snippet: "throw new InvalidOperationException(\"KnownSourceError\");",
                    sourceBytes: 777)),
                current.DbPath,
                "current-ws",
                currentRoot),
            textContentTargets: Array.Empty<(string, WorkspaceTextContentSearchContext)>());
        var tool = new SearchTool(provider, provider, provider, provider);

        try
        {
            using (var ledger = TelemetryLedger.Open(telemetryDb, workspaceId: "current-ws", currentRoot))
            {
                using var scope = ledger.Measure("search", op: "source");
                string output = tool.Search("KnownSourceError", mode: "source");
                Assert.Contains("src/Api.cs", output);
            }

            Assert.Equal(777, ReadTelemetrySourceBytes(telemetryDb));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Search_AutoMode_EmptySymbolSearch_RendersBoundedSourceRescue()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            currentTextContent: ReadToolRoutingTestSupport.TextContentContextFor(
                TextContentIndex(SourceHit(
                    "src/Api.cs",
                    line: 42,
                    snippet: "throw new InvalidOperationException(\"KnownSourceError\");")),
                current.DbPath,
                "current-ws",
                currentRoot),
            textContentTargets: Array.Empty<(string, WorkspaceTextContentSearchContext)>());
        var tool = new SearchTool(provider, provider, provider, provider);

        string output = tool.Search("KnownSourceError"); // mode defaults to auto

        Assert.Equal(1, provider.SymbolSearchResolveCount);
        Assert.Equal(1, provider.TextContentSearchResolveCount);
        Assert.Contains("Source matches also found:", output);
        Assert.Contains("src/Api.cs:42", output);
        Assert.Contains("KnownSourceError", output);
        Assert.Contains("mode=source", output);
    }

    [Fact]
    public void Search_AutoMode_WeakSymbolSearch_RendersBoundedSourceRescue()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-weak-search-" + Guid.NewGuid().ToString("N"));
        var weakSymbol = Symbol(
            1,
            "weak-helper",
            "KnownSourceErrorHelper",
            "class",
            "src/KnownSourceErrorHelper.cs",
            7,
            "public sealed class KnownSourceErrorHelper");
        var provider = new FixedSymbolSearchProvider(
            new StubSymbolSearchIndex((weakSymbol, 0.05)),
            root,
            TextContentIndex(SourceHit(
                "src/Api.cs",
                line: 42,
                snippet: "throw new InvalidOperationException(\"KnownSourceError\");")));
        var tool = new SearchTool(provider, provider);

        string output = tool.Search("KnownSourceError"); // mode defaults to auto

        Assert.Equal(1, provider.TextContentSearchResolveCount);
        Assert.Contains("KnownSourceErrorHelper", output);
        Assert.Contains("Source matches also found:", output);
        Assert.Contains("src/Api.cs:42", output);
        Assert.Contains("KnownSourceError", output);
        Assert.Contains("mode=source", output);
    }

    [Fact]
    public void Search_AutoMode_DocsLikeQuery_RendersBoundedDocsConfigRescue()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-docs-rescue-" + Guid.NewGuid().ToString("N"));
        var weakSymbol = Symbol(
            1,
            "workspace-health-symbol",
            "WorkspaceHealthSnapshot",
            "class",
            "src/WorkspaceHealthSnapshot.cs",
            12,
            "public sealed class WorkspaceHealthSnapshot");
        var provider = new FixedSymbolSearchProvider(
            new StubSymbolSearchIndex((weakSymbol, 0.1)),
            root,
            TextContentIndex(
                CorpusHit(
                    "docs/workspace-health.md",
                    TextContentKind.WorkspaceDocs,
                    line: 9,
                    snippet: "workspace health explains stale sidecars and recovery steps"),
                CorpusHit(
                    "mcp-config.json",
                    TextContentKind.WorkspaceConfig,
                    line: 3,
                    snippet: "\"workspace health\"")));
        var tool = new SearchTool(provider, provider);

        string output = tool.Search("workspace health"); // mode defaults to auto

        Assert.Equal(1, provider.TextContentSearchResolveCount);
        Assert.Contains("WorkspaceHealthSnapshot", output);
        Assert.Contains("Docs/config matches also found:", output);
        Assert.Contains("docs/workspace-health.md:9", output);
        Assert.Contains("mcp-config.json:3", output);
        Assert.Contains("mode=content", output);
    }

    [Fact]
    public void Search_AutoMode_StrongExactDefinition_DoesNotResolveTextContentProvider()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-strong-search-" + Guid.NewGuid().ToString("N"));
        var exactSymbol = Symbol(
            1,
            "known-source-error",
            "KnownSourceError",
            "method",
            "src/Api.cs",
            17,
            "void KnownSourceError()");
        var provider = new FixedSymbolSearchProvider(
            new StubSymbolSearchIndex((exactSymbol, 42.0)),
            root,
            TextContentIndex(SourceHit("src/Api.cs", 42, "KnownSourceError appears in source text")));
        var tool = new SearchTool(provider, provider);

        string output = tool.Search("KnownSourceError"); // mode defaults to auto

        Assert.Equal(0, provider.TextContentSearchResolveCount);
        Assert.Contains("Definition found: KnownSourceError", output);
        Assert.DoesNotContain("Source matches also found:", output);
        Assert.DoesNotContain("Docs/config matches also found:", output);
    }

    [Fact]
    public void Search_AutoMode_SourceRescue_RecordsTelemetryMetadata()
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-auto-rescue-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string telemetryDb = Path.Combine(dir, "telemetry.db");
        string root = Path.Combine(dir, "root");
        var weakSymbol = Symbol(
            1,
            "weak-helper",
            "KnownSourceErrorHelper",
            "class",
            "src/KnownSourceErrorHelper.cs",
            7,
            "public sealed class KnownSourceErrorHelper");
        var provider = new FixedSymbolSearchProvider(
            new StubSymbolSearchIndex((weakSymbol, 0.05)),
            root,
            TextContentIndex(SourceHit(
                "src/Api.cs",
                line: 42,
                snippet: "throw new InvalidOperationException(\"KnownSourceError\");",
                sourceBytes: 777)));
        var tool = new SearchTool(provider, provider);

        try
        {
            using (var ledger = TelemetryLedger.Open(telemetryDb, "current-ws", root))
            {
                using var scope = ledger.Measure("search", op: "auto");
                string output = tool.Search("KnownSourceError");
                Assert.Contains("Source matches also found:", output);
            }

            using JsonDocument doc = JsonDocument.Parse(ReadTelemetryMetadata(telemetryDb));
            Assert.True(doc.RootElement.GetProperty("auto_rescue_attempted").GetBoolean());
            Assert.Equal("source", doc.RootElement.GetProperty("auto_rescue_kind").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("auto_rescue_result_count").GetInt32());
            Assert.Equal(777, ReadTelemetrySourceBytes(telemetryDb));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Search_AutoMode_FileQuery_DoesNotResolveTextContentProvider()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            currentTextContent: ReadToolRoutingTestSupport.TextContentContextFor(
                TextContentIndex(SourceHit("src/Api.cs", 42, "KnownSourceError")),
                current.DbPath,
                "current-ws",
                currentRoot),
            textContentTargets: Array.Empty<(string, WorkspaceTextContentSearchContext)>());
        var tool = new SearchTool(provider, provider, provider, provider);

        string output = tool.Search("CurrentOnly.cs"); // mode defaults to auto

        Assert.Equal(1, provider.SymbolSearchResolveCount);
        Assert.Equal(0, provider.TextContentSearchResolveCount);
        Assert.Contains("src/CurrentOnly.cs", output);
    }

    [Fact]
    public void Search_AutoModeJson_EmptySymbolSearch_DoesNotResolveTextContentProvider()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot),
            currentTextContent: ReadToolRoutingTestSupport.TextContentContextFor(
                TextContentIndex(SourceHit("src/Api.cs", 42, "KnownSourceError")),
                current.DbPath,
                "current-ws",
                currentRoot),
            textContentTargets: Array.Empty<(string, WorkspaceTextContentSearchContext)>());
        var tool = new SearchTool(provider, provider, provider, provider);

        string output = tool.Search("KnownSourceError", format: "json"); // mode defaults to auto

        Assert.Equal("[]", output);
        Assert.Equal(1, provider.SymbolSearchResolveCount);
        Assert.Equal(0, provider.TextContentSearchResolveCount);
    }

    [Fact]
    public void Search_NonContentMode_DoesNotResolveContentProvider()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", currentRoot));
        var tool = new SearchTool(provider, provider);

        string output = tool.Search("CurrentOnly"); // mode defaults to auto

        Assert.Equal(0, provider.ContentSearchResolveCount);
        Assert.Equal(0, provider.TextContentSearchResolveCount);
        Assert.Equal(1, provider.SymbolSearchResolveCount);
        Assert.Contains("CurrentOnly", output);
    }

    // ----- regions=comment|doc_comment|string_literal (source-region search) -----

    [Fact]
    public void RunRegions_Compact_RendersRegionHitShape()
    {
        var index = new StubRegionSearchIndex(
            new RegionSearchHit("src/A.cs", 2.0, 7, "comment", "// TODO migrate", "// TODO migrate",
                "region-a", "sym-a", "A"));

        string output = SearchTool.RunRegions(
            index,
            "TODO",
            new HashSet<string> { "comment" },
            limit: 10,
            excludeTests: false,
            json: false,
            out int count,
            compactBanner: "workspace: current-ws");

        Assert.Equal(1, count);
        Assert.Equal(
            "workspace: current-ws\n" +
            "src/A.cs:7  comment  A\n" +
            "    // TODO migrate",
            output);
    }

    [Fact]
    public void RunRegions_Json_HasRegionShape_NotSymbolShape()
    {
        var index = new StubRegionSearchIndex(
            new RegionSearchHit("src/A.cs", 2.0, 7, "string_literal", "\"todo\"", "\"todo\"",
                "region-a", "sym-a", "A", "csharp"));

        string output = SearchTool.RunRegions(
            index,
            "todo",
            new HashSet<string> { "string_literal" },
            limit: 10,
            excludeTests: false,
            json: true,
            out int count);

        Assert.Equal(1, count);
        using var doc = JsonDocument.Parse(output);
        var first = doc.RootElement[0];
        Assert.Equal("src/A.cs", first.GetProperty("file").GetString());
        Assert.Equal("string_literal", first.GetProperty("kind").GetString());
        Assert.Equal("region-a", first.GetProperty("region_id").GetString());
        Assert.Equal("sym-a", first.GetProperty("containing_symbol_id").GetString());
        Assert.False(first.TryGetProperty("symbol_id", out _));
        Assert.False(first.TryGetProperty("name", out _));
    }

    [Fact]
    public void RunRegions_FilePatternAndLanguageFilters()
    {
        var index = new StubRegionSearchIndex(
            new RegionSearchHit("src/ui/A.ts", 2.0, 7, "comment", "// TODO scoped", "// TODO scoped",
                "region-ts", "sym-ts", "A", "typescript"),
            new RegionSearchHit("src/api/A.cs", 1.0, 7, "comment", "// TODO scoped", "// TODO scoped",
                "region-cs", "sym-cs", "A", "csharp"));

        string output = SearchTool.RunRegions(
            index,
            "TODO",
            new HashSet<string> { "comment" },
            limit: 10,
            excludeTests: false,
            json: false,
            out int count,
            filePattern: "src/ui/**",
            language: "typescript");

        Assert.Equal(1, count);
        Assert.Contains("src/ui/A.ts", output);
        Assert.DoesNotContain("src/api/A.cs", output);
    }

    [Fact]
    public void RunRegions_FilteredMiss_ShowsOutsideScopeHintInCompact()
    {
        var index = new StubRegionSearchIndex(
            new RegionSearchHit("src/api/A.cs", 2.0, 7, "comment", "// TODO scoped", "// TODO scoped",
                "region-cs", "sym-cs", "A", "csharp"));

        string output = SearchTool.RunRegions(
            index,
            "TODO",
            new HashSet<string> { "comment" },
            limit: 10,
            excludeTests: false,
            json: false,
            out int count,
            filePattern: "src/ui/**");

        Assert.Equal(0, count);
        Assert.Contains("No results within file_pattern=src/ui/**.", output);
        Assert.Contains("Outside scope:", output);
        Assert.Contains("src/api/A.cs:7  comment  A", output);
        Assert.Contains("// TODO scoped", output);
    }

    [Fact]
    public void Search_RegionsPresent_RoutesOnlyToRegionProvider_AndRegionsWinsOverMode()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string root = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var regionIndex = new StubRegionSearchIndex(
            new RegionSearchHit("src/A.cs", 2.0, 7, "comment", "// TODO migrate", "// TODO migrate",
                "region-a", "sym-a", "A"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", root),
            ReadToolRoutingTestSupport.RegionContextFor(regionIndex, current.DbPath, "current-ws", root),
            regionTargets: Array.Empty<(string, WorkspaceRegionSearchContext)>());
        var tool = new SearchTool(provider, provider, provider);

        string output = tool.Search("TODO", mode: "content", regions: "comment");

        Assert.Equal(1, provider.RegionSearchResolveCount);
        Assert.Equal(0, provider.SymbolSearchResolveCount);
        Assert.Equal(0, provider.ContentSearchResolveCount);
        Assert.Contains("mode=content ignored", output);
        Assert.Contains("src/A.cs:7  comment  A", output);
    }

    [Fact]
    public void Search_ModeMarkers_RoutesToRegionProvider_AndRendersMarkerRows()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string root = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var regionIndex = new StubRegionSearchIndex(
            new RegionSearchHit("src/A.cs", 2.0, 7, "comment", "// TODO migrate", "// TODO migrate",
                "region-todo", "sym-a", "A", "csharp"),
            new RegionSearchHit("src/B.cs", 2.0, 11, "doc_comment", "/// HACK temporary", "/// HACK temporary",
                "region-hack", "sym-b", "B", "csharp"),
            new RegionSearchHit("src/C.cs", 2.0, 13, "string_literal", "\"HACK not a marker\"", "\"HACK not a marker\"",
                "region-string", "sym-c", "C", "csharp"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", root),
            ReadToolRoutingTestSupport.RegionContextFor(regionIndex, current.DbPath, "current-ws", root),
            regionTargets: Array.Empty<(string, WorkspaceRegionSearchContext)>());
        var tool = new SearchTool(provider, provider, provider);

        string compact = tool.Search("HACK", mode: "markers");
        string json = tool.Search("HACK", mode: "markers", format: "json");

        Assert.Equal(2, provider.RegionSearchResolveCount);
        Assert.Equal(0, provider.SymbolSearchResolveCount);
        Assert.Equal(0, provider.ContentSearchResolveCount);
        Assert.Contains("src/B.cs:11  HACK  doc_comment  B", compact);
        Assert.DoesNotContain("TODO", compact);
        Assert.DoesNotContain("string_literal", compact);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement item = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("HACK", item.GetProperty("marker").GetString());
        Assert.Equal("src/B.cs", item.GetProperty("file").GetString());
        Assert.Equal("B", item.GetProperty("containing_symbol_name").GetString());
    }

    [Fact]
    public void Search_ModeMarkers_DoesNotAutoExcludeTestsForSpaceSeparatedMarkerList()
    {
        using var current = FixtureWithSymbol("current-ws", "CurrentOnly");
        string root = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var regionIndex = new StubRegionSearchIndex(
            new RegionSearchHit("tests/A.cs", 2.0, 7, "comment", "// TODO test marker", "// TODO test marker",
                "region-a", "sym-a", "A", "csharp"),
            new RegionSearchHit("tests/B.cs", 2.0, 11, "comment", "// HACK test marker", "// HACK test marker",
                "region-b", "sym-b", "B", "csharp"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(current), current.DbPath, "current-ws", root),
            ReadToolRoutingTestSupport.RegionContextFor(regionIndex, current.DbPath, "current-ws", root),
            regionTargets: Array.Empty<(string, WorkspaceRegionSearchContext)>());
        var tool = new SearchTool(provider, provider, provider);

        _ = tool.Search("TODO HACK", mode: "markers");

        Assert.Equal(2, regionIndex.SearchCalls.Count);
        Assert.All(regionIndex.SearchCalls, call => Assert.False(call.ExcludeTests));
    }

    [Fact]
    public void Search_SymbolResultsAnnotateHasDocFromSymbolsDocComment()
    {
        using var fx = FixtureWithDocCommentSymbol();
        string root = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(BuildIndex(fx), fx.DbPath, "current-ws", root));
        var tool = new SearchTool(provider, provider);

        string compact = tool.Search("HasDoc");
        string json = tool.Search("HasDoc", format: "json");

        Assert.Contains("HasDoc", compact);
        Assert.Contains("has_doc", compact);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement[0].GetProperty("has_doc").GetBoolean());
    }

    private sealed class StubSymbolSearchIndex : ISymbolLookupIndex
    {
        private readonly SearchHit[] _hits;
        private readonly Dictionary<int, IndexedSymbol> _symbols;
        private readonly Dictionary<string, List<IndexedSymbol>> _byName;
        private readonly Dictionary<string, List<IndexedSymbol>> _byFilePath;
        private readonly Dictionary<string, List<IndexedSymbol>> _byParentId;

        public StubSymbolSearchIndex(params (IndexedSymbol Symbol, double Score)[] rows)
        {
            _symbols = rows.ToDictionary(static row => row.Symbol.DocId, static row => row.Symbol);
            _byName = rows
                .GroupBy(static row => row.Symbol.Name, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Select(row => row.Symbol).ToList(),
                    StringComparer.Ordinal);
            _byFilePath = rows
                .GroupBy(static row => row.Symbol.FilePath, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Select(row => row.Symbol).ToList(),
                    StringComparer.Ordinal);
            _byParentId = rows
                .Select(static row => row.Symbol)
                .Where(static symbol => symbol.ParentId is not null)
                .GroupBy(static symbol => symbol.ParentId!, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
            _hits = rows
                .Select(static row => new SearchHit(row.Symbol.ToSearchableDocument(), row.Score))
                .ToArray();
            KnownExtensions = rows
                .Select(static row => Path.GetExtension(row.Symbol.FilePath))
                .Where(static ext => ext.Length > 1)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private StubSymbolSearchIndex(IReadOnlyList<IndexedSymbol> symbols)
        {
            _symbols = symbols.ToDictionary(static symbol => symbol.DocId);
            _byName = symbols
                .GroupBy(static symbol => symbol.Name, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
            _byFilePath = symbols
                .GroupBy(static symbol => symbol.FilePath, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
            _byParentId = symbols
                .Where(static symbol => symbol.ParentId is not null)
                .GroupBy(static symbol => symbol.ParentId!, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
            _hits = [];
            KnownExtensions = symbols
                .Select(static symbol => Path.GetExtension(symbol.FilePath))
                .Where(static ext => ext.Length > 1)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public static StubSymbolSearchIndex WithSymbolsOnly(params IndexedSymbol[] symbols) => new(symbols);

        public int DocumentCount => _symbols.Count;

        public IReadOnlySet<string> KnownExtensions { get; }

        public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
            _hits.Take(limit).ToArray();

        public IReadOnlyList<IndexedSymbol> FindByName(string name) =>
            _byName.TryGetValue(name, out var symbols) ? symbols : Array.Empty<IndexedSymbol>();

        public IndexedSymbol? FindBySymbolId(string symbolId) =>
            _symbols.Values.FirstOrDefault(symbol => symbol.SymbolId == symbolId);

        public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) =>
            _byParentId.TryGetValue(parentId, out var symbols) ? symbols : Array.Empty<IndexedSymbol>();

        public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) =>
            _byFilePath.TryGetValue(filePath, out var symbols) ? symbols : Array.Empty<IndexedSymbol>();

        public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
            FilePathSymbolLookup.FindByFilePathFragment(_byFilePath, query, limit);

        public bool IsIndexedFilePath(string path) => _byFilePath.ContainsKey(path);

        public string? ResolveIndexedFilePath(string target) =>
            IsIndexedFilePath(target) ? target : null;

        public IndexedSymbol Resolve(int docId) => _symbols[docId];
    }

    private sealed class StubRegionSearchIndex : IRegionSearchIndex
    {
        private readonly IReadOnlyList<RegionSearchHit> _hits;

        public StubRegionSearchIndex(params RegionSearchHit[] hits)
        {
            _hits = hits;
        }

        public int DocumentCount => _hits.Count;

        public long Revision { get; } = 1;

        public List<(string Query, bool ExcludeTests)> SearchCalls { get; } = [];

        public IReadOnlyList<RegionSearchHit> Search(
            string query,
            IReadOnlySet<string> kinds,
            int limit = 10,
            bool excludeTests = false)
        {
            SearchCalls.Add((query, excludeTests));
            return _hits
                .Where(hit => kinds.Contains(hit.Kind))
                .Take(limit)
                .ToArray();
        }
    }

    private sealed class StubTextContentSearchIndex : ITextContentSearchIndex
    {
        private readonly IReadOnlyList<TextContentSearchHit> _hits;

        public StubTextContentSearchIndex(params TextContentSearchHit[] hits)
        {
            _hits = hits;
        }

        public int DocumentCount => _hits.Count;

        public IReadOnlyList<TextContentSearchHit> Search(
            string query,
            string contentKind,
            int limit = 10,
            bool excludeTests = false) =>
            Search(query, new[] { contentKind }, limit, excludeTests);

        public IReadOnlyList<TextContentSearchHit> Search(
            string query,
            IReadOnlyCollection<string> contentKinds,
            int limit = 10,
            bool excludeTests = false) =>
            _hits
                .Where(hit => contentKinds.Contains(hit.ContentKind))
                .Where(hit => !excludeTests || !IsTestPath.Check(hit.Path ?? hit.DisplayPath))
                .Take(limit)
                .ToArray();
    }
}
