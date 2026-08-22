using Miller.Indexing;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins effective <c>.julieignore</c> policy ownership: user and inherited policy stays in-tree, while a fresh
/// ordinary root receives deterministic baseline/vendor bytes under an isolated Miller-home policy directory.
/// Tiny temp trees — fast suite (the same pattern as <c>WatchPathFilterTests</c>; no subprocess anywhere on this
/// path).
/// </summary>
public sealed class JulieIgnoreSeederTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "miller-julieignore-seed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static void WriteTree(string root, params string[] relativeFiles)
    {
        foreach (string relative in relativeFiles)
        {
            string full = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "// fixture");
        }
    }

    private static string LinkedWorktree(string container, string? mainIgnoreContents)
    {
        string main = System.IO.Path.Combine(container, "main");
        string gitDir = System.IO.Path.Combine(main, ".git", "worktrees", "feature");
        string worktree = System.IO.Path.Combine(container, "wt-feature");
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(worktree);
        if (mainIgnoreContents is not null)
            File.WriteAllText(System.IO.Path.Combine(main, ".julieignore"), mainIgnoreContents);
        File.WriteAllText(System.IO.Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n");
        File.WriteAllText(System.IO.Path.Combine(gitDir, "commondir"), "../..\n");
        return worktree;
    }

    private static EffectiveIgnorePolicy Prepare(string root, string? millerHome = null)
    {
        string home = millerHome ?? System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(root)!, "miller-home-" + Guid.NewGuid().ToString("N"));
        EffectiveIgnorePolicy? policy = JulieIgnoreSeeder.PreparePolicy(
            root, WorkspaceId.FromCanonicalRoot(Path.GetFullPath(root)), home);
        Assert.NotNull(policy);
        return policy!;
    }

    [Fact]
    public void PreparePolicy_PlainRoot_UsesDeterministicGlobalPolicyWithoutWritingRoot()
    {
        using var temp = new TempDir();
        string millerHome = System.IO.Path.Combine(temp.Path, "miller-home");
        string root = System.IO.Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(root);
        string workspaceId = WorkspaceId.FromCanonicalRoot(root);

        EffectiveIgnorePolicy? policyMaybe = JulieIgnoreSeeder.PreparePolicy(root, workspaceId, millerHome);
        Assert.NotNull(policyMaybe);
        EffectiveIgnorePolicy policy = policyMaybe!;
        Assert.Equal(IgnorePolicySource.GeneratedGlobal, policy.Source);
        Assert.True(policy.WroteNewBytes);
        Assert.False(File.Exists(System.IO.Path.Combine(root, ".julieignore")));
        Assert.Equal(
            System.IO.Path.Combine(millerHome, "ignore-policies", workspaceId + ".julieignore"),
            policy.Path);
        Assert.Equal(policy.ContentHash, JulieIgnoreSeeder.ContentHash(File.ReadAllBytes(policy.Path)));

        EffectiveIgnorePolicy? secondMaybe = JulieIgnoreSeeder.PreparePolicy(root, workspaceId, millerHome);
        Assert.NotNull(secondMaybe);
        EffectiveIgnorePolicy second = secondMaybe!;
        Assert.False(second.WroteNewBytes);
        Assert.Equal(policy.ContentHash, second.ContentHash);
        Assert.Equal(File.ReadAllBytes(policy.Path), File.ReadAllBytes(second.Path));
    }

    [Fact]
    public void PreparePolicy_UserRootWinsAndNeverCreatesGlobalPolicy()
    {
        using var temp = new TempDir();
        string millerHome = System.IO.Path.Combine(temp.Path, "miller-home");
        string root = System.IO.Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(root);
        string userPath = System.IO.Path.Combine(root, ".julieignore");
        const string UserContent = "# user\ngenerated/\n";
        File.WriteAllText(userPath, UserContent);
        string workspaceId = WorkspaceId.FromCanonicalRoot(root);

        EffectiveIgnorePolicy? policyMaybe = JulieIgnoreSeeder.PreparePolicy(root, workspaceId, millerHome);
        Assert.NotNull(policyMaybe);
        EffectiveIgnorePolicy policy = policyMaybe!;
        Assert.Equal(IgnorePolicySource.UserRoot, policy.Source);
        Assert.False(policy.WroteNewBytes);
        Assert.Equal(Path.GetFullPath(userPath), policy.Path);
        Assert.Equal(UserContent, File.ReadAllText(userPath));
        Assert.False(File.Exists(
            System.IO.Path.Combine(millerHome, "ignore-policies", workspaceId + ".julieignore")));
    }

    [Fact]
    public void PreparePolicy_LinkedWorktreeRetainsMalformedInheritedCopyInTree()
    {
        using var temp = new TempDir();
        string millerHome = System.IO.Path.Combine(temp.Path, "miller-home");
        string worktree = LinkedWorktree(temp.Path, "a{b\n");
        string workspaceId = WorkspaceId.FromCanonicalRoot(worktree);

        EffectiveIgnorePolicy? policyMaybe = JulieIgnoreSeeder.PreparePolicy(worktree, workspaceId, millerHome);
        Assert.NotNull(policyMaybe);
        EffectiveIgnorePolicy policy = policyMaybe!;
        Assert.Equal(IgnorePolicySource.InheritedRootCopy, policy.Source);
        Assert.True(policy.WroteNewBytes);
        Assert.Equal(Path.GetFullPath(System.IO.Path.Combine(worktree, ".julieignore")), policy.Path);
        Assert.Contains("a{b", File.ReadAllText(policy.Path), StringComparison.Ordinal);
        Assert.False(File.Exists(
            System.IO.Path.Combine(millerHome, "ignore-policies", workspaceId + ".julieignore")));
    }

    [Fact]
    public void PreparePolicy_MainCheckoutPolicyAppearingDuringRenderWinsOverGeneratedPublication()
    {
        using var temp = new TempDir();
        string worktree = LinkedWorktree(temp.Path, mainIgnoreContents: null);
        string mainPolicy = Path.Combine(temp.Path, "main", ".julieignore");
        string millerHome = Path.Combine(temp.Path, "miller-home");
        string workspaceId = WorkspaceId.FromCanonicalRoot(worktree);

        EffectiveIgnorePolicy? policy = JulieIgnoreSeeder.PreparePolicy(
            worktree,
            workspaceId,
            millerHome,
            betweenProbeAndCreate: () => File.WriteAllText(mainPolicy, "generated/\n"));

        Assert.NotNull(policy);
        Assert.Equal(IgnorePolicySource.InheritedRootCopy, policy!.Source);
        Assert.Contains("generated/", File.ReadAllText(Path.Combine(worktree, ".julieignore")), StringComparison.Ordinal);
        Assert.False(File.Exists(
            Path.Combine(millerHome, "ignore-policies", workspaceId + ".julieignore")));
    }

    [Fact]
    public void ResolvePolicyForUpdate_DoesNotWalkOrMaterializeMissingGeneratedPolicy()
    {
        using var temp = new TempDir();
        string root = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "package"));
        string millerHome = Path.Combine(temp.Path, "miller-home");
        string workspaceId = WorkspaceId.FromCanonicalRoot(root);

        EffectiveIgnorePolicy? policy = JulieIgnoreSeeder.ResolvePolicyForUpdate(
            root, workspaceId, millerHome);

        Assert.Null(policy);
        Assert.False(Directory.Exists(millerHome));
        Assert.False(File.Exists(Path.Combine(root, ".julieignore")));
    }

    [Fact]
    public void ResolvePolicyForUpdate_LinkedMainPolicyCreatesOnlyTheInheritedSnapshot()
    {
        using var temp = new TempDir();
        string worktree = LinkedWorktree(temp.Path, "a{b\n");
        File.Delete(Path.Combine(worktree, ".julieignore"));
        string millerHome = Path.Combine(temp.Path, "miller-home");
        string workspaceId = WorkspaceId.FromCanonicalRoot(worktree);

        EffectiveIgnorePolicy? policy = JulieIgnoreSeeder.ResolvePolicyForUpdate(
            worktree, workspaceId, millerHome);

        Assert.NotNull(policy);
        Assert.Equal(IgnorePolicySource.InheritedRootCopy, policy!.Source);
        Assert.Contains("a{b", File.ReadAllText(Path.Combine(worktree, ".julieignore")), StringComparison.Ordinal);
        Assert.False(Directory.Exists(millerHome));
        Assert.False(File.Exists(
            Path.Combine(millerHome, "ignore-policies", workspaceId + ".julieignore")));
    }

    [Fact]
    public void PreparePolicy_RejectsAWorkspaceIdForAnotherRoot()
    {
        using var temp = new TempDir();
        string root = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(root);
        string millerHome = Path.Combine(temp.Path, "miller-home");
        string wrongId = WorkspaceId.FromCanonicalRoot(Path.Combine(temp.Path, "other"));

        Assert.Throws<ArgumentException>(() =>
            JulieIgnoreSeeder.PreparePolicy(root, wrongId, millerHome));
        Assert.False(Directory.Exists(millerHome));
    }

    [Fact]
    public void EnsureSeeded_NoExistingFile_WritesBaselineAndDetectedVendorDirs()
    {
        using var temp = new TempDir();
        WriteTree(
            temp.Path,
            "src/main.cs",
            "node_modules/a/1.js", "node_modules/a/2.js", "node_modules/b/3.js",
            "node_modules/b/4.js", "node_modules/c/5.js", "node_modules/c/6.js");

        EffectiveIgnorePolicy policy = Prepare(temp.Path);

        string content = File.ReadAllText(policy.Path);
        Assert.False(File.Exists(System.IO.Path.Combine(temp.Path, ".julieignore")));
        Assert.StartsWith("# .julieignore", content, StringComparison.Ordinal);
        Assert.Contains("Generated by Miller", content, StringComparison.Ordinal);
        Assert.Contains("owned by Miller", content, StringComparison.Ordinal);
        Assert.Contains("Create .julieignore at the workspace root", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit freely", content, StringComparison.Ordinal);
        Assert.Contains("\n*.log\n", content, StringComparison.Ordinal);
        Assert.Contains("\n.miller/\n", content, StringComparison.Ordinal);
        Assert.Contains("\nnode_modules/\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSeeded_ExistingFile_IsNeverOverwrittenOrAppended()
    {
        using var temp = new TempDir();
        WriteTree(temp.Path, "node_modules/a/1.js", "node_modules/a/2.js");
        string ignorePath = System.IO.Path.Combine(temp.Path, ".julieignore");
        const string UserAuthored = "# mine — hands off\nonly_this/\n";
        File.WriteAllText(ignorePath, UserAuthored);

        Assert.False(Prepare(temp.Path).WroteNewBytes);

        Assert.Equal(UserAuthored, File.ReadAllText(ignorePath)); // byte-for-byte untouched
    }

    [Fact]
    public void EnsureSeeded_SecondCall_IsANoOp_BecauseTheFirstGenerationNowExists()
    {
        using var temp = new TempDir();
        WriteTree(temp.Path, "src/main.cs");

        string millerHome = Path.Combine(temp.Path, "miller-home");
        EffectiveIgnorePolicy first = Prepare(temp.Path, millerHome);
        EffectiveIgnorePolicy second = Prepare(temp.Path, millerHome);

        Assert.False(second.WroteNewBytes);
        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void EnsureSeeded_UserAuthoredFileAppearsInsideTheRaceWindow_KeepsTheirsAndReportsNotSeeded()
    {
        using var temp = new TempDir();
        WriteTree(temp.Path, "node_modules/a/1.js", "node_modules/a/2.js");
        string ignorePath = System.IO.Path.Combine(temp.Path, ".julieignore");
        const string UserAuthored = "# mine — hands off\nonly_this/\n";

        string millerHome = Path.Combine(temp.Path, "miller-home");
        EffectiveIgnorePolicy? policy = JulieIgnoreSeeder.PreparePolicy(
            temp.Path,
            WorkspaceId.FromCanonicalRoot(temp.Path),
            millerHome,
            betweenProbeAndCreate: () => File.WriteAllText(ignorePath, UserAuthored));

        Assert.NotNull(policy);
        Assert.Equal(IgnorePolicySource.UserRoot, policy!.Source);
        Assert.Equal(UserAuthored, File.ReadAllText(ignorePath));
    }

    [Fact]
    public void EnsureSeeded_ConcurrentSeedersOnOneFreshRoot_ReportExactlyOneWriter()
    {
        using var temp = new TempDir();
        WriteTree(temp.Path, "src/main.cs");
        const int Racers = 8;
        string millerHome = Path.Combine(temp.Path, "miller-home");
        using var start = new Barrier(Racers);
        var seeded = new bool[Racers];
        var racers = new Thread[Racers];

        for (int i = 0; i < Racers; i++)
        {
            int index = i;
            racers[i] = new Thread(() =>
            {
                start.SignalAndWait();
                seeded[index] = JulieIgnoreSeeder.PreparePolicy(
                    temp.Path, WorkspaceId.FromCanonicalRoot(temp.Path), millerHome)!.WroteNewBytes;
            })
            { IsBackground = true };
            racers[i].Start();
        }

        foreach (Thread racer in racers)
            Assert.True(racer.Join(TimeSpan.FromSeconds(30)));

        Assert.Equal(1, seeded.Count(won => won));
    }

    [Fact]
    public void EnsureSeeded_MissingRoot_ReturnsFalse_NeverThrows()
    {
        string missing = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "miller-julieignore-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(JulieIgnoreSeeder.EnsureSeeded(missing));
    }

    [Fact]
    public void EnsureSeeded_DetectionWalk_SkipsVcsInternals_ButDetectsVendorDirs()
    {
        using var temp = new TempDir();
        WriteTree(
            temp.Path,
            ".git/objects/aa/1", ".git/objects/aa/2", ".git/objects/bb/3",
            ".git/objects/bb/4", ".git/objects/cc/5", ".git/objects/cc/6",
            "dist/a/1.js", "dist/a/2.js", "dist/b/3.js", "dist/b/4.js", "dist/c/5.js", "dist/c/6.js",
            "src/main.cs");

        EffectiveIgnorePolicy policy = Prepare(temp.Path);

        string content = File.ReadAllText(policy.Path);
        Assert.Contains("\ndist/\n", content, StringComparison.Ordinal);
        Assert.DoesNotContain(".git", content, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSeeded_DetectionWalk_SkipsNestedWorktreePools()
    {
        using var temp = new TempDir();
        WriteTree(
            temp.Path,
            ".worktrees/feature/dist/a/1.js", ".worktrees/feature/dist/a/2.js",
            ".worktrees/feature/dist/b/3.js", ".worktrees/feature/dist/b/4.js",
            ".worktrees/feature/dist/c/5.js", ".worktrees/feature/dist/c/6.js",
            ".claude/worktrees/other/out/a/1.js", ".claude/worktrees/other/out/a/2.js",
            ".claude/worktrees/other/out/b/3.js", ".claude/worktrees/other/out/b/4.js",
            ".claude/worktrees/other/out/c/5.js", ".claude/worktrees/other/out/c/6.js",
            "src/main.cs");

        EffectiveIgnorePolicy policy = Prepare(temp.Path);

        string content = File.ReadAllText(policy.Path);
        Assert.DoesNotContain("worktrees", content, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSeeded_LinkedWorktree_CopiesTheMainCheckoutIgnoreFileInTree()
    {
        using var temp = new TempDir();
        string worktree = LinkedWorktree(temp.Path, "generated/\nsecrets/\n");

        Assert.True(JulieIgnoreSeeder.EnsureSeeded(worktree));

        string content = File.ReadAllText(System.IO.Path.Combine(worktree, ".julieignore"));
        Assert.Contains("generated/", content, StringComparison.Ordinal);
        Assert.Contains("secrets/", content, StringComparison.Ordinal);
        Assert.Contains(
            System.IO.Path.Combine(temp.Path, "main", ".julieignore"), content, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSeeded_LinkedWorktreeWhoseMainIgnoreFileCannotBeRead_WritesNothingAndRetriesLater()
    {
        using var temp = new TempDir();
        string worktree = LinkedWorktree(temp.Path, "generated/\nsecrets/\n");
        string ignorePath = System.IO.Path.Combine(worktree, ".julieignore");

        string millerHome = Path.Combine(temp.Path, "miller-home");
        EffectiveIgnorePolicy? policy = JulieIgnoreSeeder.PreparePolicy(
            worktree,
            WorkspaceId.FromCanonicalRoot(worktree),
            millerHome,
            readAllText: _ => throw new IOException("main checkout file is momentarily unreadable"));

        Assert.Null(policy);
        Assert.False(File.Exists(ignorePath));

        Assert.True(JulieIgnoreSeeder.PreparePolicy(
            worktree, WorkspaceId.FromCanonicalRoot(worktree), millerHome)!.WroteNewBytes);
        Assert.Contains("secrets/", File.ReadAllText(ignorePath), StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSeeded_LinkedWorktreeCopy_IsVisibleToTheWatcherIgnorePolicy()
    {
        using var temp = new TempDir();
        string worktree = LinkedWorktree(temp.Path, "generated/\n");

        JulieIgnoreSeeder.EnsureSeeded(worktree);

        Assert.False(WatchPathFilter.ShouldProcess(
            worktree, System.IO.Path.Combine(worktree, "generated", "foo.cs")));
        Assert.True(WatchPathFilter.ShouldProcess(
            worktree, System.IO.Path.Combine(worktree, "src", "keep.cs")));
    }

    [Fact]
    public void EnsureSeeded_LinkedWorktreeCopy_CarriesTheMainCheckoutBaselinePatterns()
    {
        using var temp = new TempDir();
        string worktree = LinkedWorktree(temp.Path, "# seeded by miller\n.miller/\n*.log\nnode_modules/\n");

        JulieIgnoreSeeder.EnsureSeeded(worktree);

        Assert.False(WatchPathFilter.ShouldProcess(
            worktree, System.IO.Path.Combine(worktree, "daemon.log")));
    }

    [Fact]
    public void EnsureSeeded_LinkedWorktreeWithItsOwnIgnoreFile_IsNeverOverwritten()
    {
        using var temp = new TempDir();
        string worktree = LinkedWorktree(temp.Path, "generated/\n");
        string local = System.IO.Path.Combine(worktree, ".julieignore");
        File.WriteAllText(local, "local_only/\n");

        Assert.False(JulieIgnoreSeeder.EnsureSeeded(worktree));
        Assert.Equal("local_only/\n", File.ReadAllText(local));
    }

    [Fact]
    public void EnsureSeeded_LinkedWorktreeWhoseMainCheckoutHasNone_FallsBackToTheGeneratedSeed()
    {
        using var temp = new TempDir();
        string worktree = LinkedWorktree(temp.Path, mainIgnoreContents: null);

        EffectiveIgnorePolicy policy = Prepare(worktree);

        string content = File.ReadAllText(policy.Path);
        Assert.Equal(IgnorePolicySource.GeneratedGlobal, policy.Source);
        Assert.Contains("Generated by Miller", content, StringComparison.Ordinal);
        foreach (string pattern in VendorScan.BaselinePatterns)
            Assert.Contains(pattern, content, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSeeded_PlainCheckout_SeedsTheGeneratedFileNotACopy()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, ".git"));

        EffectiveIgnorePolicy policy = Prepare(temp.Path);

        string content = File.ReadAllText(policy.Path);
        Assert.Equal(IgnorePolicySource.GeneratedGlobal, policy.Source);
        Assert.Contains("Generated by Miller", content, StringComparison.Ordinal);
        Assert.DoesNotContain("main checkout", content, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSeeded_MalformedMainCheckoutIgnoreFile_IsCopiedVerbatim()
    {
        using var temp = new TempDir();
        string worktree = LinkedWorktree(temp.Path, "a{b\n");

        Assert.True(JulieIgnoreSeeder.EnsureSeeded(worktree));

        Assert.Contains(
            "a{b", File.ReadAllText(System.IO.Path.Combine(worktree, ".julieignore")), StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveInheritedIgnoreFile_LinkedWorktree_ResolvesTheMainCheckoutFile()
    {
        using var temp = new TempDir();
        string worktree = LinkedWorktree(temp.Path, "generated/\n");

        Assert.Equal(
            System.IO.Path.Combine(temp.Path, "main", ".julieignore"),
            JulieIgnoreSeeder.ResolveInheritedIgnoreFile(worktree));
    }

    [Fact]
    public void ResolveInheritedIgnoreFile_LinkedWorktreeWithItsOwnFile_ResolvesNothing()
    {
        using var temp = new TempDir();
        string worktree = LinkedWorktree(temp.Path, "generated/\n");
        File.WriteAllText(System.IO.Path.Combine(worktree, ".julieignore"), "local/\n");

        Assert.Null(JulieIgnoreSeeder.ResolveInheritedIgnoreFile(worktree));
    }

    [Fact]
    public void ResolveInheritedIgnoreFile_LinkedWorktreeWhoseMainCheckoutHasNone_ResolvesNothing()
    {
        using var temp = new TempDir();

        Assert.Null(JulieIgnoreSeeder.ResolveInheritedIgnoreFile(LinkedWorktree(temp.Path, null)));
    }

    [Fact]
    public void ResolveInheritedIgnoreFile_PlainCheckout_ResolvesNothing()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, ".git"));

        Assert.Null(JulieIgnoreSeeder.ResolveInheritedIgnoreFile(temp.Path));
    }

    [Fact]
    public void ResolveInheritedIgnoreFile_NonGitRoot_ResolvesNothing()
    {
        using var temp = new TempDir();

        Assert.Null(JulieIgnoreSeeder.ResolveInheritedIgnoreFile(temp.Path));
    }

    [Fact]
    public void RenderInheritedContent_NamesTheSourceAndKeepsTheContentVerbatim()
    {
        string content = JulieIgnoreSeeder.RenderInheritedContent("/main/.julieignore", "generated/\n*.log\n");

        Assert.Contains("/main/.julieignore", content, StringComparison.Ordinal);
        Assert.EndsWith("generated/\n*.log\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderInheritedContent_HeaderIsCommentsOnly()
    {
        string content = JulieIgnoreSeeder.RenderInheritedContent("/main/.julieignore", "generated/\n");

        string[] header = content.Split("\n\n", 2, StringSplitOptions.None)[0].Split('\n');
        Assert.All(header, line => Assert.StartsWith("#", line, StringComparison.Ordinal));
    }

    [Fact]
    public void RenderInheritedContent_SourceWithoutATrailingNewline_StillEndsOnOne()
    {
        string content = JulieIgnoreSeeder.RenderInheritedContent("/main/.julieignore", "generated/");

        Assert.EndsWith("generated/\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_VendorNamedDirectory_IsPrunedNotEnumerated()
    {
        using var temp = new TempDir();
        WriteTree(
            temp.Path,
            "src/main.cs",
            "node_modules/a/1.js", "node_modules/a/2.js", "node_modules/b/3.js",
            "node_modules/b/4.js", "node_modules/c/5.js", "node_modules/c/6.js");

        var detection = JulieIgnoreSeeder.Detect(temp.Path, maxEnumeratedFiles: 2);

        Assert.Contains("node_modules", detection.VendorDirectories);
        Assert.False(detection.Truncated);
    }

    [Fact]
    public void Detect_VendorNamedDirectoryBelowTheFileThreshold_IsNotReported()
    {
        using var temp = new TempDir();
        WriteTree(temp.Path, "src/main.cs", "out/1.js", "out/2.js");

        var detection = JulieIgnoreSeeder.Detect(temp.Path);

        Assert.DoesNotContain("out", detection.VendorDirectories);
    }

    [Fact]
    public void Detect_NonVendorTreeWithinTheBound_IsNotReportedAsTruncated()
    {
        using var temp = new TempDir();
        WriteTree(temp.Path, "src/main.cs", "docs/readme.md");

        Assert.False(JulieIgnoreSeeder.Detect(temp.Path).Truncated);
    }

    [Fact]
    public void Detect_FileBoundReached_ReportsTruncationInsteadOfStoppingSilently()
    {
        using var temp = new TempDir();
        WriteTree(temp.Path, "src/a.cs", "src/b.cs", "src/c.cs", "src/d.cs");

        Assert.True(JulieIgnoreSeeder.Detect(temp.Path, maxEnumeratedFiles: 2).Truncated);
    }

    [Fact]
    public void Detect_FileBoundNotReached_ReportsNoTruncation()
    {
        using var temp = new TempDir();
        WriteTree(temp.Path, "src/a.cs", "src/b.cs");

        Assert.False(JulieIgnoreSeeder.Detect(temp.Path, maxEnumeratedFiles: 100).Truncated);
    }

    [Fact]
    public void Detect_LargeVendorTree_DoesNotConsumeTheFileBound()
    {
        using var temp = new TempDir();
        var files = new List<string> { "src/main.cs" };
        for (int i = 0; i < 200; i++)
            files.Add($"node_modules/pkg{i}/index.js");
        WriteTree(temp.Path, files.ToArray());

        var detection = JulieIgnoreSeeder.Detect(temp.Path, maxEnumeratedFiles: 50);

        Assert.Contains("node_modules", detection.VendorDirectories);
        Assert.False(detection.Truncated);
    }

    [Fact]
    public void RenderContent_TruncatedDetection_SaysSoInTheGeneratedFile()
    {
        string content = JulieIgnoreSeeder.RenderContent(
            Array.Empty<string>(),
            new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            detectionTruncated: true);

        Assert.Contains("# TRUNCATED: detection stopped after 200000 files", content, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderContent_CompleteDetection_HasNoTruncationNote()
    {
        string content = JulieIgnoreSeeder.RenderContent(
            Array.Empty<string>(), new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.DoesNotContain("TRUNCATED", content, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderContent_CleanTree_HasHeaderAndBaseline_NoVendorSection()
    {
        string content = JulieIgnoreSeeder.RenderContent(
            Array.Empty<string>(), new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc));

        Assert.StartsWith("# .julieignore", content, StringComparison.Ordinal);
        Assert.Contains("Generated by Miller", content, StringComparison.Ordinal);
        Assert.Contains("\n*.log\n", content, StringComparison.Ordinal);
        Assert.Contains("\n.miller/\n", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Auto-detected", content, StringComparison.Ordinal);
        // Every non-comment line must be a usable gitignore pattern (no stray prose).
        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(line.StartsWith('#') || line is "*.log" or ".miller/", $"unexpected non-pattern line: '{line}'");
    }

    [Fact]
    public void RenderContent_VendorDirs_AreEmittedAsDirectoryPatterns()
    {
        string content = JulieIgnoreSeeder.RenderContent(
            new[] { "node_modules", "wwwroot/scripts" },
            new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("\nnode_modules/\n", content, StringComparison.Ordinal);
        Assert.Contains("\nwwwroot/scripts/\n", content, StringComparison.Ordinal);
    }
}
