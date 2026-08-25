using Microsoft.Extensions.DependencyInjection;
using Miller.Core.Resolution;
using Miller.Indexing.Resolution;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing.Resolution;

public sealed class RevisionFactCacheStoreTests
{
    [Fact]
    public void GetOrAdvance_KeepsOneRevisionPerScopeAndAdvances()
    {
        using ResolutionStoreFixture fixture = ResolutionStoreFixture.Create();
        fixture.AddFile(1, "keep.cs");
        fixture.AddFile(2, "change.cs");
        fixture.AddSymbol(1, "kept", "Kept", "class", "keep.cs");
        fixture.AddSymbol(2, "old", "Old", "class", "change.cs");

        var store = new RevisionFactCacheStore();
        RevisionFactCache first = store.GetOrAdvance("ws-a", "rev-1", fixture.OpenRead, fixture.Visibility());
        FactSymbol[] kept = first.SymbolsOfVersion(1);
        RevisionFactCache same = store.GetOrAdvance("ws-a", "rev-1", fixture.OpenRead, fixture.Visibility());
        Assert.Same(first, same);
        Assert.Equal(1, store.ScopeCount);

        fixture.AddSymbol(3, "neu", "New", "class", "change.cs");
        fixture.FlipManifest(2, [("keep.cs", 1, "csharp", "indexed"), ("change.cs", 3, "csharp", "indexed")]);

        RevisionFactCache second = store.GetOrAdvance("ws-a", "rev-2", fixture.OpenRead, fixture.Visibility());
        Assert.NotSame(first, second);
        Assert.Same(kept, second.SymbolsOfVersion(1));
        Assert.Equal("New", System.Linq.Enumerable.Single(second.SymbolsNamed("New")).Name);
        Assert.Equal(1, store.ScopeCount);
    }

    [Fact]
    public void GetOrAdvance_EvictsLeastRecentlyUsedScopeWhenOverBudget()
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
        RevisionFactCache first = store.GetOrAdvance("ws-a", "r1", firstFixture.OpenRead, firstFixture.Visibility());
        _ = store.GetOrAdvance("ws-b", "r1", secondFixture.OpenRead, secondFixture.Visibility());
        Assert.Equal(1, store.ScopeCount);
        RevisionFactCache again = store.GetOrAdvance("ws-a", "r1", firstFixture.OpenRead, firstFixture.Visibility());
        Assert.NotSame(first, again);
        Assert.Equal("Alpha", System.Linq.Enumerable.Single(again.SymbolsNamed("Alpha")).Name);

        store.GetOrAdvance("ws-c", "r1", thirdFixture.OpenRead, thirdFixture.Visibility());
        Assert.Equal(1, store.ScopeCount);
        Assert.Equal("Gamma", System.Linq.Enumerable.Single(
            store.GetOrAdvance("ws-c", "r1", thirdFixture.OpenRead, thirdFixture.Visibility()).SymbolsNamed("Gamma")).Name);
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

        IResolutionFacts facts = new RevisionFactCacheStore().GetOrAdvance(
            "qml-store",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());

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
        IResolutionFacts malformed = new RevisionFactCacheStore().GetOrAdvance(
            "qml-store-malformed",
            "qml-rev-1",
            malformedFixture.OpenRead,
            malformedFixture.Visibility());

        Assert.Equal(
            ["LocalCard", "Theme", "Theme", "source"],
            malformed.QmlTypesVisibleTo(1).Select(candidate => candidate.ExportedName));

        using ResolutionStoreFixture futureFixture = ResolutionStoreFixture.Create();
        QmlVisibilityFixtureSupport.Populate(futureFixture);
        futureFixture.ExecuteWrite("UPDATE structural_facts SET pattern_id='qmldir.object_type.v2' WHERE structural_fact_id='fact-remote';");
        IResolutionFacts future = new RevisionFactCacheStore().GetOrAdvance(
            "qml-store-future",
            "qml-rev-1",
            futureFixture.OpenRead,
            futureFixture.Visibility());

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

        IResolutionFacts facts = new RevisionFactCacheStore().GetOrAdvance(
            "qml-store-missing-typeinfo",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());

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

        IResolutionFacts facts = new RevisionFactCacheStore().GetOrAdvance(
            "qml-store-export-alias",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());

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

        IResolutionFacts facts = new RevisionFactCacheStore().GetOrAdvance(
            "qml-store-dot-import",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());

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

        IResolutionFacts facts = new RevisionFactCacheStore().GetOrAdvance(
            "qml-store-paths",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());
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
        IResolutionFacts facts = new RevisionFactCacheStore().GetOrAdvance(
            "qml-store-same-file",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());

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

        IResolutionFacts facts = new RevisionFactCacheStore().GetOrAdvance(
            "qml-store-unversioned-entry",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());

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

        IResolutionFacts facts = new RevisionFactCacheStore().GetOrAdvance(
            "qml-store-loose-dir",
            "qml-rev-1",
            fixture.OpenRead,
            fixture.Visibility());
        QmlVisibleType loose = Assert.Single(
            facts.QmlTypesVisibleTo(1),
            candidate => candidate.Target.SymbolId == "loose-card");

        Assert.Equal("source.qml", loose.Evidence.SourcePath);
        Assert.Equal("qml.import", loose.Evidence.Provenance);
        Assert.Equal(0, loose.Evidence.StartByte);
        Assert.Equal(1, loose.Evidence.EndByte);
    }
}
