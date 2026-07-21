using Miller.Indexing;
using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SemanticEncoderSelectionTests
{
    [Fact]
    public void KnownEncoders_AreTheQwen3AndBgeSmallPinsKeyedByModelId()
    {
        Assert.Equal(
            new[] { "qwen3-0.6b-f16", "bge-small-en-v1.5-f32" },
            MillerSemanticContract.KnownEncoders.Select(pin => pin.ModelId));

        Assert.Contains(MillerSemanticContract.DefaultEncoder, MillerSemanticContract.KnownEncoders);
        Assert.Contains(MillerSemanticContract.FallbackEncoder, MillerSemanticContract.KnownEncoders);
    }

    [Fact]
    public void FindEncoder_ReturnsTheExactModelIdMatchOrNull()
    {
        Assert.Same(MillerSemanticContract.DefaultEncoder, MillerSemanticContract.FindEncoder("qwen3-0.6b-f16"));
        Assert.Same(MillerSemanticContract.FallbackEncoder, MillerSemanticContract.FindEncoder("bge-small-en-v1.5-f32"));
        Assert.Null(MillerSemanticContract.FindEncoder("qwen3-0.6b-f16 "));
        Assert.Null(MillerSemanticContract.FindEncoder("nope"));
        Assert.Null(MillerSemanticContract.FindEncoder(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_UnsetOrEmpty_IsTheDefaultEncoderWithNoUnknownValue(string? raw)
    {
        SemanticEncoderResolution resolution = SemanticEncoderSelection.Resolve(raw);

        Assert.Same(MillerSemanticContract.DefaultEncoder, resolution.Pin);
        Assert.Null(resolution.UnknownModelId);
    }

    [Fact]
    public void Resolve_AKnownModelId_SelectsThatPinAndTrimsSurroundingWhitespace()
    {
        Assert.Same(MillerSemanticContract.FallbackEncoder, SemanticEncoderSelection.Resolve("bge-small-en-v1.5-f32").Pin);
        Assert.Same(MillerSemanticContract.DefaultEncoder, SemanticEncoderSelection.Resolve("qwen3-0.6b-f16").Pin);
        Assert.Same(MillerSemanticContract.FallbackEncoder, SemanticEncoderSelection.Resolve("  bge-small-en-v1.5-f32  ").Pin);
    }

    [Fact]
    public void Resolve_AnUnknownModelId_FallsBackToDefaultAndReportsTheUnknownValue()
    {
        SemanticEncoderResolution resolution = SemanticEncoderSelection.Resolve("mistral-7b");

        Assert.Same(MillerSemanticContract.DefaultEncoder, resolution.Pin);
        Assert.Equal("mistral-7b", resolution.UnknownModelId);
    }

    [Fact]
    public void ResolveAndWarn_AnUnknownModelId_WarnsExactlyOnce()
    {
        var warnings = new List<string>();

        SemanticEncoderPin pin = SemanticEncoderSelection.ResolveAndWarn("mistral-7b", warnings.Add);

        Assert.Same(MillerSemanticContract.DefaultEncoder, pin);
        Assert.Single(warnings);
        Assert.Contains("mistral-7b", warnings[0]);
        Assert.Contains(SemanticEncoderSelection.EnvVar, warnings[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("qwen3-0.6b-f16")]
    [InlineData("bge-small-en-v1.5-f32")]
    public void ResolveAndWarn_AKnownOrUnsetModelId_NeverWarns(string? raw)
    {
        var warnings = new List<string>();

        SemanticEncoderSelection.ResolveAndWarn(raw, warnings.Add);

        Assert.Empty(warnings);
    }

    [Fact]
    public void SelectingTheFallbackPin_ClassifiesAsAShadowRebuildAgainstTheDefault()
    {
        SemanticGenerationIdentity qwen3 = MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder);
        SemanticGenerationIdentity bge = MillerSemanticContract.PinnedIdentity(
            SemanticEncoderSelection.Resolve("bge-small-en-v1.5-f32").Pin);

        Assert.NotEqual(qwen3.EncoderFingerprint, bge.EncoderFingerprint);
        Assert.NotEqual(qwen3.StorageSchema, bge.StorageSchema);
        Assert.Equal(InvalidationAction.ShadowRebuild, MillerSemanticContract.ClassifyChange(qwen3, bge));
    }

    [Fact]
    public void EnvVar_IsTheModelSwapContractName() =>
        Assert.Equal("MILLER_SEMANTIC_MODEL", SemanticEncoderSelection.EnvVar);

    [Fact]
    public void VectorSidecar_ExposesTheInjectedEncoderAndAReaderThatCanOpenItsGenerations()
    {
        var sidecar = new VectorSidecar(
            SemanticMode.On,
            NullProbe.Instance,
            encoder: MillerSemanticContract.FallbackEncoder);

        Assert.Same(MillerSemanticContract.FallbackEncoder, sidecar.Encoder);
        Assert.Equal(
            MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.FallbackEncoder),
            sidecar.Reader.EncoderFingerprint);
    }

    [Fact]
    public void VectorSidecar_DefaultsToTheProcessWideActiveEncoder()
    {
        var sidecar = new VectorSidecar(SemanticMode.On, NullProbe.Instance);

        Assert.Same(SemanticEncoderSelection.Active, sidecar.Encoder);
    }

    [Fact]
    public void NoProductionSiteReadsDefaultEncoderDirectlyOutsideTheContractFile()
    {
        string srcRoot = Path.Combine(ScaleTestSupport.RepoRoot(), "src");
        Assert.True(Directory.Exists(srcRoot), $"Could not locate the source root at '{srcRoot}'.");

        var offenders = new List<string>();
        var scanned = 0;
        foreach (string path in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = path.Replace('\\', '/');
            if (normalized.Contains("/bin/", StringComparison.Ordinal) || normalized.Contains("/obj/", StringComparison.Ordinal))
                continue;
            if (string.Equals(Path.GetFileName(path), "MillerSemanticContract.cs", StringComparison.Ordinal))
                continue;

            scanned++;
            if (File.ReadAllText(path).Contains("DefaultEncoder", StringComparison.Ordinal))
                offenders.Add(Path.GetRelativePath(srcRoot, path));
        }

        Assert.True(scanned > 50, $"Expected to scan the production sources but saw only {scanned} files.");
        Assert.True(
            offenders.Count == 0,
            "These production files read MillerSemanticContract.DefaultEncoder directly instead of the resolved " +
            "active pin (SemanticEncoderSelection.Active). Route them through the selection seam:\n  " +
            string.Join("\n  ", offenders));
    }

    private sealed class NullProbe : IVectorFileProbe
    {
        public static readonly NullProbe Instance = new();

        public bool FileExists(string path) => false;

        public IReadOnlyList<string> EnumerateRetainedGenerations(string millerDir) => [];
    }
}
