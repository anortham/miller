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

    [Fact]
    public void ReapWorkspaceStaging_ReclaimsBothPrefixesWithoutStartingASidecarBuild()
    {
        string staleSearch = Stage(".search-build-aaaa.db", TimeSpan.FromHours(2));
        string staleContent = Stage(".content-build-bbbb.db", TimeSpan.FromHours(2));
        string artifact = Stage("symbols.db", TimeSpan.FromHours(2));

        int reaped = SidecarStagingReaper.ReapWorkspaceStaging(_dir, SidecarStagingReaper.DefaultStaleAge);

        Assert.Equal(2, reaped);
        Assert.False(File.Exists(staleSearch));
        Assert.False(File.Exists(staleContent));
        Assert.True(File.Exists(artifact));
        Assert.False(File.Exists(Path.Combine(_dir, "search.db")));
        Assert.False(File.Exists(Path.Combine(_dir, "content.db")));
    }

    [Fact]
    public void ReapWorkspaceStaging_ReclaimsOrphansInAWorkspaceStuckInScanError()
    {
        string orphan = Stage(".search-build-stuck.db", TimeSpan.FromHours(2));
        string failureJournal = Path.Combine(_dir, "scan-failure.json");
        File.WriteAllText(
            failureJournal,
            """{"last_intent":"IncrementalReconcile","exit_code":2,"consecutive_failures":9}""");

        int reaped = SidecarStagingReaper.ReapWorkspaceStaging(_dir, SidecarStagingReaper.DefaultStaleAge);

        Assert.Equal(1, reaped);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(failureJournal));
    }

    [Fact]
    public void ReapWorkspaceStaging_KeepsStagingFilesYoungerThanTheDefaultStaleAge()
    {
        string liveBuild = Stage(".search-build-live.db", TimeSpan.Zero);
        string recent = Stage(".content-build-recent.db", TimeSpan.FromMinutes(14));
        string stale = Stage(".search-build-dead.db", TimeSpan.FromMinutes(16));

        int reaped = SidecarStagingReaper.ReapWorkspaceStaging(_dir, SidecarStagingReaper.DefaultStaleAge);

        Assert.Equal(1, reaped);
        Assert.True(File.Exists(liveBuild));
        Assert.True(File.Exists(recent));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void ReapWorkspaceStaging_AbsentOrUnnamedDirectoryReturnsZero()
    {
        Assert.Equal(0, SidecarStagingReaper.ReapWorkspaceStaging(null, SidecarStagingReaper.DefaultStaleAge));
        Assert.Equal(0, SidecarStagingReaper.ReapWorkspaceStaging("   ", SidecarStagingReaper.DefaultStaleAge));
        Assert.Equal(
            0,
            SidecarStagingReaper.ReapWorkspaceStaging(
                Path.Combine(_dir, "absent"),
                SidecarStagingReaper.DefaultStaleAge));
    }

    [Fact]
    public void ReapWorkspaceStaging_SwallowsAReapFailureSoALifecycleCallCannotFail()
    {
        int reaped = SidecarStagingReaper.ReapWorkspaceStaging(
            _dir,
            SidecarStagingReaper.DefaultStaleAge,
            (_, _, _, _) => throw new UnauthorizedAccessException("staging directory is not readable"));

        Assert.Equal(0, reaped);
    }

    [Fact]
    public void ReapWorkspaceStaging_StillReapsTheOtherPrefixWhenOneFails()
    {
        int reaped = SidecarStagingReaper.ReapWorkspaceStaging(
            _dir,
            SidecarStagingReaper.DefaultStaleAge,
            (_, prefix, _, _) => prefix == ".search-build-" ? throw new IOException("held handle") : 1);

        Assert.Equal(1, reaped);
    }
}
