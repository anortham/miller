using Microsoft.Extensions.Logging;
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
[Collection(SqliteVecEnvironment.Name)]
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

        // The csproj now copies a restored vec0 into this test assembly's own .tools/, so "no extension
        // resolvable" must be manufactured by moving the packaged file aside for the duration. Safe because
        // this class serializes on the SqliteVecEnvironment collection with every other vec0-touching class.
        string packaged = Path.Combine(AppContext.BaseDirectory, ".tools", VectorStore.PackagedExtensionFileName);
        string? parked = File.Exists(packaged) ? packaged + ".parked" : null;
        if (parked is not null)
            File.Move(packaged, parked);

        try
        {
            Assert.Null(SqliteVectorConvergePort.TryOpen(SeedWorkspace()));
        }
        finally
        {
            if (parked is not null)
                File.Move(parked, packaged);
        }
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
        Assert.Equal(384, commit.Vectors[0].Embedding.Length);
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
    public async Task Drain_ShadowRebuildDecision_BuildsTheShadowGenerationPromotesItAndStopsTheDrain()
    {
        var live = new FakePort();
        live.SymbolSnapshot = live.SymbolSnapshot with { DeltaHistoryComplete = false };
        var shadow = new FakePort();
        shadow.SymbolUnits = [Card("a", "src/A.cs", "card a"), Card("b", "src/B.cs", "card b")];
        var rebuilder = new FakeShadowRebuilder(shadow);

        IReadOnlyList<VectorCursorOutcome> outcomes = await DrainAsync(live, rebuilder);

        VectorCursorOutcome only = Assert.Single(outcomes);
        Assert.Equal(VectorConvergeDecision.ShadowRebuild, only.Decision);
        Assert.Equal(2, only.Embedded);

        CommitRecord built = Assert.Single(shadow.Commits);
        Assert.Equal(VectorUnitKind.Symbol, built.Kind);
        Assert.Equal(["a", "b"], built.Vectors.Select(v => v.Unit.UnitId));
        Assert.Equal(5, built.AdvanceTo);

        // The promoted generation is queryable: its symbol cursor reached the target it was given.
        Assert.Equal(
            "ready",
            VectorGenerationManager.EvaluateBuildState(new VectorBuildProgress(
                long.Parse(shadow.Meta(VectorConvergeService.SymbolCompletedKey)!),
                long.Parse(shadow.Meta(VectorConvergeService.SymbolTargetKey)!),
                "building")).BuildState);

        Assert.True(rebuilder.Promoted);
        Assert.True(shadow.Disposed);
        Assert.True(live.Disposed);
    }

    [Fact]
    public async Task Drain_ShadowRebuildLeavesTheChunkCursorToTheGatedPathOnThePromotedArtifact()
    {
        var live = new FakePort();
        live.SymbolSnapshot = live.SymbolSnapshot with { DeltaHistoryComplete = false };
        var shadow = new FakePort();
        shadow.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        shadow.ChunkUnits = [Card("c1", "docs/a.md", "chunk one")];

        await DrainAsync(live, new FakeShadowRebuilder(shadow));

        Assert.DoesNotContain(shadow.Commits, commit => commit.Kind is VectorUnitKind.Chunk);
        Assert.Equal("0", shadow.Meta(VectorConvergeService.ChunkCompletedKey));
    }

    [Fact]
    public async Task Drain_ShadowRebuildFails_HoldsTheCursorRecordsTheErrorAndNeverRetriesOnTheSameWake()
    {
        var live = new FakePort();
        live.SymbolSnapshot = live.SymbolSnapshot with { DeltaHistoryComplete = false };
        var rebuilder = new FakeShadowRebuilder(null) { OpenFault = new IOException("no disk space for a shadow") };

        IReadOnlyList<VectorCursorOutcome> outcomes = await DrainAsync(live, rebuilder);

        Assert.Equal(1, rebuilder.OpenShadowCalls);
        Assert.False(rebuilder.Promoted);
        Assert.False(live.Disposed);
        Assert.Equal("0", live.Meta(VectorConvergeService.SymbolCompletedKey));
        Assert.False(string.IsNullOrEmpty(live.Meta(VectorConvergeService.SymbolErrorKey)));

        VectorCursorOutcome chunks = outcomes.Single(o => o.Kind is VectorUnitKind.Chunk);
        Assert.Equal(VectorConvergeDecision.Incremental, chunks.Decision);
        Assert.Contains("shadow rebuild", chunks.LastError!, StringComparison.Ordinal);
        Assert.DoesNotContain(live.Commits, c => c.Kind is VectorUnitKind.Chunk);
    }

    [Fact]
    public async Task Drain_ShadowBuildCannotEmbed_LeavesTheLiveGenerationUnpromoted()
    {
        var live = new FakePort();
        live.SymbolSnapshot = live.SymbolSnapshot with { DeltaHistoryComplete = false };
        var shadow = new FakePort();
        shadow.SymbolUnits = [Card("a", "src/A.cs", "card a")];

        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.ModelNotPrepared));
        var rebuilder = new FakeShadowRebuilder(shadow);

        await NewService().DrainAsync(live, session, rebuilder, TestContext.Current.CancellationToken);

        Assert.False(rebuilder.Promoted);
        Assert.False(live.Disposed);
        Assert.True(shadow.Disposed);
        Assert.False(string.IsNullOrEmpty(live.Meta(VectorConvergeService.SymbolErrorKey)));
    }

    [Fact]
    public async Task Drain_InitialBuildBeyondOneTransaction_ShadowRebuildEmbedsTheWholeCorpusInBoundedCommits()
    {
        var live = new FakePort();
        live.SymbolUnits = ManyCards(VectorConvergePlanner.MaxUnitsPerTransaction + 1);
        var shadow = new FakePort();
        shadow.SymbolUnits = live.SymbolUnits;
        var rebuilder = new FakeShadowRebuilder(shadow);

        IReadOnlyList<VectorCursorOutcome> outcomes = await DrainAsync(live, rebuilder);

        VectorCursorOutcome only = Assert.Single(outcomes);
        Assert.Equal(VectorConvergeDecision.ShadowRebuild, only.Decision);
        Assert.Equal(VectorEscalationTrigger.BatchTooLarge, only.Trigger);
        Assert.Equal(live.SymbolUnits.Count, only.Embedded);
        Assert.True(rebuilder.Promoted);

        List<CommitRecord> commits = [.. shadow.Commits.Where(c => c.Kind is VectorUnitKind.Symbol)];
        Assert.Equal(live.SymbolUnits.Count, commits.Sum(c => c.Vectors.Count));
        Assert.True(commits.Count >= 2);
        Assert.All(commits, c => Assert.True(c.Vectors.Count <= VectorConvergePlanner.MaxUnitsPerTransaction));
        Assert.Equal(
            shadow.Meta(VectorConvergeService.SymbolTargetKey),
            shadow.Meta(VectorConvergeService.SymbolCompletedKey));
    }

    [Fact]
    public async Task Drain_ShadowRebuild_ToleratesFlaggedUnitsAndPromotesTheRest()
    {
        var live = new FakePort();
        live.SymbolSnapshot = live.SymbolSnapshot with { DeltaHistoryComplete = false };
        var shadow = new FakePort();
        shadow.SymbolUnits =
            [Card("a", "src/A.cs", "card a"), Card("b", "src/B.cs", "card b"), Card("c", "src/C.cs", "card c")];
        var rebuilder = new FakeShadowRebuilder(shadow);

        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.PoisonItem, poisonIndices: [1]));
        IReadOnlyList<VectorCursorOutcome> outcomes = await NewService()
            .DrainAsync(live, session, rebuilder, TestContext.Current.CancellationToken);

        Assert.True(rebuilder.Promoted);
        Assert.Equal(2, Assert.Single(outcomes).Embedded);
        Assert.Equal(
            ["a", "c"],
            shadow.Commits
                .Where(c => c.Kind is VectorUnitKind.Symbol)
                .SelectMany(c => c.Vectors)
                .Select(v => v.Unit.UnitId));
    }

    [Fact]
    public async Task Drain_ShadowRebuild_RefusesToPromoteWhenEveryUnitFlags()
    {
        var live = new FakePort();
        live.SymbolSnapshot = live.SymbolSnapshot with { DeltaHistoryComplete = false };
        var shadow = new FakePort();
        shadow.SymbolUnits = [Card("a", "src/A.cs", "card a"), Card("b", "src/B.cs", "card b")];
        var rebuilder = new FakeShadowRebuilder(shadow);

        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.PoisonItem, poisonIndices: [0, 1]));
        await NewService().DrainAsync(live, session, rebuilder, TestContext.Current.CancellationToken);

        Assert.False(rebuilder.Promoted);
        Assert.False(live.Disposed);
        Assert.Contains("refusing", live.Meta(VectorConvergeService.SymbolErrorKey)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drain_ChunkSpanBeyondOneTransaction_ConvergesInBoundedBatchesWithinOneWake()
    {
        var port = new FakePort();
        port.ChunkUnits = ManyChunks(VectorConvergePlanner.MaxUnitsPerTransaction + 1);

        IReadOnlyList<VectorCursorOutcome> outcomes = await DrainAsync(port);

        VectorCursorOutcome chunks = outcomes.Single(o => o.Kind is VectorUnitKind.Chunk);
        Assert.Equal(VectorConvergeDecision.Incremental, chunks.Decision);
        Assert.Null(chunks.LastError);

        List<CommitRecord> commits = [.. port.Commits.Where(c => c.Kind is VectorUnitKind.Chunk)];
        Assert.Equal(port.ChunkUnits.Count, commits.Sum(c => c.Vectors.Count));
        Assert.All(commits, c => Assert.True(c.Vectors.Count <= VectorConvergePlanner.MaxUnitsPerTransaction));
        Assert.Equal("5", port.Meta(VectorConvergeService.ChunkCompletedKey));
    }

    [Fact]
    public async Task Drain_AfterAPromote_ContinuesIntoTheChunkCursorOnTheReopenedArtifactSameWake()
    {
        var live = new FakePort();
        live.SymbolSnapshot = live.SymbolSnapshot with { DeltaHistoryComplete = false };
        var shadow = new FakePort();
        shadow.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        var promoted = new FakePort();
        promoted.ChunkUnits = [Card("c1", "docs/a.md", "chunk one")];
        var rebuilder = new FakeShadowRebuilder(shadow);

        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        IReadOnlyList<VectorCursorOutcome> outcomes = await NewService().DrainAsync(
            live, session, rebuilder, () => promoted, TestContext.Current.CancellationToken);

        Assert.True(rebuilder.Promoted);
        VectorCursorOutcome chunks = outcomes.Single(o => o.Kind is VectorUnitKind.Chunk);
        Assert.Equal(1, chunks.Embedded);
        Assert.Equal("5", promoted.Meta(VectorConvergeService.ChunkCompletedKey));
        Assert.True(promoted.Disposed);
    }

    [Fact]
    public void OpenPort_CorruptArtifact_RecoversTheGenerationAndLeavesSymbolsDbUntouched()
    {
        string root = Directory.CreateTempSubdirectory("miller-vec-corrupt-").FullName;
        try
        {
            string symbols = SeedCorruptVectorArtifact(root);
            int opens = 0;
            VectorConvergeService service = ServiceOverPorts(_ => opens++ == 0
                ? throw new InvalidOperationException("vector artifact has malformed meta")
                : null);

            Assert.Null(service.OpenPortWithRecovery(WorkspaceAt(root, symbols)));
            Assert.False(File.Exists(VectorSidecar.PathFor(root)));
            Assert.Equal("source of truth", File.ReadAllText(symbols));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OpenPort_AfterRecovery_ReturnsTheRebuiltGenerationSoThisDrainContinuesIntoIt()
    {
        string root = Directory.CreateTempSubdirectory("miller-vec-corrupt-").FullName;
        try
        {
            string symbols = SeedCorruptVectorArtifact(root);
            var rebuilt = new FakePort();
            int opens = 0;
            VectorConvergeService service = ServiceOverPorts(_ => opens++ == 0
                ? throw new InvalidOperationException("vector artifact has malformed meta")
                : rebuilt);

            Assert.Same(rebuilt, service.OpenPortWithRecovery(WorkspaceAt(root, symbols)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OpenPort_RebuiltGenerationAlsoCorrupt_PropagatesRatherThanRecoveringAgain()
    {
        string root = Directory.CreateTempSubdirectory("miller-vec-corrupt-").FullName;
        try
        {
            string symbols = SeedCorruptVectorArtifact(root);
            VectorConvergeService service = ServiceOverPorts(
                _ => throw new InvalidOperationException("vector artifact has malformed meta"));

            Assert.Throws<InvalidOperationException>(
                () => service.OpenPortWithRecovery(WorkspaceAt(root, symbols)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Drain_CorruptArtifactOnOneWake_ConvergesIntoTheRebuiltGenerationWithNoLaterStamp()
    {
        string root = Directory.CreateTempSubdirectory("miller-vec-corrupt-").FullName;
        try
        {
            string symbols = SeedCorruptVectorArtifact(root);
            var rebuilt = new FakePort();
            rebuilt.SymbolUnits = [Card("a", "src/A.cs", "card a")];
            int opens = 0;

            await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);
            VectorConvergeService service = ServiceOverWorkspace(
                root,
                symbols,
                _ => opens++ == 0
                    ? throw new InvalidOperationException("vector artifact has malformed meta")
                    : rebuilt,
                _ => session);

            await service.DrainOnceAsync(TestContext.Current.CancellationToken);

            CommitRecord commit = rebuilt.Commits.Single(c => c.Kind is VectorUnitKind.Symbol);
            Assert.Equal(["a"], commit.Vectors.Select(v => v.Unit.UnitId));
            Assert.Equal(5, rebuilt.Commits.Single(c => c.Kind is VectorUnitKind.Symbol).AdvanceTo);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Drain_AcrossTwoWakes_ReusesOneResidentEmbeddingSession()
    {
        string root = Directory.CreateTempSubdirectory("miller-vec-session-").FullName;
        try
        {
            string symbols = SeedSymbolsDb(root);
            var port = new FakePort();
            port.SymbolUnits = [Card("a", "src/A.cs", "card a")];

            int created = 0;
            await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);
            VectorConvergeService service = ServiceOverWorkspace(
                root, symbols, _ => port, _ => { created++; return session; });

            await service.DrainOnceAsync(TestContext.Current.CancellationToken);
            port.SymbolStored = [State("a", "src/A.cs", "card a")];
            await service.DrainOnceAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, created);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Drain_AcrossTwoWakes_KeepsSidecarFailureStateSoRepeatedFailuresTripTheCircuit()
    {
        string root = Directory.CreateTempSubdirectory("miller-vec-circuit-").FullName;
        try
        {
            string symbols = SeedSymbolsDb(root);
            var port = new FakePort();
            port.SymbolUnits = [Card("a", "src/A.cs", "card a")];

            await using var session = new SemanticEmbeddingSession(
                FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.CrashMidBatch), FastOptions);
            VectorConvergeService service = ServiceOverWorkspace(root, symbols, _ => port, _ => session);

            await service.DrainOnceAsync(TestContext.Current.CancellationToken);
            int restartsAfterFirstWake = session.RestartCount;
            await service.DrainOnceAsync(TestContext.Current.CancellationToken);

            Assert.True(restartsAfterFirstWake > 0);
            Assert.True(session.RestartCount > restartsAfterFirstWake);
            Assert.Equal(SemanticSessionState.CircuitOpen, session.State);
            Assert.False(string.IsNullOrEmpty(port.Meta(VectorConvergeService.SymbolErrorKey)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_DisposesTheResidentSessionExactlyOnceAndStartsNoOther()
    {
        string root = Directory.CreateTempSubdirectory("miller-vec-stop-").FullName;
        try
        {
            string symbols = SeedSymbolsDb(root);
            var port = new FakePort();
            port.SymbolUnits = [Card("a", "src/A.cs", "card a")];

            int created = 0;
            SemanticEmbeddingSession? session = null;
            var signal = new VectorConvergeSignal(enabled: true);
            VectorConvergeService service = ServiceOverWorkspace(
                root,
                symbols,
                _ => port,
                _ =>
                {
                    created++;
                    session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);
                    return session;
                },
                signal);

            await service.StartAsync(CancellationToken.None);
            signal.StampTarget(5, fullRebuild: false);
            await WaitUntil(() => port.Commits.Count > 0);

            // The drain is done but the service is not: the resident child outlives the wake that started it.
            Assert.NotEqual(SemanticSessionState.Stopped, session!.State);

            await service.StopAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(1, created);
            Assert.Equal(SemanticSessionState.Stopped, session!.State);
            Assert.Equal("session disposed", session.UnavailableReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Drain_OnALeaderWake_RunsGcWithActiveReadinessFromThePortAndTheLiveReaderTags()
    {
        string root = Directory.CreateTempSubdirectory("miller-vec-gc-").FullName;
        try
        {
            string symbols = SeedSymbolsDb(root);
            var port = new FakePort();
            port.SymbolUnits = [Card("a", "src/A.cs", "card a")];
            port.Metadata["build_state"] = "ready";

            var gc = new RecordingGc();
            var registry = new VectorLiveReaderRegistry();
            using IDisposable held = registry.Register("aaaaaaaaaaaaaaaa");

            await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);
            VectorConvergeService service = ServiceOverWorkspace(
                root, symbols, _ => port, _ => session, openGc: _ => gc, registry: registry);

            await service.DrainOnceAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, gc.Collects);
            Assert.True(gc.LastActiveIsReady);
            Assert.Contains("aaaaaaaaaaaaaaaa", gc.LastTags);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Drain_WhenTheActiveArtifactIsNotReady_RunsGcWithActiveReadinessFalse()
    {
        string root = Directory.CreateTempSubdirectory("miller-vec-gc-").FullName;
        try
        {
            string symbols = SeedSymbolsDb(root);
            var port = new FakePort();
            port.SymbolUnits = [Card("a", "src/A.cs", "card a")];
            port.Metadata["build_state"] = "building";

            var gc = new RecordingGc();
            await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);
            VectorConvergeService service = ServiceOverWorkspace(root, symbols, _ => port, _ => session, openGc: _ => gc);

            await service.DrainOnceAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, gc.Collects);
            Assert.False(gc.LastActiveIsReady);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Drain_AfterWorkspaceRebind_RecreatesTheRootBoundGenerationGc()
    {
        string rootA = Directory.CreateTempSubdirectory("miller-vec-gc-a-").FullName;
        string rootB = Directory.CreateTempSubdirectory("miller-vec-gc-b-").FullName;
        try
        {
            string symbolsA = SeedSymbolsDb(rootA);
            string symbolsB = SeedSymbolsDb(rootB);
            var portA = new FakePort();
            var portB = new FakePort();
            var openedGcRoots = new List<string>();
            IndexBootstrapService bootstrap = IsolatedBootstrap();
            bootstrap.SeedForTest(
                WorkspaceAt(rootA, symbolsA), new IndexHolder(MillerRepositoryIndex.Build([]), 1));
            await using SemanticEmbeddingSession session = new(
                FakeSemanticSidecar.InProcessLauncher(), FastOptions);
            var service = new VectorConvergeService(
                bootstrap,
                new VectorSidecar(SemanticMode.On),
                new VectorConvergeSignal(enabled: true),
                NullLogger.Instance,
                workspace => workspace.WorkspaceRoot == rootA ? portA : portB,
                _ => session,
                () => DateTimeOffset.UnixEpoch,
                openGc: workspace =>
                {
                    openedGcRoots.Add(workspace.WorkspaceRoot);
                    return new RecordingGc();
                });

            await service.DrainOnceAsync(TestContext.Current.CancellationToken);
            bootstrap.SeedForTest(
                WorkspaceAt(rootB, symbolsB), new IndexHolder(MillerRepositoryIndex.Build([]), 2));
            await service.DrainOnceAsync(TestContext.Current.CancellationToken);

            Assert.Equal([rootA, rootB], openedGcRoots);
        }
        finally
        {
            Directory.Delete(rootA, recursive: true);
            Directory.Delete(rootB, recursive: true);
        }
    }

    [Fact]
    public async Task Gc_NeverRunsWithoutAConvergeWake_SoAReaderInstanceNeverCollects()
    {
        string root = Directory.CreateTempSubdirectory("miller-vec-gc-reader-").FullName;
        try
        {
            string symbols = SeedSymbolsDb(root);
            var port = new FakePort();
            var gc = new RecordingGc();

            // A reader instance's converge signal is never stamped — only the indexer leader stamps it — so its
            // drain, and the GC piggybacked on the drain, never runs.
            var signal = new VectorConvergeSignal(enabled: true);
            await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);
            VectorConvergeService service =
                ServiceOverWorkspace(root, symbols, _ => port, _ => session, signal, openGc: _ => gc);

            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(0, gc.Collects);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingGc : IVectorGenerationGc
    {
        public int Collects { get; private set; }

        public bool LastActiveIsReady { get; private set; }

        public IReadOnlySet<string> LastTags { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

        public void Collect(bool activeIsReady, DateTimeOffset now, IReadOnlySet<string> tagsWithLiveReaders)
        {
            Collects++;
            LastActiveIsReady = activeIsReady;
            LastTags = tagsWithLiveReaders;
        }
    }

    private static SemanticSessionOptions FastOptions => new()
    {
        RequestTimeout = TimeSpan.FromSeconds(10),
        InitTimeout = TimeSpan.FromSeconds(10),
        ShutdownTimeout = TimeSpan.FromMilliseconds(200),
        RestartBackoff = TimeSpan.Zero,
        RestartBackoffCap = TimeSpan.Zero,
        Delay = static (_, _) => Task.CompletedTask,
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.True(condition());
    }

    private static string SeedCorruptVectorArtifact(string root)
    {
        string symbols = SeedSymbolsDb(root);
        File.WriteAllText(VectorSidecar.PathFor(root), "corrupt");
        return symbols;
    }

    private static string SeedSymbolsDb(string root)
    {
        string millerDir = Path.Combine(root, ".miller");
        Directory.CreateDirectory(millerDir);
        string symbols = Path.Combine(millerDir, "symbols.db");
        File.WriteAllText(symbols, "source of truth");
        return symbols;
    }

    private static WorkspaceContext WorkspaceAt(string root, string symbolsDbPath) =>
        WorkspaceContext.Create(root, AppContext.BaseDirectory, root) with
        {
            CanonicalRoot = root,
            CanonicalExtractDbPath = symbolsDbPath,
        };

    private static VectorConvergeService ServiceOverPorts(Func<WorkspaceContext, IVectorConvergePort?> openPort) =>
        new(
            IsolatedBootstrap(),
            new VectorSidecar(SemanticMode.On),
            new VectorConvergeSignal(enabled: true),
            NullLogger.Instance,
            openPort,
            _ => null,
            () => DateTimeOffset.UnixEpoch);

    private static VectorConvergeService ServiceOverWorkspace(
        string root,
        string symbolsDbPath,
        Func<WorkspaceContext, IVectorConvergePort?> openPort,
        Func<WorkspaceContext, SemanticEmbeddingSession?> openSession,
        VectorConvergeSignal? signal = null,
        Func<WorkspaceContext, IVectorGenerationGc?>? openGc = null,
        VectorLiveReaderRegistry? registry = null)
    {
        IndexBootstrapService bootstrap = IsolatedBootstrap();
        bootstrap.SeedForTest(WorkspaceAt(root, symbolsDbPath), new IndexHolder(MillerRepositoryIndex.Build([]), 1));

        return new VectorConvergeService(
            bootstrap,
            new VectorSidecar(SemanticMode.On),
            signal ?? new VectorConvergeSignal(enabled: true),
            NullLogger.Instance,
            openPort,
            openSession,
            () => DateTimeOffset.UnixEpoch,
            openGc: openGc,
            readerRegistry: registry);
    }

    [Fact]
    public async Task Drain_WakeEndsWithCircuitOpen_StampsCircuitOpenPauseAndANonEmptyReason()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];

        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.CrashMidBatch), FastOptions);
        VectorConvergeService service = NewService();

        await service.DrainAsync(port, session, TestContext.Current.CancellationToken);
        await service.DrainAsync(port, session, TestContext.Current.CancellationToken);

        Assert.Equal(SemanticSessionState.CircuitOpen, session.State);
        Assert.Equal("circuit-open", port.Meta("converge_pause_state"));
        Assert.False(string.IsNullOrWhiteSpace(port.Meta("converge_pause_reason")));
    }

    [Fact]
    public async Task Drain_FirstSuccessfulWakeAfterRecovery_ClearsAStaleCircuitOpenPause()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        port.Metadata["converge_pause_state"] = "circuit-open";
        port.Metadata["converge_pause_reason"] = "sidecar restarts exhausted";

        await using var healthy = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        await NewService().DrainAsync(port, healthy, TestContext.Current.CancellationToken);

        Assert.NotEqual(SemanticSessionState.CircuitOpen, healthy.State);

        // Empty is what the consumer's PauseState treats as absent (its switch falls through to null), so
        // VectorSidecar classification returns to ready/building — fixed by VectorSidecarClassificationTests.
        Assert.True(string.IsNullOrEmpty(port.Meta("converge_pause_state")));
        Assert.True(string.IsNullOrEmpty(port.Meta("converge_pause_reason")));
    }

    [Fact]
    public async Task Drain_SteadyHealthyWake_WritesNoPauseMeta()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];

        await DrainAsync(port);

        Assert.DoesNotContain("converge_pause_state", port.MetaWrites);
        Assert.DoesNotContain("converge_pause_reason", port.MetaWrites);
    }

    [Fact]
    public async Task Drain_CircuitStaysOpenAcrossWakes_StampsThePauseExactlyOnce()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];

        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.CrashMidBatch), FastOptions);
        VectorConvergeService service = NewService();

        await service.DrainAsync(port, session, TestContext.Current.CancellationToken);
        await service.DrainAsync(port, session, TestContext.Current.CancellationToken);
        await service.DrainAsync(port, session, TestContext.Current.CancellationToken);

        Assert.Equal(SemanticSessionState.CircuitOpen, session.State);
        Assert.Equal(1, port.MetaWrites.Count(key => key == "converge_pause_state"));
    }

    [Fact]
    public async Task Drain_ShadowBuildRefusedForDisk_StampsDiskBlockedWithFreeAndRequiredAndHoldsTheCursorWithNoDebris()
    {
        var live = new FakePort();
        live.SymbolSnapshot = live.SymbolSnapshot with { DeltaHistoryComplete = false };
        live.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        var shadow = new FakePort();
        shadow.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        var rebuilder = new FakeShadowRebuilder(shadow);

        IReadOnlyList<VectorCursorOutcome> outcomes =
            await DrainWithDiskAsync(live, rebuilder, Blocking(free: 399, required: 400));

        Assert.False(rebuilder.Promoted);
        Assert.False(live.Disposed);
        Assert.Equal(0, rebuilder.OpenShadowCalls);
        Assert.DoesNotContain(shadow.Commits, c => c.Kind is VectorUnitKind.Symbol);

        Assert.Equal("disk-blocked", live.Meta("converge_pause_state"));
        string reason = live.Meta("converge_pause_reason")!;
        Assert.Contains("399", reason, StringComparison.Ordinal);
        Assert.Contains("400", reason, StringComparison.Ordinal);

        Assert.Equal("0", live.Meta(VectorConvergeService.SymbolCompletedKey));
        Assert.False(string.IsNullOrEmpty(live.Meta(VectorConvergeService.SymbolErrorKey)));
    }

    [Fact]
    public async Task Drain_DiskRecoversAndBuildPromotes_LeavesNoDiskBlockedPauseOnTheServedArtifact()
    {
        var live = new FakePort();
        live.SymbolSnapshot = live.SymbolSnapshot with { DeltaHistoryComplete = false };
        live.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        live.Metadata["converge_pause_state"] = "disk-blocked";
        live.Metadata["converge_pause_reason"] = "not enough free disk (399 bytes free, 400 bytes required)";

        var shadow = new FakePort();
        shadow.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        var promoted = new FakePort();
        var rebuilder = new FakeShadowRebuilder(shadow);

        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        IReadOnlyList<VectorCursorOutcome> outcomes = await NewService().DrainAsync(
            live, session, rebuilder, () => promoted, VectorConvergeService.AlwaysAvailable,
            TestContext.Current.CancellationToken);

        Assert.True(rebuilder.Promoted);
        Assert.True(string.IsNullOrEmpty(promoted.Meta("converge_pause_state")));
    }

    [Fact]
    public async Task Drain_FirstIncrementalWakeAfterDiskRecovers_ClearsAStaleDiskBlockedPause()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        port.Metadata["converge_pause_state"] = "disk-blocked";
        port.Metadata["converge_pause_reason"] = "not enough free disk (399 bytes free, 400 bytes required)";

        await DrainAsync(port);

        Assert.True(string.IsNullOrEmpty(port.Meta("converge_pause_state")));
        Assert.True(string.IsNullOrEmpty(port.Meta("converge_pause_reason")));
    }

    [Fact]
    public async Task Drain_DiskBlockedAndCircuitOpen_CircuitOpenWinsThePauseState()
    {
        var live = new FakePort();
        live.SymbolUnits = [Card("a", "src/A.cs", "card a")];
        var rebuilder = new FakeShadowRebuilder(new FakePort());

        await using var session = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(FakeSidecarFault.CrashMidBatch), FastOptions);
        VectorConvergeService service = NewService();

        await service.DrainAsync(live, session, TestContext.Current.CancellationToken);
        await service.DrainAsync(live, session, TestContext.Current.CancellationToken);
        Assert.Equal(SemanticSessionState.CircuitOpen, session.State);

        live.SymbolSnapshot = live.SymbolSnapshot with { DeltaHistoryComplete = false };
        await service.DrainAsync(
            live, session, rebuilder, null, Blocking(free: 399, required: 400),
            TestContext.Current.CancellationToken);

        Assert.Equal(SemanticSessionState.CircuitOpen, session.State);
        Assert.Equal("circuit-open", live.Meta("converge_pause_state"));
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

    [Fact]
    public async Task Drain_IncrementalReEmbedRefusedForDisk_HoldsTheCursorStampsDiskBlockedAndWritesNothing()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a")];

        IReadOnlyList<VectorCursorOutcome> blocked =
            await DrainWithDiskAsync(port, rebuilder: null, Blocking(free: 399, required: 400));

        VectorCursorOutcome symbols = blocked.Single(o => o.Kind is VectorUnitKind.Symbol);
        Assert.Equal(0, symbols.CompletedRevision);
        Assert.DoesNotContain(port.Commits, c => c.Kind is VectorUnitKind.Symbol);
        Assert.Equal("disk-blocked", port.Meta("converge_pause_state"));
        string reason = port.Meta("converge_pause_reason")!;
        Assert.Contains("399", reason, StringComparison.Ordinal);
        Assert.Contains("400", reason, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(port.Meta(VectorConvergeService.SymbolErrorKey)));

        IReadOnlyList<VectorCursorOutcome> resumed = await DrainAsync(port);

        VectorCursorOutcome symbolsAfter = resumed.Single(o => o.Kind is VectorUnitKind.Symbol);
        Assert.Equal(1, symbolsAfter.Embedded);
        Assert.Equal(5, symbolsAfter.CompletedRevision);
        Assert.True(string.IsNullOrEmpty(port.Meta("converge_pause_state")));
    }

    [Fact]
    public async Task Drain_ChunkSourcesDeferred_LogsAnInfoLineNamingThePathsWhileTheStoredReasonStaysPathFree()
    {
        var port = new FakePort();
        port.ChunkFactsValue = port.ChunkFactsValue with
        {
            Sources = [new ChunkSourceHash("docs/guide.md", "hashA", null)],
        };
        var logger = new RecordingLogger();

        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        await ServiceWithLogger(logger).DrainAsync(port, session, TestContext.Current.CancellationToken);

        LogEntry info = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("deferred", StringComparison.Ordinal));
        Assert.Contains("docs/guide.md", info.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/guide.md", port.Meta(VectorConvergeService.ChunkErrorKey)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drain_IncrementalEmbed_LogsTheUnitCountAndElapsedMilliseconds()
    {
        var port = new FakePort();
        port.SymbolUnits = [Card("a", "src/A.cs", "card a v2"), Card("b", "src/A.cs", "card b")];
        port.SymbolStored = [State("a", "src/A.cs", "card a"), State("b", "src/A.cs", "card b")];
        var logger = new RecordingLogger();

        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        await ServiceWithLogger(logger).DrainAsync(port, session, TestContext.Current.CancellationToken);

        LogEntry info = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("embedded 1 unit", StringComparison.Ordinal));
        Assert.Contains(" ms", info.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drain_ShadowRebuildPromote_LogsTheBuildDurationAndThroughput()
    {
        var live = new FakePort();
        live.SymbolSnapshot = live.SymbolSnapshot with { DeltaHistoryComplete = false };
        var shadow = new FakePort();
        shadow.SymbolUnits = [Card("a", "src/A.cs", "card a"), Card("b", "src/B.cs", "card b")];
        var logger = new RecordingLogger();

        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        await ServiceWithLogger(logger).DrainAsync(
            live, session, new FakeShadowRebuilder(shadow), TestContext.Current.CancellationToken);

        LogEntry info = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Promoted a shadow", StringComparison.Ordinal));
        Assert.Contains("2 embedded symbol cards", info.Message, StringComparison.Ordinal);
        Assert.Contains(" ms", info.Message, StringComparison.Ordinal);
        Assert.Contains("cards/s", info.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeldChunkCursorOnAQuietWorkspace_ReDrainsAfterTheRetryDelayWithNoExternalStamp()
    {
        string root = Directory.CreateTempSubdirectory("miller-vec-retry-").FullName;
        try
        {
            string symbols = SeedSymbolsDb(root);
            var port = DeferredChunkPort();
            var signal = new VectorConvergeSignal(enabled: true);
            var gate = new DelayGate();

            await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);
            VectorConvergeService service = ServiceWithRetry(root, symbols, _ => port, _ => session, signal, gate.DelayAsync);

            await service.StartAsync(CancellationToken.None);
            signal.StampTarget(5, fullRebuild: false);

            await gate.Requested.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal("0", port.Meta(VectorConvergeService.ChunkCompletedKey));

            AgreeChunkSources(port);
            gate.Release();

            await WaitUntil(() => port.Meta(VectorConvergeService.ChunkCompletedKey) == "5");
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(1, gate.RequestCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HeldChunkCursor_ARealConvergeWakeBeforeTheRetryDelay_CancelsThePendingRetry()
    {
        string root = Directory.CreateTempSubdirectory("miller-vec-retry-").FullName;
        try
        {
            string symbols = SeedSymbolsDb(root);
            var port = DeferredChunkPort();
            var signal = new VectorConvergeSignal(enabled: true);
            var gate = new DelayGate();

            await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher(), FastOptions);
            VectorConvergeService service = ServiceWithRetry(root, symbols, _ => port, _ => session, signal, gate.DelayAsync);

            await service.StartAsync(CancellationToken.None);
            signal.StampTarget(5, fullRebuild: false);
            await gate.Requested.WaitAsync(TestContext.Current.CancellationToken);

            AgreeChunkSources(port);
            signal.StampTarget(6, fullRebuild: false);

            await WaitUntil(() => gate.Canceled && port.Meta(VectorConvergeService.ChunkCompletedKey) == "5");
            await service.StopAsync(CancellationToken.None);

            Assert.True(gate.Canceled);
            Assert.Equal(1, gate.RequestCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FakePort DeferredChunkPort()
    {
        var port = new FakePort();
        port.ChunkFactsValue = port.ChunkFactsValue with
        {
            Sources = [new ChunkSourceHash("docs/guide.md", "hashA", null)],
        };
        return port;
    }

    private static void AgreeChunkSources(FakePort port) =>
        port.ChunkFactsValue = port.ChunkFactsValue with
        {
            Sources = [new ChunkSourceHash("docs/guide.md", "hashA", "hashA")],
        };

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed class DelayGate
    {
        private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public bool Canceled { get; private set; }

        public Task Requested => _requested.Task;

        public async Task DelayAsync(TimeSpan _, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _requested.TrySetResult();
            using (cancellationToken.Register(() =>
            {
                Canceled = true;
                _release.TrySetCanceled(cancellationToken);
            }))
            {
                await _release.Task.ConfigureAwait(false);
            }
        }

        public void Release() => _release.TrySetResult();
    }

    private static VectorConvergeService ServiceWithLogger(ILogger logger) =>
        new(
            IsolatedBootstrap(),
            new VectorSidecar(SemanticMode.On),
            new VectorConvergeSignal(enabled: true),
            logger,
            _ => null,
            _ => null,
            () => DateTimeOffset.UnixEpoch);

    private static VectorConvergeService ServiceWithRetry(
        string root,
        string symbolsDbPath,
        Func<WorkspaceContext, IVectorConvergePort?> openPort,
        Func<WorkspaceContext, SemanticEmbeddingSession?> openSession,
        VectorConvergeSignal signal,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        IndexBootstrapService bootstrap = IsolatedBootstrap();
        bootstrap.SeedForTest(WorkspaceAt(root, symbolsDbPath), new IndexHolder(MillerRepositoryIndex.Build([]), 1));

        return new VectorConvergeService(
            bootstrap,
            new VectorSidecar(SemanticMode.On),
            signal,
            NullLogger.Instance,
            openPort,
            openSession,
            () => DateTimeOffset.UnixEpoch,
            heldRetryDelay: TimeSpan.FromMinutes(5),
            delay: delay);
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

    private static async Task<IReadOnlyList<VectorCursorOutcome>> DrainAsync(
        FakePort port,
        IVectorShadowRebuilder? rebuilder = null)
    {
        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        return await NewService().DrainAsync(port, session, rebuilder, TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<VectorCursorOutcome>> DrainWithDiskAsync(
        FakePort port,
        IVectorShadowRebuilder? rebuilder,
        DiskGate diskGate)
    {
        await using var session = new SemanticEmbeddingSession(FakeSemanticSidecar.InProcessLauncher());
        return await NewService().DrainAsync(
            port, session, rebuilder, null, diskGate, TestContext.Current.CancellationToken);
    }

    private static DiskGate Blocking(long free, long required) =>
        _ => new DiskPreflightVerdict(false, free, required);

    private sealed class FakeShadowRebuilder(FakePort? shadow) : IVectorShadowRebuilder
    {
        public int OpenShadowCalls { get; private set; }

        public bool Promoted { get; private set; }

        public Exception? OpenFault { get; init; }

        public IVectorConvergePort? OpenShadow()
        {
            OpenShadowCalls++;
            return OpenFault is null ? shadow : throw OpenFault;
        }

        public void Promote(SemanticGenerationIdentity live, SemanticGenerationIdentity built) => Promoted = true;
    }

    private static VectorCorpusUnit Card(string id, string path, string text) =>
        new(id, path, text, "method", IsTest: false);

    private static IReadOnlyList<VectorCorpusUnit> ManyCards(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Card($"u{i}", $"src/F{i % 50}.cs", $"card {i}"))];

    private static IReadOnlyList<VectorCorpusUnit> ManyChunks(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Card($"c{i}", "docs/a.md", $"chunk {i}"))];

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

        // The real port's Stored reflects every prior commit; the overlay keeps this fake contract-faithful so
        // multi-batch drains see their own progress through the hash gate.
        private readonly Dictionary<string, VectorUnitState> _committedSymbols = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VectorUnitState> _committedChunks = new(StringComparer.Ordinal);

        public IReadOnlyList<VectorUnitState> Stored(VectorUnitKind kind, IReadOnlyCollection<string>? paths) =>
            [.. Merged(kind).Values];

        public int TotalStored(VectorUnitKind kind) => Merged(kind).Count;

        private Dictionary<string, VectorUnitState> Merged(VectorUnitKind kind)
        {
            IReadOnlyList<VectorUnitState> seeded = kind is VectorUnitKind.Symbol ? SymbolStored : ChunkStored;
            Dictionary<string, VectorUnitState> committed =
                kind is VectorUnitKind.Symbol ? _committedSymbols : _committedChunks;

            var merged = new Dictionary<string, VectorUnitState>(StringComparer.Ordinal);
            foreach (VectorUnitState state in seeded)
                merged[state.UnitId] = state;
            foreach ((string id, VectorUnitState state) in committed)
                merged[id] = state;
            return merged;
        }

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

            Dictionary<string, VectorUnitState> committed =
                kind is VectorUnitKind.Symbol ? _committedSymbols : _committedChunks;
            foreach (VectorCommit vector in vectors)
                committed[vector.Unit.UnitId] =
                    new VectorUnitState(vector.Unit.UnitId, vector.Unit.Path, vector.Unit.EmbedTextHash);
            foreach (string id in delete)
                committed.Remove(id);

            if (advanceTo > 0)
                Metadata[completedRevisionKey] = advanceTo.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
