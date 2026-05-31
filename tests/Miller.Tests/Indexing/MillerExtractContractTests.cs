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

        string pinnedVersion = MillerExtractContract.PinnedJulieServerVersion;
        Assert.Equal(
            pinnedVersion,
            doc.RootElement.GetProperty("version").GetString());

        foreach (JsonProperty asset in doc.RootElement.GetProperty("assets").EnumerateObject())
        {
            string? name = asset.Value.GetProperty("name").GetString();
            string? sha256 = asset.Value.GetProperty("sha256").GetString();

            Assert.Contains($"v{pinnedVersion}", name, StringComparison.Ordinal);
            Assert.True(IsSha256Hex(sha256), $"missing or invalid sha256 pin for {asset.Name}");
        }
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

    private static bool IsSha256Hex(string? value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }

        foreach (char c in value)
        {
            bool isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
