using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Testing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Live proof of the scan-time ignore policy against the pinned binary: a linked worktree is seeded with an
/// in-tree COPY of the main checkout's <c>.julieignore</c>, that copy governs the incremental
/// <c>update</c> path as well as the scan (the divergence an <c>--ignore-file</c> propagation could not close),
/// a MALFORMED main-checkout file still produces an artifact because in-tree ignore files only warn, an
/// UNREADABLE one seeds nothing so the copy is retried rather than permanently replaced by the generated
/// baseline, and Miller's invariant file keeps a repo-root <c>.worktrees/</c> pool out of the parent index.
/// Spawns the pinned binary, so it is <c>[Trait("Category","Scale")]</c> and obtains the binary via
/// <see cref="ScaleTestSupport.RequireJulieServer"/>; SKIPS when <c>.tools/julie-extract</c> is absent.
/// </summary>
[Trait("Category", "Scale")]
[Collection(MillerHomeEnvironmentCollection.Name)]
public sealed class WorktreeIgnorePropagationScaleTests
{
    [Fact]
    public void LinkedWorktreeScan_AppliesTheMainCheckoutJulieignore()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
        using var home = work.IsolatedMillerHome();
        string main = work.MainCheckout(".julieignore", "generated/\n");
        string worktree = work.LinkedWorktree(main, "feature");
        WriteSource(Path.Combine(worktree, "src", "keep.cs"), "KeptWidget");
        WriteSource(Path.Combine(worktree, "generated", "skip.cs"), "GeneratedWidget");

        ExtractReport report = new JulieExtractRunner(binary).Scan(worktree, work.DbFor(worktree));

        Assert.NotEqual("failed", report.Status);
        var names = SymbolNames(work.DbFor(worktree));
        Assert.Contains("KeptWidget", names);
        Assert.DoesNotContain("GeneratedWidget", names);
    }

    [Fact]
    public void LinkedWorktreeScan_SeedsTheCopyInTree_SoTheExclusionSurvivesTheNextScan()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
        using var home = work.IsolatedMillerHome();
        string main = work.MainCheckout(".julieignore", "generated/\n");
        string worktree = work.LinkedWorktree(main, "feature");
        WriteSource(Path.Combine(worktree, "src", "keep.cs"), "KeptWidget");
        WriteSource(Path.Combine(worktree, "generated", "skip.cs"), "GeneratedWidget");
        var runner = new JulieExtractRunner(binary);

        runner.Scan(worktree, work.DbFor(worktree));
        Assert.True(File.Exists(Path.Combine(worktree, ".julieignore")));

        runner.Scan(worktree, work.DbFor(worktree), force: true);

        var names = SymbolNames(work.DbFor(worktree));
        Assert.Contains("KeptWidget", names);
        Assert.DoesNotContain("GeneratedWidget", names);
    }

    [Fact]
    public void LinkedWorktreeUpdate_OnAFileTheScanExcluded_InsertsNothing()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
        using var home = work.IsolatedMillerHome();
        string main = work.MainCheckout(".julieignore", "generated/\n");
        string worktree = work.LinkedWorktree(main, "feature");
        WriteSource(Path.Combine(worktree, "src", "keep.cs"), "KeptWidget");
        string excluded = Path.Combine(worktree, "generated", "skip.cs");
        WriteSource(excluded, "GeneratedWidget");
        var runner = new JulieExtractRunner(binary);
        runner.Scan(worktree, work.DbFor(worktree));

        runner.Update(worktree, Path.GetFullPath(work.DbFor(worktree)), excluded);

        Assert.DoesNotContain("GeneratedWidget", SymbolNames(work.DbFor(worktree)));
    }

    [Fact]
    public void Update_OnAFileTheInvariantIgnoreFileExcludes_InsertsNothing()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
        using var home = work.IsolatedMillerHome();
        string repo = work.MainCheckout();
        WriteSource(Path.Combine(repo, "src", "keep.cs"), "KeptWidget");
        string nested = Path.Combine(repo, ".worktrees", "feature", "src", "nested.cs");
        WriteSource(nested, "NestedWidget");
        var runner = new JulieExtractRunner(binary);
        runner.Scan(repo, work.DbFor(repo));

        runner.Update(repo, Path.GetFullPath(work.DbFor(repo)), nested);

        Assert.DoesNotContain("NestedWidget", SymbolNames(work.DbFor(repo)));
    }

    [Fact]
    public void LinkedWorktreeScan_WithAMalformedMainCheckoutJulieignore_StillProducesAnArtifact()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
        using var home = work.IsolatedMillerHome();
        string main = work.MainCheckout(".julieignore", "a{b\n");
        string worktree = work.LinkedWorktree(main, "feature");
        WriteSource(Path.Combine(worktree, "src", "keep.cs"), "KeptWidget");

        ExtractReport report = new JulieExtractRunner(binary).Scan(worktree, work.DbFor(worktree));

        Assert.NotEqual("failed", report.Status);
        Assert.True(File.Exists(work.DbFor(worktree)));
        Assert.Contains("KeptWidget", SymbolNames(work.DbFor(worktree)));
    }

    [Fact]
    public void LinkedWorktreeScan_WithAnUnreadableMainCheckoutJulieignore_SeedsNothingUntilItCanBeRead()
    {
        if (OperatingSystem.IsWindows())
            return;

        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
        using var home = work.IsolatedMillerHome();
        string main = work.MainCheckout(".julieignore", "generated/\n");
        string ignoreFile = Path.Combine(main, ".julieignore");
        File.SetUnixFileMode(ignoreFile, UnixFileMode.None);
        string worktree = work.LinkedWorktree(main, "feature");
        string seeded = Path.Combine(worktree, ".julieignore");
        WriteSource(Path.Combine(worktree, "src", "keep.cs"), "KeptWidget");
        WriteSource(Path.Combine(worktree, "generated", "skip.cs"), "GeneratedWidget");
        var runner = new JulieExtractRunner(binary);

        try
        {
            ExtractReport report = runner.Scan(worktree, work.DbFor(worktree));

            Assert.NotEqual("failed", report.Status);
            Assert.Contains("KeptWidget", SymbolNames(work.DbFor(worktree)));
            Assert.False(File.Exists(seeded));
        }
        finally
        {
            File.SetUnixFileMode(ignoreFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        runner.Scan(worktree, work.DbFor(worktree), force: true);

        Assert.Contains("generated/", File.ReadAllText(seeded), StringComparison.Ordinal);
        var names = SymbolNames(work.DbFor(worktree));
        Assert.Contains("KeptWidget", names);
        Assert.DoesNotContain("GeneratedWidget", names);
    }

    [Fact]
    public void Scan_ExcludesARepoRootWorktreePool_ViaTheInvariantIgnoreFile()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
        using var home = work.IsolatedMillerHome();
        string repo = work.MainCheckout();
        WriteSource(Path.Combine(repo, "src", "keep.cs"), "KeptWidget");
        WriteSource(Path.Combine(repo, ".worktrees", "feature", "src", "nested.cs"), "NestedWidget");
        WriteSource(Path.Combine(repo, ".claude", "worktrees", "other", "src", "nested.cs"), "ClaudeNestedWidget");

        ExtractReport report = new JulieExtractRunner(binary).Scan(repo, work.DbFor(repo));

        Assert.NotEqual("failed", report.Status);
        var names = SymbolNames(work.DbFor(repo));
        Assert.Contains("KeptWidget", names);
        Assert.DoesNotContain("NestedWidget", names);
        Assert.DoesNotContain("ClaudeNestedWidget", names);
    }

    [Fact]
    public void Scan_OfAWorkspaceRootedInsideAWorktreePool_IndexesItsOwnFilesAndExcludesOnlyNestedPools()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
        using var home = work.IsolatedMillerHome();
        string main = work.MainCheckout();
        string worktree = work.LinkedWorktreeInPool(main, "feature");
        WriteSource(Path.Combine(worktree, "src", "keep.cs"), "KeptWidget");
        WriteSource(Path.Combine(worktree, ".worktrees", "inner", "src", "nested.cs"), "NestedWidget");

        ExtractReport report = new JulieExtractRunner(binary).Scan(worktree, work.DbFor(worktree));

        Assert.NotEqual("failed", report.Status);
        var names = SymbolNames(work.DbFor(worktree));
        Assert.Contains("KeptWidget", names);
        Assert.DoesNotContain("NestedWidget", names);
    }

    [Fact]
    public void Scan_ExcludesTheMillerSidecar_EvenWithAUserAuthoredJulieignoreThatOmitsIt()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
        using var home = work.IsolatedMillerHome();
        string repo = work.MainCheckout(".julieignore", "# mine, and it says nothing about .miller\n");
        WriteSource(Path.Combine(repo, "src", "keep.cs"), "KeptWidget");
        string logs = Path.Combine(repo, ".miller", "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "miller-20260802.jsonl"), "{\"MillerLogMarker\":1}\n");

        ExtractReport report = new JulieExtractRunner(binary).Scan(repo, work.DbFor(repo));

        Assert.NotEqual("failed", report.Status);
        Assert.Contains("KeptWidget", SymbolNames(work.DbFor(repo)));
        Assert.DoesNotContain("MillerLogMarker", SymbolNames(work.DbFor(repo)));
    }

    [Fact]
    public void FreshPlainScan_UsesGlobalGeneratedPolicy_AndKeepsRootClean()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
        using var home = work.IsolatedMillerHome();
        string repo = work.MainCheckout();
        WriteSource(Path.Combine(repo, "src", "keep.cs"), "KeptWidget");
        for (int i = 0; i < 4; i++)
            WriteSource(Path.Combine(repo, "libs", $"jquery-{i}.cs"), $"VendorWidget{i}");
        work.InitializeGit(repo);

        string db = work.DbFor(repo);
        string workspaceId = WorkspaceId.FromCanonicalRoot(repo);
        using var registry = WorkspaceRegistry.Open(Path.Combine(home.MillerDirectory, "registry.db"));
        string beforeStatus = work.GitStatus(repo);
        Assert.Equal(string.Empty, beforeStatus);
        registry.UpsertSeen(workspaceId, "fresh-plain", repo, db);
        Assert.Equal(beforeStatus, work.GitStatus(repo));

        ExtractReport report = new JulieExtractRunner(binary).Scan(repo, db);

        string generatedPath = JulieIgnoreSeeder.GeneratedGlobalIgnorePathFor(repo);
        Assert.NotEqual("failed", report.Status);
        Assert.False(File.Exists(Path.Combine(repo, ".julieignore")));
        Assert.True(File.Exists(generatedPath));
        string generated = File.ReadAllText(generatedPath);
        Assert.Contains("*.log", generated, StringComparison.Ordinal);
        Assert.Contains("libs/", generated, StringComparison.Ordinal);
        var names = SymbolNames(db);
        Assert.Contains("KeptWidget", names);
        Assert.DoesNotContain("VendorWidget0", names);
        Assert.False(WatchPathFilter.ShouldProcess(repo, Path.Combine(repo, "libs", "jquery-0.cs")));
        Assert.True(WorkspaceIgnorePolicy.IsIgnored(repo, Path.Combine(repo, "libs", "jquery-0.cs"), home.MillerDirectory));

        string afterStatus = work.GitStatus(repo);
        Assert.Equal("?? .miller/", afterStatus);
        Assert.DoesNotContain(".julieignore", afterStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshPlainScan_DirectUpdateUsesTheSameGeneratedPolicy()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
        using var home = work.IsolatedMillerHome();
        string repo = work.MainCheckout();
        WriteSource(Path.Combine(repo, "src", "keep.cs"), "KeptWidget");
        for (int i = 0; i < 4; i++)
            WriteSource(Path.Combine(repo, "libs", $"jquery-{i}.cs"), $"VendorWidget{i}");
        string excluded = Path.Combine(repo, "libs", "jquery-0.cs");
        var runner = new JulieExtractRunner(binary);
        runner.Scan(repo, work.DbFor(repo));

        WriteSource(excluded, "ReintroducedVendorWidget");
        ExtractReport update = runner.Update(
            PathCanonicalizer.CanonicalizeRoot(repo),
            Path.GetFullPath(work.DbFor(repo)),
            PathCanonicalizer.CanonicalizeFile(repo, excluded));

        Assert.Equal("unsupported", update.Status);
        Assert.DoesNotContain("ReintroducedVendorWidget", SymbolNames(work.DbFor(repo)));
        Assert.False(WatchPathFilter.ShouldProcess(repo, excluded));
        Assert.True(WorkspaceIgnorePolicy.IsIgnored(repo, excluded, home.MillerDirectory));
    }

    [Fact]
    public void FreshPlainScan_UserRootTakesAuthority_AndRemovalLeavesItUntouched()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
        using var home = work.IsolatedMillerHome();
        string repo = work.MainCheckout();
        WriteSource(Path.Combine(repo, "src", "keep.cs"), "KeptWidget");
        for (int i = 0; i < 4; i++)
            WriteSource(Path.Combine(repo, "libs", $"jquery-{i}.cs"), $"VendorWidget{i}");
        string db = work.DbFor(repo);
        var runner = new JulieExtractRunner(binary);
        runner.Scan(repo, db);

        string generatedPath = JulieIgnoreSeeder.GeneratedGlobalIgnorePathFor(repo);
        Assert.True(File.Exists(generatedPath));
        string rootPolicy = Path.Combine(repo, ".julieignore");
        File.WriteAllText(rootPolicy, "# user-owned policy\n");
        byte[] rootBytes = File.ReadAllBytes(rootPolicy);

        EffectiveIgnorePolicy policy = JulieIgnoreSeeder.PreparePolicy(repo, WorkspaceId.FromCanonicalRoot(repo))!;
        Assert.Equal(IgnorePolicySource.UserRoot, policy.Source);
        Assert.DoesNotContain(generatedPath, ScanIgnorePolicy.PrepareForScan(repo, policy));

        ExtractReport report = runner.Scan(repo, db, force: true);

        Assert.NotEqual("failed", report.Status);
        Assert.Equal(rootBytes, File.ReadAllBytes(rootPolicy));
        Assert.Contains("VendorWidget0", SymbolNames(db));
        Assert.True(WatchPathFilter.ShouldProcess(repo, Path.Combine(repo, "libs", "jquery-0.cs")));
        Assert.False(WorkspaceIgnorePolicy.IsIgnored(repo, Path.Combine(repo, "libs", "jquery-0.cs"), home.MillerDirectory));

        string registryPath = Path.Combine(home.MillerDirectory, "registry.db");
        using var registry = WorkspaceRegistry.Open(registryPath);
        registry.UpsertSeen(WorkspaceId.FromCanonicalRoot(repo), "fresh-plain", repo, db);
        WorkspaceRemoveResult removed = WorkspaceRemoval.RemoveByPath(
            registry, repo, millerDirectory: home.MillerDirectory, liveRoot: null);

        Assert.Equal(WorkspaceRemoveResult.Outcome.Removed, removed.Result);
        Assert.False(File.Exists(generatedPath));
        Assert.Equal(rootBytes, File.ReadAllBytes(rootPolicy));
        Assert.False(Directory.Exists(Path.Combine(repo, ".miller")));
    }

    private static void WriteSource(string path, string typeName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"namespace Demo;\n\npublic sealed class {typeName}\n{{\n}}\n");
    }

    private static IReadOnlyList<string> SymbolNames(string dbPath) =>
        SqliteSymbolReader.Read(dbPath).Select(s => s.Name).ToArray();

    private sealed class WorkTree : IDisposable
    {
        private readonly string _root;

        public WorkTree()
        {
            _root = Path.Combine(Path.GetTempPath(), "miller-wt-ignore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _root = PathCanonicalizer.CanonicalizeRoot(_root);
        }

        public string MainCheckout(string? ignoreFileName = null, string? ignoreContents = null)
        {
            string main = Path.Combine(_root, "main");
            Directory.CreateDirectory(Path.Combine(main, ".git"));
            if (ignoreFileName is not null && ignoreContents is not null)
                File.WriteAllText(Path.Combine(main, ignoreFileName), ignoreContents);
            return main;
        }

        public string LinkedWorktree(string mainCheckout, string name) =>
            LinkWorktreeAt(mainCheckout, name, Path.Combine(_root, "wt-" + name));

        public string LinkedWorktreeInPool(string mainCheckout, string name) =>
            LinkWorktreeAt(mainCheckout, name, Path.Combine(mainCheckout, ".worktrees", name));

        private static string LinkWorktreeAt(string mainCheckout, string name, string worktree)
        {
            Directory.CreateDirectory(worktree);
            string gitDir = Path.Combine(mainCheckout, ".git", "worktrees", name);
            Directory.CreateDirectory(gitDir);
            File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n");
            File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");
            return worktree;
        }

        public string DbFor(string root) => Path.Combine(root, ".miller", "symbols.db");

        public IsolatedMillerHome IsolatedMillerHome() => new(Path.Combine(_root, "miller-home"));

        public void InitializeGit(string root)
        {
            RunGit(root, "init", "--quiet");
            RunGit(root, "config", "user.email", "miller-scale@example.invalid");
            RunGit(root, "config", "user.name", "Miller Scale");
            RunGit(root, "add", "src", "libs");
            RunGit(root, "commit", "--quiet", "-m", "fixture baseline");
        }

        public string GitStatus(string root) => RunGit(root, "status", "--short");

        private static string RunGit(string root, params string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string arg in args)
                start.ArgumentList.Add(arg);
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
            return stdout.ReplaceLineEndings().Trim();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    private sealed class IsolatedMillerHome : IDisposable
    {
        private readonly string? _previous;

        public IsolatedMillerHome(string home)
        {
            Directory.CreateDirectory(home);
            MillerDirectory = Path.Combine(home, ".miller");
            _previous = Environment.GetEnvironmentVariable(MillerHome.EnvironmentVariable);
            Environment.SetEnvironmentVariable(MillerHome.EnvironmentVariable, home);
        }

        public string MillerDirectory { get; }

        public void Dispose() => Environment.SetEnvironmentVariable(MillerHome.EnvironmentVariable, _previous);
    }
}
