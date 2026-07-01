using Miller.Core.Contracts;
using Miller.Core.Resolver;
using Xunit;

namespace Miller.Tests.Resolver;

/// <summary>
/// Pins <see cref="BridgeScorer"/> — the trust contract (design §5). Every assertion is proven from the candidate
/// PAYLOAD before any leg exists: the scorer reads typed <see cref="Signal"/> records (fieldCount/Jaccard, name tier,
/// per-side <see cref="NameResolutionSignal"/>) and never re-queries the resolver. Covers the four required cases —
/// <c>fieldset{count=1}</c> ⇒ no edge; <c>fieldset{count=8,jaccard=0.6}</c> + a structural signal ⇒ scores;
/// ambiguous-name ⇒ never High; multi-signal outscores single — plus the §5 band boundaries and the unresolved-no-edge
/// rule. Asserts on the concrete band and numeric score, not just "did not throw".
/// </summary>
public sealed class BridgeScorerTests
{
    // ---- payload builders (keep each test about the SIGNALS, not ref plumbing) ---------------------------------

    private static NameResolution Resolved(string id = "1") => new(ResolutionStatus.Resolved, id, 1);
    private static NameResolution Ambiguous(int matchCount = 2) => new(ResolutionStatus.Ambiguous, null, matchCount);
    private static NameResolution Unresolved() => new(ResolutionStatus.Unresolved, null, 0);

    private static EdgeRef Ref(string display, NameResolution resolution)
        => new(display, resolution.SymbolId, "x.cs", resolution);

    private static CandidateEdge Edge(
        IReadOnlyList<Signal> signals,
        NameResolution? source = null,
        NameResolution? target = null,
        BridgeKind kind = BridgeKind.MapsTo,
        FieldSet? sourceFields = null,
        FieldSet? targetFields = null)
    {
        var src = source ?? Resolved("S");
        var tgt = target ?? Resolved("T");
        return new CandidateEdge(
            kind,
            Ref("Source", src),
            Ref("Target", tgt),
            Evidence: [],
            Signals: signals,
            SourceFieldSet: sourceFields,
            TargetFieldSet: targetFields);
    }

    private static FieldSet Fields(int count)
    {
        var members = new List<FieldMember>(count);
        for (int i = 0; i < count; i++)
            members.Add(new FieldMember("F" + i, "string"));
        return new FieldSet("owner", members);
    }

    private static readonly Evidence Site = new("Profile.cs", 24);

    // ---- the four required payload-proven cases ----------------------------------------------------------------

    [Fact]
    public void FieldSetOnly_OneField_ProducesNoEdge()
    {
        // A 1-field/generic shape can NOT anchor, and field-set Jaccard is NEVER a sole signal — so a candidate whose
        // ONLY signal is a 1-field overlap must yield no edge at all.
        var edge = Edge([new FieldSetSignal(FieldCount: 1, Jaccard: 1.0, Site)]);

        var scored = BridgeScorer.Score(edge);

        Assert.Null(scored);
    }

    [Fact]
    public void FieldSetOnly_ManyFields_StillProducesNoEdge()
    {
        // Even a rich, high-Jaccard field-set is never a SOLE anchor — it only raises an edge that already has a signal.
        var edge = Edge([new FieldSetSignal(FieldCount: 8, Jaccard: 0.9, Site)]);

        var scored = BridgeScorer.Score(edge);

        Assert.Null(scored);
    }

    [Fact]
    public void Structural_PlusRichFieldSet_ScoresHigh()
    {
        // fieldset{count=8, jaccard=0.6} + a structural breadcrumb (CreateMap) => a real, High edge.
        var edge = Edge(
        [
            new StructuralSignal(SignalRule.CreateMap, Present: true, Site),
            new FieldSetSignal(FieldCount: 8, Jaccard: 0.6, Site),
        ]);

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.True(scored.Score >= 0.9, $"expected High (>=0.9), got {scored.Score}");
    }

    [Fact]
    public void AmbiguousName_WithStructuralBreadcrumb_IsNeverHigh()
    {
        // The structural breadcrumb alone would be High, but a side whose name resolves AMBIGUOUSLY can NEVER be High —
        // the scorer reads NameResolution.status off the payload, it does NOT re-query the resolver.
        var edge = Edge(
            signals:
            [
                new StructuralSignal(SignalRule.CreateMap, Present: true, Site),
                new NameResolutionSignal(EndpointSide.Target, ResolutionStatus.Ambiguous, MatchCount: 2),
            ],
            target: Ambiguous());

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.NotEqual(ConfidenceBand.High, scored!.Band);
        Assert.True(scored.Score < 0.9, $"ambiguous-name edge must be < High, got {scored.Score}");
    }

    [Fact]
    public void MultiSignal_OutscoresOtherwiseIdenticalSingleSignal()
    {
        // N independent signals score higher than one (the multi-signal boost). Same structural anchor, but the
        // multi-signal candidate adds a name corroborator + a rich field-set.
        var single = Edge([new StructuralSignal(SignalRule.RouteVerbMatch, Present: true, Site)]);
        var multi = Edge(
        [
            new StructuralSignal(SignalRule.RouteVerbMatch, Present: true, Site),
            new NameSignal(NameTier.Exact, Site),
            new FieldSetSignal(FieldCount: 8, Jaccard: 0.8, Site),
        ]);

        var scoredSingle = BridgeScorer.Score(single);
        var scoredMulti = BridgeScorer.Score(multi);

        Assert.NotNull(scoredSingle);
        Assert.NotNull(scoredMulti);
        Assert.True(
            scoredMulti!.Score > scoredSingle!.Score,
            $"multi ({scoredMulti.Score}) should outscore single ({scoredSingle.Score})");
        Assert.True(scoredMulti.IsMultiSignal);
        Assert.False(scoredSingle.IsMultiSignal);
    }

    [Fact]
    public void DuplicateRuleSignals_DoNotInflateScoreOrBoost()
    {
        // Two field-set comparisons of the SAME rule must count once: the dup candidate scores identically to the
        // single-field-set one and is not "more multi-signal". The boost rewards independent KINDS of evidence.
        var single = Edge(
        [
            new StructuralSignal(SignalRule.CreateMap, Present: true, Site),
            new FieldSetSignal(FieldCount: 8, Jaccard: 0.6, Site),
        ]);
        var withDuplicate = Edge(
        [
            new StructuralSignal(SignalRule.CreateMap, Present: true, Site),
            new FieldSetSignal(FieldCount: 8, Jaccard: 0.6, Site),
            new FieldSetSignal(FieldCount: 6, Jaccard: 0.9, Site), // same rule (FieldSetJaccard) — must not double-count
        ]);

        var scoredSingle = BridgeScorer.Score(single);
        var scoredDup = BridgeScorer.Score(withDuplicate);

        Assert.NotNull(scoredSingle);
        Assert.NotNull(scoredDup);
        Assert.Equal(scoredSingle!.Score, scoredDup!.Score);
        Assert.Equal(scoredSingle.IsMultiSignal, scoredDup.IsMultiSignal);
    }

    [Fact]
    public void DuplicateStructuralRule_IsNotMultiSignal()
    {
        // The same structural breadcrumb emitted twice is ONE kind of evidence: High band, but NOT multi-signal and no
        // within-band boost (the score sits at the High base, not base + step).
        var edge = Edge(
        [
            new StructuralSignal(SignalRule.CreateMap, Present: true, Site),
            new StructuralSignal(SignalRule.CreateMap, Present: true, Site),
        ]);

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.False(scored.IsMultiSignal);
        Assert.Equal(0.90, scored.Score, precision: 5);
    }

    [Fact]
    public void TwoDistinctStructuralRules_AreMultiSignal()
    {
        // Different structural rules ARE independent kinds of evidence — they stack into the multi-signal boost (only
        // same-rule repeats are deduped).
        var edge = Edge(
        [
            new StructuralSignal(SignalRule.RouteVerbMatch, Present: true, Site),
            new StructuralSignal(SignalRule.ReturnTypeDto, Present: true, Site),
        ], kind: BridgeKind.Hits);

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.True(scored.IsMultiSignal);
        Assert.True(scored.Score > 0.90, $"two distinct structural rules should boost above the High base, got {scored.Score}");
    }

    // ---- §5 band boundaries ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SignalRule.CreateMap)]
    [InlineData(SignalRule.DbSetProperty)]
    [InlineData(SignalRule.RouteVerbMatch)]
    [InlineData(SignalRule.RouteReferenceMatch)]
    public void StructuralBreadcrumb_Resolved_ScoresHigh(SignalRule rule)
    {
        var edge = Edge([new StructuralSignal(rule, Present: true, Site)]);

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.True(scored.Score >= 0.9);
    }

    [Fact]
    public void DapperFrom_WithRealFrom_ScoresHigh()
    {
        // DapperFrom is High ONLY when a real FROM is present — a leg signals that by emitting it present=true.
        var edge = Edge([new StructuralSignal(SignalRule.DapperFrom, Present: true, Site)], kind: BridgeKind.StoredIn);

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
    }

    [Fact]
    public void StructuralSignal_PresentFalse_DoesNotAnchor()
    {
        // A considered-but-absent structural signal is no anchor — alone it yields no edge.
        var edge = Edge([new StructuralSignal(SignalRule.CreateMap, Present: false, Site)]);

        var scored = BridgeScorer.Score(edge);

        Assert.Null(scored);
    }

    [Fact]
    public void RouteOnlyMatch_IsMedium_NotHigh()
    {
        // A verb-unknown carrier matched on route alone => Medium, never High (never assume GET).
        var edge = Edge([new StructuralSignal(SignalRule.RouteOnlyMatch, Present: true, Site)], kind: BridgeKind.Hits);

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.Medium, scored!.Band);
        Assert.InRange(scored.Score, 0.7, 0.85);
    }

    [Fact]
    public void ExactName_PlusCorroborator_IsMedium()
    {
        // Exact/affix name + >=1 corroborator (a rich field-set) => Medium. Name alone would be no edge.
        var edge = Edge(
        [
            new NameSignal(NameTier.Exact, Site),
            new FieldSetSignal(FieldCount: 6, Jaccard: 0.7, Site),
        ]);

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.Medium, scored!.Band);
    }

    [Fact]
    public void NameMatchAlone_ProducesNoEdge()
    {
        // The name finisher is NEVER the sole signal — a name match with no corroborator yields no edge.
        var edge = Edge([new NameSignal(NameTier.Exact, Site)]);

        var scored = BridgeScorer.Score(edge);

        Assert.Null(scored);
    }

    [Fact]
    public void NameMatch_CorroboratedByOneFieldShape_ProducesNoEdge()
    {
        // A 1-field shape can NOT corroborate a name match into an edge (kills RevisionEntry↔DocumentRevisionDto FPs).
        var edge = Edge(
        [
            new NameSignal(NameTier.Affix, Site),
            new FieldSetSignal(FieldCount: 1, Jaccard: 1.0, Site),
        ]);

        var scored = BridgeScorer.Score(edge);

        Assert.Null(scored);
    }

    // ---- resolution gates --------------------------------------------------------------------------------------

    [Fact]
    public void UnresolvedSide_ProducesNoEdge()
    {
        // unresolved => no edge, even with a structural breadcrumb (there is no symbol to point at).
        var edge = Edge(
            signals:
            [
                new StructuralSignal(SignalRule.CreateMap, Present: true, Site),
                new NameResolutionSignal(EndpointSide.Source, ResolutionStatus.Unresolved, MatchCount: 0),
            ],
            source: Unresolved());

        var scored = BridgeScorer.Score(edge);

        Assert.Null(scored);
    }

    [Fact]
    public void AmbiguousName_WithCorroborator_DegradesToMedium_NotDropped()
    {
        // An ambiguous side is never High, but a corroborated structural edge still surfaces at a reduced band so the
        // user sees the candidate (flagged), rather than silently vanishing.
        var edge = Edge(
            signals:
            [
                new StructuralSignal(SignalRule.CreateMap, Present: true, Site),
                new NameSignal(NameTier.Exact, Site),
                new NameResolutionSignal(EndpointSide.Target, ResolutionStatus.Ambiguous, MatchCount: 3),
            ],
            target: Ambiguous(3));

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.Medium, scored!.Band);
        Assert.True(scored.HasAmbiguousName);
    }

    [Fact]
    public void EmptySignals_ProducesNoEdge()
    {
        var edge = Edge([]);

        var scored = BridgeScorer.Score(edge);

        Assert.Null(scored);
    }

    [Fact]
    public void Score_NullEdge_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BridgeScorer.Score(null!));
    }

    [Fact]
    public void ScoredEdge_CarriesTheOriginalCandidate()
    {
        // The scorer wraps, never mutates — the candidate (with its evidence + signals) is preserved for rendering.
        var edge = Edge([new StructuralSignal(SignalRule.DbSetProperty, Present: true, Site)], kind: BridgeKind.StoredIn);

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.Same(edge, scored!.Edge);
    }

    [Fact]
    public void RouteVerbMatch_AmbiguousName_FlagDoesNotPromoteAboveMedium()
    {
        // A High-eligible route match still can't be High with an ambiguous side; the verb-unknown/ambiguous flags ride
        // along so the trace tool renders the reduced certainty.
        var edge = Edge(
            signals:
            [
                new StructuralSignal(SignalRule.RouteVerbMatch, Present: true, Site),
                new NameResolutionSignal(EndpointSide.Target, ResolutionStatus.Ambiguous, MatchCount: 2),
            ],
            kind: BridgeKind.Hits,
            target: Ambiguous());

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.NotEqual(ConfidenceBand.High, scored!.Band);
        Assert.True(scored.HasAmbiguousName);
    }
}
