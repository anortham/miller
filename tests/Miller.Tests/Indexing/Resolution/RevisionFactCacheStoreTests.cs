using Microsoft.Extensions.DependencyInjection;
using Miller.Core.Resolution;
using Miller.Indexing.Resolution;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing.Resolution;

public sealed class RevisionFactCacheStoreTests
{
    [Fact]
    public void Acquire_KeepsOneRevisionPerScopeAndAdvances()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "keep.cs");
        fixture.AddFile(2, "change.cs");
        fixture.AddSymbol(1, "kept", "Kept", "class", "keep.cs");
        fixture.AddSymbol(2, "old", "Old", "class", "change.cs");

        var store = new RevisionFactCacheStore();
        using RevisionFactCacheLease firstLease = store.Acquire("ws-a", "rev-1", fixture.OpenRead, fixture.Visibility());
        RevisionFactCache first = firstLease.Cache;
        FactSymbol[] kept = first.SymbolsOfVersion(1);
        using RevisionFactCacheLease sameLease = store.Acquire("ws-a", "rev-1", fixture.OpenRead, fixture.Visibility());
        RevisionFactCache same = sameLease.Cache;
        Assert.Same(first, same);
        Assert.Equal(1, store.ScopeCount);

        fixture.AddSymbol(3, "neu", "New", "class", "change.cs");
        fixture.FlipManifest(2, [("keep.cs", 1, "csharp", "indexed"), ("change.cs", 3, "csharp", "indexed")]);

        using RevisionFactCacheLease secondLease = store.Acquire("ws-a", "rev-2", fixture.OpenRead, fixture.Visibility());
        RevisionFactCache second = secondLease.Cache;
        Assert.NotSame(first, second);
        Assert.Same(kept, second.SymbolsOfVersion(1));
        Assert.Equal("New", System.Linq.Enumerable.Single(second.SymbolsNamed("New")).Name);
        Assert.Equal(1, store.ScopeCount);
    }

    [Fact]
    public void Acquire_EvictsLeastRecentlyUsedScopeWhenOverBudget()
    {
        using ResolutionStoreFixture firstFixture = ResolutionStoreFixture.Create();
        firstFixture.AddFile(1, "a.cs");
        firstFixture.AddSymbol(1, "a", "Alpha", "class", "a.cs");
        using ResolutionStoreFixture secondFixture = ResolutionStoreFixture.Create();
        secondFixture.AddFile(1, "b.cs");
        secondFixture.AddSymbol(1, "b", "Beta", "class", "b.cs");
        using ResolutionStoreFixture thirdFixture = ResolutionStoreFixture.Create();
        thirdFixture.AddFile(1, "c.cs");
        thirdFixture.AddSymbol(1, "c", "Gamma", "class", "c.cs");

        var store = new RevisionFactCacheStore(byteBudget: 1);
        using (RevisionFactCacheLease first = store.Acquire("ws-a", "r1", firstFixture.OpenRead, firstFixture.Visibility()))
        {
            using (RevisionFactCacheLease second = store.Acquire("ws-b", "r1", secondFixture.OpenRead, secondFixture.Visibility()))
            {
                Assert.Equal(1, store.ScopeCount);
            }
            using RevisionFactCacheLease again = store.Acquire("ws-a", "r1", firstFixture.OpenRead, firstFixture.Visibility());
            Assert.NotSame(first.Cache, again.Cache);
            Assert.Equal("Alpha", System.Linq.Enumerable.Single(again.Cache.SymbolsNamed("Alpha")).Name);
        }

        using (RevisionFactCacheLease third = store.Acquire("ws-c", "r1", thirdFixture.OpenRead, thirdFixture.Visibility()))
        {
            Assert.Equal(1, store.ScopeCount);
            using RevisionFactCacheLease thirdAgain = store.Acquire("ws-c", "r1", thirdFixture.OpenRead, thirdFixture.Visibility());
            Assert.Equal("Gamma", System.Linq.Enumerable.Single(thirdAgain.Cache.SymbolsNamed("Gamma")).Name);
        }
    }

    [Fact]
    public void IsWarm_ReportsFalseUntilALoadCompletes_ThenTrueForTheLoadedAndAdvanceableIdentity()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "a", "Alpha", "class", "a.cs");

        var store = new RevisionFactCacheStore();
        Assert.False(store.IsWarm("ws-a", "rev-1"));

        using (RevisionFactCacheLease lease = store.Acquire("ws-a", "rev-1", fixture.OpenRead, fixture.Visibility()))
        {
        }
        Assert.True(store.IsWarm("ws-a", "rev-1"));
        Assert.True(store.IsWarm("ws-a", "rev-2"));
        Assert.False(store.IsWarm("ws-b", "rev-1"));
    }

    [Fact]
    public async Task WarmInBackground_ColdConcurrentCallsShareOneLoad_AndAWarmScopeSpawnsNothing()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "a", "Alpha", "class", "a.cs");

        var store = new RevisionFactCacheStore();
        using var loadGate = new ManualResetEventSlim(initialState: false);
        int opens = 0;
        Func<Microsoft.Data.Sqlite.SqliteConnection> blockingOpen = () =>
        {
            Interlocked.Increment(ref opens);
            loadGate.Wait(TimeSpan.FromSeconds(10));
            return fixture.OpenRead();
        };

        Task first = store.WarmInBackground("ws-a", "rev-1", blockingOpen, fixture.Visibility());
        Task second = store.WarmInBackground("ws-a", "rev-1", blockingOpen, fixture.Visibility());
        Assert.Same(first, second);

        loadGate.Set();
        await first;

        Assert.True(store.IsWarm("ws-a", "rev-1"));
        Assert.Equal(1, opens);
        Assert.Same(
            Task.CompletedTask,
            store.WarmInBackground("ws-a", "rev-1", blockingOpen, fixture.Visibility()));
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task WarmInBackground_AFaultedLoadClearsItself_SoTheNextCallRetries()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "a.cs");
        fixture.AddSymbol(1, "a", "Alpha", "class", "a.cs");

        var store = new RevisionFactCacheStore();
        int calls = 0;
        Func<Microsoft.Data.Sqlite.SqliteConnection> failingOnce = () =>
            Interlocked.Increment(ref calls) == 1
                ? throw new InvalidOperationException("simulated open failure")
                : fixture.OpenRead();

        Task first = store.WarmInBackground("ws-a", "rev-1", failingOnce, fixture.Visibility());
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.False(store.IsWarm("ws-a", "rev-1"));

        await store.WarmInBackground("ws-a", "rev-1", failingOnce, fixture.Visibility());

        Assert.True(store.IsWarm("ws-a", "rev-1"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Store_IsRegisteredAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RevisionFactCacheStore>();
        ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<RevisionFactCacheStore>(),
            provider.GetRequiredService<RevisionFactCacheStore>());
    }

    [Fact]
    public void StoreCatalog_ProducesScopedQmlCandidatesFromManifestAndImports()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(fixture);

        using RevisionFactCacheLease lease = new RevisionFactCacheStore().Acquire(
            "qml-store",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());
        IResolutionFacts facts = lease.Cache;

        QmlVisibleType[] candidates = facts.QmlTypesVisibleTo(1).ToArray();

        Assert.Equal(QmlVisibilityFixtureSupport.ExpectedExportedNames, candidates.Select(candidate => candidate.ExportedName));
        Assert.Equal(
            ["local", "remote", "remote", "theme", "theme", "source"],
            candidates.Select(candidate => candidate.Target.SymbolId));

        QmlVisibleType local = candidates[0];
        Assert.Equal("LocalCard.qml", local.Evidence.SourcePath);
        Assert.Equal("qml.component", local.Evidence.Provenance);
        Assert.Equal(0, local.Evidence.StartByte);
        Assert.Equal(1, local.Evidence.EndByte);

        QmlVisibleType directoryRemote = candidates[1];
        Assert.Equal("Components", directoryRemote.ImportAlias);
        Assert.Equal(QmlVisibilityScope.ForDirectory("components"), directoryRemote.Scope);
        Assert.Equal("components/RemoteCard.qml", directoryRemote.SourceComponentPath);
        Assert.Equal(new QmlVersionConstraint(new QmlVersion(1, 0), new QmlVersion(1, 0)), directoryRemote.VersionConstraint);
        Assert.False(directoryRemote.IsSingleton);
        Assert.Equal("components/qmldir", directoryRemote.Evidence.SourcePath);
        Assert.Equal("qmldir", directoryRemote.Evidence.Provenance);
        Assert.Equal(20, directoryRemote.Evidence.StartByte);
        Assert.Equal(40, directoryRemote.Evidence.EndByte);

        QmlVisibleType moduleTheme = candidates[4];
        Assert.Equal("EC", moduleTheme.ImportAlias);
        Assert.Equal(QmlVisibilityScope.ForModule("Example.Components"), moduleTheme.Scope);
        Assert.Equal("components/Theme.qml", moduleTheme.SourceComponentPath);
        Assert.True(moduleTheme.IsSingleton);
    }

    [Fact]
    public void StoreCatalog_DropsMalformedAndFutureManifestFacts()
    {
        using ResolutionStoreFixture malformedFixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(malformedFixture);
        malformedFixture.ExecuteWrite("UPDATE structural_facts SET metadata_json='not-json' WHERE structural_fact_id='fact-remote';");
        using RevisionFactCacheLease malformedLease = new RevisionFactCacheStore().Acquire(
            "qml-store-malformed",
            "qml-rev-1",
            malformedFixture.OpenRead,
            malformedFixture.Visibility());
        IResolutionFacts malformed = malformedLease.Cache;

        Assert.Equal(
            ["LocalCard", "Theme", "Theme", "source"],
            malformed.QmlTypesVisibleTo(1).Select(candidate => candidate.ExportedName));

        using ResolutionStoreFixture futureFixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(futureFixture);
        futureFixture.ExecuteWrite("UPDATE structural_facts SET pattern_id='qmldir.object_type.v2' WHERE structural_fact_id='fact-remote';");
        using RevisionFactCacheLease futureLease = new RevisionFactCacheStore().Acquire(
            "qml-store-future",
            "qml-rev-1",
            futureFixture.OpenRead,
            futureFixture.Visibility());
        IResolutionFacts future = futureLease.Cache;

        Assert.Equal(
            ["LocalCard", "Theme", "Theme", "source"],
            future.QmlTypesVisibleTo(1).Select(candidate => candidate.ExportedName));
    }

    [Fact]
    public void StoreCatalog_RetainsPublicEntriesWhenTypeInfoModelIsMissing()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(fixture);
        fixture.ExecuteWrite(
            """
            UPDATE structural_facts
            SET metadata_json='{"directive":"typeinfo","file":"Missing.qmltypes","pattern_version":1,"query_family":"qmldir"}'
            WHERE structural_fact_id='fact-typeinfo';
            """);

        using RevisionFactCacheLease lease = new RevisionFactCacheStore().Acquire(
            "qml-store-missing-typeinfo",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());
        IResolutionFacts facts = lease.Cache;

        QmlVisibleType remote = Assert.Single(
            facts.QmlTypesVisibleTo(1),
            candidate => candidate.ExportedName == "RemoteCard" && candidate.ImportAlias == "EC");
        Assert.Equal("remote", remote.Target.SymbolId);
        Assert.DoesNotContain(
            facts.QmlTypesVisibleTo(1),
            candidate => candidate.ExportedName == "InternalCard" && candidate.ImportAlias == "EC");
    }

    [Fact]
    public void StoreCatalog_BindsExportAliasToTheComponentTarget()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(fixture);
        fixture.ExecuteWrite(
            """
            UPDATE structural_facts
            SET metadata_json='{"directive":"object_type","file":"RemoteCard.qml","pattern_version":1,"query_family":"qmldir","type_name":"FancyRemote","version":"1.0"}'
            WHERE structural_fact_id='fact-remote';
            """);

        using RevisionFactCacheLease lease = new RevisionFactCacheStore().Acquire(
            "qml-store-export-alias",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());
        IResolutionFacts facts = lease.Cache;

        QmlVisibleType remote = Assert.Single(
            facts.QmlTypesVisibleTo(1),
            candidate => candidate.ExportedName == "FancyRemote" && candidate.ImportAlias == "Components");
        Assert.Equal("remote", remote.Target.SymbolId);
        Assert.Contains(
            facts.QmlTypesVisibleTo(1),
            candidate => candidate.ExportedName == "FancyRemote" && candidate.ImportAlias == "EC");
    }

    [Fact]
    public void StoreCatalog_DotImportDoesNotMakeTheSameTargetAmbiguous()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(fixture);
        fixture.AddSymbol(
            1,
            "import-current",
            ".",
            "import",
            "source.qml",
            language: "qml",
            metadataJson: """{"import_kind":"directory","source":"."}""");

        using RevisionFactCacheLease lease = new RevisionFactCacheStore().Acquire(
            "qml-store-dot-import",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());
        IResolutionFacts facts = lease.Cache;

        ResolutionOutcome outcome = new QueryTimeResolver(facts).Resolve(new ResolutionInput(
            ResolutionOrigin.Pending,
            ResolutionRefKind.Instantiates,
            "qml",
            1,
            "LocalCard",
            null,
            null,
            null,
            1.0,
            "source.qml"));

        Assert.Equal(ResolutionOutcomeKind.Resolved, outcome.Kind);
        Assert.Equal(new FactSymbolKey(2, "local"), outcome.Target);
    }

    [Fact]
    public void StoreCatalog_NormalizesBackslashComponentPaths()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(fixture);
        fixture.ExecuteWrite(
            """
            UPDATE file_versions SET path='components\RemoteCard.qml' WHERE version_id=3;
            UPDATE manifest_entries SET path='components\RemoteCard.qml' WHERE version_id=3;
            UPDATE symbols SET path='components\RemoteCard.qml' WHERE version_id=3;
            """);

        using RevisionFactCacheLease lease = new RevisionFactCacheStore().Acquire(
            "qml-store-paths",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());
        IResolutionFacts facts = lease.Cache;
        QmlVisibleType remote = Assert.Single(
            facts.QmlTypesVisibleTo(1),
            candidate => candidate.Target.SymbolId == "remote" && candidate.ImportAlias == "Components");

        Assert.Equal("components/RemoteCard.qml", remote.SourceComponentPath);
    }

    [Fact]
    public void StoreCatalog_EmitsSameFileCandidateWithComponentEvidence()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(fixture);
        using RevisionFactCacheLease lease = new RevisionFactCacheStore().Acquire(
            "qml-store-same-file",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());
        IResolutionFacts facts = lease.Cache;

        QmlVisibleType sameFile = Assert.Single(
            facts.QmlTypesVisibleTo(1),
            candidate => candidate.Target.SymbolId == "source");

        Assert.Equal("source.qml", sameFile.Evidence.SourcePath);
        Assert.Equal("qml.component", sameFile.Evidence.Provenance);
        Assert.Equal(0, sameFile.Evidence.StartByte);
        Assert.Equal(1, sameFile.Evidence.EndByte);
    }

    [Fact]
    public void StoreCatalog_AllowsUnversionedManifestEntryForVersionedImport()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(fixture);
        fixture.ExecuteWrite(
            """
            UPDATE structural_facts
            SET metadata_json='{"directive":"singleton","file":"Theme.qml","pattern_version":1,"query_family":"qmldir","singleton":true,"type_name":"Theme"}'
            WHERE structural_fact_id='fact-theme';
            """);

        using RevisionFactCacheLease lease = new RevisionFactCacheStore().Acquire(
            "qml-store-unversioned-entry",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());
        IResolutionFacts facts = lease.Cache;

        Assert.Contains(
            facts.QmlTypesVisibleTo(1),
            candidate => candidate.Target.SymbolId == "theme" && candidate.ImportAlias == "EC");
    }

    [Fact]
    public void StoreCatalog_UsesImportEvidenceForManifestlessDirectoryCandidate()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(fixture);
        fixture.AddFile(8, "loose/LooseCard.qml", "qml");
        fixture.AddSymbol(8, "loose-card", "LooseCard", "class", "loose/LooseCard.qml", language: "qml");
        fixture.AddSymbol(
            1,
            "import-loose",
            "loose",
            "import",
            "source.qml",
            language: "qml",
            metadataJson: """{"import_kind":"directory","source":"loose","alias":"Loose","local_name":"Loose","is_namespace":true}""");

        using RevisionFactCacheLease lease = new RevisionFactCacheStore().Acquire(
            "qml-store-loose-dir",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());
        IResolutionFacts facts = lease.Cache;
        QmlVisibleType loose = Assert.Single(
            facts.QmlTypesVisibleTo(1),
            candidate => candidate.Target.SymbolId == "loose-card");

        Assert.Equal("source.qml", loose.Evidence.SourcePath);
        Assert.Equal("qml.import", loose.Evidence.Provenance);
        Assert.Equal(0, loose.Evidence.StartByte);
        Assert.Equal(1, loose.Evidence.EndByte);
    }

    [Fact]
    public void CacheResourceState_TwoRetainedScopes_ReportsBothInRetainedAndLiveUnion()
    {
        var obj1 = new object();
        var obj2 = new object();
        var retained = new HashSet<object> { obj1, obj2 };
        var active = new HashSet<object>();
        var bytes = new Dictionary<object, long> { [obj1] = 100L, [obj2] = 200L };

        var state = new CacheResourceState(retained, active, bytes);
        CacheResourceSnapshot snapshot = state.ToSnapshot();

        Assert.Equal(2, snapshot.RetainedEntryCount);
        Assert.Equal(300L, snapshot.RetainedBytes);
        Assert.Equal(0, snapshot.ActiveLeaseCount);
        Assert.Equal(0L, snapshot.ActiveBytes);
        Assert.Equal(0, snapshot.EvictedHeldEntryCount);
        Assert.Equal(0L, snapshot.EvictedHeldBytes);
        Assert.Equal(2, snapshot.UniqueLiveEntryCount);
        Assert.Equal(300L, snapshot.UniqueLiveBytes);
        Assert.Equal(0, snapshot.OversizedEntryCount);
    }

    [Fact]
    public void CacheResourceState_TwoIdentities_ReportsEvictedHeldAndRetainedSeparately()
    {
        var oldRev = new object();
        var newRev = new object();
        var retained = new HashSet<object> { newRev };
        var active = new HashSet<object> { oldRev };
        var bytes = new Dictionary<object, long> { [oldRev] = 100L, [newRev] = 150L };

        var state = new CacheResourceState(retained, active, bytes);
        CacheResourceSnapshot snapshot = state.ToSnapshot(activeLeaseCount: 1);

        Assert.Equal(1, snapshot.RetainedEntryCount);
        Assert.Equal(150L, snapshot.RetainedBytes);
        Assert.Equal(1, snapshot.ActiveLeaseCount);
        Assert.Equal(100L, snapshot.ActiveBytes);
        Assert.Equal(1, snapshot.EvictedHeldEntryCount);
        Assert.Equal(100L, snapshot.EvictedHeldBytes);
        Assert.Equal(2, snapshot.UniqueLiveEntryCount);
        Assert.Equal(250L, snapshot.UniqueLiveBytes);
        Assert.Equal(0, snapshot.OversizedEntryCount);
    }

    [Fact]
    public void CacheResourceState_OverlappingRetainedAndActive_UnionAvoidsDoubleCounting()
    {
        var sharedObj = new object();
        var retained = new HashSet<object> { sharedObj };
        var active = new HashSet<object> { sharedObj };
        var bytes = new Dictionary<object, long> { [sharedObj] = 500L };

        var state = new CacheResourceState(retained, active, bytes);
        CacheResourceSnapshot snapshot = state.ToSnapshot(activeLeaseCount: 3);

        Assert.Equal(1, snapshot.RetainedEntryCount);
        Assert.Equal(500L, snapshot.RetainedBytes);
        Assert.Equal(3, snapshot.ActiveLeaseCount);
        Assert.Equal(500L, snapshot.ActiveBytes);
        Assert.Equal(0, snapshot.EvictedHeldEntryCount);
        Assert.Equal(0L, snapshot.EvictedHeldBytes);
        Assert.Equal(1, snapshot.UniqueLiveEntryCount);
        Assert.Equal(500L, snapshot.UniqueLiveBytes);
    }

    [Fact]
    public void CacheResourceState_OversizedEntry_ReportsOversizedCountWithoutFailing()
    {
        var bigObj = new object();
        long budget = 256L * 1024 * 1024;
        long bigBytes = budget + 1024;
        var retained = new HashSet<object> { bigObj };
        var active = new HashSet<object>();
        var bytes = new Dictionary<object, long> { [bigObj] = bigBytes };

        var state = new CacheResourceState(retained, active, bytes);
        CacheResourceSnapshot snapshot = state.ToSnapshot(byteBudget: budget);

        Assert.Equal(1, snapshot.RetainedEntryCount);
        Assert.Equal(bigBytes, snapshot.RetainedBytes);
        Assert.Equal(1, snapshot.UniqueLiveEntryCount);
        Assert.Equal(bigBytes, snapshot.UniqueLiveBytes);
        Assert.Equal(1, snapshot.OversizedEntryCount);
    }

    [Fact]
    public void GetResourceSnapshot_RetainedSqliteFixture_MatchesResidentBytesEstimate()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "source.cs");
        fixture.AddSymbol(1, "sym", "Sym", "class", "source.cs");

        var store = new RevisionFactCacheStore();
        using (var lease = store.Acquire("ws-1", "rev-1", fixture.OpenRead, fixture.Visibility()))
        {
        }

        CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
        Assert.Equal(1, snapshot.RetainedEntryCount);
        Assert.True(snapshot.RetainedBytes > 0);
        Assert.Equal(store.ResidentBytes, snapshot.RetainedBytes);
        Assert.Equal(0, snapshot.ActiveLeaseCount);
        Assert.Equal(0L, snapshot.ActiveBytes);
        Assert.Equal(0, snapshot.EvictedHeldEntryCount);
        Assert.Equal(0L, snapshot.EvictedHeldBytes);
        Assert.Equal(1, snapshot.UniqueLiveEntryCount);
        Assert.Equal(snapshot.RetainedBytes, snapshot.UniqueLiveBytes);
        Assert.Equal(0, snapshot.OversizedEntryCount);
    }

    [Fact]
    public async Task GetResourceSnapshot_DoesNotForceLazyLoadUnderGate()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "source.cs");
        fixture.AddSymbol(1, "sym", "Sym", "class", "source.cs");

        var store = new RevisionFactCacheStore();
        using var blocker = new ManualResetEventSlim(initialState: false);
        int opens = 0;

        Task warm = store.WarmInBackground(
            "ws-1",
            "rev-1",
            () =>
            {
                Interlocked.Increment(ref opens);
                blocker.Wait(TimeSpan.FromSeconds(5));
                return fixture.OpenRead();
            },
            fixture.Visibility());

        // Snapshot while warm is in-flight:
        CacheResourceSnapshot snapshot = store.GetResourceSnapshot();
        Assert.Equal(0, snapshot.RetainedEntryCount);
        Assert.Equal(0L, snapshot.RetainedBytes);
        Assert.Equal(0, snapshot.UniqueLiveEntryCount);
        Assert.Equal(0L, snapshot.UniqueLiveBytes);

        blocker.Set();
        await warm;

        CacheResourceSnapshot after = store.GetResourceSnapshot();
        Assert.Equal(1, after.RetainedEntryCount);
        Assert.True(after.RetainedBytes > 0);
    }
}
