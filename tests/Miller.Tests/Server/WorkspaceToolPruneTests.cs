using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins <c>workspace(operation="prune")</c>: registry GC for rows whose <c>canonical_root</c> no longer exists.
/// No julie, no symbols.db — only <see cref="WorkspaceRegistry.List"/> + <see cref="Directory.Exists"/> +
/// <see cref="WorkspaceRegistry.Remove"/>.
/// </summary>
public sealed class WorkspaceToolPruneTests : IDisposable
{
    private const string CurrentWs = "ws-prune-current-001";
    private const string ExistingWs = "ws-prune-existing-001";
    private const string MissingWs = "ws-prune-missing-001";

    private readonly List<IDisposable> _disposables = [];
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch (ObjectDisposedException) { }
        }
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private string NewTempDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"miller-prune-{label}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private (WorkspaceTool tool, WorkspaceRegistry registry, string currentRoot) BuildTool(
        Action<WorkspaceRegistry, string, string, string>? seed = null)
    {
        string currentRoot = NewTempDir("current");
        string existingRoot = NewTempDir("existing");
        string missingRoot = Path.Combine(NewTempDir("gone-parent"), "missing-repo");
        // missingRoot is never created on disk

        string home = NewTempDir("home");
        var workspace = WorkspaceContext.Create(currentRoot, AppContext.BaseDirectory, home) with
        {
            WorkspaceId = CurrentWs,
            CanonicalRoot = Path.GetFullPath(currentRoot),
        };

        var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
        _disposables.Add(registry);

        string IndexDb(string root) => Path.Combine(root, ".miller", "symbols.db");

        registry.UpsertSeen(CurrentWs, "current-repo", workspace.CanonicalRoot!, IndexDb(currentRoot),
            WorkspaceRegistryState.Current);
        registry.UpsertSeen(ExistingWs, "existing-repo", Path.GetFullPath(existingRoot), IndexDb(existingRoot),
            WorkspaceRegistryState.Ready);
        registry.UpsertSeen(MissingWs, "missing-repo", Path.GetFullPath(missingRoot), IndexDb(missingRoot),
            WorkspaceRegistryState.Stale);

        seed?.Invoke(registry, currentRoot, existingRoot, missingRoot);

        JulieDbFixture fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, JulieDbFixture.DefaultRows,
            workspaceId: CurrentWs);
        string synthRoot = Path.GetDirectoryName(fx.DbPath)!;
        var holder = new IndexHolder(
            MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath)), builtRevision: 1);
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = home;
        bootstrap.SeedForTest(workspace with { ExtractDbPath = fx.DbPath }, holder);

        var indexerBootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        indexerBootstrap.TestHomeDirectoryOverride = home;
        var indexer = new IndexerService(
            indexerBootstrap,
            NullLogger<IndexerService>.Instance, NullLoggerFactory.Instance, SymbolSearchSidecar.Disabled);
        var freshness = new FreshnessService(bootstrap, NullLogger<FreshnessService>.Instance);
        var probe = new IndexFreshProbe(holder, () => 0, () => true);
        var ledger = TelemetryLedger.Open(Path.Combine(NewTempDir("ledger"), "telemetry.db"), CurrentWs);
        _disposables.Add(ledger);

        string stubBinary = Path.Combine(NewTempDir("stub"),
            OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract");
        File.WriteAllText(stubBinary, "#!/bin/sh\n");
        var runner = new JulieExtractRunner(stubBinary);
        var crossRefresh = new CrossWorkspaceRefreshService(
            registry,
            (_, _, _) => throw new InvalidOperationException("scan not expected"),
            _ => SingleWriterLock.TryAcquire(_),
            _ => 0,
            lockBusyWait: TimeSpan.Zero,
            lockBusyPollInterval: TimeSpan.FromMilliseconds(1),
            sleep: _ => { },
            utcNow: () => DateTimeOffset.UtcNow,
            sidecar: SymbolSearchSidecar.Disabled);

        var tool = new WorkspaceTool(
            holder, workspace, indexer, freshness, probe, bootstrap, ledger, runner, registry, crossRefresh,
            SymbolSearchSidecar.Disabled,
            (_, _, _) => throw new InvalidOperationException("open scan not expected"),
            _ => SingleWriterLock.TryAcquire(_),
            new RecordingDashboardLauncher(new DashboardLaunchResult(
                DashboardLaunchOutcome.AlreadyRunning,
                new Uri("http://127.0.0.1:4977/"),
                ProcessId: null,
                Message: "already running")),
            NullLogger<WorkspaceTool>.Instance);

        return (tool, registry, currentRoot);
    }

    [Fact]
    public void Prune_RemovesRowsWithMissingRoots()
    {
        (WorkspaceTool tool, WorkspaceRegistry registry, _) = BuildTool();

        string output = tool.Workspace(operation: "prune");

        Assert.Contains("pruned: 1", output);
        Assert.Contains("missing-repo", output);
        Assert.Contains("kept: 2", output);
        Assert.Null(registry.Get(MissingWs));
        Assert.NotNull(registry.Get(CurrentWs));
        Assert.NotNull(registry.Get(ExistingWs));
    }

    [Fact]
    public void Prune_DryRun_ListsWithoutRemoving()
    {
        (WorkspaceTool tool, WorkspaceRegistry registry, _) = BuildTool();

        string output = tool.Workspace(operation: "prune", dry_run: true);

        Assert.Contains("would prune: 1", output);
        Assert.Contains("missing-repo", output);
        Assert.Contains("kept: 2", output);
        Assert.NotNull(registry.Get(MissingWs));
    }

    [Fact]
    public void Prune_NeverPrunesCurrentWorkspace_EvenWhenRootMissing()
    {
        (WorkspaceTool tool, WorkspaceRegistry registry, string currentRoot) = BuildTool();
        Directory.Delete(currentRoot, recursive: true);

        string output = tool.Workspace(operation: "prune");

        Assert.Contains("pruned: 1", output);
        Assert.Contains("kept: 2", output);
        Assert.NotNull(registry.Get(CurrentWs));
        Assert.Null(registry.Get(MissingWs));
    }

    [Fact]
    public void Prune_CompactOutput_CapsExamplesAt10()
    {
        (WorkspaceTool tool, WorkspaceRegistry registry, _) = BuildTool((reg, _, _, _) =>
        {
            for (int i = 0; i < 11; i++)
            {
                string wsId = $"ws-prune-extra-{i:D3}";
                string root = Path.Combine(NewTempDir($"extra-{i}"), "gone");
                reg.UpsertSeen(wsId, $"extra-{i}", Path.GetFullPath(root), Path.Combine(root, ".miller", "symbols.db"),
                    WorkspaceRegistryState.Stale);
            }
        });

        string output = tool.Workspace(operation: "prune");

        Assert.Contains("pruned: 12", output);
        int exampleLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith("  ", StringComparison.Ordinal) && line.Contains(' '));
        Assert.Equal(10, exampleLines);
    }

    [Fact]
    public void Prune_JsonOutput_MatchesShape()
    {
        (WorkspaceTool tool, _, _) = BuildTool();

        using JsonDocument doc = JsonDocument.Parse(tool.Workspace(operation: "prune", format: "json"));

        Assert.False(doc.RootElement.GetProperty("dry_run").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("kept").GetInt32());
        JsonElement pruned = doc.RootElement.GetProperty("pruned");
        Assert.Equal(JsonValueKind.Array, pruned.ValueKind);
        Assert.Equal(1, pruned.GetArrayLength());
        JsonElement entry = pruned[0];
        Assert.Equal(MissingWs, entry.GetProperty("workspace_id").GetString());
        Assert.Equal("missing-repo", entry.GetProperty("display_id").GetString());
        Assert.Contains("missing-repo", entry.GetProperty("root").GetString());
    }

    [Fact]
    public void Prune_JsonDryRun_ListsWithoutRemoving()
    {
        (WorkspaceTool tool, WorkspaceRegistry registry, _) = BuildTool();

        using JsonDocument doc = JsonDocument.Parse(tool.Workspace(operation: "prune", format: "json", dry_run: true));

        Assert.True(doc.RootElement.GetProperty("dry_run").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("pruned").GetArrayLength());
        Assert.NotNull(registry.Get(MissingWs));
    }

    [Fact]
    public void RegistryPrune_RemovesOnlyMissingRoots()
    {
        string home = NewTempDir("home");
        string registryPath = Path.Combine(home, "workspaces.db");
        using var registry = WorkspaceRegistry.Open(registryPath);

        string existingRoot = NewTempDir("exists");
        string missingRoot = Path.Combine(NewTempDir("parent"), "gone");
        registry.UpsertSeen("ws-a", "repo-a", Path.GetFullPath(existingRoot),
            Path.Combine(existingRoot, ".miller", "symbols.db"), WorkspaceRegistryState.Ready);
        registry.UpsertSeen("ws-b", "repo-b", Path.GetFullPath(missingRoot),
            Path.Combine(missingRoot, ".miller", "symbols.db"), WorkspaceRegistryState.Stale);

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(registry, protectedWorkspaceId: null, dryRun: false);

        Assert.Single(result.Pruned);
        Assert.Equal("ws-b", result.Pruned[0].WorkspaceId);
        Assert.Equal(1, result.Kept);
        Assert.Null(registry.Get("ws-b"));
        Assert.NotNull(registry.Get("ws-a"));
    }

    private sealed class RecordingDashboardLauncher(DashboardLaunchResult result) : IDashboardLauncher
    {
        public DashboardLaunchResult EnsureRunning(DashboardLaunchRequest request) => result;
    }
}
