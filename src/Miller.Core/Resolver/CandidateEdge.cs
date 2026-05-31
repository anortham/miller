using Miller.Core.Contracts;

namespace Miller.Core.Resolver;

/// <summary>
/// A candidate cross-language bridge edge emitted by a leg, carrying everything the <see cref="BridgeScorer"/> needs and
/// NO final score yet (design §4/§5). The trust contract: the scorer decides the §5 band from this payload ALONE — the
/// typed <see cref="Signals"/>, the per-side <see cref="SourceRef"/>/<see cref="TargetRef"/> resolution outcomes, and the
/// optional field-sets — with no leg-side precision logic and no re-query of the resolver. Built BEFORE any leg exists so
/// the scorer's invariants are pinned against synthetic candidates first.
/// </summary>
/// <param name="Kind">The semantic edge kind (carried for rendering; the band is NOT derived from it).</param>
/// <param name="SourceRef">
/// The edge's source endpoint, carrying its <see cref="SymbolResolver"/> outcome so ambiguity is visible in the payload.
/// </param>
/// <param name="TargetRef">The edge's target endpoint, likewise carrying its resolution outcome.</param>
/// <param name="Evidence">The edge-level locating evidence (<c>file:line</c> sites); may be empty.</param>
/// <param name="Signals">
/// The TYPED signals that fired for this edge (the closed <see cref="SignalRule"/> set). The scorer reads payloads
/// (fieldCount/Jaccard, name tier, per-side resolution status), never bare rule names. Order is leg-defined.
/// </param>
/// <param name="SourceFieldSet">The source endpoint's field shape, when a leg captured it (the Jaccard corroborator input); null otherwise.</param>
/// <param name="TargetFieldSet">The target endpoint's field shape, when captured; null otherwise.</param>
public sealed record CandidateEdge(
    BridgeKind Kind,
    EdgeRef SourceRef,
    EdgeRef TargetRef,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<Signal> Signals,
    FieldSet? SourceFieldSet = null,
    FieldSet? TargetFieldSet = null);
