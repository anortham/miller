using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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

    [Fact]
    public void ServerProjectCopiesPlatformSpecificRestoredJulieExtractBinaries()
    {
        string projectPath = Path.Combine(ScaleTestSupport.RepoRoot(), "src", "Miller.Server", "Miller.Server.csproj");
        XDocument project = XDocument.Load(projectPath);

        Dictionary<string, string> copiedTools = project.Descendants("Content")
            .Select(content => new
            {
                Include = content.Attribute("Include")?.Value,
                Link = content.Element("Link")?.Value,
            })
            .Where(item => item.Link is not null)
            .ToDictionary(item => item.Link!, item => item.Include ?? string.Empty, StringComparer.Ordinal);

        Assert.True(copiedTools.TryGetValue(".tools/julie-extract", out string? unixInclude),
            "Miller.Server.csproj must copy the Unix julie-extract binary into the app .tools directory.");
        Assert.Contains(".tools/julie-extract", unixInclude, StringComparison.Ordinal);

        Assert.True(copiedTools.TryGetValue(".tools/julie-extract.exe", out string? windowsInclude),
            "Miller.Server.csproj must copy the Windows julie-extract.exe binary into the app .tools directory.");
        Assert.Contains(".tools/julie-extract.exe", windowsInclude, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreScriptPythonFallbackReadsArchiveInnerPathTemplate()
    {
        string scriptPath = Path.Combine(ScaleTestSupport.RepoRoot(), "scripts", "restore-julie-extract.sh");
        string pythonFallback = ExtractPythonFallback(File.ReadAllText(scriptPath));
        string pinsPath = Path.Combine(Path.GetTempPath(), $"miller-julie-pins-{Guid.NewGuid():N}.json");
        File.WriteAllText(pinsPath, """
            {
              "version": "2.0.0",
              "urlTemplate": "https://example.test/{VER}/{asset}",
              "archiveInnerPathTemplate": "dist/{triple}/julie-extract{exe}",
              "assets": {}
            }
            """);

        try
        {
            var psi = new ProcessStartInfo("python3")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-");
            psi.ArgumentList.Add(".archiveInnerPathTemplate");
            psi.ArgumentList.Add(pinsPath);

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start python3 for restore-script fallback contract test.");
            process.StandardInput.Write(pythonFallback);
            process.StandardInput.Close();

            bool exited = process.WaitForExit(5000);
            if (!exited)
                process.Kill(entireProcessTree: true);

            Assert.True(exited, "restore-script python3 fallback did not exit within 5s.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            Assert.True(process.ExitCode == 0, $"python3 fallback exited {process.ExitCode}: {stderr}");
            Assert.Equal("dist/{triple}/julie-extract{exe}", stdout.Trim());
        }
        finally
        {
            File.Delete(pinsPath);
        }
    }

    private static string ExtractPythonFallback(string script)
    {
        Match match = Regex.Match(
            script,
            "python3 - \\\"\\$expr\\\" \\\"\\$\\{PINS\\}\\\" <<'PY'\\n(?<code>.*?)\\nPY",
            RegexOptions.Singleline);
        Assert.True(match.Success, "Could not find the restore script's python3 fallback heredoc.");
        return match.Groups["code"].Value;
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value is not { Length: 64 }) return false;
        foreach (char c in value)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
