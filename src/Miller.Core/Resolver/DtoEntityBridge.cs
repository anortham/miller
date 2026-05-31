using Miller.Core.Contracts;

namespace Miller.Core.Resolver;

/// <summary>
/// An AutoMapper <c>CreateMap&lt;A,B&gt;</c> use-site, already reduced from julie's <c>type_arguments</c> rows (design
/// §4 Leg 2 PRIMARY; findings 28-2). The two type names come from the call's <c>type_arguments</c> grouped by
/// <c>identifier_id</c> and read in ordinal order: ordinal 0 = copy-SOURCE, ordinal 1 = copy-DEST (a julie-tested
/// invariant). <b>That ordinal encodes the COPY direction, NOT entity-vs-DTO</b> — an inbound
/// <c>CreateMap&lt;CreateOrderRequest, Order&gt;</c> legitimately puts the DTO at ordinal 0, so the leg directs the
/// edge source→dest and never assumes ordinal 0 is the entity (the design §8 trap).
///
/// <para>The ordinal-to-record reduction (grouping <c>type_arguments</c> by <c>identifier_id</c>, filtering to the
/// <c>CreateMap</c> use-site with <c>kind='type_usage'</c>) is the graph builder's job — plan Task 8, out of this leg's
/// scope. This is the already-reduced input the pure leg consumes. <see cref="HasReverseMap"/> is set by that builder
/// when it detects a sibling <c>.ReverseMap()</c> on the same chain (julie captures no separate structured row for it),
/// so the leg can emit the inverse edge.</para>
/// </summary>
/// <param name="SourceTypeName">The ordinal-0 type (copy-source) as written; resolved to a symbol by name.</param>
/// <param name="DestTypeName">The ordinal-1 type (copy-dest) as written; resolved to a symbol by name.</param>
/// <param name="FilePath">The CreateMap use-site file (workspace-relative), for the edge evidence.</param>
/// <param name="Line">The 1-based use-site line, for the edge evidence (file:line).</param>
/// <param name="HasReverseMap">True when a sibling <c>.ReverseMap()</c> was detected — the leg emits the inverse edge.</param>
public sealed record CreateMapCandidate(
    string SourceTypeName,
    string DestTypeName,
    string FilePath,
    int Line,
    bool HasReverseMap = false);

/// <summary>
/// A manual/projection mapping use-site (design §4 Leg 2 SECONDARY — WEAK / corroborator-only): a <c>ToDto</c> /
/// <c>Select(x =&gt; new XDto{…})</c> projection that has NO structured copy-source→dest breadcrumb. The graph builder
/// (plan Task 8) pairs the source entity to the dest DTO by name-overlap over <c>code_context</c>; the pure leg consumes
/// the already-paired (source, dest) names and corroborates the pairing with a name-stem match plus a field-set Jaccard.
/// Because there is no structural breadcrumb, the scorer can only ever land this at Medium — <b>never High</b> — and a
/// 1-field shape can NOT anchor it (the <c>RevisionEntry↔DocumentRevisionDto</c> false-positive class, design §5/§8).
/// </summary>
/// <param name="SourceTypeName">The projection's source type (the entity) as written; resolved by name.</param>
/// <param name="DestTypeName">The projection's dest type (the DTO) as written; resolved by name.</param>
/// <param name="FilePath">The projection use-site file (workspace-relative), for the edge evidence.</param>
/// <param name="Line">The 1-based use-site line, for the edge evidence (file:line).</param>
public sealed record ProjectionCandidate(
    string SourceTypeName,
    string DestTypeName,
    string FilePath,
    int Line);

/// <summary>
/// The owner symbol plus the inputs <see cref="FieldSetExtractor.ExtractFields"/> needs to build its
/// <see cref="FieldSet"/>: the owning type, its child symbols (properties/fields via <c>parent_id</c>, already scoped to
/// this owner), and their annotations (e.g. <c>[JsonProperty]</c>). For a C# <c>record</c> the children may be empty —
/// the extractor parses the positional params from the owner's signature instead.
/// </summary>
/// <param name="Owner">The owning type symbol whose field shape is being built.</param>
/// <param name="Children">The owner's child symbols (already scoped by the caller via <c>parent_id</c>); may be empty.</param>
/// <param name="Annotations">Annotations on the children (e.g. <c>[JsonProperty]</c>); may be empty.</param>
public sealed record TypeFieldSource(
    SymbolDetail Owner,
    IReadOnlyList<SymbolDetail> Children,
    IReadOnlyList<SymbolAnnotation> Annotations);

/// <summary>
/// The in-memory contract collections <see cref="DtoEntityBridge"/> consumes (design §4 Leg 2; plan Task 6). Pure value
/// input — no DB, no I/O. The DB loader (plan Task 9) / graph builder (Task 8) builds these from julie rows; the leg
/// never reads SQLite.
/// </summary>
/// <param name="CreateMaps">
/// AutoMapper <c>CreateMap&lt;A,B&gt;</c> use-sites (Leg 2 PRIMARY breadcrumb): each yields a High-eligible source→dest
/// edge, plus the inverse when <see cref="CreateMapCandidate.HasReverseMap"/> is set.
/// </param>
/// <param name="Projections">
/// Manual/projection mappings (Leg 2 SECONDARY, corroborator-only): each yields at most a Medium name+field-set edge.
/// </param>
/// <param name="FieldSources">
/// Per-owner-id field-set sources (<c>symbol id → TypeFieldSource</c>). The leg builds a side's <see cref="FieldSet"/>
/// only when its resolved symbol id has an entry here; absent an entry it carries no field-set (the structural
/// CreateMap breadcrumb still stands alone). PRIMARY CreateMap edges use it opportunistically (a richer corroborator);
/// SECONDARY projections REQUIRE both sides to have a field-set or they cannot anchor.
/// </param>
public sealed record DtoEntityInput(
    IReadOnlyList<CreateMapCandidate> CreateMaps,
    IReadOnlyList<ProjectionCandidate> Projections,
    IReadOnlyDictionary<string, TypeFieldSource>? FieldSources = null);

/// <summary>
/// Leg 2 of the cross-language resolver (design §4): builds candidate <see cref="BridgeKind.MapsTo"/> edges linking a C#
/// DTO and the entity it maps to/from. PURE Miller.Core — it operates over the in-memory <see cref="DtoEntityInput"/>,
/// resolves each type name via <see cref="SymbolResolver"/>, builds field-sets via <see cref="FieldSetExtractor"/>, and
/// emits typed <see cref="CandidateEdge"/>s. It NEVER scores, bands, or re-implements confidence logic; every signal it
/// emits is decidable by <see cref="BridgeScorer"/> from the candidate payload alone (the trust contract, design §5).
///
/// <para><b>PRIMARY — AutoMapper CreateMap (High-eligible).</b> A <c>CreateMap&lt;A,B&gt;</c> use-site emits a
/// <see cref="SignalRule.CreateMap"/> structural breadcrumb on a source(ordinal 0)→dest(ordinal 1) edge. The ordinal is
/// the COPY direction, NOT entity-vs-DTO, so an inbound map is not mislabeled. A sibling <c>.ReverseMap()</c>
/// (<see cref="CreateMapCandidate.HasReverseMap"/>) emits the inverse edge. A corroborating <see cref="NameSignal"/> and
/// a <see cref="FieldSetSignal"/> ride along when the stems fold / both field-sets are available.</para>
///
/// <para><b>SECONDARY — manual/projection mapping (Medium ceiling — never High).</b> A projection carries NO structural
/// breadcrumb: the leg emits a <see cref="NameSignal"/> (when the stems fold) plus a <see cref="FieldSetSignal"/> (when
/// both field-sets are available). The scorer's name-never-sole + 1-field-can't-anchor rules then decide whether this
/// reaches Medium or is dropped — the leg never pre-judges (design §5).</para>
/// </summary>
public static class DtoEntityBridge
{
    /// <summary>
    /// Build the DTO↔entity candidate edges from <paramref name="input"/>, resolving each type name through
    /// <paramref name="resolver"/>. Returns one candidate per CreateMap (two when <c>ReverseMap</c>), plus one per
    /// projection. An unresolved/ambiguous side is reflected in the candidate's <see cref="EdgeRef.Resolution"/> + a
    /// <see cref="NameResolutionSignal"/> so the scorer (not the leg) applies the §5 drop/cap rules. The leg does NOT
    /// score; it never returns a band.
    /// </summary>
    /// <param name="input">The in-memory CreateMap + projection breadcrumbs and per-owner field sources.</param>
    /// <param name="resolver">The name resolver over the workspace's symbols.</param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="resolver"/> is null.</exception>
    public static IReadOnlyList<CandidateEdge> Resolve(DtoEntityInput input, SymbolResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(resolver);

        var edges = new List<CandidateEdge>();

        foreach (var map in input.CreateMaps)
        {
            edges.Add(BuildCreateMapEdge(map.SourceTypeName, map.DestTypeName, map, input, resolver));
            if (map.HasReverseMap)
                edges.Add(BuildCreateMapEdge(map.DestTypeName, map.SourceTypeName, map, input, resolver));
        }

        foreach (var projection in input.Projections)
            edges.Add(BuildProjectionEdge(projection, input, resolver));

        return edges;
    }

    /// <summary>
    /// Build a PRIMARY CreateMap edge from <paramref name="sourceName"/>→<paramref name="destName"/> (the direction the
    /// caller wants — forward for the declared map, swapped for the ReverseMap). Emits the CreateMap breadcrumb, both
    /// sides' NameResolution metadata, an opportunistic field-set corroborator, and a name corroborator when the stems
    /// fold.
    /// </summary>
    private static CandidateEdge BuildCreateMapEdge(
        string sourceName, string destName, CreateMapCandidate map, DtoEntityInput input, SymbolResolver resolver)
    {
        var evidence = new Evidence(map.FilePath, map.Line);

        var (sourceRef, sourceFields) = ResolveSide(sourceName, map.FilePath, input, resolver);
        var (targetRef, targetFields) = ResolveSide(destName, map.FilePath, input, resolver);

        var signals = new List<Signal>
        {
            new StructuralSignal(SignalRule.CreateMap, Present: true, evidence),
            new NameResolutionSignal(EndpointSide.Source, sourceRef.Resolution.Status, sourceRef.Resolution.MatchCount, evidence),
            new NameResolutionSignal(EndpointSide.Target, targetRef.Resolution.Status, targetRef.Resolution.MatchCount, evidence),
        };

        AddFieldSetCorroboratorIfAvailable(signals, sourceFields, targetFields, evidence);
        AddNameCorroboratorIfStemsMatch(signals, sourceName, destName, evidence);

        return new CandidateEdge(
            BridgeKind.MapsTo,
            sourceRef,
            targetRef,
            [evidence],
            signals,
            sourceFields,
            targetFields);
    }

    /// <summary>
    /// Build a SECONDARY projection edge (corroborator-only — never High): no structural breadcrumb, just the per-side
    /// NameResolution metadata plus a name corroborator (when stems fold) and a field-set corroborator (when both
    /// field-sets are available). The scorer decides whether these reach Medium or are dropped.
    /// </summary>
    private static CandidateEdge BuildProjectionEdge(
        ProjectionCandidate projection, DtoEntityInput input, SymbolResolver resolver)
    {
        var evidence = new Evidence(projection.FilePath, projection.Line);

        var (sourceRef, sourceFields) = ResolveSide(projection.SourceTypeName, projection.FilePath, input, resolver);
        var (targetRef, targetFields) = ResolveSide(projection.DestTypeName, projection.FilePath, input, resolver);

        var signals = new List<Signal>
        {
            new NameResolutionSignal(EndpointSide.Source, sourceRef.Resolution.Status, sourceRef.Resolution.MatchCount, evidence),
            new NameResolutionSignal(EndpointSide.Target, targetRef.Resolution.Status, targetRef.Resolution.MatchCount, evidence),
        };

        AddFieldSetCorroboratorIfAvailable(signals, sourceFields, targetFields, evidence);
        AddNameCorroboratorIfStemsMatch(signals, projection.SourceTypeName, projection.DestTypeName, evidence);

        return new CandidateEdge(
            BridgeKind.MapsTo,
            sourceRef,
            targetRef,
            [evidence],
            signals,
            sourceFields,
            targetFields);
    }

    /// <summary>
    /// Resolve one side's type name to an <see cref="EdgeRef"/> and, when a field source is available for the resolved
    /// symbol, build its <see cref="FieldSet"/> via <see cref="FieldSetExtractor"/> (handles C# record positional
    /// params). The field-set is null when the side did not resolve to a single symbol or has no field source.
    /// </summary>
    private static (EdgeRef Ref, FieldSet? Fields) ResolveSide(
        string typeName, string useSiteFile, DtoEntityInput input, SymbolResolver resolver)
    {
        var resolution = resolver.Resolve(typeName, preferFile: useSiteFile);

        FieldSet? fields = null;
        if (resolution.Status == ResolutionStatus.Resolved &&
            resolution.SymbolId is not null &&
            input.FieldSources is not null &&
            input.FieldSources.TryGetValue(resolution.SymbolId, out var source))
        {
            fields = FieldSetExtractor.ExtractFields(source.Owner, source.Children, source.Annotations);
        }

        var edgeRef = new EdgeRef(
            Display: LeafName(typeName),
            SymbolId: resolution.SymbolId,
            FilePath: useSiteFile,
            Resolution: resolution);

        return (edgeRef, fields);
    }

    /// <summary>
    /// Add a <see cref="FieldSetSignal"/> corroborator when BOTH sides have a field-set, computed by
    /// <see cref="FieldSetSimilarity.Compare"/> (Jaccard over field names + the anchoring min field count). The scorer
    /// alone decides whether the count/Jaccard is sufficient to corroborate (a 1-field shape can't anchor) — the leg
    /// always emits the real numbers and never pre-filters.
    /// </summary>
    private static void AddFieldSetCorroboratorIfAvailable(
        List<Signal> signals, FieldSet? sourceFields, FieldSet? targetFields, Evidence evidence)
    {
        if (sourceFields is null || targetFields is null)
            return;

        signals.Add(FieldSetSimilarity.Compare(sourceFields, targetFields, evidence));
    }

    /// <summary>
    /// Add a corroborating <see cref="NameSignal"/> when the two type names fold to the same canonical stem (e.g.
    /// <c>Account</c> ⇄ <c>AccountDto</c>). Exact when the raw leaf names are already identical (case-folded);
    /// <see cref="NameTier.Affix"/> when they only matched after suffix/singular-plural folding. This only RAISES an
    /// already-anchored edge (it is never the sole signal — the scorer enforces that).
    /// </summary>
    private static void AddNameCorroboratorIfStemsMatch(
        List<Signal> signals, string sourceName, string destName, Evidence evidence)
    {
        var sourceLeaf = LeafName(sourceName);
        var destLeaf = LeafName(destName);
        var sourceStem = NameNormalizer.Stem(sourceLeaf);
        var destStem = NameNormalizer.Stem(destLeaf);
        if (sourceStem.Length == 0 || destStem.Length == 0 || !string.Equals(sourceStem, destStem, StringComparison.Ordinal))
            return;

        var tier = string.Equals(sourceLeaf, destLeaf, StringComparison.OrdinalIgnoreCase)
            ? NameTier.Exact
            : NameTier.Affix;
        signals.Add(new NameSignal(tier, evidence));
    }

    /// <summary>The leaf (simple) name of a possibly-qualified type name (<c>Core.Data.Account</c> → <c>Account</c>).</summary>
    private static string LeafName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return typeName;
        int dot = typeName.LastIndexOf('.');
        return (dot >= 0 && dot < typeName.Length - 1) ? typeName[(dot + 1)..] : typeName;
    }
}
