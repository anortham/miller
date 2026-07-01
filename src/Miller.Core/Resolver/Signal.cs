using Miller.Core.Contracts;

namespace Miller.Core.Resolver;

/// <summary>
/// The closed set of scoring rules a candidate edge can carry, defined HERE (plan Task 4) BEFORE any leg emits so every
/// design §5 band/invariant is decidable by the <see cref="BridgeScorer"/> from the candidate payload alone — no
/// leg-side precision logic, no Task-5 retrofit. Each rule maps to a concrete <see cref="Signal"/> subtype.
/// </summary>
public enum SignalRule
{
    // ---- structural breadcrumbs (the §5 High anchors) ----------------------------------------------------------

    /// <summary>An AutoMapper <c>CreateMap&lt;A,B&gt;</c> use-site fired (Leg 2). Explicit structural breadcrumb ⇒ High-eligible.</summary>
    CreateMap,

    /// <summary>A DbContext <c>DbSet&lt;T&gt;</c> property fired (Leg 3). Explicit structural breadcrumb ⇒ High-eligible.</summary>
    DbSetProperty,

    /// <summary>A (verb, route) endpoint match with a KNOWN verb after token expansion (Leg 1). High-eligible.</summary>
    RouteVerbMatch,

    /// <summary>A Next.js route reference matched a file-route template. Explicit structural breadcrumb ⇒ High-eligible.</summary>
    RouteReferenceMatch,

    /// <summary>A route-only match — the route lines up but the client verb was unknown (Leg 1). Medium, never High.</summary>
    RouteOnlyMatch,

    /// <summary>The endpoint return type unwrapped to a named user DTO (Leg 1 <c>responds→</c>). Structural breadcrumb.</summary>
    ReturnTypeDto,

    /// <summary>A request DTO recovered from a <c>[FromBody]</c>/parameter type (Leg 1 <c>consumes→</c>). Structural breadcrumb.</summary>
    FromBodyDto,

    /// <summary>A Dapper <c>FROM &lt;table&gt;</c> literal fired (Leg 3). High-eligible ONLY when a real FROM is present.</summary>
    DapperFrom,

    // ---- corroborators (never a sole anchor) -------------------------------------------------------------------

    /// <summary>A field-set Jaccard overlap (carries fieldCount + Jaccard). NEVER a sole signal; only raises an existing edge.</summary>
    FieldSetJaccard,

    /// <summary>A canonical-name-stem match (carries the exact|affix tier). The "safe finisher"; never the sole High signal.</summary>
    NameMatch,

    // ---- per-side metadata (not itself a positive corroborator) ------------------------------------------------

    /// <summary>
    /// The <see cref="SymbolResolver"/> outcome for one edge side (carries endpoint + status + matchCount). Backs the
    /// ambiguous-name-never-High and unresolved-no-edge rules; it is NOT a positive corroborator — it never raises a band.
    /// </summary>
    NameResolution,
}

/// <summary>
/// A typed scoring signal on a candidate edge: <c>{rule, value, evidence}</c> with the value carried in the concrete
/// subtype's payload, NOT as a bare rule name (design §5). The typed payload is what lets the scorer enforce
/// "1-field can't anchor" (it reads <see cref="FieldSetSignal.FieldCount"/>) and "ambiguous-name never High" (it reads
/// <see cref="NameResolutionSignal.Status"/>) from the candidate alone. Closed hierarchy — every variant is one of the
/// sealed subclasses below.
/// </summary>
/// <param name="Rule">Which <see cref="SignalRule"/> this signal carries.</param>
/// <param name="Evidence">A <c>file:line</c> the signal can be traced to, or null when the signal has no single site.</param>
public abstract record Signal(SignalRule Rule, Evidence? Evidence);

/// <summary>
/// A structural breadcrumb signal carrying only a present/absent boolean + its evidence: the CreateMap, DbSetProperty,
/// RouteVerbMatch, RouteReferenceMatch, RouteOnlyMatch, ReturnTypeDto, FromBodyDto, or DapperFrom rules.
/// <see cref="Present"/> is true when the breadcrumb actually fired; a leg only emits a present=false signal as a
/// deliberate "considered-but-absent" record (the scorer treats a non-present structural signal as no anchor).
/// </summary>
/// <param name="Rule">The structural rule (must be one of the structural <see cref="SignalRule"/> values).</param>
/// <param name="Present">True when the breadcrumb fired.</param>
/// <param name="Evidence">The breadcrumb's <c>file:line</c>, or null.</param>
public sealed record StructuralSignal(SignalRule Rule, bool Present, Evidence? Evidence = null)
    : Signal(Rule, Evidence);

/// <summary>
/// A field-set overlap corroborator (<see cref="SignalRule.FieldSetJaccard"/>) carrying the overlap shape so the scorer
/// can enforce the design §5 invariants from the payload: <see cref="FieldCount"/> (the smaller of the two compared
/// shapes — a 1-field/generic shape can NOT anchor) and <see cref="Jaccard"/> (the field-name overlap ratio). This is
/// NEVER a sole signal; it only raises an edge that already has a structural or name signal.
/// </summary>
/// <param name="FieldCount">
/// The anchoring field count — the MIN of the two compared field-sets' counts. The scorer refuses a 1-field shape as a
/// corroborator (a 1-field generic wrapper Jaccard-matches everything), so this is the value the §5 rule reads.
/// </param>
/// <param name="Jaccard">The field-name Jaccard similarity in [0,1] (|A∩B| / |A∪B|).</param>
/// <param name="Evidence">The compared owner's <c>file:line</c>, or null.</param>
public sealed record FieldSetSignal(int FieldCount, double Jaccard, Evidence? Evidence = null)
    : Signal(SignalRule.FieldSetJaccard, Evidence);

/// <summary>The tier of a canonical-name-stem match: an exact stem equality, or an equality only after affix folding.</summary>
public enum NameTier
{
    /// <summary>The two names share the exact canonical stem (the stronger name tier).</summary>
    Exact,

    /// <summary>The names match only after affix folding (singular↔plural, role-suffix strip) — the weaker tier.</summary>
    Affix,
}

/// <summary>
/// A canonical-name-stem corroborator (<see cref="SignalRule.NameMatch"/>) carrying the match <see cref="Tier"/>
/// (<see cref="NameTier.Exact"/> vs <see cref="NameTier.Affix"/>). The "safe finisher": pairs with a corroborator for
/// Medium, but is never the sole signal for a High edge (design §4 finisher / §5 bands).
/// </summary>
/// <param name="Tier">Whether the stems matched exactly or only after affix folding.</param>
/// <param name="Evidence">The matched name's <c>file:line</c>, or null.</param>
public sealed record NameSignal(NameTier Tier, Evidence? Evidence = null)
    : Signal(SignalRule.NameMatch, Evidence);

/// <summary>
/// A per-side <see cref="SymbolResolver"/> outcome (<see cref="SignalRule.NameResolution"/>): the
/// <see cref="EndpointSide"/> it describes, its <see cref="ResolutionStatus"/>, and the candidate
/// <see cref="MatchCount"/>. The <see cref="BridgeScorer"/> reads <see cref="Status"/> off the payload to enforce both
/// resolution rules directly: an <see cref="ResolutionStatus.Ambiguous"/> side can never be High, and an
/// <see cref="ResolutionStatus.Unresolved"/> side yields no edge. The scorer is the single enforcement point for both
/// (a future builder MAY pre-filter an unresolved side as an optimization, but is not required to). It is metadata, not
/// a positive corroborator — it never raises a band.
/// </summary>
/// <param name="Endpoint">Which edge side this resolution describes.</param>
/// <param name="Status">The resolver's outcome for that side.</param>
/// <param name="MatchCount">How many name candidates the resolver considered (0 unresolved, 1 resolved, ≥2 before tie-break).</param>
/// <param name="Evidence">The use-site <c>file:line</c>, or null.</param>
public sealed record NameResolutionSignal(
    EndpointSide Endpoint,
    ResolutionStatus Status,
    int MatchCount,
    Evidence? Evidence = null)
    : Signal(SignalRule.NameResolution, Evidence);
