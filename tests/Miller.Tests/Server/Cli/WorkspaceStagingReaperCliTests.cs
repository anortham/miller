using Miller.Server;
using Miller.Server.Cli;
using Miller.Tests.Indexing;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server.Cli;

/// <summary>
/// Pins the CLI half of the sidecar staging reaper. The CLI workspace verbs render through
/// <c>WorkspaceRender</c> and never construct <c>WorkspaceTool</c>, so the MCP hook covered by
/// <c>WorkspaceToolTests</c> does not reach them; without their own reap a person running
/// <c>miller workspace status</c> on a workspace whose scans never reach the sidecar writers would watch it
/// keep every orphaned <c>.search-build-*.db</c> forever. Shares the semantic-activation collection with
/// <see cref="CliDispatchTests"/> because those tests mutate <c>MILLER_SEMANTIC</c> process-wide.
/// </summary>
[Collection(SemanticActivationEnvironmentCollection.Name)]
public sealed class WorkspaceStagingReaperCliTests : IDisposable
{
    // Short name on purpose, mirroring CliDispatchTests: the semantic broker derives a unix domain socket path
    // under this directory, and macOS caps those at 104 characters.
    private readonly string _home = Path.Combine(Path.GetTempPath(), "msr-" + Guid.NewGuid().ToString("N")[..8]);

    public WorkspaceStagingReaperCliTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private WorkspaceContext Context(string extractDbPath) =>
        new(
            WorkspaceRoot: Path.GetDirectoryName(extractDbPath)!,
            ExtractDbPath: extractDbPath,
            TelemetryDbPath: Path.Combine(_home, "telemetry.db"),
            RegistryDbPath: Path.Combine(_home, "workspaces.db"),
            ToolsRoot: Path.Combine(_home, ".tools"),
            WorkspaceId: null);

    private static (int Code, string Out, string Err) Run(IReadOnlyList<string> args, WorkspaceContext ctx)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliDispatch.Run(args, ctx, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string Stage(string sidecarDir, string name, TimeSpan age)
    {
        string path = Path.Combine(sidecarDir, name);
        File.WriteAllText(path, "staging content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void WorkspaceStatus_ReapsStaleStagingOrphansWithoutASidecarBuild()
    {
        using var fx = JulieDbFixture.CreateDefault();
        string sidecarDir = Path.GetDirectoryName(fx.DbPath)!;
        string orphan = Stage(sidecarDir, ".search-build-orphan.db", TimeSpan.FromHours(2));
        string liveBuild = Stage(sidecarDir, ".content-build-live.db", TimeSpan.Zero);

        var (code, _, _) = Run(["workspace", "status"], Context(fx.DbPath));

        Assert.Equal(0, code);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(liveBuild));
        Assert.False(File.Exists(Path.Combine(sidecarDir, "search.db")));
    }

    [Fact]
    public void WorkspaceHealth_ReapsStaleStagingOrphansWithoutASidecarBuild()
    {
        using var fx = JulieDbFixture.CreateDefault();
        string sidecarDir = Path.GetDirectoryName(fx.DbPath)!;
        string orphan = Stage(sidecarDir, ".content-build-orphan.db", TimeSpan.FromHours(2));
        string recent = Stage(sidecarDir, ".search-build-recent.db", TimeSpan.FromMinutes(14));

        var (code, _, _) = Run(["workspace", "health"], Context(fx.DbPath));

        Assert.Equal(0, code);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(recent));
    }
}
