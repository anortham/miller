using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Server.Hosting;
using Miller.Tests.Indexing;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

public sealed class StoreSidecarConvergerTests
{
    [Fact]
    public void ConvergeStoreRecordsHistoryFromThePinnedFamilySession()
    {
        using var fixture = JulieDbFixture.CreateDefault();
        string storeRoot = Path.Combine(fixture.WorkspaceRoot, "store");
        Directory.CreateDirectory(storeRoot);
        using var session = new FixtureStoreSession(fixture);
        var converger = NewStoreConverger((_, _) => false);

        converger.ConvergeStore(storeRoot, session);

        string historyPath = Path.Combine(fixture.WorkspaceRoot, ".miller", MetricHistoryStore.HistoryDbFileName);
        Assert.Equal(1, SnapshotCount(historyPath));
        Assert.Equal(31L, ScalarLong(historyPath, "SELECT revision FROM snapshots LIMIT 1;"));
        Assert.Equal(fixture.Rows.Count, ScalarDouble(
            historyPath,
            $"SELECT value FROM snapshot_metrics WHERE metric = '{MetricSnapshotAggregates.SymbolCount}';"));
    }

    [Fact]
    public void ConvergeStoreBuildsDerivedSidecarsBeforePublishingTheVectorTarget()
    {
        var calls = new List<string>();
        var signal = new VectorConvergeSignal(enabled: true);
        using var session = new FakeStoreSession();
        var converger = new IndexerSidecarConverger(
            searchEnabled: true,
            (_, _, _, _) => false,
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            static path => path,
            static path => path,
            (_, _, _) => false,
            NullLogger.Instance,
            signal,
            ensureStoreContent: (root, _) => { calls.Add("content:" + root); return Detail(true); },
            ensureStoreSearch: (root, _) => { calls.Add("search:" + root); return Detail(true); });

        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            converger.ConvergeStore(root, session);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Assert.Equal(["content:" + root, "search:" + root], calls);
        Assert.Equal(0, signal.TargetRevision);
    }

    [Fact]
    public void ConvergeStoreContainsSidecarFailureAndStillPublishesTheVectorTarget()
    {
        var calls = new List<string>();
        var signal = new VectorConvergeSignal(enabled: true);
        using var session = new FakeStoreSession();
        var converger = new IndexerSidecarConverger(
            searchEnabled: true,
            (_, _, _, _) => false,
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            static path => path,
            static path => path,
            (_, _, _) => false,
            NullLogger.Instance,
            signal,
            ensureStoreContent: (_, _) => throw new TimeoutException("sidecar lease timeout"),
            ensureStoreSearch: (_, _) =>
            {
                calls.Add("search");
                return Detail(true);
            });

        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            converger.ConvergeStore(root, session);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Assert.Equal(["search"], calls);
        Assert.Equal(0, signal.TargetRevision);
    }

    [Fact]
    public void ConvergeStore_ReturnsPerSidecarOutcomesAndBoundedFailureEvidence()
    {
        var signal = new VectorConvergeSignal(enabled: true);
        using var session = new FakeStoreSession();
        var converger = new IndexerSidecarConverger(
            searchEnabled: true,
            (_, _, _, _) => false,
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            static path => path,
            static path => path,
            (_, _, _) => false,
            NullLogger.Instance,
            signal,
            ensureStoreContent: (_, _) => throw new TimeoutException(new string('x', 512)),
            ensureStoreSearch: (_, _) => Detail(true));

        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            StoreSidecarConvergenceResult result = converger.ConvergeStore(root, session);

            Assert.Equal(31, result.TargetSequence);
            Assert.Equal("failed", result.Content.Status);
            Assert.Equal("repaired", result.Search.Status);
            Assert.Equal("leader_required", result.Vector.Status);
            Assert.False(result.Content.DidWork);
            Assert.True(result.Search.DidWork);
            Assert.True(result.DidWork);
            Assert.True(result.Pending);
            Assert.True(result.LeaderRequired);
            Assert.NotNull(result.FailureReason);
            Assert.True(result.FailureReason!.Length <= 300);
            Assert.DoesNotContain("resident vector drain", result.FailureReason, StringComparison.Ordinal);
            Assert.Contains("resident vector drain", result.Vector.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConvergeStore_WithResidentDrain_QueuesVectorTarget()
    {
        var signal = new VectorConvergeSignal(enabled: true);
        using var session = new FakeStoreSession();
        var converger = new IndexerSidecarConverger(
            searchEnabled: false,
            (_, _, _, _) => false,
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            static path => path,
            static path => path,
            (_, _, _) => false,
            NullLogger.Instance,
            signal,
            ensureStoreContent: (_, _) => Detail(false),
            vectorDrainAvailable: static () => true);

        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-queued-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            StoreSidecarConvergenceResult result = converger.ConvergeStore(root, session);

            Assert.Equal("current", result.Content.Status);
            Assert.Equal("disabled", result.Search.Status);
            Assert.Equal("queued", result.Vector.Status);
            Assert.True(result.Vector.Pending);
            Assert.False(result.LeaderRequired);
            Assert.True(result.Pending);
            Assert.Equal(31, signal.TargetRevision);

            StoreSidecarConvergenceResult second = converger.ConvergeStore(root, session);

            Assert.Equal("queued", second.Vector.Status);
            Assert.False(second.Vector.DidWork);
            Assert.True(second.Vector.Pending);
            Assert.False(second.DidWork);
            Assert.Equal(31, signal.TargetRevision);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConvergeStore_WithoutResidentDrainReportsLeaderRequirementSeparatelyFromFailure()
    {
        using var session = new FakeStoreSession();
        var converger = NewStoreConverger((_, _) => false);
        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-leader-required-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            StoreSidecarConvergenceResult result = converger.ConvergeStore(root, session);

            Assert.Equal("leader_required", result.Vector.Status);
            Assert.True(result.Vector.Pending);
            Assert.True(result.LeaderRequired);
            Assert.Null(result.FailureReason);
            Assert.Contains("resident leader", result.WarningText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConvergeStore_RecordsStablePhasesAndTruthfulNoOpWork()
    {
        var phases = new RecordingPhaseSink();
        var signal = new VectorConvergeSignal(enabled: true);
        int contentRuns = 0;
        int searchRuns = 0;
        using var fixture = JulieDbFixture.CreateDefault();
        using var session = new FixtureStoreSession(fixture);
        var converger = new IndexerSidecarConverger(
            searchEnabled: true,
            (_, _, _, _) => false,
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            static path => path,
            static path => path,
            (_, _, _) => false,
            NullLogger.Instance,
            signal,
            ensureStoreContent: (_, _) => Detail(++contentRuns == 1),
            ensureStoreSearch: (_, _) => Detail(++searchRuns == 1),
            phaseSink: phases);

        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-phases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            converger.ConvergeStore(root, session);
            converger.ConvergeStore(root, session);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Assert.Equal(
            ["content", "search", "metrics", "vector", "sidecar_total", "content", "search", "metrics", "vector", "sidecar_total"],
            phases.Records.Select(static phase => phase.Phase));
        Assert.All(phases.Records, static phase => Assert.True(phase.ElapsedMilliseconds >= 0));
        Assert.Equal([true, true, true, false, true], phases.Records.Take(5).Select(static phase => phase.DidWork));
        Assert.Equal([false, false, false, false, false], phases.Records.Skip(5).Select(static phase => phase.DidWork));
        Assert.All(phases.Records, static phase =>
            Assert.Equal(
                31,
                phase.StoreSequence));
    }

    [Fact]
    public void ConvergeStore_RecordsContainedSidecarFailure()
    {
        var phases = new RecordingPhaseSink();
        var details = new RecordingDetailLogger();
        using var fixture = JulieDbFixture.CreateDefault();
        using var session = new FixtureStoreSession(fixture);
        var converger = new IndexerSidecarConverger(
            searchEnabled: true,
            (_, _, _, _) => false,
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            static path => path,
            static path => path,
            (_, _, _) => false,
            details,
            new VectorConvergeSignal(enabled: true),
            ensureStoreContent: (_, _) => throw new TimeoutException("sidecar lease timeout"),
            ensureStoreSearch: (_, _) => Detail(true),
            phaseSink: phases);

        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-phase-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            converger.ConvergeStore(root, session);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        IndexerPhaseRecord content = Assert.Single(phases.Records, static phase => phase.Phase == "content");
        Assert.Equal("failed", content.Outcome);
        Assert.False(content.DidWork);
        Assert.Equal("completed", Assert.Single(phases.Records, static phase => phase.Phase == "search").Outcome);
        Assert.Equal("completed", Assert.Single(phases.Records, static phase => phase.Phase == "sidecar_total").Outcome);
        Assert.DoesNotContain(details.Details, static entry => entry.Kind == "content");
        Assert.Single(details.Details, static entry => entry.Kind == "search");
    }

    [Fact]
    public void ConvergeStore_RunsSidecarsBeforeRejectingMissingStoreSequence()
    {
        var calls = new List<string>();
        var phases = new RecordingPhaseSink();
        using var session = new FakeStoreSession(storeLogSequence: null);
        var converger = new IndexerSidecarConverger(
            searchEnabled: true,
            (_, _, _, _) => false,
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            static path => path,
            static path => path,
            (_, _, _) => false,
            NullLogger.Instance,
            new VectorConvergeSignal(enabled: true),
            ensureStoreContent: (_, _) => { calls.Add("content"); return Detail(true); },
            ensureStoreSearch: (_, _) => { calls.Add("search"); return Detail(true); },
            phaseSink: phases);

        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-missing-sequence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<InvalidOperationException>(() => converger.ConvergeStore(root, session));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Assert.Empty(calls);
        Assert.Equal(["sidecar_total"], phases.Records.Select(static phase => phase.Phase));
        Assert.All(phases.Records, static phase => Assert.Null(phase.StoreSequence));
        Assert.Equal("failed", phases.Records[^1].Outcome);
    }

    [Fact]
    public void ConvergeStore_LeaseFailureReturnsFailedEvidenceForEnabledSidecars()
    {
        using var session = new FakeStoreSession();
        var signal = new VectorConvergeSignal(enabled: true);
        var converger = new IndexerSidecarConverger(
            searchEnabled: true,
            (_, _, _, _) => false,
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            static path => path,
            static path => path,
            (_, _, _) => false,
            NullLogger.Instance,
            signal,
            ensureStoreContent: (_, _) => Detail(true),
            ensureStoreSearch: (_, _) => Detail(true));

        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-lease-failure-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(root, "not a directory");
        try
        {
            StoreSidecarConvergenceResult result = converger.ConvergeStore(root, session);

            Assert.Equal("failed", result.Content.Status);
            Assert.Equal("failed", result.Search.Status);
            Assert.NotNull(result.Content.FailureReason);
            Assert.NotNull(result.Search.FailureReason);
            Assert.NotNull(result.FailureReason);
            Assert.Contains("sidecar", result.WarningText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(root);
        }
    }

    [Fact]
    public void ConvergeStore_WhenPhaseSinkThrows_PreservesSidecarConvergence()
    {
        using var session = new FakeStoreSession();
        var signal = new VectorConvergeSignal(enabled: true);
        int contentCalls = 0;
        var converger = new IndexerSidecarConverger(
            searchEnabled: false,
            (_, _, _, _) => false,
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            static path => path,
            static path => path,
            (_, _, _) => false,
            NullLogger.Instance,
            signal,
            ensureStoreContent: (_, _) => { contentCalls++; return Detail(true); },
            phaseSink: new ThrowingPhaseSink());

        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-sink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            converger.ConvergeStore(root, session);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Assert.Equal(1, contentCalls);
        Assert.Equal(0, signal.TargetRevision);
    }

    [Fact]
    public void ConvergeStore_Records_full_missing_then_current_from_the_actual_content_sidecar()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);
        var logger = new RecordingDetailLogger();
        IndexerSidecarConverger converger = NewRealStoreConverger(logger);

        StoreSidecarConvergenceResult first = converger.ConvergeStore(fixture.Binding.StoreRoot, session);
        StoreSidecarConvergenceResult second = converger.ConvergeStore(fixture.Binding.StoreRoot, session);

        Assert.Equal("repaired", first.Content.Status);
        Assert.True(first.Content.DidWork);
        Assert.Equal("current", second.Content.Status);
        Assert.False(second.Content.DidWork);
        Assert.Equal(
            [
                new SidecarConvergenceDetail(SidecarConvergencePath.Full, SidecarConvergenceReason.DeltaMissing, true),
                new SidecarConvergenceDetail(SidecarConvergencePath.Current, SidecarConvergenceReason.None, false),
            ],
            logger.Details.Select(static entry => entry.Detail));
        Assert.All(logger.Details, static entry => Assert.Equal(LogLevel.Information, entry.Level));
    }

    [Fact]
    public void ConvergeStore_Records_empty_delta_from_the_actual_content_sidecar()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var logger = new RecordingDetailLogger();
        IndexerSidecarConverger converger = NewRealStoreConverger(logger);
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            converger.ConvergeStore(fixture.Binding.StoreRoot, initial);
        AppendReusedManifestImport(fixture);
        logger.Details.Clear();

        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        StoreSidecarConvergenceResult result = converger.ConvergeStore(fixture.Binding.StoreRoot, updated);

        Assert.True(result.Content.DidWork);
        Assert.Equal(
            new SidecarConvergenceDetail(SidecarConvergencePath.EmptyDelta, SidecarConvergenceReason.None, true),
            Assert.Single(logger.Details).Detail);
    }

    [Fact]
    public void ConvergeStore_Records_incremental_from_the_actual_content_sidecar()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var logger = new RecordingDetailLogger();
        IndexerSidecarConverger converger = NewRealStoreConverger(logger);
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            converger.ConvergeStore(fixture.Binding.StoreRoot, initial);
        AppendAddedFileManifest(fixture);
        logger.Details.Clear();

        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        StoreSidecarConvergenceResult result = converger.ConvergeStore(fixture.Binding.StoreRoot, updated);

        Assert.True(result.Content.DidWork);
        Assert.Equal(
            new SidecarConvergenceDetail(SidecarConvergencePath.Incremental, SidecarConvergenceReason.None, true),
            Assert.Single(logger.Details).Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConvergeStore_Records_identity_or_stamp_full_fallback_from_the_actual_content_sidecar(bool identityChanged)
    {
        using StoreFixture fixture = StoreFixture.Create();
        var logger = new RecordingDetailLogger();
        IndexerSidecarConverger converger = NewRealStoreConverger(logger);
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);
        converger.ConvergeStore(fixture.Binding.StoreRoot, session);
        string databasePath = StoreSidecarCatalog.PathFor(
            fixture.Binding.StoreRoot,
            StoreSidecarKind.Content,
            fixture.Binding.ViewId);
        StoreSidecarStamp stamp = StoreSidecarCatalog.TryRead(databasePath)!;
        StoreSidecarCatalog.Stamp(
            databasePath,
            identityChanged
                ? stamp with { GenerationName = "gen-other" }
                : stamp with { ManifestHash = "manifest-other" });
        logger.Details.Clear();

        StoreSidecarConvergenceResult result = converger.ConvergeStore(fixture.Binding.StoreRoot, session);

        Assert.Equal("repaired", result.Content.Status);
        Assert.True(result.Content.DidWork);
        Assert.Equal(
            new SidecarConvergenceDetail(
                SidecarConvergencePath.Full,
                identityChanged ? SidecarConvergenceReason.IdentityChanged : SidecarConvergenceReason.StampMismatch,
                true),
            Assert.Single(logger.Details).Detail);
    }

    [Fact]
    public void ConvergeStore_Records_incomplete_delta_full_fallback_from_the_actual_content_sidecar()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var logger = new RecordingDetailLogger();
        IndexerSidecarConverger converger = NewRealStoreConverger(logger);
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            converger.ConvergeStore(fixture.Binding.StoreRoot, initial);
        AppendReusedManifestImport(fixture);
        ExecuteStore(fixture, "DELETE FROM store_log WHERE sequence <= 2;");
        logger.Details.Clear();

        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        StoreSidecarConvergenceResult result = converger.ConvergeStore(fixture.Binding.StoreRoot, updated);

        Assert.Equal("repaired", result.Content.Status);
        Assert.True(result.Content.DidWork);
        Assert.Equal(
            new SidecarConvergenceDetail(SidecarConvergencePath.Full, SidecarConvergenceReason.DeltaIncomplete, true),
            Assert.Single(logger.Details).Detail);
    }

    [Fact]
    public void ConvergeStore_Records_apply_failed_full_fallback_from_the_actual_content_sidecar()
    {
        using StoreFixture fixture = StoreFixture.Create();
        var logger = new RecordingDetailLogger();
        IndexerSidecarConverger converger = NewRealStoreConverger(logger, searchEnabled: true);
        using (FamilyStoreReadSession initial = FamilyStoreReadSession.Open(fixture.Binding))
            converger.ConvergeStore(fixture.Binding.StoreRoot, initial);
        AppendAddedFileManifest(fixture);
        string databasePath = StoreSidecarCatalog.PathFor(
            fixture.Binding.StoreRoot,
            StoreSidecarKind.Search,
            fixture.Binding.ViewId);
        Execute(databasePath, "DROP TABLE symbols_fts;");
        logger.Details.Clear();

        using FamilyStoreReadSession updated = FamilyStoreReadSession.Open(fixture.Binding);
        StoreSidecarConvergenceResult result = converger.ConvergeStore(fixture.Binding.StoreRoot, updated);

        Assert.Equal("repaired", result.Search.Status);
        Assert.True(result.Search.DidWork);
        Assert.Equal(
            new SidecarConvergenceDetail(SidecarConvergencePath.Full, SidecarConvergenceReason.ApplyFailed, true),
            Assert.Single(logger.Details, static entry => entry.Kind == "search").Detail);
    }

    [Fact]
    public void ConvergeStore_Contains_detail_recorder_failure_without_changing_public_success()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);
        IndexerSidecarConverger converger = NewRealStoreConverger(new ThrowingLogger());

        StoreSidecarConvergenceResult result = converger.ConvergeStore(fixture.Binding.StoreRoot, session);

        Assert.Equal("repaired", result.Content.Status);
        Assert.True(result.Content.DidWork);
    }

    [Fact]
    public async Task ConvergeStore_SerializesFamilySidecarWork()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-store-converger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var firstSession = new FakeStoreSession();
            using var secondSession = new FakeStoreSession();
            using var firstEntered = new ManualResetEventSlim();
            using var secondEntered = new ManualResetEventSlim();
            using var releaseFirst = new ManualResetEventSlim();
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            int calls = 0;
            int active = 0;
            int maximumActive = 0;
            var converger = NewStoreConverger((_, _) =>
            {
                int current = Interlocked.Increment(ref active);
                while (true)
                {
                    int observed = Volatile.Read(ref maximumActive);
                    if (observed >= current || Interlocked.CompareExchange(ref maximumActive, current, observed) == observed)
                        break;
                }

                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(5), cancellationToken);
                }
                else
                {
                    secondEntered.Set();
                }

                Interlocked.Decrement(ref active);
                return true;
            });

            Task first = Task.Run(() => converger.ConvergeStore(root, firstSession), cancellationToken);
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));
            Task second = Task.Run(() => converger.ConvergeStore(root, secondSession), cancellationToken);
            Assert.False(secondEntered.Wait(TimeSpan.FromMilliseconds(100), cancellationToken));
            releaseFirst.Set();
            await Task.WhenAll(first, second);

            Assert.Equal(1, maximumActive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IndexerSidecarConverger NewStoreConverger(
        Func<string, IWorkspaceReadSession, bool> ensureStoreContent) =>
        new(
            searchEnabled: false,
            (_, _, _, _) => false,
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            (string _, long _, string _, out string? reason) => { reason = null; return false; },
            static path => path,
            static path => path,
            (_, _, _) => false,
            NullLogger.Instance,
            new VectorConvergeSignal(enabled: true),
            (root, session) => Detail(ensureStoreContent(root, session)),
            ensureStoreSearch: null);

    private static SidecarConvergenceDetail Detail(bool didWork) =>
        didWork
            ? new(SidecarConvergencePath.Full, SidecarConvergenceReason.None, true)
            : new(SidecarConvergencePath.Current, SidecarConvergenceReason.None, false);

    private static IndexerSidecarConverger NewRealStoreConverger(ILogger logger, bool searchEnabled = false) =>
        new(
            new SymbolSearchSidecar(searchEnabled, RegionIndexOptions.Disabled),
            new ContentCorpusSidecar(),
            logger,
            new VectorConvergeSignal(enabled: false));

    private static void AppendReusedManifestImport(StoreFixture fixture) =>
        ExecuteStore(
            fixture,
            """
            INSERT INTO store_log VALUES
              (3,'request-reuse','store_import_l3_chunk','view-a',2,2,3,0,'{}','2026-08-09T00:00:03Z'),
              (4,'request-reuse','store_import_completed','view-a',2,NULL,3,1,
               '{"manifest":{"disposition":"reused"}}','2026-08-09T00:00:04Z'),
              (5,'request-reuse','store_resolve_completed','view-a',2,NULL,3,1,
               '{}','2026-08-09T00:00:05Z');
            """);

    private static void AppendAddedFileManifest(StoreFixture fixture) =>
        ExecuteStore(
            fixture,
            """
            INSERT INTO file_versions VALUES
              (3,'added.cs','blake3:added',1,'csharp',12,1,NULL,1,2,3);
            INSERT INTO manifests VALUES
              ('view-a',3,'manifest-added','request-added','2026-08-09T00:00:02Z');
            INSERT INTO manifest_entries VALUES
              ('view-a',3,'same.cs','csharp',2,'indexed','blake3:visible','2026-08-09T00:00:02Z',NULL,NULL),
              ('view-a',3,'added.cs','csharp',3,'indexed','blake3:added','2026-08-09T00:00:02Z',NULL,NULL);
            INSERT INTO symbols VALUES
              (3,'added-symbol','added.cs','csharp','Added','class',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL);
            UPDATE views
            SET current_generation=3,
                updated_at='2026-08-09T00:00:02Z'
            WHERE view_id='view-a';
            INSERT INTO store_log VALUES
              (3,'request-added','manifest_flipped','view-a',3,NULL,NULL,1,'{}','2026-08-09T00:00:02Z');
            """);

    private static void ExecuteStore(StoreFixture fixture, string sql) =>
        Execute(Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db"), sql);

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double ScalarDouble(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToDouble(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long SnapshotCount(string path) => ScalarLong(path, "SELECT COUNT(*) FROM snapshots;");

    private sealed class RecordingPhaseSink : IIndexerPhaseSink
    {
        public List<IndexerPhaseRecord> Records { get; } = [];

        public void Record(IndexerPhaseRecord record) => Records.Add(record);
    }

    private sealed class ThrowingPhaseSink : IIndexerPhaseSink
    {
        public void Record(IndexerPhaseRecord record) => throw new InvalidOperationException("sink failed");
    }

    private sealed class RecordingDetailLogger : ILogger
    {
        public List<(LogLevel Level, string Kind, SidecarConvergenceDetail Detail)> Details { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                return;
            string? kind = values.FirstOrDefault(static pair => pair.Key == "SidecarKind").Value as string;
            object? path = values.FirstOrDefault(static pair => pair.Key == "ConvergencePath").Value;
            object? reason = values.FirstOrDefault(static pair => pair.Key == "ConvergenceReason").Value;
            object? didWork = values.FirstOrDefault(static pair => pair.Key == "DidWork").Value;
            if (kind is not null
                && path is SidecarConvergencePath convergencePath
                && reason is SidecarConvergenceReason convergenceReason
                && didWork is bool changed)
            {
                Details.Add((logLevel, kind, new SidecarConvergenceDetail(convergencePath, convergenceReason, changed)));
            }
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger failed");
    }

    private sealed class FixtureStoreSession : IWorkspaceReadSession
    {
        private readonly string _dbPath;

        public FixtureStoreSession(JulieDbFixture fixture, long? storeLogSequence = 31)
        {
            _dbPath = fixture.DbPath;
            Snapshot = new WorkspaceReadSnapshot(
                fixture.WorkspaceRoot,
                "workspace-a",
                "family-a",
                "view-a",
                new WorkspaceFreshnessToken(
                    "family-a",
                    2,
                    "manifest-a",
                    storeLogSequence,
                    "resolution-a",
                    StoreInstanceId: "family-a:gen-001",
                    ViewId: "view-a",
                    GenerationName: "gen-001",
                    ManifestGeneration: 2,
                    IndexLevel: "full",
                    LevelStampL1: "l1-a",
                    LevelStampL2: "l2-a",
                    LevelStampL3: "l3-a"),
                "full",
                WorkspaceReadMode.FamilyStore,
                GenerationName: "gen-001",
                ManifestGeneration: 2);
        }

        public WorkspaceReadSnapshot Snapshot { get; }

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            return query(connection);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeStoreSession : IWorkspaceReadSession
    {
        public FakeStoreSession(long? storeLogSequence = 31)
        {
            Snapshot = new WorkspaceReadSnapshot(
                "/workspace",
                "workspace-a",
                "family-a",
                "view-a",
                new WorkspaceFreshnessToken(
                    "family-a",
                    2,
                    "manifest-a",
                    storeLogSequence,
                    "resolution-a",
                    StoreInstanceId: "family-a:gen-001",
                    ViewId: "view-a",
                    GenerationName: "gen-001",
                    ManifestGeneration: 2,
                    IndexLevel: "full",
                    LevelStampL1: "l1-a",
                    LevelStampL2: "l2-a",
                    LevelStampL3: "l3-a"),
                "full",
                WorkspaceReadMode.FamilyStore,
                GenerationName: "gen-001",
                ManifestGeneration: 2);
        }

        public WorkspaceReadSnapshot Snapshot { get; }

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
