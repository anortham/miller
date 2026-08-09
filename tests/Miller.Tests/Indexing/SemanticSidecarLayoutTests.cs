using System.Text.Json;
using System.Xml.Linq;
using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SemanticSidecarLayoutTests
{
    [Fact]
    public void StableSidecarPackageContractIsCurrent()
    {
        string root = ScaleTestSupport.RepoRoot();
        using JsonDocument pins = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "scripts", "semantic-pins.json")));

        Assert.Equal(
            "0.1.0",
            pins.RootElement.GetProperty("sidecar").GetProperty("version").GetString());

        using JsonDocument juliePins = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "scripts", "julie-pins.json")));
        string julieVersion = juliePins.RootElement.GetProperty("version").GetString()!;
        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md"));
        Assert.Contains($"currently pinned at version **{julieVersion}**", notices, StringComparison.Ordinal);
        Assert.Contains("pinned at version **0.1.0**", notices, StringComparison.Ordinal);
        Assert.Contains(".tools/julie-semantic-sidecar-runtime", notices, StringComparison.Ordinal);
        Assert.Contains("THIRD_PARTY-LICENSES.html", notices, StringComparison.Ordinal);

        string[] operationalFiles =
        [
            Path.Combine(root, "scripts", "semantic-broker-soak.sh"),
            Path.Combine(root, "scripts", "semantic-broker-soak.ps1"),
            Path.Combine(root, "scripts", "Miller.SemanticBrokerProbe", "Program.cs"),
            Path.Combine(root, "tests", "Miller.Tests", "Indexing", "SemanticBrokerScaleTests.cs"),
        ];
        foreach (string path in operationalFiles)
        {
            string guidance = File.ReadAllText(path);
            Assert.DoesNotContain("rc.", guidance, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Task 8", guidance, StringComparison.Ordinal);
            Assert.Contains("restore-semantic-sidecar", guidance, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExecutablePath_KeepsTheRuntimePackageTogetherUnderToolsRoot()
    {
        string toolsRoot = Path.Combine(Path.GetTempPath(), "miller-tools");

        string executable = SemanticSidecarLayout.ExecutablePath(toolsRoot);

        Assert.Equal(
            Path.Combine(
                toolsRoot,
                "julie-semantic-sidecar-runtime",
                OperatingSystem.IsWindows() ? "julie-semantic-sidecar.exe" : "julie-semantic-sidecar"),
            executable);
    }

    [Fact]
    public void ServerProjectCopiesTheWholeRuntimePackage()
    {
        XDocument project = XDocument.Load(Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "src",
            "Miller.Server",
            "Miller.Server.csproj"));

        XElement content = Assert.Single(project.Descendants("Content"), element =>
            ((string?)element.Attribute("Include"))?.Contains(
                ".tools/julie-semantic-sidecar-runtime/**/*",
                StringComparison.Ordinal) == true);

        Assert.Equal(
            ".tools/julie-semantic-sidecar-runtime/%(RecursiveDir)%(Filename)%(Extension)",
            (string?)content.Element("Link"));
    }

    [Theory]
    [InlineData("restore-semantic-sidecar.sh")]
    [InlineData("restore-semantic-sidecar.ps1")]
    public void RestoreScriptsVerifyTheRuntimeManifestBeforeInstallation(string scriptName)
    {
        string script = File.ReadAllText(Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "scripts",
            scriptName));

        Assert.Contains("package-manifest.json", script, StringComparison.Ordinal);
        Assert.Contains("sidecar_version", script, StringComparison.Ordinal);
        Assert.Contains("rust_target", script, StringComparison.Ordinal);
        Assert.Contains("julie-semantic-sidecar-runtime", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflowVerifiesTheRuntimeDirectoryLayout()
    {
        string workflow = File.ReadAllText(Path.Combine(
            ScaleTestSupport.RepoRoot(),
            ".github",
            "workflows",
            "release.yml"));

        Assert.Contains(
            ".tools/julie-semantic-sidecar-runtime/julie-semantic-sidecar",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            ".tools/julie-semantic-sidecar-runtime/julie-semantic-sidecar.exe",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Join-Path $publishDir \".tools/$sidecarName\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "! -path \"${package_dir}/.tools/julie-semantic-sidecar-runtime/*\"",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "julie-semantic-sidecar-runtime\" -prune",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "restore-semantic-sidecar.sh --verify-package",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "restore-semantic-sidecar.ps1 -VerifyPackage",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "[System.IO.Path]::GetFullPath",
            workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellVerifierIncludesHiddenPackageEntries()
    {
        string script = File.ReadAllText(Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "scripts",
            "restore-semantic-sidecar.ps1"));

        Assert.Contains(
            "Get-ChildItem -LiteralPath $PackageRoot -Directory -Force",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-ChildItem -LiteralPath $PackageRoot -File -Force",
            script,
            StringComparison.Ordinal);
    }
}
