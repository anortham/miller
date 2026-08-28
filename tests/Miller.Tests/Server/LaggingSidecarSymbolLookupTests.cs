using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class LaggingSidecarSymbolLookupTests
{
    [Fact]
    public void FindByName_ReplacesLiveRows_AndRetainsSidecarDocIdsAcrossBatches()
    {
        var targetRows = Enumerable.Range(0, 501)
            .Select(index => new JulieDbFixture.SymbolRow(
                index.ToString("x32"),
                "Needle",
                "method",
                "csharp",
                $"selected/{index:D3}.cs",
                "void Needle()",
                index + 1,
                null))
            .ToArray();
        var sidecarRows = targetRows
            .Prepend(new JulieDbFixture.SymbolRow(
                "ffffffffffffffffffffffffffffffff",
                "OutsideSidecar",
                "method",
                "csharp",
                "aaa-sidecar.cs",
                "void OutsideSidecar()",
                1,
                null))
            .ToArray();
        var liveRows = targetRows
            .Select(row => row with { Name = "NeedleLive" })
            .Append(new JulieDbFixture.SymbolRow(
                "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                "OutsideLive",
                "method",
                "csharp",
                "zzz-live.cs",
                "void OutsideLive()",
                1,
                null))
            .ToArray();
        using var sidecarFx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            sidecarRows);
        using var liveFx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            liveRows);

        var sidecar = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(sidecarFx.DbPath));
        using var live = LegacyArtifactReadSession.Open(liveFx.DbPath, liveFx.WorkspaceRoot);
        ISymbolLookupIndex lookup = LaggingSidecarSymbolLookup.Wrap(sidecar, servedStampLagsLive: true, live);

        IReadOnlyList<IndexedSymbol> expected = sidecar.FindByName("Needle");
        IReadOnlyList<IndexedSymbol> actual = lookup.FindByName("Needle");

        Assert.Equal(501, actual.Count);
        Assert.Equal(expected.Select(row => row.DocId), actual.Select(row => row.DocId));
        Assert.Equal(expected.Select(row => row.SymbolId), actual.Select(row => row.SymbolId));
        Assert.All(actual, row => Assert.Equal("NeedleLive", row.Name));

        IndexedSymbol resolved = lookup.Resolve(expected[250].DocId);
        Assert.Equal(expected[250].DocId, resolved.DocId);
        Assert.Equal("NeedleLive", resolved.Name);

        IReadOnlyDictionary<int, IndexedSymbol> resolvedMany = lookup.ResolveMany(
            expected.Select(static row => row.DocId).ToArray());
        Assert.Equal(expected.Select(static row => row.DocId), resolvedMany.Keys);
        Assert.Equal(expected.Select(static row => row.DocId), resolvedMany.Values.Select(static row => row.DocId));
        Assert.All(resolvedMany.Values, row => Assert.Equal("NeedleLive", row.Name));
    }
}
