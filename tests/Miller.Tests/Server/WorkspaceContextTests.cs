using Miller.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the M2 §1 path conventions: the extract + telemetry DBs live under <c>&lt;root&gt;/.miller</c>, the
/// tools root is under the APP BASE DIRECTORY (NOT the repo cwd — that is where the pinned julie-server
/// ships), and workspace_id starts null (the bootstrap fills it after the scan).
/// </summary>
public sealed class WorkspaceContextTests
{
    [Fact]
    public void Create_PutsBothDbsUnderTheRootMillerDir()
    {
        var ctx = WorkspaceContext.Create("/repo/work", "/app/base");

        Assert.Equal(Path.Combine(Path.GetFullPath("/repo/work"), ".miller", "symbols.db"), ctx.ExtractDbPath);
        Assert.Equal(Path.Combine(Path.GetFullPath("/repo/work"), ".miller", "telemetry.db"), ctx.TelemetryDbPath);
    }

    [Fact]
    public void Create_PutsToolsRootUnderTheAppBaseDir_NotTheRepo()
    {
        var ctx = WorkspaceContext.Create("/repo/work", "/app/base");

        Assert.Equal(Path.Combine(Path.GetFullPath("/app/base"), ".tools"), ctx.ToolsRoot);
        Assert.DoesNotContain(Path.GetFullPath("/repo/work"), ctx.ToolsRoot);
    }

    [Fact]
    public void Create_WorkspaceIdStartsNull()
    {
        Assert.Null(WorkspaceContext.Create("/repo/work", "/app/base").WorkspaceId);
    }

    [Fact]
    public void Create_CanonicalRootStartsNull_UntilResolvedAgainstARealTree()
    {
        // M3: the symlink-resolved canonical root requires a real filesystem walk (verified-fact 4), so Create
        // leaves it null; the bootstrap fills it via PathCanonicalizer.CanonicalizeRoot at startup.
        Assert.Null(WorkspaceContext.Create("/repo/work", "/app/base").CanonicalRoot);
    }

    [Fact]
    public void With_SetsCanonicalRoot_WithoutTouchingOtherPaths()
    {
        var ctx = WorkspaceContext.Create("/repo/work", "/app/base");
        var updated = ctx with { CanonicalRoot = "/private/repo/work" };

        Assert.Equal("/private/repo/work", updated.CanonicalRoot);
        Assert.Equal(ctx.WorkspaceRoot, updated.WorkspaceRoot);
        Assert.Equal(ctx.ExtractDbPath, updated.ExtractDbPath);
    }

    [Fact]
    public void Create_CanonicalExtractDbPathStartsNull_UntilComposedUnderTheCanonicalRoot()
    {
        // M3 verified-fact 4: the DB path julie receives must be CANONICAL too (a non-canonical --db under a
        // symlinked root trips the same outside-root family of validation as --file). Canonicalization needs a
        // real filesystem walk, so Create leaves it null; the bootstrap composes it under the canonical root.
        Assert.Null(WorkspaceContext.Create("/repo/work", "/app/base").CanonicalExtractDbPath);
    }

    [Fact]
    public void With_SetsCanonicalExtractDbPath_WithoutTouchingTheNonCanonicalExtractDbPath()
    {
        var ctx = WorkspaceContext.Create("/repo/work", "/app/base");
        var updated = ctx with
        {
            CanonicalRoot = "/private/repo/work",
            CanonicalExtractDbPath = "/private/repo/work/.miller/symbols.db",
        };

        Assert.Equal("/private/repo/work/.miller/symbols.db", updated.CanonicalExtractDbPath);
        // The original non-canonical path is preserved (it is what File.Exists/the gate keys on at bootstrap).
        Assert.Equal(ctx.ExtractDbPath, updated.ExtractDbPath);
    }

    [Fact]
    public void With_SetsWorkspaceId_WithoutTouchingPaths()
    {
        var ctx = WorkspaceContext.Create("/repo/work", "/app/base");
        var updated = ctx with { WorkspaceId = "ws-42" };

        Assert.Equal("ws-42", updated.WorkspaceId);
        Assert.Equal(ctx.ExtractDbPath, updated.ExtractDbPath);
        Assert.Equal(ctx.ToolsRoot, updated.ToolsRoot);
    }
}
