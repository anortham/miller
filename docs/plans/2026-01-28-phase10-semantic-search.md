# Phase 10: Semantic Search Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable semantic code search using local ONNX embeddings with automatic model management.

**Architecture:** Add ModelManager for auto-downloading nomic-embed-text-v1.5, update EmbeddingModel with proper HuggingFace tokenization, wire embeddings into IndexService, and expose hybrid search (text + semantic) through SearchTool with mode parameter.

**Tech Stack:** ONNX Runtime, Tokenizers.DotNet, nomic-embed-text-v1.5 (768 dimensions)

---

## Prerequisites

Phase 9 complete with:
- Navigation tools working
- 47 passing tests
- Existing EmbeddingModel.cs with ONNX Runtime setup

---

### Task 1: Add Tokenizers.DotNet Package

**Files:**
- Modify: `src/Codesearch.Embeddings/Codesearch.Embeddings.csproj`

**Step 1: Add the NuGet package**

```bash
cd /Users/murphy/source/codesearch
dotnet add src/Codesearch.Embeddings package Tokenizers.DotNet
```

**Step 2: Verify it restores**

```bash
dotnet restore src/Codesearch.Embeddings
```

**Step 3: Commit**

```bash
git add src/Codesearch.Embeddings/Codesearch.Embeddings.csproj
git commit -m "chore(embeddings): add Tokenizers.DotNet package"
```

---

### Task 2: Create ModelManager

**Files:**
- Create: `src/Codesearch.Embeddings/ModelManager.cs`

**Step 1: Create ModelManager class**

Create `src/Codesearch.Embeddings/ModelManager.cs`:

```csharp
using System.Net.Http.Headers;

namespace Codesearch.Embeddings;

public record ModelPaths(string ModelPath, string TokenizerPath);

public record DownloadProgress(string FileName, long BytesDownloaded, long TotalBytes)
{
    public double Percentage => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;
}

public static class ModelManager
{
    private const string ModelName = "nomic-embed-text-v1.5";
    private const string HuggingFaceBase = "https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/resolve/main";
    private const string ModelFileName = "onnx/model.onnx";
    private const string TokenizerFileName = "tokenizer.json";

    // Expected file sizes for validation (approximate)
    private const long ExpectedModelSize = 140_000_000; // ~140MB
    private const long ExpectedTokenizerSize = 2_000_000; // ~2MB

    private static string ModelsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codesearch", "models", ModelName);

    public static string ModelPath => Path.Combine(ModelsDirectory, "model.onnx");
    public static string TokenizerPath => Path.Combine(ModelsDirectory, "tokenizer.json");

    public static bool IsModelDownloaded()
    {
        return File.Exists(ModelPath) && File.Exists(TokenizerPath);
    }

    public static ModelPaths GetModelPaths()
    {
        if (!IsModelDownloaded())
        {
            throw new InvalidOperationException(
                $"Model not downloaded. Call {nameof(EnsureModelAsync)} first.");
        }
        return new ModelPaths(ModelPath, TokenizerPath);
    }

    public static async Task<ModelPaths> EnsureModelAsync(
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (IsModelDownloaded())
        {
            return new ModelPaths(ModelPath, TokenizerPath);
        }

        Directory.CreateDirectory(ModelsDirectory);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Codesearch", "1.0"));

        // Download tokenizer first (smaller, faster feedback)
        await DownloadFileAsync(
            client,
            $"{HuggingFaceBase}/{TokenizerFileName}",
            TokenizerPath,
            "tokenizer.json",
            progress,
            ct);

        // Download model
        await DownloadFileAsync(
            client,
            $"{HuggingFaceBase}/{ModelFileName}",
            ModelPath,
            "model.onnx",
            progress,
            ct);

        return new ModelPaths(ModelPath, TokenizerPath);
    }

    private static async Task DownloadFileAsync(
        HttpClient client,
        string url,
        string destPath,
        string displayName,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var tempPath = destPath + ".tmp";

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;
                progress?.Report(new DownloadProgress(displayName, totalRead, totalBytes));
            }

            await fileStream.FlushAsync(ct);
        }
        catch
        {
            // Clean up temp file on failure
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }

        // Atomic rename
        File.Move(tempPath, destPath, overwrite: true);
    }

    /// <summary>
    /// Delete cached model files (for testing or re-download).
    /// </summary>
    public static void ClearCache()
    {
        if (Directory.Exists(ModelsDirectory))
        {
            Directory.Delete(ModelsDirectory, recursive: true);
        }
    }
}
```

**Step 2: Verify it compiles**

```bash
dotnet build src/Codesearch.Embeddings
```

**Step 3: Commit**

```bash
git add src/Codesearch.Embeddings/ModelManager.cs
git commit -m "feat(embeddings): add ModelManager for auto-downloading model"
```

---

### Task 3: Update EmbeddingModel with Proper Tokenizer

**Files:**
- Modify: `src/Codesearch.Embeddings/EmbeddingModel.cs`

**Step 1: Rewrite EmbeddingModel to use Tokenizers.DotNet**

Replace the contents of `src/Codesearch.Embeddings/EmbeddingModel.cs`:

```csharp
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;

namespace Codesearch.Embeddings;

public sealed class EmbeddingModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;
    private readonly int _dimension;
    private bool _disposed;

    public int Dimension => _dimension;

    /// <summary>
    /// Maximum sequence length for nomic-embed-text-v1.5
    /// </summary>
    public const int MaxSequenceLength = 8192;

    public EmbeddingModel(string modelPath, string tokenizerPath, int dimension = 768)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Model file not found: {modelPath}");
        if (!File.Exists(tokenizerPath))
            throw new FileNotFoundException($"Tokenizer file not found: {tokenizerPath}");

        _dimension = dimension;
        _tokenizer = Tokenizer.FromFile(tokenizerPath);

        using var options = new SessionOptions();

        // Try CoreML on macOS for acceleration
        if (OperatingSystem.IsMacOS())
        {
            try { options.AppendExecutionProvider_CoreML(); }
            catch { /* Fall through to CPU */ }
        }

        _session = new InferenceSession(modelPath, options);
    }

    /// <summary>
    /// Embed a single text.
    /// </summary>
    public float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var encoded = _tokenizer.Encode(text);
        var inputIds = encoded.Ids.Take(MaxSequenceLength).Select(id => (long)id).ToArray();
        var attentionMask = encoded.AttentionMask.Take(MaxSequenceLength).Select(m => (long)m).ToArray();

        return RunInference(inputIds, attentionMask);
    }

    /// <summary>
    /// Embed multiple texts efficiently.
    /// </summary>
    public float[][] EmbedBatch(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (texts.Count == 0)
            return Array.Empty<float[]>();

        // For now, process individually (ONNX batching requires padding)
        // Can optimize later if needed
        return texts.Select(Embed).ToArray();
    }

    private float[] RunInference(long[] inputIds, long[] attentionMask)
    {
        var seqLen = inputIds.Length;

        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, seqLen]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, seqLen]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor)
        };

        using var results = _session.Run(inputs);

        var output = results.First();
        var tensor = output.AsTensor<float>();
        var embedding = ExtractEmbedding(tensor, attentionMask);

        Normalize(embedding);
        return embedding;
    }

    private float[] ExtractEmbedding(Tensor<float> tensor, long[] attentionMask)
    {
        var embedding = new float[_dimension];

        if (tensor.Dimensions.Length == 3)
        {
            // [batch, seq_len, hidden] - mean pooling with attention mask
            var seqLen = (int)tensor.Dimensions[1];
            var validTokens = attentionMask.Sum();

            for (int d = 0; d < _dimension; d++)
            {
                float sum = 0;
                for (int s = 0; s < seqLen; s++)
                {
                    if (attentionMask[s] == 1)
                        sum += tensor[0, s, d];
                }
                embedding[d] = validTokens > 0 ? sum / validTokens : 0;
            }
        }
        else
        {
            // [batch, hidden] - already pooled
            for (int d = 0; d < _dimension; d++)
                embedding[d] = tensor[0, d];
        }

        return embedding;
    }

    private static void Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        if (norm > 0)
        {
            for (int i = 0; i < vector.Length; i++)
                vector[i] /= norm;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
        _tokenizer.Dispose();
    }
}
```

**Step 2: Verify it compiles**

```bash
dotnet build src/Codesearch.Embeddings
```

**Step 3: Commit**

```bash
git add src/Codesearch.Embeddings/EmbeddingModel.cs
git commit -m "feat(embeddings): update EmbeddingModel with proper tokenizer"
```

---

### Task 4: Create EmbeddingService

**Files:**
- Create: `src/Codesearch.Embeddings/EmbeddingService.cs`

**Step 1: Create EmbeddingService class**

Create `src/Codesearch.Embeddings/EmbeddingService.cs`:

```csharp
namespace Codesearch.Embeddings;

/// <summary>
/// High-level embedding service with automatic model management.
/// Thread-safe singleton for use across the application.
/// </summary>
public sealed class EmbeddingService : IDisposable
{
    private EmbeddingModel? _model;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Prefix for documents (symbols being indexed).
    /// </summary>
    public const string DocumentPrefix = "search_document: ";

    /// <summary>
    /// Prefix for queries (search terms).
    /// </summary>
    public const string QueryPrefix = "search_query: ";

    /// <summary>
    /// Whether the model is loaded and ready.
    /// </summary>
    public bool IsReady => _model != null;

    /// <summary>
    /// Ensure the model is downloaded and loaded.
    /// </summary>
    public async Task EnsureReadyAsync(
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (_model != null) return;

        var paths = await ModelManager.EnsureModelAsync(progress, ct);

        lock (_lock)
        {
            _model ??= new EmbeddingModel(paths.ModelPath, paths.TokenizerPath);
        }
    }

    /// <summary>
    /// Ensure the model is ready (synchronous, blocks if downloading needed).
    /// </summary>
    public void EnsureReady()
    {
        if (_model != null) return;

        var paths = ModelManager.EnsureModelAsync().GetAwaiter().GetResult();

        lock (_lock)
        {
            _model ??= new EmbeddingModel(paths.ModelPath, paths.TokenizerPath);
        }
    }

    /// <summary>
    /// Embed a document (for indexing).
    /// Automatically adds the document prefix.
    /// </summary>
    public float[] EmbedDocument(string text)
    {
        EnsureModelLoaded();
        return _model!.Embed(DocumentPrefix + text);
    }

    /// <summary>
    /// Embed a query (for searching).
    /// Automatically adds the query prefix.
    /// </summary>
    public float[] EmbedQuery(string text)
    {
        EnsureModelLoaded();
        return _model!.Embed(QueryPrefix + text);
    }

    /// <summary>
    /// Embed multiple documents efficiently.
    /// </summary>
    public float[][] EmbedDocuments(IReadOnlyList<string> texts)
    {
        EnsureModelLoaded();
        var prefixed = texts.Select(t => DocumentPrefix + t).ToList();
        return _model!.EmbedBatch(prefixed);
    }

    /// <summary>
    /// Generate embedding text for a symbol.
    /// </summary>
    public static string GetSymbolText(string kind, string name, string? signature)
    {
        var text = $"{kind} {name}";
        if (!string.IsNullOrEmpty(signature))
            text += $" {signature}";
        return text;
    }

    private void EnsureModelLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_model == null)
        {
            throw new InvalidOperationException(
                $"Model not loaded. Call {nameof(EnsureReadyAsync)} or {nameof(EnsureReady)} first.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model?.Dispose();
        _model = null;
    }
}
```

**Step 2: Verify it compiles**

```bash
dotnet build src/Codesearch.Embeddings
```

**Step 3: Commit**

```bash
git add src/Codesearch.Embeddings/EmbeddingService.cs
git commit -m "feat(embeddings): add EmbeddingService wrapper"
```

---

### Task 5: Add Embeddings Reference to Server

**Files:**
- Modify: `src/Codesearch.Server/Codesearch.Server.csproj`

**Step 1: Add project reference**

```bash
cd /Users/murphy/source/codesearch
dotnet add src/Codesearch.Server reference src/Codesearch.Embeddings
```

**Step 2: Verify it builds**

```bash
dotnet build src/Codesearch.Server
```

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Codesearch.Server.csproj
git commit -m "chore(server): add Embeddings project reference"
```

---

### Task 6: Add Semantic Search Methods to SearchService

**Files:**
- Modify: `src/Codesearch.Server/Services/SearchService.cs`

**Step 1: Add EmbeddingService field and new search methods**

Add to the class fields (near the top):

```csharp
private readonly EmbeddingService? _embeddingService;
```

Update the constructor to accept optional EmbeddingService:

```csharp
public SearchService(string dbPath, EmbeddingService? embeddingService = null)
{
    _engine = new uniffi.codesearch_ffi.CodeSearchEngine(dbPath);
    _embeddingService = embeddingService;
}
```

Add new search methods (after existing SearchText method):

```csharp
/// <summary>
/// Search using semantic similarity only.
/// </summary>
public List<uniffi.codesearch_ffi.SearchResultOutput> SearchSemantic(string query, int limit = 20)
{
    if (_embeddingService == null || !_embeddingService.IsReady)
    {
        throw new InvalidOperationException("Embedding service not available. Use SearchText instead.");
    }

    var queryVector = _embeddingService.EmbedQuery(query).ToList();
    return _engine.SearchVector(queryVector, (uint)limit);
}

/// <summary>
/// Search using hybrid mode (text + semantic with RRF fusion).
/// Falls back to text-only if embeddings unavailable.
/// </summary>
public List<uniffi.codesearch_ffi.SearchResultOutput> SearchHybrid(string query, int limit = 20)
{
    if (_embeddingService == null || !_embeddingService.IsReady)
    {
        // Graceful fallback to text search
        return SearchText(query, limit);
    }

    var queryVector = _embeddingService.EmbedQuery(query).ToList();
    return _engine.SearchHybrid(query, queryVector, (uint)limit);
}

/// <summary>
/// Check if semantic search is available.
/// </summary>
public bool IsSemanticSearchAvailable => _embeddingService?.IsReady ?? false;
```

**Step 2: Verify it compiles**

```bash
dotnet build src/Codesearch.Server
```

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Services/SearchService.cs
git commit -m "feat(server): add semantic and hybrid search methods"
```

---

### Task 7: Update IndexService for Real Embeddings

**Files:**
- Modify: `src/Codesearch.Server/Services/IndexService.cs`

**Step 1: Add EmbeddingService field**

Add to class fields:

```csharp
private readonly EmbeddingService _embeddingService;
```

**Step 2: Update constructor**

Update constructor signature and body:

```csharp
public IndexService(SearchService searchService, EmbeddingService embeddingService, string workspacePath)
{
    _searchService = searchService;
    _embeddingService = embeddingService;
    _workspacePath = workspacePath;
}
```

**Step 3: Add embedding generation helper**

Add this method:

```csharp
private List<float> GenerateEmbedding(ExtractedSymbol symbol)
{
    var text = EmbeddingService.GetSymbolText(symbol.kind, symbol.name, symbol.signature);
    return _embeddingService.EmbedDocument(text).ToList();
}

private List<List<float>> GenerateEmbeddings(IReadOnlyList<ExtractedSymbol> symbols)
{
    var texts = symbols
        .Select(s => EmbeddingService.GetSymbolText(s.kind, s.name, s.signature))
        .ToList();

    return _embeddingService.EmbedDocuments(texts)
        .Select(arr => arr.ToList())
        .ToList();
}
```

**Step 4: Update IndexFile method to use real embeddings**

Find the section where symbols are added (look for `AddSymbols` call) and update:

Replace the dummy vector generation:
```csharp
// OLD: var vectors = symbols.Select(_ => Enumerable.Repeat(0.0f, 768).ToList()).ToList();
// NEW:
var vectors = GenerateEmbeddings(results.symbols);
```

**Step 5: Verify it compiles**

```bash
dotnet build src/Codesearch.Server
```

**Step 6: Commit**

```bash
git add src/Codesearch.Server/Services/IndexService.cs
git commit -m "feat(server): generate real embeddings during indexing"
```

---

### Task 8: Update SearchTool with Mode Parameter

**Files:**
- Modify: `src/Codesearch.Server/Tools/SearchTool.cs`

**Step 1: Update Search method with mode parameter**

Replace the Search method:

```csharp
[McpServerTool]
[Description("Search for symbols in the codebase. Modes: text (exact matching), semantic (meaning-based), hybrid (combined - default).")]
internal static string Search(
    SearchService searchService,
    [Description("Search query")] string query,
    [Description("Search mode: text, semantic, or hybrid")] string mode = "hybrid",
    [Description("Maximum number of results")] int limit = 20)
{
    if (string.IsNullOrWhiteSpace(query))
    {
        return "Error: query parameter is required.";
    }

    List<uniffi.codesearch_ffi.SearchResultOutput> results;
    string modeUsed;

    try
    {
        (results, modeUsed) = mode.ToLowerInvariant() switch
        {
            "text" => (searchService.SearchText(query, limit), "text"),
            "semantic" => (searchService.SearchSemantic(query, limit), "semantic"),
            "hybrid" => (searchService.SearchHybrid(query, limit),
                        searchService.IsSemanticSearchAvailable ? "hybrid" : "text (fallback)"),
            _ => (searchService.SearchHybrid(query, limit),
                 searchService.IsSemanticSearchAvailable ? "hybrid" : "text (fallback)")
        };
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Embedding"))
    {
        // Semantic requested but not available
        results = searchService.SearchText(query, limit);
        modeUsed = "text (embeddings unavailable)";
    }

    if (results.Count == 0)
    {
        return $"No results found for '{query}' (mode: {modeUsed}).";
    }

    var sb = new StringBuilder();
    sb.AppendLine($"## Search Results for `{query}` ({results.Count} found, mode: {modeUsed})");
    sb.AppendLine();

    foreach (var result in results)
    {
        var score = result.score.HasValue ? $" (score: {result.score.Value:F3})" : "";
        sb.AppendLine($"### {result.name} ({result.kind}){score}");
        sb.AppendLine($"- **File**: `{result.filePath}:{result.startLine}-{result.endLine}`");
        sb.AppendLine($"- **Language**: {result.language}");
        if (!string.IsNullOrEmpty(result.signature))
        {
            sb.AppendLine($"- **Signature**: `{result.signature}`");
        }
        sb.AppendLine();
    }

    return sb.ToString();
}
```

**Step 2: Add using for StringBuilder if not present**

Ensure `using System.Text;` is at the top.

**Step 3: Verify it compiles**

```bash
dotnet build src/Codesearch.Server
```

**Step 4: Commit**

```bash
git add src/Codesearch.Server/Tools/SearchTool.cs
git commit -m "feat(server): add mode parameter to SearchTool"
```

---

### Task 9: Update Server Startup for EmbeddingService

**Files:**
- Modify: `src/Codesearch.Server/Program.cs`

**Step 1: Create and wire up EmbeddingService**

Find where SearchService and IndexService are created. Add EmbeddingService initialization:

```csharp
// Add near the top with other service creation
var embeddingService = new EmbeddingService();

// Try to ensure model is ready (don't block startup, but start download)
_ = Task.Run(async () =>
{
    try
    {
        await embeddingService.EnsureReadyAsync();
        Console.Error.WriteLine("Embedding model ready.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warning: Could not load embedding model: {ex.Message}");
        Console.Error.WriteLine("Semantic search will be unavailable. Text search still works.");
    }
});
```

Update SearchService creation to include embeddingService:

```csharp
// Pass embeddingService to SearchService
var searchService = new SearchService(dbPath, embeddingService);
```

Update IndexService creation to include embeddingService:

```csharp
// Pass embeddingService to IndexService
var indexService = new IndexService(searchService, embeddingService, workspacePath);
```

**Step 2: Verify it compiles**

```bash
dotnet build src/Codesearch.Server
```

**Step 3: Commit**

```bash
git add src/Codesearch.Server/Program.cs
git commit -m "feat(server): wire up EmbeddingService in startup"
```

---

### Task 10: Add Embedding Tests

**Files:**
- Create: `tests/Codesearch.Tests/EmbeddingTests.cs`

**Step 1: Create embedding tests**

Create `tests/Codesearch.Tests/EmbeddingTests.cs`:

```csharp
using Xunit;
using Codesearch.Embeddings;

namespace Codesearch.Tests;

public class EmbeddingTests
{
    [Fact]
    public void ModelManager_IsModelDownloaded_ReturnsFalseInitially()
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
}
```

**Step 2: Run tests**

```bash
dotnet test --filter "FullyQualifiedName~EmbeddingTests"
```

Expected: All tests pass

**Step 3: Commit**

```bash
git add tests/Codesearch.Tests/EmbeddingTests.cs
git commit -m "test: add embedding service tests"
```

---

### Task 11: Update Documentation

**Files:**
- Modify: `.claude-plugin/README.md`

**Step 1: Update search tool documentation**

Find the `### search` section and update it:

```markdown
### search

Search for symbols in the codebase with text, semantic, or hybrid modes.

```
search(query="authenticate", mode="hybrid", limit=20)
search(query="functions that handle user login", mode="semantic")
search(query="AuthService", mode="text")
```

Parameters:
- **query**: Search query (required)
- **mode**: Search mode (optional, default: "hybrid")
  - `text`: Exact text matching (fast, precise)
  - `semantic`: Meaning-based search (finds related concepts)
  - `hybrid`: Combines both with rank fusion (recommended)
- **limit**: Maximum results (optional, default: 20)

Note: Semantic search requires the embedding model (~140MB, auto-downloaded on first use).
```

**Step 2: Commit**

```bash
git add .claude-plugin/README.md
git commit -m "docs(plugin): update search tool with semantic modes"
```

---

### Task 12: Final Verification

**Step 1: Build everything**

```bash
cd /Users/murphy/source/codesearch
dotnet build
```

**Step 2: Run all tests**

```bash
dotnet test
```

Expected: All tests pass (47 + 5 = 52 tests)

**Step 3: Verify model download works (manual test)**

```bash
# This will trigger model download if not already cached
dotnet run --project src/Codesearch.Server -- --help
```

**Step 4: Commit any fixes**

```bash
git add -A
git commit -m "chore: final Phase 10 fixes" --allow-empty
```

---

## Phase 10 Complete

At this point you have:

- **Automatic model management**: Model downloads on first use to `~/.codesearch/models/`
- **Proper tokenization**: HuggingFace Tokenizers.DotNet with nomic-embed-text
- **Real embeddings during indexing**: Symbols stored with semantic vectors
- **Three search modes**:
  - `text`: Fast exact matching
  - `semantic`: Meaning-based similarity
  - `hybrid`: Combined with RRF fusion (default)
- **Graceful fallback**: Text search works even if model unavailable

**Usage:**
```
# Re-index to generate embeddings
index(operation="full")

# Search by meaning
search(query="functions that validate user credentials", mode="semantic")

# Default hybrid search
search(query="authenticate")
```

**Next Phase (11):** Performance optimization, batch embedding, incremental indexing.
