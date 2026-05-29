using Miller.Core.Search;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// End-to-end pin for the M1 in-process deliverable: read the synthesized julie DB → build the Core index →
/// query a known term → assert ranked order and the opaque-id bridge. <see cref="MillerRepositoryIndex"/> is
/// where the opaque-string-id ⇄ int-DocId bridge lives: a <see cref="SearchHit"/>'s DocId must resolve back to
/// the exact julie symbol id (the M4 join key) and its parent.
/// </summary>
public sealed class MillerRepositoryIndexTests
{
    private static MillerRepositoryIndex BuildFromFixture(JulieDbFixture fx) =>
        MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));

    [Fact]
    public void Build_IndexesEveryReadSymbol()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var repo = BuildFromFixture(fx);

        Assert.Equal(JulieDbFixture.DefaultRows.Count, repo.DocumentCount);
    }

    [Fact]
    public void Search_FindsKnownSymbolByName()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var repo = BuildFromFixture(fx);

        var hits = repo.Search("parseToken", limit: 10);

        Assert.NotEmpty(hits);
        // Exact-name match must be the top hit (the 1.5x boost in Core, validated end to end here).
        Assert.Equal("parseToken", repo.Resolve(hits[0].Document.DocId).Name);
    }

    [Fact]
    public void Search_ComponentTokenMatchesCamelCaseSymbol()
    {
        // The whole point of the code tokenizer flowing through Indexing: "http" must hit getHTTPResponseCode.
        using var fx = JulieDbFixture.CreateDefault();
        var repo = BuildFromFixture(fx);

        var hits = repo.Search("http", limit: 10);

        Assert.Contains(hits, h => repo.Resolve(h.Document.DocId).Name == "getHTTPResponseCode");
    }

    [Fact]
    public void Search_MatchesSignatureTokens()
    {
        // Decision D3: name + signature are indexed. "Vector512" appears in dot's signature only (its name
        // is "dot"), so a query on it must surface dot through the signature tokens.
        using var fx = JulieDbFixture.CreateDefault();
        var repo = BuildFromFixture(fx);

        var hits = repo.Search("vector512", limit: 10);
        var names = hits.Select(h => repo.Resolve(h.Document.DocId).Name).ToList();

        Assert.Contains("Vector512", names); // the struct (name match)
        Assert.Contains("dot", names);        // matched via its signature "...&Vector512..."
    }

    [Fact]
    public void Resolve_ReturnsTheOpaqueJulieIdAndParentForAHit()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var repo = BuildFromFixture(fx);

        var hit = repo.Search("GetUser", limit: 1)[0];
        var symbol = repo.Resolve(hit.Document.DocId);

        // The bridge: SearchHit.Document.DocId → the exact julie symbol id + its containment parent (M4 join).
        Assert.Equal("GetUser", symbol.Name);
        Assert.Equal("b2c3d4e5f6001122334455667788990a", symbol.SymbolId);
        Assert.Equal("a1b2c3d4e5f600112233445566778899", symbol.ParentId); // UserService
    }

    [Fact]
    public void Resolve_IsO1ByDocIdOrdinal_AndRoundTripsEverySymbol()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var symbols = SqliteSymbolReader.Read(fx.DbPath);
        var repo = MillerRepositoryIndex.Build(symbols);

        // Resolve(docId) must return the same IndexedSymbol the reader produced at that ordinal.
        foreach (var expected in symbols)
            Assert.Equal(expected, repo.Resolve(expected.DocId));
    }

    [Fact]
    public void Resolve_OutOfRangeDocId_Throws()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var repo = BuildFromFixture(fx);

        Assert.Throws<ArgumentOutOfRangeException>(() => repo.Resolve(repo.DocumentCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => repo.Resolve(-1));
    }

    [Fact]
    public void Search_AndMode_RequiresAllTerms()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var repo = BuildFromFixture(fx);

        // "serve http" — only ServeHTTP matches both terms via name+signature; AND must exclude others.
        var hits = repo.Search("serve http", limit: 10, mode: SearchMode.And);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("ServeHTTP", repo.Resolve(h.Document.DocId).Name));
    }

    [Fact]
    public void Build_NullSymbols_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MillerRepositoryIndex.Build(null!));
    }

    // ---------- D9: graph travels with the index ----------

    // Three symbols forming Process → Validate and Handle → Validate (Validate has two dependents).
    private const string ProcessId = "00000000000000000000000000000001";
    private const string ValidateId = "00000000000000000000000000000002";
    private const string HandleId = "00000000000000000000000000000003";

    private static IReadOnlyList<IndexedSymbol> ThreeSymbols(bool processIsTest = false) => new[]
    {
        new IndexedSymbol(0, ProcessId, "Process", "public void Process()", "method", "csharp",
            "src/A.cs", 1, 3, null, IsTest: processIsTest),
        new IndexedSymbol(1, ValidateId, "Validate", "public void Validate()", "method", "csharp",
            "src/A.cs", 5, 7, null),
        new IndexedSymbol(2, HandleId, "Handle", "public void Handle()", "method", "csharp",
            "src/B.cs", 1, 3, null),
    };

    [Fact]
    public void Build_WithEdges_ExposesAGraphWhoseDependentsHydrate()
    {
        var symbols = ThreeSymbols();
        var edges = new[]
        {
            new Miller.Core.Graph.GraphEdge(ProcessId, ValidateId, "calls"),
            new Miller.Core.Graph.GraphEdge(HandleId, ValidateId, "calls"),
        };

        var repo = MillerRepositoryIndex.Build(symbols, edges);

        // The graph is published on the index and carries the edges.
        Assert.True(repo.Graph.Contains(ProcessId));
        // Dependents(Validate) = {Process, Handle}, hydrated to IndexedSymbols. The graph returns neighbours in
        // id order (ProcessId=…01 < HandleId=…03), so the hydrated order is [Process, Handle].
        var dependents = repo.Dependents(ValidateId);
        Assert.Equal(new[] { ProcessId, HandleId }, dependents.Select(s => s.SymbolId).ToArray());
        Assert.Equal(new[] { "Process", "Handle" }, dependents.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Build_WithEdges_DependenciesHydrate()
    {
        var symbols = ThreeSymbols();
        var edges = new[]
        {
            new Miller.Core.Graph.GraphEdge(ProcessId, ValidateId, "calls"),
        };

        var repo = MillerRepositoryIndex.Build(symbols, edges);

        // Dependencies(Process) = {Validate}, hydrated.
        var deps = repo.Dependencies(ProcessId);
        Assert.Single(deps);
        Assert.Equal("Validate", deps[0].Name);
        Assert.Equal(ValidateId, deps[0].SymbolId);
    }

    [Fact]
    public void Build_WithEdges_PreservesIsTestOnGraphNodes()
    {
        // The graph nodes carry IsTest so impact can partition "likely tests" without a second lookup.
        var repo = MillerRepositoryIndex.Build(ThreeSymbols(processIsTest: true), Array.Empty<Miller.Core.Graph.GraphEdge>());

        Assert.True(repo.Graph.IsTest(ProcessId));
        Assert.False(repo.Graph.IsTest(ValidateId));
    }

    [Fact]
    public void Build_WithoutEdges_YieldsAnEmptyGraph_BackCompat()
    {
        // The existing Build(symbols) keeps working: every symbol is a node, but there are no edges.
        using var fx = JulieDbFixture.CreateDefault();
        var repo = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));

        // A node exists for an indexed symbol, but it has no dependencies/dependents.
        var anyId = SqliteSymbolReader.Read(fx.DbPath)[0].SymbolId;
        Assert.True(repo.Graph.Contains(anyId));
        Assert.Empty(repo.Dependents(anyId));
        Assert.Empty(repo.Dependencies(anyId));
    }

    [Fact]
    public void Dependents_SkipsIdsNotInTheIndex()
    {
        // An edge whose endpoint is not an indexed symbol is dropped by the graph build (edge hygiene), so the
        // hydration never sees an id absent from the index. This pins the contract end to end.
        var symbols = ThreeSymbols();
        var edges = new[]
        {
            new Miller.Core.Graph.GraphEdge(ProcessId, ValidateId, "calls"),
            // An edge to a non-indexed id — the graph drops it (unknown endpoint), so Validate gains no phantom dependent.
            new Miller.Core.Graph.GraphEdge("ffffffffffffffffffffffffffffffff", ValidateId, "calls"),
        };

        var repo = MillerRepositoryIndex.Build(symbols, edges);

        var dependents = repo.Dependents(ValidateId);
        Assert.Equal(new[] { ProcessId }, dependents.Select(s => s.SymbolId).ToArray());
    }

    [Fact]
    public void Build_WithEdges_NullEdges_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => MillerRepositoryIndex.Build(ThreeSymbols(), null!));
    }

    [Fact]
    public void Dependents_UnknownId_ReturnsEmpty()
    {
        var repo = MillerRepositoryIndex.Build(ThreeSymbols(), Array.Empty<Miller.Core.Graph.GraphEdge>());

        Assert.Empty(repo.Dependents("ffffffffffffffffffffffffffffffff"));
        Assert.Empty(repo.Dependencies("ffffffffffffffffffffffffffffffff"));
    }
}
