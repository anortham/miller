using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SemanticSearchArmTests
{
    private const string Root = "/ws";

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

    [Fact]
    public async Task Off_StatesTheDisabledReasonAndAsksNeitherTheArtifactNorTheSidecar()
    {
        var port = new RecordingPort();
        var sessions = new RecordingSessionFactory();
        var arm = new SemanticSearchArm(Root, enabled: false, port.Factory, sessions.Open);

        SemanticQueryResult result = await arm.QuerySymbolsAsync("workspace refresh", 5, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Hits);
        Assert.False(result.Served);
        Assert.Contains(VectorSidecar.EnvVar, result.UnavailableReason!, StringComparison.Ordinal);
        Assert.Equal(0, port.OpenCount);
        Assert.Equal(0, sessions.OpenCount);
    }

    [Fact]
    public async Task NoArtifact_ReturnsTheGatesReasonAndNeverLaunchesTheSidecar()
    {
        var port = new RecordingPort { UnavailableReason = "no vector artifact exists" };
        var sessions = new RecordingSessionFactory();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, sessions.Open);

        SemanticQueryResult result = await arm.QuerySymbolsAsync("workspace refresh", 5, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Hits);
        Assert.Equal("no vector artifact exists", result.UnavailableReason);
        Assert.Equal(0, sessions.OpenCount);
    }

    [Theory]
    [InlineData("building", SemanticFallbackKind.VectorsBuilding)]
    [InlineData("incompatible", SemanticFallbackKind.VectorsIncompatible)]
    [InlineData("disk-blocked", SemanticFallbackKind.DiskBlocked)]
    [InlineData("circuit-open", SemanticFallbackKind.CircuitOpen)]
    public async Task NonReadyVectorClassifications_KeepTheirTypedFallback(
        string state,
        SemanticFallbackKind expected)
    {
        var arm = new SemanticSearchArm(
            Root,
            ClassifiedSidecar(state),
            static () => throw new InvalidOperationException("non-ready vectors must not ask for a session"));

        SemanticQueryResult result = await arm.QuerySymbolsAsync(
            "workspace refresh", 5, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Diagnostics!.Fallback);
    }

    [Fact]
    public async Task MissingSidecarBinary_IsAnEmptyResultWithAReasonAndTheStoreIsDisposed()
    {
        var port = new RecordingPort { Matches = [Match(1, 0.1, "a", "src/A.cs")] };
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, static () => null);

        SemanticQueryResult result = await arm.QuerySymbolsAsync("workspace refresh", 5, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Hits);
        Assert.False(string.IsNullOrWhiteSpace(result.UnavailableReason));
        Assert.Empty(port.RequestedK);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task ReadyGenerationForADifferentLiveArtifact_IsStaleBeforeTheSessionOpens()
    {
        string root = FreshWorkspace("live-artifact", 7);
        try
        {
            var port = new RecordingPort();
            port.Freshness.Enqueue(("old-artifact", 7));
            var sessions = new RecordingSessionFactory();
            var arm = new SemanticSearchArm(root, enabled: true, port.Factory, sessions.Open);

            SemanticQueryResult result = await arm.QuerySymbolsAsync(
                "workspace refresh", 5, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(SemanticFallbackKind.VectorsStale, result.Diagnostics!.Fallback);
            Assert.Equal(0, sessions.OpenCount);
            Assert.Empty(port.RequestedK);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RelevantCursorBehindTheLiveRevision_IsStaleBeforeTheSessionOpens()
    {
        string root = FreshWorkspace("artifact-1", 8);
        try
        {
            var port = new RecordingPort();
            port.Freshness.Enqueue(("artifact-1", 7));
            var sessions = new RecordingSessionFactory();
            var arm = new SemanticSearchArm(root, enabled: true, port.Factory, sessions.Open);

            SemanticQueryResult result = await arm.QuerySymbolsAsync(
                "workspace refresh", 5, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(SemanticFallbackKind.VectorsStale, result.Diagnostics!.Fallback);
            Assert.Equal(0, sessions.OpenCount);
            Assert.Empty(port.RequestedK);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FamilyStoreVectorIdentityMatchesThePinnedFamilyView()
    {
        var port = new RecordingPort { Matches = [Match(1, 0.1, "sym-1", "src/A.cs")] };
        port.Freshness.Enqueue(("family-a:view-a", 12));
        port.Freshness.Enqueue(("family-a:view-a", 12));
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(
            Root,
            enabled: true,
            port.Factory,
            () => session,
            liveFreshness: static () => ("family-a:view-a", 12));

        SemanticQueryResult result = await arm.QuerySymbolsAsync(
            "workspace refresh", 5, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Served, result.UnavailableReason);
        Assert.Single(result.Hits);
    }

    [Fact]
    public async Task GenerationChangeDuringEmbedding_IsStaleBeforeKnnServes()
    {
        string root = FreshWorkspace("artifact-1", 7);
        try
        {
            var port = new RecordingPort { Matches = [Match(1, 0.1, "sym-1", "src/A.cs")] };
            port.Freshness.Enqueue(("artifact-1", 7));
            port.Freshness.Enqueue(("promoted-artifact", 0));
            await using SemanticEmbeddingSession session = NewSession();
            var arm = new SemanticSearchArm(root, enabled: true, port.Factory, () => session);

            SemanticQueryResult result = await arm.QuerySymbolsAsync(
                "workspace refresh", 5, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(SemanticFallbackKind.VectorsStale, result.Diagnostics!.Fallback);
            Assert.Empty(port.RequestedK);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Symbols_MapUnitIdsAndPathsToRankedHitsWithCosineFromTheStoresDistance()
    {
        var port = new RecordingPort
        {
            Matches =
            [
                Match(7, 0.25, "sym-b", "src/B.cs"),
                Match(3, 0.10, "sym-a", "src/A.cs"),
            ],
        };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync("workspace refresh", 5, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Served, result.UnavailableReason);
        Assert.Collection(
            result.Hits,
            first =>
            {
                Assert.Equal("sym-a", first.SymbolId);
                Assert.Null(first.DocId);
                Assert.Equal("src/A.cs", first.FilePath);
                Assert.Equal(1, first.Rank);
                Assert.Equal(0.90, first.Cosine, 6);
            },
            second =>
            {
                Assert.Equal("sym-b", second.SymbolId);
                Assert.Equal(2, second.Rank);
                Assert.Equal(0.75, second.Cosine, 6);
            });
        Assert.Equal([VectorUnitKind.Symbol], port.RequestedKinds);
    }

    [Fact]
    public async Task Ties_ResolveByRowIdSoTwoRunsOfTheSameQueryAgreeExactly()
    {
        var port = new RecordingPort
        {
            Matches =
            [
                Match(9, 0.2, "sym-c", "src/C.cs"),
                Match(2, 0.2, "sym-a", "src/A.cs"),
                Match(5, 0.2, "sym-b", "src/B.cs"),
            ],
        };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        SemanticQueryResult first = await arm.QuerySymbolsAsync("q", 3, cancellationToken: TestContext.Current.CancellationToken);
        SemanticQueryResult second = await arm.QuerySymbolsAsync("q", 3, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["sym-a", "sym-b", "sym-c"], first.Hits.Select(static hit => hit.SymbolId));
        Assert.Equal(first.Hits, second.Hits);
    }

    [Fact]
    public async Task Chunks_RouteToTheChunkCorpusAndCarryTheChunkIdAsDocId()
    {
        var port = new RecordingPort { Matches = [Match(1, 0.4, "chunk-1", "docs/README.md")] };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        SemanticQueryResult result = await arm.QueryChunksAsync("how do i refresh", 3, cancellationToken: TestContext.Current.CancellationToken);

        SemanticHit hit = Assert.Single(result.Hits);
        Assert.Equal("chunk-1", hit.DocId);
        Assert.Null(hit.SymbolId);
        Assert.Equal("docs/README.md", hit.FilePath);
        Assert.Equal([VectorUnitKind.Chunk], port.RequestedKinds);
    }

    [Fact]
    public async Task RejectingPredicate_RefillsDeeperUntilEveryAllowedHitIsReturned()
    {
        var port = new RecordingPort { Matches = [.. Enumerable.Range(1, 40).Select(i => Match(i, i / 100d, $"sym-{i}", i <= 30 ? "tests/T.cs" : "src/S.cs"))] };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync(
            "q",
            4,
            match => match.Path.StartsWith("src/", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.Equal(["sym-31", "sym-32", "sym-33", "sym-34"], result.Hits.Select(static hit => hit.SymbolId));
        Assert.Equal([1, 2, 3, 4], result.Hits.Select(static hit => hit.Rank));
        Assert.Equal([4, 8, 16, 32, 64], port.RequestedK);
    }

    [Fact]
    public async Task RefillStops_WhenTheCorpusIsExhaustedRatherThanEscalatingToTheCeiling()
    {
        var port = new RecordingPort { Matches = [Match(1, 0.1, "sym-1", "tests/T.cs"), Match(2, 0.2, "sym-2", "src/S.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync(
            "q",
            4,
            match => match.Path.StartsWith("src/", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.Equal(["sym-2"], result.Hits.Select(static hit => hit.SymbolId));
        Assert.Equal([4], port.RequestedK);
    }

    [Fact]
    public async Task Refill_IsBoundedByTheCandidateCeilingSoAHostilePredicateCannotScanForever()
    {
        var port = new RecordingPort
        {
            Matches = [.. Enumerable.Range(1, SemanticSearchArm.MaxCandidates + 50).Select(i => Match(i, i / 1000d, $"sym-{i}", "tests/T.cs"))],
        };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync("q", 4, static _ => false, TestContext.Current.CancellationToken);

        Assert.Empty(result.Hits);
        Assert.True(result.Served, result.UnavailableReason);
        Assert.Equal(SemanticSearchArm.MaxCandidates, port.RequestedK[^1]);
        Assert.All(port.RequestedK, requested => Assert.True(requested <= SemanticSearchArm.MaxCandidates));
    }

    [Fact]
    public async Task EmbedFailure_LeavesTheResultEmptyWithAStatedReasonAndRunsNoKnn()
    {
        var port = new RecordingPort { Matches = [Match(1, 0.1, "sym-1", "src/A.cs")] };
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.ErrorEnvelope), FastOptions);
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync("q", 4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Hits);
        Assert.False(string.IsNullOrWhiteSpace(result.UnavailableReason));
        Assert.Empty(port.RequestedK);
    }

    [Fact]
    public async Task CircuitOpenSession_KeepsStatingItsReasonWithoutEverReachingTheStore()
    {
        var port = new RecordingPort { Matches = [Match(1, 0.1, "sym-1", "src/A.cs")] };
        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.GarbageOnStdout),
            FastOptions with { FatalThreshold = 1 });
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        await arm.QuerySymbolsAsync("q", 4, cancellationToken: TestContext.Current.CancellationToken);
        SemanticQueryResult result = await arm.QuerySymbolsAsync("q", 4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(SemanticSessionState.CircuitOpen, session.State);
        Assert.Empty(result.Hits);
        Assert.False(string.IsNullOrWhiteSpace(result.UnavailableReason));
        Assert.Empty(port.RequestedK);
    }

    [Fact]
    public async Task LaneDimsDisagreeingWithTheEmbedding_DegradesWithAReasonInsteadOfThrowing()
    {
        var port = new RecordingPort { Lane = MillerSemanticContract.ParseStorageSchema("vec0-int8-512-cosine-v1") };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync("q", 4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Hits);
        Assert.Contains("512", result.UnavailableReason!, StringComparison.Ordinal);
        Assert.Empty(port.RequestedK);
    }

    [Fact]
    public async Task NonCosineLane_DegradesRatherThanReportingAFabricatedCosine()
    {
        var port = new RecordingPort { Lane = MillerSemanticContract.ParseStorageSchema("vec0-int8-384-l2-v1") };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync("q", 4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Hits);
        Assert.Contains("l2", result.UnavailableReason!, StringComparison.Ordinal);
        Assert.Empty(port.RequestedK);
    }

    [Fact]
    public async Task TheQueryIsQuantizedWithTheSharedWriterQuantizerSoBothSidesShareOneLane()
    {
        var port = new RecordingPort { Matches = [Match(1, 0.1, "sym-1", "src/A.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        await arm.QuerySymbolsAsync("workspace refresh", 4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            SemanticVectorQuantizer.ToInt8(FakeSemanticSidecar.ExpectedVector("query", "workspace refresh", Pin.Dims)),
            port.LastQuery);
    }

    [Fact]
    public async Task EveryQuery_DisposesTheStoreItOpenedSoAPromoteIsNeverBlocked()
    {
        var port = new RecordingPort { Matches = [Match(1, 0.1, "sym-1", "src/A.cs")] };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        await arm.QuerySymbolsAsync("q", 2, cancellationToken: TestContext.Current.CancellationToken);
        await arm.QueryChunksAsync("q", 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, port.OpenCount);
        Assert.Equal(2, port.DisposeCount);
    }

    [Fact]
    public async Task AnUnexpectedStoreFailure_IsStatedRatherThanThrownAtTheCaller()
    {
        var port = new RecordingPort { SearchFailure = new VectorStoreException("vec0 table is gone") };
        await using SemanticEmbeddingSession session = NewSession();
        var arm = new SemanticSearchArm(Root, enabled: true, port.Factory, () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync("q", 4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Hits);
        Assert.Contains("vec0 table is gone", result.UnavailableReason!, StringComparison.Ordinal);
    }

    private static SemanticEmbeddingSession NewSession() =>
        new(FakeSemanticSidecar.InProcessLauncher(), FastOptions);

    private static VectorMatch Match(long rowId, double distance, string unitId, string path) =>
        new(rowId, distance, unitId, path);

    private static string FreshWorkspace(string artifactId, long revision)
    {
        string root = Directory.CreateTempSubdirectory("miller-semantic-fresh-").FullName;
        string millerDir = Path.Combine(root, ".miller");
        Directory.CreateDirectory(millerDir);

        using var connection = new SqliteConnection($"Data Source={Path.Combine(millerDir, "symbols.db")};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE extraction_revisions (revision_id INTEGER PRIMARY KEY);
            INSERT INTO artifact_metadata(key, value) VALUES ('artifact_id', $artifact);
            INSERT INTO extraction_revisions(revision_id) VALUES ($revision);
            """;
        command.Parameters.AddWithValue("$artifact", artifactId);
        command.Parameters.AddWithValue("$revision", revision);
        command.ExecuteNonQuery();
        return root;
    }

    private static VectorSidecar ClassifiedSidecar(string state)
    {
        SemanticGenerationIdentity identity = MillerSemanticContract.PinnedIdentity(Pin);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contract_version"] = MillerSemanticContract.ContractVersion,
            ["encoder_fingerprint"] = state == "incompatible"
                ? "sha256:" + new string('e', 64)
                : identity.EncoderFingerprint,
            ["storage_schema"] = identity.StorageSchema,
            ["corpus_generation"] = identity.CorpusGeneration,
            ["writer_version"] = identity.WriterVersion,
            ["min_reader_version"] = identity.MinReaderVersion,
            ["fusion_profile"] = identity.FusionProfile,
            ["artifact_id"] = "artifact-1",
            ["build_state"] = state == "building" ? "building" : "ready",
            ["symbol_completed_revision"] = "7",
            ["symbol_target_revision"] = "7",
            ["chunk_completed_revision"] = "7",
            ["chunk_target_revision"] = "7",
        };
        if (state is "disk-blocked" or "circuit-open")
            meta["converge_pause_state"] = state;

        return new VectorSidecar(
            SemanticMode.On,
            new SingleVectorProbe(),
            new SingleVectorOpener(meta),
            new SemanticReaderIdentity(identity.EncoderFingerprint, MillerSemanticContract.MinReaderVersion));
    }

    private sealed class RecordingSessionFactory
    {
        public int OpenCount { get; private set; }

        public SemanticEmbeddingSession? Open()
        {
            OpenCount++;
            return null;
        }
    }

    private sealed class SingleVectorProbe : IVectorFileProbe
    {
        public bool FileExists(string path) => true;

        public IReadOnlyList<string> EnumerateRetainedGenerations(string millerDir) => [];
    }

    private sealed class SingleVectorOpener(IReadOnlyDictionary<string, string> meta) : IVectorStoreOpener
    {
        public bool TryReadMeta(
            string path,
            out IReadOnlyDictionary<string, string> opened,
            out string failureReason)
        {
            opened = meta;
            failureReason = string.Empty;
            return true;
        }

        public VectorStore? OpenStore(string path, out string failureReason)
        {
            failureReason = "non-ready test never opens";
            return null;
        }
    }

    private sealed class RecordingPort
    {
        public string? UnavailableReason { get; init; }

        public IReadOnlyList<VectorMatch> Matches { get; init; } = [];

        public SemanticStorageLane Lane { get; init; } =
            MillerSemanticContract.ParseStorageSchema(MillerSemanticContract.DefaultEncoder.StorageSchema);

        public Exception? SearchFailure { get; init; }

        public int OpenCount { get; private set; }

        public int DisposeCount { get; private set; }

        public List<int> RequestedK { get; } = [];

        public List<VectorUnitKind> RequestedKinds { get; } = [];

        public Queue<(string? ArtifactId, long CompletedRevision)> Freshness { get; } = new();

        public sbyte[] LastQuery { get; private set; } = [];

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

            public (string? ArtifactId, long CompletedRevision)? ReadFreshness(VectorUnitKind kind) =>
                owner.Freshness.Count == 0 ? null : owner.Freshness.Dequeue();

            public IReadOnlyList<VectorMatch> Search(VectorUnitKind kind, ReadOnlySpan<sbyte> query, int k)
            {
                owner.RequestedK.Add(k);
                owner.RequestedKinds.Add(kind);
                owner.LastQuery = query.ToArray();

                if (owner.SearchFailure is { } failure)
                    throw failure;

                return [.. owner.Matches.Take(k)];
            }

            public void Dispose() => owner.DisposeCount++;
        }
    }
}

/// <summary>
/// The arm against the real pinned sqlite-vec extension and a real artifact: the fast suite proves the
/// filter/refill/fail-open logic, this proves the SQL and the cosine mapping the store actually returns.
/// </summary>
[Trait("Category", "Scale")]
[Collection(SqliteVecEnvironment.Name)]
public sealed class SemanticSearchArmScaleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miller-semantic-arm-" + Guid.NewGuid());
    private readonly string? _previousExtensionPath;

    public SemanticSearchArmScaleTests()
    {
        _previousExtensionPath = Environment.GetEnvironmentVariable(VectorStore.ExtensionPathEnvVar);
        Directory.CreateDirectory(Path.Combine(_root, ".miller"));
    }

    [Fact]
    public async Task RealArtifact_ReturnsTheNearestCardFirstWithACosineTheStoreAgreesWith()
    {
        string extension = SqliteVecTestSupport.RequireExtension();
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, extension);

        SemanticEncoderPin pin = MillerSemanticContract.DefaultEncoder;
        SemanticGenerationIdentity identity = MillerSemanticContract.PinnedIdentity(pin);
        string path = VectorSidecar.PathFor(_root);

        using (VectorStore store = VectorStore.Create(path, identity, "artifact-1", extension))
        {
            store.CommitBatch(
                VectorUnitKind.Symbol,
                [
                    Entry("near", "src/Near.cs", FakeSemanticSidecar.ExpectedVector("query", "workspace refresh", pin.Dims)),
                    Entry("far", "src/Far.cs", FakeSemanticSidecar.ExpectedVector("document", "something else entirely", pin.Dims)),
                ],
                [],
                new Dictionary<string, string>
                {
                    ["build_state"] = "ready",
                    ["symbol_completed_revision"] = "1",
                    ["symbol_target_revision"] = "1",
                },
                1);
        }
        ScaleTestSupport.WriteFreshnessArtifact(_root, "artifact-1", 1);

        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        var arm = new SemanticSearchArm(_root, new VectorSidecar(SemanticMode.On), () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync("workspace refresh", 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Served, result.UnavailableReason);
        Assert.Equal("near", result.Hits[0].SymbolId);
        Assert.Equal(1, result.Hits[0].Rank);
        Assert.True(result.Hits[0].Cosine > result.Hits[1].Cosine);
        Assert.Equal(1.0, result.Hits[0].Cosine, 1);
    }

    [Fact]
    public async Task NoArtifactOnDisk_SkipsRatherThanFailingAndStatesWhyTheArmCannotServe()
    {
        string extension = SqliteVecTestSupport.RequireExtension();
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, extension);

        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        var arm = new SemanticSearchArm(_root, new VectorSidecar(SemanticMode.On), () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync("workspace refresh", 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Hits);
        Assert.Contains("vector artifact", result.UnavailableReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnArtifactWhoseVectorTableIsGone_DegradesWithAReasonInsteadOfThrowingSqlite()
    {
        string extension = SqliteVecTestSupport.RequireExtension();
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, extension);

        SemanticEncoderPin pin = MillerSemanticContract.DefaultEncoder;
        string path = VectorSidecar.PathFor(_root);

        using (VectorStore store = VectorStore.Create(path, MillerSemanticContract.PinnedIdentity(pin), "artifact-1", extension))
        {
            store.CommitBatch(
                VectorUnitKind.Symbol,
                [Entry("near", "src/Near.cs", FakeSemanticSidecar.ExpectedVector("query", "workspace refresh", pin.Dims))],
                [],
                new Dictionary<string, string>
                {
                    ["build_state"] = "ready",
                    ["symbol_completed_revision"] = "1",
                    ["symbol_target_revision"] = "1",
                },
                1);
        }
        ScaleTestSupport.WriteFreshnessArtifact(_root, "artifact-1", 1);

        DropSymbolVectors(path, extension);

        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        var arm = new SemanticSearchArm(_root, new VectorSidecar(SemanticMode.On), () => session);

        SemanticQueryResult result = await arm.QuerySymbolsAsync("workspace refresh", 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Hits);
        Assert.False(result.Served);
        Assert.Contains("symbol_vectors", result.UnavailableReason!, StringComparison.Ordinal);
    }

    private static void DropSymbolVectors(string path, string extensionPath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());

        connection.Open();
        connection.EnableExtensions(true);
        connection.LoadExtension(extensionPath);
        connection.EnableExtensions(false);

        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DROP TABLE symbol_vectors";
        command.ExecuteNonQuery();
    }

    private static VectorBatchEntry Entry(string unitId, string path, float[] vector) =>
        new(unitId, path, "class", false, SemanticVectorQuantizer.ToInt8(vector), unitId + "-hash");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, _previousExtensionPath);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
