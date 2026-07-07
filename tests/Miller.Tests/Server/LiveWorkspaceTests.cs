using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The M7 end-to-end Scale proof: restore julie-extract → scan a temp repo with the REAL pinned binary → drive
/// the <c>workspace</c> tool over genuine extracts + the real freshness path. <c>[Trait("Category","Scale")]</c>
/// so it is EXCLUDED from the default fast suite; it <see cref="Assert.SkipWhen"/>s if <c>.tools/julie-extract</c>
/// is absent rather than failing. Covers the four operations whose correctness only the real subprocess + the
/// real rebuild/swap can prove:
/// <list type="bullet">
/// <item><c>status</c> renders the index facts + a telemetry tool-breakdown after some recorded calls;</item>
/// <item><c>full</c> as the indexer leader force-scans and the in-memory index swaps (the revision advances);</item>
/// <item><c>open(path)</c> primes a SECOND temp repo's <c>.miller/symbols.db</c>;</item>
/// <item><c>remove(path)</c> deletes a non-live <c>.miller</c> dir and REFUSES the live one.</item>
/// </list>
/// </summary>
[Trait("Category", "Scale")]
public sealed class LiveWorkspaceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly List<IDisposable> _disposables = [];

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
        string dir = Path.Combine(Path.GetTempPath(), $"miller-live-ws-{label}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // Scan a small real source tree with the pinned binary into a temp .miller, then build the tool's live
    // collaborators over the genuine extract. The served root is a sandbox temp dir (so a `full` force-scan and a
    // `remove` of the live root never touch the real repo). julie binds workspace_id + the canonical root on the
    // first scan. The indexer starts non-leader (no ops published); a test that needs the leader path publishes
    // real extract ops bound to the sandbox.
    private (WorkspaceTool tool, IndexerService indexer, IndexHolder holder, TelemetryLedger ledger,
             string root, string dbPath, JulieExtractRunner runner) BuildLiveTool(string binary)
    {
        string root = NewTempDir("root");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "Sample.cs"),
            "namespace Demo;\npublic class Sample\n{\n    public int Add(int a, int b) => a + b;\n" +
            "    public int Sub(int a, int b) => a - b;\n}\n");

        var runner = new JulieExtractRunner(binary);
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(root);
        string dbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db");

        ExtractReport scan = runner.Scan(canonicalRoot, dbPath, force: false);
        Assert.NotEqual("failed", scan.Status);
        // v1 stores no workspace_id; the stable id is derived from the canonical root (reconciliation #17).
        string workspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);

        string home = NewTempDir("home");
        var workspace = WorkspaceContext.Create(root, AppContext.BaseDirectory, home) with
        {
            ExtractDbPath = dbPath,
            WorkspaceId = workspaceId,
            CanonicalRoot = canonicalRoot,
            CanonicalExtractDbPath = dbPath,
        };

        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(dbPath));
        // Seed the artifact identity exactly as the production bootstrap does: a `full` force rebuild PROMOTES
        // a fresh file whose revision counter restarts, and the freshness swap is detected by the id changing.
        string? builtArtifactId;
        using (var freshnessSeed = new FreshnessReader(dbPath))
            builtArtifactId = freshnessSeed.ArtifactId();
        var holder = new IndexHolder(index, scan.Revision ?? 0, builtArtifactId);

        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.SeedForTest(workspace, holder);

        var indexer = new IndexerService(
            new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance),
            NullLogger<IndexerService>.Instance, NullLoggerFactory.Instance, SymbolSearchSidecar.Disabled);
        var freshness = new FreshnessService(bootstrap, NullLogger<FreshnessService>.Instance);
        var probe = new IndexFreshProbe(
            holder,
            latestRevision: () => freshness.LatestObservedRevision,
            queueEmpty: () => indexer.QueueEmpty);

        var ledger = TelemetryLedger.Open(Path.Combine(NewTempDir("ledger"), "telemetry.db"), workspaceId);
        _disposables.Add(ledger);
        var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
        _disposables.Add(registry);
        if (workspaceId is not null)
        {
            registry.UpsertSeen(
                workspaceId,
                WorkspaceId.Display(canonicalRoot, workspaceId),
                canonicalRoot,
                dbPath,
                WorkspaceRegistryState.Current);
            registry.MarkScanned(workspaceId, scan.Revision ?? 0);
        }
        var crossRefresh = new CrossWorkspaceRefreshService(registry, runner, SymbolSearchSidecar.Disabled);

        var tool = new WorkspaceTool(
            holder, workspace, indexer, freshness, probe, bootstrap, ledger, runner, registry, crossRefresh,
            SymbolSearchSidecar.Disabled,
            NullLogger<WorkspaceTool>.Instance);
        return (tool, indexer, holder, ledger, root, dbPath, runner);
    }

    [Fact]
    public void Status_OverRealExtract_RendersIndexFactsAndTelemetryBreakdown()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        var (tool, _, holder, ledger, root, _, _) = BuildLiveTool(binary!);
        Assert.True(holder.Current.DocumentCount > 0); // the real scan extracted symbols

        // Record a few real telemetry rows so the breakdown is non-empty.
        ledger.InsertRawForTest(Guid.NewGuid().ToString(), DateTime.UtcNow, "search");
        ledger.InsertRawForTest(Guid.NewGuid().ToString(), DateTime.UtcNow, "search");
        ledger.InsertRawForTest(Guid.NewGuid().ToString(), DateTime.UtcNow, "inspect");

        string output = tool.Workspace(); // status, compact

        Assert.Contains(root, output);
        Assert.Contains("# workspace", output);
        Assert.Contains("symbols:", output);
        Assert.Contains("telemetry:", output);  // compact status surfaces the telemetry summary
        Assert.Contains("top=search", output);
    }

    [Fact]
    public void Full_AsLeader_ForceScans_AndIndexSwapsToTheAdvancedRevision()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        var (tool, indexer, holder, _, root, dbPath, runner) = BuildLiveTool(binary!);

        // Become the indexer leader with REAL extract ops bound to this sandbox (mirrors production's publish once
        // leadership is won), so `full` runs an actual `extract scan --force` through the subprocess.
        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(root);
        IExtractOps ops = JulieExtractOps.Create(canonicalRoot, dbPath, runner);
        indexer.PublishOpsForTest(ops);

        string? beforeArtifactId = holder.BuiltArtifactId;
        Assert.False(string.IsNullOrWhiteSpace(beforeArtifactId)); // the real extract stamps an identity

        string output = tool.Workspace(operation: "full");

        // A force rebuild PROMOTES a fresh artifact (FullRebuildPromotion) whose revision counter RESTARTS, so
        // the held revision does not advance — the swap is confirmed by the artifact identity changing. PollNow
        // detects exactly that and rebuilds + swaps the in-memory index onto the promoted file.
        Assert.Contains("scanned: yes", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("swapped: yes", output, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(beforeArtifactId, holder.BuiltArtifactId);
        Assert.True(holder.BuiltRevision > 0, "the swapped index must carry the promoted artifact's revision");
    }

    [Fact]
    public void Open_OnASecondRepo_PrimesItsMillerSymbolsDb()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        var (tool, _, _, _, _, _, _) = BuildLiveTool(binary!);

        // A SECOND, independent repo with no .miller yet.
        string other = NewTempDir("prime-target");
        Directory.CreateDirectory(Path.Combine(other, "lib"));
        File.WriteAllText(Path.Combine(other, "lib", "Widget.cs"),
            "namespace Lib;\npublic class Widget { public string Name() => \"w\"; }\n");

        string output = tool.Workspace(operation: "open", path: other);

        string primedDb = Path.Combine(PathCanonicalizer.CanonicalizeRoot(other), ".miller", "symbols.db");
        Assert.True(File.Exists(primedDb), $"expected a primed extract DB at {primedDb}");
        Assert.Contains("symbols_extracted:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a live switch", output, StringComparison.OrdinalIgnoreCase); // honest: a prime, not a switch
    }

    [Fact]
    public void Remove_DeletesNonLiveMillerDir_ButRefusesTheLiveOne()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        var (tool, _, _, _, root, _, _) = BuildLiveTool(binary!);

        // --- the live workspace is refused (its .miller is in use) ---
        string liveMiller = Path.Combine(PathCanonicalizer.CanonicalizeRoot(root), ".miller");
        Assert.True(Directory.Exists(liveMiller)); // the live extract dir exists

        string refused = tool.Workspace(operation: "remove", path: root);
        Assert.Contains("refus", refused, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(liveMiller)); // untouched — a real refusal, not a half-delete

        // --- a different, non-live workspace IS removed ---
        string other = NewTempDir("removable");
        string otherMiller = Path.Combine(other, ".miller");
        Directory.CreateDirectory(otherMiller);
        File.WriteAllText(Path.Combine(otherMiller, "symbols.db"), "stub");

        string removed = tool.Workspace(operation: "remove", path: other);
        Assert.Contains("removed", removed, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(otherMiller)); // actually deleted
    }
}
