using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// The pinned <c>julie-semantic-sidecar</c> against Miller's real client half — the promotion gate for a
/// sidecar release candidate. Every other semantic test drives a fake launcher, which proves Miller's protocol
/// logic but can never prove the two halves agree; these three do.
/// </summary>
/// <remarks>
/// <para>Scale-tagged because each test launches the real binary and loads a ~1.2 GB GGUF. All three obtain the
/// binary through <see cref="ScaleTestSupport.RequireSemanticSidecar"/>, which skips rather than fails when
/// restore has not run, and the round trip additionally skips when the pinned sqlite-vec extension is absent.</para>
/// <para>The extension path is passed explicitly rather than through <c>MILLER_SQLITE_VEC_PATH</c>, exactly as
/// <see cref="VectorStoreTests"/> does — this class mutates no process-global state itself, but it still LOADS
/// the packaged vec0 file, and a test in the SqliteVecEnvironment collection parks that file for its duration,
/// so the class serializes on the collection to never overlap the parked window.</para>
/// <para>Timeouts are deliberately far above production defaults: a cold machine's first <c>serve</c> downloads
/// the model before it can answer <c>health</c>, and a gate that flakes on a slow download proves nothing.</para>
/// </remarks>
[Collection(SqliteVecEnvironment.Name)]
[Trait("Category", "Scale")]
public sealed class SemanticSidecarScaleTests : IDisposable
{
    private const string ArtifactId = "artifact-scale-0001";

    private static readonly SemanticSessionOptions LiveBudgets = new()
    {
        InitTimeout = TimeSpan.FromMinutes(15),
        RequestTimeout = TimeSpan.FromMinutes(5),
    };

    private readonly string _dir = Directory.CreateTempSubdirectory("miller-semantic-scale-").FullName;

    [Fact]
    public async Task Handshake_AgreesWithThePinnedDefaultEncoder()
    {
        await using SemanticEmbeddingSession session = StartSession();

        SemanticEncoderHandshake? handshake = await session.EnsureStartedAsync(TestContext.Current.CancellationToken);

        Assert.True(handshake is not null,
            $"The pinned sidecar refused Miller's encoder handshake: {session.UnavailableReason}");
        Assert.Equal(
            MillerSemanticContract.EncoderFingerprint(MillerSemanticContract.DefaultEncoder),
            handshake!.EncoderFingerprint);
        Assert.Equal(MillerSemanticContract.DefaultEncoder.Dims, handshake.Dims);
        Assert.Equal(SemanticSessionState.Ready, session.State);
    }

    [Fact]
    public async Task EmbedBatch_ReturnsThePinnedDimsAndQuantizesIntoThePinnedInt8Lane()
    {
        await using SemanticEmbeddingSession session = StartSession();
        string card = SymbolCardBuilder.Build(new SymbolCardInput(
            "sym-1",
            "FullRebuildPromotion",
            "class",
            "src/Miller.Indexing/FullRebuildPromotion.cs",
            IsTest: false,
            Signature: "public static class FullRebuildPromotion",
            DocComment: "/// Promotes a freshly extracted rebuild over the live artifact atomically."));

        SemanticEmbedOutcome outcome = await session.EmbedBatchAsync([card], TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        float[] vector = Assert.Single(outcome.Vectors);
        Assert.Equal(MillerSemanticContract.DefaultEncoder.Dims, vector.Length);
        Assert.Empty(outcome.FlaggedIndices);

        SemanticStorageLane lane = MillerSemanticContract.ParseStorageSchema(
            MillerSemanticContract.DefaultEncoder.StorageSchema);
        sbyte[] quantized = SemanticVectorQuantizer.ToInt8(vector);

        Assert.Equal(lane.Dims, quantized.Length);
        Assert.Equal("int8", lane.Element);
        Assert.Contains(quantized, component => component != 0);
    }

    [Fact]
    public async Task PlantedSymbol_ConvergesThenAnswersASemanticallySimilarProseQuery()
    {
        string extension = SqliteVecTestSupport.RequireExtension();
        await using SemanticEmbeddingSession session = StartSession();

        string workspaceRoot = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, ".miller"));

        SymbolCardInput[] corpus =
        [
            new("sym-promote", "FullRebuildPromotion", "class",
                "src/Miller.Indexing/FullRebuildPromotion.cs", IsTest: false,
                Signature: "public static class FullRebuildPromotion",
                DocComment: "Atomically replaces the live symbol index file with a freshly rebuilt one."),
            new("sym-colour", "PaletteSwatch", "class",
                "src/Miller.Dashboard/PaletteSwatch.cs", IsTest: false,
                Signature: "public sealed class PaletteSwatch",
                DocComment: "A named colour swatch rendered in the dashboard legend."),
            new("sym-clock", "MarketHoursCalendar", "class",
                "src/Miller.Reports/MarketHoursCalendar.cs", IsTest: false,
                Signature: "public sealed class MarketHoursCalendar",
                DocComment: "Weekday and holiday calendar for stock exchange trading sessions."),
        ];

        string[] cards = [.. corpus.Select(SymbolCardBuilder.Build)];
        SemanticEmbedOutcome embedded = await session.EmbedBatchAsync(cards, TestContext.Current.CancellationToken);
        Assert.True(embedded.Succeeded, embedded.FailureReason);

        using (VectorStore store = VectorStore.Create(
                   VectorSidecar.PathFor(workspaceRoot),
                   MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder),
                   ArtifactId,
                   extension))
        {
            store.CommitBatch(
                VectorUnitKind.Symbol,
                [.. corpus.Select((input, i) => new VectorBatchEntry(
                    input.SymbolId,
                    input.Path,
                    input.Kind,
                    input.IsTest,
                    SemanticVectorQuantizer.ToInt8(embedded.Vectors[i]),
                    SymbolCardBuilder.EmbedTextHash(cards[i])))],
                [],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["build_state"] = "ready" },
                revision: 1);
        }

        var sidecar = new VectorSidecar(SemanticMode.On, SystemVectorFileProbe.Instance, new RealOpener(extension));
        var arm = new SemanticSearchArm(workspaceRoot, sidecar, () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync(
            "swap the freshly built index over the one being served, all at once",
            k: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Served, result.UnavailableReason);
        Assert.NotEmpty(result.Hits);
        Assert.Equal("sym-promote", result.Hits[0].SymbolId);
        Assert.Equal("src/Miller.Indexing/FullRebuildPromotion.cs", result.Hits[0].FilePath);
        Assert.Equal(1, result.Hits[0].Rank);
    }

    private SemanticEmbeddingSession StartSession() =>
        new(new ProcessSemanticSidecarLauncher(ScaleTestSupport.RequireSemanticSidecar()), LiveBudgets);

    /// <summary>Opens the real artifact through an explicit extension path, mirroring
    /// <see cref="VectorStoreTests"/>: the environment override is process-global and unsafe to set under
    /// xunit's parallel collections.</summary>
    private sealed class RealOpener(string extensionPath) : IVectorStoreOpener
    {
        public bool TryReadMeta(string path, out IReadOnlyDictionary<string, string> meta, out string failureReason)
        {
            meta = VectorStore.ReadMetaAt(path, extensionPath);
            failureReason = string.Empty;
            return true;
        }

        public VectorStore? OpenStore(string path, out string failureReason)
        {
            failureReason = string.Empty;
            return VectorStore.Open(path, extensionPath, readOnly: true);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
