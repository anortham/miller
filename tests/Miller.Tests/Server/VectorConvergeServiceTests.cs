using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Tests.Indexing;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

public sealed class VectorConvergeSignalTests
{
    [Fact]
    public async Task Signal_ManyStampsBetweenDrains_CoalesceToOneWakeCarryingTheLatestTarget()
    {
        var signal = new VectorConvergeSignal(enabled: true);

        signal.StampTarget(4, fullRebuild: false);
        signal.StampTarget(9, fullRebuild: false);
        signal.StampTarget(7, fullRebuild: false);

        await signal.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(9, signal.TargetRevision);
        Assert.False(await WaitsAgain(signal));
    }

    [Fact]
    public async Task Signal_Disabled_NeverWakesAndNeverStampsATarget()
    {
        var signal = new VectorConvergeSignal(enabled: false);

        signal.StampTarget(9, fullRebuild: true);

        Assert.Equal(0, signal.TargetRevision);
        Assert.False(signal.TakeFullRebuild());
        Assert.False(await WaitsAgain(signal));
    }

    [Fact]
    public void Signal_FullRebuildFlag_IsConsumedExactlyOnce()
    {
        var signal = new VectorConvergeSignal(enabled: true);

        signal.StampTarget(3, fullRebuild: true);

        Assert.True(signal.TakeFullRebuild());
        Assert.False(signal.TakeFullRebuild());
    }

    private static async Task<bool> WaitsAgain(VectorConvergeSignal signal)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        try
        {
            await signal.WaitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// The real write path against a real <c>vectors.db</c>: the pinned sqlite-vec extension is loaded, the
/// artifact is created and converged from a real julie v1 artifact, and the commit's atomicity is asserted on
/// disk. Scale because it loads the native extension — it SKIPS, never fails, when the extension is absent.
/// </summary>
[Trait("Category", "Scale")]
public sealed class VectorConvergePortScaleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miller-vec-converge-" + Guid.NewGuid());
    private readonly string? _previousExtensionPath =
        Environment.GetEnvironmentVariable(VectorStore.ExtensionPathEnvVar);

    [Fact]
    public async Task Converge_AgainstARealArtifact_WritesCardVectorsAndAdvancesTheSymbolCursorTogether()
    {
        string extension = SqliteVecTestSupport.RequireExtension();
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, extension);

        WorkspaceContext workspace = SeedWorkspace();
        using IVectorConvergePort port = SqliteVectorConvergePort.TryOpen(workspace)!;
        Assert.NotNull(port);

        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        IReadOnlyList<VectorCursorOutcome> outcomes = await NewService()
            .DrainAsync(port, session, TestContext.Current.CancellationToken);

        VectorCursorOutcome symbols = outcomes.Single(o => o.Kind is VectorUnitKind.Symbol);
        Assert.Equal(2, symbols.Embedded);
        Assert.Equal(3, symbols.CompletedRevision);
        Assert.Equal("3", port.Meta(VectorConvergeService.SymbolCompletedKey));

        // Replay is idempotent: the stored embed_text_hash gates every unit out on the second pass.
        IReadOnlyList<VectorCursorOutcome> replay = await NewService()
            .DrainAsync(port, session, TestContext.Current.CancellationToken);
        Assert.Equal(0, replay.Single(o => o.Kind is VectorUnitKind.Symbol).Embedded);
        Assert.Equal(2, port.Stored(VectorUnitKind.Symbol, null).Count);
    }

    [Fact]
    public void TryOpen_WithoutThePinnedExtension_ReturnsNullRatherThanThrowing()
    {
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, null);

        Assert.Null(SqliteVectorConvergePort.TryOpen(SeedWorkspace()));
    }

    private static VectorConvergeService NewService() =>
        new(
            IsolatedBootstrap(),
            new VectorSidecar(SemanticMode.On),
            new VectorConvergeSignal(enabled: true),
            NullLogger.Instance,
            _ => null,
            _ => null,
            () => DateTimeOffset.UnixEpoch);


    // The bootstrap is never started here — it exists only to satisfy the constructor — but the registry
    // isolation convention requires every direct construction to point at a temp home.
    internal static IndexBootstrapService IsolatedBootstrap() =>
        new(NullLogger<IndexBootstrapService>.Instance)
        {
            TestHomeDirectoryOverride = Path.Combine(Path.GetTempPath(), "miller-vec-home-" + Guid.NewGuid()),
        };

    private WorkspaceContext SeedWorkspace()
    {
        string millerDir = Path.Combine(_root, ".miller");
        Directory.CreateDirectory(millerDir);
        string symbolsDbPath = Path.Combine(millerDir, "symbols.db");

        if (!File.Exists(symbolsDbPath))
        {
            using var fixture = JulieDbFixture.Create(
                JulieDbFixture.PinnedSchema,
                JulieDbFixture.PinnedContract,
                [
                    new JulieDbFixture.SymbolRow(
                        "sym-a", "Alpha", "class", "csharp", "src/Alpha.cs", "public class Alpha", 1, null),
                    new JulieDbFixture.SymbolRow(
                        "sym-b", "Run", "method", "csharp", "src/Alpha.cs", "public void Run()", 5, "sym-a")
                        { DocComment = "/// Runs alpha." },
                    new JulieDbFixture.SymbolRow(
                        "sym-c", "Count", "variable", "csharp", "src/Alpha.cs", null, 9, "sym-a"),
                ],
                revisions: [new JulieDbFixture.RevisionRow(3)]);
            fixture.SetArtifactMetadata("artifact_id", "artifact-scale");

            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                if (File.Exists(fixture.DbPath + suffix))
                    File.Copy(fixture.DbPath + suffix, symbolsDbPath + suffix, overwrite: true);
            }
        }

        return WorkspaceContext.Create(_root, AppContext.BaseDirectory, _root) with
        {
            CanonicalRoot = _root,
            CanonicalExtractDbPath = symbolsDbPath,
        };
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, _previousExtensionPath);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class VectorConvergeServiceTests
{
    private const string Artifact = "artifact-1";

    [Fact]
    public void Quantize_MapsUnitNormFloatsIntoThePinnedInt8Lane()
    {
        sbyte[] quantized = VectorConvergeService.QuantizeToInt8([1f, -1f, 0f, 0.5f, 2f, -2f]);

        Assert.Equal<sbyte[]>([127, -127, 0, 64, 127, -127], quantized);
    }

    [Fact]
    public void Scrub_ReplacesPathsAndBoundsTheStoredReason()
    {
        string scrubbed = VectorConvergeService.Scrub("failed reading /Users/someone/repo/.miller/content.db now");

        Assert.DoesNotContain("/Users/", scrubbed, StringComparison.Ordinal);
        Assert.Contains("<path>", scrubbed, StringComparison.Ordinal);
        Assert.True(VectorConvergeService.Scrub(new string('x', 5000)).Length <= 300);
    }

    [Fact]
    public async Task Drain_EmbedsOnlyHashChangedUnitsAndAdvancesTheCursorInsideTheCommit()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a v2"), Card("b", "src/A.cs", "card b")];
        port.SymbolStored = [State("a", "src/A.cs", "card a"), State("b", "src/A.cs", "card b")];

        IReadOnlyList<VectorCursorOutcome> outcomes = await DrainAsync(port);

        VectorCursorOutcome symbols = outcomes.Single(o => o.Kind is VectorUnitKind.Symbol);
        Assert.Equal(1, symbols.Embedded);
        Assert.Equal(5, symbols.CompletedRevision);

        CommitRecord commit = port.Commits.Single(c => c.Kind is VectorUnitKind.Symbol);
        Assert.Equal(["a"], commit.Vectors.Select(v => v.Unit.UnitId));
        Assert.Equal(5, commit.AdvanceTo);
        Assert.Equal(VectorConvergeService.SymbolCompletedKey, commit.CompletedRevisionKey);
        Assert.Equal(512, commit.Vectors[0].Embedding.Length);
    }

    [Fact]
    public async Task Drain_NeverWritesACompletedRevisionOutsideTheCommitTransaction()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];

        await DrainAsync(port);

        Assert.DoesNotContain(VectorConvergeService.SymbolCompletedKey, port.MetaWrites);
        Assert.DoesNotContain(VectorConvergeService.ChunkCompletedKey, port.MetaWrites);
    }

    [Fact]
    public async Task Drain_CrashBetweenStagedBatchAndCursorAdvance_LeavesARerunnableState()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a"), Card("b", "src/A.cs", "card b")];
        port.CommitFault = kind => kind is VectorUnitKind.Symbol
            ? new IOException("the process died mid-commit")
            : null;

        IReadOnlyList<VectorCursorOutcome> crashed = await DrainAsync(port);

        // The cursor is exactly where it was: it can never claim a revision the artifact only partially contains.
        Assert.Equal(0, crashed.Single(o => o.Kind is VectorUnitKind.Symbol).CompletedRevision);
        Assert.Equal("0", port.Meta(VectorConvergeService.SymbolCompletedKey));
        Assert.NotNull(port.Meta(VectorConvergeService.SymbolErrorKey));

        port.CommitFault = null;
        IReadOnlyList<VectorCursorOutcome> replayed = await DrainAsync(port);

        VectorCursorOutcome symbols = replayed.Single(o => o.Kind is VectorUnitKind.Symbol);
        Assert.Equal(2, symbols.Embedded);
        Assert.Equal(5, symbols.CompletedRevision);
        Assert.Equal(string.Empty, port.Meta(VectorConvergeService.SymbolErrorKey));
    }

    [Fact]
    public async Task Drain_ReplayAfterASuccessfulCommit_IsIdempotentAndReEmbedsNothing()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];

        await DrainAsync(port);
        port.SymbolStored = [State("a", "src/A.cs", "card a")];
        port.Commits.Clear();

        IReadOnlyList<VectorCursorOutcome> replay = await DrainAsync(port);

        Assert.Equal(0, replay.Single(o => o.Kind is VectorUnitKind.Symbol).Embedded);
        Assert.All(port.Commits, commit => Assert.Empty(commit.Vectors));
    }

    [Fact]
    public async Task Drain_BlockedChunkCursor_NeverStallsTheSymbolCursor()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        port.ChunkUnits = [Card("c1", "docs/a.md", "chunk one")];
        port.ChunkFactsValue = port.ChunkFactsValue with { ContentWorkspaceRevision = 1 };

        IReadOnlyList<VectorCursorOutcome> outcomes = await DrainAsync(port);

        Assert.Equal(5, outcomes.Single(o => o.Kind is VectorUnitKind.Symbol).CompletedRevision);
        Assert.Equal(0, outcomes.Single(o => o.Kind is VectorUnitKind.Chunk).CompletedRevision);
        Assert.DoesNotContain(port.Commits, c => c.Kind is VectorUnitKind.Chunk);
    }

    [Fact]
    public async Task Drain_EachCursorKeepsItsOwnLastError()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        port.ChunkFactsValue = port.ChunkFactsValue with { ContentChunkerVersion = "line-v9" };

        await DrainAsync(port);

        Assert.Equal(string.Empty, port.Meta(VectorConvergeService.SymbolErrorKey));
        Assert.Contains("chunker", port.Meta(VectorConvergeService.ChunkErrorKey)!, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrEmpty(port.Meta(VectorConvergeService.ChunkErrorAtKey)));
    }

    [Fact]
    public async Task Drain_ChunkArtifactIdentityChanged_ResetsTheChunkCursorBeforeAnyRevisionComparison()
    {
        var port = new FakePort();
        port.Metadata[VectorConvergeService.ChunkSourceArtifactKey] = "artifact-0";
        port.Metadata[VectorConvergeService.ChunkCompletedKey] = "99";
        port.ChunkFactsValue = port.ChunkFactsValue with
        {
            ChunkSourceArtifactId = "artifact-0",
            ContentWorkspaceRevision = 9999,
        };

        await DrainAsync(port);

        Assert.Equal("0", port.Meta(VectorConvergeService.ChunkCompletedKey));
        Assert.Equal("0", port.Meta(VectorConvergeService.ChunkTargetKey));
        Assert.Equal(Artifact, port.Meta(VectorConvergeService.ChunkSourceArtifactKey));
        Assert.DoesNotContain(port.Commits, c => c.Kind is VectorUnitKind.Chunk);
    }

    [Fact]
    public async Task Drain_GenerationChangedWhileEmbedding_DiscardsTheBatchWithoutCommitting()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        port.Valid = false;

        IReadOnlyList<VectorCursorOutcome> outcomes = await DrainAsync(port);

        Assert.DoesNotContain(port.Commits, c => c.Kind is VectorUnitKind.Symbol);
        Assert.Equal(0, outcomes.Single(o => o.Kind is VectorUnitKind.Symbol).CompletedRevision);
        Assert.Contains("in flight", port.Meta(VectorConvergeService.SymbolErrorKey)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drain_EscalationTrigger_IsSurfacedAsADecisionAndEmbedsNothing()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        port.SymbolSnapshot = port.SymbolSnapshot with { DeltaHistoryComplete = false };

        IReadOnlyList<VectorCursorOutcome> outcomes = await DrainAsync(port);

        VectorCursorOutcome symbols = outcomes.Single(o => o.Kind is VectorUnitKind.Symbol);
        Assert.Equal(VectorConvergeDecision.ShadowRebuild, symbols.Decision);
        Assert.Equal(VectorEscalationTrigger.DeltaHistoryMissing, symbols.Trigger);
        Assert.DoesNotContain(port.Commits, c => c.Kind is VectorUnitKind.Symbol);
    }

    [Fact]
    public async Task Drain_SidecarUnavailable_HoldsTheCursorAndRecordsTheReason()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];

        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.ModelNotPrepared));
        IReadOnlyList<VectorCursorOutcome> outcomes = await NewService()
            .DrainAsync(port, session, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(port.Commits, c => c.Kind is VectorUnitKind.Symbol);
        Assert.Equal(0, outcomes.Single(o => o.Kind is VectorUnitKind.Symbol).CompletedRevision);
        Assert.False(string.IsNullOrEmpty(port.Meta(VectorConvergeService.SymbolErrorKey)));
    }

    [Fact]
    public async Task OffMode_TheServiceNeverOpensAPortOrLaunchesASession()
    {
        var opened = new List<string>();
        var service = new VectorConvergeService(
            IsolatedBootstrap(),
            VectorSidecar.Disabled,
            new VectorConvergeSignal(enabled: true),
            NullLogger.Instance,
            _ => { opened.Add("port"); return null; },
            _ => { opened.Add("session"); return null; },
            null);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(opened);
    }

    private static VectorConvergeService NewService() =>
        new(
            IsolatedBootstrap(),
            new VectorSidecar(SemanticMode.On),
            new VectorConvergeSignal(enabled: true),
            NullLogger.Instance,
            _ => null,
            _ => null,
            () => DateTimeOffset.UnixEpoch);

    private static IndexBootstrapService IsolatedBootstrap() =>
        VectorConvergePortScaleTests.IsolatedBootstrap();

    private static async Task<IReadOnlyList<VectorCursorOutcome>> DrainAsync(FakePort port)
    {
        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        return await NewService().DrainAsync(port, session, TestContext.Current.CancellationToken);
    }

    private static VectorCorpusUnit Card(string id, string path, string text) =>
        new(id, path, text, "method", IsTest: false);

    private static VectorUnitState State(string id, string path, string text) =>
        new(id, path, SymbolCardBuilder.EmbedTextHash(text));

    private sealed record CommitRecord(
        VectorUnitKind Kind,
        IReadOnlyList<VectorCommit> Vectors,
        IReadOnlyList<string> Delete,
        string CompletedRevisionKey,
        long AdvanceTo);

    private sealed class FakePort : IVectorConvergePort
    {
        public Dictionary<string, string> Metadata { get; } = new(StringComparer.Ordinal)
        {
            ["artifact_id"] = Artifact,
            [VectorConvergeService.SymbolCompletedKey] = "0",
            [VectorConvergeService.ChunkCompletedKey] = "0",
            [VectorConvergeService.ChunkSourceArtifactKey] = Artifact,
        };

        public List<string> MetaWrites { get; } = [];

        public List<CommitRecord> Commits { get; } = [];

        public SemanticGenerationIdentity StoredIdentity { get; } =
            MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder);

        public VectorConvergeSnapshot SymbolSnapshot { get; set; } =
            new(Artifact, 5, DeltaHistoryComplete: true, ["src/A.cs", "docs/a.md"]);

        public IReadOnlyList<VectorCorpusUnit> SymbolUnits { get; set; } = [];

        public IReadOnlyList<VectorCorpusUnit> ChunkUnits { get; set; } = [];

        public IReadOnlyList<VectorUnitState> SymbolStored { get; set; } = [];

        public IReadOnlyList<VectorUnitState> ChunkStored { get; set; } = [];

        public ChunkCursorFacts ChunkFactsValue { get; set; } = new()
        {
            SymbolsArtifactId = Artifact,
            VectorsArtifactId = Artifact,
            ChunkSourceArtifactId = Artifact,
            ContentSchemaVersion = ContentCorpusSchema.SchemaVersion,
            RecordedChunkSchemaVersion = ContentCorpusSchema.SchemaVersion,
            ContentChunkerVersion = ContentCorpusSchema.ChunkerVersion,
            CorpusGeneration = MillerSemanticContract.CorpusGeneration,
            ContentWorkspaceRevision = 5,
            TargetRevision = 5,
            Sources = [],
        };

        public bool Valid { get; set; } = true;

        public Func<VectorUnitKind, Exception?>? CommitFault { get; set; }

        public string? Meta(string key) => Metadata.GetValueOrDefault(key);

        public void SetMeta(string key, string value)
        {
            MetaWrites.Add(key);
            Metadata[key] = value;
        }

        public VectorConvergeSnapshot Snapshot(long completedRevision) => SymbolSnapshot;

        public IReadOnlyList<VectorCorpusUnit> Units(VectorUnitKind kind, IReadOnlyCollection<string>? paths) =>
            kind is VectorUnitKind.Symbol ? SymbolUnits : ChunkUnits;

        public IReadOnlyList<VectorUnitState> Stored(VectorUnitKind kind, IReadOnlyCollection<string>? paths) =>
            kind is VectorUnitKind.Symbol ? SymbolStored : ChunkStored;

        public int TotalStored(VectorUnitKind kind) =>
            kind is VectorUnitKind.Symbol ? SymbolStored.Count : ChunkStored.Count;

        public ChunkCursorFacts ChunkFacts(long targetRevision) => ChunkFactsValue;

        public bool StillValid(SemanticGenerationIdentity identity, string artifactId) => Valid;

        public void Commit(
            VectorUnitKind kind,
            IReadOnlyList<VectorCommit> vectors,
            IReadOnlyList<string> delete,
            string completedRevisionKey,
            long advanceTo,
            long revision)
        {
            if (CommitFault?.Invoke(kind) is { } fault)
                throw fault;

            Commits.Add(new CommitRecord(kind, vectors, delete, completedRevisionKey, advanceTo));
            if (advanceTo > 0)
                Metadata[completedRevisionKey] = advanceTo.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
        }
    }
}
