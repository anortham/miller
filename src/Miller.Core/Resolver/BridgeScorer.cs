namespace Miller.Core.Resolver;

/// <summary>The §5 confidence band an edge lands in.</summary>
public enum ConfidenceBand
{
    /// <summary>≥0.9 — an explicit structural breadcrumb fired on a non-ambiguous edge (design §5).</summary>
    High,

    /// <summary>~0.7–0.85 — exact/affix name + ≥1 corroborator, route-only, or a breadcrumb degraded by an ambiguous name.</summary>
    Medium,
}

/// <summary>
/// A scored bridge edge: the original <see cref="CandidateEdge"/> wrapped with its computed <see cref="Score"/>,
/// <see cref="Band"/>, and the §5 flags (<see cref="IsMultiSignal"/>, <see cref="HasAmbiguousName"/>,
/// <see cref="IsVerbUnknown"/>) the <c>trace</c> tool renders so nothing is presented as certain when it isn't. The
/// scorer never mutates the candidate; it wraps it.
/// </summary>
/// <param name="Edge">The scored candidate, preserved verbatim (evidence + signals) for rendering.</param>
/// <param name="Score">The confidence in [0,1].</param>
/// <param name="Band">The §5 band the score falls in.</param>
/// <param name="IsMultiSignal">True when ≥2 independent positive signals fired (the multi-signal boost applied).</param>
/// <param name="HasAmbiguousName">True when a side resolved ambiguously — the edge can NEVER be High (it is flagged).</param>
/// <param name="IsVerbUnknown">True when the edge rests on a route-only (verb-unknown) match — flagged as reduced certainty.</param>
public sealed record ScoredEdge(
    CandidateEdge Edge,
    double Score,
    ConfidenceBand Band,
    bool IsMultiSignal,
    bool HasAmbiguousName,
    bool IsVerbUnknown);

/// <summary>
/// Assigns the design §5 confidence band to a candidate edge from its typed-signal PAYLOAD ALONE — no leg-side
/// precision logic, no re-query of the <see cref="SymbolResolver"/>. The load-bearing invariants, each decidable from
/// the candidate:
/// <list type="bullet">
/// <item><b>unresolved ⇒ no edge.</b> A side whose <c>NameResolution</c> status is Unresolved (in a per-side signal or
/// on the <see cref="EdgeRef"/>) has no symbol to point at — the edge is dropped (null).</item>
/// <item><b>field-set Jaccard is NEVER a sole signal,</b> and a <b>1-field/generic shape can't anchor</b> — the scorer
/// reads <see cref="FieldSetSignal.FieldCount"/>; a corroborator needs ≥2 fields, and a corroborator never stands alone.</item>
/// <item><b>the name finisher is never the sole signal</b> — a NameMatch needs a structural breadcrumb or a valid
/// field-set corroborator to form an edge.</item>
/// <item><b>High (≥0.9) requires an explicit structural breadcrumb</b> (CreateMap, DbSetProperty, RouteVerbMatch, or a
/// real DapperFrom) on a non-ambiguous edge.</item>
/// <item><b>an ambiguous-name edge can NEVER be High</b> — the scorer reads <c>NameResolution.status</c> off the
/// payload and caps the band at Medium, flagging it.</item>
/// <item><b>multi-signal boost</b> — ≥2 independent positive signals score strictly higher (within the band) and are
/// flagged.</item>
/// </list>
/// </summary>
public static class BridgeScorer
{
    // §5 band anchors. High starts at 0.9; Medium spans ~0.7–0.85. A small per-corroborator increment gives the
    // multi-signal boost while staying inside the band's ceiling.
    private const double HighBase = 0.90;
    private const double HighCeiling = 0.99;
    private const double MediumBase = 0.70;
    private const double MediumCeiling = 0.85;
    private const double CorroboratorStep = 0.05;

    // A field-set overlap can only corroborate when the (smaller) shape has at least this many fields — a 1-field /
    // generic wrapper Jaccard-matches everything, so it can neither anchor nor corroborate (design §5).
    private const int MinAnchoringFieldCount = 2;

    /// <summary>
    /// Score <paramref name="candidate"/>, or return null when the §5 rules say no edge should be emitted (no anchor,
    /// an unresolved side, or only an insufficient corroborator). The result's <see cref="ScoredEdge.Band"/>/score and
    /// flags are decided entirely from the candidate payload.
    /// </summary>
    /// <param name="candidate">The leg-emitted candidate edge.</param>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is null.</exception>
    public static ScoredEdge? Score(CandidateEdge candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // Gate 1 — unresolved side ⇒ no edge (no symbol to point at). Read from both the per-side signals and the refs.
        if (HasUnresolvedSide(candidate))
            return null;

        bool ambiguous = HasAmbiguousSide(candidate);

        // Classify the positive signals from the payload.
        bool hasStructuralBreadcrumb = false; // a High-eligible structural anchor (CreateMap/DbSet/RouteVerb/DapperFrom/ReturnTypeDto/FromBodyDto)
        bool hasRouteOnly = false;            // a verb-unknown route match (Medium anchor)
        bool hasName = false;                 // a name-stem match (finisher; never sole)
        bool hasFieldCorroborator = false;    // a field-set overlap with >= MinAnchoringFieldCount fields

        // Count DISTINCT positive signal kinds for the multi-signal boost. Deduping by rule stops a leg that emits the
        // same rule twice (e.g. two field-set comparisons, or two CreateMap sites for one pair) from inflating the
        // score/flag — the boost rewards N *independent kinds* of evidence, not N signal records.
        var positiveRules = new HashSet<SignalRule>();

        foreach (var signal in candidate.Signals)
        {
            switch (signal)
            {
                case StructuralSignal { Present: true } s:
                    if (s.Rule == SignalRule.RouteOnlyMatch)
                    {
                        hasRouteOnly = true;
                    }
                    else
                    {
                        hasStructuralBreadcrumb = true;
                    }
                    positiveRules.Add(s.Rule);
                    break;

                case NameSignal:
                    hasName = true;
                    positiveRules.Add(SignalRule.NameMatch);
                    break;

                case FieldSetSignal fs when fs.FieldCount >= MinAnchoringFieldCount && fs.Jaccard > 0.0:
                    hasFieldCorroborator = true;
                    positiveRules.Add(SignalRule.FieldSetJaccard);
                    break;

                // A present=false structural signal, a NameResolution signal, or a sub-threshold field-set: not a
                // positive corroborator. (NameResolution is metadata; it never raises a band.)
                default:
                    break;
            }
        }

        int positiveSignals = positiveRules.Count;

        // Gate 2 — there must be a real anchor:
        //   * a structural breadcrumb, OR
        //   * a route-only match, OR
        //   * a name match WITH a valid field-set corroborator (name is never sole; field-set is never sole).
        bool nameAnchored = hasName && hasFieldCorroborator;
        bool hasAnchor = hasStructuralBreadcrumb || hasRouteOnly || nameAnchored;
        if (!hasAnchor)
            return null;

        bool multiSignal = positiveSignals >= 2;

        // Band selection (design §5):
        //   High  — a structural breadcrumb on a non-ambiguous edge.
        //   Medium — everything else that anchored (route-only, name+corroborator, or a breadcrumb degraded by an
        //            ambiguous name).
        ConfidenceBand band = (hasStructuralBreadcrumb && !ambiguous) ? ConfidenceBand.High : ConfidenceBand.Medium;

        double score = ScoreFor(band, positiveSignals);

        return new ScoredEdge(
            candidate,
            score,
            band,
            IsMultiSignal: multiSignal,
            HasAmbiguousName: ambiguous,
            IsVerbUnknown: hasRouteOnly);
    }

    /// <summary>
    /// The numeric score within a band: the band base plus a per-extra-positive-signal step (the multi-signal boost),
    /// capped at the band ceiling. One signal sits at the base; each additional independent signal raises it, so a
    /// multi-signal edge strictly outscores an otherwise-identical single-signal one without crossing the band line.
    /// </summary>
    private static double ScoreFor(ConfidenceBand band, int positiveSignals)
    {
        var (@base, ceiling) = band == ConfidenceBand.High
            ? (HighBase, HighCeiling)
            : (MediumBase, MediumCeiling);

        int extra = Math.Max(0, positiveSignals - 1);
        double raw = @base + extra * CorroboratorStep;
        return Math.Min(raw, ceiling);
    }

    /// <summary>True when any side resolved Unresolved (per-side signal OR the ref's own resolution).</summary>
    private static bool HasUnresolvedSide(CandidateEdge candidate)
    {
        if (candidate.SourceRef.Resolution.Status == ResolutionStatus.Unresolved ||
            candidate.TargetRef.Resolution.Status == ResolutionStatus.Unresolved)
            return true;

        foreach (var signal in candidate.Signals)
        {
            if (signal is NameResolutionSignal { Status: ResolutionStatus.Unresolved })
                return true;
        }
        return false;
    }

    /// <summary>True when any side resolved Ambiguous (per-side signal OR the ref's own resolution).</summary>
    private static bool HasAmbiguousSide(CandidateEdge candidate)
    {
        if (candidate.SourceRef.Resolution.Status == ResolutionStatus.Ambiguous ||
            candidate.TargetRef.Resolution.Status == ResolutionStatus.Ambiguous)
            return true;

        foreach (var signal in candidate.Signals)
        {
            if (signal is NameResolutionSignal { Status: ResolutionStatus.Ambiguous })
                return true;
        }
        return false;
    }
}
