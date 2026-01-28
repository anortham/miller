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
