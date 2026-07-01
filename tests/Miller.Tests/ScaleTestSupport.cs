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
/// </summary>
public static class ScaleTestSupport
{
    /// <summary>
    /// The repo root (the dir holding <c>Miller.slnx</c>), resolved through a three-step fallback chain:
    /// walk up from the test assembly, then from the process's current working directory, then from the
    /// <c>EROS_WORKSPACE_ROOT</c> environment variable. Each step exists for Eros CT, which runs Miller's
    /// test binary from an out-of-repo sandbox: the assembly-based walk starts outside the repo and never
    /// finds <c>Miller.slnx</c>. The cwd walk cannot be relied on either, because xunit v3 resets the
    /// process current directory to the test-assembly directory (the out-of-repo sandbox) before tests
    /// execute, so <c>Directory.GetCurrentDirectory()</c> no longer points at CT's launch cwd. The
    /// <c>EROS_WORKSPACE_ROOT</c> variable, which CT always sets to the repo root, survives that reset and
    /// is therefore the reliable channel under CT. This mirrors Miller's own multi-fallback
    /// workspace-resolution idiom.
    /// </summary>
    public static string RepoRoot() =>
        LocateRepoRoot(AppContext.BaseDirectory)
        ?? LocateRepoRoot(Directory.GetCurrentDirectory())
        ?? LocateRepoRootFromWorkspaceRoot(Environment.GetEnvironmentVariable("EROS_WORKSPACE_ROOT"))
        ?? throw new InvalidOperationException("Could not locate repo root (Miller.slnx).");

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
    /// The pinned julie-extract binary under <c>.tools/</c>, or <c>null</c> if restore has not been run.
    /// Referencing this method marks a test as julie-spawning (see the class remarks).
    /// </summary>
    public static string? LocateJulieServer()
    {
        string name = OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract";
        string candidate = Path.Combine(RepoRoot(), ".tools", name);
        return File.Exists(candidate) ? candidate : null;
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
}
