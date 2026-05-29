using Miller.Indexing;
using Miller.Server;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the M3 repoint (implementation-order step 10): <see cref="SearchTool"/>, <see cref="InspectTool"/>, and
/// <see cref="SmartTargetResolver"/> depend on <see cref="IndexHolder"/> and read <c>holder.Current</c> per
/// call — so an <see cref="IndexHolder.Swap"/> behind a live instance is observed on the NEXT call without
/// reconstructing the tool/resolver. Before the swap a symbol is absent; after, it is present. This proves the
/// freshness rebuild actually reaches the read tools, which is the whole point of the holder seam.
/// </summary>
public sealed class HolderRepointTests
{
    private static MillerRepositoryIndex BuildIndex(JulieDbFixture fx) =>
        MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));

    // An index WITHOUT the marker symbol, and one WITH it — the swap moves between them.
    private static JulieDbFixture WithoutMarker() => JulieDbFixture.Create(26, "1", new[]
    {
        new JulieDbFixture.SymbolRow("a0001122334455667788990a1b2c3d4e", "ExistingType", "class", "csharp",
            "src/Existing.cs", "public class ExistingType", 1, null),
    });

    private static JulieDbFixture WithMarker() => JulieDbFixture.Create(26, "1", new[]
    {
        new JulieDbFixture.SymbolRow("a0001122334455667788990a1b2c3d4e", "ExistingType", "class", "csharp",
            "src/Existing.cs", "public class ExistingType", 1, null),
        new JulieDbFixture.SymbolRow("b0001122334455667788990a1b2c3d4e", "Zebraphone", "class", "csharp",
            "src/Fresh.cs", "public class Zebraphone", 1, null),
    });

    [Fact]
    public void SearchTool_OnHolder_SeesSwappedIndexOnNextCall()
    {
        using var before = WithoutMarker();
        using var after = WithMarker();
        var holder = new IndexHolder(BuildIndex(before), builtRevision: 1);
        var tool = new SearchTool(holder);

        Assert.Equal("No results.", tool.Search("Zebraphone").Trim());

        holder.Swap(BuildIndex(after), revision: 2);

        Assert.Contains("Zebraphone", tool.Search("Zebraphone"));
    }

    [Fact]
    public void SmartTargetResolver_OnHolder_ResolvesAgainstTheCurrentIndex()
    {
        using var before = WithoutMarker();
        using var after = WithMarker();
        var holder = new IndexHolder(BuildIndex(before), builtRevision: 1);
        var resolver = new SmartTargetResolver(holder);

        Assert.IsType<TargetResolution.NotFound>(resolver.Resolve("Zebraphone"));

        holder.Swap(BuildIndex(after), revision: 2);

        Assert.IsType<TargetResolution.Symbol>(resolver.Resolve("Zebraphone"));
    }

    [Fact]
    public void InspectTool_OnHolder_SeesSwappedIndexOnNextCall()
    {
        using var before = WithoutMarker();
        using var after = WithMarker();
        var holder = new IndexHolder(BuildIndex(before), builtRevision: 1);
        // InspectTool reads the extract DB for detail; point it at the "after" DB (where the symbol's file lives).
        var workspace = WorkspaceContext.Create(Path.GetTempPath(), AppContext.BaseDirectory)
            with { ExtractDbPath = after.DbPath };
        var resolver = new SmartTargetResolver(holder);
        var tool = new InspectTool(holder, resolver, workspace);

        string beforeOut = tool.Inspect("Zebraphone");
        Assert.Contains("not found", beforeOut);

        holder.Swap(BuildIndex(after), revision: 2);

        string afterOut = tool.Inspect("Zebraphone");
        Assert.Contains("Zebraphone", afterOut);
    }
}
