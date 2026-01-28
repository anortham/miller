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
    private const int BufferSize = 81920;

    private static readonly SemaphoreSlim _downloadLock = new(1, 1);

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
        await _downloadLock.WaitAsync(ct);
        try
        {
            if (IsModelDownloaded())
            {
                return new ModelPaths(ModelPath, TokenizerPath);
            }

            Directory.CreateDirectory(ModelsDirectory);

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
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
        finally
        {
            _downloadLock.Release();
        }
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
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);

            var buffer = new byte[BufferSize];
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
