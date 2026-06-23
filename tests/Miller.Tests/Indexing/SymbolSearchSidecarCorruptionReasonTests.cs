using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the corruption-visibility seam on the sidecar rebuild path: a rebuild forced by a corrupt/malformed
/// <c>search.db</c> surfaces a non-null reason (the lock-holding writer logs it as a warning), while
/// normal-lifecycle rebuilds — missing artifact, plain revision staleness — stay quiet so routine convergence
/// produces no warning noise.
/// </summary>
public sealed class SymbolSearchSidecarCorruptionReasonTests
{
    private static JulieDbFixture JulieDb() => JulieDbFixture.Create(
        JulieDbFixture.PinnedSchema,
        JulieDbFixture.PinnedContract,
        new[]
        {
            new JulieDbFixture.SymbolRow("s1", "IAuthenticationProvider", "interface", "csharp",
                "src/Auth.cs", "public interface IAuthenticationProvider", 1, ParentId: null),
            new JulieDbFixture.SymbolRow("s2", "Cache", "class", "csharp",
                "src/Cache.cs", "public class Cache", 1, ParentId: null),
        });

    [Fact]
    public void EnsureBuilt_CorruptArtifact_RebuildsAndReportsReason()
    {
        using var julie = JulieDb();
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        File.WriteAllText(searchDb, "this is not a sqlite database");
        var sidecar = new SymbolSearchSidecar(enabled: true);

        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot, out string? reason));

        Assert.NotNull(reason);
        Assert.NotNull(sidecar.TryOpen(julie.DbPath, expectedRevision: 5));
    }

    [Fact]
    public void EnsureCurrent_CorruptArtifact_RebuildsAndReportsReason()
    {
        using var julie = JulieDb();
        string searchDb = SymbolSearchSidecar.SearchDbPathFor(julie.DbPath);
        File.WriteAllText(searchDb, "garbage bytes, not sqlite");
        var sidecar = new SymbolSearchSidecar(enabled: true);

        Assert.True(sidecar.EnsureCurrent(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot, out string? reason));

        Assert.NotNull(reason);
        Assert.NotNull(sidecar.TryOpen(julie.DbPath, expectedRevision: 5));
    }

    [Fact]
    public void EnsureBuilt_MissingArtifact_BuildsQuietly()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);

        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot, out string? reason));

        Assert.Null(reason);
    }

    [Fact]
    public void EnsureBuilt_StaleButHealthyArtifact_RebuildsQuietly()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 1, workspaceRoot: julie.WorkspaceRoot));

        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 2, workspaceRoot: julie.WorkspaceRoot, out string? reason));

        Assert.Null(reason);
    }

    [Fact]
    public void EnsureBuilt_FreshArtifact_NoRebuildAndNoReason()
    {
        using var julie = JulieDb();
        var sidecar = new SymbolSearchSidecar(enabled: true);
        Assert.True(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot));

        Assert.False(sidecar.EnsureBuilt(julie.DbPath, revision: 5, workspaceRoot: julie.WorkspaceRoot, out string? reason));

        Assert.Null(reason);
    }
}
