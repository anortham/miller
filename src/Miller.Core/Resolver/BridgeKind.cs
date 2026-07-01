namespace Miller.Core.Resolver;

/// <summary>
/// The closed set of cross-language bridge edge kinds the legs emit (design §4). The scorer does NOT branch on the
/// kind to assign a band — the band is decided from the typed <c>signals[]</c> payload alone — but the kind is the
/// edge's semantic label and is carried for the <c>trace</c> tool's rendering (<c>UserDto ←CreateMap─ ApplicationUser</c>).
/// </summary>
public enum BridgeKind
{
    /// <summary>
    /// A C# entity ↔ DB table edge (Leg 3): <c>CsEntity —stored_in→ DbTable</c>, anchored by a <c>DbSet&lt;T&gt;</c>
    /// property (table = property name) or, opportunistically, a Dapper <c>FROM</c> literal.
    /// </summary>
    StoredIn,

    /// <summary>
    /// A C# DTO ↔ entity edge (Leg 2): <c>source —maps_to→ dest</c>, anchored by an AutoMapper <c>CreateMap&lt;A,B&gt;</c>
    /// (copy-source→copy-dest), a <c>ToDto</c> extension method, or a (corroborator-only) inline projection.
    /// </summary>
    MapsTo,

    /// <summary>A TS client call ↔ C# endpoint edge (Leg 1): <c>TsCall —hits→ Endpoint</c> by matched (verb, route).</summary>
    Hits,

    /// <summary>A framework route reference ↔ file route edge: <c>RouteReference —navigates_to→ FileRoute</c>.</summary>
    NavigatesTo,

    /// <summary>An endpoint ↔ response DTO edge (Leg 1): <c>Endpoint —responds→ CsDto</c> from the unwrapped return type.</summary>
    Responds,

    /// <summary>A call/endpoint ↔ request DTO edge (Leg 1): <c>—consumes→ CsDto</c> from the <c>[FromBody]</c>/param type.</summary>
    Consumes,

    /// <summary>A name-finisher edge (the "safe finisher"): a canonical-stem match between a TS type and a C# DTO/entity.</summary>
    NameMatch,
}
