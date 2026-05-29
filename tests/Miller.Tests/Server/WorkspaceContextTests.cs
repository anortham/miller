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
    public void With_SetsWorkspaceId_WithoutTouchingPaths()
    {
        var ctx = WorkspaceContext.Create("/repo/work", "/app/base");
        var updated = ctx with { WorkspaceId = "ws-42" };

        Assert.Equal("ws-42", updated.WorkspaceId);
        Assert.Equal(ctx.ExtractDbPath, updated.ExtractDbPath);
        Assert.Equal(ctx.ToolsRoot, updated.ToolsRoot);
    }
}
