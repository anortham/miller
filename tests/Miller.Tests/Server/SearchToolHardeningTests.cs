using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the <c>search</c> input-hardening seams: (1) auto-mode file routing requires a path-SHAPED query — a
/// bare separator must not pull <c>src/utils#helper</c> or <c>Foo::Bar</c>-style symbol syntax onto the file
/// arm, while <c>package.json</c> / <c>src/foo/bar.ts</c> / <c>scripts/test.sh</c> keep routing to file mode;
/// (2) a pasted-blob query longer than <see cref="SearchTool.MaxQueryLength"/> is rejected with a clear
/// refusal before tokenization.
/// </summary>
public sealed class SearchToolHardeningTests
{
    private static IndexedSymbol Symbol(int docId, string symbolId, string name, string filePath) =>
        new(docId, symbolId, name, Signature: null, "class", "csharp", filePath,
            StartLine: 1, EndLine: 1, ParentId: null, IsTest: false);

    [Theory]
    [InlineData("src/utils#helper")]   // separator + symbol fragment syntax
    [InlineData("Foo::Bar")]           // qualified symbol syntax, no separator
    [InlineData("List<Foo>/Bar")]      // one separator + generic syntax
    [InlineData("src/Run(query)")]     // one separator + call syntax
    [InlineData("ns/Class:member")]    // one separator + colon
    [InlineData("render the page/view")] // one separator inside a phrase
    public void Run_AutoMode_SymbolSyntaxQueries_DoNotRouteToFileMode(string query)
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "sym-a", "HelperThing", "src/utils.cs"), 5.0));

        string output = SearchTool.Run(
            index, query, SearchToolMode.Auto, limit: 5, excludeTests: false, json: false, out _);

        Assert.DoesNotContain("File match", output);
        Assert.Contains("HelperThing", output);
    }

    [Theory]
    [InlineData("package.json")]    // known extension, no separator
    [InlineData("src/foo/bar.ts")]  // known extension + separators
    [InlineData("scripts/test.sh")] // known extension + one separator
    [InlineData("docs/readme")]     // one separator, no whitespace, no symbol syntax ⇒ path-shaped
    [InlineData("src/a/b/widget")]  // multiple separators
    public void Run_AutoMode_PathShapedQueries_RouteToFileMode(string query)
    {
        var index = new StubSymbolSearchIndex(
            (Symbol(0, "sym-file", "InFile", query), 5.0),
            (Symbol(1, "sym-ts", "Other", "src/Other.ts"), 4.0),
            (Symbol(2, "sym-json", "Config", "config/app.json"), 3.0),
            (Symbol(3, "sym-sh", "Script", "scripts/other.sh"), 2.0));

        string output = SearchTool.Run(
            index, query, SearchToolMode.Auto, limit: 5, excludeTests: false, json: false, out _);

        Assert.Contains("File match", output);
        Assert.Contains(query, output);
    }

    [Fact]
    public void Run_QueryOverMaxLength_IsRejectedBeforeTokenization()
    {
        var index = new StubSymbolSearchIndex((Symbol(0, "sym-a", "Alpha", "src/A.cs"), 1.0));
        string query = new string('a', SearchTool.MaxQueryLength + 1);

        var ex = Assert.Throws<ArgumentException>(() =>
            SearchTool.Run(index, query, SearchToolMode.Symbol, limit: 5, excludeTests: false, json: false, out _));

        Assert.Contains("1000", ex.Message);
    }

    [Fact]
    public void Run_QueryAtExactlyMaxLength_IsAccepted()
    {
        var index = new StubSymbolSearchIndex((Symbol(0, "sym-a", "Alpha", "src/A.cs"), 1.0));
        string query = new string('a', SearchTool.MaxQueryLength);

        Exception? ex = Record.Exception(() =>
            SearchTool.Run(index, query, SearchToolMode.Symbol, limit: 5, excludeTests: false, json: false, out _));

        Assert.Null(ex);
    }

    [Fact]
    public void RunContentCorpus_QueryOverMaxLength_IsRejected()
    {
        var index = new EmptyTextContentIndex();
        string query = new string('a', SearchTool.MaxQueryLength + 1);

        var ex = Assert.Throws<ArgumentException>(() =>
            SearchTool.RunContentCorpus(index, query, limit: 5, json: false, out _));

        Assert.Contains("1000", ex.Message);
    }

    [Fact]
    public void RunRegions_QueryOverMaxLength_IsRejected()
    {
        var index = new EmptyRegionIndex();
        string query = new string('a', SearchTool.MaxQueryLength + 1);

        var ex = Assert.Throws<ArgumentException>(() =>
            SearchTool.RunRegions(
                index, query, new HashSet<string>(StringComparer.Ordinal) { "comment" }, limit: 5,
                excludeTests: false, json: false, out _));

        Assert.Contains("1000", ex.Message);
    }

    private sealed class EmptyTextContentIndex : ITextContentSearchIndex
    {
        public int DocumentCount => 0;

        public IReadOnlyList<TextContentSearchHit> Search(
            string query, string contentKind, int limit = 10, bool excludeTests = false) =>
            Array.Empty<TextContentSearchHit>();

        public IReadOnlyList<TextContentSearchHit> Search(
            string query, IReadOnlyCollection<string> contentKinds, int limit = 10, bool excludeTests = false) =>
            Array.Empty<TextContentSearchHit>();
    }

    private sealed class EmptyRegionIndex : IRegionSearchIndex
    {
        public int DocumentCount => 0;

        public long Revision { get; } = 1;

        public IReadOnlyList<RegionSearchHit> Search(
            string query, IReadOnlySet<string> kinds, int limit = 10, bool excludeTests = false) =>
            Array.Empty<RegionSearchHit>();
    }

    private sealed class StubSymbolSearchIndex : ISymbolLookupIndex
    {
        private readonly SearchHit[] _hits;
        private readonly Dictionary<int, IndexedSymbol> _symbols;
        private readonly Dictionary<string, List<IndexedSymbol>> _byFilePath;

        public StubSymbolSearchIndex(params (IndexedSymbol Symbol, double Score)[] rows)
        {
            _symbols = rows.ToDictionary(static row => row.Symbol.DocId, static row => row.Symbol);
            _byFilePath = rows
                .GroupBy(static row => row.Symbol.FilePath, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Select(row => row.Symbol).ToList(),
                    StringComparer.Ordinal);
            _hits = rows
                .Select(static row => new SearchHit(row.Symbol.ToSearchableDocument(), row.Score))
                .ToArray();
            KnownExtensions = rows
                .Select(static row => Path.GetExtension(row.Symbol.FilePath))
                .Where(static ext => ext.Length > 1)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public int DocumentCount => _symbols.Count;

        public IReadOnlySet<string> KnownExtensions { get; }

        public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) =>
            _hits.Take(limit).ToArray();

        public IReadOnlyList<IndexedSymbol> FindByName(string name) =>
            _symbols.Values.Where(symbol => symbol.Name == name).ToArray();

        public IndexedSymbol? FindBySymbolId(string symbolId) =>
            _symbols.Values.FirstOrDefault(symbol => symbol.SymbolId == symbolId);

        public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) => Array.Empty<IndexedSymbol>();

        public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) =>
            _byFilePath.TryGetValue(filePath, out var symbols) ? symbols : Array.Empty<IndexedSymbol>();

        public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
            FilePathSymbolLookup.FindByFilePathFragment(_byFilePath, query, limit);

        public bool IsIndexedFilePath(string path) => _byFilePath.ContainsKey(path);

        public string? ResolveIndexedFilePath(string target) =>
            IsIndexedFilePath(target) ? target : null;

        public IndexedSymbol Resolve(int docId) => _symbols[docId];
    }
}
