using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SidecarStagingReaperTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("miller-reaper-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Stage(string name, TimeSpan age)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "stale staging content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void ReapStale_DeletesOnlyStalePrefixedFiles()
    {
        string stale = Stage(".search-build-aaaa.db", TimeSpan.FromHours(2));
        string fresh = Stage(".search-build-bbbb.db", TimeSpan.Zero);
        string otherPrefix = Stage(".content-build-cccc.db", TimeSpan.FromHours(2));
        string target = Stage("search.db", TimeSpan.FromHours(2));

        int reaped = SidecarStagingReaper.ReapStale(_dir, ".search-build-", TimeSpan.FromMinutes(15));

        Assert.Equal(1, reaped);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(otherPrefix));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void ReapStale_NeverDeletesTheCallersOwnStagingFile()
    {
        string own = Stage(".search-build-own.db", TimeSpan.FromHours(2));

        int reaped = SidecarStagingReaper.ReapStale(_dir, ".search-build-", TimeSpan.FromMinutes(15), own);

        Assert.Equal(0, reaped);
        Assert.True(File.Exists(own));
    }

    [Fact]
    public void ReapStale_MissingDirectoryReturnsZero()
    {
        Assert.Equal(0, SidecarStagingReaper.ReapStale(Path.Combine(_dir, "absent"), ".search-build-", TimeSpan.Zero));
    }

    [Fact]
    public void SearchIndexWriter_Write_ReapsStaleStagingSiblings()
    {
        string stale = Stage(".search-build-dead.db", TimeSpan.FromHours(2));
        string searchDb = Path.Combine(_dir, "search.db");

        SearchIndexWriter.Write(searchDb, [], revision: 1);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(searchDb));
        Assert.Empty(Directory.EnumerateFiles(_dir, ".search-build-*.db"));
    }
}
