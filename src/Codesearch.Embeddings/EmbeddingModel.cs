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
        _tokenizer = new Tokenizer(tokenizerPath);

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

        var tokenIds = _tokenizer.Encode(text);

        // Truncate if needed and convert to long[]
        var length = Math.Min(tokenIds.Length, MaxSequenceLength);
        var inputIds = new long[length];
        var attentionMask = new long[length];

        for (int i = 0; i < length; i++)
        {
            inputIds[i] = tokenIds[i];
            attentionMask[i] = 1; // All tokens are valid (no padding)
        }

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
