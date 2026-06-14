using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

public sealed class IndexerWatcherSetTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "miller-watcher-set-" + Guid.NewGuid());

    public IndexerWatcherSetTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void Attach_AlwaysCreatesFileAndDirectoryWatchers()
    {
        string root = Root("repo");

        using IndexerWatcherSet watchers = IndexerWatcherSet.Attach(root, NoopCallbacks());

        Assert.True(watchers.HasFileWatcher);
        Assert.True(watchers.HasDirectoryWatcher);
    }

    [Fact]
    public void Attach_GitDirectoryExists_CreatesHeadWatcher()
    {
        string root = Root("repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        using IndexerWatcherSet watchers = IndexerWatcherSet.Attach(root, NoopCallbacks());

        Assert.True(watchers.HasGitHeadWatcher);
    }

    [Fact]
    public void Attach_NoGitDirectory_SkipsHeadWatcher()
    {
        string root = Root("repo");

        using IndexerWatcherSet watchers = IndexerWatcherSet.Attach(root, NoopCallbacks());

        Assert.False(watchers.HasGitHeadWatcher);
    }

    [Fact]
    public void Attach_NestedWorkspace_CreatesAncestorIgnorePolicyWatchers()
    {
        string repo = Root("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "private_data/\n");
        string workspace = Path.Combine(repo, "packages", "app");
        Directory.CreateDirectory(workspace);

        using IndexerWatcherSet watchers = IndexerWatcherSet.Attach(workspace, NoopCallbacks());

        Assert.Equal(2, watchers.AncestorIgnorePolicyWatcherCount);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        string root = Root("repo");
        var watchers = IndexerWatcherSet.Attach(root, NoopCallbacks());

        watchers.Dispose();
        watchers.Dispose();

        Assert.False(watchers.HasFileWatcher);
        Assert.False(watchers.HasDirectoryWatcher);
        Assert.False(watchers.HasGitHeadWatcher);
        Assert.Equal(0, watchers.AncestorIgnorePolicyWatcherCount);
    }

    private string Root(string name)
    {
        string root = Path.Combine(_temp, name);
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static IndexerWatcherCallbacks NoopCallbacks() => new(
        FileChanged: static (_, _) => { },
        FileRenamed: static (_, _) => { },
        Error: static (_, _) => { },
        DirectoryChanged: static (_, _) => { },
        DirectoryRenamed: static (_, _) => { },
        HeadChanged: static (_, _) => { },
        IgnorePolicyChanged: static (_, _) => { });

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }
}
