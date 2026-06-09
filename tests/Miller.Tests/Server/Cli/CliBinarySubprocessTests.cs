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
    private readonly string _home;

    public CliBinarySubprocessTests()
    {
        string unique = Guid.NewGuid().ToString("N");
        _root = Path.Combine(Path.GetTempPath(), "miller-cli-e2e-" + unique);
        // An ISOLATED home so the binary's machine-global registry/telemetry (<home>/.miller/workspaces.db) lands
        // in a temp dir — a subprocess `workspace open` must NOT register into the dev machine's real ~/.miller.
        _home = Path.Combine(Path.GetTempPath(), "miller-cli-home-" + unique);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
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
        Assert.StartsWith("0.3.6", version.Stdout.Trim());

        // search → resolves <cwd>/.miller/symbols.db and finds the symbol.
        ProcessResult search = RunMiller(millerDll, _root, "search", "WidgetFromCli");
        Assert.Equal(0, search.ExitCode);
        Assert.Contains("WidgetFromCli", search.Stdout);

        // unknown verb → usage error, exit 2 (the documented contract, from a real process).
        ProcessResult bad = RunMiller(millerDll, _root, "frobnicate");
        Assert.Equal(2, bad.ExitCode);
    }

    [Fact]
    public void BuiltBinary_WorkspaceOpen_BootstrapsFreshDir_ThenRemove()
    {
        ScaleTestSupport.RequireJulieServer();          // Scale launch signal; skips when .tools is absent
        string millerDll = ServerBinaryOrSkip();

        // A fresh source tree with NO pre-built index — `workspace open` must build it from scratch (the path
        // no in-process test can reach: a real second process resolving its CWD + locating its own .tools julie).
        string srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Gadget.cs"),
            "namespace Demo;\npublic class GadgetFromOpen\n{\n    public int Spin() => 7;\n}\n");
        string dbPath = Path.Combine(_root, ".miller", "symbols.db");
        Assert.False(File.Exists(dbPath), "the index must not exist before `workspace open`.");

        // open → builds <root>/.miller/symbols.db and registers the workspace (exit 0).
        ProcessResult open = RunMiller(millerDll, _root, "workspace", "open");
        Assert.Equal(0, open.ExitCode);
        Assert.True(File.Exists(dbPath), $"`workspace open` did not create {dbPath}.\n{open.Stdout}\n{open.Stderr}");

        // search now resolves the freshly-built index and finds the symbol.
        ProcessResult search = RunMiller(millerDll, _root, "search", "GadgetFromOpen");
        Assert.Equal(0, search.ExitCode);
        Assert.Contains("GadgetFromOpen", search.Stdout);

        // Re-open is idempotent (a cheap delta) → still exit 0.
        ProcessResult reopen = RunMiller(millerDll, _root, "workspace", "open");
        Assert.Equal(0, reopen.ExitCode);

        // --full is accepted and forces a from-scratch rebuild → exit 0, scanned.
        ProcessResult full = RunMiller(millerDll, _root, "workspace", "open", "--full");
        Assert.Equal(0, full.ExitCode);
        Assert.Contains("scanned: yes", full.Stdout);

        // remove --path deletes the .miller index dir (exit 0); the dir is gone afterward.
        ProcessResult remove = RunMiller(millerDll, _root, "workspace", "remove", "--path", _root);
        Assert.Equal(0, remove.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")),
            $"`workspace remove` left .miller behind.\n{remove.Stdout}\n{remove.Stderr}");
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

    private ProcessResult RunMiller(string millerDll, string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Isolate the machine-global Miller home (registry + telemetry) into the test's temp home so a real
        // `workspace open` registers there, never in the dev machine's ~/.miller. HOME drives UserProfile on
        // POSIX; USERPROFILE on Windows — set both for a cross-platform isolation.
        psi.Environment["HOME"] = _home;
        psi.Environment["USERPROFILE"] = _home;
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
