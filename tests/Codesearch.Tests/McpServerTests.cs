using Xunit;
using Codesearch.Server.Services;

namespace Codesearch.Tests;

public class McpServerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SearchService _searchService;
    private readonly IndexService _indexService;

    public McpServerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"codesearch_mcp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var dbPath = Path.Combine(_tempDir, "test.lance");
        _searchService = new SearchService(dbPath);
        _indexService = new IndexService(_searchService);
    }

    public void Dispose()
    {
        _searchService.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void SearchService_HealthCheck_ReturnsTrue()
    {
        Assert.True(_searchService.HealthCheck());
    }

    [Fact]
    public void IndexService_GetStatus_ReturnsValidStatus()
    {
        var status = _indexService.GetStatus();

        Assert.NotNull(status);
        Assert.True(status.IsHealthy);
        Assert.Equal(0UL, status.SymbolCount);
    }

    [Fact]
    public async Task IndexService_FullIndex_IndexesFiles()
    {
        // Create a test file
        var testFile = Path.Combine(_tempDir, "test.rs");
        await File.WriteAllTextAsync(testFile, "pub fn hello() {}");

        // Run full index
        var result = await _indexService.FullIndexAsync(_tempDir);

        Assert.Equal(1, result.FilesIndexed);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SearchService_SearchText_ReturnsResults()
    {
        // Add a test symbol
        var symbol = new uniffi.codesearch_ffi.SymbolInput(
            id: "test1",
            name: "test_function",
            kind: "function",
            language: "rust",
            filePath: "test.rs",
            signature: "fn test_function()",
            docComment: null,
            startLine: 1,
            endLine: 5,
            content: null
        );
        var vector = Enumerable.Repeat(0.0f, 768).ToList();
        _searchService.AddSymbols(
            new List<uniffi.codesearch_ffi.SymbolInput> { symbol },
            new List<List<float>> { vector }
        );
        _searchService.CreateFtsIndex();

        // Search
        var results = _searchService.SearchText("test_function", 10);

        Assert.NotEmpty(results);
        Assert.Equal("test_function", results[0].name);
    }
}
