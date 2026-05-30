using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the pure <c>remove</c> safety predicate (M7 decision-1/8): <see cref="WorkspaceSafety.IsLiveWorkspace"/>
/// answers "does this candidate path resolve to the workspace this process is currently serving?" so the tool can
/// REFUSE to delete the live <c>.miller</c> dir (it is in use) while still allowing the removal of any OTHER
/// workspace's index. The comparison is canonical (symlink-resolved, separator-normalized, case-folded on the
/// platforms that warrant it) so a trailing slash, a <c>./</c> segment, or a symlink alias to the live root is
/// still recognised as the live workspace — a half-delete of the in-use index is the failure this guards against.
/// Real temp dirs (the canonicalizer walks the filesystem), but no SQLite / subprocess — fast suite.
/// </summary>
public sealed class WorkspaceSafetyTests : IDisposable
{
    private readonly string _live;
    private readonly string _other;

    public WorkspaceSafetyTests()
    {
        _live = Path.Combine(Path.GetTempPath(), "miller-ws-safe-live-" + Guid.NewGuid().ToString("N"));
        _other = Path.Combine(Path.GetTempPath(), "miller-ws-safe-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_live);
        Directory.CreateDirectory(_other);
    }

    public void Dispose()
    {
        try { Directory.Delete(_live, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_other, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void IsLiveWorkspace_SamePath_IsTrue()
    {
        Assert.True(WorkspaceSafety.IsLiveWorkspace(_live, _live));
    }

    [Fact]
    public void IsLiveWorkspace_TrailingSlashAndDotSegment_StillRecognisedAsLive()
    {
        // A cosmetic difference (trailing separator, a `./` segment) must not let a half-delete slip through.
        string noisy = Path.Combine(_live, ".") + Path.DirectorySeparatorChar;
        Assert.True(WorkspaceSafety.IsLiveWorkspace(noisy, _live));
    }

    [Fact]
    public void IsLiveWorkspace_DifferentPath_IsFalse()
    {
        Assert.False(WorkspaceSafety.IsLiveWorkspace(_other, _live));
    }

    [Fact]
    public void IsLiveWorkspace_SymlinkAliasToLiveRoot_IsTrue()
    {
        // A symlink pointing at the live root canonicalizes to the same real path — removing through the alias
        // would still corrupt the in-use index, so it must be refused.
        string aliasParent = Path.Combine(Path.GetTempPath(), "miller-ws-safe-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(aliasParent);
        string alias = Path.Combine(aliasParent, "live-link");
        try
        {
            Directory.CreateSymbolicLink(alias, _live);
            Assert.True(WorkspaceSafety.IsLiveWorkspace(alias, _live));
        }
        finally
        {
            try { Directory.Delete(aliasParent, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void IsLiveWorkspace_NonExistentCandidate_IsFalse_NeverThrows()
    {
        // A candidate dir that does not exist cannot be the live workspace (the live root always exists).
        // The predicate degrades to a lexical compare rather than throwing on the canonicalizer's filesystem walk.
        string missing = Path.Combine(_other, "does-not-exist", "nested");
        Assert.False(WorkspaceSafety.IsLiveWorkspace(missing, _live));
    }
}
