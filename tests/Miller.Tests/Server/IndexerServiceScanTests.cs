using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the leader-gated scan trigger behind <c>workspace refresh/full</c> (M7 decision-3): only the indexer
/// LEADER (the instance holding the writer lock, with its <see cref="IExtractOps"/> published) may run an
/// <c>extract scan</c>; a non-leader must NOT scan (the M3 single-writer corruption guard) and reports
/// <see cref="ScanOutcome.Kind.NotLeader"/> honestly. The leader threads <paramref name="force"/> through to the
/// ops (delta vs from-scratch rebuild) and an extract failure surfaces as <see cref="ScanOutcome.Kind.Failed"/>,
/// never thrown into the tool. No FileSystemWatcher, no subprocess, no SQLite — the ops are faked and published
/// through the internal test seam that mirrors the production publish under <c>_opsGate</c>. The live subprocess
/// path is the Scale suite (<see cref="LiveWorkspaceTests"/>).
/// </summary>
public sealed class IndexerServiceScanTests
{
    /// <summary>A fake <see cref="IExtractOps"/> recording the force value of each scan; can be told to throw.</summary>
    private sealed class RecordingScanOps : IExtractOps
    {
        public List<bool> ScanForce { get; } = new();
        public Exception? ThrowOnScan { get; set; }

        public ExtractReport Update(string path) => throw new NotSupportedException("not exercised here");
        public ExtractReport Delete(string path) => throw new NotSupportedException("not exercised here");

        public ExtractReport Scan(bool force = false)
        {
            ScanForce.Add(force);
            if (ThrowOnScan is not null)
                throw ThrowOnScan;
            return Stub();
        }

        private static ExtractReport Stub() => new(
            Status: "changed", Operation: "scan", DbPath: "x", Root: null, SchemaVersion: 26,
            SchemaState: "current", ExtractContractVersion: 1, AnalysisState: null,
            FilesScanned: 0, SymbolsExtracted: 0, FilesTotal: 0, SymbolsTotal: 0,
            RelationshipsTotal: 0, IdentifiersTotal: 0, TypesTotal: 0, Errors: System.Array.Empty<ExtractError>(),
            Revision: 7, FilesUpdated: 0, FilesDeleted: 0);
    }

    // A never-started IndexerService: TryScanAsLeader reads only the published _ops under _opsGate (it never
    // touches the bootstrap), so an un-started instance is the correct, I/O-free unit-test surface.
    private static IndexerService NewService() =>
        new(new IndexBootstrapService(NullLogger<IndexBootstrapService>.Instance),
            NullLogger<IndexerService>.Instance, NullLoggerFactory.Instance);

    [Fact]
    public void TryScanAsLeader_WhenNotLeader_DoesNotScan_AndReportsNotLeader()
    {
        var service = NewService(); // no ops published => not the leader

        ScanOutcome outcome = service.TryScanAsLeader(force: false);

        Assert.Equal(ScanOutcome.Kind.NotLeader, outcome.Result);
        Assert.Null(outcome.Report); // a non-leader produced no extract report (it cannot write)
    }

    [Fact]
    public void TryScanAsLeader_WhenLeader_DeltaScan_RunsForceFalse_AndReportsScanned()
    {
        var service = NewService();
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops); // become the leader (the production publish happens once leadership wins)

        ScanOutcome outcome = service.TryScanAsLeader(force: false);

        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
        Assert.Equal(new[] { false }, ops.ScanForce); // refresh = delta reconcile (no --force)
        Assert.NotNull(outcome.Report);
        Assert.Equal(7, outcome.Report!.Revision);
    }

    [Fact]
    public void TryScanAsLeader_WhenLeader_ForceTrue_ThreadsForceThrough()
    {
        var service = NewService();
        var ops = new RecordingScanOps();
        service.PublishOpsForTest(ops);

        ScanOutcome outcome = service.TryScanAsLeader(force: true);

        Assert.Equal(ScanOutcome.Kind.Scanned, outcome.Result);
        Assert.Equal(new[] { true }, ops.ScanForce); // full = from-scratch rebuild (--force)
    }

    [Fact]
    public void TryScanAsLeader_WhenLeaderScanThrows_ReportsFailed_NeverThrows()
    {
        var service = NewService();
        var ops = new RecordingScanOps
        {
            ThrowOnScan = new JulieExtractException("boom", standardError: "disk full"),
        };
        service.PublishOpsForTest(ops);

        // Best-effort: an extract failure is logged + returned as Failed, never thrown into the caller (the tool).
        ScanOutcome outcome = service.TryScanAsLeader(force: true);

        Assert.Equal(ScanOutcome.Kind.Failed, outcome.Result);
        Assert.Null(outcome.Report);
        Assert.Equal(new[] { true }, ops.ScanForce); // the scan WAS attempted (then threw)
    }
}
