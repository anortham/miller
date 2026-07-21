using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Miller.Indexing.Semantic;

/// <summary>
/// The one defined consequence of changing a generation-identity field (vectors-v1 §Invalidation matrix).
/// Members are ordered weakest-to-strongest so a multi-field change resolves to the strongest mechanism by
/// ordinal comparison.
/// </summary>
public enum InvalidationAction
{
    /// <summary>Nothing changed that the artifact records.</summary>
    None,

    /// <summary>Query-time ranking only — <c>fusion_profile</c>. Never touches stored vectors.</summary>
    QueryTimeOnly,

    /// <summary>Which binaries may open the artifact — <c>reader_compatibility</c>. Never re-embeds.</summary>
    ReaderGate,

    /// <summary>Re-embed only the units whose constructed text changed — <c>corpus_generation</c>.</summary>
    TargetedReEmbed,

    /// <summary>Build a full new generation beside the active one — <c>encoder_fingerprint</c> or
    /// <c>storage_schema</c>.</summary>
    ShadowRebuild,
}

/// <summary>
/// The nine values composing an <c>encoder_fingerprint</c> (vectors-v1 §Pinned initial values), plus the
/// storage lane the pin embeds into. Model identity is sourced from <c>eval/model-bench/bench-pins.json</c>,
/// which the contract names as the single source — these values are never re-derived.
/// </summary>
public sealed record SemanticEncoderPin(
    string ModelId,
    string ModelSha256,
    string ModelRevision,
    int Dims,
    string Pooling,
    string EosAppend,
    string QueryInstruction,
    string DocumentInstruction,
    string StorageSchema);

/// <summary>The decomposed <c>storage_schema</c> lane string <c>vec0-&lt;element&gt;-&lt;dims&gt;-&lt;metric&gt;-v&lt;rev&gt;</c>.</summary>
public sealed record SemanticStorageLane(string Lane, string Element, int Dims, string Metric, int SchemaRevision);

/// <summary>
/// The five independent generation-identity fields of vectors-v1 §Generation identity, as they are stored in
/// <c>vectors_meta</c>. <c>reader_compatibility</c> is two meta values, so it appears here as
/// <see cref="WriterVersion"/> plus <see cref="MinReaderVersion"/>.
/// </summary>
public sealed record SemanticGenerationIdentity(
    string EncoderFingerprint,
    string StorageSchema,
    string CorpusGeneration,
    string WriterVersion,
    string MinReaderVersion,
    string FusionProfile);

/// <summary>The identity a reader brings to a generation: whose vectors it can interpret, and which build it is.</summary>
public sealed record SemanticReaderIdentity(string EncoderFingerprint, string ReaderVersion);

/// <summary>
/// Pure generation-identity and invalidation logic for the vectors-v1 artifact — the pinned initial values, the
/// <c>encoder_fingerprint</c> and generation-tag compositions, the invalidation matrix, and the semver reader
/// gate. Deliberately I/O-free: the physical artifact lives in <see cref="VectorStore"/>, so every rule here is
/// unit-testable without sqlite-vec.
/// </summary>
public static class MillerSemanticContract
{
    public const string ContractVersion = "1";

    /// <summary>Matches the extract artifact's <c>artifact_metadata.hash_algorithm</c> guard.</summary>
    public const string HashAlgorithm = "blake3";

    public const string CorpusGeneration = "cards-v1-chunks-v1";

    /// <summary>The first Miller version able to read a vectors-v1 artifact.</summary>
    public const string MinReaderVersion = "1.13.0";

    /// <summary>The reader-side fusion profile recorded at build time. Query-time only — changing it never
    /// invalidates stored vectors.</summary>
    public const string FusionProfile = "fusion-v1";

    private const string EncoderCompositionVersion = "encoder-v1";

    private const string Normalization = "l2";

    private const int GenerationTagLength = 16;

    public static SemanticEncoderPin DefaultEncoder { get; } = new(
        ModelId: "qwen3-0.6b-f16",
        ModelSha256: "421a27e58d165478cc7acb984a688c2aa41404968b0203e7cd743ece44c54340",
        ModelRevision: "main",
        Dims: 512,
        Pooling: "last",
        EosAppend: "<|endoftext|>",
        QueryInstruction: "Instruct: Given a code search query, retrieve the code or documentation that answers it\nQuery: ",
        DocumentInstruction: "",
        StorageSchema: "vec0-int8-512-cosine-v1");

    public static SemanticEncoderPin FallbackEncoder { get; } = new(
        ModelId: "bge-small-en-v1.5-f32",
        ModelSha256: "bf40c42ad7d89382e9ba7376d5c4b73f6b556cb541fab37aaa1da9c320149b65",
        ModelRevision: "main",
        Dims: 384,
        Pooling: "cls",
        EosAppend: "",
        QueryInstruction: "Represent this sentence for searching relevant passages: ",
        DocumentInstruction: "",
        StorageSchema: "vec0-int8-384-cosine-v1");

    /// <summary>The encoders Miller can build and read, newest-canonical first. The active encoder is selected
    /// from this set by <see cref="SemanticEncoderSelection"/>; nothing outside it is a valid Miller pin.</summary>
    public static IReadOnlyList<SemanticEncoderPin> KnownEncoders { get; } = [DefaultEncoder, FallbackEncoder];

    /// <summary>The known encoder whose <see cref="SemanticEncoderPin.ModelId"/> exactly equals
    /// <paramref name="modelId"/>, or null when none matches (including a null argument).</summary>
    public static SemanticEncoderPin? FindEncoder(string modelId)
    {
        foreach (SemanticEncoderPin pin in KnownEncoders)
        {
            if (string.Equals(pin.ModelId, modelId, StringComparison.Ordinal))
                return pin;
        }

        return null;
    }

    /// <summary>The identity a fresh generation is stamped with for <paramref name="pin"/>.</summary>
    public static SemanticGenerationIdentity PinnedIdentity(SemanticEncoderPin pin, string? writerVersion = null)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return new SemanticGenerationIdentity(
            EncoderFingerprint(pin),
            pin.StorageSchema,
            CorpusGeneration,
            writerVersion ?? MinReaderVersion,
            MinReaderVersion,
            FusionProfile);
    }

    /// <summary>Renders the fingerprint as <c>sha256:&lt;64 hex&gt;</c> over
    /// <see cref="CanonicalEncoderString"/>.</summary>
    public static string EncoderFingerprint(SemanticEncoderPin pin) =>
        "sha256:" + Sha256Hex(CanonicalEncoderString(pin));

    /// <summary>
    /// The newline-joined canonical string of vectors-v1 §Pinned initial values. Field order is fixed, a
    /// missing value is the empty string rather than an omitted line, and instruction newlines are escaped as
    /// a literal <c>\n</c> so one field can never be mistaken for the field separator.
    /// </summary>
    internal static string CanonicalEncoderString(SemanticEncoderPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return string.Join('\n',
            EncoderCompositionVersion,
            $"model_id={pin.ModelId}",
            $"model_sha256={pin.ModelSha256}",
            $"model_revision={pin.ModelRevision}",
            $"dims={pin.Dims.ToString(CultureInfo.InvariantCulture)}",
            $"pooling={pin.Pooling}",
            $"eos_append={pin.EosAppend}",
            $"query_instruction={EscapeNewlines(pin.QueryInstruction)}",
            $"document_instruction={EscapeNewlines(pin.DocumentInstruction)}",
            $"normalization={Normalization}");
    }

    /// <summary>
    /// The generation tag that names a retained <c>vectors.gen-&lt;tag&gt;.db</c>. It covers exactly the two
    /// fields that gate readability, so two generations sharing a tag are query-interchangeable.
    /// </summary>
    public static string GenerationTag(SemanticGenerationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return GenerationTag(identity.EncoderFingerprint, identity.StorageSchema);
    }

    public static string GenerationTag(string encoderFingerprint, string storageSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageSchema);
        return Sha256Hex($"{encoderFingerprint}\n{storageSchema}")[..GenerationTagLength];
    }

    /// <summary>
    /// The invalidation matrix. A change touching several fields resolves to the strongest mechanism, which
    /// subsumes the weaker ones — a shadow rebuild restamps every other field anyway.
    /// </summary>
    public static InvalidationAction ClassifyChange(
        SemanticGenerationIdentity previous,
        SemanticGenerationIdentity current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var action = InvalidationAction.None;

        if (!Same(previous.EncoderFingerprint, current.EncoderFingerprint)
            || !Same(previous.StorageSchema, current.StorageSchema))
        {
            action = Strongest(action, InvalidationAction.ShadowRebuild);
        }

        if (!Same(previous.CorpusGeneration, current.CorpusGeneration))
            action = Strongest(action, InvalidationAction.TargetedReEmbed);

        if (!Same(previous.WriterVersion, current.WriterVersion)
            || !Same(previous.MinReaderVersion, current.MinReaderVersion))
        {
            action = Strongest(action, InvalidationAction.ReaderGate);
        }

        if (!Same(previous.FusionProfile, current.FusionProfile))
            action = Strongest(action, InvalidationAction.QueryTimeOnly);

        return action;
    }

    /// <summary>Only identity fields 1–3 cost embedding work; the reader gate and the fusion profile never do.</summary>
    public static bool RequiresEmbeddingWork(InvalidationAction action) =>
        action is InvalidationAction.ShadowRebuild or InvalidationAction.TargetedReEmbed;

    /// <summary>
    /// Whether <paramref name="readerVersion"/> may open a generation stamped with
    /// <paramref name="minReaderVersion"/>. Version strings are not lexicographically orderable
    /// (<c>'1.9.0' &gt; '1.13.0'</c> as text), so the comparison parses semver components; build metadata and
    /// prerelease suffixes are ignored, and an unparseable reader version is refused rather than assumed new.
    /// </summary>
    public static bool SatisfiesMinReaderVersion(string readerVersion, string minReaderVersion)
    {
        if (!TryParseSemver(readerVersion, out (int Major, int Minor, int Patch) reader))
            return false;
        if (!TryParseSemver(minReaderVersion, out (int Major, int Minor, int Patch) minimum))
            return false;

        if (reader.Major != minimum.Major)
            return reader.Major > minimum.Major;
        if (reader.Minor != minimum.Minor)
            return reader.Minor > minimum.Minor;
        return reader.Patch >= minimum.Patch;
    }

    /// <summary>Decomposes a lane string so vec0 declarations are derived from it rather than hard-coded.</summary>
    public static SemanticStorageLane ParseStorageSchema(string lane)
    {
        string[] parts = (lane ?? string.Empty).Split('-');
        if (parts.Length != 5
            || !string.Equals(parts[0], "vec0", StringComparison.Ordinal)
            || parts[1].Length == 0
            || parts[3].Length == 0
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int dims)
            || parts[4].Length < 2
            || parts[4][0] != 'v'
            || !int.TryParse(parts[4][1..], NumberStyles.None, CultureInfo.InvariantCulture, out int schemaRevision))
        {
            throw new FormatException(
                $"'{lane}' is not a vectors-v1 storage_schema lane (expected vec0-<element>-<dims>-<metric>-v<rev>).");
        }

        return new SemanticStorageLane(lane!, parts[1], dims, parts[3], schemaRevision);
    }

    private static bool TryParseSemver(string? value, out (int Major, int Minor, int Patch) parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string core = value.Split('+', 2)[0].Split('-', 2)[0];
        string[] parts = core.Split('.');
        if (parts.Length != 3)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
        {
            return false;
        }

        parsed = (major, minor, patch);
        return true;
    }

    private static InvalidationAction Strongest(InvalidationAction left, InvalidationAction right) =>
        left > right ? left : right;

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    private static string EscapeNewlines(string value) => (value ?? string.Empty).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>The outcome of resolving a requested model id against <see cref="MillerSemanticContract.KnownEncoders"/>:
/// the pin to use, and the requested id when it was a non-empty value that matched no known encoder (the single
/// signal that a fallback-to-default warning is warranted).</summary>
public readonly record struct SemanticEncoderResolution(SemanticEncoderPin Pin, string? UnknownModelId);

/// <summary>
/// Selects the active semantic encoder from <c>MILLER_SEMANTIC_MODEL</c>. An unset or empty value keeps the
/// <see cref="MillerSemanticContract.DefaultEncoder"/>; an exact <see cref="SemanticEncoderPin.ModelId"/> match
/// against <see cref="MillerSemanticContract.KnownEncoders"/> swaps to that pin; an unrecognized value falls
/// back to the default and warns once. The swap is a pin change only — its
/// <see cref="MillerSemanticContract.PinnedIdentity"/> classifies as a <see cref="InvalidationAction.ShadowRebuild"/>,
/// which the existing generation machinery converges with the old generation retained for rollback.
/// </summary>
public static class SemanticEncoderSelection
{
    public const string EnvVar = "MILLER_SEMANTIC_MODEL";

    private static readonly Lazy<SemanticEncoderPin> ResolvedActive =
        new(() => ResolveAndWarn(Environment.GetEnvironmentVariable(EnvVar), Console.Error.WriteLine));

    /// <summary>The process-wide active encoder, resolved once on first access so the fallback warning fires at
    /// most once for the process lifetime.</summary>
    public static SemanticEncoderPin Active => ResolvedActive.Value;

    /// <summary>Reads <see cref="EnvVar"/> and returns the active pin. Cached — repeated calls never re-read the
    /// environment nor re-warn.</summary>
    public static SemanticEncoderPin FromEnvironment() => ResolvedActive.Value;

    /// <summary>The pure env-value ⇒ resolution mapping, side-effect free so selection is testable without
    /// mutating the process environment.</summary>
    public static SemanticEncoderResolution Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new SemanticEncoderResolution(MillerSemanticContract.DefaultEncoder, null);

        string modelId = raw.Trim();
        SemanticEncoderPin? match = MillerSemanticContract.FindEncoder(modelId);
        return match is not null
            ? new SemanticEncoderResolution(match, null)
            : new SemanticEncoderResolution(MillerSemanticContract.DefaultEncoder, modelId);
    }

    internal static SemanticEncoderPin ResolveAndWarn(string? raw, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);
        SemanticEncoderResolution resolution = Resolve(raw);
        if (resolution.UnknownModelId is { } unknown)
        {
            string known = string.Join(", ", MillerSemanticContract.KnownEncoders.Select(pin => pin.ModelId));
            warn($"{EnvVar}='{unknown}' is not a known Miller encoder ({known}); using '{resolution.Pin.ModelId}'.");
        }

        return resolution.Pin;
    }
}
