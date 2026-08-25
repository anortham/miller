using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server.Hosting;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
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
        Assert.DoesNotContain(client.Requests, static request => request.Operation == StoreOperation.Resolve);
    }

    [Fact]
    public void ACommittedRequestSucceedsEvenWhenTheProducerExitsNonzero()
    {
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            exitCode: 1,
            failureClass: "resolution_failed",
            stateOverride: StoreRequestState.Committed);
        var snapshots = new Queue<StoreWorkspaceState>([new(41, "full"), new(42, "full")]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Progressive,
            _ => snapshots.Dequeue(),
            () => "request-a");

        ExtractReport report = coordinator.Update(Path.Combine(Binding.WorkspaceRoot, "src", "a.cs"));

        Assert.Equal("completed", report.Status);
        Assert.Equal(42, report.Revision);
    }

    [Fact]
    public void AnAcknowledgedRequestSucceedsEvenWhenTheProducerExitsNonzero()
    {
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            exitCode: 1,
            failureClass: "resolution_failed",
            stateOverride: StoreRequestState.Acknowledged);
        var snapshots = new Queue<StoreWorkspaceState>([new(41, "full"), new(42, "full")]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Progressive,
            _ => snapshots.Dequeue(),
            () => "request-a");

        Assert.Equal("completed", coordinator.Update(Path.Combine(Binding.WorkspaceRoot, "src", "a.cs")).Status);
    }

    [Fact]
    public void AFailedRequestIsStillAHardFailure()
    {
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            exitCode: 1,
            failureClass: "resolution_failed",
            stateOverride: StoreRequestState.Failed);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Progressive,
            _ => new StoreWorkspaceState(41, "full"),
            () => "request-a");

        StoreWorkspaceOperationException failure = Assert.Throws<StoreWorkspaceOperationException>(
            () => coordinator.Update(Path.Combine(Binding.WorkspaceRoot, "src", "a.cs")));

        Assert.Equal("resolution_failed", failure.FailureClass.Code);
        Assert.False(failure.IsRetryable);
    }

    [Fact]
    public void AResolutionTargetNotVisibleFailedRequestIsRetryable()
    {
        const string missing =
            "resolution_failed: resolution artifact error: resolution target (854, 02212562fcd58c23875c7998783016be) is not visible";
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            exitCode: 1,
            failureClass: "resolution_failed",
            failureMessage: missing,
            stateOverride: StoreRequestState.Failed);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Progressive,
            _ => new StoreWorkspaceState(41, "full"),
            () => "request-a");

        StoreWorkspaceOperationException failure = Assert.Throws<StoreWorkspaceOperationException>(
            () => coordinator.Update(Path.Combine(Binding.WorkspaceRoot, "src", "a.cs")));

        Assert.Equal("resolution_failed", failure.FailureClass.Code);
        Assert.True(failure.IsRetryable);
        Assert.True(StoreWorkspaceOperationException.IsRetryableProducerFailure(failure));
    }

    [Fact]
    public void AResolutionTargetNotVisibleMessageOnADifferentFailureClassIsNotRetryable()
    {
        const string missing =
            "resolution_failed: resolution artifact error: resolution target (854, 02212562fcd58c23875c7998783016be) is not visible";
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            exitCode: 1,
            failureClass: "producer_timeout",
            failureMessage: missing,
            stateOverride: StoreRequestState.Failed);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Progressive,
            _ => new StoreWorkspaceState(41, "full"),
            () => "request-a");

        StoreWorkspaceOperationException failure = Assert.Throws<StoreWorkspaceOperationException>(
            () => coordinator.Update(Path.Combine(Binding.WorkspaceRoot, "src", "a.cs")));

        Assert.Equal("producer_timeout", failure.FailureClass.Code);
        Assert.False(failure.IsRetryable);
        Assert.False(StoreWorkspaceOperationException.IsRetryableProducerFailure(failure));
    }

    [Fact]
    public void AQuantumTimeoutFailedRequestIsRetryableAndKeepsThePriorView()
    {
        const string quantum = "coordinator quantum took 4359 ms; maximum is 4000 ms";
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            exitCode: 1,
            failureClass: "producer_timeout",
            failureMessage: quantum,
            stateOverride: StoreRequestState.Failed);
        int reads = 0;
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ =>
            {
                reads++;
                return new StoreWorkspaceState(41, "full");
            },
            () => "request-quantum",
            fromArtifact: null,
            inspectTree: static () => new StoreTreeDelta(["src/a.cs"], []));

        StoreWorkspaceOperationException failure = Assert.Throws<StoreWorkspaceOperationException>(
            () => coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1));

        Assert.True(failure.IsRetryable);
        Assert.Equal(StoreWorkspaceOperationException.CoordinatorQuantumFailureCode, failure.FailureClass.Code);
        Assert.Equal(quantum, failure.Message);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void ARetryableFailureKeepsTheJournalSoTheRetryReusesTheRequestId()
    {
        using var workspace = new TempWorkspace();
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            exitCode: 1,
            failureClass: StoreWorkspaceOperationException.CoordinatorQuantumFailureCode,
            stateOverride: StoreRequestState.Failed);
        StoreWorkspaceCoordinator coordinator = workspace.Coordinator(client);

        Assert.Throws<StoreWorkspaceOperationException>(() => coordinator.Update(workspace.SourcePath));
        Assert.NotEmpty(Directory.GetFiles(workspace.JournalDirectory, "*.json"));
        Assert.Throws<StoreWorkspaceOperationException>(() => coordinator.Update(workspace.SourcePath));

        Assert.Equal(2, client.Requests.Count);
        Assert.NotEmpty(Directory.GetFiles(workspace.JournalDirectory, "*.json"));
        Assert.Equal(
            ((StoreUpdateRequest)client.Requests[0]).Request.RequestId,
            ((StoreUpdateRequest)client.Requests[1]).Request.RequestId);
    }

    [Fact]
    public void ANonRetryableFailureStillRetiresTheJournalSoTheRetryIsANewRequest()
    {
        using var workspace = new TempWorkspace();
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            exitCode: 1,
            failureClass: "extract_failed",
            stateOverride: StoreRequestState.Failed);
        StoreWorkspaceCoordinator coordinator = workspace.Coordinator(client);

        Assert.Throws<StoreWorkspaceOperationException>(() => coordinator.Update(workspace.SourcePath));
        Assert.Empty(Directory.GetFiles(workspace.JournalDirectory, "*.json"));
        Assert.Throws<StoreWorkspaceOperationException>(() => coordinator.Update(workspace.SourcePath));

        Assert.Equal(2, client.Requests.Count);
        Assert.NotEqual(
            ((StoreUpdateRequest)client.Requests[0]).Request.RequestId,
            ((StoreUpdateRequest)client.Requests[1]).Request.RequestId);
    }

    [Fact]
    public void FailedImportRecordsFailedPhasesAndPreservesFailurePropagation()
    {
        var phases = new RecordingPhaseSink();
        var client = new RecordingStoreClient(
            StoreOperation.Import,
            exitCode: 1,
            failureClass: "import_failed",
            stateOverride: StoreRequestState.Failed);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Progressive,
            _ => new StoreWorkspaceState(41, "l1"),
            () => "request-import",
            null,
            phases);

        StoreWorkspaceOperationException failure = Assert.Throws<StoreWorkspaceOperationException>(
            () => coordinator.Scan(jobs: 1));

        Assert.Equal("import_failed", failure.FailureClass.Code);
        Assert.Equal(["import", "coordinator_total"], phases.Records.Select(static phase => phase.Phase));
        Assert.All(phases.Records, static phase => Assert.Equal("failed", phase.Outcome));
    }

    [Fact]
    public void PhaseSinkFailureDoesNotChangeCoordinatorSuccess()
    {
        var client = new RecordingStoreClient(StoreOperation.Import);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Progressive,
            _ => new StoreWorkspaceState(41, "l1"),
            () => "request-import",
            null,
            new ThrowingPhaseSink());

        ExtractReport report = coordinator.Scan(jobs: 1);

        Assert.Equal("completed", report.Status);
        Assert.Single(client.Requests);
    }

    [Fact]
    public void ANonTerminalRequestIsNotTreatedAsCommitted()
    {
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            stateOverride: StoreRequestState.Claimed);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Progressive,
            _ => new StoreWorkspaceState(41, "full"),
            () => "request-a");

        StoreWorkspaceOperationException failure = Assert.Throws<StoreWorkspaceOperationException>(
            () => coordinator.Update(Path.Combine(Binding.WorkspaceRoot, "src", "a.cs")));

        Assert.Equal("request_not_terminal", failure.FailureClass.Code);
        Assert.True(failure.IsRetryable);
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
        using var artifact = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>());
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
            artifact.DbPath);

        ExtractReport report = coordinator.Scan(intent, jobs: 2);

        StoreImportRequest request = Assert.IsType<StoreImportRequest>(client.SingleRequest);
        Assert.Equal(expected, request.Level);
        Assert.Equal(2, request.Scan.Jobs);
        Assert.Equal(artifact.DbPath, request.FromArtifact);
        Assert.Equal("request-import", report.Input?.Format);
        Assert.DoesNotContain(client.Requests, static request => request.Operation == StoreOperation.Resolve);
    }

    [Fact]
    public void FailedSeedImportRetriesWithFreshRequestAndCompletesTheScan()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-seed-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var artifact = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>());
        try
        {
            var client = new RecordingStoreClient(
                StoreOperation.Import,
                failureClass: "store_incompatible",
                stateOverrides: new Queue<StoreRequestState>(
                [
                    StoreRequestState.Failed,
                    StoreRequestState.Committed,
                ]));
            var snapshots = new Queue<StoreWorkspaceState?>(
            [
                null,
                new StoreWorkspaceState(1, "l1"),
            ]);
            int minted = 0;
            var coordinator = new StoreWorkspaceCoordinator(
                Binding with
                {
                    WorkspaceRoot = Path.GetFullPath(root),
                    StoreRoot = Path.Combine(root, "store"),
                    State = StoreBindingState.Planned,
                },
                client,
                () => IndexLevelPolicy.Progressive,
                _ => snapshots.Dequeue(),
                () => $"request-{++minted}",
                artifact.DbPath);

            ExtractReport report = coordinator.Scan(jobs: 1);

            StoreImportRequest[] imports = client.Requests.OfType<StoreImportRequest>().ToArray();
            Assert.Equal(2, imports.Length);
            Assert.Equal(artifact.DbPath, imports[0].FromArtifact);
            Assert.Null(imports[1].FromArtifact);
            Assert.Equal("request-1", imports[0].Request.RequestId);
            Assert.Equal("request-2", imports[1].Request.RequestId);
            Assert.NotEqual(imports[0].Request.IdempotencyKey, imports[1].Request.IdempotencyKey);
            Assert.Equal("completed", report.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedSeedImportDoesNotRetryForUnrelatedFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-seed-no-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var artifact = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>());
        try
        {
            var client = new RecordingStoreClient(
                StoreOperation.Import,
                failureClass: "view_root_mismatch",
                stateOverrides: new Queue<StoreRequestState>([StoreRequestState.Failed]));
            var coordinator = new StoreWorkspaceCoordinator(
                Binding with
                {
                    WorkspaceRoot = Path.GetFullPath(root),
                    StoreRoot = Path.Combine(root, "store"),
                    State = StoreBindingState.Planned,
                },
                client,
                () => IndexLevelPolicy.Progressive,
                _ => null,
                fromArtifact: artifact.DbPath);

            StoreWorkspaceOperationException failure = Assert.Throws<StoreWorkspaceOperationException>(
                () => coordinator.Scan(jobs: 1));

            Assert.Equal("view_root_mismatch", failure.FailureClass.Code);
            StoreImportRequest request = Assert.Single(client.Requests.OfType<StoreImportRequest>());
            Assert.Equal(artifact.DbPath, request.FromArtifact);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedSeedImportPropagatesSecondFailureWithoutAnotherRetry()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-seed-second-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var artifact = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>());
        try
        {
            var client = new RecordingStoreClient(
                StoreOperation.Import,
                failureClass: "store_incompatible",
                stateOverrides: new Queue<StoreRequestState>(
                [
                    StoreRequestState.Failed,
                    StoreRequestState.Failed,
                ]));
            var coordinator = new StoreWorkspaceCoordinator(
                Binding with
                {
                    WorkspaceRoot = Path.GetFullPath(root),
                    StoreRoot = Path.Combine(root, "store"),
                    State = StoreBindingState.Planned,
                },
                client,
                () => IndexLevelPolicy.Progressive,
                _ => null,
                fromArtifact: artifact.DbPath);

            StoreWorkspaceOperationException failure = Assert.Throws<StoreWorkspaceOperationException>(
                () => coordinator.Scan(jobs: 1));

            Assert.Equal("store_incompatible", failure.FailureClass.Code);
            StoreImportRequest[] imports = client.Requests.OfType<StoreImportRequest>().ToArray();
            Assert.Equal(2, imports.Length);
            Assert.Equal(artifact.DbPath, imports[0].FromArtifact);
            Assert.Null(imports[1].FromArtifact);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, ".miller", "store-requests"), "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_IncrementalReconcile_WhenCurrentTreeMatchesStore_SkipsImport()
    {
        var phases = new RecordingPhaseSink();
        var client = new RecordingStoreClient(StoreOperation.Import);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ => new StoreWorkspaceState(41, "full"),
            () => "request-import",
            null,
            phases,
            inspectTree: static () => StoreTreeDelta.Empty);

        ExtractReport report = coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);

        Assert.Empty(client.Requests);
        Assert.Equal("no_change", report.Status);
        Assert.Null(report.CreatedRevision);
        Assert.Equal(41, report.Revision);
        Assert.Equal(["import", "coordinator_total"], phases.Records.Select(static phase => phase.Phase));
        Assert.All(phases.Records, static phase => Assert.False(phase.DidWork));
        Assert.All(phases.Records, static phase => Assert.Equal("skipped", phase.Outcome));
    }

    [Fact]
    public void Scan_IncrementalReconcile_WhenSomeHashesDiffer_UpdatesAndDeletesThoseFilesOnly()
    {
        var phases = new RecordingPhaseSink();
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            allowedOperations: [StoreOperation.Update, StoreOperation.Delete, StoreOperation.Resolve]);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(41, "full"),
            new(44, "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ => snapshots.Dequeue(),
            () => "request-delta",
            null,
            phases,
            inspectTree: static () => new StoreTreeDelta(["src/a.cs", "src/b.cs"], ["gone.cs"]));

        ExtractReport report = coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);

        Assert.DoesNotContain(client.Requests, static request => request is StoreImportRequest);
        StoreUpdateRequest[] updates = client.Requests.OfType<StoreUpdateRequest>().ToArray();
        Assert.Equal(["src/a.cs", "src/b.cs"], updates.Select(static request => request.FilePath.Replace('\\', '/')));
        Assert.Equal(["gone.cs"], Assert.Single(client.Requests.OfType<StoreDeleteRequest>()).FilePaths);
        Assert.DoesNotContain(client.Requests, static request => request.Operation == StoreOperation.Resolve);
        Assert.Equal("completed", report.Status);
        Assert.Equal((ulong)2, report.FilesUpdated);
        Assert.Equal((ulong)1, report.FilesDeleted);
        Assert.Equal(44, report.Revision);
        Assert.Equal(44, report.CreatedRevision);
        Assert.NotEqual("import", report.Operation);
    }

    [Fact]
    public void Scan_UserFullRebuild_StillImportsWhenTheStoreIsAlreadyFull()
    {
        var client = new RecordingStoreClient(StoreOperation.Import);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(41, "full"),
            new(42, "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ => snapshots.Dequeue(),
            () => "request-full",
            fromArtifact: null,
            inspectTree: static () => new StoreTreeDelta(["src/a.cs"], []));

        ExtractReport report = coordinator.Scan(ScanIntent.UserFullRebuild, jobs: 1);

        Assert.IsType<StoreImportRequest>(client.Requests[0]);
        Assert.Equal("completed", report.Status);
        Assert.Equal("import", report.Operation);
    }

    [Fact]
    public void Scan_IncrementalReconcile_SkipsResolveWhenTheLastUpdateIsAlreadyExact()
    {
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            allowedOperations: [StoreOperation.Update, StoreOperation.Resolve]);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(41, "full"),
            new(42, "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ => snapshots.Dequeue(),
            () => "request-exact",
            fromArtifact: null,
            inspectTree: static () => new StoreTreeDelta(["src/a.cs"], []));

        coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);

        Assert.IsType<StoreUpdateRequest>(Assert.Single(client.Requests));
    }

    [Fact]
    public void Scan_IncrementalReconcile_SkipsResolveWhenTheJournalHasNoResolveKeys()
    {
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            allowedOperations: [StoreOperation.Update, StoreOperation.Resolve]);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(41, "full"),
            new(42, "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ => snapshots.Dequeue(),
            () => "request-docs",
            fromArtifact: null,
            inspectTree: static () => new StoreTreeDelta(["README.md"], []));

        coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);

        Assert.IsType<StoreUpdateRequest>(Assert.Single(client.Requests));
        Assert.DoesNotContain(client.Requests, static request => request.Operation == StoreOperation.Resolve);
    }

    [Fact]
    public void Update_SkipsResolveWhenTheJournalHasNoResolveKeys()
    {
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            allowedOperations: [StoreOperation.Update, StoreOperation.Resolve]);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(41, "full"),
            new(42, "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ => snapshots.Dequeue(),
            () => "request-docs",
            fromArtifact: null);

        ExtractReport report = coordinator.Update(Path.Combine(Binding.WorkspaceRoot, "docs", "README.md"));

        Assert.IsType<StoreUpdateRequest>(Assert.Single(client.Requests));
        Assert.DoesNotContain(client.Requests, static request => request.Operation == StoreOperation.Resolve);
        Assert.Equal("completed", report.Status);
    }

    [Fact]
    public void Update_DoesNotSubmitResolveAfterAFullLevelSave()
    {
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            allowedOperations: [StoreOperation.Update]);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(41, "full"),
            new(42, "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ => snapshots.Dequeue(),
            () => "request-code",
            fromArtifact: null);

        coordinator.Update(Path.Combine(Binding.WorkspaceRoot, "src", "a.cs"));

        Assert.IsType<StoreUpdateRequest>(Assert.Single(client.Requests));
        Assert.DoesNotContain(client.Requests, static request => request.Operation == StoreOperation.Resolve);
    }

    [Fact]
    public void Diff_DoesNotAddWatcherNoiseOrUnknownExtensions()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-tree-delta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "dist"));
            File.WriteAllText(Path.Combine(root, "src", "App.cs"), "class App;");
            File.WriteAllText(Path.Combine(root, "LICENSE"), "MIT");
            File.WriteAllBytes(Path.Combine(root, "icon.png"), [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(root, "src", "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "src", "Changed.cs"), "class Changed;");
            File.WriteAllText(Path.Combine(root, "src", "Generated.cs"), OverLimitText());
            File.WriteAllText(Path.Combine(root, "src", "vendor.min.js"), "var a=1;");
            File.WriteAllText(Path.Combine(root, "dist", "Bundled.cs"), "class Bundled;");
            var stored = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/Changed.cs"] = "deadbeef",
            };

            StoreTreeDelta delta = StoreTreeDelta.Diff(
                stored,
                root,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs", "js" });

            Assert.Equal(["src/App.cs", "src/Changed.cs"], delta.ChangedOrAdded);
            Assert.Equal(["src/App.cs"], delta.Added?.Order(StringComparer.Ordinal));
            Assert.Empty(delta.Deleted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Diff_SkipsARefusedAddUntilItsContentChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-tree-delta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            string source = Path.Combine(root, "src", "Refused.cs");
            File.WriteAllText(source, "class Refused;");
            var stored = new Dictionary<string, string>(StringComparer.Ordinal);
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs" };
            var refusals = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/Refused.cs"] = ContentHasher.Blake3FileHex(source),
            };

            Assert.Empty(StoreTreeDelta.Diff(stored, root, extensions, refusals).ChangedOrAdded);

            File.WriteAllText(source, "class Refused { }");

            Assert.Equal(
                ["src/Refused.cs"],
                StoreTreeDelta.Diff(stored, root, extensions, refusals).ChangedOrAdded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ARefusedAddIsSubmittedOnceUntilItsContentChanges()
    {
        using var workspace = new TempWorkspace();
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs" };
        var ledger = new StoreRefusalLedger(workspace.Root);
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            manifestDisposition: StoreManifestDisposition.Reused);
        StoreWorkspaceCoordinator coordinator = workspace.Coordinator(
            client,
            () => StoreTreeDelta.Diff(stored, workspace.Root, extensions, ledger.Read()));

        coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);
        Assert.Single(client.Requests);

        coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);
        Assert.Single(client.Requests);

        File.WriteAllText(workspace.SourcePath, "class A { }");
        coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);
        Assert.Equal(2, client.Requests.Count);

        coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public void APublishedAddLeavesNoRefusalBehind()
    {
        using var workspace = new TempWorkspace();
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs" };
        var ledger = new StoreRefusalLedger(workspace.Root);
        var client = new RecordingStoreClient(StoreOperation.Update);
        StoreWorkspaceCoordinator coordinator = workspace.Coordinator(
            client,
            () => StoreTreeDelta.Diff(stored, workspace.Root, extensions, ledger.Read()));

        coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);

        Assert.Single(client.Requests);
        Assert.Empty(ledger.Read());
    }

    [Fact]
    public void ARefusedChangeToAStoredFileIsNeverRemembered()
    {
        using var workspace = new TempWorkspace();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs" };
        var stored = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/a.cs"] = "blake3:deadbeef",
        };
        var ledger = new StoreRefusalLedger(workspace.Root);
        var client = new RecordingStoreClient(
            StoreOperation.Update,
            manifestDisposition: StoreManifestDisposition.Reused);
        StoreWorkspaceCoordinator coordinator = workspace.Coordinator(
            client,
            () => StoreTreeDelta.Diff(stored, workspace.Root, extensions, ledger.Read()));

        coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);
        coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);

        Assert.Equal(2, client.Requests.Count);
        Assert.Empty(ledger.Read());
    }

    [Fact]
    public void Diff_SubmitsAnOverLimitFileTheManifestAlreadyLists()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-tree-delta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            File.WriteAllText(Path.Combine(root, "src", "Grown.cs"), OverLimitText());
            var stored = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/Grown.cs"] = "blake3:deadbeef",
            };

            StoreTreeDelta delta = StoreTreeDelta.Diff(
                stored,
                root,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs" });

            Assert.Equal(["src/Grown.cs"], delta.ChangedOrAdded);
            Assert.False(delta.IsAdded("src/Grown.cs"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string OverLimitText() =>
        new('x', (int)ExtractSourceLimits.DefaultMaxSourceFileBytes + 1);

    [Fact]
    public void Diff_WithoutCatalogStillAddsSourceFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-tree-delta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            File.WriteAllText(Path.Combine(root, "src", "App.cs"), "class App;");
            File.WriteAllText(Path.Combine(root, "LICENSE"), "MIT");
            var stored = new Dictionary<string, string>(StringComparer.Ordinal);

            StoreTreeDelta delta = StoreTreeDelta.Diff(stored, root, supportedExtensions: null);

            Assert.Contains("src/App.cs", delta.ChangedOrAdded);
            Assert.Contains("LICENSE", delta.ChangedOrAdded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AReusedExactFullImportDoesNotSubmitResolve()
    {
        var phases = new RecordingPhaseSink();
        var client = new RecordingStoreClient(
            StoreOperation.Import,
            manifestDisposition: StoreManifestDisposition.Reused);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(41, "l1"),
            new(42, "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ => snapshots.Dequeue(),
            () => "request-import",
            null,
            phaseSink: phases);

        coordinator.Scan(jobs: 1);

        Assert.Single(client.Requests);
        Assert.IsType<StoreImportRequest>(client.SingleRequest);
        Assert.Equal(["import", "coordinator_total"], phases.Records.Select(static phase => phase.Phase));
        Assert.True(phases.Records.Single(static phase => phase.Phase == "import").DidWork);
        Assert.True(phases.Records.Single(static phase => phase.Phase == "coordinator_total").DidWork);
        Assert.DoesNotContain(client.Requests, static request => request.Operation == StoreOperation.Resolve);
    }

    [Fact]
    public void AReusedLevelOneImportFromFullDoesNotReportWork()
    {
        var phases = new RecordingPhaseSink();
        var client = new RecordingStoreClient(
            StoreOperation.Import,
            manifestDisposition: StoreManifestDisposition.Reused);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(41, "full"),
            new(42, "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.SymbolsOnly,
            _ => snapshots.Dequeue(),
            () => "request-import",
            null,
            phaseSink: phases);

        ExtractReport report = coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);

        Assert.Equal(StoreLevel.L1, Assert.IsType<StoreImportRequest>(client.SingleRequest).Level);
        Assert.Equal("no_change", report.Status);
        Assert.False(phases.Records.Single(static phase => phase.Phase == "import").DidWork);
        Assert.False(phases.Records.Single(static phase => phase.Phase == "coordinator_total").DidWork);
    }

    [Fact]
    public void Scan_RecordsImportAndCoordinatorTotal()
    {
        var phases = new RecordingPhaseSink();
        var client = new RecordingStoreClient(StoreOperation.Import);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(41, "l1"),
            new(42, "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ => snapshots.Dequeue(),
            () => "request-import",
            null,
            phaseSink: phases);

        ExtractReport report = coordinator.Scan(jobs: 1);

        Assert.Equal("completed", report.Status);
        Assert.Equal(["import", "coordinator_total"], phases.Records.Select(static phase => phase.Phase));
        Assert.All(phases.Records, static phase => Assert.True(phase.ElapsedMilliseconds >= 0));
        Assert.Equal(42, phases.Records.Single(static phase => phase.Phase == "coordinator_total").StoreSequence);
        Assert.All(phases.Records, static phase => Assert.True(phase.DidWork));
        Assert.All(phases.Records, static phase => Assert.Equal("completed", phase.Outcome));
    }

    [Fact]
    public void AReusedExactImportWithAMismatchedFenceDoesNotSubmitResolve()
    {
        var client = new RecordingStoreClient(
            StoreOperation.Import,
            manifestDisposition: StoreManifestDisposition.Reused);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(41, "l1"),
            new(42, "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ => snapshots.Dequeue(),
            () => "request-import");

        coordinator.Scan(jobs: 1);

        Assert.IsType<StoreImportRequest>(Assert.Single(client.Requests));
        Assert.DoesNotContain(client.Requests, static request => request.Operation == StoreOperation.Resolve);
    }

    [Fact]
    public void AFullImportDoesNotSubmitResolve()
    {
        var client = new RecordingStoreClient(StoreOperation.Import);
        var snapshots = new Queue<StoreWorkspaceState>(
        [
            new(41, "l1"),
            new(42, "full"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding,
            client,
            () => IndexLevelPolicy.Full,
            _ => snapshots.Dequeue(),
            () => "request-import");

        coordinator.Scan(jobs: 1);

        Assert.IsType<StoreImportRequest>(Assert.Single(client.Requests));
        Assert.DoesNotContain(client.Requests, static request => request.Operation == StoreOperation.Resolve);
    }

    /// <summary>
    /// Proves: a re-planned view recovers through the ORDINARY refresh path. An IncrementalReconcile against a
    /// Planned binding submits a full import, never a file delta, so RequiresRootRebind returning false for a
    /// planned view costs nothing.
    /// </summary>
    [Fact]
    public void PlannedBindingScanSubmitsAFullImport()
    {
        var client = new RecordingStoreClient(StoreOperation.Import);
        var snapshots = new Queue<StoreWorkspaceState?>(
        [
            null,
            new StoreWorkspaceState(1, "l1"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding with { State = StoreBindingState.Planned },
            client,
            () => IndexLevelPolicy.Progressive,
            _ => snapshots.Dequeue(),
            () => "request-import");

        coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);

        Assert.IsType<StoreImportRequest>(Assert.Single(client.Requests));
        Assert.DoesNotContain(client.Requests, static request => request is StoreUpdateRequest);
        Assert.DoesNotContain(client.Requests, static request => request is StoreDeleteRequest);
    }

    /// <summary>
    /// Proves: a view that was published and then LOST is rebuilt by real extraction, never republished from
    /// the workspace's legacy symbols.db. A seeded import emits --from-artifact with no --level and no scan
    /// controls, so a months-old artifact would come back reporting itself fresh. The legacy-to-store migration
    /// seed for a never-published view is untouched.
    /// </summary>
    [Fact]
    public void AVanishedViewNeverSeedsFromTheLegacyArtifact()
    {
        using var artifact = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>());

        StoreImportRequest vanished = RunReplannedImport(artifact.DbPath, StoreViewReplan.VanishedFromCatalog);
        StoreImportRequest neverPublished = RunReplannedImport(artifact.DbPath, StoreViewReplan.NeverPublished);

        Assert.Null(vanished.FromArtifact);
        Assert.Equal(artifact.DbPath, neverPublished.FromArtifact);
        Assert.True(File.Exists(artifact.DbPath));
    }

    private static StoreImportRequest RunReplannedImport(string fromArtifact, StoreViewReplan replan)
    {
        var client = new RecordingStoreClient(StoreOperation.Import);
        var snapshots = new Queue<StoreWorkspaceState?>(
        [
            null,
            new StoreWorkspaceState(1, "l1"),
        ]);
        var coordinator = new StoreWorkspaceCoordinator(
            Binding with { State = StoreBindingState.Planned, Replan = replan },
            client,
            () => IndexLevelPolicy.Progressive,
            _ => snapshots.Dequeue(),
            () => "request-import",
            fromArtifact);

        coordinator.Scan(ScanIntent.IncrementalReconcile, jobs: 1);

        return Assert.IsType<StoreImportRequest>(client.SingleRequest);
    }

    [Fact]
    public void IncompatibleLegacyArtifactFallsBackToSourceImport()
    {
        using var artifact = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema - 1,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>());

        StoreImportRequest request = RunNewFamilyImport(artifact.DbPath);

        Assert.Null(request.FromArtifact);
        Assert.True(File.Exists(artifact.DbPath));
    }

    [Fact]
    public void MissingLegacyArtifactFallsBackToSourceImport()
    {
        string artifactPath = Path.Combine(
            Path.GetTempPath(),
            "miller-store-missing-seed-" + Guid.NewGuid().ToString("N"),
            "symbols.db");

        StoreImportRequest request = RunNewFamilyImport(artifactPath);

        Assert.Null(request.FromArtifact);
    }

    [Fact]
    public void NonArtifactDatabaseFallsBackToSourceImport()
    {
        using var artifact = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>(),
            createMetadataTable: false);

        StoreImportRequest request = RunNewFamilyImport(artifact.DbPath);

        Assert.Null(request.FromArtifact);
        Assert.True(File.Exists(artifact.DbPath));
    }

    [Fact]
    public void CorruptLegacyArtifactFallsBackToSourceImport()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-corrupt-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string artifactPath = Path.Combine(root, "symbols.db");
        File.WriteAllText(artifactPath, "not a sqlite database");
        try
        {
            StoreImportRequest request = RunNewFamilyImport(artifactPath);

            Assert.Null(request.FromArtifact);
            Assert.True(File.Exists(artifactPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
        Assert.Equal(
            ExtractWaitPolicy.HardTimeoutForEnvironment(
                JulieExtractRunner.DefaultTimeout,
                Environment.GetEnvironmentVariable),
            request.Request.Timeout);
    }

    [Fact]
    public void EnvironmentHardCapIsNormalizedToProducerRequestSeconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(900),
            StoreWorkspaceCoordinator.DefaultLongRequestTimeoutFor(_ => "900.5"));
    }

    [Fact]
    public void StoreTimeoutOverrideOnlyAppliesToLongOperations()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            StoreWorkspaceCoordinator.RequestTimeout(StoreOperation.Import, "30"));
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            StoreWorkspaceCoordinator.RequestTimeout(StoreOperation.Update, "30"));
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            StoreWorkspaceCoordinator.RequestTimeout(StoreOperation.Delete, "30"));
    }

    [Fact]
    public void NewFamilyImportPassesMillersInvariantIgnoreFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-ignore-" + Guid.NewGuid().ToString("N"));
        string millerHome = Path.Combine(root, "miller-home");
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
                fromArtifact: null,
                phaseSink: null,
                inspectTree: null,
                millerDirectory: millerHome);

            coordinator.Scan(jobs: 1);

            StoreImportRequest request = Assert.IsType<StoreImportRequest>(client.SingleRequest);
            string generated = JulieIgnoreSeeder.GeneratedGlobalIgnorePathForWorkspaceId(
                WorkspaceId.FromCanonicalRoot(root), millerHome);
            string invariant = ScanIgnorePolicy.InvariantIgnorePathFor(root);
            Assert.Equal([generated, invariant], request.Scan.IgnoreFiles);
            Assert.False(File.Exists(Path.Combine(root, ".julieignore")));
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
        string millerHome = Path.Combine(root, "miller-home");
        Directory.CreateDirectory(Path.Combine(root, ".miller"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "a.cs"), "class A {}");
            string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(root);
            string invariant = ScanIgnorePolicy.InvariantIgnorePathFor(canonicalRoot);
            File.WriteAllText(invariant, ScanIgnorePolicy.RenderInvariantContent());
            string generated = JulieIgnoreSeeder.GeneratedGlobalIgnorePathForWorkspaceId(
                WorkspaceId.FromCanonicalRoot(canonicalRoot), millerHome);
            Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
            File.WriteAllText(generated, "generated/\n");
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
                () => "request-update-ignore",
                fromArtifact: null,
                phaseSink: null,
                inspectTree: null,
                millerDirectory: millerHome);

            coordinator.Update(Path.Combine(canonicalRoot, "src", "a.cs"));

            StoreUpdateRequest request = Assert.IsType<StoreUpdateRequest>(client.SingleRequest);
            Assert.Equal([generated, invariant], request.Scan.IgnoreFiles);
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
        var phases = new RecordingPhaseSink();
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
                () => "request-repair",
                null,
                phaseSink: phases);

            coordinator.EnsureBindingPointer();

            StoreWorkspacePointerDocument repaired = Assert.IsType<StoreWorkspacePointerDocument>(
                StoreWorkspacePointer.Read(root));
            Assert.Equal(binding.FamilyId, repaired.FamilyId);
            Assert.Equal(binding.ViewId, repaired.ViewId);
            Assert.Equal(binding.StoreRoot, repaired.StoreRoot);
            IndexerPhaseRecord phase = Assert.Single(phases.Records);
            Assert.Equal("bind", phase.Phase);
            Assert.True(phase.DidWork);
            Assert.Null(phase.StoreSequence);
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

    [Fact]
    public void InterruptedTerminalImportIsReplayedThenReconciledWithAFreshRequest()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-request-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var artifact = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>());
        try
        {
            string fromArtifact = artifact.DbPath;
            string fingerprint = $"import|{Binding.FamilyId:D}|{Binding.ViewId}|L1|{fromArtifact}";
            var journal = new StoreRequestJournal(root);
            Assert.Equal("orphan-request", journal.GetOrCreate(fingerprint, () => "orphan-request"));

            var client = new RecordingStoreClient(StoreOperation.Import);
            var snapshots = new Queue<StoreWorkspaceState?>
            (
            [
                null,
                new StoreWorkspaceState(2, "l1"),
            ]);
            var coordinator = new StoreWorkspaceCoordinator(
                Binding with
                {
                    WorkspaceRoot = Path.GetFullPath(root),
                    StoreRoot = Path.Combine(root, "store"),
                    State = StoreBindingState.Planned,
                },
                client,
                () => IndexLevelPolicy.Progressive,
                _ => snapshots.Dequeue(),
                fromArtifact: fromArtifact);

            coordinator.Scan(jobs: 1);

            StoreRequest[] imports = client.Requests
                .Where(request => request.Operation == StoreOperation.Import)
                .ToArray();
            Assert.Equal(2, imports.Length);
            Assert.Equal("orphan-request", Assert.IsType<StoreImportRequest>(imports[0]).Request.RequestId);
            Assert.Equal(fromArtifact, Assert.IsType<StoreImportRequest>(imports[0]).FromArtifact);
            Assert.NotEqual(
                Assert.IsType<StoreImportRequest>(imports[0]).Request.RequestId,
                Assert.IsType<StoreImportRequest>(imports[1]).Request.RequestId);
            Assert.Null(Assert.IsType<StoreImportRequest>(imports[1]).FromArtifact);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedReplayedSeedImportRetriesOnceWithoutTheSeedUsingTheFreshFingerprint()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-request-replay-failed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var artifact = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>());
        try
        {
            string fromArtifact = artifact.DbPath;
            string seedFingerprint = $"import|{Binding.FamilyId:D}|{Binding.ViewId}|L1|{fromArtifact}";
            string freshFingerprint = $"import|{Binding.FamilyId:D}|{Binding.ViewId}|L1|";
            var journal = new StoreRequestJournal(root);
            Assert.Equal("orphan-request", journal.GetOrCreate(seedFingerprint, () => "orphan-request"));
            Assert.Equal("fresh-request", journal.GetOrCreate(freshFingerprint, () => "fresh-request"));

            var client = new RecordingStoreClient(
                StoreOperation.Import,
                failureClass: "store_incompatible",
                stateOverrides: new Queue<StoreRequestState>(
                [
                    StoreRequestState.Failed,
                    StoreRequestState.Committed,
                ]));
            var snapshots = new Queue<StoreWorkspaceState?>(
            [
                null,
                new StoreWorkspaceState(2, "l1"),
            ]);
            var coordinator = new StoreWorkspaceCoordinator(
                Binding with
                {
                    WorkspaceRoot = Path.GetFullPath(root),
                    StoreRoot = Path.Combine(root, "store"),
                    State = StoreBindingState.Planned,
                },
                client,
                () => IndexLevelPolicy.Progressive,
                _ => snapshots.Dequeue(),
                fromArtifact: fromArtifact);

            ExtractReport report = coordinator.Scan(jobs: 1);

            StoreImportRequest[] imports = client.Requests.OfType<StoreImportRequest>().ToArray();
            Assert.Equal(2, imports.Length);
            Assert.Equal("orphan-request", imports[0].Request.RequestId);
            Assert.Equal(fromArtifact, imports[0].FromArtifact);
            Assert.Equal("fresh-request", imports[1].Request.RequestId);
            Assert.Null(imports[1].FromArtifact);
            Assert.Equal("completed", report.Status);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, ".miller", "store-requests"), "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExistingStateScanBypassesLegacySeedSelection()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-existing-state-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var artifact = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>());
        try
        {
            string fingerprint = $"import|{Binding.FamilyId:D}|{Binding.ViewId}|L1|";
            var journal = new StoreRequestJournal(root);
            Assert.Equal("orphan-request", journal.GetOrCreate(fingerprint, () => "orphan-request"));
            var client = new RecordingStoreClient(StoreOperation.Import);
            var snapshots = new Queue<StoreWorkspaceState?>
            (
            [
                new StoreWorkspaceState(1, "l1"),
                new StoreWorkspaceState(2, "l1"),
            ]);
            var coordinator = new StoreWorkspaceCoordinator(
                Binding with
                {
                    WorkspaceRoot = Path.GetFullPath(root),
                    StoreRoot = Path.Combine(root, "store"),
                },
                client,
                () => IndexLevelPolicy.Progressive,
                _ => snapshots.Dequeue(),
                fromArtifact: artifact.DbPath);

            coordinator.Scan(jobs: 1);

            StoreImportRequest[] imports = client.Requests.OfType<StoreImportRequest>().ToArray();
            Assert.Equal(2, imports.Length);
            Assert.All(imports, request => Assert.Null(request.FromArtifact));
            Assert.Equal("orphan-request", imports[0].Request.RequestId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StoreImportRequest RunNewFamilyImport(string fromArtifact)
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
            () => "request-import",
            fromArtifact);

        coordinator.Scan(jobs: 1);

        return Assert.IsType<StoreImportRequest>(client.SingleRequest);
    }

    private sealed class RecordingPhaseSink : IIndexerPhaseSink
    {
        public List<IndexerPhaseRecord> Records { get; } = [];

        public void Record(IndexerPhaseRecord record) => Records.Add(record);
    }

    private sealed class ThrowingPhaseSink : IIndexerPhaseSink
    {
        public void Record(IndexerPhaseRecord record) => throw new InvalidOperationException("sink failed");
    }

    private sealed class TempWorkspace : IDisposable
    {
        private readonly string _root;

        public TempWorkspace()
        {
            _root = Path.Combine(Path.GetTempPath(), "miller-store-journal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            SourcePath = Path.Combine(_root, "src", "a.cs");
            File.WriteAllText(SourcePath, "class A;");
        }

        public string Root => _root;

        public string SourcePath { get; }

        public string JournalDirectory => Path.Combine(_root, ".miller", "store-requests");

        public StoreWorkspaceCoordinator Coordinator(
            IJulieStoreClient client,
            Func<StoreTreeDelta>? inspectTree = null) =>
            new(
                new StoreFamilyBinding(
                    Binding.FamilyId,
                    Path.Combine(_root, "family"),
                    Binding.ViewId,
                    _root,
                    StoreBindingState.Ready),
                client,
                () => IndexLevelPolicy.Full,
                _ => new StoreWorkspaceState(41, "full"),
                mintRequestId: null,
                fromArtifact: null,
                phaseSink: null,
                inspectTree: inspectTree,
                millerDirectory: Path.Combine(_root, "home"));

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingStoreClient(
        StoreOperation expectedOperation,
        StoreManifestDisposition manifestDisposition = StoreManifestDisposition.Created,
        int exitCode = 0,
        string failureClass = "none",
        StoreRequestState? stateOverride = null,
        Queue<StoreRequestState>? stateOverrides = null,
        StoreOperation[]? allowedOperations = null,
        string? failureMessage = null) : IJulieStoreClient
    {
        private readonly List<StoreRequest> _requests = [];

        public IReadOnlyList<StoreRequest> Requests => _requests;
        public StoreRequest SingleRequest => Assert.Single(_requests);

        public StoreRequestResult Submit(StoreRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (allowedOperations is { Length: > 0 })
            {
                Assert.Contains(request.Operation, allowedOperations);
            }
            else
            {
                Assert.Equal(expectedOperation, request.Operation);
            }
            _requests.Add(request);
            StoreLevel level = request switch
            {
                StoreImportRequest import => import.Level,
                StoreUpdateRequest update => update.Level,
                _ => StoreLevel.NotApplicable,
            };
            StoreRequestState state = stateOverride
                ?? (stateOverrides?.Count > 0
                    ? stateOverrides.Dequeue()
                    : exitCode == 0 ? StoreRequestState.Committed : StoreRequestState.Failed);
            bool durable = state is StoreRequestState.Committed or StoreRequestState.Acknowledged;
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
                state,
                level,
                new StoreLevelCompletion(true, level == StoreLevel.Full, level == StoreLevel.Full),
                new StoreManifestResult(1, "manifest-hash", manifestDisposition),
                new StoreRowCounts(1, 1, level == StoreLevel.Full ? 1 : 0, level == StoreLevel.Full ? 1 : 0),
                null,
                durable ? StoreCoordinatorDisposition.Committed : StoreCoordinatorDisposition.Failed,
                new StoreFailure(
                    new StoreFailureClass(failureClass),
                    failureMessage ?? (exitCode == 0 ? null : "failed")),
                exitCode);
        }
    }
}
