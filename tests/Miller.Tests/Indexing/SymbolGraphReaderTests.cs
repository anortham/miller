using Miller.Core.Graph;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SymbolGraphReaderTests
{
    private const string ProcessId = "00000000000000000000000000000001";
    private const string ValidateId = "00000000000000000000000000000002";
    private const string HandleId = "00000000000000000000000000000003";
    private const string ProgramId = "00000000000000000000000000000004";
    private const string FooId = "00000000000000000000000000000005";
    private const string LogAId = "0000000000000000000000000000000a";
    private const string LogBId = "0000000000000000000000000000000b";

    private static Func<string, IReadOnlyList<string>> ResolverFor(
        IReadOnlyDictionary<string, IReadOnlyList<string>> map) =>
        name => map.TryGetValue(name, out var ids) ? ids : Array.Empty<string>();

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NameMap =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Process"] = new[] { ProcessId },
            ["Validate"] = new[] { ValidateId },
            ["Handle"] = new[] { HandleId },
            ["Log"] = new[] { LogAId, LogBId },
        };

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
            new JulieDbFixture.SymbolRow(ProgramId, "Program", "module", "csharp", "src/Program.cs",
                "Program", 1, null),
            new JulieDbFixture.SymbolRow(FooId, "Foo", "class", "csharp", "src/Foo.cs",
                "public sealed class Foo", 1, null),
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
    public void Read_UnresolvedHomonymName_EmitsNoFallbackEdges()
    {
        using var fx = FixtureWith(
            relationships: null,
            identifiers: new[]
            {
                new JulieDbFixture.IdentifierRow("i1", "Log", "call", "csharp", "src/A.cs", 2, ProcessId),
            });

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.DoesNotContain(edges, edge => edge.From == ProcessId && edge.To == LogAId);
        Assert.DoesNotContain(edges, edge => edge.From == ProcessId && edge.To == LogBId);
    }

    [Fact]
    public void Read_ResolvedHomonymIdentifier_EmitsOnlyTheExactTarget()
    {
        using var fx = FixtureWith(
            relationships: null,
            identifiers:
            [
                new JulieDbFixture.IdentifierRow("i1", "Log", "call", "csharp", "src/A.cs", 2, ProcessId),
            ]);
        fx.AddIdentifierResolution("i1", LogBId);

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.DoesNotContain(edges, edge => edge.From == ProcessId && edge.To == LogAId);
        Assert.Contains(edges, edge => edge.From == ProcessId && edge.To == LogBId && edge.Kind == "call");
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
    public void Read_ResolvedPendingRelationship_BecomesByIdEdge()
    {
        using var fx = FixtureWith(relationships: null, identifiers: null);
        fx.AddPendingRelationship("pr1", ProgramId, "src/Program.cs", kind: "instantiates",
            targetDisplayName: "Foo", targetTerminalName: "Foo");
        fx.AddPendingResolution("pr1", FooId, method: "qualified_name");

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.Contains(edges, e => e.From == ProgramId && e.To == FooId && e.Kind == "instantiates");
    }

    [Fact]
    public void Load_ResolvedPendingRelationship_ReachesTargetWithInstantiatesEvidence()
    {
        using var fx = FixtureWith(relationships: null, identifiers: null);
        fx.AddPendingRelationship("pr1", ProgramId, "src/Program.cs", kind: "instantiates",
            targetDisplayName: "Foo", targetTerminalName: "Foo");
        fx.AddPendingResolution("pr1", FooId, method: "qualified_name");

        var index = RepositoryIndexLoader.Load(fx.DbPath);
        var reach = index.Graph.ReachWithEvidence([ProgramId], 1, 10, Direction.Forward);
        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.Contains(reach.Nodes, node => node.Id == FooId && node.Hop == 1);
        Assert.Contains(edges, edge =>
            edge.From == ProgramId && edge.To == FooId && edge.Kind == "instantiates");
    }

    [Fact]
    public void Read_UnresolvedPendingRelationship_IsDropped()
    {
        using var fx = FixtureWith(relationships: null, identifiers: null);
        fx.AddPendingRelationship("pr1", ProgramId, "src/Program.cs", kind: "instantiates",
            targetDisplayName: "Foo", targetTerminalName: "Foo");

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.Empty(edges);
    }

    [Fact]
    public void Read_ResolvedPendingRelationshipSelfLoop_IsDropped()
    {
        using var fx = FixtureWith(relationships: null, identifiers: null);
        fx.AddPendingRelationship("pr1", ProgramId, "src/Program.cs", kind: "instantiates",
            targetDisplayName: "Program", targetTerminalName: "Program");
        fx.AddPendingResolution("pr1", ProgramId, method: "qualified_name");

        var edges = SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap));

        Assert.Empty(edges);
    }

    [Fact]
    public void Read_MissingRequiredPendingTables_Throws()
    {
        using var fx = FixtureWith(relationships: null, identifiers: null);
        fx.ExecuteWrite("DROP TABLE pending_resolutions; DROP TABLE pending_relationships;");

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
            () => SymbolGraphReader.Read(fx.DbPath, ResolverFor(NameMap)));
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
