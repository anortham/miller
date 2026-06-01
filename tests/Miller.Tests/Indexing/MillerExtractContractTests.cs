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

        // "binary" identifies the tool; version must be non-empty; assets must carry valid sha256 pins.
        // (The coupling of pins.json version to MillerExtractContract.PinnedJulieServerVersion is
        // re-established by A1 when that constant is updated for v2.0.0 — Phase 2.)
        string? binary = doc.RootElement.GetProperty("binary").GetString();
        string? version = doc.RootElement.GetProperty("version").GetString();
        Assert.Equal("julie-extract", binary);
        Assert.False(string.IsNullOrWhiteSpace(version), "julie-pins.json must carry a non-empty version");

        foreach (JsonProperty asset in doc.RootElement.GetProperty("assets").EnumerateObject())
        {
            string? name = asset.Value.GetProperty("name").GetString();
            string? sha256 = asset.Value.GetProperty("sha256").GetString();

            Assert.Contains($"v{{VER}}", name, StringComparison.Ordinal);
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
