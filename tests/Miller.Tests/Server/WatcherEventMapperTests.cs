using Miller.Core.Freshness;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the pure translation from a .NET <see cref="System.IO.WatcherChangeTypes"/> notification to a Core
/// <see cref="WatchEvent"/> (the seam between the infra FileSystemWatcher and the pure coalescing queue). A
/// Created/Changed/Deleted maps to the matching kind on the affected path; a Renamed maps to
/// <see cref="WatchEvent.Renamed"/> carrying BOTH the old and new paths (so the router can Delete(old)+Update(new)).
/// Pure — no FileSystemWatcher is constructed.
/// </summary>
public sealed class WatcherEventMapperTests
{
    [Fact]
    public void Map_Created_ProducesCreatedEvent()
    {
        var ev = WatcherEventMapper.Map(System.IO.WatcherChangeTypes.Created, "/repo/a.cs");
        Assert.Equal(WatchEventKind.Created, ev.Kind);
        Assert.Equal("/repo/a.cs", ev.Path);
        Assert.Null(ev.OldPath);
    }

    [Fact]
    public void Map_Changed_ProducesModifiedEvent()
    {
        var ev = WatcherEventMapper.Map(System.IO.WatcherChangeTypes.Changed, "/repo/a.cs");
        Assert.Equal(WatchEventKind.Modified, ev.Kind);
        Assert.Equal("/repo/a.cs", ev.Path);
    }

    [Fact]
    public void Map_Deleted_ProducesDeletedEvent()
    {
        var ev = WatcherEventMapper.Map(System.IO.WatcherChangeTypes.Deleted, "/repo/a.cs");
        Assert.Equal(WatchEventKind.Deleted, ev.Kind);
        Assert.Equal("/repo/a.cs", ev.Path);
    }

    [Fact]
    public void MapRenamed_CarriesBothOldAndNewPaths()
    {
        var ev = WatcherEventMapper.MapRenamed(oldPath: "/repo/old.cs", newPath: "/repo/new.cs");
        Assert.Equal(WatchEventKind.Renamed, ev.Kind);
        Assert.Equal("/repo/new.cs", ev.Path);   // affected path = destination
        Assert.Equal("/repo/old.cs", ev.OldPath); // source preserved for the Delete(old)
    }

    [Fact]
    public void Map_AllChangeTypeBit_DefaultsToModified()
    {
        // FileSystemWatcher can deliver attribute/size/lastwrite changes folded into Changed; anything that is
        // not Created/Deleted/Renamed is treated as a content modification (julie blake3-checks and no-ops if
        // the bytes did not actually change).
        var ev = WatcherEventMapper.Map(
            System.IO.WatcherChangeTypes.Changed | System.IO.WatcherChangeTypes.Created, "/repo/a.cs");
        // Created bit present -> Created wins (a new file).
        Assert.Equal(WatchEventKind.Created, ev.Kind);
    }
}
