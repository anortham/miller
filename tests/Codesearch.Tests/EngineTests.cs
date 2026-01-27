using Xunit;
using uniffi.codesearch_ffi;

namespace Codesearch.Tests;

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
}
