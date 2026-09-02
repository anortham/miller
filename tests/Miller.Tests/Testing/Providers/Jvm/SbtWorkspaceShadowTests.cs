using System.Text.Json;
using System.Runtime.InteropServices;
using Miller.Testing;
using Miller.Testing.Providers.Shared;
using Miller.Testing.Providers.Jvm;
using Xunit;

namespace Miller.Tests.Testing.Providers.Jvm;

public sealed class SbtWorkspaceShadowTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-sbt-shadow-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Initial_sync_materializes_the_build_root_and_isolated_unborn_git_barrier()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(Path.Combine(projectRoot, "README.md"), "sbt shadow\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(
            workspace,
            CancellationToken.None);

        string workspaceCandidate = CtGenerationPaths.CacheDirectory(workspace, "sbt-workspace");
        string shadowRoot = Path.Combine(workspaceCandidate, "build");

        Assert.Equal(shadowRoot, result.ShadowRoot);
        Assert.Equal(Path.Combine(shadowRoot, "build.sbt"), result.ShadowProjectPath);
        Assert.Equal("name := \"shadow\"\n", File.ReadAllText(result.ShadowProjectPath));
        Assert.Equal("sbt shadow\n", File.ReadAllText(Path.Combine(shadowRoot, "README.md")));
        Assert.Equal("ref: refs/heads/miller-shadow\n", File.ReadAllText(Path.Combine(shadowRoot, ".git", "HEAD")));
        Assert.True(File.Exists(Path.Combine(shadowRoot, ".git", "config")));
        Assert.True(File.Exists(Path.Combine(workspaceCandidate, "manifest.json")));
        Assert.True(Directory.Exists(CtGenerationPaths.CacheDirectory(workspace, "sbt-deps")));
        Assert.Equal(2, result.EntriesCopied);
        Assert.Equal(0, result.EntriesUpdated);
        Assert.Equal(0, result.EntriesDeleted);
        Assert.Equal(28, result.BytesCopied);
    }

    [Fact]
    public void Warm_noop_sync_reports_zero_copied_files_and_bytes()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(Path.Combine(projectRoot, "README.md"), "sbt shadow\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        _ = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);

        Assert.Equal(0, result.EntriesCopied);
        Assert.Equal(0, result.EntriesUpdated);
        Assert.Equal(0, result.EntriesDeleted);
        Assert.Equal(0, result.BytesCopied);
    }

    [Fact]
    public void One_file_update_reconciles_only_the_changed_file()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string readmePath = Path.Combine(projectRoot, "README.md");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(readmePath, "sbt shadow\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        _ = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        File.WriteAllText(projectPath, "name := \"changed-shadow\"\n");
        File.SetLastWriteTimeUtc(projectPath, DateTime.UtcNow.AddSeconds(2));

        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);

        Assert.Equal("name := \"changed-shadow\"\n", File.ReadAllText(Path.Combine(result.ShadowRoot, "build.sbt")));
        Assert.Equal("sbt shadow\n", File.ReadAllText(Path.Combine(result.ShadowRoot, "README.md")));
        Assert.Equal(0, result.EntriesCopied);
        Assert.Equal(1, result.EntriesUpdated);
        Assert.Equal(0, result.EntriesDeleted);
        Assert.Equal(25, result.BytesCopied);
    }

    [Fact]
    public void Deleting_a_source_file_removes_only_its_manifest_owned_mirror()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string readmePath = Path.Combine(projectRoot, "README.md");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(readmePath, "sbt shadow\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        SbtWorkspaceShadowResult first = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        Assert.Contains("README.md", File.ReadAllText(Path.Combine(first.WorkspaceCandidateRoot, "manifest.json")));
        File.Delete(readmePath);

        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(first.ShadowRoot, "build.sbt")));
        Assert.False(File.Exists(Path.Combine(result.ShadowRoot, "README.md")));
        Assert.Equal(1, result.EntriesDeleted);
        Assert.Equal(0, result.EntriesCopied);
        Assert.Equal(0, result.EntriesUpdated);
    }

    [Fact]
    public void File_to_directory_transition_replaces_the_old_mirror_type()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string transitionPath = Path.Combine(projectRoot, "settings");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(transitionPath, "old\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        _ = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        File.Delete(transitionPath);
        Directory.CreateDirectory(transitionPath);
        File.WriteAllText(Path.Combine(transitionPath, "build.sbt"), "new\n");

        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(result.ShadowRoot, "settings")));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(result.ShadowRoot, "settings", "build.sbt")));
        Assert.False(File.Exists(Path.Combine(result.ShadowRoot, "settings")));
    }

    [Fact]
    public void Directory_to_file_transition_replaces_the_old_mirror_type()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string transitionPath = Path.Combine(projectRoot, "settings");
        Directory.CreateDirectory(transitionPath);
        File.WriteAllText(Path.Combine(transitionPath, "build.sbt"), "old\n");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        _ = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        Directory.Delete(transitionPath, recursive: true);
        File.WriteAllText(transitionPath, "new\n");

        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(result.ShadowRoot, "settings")));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(result.ShadowRoot, "settings")));
        Assert.False(Directory.Exists(Path.Combine(result.ShadowRoot, "settings")));
    }

    [Fact]
    public void Source_file_timestamp_and_permissions_are_preserved_in_the_mirror()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        DateTime sourceTime = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(projectPath, sourceTime);
        UnixFileMode sourceMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(projectPath, sourceMode);

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        string destinationPath = Path.Combine(result.ShadowRoot, "build.sbt");

        Assert.Equal(sourceTime, File.GetLastWriteTimeUtc(destinationPath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(sourceMode, File.GetUnixFileMode(destinationPath));
    }

    [Fact]
    public void Internal_symbolic_links_are_recreated_without_dereferencing()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string targetPath = Path.Combine(projectRoot, "real.txt");
        string linkPath = Path.Combine(projectRoot, "alias.txt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(targetPath, "real\n");
        File.CreateSymbolicLink(linkPath, "real.txt");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        string destinationPath = Path.Combine(result.ShadowRoot, "alias.txt");
        FileInfo destinationInfo = new(destinationPath);

        Assert.Equal("real.txt", destinationInfo.LinkTarget);
        Assert.True(File.GetAttributes(destinationPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("real\n", File.ReadAllText(Path.Combine(result.ShadowRoot, "real.txt")));
    }

    [Fact]
    public void External_symbolic_links_are_rejected_with_the_offending_relative_path()
    {
        string projectRoot = Path.Combine(_root, "project");
        string outsideRoot = Path.Combine(_root, "outside");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(outsideRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string outsidePath = Path.Combine(outsideRoot, "secret.txt");
        string linkPath = Path.Combine(projectRoot, "escape.txt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(outsidePath, "secret\n");
        File.CreateSymbolicLink(linkPath, outsidePath);

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        IOException exception = Assert.Throws<IOException>(() =>
            SbtWorkspaceShadow.Sync(workspace, CancellationToken.None));

        Assert.Contains("escape.txt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Absolute_symbolic_links_are_rejected_even_when_the_target_is_inside_the_source_root()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string targetPath = Path.Combine(projectRoot, "real.txt");
        string linkPath = Path.Combine(projectRoot, "absolute.txt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(targetPath, "real\n");
        File.CreateSymbolicLink(linkPath, targetPath);

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        IOException exception = Assert.Throws<IOException>(() =>
            SbtWorkspaceShadow.Sync(workspace, CancellationToken.None));

        Assert.Contains("absolute.txt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_build_owned_git_barrier_is_not_rewritten_on_warm_sync()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        SbtWorkspaceShadowResult first = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        string headPath = Path.Combine(first.ShadowRoot, ".git", "HEAD");
        File.WriteAllText(headPath, "custom\n");

        SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);

        Assert.Equal("custom\n", File.ReadAllText(headPath));
    }

    [Fact]
    public void Removing_a_source_directory_preserves_nested_build_owned_target_artifacts()
    {
        string projectRoot = Path.Combine(_root, "project");
        string moduleRoot = Path.Combine(projectRoot, "module");
        Directory.CreateDirectory(moduleRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string sourcePath = Path.Combine(moduleRoot, "source.scala");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(sourcePath, "object Source\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        SbtWorkspaceShadowResult first = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        string targetArtifactPath = Path.Combine(first.ShadowRoot, "module", "target", "classes.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(targetArtifactPath)!);
        File.WriteAllText(targetArtifactPath, "compiled\n");
        Directory.Delete(moduleRoot, recursive: true);

        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);

        Assert.True(File.Exists(targetArtifactPath));
        Assert.Equal("compiled\n", File.ReadAllText(targetArtifactPath));
        Assert.True(File.Exists(Path.Combine(result.ShadowRoot, "build.sbt")));
    }

    [Fact]
    public void Self_referential_symbolic_links_are_rejected_with_the_offending_relative_path()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string selfPath = Path.Combine(projectRoot, "self.txt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.CreateSymbolicLink(selfPath, "self.txt");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        IOException exception = Assert.Throws<IOException>(() =>
            SbtWorkspaceShadow.Sync(workspace, CancellationToken.None));

        Assert.Contains("self.txt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_link_symbolic_cycles_are_rejected_with_an_offending_relative_path()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string firstPath = Path.Combine(projectRoot, "first.txt");
        string secondPath = Path.Combine(projectRoot, "second.txt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.CreateSymbolicLink(firstPath, "second.txt");
        File.CreateSymbolicLink(secondPath, "first.txt");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        IOException exception = Assert.Throws<IOException>(() =>
            SbtWorkspaceShadow.Sync(workspace, CancellationToken.None));

        Assert.True(
            exception.Message.Contains("first.txt", StringComparison.Ordinal)
            || exception.Message.Contains("second.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsafe_manifest_paths_cannot_delete_files_outside_the_shadow_root()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string outsidePath = Path.Combine(_root, "outside-sentinel.txt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(outsidePath, "keep\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        SbtWorkspaceShadowResult first = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        string maliciousPath = Path.GetRelativePath(first.ShadowRoot, outsidePath);
        string manifest = JsonSerializer.Serialize(new[]
        {
            new
            {
                Path = maliciousPath,
                Kind = 0,
                Length = new FileInfo(outsidePath).Length,
                LastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(outsidePath).Ticks,
                LinkTarget = (string?)null,
                Hash = (string?)null,
                UnixMode = (int?)null,
                IsReadOnly = false,
            },
        });
        File.WriteAllText(Path.Combine(first.WorkspaceCandidateRoot, "manifest.json"), manifest);

        Exception? exception = Record.Exception(() =>
            SbtWorkspaceShadow.Sync(workspace, CancellationToken.None));

        Assert.True(exception is null or IOException);
        Assert.Equal("keep\n", File.ReadAllText(outsidePath));
    }

    [Fact]
    public void Unix_fifo_source_entries_are_rejected_before_any_read()
    {
        if (OperatingSystem.IsWindows())
            return;

        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string fifoPath = Path.Combine(projectRoot, "stream.fifo");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        Assert.Equal(0, mkfifo(fifoPath, 0x1B6));

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        IOException exception = Assert.Throws<IOException>(() =>
            SbtWorkspaceShadow.Sync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("stream.fifo", exception.Message, StringComparison.Ordinal);
        string workspaceCandidate = CtGenerationPaths.CacheDirectory(workspace, "sbt-workspace");
        Assert.False(File.Exists(Path.Combine(workspaceCandidate, "manifest.json")));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, uint mode);

    [Fact]
    public void Destination_mutation_with_unchanged_metadata_uses_a_hash_fallback_and_repairs_content()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        File.WriteAllText(projectPath, "good\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        SbtWorkspaceShadowResult first = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        string destinationPath = Path.Combine(first.ShadowRoot, "build.sbt");
        DateTime sourceTime = File.GetLastWriteTimeUtc(projectPath);
        File.WriteAllText(destinationPath, "evil\n");
        File.SetLastWriteTimeUtc(destinationPath, sourceTime);

        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);

        Assert.Equal("good\n", File.ReadAllText(destinationPath));
        Assert.Equal(1, result.HashFallbacks);
        Assert.Equal(1, result.EntriesUpdated);
    }

    [Fact]
    public void Destination_permission_drift_is_repaired_from_source_metadata()
    {
        if (OperatingSystem.IsWindows())
            return;

        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        UnixFileMode sourceMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(projectPath, sourceMode);

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        SbtWorkspaceShadowResult first = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        string destinationPath = Path.Combine(first.ShadowRoot, "build.sbt");
        File.SetUnixFileMode(destinationPath, UnixFileMode.UserRead);

        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);

        Assert.Equal(sourceMode, File.GetUnixFileMode(destinationPath));
        Assert.Equal(1, result.EntriesUpdated);
    }

    [Fact]
    public void Nested_git_miller_and_target_sources_are_excluded_while_build_targets_survive()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        Directory.CreateDirectory(Path.Combine(projectRoot, "nested", ".git"));
        Directory.CreateDirectory(Path.Combine(projectRoot, ".miller"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "target"));
        File.WriteAllText(Path.Combine(projectRoot, "nested", ".git", "hidden"), "git\n");
        File.WriteAllText(Path.Combine(projectRoot, ".miller", "hidden"), "miller\n");
        File.WriteAllText(Path.Combine(projectRoot, "target", "generated"), "target\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        SbtWorkspaceShadowResult first = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        string buildTargetPath = Path.Combine(first.ShadowRoot, "target", "classes.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(buildTargetPath)!);
        File.WriteAllText(buildTargetPath, "compiled\n");

        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        string manifest = File.ReadAllText(Path.Combine(result.WorkspaceCandidateRoot, "manifest.json"));

        Assert.DoesNotContain("nested/.git", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain(".miller", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("target", manifest, StringComparison.Ordinal);
        Assert.True(File.Exists(buildTargetPath));
        Assert.False(File.Exists(Path.Combine(result.ShadowRoot, "nested", ".git", "hidden")));
        Assert.False(File.Exists(Path.Combine(result.ShadowRoot, ".miller", "hidden")));
        Assert.False(File.Exists(Path.Combine(result.ShadowRoot, "target", "generated")));
    }

    [Fact]
    public void Case_colliding_source_entries_are_rejected()
    {
        if (OperatingSystem.IsWindows())
            return;

        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(Path.Combine(projectRoot, "Case.scala"), "one\n");
        File.WriteAllText(Path.Combine(projectRoot, "case.scala"), "two\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        IOException exception = Assert.Throws<IOException>(() =>
            SbtWorkspaceShadow.Sync(workspace, CancellationToken.None));

        Assert.Contains("Case.scala", exception.Message, StringComparison.Ordinal);
        Assert.Contains("case.scala", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_copy_over_the_build_cache_budget_is_rejected_with_its_relative_path()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string oversizedPath = Path.Combine(projectRoot, "oversized.bin");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        using (FileStream stream = new(oversizedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(ContinuousTestCoordinatorOptions.DefaultBuildCacheBudgetBytes + 1);

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        IOException exception = Assert.Throws<IOException>(() =>
            SbtWorkspaceShadow.Sync(workspace, CancellationToken.None));

        Assert.Contains("oversized.bin", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Final_mirror_paths_over_the_windows_budget_are_rejected_with_the_relative_path()
    {
        string projectRoot = Path.Combine(_root, "project");
        string deepDirectory = Path.Combine(projectRoot, new string('d', 190));
        Directory.CreateDirectory(deepDirectory);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string deepFile = Path.Combine(deepDirectory, "source.scala");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(deepFile, "object Source\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        IOException exception = Assert.Throws<IOException>(() =>
            SbtWorkspaceShadow.Sync(workspace, CancellationToken.None));

        Assert.Contains(new string('d', 190), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Interrupted_manifest_recovery_reconstructs_ownership_and_repairs_stale_entries()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        string stalePath = Path.Combine(projectRoot, "stale.txt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");
        File.WriteAllText(stalePath, "stale\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        SbtWorkspaceShadowResult first = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);
        File.Delete(stalePath);
        File.WriteAllText(Path.Combine(first.WorkspaceCandidateRoot, "manifest.json"), "{\n");

        SbtWorkspaceShadowResult result = SbtWorkspaceShadow.Sync(workspace, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(result.ShadowRoot, "stale.txt")));
        Assert.Contains("build.sbt", File.ReadAllText(Path.Combine(result.WorkspaceCandidateRoot, "manifest.json")), StringComparison.Ordinal);
        Assert.Equal(1, result.EntriesDeleted);
    }

    [Fact]
    public async Task Source_mutation_during_sync_fails_closed_without_publishing_a_manifest()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        File.WriteAllBytes(projectPath, new byte[32 * 1024 * 1024]);

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        using CancellationTokenSource mutationStop = new();
        Task mutator = Task.Run(() =>
        {
            int offset = 0;
            while (!mutationStop.IsCancellationRequested)
            {
                try
                {
                    using FileStream stream = new(projectPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    stream.Position = offset++ % (32 * 1024 * 1024);
                    stream.WriteByte((byte)offset);
                    stream.Flush(flushToDisk: false);
                    File.SetLastWriteTimeUtc(projectPath, DateTime.UtcNow.AddTicks(offset));
                }
                catch (IOException)
                {
                }
            }
        }, TestContext.Current.CancellationToken);

        await Task.Delay(25, TestContext.Current.CancellationToken);
        Exception? exception = Record.Exception(() =>
            SbtWorkspaceShadow.Sync(workspace, TestContext.Current.CancellationToken));
        mutationStop.Cancel();
        await mutator;

        Assert.IsType<IOException>(exception);
        string workspaceCandidate = CtGenerationPaths.CacheDirectory(workspace, "sbt-workspace");
        Assert.False(File.Exists(Path.Combine(workspaceCandidate, "manifest.json")));
    }

    [Fact]
    public void Precancelled_sync_does_not_create_cache_directories()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SbtWorkspaceShadow.Sync(workspace, cancellation.Token));
        Assert.False(Directory.Exists(workspace.BuildOutputRoot));
    }

    [Fact]
    public async Task Concurrent_synces_share_one_serialized_reconciliation()
    {
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "build.sbt");
        File.WriteAllText(projectPath, "name := \"shadow\"\n");

        ContinuousTestWorkspace workspace = new(
            WorkspaceId: "ws:sbt-shadow",
            WorkspaceRoot: _root,
            ProjectPath: projectPath,
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-sbt"),
            Framework: "sbt");

        Task<SbtWorkspaceShadowResult> first = Task.Run(
            () => SbtWorkspaceShadow.Sync(workspace, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Task<SbtWorkspaceShadowResult> second = Task.Run(
            () => SbtWorkspaceShadow.Sync(workspace, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        SbtWorkspaceShadowResult[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Sum(result => result.EntriesCopied));
        Assert.All(results, result => Assert.Equal(0, result.EntriesUpdated));
        Assert.Equal("name := \"shadow\"\n", File.ReadAllText(results[0].ShadowProjectPath));
        Assert.Contains("build.sbt", File.ReadAllText(Path.Combine(results[0].WorkspaceCandidateRoot, "manifest.json")), StringComparison.Ordinal);
    }
}
