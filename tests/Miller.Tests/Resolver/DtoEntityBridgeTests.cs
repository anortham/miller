using Miller.Core.Contracts;
using Miller.Core.Resolver;
using Xunit;

namespace Miller.Tests.Resolver;

/// <summary>
/// Pins design §4 Leg 2 (<see cref="DtoEntityBridge"/>): C# DTO ⇄ entity edges (<see cref="BridgeKind.MapsTo"/>).
/// Every fixture is hand-built in-memory (NO julie, NO I/O), so these are fast-suite tests. The leg only builds
/// candidates and delegates ALL confidence to <see cref="BridgeScorer"/>; the tests assert the resulting band/score and
/// the load-bearing traps:
/// <list type="bullet">
/// <item>a <c>CreateMap&lt;A,B&gt;</c> use-site yields a High <see cref="SignalRule.CreateMap"/> edge directed
///   copy-source(ord 0)→copy-dest(ord 1) — NOT entity→DTO, so an inbound <c>CreateMap&lt;XRequest, Entity&gt;</c> is not
///   mislabeled (findings 28-2 + design §4);</item>
/// <item>a <c>ReverseMap</c> emits the inverse edge too;</item>
/// <item>a manual/projection mapping (name + a rich field-set) is corroborated to Medium — never High — and a
///   1-field shape can NOT anchor it (the <c>RevisionEntry↔DocumentRevisionDto</c> false-positive class);</item>
/// <item>a record-DTO's positional params produce the field-set the Jaccard reads;</item>
/// <item>an ambiguous side is never High; an unresolved side yields no edge.</item>
/// </list>
/// </summary>
public sealed class DtoEntityBridgeTests
{
    // ---- in-memory fixture builders ----------------------------------------------------------------------------
    // A type owner plus its property children: the test seeds these into BOTH the SymbolResolver (so the name resolves)
    // and the FieldSources map (so the leg can build the field-set via FieldSetExtractor), exactly as a Task-8 builder
    // would. SymbolDetail ctor order: (Id, Name, Kind, FilePath, Signature, Namespace, IsTest, ParentClassName).

    private sealed record OwnerSpec(
        string Id, string Name, string? Namespace, string Kind, string? Signature, string File,
        IReadOnlyList<(string Name, string Type)> Props);

    private static OwnerSpec Owner(
        string id, string name, string? ns, string kind = "class", string? signature = null,
        string file = "Domain/Types.cs", params (string Name, string Type)[] props) =>
        new(id, name, ns, kind, signature, file, props);

    private static SymbolDetail OwnerSymbol(OwnerSpec o) =>
        new(o.Id, o.Name, o.Kind, o.File, o.Signature ?? $"public {o.Kind} {o.Name}", o.Namespace, false, null);

    private static SymbolDetail PropSymbol(OwnerSpec o, (string Name, string Type) p) =>
        new($"{o.Id}.{p.Name}", p.Name, "property", o.File, $"{p.Type} {p.Name}", o.Namespace, false, null);

    // Build the (resolver, input) pair from owner specs + the CreateMap/projection candidates. Every owner and its
    // properties go into the resolver's symbol set; each owner with properties gets a TypeFieldSource entry.
    private static (SymbolResolver Resolver, DtoEntityInput Input) Build(
        IReadOnlyList<OwnerSpec> owners,
        IReadOnlyList<CreateMapCandidate>? maps = null,
        IReadOnlyList<ProjectionCandidate>? projections = null)
    {
        var symbols = new List<SymbolDetail>();
        var fieldSources = new Dictionary<string, TypeFieldSource>(StringComparer.Ordinal);

        foreach (var o in owners)
        {
            var ownerSymbol = OwnerSymbol(o);
            symbols.Add(ownerSymbol);

            var children = new List<SymbolDetail>();
            foreach (var p in o.Props)
            {
                var child = PropSymbol(o, p);
                children.Add(child);
                symbols.Add(child);
            }

            // A record carries its fields in the signature (no property children); a class/interface carries them as
            // children. Either way the owner gets a field source so the leg can build its field-set.
            fieldSources[o.Id] = new TypeFieldSource(ownerSymbol, children, []);
        }

        var resolver = new SymbolResolver(symbols);
        var input = new DtoEntityInput(maps ?? [], projections ?? [], fieldSources);
        return (resolver, input);
    }

    // CreateMapCandidate ctor order: (SourceTypeName, DestTypeName, FilePath, Line, HasReverseMap).
    private static CreateMapCandidate Map(
        string source, string dest, bool reverse = false, string file = "Mapping/Profile.cs", int line = 24) =>
        new(source, dest, file, line, reverse);

    // ProjectionCandidate ctor order: (SourceTypeName, DestTypeName, FilePath, Line).
    private static ProjectionCandidate Projection(
        string source, string dest, string file = "Services/Mapper.cs", int line = 50) =>
        new(source, dest, file, line);

    // ---- PRIMARY: CreateMap<A,B> -------------------------------------------------------------------------------

    [Fact]
    public void Resolve_CreateMap_EmitsHighMapsToEdge_SourceToDest()
    {
        // CreateMap<Account, AccountDto>: ordinal 0 = copy-source (entity), ordinal 1 = copy-dest (DTO). The edge is
        // directed source->dest, anchored by the CreateMap structural breadcrumb => High.
        var (resolver, input) = Build(
            [Owner("e1", "Account", "Domain"), Owner("d1", "AccountDto", "Dtos")],
            maps: [Map("Account", "AccountDto")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));

        Assert.Equal(BridgeKind.MapsTo, edge.Kind);
        Assert.Equal("Account", edge.SourceRef.Display);
        Assert.Equal("AccountDto", edge.TargetRef.Display);
        Assert.Equal("e1", edge.SourceRef.SymbolId);
        Assert.Equal("d1", edge.TargetRef.SymbolId);
        Assert.Contains(edge.Signals, s => s is StructuralSignal { Rule: SignalRule.CreateMap, Present: true });

        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.True(scored.Score >= 0.90);
        Assert.False(scored.HasAmbiguousName);
    }

    [Fact]
    public void Resolve_CreateMap_QualifiedTypeNames_ResolveByLeafName()
    {
        // Real MyraNext rows are namespace-qualified (Core.Reporting.Data.Account / ResponseObjects.Account). The leaf
        // name resolves via the embedded qualifier tie-break; the Display is the leaf name, not the qualified string.
        var (resolver, input) = Build(
            [Owner("e1", "Account", "Core.Reporting.Data"), Owner("d1", "Account", "ResponseObjects")],
            maps: [Map("Core.Reporting.Data.Account", "ResponseObjects.Account")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));

        Assert.Equal("Account", edge.SourceRef.Display);
        Assert.Equal("Account", edge.TargetRef.Display);
        Assert.Equal("e1", edge.SourceRef.SymbolId);
        Assert.Equal("d1", edge.TargetRef.SymbolId);

        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
    }

    [Fact]
    public void Resolve_CreateMap_InboundRequestToEntity_DirectionIsCopyFlow_NotEntityVsDto()
    {
        // An INBOUND map CreateMap<CreateOrderRequest, Order>: the DTO is at ordinal 0, the entity at ordinal 1. The
        // edge must follow the COPY direction (source=request, dest=entity) and must NOT be flipped to entity->DTO.
        // (Hardcoding entity=ordinal-0 would emit a confident reversed edge — the design §8 trap.)
        var (resolver, input) = Build(
            [Owner("req", "CreateOrderRequest", "Requests"), Owner("ord", "Order", "Domain")],
            maps: [Map("CreateOrderRequest", "Order")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));

        Assert.Equal("CreateOrderRequest", edge.SourceRef.Display);
        Assert.Equal("Order", edge.TargetRef.Display);
        Assert.Equal("req", edge.SourceRef.SymbolId);
        Assert.Equal("ord", edge.TargetRef.SymbolId);
    }

    [Fact]
    public void Resolve_CreateMap_ZeroSharedFields_StillHigh()
    {
        // CreateMap is a structural breadcrumb: even when the two shapes share NO fields, the breadcrumb alone is High.
        // (The field-set is a corroborator, never a requirement.) Source and dest are seeded with disjoint properties.
        var (resolver, input) = Build(
            [
                Owner("e1", "Account", "Domain", props: [("AccountNumber", "string"), ("Balance", "decimal")]),
                Owner("d1", "AccountDto", "Dtos", props: [("DisplayName", "string"), ("Tier", "int")]),
            ],
            maps: [Map("Account", "AccountDto")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));

        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
    }

    [Fact]
    public void Resolve_CreateMap_ReverseMap_EmitsBothDirections()
    {
        // CreateMap<Account, AccountDto>().ReverseMap() => the forward edge Account->AccountDto AND the inverse
        // AccountDto->Account, both High, both CreateMap breadcrumbs.
        var (resolver, input) = Build(
            [Owner("e1", "Account", "Domain"), Owner("d1", "AccountDto", "Dtos")],
            maps: [Map("Account", "AccountDto", reverse: true)]);

        var edges = DtoEntityBridge.Resolve(input, resolver);

        Assert.Equal(2, edges.Count);
        Assert.Contains(edges, e => e.SourceRef.Display == "Account" && e.TargetRef.Display == "AccountDto");
        Assert.Contains(edges, e => e.SourceRef.Display == "AccountDto" && e.TargetRef.Display == "Account");
        Assert.All(edges, e =>
        {
            Assert.Contains(e.Signals, s => s is StructuralSignal { Rule: SignalRule.CreateMap, Present: true });
            var scored = BridgeScorer.Score(e);
            Assert.NotNull(scored);
            Assert.Equal(ConfidenceBand.High, scored!.Band);
        });
    }

    [Fact]
    public void Resolve_CreateMap_StemMatch_AddsNameCorroborator_MultiSignalHigh()
    {
        // Account entity vs AccountDto DTO: the canonical stems fold together (Dto suffix stripped), so a NameSignal
        // corroborator fires alongside the CreateMap breadcrumb — a multi-signal High edge.
        var (resolver, input) = Build(
            [Owner("e1", "Account", "Domain"), Owner("d1", "AccountDto", "Dtos")],
            maps: [Map("Account", "AccountDto")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));

        Assert.Contains(edge.Signals, s => s is NameSignal);

        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.True(scored.IsMultiSignal);
    }

    [Fact]
    public void Resolve_CreateMap_UnrelatedNames_NoNameSignal()
    {
        // CreateMap<Permission, SecurityPermission>: the stems do NOT fold (the leg never fabricates a name match), so
        // no NameSignal — still a valid High edge on the CreateMap breadcrumb alone, but single-signal.
        var (resolver, input) = Build(
            [Owner("e1", "Permission", "Domain"), Owner("d1", "SecurityPermission", "Dtos")],
            maps: [Map("Permission", "SecurityPermission")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));

        Assert.DoesNotContain(edge.Signals, s => s is NameSignal);

        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.False(scored.IsMultiSignal);
    }

    [Fact]
    public void Resolve_CreateMap_AmbiguousSide_NeverHigh()
    {
        // Two same-named entities in different namespaces/projects, an unqualified source name + no usable hint =>
        // ambiguous => the edge is capped at Medium and flagged, even though the CreateMap breadcrumb is present.
        var (resolver, input) = Build(
            [
                Owner("e1", "Account", "Core.Reporting.Data", file: "ServiceA/Account.cs"),
                Owner("e2", "Account", "Billing.Data", file: "ServiceB/Account.cs"),
                Owner("d1", "AccountDto", "Dtos"),
            ],
            // Map use-site is in a third project so the file tie-break cannot pick one of the two Accounts.
            maps: [Map("Account", "AccountDto", file: "Mapping/Profile.cs")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));
        Assert.Equal(ResolutionStatus.Ambiguous, edge.SourceRef.Resolution.Status);
        Assert.Contains(edge.Signals,
            s => s is NameResolutionSignal { Endpoint: EndpointSide.Source, Status: ResolutionStatus.Ambiguous });

        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.Medium, scored!.Band);
        Assert.True(scored.HasAmbiguousName);
    }

    [Fact]
    public void Resolve_CreateMap_UnresolvedSide_NoEdge()
    {
        // The dest type names a DTO that does not exist as a symbol => unresolved => the leg emits a candidate carrying
        // the Unresolved status, and the scorer drops it (no symbol to point at).
        var (resolver, input) = Build(
            [Owner("e1", "Account", "Domain")],
            maps: [Map("Account", "GhostDto")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));
        Assert.Equal(ResolutionStatus.Unresolved, edge.TargetRef.Resolution.Status);

        Assert.Null(BridgeScorer.Score(edge));
    }

    [Fact]
    public void Resolve_MultipleCreateMaps_OneEdgePerMap()
    {
        var (resolver, input) = Build(
            [
                Owner("e1", "Account", "Domain"), Owner("d1", "AccountDto", "Dtos"),
                Owner("e2", "Permission", "Domain"), Owner("d2", "PermissionDto", "Dtos"),
            ],
            maps: [Map("Account", "AccountDto"), Map("Permission", "PermissionDto")]);

        var edges = DtoEntityBridge.Resolve(input, resolver);

        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal(BridgeKind.MapsTo, e.Kind));
        Assert.Contains(edges, e => e.SourceRef.Display == "Account");
        Assert.Contains(edges, e => e.SourceRef.Display == "Permission");
    }

    // ---- SECONDARY: manual / projection mapping (name + field-set) ---------------------------------------------

    [Fact]
    public void Resolve_Projection_NameAndRichFieldSet_IsMedium_NeverHigh()
    {
        // A manual mapping with no structural breadcrumb: name stems fold (Order/OrderDto) AND the field-sets overlap
        // richly (>=2 shared fields). The scorer lands this at Medium — a projection is NEVER High (no breadcrumb).
        var (resolver, input) = Build(
            [
                Owner("e1", "Order", "Domain", props: [("Id", "int"), ("CustomerName", "string"), ("Total", "decimal")]),
                Owner("d1", "OrderDto", "Dtos", props: [("Id", "int"), ("CustomerName", "string"), ("Total", "decimal")]),
            ],
            projections: [Projection("Order", "OrderDto")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));
        Assert.Equal(BridgeKind.MapsTo, edge.Kind);
        Assert.Contains(edge.Signals, s => s is NameSignal);
        Assert.Contains(edge.Signals, s => s is FieldSetSignal { FieldCount: >= 2, Jaccard: > 0.0 });
        // A projection carries NO structural breadcrumb.
        Assert.DoesNotContain(edge.Signals, s => s is StructuralSignal { Present: true });

        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.Medium, scored!.Band);
    }

    [Fact]
    public void Resolve_Projection_CarriesBothFieldSets_WithRealJaccard()
    {
        // The candidate carries SourceFieldSet/TargetFieldSet (the §5 Jaccard inputs) AND the FieldSetSignal computed
        // from them. With 2 of 3 fields shared the Jaccard is 0.5 (|{Id,Name}| / |{Id,Name,Extra,Other}|).
        var (resolver, input) = Build(
            [
                Owner("e1", "Widget", "Domain", props: [("Id", "int"), ("Name", "string"), ("Extra", "string")]),
                Owner("d1", "WidgetDto", "Dtos", props: [("Id", "int"), ("Name", "string"), ("Other", "string")]),
            ],
            projections: [Projection("Widget", "WidgetDto")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));

        Assert.NotNull(edge.SourceFieldSet);
        Assert.NotNull(edge.TargetFieldSet);
        Assert.Equal(3, edge.SourceFieldSet!.Count);
        Assert.Equal(3, edge.TargetFieldSet!.Count);

        var fs = Assert.IsType<FieldSetSignal>(Assert.Single(edge.Signals, s => s is FieldSetSignal));
        Assert.Equal(3, fs.FieldCount);
        // {Id,Name} shared; union {Id,Name,Extra,Other} => 2/4 = 0.5.
        Assert.Equal(0.5, fs.Jaccard, precision: 5);
    }

    [Fact]
    public void Resolve_Projection_RecordDto_PositionalParams_ProduceTheFieldSet()
    {
        // A record DTO has NO property children — its field-set comes from the positional params in the signature. The
        // entity is a class with matching properties; the Jaccard must read the record params correctly.
        var (resolver, input) = Build(
            [
                Owner("e1", "DocumentRevision", "Domain",
                    props: [("Id", "int"), ("Title", "string"), ("CreatedAt", "DateTime")]),
                Owner("d1", "DocumentRevisionDto", "Dtos", kind: "record",
                    signature: "public record DocumentRevisionDto(int Id, string Title, DateTime CreatedAt)"),
            ],
            projections: [Projection("DocumentRevision", "DocumentRevisionDto")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));

        // The record DTO's field-set is the 3 positional params (not an empty set from a naive child query).
        Assert.NotNull(edge.TargetFieldSet);
        Assert.Equal(3, edge.TargetFieldSet!.Count);
        Assert.Contains(edge.TargetFieldSet.Fields, f => f.Name == "Title");

        // All 3 fields shared => Jaccard 1.0, count 3: a valid corroborator. With the name match => Medium.
        var fs = Assert.IsType<FieldSetSignal>(Assert.Single(edge.Signals, s => s is FieldSetSignal));
        Assert.Equal(3, fs.FieldCount);
        Assert.Equal(1.0, fs.Jaccard, precision: 5);

        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.Medium, scored!.Band);
    }

    [Fact]
    public void Resolve_Projection_OneFieldShape_DoesNotAnchor_NoEdge()
    {
        // The RevisionEntry↔DocumentRevisionDto false-positive class: a 1-field shape can NOT corroborate a name match
        // into an edge. A projection whose only overlap is a single shared field yields NO edge from the scorer.
        var (resolver, input) = Build(
            [
                Owner("e1", "RevisionEntry", "Domain", props: [("Id", "int")]),
                Owner("d1", "RevisionEntryDto", "Dtos", props: [("Id", "int")]),
            ],
            projections: [Projection("RevisionEntry", "RevisionEntryDto")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));
        // The leg still emits the FieldSetSignal carrying fieldCount=1 — the scorer is the one that refuses it.
        Assert.Contains(edge.Signals, s => s is FieldSetSignal { FieldCount: 1 });

        Assert.Null(BridgeScorer.Score(edge));
    }

    [Fact]
    public void Resolve_Projection_NameMatchButNoFieldOverlap_NoEdge()
    {
        // A projection whose names fold but whose shapes share NO fields: the name finisher is never sole, and a
        // 0-overlap field-set is no corroborator => the scorer drops it (no confident garbage on name alone).
        var (resolver, input) = Build(
            [
                Owner("e1", "Account", "Domain", props: [("AccountNumber", "string"), ("Balance", "decimal")]),
                Owner("d1", "AccountDto", "Dtos", props: [("DisplayName", "string"), ("Tier", "int")]),
            ],
            projections: [Projection("Account", "AccountDto")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));

        Assert.Null(BridgeScorer.Score(edge));
    }

    [Fact]
    public void Resolve_Projection_NoFieldSources_NoCorroborator_NoEdge()
    {
        // When neither side has a field source (e.g. the builder could not scope children), a projection has only a
        // name match — never sole — so the scorer drops it. The leg emits the name signal but no field-set signal.
        var resolver = new SymbolResolver(
        [
            new SymbolDetail("e1", "Account", "class", "Domain/Account.cs", "public class Account", "Domain", false, null),
            new SymbolDetail("d1", "AccountDto", "class", "Dtos/AccountDto.cs", "public class AccountDto", "Dtos", false, null),
        ]);
        var input = new DtoEntityInput([], [Projection("Account", "AccountDto")], FieldSources: null);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));
        Assert.Contains(edge.Signals, s => s is NameSignal);
        Assert.DoesNotContain(edge.Signals, s => s is FieldSetSignal);

        Assert.Null(BridgeScorer.Score(edge));
    }

    [Fact]
    public void Resolve_Projection_UnresolvedSide_NoEdge()
    {
        var (resolver, input) = Build(
            [Owner("e1", "Account", "Domain", props: [("Id", "int"), ("Name", "string")])],
            projections: [Projection("Account", "GhostDto")]);

        var edge = Assert.Single(DtoEntityBridge.Resolve(input, resolver));
        Assert.Equal(ResolutionStatus.Unresolved, edge.TargetRef.Resolution.Status);

        Assert.Null(BridgeScorer.Score(edge));
    }

    // ---- guards ------------------------------------------------------------------------------------------------

    [Fact]
    public void Resolve_NullInput_Throws()
    {
        var resolver = new SymbolResolver([]);
        Assert.Throws<ArgumentNullException>(() => DtoEntityBridge.Resolve(null!, resolver));
    }

    [Fact]
    public void Resolve_NullResolver_Throws()
    {
        var input = new DtoEntityInput([], []);
        Assert.Throws<ArgumentNullException>(() => DtoEntityBridge.Resolve(input, null!));
    }
}
