using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the <c>workspace</c> tool dispatch (M7 decision-1/2/3/7/8) over fakes + a synth index — no live
/// julie-server, no FileSystemWatcher, no timer-driven hosted loop. The tool's collaborators are real classes
/// driven through their unit seams: a never-started <see cref="IndexerService"/> reports NotLeader (so refresh/
/// full take the non-leader poll-only path), the publish seam makes it the leader; a <see cref="FreshnessService"/>
/// over a synthesized extract DB does a real on-demand poll+swap; a temp <see cref="TelemetryLedger"/> with seeded
/// rows produces the status tool-breakdown. Covers: status assembles + renders both formats; list shows the
/// current workspace; open/remove arg guards (missing path → usage); the remove-live refusal and remove not-found;
/// and the non-leader refresh/full path (poll only, with the honest cannot-force note). The live extract path
/// (open's prime scan, full's force-scan + swap on a real repo) is the Scale suite (<see cref="LiveWorkspaceTests"/>).
/// </summary>
public sealed class WorkspaceToolTests : IDisposable
{
    private const string Ws = "ws-tool-001";

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
        string dir = Path.Combine(Path.GetTempPath(), $"miller-wstool-{label}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // Build a real WorkspaceTool over a synthesized extract DB whose .miller dir IS the served workspace root's
    // .miller (so the workspace context's paths are self-consistent for the live-remove safety check). The
    // indexer is never-started (NotLeader) unless the caller publishes ops; the freshness service polls the synth
    // DB on demand; the ledger is a temp DB the test can seed.
    private (WorkspaceTool tool, IndexerService indexer, TelemetryLedger ledger, string root) BuildTool(
        JulieDbFixture fx, long builtRevision, string? workspaceId)
    {
        // The served workspace root is the fixture dir's parent of .miller; point ExtractDbPath at the fixture DB.
        string root = Path.GetDirectoryName(fx.DbPath)!;
        var workspace = WorkspaceContext.Create(root, AppContext.BaseDirectory) with
        {
            ExtractDbPath = fx.DbPath,
            WorkspaceId = workspaceId,
        };

        var holder = new IndexHolder(MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath)), builtRevision);

        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.SeedForTest(workspace, holder);

        var indexer = new IndexerService(
            new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance),
            NullLogger<IndexerService>.Instance, NullLoggerFactory.Instance);
        var freshness = new FreshnessService(bootstrap, NullLogger<FreshnessService>.Instance);

        var probe = new IndexFreshProbe(
            holder,
            latestRevision: () => freshness.LatestObservedRevision,
            queueEmpty: () => indexer.QueueEmpty);

        var ledger = TelemetryLedger.Open(Path.Combine(NewTempDir("ledger"), "telemetry.db"), workspaceId);
        _disposables.Add(ledger);

        // The runner is needed only by open()'s prime scan (a Scale path). The default-suite tests never invoke
        // the spawning path, so construct it against a STUB file (JulieExtractRunner only File.Exists-validates at
        // construction) — keeping this suite pure + binary-independent (no pinned julie-server required to run it).
        string stubBinary = Path.Combine(NewTempDir("toolstub"),
            OperatingSystem.IsWindows() ? "julie-server.exe" : "julie-server");
        File.WriteAllText(stubBinary, "#!/bin/sh\n");
        var runner = new JulieExtractRunner(stubBinary);

        var tool = new WorkspaceTool(
            holder, workspace, indexer, freshness, probe, ledger, runner,
            NullLogger<WorkspaceTool>.Instance);
        return (tool, indexer, ledger, root);
    }

    private static JulieDbFixture CreateSynth(long revision, string? workspaceId) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, JulieDbFixture.DefaultRows, workspaceId: workspaceId,
            revisions: workspaceId is null
                ? null
                : new[] { new JulieDbFixture.RevisionRow(revision, workspaceId) });

    // A fake leader: publish recording ops so the indexer reports Scanned for refresh/full.
    private sealed class RecordingScanOps : IExtractOps
    {
        public List<bool> ScanForce { get; } = [];
        public ExtractReport Update(string path) => throw new NotSupportedException();
        public ExtractReport Delete(string path) => throw new NotSupportedException();
        public ExtractReport Scan(bool force = false)
        {
            ScanForce.Add(force);
            return new ExtractReport(
                Status: "changed", Operation: "scan", DbPath: "x", Root: null, SchemaVersion: (int)MillerExtractContract.ExpectedSchemaVersion,
                SchemaState: "current", ExtractContractVersion: (int)MillerExtractContract.ExpectedExtractContractVersion, AnalysisState: null,
                FilesScanned: 0, SymbolsExtracted: 0, FilesTotal: 0, SymbolsTotal: 0,
                RelationshipsTotal: 0, IdentifiersTotal: 0, TypesTotal: 0, Errors: [],
                Revision: 99, FilesUpdated: 0, FilesDeleted: 0);
        }
    }

    // ---- status ----

    [Fact]
    public void Status_Default_AssemblesFactsAndTelemetry_Compact()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, ledger, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        ledger.InsertRawForTest(Guid.NewGuid().ToString(), DateTime.UtcNow, "search");

        string output = tool.Workspace(); // operation defaults to status, compact

        Assert.Contains(root, output);
        Assert.Contains(Ws, output);
        Assert.Contains("# index", output);
        Assert.Contains("symbols:", output);
        // A non-leader (never-started indexer) reads as "reader", not leader — honest about this process's role.
        Assert.Contains("reader", output, StringComparison.OrdinalIgnoreCase);
        // The seeded telemetry row shows in the embedded breakdown.
        Assert.Contains("search", output);
    }

    [Fact]
    public void Status_Json_HasWorkspaceIndexAndTelemetrySections()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        using var doc = JsonDocument.Parse(tool.Workspace(operation: "status", format: "json"));
        var rootEl = doc.RootElement;
        Assert.Equal(root, rootEl.GetProperty("workspace").GetProperty("root").GetString());
        Assert.False(rootEl.GetProperty("workspace").GetProperty("leader").GetBoolean());
        Assert.True(rootEl.GetProperty("index").GetProperty("document_count").GetInt64() > 0);
        Assert.Equal(4, rootEl.GetProperty("index").GetProperty("built_revision").GetInt64());
        Assert.True(rootEl.TryGetProperty("telemetry", out _));
    }

    // ---- list ----

    [Fact]
    public void List_ShowsCurrentWorkspace()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "list");
        Assert.Contains(root, output);
        Assert.Contains("current", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- open / remove arg guards ----

    [Fact]
    public void Open_MissingPath_ReturnsUsage_DoesNotScan()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "open", path: null);
        Assert.Contains("requires a path", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_MissingPath_ReturnsUsage()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "remove", path: null);
        Assert.Contains("requires a path", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- open safety (decision-2/3/8) ----

    [Fact]
    public void Open_LiveWorkspace_IsRefused_DoesNotScan()
    {
        // D2: open's scan MUST respect the single-writer discipline. open on the live workspace MUST refuse (an
        // honest note) and point to refresh/full, rather than spawn a second `extract scan` against the in-use DB
        // outside the leader's _opsGate serialization. Mirrors the remove-live guard. The runner here is bound to a
        // non-executable stub, so if open DID scan it would surface a failure — the guard must short-circuit first.
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "open", path: root);

        Assert.Contains("live workspace", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refresh", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("primed", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_NonExistentPath_IsACleanNotFound_NotAToolFailure()
    {
        // Symmetric with remove's not-found: open against a non-null path that does not exist on disk must give
        // clear "no directory at <path>" guidance (not a generic "workspace failed" from the canonicalizer's
        // DirectoryNotFoundException leaking to the outer catch).
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        string missing = Path.Combine(Path.GetTempPath(), "miller-open-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));

        string output = tool.Workspace(operation: "open", path: missing);

        Assert.DoesNotContain("workspace failed", output, StringComparison.Ordinal);
        Assert.Contains("no directory", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missing, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_PathAlreadyServedByAnotherLeader_IsNotPrimed_HonestNote()
    {
        // A prime scan must not run a second `extract scan` against a DB another Miller leader already owns (the
        // M3 single-writer guard). Stand in for that leader by holding the target .miller's cross-process
        // SingleWriterLock, then call open on that path: it must NOT scan (no faked "primed") and report an honest
        // "already serving" note instead. (No --force/julie spawn occurs — the lock guard short-circuits.)
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        string target = NewTempDir("open-served");
        string targetMillerDir = Path.Combine(target, ".miller");
        using SingleWriterLock? heldLease = SingleWriterLock.TryAcquire(targetMillerDir); // the other leader
        Assert.NotNull(heldLease);

        string output = tool.Workspace(operation: "open", path: target);

        Assert.DoesNotContain("primed", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already serving", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- telemetry op sub-axis (decision-7) ----

    [Fact]
    public void Workspace_StampsTheOperationOntoTheAmbientTelemetryScope()
    {
        // D7: workspace is in the tool-breakdown WITH its operation sub-axis. The tool must stamp the ambient
        // scope's Op so the central filter's row records op=<operation> instead of NULL. The central filter opens
        // the scope with op=null; here a real Measure scope stands in for it as TelemetryContext.Current.
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, ledger, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        using (var scope = ledger.Measure("workspace", op: null))
        {
            tool.Workspace(operation: "full"); // non-leader path — deterministic, no live julie spawn
            Assert.Equal("full", scope.Op);
        }
    }

    // ---- remove safety ----

    [Fact]
    public void Remove_LiveWorkspace_IsRefused_AndDoesNotDelete()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, root) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        string liveMiller = Path.Combine(root, ".miller");
        Directory.CreateDirectory(liveMiller);
        File.WriteAllText(Path.Combine(liveMiller, "sentinel.txt"), "live");

        string output = tool.Workspace(operation: "remove", path: root);

        Assert.Contains("refus", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in use", output, StringComparison.OrdinalIgnoreCase);
        // The live .miller dir is untouched — the refusal is real, not a half-delete.
        Assert.True(File.Exists(Path.Combine(liveMiller, "sentinel.txt")));
    }

    [Fact]
    public void Remove_NotFound_IsNotAnError_NoMillerDir()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        string other = NewTempDir("other-empty"); // exists, but has no .miller

        string output = tool.Workspace(operation: "remove", path: other);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(output.StartsWith("workspace failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Remove_NonLiveWorkspaceWithMillerDir_DeletesIt()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        string other = NewTempDir("other-with-index");
        string otherMiller = Path.Combine(other, ".miller");
        Directory.CreateDirectory(otherMiller);
        File.WriteAllText(Path.Combine(otherMiller, "symbols.db"), "stub");

        string output = tool.Workspace(operation: "remove", path: other);

        Assert.Contains("removed", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(otherMiller)); // actually deleted
    }

    // ---- refresh / full: non-leader path (poll only, honest note) ----

    [Fact]
    public void Refresh_NonLeader_PollsOnly_AndConverges()
    {
        // The writer (another instance) has moved to revision 9; this reader's held index is at 2. A non-leader
        // refresh does NOT scan (it cannot — no writer lock) but its PollNow picks up the writer's persisted
        // revision and swaps. So: scanned=no, swapped=yes, revision=9.
        using var fx = CreateSynth(revision: 9, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 2, workspaceId: Ws);

        string output = tool.Workspace(operation: "refresh");

        Assert.Contains("scanned: no", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("swapped: yes", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9", output);
    }

    [Fact]
    public void Full_NonLeader_CannotForceRescan_ReportsHonestNote()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "full");

        Assert.Contains("scanned: no", output, StringComparison.OrdinalIgnoreCase);
        // Honest: a non-leader cannot force a global rescan — never a faked success.
        Assert.Contains("not the indexer leader", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot force", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- refresh / full: leader path (scan runs through the published ops) ----

    [Fact]
    public void Full_AsLeader_ForceScans_ThenPollsAndSwaps()
    {
        // Publish recording ops so the indexer is the leader; full → TryScanAsLeader(force:true). The writer has
        // persisted revision 7; the held index is at 1, so the subsequent PollNow swaps to 7.
        using var fx = CreateSynth(revision: 7, workspaceId: Ws);
        var (tool, indexer, _, _) = BuildTool(fx, builtRevision: 1, workspaceId: Ws);
        var ops = new RecordingScanOps();
        indexer.PublishOpsForTest(ops);

        string output = tool.Workspace(operation: "full");

        Assert.Equal(new[] { true }, ops.ScanForce);                 // full = --force scan ran
        Assert.Contains("scanned: yes", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("swapped: yes", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7", output);                                 // converged on the persisted revision
    }

    [Fact]
    public void Refresh_AsLeader_DeltaScans_ForceFalse()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, indexer, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);
        var ops = new RecordingScanOps();
        indexer.PublishOpsForTest(ops);

        string output = tool.Workspace(operation: "refresh");

        Assert.Equal(new[] { false }, ops.ScanForce);  // refresh = delta reconcile (no --force)
        Assert.Contains("scanned: yes", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- unknown operation ----

    [Fact]
    public void UnknownOperation_ReturnsUsageNote_NotAnError()
    {
        using var fx = CreateSynth(revision: 4, workspaceId: Ws);
        var (tool, _, _, _) = BuildTool(fx, builtRevision: 4, workspaceId: Ws);

        string output = tool.Workspace(operation: "frobnicate");
        Assert.Contains("unknown workspace operation", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(output.StartsWith("workspace failed", StringComparison.Ordinal));
    }
}
