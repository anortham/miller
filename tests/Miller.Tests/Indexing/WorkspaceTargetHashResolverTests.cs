using System.Security.Cryptography;
using System.Text;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class WorkspaceTargetHashResolverTests
{
    [Fact]
    public void Resolve_RecoversKnownSymbolAndFileHashesWithoutRawTelemetry()
    {
        using JulieDbFixture fx = JulieDbFixture.CreateForInspect();
        var frequencies = new[]
        {
            new TargetHashFrequency(Hash(JulieDbFixture.GetUserId), calls: 5),
            new TargetHashFrequency(Hash("GetUser"), calls: 4),
            new TargetHashFrequency(Hash("auth/UserService.cs"), calls: 3),
            new TargetHashFrequency(Hash("auth/UserService.cs:GetUser"), calls: 2),
            new TargetHashFrequency(Hash("not-in-index"), calls: 7),
        };

        IReadOnlyList<RecoveredTargetHash> recovered = WorkspaceTargetHashResolver.Resolve(fx.DbPath, frequencies);

        Assert.Contains(recovered, row =>
            row.Confidence == "symbol_id_hash" &&
            row.SymbolId == JulieDbFixture.GetUserId &&
            row.Name == "GetUser" &&
            row.Path == "auth/UserService.cs" &&
            row.Calls == 5);
        Assert.Contains(recovered, row =>
            row.Confidence == "symbol_name_hash" &&
            row.Name == "GetUser" &&
            row.CandidateCount == 1 &&
            row.Calls == 4);
        Assert.Contains(recovered, row =>
            row.Confidence == "file_path_hash" &&
            row.Path == "auth/UserService.cs" &&
            row.Calls == 3);
        Assert.Contains(recovered, row =>
            row.Confidence == "scoped_symbol_hash" &&
            row.Name == "GetUser" &&
            row.Path == "auth/UserService.cs" &&
            row.Calls == 2);
        Assert.Contains(recovered, row =>
            row.Confidence == "unresolved_hash" &&
            row.Name is null &&
            row.Path is null &&
            row.Calls == 7);
    }

    [Fact]
    public void Resolve_NameHashMarksAmbiguousMatches()
    {
        using JulieDbFixture fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("a100", "Run", "method", "csharp", "A.cs", "void Run()", 1, null),
                new JulieDbFixture.SymbolRow("b100", "Run", "method", "csharp", "B.cs", "void Run()", 1, null),
            });

        RecoveredTargetHash recovered = Assert.Single(
            WorkspaceTargetHashResolver.Resolve(fx.DbPath, new[] { new TargetHashFrequency(Hash("Run"), 9) }));

        Assert.Equal("symbol_name_hash", recovered.Confidence);
        Assert.Equal("Run", recovered.Name);
        Assert.Equal(2, recovered.CandidateCount);
        Assert.Equal(9, recovered.Calls);
    }

    private static string Hash(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
