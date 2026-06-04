using System.Diagnostics;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Server.Cli;

/// <summary>
/// End-to-end coverage of the REAL <c>miller</c> binary's CLI path (TODO #6): the in-process
/// <see cref="Miller.Server.Cli.CliDispatch"/> tests prove verb routing cheaply, but only spawning the built
/// host exercises <c>Program.cs</c>'s entry branch — <c>IsCliInvocation</c>, the
/// <c>WorkspaceContext.Create(Environment.CurrentDirectory, …)</c> resolution, real Console wiring, and the
/// process exit code. The index is built once with an in-process julie scan (the Scale launch signal, via
/// <see cref="ScaleTestSupport.RequireJulieServer"/>); the binary then READS it from a separate process whose
/// CWD is the workspace, which is the path no in-process test can reach. <c>[Trait("Category","Scale")]</c>:
/// it spawns julie-extract and a second process, so it stays out of the fast suite and SKIPS when restore has
/// not run.
/// </summary>
[Trait("Category", "Scale")]
public sealed class CliBinarySubprocessTests : IDisposable
{
    private readonly string _root;

    public CliBinarySubprocessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miller-cli-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void BuiltBinary_ResolvesCwdIndex_AndHonorsExitCodes()
    {
        string julie = ScaleTestSupport.RequireJulieServer();   // skips when .tools/julie-extract is absent
        string millerDll = ServerBinaryOrSkip();

        // A tiny source tree for julie to extract.
        string srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Widget.cs"),
            "namespace Demo;\npublic class WidgetFromCli\n{\n    public int Frobnicate() => 42;\n}\n");

        // Build <root>/.miller/symbols.db with an in-process julie scan, then let the binary READ it: this proves
        // the real-process entry branch + the Environment.CurrentDirectory-based index resolution end to end.
        string toolsRoot = Path.GetDirectoryName(julie)!;
        string dbPath = Path.Combine(_root, ".miller", "symbols.db");
        ExtractReport report = JulieExtractRunner.Locate(toolsRoot).Scan(_root, dbPath, force: true);
        Assert.True(File.Exists(dbPath), $"julie scan produced no {dbPath} (symbols={report.SymbolsExtracted}).");

        // version → exit 0, prints the build version.
        ProcessResult version = RunMiller(millerDll, _root, "version");
        Assert.Equal(0, version.ExitCode);
        Assert.StartsWith("0.1.0", version.Stdout.Trim());

        // search → resolves <cwd>/.miller/symbols.db and finds the symbol.
        ProcessResult search = RunMiller(millerDll, _root, "search", "WidgetFromCli");
        Assert.Equal(0, search.ExitCode);
        Assert.Contains("WidgetFromCli", search.Stdout);

        // unknown verb → usage error, exit 2 (the documented contract, from a real process).
        ProcessResult bad = RunMiller(millerDll, _root, "frobnicate");
        Assert.Equal(2, bad.ExitCode);
    }

    // The server's built miller.dll (runtimeconfig + deps + .tools live next to it), located via the repo root +
    // the build configuration/TFM the test assembly itself was built under. Run through `dotnet` for portability.
    private static string ServerBinaryOrSkip()
    {
        var testBin = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string tfm = testBin.Name;                 // e.g. net10.0
        string configuration = testBin.Parent!.Name;   // e.g. Release
        string dll = Path.Combine(
            ScaleTestSupport.RepoRoot(), "src", "Miller.Server", "bin", configuration, tfm, "miller.dll");
        Assert.SkipUnless(File.Exists(dll), $"built miller host not found at {dll} — build Miller.Server first.");
        return dll;
    }

    private static ProcessResult RunMiller(string millerDll, string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(millerDll);
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start `dotnet` for the miller CLI.");
        string stdout = process.StandardOutput.ReadToEnd();   // tiny CLI output — read-then-wait won't deadlock
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("the miller CLI did not exit within 30s.");
        }
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
