using System.Runtime.InteropServices;
using Miller.Testing;
using Miller.Testing.Providers.Shared;
using Xunit;

namespace Miller.Tests.Testing.Providers.Shared;

public sealed class CtWorkspaceMirrorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-mirror-").FullName;

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
    public void Sync_materializes_owned_files_and_reports_metadata_digest()
    {
        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "one.txt"), "one");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "nested"));
        File.WriteAllText(Path.Combine(sourceRoot, "nested", "two.txt"), "two");
        ContinuousTestWorkspace workspace = Workspace("shared");

        CtWorkspaceMirrorResult result = CtWorkspaceMirror.Sync(
            workspace,
            sourceRoot,
            Policy(CtWorkspaceMirrorIntegrity.StrictHash),
            CancellationToken.None);

        Assert.Equal(Path.Combine(CtGenerationPaths.CacheDirectory(workspace, "shared"), "shadow"), result.MirrorRoot);
        Assert.Equal("one", File.ReadAllText(Path.Combine(result.MirrorRoot, "one.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(result.MirrorRoot, "nested", "two.txt")));
        Assert.Equal(2, result.EntriesCopied);
        Assert.True(result.BytesCopied > 0);
        Assert.NotEmpty(result.SourceMetadataDigest);
        Assert.True(result.SourceOwnedStateChanged);
    }

    [Fact]
    public void Metadata_fast_path_warm_sync_hashes_and_copies_zero_bytes()
    {
        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "one.txt"), "one");
        ContinuousTestWorkspace workspace = Workspace("fast");
        CtWorkspaceMirrorPolicy policy = Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "fast");

        _ = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);
        CtWorkspaceMirrorResult result = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);

        Assert.Equal(0, result.EntriesCopied);
        Assert.Equal(0, result.EntriesUpdated);
        Assert.Equal(0, result.EntriesDeleted);
        Assert.Equal(0, result.BytesCopied);
        Assert.Equal(0, result.FilesHashed);
        Assert.Equal(0, result.BytesHashed);
        Assert.False(result.SourceOwnedStateChanged);
    }

    [Fact]
    public void Strict_hash_repairs_destination_content_without_source_metadata_change()
    {
        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "one.txt"), "one");
        ContinuousTestWorkspace workspace = Workspace("repair");
        CtWorkspaceMirrorPolicy policy = Policy(CtWorkspaceMirrorIntegrity.StrictHash, "repair");

        CtWorkspaceMirrorResult first = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);
        File.WriteAllText(Path.Combine(first.MirrorRoot, "one.txt"), "bad");
        File.SetLastWriteTimeUtc(Path.Combine(first.MirrorRoot, "one.txt"), File.GetLastWriteTimeUtc(Path.Combine(sourceRoot, "one.txt")));

        CtWorkspaceMirrorResult result = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);

        Assert.Equal("one", File.ReadAllText(Path.Combine(result.MirrorRoot, "one.txt")));
        Assert.Equal(1, result.EntriesUpdated);
        Assert.True(result.FilesHashed > 0);
        Assert.True(result.BytesHashed > 0);
    }

    [Fact]
    public void Excluded_and_build_owned_entries_are_not_source_owned_or_deleted()
    {
        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "source.txt"), "source");
        Directory.CreateDirectory(Path.Combine(sourceRoot, ".git"));
        File.WriteAllText(Path.Combine(sourceRoot, ".git", "config"), "live");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "target"));
        File.WriteAllText(Path.Combine(sourceRoot, "target", "cache.bin"), "cache");
        ContinuousTestWorkspace workspace = Workspace("owned");
        CtWorkspaceMirrorPolicy policy = Policy(CtWorkspaceMirrorIntegrity.StrictHash, "owned");

        CtWorkspaceMirrorResult result = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(result.MirrorRoot, ".git", "HEAD")));
        Assert.True(File.Exists(Path.Combine(result.MirrorRoot, ".git", "config")));
        Assert.False(Directory.Exists(Path.Combine(result.MirrorRoot, "target")));
    }

    [Fact]
    public void Measure_candidate_bytes_does_not_follow_reparse_points()
    {
        string candidate = Path.Combine(_root, "candidate");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(candidate);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "large.txt"), new string('x', 100));
        string link = Path.Combine(candidate, "outside");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Assert.Equal(0, CtWorkspaceMirror.MeasureCandidateBytes(candidate));
    }

    [Fact]
    public void A_reparse_candidate_root_is_rejected_before_any_source_is_copied()
    {
        string sourceRoot = Path.Combine(_root, "source");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(sourceRoot, "one.txt"), "one");
        ContinuousTestWorkspace workspace = Workspace("reparse");
        string candidate = CtGenerationPaths.CacheDirectory(workspace, "reparse");
        Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
        try
        {
            Directory.CreateSymbolicLink(candidate, outside);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Assert.Throws<IOException>(() => CtWorkspaceMirror.Sync(
            workspace,
            sourceRoot,
            Policy(CtWorkspaceMirrorIntegrity.StrictHash, "reparse"),
            CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(outside, "shadow", "one.txt")));
    }

    [Fact]
    public void Metadata_fast_path_does_not_adopt_an_unowned_matching_destination()
    {
        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        string sourcePath = Path.Combine(sourceRoot, "one.txt");
        File.WriteAllText(sourcePath, "good");
        ContinuousTestWorkspace workspace = Workspace("unowned");
        CtWorkspaceMirrorPolicy policy = Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "unowned");

        CtWorkspaceMirrorResult first = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);
        string destinationPath = Path.Combine(first.MirrorRoot, "one.txt");
        File.WriteAllText(destinationPath, "evil");
        File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
        File.WriteAllText(Path.Combine(first.CandidateRoot, "manifest.json"), "[]");

        CtWorkspaceMirrorResult result = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);

        Assert.Equal("good", File.ReadAllText(destinationPath));
        Assert.Equal(1, result.EntriesCopied);
    }

    [Fact]
    public void A_new_empty_directory_marks_source_owned_state_as_changed()
    {
        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "one.txt"), "one");
        ContinuousTestWorkspace workspace = Workspace("empty-dir");
        CtWorkspaceMirrorPolicy policy = Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "empty-dir");

        _ = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(sourceRoot, "empty"));

        CtWorkspaceMirrorResult result = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(result.MirrorRoot, "empty")));
        Assert.True(result.SourceOwnedStateChanged);
    }

    [Fact]
    public void Sync_preserves_empty_directory_timestamp_and_permissions()
    {
        string sourceRoot = Path.Combine(_root, "source");
        string sourceDirectory = Path.Combine(sourceRoot, "empty");
        Directory.CreateDirectory(sourceDirectory);
        UnixFileMode sourceMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(sourceDirectory, sourceMode);
        DateTime sourceTime = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(sourceDirectory, sourceTime);
        ContinuousTestWorkspace workspace = Workspace("empty-directory-metadata");

        CtWorkspaceMirrorResult result = CtWorkspaceMirror.Sync(
            workspace,
            sourceRoot,
            Policy(CtWorkspaceMirrorIntegrity.StrictHash, "empty-directory-metadata"),
            CancellationToken.None);

        string mirroredDirectory = Path.Combine(result.MirrorRoot, "empty");
        Assert.Equal(sourceTime, Directory.GetLastWriteTimeUtc(mirroredDirectory));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(sourceMode, File.GetUnixFileMode(mirroredDirectory));
    }

    [Fact]
    public void Metadata_fast_path_reconciles_changed_and_deleted_files()
    {
        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        string changedPath = Path.Combine(sourceRoot, "changed.txt");
        string deletedPath = Path.Combine(sourceRoot, "deleted.txt");
        File.WriteAllText(changedPath, "old");
        File.WriteAllText(deletedPath, "gone");
        ContinuousTestWorkspace workspace = Workspace("delta");
        CtWorkspaceMirrorPolicy policy = Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "delta");

        CtWorkspaceMirrorResult first = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);
        File.WriteAllText(changedPath, "new");
        File.Delete(deletedPath);

        CtWorkspaceMirrorResult result = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);

        Assert.Equal("new", File.ReadAllText(Path.Combine(first.MirrorRoot, "changed.txt")));
        Assert.False(File.Exists(Path.Combine(result.MirrorRoot, "deleted.txt")));
        Assert.Equal(1, result.EntriesUpdated);
        Assert.Equal(1, result.EntriesDeleted);
    }

    [Fact]
    public void Metadata_fast_path_rejects_case_collisions_before_writing()
    {
        if (OperatingSystem.IsWindows())
            return;

        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "Case.txt"), "one");
        File.WriteAllText(Path.Combine(sourceRoot, "case.txt"), "two");
        ContinuousTestWorkspace workspace = Workspace("collision");

        IOException error = Assert.Throws<IOException>(() => CtWorkspaceMirror.Sync(
            workspace,
            sourceRoot,
            Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "collision"),
            CancellationToken.None));

        Assert.Contains("Case.txt", error.Message, StringComparison.Ordinal);
        Assert.Contains("case.txt", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(CtGenerationPaths.CacheDirectory(workspace, "collision")));
    }

    [Fact]
    public void Metadata_fast_path_rejects_traversal_manifest_entries_without_touching_sentinels()
    {
        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        string sourcePath = Path.Combine(sourceRoot, "one.txt");
        string sentinel = Path.Combine(_root, "sentinel.txt");
        File.WriteAllText(sourcePath, "one");
        File.WriteAllText(sentinel, "keep");
        ContinuousTestWorkspace workspace = Workspace("manifest");
        CtWorkspaceMirrorPolicy policy = Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "manifest");

        CtWorkspaceMirrorResult first = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);
        File.WriteAllText(
            Path.Combine(first.CandidateRoot, "manifest.json"),
            "[{\"Path\":\"../../sentinel.txt\",\"Kind\":0,\"Length\":4,\"LastWriteTimeUtcTicks\":0,\"IsReadOnly\":false}]");

        Assert.Throws<IOException>(() => CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Metadata_fast_path_recovers_an_interrupted_manifest_and_removes_stale_entries()
    {
        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        string stalePath = Path.Combine(sourceRoot, "stale.txt");
        File.WriteAllText(Path.Combine(sourceRoot, "one.txt"), "one");
        File.WriteAllText(stalePath, "stale");
        ContinuousTestWorkspace workspace = Workspace("recovery");
        CtWorkspaceMirrorPolicy policy = Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "recovery");

        CtWorkspaceMirrorResult first = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);
        File.Delete(stalePath);
        File.WriteAllText(Path.Combine(first.CandidateRoot, "manifest.json"), "{\n");

        CtWorkspaceMirrorResult result = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(result.MirrorRoot, "stale.txt")));
        Assert.Equal(1, result.EntriesDeleted);
    }

    [Fact]
    public void Metadata_fast_path_rejects_an_escaping_link()
    {
        if (OperatingSystem.IsWindows())
            return;

        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "one.txt"), "one");
        File.CreateSymbolicLink(Path.Combine(sourceRoot, "escape"), "../outside");
        ContinuousTestWorkspace workspace = Workspace("links");

        IOException error = Assert.Throws<IOException>(() => CtWorkspaceMirror.Sync(
            workspace,
            sourceRoot,
            Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "links"),
            CancellationToken.None));

        Assert.Contains("escape", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_fast_path_rejects_a_symbolic_link_cycle_before_materializing_the_mirror()
    {
        if (OperatingSystem.IsWindows())
            return;

        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "one.txt"), "one");
        File.CreateSymbolicLink(Path.Combine(sourceRoot, "first"), "second");
        File.CreateSymbolicLink(Path.Combine(sourceRoot, "second"), "first");
        ContinuousTestWorkspace workspace = Workspace("cycle");

        IOException error = Assert.Throws<IOException>(() => CtWorkspaceMirror.Sync(
            workspace,
            sourceRoot,
            Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "cycle"),
            CancellationToken.None));

        Assert.True(error.Message.Contains("first", StringComparison.Ordinal)
            || error.Message.Contains("second", StringComparison.Ordinal));
        Assert.False(Directory.Exists(CtGenerationPaths.CacheDirectory(workspace, "cycle")));
    }

    [Fact]
    public void Metadata_fast_path_rejects_special_files_without_publishing_a_manifest()
    {
        if (OperatingSystem.IsWindows())
            return;

        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        string fifoPath = Path.Combine(sourceRoot, "stream.fifo");
        File.WriteAllText(Path.Combine(sourceRoot, "one.txt"), "one");
        Assert.Equal(0, mkfifo(fifoPath, 0x1B6));
        ContinuousTestWorkspace workspace = Workspace("special");

        IOException error = Assert.Throws<IOException>(() => CtWorkspaceMirror.Sync(
            workspace,
            sourceRoot,
            Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "special"),
            CancellationToken.None));

        Assert.Contains("stream.fifo", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(CtGenerationPaths.CacheDirectory(workspace, "special"), "manifest.json")));
    }

    [Fact]
    public void Metadata_fast_path_rejects_a_source_tree_over_the_build_cache_budget()
    {
        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        string oversizedPath = Path.Combine(sourceRoot, "oversized.bin");
        using (FileStream stream = new(oversizedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(ContinuousTestCoordinatorOptions.DefaultBuildCacheBudgetBytes + 1);
        ContinuousTestWorkspace workspace = Workspace("budget");

        IOException error = Assert.Throws<IOException>(() => CtWorkspaceMirror.Sync(
            workspace,
            sourceRoot,
            Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "budget"),
            CancellationToken.None));

        Assert.Contains("oversized.bin", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(CtGenerationPaths.CacheDirectory(workspace, "budget")));
    }

    [Fact]
    public void Metadata_fast_path_preserves_file_metadata_and_rejects_long_mirror_paths()
    {
        string sourceRoot = Path.Combine(_root, "source");
        string deepDirectory = Path.Combine(sourceRoot, new string('d', 190));
        string sourcePath = Path.Combine(sourceRoot, "one.txt");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(sourcePath, "one");
        DateTime sourceTime = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sourcePath, sourceTime);
        ContinuousTestWorkspace workspace = Workspace("metadata");
        CtWorkspaceMirrorPolicy policy = Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "metadata");
        CtWorkspaceMirrorResult result = CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None);

        Assert.Equal(sourceTime, File.GetLastWriteTimeUtc(Path.Combine(result.MirrorRoot, "one.txt")));
        Directory.CreateDirectory(deepDirectory);
        File.WriteAllText(Path.Combine(deepDirectory, "source.scala"), "object Source\n");

        IOException error = Assert.Throws<IOException>(() => CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None));

        Assert.Contains(new string('d', 190), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metadata_fast_path_honors_cancellation_and_serializes_concurrent_syncs()
    {
        string sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "one.txt"), "one");
        ContinuousTestWorkspace workspace = Workspace("concurrent");
        CtWorkspaceMirrorPolicy policy = Policy(CtWorkspaceMirrorIntegrity.MetadataFastPath, "concurrent");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, cancellation.Token));
        Assert.False(Directory.Exists(workspace.BuildOutputRoot));

        Task<CtWorkspaceMirrorResult> first = Task.Run(
            () => CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None));
        Task<CtWorkspaceMirrorResult> second = Task.Run(
            () => CtWorkspaceMirror.Sync(workspace, sourceRoot, policy, CancellationToken.None));
        CtWorkspaceMirrorResult[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Sum(result => result.EntriesCopied));
        Assert.All(results, result => Assert.Equal(0, result.EntriesUpdated));
    }

    private ContinuousTestWorkspace Workspace(string name) => new(
        WorkspaceId: $"ws:{name}",
        WorkspaceRoot: _root,
        ProjectPath: Path.Combine(_root, "source", "one.txt"),
        BuildOutputRoot: Path.Combine(_root, ".miller", $"ct-{name}"),
        Framework: "test");

    private static CtWorkspaceMirrorPolicy Policy(CtWorkspaceMirrorIntegrity integrity, string cacheName = "shared") => new(
        ProviderName: "test",
        CacheName: cacheName,
        MirrorDirectoryName: "shadow",
        ExcludedEntryNames: [".git", ".miller", "target"],
        BuildOwnedEntryNames: [".git", "target"],
        CreateGitBarrier: true,
        Integrity: integrity);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, uint mode);
}
