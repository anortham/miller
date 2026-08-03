using Microsoft.Data.Sqlite;
using Miller.Indexing;
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
public sealed class WorktreeIgnorePropagationScaleTests
{
    [Fact]
    public void LinkedWorktreeScan_AppliesTheMainCheckoutJulieignore()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new WorkTree();
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

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
        }
    }
}
