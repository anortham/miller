using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the watcher wiring of the supported-extension gate: with julie's claimed set installed (via the test
/// seam — the live <c>languages --json</c> probe is Scale), an event for an unclaimed extension is dropped
/// BEFORE dispatch (no <c>extract update</c> subprocess would ever spawn), a claimed extension still flows,
/// a null set gates nothing (fail soft), and an ignore-policy file still forces a rescan even though the
/// gate would drop it as a dispatchable file. No FileSystemWatcher, no subprocess — fake ops + the same
/// internal seams the other IndexerService unit tests use.
/// </summary>
public sealed class IndexerWatcherExtensionGateTests
{
    private static readonly IReadOnlySet<string> CsOnly =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs" };

    private sealed class RecordingOps : IExtractOps
    {
        public List<string> UpdatedPaths { get; } = new();
        public List<string> DeletedPaths { get; } = new();
        public int ScanCount { get; private set; }

        public ExtractReport Update(string path)
        {
            UpdatedPaths.Add(path);
            return Stub();
        }

        public ExtractReport Delete(string path)
        {
            DeletedPaths.Add(path);
            return Stub();
        }

        public ExtractReport Scan(bool force = false)
        {
            ScanCount++;
            return Stub();
        }

        private static ExtractReport Stub() => new(
            ReportSchemaVersion: 1, Status: "ok", Operation: "scan", Mode: "incremental", Input: null,
            Artifact: new ExtractArtifact(
                DbPath: "x", RootPath: "/abs/r", ArtifactId: "a",
                SchemaVersion: MillerExtractContract.ExpectedSchemaVersion,
                ExtractContractVersion: MillerExtractContract.ExpectedExtractContractVersion,
                SqliteSchemaVersion: MillerExtractContract.ExpectedSqliteSchemaVersion,
                JsonlSchemaVersion: 1, HashAlgorithm: MillerExtractContract.ExpectedHashAlgorithm,
                ParserInventoryFingerprint: "p", CapabilitySnapshotFingerprint: "c"),
            Tool: new ExtractTool("julie-extract", "2.0.0"),
            RevisionBlock: new ExtractRevision(1, 1),
            Counts: null,
            Errors: Array.Empty<ReportDiagnostic>(), Warnings: Array.Empty<ReportDiagnostic>());
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "miller-ext-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    // Never started: PublishOpsForTest installs the core exactly as the leader path does; the bootstrap is
    // seeded so HandleChanged can read CanonicalRoot. The public ctor's catalog fetch only runs inside
    // ExecuteAsync, which these tests never start — nothing here can spawn a process.
    private static (IndexerService Service, RecordingOps Ops) NewLeader(string root)
    {
        string tempHome = Path.Combine(root, "home");
        Directory.CreateDirectory(tempHome);
        var bootstrap = new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance);
        bootstrap.TestHomeDirectoryOverride = tempHome;
        bootstrap.SeedForTest(
            new WorkspaceContext(
                root,
                System.IO.Path.Combine(root, ".miller", "symbols.db"),
                System.IO.Path.Combine(root, "telemetry.db"),
                System.IO.Path.Combine(root, "workspaces.db"),
                System.IO.Path.Combine(root, ".tools"),
                WorkspaceId: "w", CanonicalRoot: root, CanonicalExtractDbPath: null),
            new IndexHolder(MillerRepositoryIndex.Build(Array.Empty<IndexedSymbol>()), builtRevision: 0));
        var service = new IndexerService(
            bootstrap, NullLogger<IndexerService>.Instance, NullLoggerFactory.Instance,
            SymbolSearchSidecar.Disabled);
        var ops = new RecordingOps();
        service.PublishOpsForTest(ops);
        return (service, ops);
    }

    [Fact]
    public void GateActive_UnclaimedExtensions_NeverReachExtractOps()
    {
        using var temp = new TempDir();
        var (service, ops) = NewLeader(temp.Path);
        service.SetSupportedExtensionsForTest(CsOnly);

        service.HandleChangedForTest(WatcherChangeTypes.Changed, System.IO.Path.Combine(temp.Path, "src", "A.cs"));
        service.HandleChangedForTest(WatcherChangeTypes.Changed, System.IO.Path.Combine(temp.Path, "daemon.log"));
        service.HandleChangedForTest(WatcherChangeTypes.Created, System.IO.Path.Combine(temp.Path, "logo.png"));
        service.HandleChangedForTest(WatcherChangeTypes.Changed, System.IO.Path.Combine(temp.Path, "yarn.lock"));
        service.DrainForTest(headChanged: false);

        string expected = System.IO.Path.Combine(temp.Path, "src", "A.cs");
        Assert.Equal(new[] { expected }, ops.UpdatedPaths);
        Assert.Equal(0, ops.ScanCount);
    }

    [Fact]
    public void GateActive_ExtensionlessFiles_RemainFailSoftAndReachExtractOps()
    {
        using var temp = new TempDir();
        var (service, ops) = NewLeader(temp.Path);
        service.SetSupportedExtensionsForTest(CsOnly);

        service.HandleChangedForTest(WatcherChangeTypes.Changed, System.IO.Path.Combine(temp.Path, "Dockerfile"));
        service.HandleChangedForTest(WatcherChangeTypes.Changed, System.IO.Path.Combine(temp.Path, ".env"));
        service.DrainForTest(headChanged: false);

        Assert.Equal(
            new[]
            {
                System.IO.Path.Combine(temp.Path, "Dockerfile"),
                System.IO.Path.Combine(temp.Path, ".env"),
            },
            ops.UpdatedPaths);
    }

    [Fact]
    public void NoGate_NullSet_KeepsTheHistoricalAcceptEverythingBehavior()
    {
        using var temp = new TempDir();
        var (service, ops) = NewLeader(temp.Path);
        service.SetSupportedExtensionsForTest(null);

        service.HandleChangedForTest(WatcherChangeTypes.Changed, System.IO.Path.Combine(temp.Path, "daemon.log"));
        service.DrainForTest(headChanged: false);

        Assert.Single(ops.UpdatedPaths); // over-fed, as before — julie no-ops harmlessly (verified-fact 2)
    }

    [Fact]
    public void GateActive_IgnorePolicyFileChange_StillForcesRescan()
    {
        using var temp = new TempDir();
        var (service, ops) = NewLeader(temp.Path);
        service.SetSupportedExtensionsForTest(CsOnly);

        // .gitignore has no supported extension, but it must hit the force-rescan branch, which the watcher
        // consults BEFORE the gate — otherwise ignore-policy edits would stop pruning newly ignored files.
        service.HandleChangedForTest(WatcherChangeTypes.Changed, System.IO.Path.Combine(temp.Path, ".gitignore"));
        service.DrainForTest(headChanged: false);

        Assert.Equal(1, ops.ScanCount);
        Assert.Empty(ops.UpdatedPaths);
    }
}
