using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class CloneGroupReaderTests
{
    [Fact]
    public void Read_LimitFive_ReturnsTheTopFiveGroupsRankedByCloneCount()
    {
        using JulieDbFixture fixture = CreateCloneFixture();

        IReadOnlyList<CloneGroup> groups = CloneGroupReader.Read(
            fixture.DbPath, limit: 5, minCount: 2, symbolsPerGroup: 3);

        Assert.Equal(
            ["hash-a", "hash-b", "hash-c", "hash-d", "hash-e"],
            groups.Select(group => group.BodyHash));
        Assert.Equal([6, 5, 4, 3, 2], groups.Select(group => group.Count));
    }

    [Fact]
    public void Read_SymbolsPerGroup_CapsListedSymbolsButStillReportsTheTrueCloneCount()
    {
        using JulieDbFixture fixture = CreateCloneFixture();

        CloneGroup largest = CloneGroupReader
            .Read(fixture.DbPath, limit: 5, minCount: 2, symbolsPerGroup: 3)
            .First();

        Assert.Equal(6, largest.Count);
        Assert.Equal(3, largest.Symbols.Count);
        Assert.Equal(["a0.cs", "a1.cs", "a2.cs"], largest.Symbols.Select(symbol => symbol.Path));
    }

    [Fact]
    public void Read_MinCount_ExcludesGroupsBelowTheThreshold()
    {
        using JulieDbFixture fixture = CreateCloneFixture();

        IReadOnlyList<CloneGroup> groups = CloneGroupReader.Read(
            fixture.DbPath, limit: 50, minCount: 4, symbolsPerGroup: 25);

        Assert.Equal(["hash-a", "hash-b", "hash-c"], groups.Select(group => group.BodyHash));
    }

    [Fact]
    public void Read_IgnoresSymbolsWithNoBodyHash()
    {
        using JulieDbFixture fixture = CreateCloneFixture();

        IReadOnlyList<CloneGroup> groups = CloneGroupReader.Read(
            fixture.DbPath, limit: 50, minCount: 2, symbolsPerGroup: 25);

        Assert.DoesNotContain("", groups.Select(group => group.BodyHash));
        Assert.All(groups, group => Assert.All(group.Symbols, symbol => Assert.NotEqual("unhashed.cs", symbol.Path)));
    }

    [Fact]
    public void Read_LimitBelowOne_IsClampedInsteadOfReturningEverything()
    {
        using JulieDbFixture fixture = CreateCloneFixture();

        IReadOnlyList<CloneGroup> groups = CloneGroupReader.Read(
            fixture.DbPath, limit: 0, minCount: 2, symbolsPerGroup: 25);

        Assert.Single(groups);
        Assert.Equal("hash-a", groups[0].BodyHash);
    }

    private static JulieDbFixture CreateCloneFixture()
    {
        var rows = new List<JulieDbFixture.SymbolRow>();
        foreach ((string hash, int count) in new[]
                 {
                     ("hash-a", 6), ("hash-b", 5), ("hash-c", 4),
                     ("hash-d", 3), ("hash-e", 2), ("hash-f", 2),
                     ("hash-lonely", 1),
                 })
        {
            for (int i = 0; i < count; i++)
                rows.Add(Symbol($"{hash}-{i}", $"{hash[5..]}{i}.cs", i + 1, hash));
        }

        rows.Add(Symbol("no-hash", "unhashed.cs", 1, null));
        rows.Add(Symbol("blank-hash", "unhashed.cs", 2, ""));

        return JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows);
    }

    private static JulieDbFixture.SymbolRow Symbol(string id, string path, int line, string? bodyHash) =>
        new(id, id, "method", "csharp", path, $"void {id}()", line, null) { BodyHash = bodyHash };
}
