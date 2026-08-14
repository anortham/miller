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
        Assert.IsType<StoreResolveRequest>(client.Requests[1]);
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
        Assert.Equal(expected == StoreLevel.Full, client.Requests.Any(request => request.Operation == StoreOperation.Resolve));
    }

    [Fact]
    public void AReusedExactFullImportDoesNotSubmitResolve()
    {
        var phases = new RecordingPhaseSink();
        var client = new RecordingStoreClient(
            StoreOperation.Import,
            manifestDisposition: StoreManifestDisposition.Reused,
            importResolutionState: StoreResolutionState.Exact,
            importExactAtMatches: true);
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
        Assert.Equal(["import", "resolve", "coordinator_total"], phases.Records.Select(static phase => phase.Phase));
        Assert.False(phases.Records.Single(static phase => phase.Phase == "resolve").DidWork);
        Assert.True(phases.Records.Single(static phase => phase.Phase == "coordinator_total").DidWork);
    }

    [Fact]
    public void Scan_RecordsImportResolveAndCoordinatorTotal()
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
        Assert.Equal(["import", "resolve", "coordinator_total"], phases.Records.Select(static phase => phase.Phase));
        Assert.All(phases.Records, static phase => Assert.True(phase.ElapsedMilliseconds >= 0));
        Assert.Equal(42, phases.Records.Single(static phase => phase.Phase == "coordinator_total").StoreSequence);
        Assert.All(phases.Records, static phase => Assert.True(phase.DidWork));
        Assert.All(phases.Records, static phase => Assert.Equal("completed", phase.Outcome));
    }

    [Fact]
    public void AReusedExactImportWithAMismatchedFenceSubmitsResolve()
    {
        var client = new RecordingStoreClient(
            StoreOperation.Import,
            manifestDisposition: StoreManifestDisposition.Reused,
            importResolutionState: StoreResolutionState.Exact,
            importExactAtMatches: false);
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

        Assert.Equal(2, client.Requests.Count);
        Assert.IsType<StoreImportRequest>(client.Requests[0]);
        Assert.IsType<StoreResolveRequest>(client.Requests[1]);
    }

    [Fact]
    public void AFullImportWithoutExactResolutionStillSubmitsResolve()
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

        Assert.Equal(2, client.Requests.Count);
        Assert.IsType<StoreImportRequest>(client.Requests[0]);
        Assert.IsType<StoreResolveRequest>(client.Requests[1]);
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
            TimeSpan.FromSeconds(30),
            StoreWorkspaceCoordinator.RequestTimeout(StoreOperation.Resolve, "00:00:30"));
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

    [Fact]
    public void InterruptedResolveIsReplayedThenSubmittedWithAFreshRequest()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-resolve-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string fingerprint = $"resolve|{Binding.FamilyId:D}|{Binding.ViewId}";
            var journal = new StoreRequestJournal(root);
            Assert.Equal("orphan-resolve", journal.GetOrCreate(fingerprint, () => "orphan-resolve"));

            var client = new RecordingStoreClient(StoreOperation.Update);
            var snapshots = new Queue<StoreWorkspaceState>(
            [
                new StoreWorkspaceState(1, "full"),
                new StoreWorkspaceState(2, "full"),
            ]);
            string source = Path.Combine(root, "src", "a.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "class A {}");
            var coordinator = new StoreWorkspaceCoordinator(
                Binding with
                {
                    WorkspaceRoot = PathCanonicalizer.CanonicalizeRoot(root),
                    StoreRoot = Path.Combine(root, "store"),
                },
                client,
                () => IndexLevelPolicy.Full,
                _ => snapshots.Dequeue());

            coordinator.Update(source);

            StoreRequest[] resolves = client.Requests
                .Where(request => request.Operation == StoreOperation.Resolve)
                .ToArray();
            Assert.Equal(2, resolves.Length);
            Assert.Equal("orphan-resolve", Assert.IsType<StoreResolveRequest>(resolves[0]).Request.RequestId);
            Assert.NotEqual(
                Assert.IsType<StoreResolveRequest>(resolves[0]).Request.RequestId,
                Assert.IsType<StoreResolveRequest>(resolves[1]).Request.RequestId);
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

    private sealed class RecordingStoreClient(
        StoreOperation expectedOperation,
        StoreManifestDisposition manifestDisposition = StoreManifestDisposition.Created,
        int exitCode = 0,
        string failureClass = "none",
        StoreRequestState? stateOverride = null,
        StoreResolutionState importResolutionState = StoreResolutionState.Unbound,
        bool importExactAtMatches = false) : IJulieStoreClient
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
            StoreRequestState state = stateOverride
                ?? (exitCode == 0 ? StoreRequestState.Committed : StoreRequestState.Failed);
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
                request.Operation == StoreOperation.Resolve
                    ? new StoreResolutionResult(StoreResolutionState.Exact, true, "base", 1, 1, 0, 0, 0)
                    : new StoreResolutionResult(
                        importResolutionState,
                        importExactAtMatches,
                        importResolutionState == StoreResolutionState.Exact ? "base" : null,
                        importResolutionState == StoreResolutionState.Exact ? 1 : null,
                        importResolutionState == StoreResolutionState.Exact ? 1 : null,
                        null,
                        null,
                        null),
                null,
                durable ? StoreCoordinatorDisposition.Committed : StoreCoordinatorDisposition.Failed,
                new StoreFailure(new StoreFailureClass(failureClass), exitCode == 0 ? null : "failed"),
                exitCode);
        }
    }
}
