using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins Miller's own <c>--ignore-file</c> contribution: the generated invariant file, always last, exclusions
/// only, on scans and on single-file updates. Nothing user-authored is ever routed this way — that is the
/// seeder's in-tree copy (<c>JulieIgnoreSeederTests</c>). Tiny temp trees — fast suite, no subprocess.
/// </summary>
public sealed class ScanIgnorePolicyTests : IDisposable
{
    private readonly string _dir;

    public ScanIgnorePolicyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-scan-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string NewDirectory(params string[] segments)
    {
        string path = Path.Combine(new[] { _dir }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void InvariantPatternsCoverTheDirectoriesJulieExtractDoesNotHardExclude()
    {
        Assert.Contains(".miller/", ScanIgnorePolicy.InvariantPatterns);
        Assert.Contains(".worktrees/", ScanIgnorePolicy.InvariantPatterns);
        Assert.Contains(".claude/worktrees/", ScanIgnorePolicy.InvariantPatterns);
    }

    [Fact]
    public void InvariantContentCarriesNoWhitelistPattern()
    {
        string content = ScanIgnorePolicy.RenderInvariantContent();

        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.False(line.TrimStart().StartsWith('!'), $"whitelist pattern in the invariant file: '{line}'");
    }

    [Fact]
    public void InvariantContentIsPatternsAndCommentsOnly()
    {
        string content = ScanIgnorePolicy.RenderInvariantContent();

        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(
                line.StartsWith('#') || ScanIgnorePolicy.InvariantPatterns.Contains(line),
                $"unexpected non-pattern line: '{line}'");
    }

    [Fact]
    public void PrepareForScanWritesTheInvariantFileUnderTheMillerDirectory()
    {
        string root = NewDirectory("plain");

        IReadOnlyList<string> paths = ScanIgnorePolicy.PrepareForScan(root);

        string expected = Path.Combine(root, ".miller", "invariant.julieignore");
        Assert.Equal(new[] { expected }, paths);
        Assert.Equal(ScanIgnorePolicy.RenderInvariantContent(), File.ReadAllText(expected));
    }

    [Fact]
    public void PrepareForScanRewritesTheInvariantFileAndDiscardsUserEdits()
    {
        string root = NewDirectory("edited");
        ScanIgnorePolicy.PrepareForScan(root);
        string path = ScanIgnorePolicy.InvariantIgnorePathFor(root);
        File.WriteAllText(path, "!.worktrees/\n");

        ScanIgnorePolicy.PrepareForScan(root);

        Assert.Equal(ScanIgnorePolicy.RenderInvariantContent(), File.ReadAllText(path));
    }

    [Fact]
    public void PrepareForScanOnAMissingRootWritesNothingAndDoesNotCreateTheRoot()
    {
        string missing = Path.Combine(_dir, "not-there");

        Assert.Empty(ScanIgnorePolicy.PrepareForScan(missing));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void ForFileUpdateCarriesTheInvariantFileAScanAlreadyWrote()
    {
        string root = NewDirectory("scanned");
        ScanIgnorePolicy.PrepareForScan(root);

        Assert.Equal(
            new[] { ScanIgnorePolicy.InvariantIgnorePathFor(root) }, ScanIgnorePolicy.ForFileUpdate(root));
    }

    [Fact]
    public void ForFileUpdateBeforeAnyScanCarriesNothingAndWritesNothing()
    {
        string root = NewDirectory("unscanned");

        Assert.Empty(ScanIgnorePolicy.ForFileUpdate(root));
        Assert.False(File.Exists(ScanIgnorePolicy.InvariantIgnorePathFor(root)));
    }

    [Fact]
    public void PrepareForScan_GeneratedPolicyComesBeforeInvariantPolicy()
    {
        string root = NewDirectory("generated");
        string millerHome = NewDirectory("miller-home");
        string workspaceId = WorkspaceId.FromCanonicalRoot(root);
        EffectiveIgnorePolicy? policyMaybe = JulieIgnoreSeeder.PreparePolicy(root, workspaceId, millerHome);
        Assert.NotNull(policyMaybe);
        EffectiveIgnorePolicy policy = policyMaybe!;

        IReadOnlyList<string> paths = ScanIgnorePolicy.PrepareForScan(root, policy);

        Assert.Equal(new[] { policy.Path, ScanIgnorePolicy.InvariantIgnorePathFor(root) }, paths);
    }

    [Fact]
    public void ForFileUpdate_UserPolicyNeverBecomesExternalIgnoreFile()
    {
        string root = NewDirectory("user");
        string userPath = Path.Combine(root, ".julieignore");
        File.WriteAllText(userPath, "generated/\n");
        string workspaceId = WorkspaceId.FromCanonicalRoot(root);
        EffectiveIgnorePolicy? policyMaybe = JulieIgnoreSeeder.PreparePolicy(
            root, workspaceId, NewDirectory("miller-home"));
        Assert.NotNull(policyMaybe);
        EffectiveIgnorePolicy policy = policyMaybe!;
        ScanIgnorePolicy.PrepareForScan(root, policy);

        Assert.Equal(new[] { ScanIgnorePolicy.InvariantIgnorePathFor(root) },
            ScanIgnorePolicy.ForFileUpdate(root, policy));
    }

    [Fact]
    public void ForFileUpdate_GeneratedPolicyComesBeforeInvariantPolicy()
    {
        string root = NewDirectory("generated-update");
        string millerHome = NewDirectory("miller-home-update");
        string workspaceId = WorkspaceId.FromCanonicalRoot(root);
        EffectiveIgnorePolicy? policyMaybe = JulieIgnoreSeeder.PreparePolicy(root, workspaceId, millerHome);
        Assert.NotNull(policyMaybe);
        EffectiveIgnorePolicy policy = policyMaybe!;
        ScanIgnorePolicy.PrepareForScan(root, policy);

        Assert.Equal(
            new[] { policy.Path, ScanIgnorePolicy.InvariantIgnorePathFor(root) },
            ScanIgnorePolicy.ForFileUpdate(root, policy));
    }

    [Fact]
    public void PrepareForScan_RootPolicyCreatedAfterMaterializationDisablesGlobalPolicy()
    {
        string root = NewDirectory("root-takeover");
        string millerHome = NewDirectory("miller-home-takeover");
        string workspaceId = WorkspaceId.FromCanonicalRoot(root);
        EffectiveIgnorePolicy? policyMaybe = JulieIgnoreSeeder.PreparePolicy(root, workspaceId, millerHome);
        Assert.NotNull(policyMaybe);
        EffectiveIgnorePolicy policy = policyMaybe!;
        File.WriteAllText(Path.Combine(root, ".julieignore"), "user_only/\n");

        IReadOnlyList<string> paths = ScanIgnorePolicy.PrepareForScan(root, policy);

        Assert.Equal(new[] { ScanIgnorePolicy.InvariantIgnorePathFor(root) }, paths);
        Assert.Equal("user_only/\n", File.ReadAllText(Path.Combine(root, ".julieignore")));
    }

    [Fact]
    public void PrepareForScan_StaleGeneratedDescriptorIsRejectedWhenInheritedPolicyAppears()
    {
        string container = NewDirectory("linked-stale");
        string main = Path.Combine(container, "main");
        string admin = Path.Combine(main, ".git", "worktrees", "feature");
        string root = Path.Combine(container, "wt-feature");
        Directory.CreateDirectory(admin);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {admin}\n");
        File.WriteAllText(Path.Combine(admin, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(main, ".julieignore"), "main_only/\n");

        string millerHome = NewDirectory("miller-home-stale");
        string workspaceId = WorkspaceId.FromCanonicalRoot(root);
        string generatedPath = JulieIgnoreSeeder.GeneratedGlobalIgnorePathForWorkspaceId(workspaceId, millerHome);
        Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
        File.WriteAllText(generatedPath, "generated/\n");
        var stale = new EffectiveIgnorePolicy(
            IgnorePolicySource.GeneratedGlobal,
            generatedPath,
            JulieIgnoreSeeder.ContentHash(File.ReadAllBytes(generatedPath)),
            false);

        IReadOnlyList<string> paths = ScanIgnorePolicy.PrepareForScan(root, stale);

        Assert.Equal(new[] { ScanIgnorePolicy.InvariantIgnorePathFor(root) }, paths);
    }
}
