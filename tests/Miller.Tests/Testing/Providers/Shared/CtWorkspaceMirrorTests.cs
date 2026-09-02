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
}
