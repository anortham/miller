using Xunit;
using Codesearch.Embeddings;

namespace Codesearch.Tests;

public class EmbeddingTests
{
    [Fact]
    public void ModelManager_IsModelDownloaded_DoesNotThrow()
    {
        // This test checks the API works, not actual download state
        // (model might already be downloaded on dev machine)
        var result = ModelManager.IsModelDownloaded();
        Assert.True(result || !result); // Just verify it doesn't throw
    }

    [Fact]
    public void EmbeddingService_GetSymbolText_FormatsCorrectly()
    {
        var text = EmbeddingService.GetSymbolText("function", "authenticate", "fn authenticate(token: &str) -> bool");

        Assert.Contains("function", text);
        Assert.Contains("authenticate", text);
        Assert.Contains("fn authenticate", text);
    }

    [Fact]
    public void EmbeddingService_GetSymbolText_HandlesNullSignature()
    {
        var text = EmbeddingService.GetSymbolText("class", "User", null);

        Assert.Equal("class User", text);
    }

    [Fact]
    public void EmbeddingService_ThrowsWhenNotReady()
    {
        using var service = new EmbeddingService();

        // Should throw because model not loaded
        Assert.Throws<InvalidOperationException>(() => service.EmbedQuery("test"));
    }

    [Fact]
    public void EmbeddingService_Prefixes_AreCorrect()
    {
        Assert.Equal("search_document: ", EmbeddingService.DocumentPrefix);
        Assert.Equal("search_query: ", EmbeddingService.QueryPrefix);
    }

    [Fact]
    public void EmbeddingService_IsReady_FalseBeforeLoad()
    {
        using var service = new EmbeddingService();
        Assert.False(service.IsReady);
    }

    [Fact]
    public void ModelManager_ModelPath_IsInUserDirectory()
    {
        var path = ModelManager.ModelPath;
        Assert.Contains(".codesearch", path);
        Assert.Contains("models", path);
        Assert.EndsWith("model.onnx", path);
    }

    [Fact]
    public void ModelManager_TokenizerPath_IsInUserDirectory()
    {
        var path = ModelManager.TokenizerPath;
        Assert.Contains(".codesearch", path);
        Assert.Contains("models", path);
        Assert.EndsWith("tokenizer.json", path);
    }
}
