using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Codesearch.Embeddings;

public sealed class EmbeddingModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _dimension;
    private bool _disposed;

    public int Dimension => _dimension;

    public EmbeddingModel(string modelPath, int dimension = 768)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Model file not found: {modelPath}");

        _dimension = dimension;
        using var options = new SessionOptions();

        // Try CoreML on macOS
        if (OperatingSystem.IsMacOS())
        {
            try { options.AppendExecutionProvider_CoreML(); }
            catch { /* Fall through to CPU */ }
        }

        _session = new InferenceSession(modelPath, options);
    }

    public float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Simplified tokenization (placeholder - real impl needs proper tokenizer)
        var inputIds = TokenizeSimple(text);
        var attentionMask = new long[inputIds.Length];
        Array.Fill(attentionMask, 1L);

        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, inputIds.Length]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, attentionMask.Length]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor)
        };

        using var results = _session.Run(inputs);

        // Get output and handle pooling if needed
        var output = results.First();
        var tensor = output.AsTensor<float>();
        var embedding = ExtractEmbedding(tensor);

        // L2 normalize
        Normalize(embedding);
        return embedding;
    }

    /// <summary>
    /// Embed multiple texts (calls Embed individually - not true batching).
    /// </summary>
    public float[][] EmbedMany(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return texts.Select(Embed).ToArray();
    }

    private float[] ExtractEmbedding(Tensor<float> tensor)
    {
        var embedding = new float[_dimension];

        if (tensor.Dimensions.Length == 3)
        {
            // [batch, seq_len, hidden] - need mean pooling
            var seqLen = (int)tensor.Dimensions[1];
            for (int d = 0; d < _dimension; d++)
            {
                float sum = 0;
                for (int s = 0; s < seqLen; s++)
                    sum += tensor[0, s, d];
                embedding[d] = sum / seqLen;
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
            for (int i = 0; i < vector.Length; i++)
                vector[i] /= norm;
    }

    private static long[] TokenizeSimple(string text)
    {
        // Placeholder tokenization - hash words to token IDs
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokens = new long[Math.Min(words.Length + 2, 512)];

        tokens[0] = 101; // [CLS]
        for (int i = 0; i < Math.Min(words.Length, 510); i++)
            tokens[i + 1] = Math.Abs(words[i].GetHashCode()) % 30000 + 1000;
        tokens[^1] = 102; // [SEP]

        return tokens;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _session.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
