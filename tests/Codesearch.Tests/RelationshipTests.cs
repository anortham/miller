using Xunit;
using Codesearch.Server.Services;
using uniffi.codesearch_ffi;

namespace Codesearch.Tests;

public class RelationshipTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SearchService _searchService;

    public RelationshipTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_rel_{Guid.NewGuid():N}");
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

    private void AddTestSymbols()
    {
        var symbol1 = new SymbolInput(
            id: "func::caller",
            name: "caller",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn caller()",
            docComment: null,
            startLine: 1,
            endLine: 5,
            content: null
        );
        var symbol2 = new SymbolInput(
            id: "func::callee",
            name: "callee",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn callee()",
            docComment: null,
            startLine: 10,
            endLine: 15,
            content: null
        );

        var vector = TestHelpers.CreateTestVector(1.0f);
        _searchService.AddSymbols(
            new List<SymbolInput> { symbol1, symbol2 },
            new List<List<float>> { vector, vector }
        );
    }

    private void AddTestRelationship()
    {
        var relationship = new RelationshipInput(
            fromSymbolId: "func::caller",
            toSymbolId: "func::callee",
            kind: "Calls",
            filePath: "test.rs",
            lineNumber: 3,
            confidence: 0.95f
        );
        _searchService.AddRelationships(new List<RelationshipInput> { relationship });
    }

    [Fact]
    public void AddRelationships_StoresRelationships()
    {
        // Add test symbols first (relationships reference symbols)
        AddTestSymbols();

        // Add relationship
        var relationship = new RelationshipInput(
            fromSymbolId: "func::caller",
            toSymbolId: "func::callee",
            kind: "Calls",
            filePath: "test.rs",
            lineNumber: 3,
            confidence: 0.95f
        );
        var count = _searchService.AddRelationships(
            new List<RelationshipInput> { relationship }
        );

        Assert.Equal(1UL, count);
    }

    [Fact]
    public void GetCallers_ReturnsCallers()
    {
        // Setup symbols and relationship
        AddTestSymbols();
        AddTestRelationship();

        // Query callers of "callee" - should return "caller"
        var callers = _searchService.GetCallers("func::callee", 10);

        Assert.Single(callers);
        Assert.Equal("func::caller", callers[0].fromSymbolId);
        Assert.Equal("func::callee", callers[0].toSymbolId);
        Assert.Equal("Calls", callers[0].kind);
    }

    [Fact]
    public void GetCallees_ReturnsCallees()
    {
        // Setup symbols and relationship
        AddTestSymbols();
        AddTestRelationship();

        // Query callees of "caller" - should return "callee"
        var callees = _searchService.GetCallees("func::caller", 10);

        Assert.Single(callees);
        Assert.Equal("func::caller", callees[0].fromSymbolId);
        Assert.Equal("func::callee", callees[0].toSymbolId);
        Assert.Equal("Calls", callees[0].kind);
    }

    [Fact]
    public void GetCallers_ReturnsEmptyForNoCallers()
    {
        // Add symbols but no relationship
        AddTestSymbols();

        // Query callers - should return empty
        var callers = _searchService.GetCallers("func::callee", 10);

        Assert.Empty(callers);
    }

    [Fact]
    public void GetCallees_ReturnsEmptyForNoCallees()
    {
        // Add symbols but no relationship
        AddTestSymbols();

        // Query callees - should return empty
        var callees = _searchService.GetCallees("func::caller", 10);

        Assert.Empty(callees);
    }
}
