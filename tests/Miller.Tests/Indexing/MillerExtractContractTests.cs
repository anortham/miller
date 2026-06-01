using System.Text.Json;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class MillerExtractContractTests
{
    [Fact]
    public void ContractPinsJulieExtractV1Versions()
    {
        Assert.Equal(1, MillerExtractContract.ExpectedSchemaVersion);
        Assert.Equal(1, MillerExtractContract.ExpectedSqliteSchemaVersion);
        Assert.Equal(1, MillerExtractContract.ExpectedExtractContractVersion);
        Assert.Equal(1, MillerExtractContract.ExpectedReportSchemaVersion);
        Assert.Equal("blake3", MillerExtractContract.ExpectedHashAlgorithm);
        Assert.False(string.IsNullOrWhiteSpace(MillerExtractContract.PinnedJulieExtractVersion));
    }

    [Fact]
    public void JuliePinsJsonMatchesContractVersion()
    {
        string pinsPath = Path.Combine(ScaleTestSupport.RepoRoot(), "scripts", "julie-pins.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(pinsPath));

        string pinnedVersion = MillerExtractContract.PinnedJulieExtractVersion;
        Assert.Equal(pinnedVersion, doc.RootElement.GetProperty("version").GetString());

        foreach (JsonProperty asset in doc.RootElement.GetProperty("assets").EnumerateObject())
        {
            string? name = asset.Value.GetProperty("name").GetString();
            string? sha256 = asset.Value.GetProperty("sha256").GetString();
            // The pins 'name' carries a literal {VER} placeholder; substitute before asserting (reconciliation #4).
            string? resolvedName = name?.Replace("{VER}", pinnedVersion);
            Assert.Contains($"v{pinnedVersion}", resolvedName, StringComparison.Ordinal); // published assets carry the leading 'v'
            Assert.True(IsSha256Hex(sha256), $"missing or invalid sha256 pin for {asset.Name}");
        }
    }

    [Theory]
    [InlineData("restore-julie-extract.sh")]
    [InlineData("restore-julie-extract.ps1")]
    public void RestoreScriptsSupportLocalSourceBuildUntilReleaseAssetsPublish(string scriptName)
    {
        string script = File.ReadAllText(Path.Combine(ScaleTestSupport.RepoRoot(), "scripts", scriptName));
        Assert.Contains("MILLER_JULIE_SOURCE", script, StringComparison.Ordinal);
        Assert.Contains("from-source", script, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value is not { Length: 64 }) return false;
        foreach (char c in value)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
