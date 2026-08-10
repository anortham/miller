using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

public sealed class StoreWorkspaceCoordinatorTests
{
    private static readonly StoreFamilyBinding Binding = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        Path.GetFullPath("/family"),
        "view-a",
        Path.GetFullPath("/workspace"),
        StoreBindingState.Ready);

    [Fact]
    public void UpdateUsesTheCurrentFullLevelAndReturnsACompatibleFreshnessReport()
    {
        var client = new RecordingStoreClient(StoreOperation.Update);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(41, "full"),
            new(42, "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Progressive,
            _ => snapshots.Dequeue(),
            () => "request-a");

        ExtractReport report = coordinator.Update(Path.Combine(Binding.WorkspaceRoot, "src", "a.cs"));

        StoreUpdateRequest request = Assert.IsType<StoreUpdateRequest>(client.SingleRequest);
        Assert.Equal("src/a.cs", request.FilePath.Replace('\\', '/'));
        Assert.Equal(StoreLevel.Full, request.Level);
        Assert.Equal(ExtractJobsPolicy.FromEnvironment(), request.Scan.Jobs);
        Assert.Equal(
            Path.Combine(Binding.WorkspaceRoot, ".miller", "spool"),
            request.Scan.SpoolDirectory);
        Assert.Equal(
            Path.Combine(Binding.WorkspaceRoot, ".miller", "scan.progress"),
            request.Scan.ProgressFile);
        Assert.Equal("request-a", request.Request.RequestId);
        Assert.Equal("request-a", request.Request.IdempotencyKey);
        Assert.Equal(42, report.Revision);
        Assert.Equal(42, report.CreatedRevision);
        Assert.Equal("completed", report.Status);
        Assert.Equal((ulong)1, report.FilesUpdated);
        Assert.Equal(Binding.FamilyId.ToString("D"), report.Artifact?.ArtifactId);
        Assert.IsType<StoreResolveRequest>(client.Requests[1]);
    }

    [Fact]
    public void MissingDeleteIsANoChangeAndKeepsTheLatestStoreCursor()
    {
        var client = new RecordingStoreClient(
            StoreOperation.Delete,
            manifestDisposition: StoreManifestDisposition.Reused);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(75, "l1"),
            new(76, "l1"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Progressive,
            _ => snapshots.Dequeue(),
            () => "request-delete");

        ExtractReport report = coordinator.Delete(Path.Combine(Binding.WorkspaceRoot, "gone.cs"));

        StoreDeleteRequest request = Assert.IsType<StoreDeleteRequest>(client.SingleRequest);
        Assert.Equal(["gone.cs"], request.FilePaths);
        Assert.Equal("no_change", report.Status);
        Assert.Null(report.CreatedRevision);
        Assert.Equal(76, report.Revision);
        Assert.Equal((ulong)0, report.FilesDeleted);
    }

    [Theory]
    [InlineData(IndexLevelPolicy.Progressive, ScanIntent.IncrementalReconcile, StoreLevel.L1)]
    [InlineData(IndexLevelPolicy.Progressive, ScanIntent.LevelUpgrade, StoreLevel.Full)]
    [InlineData(IndexLevelPolicy.SymbolsOnly, ScanIntent.UserFullRebuild, StoreLevel.L1)]
    [InlineData(IndexLevelPolicy.Full, ScanIntent.IncrementalReconcile, StoreLevel.Full)]
    public void NewFamilyImportPreservesTheIndexLevelPolicy(
        IndexLevelPolicy policy,
        ScanIntent intent,
        StoreLevel expected)
    {
        var client = new RecordingStoreClient(StoreOperation.Import);
        var snapshots = new Queue<StoreWorkspaceState?>(
        [
            null,
            new StoreWorkspaceState(1, expected == StoreLevel.L1 ? "l1" : "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding with { State = StoreBindingState.Planned },
            client,
            () => policy,
            _ => snapshots.Dequeue(),
            () => "request-import",
            "/workspace/.miller/symbols.db");

        ExtractReport report = coordinator.Scan(intent, jobs: 2);

        StoreImportRequest request = Assert.IsType<StoreImportRequest>(client.SingleRequest);
        Assert.Equal(expected, request.Level);
        Assert.Equal(2, request.Scan.Jobs);
        Assert.Equal("/workspace/.miller/symbols.db", request.FromArtifact);
        Assert.Equal("request-import", report.Input?.Format);
        Assert.Equal(expected == StoreLevel.Full, client.Requests.Any(request => request.Operation == StoreOperation.Resolve));
    }

    [Fact]
    public void NewFamilyImportUsesTheLongStoreRequestTimeout()
    {
        var client = new RecordingStoreClient(StoreOperation.Import);
        var snapshots = new Queue<StoreWorkspaceState?>
        (
        [
            null,
            new StoreWorkspaceState(1, "l1"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding with { State = StoreBindingState.Planned },
            client,
            () => IndexLevelPolicy.Progressive,
            _ => snapshots.Dequeue(),
            () => "request-timeout",
            fromArtifact: null);

        coordinator.Scan();

        StoreImportRequest request = Assert.IsType<StoreImportRequest>(client.SingleRequest);
        Assert.Equal(TimeSpan.FromHours(1), request.Request.Timeout);
    }

    [Fact]
    public void NewFamilyImportPassesMillersInvariantIgnoreFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var client = new RecordingStoreClient(StoreOperation.Import);
            var coordinator = new StoreWorkspaceCoordinator(
                Binding with
                {
                    WorkspaceRoot = Path.GetFullPath(root),
                    StoreRoot = Path.Combine(root, "store"),
                    State = StoreBindingState.Planned,
                },
                client,
                () => IndexLevelPolicy.Progressive,
                _ => new StoreWorkspaceState(1, "l1"),
                () => "request-ignore",
                fromArtifact: null);

            coordinator.Scan(jobs: 1);

            StoreImportRequest request = Assert.IsType<StoreImportRequest>(client.SingleRequest);
            string invariant = ScanIgnorePolicy.InvariantIgnorePathFor(root);
            Assert.Equal([invariant], request.Scan.IgnoreFiles);
            Assert.Contains(".worktrees/", File.ReadAllText(invariant), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExistingInvariantIgnoreFileIsPassedToStoreUpdates()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-update-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".miller"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "a.cs"), "class A {}");
            string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(root);
            string invariant = ScanIgnorePolicy.InvariantIgnorePathFor(canonicalRoot);
            File.WriteAllText(invariant, ScanIgnorePolicy.RenderInvariantContent());
            var client = new RecordingStoreClient(StoreOperation.Update);
            var coordinator = new StoreWorkspaceCoordinator(
                Binding with
                {
                    WorkspaceRoot = canonicalRoot,
                    StoreRoot = Path.Combine(root, "store"),
                },
                client,
                () => IndexLevelPolicy.Progressive,
                _ => new StoreWorkspaceState(1, "l1"),
                () => "request-update-ignore");

            coordinator.Update(Path.Combine(canonicalRoot, "src", "a.cs"));

            StoreUpdateRequest request = Assert.IsType<StoreUpdateRequest>(client.SingleRequest);
            Assert.Equal([invariant], request.Scan.IgnoreFiles);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureBindingPointerRepairsMalformedStoreMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-pointer-repair-" + Guid.NewGuid().ToString("N"));
        string storeRoot = Path.Combine(root, "store");
        Directory.CreateDirectory(Path.Combine(root, ".miller"));
        Directory.CreateDirectory(storeRoot);
        try
        {
            File.WriteAllText(Path.Combine(root, ".miller", "store.json"), "not-json");
            StoreFamilyBinding binding = Binding with
            {
                StoreRoot = storeRoot,
                WorkspaceRoot = PathCanonicalizer.CanonicalizeRoot(root),
            };
            var coordinator = new StoreWorkspaceCoordinator(
                binding,
                new RecordingStoreClient(StoreOperation.Update),
                () => IndexLevelPolicy.Progressive,
                _ => new StoreWorkspaceState(1, "l1"),
                () => "request-repair");

            coordinator.EnsureBindingPointer();

            StoreWorkspacePointerDocument repaired = Assert.IsType<StoreWorkspacePointerDocument>(
                StoreWorkspacePointer.Read(root));
            Assert.Equal(binding.FamilyId, repaired.FamilyId);
            Assert.Equal(binding.ViewId, repaired.ViewId);
            Assert.Equal(binding.StoreRoot, repaired.StoreRoot);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TypedStoreFailureDoesNotMasqueradeAsACompletedExtractReport()
    {
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            exitCode: 1,
            failureClass: "view_root_mismatch");
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ => new StoreWorkspaceState(1, "full"),
            () => "request-failed");

        StoreWorkspaceOperationException error = Assert.Throws<StoreWorkspaceOperationException>(() =>
            coordinator.Update(Path.Combine(Binding.WorkspaceRoot, "src", "a.cs")));

        Assert.Equal("view_root_mismatch", error.FailureClass.Code);
    }

    [Fact]
    public void DurableRequestJournalReusesAnInterruptedRequestAndClearsAfterTerminalObservation()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-request-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int minted = 0;
            var first = new StoreRequestJournal(root);
            string request = first.GetOrCreate("update|family|view|src/a.cs|Full", () => $"request-{++minted}");

            var restarted = new StoreRequestJournal(root);
            string resumed = restarted.GetOrCreate(
                "update|family|view|src/a.cs|Full",
                () => $"request-{++minted}");

            Assert.Equal(request, resumed);
            Assert.Equal(1, minted);
            restarted.Complete(resumed);

            var next = new StoreRequestJournal(root);
            Assert.Equal(
                "request-2",
                next.GetOrCreate("update|family|view|src/a.cs|Full", () => $"request-{++minted}"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingStoreClient(
        StoreOperation expectedOperation,
        StoreManifestDisposition manifestDisposition = StoreManifestDisposition.Created,
        int exitCode = 0,
        string failureClass = "none") : IJulieStoreClient
    {
        private readonly List<StoreRequest> _requests = [];

        public IReadOnlyList<StoreRequest> Requests => _requests;
        public StoreRequest SingleRequest => Assert.Single(
            _requests,
            request => request.Operation != StoreOperation.Resolve);

        public StoreRequestResult Submit(StoreRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(
                request.Operation == expectedOperation || request.Operation == StoreOperation.Resolve,
                $"Expected {expectedOperation} or Resolve, got {request.Operation}.");
            _requests.Add(request);
            StoreLevel level = request switch
            {
                StoreImportRequest import => import.Level,
                StoreUpdateRequest update => update.Level,
                _ => StoreLevel.NotApplicable,
            };
            return new StoreRequestResult(
                JulieStoreContract.ReportSchemaVersion,
                request.Operation,
                new StoreRequestIdentity(
                    request switch
                    {
                        StoreImportRequest import => import.Request.RequestId,
                        StoreUpdateRequest update => update.Request.RequestId,
                        StoreDeleteRequest delete => delete.Request.RequestId,
                        _ => "request",
                    },
                    null),
                Binding.FamilyId.ToString("D"),
                Binding.ViewId,
                Binding.WorkspaceRoot,
                exitCode == 0 ? StoreRequestState.Committed : StoreRequestState.Failed,
                level,
                new StoreLevelCompletion(true, level == StoreLevel.Full, level == StoreLevel.Full),
                new StoreManifestResult(1, "manifest-hash", manifestDisposition),
                new StoreRowCounts(1, 1, level == StoreLevel.Full ? 1 : 0, level == StoreLevel.Full ? 1 : 0),
                request.Operation == StoreOperation.Resolve
                    ? new StoreResolutionResult(StoreResolutionState.Exact, true, "base", 1, 1, 0, 0, 0)
                    : new StoreResolutionResult(StoreResolutionState.Unbound, false, null, null, null, null, null, null),
                null,
                exitCode == 0 ? StoreCoordinatorDisposition.Committed : StoreCoordinatorDisposition.Failed,
                new StoreFailure(new StoreFailureClass(failureClass), exitCode == 0 ? null : "failed"),
                exitCode);
        }
    }
}
