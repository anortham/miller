using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Generation-tag naming, promote ordering, and the GC never-delete rules of vectors-v1 §Shadow generations and
/// rollback. Pure decision logic plus an in-memory file seam, so the whole lifecycle is guarded by the fast
/// suite without sqlite-vec.
/// </summary>
public sealed class VectorGenerationManagerTests
{
    private const string ActiveTag = "aaaaaaaaaaaaaaaa";
    private const string ShadowTag = "bbbbbbbbbbbbbbbb";

    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RetainedPath_UsesTheContractFileName()
    {
        (VectorGenerationManager manager, _) = Manager();

        Assert.Equal(
            Path.Combine("/ws", ".miller", $"vectors.gen-{ActiveTag}.db"),
            manager.RetainedPathFor(ActiveTag));
    }

    [Fact]
    public void ShadowPath_IsTheRebuildSiblingOfTheActiveArtifact()
    {
        (VectorGenerationManager manager, _) = Manager();

        Assert.Equal(manager.ActivePath + ".rebuild", manager.ShadowPath);
    }

    [Fact]
    public void TagFromRetainedPath_RoundTripsTheRetainedFileName()
    {
        (VectorGenerationManager manager, _) = Manager();

        Assert.Equal(ActiveTag, VectorGenerationManager.TagFromRetainedPath(manager.RetainedPathFor(ActiveTag)));
        Assert.Null(VectorGenerationManager.TagFromRetainedPath(manager.ActivePath));
        Assert.Null(VectorGenerationManager.TagFromRetainedPath(manager.ShadowPath));
    }

    [Fact]
    public void GenerationTag_CoversOnlyTheTwoFieldsThatGateReadability()
    {
        SemanticGenerationIdentity pinned =
            MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder);
        SemanticGenerationIdentity readerGateOnly = pinned with
        {
            CorpusGeneration = "cards-v2-chunks-v1",
            WriterVersion = "9.9.9",
            MinReaderVersion = "9.9.9",
            FusionProfile = "fusion-v9",
        };

        Assert.Equal(
            VectorPromoteKind.Compatible,
            VectorGenerationManager.ClassifyPromote(pinned, readerGateOnly));
    }

    [Fact]
    public void ClassifyPromote_TagChange_IsIncompatible()
    {
        SemanticGenerationIdentity pinned =
            MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder);
        SemanticGenerationIdentity otherEncoder =
            MillerSemanticContract.PinnedIdentity(MillerSemanticContract.FallbackEncoder);

        Assert.Equal(VectorPromoteKind.Incompatible, VectorGenerationManager.ClassifyPromote(pinned, otherEncoder));
    }

    [Fact]
    public void ClassifyPromote_NoActiveGeneration_IsCompatible()
    {
        Assert.Equal(VectorPromoteKind.Compatible, VectorGenerationManager.ClassifyPromote(null, ShadowTag));
    }

    [Fact]
    public void PrepareShadow_DeletesTheStaleRebuildTrio()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ShadowPath, "stale");
        files.Write(manager.ShadowPath + "-wal", "stale");
        files.Write(manager.ShadowPath + "-shm", "stale");

        manager.PrepareShadow();

        Assert.False(files.Exists(manager.ShadowPath));
        Assert.False(files.Exists(manager.ShadowPath + "-wal"));
        Assert.False(files.Exists(manager.ShadowPath + "-shm"));
    }

    [Fact]
    public void Promote_Incompatible_RetainsTheSupersededGenerationUnderItsOwnTag()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ActivePath, "old-generation");
        files.Write(manager.ShadowPath, "new-generation");

        VectorPromoteResult result = manager.Promote(ShadowTag, ActiveTag);

        Assert.Equal(VectorPromoteKind.Incompatible, result.Kind);
        Assert.Equal(manager.RetainedPathFor(ActiveTag), result.RetainedPath);
        Assert.Equal("old-generation", files.Read(manager.RetainedPathFor(ActiveTag)));
        Assert.Equal("new-generation", files.Read(manager.ActivePath));
        Assert.False(files.Exists(manager.ShadowPath));
    }

    [Fact]
    public void Promote_Incompatible_StampsRetentionTimeSoAnIdleWorkspaceKeepsItsRollbackGeneration()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        DateTimeOffset promotedAt = Now;
        DateTimeOffset staleActiveMtime = Now.AddDays(-30);
        files.Write(manager.ActivePath, "old-generation");
        files.SetLastWriteTime(manager.ActivePath, staleActiveMtime);
        files.Write(manager.ShadowPath, "new-generation");
        files.TouchTime = promotedAt;

        manager.Promote(ShadowTag, ActiveTag);

        Assert.Contains(manager.RetainedPathFor(ActiveTag), files.Touched);

        RetainedGeneration retained = Assert.Single(manager.Retained());
        Assert.Equal(promotedAt, retained.RetainedAt);

        VectorGcPlan plan = VectorGenerationManager.PlanGarbageCollection(
            Inputs(activeIsReady: true) with { Retained = manager.Retained(), Now = promotedAt });

        Assert.Empty(plan.Deletions);
        Assert.Equal(VectorGcOutcome.WithinSoakWindow, Assert.Single(plan.Decisions).Outcome);
    }

    [Fact]
    public void Promote_Compatible_OverwritesTheActiveArtifactAndRetainsNothing()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ActivePath, "old-generation");
        files.Write(manager.ShadowPath, "new-generation");

        VectorPromoteResult result = manager.Promote(ShadowTag, ShadowTag);

        Assert.Equal(VectorPromoteKind.Compatible, result.Kind);
        Assert.Null(result.RetainedPath);
        Assert.Equal("new-generation", files.Read(manager.ActivePath));
        Assert.Empty(files.EnumerateRetained(manager.MillerDir));
    }

    [Fact]
    public void Promote_RenamedFilesAreSelfContained()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ActivePath, "old-generation");
        files.Write(manager.ActivePath + "-wal", "active wal");
        files.Write(manager.ActivePath + "-shm", "active shm");
        files.Write(manager.ShadowPath, "new-generation");
        files.Write(manager.ShadowPath + "-wal", "shadow wal");
        files.Write(manager.ShadowPath + "-shm", "shadow shm");

        manager.Promote(ShadowTag, ActiveTag);

        Assert.Contains(manager.ShadowPath, files.Folded);
        Assert.Contains(manager.ActivePath, files.Folded);
        Assert.False(files.Exists(manager.RetainedPathFor(ActiveTag) + "-wal"));
        Assert.False(files.Exists(manager.RetainedPathFor(ActiveTag) + "-shm"));
        Assert.False(files.Exists(manager.ActivePath + "-wal"));
        Assert.False(files.Exists(manager.ActivePath + "-shm"));
        Assert.False(files.Exists(manager.ShadowPath + "-wal"));
        Assert.False(files.Exists(manager.ShadowPath + "-shm"));
    }

    [Fact]
    public void Promote_ATagReturningAfterARevert_ReplacesTheExistingRetainedFile()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.RetainedPathFor(ActiveTag), "previous retention");
        files.Write(manager.ActivePath, "old-generation");
        files.Write(manager.ShadowPath, "new-generation");

        manager.Promote(ShadowTag, ActiveTag);

        Assert.Equal("old-generation", files.Read(manager.RetainedPathFor(ActiveTag)));
        Assert.Single(files.EnumerateRetained(manager.MillerDir));
    }

    [Fact]
    public void Promote_RetainFailure_LeavesTheActiveArtifactAndTheShadowUntouched()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ActivePath, "old-generation");
        files.Write(manager.ShadowPath, "new-generation");
        files.FailMoveTo = manager.RetainedPathFor(ActiveTag);

        Assert.Throws<IOException>(() => manager.Promote(ShadowTag, ActiveTag));

        Assert.Equal("old-generation", files.Read(manager.ActivePath));
        Assert.Equal("new-generation", files.Read(manager.ShadowPath));
    }

    [Fact]
    public void Promote_WithoutAShadow_Refuses()
    {
        (VectorGenerationManager manager, _) = Manager();

        Assert.Throws<InvalidOperationException>(() => manager.Promote(ShadowTag, ActiveTag));
    }

    [Fact]
    public void Promote_NoActiveArtifact_PromotesWithoutRetaining()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ShadowPath, "new-generation");

        VectorPromoteResult result = manager.Promote(ShadowTag, activeTag: null);

        Assert.Null(result.RetainedPath);
        Assert.Equal("new-generation", files.Read(manager.ActivePath));
    }

    [Fact]
    public void Gc_NeverDeletesTheOnlyReadyGeneration()
    {
        VectorGcPlan plan = VectorGenerationManager.PlanGarbageCollection(Inputs(
            activeIsReady: false,
            Retained(ActiveTag, Now.AddDays(-30))));

        Assert.Empty(plan.Deletions);
        Assert.Equal(VectorGcOutcome.OnlyReadyGeneration, Assert.Single(plan.Decisions).Outcome);
    }

    [Fact]
    public void Gc_NeverDeletesAGenerationInsideItsSoakWindow()
    {
        VectorGcPlan plan = VectorGenerationManager.PlanGarbageCollection(Inputs(
            activeIsReady: true,
            Retained(ActiveTag, Now.AddHours(-1))));

        Assert.Empty(plan.Deletions);
        Assert.Equal(VectorGcOutcome.WithinSoakWindow, Assert.Single(plan.Decisions).Outcome);
    }

    [Fact]
    public void Gc_NeverDeletesAGenerationWithAKnownLiveCompatibleReader()
    {
        VectorGcInputs inputs = Inputs(activeIsReady: true, Retained(ActiveTag, Now.AddDays(-30))) with
        {
            TagsWithLiveReaders = new HashSet<string>(StringComparer.Ordinal) { ActiveTag },
        };

        VectorGcPlan plan = VectorGenerationManager.PlanGarbageCollection(inputs);

        Assert.Empty(plan.Deletions);
        Assert.Equal(VectorGcOutcome.LiveReader, Assert.Single(plan.Decisions).Outcome);
    }

    [Fact]
    public void Gc_DeletesAnEligibleGenerationPastItsSoakWindow()
    {
        VectorGcPlan plan = VectorGenerationManager.PlanGarbageCollection(Inputs(
            activeIsReady: true,
            Retained(ActiveTag, Now.AddDays(-30))));

        Assert.Equal(ActiveTag, Assert.Single(plan.Deletions).Tag);
    }

    [Fact]
    public void Gc_OrdersEligibleDeletionsOldestMtimeFirst()
    {
        VectorGcPlan plan = VectorGenerationManager.PlanGarbageCollection(Inputs(
            activeIsReady: true,
            Retained("1111111111111111", Now.AddDays(-2)),
            Retained("2222222222222222", Now.AddDays(-9)),
            Retained("3333333333333333", Now.AddDays(-5))));

        Assert.Equal(
            ["2222222222222222", "3333333333333333", "1111111111111111"],
            plan.Deletions.Select(static generation => generation.Tag));
    }

    [Fact]
    public void Gc_ReportsOverRetentionCapWithoutOverridingAProtection()
    {
        VectorGcInputs inputs = Inputs(
            activeIsReady: true,
            Retained("1111111111111111", Now.AddMinutes(-1)),
            Retained("2222222222222222", Now.AddMinutes(-2)),
            Retained("3333333333333333", Now.AddMinutes(-3))) with
        {
            RetentionCap = 2,
        };

        VectorGcPlan plan = VectorGenerationManager.PlanGarbageCollection(inputs);

        Assert.Empty(plan.Deletions);
        Assert.True(plan.OverRetentionCap);
    }

    [Fact]
    public void Gc_NeverTargetsTheActiveArtifact()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ActivePath, "active");
        files.Write(manager.RetainedPathFor(ActiveTag), "retained");
        files.SetLastWriteTime(manager.RetainedPathFor(ActiveTag), Now.AddDays(-30));

        manager.CollectGarbage(Inputs(activeIsReady: true) with { Retained = manager.Retained() });

        Assert.True(files.Exists(manager.ActivePath));
        Assert.False(files.Exists(manager.RetainedPathFor(ActiveTag)));
    }

    [Fact]
    public void CollectGarbage_AlsoReclaimsAStaleShadowTrio()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ActivePath, "active");
        files.Write(manager.ShadowPath, "stale shadow");
        files.Write(manager.ShadowPath + "-wal", "stale");

        manager.CollectGarbage(Inputs(activeIsReady: true));

        Assert.False(files.Exists(manager.ShadowPath));
        Assert.False(files.Exists(manager.ShadowPath + "-wal"));
        Assert.True(files.Exists(manager.ActivePath));
    }

    [Fact]
    public void Retained_ReadsTheTagAndRetentionTimeFromTheSiblingFiles()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.RetainedPathFor(ActiveTag), "retained");
        files.SetLastWriteTime(manager.RetainedPathFor(ActiveTag), Now.AddDays(-3));

        RetainedGeneration only = Assert.Single(manager.Retained());

        Assert.Equal(ActiveTag, only.Tag);
        Assert.Equal(Now.AddDays(-3), only.RetainedAt);
    }

    [Fact]
    public void BuildState_StaysBuildingUntilTheSymbolCursorCatchesUp()
    {
        VectorBuildStateUpdate update =
            VectorGenerationManager.EvaluateBuildState(new VectorBuildProgress(30, 100, "building"));

        Assert.Equal("building", update.BuildState);
        Assert.Equal(30, update.ProgressPercent);
    }

    [Fact]
    public void BuildState_FlipsToReadyWhenTheSymbolCursorReachesItsTarget()
    {
        VectorBuildStateUpdate update =
            VectorGenerationManager.EvaluateBuildState(new VectorBuildProgress(100, 100, "building"));

        Assert.Equal("ready", update.BuildState);
        Assert.Equal(100, update.ProgressPercent);
    }

    [Fact]
    public void BuildState_AnUnstartedCursorIsNeverReady()
    {
        VectorBuildStateUpdate update =
            VectorGenerationManager.EvaluateBuildState(new VectorBuildProgress(0, 0, "building"));

        Assert.Equal("building", update.BuildState);
        Assert.Equal(0, update.ProgressPercent);
    }

    [Fact]
    public void BuildState_NeverRegressesOnceTheGenerationIsQueryable()
    {
        VectorBuildStateUpdate update =
            VectorGenerationManager.EvaluateBuildState(new VectorBuildProgress(100, 140, "ready"));

        Assert.Equal("ready", update.BuildState);
        Assert.Equal(100, update.ProgressPercent);
    }

    [Fact]
    public void GcScheduler_DeletesAnEligibleGenerationPastSoakWithNoLiveReader()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ActivePath, "active");
        files.Write(manager.RetainedPathFor(ActiveTag), "retained");
        files.SetLastWriteTime(manager.RetainedPathFor(ActiveTag), Now.AddDays(-30));

        Gc(manager).Collect(activeIsReady: true, Now, NoLiveReaders);

        Assert.False(files.Exists(manager.RetainedPathFor(ActiveTag)));
        Assert.True(files.Exists(manager.ActivePath));
    }

    [Fact]
    public void GcScheduler_KeepsAGenerationInsideItsSoakWindow()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ActivePath, "active");
        files.Write(manager.RetainedPathFor(ActiveTag), "retained");
        files.SetLastWriteTime(manager.RetainedPathFor(ActiveTag), Now.AddHours(-1));

        Gc(manager).Collect(activeIsReady: true, Now, NoLiveReaders);

        Assert.True(files.Exists(manager.RetainedPathFor(ActiveTag)));
    }

    [Fact]
    public void GcScheduler_KeepsEveryGenerationWhenTheActiveArtifactIsNotReady()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.RetainedPathFor(ActiveTag), "retained");
        files.SetLastWriteTime(manager.RetainedPathFor(ActiveTag), Now.AddDays(-30));

        Gc(manager).Collect(activeIsReady: false, Now, NoLiveReaders);

        Assert.True(files.Exists(manager.RetainedPathFor(ActiveTag)));
    }

    [Fact]
    public void GcScheduler_ALiveReaderBlocksDeletion_AndDisposalLetsTheNextPassCollectIt()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        var registry = new VectorLiveReaderRegistry();
        files.Write(manager.ActivePath, "active");
        files.Write(manager.RetainedPathFor(ActiveTag), "retained");
        files.SetLastWriteTime(manager.RetainedPathFor(ActiveTag), Now.AddDays(-30));

        IDisposable reader = registry.Register(ActiveTag);
        Gc(manager).Collect(activeIsReady: true, Now, registry.LiveTags);
        Assert.True(files.Exists(manager.RetainedPathFor(ActiveTag)));

        reader.Dispose();
        Gc(manager).Collect(activeIsReady: true, Now, registry.LiveTags);
        Assert.False(files.Exists(manager.RetainedPathFor(ActiveTag)));
    }

    [Fact]
    public void GcScheduler_ADeletionThatThrows_IsSwallowedAndRetriedOnTheNextPass()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ActivePath, "active");
        files.Write(manager.RetainedPathFor(ActiveTag), "retained");
        files.SetLastWriteTime(manager.RetainedPathFor(ActiveTag), Now.AddDays(-30));
        files.FailDeleteOf = manager.RetainedPathFor(ActiveTag);

        Gc(manager).Collect(activeIsReady: true, Now, NoLiveReaders);
        Assert.True(files.Exists(manager.RetainedPathFor(ActiveTag)));

        files.FailDeleteOf = null;
        Gc(manager).Collect(activeIsReady: true, Now, NoLiveReaders);
        Assert.False(files.Exists(manager.RetainedPathFor(ActiveTag)));
    }

    private static readonly IReadOnlySet<string> NoLiveReaders = new HashSet<string>(StringComparer.Ordinal);

    private static VectorGenerationGc Gc(VectorGenerationManager manager) =>
        new(manager, NullLogger.Instance);

    private static (VectorGenerationManager Manager, FakeGenerationFiles Files) Manager()
    {
        var files = new FakeGenerationFiles();
        return (new VectorGenerationManager("/ws", files), files);
    }

    private static RetainedGeneration Retained(string tag, DateTimeOffset retainedAt) =>
        new(tag, Path.Combine("/ws", ".miller", $"vectors.gen-{tag}.db"), retainedAt);

    private static VectorGcInputs Inputs(bool activeIsReady, params RetainedGeneration[] retained) =>
        new()
        {
            Retained = retained,
            ActiveIsReady = activeIsReady,
            Now = Now,
        };

    [Fact]
    public void PrepareShadow_AfterPromoteDiedBetweenItsRenames_AdoptsTheReadyShadowInsteadOfDiscardingIt()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ShadowPath, "promoted-generation");
        files.BuildStates[manager.ShadowPath] = "ready";
        files.Write(manager.RetainedPathFor("old"), "superseded");

        manager.PrepareShadow();

        Assert.True(files.Exists(manager.ActivePath));
        Assert.Equal("promoted-generation", files.Read(manager.ActivePath));
        Assert.False(files.Exists(manager.ShadowPath));
    }

    [Fact]
    public void PrepareShadow_ShadowFromAnInterruptedBuildRatherThanAnInterruptedPromote_IsStillDiscarded()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ShadowPath, "half-embedded");
        files.BuildStates[manager.ShadowPath] = "building";

        manager.PrepareShadow();

        Assert.False(files.Exists(manager.ActivePath));
        Assert.False(files.Exists(manager.ShadowPath));
    }

    [Fact]
    public void PrepareShadow_ActiveGenerationPresent_LeavesItAloneAndDropsTheShadow()
    {
        (VectorGenerationManager manager, FakeGenerationFiles files) = Manager();
        files.Write(manager.ActivePath, "live");
        files.Write(manager.ShadowPath, "stale-shadow");
        files.BuildStates[manager.ShadowPath] = "ready";

        manager.PrepareShadow();

        Assert.Equal("live", files.Read(manager.ActivePath));
        Assert.False(files.Exists(manager.ShadowPath));
    }

    private sealed class FakeGenerationFiles : IVectorGenerationFiles
    {
        private readonly Dictionary<string, string> _contents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _times = new(StringComparer.Ordinal);

        public List<string> Folded { get; } = [];

        public List<string> Touched { get; } = [];

        public DateTimeOffset TouchTime { get; set; } = Now;

        public string? FailMoveTo { get; set; }

        public string? FailDeleteOf { get; set; }

        public void Write(string path, string content)
        {
            _contents[path] = content;
            _times[path] = Now;
        }

        public string Read(string path) => _contents[path];

        public void SetLastWriteTime(string path, DateTimeOffset time) => _times[path] = time;

        public bool Exists(string path) => _contents.ContainsKey(path);

        public void Delete(string path)
        {
            if (string.Equals(path, FailDeleteOf, StringComparison.Ordinal))
                throw new IOException($"cannot delete '{path}'.");

            _contents.Remove(path);
            _times.Remove(path);
        }

        public void Move(string source, string destination)
        {
            if (string.Equals(destination, FailMoveTo, StringComparison.Ordinal))
                throw new IOException($"cannot move onto '{destination}'.");

            _contents[destination] = _contents[source];
            _times[destination] = _times[source];
            Delete(source);
        }

        public void Touch(string path)
        {
            Touched.Add(path);
            _times[path] = TouchTime;
        }

        public DateTimeOffset LastWriteTime(string path) => _times.GetValueOrDefault(path);

        public IReadOnlyList<string> EnumerateRetained(string millerDir) =>
        [
            .. _contents.Keys
                .Where(path => VectorGenerationManager.TagFromRetainedPath(path) is not null)
                .Order(StringComparer.Ordinal),
        ];

        public void FoldWal(string path) => Folded.Add(path);

        public string? ReadBuildState(string path) => BuildStates.GetValueOrDefault(path);

        public Dictionary<string, string> BuildStates { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>
/// The rollback guarantee of vectors-v1 conformance clause 6 against the real pinned sqlite-vec extension: an
/// incompatible promote retains the superseded generation, and a reader whose encoder matches it serves from it
/// across a process restart. Scale-tagged because it loads the native loadable extension; it SKIPs rather than
/// fails when the extension has not been fetched. Serialized on the SqliteVecEnvironment collection because a
/// test there parks the packaged vec0 file for its duration — extension loads must not overlap that window.
/// </summary>
[Collection(SqliteVecEnvironment.Name)]
[Trait("Category", "Scale")]
public sealed class VectorGenerationManagerScaleTests : IDisposable
{
    private const string ArtifactId = "artifact-0001";

    private readonly string _root = Directory.CreateTempSubdirectory("miller-vector-generation-").FullName;

    private string MillerDir => Path.Combine(_root, ".miller");

    [Fact]
    public void IncompatiblePromote_LeavesTheOldGenerationQueryableByAnOldFingerprintReaderAcrossARestart()
    {
        string extension = SqliteVecTestSupport.RequireExtension();
        Directory.CreateDirectory(MillerDir);

        var manager = new VectorGenerationManager(_root);
        SemanticGenerationIdentity old =
            MillerSemanticContract.PinnedIdentity(MillerSemanticContract.FallbackEncoder);
        SemanticGenerationIdentity fresh =
            MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder);

        Build(manager.ActivePath, old, extension, "old-unit");
        manager.PrepareShadow();
        Build(manager.ShadowPath, fresh, extension, "new-unit");

        VectorPromoteResult result = manager.Promote(
            MillerSemanticContract.GenerationTag(fresh),
            MillerSemanticContract.GenerationTag(old));

        Assert.Equal(VectorPromoteKind.Incompatible, result.Kind);

        // A fresh sidecar + store instance with no surviving handle is the process restart this clause is about:
        // discovery has to go through the named sibling file, not an inherited connection.
        var reader = new VectorSidecar(
            SemanticMode.On,
            SystemVectorFileProbe.Instance,
            reader: new SemanticReaderIdentity(old.EncoderFingerprint, MillerSemanticContract.MinReaderVersion));

        string retained = Assert.Single(reader.RetainedGenerations(_root));
        Assert.Equal(manager.RetainedPathFor(MillerSemanticContract.GenerationTag(old)), retained);

        using VectorStore served = VectorStore.Open(retained, extension, readOnly: true);
        Assert.Equal(old.EncoderFingerprint, served.Identity.EncoderFingerprint);
        Assert.Equal("ready", served.Meta("build_state"));
        Assert.Equal(
            "old-unit",
            Assert.Single(served.Search(VectorUnitKind.Symbol, Vector(served.Lane.Dims, 100), k: 4)).UnitId);

        using VectorStore active = VectorStore.Open(manager.ActivePath, extension, readOnly: true);
        Assert.Equal(fresh.EncoderFingerprint, active.Identity.EncoderFingerprint);
        Assert.Equal("new-unit", Assert.Single(active.MappedUnits(VectorUnitKind.Symbol, null)).UnitId);
    }

    [Fact]
    public void CommitBatch_WritesVectorsDeletesAndMetaInOneTransaction()
    {
        string extension = SqliteVecTestSupport.RequireExtension();
        Directory.CreateDirectory(MillerDir);
        var manager = new VectorGenerationManager(_root);
        SemanticGenerationIdentity identity =
            MillerSemanticContract.PinnedIdentity(MillerSemanticContract.DefaultEncoder);

        using VectorStore store = VectorStore.Create(manager.ActivePath, identity, ArtifactId, extension);
        store.CommitBatch(
            VectorUnitKind.Symbol,
            [Entry("keep", store.Lane.Dims, 100), Entry("drop", store.Lane.Dims, -100)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["symbol_completed_revision"] = "7" },
            revision: 7);

        store.CommitBatch(
            VectorUnitKind.Symbol,
            [],
            ["drop"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["symbol_completed_revision"] = "9",
                ["build_state"] = "ready",
            },
            revision: 9);

        Assert.Equal("keep", Assert.Single(store.MappedUnits(VectorUnitKind.Symbol, null)).UnitId);
        Assert.Equal(1, store.MappedCount(VectorUnitKind.Symbol));
        Assert.Equal("9", store.Meta("symbol_completed_revision"));
        Assert.Equal("ready", store.Meta("build_state"));
    }

    private static void Build(string path, SemanticGenerationIdentity identity, string extension, string unitId)
    {
        using VectorStore store = VectorStore.Create(path, identity, ArtifactId, extension);
        store.CommitBatch(
            VectorUnitKind.Symbol,
            [Entry(unitId, store.Lane.Dims, 100)],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["symbol_completed_revision"] = "1",
                ["symbol_target_revision"] = "1",
                ["build_state"] = "ready",
                ["build_progress_percent"] = "100",
            },
            revision: 1);
    }

    private static VectorBatchEntry Entry(string unitId, int dims, sbyte value) =>
        new(unitId, $"src/{unitId}.cs", "class", false, Vector(dims, value), $"sha256:{unitId}");

    private static sbyte[] Vector(int dims, sbyte value)
    {
        var vector = new sbyte[dims];
        vector[0] = value;
        return vector;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
