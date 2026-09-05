using System.Diagnostics;
using System.Text;
using Miller.Indexing.Semantic;
using Miller.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Miller.Tests;

/// <summary>
/// Shared scaffolding for the Scale suite's live-binary tests. Centralizes the ONE thing every
/// julie-spawning test needs — locating the pinned <c>.tools/julie-extract</c> and skipping (never
/// failing) when restore has not been run — so the launch signal lives in exactly one place.
///
/// That single signal is what the <see cref="Conventions.ScaleTraitConventionTests"/> drift guard
/// keys on: any test that calls <see cref="RequireJulieServer"/> (or <see cref="LocateJulieServer"/>)
/// spawns the real subprocess and MUST therefore carry <c>[Trait("Category","Scale")]</c> so the
/// default fast suite excludes it. Before this helper existed the locator was copy-pasted into seven
/// files, so there was no reliable signal a guard could trust.
///
/// <see cref="RequireSemanticSidecar"/> is the same arrangement for the second live binary Miller
/// spawns, the pinned <c>julie-semantic-sidecar</c>. It is a separate signal rather than a widened
/// julie one because the two binaries are restored by different scripts and a skip message that names
/// the wrong script sends the reader to a command that will not help.
/// </summary>
public static class ScaleTestSupport
{
    /// <summary>
    /// The repo root (the dir holding <c>Miller.slnx</c>), resolved through a three-step fallback chain:
    /// walk up from the test assembly, then from the process's current working directory, then from the
    /// <see cref="CtEnvironment.WorkspaceRoot"/> environment variable. Each step exists for continuous
    /// testing, which runs Miller's test binary from an out-of-repo build directory: the assembly-based walk
    /// starts outside the repo and never finds <c>Miller.slnx</c>. The cwd walk cannot be relied on either,
    /// because xunit v3 resets the process current directory to the test-assembly directory before tests
    /// execute, so <c>Directory.GetCurrentDirectory()</c> no longer points at CT's launch cwd. The
    /// environment variable, which CT sets to the workspace root in
    /// <c>DotnetTestProvider.WorkspaceEnvironment</c>, survives that reset and is therefore the reliable
    /// channel under CT. This mirrors Miller's own multi-fallback workspace-resolution idiom.
    ///
    /// <para><b>The name is load-bearing.</b> This step read the retired <c>EROS_WORKSPACE_ROOT</c> while CT
    /// set <c>MILLER_CT_WORKSPACE_ROOT</c>, so the channel was broken on the consumer side and every test
    /// that needs the repo root failed under CT — about 50 of them. The rename was required by
    /// <c>docs/plans/2026-08-18-ct-sidecar-migration.md</c>; the producer side landed and this side did not.
    /// Read the constant, never a literal, and see
    /// <c>ScaleTestSupportTests.RepoRootFrom_ReadsTheWorkspaceRootVariableCtActuallySets</c>.</para>
    /// </summary>
    public static string RepoRoot() =>
        RepoRootFrom(AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable)
        ?? throw new InvalidOperationException("Could not locate repo root (Miller.slnx).");

    /// <summary>
    /// The resolution chain with every input supplied, so a test can drive the env-var step — the step that
    /// only fires when BOTH walks miss, which never happens in an ordinary in-repo <c>dotnet test</c> run.
    /// Returns <c>null</c> rather than throwing so the caller owns the error message.
    /// </summary>
    internal static string? RepoRootFrom(
        string assemblyDirectory,
        string currentDirectory,
        Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);
        return LocateRepoRoot(assemblyDirectory)
            ?? LocateRepoRoot(currentDirectory)
            ?? LocateRepoRootFromWorkspaceRoot(readVariable(CtEnvironment.WorkspaceRoot));
    }

    /// <summary>
    /// Walk up from <paramref name="startDirectory"/> looking for the directory containing
    /// <c>Miller.slnx</c>. Returns <c>null</c> (never throws) if the walk reaches the filesystem root
    /// without finding it, so callers can try another starting point before giving up.
    /// </summary>
    internal static string? LocateRepoRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Miller.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>
    /// Resolve the repo root from a raw <c>EROS_WORKSPACE_ROOT</c> value by walking up from it, or return
    /// <c>null</c> when the value is unset/blank so <see cref="RepoRoot"/> falls through to its next step.
    /// Kept as a pure helper (the env read stays in <see cref="RepoRoot"/>) so it is testable without
    /// mutating the process-global environment — unsafe under xunit v3's parallel collections.
    /// </summary>
    internal static string? LocateRepoRootFromWorkspaceRoot(string? workspaceRoot) =>
        string.IsNullOrWhiteSpace(workspaceRoot) ? null : LocateRepoRoot(workspaceRoot);

    /// <summary>
    /// The pinned binary, or an explicit source binary selected only for test qualification.
    /// An unavailable default pin returns null; an invalid explicit selection fails without fallback.
    /// Referencing this method marks a test as julie-spawning (see the class remarks).
    /// </summary>
    public static string? LocateJulieServer()
    {
        return SelectJulieBinary(RepoRoot(), Environment.GetEnvironmentVariable(JulieBinaryOverride), File.Exists);
    }

    // Test-only input. Production tooling and package compatibility checks never read this variable.
    internal const string JulieBinaryOverride = "MILLER_TEST_JULIE_EXTRACT";

    internal static string? SelectJulieBinary(string repoRoot, string? sourceBinary, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(sourceBinary))
        {
            if (!Path.IsPathFullyQualified(sourceBinary))
                throw new ArgumentException($"{JulieBinaryOverride} must name an absolute binary path.", nameof(sourceBinary));
            if (!exists(sourceBinary))
                throw new FileNotFoundException($"{JulieBinaryOverride} selected an unavailable binary; refusing pinned fallback.", sourceBinary);
            return sourceBinary;
        }
        string name = OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract";
        string candidate = Path.Combine(repoRoot, ".tools", name);
        return exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Locate the pinned julie-extract, or SKIP the calling test (never fail) when restore has not run.
    /// This is THE launch signal every live test funnels through: the returned path is non-null, and a
    /// missing binary short-circuits via <see cref="Assert.SkipWhen"/> with an actionable message.
    /// </summary>
    public static string RequireJulieServer()
    {
        string? binary = LocateJulieServer();
        Assert.SkipWhen(binary is null,
            "julie-extract not found in .tools/. Run scripts/restore-julie-extract.sh to enable the Scale test.");
        return binary!;
    }

    /// <summary>
    /// The pinned julie-semantic-sidecar runtime under <c>.tools/</c>, or <c>null</c> if restore has not
    /// been run. Referencing this method marks a test as sidecar-spawning (see the class remarks).
    /// </summary>
    public static string? LocateSemanticSidecar()
    {
        string candidate = SemanticSidecarLayout.ExecutablePath(Path.Combine(RepoRoot(), ".tools"));
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Locate the pinned julie-semantic-sidecar, or SKIP the calling test (never fail) when restore has not
    /// run. The message names the sidecar's own restore script and the one-time <c>prepare</c> verb: the
    /// binary downloads its ~1.2 GB GGUF into a shared cache on first use, so a cold machine's first
    /// <c>serve</c> pays a download rather than a model load.
    /// </summary>
    public static string RequireSemanticSidecar()
    {
        string? binary = LocateSemanticSidecar();
        Assert.SkipWhen(binary is null,
            "julie-semantic-sidecar not found in .tools/. Run scripts/restore-semantic-sidecar.sh, then " +
            "run `miller semantic prepare` once to populate the model cache, to enable the Scale test.");
        return binary!;
    }

    internal static void WriteFreshnessArtifact(string workspaceRoot, string artifactId, long revision)
    {
        string millerDir = Path.Combine(workspaceRoot, ".miller");
        Directory.CreateDirectory(millerDir);
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(millerDir, "symbols.db")};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE extraction_revisions (revision_id INTEGER PRIMARY KEY);
            INSERT INTO artifact_metadata(key, value) VALUES ('artifact_id', $artifact);
            INSERT INTO extraction_revisions(revision_id) VALUES ($revision);
            """;
        command.Parameters.AddWithValue("$artifact", artifactId);
        command.Parameters.AddWithValue("$revision", revision);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Run the julie-extract CLI (a <see cref="RequireJulieServer"/> path) and return its stdout, asserting
    /// exit code 0. Lives here so every julie-CLI scale test shares ONE process helper instead of pasting
    /// copies that drift. stderr is drained through the async event pump while stdout is read: reading the
    /// two redirected pipes to end sequentially deadlocks once the child fills the second pipe's OS buffer
    /// (~64KB) while the parent is still blocked on the first.
    /// </summary>
    public static string RunJulie(string binary, params string[] args)
    {
        var start = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(); // parameterless overload also drains the pending async stderr callbacks
        Assert.True(process.ExitCode == 0, $"julie-extract {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }
}
