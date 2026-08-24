using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Store;

public sealed class ContinuousTestCursorStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-cursor-").FullName;

    [Fact]
    public void Missing_cursor_is_empty_and_round_trip_preserves_identity_and_revision()
    {
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));

        Assert.Null(store.ReadLastReconciledCursor("workspace-a"));

        var expected = new CtFreshnessKey("ctgen1:artifact:a:blake3", 17);
        store.SaveLastReconciledCursor("workspace-a", expected);

        Assert.Equal(expected, store.ReadLastReconciledCursor("workspace-a"));
    }

    [Fact]
    public void Cursors_are_scoped_by_workspace_and_update_only_the_selected_row()
    {
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        var first = new CtFreshnessKey("gen-1", 4);
        var second = new CtFreshnessKey("gen-2", 9);

        store.SaveLastReconciledCursor("workspace-a", first);
        store.SaveLastReconciledCursor("workspace-b", second);
        store.SaveLastReconciledCursor("workspace-a", second);

        Assert.Equal(second, store.ReadLastReconciledCursor("workspace-a"));
        Assert.Equal(second, store.ReadLastReconciledCursor("workspace-b"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
