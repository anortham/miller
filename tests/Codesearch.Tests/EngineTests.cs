using Xunit;
using uniffi.codesearch_ffi;

namespace Codesearch.Tests;

#pragma warning disable CS8602 // Dereference of a possibly null reference

public class EngineTests : IDisposable
{
    private readonly string _tempDir;

    public EngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    [Fact]
    public void CanCreateEngine()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");

        using var engine = new CodeSearchEngine(dbPath);

        Assert.Equal(dbPath, engine.DbPath());
    }

    [Fact]
    public void HealthCheckReturnsTrue()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");
        using var engine = new CodeSearchEngine(dbPath);

        var healthy = engine.HealthCheck();

        Assert.True(healthy);
    }

    [Fact]
    public void CanAddSymbols()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");
        using var engine = new CodeSearchEngine(dbPath);

        var symbols = new List<SymbolInput>
        {
            new SymbolInput(
                id: "test_foo",
                name: "foo",
                kind: "function",
                language: "rust",
                filePath: "src/test.rs",
                signature: "fn foo()",
                docComment: null,
                startLine: 1,
                endLine: 10,
                content: null
            )
        };

        // Create a dummy vector (768 dimensions for nomic-embed)
        var vector = Enumerable.Repeat(0.1f, 768).ToList();
        var vectors = new List<List<float>> { vector };

        var count = engine.AddSymbols(symbols, vectors);

        Assert.Equal(1UL, count);
    }

    [Fact]
    public void CanGetSymbolCount()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");
        using var engine = new CodeSearchEngine(dbPath);

        Assert.Equal(0UL, engine.SymbolCount());

        var symbols = new List<SymbolInput>
        {
            new SymbolInput(
                id: "test_foo",
                name: "foo",
                kind: "function",
                language: "rust",
                filePath: "src/test.rs",
                signature: "fn foo()",
                docComment: null,
                startLine: 1,
                endLine: 10,
                content: null
            )
        };

        var vector = Enumerable.Repeat(0.1f, 768).ToList();
        var vectors = new List<List<float>> { vector };

        engine.AddSymbols(symbols, vectors);

        Assert.Equal(1UL, engine.SymbolCount());
    }

    [Fact]
    public void CanSearchByVector()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");
        using var engine = new CodeSearchEngine(dbPath);

        // Create test vectors with different seeds
        var vector1 = CreateTestVector(1.0f);
        var vector2 = CreateTestVector(2.0f);

        var symbols = new List<SymbolInput>
        {
            new SymbolInput(
                id: "test_foo",
                name: "foo",
                kind: "function",
                language: "rust",
                filePath: "src/test.rs",
                signature: "fn foo()",
                docComment: null,
                startLine: 1,
                endLine: 10,
                content: null
            ),
            new SymbolInput(
                id: "test_bar",
                name: "bar",
                kind: "function",
                language: "rust",
                filePath: "src/test.rs",
                signature: "fn bar()",
                docComment: null,
                startLine: 11,
                endLine: 20,
                content: null
            )
        };

        var vectors = new List<List<float>> { vector1, vector2 };
        engine.AddSymbols(symbols, vectors);

        // Search with a vector similar to vector1
        var queryVector = CreateTestVector(1.0f);
        var results = engine.SearchVector(queryVector, 10);

        Assert.NotEmpty(results);
        Assert.True(results[0].score > 0);
        Assert.Equal("test_foo", results[0].id);
    }

    [Fact]
    public void CanSearchByText()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");
        using var engine = new CodeSearchEngine(dbPath);

        // The FTS index uses whitespace tokenizer, so the code_pattern will be:
        // "fn authenticate() authenticate function"
        // Searchable tokens: "fn", "authenticate()", "authenticate", "function"
        var symbols = new List<SymbolInput>
        {
            new SymbolInput(
                id: "test_auth",
                name: "authenticate",  // Single word token for FTS
                kind: "function",
                language: "rust",
                filePath: "src/auth.rs",
                signature: "fn authenticate()",
                docComment: null,
                startLine: 1,
                endLine: 10,
                content: null
            )
        };

        var vector = CreateTestVector(1.0f);
        var vectors = new List<List<float>> { vector };

        engine.AddSymbols(symbols, vectors);
        engine.CreateFtsIndex();

        // Search for exact token that appears in code_pattern
        var results = engine.SearchText("authenticate", 10);

        Assert.NotEmpty(results);
        Assert.Equal("test_auth", results[0].id);
    }

    [Fact]
    public void CanSearchHybrid()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");
        using var engine = new CodeSearchEngine(dbPath);

        var symbols = new List<SymbolInput>
        {
            new SymbolInput(
                id: "test_login",
                name: "login_handler",
                kind: "function",
                language: "rust",
                filePath: "src/handlers.rs",
                signature: "fn login_handler()",
                docComment: null,
                startLine: 1,
                endLine: 10,
                content: null
            ),
            new SymbolInput(
                id: "test_logout",
                name: "logout_handler",
                kind: "function",
                language: "rust",
                filePath: "src/handlers.rs",
                signature: "fn logout_handler()",
                docComment: null,
                startLine: 11,
                endLine: 20,
                content: null
            )
        };

        var vector1 = CreateTestVector(1.0f);
        var vector2 = CreateTestVector(2.0f);
        var vectors = new List<List<float>> { vector1, vector2 };

        engine.AddSymbols(symbols, vectors);
        engine.CreateFtsIndex();

        // Search with both text and vector
        var queryVector = CreateTestVector(1.0f);
        var results = engine.SearchHybrid("login", queryVector, 10);

        Assert.NotEmpty(results);
        Assert.True(results[0].score > 0);
        // The login_handler should be found via hybrid search
        Assert.Contains(results, r => r.id == "test_login");
    }

    [Fact]
    public void SearchTextBoostedRanksCorrectly()
    {
        var dbPath = Path.Combine(_tempDir, "test.lance");
        using var engine = new CodeSearchEngine(dbPath);

        // Add two symbols with same name but different kinds
        var symbols = new List<SymbolInput>
        {
            new SymbolInput(
                id: "import_process",
                name: "process_data",
                kind: "import",
                language: "rust",
                filePath: "src/imports.rs",
                signature: null,
                docComment: null,
                startLine: 1,
                endLine: 1,
                content: null
            ),
            new SymbolInput(
                id: "function_process",
                name: "process_data",
                kind: "function",
                language: "rust",
                filePath: "src/handlers.rs",
                signature: "fn process_data()",
                docComment: null,
                startLine: 1,
                endLine: 10,
                content: null
            )
        };

        var vector1 = CreateTestVector(1.0f);
        var vector2 = CreateTestVector(1.0f); // Same vector so only kind boosting matters
        var vectors = new List<List<float>> { vector1, vector2 };

        engine.AddSymbols(symbols, vectors);
        engine.CreateFtsIndex();

        // Search with boosting
        var results = engine.SearchTextBoosted("process_data", 10);

        Assert.NotEmpty(results);
        Assert.True(results.Count >= 2);

        // Find the function and import results
        var functionResult = results.FirstOrDefault(r => r.id == "function_process");
        var importResult = results.FirstOrDefault(r => r.id == "import_process");

        Assert.NotNull(functionResult);
        Assert.NotNull(importResult);

        // Function should rank higher (have higher score) than import due to kind boosting
        Assert.True(functionResult.score > importResult.score,
            $"Expected function score ({functionResult.score}) > import score ({importResult.score})");
    }

    private static List<float> CreateTestVector(float seed)
    {
        var vector = new List<float>(768);
        for (int i = 0; i < 768; i++)
        {
            vector.Add((float)Math.Sin(seed + i * 0.001));
        }
        // L2 normalize
        var norm = (float)Math.Sqrt(vector.Sum(x => x * x));
        return vector.Select(x => x / norm).ToList();
    }
}
