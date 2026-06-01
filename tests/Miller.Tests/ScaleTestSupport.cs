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
    /// <summary>The repo root (the dir holding <c>Miller.slnx</c>), walked up from the test assembly.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Miller.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (Miller.slnx).");
    }

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
