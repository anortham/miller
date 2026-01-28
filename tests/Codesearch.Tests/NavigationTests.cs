using Xunit;
using Codesearch.Server.Services;
using uniffi.codesearch_ffi;

namespace Codesearch.Tests;

public class NavigationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SearchService _searchService;

    public NavigationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_nav_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var dbPath = Path.Combine(_tempDir, "test.lance");
        _searchService = new SearchService(dbPath);
    }

    public void Dispose()
    {
        _searchService.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    [Fact]
    public void GetSymbolById_ReturnsSymbol()
    {
        // Add a test symbol
        var symbol = new SymbolInput(
            id: "test::my_function",
            name: "my_function",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn my_function()",
            docComment: "A test function",
            startLine: 1,
            endLine: 5,
            content: null
        );
        var vector = TestHelpers.CreateTestVector(1.0f);
        _searchService.AddSymbols(
            new List<SymbolInput> { symbol },
            new List<List<float>> { vector }
        );

        // Query by ID
        var result = _searchService.GetSymbolById("test::my_function");

        Assert.NotNull(result);
        Assert.Equal("my_function", result.name);
        Assert.Equal("function", result.kind);
    }

    [Fact]
    public void GetSymbolById_ReturnsNullForMissing()
    {
        var result = _searchService.GetSymbolById("nonexistent::symbol");
        Assert.Null(result);
    }

    [Fact]
    public void GetSymbolsByFile_ReturnsSymbolsInFile()
    {
        // Add symbols in same file
        var symbols = new List<SymbolInput>
        {
            new("file1::func1", "func1", "function", "rust", "src/lib.rs", null, null, 1, 5, null),
            new("file1::func2", "func2", "function", "rust", "src/lib.rs", null, null, 10, 15, null),
            new("file2::func3", "func3", "function", "rust", "src/main.rs", null, null, 1, 5, null),
        };
        var vectors = symbols.Select(_ => TestHelpers.CreateTestVector(1.0f)).ToList();
        _searchService.AddSymbols(symbols, vectors);

        // Query by file
        var results = _searchService.GetSymbolsByFile("src/lib.rs", 100);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("src/lib.rs", r.filePath));
    }

    [Fact]
    public void GetSymbolsByKind_ReturnsSymbolsOfKind()
    {
        // Add symbols of different kinds
        var symbols = new List<SymbolInput>
        {
            new("sym1", "MyClass", "class", "python", "test.py", null, null, 1, 10, null),
            new("sym2", "my_func", "function", "python", "test.py", null, null, 15, 20, null),
            new("sym3", "OtherClass", "class", "python", "test.py", null, null, 25, 35, null),
        };
        var vectors = symbols.Select(_ => TestHelpers.CreateTestVector(1.0f)).ToList();
        _searchService.AddSymbols(symbols, vectors);

        // Query by kind
        var results = _searchService.GetSymbolsByKind("class", 100);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("class", r.kind));
    }

    [Fact]
    public void GetReferences_ReturnsIdentifiersTargetingSymbol()
    {
        // Add a symbol
        var symbol = new SymbolInput(
            id: "target::symbol",
            name: "target_func",
            kind: "function",
            language: "rust",
            filePath: "lib.rs",
            signature: null,
            docComment: null,
            startLine: 1,
            endLine: 5,
            content: null
        );
        var vector = TestHelpers.CreateTestVector(1.0f);
        _searchService.AddSymbols(new List<SymbolInput> { symbol }, new List<List<float>> { vector });

        // Add identifiers that reference the symbol
        var identifiers = new List<IdentifierInput>
        {
            new("target_func", "Call", "main.rs", 10, 5, "caller::func", "target::symbol"),
            new("target_func", "Call", "other.rs", 20, 8, "other::func", "target::symbol"),
        };
        _searchService.AddIdentifiers(identifiers);

        // Query references
        var refs = _searchService.GetReferences("target::symbol", 100);

        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.filePath == "main.rs");
        Assert.Contains(refs, r => r.filePath == "other.rs");
    }
}
