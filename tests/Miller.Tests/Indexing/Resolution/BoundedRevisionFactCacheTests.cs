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

        Assert.Equal(0, full.Cache.BoundedSliceMisses);
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
    public void BoundedGraphFrontierLoadsEachRequestedVersionOnce()
    {
        using ResolutionStoreFixture fixture = Populate();
        using SqliteConnection connection = fixture.OpenRead();
        RevisionFactCache bounded = RevisionFactCache.LoadBounded(connection, fixture.Visibility());
        var reader = new QueryTimeResolutionReader(bounded, fixture.Visibility());

        _ = reader.ReadResolutionEdges(connection, [Run], Direction.Both, statementObserver: null);

        Assert.Equal(3, bounded.BoundedSliceMisses);
        Assert.Equal(3, bounded.LoadedSliceCount);
    }

    [Fact]
    public void BoundedLazySliceUsesThePointLoader()
    {
        using ResolutionStoreFixture fixture = Populate();
        using SqliteConnection connection = fixture.OpenRead();
        RevisionFactCache bounded = RevisionFactCache.LoadBounded(connection, fixture.Visibility());

        Assert.NotNull(bounded.Slice(VisibleVersions[0]));
        Assert.Equal(1, bounded.BoundedPointSliceLoads);
    }

    // The fast relationship shape is the only new SQL on the bounded path, and it carries two safety predicates.
    // This is the first: for every visible file it must return exactly the rows the manifest-joined read
    // returns, which includes DROPPING a relationship whose target is outside the pinned manifest.
    [Fact]
    public void TheFastRelationshipShapeAnswersWhatTheManifestJoinedReadAnswers()
    {
        using ResolutionStoreFixture fixture = Populate();
        using SqliteConnection connection = fixture.OpenRead();

        foreach (long versionId in VisibleVersions)
        {
            List<RevisionFactCacheLoader.RelationshipLocateRow>? fast =
                RevisionFactCacheLoader.TryReadStoreRelationshipsByVersion(
                    connection,
                    fixture.Visibility(),
                    versionId);
            List<RevisionFactCacheLoader.RelationshipLocateRow> manifestJoined =
                RevisionFactCacheLoader.ReadStoreRelationships(connection, fixture.Visibility(), versionId);

            Assert.NotNull(fast);
            Assert.Equal(Serialize(ByRowId(manifestJoined)), Serialize(ByRowId(fast)));
        }

        // The guard on the guard: the fixture really does carry a relationship whose target is invisible, so
        // the comparison above is not comparing two empty answers.
        Assert.DoesNotContain(
            RevisionFactCacheLoader.ReadStoreRelationships(connection, fixture.Visibility(), 1),
            row => string.Equals(row.RowId, "rel-ghost", StringComparison.Ordinal));
    }

    // The second predicate. `symbol_id` is unique per VERSION, not per generation, so one to_symbol_id can name
    // two visible symbol rows. When their names differ, which row each shape keeps decides the answer and
    // neither shape's row order is promised — so the fast shape must refuse itself and let the caller fall back.
    [Fact]
    public void TheFastRelationshipShapeRefusesATargetIdWithTwoVisibleNames()
    {
        using ResolutionStoreFixture fixture = PopulateWithAConflictingRelationshipTarget();
        using SqliteConnection connection = fixture.OpenRead();

        Assert.Null(RevisionFactCacheLoader.TryReadStoreRelationshipsByVersion(
            connection,
            fixture.Visibility(),
            1));

        // A file with no such conflict still takes the fast shape.
        Assert.NotNull(RevisionFactCacheLoader.TryReadStoreRelationshipsByVersion(
            connection,
            fixture.Visibility(),
            2));
    }

    // And the refusal must reach the answer: a bounded cache over that fixture falls back and agrees with the
    // whole-generation load anyway.
    [Fact]
    public void BoundedAndFullAgreeWhenTheFastRelationshipShapeRefusesItself()
    {
        using ResolutionStoreFixture fixture = PopulateWithAConflictingRelationshipTarget();
        using SqliteConnection fullConnection = fixture.OpenRead();
        using SqliteConnection boundedConnection = fixture.OpenRead();
        RevisionFactCache full = RevisionFactCache.Load(fullConnection, fixture.Visibility());
        RevisionFactCache bounded = RevisionFactCache.LoadBounded(boundedConnection, fixture.Visibility());

        foreach (long versionId in VisibleVersions)
        {
            Assert.Equal(
                Serialize(LocatedRows(full.Slice(versionId))),
                Serialize(LocatedRows(bounded.Slice(versionId))));
        }
    }

    // A bounded cache fills as it is queried, and the reader that holds it is handed out with no promise that
    // one thread owns it. Unsynchronized growth of its dictionaries shows up here as a throw or a wrong answer.
    [Fact]
    public void BoundedFactsServeConcurrentReadersWhatASerialReaderSees()
    {
        using ResolutionStoreFixture fixture = Populate();
        using SqliteConnection fullConnection = fixture.OpenRead();
        using SqliteConnection boundedConnection = fixture.OpenRead();
        RevisionFactCache full = RevisionFactCache.Load(fullConnection, fixture.Visibility());
        RevisionFactCache bounded = RevisionFactCache.LoadBounded(boundedConnection, fixture.Visibility());
        string[] expected = VisibleVersions
            .Select(versionId => Serialize(full.SymbolsOfVersion(versionId)))
            .ToArray();
        string expectedNamed = Serialize(full.SymbolsNamed("Helper"));

        var observed = new string[64];
        var observedNamed = new string[64];
        Parallel.For(0, observed.Length, i =>
        {
            long versionId = VisibleVersions[i % VisibleVersions.Length];
            observed[i] = Serialize(bounded.SymbolsOfVersion(versionId));
            observedNamed[i] = Serialize(bounded.SymbolsNamed("Helper"));
        });

        for (int i = 0; i < observed.Length; i++)
        {
            Assert.Equal(expected[i % VisibleVersions.Length], observed[i]);
            Assert.Equal(expectedNamed, observedNamed[i]);
        }
    }

    // The full load builds a fresh array per call, so a caller may sort or rewrite the result in place. The
    // bounded cache keeps the materialized list for the name, so it has to hand out a copy or the two modes
    // would differ in a way no accessor comparison can see.
    [Fact]
    public void BoundedSymbolsNamedHandsEveryCallerItsOwnArray()
    {
        using ResolutionStoreFixture fixture = Populate();
        using SqliteConnection fullConnection = fixture.OpenRead();
        using SqliteConnection boundedConnection = fixture.OpenRead();
        RevisionFactCache full = RevisionFactCache.Load(fullConnection, fixture.Visibility());
        RevisionFactCache bounded = RevisionFactCache.LoadBounded(boundedConnection, fixture.Visibility());
        string expected = Serialize(full.SymbolsNamed("Dup"));

        // The first call materializes the name; both calls under test therefore come from the cached list,
        // which is where handing out the instance itself would let one caller rewrite another's answer.
        _ = bounded.SymbolsNamed("Dup");
        var first = Assert.IsType<FactSymbol[]>(bounded.SymbolsNamed("Dup"));
        Assert.Equal(2, first.Length);
        Array.Reverse(first);
        var second = Assert.IsType<FactSymbol[]>(bounded.SymbolsNamed("Dup"));

        Assert.NotSame(first, second);
        Assert.Equal(expected, Serialize(second));
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

    // Every version the pinned manifest carries, so the comparison covers the TypeScript pair as well: file 5
    // is the only one with an import symbol, and ImportsOf is the one accessor where the bounded path binds the
    // imports itself instead of going through the whole-generation BindAllImports.
    private static readonly long[] VisibleVersions = [1, 2, 3, 4, 5, 6];

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

    private static IEnumerable<RevisionFactCacheLoader.RelationshipLocateRow> ByRowId(
        IEnumerable<RevisionFactCacheLoader.RelationshipLocateRow> rows) =>
        rows.OrderBy(row => row.RowId, StringComparer.Ordinal);

    // The base fixture plus one to_symbol_id that names two VISIBLE symbol rows with different names.
    private static ResolutionStoreFixture PopulateWithAConflictingRelationshipTarget()
    {
        ResolutionStoreFixture fixture = Populate();
        fixture.AddSymbol(1, "cls-twin", "TwinHere", "class", "src/App.cs");
        fixture.AddSymbol(2, "cls-twin", "TwinThere", "class", "src/Other.cs");
        fixture.AddIdentifier(
            1, "id-twin", "TwinHere", "src/App.cs", kind: "type_usage", containingSymbolId: Run,
            startByte: 300, endByte: 308, startLine: 9);
        fixture.AddRelationship(
            1, "rel-twin", Run, "cls-twin", "src/App.cs", kind: "uses",
            startByte: 300, endByte: 308, startLine: 9);
        return fixture;
    }

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

        // A relationship whose TARGET symbol lives on a version the pinned manifest does not carry. Both read
        // shapes must drop it: the manifest-joined read by its target-visibility join, the fast read by its
        // target-visibility EXISTS. The identifier at the same span is what makes the difference observable —
        // keeping the relationship would locate that identifier and change the propagation row set.
        fixture.AddFile(7, "src/Ghost.cs");
        fixture.AddSymbol(7, "cls-ghost", "Ghost", "class", "src/Ghost.cs");
        fixture.ExecuteWrite("DELETE FROM manifest_entries WHERE path='src/Ghost.cs'");
        fixture.AddIdentifier(
            1, "id-ghost", "Ghost", "src/App.cs", kind: "type_usage", containingSymbolId: Run,
            startByte: 200, endByte: 205, startLine: 7);
        fixture.AddRelationship(
            1, "rel-ghost", Run, "cls-ghost", "src/App.cs", kind: "uses",
            startByte: 200, endByte: 205, startLine: 7);

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
