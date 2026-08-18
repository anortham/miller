using Microsoft.Extensions.DependencyInjection;
using Miller.Core.Resolution;
using Miller.Indexing.Resolution;
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
}
