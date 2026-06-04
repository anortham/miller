using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server.Cli;

/// <summary>
/// Pins the CLI dispatch <see cref="CliDispatch"/> end-to-end in-process (no subprocess, no MCP host): verbs map
/// to the right tool core, exit codes follow the contract (0 ok / 2 usage / 3 no-index), and output flows to the
/// injected writers. The index comes from a real <see cref="JulieDbFixture"/> <c>symbols.db</c> and the registry
/// from a seeded temp DB, so these stay in the fast suite. <see cref="WorkspaceContext"/> is constructed directly
/// (rather than from a CWD) so the tests never chdir — that would race xUnit's parallel collections.
/// </summary>
public sealed class CliDispatchTests : IDisposable
{
    private readonly string _dir;
    private readonly string _registryDb;

    public CliDispatchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registryDb = Path.Combine(_dir, "workspaces.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private WorkspaceContext Context(string extractDbPath, string? workspaceRoot = null) =>
        new(
            WorkspaceRoot: workspaceRoot ?? _dir,
            ExtractDbPath: extractDbPath,
            TelemetryDbPath: Path.Combine(_dir, "telemetry.db"),
            RegistryDbPath: _registryDb,
            ToolsRoot: Path.Combine(_dir, ".tools"),
            WorkspaceId: null);

    private static (int Code, string Out, string Err) Run(IReadOnlyList<string> args, WorkspaceContext ctx)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliDispatch.Run(args, ctx, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void IsCliInvocation_ServeAndEmptyAreServer_EverythingElseIsCli()
    {
        Assert.False(CliDispatch.IsCliInvocation(Array.Empty<string>()));
        Assert.False(CliDispatch.IsCliInvocation(new[] { "serve" }));
        Assert.False(CliDispatch.IsCliInvocation(new[] { "SERVE" }));   // case-insensitive
        Assert.True(CliDispatch.IsCliInvocation(new[] { "search", "x" }));
        Assert.True(CliDispatch.IsCliInvocation(new[] { "--version" }));
    }

    [Fact]
    public void Version_PrintsBuildVersion()
    {
        var (code, outText, _) = Run(new[] { "version" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, code);
        Assert.StartsWith("0.1.0", outText.Trim());
    }

    [Fact]
    public void Help_ListsCommands()
    {
        var (code, outText, _) = Run(new[] { "help" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, code);
        Assert.Contains("Commands:", outText);
        Assert.Contains("search", outText);
        Assert.Contains("serve", outText);
    }

    [Fact]
    public void UnknownVerb_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "frobnicate" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("unknown command", errText);
    }

    [Fact]
    public void Search_NoQuery_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "search" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.Contains("usage:", errText);
    }

    [Fact]
    public void Search_NoIndex_ExitsThreeWithGuidance()
    {
        var (code, _, errText) = Run(new[] { "search", "UserService" }, Context(Path.Combine(_dir, "nope.db")));
        Assert.Equal(3, code);
        Assert.Contains("no Miller index", errText);
    }

    [Fact]
    public void Search_FindsAKnownSymbol()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var (code, outText, _) = Run(new[] { "search", "UserService" }, Context(fx.DbPath));
        Assert.Equal(0, code);
        Assert.Contains("UserService", outText);
    }

    [Fact]
    public void Search_Json_EmitsAJsonArray()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var (code, outText, _) = Run(new[] { "search", "UserService", "--json" }, Context(fx.DbPath));
        Assert.Equal(0, code);
        Assert.StartsWith("[", outText.Trim());
    }

    [Fact]
    public void Inspect_File_ListsItsSymbols()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var (code, outText, _) = Run(new[] { "inspect", "auth/UserService.cs" }, Context(fx.DbPath));
        Assert.Equal(0, code);
        Assert.Contains("GetUser", outText);
    }

    [Fact]
    public void WorkspaceList_RendersSeededRegistryRows()
    {
        using (WorkspaceRegistry registry = WorkspaceRegistry.Open(_registryDb))
        {
            registry.UpsertSeen("ws-aaaaaaaaaaaa", "alpha-ws", Path.Combine(_dir, "alpha"),
                Path.Combine(_dir, "alpha", ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
            registry.UpsertSeen("ws-bbbbbbbbbbbb", "beta-ws", Path.Combine(_dir, "beta"),
                Path.Combine(_dir, "beta", ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
        }
        SqliteConnection.ClearAllPools();

        var (code, outText, _) = Run(new[] { "workspace", "list" }, Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(0, code);
        Assert.Contains("alpha-ws", outText);
        Assert.Contains("beta-ws", outText);
    }

    [Fact]
    public void WorkspaceStatus_DefaultsToStatus_AndShowsBuildVersion()
    {
        using var fx = JulieDbFixture.CreateDefault();
        // No registry row matches the current root → the CLI reads the local index directly and stamps THIS
        // binary's version into the status header (the dogfooding "which build is live" signal).
        var (code, outText, _) = Run(new[] { "workspace", "status" }, Context(fx.DbPath));
        Assert.Equal(0, code);
        Assert.Contains("miller 0.1.0", outText);
        Assert.Contains("symbols:", outText);
    }

    [Fact]
    public void WorkspaceStatus_UnknownId_IsUsageErrorExitTwo()
    {
        var (code, _, errText) = Run(new[] { "workspace", "status", "--id", "does-not-exist" },
            Context(Path.Combine(_dir, "symbols.db")));
        Assert.Equal(2, code);
        Assert.False(string.IsNullOrWhiteSpace(errText));
    }

    // refresh/full must surface EVERY non-success refresh status as a non-zero exit (a CI `... && deploy` guard):
    // only Refreshed/Unchanged are success. This pins the status→code map directly (the live refresh path itself
    // is exercised by the Scale subprocess test, which spawns julie).
    [Theory]
    [InlineData(WorkspaceRefreshStatus.Refreshed, 0)]
    [InlineData(WorkspaceRefreshStatus.Unchanged, 0)]
    [InlineData(WorkspaceRefreshStatus.MissingRoot, 3)]
    [InlineData(WorkspaceRefreshStatus.MissingIndex, 3)]
    [InlineData(WorkspaceRefreshStatus.LockBusy, 3)]
    [InlineData(WorkspaceRefreshStatus.Failed, 3)]
    public void RefreshExitCode_NonSuccessIsAlwaysNonZero(WorkspaceRefreshStatus status, int expected) =>
        Assert.Equal(expected, CliDispatch.RefreshExitCode(status));
}
