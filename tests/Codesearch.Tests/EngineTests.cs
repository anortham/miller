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
}
