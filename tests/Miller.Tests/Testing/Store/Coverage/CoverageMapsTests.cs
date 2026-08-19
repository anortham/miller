using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Store.Coverage;

public sealed class CoverageMapsTests : IDisposable
{
    private const string Workspace = "ws:1";
    private const string Identity = "gen-1";

    private static readonly DateTimeOffset RecordedAt =
        new(2026, 7, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-coverage-maps-").FullName;

    private string DbPath => Path.Combine(_dir, CtSchema.DbFileName);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Coverage_map_round_trips_a_trusted_map_and_its_files_with_content_hashes()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Alpha", ProjectPath);

        store.UpsertCtCoverageMap(
            TrustedMap("xunit:Alpha"),
            [
                new CtCoverageMapFile("src/Miller.Testing/Store/ContinuousTestStore.cs", "blake3:aaa"),
                new CtCoverageMapFile("src/Miller.Testing/Contracts/CtCoverageContracts.cs", "blake3:bbb"),
            ]);

        CtCoverageMapRecord? map = store.GetCtCoverageMap(Workspace, "xunit:Alpha");
        Assert.NotNull(map);
        Assert.Equal(ContinuousTestStore.CtCoverageMapId(Workspace, "xunit:Alpha"), map.MapId);
        Assert.Equal(Identity, map.IndexIdentity);
        Assert.Equal(41, map.Revision);
        Assert.Equal("41", map.RevisionAtStart);
        Assert.True(map.StartConverged);
        Assert.Equal("41", map.RevisionAtEnd);
        Assert.True(map.EndConverged);
        Assert.True(map.Complete);
        Assert.Equal("test", map.Granularity);
        Assert.Equal("41", map.ValidThroughRevision);
        Assert.Null(map.InvalidatedAtRevision);
        Assert.Equal(RecordedAt, map.RecordedAt);
        Assert.Equal("maintenance", map.Source);

        Assert.Equal(
            [
                "src/Miller.Testing/Contracts/CtCoverageContracts.cs",
                "src/Miller.Testing/Store/ContinuousTestStore.cs",
            ],
            store.ListCtCoverageMapFiles(map.MapId).Select(file => file.FilePath));
        Assert.Equal(
            "blake3:aaa",
            store.ListCtCoverageMapFiles(map.MapId)
                .Single(file => file.FilePath.EndsWith("ContinuousTestStore.cs", StringComparison.Ordinal))
                .ContentHash);
    }

    [Fact]
    public void Coverage_map_round_trips_a_torn_map_and_an_incomplete_collector_run()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Alpha", ProjectPath);

        store.UpsertCtCoverageMap(
            TrustedMap("xunit:Alpha") with
            {
                RevisionAtStart = null,
                StartConverged = false,
                RevisionAtEnd = "43",
                EndConverged = true,
                Complete = false,
                FailureReason = "collector-timeout",
                ValidThroughRevision = null,
            },
            [new CtCoverageMapFile("src/a.cs", null)]);

        CtCoverageMapRecord map = store.GetCtCoverageMap(Workspace, "xunit:Alpha")!;
        Assert.Null(map.RevisionAtStart);
        Assert.False(map.StartConverged);
        Assert.Equal("43", map.RevisionAtEnd);
        Assert.True(map.EndConverged);
        Assert.False(map.Complete);
        Assert.Equal("collector-timeout", map.FailureReason);
        Assert.Null(map.ValidThroughRevision);
    }

    [Fact]
    public void Get_coverage_map_reports_no_map_for_an_unmapped_test()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Alpha", ProjectPath);

        Assert.Null(store.GetCtCoverageMap(Workspace, "xunit:Alpha"));
        Assert.Empty(store.ListCtCoverageMaps(Workspace));
    }

    [Fact]
    public void Upsert_replaces_the_previous_map_and_drops_its_old_file_rows()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Alpha", ProjectPath);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Alpha"), [new CtCoverageMapFile("src/old.cs", "blake3:old")]);

        store.UpsertCtCoverageMap(
            TrustedMap("xunit:Alpha") with { RunId = "run:2", Revision = 42, RevisionAtEnd = "42", ValidThroughRevision = "42" },
            [new CtCoverageMapFile("src/new.cs", "blake3:new")]);

        CtCoverageMapRecord map = store.GetCtCoverageMap(Workspace, "xunit:Alpha")!;
        Assert.Equal("run:2", map.RunId);
        Assert.Equal(["src/new.cs"], store.ListCtCoverageMapFiles(map.MapId).Select(file => file.FilePath));
        Assert.Single(store.ListCtCoverageMaps(Workspace));
    }

    [Fact]
    public void An_upsert_rolled_back_by_a_failing_transaction_leaves_the_previous_map_intact()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Alpha", ProjectPath);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Alpha"), [new CtCoverageMapFile("src/kept.cs", null)]);

        Assert.Throws<InvalidOperationException>(() => store.Transaction(() =>
        {
            store.UpsertCtCoverageMap(
                TrustedMap("xunit:Alpha") with { RunId = "run:2" },
                [new CtCoverageMapFile("src/lost.cs", null)]);
            throw new InvalidOperationException("boom");
        }));

        CtCoverageMapRecord map = store.GetCtCoverageMap(Workspace, "xunit:Alpha")!;
        Assert.Equal("run:1", map.RunId);
        Assert.Equal(["src/kept.cs"], store.ListCtCoverageMapFiles(map.MapId).Select(file => file.FilePath));
    }

    [Fact]
    public void Upsert_rejects_a_map_id_that_is_not_derived_from_the_workspace_and_test_case()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Alpha", ProjectPath);

        Assert.Throws<ArgumentException>(() => store.UpsertCtCoverageMap(
            TrustedMap("xunit:Alpha") with { MapId = "map:forged" },
            [new CtCoverageMapFile("src/a.cs", null)]));
    }

    [Fact]
    public void Coverage_maps_are_listed_per_workspace_ordered_by_test_case()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Beta", ProjectPath);
        SeedProviderCase(store, "xunit:Alpha", ProjectPath);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Beta"), [new CtCoverageMapFile("src/b.cs", null)]);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Alpha"), [new CtCoverageMapFile("src/a.cs", null)]);

        Assert.Equal(
            ["xunit:Alpha", "xunit:Beta"],
            store.ListCtCoverageMaps(Workspace).Select(map => map.TestCaseId));
        Assert.Empty(store.ListCtCoverageMaps("ws:other"));
    }

    [Fact]
    public void Candidates_put_unmapped_then_untrusted_then_trusted_oldest_first()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:TrustedOld", ProjectPath);
        SeedProviderCase(store, "xunit:UntrustedNew", ProjectPath);
        SeedProviderCase(store, "xunit:Missing", ProjectPath);
        store.UpsertCtCoverageMap(
            TrustedMap("xunit:TrustedOld") with { RecordedAt = RecordedAt },
            [new CtCoverageMapFile("src/old.cs", null)]);
        store.UpsertCtCoverageMap(
            TrustedMap("xunit:UntrustedNew") with
            {
                RevisionAtEnd = "42",
                ValidThroughRevision = null,
                RecordedAt = RecordedAt.AddHours(1),
            },
            [new CtCoverageMapFile("src/new.cs", null)]);

        Assert.Equal(
            ["xunit:Missing", "xunit:UntrustedNew", "xunit:TrustedOld"],
            store.ListCtCoverageMapCandidates(Workspace, ProjectPath, limit: 10));
    }

    [Fact]
    public void Candidates_are_scoped_to_the_projects_provider_managed_cases_and_bounded()
    {
        using var store = new ContinuousTestStore(DbPath);
        string other = Path.GetFullPath("/repo/other/Other.csproj");
        SeedProviderCase(store, "xunit:Owned", ProjectPath);
        SeedProviderCase(store, "xunit:Other", other);
        store.PutTestCase(new ContinuousTestCase(
            Id: "extractor:One",
            WorkspaceId: Workspace,
            Name: "One",
            QualifiedName: "One",
            Selector: "One.selector",
            Source: "extractor",
            Metadata: new Dictionary<string, object?> { ["ct_project_path"] = ProjectPath }));

        Assert.Equal(
            ["xunit:Owned"],
            store.ListCtCoverageMapCandidates(Workspace, ProjectPath, limit: 10));
        Assert.Equal(
            ["xunit:Owned"],
            store.ListCtCoverageMapCandidates(Workspace, ProjectPath, limit: 1));
    }

    [Fact]
    public void Deleting_a_test_case_cascades_its_coverage_map_and_files()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Alpha", ProjectPath);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Alpha"), [new CtCoverageMapFile("src/a.cs", "blake3:a")]);

        store.DeleteTestCase(Workspace, "xunit:Alpha");

        Assert.Null(store.GetCtCoverageMap(Workspace, "xunit:Alpha"));
        Assert.Empty(store.ListCtCoverageMaps(Workspace));
    }

    [Fact]
    public void Reassigned_test_case_cannot_read_or_rank_the_previous_projects_map()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Reassigned", ProjectPath);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Reassigned"), [new CtCoverageMapFile("src/old.cs", null)]);

        string newProject = Path.GetFullPath("/repo/src/New/New.csproj");
        SeedProviderCase(store, "xunit:Reassigned", newProject);
        SeedProviderCase(store, "xunit:Owned", newProject);
        store.UpsertCtCoverageMap(
            TrustedMap("xunit:Owned") with { ProjectPath = newProject },
            [new CtCoverageMapFile("src/owned.cs", null)]);

        Assert.Null(store.GetCtCoverageMap(Workspace, "xunit:Reassigned"));
        Assert.DoesNotContain(
            store.ListCtCoverageMaps(Workspace),
            map => map.TestCaseId == "xunit:Reassigned");
        Assert.Empty(store.ListCtCoverageMapFiles(ContinuousTestStore.CtCoverageMapId(Workspace, "xunit:Reassigned")));
        Assert.Equal(
            ["xunit:Reassigned", "xunit:Owned"],
            store.ListCtCoverageMapCandidates(Workspace, newProject, limit: 10));
    }

    [Fact]
    public void Narrowing_evidence_batches_owned_trusted_rejected_and_missing_cases()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Trusted", ProjectPath);
        SeedProviderCase(store, "xunit:Torn", ProjectPath);
        SeedProviderCase(store, "xunit:Foreign", ProjectPath);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Trusted"), [new CtCoverageMapFile("src/trusted.cs", null)]);
        store.UpsertCtCoverageMap(
            TrustedMap("xunit:Torn") with { RevisionAtEnd = "42", ValidThroughRevision = null },
            [new CtCoverageMapFile("src/torn.cs", null)]);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Foreign"), [new CtCoverageMapFile("src/foreign.cs", null)]);
        SeedProviderCase(store, "xunit:Foreign", Path.GetFullPath("/repo/other/Other.csproj"));

        IReadOnlyList<CtCoverageNarrowingEvidence> evidence = store.ListCtCoverageNarrowingEvidence(
            Workspace,
            ProjectPath,
            ["xunit:Missing", "xunit:Torn", "xunit:Trusted", "xunit:Foreign"],
            new CtFreshnessKey(Identity, 41));

        Assert.Equal(
            ["xunit:Foreign", "xunit:Missing", "xunit:Torn", "xunit:Trusted"],
            evidence.Select(item => item.TestCaseId));
        Assert.Null(evidence[0].Map);
        Assert.False(evidence[0].IsTrustedAtRevision);
        Assert.Null(evidence[1].Map);
        Assert.False(evidence[1].IsTrustedAtRevision);
        Assert.NotNull(evidence[2].Map);
        Assert.False(evidence[2].IsTrustedAtRevision);
        Assert.NotNull(evidence[3].Map);
        Assert.True(evidence[3].IsTrustedAtRevision);
    }

    [Fact]
    public void Empty_delta_advances_an_eligible_map_and_replay_is_idempotent()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Alpha", ProjectPath);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Alpha"), [new CtCoverageMapFile("src/a.cs", null)]);

        CtCoverageDeltaApplyResult applied = store.ApplyCtCoverageDelta(Workspace, Key(41), Key(42), []);
        CtCoverageDeltaApplyResult replay = store.ApplyCtCoverageDelta(Workspace, Key(41), Key(42), []);

        Assert.Equal(CtCoverageDeltaApplyStatus.Applied, applied.Status);
        Assert.Equal(1, applied.AdvancedMapCount);
        Assert.Equal(0, applied.InvalidatedMapCount);
        Assert.Equal(CtCoverageDeltaApplyStatus.AlreadyApplied, replay.Status);
        Assert.Equal("42", store.GetCtCoverageMap(Workspace, "xunit:Alpha")!.ValidThroughRevision);
    }

    [Fact]
    public void Delta_invalidates_intersecting_maps_and_advances_non_intersecting_maps()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Hit", ProjectPath);
        SeedProviderCase(store, "xunit:Miss", ProjectPath);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Hit"), [new CtCoverageMapFile("src/hit.cs", null)]);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Miss"), [new CtCoverageMapFile("src/miss.cs", null)]);

        CtCoverageDeltaApplyResult result = store.ApplyCtCoverageDelta(
            Workspace,
            Key(41),
            Key(42),
            ["src\\hit.cs"]);

        Assert.Equal(CtCoverageDeltaApplyStatus.Applied, result.Status);
        Assert.Equal(1, result.AdvancedMapCount);
        Assert.Equal(1, result.InvalidatedMapCount);
        CtCoverageMapRecord hit = store.GetCtCoverageMap(Workspace, "xunit:Hit")!;
        Assert.Equal("41", hit.ValidThroughRevision);
        Assert.Equal("42", hit.InvalidatedAtRevision);
        CtCoverageMapRecord miss = store.GetCtCoverageMap(Workspace, "xunit:Miss")!;
        Assert.Equal("42", miss.ValidThroughRevision);
        Assert.Null(miss.InvalidatedAtRevision);
    }

    [Fact]
    public void Gapped_and_out_of_order_deltas_do_not_advance_a_map_from_another_endpoint()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Alpha", ProjectPath);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Alpha"), [new CtCoverageMapFile("src/a.cs", null)]);

        Assert.Equal(0, store.ApplyCtCoverageDelta(Workspace, Key(42), Key(43), []).AdvancedMapCount);
        Assert.Equal(0, store.ApplyCtCoverageDelta(Workspace, Key(40), Key(41), []).AdvancedMapCount);
        Assert.Equal("41", store.GetCtCoverageMap(Workspace, "xunit:Alpha")!.ValidThroughRevision);
    }

    [Fact]
    public void Same_interval_with_a_different_digest_rejects_and_invalidates_advanced_maps()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Alpha", ProjectPath);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Alpha"), [new CtCoverageMapFile("src/a.cs", null)]);
        store.ApplyCtCoverageDelta(Workspace, Key(41), Key(42), []);

        CtCoverageDeltaApplyResult mismatch = store.ApplyCtCoverageDelta(
            Workspace,
            Key(41),
            Key(42),
            ["src/a.cs"]);

        Assert.Equal(CtCoverageDeltaApplyStatus.Rejected, mismatch.Status);
        Assert.Equal(0, mismatch.AdvancedMapCount);
        Assert.Equal(1, mismatch.InvalidatedMapCount);
        CtCoverageMapRecord map = store.GetCtCoverageMap(Workspace, "xunit:Alpha")!;
        Assert.Equal("42", map.ValidThroughRevision);
        Assert.Equal("42", map.InvalidatedAtRevision);
    }

    [Fact]
    public void Changed_index_identity_does_not_advance_or_trust_maps_from_the_prior_index()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Alpha", ProjectPath);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Alpha"), [new CtCoverageMapFile("src/a.cs", null)]);

        CtCoverageDeltaApplyResult applied = store.ApplyCtCoverageDelta(
            Workspace,
            new CtFreshnessKey("gen-2", 41),
            new CtFreshnessKey("gen-2", 42),
            []);
        IReadOnlyList<CtCoverageNarrowingEvidence> evidence = store.ListCtCoverageNarrowingEvidence(
            Workspace,
            ProjectPath,
            ["xunit:Alpha"],
            new CtFreshnessKey("gen-2", 41));

        Assert.Equal(0, applied.AdvancedMapCount);
        Assert.Equal("41", store.GetCtCoverageMap(Workspace, "xunit:Alpha")!.ValidThroughRevision);
        Assert.False(Assert.Single(evidence).IsTrustedAtRevision);
    }

    [Fact]
    public void Maintenance_claims_rotate_durably_and_prioritize_a_new_project_once()
    {
        string projectA = Path.GetFullPath("/repo/a/A.csproj");
        string projectB = Path.GetFullPath("/repo/b/B.csproj");
        string projectC = Path.GetFullPath("/repo/c/C.csproj");
        using (var store = new ContinuousTestStore(DbPath))
        {
            SeedProviderCase(store, "xunit:A", projectA);
            SeedProviderCase(store, "xunit:B", projectB);
            SeedProviderCase(store, "xunit:C", projectC);

            Assert.Equal(
                projectA,
                store.ClaimNextCtCoverageMaintenanceBatch(Workspace, [projectA, projectB], limit: 10)!.ProjectPath);
            Assert.Equal(
                projectB,
                store.ClaimNextCtCoverageMaintenanceBatch(Workspace, [projectA, projectB], limit: 10)!.ProjectPath);
        }

        using var reopened = new ContinuousTestStore(DbPath);
        Assert.Equal(
            projectC,
            reopened.ClaimNextCtCoverageMaintenanceBatch(
                Workspace,
                [projectA, projectB, projectC],
                limit: 10)!.ProjectPath);
        Assert.Equal(
            projectA,
            reopened.ClaimNextCtCoverageMaintenanceBatch(
                Workspace,
                [projectA, projectB, projectC],
                limit: 10)!.ProjectPath);
    }

    [Fact]
    public void Delta_file_intersection_uses_platform_path_case_semantics()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "xunit:Case", ProjectPath);
        store.UpsertCtCoverageMap(TrustedMap("xunit:Case"), [new CtCoverageMapFile("src/Case.cs", null)]);

        store.ApplyCtCoverageDelta(Workspace, Key(41), Key(42), ["SRC/cASE.CS"]);

        CtCoverageMapRecord map = store.GetCtCoverageMap(Workspace, "xunit:Case")!;
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("41", map.ValidThroughRevision);
            Assert.Equal("42", map.InvalidatedAtRevision);
        }
        else
        {
            Assert.Equal("42", map.ValidThroughRevision);
            Assert.Null(map.InvalidatedAtRevision);
        }
    }

    [Fact]
    public void Missing_db_coverage_reads_return_empty_and_do_not_create_the_file()
    {
        using var store = new ContinuousTestStore(DbPath);

        Assert.Null(store.GetCtCoverageMap(Workspace, "xunit:Alpha"));
        Assert.Empty(store.ListCtCoverageMaps(Workspace));
        Assert.Empty(store.ListCtCoverageMapFiles("map:missing"));
        Assert.Empty(store.ListCtCoverageNarrowingEvidence(Workspace, ProjectPath, ["xunit:Alpha"], Key(1)));
        Assert.Empty(store.ListCtCoverageMapCandidates(Workspace, ProjectPath, limit: 4));
        Assert.False(File.Exists(DbPath));
    }

    private static string ProjectPath => Path.GetFullPath("/repo/src/Miller.Testing/Miller.Testing.csproj");

    private static CtFreshnessKey Key(long revision) => new(Identity, revision);

    private static CtCoverageMapRecord TrustedMap(string testCaseId) =>
        new(
            MapId: ContinuousTestStore.CtCoverageMapId(Workspace, testCaseId),
            WorkspaceId: Workspace,
            TestCaseId: testCaseId,
            ProjectPath: "/repo/src/Miller.Testing/Miller.Testing.csproj",
            RunId: "run:1",
            GenerationId: "g0007",
            IndexIdentity: Identity,
            Revision: 41,
            RevisionAtStart: "41",
            StartConverged: true,
            RevisionAtEnd: "41",
            EndConverged: true,
            Complete: true,
            FailureReason: null,
            Granularity: "test",
            ValidThroughRevision: "41",
            InvalidatedAtRevision: null,
            RecordedAt: RecordedAt,
            Source: "maintenance");

    private static void SeedProviderCase(ContinuousTestStore store, string id, string projectPath) =>
        store.PutTestCase(new ContinuousTestCase(
            Id: id,
            WorkspaceId: Workspace,
            Name: id.Replace(":", "_", StringComparison.Ordinal),
            QualifiedName: $"Tests.{id.Replace(":", "_", StringComparison.Ordinal)}",
            Selector: $"{id}.selector",
            Framework: "xunit",
            Source: "ct-provider:dotnet",
            Metadata: new Dictionary<string, object?> { ["ct_project_path"] = projectPath }));
}
