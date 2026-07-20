using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class MillerSemanticContractTests
{
    private static SemanticGenerationIdentity Pinned() => MillerSemanticContract.PinnedIdentity(
        MillerSemanticContract.DefaultEncoder);

    [Fact]
    public void PinnedValues_MatchTheFrozenContract()
    {
        Assert.Equal("1", MillerSemanticContract.ContractVersion);
        Assert.Equal("blake3", MillerSemanticContract.HashAlgorithm);
        Assert.Equal("cards-v1-chunks-v1", MillerSemanticContract.CorpusGeneration);
        Assert.Equal("vec0-int8-512-cosine-v1", MillerSemanticContract.DefaultEncoder.StorageSchema);
        Assert.Equal("vec0-int8-384-cosine-v1", MillerSemanticContract.FallbackEncoder.StorageSchema);
    }

    [Fact]
    public void DefaultEncoderPin_IsTheQwen3LaneFromBenchPins()
    {
        SemanticEncoderPin pin = MillerSemanticContract.DefaultEncoder;

        Assert.Equal("qwen3-0.6b-f16", pin.ModelId);
        Assert.Equal("421a27e58d165478cc7acb984a688c2aa41404968b0203e7cd743ece44c54340", pin.ModelSha256);
        Assert.Equal("main", pin.ModelRevision);
        Assert.Equal(512, pin.Dims);
        Assert.Equal("last", pin.Pooling);
        Assert.Equal("<|endoftext|>", pin.EosAppend);
        Assert.Equal(
            "Instruct: Given a code search query, retrieve the code or documentation that answers it\nQuery: ",
            pin.QueryInstruction);
        Assert.Equal("", pin.DocumentInstruction);
    }

    [Fact]
    public void FallbackEncoderPin_IsTheBgeSmallLaneFromBenchPins()
    {
        SemanticEncoderPin pin = MillerSemanticContract.FallbackEncoder;

        Assert.Equal("bge-small-en-v1.5-f32", pin.ModelId);
        Assert.Equal("bf40c42ad7d89382e9ba7376d5c4b73f6b556cb541fab37aaa1da9c320149b65", pin.ModelSha256);
        Assert.Equal("main", pin.ModelRevision);
        Assert.Equal(384, pin.Dims);
        Assert.Equal("cls", pin.Pooling);
        Assert.Equal("", pin.EosAppend);
        Assert.Equal("Represent this sentence for searching relevant passages: ", pin.QueryInstruction);
    }

    [Fact]
    public void CanonicalEncoderString_IsTheContractsFieldOrderWithEscapedNewlines()
    {
        string canonical = MillerSemanticContract.CanonicalEncoderString(MillerSemanticContract.DefaultEncoder);

        Assert.Equal(
            string.Join('\n',
                "encoder-v1",
                "model_id=qwen3-0.6b-f16",
                "model_sha256=421a27e58d165478cc7acb984a688c2aa41404968b0203e7cd743ece44c54340",
                "model_revision=main",
                "dims=512",
                "pooling=last",
                "eos_append=<|endoftext|>",
                "query_instruction=Instruct: Given a code search query, retrieve the code or documentation that answers it\\nQuery: ",
                "document_instruction=",
                "normalization=l2"),
            canonical);
    }

    [Fact]
    public void EncoderFingerprint_IsAlgorithmTaggedLowercaseSha256()
    {
        string fingerprint = MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.DefaultEncoder);

        Assert.StartsWith("sha256:", fingerprint, StringComparison.Ordinal);
        Assert.Equal(71, fingerprint.Length);
        Assert.All(fingerprint["sha256:".Length..], c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void EncoderFingerprint_IsDeterministicAndSeparatesTheTwoPins()
    {
        Assert.Equal(
            MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.DefaultEncoder),
            MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.DefaultEncoder));

        Assert.NotEqual(
            MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.DefaultEncoder),
            MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.FallbackEncoder));
    }

    [Fact]
    public void EncoderFingerprint_ChangesWhenAnyComposedFieldChanges()
    {
        SemanticEncoderPin pin = MillerSemanticContract.DefaultEncoder;
        string baseline = MillerSemanticContract.EncoderFingerprint(pin);

        Assert.NotEqual(baseline, MillerSemanticContract.EncoderFingerprint(pin with { ModelId = "other" }));
        Assert.NotEqual(baseline, MillerSemanticContract.EncoderFingerprint(pin with { ModelSha256 = "deadbeef" }));
        Assert.NotEqual(baseline, MillerSemanticContract.EncoderFingerprint(pin with { ModelRevision = "other.gguf" }));
        Assert.NotEqual(baseline, MillerSemanticContract.EncoderFingerprint(pin with { Dims = 256 }));
        Assert.NotEqual(baseline, MillerSemanticContract.EncoderFingerprint(pin with { Pooling = "cls" }));
        Assert.NotEqual(baseline, MillerSemanticContract.EncoderFingerprint(pin with { EosAppend = "" }));
        Assert.NotEqual(baseline, MillerSemanticContract.EncoderFingerprint(pin with { QueryInstruction = "q: " }));
        Assert.NotEqual(baseline, MillerSemanticContract.EncoderFingerprint(pin with { DocumentInstruction = "d: " }));
    }

    [Fact]
    public void GenerationTag_IsSixteenHexCharsOverFingerprintAndLaneOnly()
    {
        SemanticGenerationIdentity identity = Pinned();
        string tag = MillerSemanticContract.GenerationTag(identity);

        Assert.Equal(16, tag.Length);
        Assert.All(tag, c => Assert.Contains(c, "0123456789abcdef"));
        Assert.Equal(
            tag,
            MillerSemanticContract.GenerationTag(identity with
            {
                CorpusGeneration = "cards-v2-chunks-v9",
                WriterVersion = "9.9.9+ffffffff",
                MinReaderVersion = "9.9.9",
                FusionProfile = "something-else",
            }));
    }

    [Fact]
    public void GenerationTag_DiffersWhenTheFingerprintOrLaneDiffers()
    {
        SemanticGenerationIdentity identity = Pinned();

        Assert.NotEqual(
            MillerSemanticContract.GenerationTag(identity),
            MillerSemanticContract.GenerationTag(identity with { EncoderFingerprint = "sha256:00" }));
        Assert.NotEqual(
            MillerSemanticContract.GenerationTag(identity),
            MillerSemanticContract.GenerationTag(identity with { StorageSchema = "vec0-int8-384-cosine-v1" }));
    }

    [Fact]
    public void ClassifyChange_UnchangedIdentityNeedsNoWork()
    {
        Assert.Equal(InvalidationAction.None, MillerSemanticContract.ClassifyChange(Pinned(), Pinned()));
    }

    [Theory]
    [InlineData("encoder_fingerprint", InvalidationAction.ShadowRebuild)]
    [InlineData("storage_schema", InvalidationAction.ShadowRebuild)]
    [InlineData("corpus_generation", InvalidationAction.TargetedReEmbed)]
    [InlineData("writer_version", InvalidationAction.ReaderGate)]
    [InlineData("min_reader_version", InvalidationAction.ReaderGate)]
    [InlineData("fusion_profile", InvalidationAction.QueryTimeOnly)]
    public void ClassifyChange_EachFieldMapsToTheContractsMechanism(string field, InvalidationAction expected)
    {
        SemanticGenerationIdentity previous = Pinned();
        SemanticGenerationIdentity current = field switch
        {
            "encoder_fingerprint" => previous with { EncoderFingerprint = "sha256:changed" },
            "storage_schema" => previous with { StorageSchema = "vec0-int8-384-cosine-v1" },
            "corpus_generation" => previous with { CorpusGeneration = "cards-v2-chunks-v1" },
            "writer_version" => previous with { WriterVersion = "9.9.9+ffffffff" },
            "min_reader_version" => previous with { MinReaderVersion = "9.9.9" },
            "fusion_profile" => previous with { FusionProfile = "rrf-k60-v2" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "unmapped identity field"),
        };

        Assert.Equal(expected, MillerSemanticContract.ClassifyChange(previous, current));
    }

    [Fact]
    public void ClassifyChange_FusionProfileNeverInvalidatesStoredVectors()
    {
        SemanticGenerationIdentity previous = Pinned();

        InvalidationAction action = MillerSemanticContract.ClassifyChange(
            previous,
            previous with { FusionProfile = "rrf-k60-v2" });

        Assert.Equal(InvalidationAction.QueryTimeOnly, action);
        Assert.False(MillerSemanticContract.RequiresEmbeddingWork(action));
    }

    [Fact]
    public void ClassifyChange_ReaderCompatibilityNeverTriggersReEmbedding()
    {
        SemanticGenerationIdentity previous = Pinned();

        InvalidationAction action = MillerSemanticContract.ClassifyChange(
            previous,
            previous with { MinReaderVersion = "99.0.0" });

        Assert.Equal(InvalidationAction.ReaderGate, action);
        Assert.False(MillerSemanticContract.RequiresEmbeddingWork(action));
    }

    [Theory]
    [InlineData(InvalidationAction.ShadowRebuild, true)]
    [InlineData(InvalidationAction.TargetedReEmbed, true)]
    [InlineData(InvalidationAction.ReaderGate, false)]
    [InlineData(InvalidationAction.QueryTimeOnly, false)]
    [InlineData(InvalidationAction.None, false)]
    public void RequiresEmbeddingWork_IsTrueOnlyForFieldsOneThroughThree(InvalidationAction action, bool expected)
    {
        Assert.Equal(expected, MillerSemanticContract.RequiresEmbeddingWork(action));
    }

    [Fact]
    public void ClassifyChange_TakesTheStrongestMechanismWhenSeveralFieldsChange()
    {
        SemanticGenerationIdentity previous = Pinned();

        Assert.Equal(
            InvalidationAction.ShadowRebuild,
            MillerSemanticContract.ClassifyChange(
                previous,
                previous with { EncoderFingerprint = "sha256:changed", FusionProfile = "rrf-k60-v2" }));

        Assert.Equal(
            InvalidationAction.TargetedReEmbed,
            MillerSemanticContract.ClassifyChange(
                previous,
                previous with { CorpusGeneration = "cards-v2-chunks-v1", MinReaderVersion = "9.9.9" }));
    }

    [Fact]
    public void ClassifyChange_ARevisionStampAdvancingIsNotAnIdentityChange()
    {
        Assert.Equal(InvalidationAction.None, MillerSemanticContract.ClassifyChange(Pinned(), Pinned()));
    }

    [Theory]
    [InlineData("1.13.0", "1.13.0", true)]
    [InlineData("1.13.0+abc1234", "1.13.0", true)]
    [InlineData("1.14.2", "1.13.0", true)]
    [InlineData("2.0.0", "1.13.0", true)]
    [InlineData("1.12.9", "1.13.0", false)]
    [InlineData("1.9.0", "1.13.0", false)]
    [InlineData("0.99.99", "1.0.0", false)]
    public void SatisfiesMinReaderVersion_ComparesSemverComponentsNotText(
        string reader, string minimum, bool expected)
    {
        Assert.Equal(expected, MillerSemanticContract.SatisfiesMinReaderVersion(reader, minimum));
    }

    [Fact]
    public void SatisfiesMinReaderVersion_AnUnparseableReaderVersionIsRefused()
    {
        Assert.False(MillerSemanticContract.SatisfiesMinReaderVersion("not-a-version", "1.13.0"));
    }

    [Theory]
    [InlineData("vec0-int8-512-cosine-v1", "int8", 512, "cosine", 1)]
    [InlineData("vec0-int8-384-cosine-v1", "int8", 384, "cosine", 1)]
    [InlineData("vec0-float-256-l2-v3", "float", 256, "l2", 3)]
    public void ParseStorageSchema_DecomposesTheLaneString(
        string lane, string element, int dims, string metric, int schemaRevision)
    {
        SemanticStorageLane parsed = MillerSemanticContract.ParseStorageSchema(lane);

        Assert.Equal(element, parsed.Element);
        Assert.Equal(dims, parsed.Dims);
        Assert.Equal(metric, parsed.Metric);
        Assert.Equal(schemaRevision, parsed.SchemaRevision);
        Assert.Equal(lane, parsed.Lane);
    }

    [Theory]
    [InlineData("")]
    [InlineData("int8-512-cosine-v1")]
    [InlineData("vec0-int8-512-cosine")]
    [InlineData("vec0-int8-notanumber-cosine-v1")]
    [InlineData("vec0-int8-512-cosine-vX")]
    public void ParseStorageSchema_RefusesAMalformedLane(string lane)
    {
        Assert.Throws<FormatException>(() => MillerSemanticContract.ParseStorageSchema(lane));
    }

    [Fact]
    public void PinnedIdentity_CarriesEveryRequiredMetaValue()
    {
        SemanticGenerationIdentity identity = Pinned();

        Assert.Equal(MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.DefaultEncoder),
            identity.EncoderFingerprint);
        Assert.Equal("vec0-int8-512-cosine-v1", identity.StorageSchema);
        Assert.Equal("cards-v1-chunks-v1", identity.CorpusGeneration);
        Assert.Equal(MillerSemanticContract.MinReaderVersion, identity.MinReaderVersion);
        Assert.Equal(MillerSemanticContract.FusionProfile, identity.FusionProfile);
        Assert.False(string.IsNullOrWhiteSpace(identity.WriterVersion));
    }
}
