using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Miller.Tests.Indexing;

[Trait("Category", "Scale")]
public sealed class SemanticRestoreScriptScaleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"miller-semantic-restore-{Guid.NewGuid():N}");

    [Fact]
    public void BashVerifier_AcceptsExactPackageAndRejectsContentDrift()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX shell verification runs on Unix hosts.");

        string script = Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "scripts",
            "restore-semantic-sidecar.sh");

        VerifyContract(
            "bash",
            [script],
            "aarch64-apple-darwin",
            "julie-semantic-sidecar",
            powerShell: false);
    }

    [Fact]
    public void PowerShellVerifier_AcceptsExactPackageAndRejectsContentDrift()
    {
        string? pwsh = FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        if (pwsh is null)
            Assert.Skip("PowerShell 7 is unavailable.");

        string script = Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "scripts",
            "restore-semantic-sidecar.ps1");

        VerifyContract(
            pwsh,
            ["-NoProfile", "-File", script],
            "x86_64-pc-windows-msvc",
            "julie-semantic-sidecar.exe",
            powerShell: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void VerifyContract(
        string command,
        IReadOnlyList<string> commandPrefix,
        string triple,
        string executableName,
        bool powerShell)
    {
        Directory.CreateDirectory(_root);
        string pins = Path.Combine(_root, "pins.json");
        File.WriteAllText(pins, """{"sidecar":{"version":"0.1.0-rc.4"}}""");

        string exact = CreatePackage("exact", triple, executableName);
        ProcessResult accepted = RunVerifier(
            command, commandPrefix, exact, triple, pins, powerShell);
        Assert.Equal(0, accepted.ExitCode);

        string tampered = CreatePackage("tampered", triple, executableName);
        File.AppendAllText(Path.Combine(tampered, executableName), "tampered");
        ProcessResult tamperedResult = RunVerifier(
            command, commandPrefix, tampered, triple, pins, powerShell);
        Assert.NotEqual(0, tamperedResult.ExitCode);
        Assert.Contains("sha256 mismatch", tamperedResult.Combined, StringComparison.OrdinalIgnoreCase);

        string extra = CreatePackage("extra", triple, executableName);
        File.WriteAllText(Path.Combine(extra, "undeclared.txt"), "extra");
        ProcessResult extraResult = RunVerifier(
            command, commandPrefix, extra, triple, pins, powerShell);
        Assert.NotEqual(0, extraResult.ExitCode);
        Assert.Contains(
            "contents do not match package-manifest.json",
            extraResult.Combined,
            StringComparison.OrdinalIgnoreCase);

        string missing = CreatePackage("missing", triple, executableName);
        File.Delete(Path.Combine(missing, "LICENSE"));
        ProcessResult missingResult = RunVerifier(
            command, commandPrefix, missing, triple, pins, powerShell);
        Assert.NotEqual(0, missingResult.ExitCode);
        Assert.Contains(
            "manifest file missing",
            missingResult.Combined,
            StringComparison.OrdinalIgnoreCase);
    }

    private string CreatePackage(string name, string triple, string executableName)
    {
        string root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        Write(root, executableName, "binary\n");
        Write(root, "LICENSE", "license\n");
        Write(root, "README.md", "readme\n");

        var files = new[]
        {
            Entry(root, executableName, "executable"),
            Entry(root, "LICENSE", "license"),
            Entry(root, "README.md", "documentation"),
        };
        File.WriteAllText(
            Path.Combine(root, "package-manifest.json"),
            JsonSerializer.Serialize(new
            {
                sidecar_version = "0.1.0-rc.4",
                rust_target = triple,
                files,
            }));
        return root;
    }

    private static object Entry(string root, string path, string role)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(root, path));
        return new
        {
            path,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            size = bytes.LongLength,
            role,
        };
    }

    private static void Write(string root, string path, string content) =>
        File.WriteAllText(Path.Combine(root, path), content);

    private static ProcessResult Run(
        string command,
        IReadOnlyList<string> args,
        string pins)
    {
        var start = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["MILLER_SEMANTIC_PINS"] = pins;

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"Failed to start {command}.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10000), $"{command} did not exit within 10 seconds.");
        return new(process.ExitCode, stdout, stderr);
    }

    private static ProcessResult RunVerifier(
        string command,
        IReadOnlyList<string> commandPrefix,
        string packageRoot,
        string triple,
        string pins,
        bool powerShell)
    {
        string[] verifierArgs = powerShell
            ? ["-VerifyPackage", packageRoot, "-ExpectedTriple", triple]
            : ["--verify-package", packageRoot, triple];
        return Run(command, [.. commandPrefix, .. verifierArgs], pins);
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
    {
        public string Combined => $"{Stdout}\n{Stderr}";
    }
}
