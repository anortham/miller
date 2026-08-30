using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the root-presence tie-break in <see cref="WorkspaceRegistrySelector"/>: a dead registry row left behind by a
/// deleted worktree must not block the short selector for a live workspace, and the tie-break must not reach any
/// selector form that was never ambiguous. Temp registry with real directories only.
/// </summary>
public sealed class WorkspaceRegistrySelectorTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkspaceRegistry _registry;

    public WorkspaceRegistrySelectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-selector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registry = WorkspaceRegistry.Open(Path.Combine(_dir, "workspaces.db"));
    }

    public void Dispose()
    {
        _registry.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private WorkspaceRegistryRow Register(string workspaceId, string displayId, bool rootExists)
    {
        string root = Path.Combine(_dir, workspaceId);
        if (rootExists)
            Directory.CreateDirectory(root);
        return _registry.UpsertSeen(
            workspaceId,
            displayId,
            root,
            Path.Combine(root, ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready);
    }

    [Fact]
    public void Resolve_ForAMutation_NeverBreaksTheTieAndStillRefusesTheAmbiguousSelector()
    {
        Register("ws-selector-mut-live-01", "miller-91250a0fd4f3", rootExists: true);
        Register("ws-selector-mut-dead-01", "miller-release-smoke.Jmoi8N-dd49aef90432", rootExists: false);

        Assert.Throws<KeyNotFoundException>(
            () => WorkspaceRegistrySelector.Resolve(
                _registry,
                "miller",
                WorkspaceSelectorIntent.Mutate));
    }

    [Fact]
    public void Resolve_AmbiguousPrefixWithOneLiveAndOneDeadRoot_ResolvesToTheLiveRoot()
    {
        Register("ws-selector-live-0001", "miller-91250a0fd4f3", rootExists: true);
        Register("ws-selector-dead-0001", "miller-release-smoke.Jmoi8N-dd49aef90432", rootExists: false);

        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(_registry, "miller");

        Assert.Equal("ws-selector-live-0001", row.WorkspaceId);
    }

    [Fact]
    public void Resolve_AmbiguousDisplayIdWithOneLiveAndOneDeadRoot_ResolvesToTheLiveRoot()
    {
        Register("ws-selector-dupe-dead", "worktrees-aaaaaaaaaaaa", rootExists: false);
        Register("ws-selector-dupe-live", "worktrees-aaaaaaaaaaaa", rootExists: true);

        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(_registry, "worktrees-aaaaaaaaaaaa");

        Assert.Equal("ws-selector-dupe-live", row.WorkspaceId);
    }

    [Fact]
    public void Resolve_TwoLiveRoots_StillReportsTheSameAmbiguity()
    {
        Register("ws-selector-both-0001", "twin-live-a-111111111111", rootExists: true);
        Register("ws-selector-both-0002", "twin-live-b-222222222222", rootExists: true);

        var exception = Assert.Throws<KeyNotFoundException>(
            () => WorkspaceRegistrySelector.Resolve(_registry, "twin"));

        Assert.Equal(
            "ambiguous workspace selector 'twin'. Matches: twin-live-a-111111111111, twin-live-b-222222222222. " +
            "Use a longer display ID or full workspace_id.",
            exception.Message);
    }

    [Fact]
    public void Resolve_EveryMatchingRootIsDead_StillReportsTheSameAmbiguity()
    {
        Register("ws-selector-ghost-001", "ghost-a-111111111111", rootExists: false);
        Register("ws-selector-ghost-002", "ghost-b-222222222222", rootExists: false);

        var exception = Assert.Throws<KeyNotFoundException>(
            () => WorkspaceRegistrySelector.Resolve(_registry, "ghost"));

        Assert.Equal(
            "ambiguous workspace selector 'ghost'. Matches: ghost-a-111111111111, ghost-b-222222222222. " +
            "Use a longer display ID or full workspace_id.",
            exception.Message);
    }

    [Fact]
    public void Resolve_ASingleDeadMatch_StillResolvesSoTheRowCanBeRemoved()
    {
        Register("ws-selector-only-dead", "orphan-111111111111", rootExists: false);

        Assert.Equal(
            "ws-selector-only-dead",
            WorkspaceRegistrySelector.Resolve(_registry, "orphan").WorkspaceId);
        Assert.Equal(
            "ws-selector-only-dead",
            WorkspaceRegistrySelector.Resolve(_registry, "orphan-111111111111").WorkspaceId);
    }

    [Fact]
    public void Resolve_ExactWorkspaceId_IgnoresRootPresenceAndTheLivePrefixSibling()
    {
        Register("ws-selector-exact-live", "exact-live-111111111111", rootExists: true);
        Register("ws-selector-exact-dead", "exact-dead-222222222222", rootExists: false);

        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(_registry, "ws-selector-exact-dead");

        Assert.Equal("ws-selector-exact-dead", row.WorkspaceId);
    }

    [Fact]
    public void Resolve_ARegisteredRootPath_ResolvesTheDeadRowItNames()
    {
        WorkspaceRegistryRow dead = Register("ws-selector-path-dead", "path-dead-111111111111", rootExists: false);
        Register("ws-selector-path-live", "path-live-222222222222", rootExists: true);

        WorkspaceRegistryRow row = WorkspaceRegistrySelector.Resolve(_registry, dead.CanonicalRoot);

        Assert.Equal("ws-selector-path-dead", row.WorkspaceId);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("primary")]
    public void Resolve_TheCallerOwnedKeywords_AreStillRefusedHere(string keyword)
    {
        Register("ws-selector-keyword-live", "keyword-live-111111111111", rootExists: true);
        Register("ws-selector-keyword-dead", "keyword-dead-222222222222", rootExists: false);

        var exception = Assert.Throws<KeyNotFoundException>(
            () => WorkspaceRegistrySelector.Resolve(_registry, keyword));

        Assert.StartsWith("unknown workspace selector", exception.Message, StringComparison.Ordinal);
    }
}
