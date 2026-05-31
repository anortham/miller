namespace Miller.Core.Resolver;

/// <summary>
/// Which side of a candidate edge a per-side signal (notably <c>NameResolution</c>) belongs to. The scorer reads the
/// <see cref="Source"/>/<see cref="Target"/> resolution status off the payload to enforce ambiguous-name-never-High
/// without re-querying the resolver.
/// </summary>
public enum EndpointSide
{
    /// <summary>The edge's source ref (<see cref="CandidateEdge.SourceRef"/>).</summary>
    Source,

    /// <summary>The edge's target ref (<see cref="CandidateEdge.TargetRef"/>).</summary>
    Target,
}

/// <summary>
/// One endpoint of a candidate edge. Carries the <see cref="SymbolResolver"/> outcome (<see cref="Resolution"/>) so
/// the candidate payload makes name ambiguity visible WITHOUT the scorer re-querying the resolver — the
/// ambiguous-name-never-High and unresolved-no-edge rules (design §5) are decidable from this alone. A leg builds the
/// ref from a name resolution; the chosen <see cref="SymbolId"/> mirrors <see cref="NameResolution.SymbolId"/> (set
/// only when <see cref="ResolutionStatus.Resolved"/>).
/// </summary>
/// <param name="Display">
/// The human-facing name of this endpoint as written at the use-site (e.g. <c>ApplicationUser</c>, <c>api/appsettings/{}</c>,
/// or a DTO/entity type name) — what the <c>trace</c> tool renders. Never null; a leg always knows the textual name.
/// </param>
/// <param name="SymbolId">
/// The resolved symbol id, or null when the name resolved <see cref="ResolutionStatus.Ambiguous"/> or
/// <see cref="ResolutionStatus.Unresolved"/> (or this endpoint is a non-symbol node like a DB table / route).
/// </param>
/// <param name="FilePath">The endpoint symbol's file (workspace-relative) when known, for evidence; null otherwise.</param>
/// <param name="Resolution">
/// The <see cref="SymbolResolver"/> outcome for this side — the source of the per-side <c>NameResolution</c> signal and
/// the scorer's ambiguity gate. For a non-symbol endpoint that needs no name resolution (a DB table named by EF
/// convention, a literal route), a leg supplies a trivially <see cref="ResolutionStatus.Resolved"/> outcome.
/// </param>
public sealed record EdgeRef(
    string Display,
    string? SymbolId,
    string? FilePath,
    NameResolution Resolution);
