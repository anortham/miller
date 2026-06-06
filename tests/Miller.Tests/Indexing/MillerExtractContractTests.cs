using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class MillerExtractContractTests
{
    [Fact]
    public void ContractPinsJulieExtractV2Versions()
    {
        Assert.Equal(2, MillerExtractContract.ExpectedSchemaVersion);
        Assert.Equal(2, MillerExtractContract.ExpectedSqliteSchemaVersion);
        Assert.Equal(2, MillerExtractContract.ExpectedExtractContractVersion);
        Assert.Equal(2, MillerExtractContract.ExpectedReportSchemaVersion);
        Assert.Equal("blake3", MillerExtractContract.ExpectedHashAlgorithm);
        Assert.Equal("2.1.3", MillerExtractContract.PinnedJulieExtractVersion);
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
    public void ReleaseWorkflowBuildMatrixMatchesPinnedJulieExtractPlatforms()
    {
        string repoRoot = ScaleTestSupport.RepoRoot();
        string pinsPath = Path.Combine(repoRoot, "scripts", "julie-pins.json");
        string workflowPath = Path.Combine(repoRoot, ".github", "workflows", "release.yml");

        using JsonDocument pins = JsonDocument.Parse(File.ReadAllText(pinsPath));
        string[] pinnedTriples = pins.RootElement.GetProperty("assets")
            .EnumerateObject()
            .Select(asset => asset.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(File.Exists(workflowPath),
            "release.yml must publish one Miller artifact for each pinned julie-extract platform.");
        string workflow = File.ReadAllText(workflowPath);

        string[] matrixTriples = Regex.Matches(
                workflow,
                @"^\s*-\s+target:\s+(?<target>[a-zA-Z0-9_.-]+)\s*$",
                RegexOptions.Multiline)
            .Select(match => match.Groups["target"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(pinnedTriples, matrixTriples);

        var expectedNativeRunners = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aarch64-apple-darwin"] = "macos-14",
            ["x86_64-apple-darwin"] = "macos-15-intel",
            ["x86_64-unknown-linux-gnu"] = "ubuntu-24.04",
            ["x86_64-pc-windows-msvc"] = "windows-2025",
        };

        foreach ((string triple, string runner) in expectedNativeRunners)
        {
            Assert.Matches(
                $@"-\s+target:\s+{Regex.Escape(triple)}\s*\n\s+rid:\s+\S+\s*\n\s+runner:\s+{Regex.Escape(runner)}",
                workflow);
        }
    }

    [Fact]
    public void ReleaseWorkflowPublishesVerifiablePrereleasePackages()
    {
        string workflowPath = Path.Combine(ScaleTestSupport.RepoRoot(), ".github", "workflows", "release.yml");
        string workflow = File.ReadAllText(workflowPath);

        Assert.Contains("prerelease:", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--prerelease", workflow, StringComparison.Ordinal);
        Assert.Contains("--latest=false", workflow, StringComparison.Ordinal);

        Assert.Contains("shasum -a 256", workflow, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -Algorithm SHA256", workflow, StringComparison.Ordinal);
        Assert.Contains(".tar.gz.sha256", workflow, StringComparison.Ordinal);
        Assert.Contains(".zip.sha256", workflow, StringComparison.Ordinal);

        Assert.Contains("test -x \"${publish_dir}/miller\"", workflow, StringComparison.Ordinal);
        Assert.Contains("\"${publish_dir}/miller\" version", workflow, StringComparison.Ordinal);
        Assert.Contains("& $millerBinary version", workflow, StringComparison.Ordinal);

        Assert.Contains("dashboard/Miller.Dashboard", workflow, StringComparison.Ordinal);
        Assert.Contains("dashboard/Miller.Dashboard.exe", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflowSupportsPackageOnlyManualValidation()
    {
        string workflowPath = Path.Combine(ScaleTestSupport.RepoRoot(), ".github", "workflows", "release.yml");
        string workflow = File.ReadAllText(workflowPath);

        Assert.Matches(
            @"publish:\s*\n\s+description:\s+'Publish or update the GitHub release'\s*\n\s+required:\s+false\s*\n\s+default:\s+false\s*\n\s+type:\s+boolean",
            workflow);
        Assert.Contains("if: github.event_name == 'push' || inputs.publish", workflow, StringComparison.Ordinal);
        Assert.Contains("Skipping GitHub release publication; packaged artifacts were uploaded for validation.", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreScriptPythonFallbackReadsArchiveInnerPathTemplate()
    {
        string scriptPath = Path.Combine(ScaleTestSupport.RepoRoot(), "scripts", "restore-julie-extract.sh");
        string pythonFallback = ExtractPythonFallback(File.ReadAllText(scriptPath));
        string pinsPath = Path.Combine(Path.GetTempPath(), $"miller-julie-pins-{Guid.NewGuid():N}.json");
        File.WriteAllText(pinsPath, """
            {
              "version": "2.0.1",
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

    [Fact]
    public void UnixRestoreScriptExtractsLinuxArchiveWithLeadingDotDistPrefix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX shell restore-script regression test.");

        string repoRoot = ScaleTestSupport.RepoRoot();
        string tempRoot = Path.Combine(Path.GetTempPath(), $"miller-restore-script-{Guid.NewGuid():N}");
        try
        {
            string scriptsDir = Path.Combine(tempRoot, "scripts");
            string fakeBin = Path.Combine(tempRoot, "fake-bin");
            string archiveRoot = Path.Combine(tempRoot, "archive-root");
            string binaryDir = Path.Combine(archiveRoot, "dist", "x86_64-unknown-linux-gnu");
            Directory.CreateDirectory(scriptsDir);
            Directory.CreateDirectory(fakeBin);
            Directory.CreateDirectory(binaryDir);

            string scriptPath = Path.Combine(scriptsDir, "restore-julie-extract.sh");
            File.Copy(Path.Combine(repoRoot, "scripts", "restore-julie-extract.sh"), scriptPath);

            string archiveBinary = Path.Combine(binaryDir, "julie-extract");
            WriteExecutable(archiveBinary, """
                #!/usr/bin/env bash
                echo "julie-extract 2.0.1"
                """);

            string archivePath = Path.Combine(tempRoot, "julie-extract-v2.0.1-x86_64-unknown-linux-gnu.tar.gz");
            RunProcess("tar", ["-czf", archivePath, "-C", archiveRoot, "."]);
            string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant();

            File.WriteAllText(Path.Combine(scriptsDir, "julie-pins.json"), $$"""
                {
                  "version": "2.0.1",
                  "binary": "julie-extract",
                  "archiveInnerPathTemplate": "dist/{triple}/julie-extract{exe}",
                  "urlTemplate": "https://example.test/{VER}/{asset}",
                  "assets": {
                    "x86_64-unknown-linux-gnu": {
                      "name": "julie-extract-v{VER}-x86_64-unknown-linux-gnu.tar.gz",
                      "sha256": "{{sha256}}"
                    }
                  }
                }
                """);

            WriteExecutable(Path.Combine(fakeBin, "uname"), """
                #!/usr/bin/env bash
                case "${1:-}" in
                  -s) echo Linux ;;
                  -m) echo x86_64 ;;
                  *) /usr/bin/uname "$@" ;;
                esac
                """);
            WriteExecutable(Path.Combine(fakeBin, "curl"), """
                #!/usr/bin/env bash
                set -euo pipefail
                out=""
                while [[ $# -gt 0 ]]; do
                  case "$1" in
                    -o) out="$2"; shift 2 ;;
                    *) shift ;;
                  esac
                done
                if [[ -z "$out" ]]; then
                  echo "missing -o" >&2
                  exit 1
                fi
                cp "$FAKE_JULIE_ARCHIVE" "$out"
                """);

            ProcessResult result = RunProcess(
                "bash",
                [scriptPath],
                cwd: tempRoot,
                env: new Dictionary<string, string?>
                {
                    ["PATH"] = fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                    ["FAKE_JULIE_ARCHIVE"] = archivePath,
                });

            Assert.True(result.ExitCode == 0, $"restore script failed:\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");
            string installed = Path.Combine(tempRoot, ".tools", "julie-extract");
            Assert.True(File.Exists(installed), $"expected restored binary at {installed}");
            ProcessResult version = RunProcess(installed, ["--version"]);
            Assert.Contains("julie-extract 2.0.1", version.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string ExtractPythonFallback(string script)
    {
        Match match = Regex.Match(
            script,
            "python3 - \\\"\\$expr\\\" \\\"\\$\\{PINS\\}\\\" <<'PY'\\r?\\n(?<code>.*?)\\r?\\nPY",
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

    private static void WriteExecutable(string path, string contents)
    {
        File.WriteAllText(path, contents);
        RunProcess("chmod", ["+x", path]);
    }

    private static ProcessResult RunProcess(
        string fileName,
        IReadOnlyList<string> args,
        string? cwd = null,
        IReadOnlyDictionary<string, string?>? env = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (cwd is not null)
            psi.WorkingDirectory = cwd;
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);
        if (env is not null)
        {
            foreach ((string key, string? value) in env)
            {
                if (value is null)
                    psi.Environment.Remove(key);
                else
                    psi.Environment[key] = value;
            }
        }

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        bool exited = process.WaitForExit(10000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} did not exit within 10s.");
        }
        return new(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
