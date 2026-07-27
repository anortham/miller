using Miller.Indexing;
using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class VectorConvergePlannerTests
{
    private static VectorCorpusUnit Unit(string id, string path, string text) =>
        new(id, path, text, "method", IsTest: false);

    private static VectorUnitState Stored(string id, string path, string text) =>
        new(id, path, SymbolCardBuilder.EmbedTextHash(text));

    private static VectorConvergeRequest Request(
        IReadOnlyList<VectorCorpusUnit> candidates,
        IReadOnlyList<VectorUnitState> stored,
        int totalStored = 10) =>
        new()
        {
            Kind = VectorUnitKind.Symbol,
            CompletedRevision = 4,
            TargetRevision = 5,
            Candidates = candidates,
            Stored = stored,
            TotalStoredUnits = totalStored,
        };

    [Fact]
    public void Plan_UnchangedEmbedTextHash_ProducesNoWork()
    {
        VectorConvergePlan plan = VectorConvergePlanner.Plan(Request(
            [Unit("a", "src/A.cs", "card a"), Unit("b", "src/A.cs", "card b")],
            [Stored("a", "src/A.cs", "card a"), Stored("b", "src/A.cs", "card b")]));

        Assert.Empty(plan.ReEmbed);
        Assert.Empty(plan.Delete);
        Assert.Equal(VectorConvergeDecision.Incremental, plan.Decision);
        Assert.Equal(5, plan.AdvanceTo);
    }

    [Fact]
    public void Plan_ChangedEmbedTextHash_ReEmbedsExactlyTheAffectedUnit()
    {
        VectorConvergePlan plan = VectorConvergePlanner.Plan(Request(
            [Unit("a", "src/A.cs", "card a v2"), Unit("b", "src/A.cs", "card b")],
            [Stored("a", "src/A.cs", "card a"), Stored("b", "src/A.cs", "card b")]));

        Assert.Equal(["a"], plan.ReEmbed.Select(u => u.UnitId));
        Assert.Equal(SymbolCardBuilder.EmbedTextHash("card a v2"), plan.ReEmbed[0].EmbedTextHash);
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void Plan_NewUnit_IsReEmbeddedAndVanishedUnitIsDeleted()
    {
        VectorConvergePlan plan = VectorConvergePlanner.Plan(Request(
            [Unit("a", "src/A.cs", "card a"), Unit("c", "src/A.cs", "card c")],
            [Stored("a", "src/A.cs", "card a"), Stored("b", "src/A.cs", "card b")]));

        Assert.Equal(["c"], plan.ReEmbed.Select(u => u.UnitId));
        Assert.Equal(["b"], plan.Delete);
    }

    [Fact]
    public void Plan_ReplayedAfterItsOwnResult_IsIdempotent()
    {
        VectorConvergeRequest first = Request(
            [Unit("a", "src/A.cs", "card a v2")],
            [Stored("a", "src/A.cs", "card a")]);
        VectorConvergePlan applied = VectorConvergePlanner.Plan(first);

        VectorConvergePlan replay = VectorConvergePlanner.Plan(first with
        {
            Stored = [.. applied.ReEmbed.Select(u => new VectorUnitState(u.UnitId, u.Path, u.EmbedTextHash))],
        });

        Assert.Empty(replay.ReEmbed);
        Assert.Empty(replay.Delete);
        Assert.Equal(first.TargetRevision, replay.AdvanceTo);
    }

    [Fact]
    public void Plan_TargetNotAheadOfCompleted_HoldsAtCompletedWithNoWork()
    {
        VectorConvergePlan plan = VectorConvergePlanner.Plan(Request([], []) with { TargetRevision = 4 });

        Assert.Empty(plan.ReEmbed);
        Assert.Equal(4, plan.AdvanceTo);
        Assert.Equal(VectorConvergeDecision.Incremental, plan.Decision);
    }

    [Fact]
    public void Plan_MissingDeltaHistory_EscalatesToShadow()
    {
        VectorConvergePlan plan = VectorConvergePlanner.Plan(
            Request([Unit("a", "src/A.cs", "x")], []) with { DeltaHistoryComplete = false });

        Assert.Equal(VectorConvergeDecision.ShadowRebuild, plan.Decision);
        Assert.Equal(VectorEscalationTrigger.DeltaHistoryMissing, plan.Trigger);
        Assert.Empty(plan.ReEmbed);
        Assert.Equal(0, plan.AdvanceTo);
    }

    [Fact]
    public void Plan_ArtifactIdChanged_EscalatesToShadow()
    {
        VectorConvergePlan plan = VectorConvergePlanner.Plan(
            Request([Unit("a", "src/A.cs", "x")], []) with { ArtifactIdChanged = true });

        Assert.Equal(VectorEscalationTrigger.ArtifactIdChanged, plan.Trigger);
        Assert.Equal(VectorConvergeDecision.ShadowRebuild, plan.Decision);
    }

    [Fact]
    public void Plan_FullRebuildSignalled_EscalatesEvenWhenArtifactIdStillMatches()
    {
        // The artifact-id comparison is one reading of the artifact and can be defeated by an unreadable or
        // coincidentally-equal id. The indexer's own report that it just rebuilt must escalate on its own.
        VectorConvergePlan plan = VectorConvergePlanner.Plan(
            Request([Unit("a", "src/A.cs", "x")], []) with
            {
                ArtifactIdChanged = false,
                FullRebuildSignalled = true,
            });

        Assert.Equal(VectorEscalationTrigger.FullRebuildSignalled, plan.Trigger);
        Assert.Equal(VectorConvergeDecision.ShadowRebuild, plan.Decision);
    }

    [Fact]
    public void Plan_LiveRevisionBelowCompletedCursor_EscalatesInsteadOfPlanningNothing()
    {
        // A promote restarts julie's revision counter, so the live revision lands BELOW the stored cursor.
        // Without an escalation the planner takes the TargetRevision <= CompletedRevision no-op branch and the
        // corpus stays pinned to a generation that no longer exists.
        VectorConvergePlan plan = VectorConvergePlanner.Plan(
            Request([Unit("a", "src/A.cs", "x")], []) with
            {
                CompletedRevision = 5000,
                TargetRevision = 1,
                ArtifactIdChanged = false,
            });

        Assert.Equal(VectorEscalationTrigger.RevisionRegressed, plan.Trigger);
        Assert.Equal(VectorConvergeDecision.ShadowRebuild, plan.Decision);
    }

    [Fact]
    public void Plan_ShadowRebuildIdentityChange_EscalatesButTargetedReEmbedDoesNot()
    {
        VectorConvergeRequest request = Request(
            [Unit("a", "src/A.cs", "x")], [Stored("a", "src/A.cs", "y")]);

        Assert.Equal(
            VectorEscalationTrigger.IdentityChanged,
            VectorConvergePlanner.Plan(request with { IdentityAction = InvalidationAction.ShadowRebuild }).Trigger);
        Assert.Equal(
            VectorEscalationTrigger.None,
            VectorConvergePlanner.Plan(request with { IdentityAction = InvalidationAction.TargetedReEmbed }).Trigger);
    }

    [Fact]
    public void Plan_ReaderGateOnlyChange_NeverReEmbeds()
    {
        VectorConvergePlan plan = VectorConvergePlanner.Plan(
            Request([Unit("a", "src/A.cs", "card a")], [Stored("a", "src/A.cs", "card a")])
                with { IdentityAction = InvalidationAction.ReaderGate });

        Assert.Equal(VectorConvergeDecision.Incremental, plan.Decision);
        Assert.Empty(plan.ReEmbed);
    }

    [Fact]
    public void Plan_ChangedRatioAboveThreshold_EscalatesToShadow()
    {
        VectorCorpusUnit[] candidates = [.. Enumerable.Range(0, 9).Select(i => Unit($"u{i}", "src/A.cs", $"v2 {i}"))];
        VectorUnitState[] stored = [.. Enumerable.Range(0, 9).Select(i => Stored($"u{i}", "src/A.cs", $"v1 {i}"))];

        VectorConvergePlan plan = VectorConvergePlanner.Plan(Request(candidates, stored, totalStored: 10));

        Assert.Equal(VectorEscalationTrigger.ChangedRatioAboveThreshold, plan.Trigger);
    }

    [Fact]
    public void Plan_InitialBuildWithNoStoredUnits_DoesNotEscalateByRatio()
    {
        VectorCorpusUnit[] candidates = [.. Enumerable.Range(0, 50).Select(i => Unit($"u{i}", "src/A.cs", $"c{i}"))];

        VectorConvergePlan plan = VectorConvergePlanner.Plan(Request(candidates, [], totalStored: 0));

        Assert.Equal(VectorConvergeDecision.Incremental, plan.Decision);
        Assert.Equal(50, plan.ReEmbed.Count);
    }

    [Fact]
    public void Plan_BatchLargerThanOneShortTransaction_EscalatesToShadow()
    {
        int over = VectorConvergePlanner.MaxUnitsPerTransaction + 1;
        VectorCorpusUnit[] candidates = [.. Enumerable.Range(0, over).Select(i => Unit($"u{i}", "src/A.cs", $"c{i}"))];

        VectorConvergePlan plan = VectorConvergePlanner.Plan(Request(candidates, [], totalStored: 0));

        Assert.Equal(VectorEscalationTrigger.BatchTooLarge, plan.Trigger);
        Assert.Equal(VectorConvergeDecision.ShadowRebuild, plan.Decision);
    }

    [Fact]
    public void Plan_ChunkSpanBeyondOneTransaction_TruncatesToABoundedBatchInsteadOfEscalating()
    {
        int over = VectorConvergePlanner.MaxUnitsPerTransaction + 1;
        VectorCorpusUnit[] candidates = [.. Enumerable.Range(0, over).Select(i => Unit($"c{i}", "docs/a.md", $"t{i}"))];

        VectorConvergePlan plan = VectorConvergePlanner.Plan(
            Request(candidates, [], totalStored: 0) with { Kind = VectorUnitKind.Chunk });

        Assert.Equal(VectorConvergeDecision.Incremental, plan.Decision);
        Assert.Equal(VectorConvergePlanner.MaxUnitsPerTransaction, plan.ReEmbed.Count);
        Assert.Equal(0, plan.AdvanceTo);
        Assert.Equal(VectorConvergePlanner.BoundedBatchHoldReason, plan.HoldReason);
    }

    [Fact]
    public void Plan_ChunkEscalationTrigger_HoldsInsteadOfShadowRebuilding()
    {
        VectorConvergePlan plan = VectorConvergePlanner.Plan(
            Request([Unit("c1", "docs/a.md", "t")], []) with
            {
                Kind = VectorUnitKind.Chunk,
                DeltaHistoryComplete = false,
            });

        Assert.Equal(VectorConvergeDecision.Incremental, plan.Decision);
        Assert.Equal(VectorEscalationTrigger.DeltaHistoryMissing, plan.Trigger);
        Assert.Empty(plan.ReEmbed);
        Assert.Equal(0, plan.AdvanceTo);
        Assert.Contains("shadow rebuild", plan.HoldReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_ChunkChangedRatio_NeverEscalates()
    {
        VectorCorpusUnit[] candidates = [.. Enumerable.Range(0, 9).Select(i => Unit($"c{i}", "docs/a.md", $"v2 {i}"))];
        VectorUnitState[] stored = [.. Enumerable.Range(0, 9).Select(i => Stored($"c{i}", "docs/a.md", $"v1 {i}"))];

        VectorConvergePlan plan = VectorConvergePlanner.Plan(
            Request(candidates, stored, totalStored: 10) with { Kind = VectorUnitKind.Chunk });

        Assert.Equal(VectorConvergeDecision.Incremental, plan.Decision);
        Assert.Equal(9, plan.ReEmbed.Count);
    }

    [Fact]
    public void RebuildWorkList_HashGatesAgainstStoredWithNoSizeCap()
    {
        int over = VectorConvergePlanner.MaxUnitsPerTransaction + 5;
        VectorCorpusUnit[] candidates = [.. Enumerable.Range(0, over).Select(i => Unit($"u{i}", "src/A.cs", $"c{i}"))];

        IReadOnlyList<VectorWorkUnit> all = VectorConvergePlanner.RebuildWorkList(candidates, []);
        IReadOnlyList<VectorWorkUnit> gated = VectorConvergePlanner.RebuildWorkList(
            candidates, [Stored("u0", "src/A.cs", "c0"), Stored("u1", "src/A.cs", "stale")]);

        Assert.Equal(over, all.Count);
        Assert.Equal(over - 1, gated.Count);
        Assert.DoesNotContain(gated, u => u.UnitId == "u0");
        Assert.Contains(gated, u => u.UnitId == "u1");
    }

    [Fact]
    public void Plan_WithDeferredPaths_StillEmbedsButHoldsTheCursor()
    {
        VectorConvergePlan plan = VectorConvergePlanner.Plan(
            Request([Unit("a", "src/A.cs", "card a v2")], [Stored("a", "src/A.cs", "card a")])
                with { DeferredPaths = ["src/B.cs"] });

        Assert.Single(plan.ReEmbed);
        Assert.Equal(0, plan.AdvanceTo);
        Assert.NotNull(plan.HoldReason);
        Assert.DoesNotContain("src/B.cs", plan.HoldReason!, StringComparison.Ordinal);
    }
}

public sealed class VectorChunkCursorGateTests
{
    private const string Artifact = "artifact-1";

    private static ChunkCursorFacts Facts() => new()
    {
        SymbolsArtifactId = Artifact,
        VectorsArtifactId = Artifact,
        ChunkSourceArtifactId = Artifact,
        ContentSchemaVersion = ContentCorpusSchema.SchemaVersion,
        RecordedChunkSchemaVersion = ContentCorpusSchema.SchemaVersion,
        ContentChunkerVersion = ContentCorpusSchema.ChunkerVersion,
        CorpusGeneration = MillerSemanticContract.CorpusGeneration,
        ContentWorkspaceRevision = 7,
        TargetRevision = 7,
        Sources = [new ChunkSourceHash("docs/a.md", "blake3:aa", "blake3:AA")],
    };

    [Fact]
    public void Rule0_AllPreconditionsHold_Advances()
    {
        ChunkCursorDecision decision = VectorConvergePlanner.EvaluateChunkCursor(Facts());

        Assert.True(decision.CanAdvance);
        Assert.False(decision.ResetCursor);
        Assert.Null(decision.Reason);
        Assert.Empty(decision.DeferredPaths);
    }

    [Fact]
    public void Rule1_VectorsArtifactIdDisagreesWithSymbols_ResetsAndHolds()
    {
        ChunkCursorDecision decision = VectorConvergePlanner.EvaluateChunkCursor(
            Facts() with { VectorsArtifactId = "artifact-2" });

        Assert.False(decision.CanAdvance);
        Assert.True(decision.ResetCursor);
        Assert.NotNull(decision.Reason);
    }

    [Fact]
    public void Rule1_ChunkSourceArtifactIdDisagrees_ResetsAndHolds()
    {
        ChunkCursorDecision decision = VectorConvergePlanner.EvaluateChunkCursor(
            Facts() with { ChunkSourceArtifactId = "artifact-2" });

        Assert.False(decision.CanAdvance);
        Assert.True(decision.ResetCursor);
    }

    [Fact]
    public void Rule1_RunsBeforeOrdering_SoAHigherStaleRevisionCannotBeAccepted()
    {
        ChunkCursorDecision decision = VectorConvergePlanner.EvaluateChunkCursor(Facts() with
        {
            ChunkSourceArtifactId = "artifact-0",
            ContentWorkspaceRevision = 9999,
        });

        Assert.False(decision.CanAdvance);
        Assert.True(decision.ResetCursor);
    }

    [Fact]
    public void Rule2_ContentSchemaVersionDisagrees_Holds()
    {
        ChunkCursorDecision decision = VectorConvergePlanner.EvaluateChunkCursor(
            Facts() with { ContentSchemaVersion = ContentCorpusSchema.SchemaVersion + 1 });

        Assert.False(decision.CanAdvance);
        Assert.False(decision.ResetCursor);
        Assert.Contains("schema", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rule2_ChunkerVersionDisagreesWithCorpusGeneration_Holds()
    {
        ChunkCursorDecision decision = VectorConvergePlanner.EvaluateChunkCursor(
            Facts() with { ContentChunkerVersion = "line-v2" });

        Assert.False(decision.CanAdvance);
        Assert.Contains("chunker", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rule2_UnknownCorpusGenerationChunkerComponent_Holds()
    {
        ChunkCursorDecision decision = VectorConvergePlanner.EvaluateChunkCursor(
            Facts() with { CorpusGeneration = "cards-v1-chunks-v9" });

        Assert.False(decision.CanAdvance);
        Assert.Contains("chunker", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rule3_ContentLagsTheTargetRevision_Holds()
    {
        ChunkCursorDecision decision = VectorConvergePlanner.EvaluateChunkCursor(
            Facts() with { ContentWorkspaceRevision = 6 });

        Assert.False(decision.CanAdvance);
        Assert.False(decision.ResetCursor);
        Assert.Contains("revision", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rule3_ContentAheadWithinTheSameArtifact_Advances() =>
        Assert.True(VectorConvergePlanner.EvaluateChunkCursor(
            Facts() with { ContentWorkspaceRevision = 9 }).CanAdvance);

    [Fact]
    public void Rule4_SourceHashDisagrees_DefersThatPathAndHolds()
    {
        ChunkCursorDecision decision = VectorConvergePlanner.EvaluateChunkCursor(Facts() with
        {
            Sources =
            [
                new ChunkSourceHash("docs/a.md", "blake3:aa", "blake3:aa"),
                new ChunkSourceHash("docs/b.md", "blake3:bb", "blake3:cc"),
            ],
        });

        Assert.False(decision.CanAdvance);
        Assert.Equal(["docs/b.md"], decision.DeferredPaths);
    }

    [Fact]
    public void Rule4_SourceMissingFromSymbols_DefersThatPathAndHolds()
    {
        ChunkCursorDecision decision = VectorConvergePlanner.EvaluateChunkCursor(Facts() with
        {
            Sources = [new ChunkSourceHash("docs/a.md", "blake3:aa", null)],
        });

        Assert.False(decision.CanAdvance);
        Assert.Equal(["docs/a.md"], decision.DeferredPaths);
    }

    [Fact]
    public void Rule4_HashesAgreeOnlyAfterNormalization_StillAdvances() =>
        Assert.True(VectorConvergePlanner.EvaluateChunkCursor(Facts() with
        {
            Sources = [new ChunkSourceHash("docs/a.md", "  BLAKE3:AA ", "blake3:aa")],
        }).CanAdvance);

    [Fact]
    public void HoldReasons_CarryNoPathsSoThePersistedLastErrorStaysScrubbed()
    {
        ChunkCursorDecision decision = VectorConvergePlanner.EvaluateChunkCursor(Facts() with
        {
            Sources = [new ChunkSourceHash("/Users/someone/secret/docs/a.md", "blake3:aa", "blake3:zz")],
        });

        Assert.NotNull(decision.Reason);
        Assert.DoesNotContain("/Users/", decision.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/a.md", decision.Reason!, StringComparison.Ordinal);
    }
}
