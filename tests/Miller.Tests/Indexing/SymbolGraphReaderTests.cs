using Miller.Core.Graph;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the D2 edge-load + name-resolution layer against the synthesized julie schema. These are Miller's
/// read-CONTRACT tests for the dependency edge model, NOT a re-test of julie extraction. They assert the
/// resolved+unioned <see cref="GraphEdge"/> list the reader produces:
/// <list type="bullet">
/// <item>a precise <c>relationships</c> row becomes a by-id edge verbatim;</item>
/// <item>an <c>identifiers</c> row resolves its <c>name</c> to every indexed symbol of that name and emits
///   <c>containing → each id</c> (homonyms over-approximate — both ids, D2 honesty clause);</item>
/// <item>a NULL <c>containing_symbol_id</c> identifier is dropped (no source node);</item>
/// <item>a name that resolves to NO indexed symbol (external/library ref) is dropped (bounds the graph);</item>
/// <item>a name whose fallback resolution is too ambiguous is dropped (bounds explosive homonym fan-out);</item>
/// <item>a self-edge (a name resolving back to its own container) is dropped defensively.</item>
/// </list>
/// The name resolver is supplied by the caller (the index name map in production); these tests pass a small
/// in-memory map so the reader's resolution + drop discipline is exercised without an index build.
/// </summary>
public sealed class SymbolGraphReaderTests
{
    // Opaque ids for the synthetic graph (32-char hex, like julie's MD5 scheme).
    private const string ProcessId = "00000000000000000000000000000001";
    private const string ValidateId = "00000000000000000000000000000002";
    private const string HandleId = "00000000000000000000000000000003";
    // Two symbols sharing the name "Log" — the homonym case (over-approximation).
    private const string LogAId = "0000000000000000000000000000000a";
    private const string LogBId = "0000000000000000000000000000000b";

    // A name→ids resolver mirroring what MillerRepositoryIndex.FindByName provides in production.
    private static Func<string, IReadOnlyList<string>> ResolverFor(
        IReadOnlyDictionary<string, IReadOnlyList<string>> map) =>
        name => map.TryGetValue(name, out var ids) ? ids : Array.Empty<string>();

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NameMap =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Process"] = new[] { ProcessId },
            ["Validate"] = new[] { ValidateId },
            ["Handle"] = new[] { HandleId },
            ["Log"] = new[] { LogAId, LogBId }, // homonym
        };

    // A fixture carrying the symbols the ids name (so the FKs resolve), plus the supplied relationships and
    // identifiers. Symbol names/ids match NameMap.
    private static JulieDbFixture FixtureWith(
        IReadOnlyList<JulieDbFixture.RelationshipRow>? relationships,
        IReadOnlyList<JulieDbFixture.IdentifierRow>? identifiers)
    {
        var rows = new[]
        {
            new JulieDbFixture.SymbolRow(ProcessId, "Process", "method", "csharp", "src/A.cs",
                "public void Process()", 1, null),
            new JulieDbFixture.SymbolRow(ValidateId, "Validate", "method", "csharp", "src/A.cs",
                "public void Validate()", 5, null),
            new JulieDbFixture.SymbolRow(HandleId, "Handle", "method", "csharp", "src/B.cs",
                "public void Handle()", 1, null),
            new JulieDbFixture.SymbolRow(LogAId, "Log", "method", "csharp", "src/C.cs",
                "public void Log()", 1, null),
            new JulieDbFixture.SymbolRow(LogBId, "Log", "method", "csharp", "src/D.cs",
                "public void Log()", 1, null),
        };
        return JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows, identifiers: identifiers, relationships: relationships);
    }

    [Fact]
    public void Read_RelationshipsRow_BecomesByIdEdgeVerbatim()
    {
        // The precise edge source: from_symbol_id → to_symbol_id, carrying kind, by id (no name resolution).
        using var fx = FixtureWith(
            relationships: new[]
            {
                new JulieDbFixture.RelationshipRow("r1", ProcessId, ValidateId, "calls"),
            },
            identifiers: null);

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.Contains(edges, e => e.From == ProcessId && e.To == ValidateId && e.Kind == "calls");
    }

    [Fact]
    public void Read_IdentifierName_ResolvesToTheRightId()
    {
        // identifiers.name "Validate" with containing=Process resolves to ValidateId → edge Process → Validate.
        using var fx = FixtureWith(
            relationships: null,
            identifiers: new[]
            {
                new JulieDbFixture.IdentifierRow("i1", "Validate", "call", "csharp", "src/A.cs", 2, ProcessId),
            });

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.Contains(edges, e => e.From == ProcessId && e.To == ValidateId && e.Kind == "call");
    }

    [Fact]
    public void Read_NullContainingSymbolId_IsDropped()
    {
        // An identifier with NULL containing_symbol_id has no source node (e.g. a namespace ref) → no edge.
        using var fx = FixtureWith(
            relationships: null,
            identifiers: new[]
            {
                new JulieDbFixture.IdentifierRow("i1", "Validate", "call", "csharp", "src/A.cs", 2, null),
            });

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.Empty(edges);
    }

    [Fact]
    public void Read_NameResolvingToNoIndexedSymbol_IsDropped()
    {
        // An external/library ref (e.g. Assert.Equal) resolves to no indexed symbol → bounded out of the graph.
        using var fx = FixtureWith(
            relationships: null,
            identifiers: new[]
            {
                new JulieDbFixture.IdentifierRow("i1", "Assert", "call", "csharp", "src/A.cs", 2, ProcessId),
            });

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.Empty(edges);
    }

    [Fact]
    public void Read_HomonymName_EmitsEdgesToBothIds()
    {
        // D2 honesty: "Log" names TWO indexed symbols, so a call to Log from Process over-approximates to BOTH
        // (the safe direction for blast radius). The reader emits Process → LogA AND Process → LogB.
        using var fx = FixtureWith(
            relationships: null,
            identifiers: new[]
            {
                new JulieDbFixture.IdentifierRow("i1", "Log", "call", "csharp", "src/A.cs", 2, ProcessId),
            });

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.Contains(edges, e => e.From == ProcessId && e.To == LogAId && e.Kind == "call");
        Assert.Contains(edges, e => e.From == ProcessId && e.To == LogBId && e.Kind == "call");
    }

    [Fact]
    public void Read_NameResolvingAboveFanoutLimit_IsDropped()
    {
        // Without target_symbol_id from julie, name fallback is an approximation. Very high homonym counts are not
        // useful dependency evidence and can explode into millions of edges on large TS repos.
        using var fx = FixtureWith(
            relationships: null,
            identifiers: new[]
            {
                new JulieDbFixture.IdentifierRow("i1", "Log", "call", "csharp", "src/A.cs", 2, ProcessId),
            });

        var edges = SymbolGraphReader.Read(
            fx.DbPath,
            ResolverFor(NameMap),
            maxNameResolutionTargets: 1);

        Assert.Empty(edges);
    }

    [Fact]
    public void Read_SelfEdge_FromNameResolvingToOwnContainer_IsDropped()
    {
        // A recursive name occurrence whose resolved id equals its containing symbol id is a self-loop; a symbol
        // is never its own dependency, so the reader drops it defensively (the graph drops it too — defense in depth).
        using var fx = FixtureWith(
            relationships: null,
            identifiers: new[]
            {
                new JulieDbFixture.IdentifierRow("i1", "Process", "call", "csharp", "src/A.cs", 2, ProcessId),
            });

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.DoesNotContain(edges, e => e.From == ProcessId && e.To == ProcessId);
    }

    [Fact]
    public void Read_UnionsRelationshipsAndIdentifiers()
    {
        // Both sources contribute. A relationships row (Handle → Validate) and an identifiers row
        // (Process → Validate by name) both appear in the unioned edge list.
        using var fx = FixtureWith(
            relationships: new[]
            {
                new JulieDbFixture.RelationshipRow("r1", HandleId, ValidateId, "calls"),
            },
            identifiers: new[]
            {
                new JulieDbFixture.IdentifierRow("i1", "Validate", "call", "csharp", "src/A.cs", 2, ProcessId),
            });

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.Contains(edges, e => e.From == HandleId && e.To == ValidateId);
        Assert.Contains(edges, e => e.From == ProcessId && e.To == ValidateId);
    }

    [Fact]
    public void Read_ByColumnName_NotOrdinal_MapsFromToKind_AndContainingName_ToTheRightFields()
    {
        // D6 by-name lock: both readers (relationships + identifiers) resolve columns via GetOrdinal(name), so a
        // future column add/reorder in the v1 tables can never silently shift a value into the wrong GraphEdge
        // field. Distinct from/to ids + a distinct kind on the relationship, and a distinct containing/name on the
        // identifier, would all cross if the reads were positional and a column moved. Asserting each lands where
        // it belongs pins the by-name wiring.
        using var fx = FixtureWith(
            relationships: new[]
            {
                new JulieDbFixture.RelationshipRow("r1", HandleId, ProcessId, "implements"),
            },
            identifiers: new[]
            {
                new JulieDbFixture.IdentifierRow("i1", "Validate", "type_usage", "csharp", "src/A.cs", 9, HandleId),
            });

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        // relationships: from=Handle, to=Process, kind=implements — none crossed.
        Assert.Contains(edges, e => e.From == HandleId && e.To == ProcessId && e.Kind == "implements");
        // identifiers: containing=Handle resolves name "Validate"→ValidateId, kind=type_usage.
        Assert.Contains(edges, e => e.From == HandleId && e.To == ValidateId && e.Kind == "type_usage");
    }

    [Fact]
    public void Read_NullResolver_Throws()
    {
        using var fx = FixtureWith(relationships: null, identifiers: null);

        Assert.Throws<ArgumentNullException>(() => SymbolGraphReader.Read(fx.DbPath, null!));
    }

    [Fact]
    public void Read_MissingDbFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), "miller-nope-" + Guid.NewGuid().ToString("N"), "symbols.db");

        var ex = Assert.Throws<FileNotFoundException>(
            () => SymbolGraphReader.Read(missing, ResolverFor(NameMap)));
        Assert.Contains(missing, ex.Message);
    }
}
