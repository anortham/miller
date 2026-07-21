using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Tools;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SemanticQueryDiagnosticsTests
{
    private const string Root = "/ws";
    private const string ConceptualQuery = "how does the workspace refresh converge";

    private static readonly SemanticEncoderPin Pin = MillerSemanticContract.DefaultEncoder;

    private static SemanticSessionOptions FastOptions => new()
    {
        RequestTimeout = TimeSpan.FromSeconds(10),
        InitTimeout = TimeSpan.FromSeconds(10),
        ShutdownTimeout = TimeSpan.FromSeconds(1),
        RestartBackoff = TimeSpan.Zero,
        RestartBackoffCap = TimeSpan.Zero,
        Delay = static (_, _) => Task.CompletedTask,
    };

    public enum Scenario
    {
        Disabled,
        VectorsMissing,
        ModelNotPrepared,
        CircuitOpen,
        EmbedError,
        EmbedTimeout,
        IncompatibleDims,
        IncompatibleMetric,
        KnnError,
    }

    [Theory]
    [InlineData(Scenario.Disabled, SemanticFallbackKind.Disabled)]
    [InlineData(Scenario.VectorsMissing, SemanticFallbackKind.VectorsMissing)]
    [InlineData(Scenario.ModelNotPrepared, SemanticFallbackKind.ModelNotPrepared)]
    [InlineData(Scenario.CircuitOpen, SemanticFallbackKind.CircuitOpen)]
    [InlineData(Scenario.EmbedError, SemanticFallbackKind.EmbedError)]
    [InlineData(Scenario.EmbedTimeout, SemanticFallbackKind.EmbedTimeout)]
    [InlineData(Scenario.IncompatibleDims, SemanticFallbackKind.VectorsIncompatible)]
    [InlineData(Scenario.IncompatibleMetric, SemanticFallbackKind.VectorsIncompatible)]
    [InlineData(Scenario.KnnError, SemanticFallbackKind.KnnError)]
    public async Task EveryAbstentionSite_YieldsItsMappedFallbackKind(Scenario scenario, SemanticFallbackKind expected)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SemanticQueryResult result;

        switch (scenario)
        {
            case Scenario.Disabled:
            {
                var port = new RecordingPort { Matches = [Match(1, 0.1, "a", "src/A.cs")] };
                var arm = new SemanticSearchArm(Root, enabled: false, port.Factory, static () => null);
                result = await arm.QuerySymbolsAsync("workspace refresh", 5, cancellationToken: ct);
                break;
            }

            case Scenario.VectorsMissing:
            {
                var port = new RecordingPort { UnavailableReason = "no vector artifact exists" };
                var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, static () => null);
                result = await arm.QuerySymbolsAsync("workspace refresh", 5, cancellationToken: ct);
                break;
            }

            case Scenario.ModelNotPrepared:
            {
                var port = new RecordingPort { Matches = [Match(1, 0.1, "a", "src/A.cs")] };
                var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, static () => null);
                result = await arm.QuerySymbolsAsync("workspace refresh", 5, cancellationToken: ct);
                break;
            }

            case Scenario.CircuitOpen:
            {
                var port = new RecordingPort { Matches = [Match(1, 0.1, "a", "src/A.cs")] };
                await using var session = new SemanticEmbeddingSession(
                    FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.GarbageOnStdout),
                    FastOptions with { FatalThreshold = 1 });
                var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);
                result = await arm.QuerySymbolsAsync("q", 4, cancellationToken: ct);
                break;
            }

            case Scenario.EmbedError:
            {
                var port = new RecordingPort { Matches = [Match(1, 0.1, "a", "src/A.cs")] };
                await using var session = new SemanticEmbeddingSession(
                    FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.ErrorEnvelope), FastOptions);
                var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);
                result = await arm.QuerySymbolsAsync("q", 4, cancellationToken: ct);
                break;
            }

            case Scenario.EmbedTimeout:
            {
                var port = new RecordingPort { Matches = [Match(1, 0.1, "a", "src/A.cs")] };
                await using var session = new SemanticEmbeddingSession(
                    FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.StallForever),
                    FastOptions with { RequestTimeout = TimeSpan.FromMilliseconds(300) });
                var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);
                result = await arm.QuerySymbolsAsync("q", 4, cancellationToken: ct);
                break;
            }

            case Scenario.IncompatibleDims:
            {
                var port = new RecordingPort { Lane = MillerSemanticContract.ParseStorageSchema("vec0-int8-512-cosine-v1") };
                await using var session = NewSession();
                var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);
                result = await arm.QuerySymbolsAsync("q", 4, cancellationToken: ct);
                break;
            }

            case Scenario.IncompatibleMetric:
            {
                var port = new RecordingPort { Lane = MillerSemanticContract.ParseStorageSchema("vec0-int8-384-l2-v1") };
                await using var session = NewSession();
                var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);
                result = await arm.QuerySymbolsAsync("q", 4, cancellationToken: ct);
                break;
            }

            case Scenario.KnnError:
            {
                var port = new RecordingPort { SearchFailure = new VectorStoreException("vec0 table is gone") };
                await using var session = NewSession();
                var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);
                result = await arm.QuerySymbolsAsync("q", 4, cancellationToken: ct);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        Assert.False(result.Served);
        Assert.NotNull(result.Diagnostics);
        Assert.Equal(expected, result.Diagnostics!.Fallback);
    }

    [Fact]
    public async Task AServedCall_CarriesFallbackNoneBackendTimingIdentityAndColdWarmth()
    {
        var port = new RecordingPort
        {
            Identity = MillerSemanticContract.PinnedIdentity(Pin),
            Matches = [Match(3, 0.10, "sym-a", "src/A.cs")],
        };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync(
            "workspace refresh", 5, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Served, result.UnavailableReason);
        SemanticQueryDiagnostics diagnostics = result.Diagnostics!;
        Assert.Equal(SemanticFallbackKind.None, diagnostics.Fallback);
        Assert.Equal("cpu", diagnostics.Backend);
        Assert.True(diagnostics.ColdEmbed);
        Assert.NotNull(diagnostics.EmbedMs);
        Assert.NotNull(diagnostics.KnnMs);
        Assert.True(diagnostics.EmbedMs >= 0);
        Assert.True(diagnostics.KnnMs >= 0);
        Assert.Equal(MillerSemanticContract.PinnedIdentity(Pin), diagnostics.Identity);
        Assert.Null(diagnostics.FusionProfile);
    }

    [Fact]
    public async Task ASecondCallOnAReadySession_ReportsAWarmEmbed()
    {
        var port = new RecordingPort { Matches = [Match(3, 0.10, "sym-a", "src/A.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);
        CancellationToken ct = TestContext.Current.CancellationToken;

        SemanticQueryResult cold = await arm.QuerySymbolsAsync("q", 5, cancellationToken: ct);
        SemanticQueryResult warm = await arm.QuerySymbolsAsync("q", 5, cancellationToken: ct);

        Assert.True(cold.Diagnostics!.ColdEmbed);
        Assert.False(warm.Diagnostics!.ColdEmbed);
    }

    [Fact]
    public async Task APreEmbedAbstention_ReportsNoBackendNoTimingAndNoIdentity()
    {
        var port = new RecordingPort { UnavailableReason = "no vector artifact exists" };
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, static () => null);

        SemanticQueryResult result = await arm.QuerySymbolsAsync(
            "q", 5, cancellationToken: TestContext.Current.CancellationToken);

        SemanticQueryDiagnostics diagnostics = result.Diagnostics!;
        Assert.Equal("none", diagnostics.Backend);
        Assert.False(diagnostics.ColdEmbed);
        Assert.Null(diagnostics.EmbedMs);
        Assert.Null(diagnostics.KnnMs);
        Assert.Null(diagnostics.Identity);
        Assert.Null(diagnostics.FusionProfile);
    }

    [Fact]
    public async Task AnEmbedFailure_TimesTheEmbedButReportsNoBackendOrKnn()
    {
        var port = new RecordingPort { Matches = [Match(1, 0.1, "a", "src/A.cs")] };
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.ErrorEnvelope), FastOptions);
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync(
            "q", 4, cancellationToken: TestContext.Current.CancellationToken);

        SemanticQueryDiagnostics diagnostics = result.Diagnostics!;
        Assert.Equal(SemanticFallbackKind.EmbedError, diagnostics.Fallback);
        Assert.Equal("none", diagnostics.Backend);
        Assert.NotNull(diagnostics.EmbedMs);
        Assert.Null(diagnostics.KnnMs);
    }

    [Fact]
    public async Task FusionArm_AfterAServedHybridCall_ExposesDiagnosticsWithTheFusionProfile()
    {
        var port = new RecordingPort
        {
            Identity = MillerSemanticContract.PinnedIdentity(Pin),
            Matches = [Match(1, 0.05, "sym-a", "src/A.cs")],
        };
        await using SemanticEmbeddingSession session = NewSession();
        var fusion = new SemanticSymbolFusionArm(
            SemanticMode.On, new SemanticSearchArm(Root, enabled: true, port.Factory, () => session));
        var index = new StubLookupIndex(Symbol(0, "sym-a", "Alpha", "src/A.cs"));

        fusion.Fuse(index, new SymbolFusionRequest(ConceptualQuery, [], 10, static _ => true, Root));

        SemanticQueryDiagnostics diagnostics = fusion.LastDiagnostics!;
        Assert.Equal(SemanticFallbackKind.None, diagnostics.Fallback);
        Assert.Equal(RrfFusion.FusionProfile, diagnostics.FusionProfile);
        Assert.Equal("cpu", diagnostics.Backend);
        Assert.NotNull(diagnostics.EmbedMs);
        Assert.NotNull(diagnostics.KnnMs);
        Assert.NotNull(diagnostics.Identity);
    }

    [Fact]
    public async Task FusionArm_WhenNotConsulted_LeavesDiagnosticsNull()
    {
        var port = new RecordingPort { Matches = [Match(1, 0.05, "sym-a", "src/A.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        var fusion = new SemanticSymbolFusionArm(
            SemanticMode.Shadow, new SemanticSearchArm(Root, enabled: true, port.Factory, () => session));
        var index = new StubLookupIndex(Symbol(0, "sym-a", "Alpha", "src/A.cs"));

        fusion.Fuse(index, new SymbolFusionRequest(ConceptualQuery, [], 10, static _ => true, Root));

        Assert.Null(fusion.LastDiagnostics);
        Assert.Equal(0, port.OpenCount);
    }

    private static SemanticEmbeddingSession NewSession() =>
        new(FakeSemanticSidecar.InProcessLauncher(), FastOptions);

    private static VectorMatch Match(long rowId, double distance, string unitId, string path) =>
        new(rowId, distance, unitId, path);

    private static IndexedSymbol Symbol(int docId, string symbolId, string name, string path) =>
        new(
            docId,
            symbolId,
            name,
            "void " + name + "()",
            "method",
            "csharp",
            path,
            3,
            6,
            ParentId: null,
            IsTest: false);

    private sealed class RecordingPort
    {
        public string? UnavailableReason { get; init; }

        public IReadOnlyList<VectorMatch> Matches { get; init; } = [];

        public SemanticStorageLane Lane { get; init; } =
            MillerSemanticContract.ParseStorageSchema(MillerSemanticContract.DefaultEncoder.StorageSchema);

        public SemanticGenerationIdentity? Identity { get; init; }

        public Exception? SearchFailure { get; init; }

        public int OpenCount { get; private set; }

        public IVectorSearchPort? Factory(string workspaceRoot, out string? unavailableReason)
        {
            OpenCount++;
            if (UnavailableReason is not null)
            {
                unavailableReason = UnavailableReason;
                return null;
            }

            unavailableReason = null;
            return new Port(this);
        }

        private sealed class Port(RecordingPort owner) : IVectorSearchPort
        {
            public SemanticStorageLane Lane => owner.Lane;

            public SemanticGenerationIdentity? Identity => owner.Identity;

            public IReadOnlyList<VectorMatch> Search(VectorUnitKind kind, ReadOnlySpan<sbyte> query, int k)
            {
                if (owner.SearchFailure is { } failure)
                    throw failure;

                return [.. owner.Matches.Take(k)];
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class StubLookupIndex(params IndexedSymbol[] symbols) : ISymbolLookupIndex
    {
        public int DocumentCount => symbols.Length;

        public IReadOnlySet<string> KnownExtensions { get; } = new HashSet<string>(StringComparer.Ordinal) { ".cs" };

        public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or) => [];

        public IndexedSymbol Resolve(int docId) => symbols.Single(symbol => symbol.DocId == docId);

        public IReadOnlyList<IndexedSymbol> FindByName(string name) =>
            [.. symbols.Where(symbol => string.Equals(symbol.Name, name, StringComparison.Ordinal))];

        public IndexedSymbol? FindBySymbolId(string symbolId) =>
            symbols.FirstOrDefault(symbol => string.Equals(symbol.SymbolId, symbolId, StringComparison.Ordinal));

        public IReadOnlyList<IndexedSymbol> FindChildren(string parentId) => [];

        public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) => [];

        public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) => [];

        public bool IsIndexedFilePath(string path) => false;

        public string? ResolveIndexedFilePath(string target) => null;
    }
}
