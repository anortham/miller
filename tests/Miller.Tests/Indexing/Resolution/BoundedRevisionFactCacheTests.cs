using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Resolution;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using Xunit;

namespace Miller.Tests.Indexing.Resolution;

/// <summary>
/// The bounded fact cache the one-shot CLI reads through must answer exactly what the whole-generation load
/// answers. These tests compare the two caches over one fixture that carries every reference shape the
/// deep-inspect path meets: a symbol nothing references, one call site, forty call sites in one file,
/// cross-file calls, a name two files both define, an import binding, a receiver type fact, and a pending row
/// that overrides an identifier.
/// </summary>
public sealed class BoundedRevisionFactCacheTests
{
    private const string App = "cls-app";
    private const string Run = "fn-run";
    private const string Helper = "fn-help";
    private const string Count = "var-count";
    private const string DupA = "cls-dup-a";
    private const string DupB = "cls-dup-b";
    private const string Lonely = "cls-lonely";
    private const string Consumer = "cls-consumer";
    private const string Use = "fn-use";
    private const int ManyCallSites = 40;

    private static readonly string[] Targets =
        [App, Run, Helper, Count, DupA, DupB, Lonely, Consumer, Use, "cls-many"];

    private static readonly string[] Names =
        ["App", "Run", "Helper", "count", "Dup", "Lonely", "Consumer", "Use", "Many", "Widget", "Missing", ""];

    [Fact]
    public void BoundedAndFullFactsAnswerEveryAccessorIdentically()
    {
        using ResolutionStoreFixture fixture = Populate();
        using SqliteConnection fullConnection = fixture.OpenRead();
        using SqliteConnection boundedConnection = fixture.OpenRead();
        RevisionFactCache full = RevisionFactCache.Load(fullConnection, fixture.Visibility());
        RevisionFactCache bounded = RevisionFactCache.LoadBounded(boundedConnection, fixture.Visibility());

        foreach (string name in Names)
            Assert.Equal(Serialize(full.SymbolsNamed(name)), Serialize(bounded.SymbolsNamed(name)));

        foreach (long versionId in VisibleVersions)
        {
            Assert.Equal(Serialize(full.TopLevelOf(versionId)), Serialize(bounded.TopLevelOf(versionId)));
            Assert.Equal(Serialize(full.ImportsOf(versionId)), Serialize(bounded.ImportsOf(versionId)));
            Assert.Equal(
                Serialize(full.SymbolsOfVersion(versionId)),
                Serialize(bounded.SymbolsOfVersion(versionId)));
            Assert.Equal(full.Slice(versionId)?.Language, bounded.Slice(versionId)?.Language);
            Assert.Equal(full.Slice(versionId)?.Path, bounded.Slice(versionId)?.Path);
            Assert.Equal(
                Serialize(LocatedRows(full.Slice(versionId))),
                Serialize(LocatedRows(bounded.Slice(versionId))));

            foreach (FactSymbol symbol in full.SymbolsOfVersion(versionId))
            {
                Assert.Equal(Serialize(full.Symbol(symbol.Key)), Serialize(bounded.Symbol(symbol.Key)));
                Assert.Equal(Serialize(full.ChildrenOf(symbol.Key)), Serialize(bounded.ChildrenOf(symbol.Key)));
                Assert.Equal(Serialize(full.TypeFactsOf(symbol.Key)), Serialize(bounded.TypeFactsOf(symbol.Key)));
            }
        }
    }

    // A version outside the pinned manifest has no slice in a full load, so the bounded cache must report the
    // same absence rather than reading the rows and reporting an empty file.
    [Fact]
    public void BoundedFactsReportNoSliceForAVersionOutsideTheManifest()
    {
        using ResolutionStoreFixture fixture = Populate();
        fixture.AddFile(9, "src/Hidden.cs");
        fixture.AddSymbol(9, "cls-hidden", "Hidden", "class", "src/Hidden.cs");
        fixture.ExecuteWrite("DELETE FROM manifest_entries WHERE path='src/Hidden.cs'");
        using SqliteConnection fullConnection = fixture.OpenRead();
        using SqliteConnection boundedConnection = fixture.OpenRead();
        RevisionFactCache full = RevisionFactCache.Load(fullConnection, fixture.Visibility());
        RevisionFactCache bounded = RevisionFactCache.LoadBounded(boundedConnection, fixture.Visibility());

        Assert.Null(full.Slice(9));
        Assert.Null(bounded.Slice(9));
        Assert.Empty(bounded.SymbolsOfVersion(9));
        Assert.Empty(bounded.SymbolsNamed("Hidden"));
        Assert.Null(bounded.Symbol(new FactSymbolKey(9, "cls-hidden")));
    }

    [Fact]
    public void BoundedAndFullReferenceEvidenceAgreeForEverySymbolShape()
    {
        using ResolutionStoreFixture fixture = Populate();
        using var full = new FixtureReadSession(fixture, bounded: false);
        using var bounded = new FixtureReadSession(fixture, bounded: true);
        var bounds = new ReferenceEvidenceBounds(ExactLimit: 100, FallbackLimit: 100);

        foreach (string target in Targets)
        {
            ReferenceEvidenceBundle fullBundle = Read(full, target, bounds);
            ReferenceEvidenceBundle boundedBundle = Read(bounded, target, bounds);

            Assert.Equal(Serialize(fullBundle), Serialize(boundedBundle));
        }
    }

    // The evidence a bounded page reports must also survive paging: the coverage counts and the bounded slices
    // come from the same rows, so a tighter limit must not diverge either.
    [Fact]
    public void BoundedAndFullReferenceEvidenceAgreeUnderATightLimit()
    {
        using ResolutionStoreFixture fixture = Populate();
        using var full = new FixtureReadSession(fixture, bounded: false);
        using var bounded = new FixtureReadSession(fixture, bounded: true);
        var bounds = new ReferenceEvidenceBounds(ExactLimit: 3, FallbackLimit: 2);

        Assert.Equal(Serialize(Read(full, Helper, bounds)), Serialize(Read(bounded, Helper, bounds)));
        // 40 call sites in Many.cs, one in App.cs, one in Other.cs, one relationship row and one pending row —
        // well past the 3-row page, so the page and its coverage counts are both exercised.
        Assert.Equal(
            ManyCallSites + 4,
            Read(bounded, Helper, bounds).Inbound.Coverage.ExactAvailable);
    }

    [Fact]
    public void BoundedAndFullGraphEdgesAgree()
    {
        using ResolutionStoreFixture fixture = Populate();
        using SqliteConnection fullConnection = fixture.OpenRead();
        using SqliteConnection boundedConnection = fixture.OpenRead();
        var full = new QueryTimeResolutionReader(
            RevisionFactCache.Load(fullConnection, fixture.Visibility()),
            fixture.Visibility());
        var bounded = new QueryTimeResolutionReader(
            RevisionFactCache.LoadBounded(boundedConnection, fixture.Visibility()),
            fixture.Visibility());

        foreach (Direction direction in new[] { Direction.Forward, Direction.Reverse, Direction.Both })
        {
            Assert.Equal(
                Serialize(full.ReadResolutionEdges(fullConnection, Targets, direction, null)),
                Serialize(bounded.ReadResolutionEdges(boundedConnection, Targets, direction, null)));
            Assert.Equal(
                Serialize(full.ReadUnresolvedNameEdges(fullConnection, Targets, direction, null)),
                Serialize(bounded.ReadUnresolvedNameEdges(boundedConnection, Targets, direction, null)));
        }
    }

    // The pending row that covers id-help suppresses the identifier edge. The bounded cache reads propagation
    // one file at a time, so this is the shape that proves the per-file locate agrees with the whole-generation
    // one.
    [Fact]
    public void BoundedFactsKeepThePendingOverride()
    {
        using ResolutionStoreFixture fixture = Populate();
        using SqliteConnection connection = fixture.OpenRead();
        RevisionFactCache bounded = RevisionFactCache.LoadBounded(connection, fixture.Visibility());
        var reader = new QueryTimeResolutionReader(bounded, fixture.Visibility());

        IReadOnlyList<FamilyGraphResolutionEdge> edges =
            reader.ReadResolutionEdges(connection, [Run], Direction.Forward, statementObserver: null);

        Assert.Contains(edges, edge => edge.Source == "pending_resolution" && edge.ToId == Helper);
        Assert.DoesNotContain(edges, edge => edge.Source == "identifier_target" && edge.ToId == Helper);
    }

    [Fact]
    public void BoundedFactsReadOnlyTheFilesTheQueryNeeds()
    {
        using ResolutionStoreFixture fixture = Populate();
        using SqliteConnection connection = fixture.OpenRead();
        RevisionFactCache bounded = RevisionFactCache.LoadBounded(connection, fixture.Visibility());
        var reader = new QueryTimeResolutionReader(bounded, fixture.Visibility());
        Assert.Equal(0, bounded.LoadedSliceCount);

        _ = reader.ReadInboundExact(connection, [Count]);

        Assert.InRange(bounded.LoadedSliceCount, 1, VisibleVersions.Length - 1);
    }

    [Fact]
    public void BoundedFactsRefuseToAdvanceOntoANewerGeneration()
    {
        using ResolutionStoreFixture fixture = Populate();
        using SqliteConnection connection = fixture.OpenRead();
        RevisionFactCache bounded = RevisionFactCache.LoadBounded(connection, fixture.Visibility());

        Assert.False(bounded.CanAdvance);
        Assert.Throws<InvalidOperationException>(() => bounded.Advance(connection, fixture.Visibility()));
    }

    private static readonly long[] VisibleVersions = [1, 2, 3, 4];

    private static ReferenceEvidenceBundle Read(
        FixtureReadSession session,
        string target,
        ReferenceEvidenceBounds bounds) =>
        ReferenceEvidenceReader.ReadForSymbol(
            session,
            target,
            new ReferenceEvidenceQuery(bounds),
            new ReferenceEvidenceQuery(bounds),
            bounds,
            [ReferenceKind.Call, ReferenceKind.TypeUsage, ReferenceKind.VariableReference]);

    private static string[] LocatedRows(VersionSlice? slice)
    {
        if (slice is null)
            return [];
        var rows = new string[slice.LocatedRowIds.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            // The located ROW SET and its origin are what the reader consults. The source row id recorded for
            // a row two source rows both locate is stored and never read, so it is not compared here.
            slice.TryGetLocated(slice.LocatedRowIds[i], out PropagationSource source);
            rows[i] = string.Create(
                CultureInfo.InvariantCulture,
                $"{slice.LocatedRowIds[i]}|{source.Origin}");
        }

        return rows;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);

    private static ResolutionStoreFixture Populate()
    {
        ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "src/App.cs");
        fixture.AddFile(2, "src/Other.cs");
        fixture.AddFile(3, "src/Third.cs");
        fixture.AddFile(4, "src/Many.cs");

        fixture.AddSymbol(1, App, "App", "class", "src/App.cs", visibility: "public");
        fixture.AddSymbol(1, Run, "Run", "method", "src/App.cs", parentId: App, signature: "void Run(int count)");
        fixture.AddSymbol(1, Helper, "Helper", "function", "src/App.cs", parentId: App);
        fixture.AddSymbol(1, Count, "count", "variable", "src/App.cs", parentId: Run);
        fixture.AddSymbol(1, DupA, "Dup", "class", "src/App.cs");
        fixture.AddSymbol(1, Lonely, "Lonely", "class", "src/App.cs");
        fixture.AddIdentifier(1, "id-help", "Helper", "src/App.cs", kind: "call", containingSymbolId: Run, startByte: 10, endByte: 16);
        fixture.AddIdentifier(1, "id-count", "count", "src/App.cs", kind: "variable_ref", containingSymbolId: Run, startByte: 40, endByte: 45);
        fixture.AddIdentifier(1, "id-missing", "Missing", "src/App.cs", kind: "call", containingSymbolId: Run, startByte: 50, endByte: 57);
        fixture.AddIdentifier(1, "id-dup", "Dup", "src/App.cs", kind: "type_usage", containingSymbolId: Run, startByte: 60, endByte: 63);
        fixture.AddPending(1, "pend-help", Run, "Helper", "src/App.cs", startByte: 10, endByte: 16);
        fixture.AddRelationship(1, "rel-help", Run, Helper, "src/App.cs", startByte: 80, endByte: 86);

        fixture.AddSymbol(2, Consumer, "Consumer", "class", "src/Other.cs");
        fixture.AddSymbol(2, Use, "Use", "method", "src/Other.cs", parentId: Consumer, signature: "void Use(App app)");
        fixture.AddSymbol(2, DupB, "Dup", "class", "src/Other.cs");
        fixture.AddTypeFact(2, "tf-use", Use, "App");
        fixture.AddIdentifier(2, "id-x-help", "Helper", "src/Other.cs", kind: "call", containingSymbolId: Use, startByte: 12, endByte: 18);
        fixture.AddIdentifier(2, "id-x-app", "App", "src/Other.cs", kind: "type_usage", containingSymbolId: Use, startByte: 30, endByte: 33);
        fixture.AddIdentifier(2, "id-x-run", "Run", "src/Other.cs", kind: "call", containingSymbolId: Use, startByte: 44, endByte: 47);
        fixture.AddPending(2, "pend-x-run", Use, "Run", "src/Other.cs", startByte: 44, endByte: 47);

        fixture.AddFile(5, "src/mod.ts", "typescript");
        fixture.AddFile(6, "src/widget.ts", "typescript");
        fixture.AddSymbol(
            5,
            "imp-widget",
            "Widget",
            "import",
            "src/mod.ts",
            language: "typescript",
            metadataJson: """{"source":"./widget","imported_name":"Widget"}""");
        fixture.AddSymbol(5, "fn-mod", "run", "function", "src/mod.ts", language: "typescript");
        fixture.AddSymbol(6, "cls-widget", "Widget", "class", "src/widget.ts", language: "typescript");
        fixture.AddIdentifier(
            5, "id-widget", "Widget", "src/mod.ts", kind: "type_usage", containingSymbolId: "fn-mod",
            startByte: 5, endByte: 11, language: "typescript");

        fixture.AddSymbol(3, "cls-third", "Third", "class", "src/Third.cs");
        fixture.AddIdentifier(3, "id-third-run", "Run", "src/Third.cs", kind: "call", containingSymbolId: "cls-third", startByte: 5, endByte: 8);

        fixture.AddSymbol(4, "cls-many", "Many", "class", "src/Many.cs");
        for (int i = 0; i < ManyCallSites; i++)
        {
            fixture.AddIdentifier(
                4,
                "id-many-" + i.ToString(CultureInfo.InvariantCulture),
                "Helper",
                "src/Many.cs",
                kind: "call",
                containingSymbolId: "cls-many",
                startByte: 100 + (i * 10),
                endByte: 106 + (i * 10),
                startLine: i + 1);
        }

        return fixture;
    }

    private sealed class FixtureReadSession : IWorkspaceReadSession, IQueryTimeResolutionHost
    {
        private readonly SqliteConnection _connection;

        public FixtureReadSession(ResolutionStoreFixture fixture, bool bounded)
        {
            _connection = fixture.OpenRead();
            Resolution = new QueryTimeResolutionReader(
                bounded
                    ? RevisionFactCache.LoadBounded(_connection, fixture.Visibility())
                    : RevisionFactCache.Load(_connection, fixture.Visibility()),
                fixture.Visibility());
            Snapshot = new WorkspaceReadSnapshot(
                fixture.Visibility().WorkspaceRoot,
                "workspace-a",
                fixture.Visibility().FamilyId,
                fixture.ViewId,
                new WorkspaceFreshnessToken(fixture.Visibility().FamilyId, fixture.Generation),
                "full",
                WorkspaceReadMode.FamilyStore);
        }

        public WorkspaceReadSnapshot Snapshot { get; }

        public QueryTimeResolutionReader Resolution { get; }

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) => query(_connection);

        public void Dispose() => _connection.Dispose();
    }
}
