using Microsoft.Data.Sqlite;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ReopeningMillerFactSourceTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-reopen-facts-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Select_after_index_identity_swap_uses_only_the_new_generation()
    {
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedCase(store, "tc:gen-a", "sym:test-a", "tests/ATests.cs", "test_a");
        SeedCase(store, "tc:gen-b", "sym:test-b", "tests/BTests.cs", "test_b");

        var generationA = new FakeMillerFactSource { Current = new CtIndexCursor("gen-a", 1) };
        generationA.Symbols.Add(FakeMillerFactSource.Symbol("sym:app-a", "AppA", "src/App.cs"));
        generationA.Tests.Add(FakeMillerFactSource.Hit("sym:test-a", "test_a", "tests/ATests.cs", isTest: true));

        var generationB = new FakeMillerFactSource { Current = new CtIndexCursor("gen-b", 1) };
        generationB.Symbols.Add(FakeMillerFactSource.Symbol("sym:app-b", "AppB", "src/App.cs"));
        generationB.Tests.Add(FakeMillerFactSource.Hit("sym:test-b", "test_b", "tests/BTests.cs", isTest: true));

        FakeMillerFactSource current = generationA;
        int opens = 0;
        var facts = new ReopeningMillerFactSource(() =>
        {
            opens++;
            return current;
        });
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult first = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: EngineTestSupport.WorkspaceId,
            ChangedPaths: ["src/App.cs"]));

        Assert.Equal(["tc:gen-a"], first.SelectedTestCaseIds);
        Assert.DoesNotContain("tc:gen-b", first.SelectedTestCaseIds);
        int opensAfterFirst = opens;
        Assert.True(opensAfterFirst > 0);

        current = generationB;

        ContinuousTestSelectionResult second = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: EngineTestSupport.WorkspaceId,
            ChangedPaths: ["src/App.cs"]));

        Assert.Equal(["tc:gen-b"], second.SelectedTestCaseIds);
        Assert.DoesNotContain("tc:gen-a", second.SelectedTestCaseIds);
        Assert.True(opens > opensAfterFirst);
        Assert.Equal(new CtFreshnessKey("gen-b", 1), facts.Freshness);
    }

    private static void SeedCase(
        ContinuousTestStore store,
        string id,
        string symbolId,
        string path,
        string name)
    {
        store.PutTestCase(new ContinuousTestCase(
            Id: id,
            WorkspaceId: EngineTestSupport.WorkspaceId,
            Name: name,
            QualifiedName: name,
            Selector: $"{path}::{name}",
            FilePath: path,
            SymbolName: symbolId,
            Source: "ct-provider:dotnet"));
    }
}
