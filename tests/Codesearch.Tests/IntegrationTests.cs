using Xunit;
using uniffi.codesearch_ffi;

namespace Codesearch.Tests;

public class IntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public IntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_integration_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_tempDir, "test.lance");
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
    public void EndToEndSearchWorkflow()
    {
        using var engine = new CodeSearchEngine(_dbPath);

        // 1. Add symbols with various kinds
        var symbols = new List<SymbolInput>
        {
            new("1", "find_user", "function", "rust", "src/db.rs",
                "pub fn find_user(id: i64) -> Option<User>", "Finds user by ID",
                10, 20, null),
            new("2", "create_user", "function", "rust", "src/db.rs",
                "pub fn create_user(data: UserData) -> User", null,
                25, 40, null),
            new("3", "User", "struct", "rust", "src/models.rs",
                "pub struct User", "User model",
                5, 15, null),
            new("4", "user", "import", "rust", "src/main.rs",
                null, null, 1, 1, null),
        };

        var vectors = symbols.Select((_, i) => TestHelpers.CreateTestVector(i * 0.5f)).ToList();
        engine.AddSymbols(symbols, vectors);

        // 2. Create FTS index
        engine.CreateFtsIndex();

        // 3. Text search
        var textResults = engine.SearchText("user", 10);
        Assert.NotEmpty(textResults);

        // 4. Boosted search - function should rank higher than import
        var boostedResults = engine.SearchTextBoosted("user", 10);
        Assert.NotEmpty(boostedResults);
        Assert.True(boostedResults[0].kind == "function" || boostedResults[0].kind == "struct",
            $"Expected function or struct, got {boostedResults[0].kind}");

        // 5. Vector search
        var queryVec = TestHelpers.CreateTestVector(0.0f);
        var vectorResults = engine.SearchVector(queryVec, 10);
        Assert.NotEmpty(vectorResults);
        Assert.Equal("find_user", vectorResults[0].name);

        // 6. Hybrid search
        var hybridResults = engine.SearchHybrid("find", queryVec, 10);
        Assert.NotEmpty(hybridResults);

        // 7. Verify count
        Assert.Equal(4UL, engine.SymbolCount());
    }

    [Fact]
    public void MultipleSearchMethodsReturnConsistentResults()
    {
        using var engine = new CodeSearchEngine(_dbPath);

        // Add a distinctive symbol - use single-word name for whitespace tokenizer compatibility
        var symbols = new List<SymbolInput>
        {
            new("unique_1", "fibonacci", "function", "rust", "src/math.rs",
                "pub fn fibonacci(n: u64) -> u64", "Calculates Fibonacci sequence",
                1, 20, null),
        };

        var vectors = new List<List<float>> { TestHelpers.CreateTestVector(1.0f) };
        engine.AddSymbols(symbols, vectors);
        engine.CreateFtsIndex();

        // All search methods should find the symbol
        var textResults = engine.SearchText("fibonacci", 10);
        var boostedResults = engine.SearchTextBoosted("fibonacci", 10);
        var vectorResults = engine.SearchVector(TestHelpers.CreateTestVector(1.0f), 10);
        var hybridResults = engine.SearchHybrid("fibonacci", TestHelpers.CreateTestVector(1.0f), 10);
        var hybridBoostedResults = engine.SearchHybridBoosted("fibonacci", TestHelpers.CreateTestVector(1.0f), 10);

        Assert.NotEmpty(textResults);
        Assert.NotEmpty(boostedResults);
        Assert.NotEmpty(vectorResults);
        Assert.NotEmpty(hybridResults);
        Assert.NotEmpty(hybridBoostedResults);

        // All should return our symbol
        Assert.Equal("unique_1", textResults[0].id);
        Assert.Equal("unique_1", boostedResults[0].id);
        Assert.Equal("unique_1", vectorResults[0].id);
        Assert.Equal("unique_1", hybridResults[0].id);
        Assert.Equal("unique_1", hybridBoostedResults[0].id);
    }

    [Fact]
    public void KindBoostingWorksAcrossSearchMethods()
    {
        using var engine = new CodeSearchEngine(_dbPath);

        // Add symbols with same name but different kinds
        var symbols = new List<SymbolInput>
        {
            new("import_1", "config", "import", "rust", "src/lib.rs",
                null, null, 1, 1, null),
            new("function_1", "config", "function", "rust", "src/lib.rs",
                "pub fn config() -> Config", null,
                10, 20, null),
            new("struct_1", "Config", "struct", "rust", "src/lib.rs",
                "pub struct Config", null,
                25, 35, null),
        };

        // Use identical vectors so only kind boosting affects ranking
        var sameVector = TestHelpers.CreateTestVector(1.0f);
        var vectors = new List<List<float>> { sameVector, sameVector, sameVector };
        engine.AddSymbols(symbols, vectors);
        engine.CreateFtsIndex();

        // Text boosted should rank function/struct higher than import
        var boostedResults = engine.SearchTextBoosted("config", 10);
        Assert.True(boostedResults.Count >= 2);
        Assert.True(boostedResults[0].kind == "function" || boostedResults[0].kind == "struct",
            $"Expected function or struct, got {boostedResults[0].kind}");

        // Hybrid boosted should also rank function/struct higher
        var hybridBoostedResults = engine.SearchHybridBoosted("config", sameVector, 10);
        Assert.True(hybridBoostedResults.Count >= 2);
        Assert.True(hybridBoostedResults[0].kind == "function" || hybridBoostedResults[0].kind == "struct",
            $"Expected function or struct, got {hybridBoostedResults[0].kind}");
    }
}
