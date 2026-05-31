using System.Text.Json;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class MillerExtractContractTests
{
    [Fact]
    public void Contract3PinsJulieVersionThatShipsHashMetadata()
    {
        Assert.Equal(28, MillerExtractContract.ExpectedSchemaVersion);
        Assert.Equal(3, MillerExtractContract.ExpectedExtractContractVersion);
        Assert.Equal("blake3", MillerExtractContract.ExpectedHashAlgorithm);
        Assert.Equal("7.13.2", MillerExtractContract.PinnedJulieServerVersion);
    }

    [Fact]
    public void JuliePinsJsonMatchesContractVersion()
    {
        string pinsPath = Path.Combine(ScaleTestSupport.RepoRoot(), "scripts", "julie-pins.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(pinsPath));

        Assert.Equal(
            MillerExtractContract.PinnedJulieServerVersion,
            doc.RootElement.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("restore-julie-server.sh")]
    [InlineData("restore-julie-server.ps1")]
    public void RestoreScriptsSupportLocalSourceBuildUntilReleaseAssetsPublish(string scriptName)
    {
        string script = File.ReadAllText(Path.Combine(ScaleTestSupport.RepoRoot(), "scripts", scriptName));

        Assert.Contains("MILLER_JULIE_SOURCE", script, StringComparison.Ordinal);
        Assert.Contains("from-source", script, StringComparison.OrdinalIgnoreCase);
    }
}
