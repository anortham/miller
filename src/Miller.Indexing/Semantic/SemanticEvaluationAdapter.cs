using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Miller.Indexing.Semantic;

/// <summary>
/// Explicit evaluator-only bridge for a protocol-v1 encoder that is not part of Miller's production model
/// selection. Loading requires a complete frozen identity and a supplied producer command.
/// </summary>
public sealed class SemanticEvaluationAdapter
{
    public const string Schema = "miller.semantic.evaluation-adapter";
    public const int Version = 1;
    public const string CodeRankArmId = "coderank-current-julie";
    public const string Normalization = "l2";

    public static SemanticEncoderPin CodeRankEncoder { get; } = new(
        ModelId: "nomic-ai/CodeRankEmbed",
        ModelSha256: "827529bcd58aef0d9082e66eeff7e7d53a02f62bd005f841a26b3d3e2fb17ebe",
        ModelRevision: "3c4b60807d71f79b43f3c4363786d9493691f8b1",
        Dims: 768,
        Pooling: "cls",
        EosAppend: "",
        QueryInstruction: "",
        DocumentInstruction: "",
        StorageSchema: "vec0-int8-768-cosine-v1");

    private SemanticEvaluationAdapter(
        string sourceSha256,
        string producerExecutable,
        IReadOnlyList<string> producerArguments,
        IReadOnlyDictionary<string, string> producerEnvironment)
    {
        SourceSha256 = sourceSha256;
        ProducerExecutable = producerExecutable;
        ProducerArguments = producerArguments;
        ProducerEnvironment = producerEnvironment;
    }

    public SemanticEncoderPin Encoder => CodeRankEncoder;

    public SemanticGenerationIdentity GenerationIdentity => MillerSemanticContract.PinnedIdentity(Encoder);

    public string SourceSha256 { get; }

    public string ProducerExecutable { get; }

    public IReadOnlyList<string> ProducerArguments { get; }

    public IReadOnlyDictionary<string, string> ProducerEnvironment { get; }

    public static SemanticEvaluationAdapter? LoadWhenEnabled(SemanticMode mode, string configPath)
    {
        if (mode is SemanticMode.Off)
            return null;

        return Load(configPath);
    }

    public static SemanticEvaluationAdapter Load(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        byte[] source = File.ReadAllBytes(configPath);

        try
        {
            using JsonDocument document = JsonDocument.Parse(source);
            JsonElement root = RequireObject(document.RootElement, "root");
            EnsureProperties(
                root,
                "root",
                ["schema", "version", "arm_id", "normalization", "encoder", "producer"]);
            RequireString(root, "schema", Schema);
            RequireInt32(root, "version", Version);
            RequireString(root, "arm_id", CodeRankArmId);
            RequireString(root, "normalization", Normalization);

            JsonElement encoder = RequireObject(root, "encoder");
            EnsureProperties(
                encoder,
                "encoder",
                [
                    "model_id",
                    "model_sha256",
                    "model_revision",
                    "dims",
                    "pooling",
                    "eos_append",
                    "query_instruction",
                    "document_instruction",
                    "storage_schema",
                ]);
            RequireString(encoder, "model_id", CodeRankEncoder.ModelId);
            RequireString(encoder, "model_sha256", CodeRankEncoder.ModelSha256);
            RequireString(encoder, "model_revision", CodeRankEncoder.ModelRevision);
            RequireInt32(encoder, "dims", CodeRankEncoder.Dims);
            RequireString(encoder, "pooling", CodeRankEncoder.Pooling);
            RequireString(encoder, "eos_append", CodeRankEncoder.EosAppend);
            RequireString(encoder, "query_instruction", CodeRankEncoder.QueryInstruction);
            RequireString(encoder, "document_instruction", CodeRankEncoder.DocumentInstruction);
            RequireString(encoder, "storage_schema", CodeRankEncoder.StorageSchema);

            JsonElement producer = RequireObject(root, "producer");
            EnsureProperties(producer, "producer", ["executable", "arguments", "environment"]);
            string executable = RequireNonEmptyString(producer, "executable");
            string[] arguments = ReadStringArray(producer, "arguments");
            IReadOnlyDictionary<string, string> environment = ReadStringMap(producer, "environment");

            return new SemanticEvaluationAdapter(
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(source)),
                executable,
                arguments,
                environment);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Semantic evaluation config '{configPath}' is not valid JSON: {ex.Message}",
                ex);
        }
    }

    public VectorSidecar CreateVectorSidecar(SemanticMode mode)
    {
        if (mode is SemanticMode.Off)
            return VectorSidecar.Disabled;

        return new VectorSidecar(mode, SystemVectorFileProbe.Instance, encoder: Encoder);
    }

    public SemanticEmbeddingSession CreateSession(SemanticSessionOptions? options = null) =>
        CreateSession(
            new ProcessSemanticSidecarLauncher(
                ProducerExecutable,
                ProducerArguments,
                ProducerEnvironment),
            options);

    public SemanticEmbeddingSession CreateSession(
        ISemanticSidecarLauncher launcher,
        SemanticSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        return new SemanticEmbeddingSession(launcher, options, Encoder);
    }

    public void WriteEvidence(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var payload = new
        {
            schema = Schema,
            version = Version,
            arm_id = CodeRankArmId,
            encoder_fingerprint = MillerSemanticContract.EncoderFingerprint(Encoder),
            model_id = Encoder.ModelId,
            model_revision = Encoder.ModelRevision,
            model_sha256 = Encoder.ModelSha256,
            dims = Encoder.Dims,
            pooling = Encoder.Pooling,
            normalization = Normalization,
            query_instruction = Encoder.QueryInstruction,
            document_instruction = Encoder.DocumentInstruction,
            storage_schema = Encoder.StorageSchema,
            corpus_generation = GenerationIdentity.CorpusGeneration,
            fusion_profile = GenerationIdentity.FusionProfile,
            evaluation_config_sha256 = SourceSha256,
            producer = new
            {
                executable = ProducerExecutable,
                arguments = ProducerArguments,
                environment_names = ProducerEnvironment.Keys.Order(StringComparer.Ordinal).ToArray(),
            },
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static void EnsureProperties(JsonElement value, string objectName, IReadOnlyList<string> allowed)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var permitted = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!observed.Add(property.Name))
                throw Invalid(property.Name, $"is a duplicate field in '{objectName}'");
            if (!permitted.Contains(property.Name))
                throw Invalid(property.Name, $"is not permitted in '{objectName}'");
        }
    }

    private static JsonElement RequireObject(JsonElement parent, string property)
    {
        JsonElement value = string.Equals(property, "root", StringComparison.Ordinal)
            ? parent
            : parent.TryGetProperty(property, out JsonElement found)
                ? found
                : throw Invalid(property, "is missing");
        return value.ValueKind is JsonValueKind.Object
            ? value
            : throw Invalid(property, "must be an object");
    }

    private static string RequireNonEmptyString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind is not JsonValueKind.String)
            throw Invalid(property, "must be a string");
        string result = value.GetString()!;
        if (string.IsNullOrWhiteSpace(result) || result.Contains('\0', StringComparison.Ordinal))
            throw Invalid(property, "must be non-empty and contain no NUL");
        return result;
    }

    private static void RequireString(JsonElement parent, string property, string expected)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind is not JsonValueKind.String)
            throw Invalid(property, "must be a string");
        string actual = value.GetString()!;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw Invalid(property, $"'{actual}' does not match frozen value '{expected}'");
    }

    private static void RequireInt32(JsonElement parent, string property, int expected)
    {
        if (!parent.TryGetProperty(property, out JsonElement value)
            || value.ValueKind is not JsonValueKind.Number
            || !value.TryGetInt32(out int actual))
        {
            throw Invalid(property, "must be an integer");
        }

        if (actual != expected)
            throw Invalid(property, $"{actual} does not match frozen value {expected}");
    }

    private static string[] ReadStringArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind is not JsonValueKind.Array)
            throw Invalid(property, "must be an array of strings");

        var result = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.String
                || item.GetString() is not { } text
                || text.Contains('\0', StringComparison.Ordinal))
            {
                throw Invalid(property, "must contain only strings without NUL");
            }
            result.Add(text);
        }
        return [.. result];
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement parent, string property)
    {
        JsonElement value = RequireObject(parent, property);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in value.EnumerateObject())
        {
            if (string.IsNullOrEmpty(entry.Name)
                || entry.Name.Contains('=', StringComparison.Ordinal)
                || entry.Name.Contains('\0', StringComparison.Ordinal)
                || entry.Value.ValueKind is not JsonValueKind.String
                || entry.Value.GetString() is not { } text
                || text.Contains('\0', StringComparison.Ordinal))
            {
                throw Invalid(property, "must map valid environment names to string values without NUL");
            }
            if (!result.TryAdd(entry.Name, text))
                throw Invalid(property, $"contains duplicate name '{entry.Name}'");
        }
        return result;
    }

    private static InvalidDataException Invalid(string property, string reason) =>
        new($"Semantic evaluation config field '{property}' {reason}.");
}
