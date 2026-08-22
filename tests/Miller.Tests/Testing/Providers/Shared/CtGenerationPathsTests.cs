using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Shared;

public sealed class CtGenerationPathsTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-generation-paths-").FullName;

    private readonly HashSet<string> _ctTemps = new(StringComparer.Ordinal);

    public void Dispose()
    {
        foreach (var temp in _ctTemps)
            BestEffortDelete(temp);
        BestEffortDelete(_dir);
    }

    /// <summary>
    /// The compiler cache is PROJECT-stable, not per-operation (finding F7). Its directory name can never
    /// collide with a generation: <see cref="CtGenerationPaths.IsGenerationId"/> accepts only 'g' plus
    /// twelve lowercase hex characters.
    /// </summary>
    [Fact]
    public void The_cache_root_is_stable_beside_the_generations_and_is_not_a_generation_id()
    {
        var workspace = Workspace();

        var first = CtGenerationPaths.Allocate(workspace);
        var second = CtGenerationPaths.Allocate(workspace);

        Assert.Equal(Path.Combine(workspace.BuildOutputRoot, "cache"), CtGenerationPaths.CacheRoot(workspace));
        Assert.Equal(
            Path.Combine(workspace.BuildOutputRoot, "cache", "cargo"),
            CtGenerationPaths.CacheDirectory(workspace, "cargo"));
        Assert.NotEqual(first.GenerationRoot, second.GenerationRoot);
        Assert.False(CtGenerationPaths.IsGenerationId("cache"));
    }

    [Fact]
    public void Allocate_uses_short_hashed_generation_ids()
    {
        var workspace = Workspace();

        var first = CtGenerationPaths.Allocate(workspace);
        var second = CtGenerationPaths.Allocate(workspace);

        Assert.True(CtGenerationPaths.IsGenerationId(first.GenerationId));
        Assert.True(CtGenerationPaths.IsGenerationId(second.GenerationId));
        Assert.NotEqual(first.GenerationId, second.GenerationId);
        Assert.True(first.GenerationId.Length <= 16);
        Assert.True(second.GenerationId.Length <= 16);
        Assert.True(Directory.Exists(first.GenerationRoot));
        Assert.True(Directory.Exists(second.GenerationRoot));
    }

    [Fact]
    public void Allocate_skips_a_generation_a_competing_allocator_claimed_first()
    {
        var workspace = Workspace();
        var seen = new List<int>();

        var allocated = CtGenerationPaths.Allocate(workspace, ordinal =>
        {
            seen.Add(ordinal);
            if (ordinal != 1)
                return;
            var claimed = Path.Combine(workspace.BuildOutputRoot, CtGenerationPaths.IdForOrdinal(workspace, 1));
            Directory.CreateDirectory(claimed);
            File.WriteAllText(Path.Combine(claimed, CtGenerationPaths.AllocationMarkerFileName), "1");
        });

        Assert.Equal([1, 2], seen);
        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 2), allocated.GenerationId);
    }

    [Fact]
    public void Allocate_ignores_foreign_directories_when_choosing_the_next_generation()
    {
        var workspace = Workspace();
        CreateDirectories(
            workspace.BuildOutputRoot,
            "foreign",
            "g1",
            "g0000001",
            "obj",
            "bin");

        var allocated = CtGenerationPaths.Allocate(workspace);

        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 1), allocated.GenerationId);
    }

    [Fact]
    public void ResolveLatestOrFirst_returns_the_latest_allocated_generation()
    {
        var workspace = Workspace();
        CtGenerationPaths.Allocate(workspace);
        var latest = CtGenerationPaths.Allocate(workspace);

        var resolved = CtGenerationPaths.ResolveLatestOrFirst(workspace);

        Assert.Equal(latest.GenerationId, resolved.GenerationId);
        Assert.Equal(latest.GenerationRoot, resolved.GenerationRoot);
        Assert.Equal(latest.OutDir, resolved.OutDir);
        Assert.Equal(latest.ResultsDirectory, resolved.ResultsDirectory);
        Assert.Equal(latest.BinlogPath, resolved.BinlogPath);
        Assert.Equal(latest.TempDirectory, resolved.TempDirectory);
    }

    [Fact]
    public void ResolveLatestOrFirst_returns_the_first_generation_without_creating_anything()
    {
        var workspace = Workspace();

        var resolved = CtGenerationPaths.ResolveLatestOrFirst(workspace);

        Assert.Equal(CtGenerationPaths.IdForOrdinal(workspace, 1), resolved.GenerationId);
        Assert.False(Directory.Exists(workspace.BuildOutputRoot));
        Assert.False(Directory.Exists(resolved.GenerationRoot));
        Assert.False(Directory.Exists(resolved.TempDirectory));
    }

    [Fact]
    public void Generation_members_all_live_under_the_generation_root()
    {
        var workspace = Workspace();

        var paths = CtGenerationPaths.Allocate(workspace);

        Assert.Equal(Path.Combine(workspace.BuildOutputRoot, paths.GenerationId), paths.GenerationRoot);
        Assert.StartsWith(paths.GenerationRoot, paths.OutDir, StringComparison.Ordinal);
        Assert.StartsWith(paths.GenerationRoot, paths.ResultsDirectory, StringComparison.Ordinal);
        Assert.StartsWith(paths.GenerationRoot, paths.BinlogPath, StringComparison.Ordinal);
        Assert.False(paths.GenerationRoot.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Assert.False(paths.GenerationRoot.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(paths.GenerationRoot, "out") + Path.DirectorySeparatorChar, paths.OutDir);
        Assert.Equal(
            Path.Combine(paths.GenerationRoot, "TestResults") + Path.DirectorySeparatorChar,
            paths.ResultsDirectory);
        Assert.Equal(Path.Combine(paths.GenerationRoot, "logs", "build.binlog"), paths.BinlogPath);
    }

    [Fact]
    public void Generation_temp_is_generation_scoped_under_miller_ct()
    {
        var workspace = Workspace();

        var paths = CtGenerationPaths.Allocate(workspace);
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));

        Assert.Equal(CtTempPaths.ForGeneration(workspace, paths.GenerationId), paths.TempDirectory);
        Assert.Contains(Path.DirectorySeparatorChar + "miller-ct" + Path.DirectorySeparatorChar, paths.TempDirectory, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.WorkspaceRoot, paths.TempDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Generation_root_is_contained_under_supervised_build_output_root()
    {
        var workspace = Workspace();
        var paths = CtGenerationPaths.Allocate(workspace);

        Assert.True(IsContained(workspace.BuildOutputRoot, paths.GenerationRoot));
        Assert.True(IsContained(workspace.BuildOutputRoot, paths.OutDir));
        Assert.True(IsContained(workspace.BuildOutputRoot, paths.ResultsDirectory));
        Assert.True(IsContained(workspace.BuildOutputRoot, paths.BinlogPath));
        Assert.False(IsContained(workspace.WorkspaceRoot, paths.GenerationRoot));
    }

    [Fact]
    public void Windows_generation_dir_names_leave_max_path_headroom()
    {
        var workspace = Workspace();
        var paths = CtGenerationPaths.Allocate(workspace);

        Assert.True(paths.GenerationId.Length <= 16);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(paths.GenerationRoot.Length < 200, paths.GenerationRoot);
            Assert.True(paths.BinlogPath.Length < 240, paths.BinlogPath);
        }
    }

    [Fact]
    public void TryReap_renames_then_deletes_a_generation_tree()
    {
        var workspace = Workspace();
        var paths = CtGenerationPaths.Allocate(workspace);
        paths.EnsureDirectories();
        File.WriteAllText(Path.Combine(paths.OutDir, "marker.txt"), "x");

        Assert.True(CtGenerationPaths.TryReap(paths.GenerationRoot));
        Assert.False(Directory.Exists(paths.GenerationRoot));
    }

    private ContinuousTestWorkspace Workspace()
    {
        var workspaceRoot = Path.Combine(_dir, "repo");
        var buildRoot = Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build");
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:1",
            WorkspaceRoot: workspaceRoot,
            ProjectPath: Path.Combine(workspaceRoot, "tests", "Sample.Tests", "Sample.Tests.csproj"),
            BuildOutputRoot: buildRoot);
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        return workspace;
    }

    private static void CreateDirectories(string root, params string[] names)
    {
        foreach (var name in names)
            Directory.CreateDirectory(Path.Combine(root, name));
    }

    private static bool IsContained(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.Ordinal)
               || string.Equals(fullPath, Path.GetFullPath(root), StringComparison.Ordinal);
    }

    private static void BestEffortDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
