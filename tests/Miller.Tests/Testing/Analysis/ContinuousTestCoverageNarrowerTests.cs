using Miller.Indexing.Testing;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Analysis;

public sealed class ContinuousTestCoverageNarrowerTests : IDisposable
{
    private const string WorkspaceId = "ws:1";
    private static readonly CtFreshnessKey Selected = new("gen-1", 42);
    private static readonly string ProjectPath = Path.GetFullPath("/repo/tests/App.Tests/App.Tests.csproj");

    private readonly string _dir;
    private readonly string _dbPath;

    public ContinuousTestCoverageNarrowerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-narrower-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, CtSchema.DbFileName);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Trusted_map_drops_an_allowlisted_heuristic_but_preserves_static_staleness()
    {
        var staticSelection = Selection(
            ["tc:graph"],
            ["tc:graph", "tc:unselected"],
            Evidence("tc:graph", "graph_reference", evidenceStatus: "unknown", evidenceReason: "parse_diagnostics"));

        var result = ContinuousTestCoverageNarrower.Narrow(
            staticSelection,
            WorkspaceId,
            ProjectPath,
            Selected,
            [TrustedEvidence("tc:graph")]);

        Assert.Same(staticSelection, result.StaticSelection);
        Assert.Equal(["tc:graph"], result.StaticSelectedTestCaseIds);
        Assert.Equal(["tc:graph", "tc:unselected"], result.StaticStaleTestCaseIds);
        Assert.Empty(result.FinalSelectedTestCaseIds);
        Assert.Equal(["tc:graph"], result.DroppedTestCaseIds);
        Assert.Empty(result.AdvisoryTestCaseIds);
        Assert.Same(staticSelection.Evidence, result.StaticEvidence);
        var decision = Assert.Single(result.Decisions);
        Assert.True(decision.Dropped);
        Assert.False(decision.Selected);
        Assert.False(decision.Advisory);
        Assert.Equal("trusted_coverage_map", decision.Reason);
        var evidence = Assert.Single(decision.StaticEvidence);
        Assert.Equal("unknown", evidence.EvidenceStatus);
        Assert.Equal("parse_diagnostics", evidence.EvidenceReason);
    }

    [Theory]
    [InlineData("changed_test_file")]
    [InlineData("test_link")]
    [InlineData("impacted_test")]
    [InlineData("impacted_test_symbol")]
    [InlineData("project_scope")]
    [InlineData("workspace_scope")]
    [InlineData("coverage")]
    [InlineData("future_tier")]
    public void Protected_and_unknown_tiers_cannot_be_dropped(string tier)
    {
        var staticSelection = Selection(["tc:protected"], ["tc:protected"], Evidence("tc:protected", tier));

        var result = ContinuousTestCoverageNarrower.Narrow(
            staticSelection,
            WorkspaceId,
            ProjectPath,
            Selected,
            [TrustedEvidence("tc:protected")]);

        Assert.Equal(["tc:protected"], result.FinalSelectedTestCaseIds);
        Assert.Empty(result.DroppedTestCaseIds);
        Assert.Empty(result.AdvisoryTestCaseIds);
        var decision = Assert.Single(result.Decisions);
        Assert.Equal("protected_static_evidence", decision.Reason);
        Assert.True(decision.Selected);
    }

    [Theory]
    [InlineData("graph_reference")]
    [InlineData("identifier_reference")]
    [InlineData("path_stem")]
    public void Allowlisted_tiers_are_removable(string tier)
    {
        var staticSelection = Selection(["tc:heuristic"], ["tc:heuristic"], Evidence("tc:heuristic", tier));

        var result = ContinuousTestCoverageNarrower.Narrow(
            staticSelection,
            WorkspaceId,
            ProjectPath,
            Selected,
            [TrustedEvidence("tc:heuristic")]);

        Assert.Equal(["tc:heuristic"], result.DroppedTestCaseIds);
    }

    [Fact]
    public void Every_static_reason_must_be_allowlisted_before_a_test_can_be_dropped()
    {
        var heuristic = Evidence("tc:mixed", "path_stem");
        var protectedEvidence = Evidence("tc:mixed", "test_link");
        var staticSelection = Selection(["tc:mixed"], ["tc:mixed"], heuristic, protectedEvidence);

        var result = ContinuousTestCoverageNarrower.Narrow(
            staticSelection,
            WorkspaceId,
            ProjectPath,
            Selected,
            [TrustedEvidence("tc:mixed")]);

        Assert.Equal(["tc:mixed"], result.FinalSelectedTestCaseIds);
        Assert.Empty(result.DroppedTestCaseIds);
        Assert.Equal([heuristic, protectedEvidence], Assert.Single(result.Decisions).StaticEvidence);
    }

    [Fact]
    public void Missing_maps_remain_selected_and_advisory()
    {
        var staticSelection = Selection(["tc:heuristic"], ["tc:heuristic"], Evidence("tc:heuristic", "graph_reference"));

        var result = ContinuousTestCoverageNarrower.Narrow(
            staticSelection,
            WorkspaceId,
            ProjectPath,
            Selected,
            []);

        Assert.Equal(["tc:heuristic"], result.FinalSelectedTestCaseIds);
        Assert.Empty(result.DroppedTestCaseIds);
        Assert.Equal(["tc:heuristic"], result.AdvisoryTestCaseIds);
        var decision = Assert.Single(result.Decisions);
        Assert.True(decision.Selected);
        Assert.True(decision.Advisory);
        Assert.Equal("coverage_map_missing", decision.Reason);
    }

    [Theory]
    [MemberData(nameof(UntrustedEvidence))]
    public void Untrusted_maps_remain_selected_and_advisory(CtCoverageNarrowingEvidence coverage)
    {
        var staticSelection = Selection(["tc:heuristic"], ["tc:heuristic"], Evidence("tc:heuristic", "graph_reference"));

        var result = ContinuousTestCoverageNarrower.Narrow(
            staticSelection,
            WorkspaceId,
            ProjectPath,
            Selected,
            [coverage]);

        Assert.Equal(["tc:heuristic"], result.FinalSelectedTestCaseIds);
        Assert.Empty(result.DroppedTestCaseIds);
        Assert.Equal(["tc:heuristic"], result.AdvisoryTestCaseIds);
        var decision = Assert.Single(result.Decisions);
        Assert.True(decision.Selected);
        Assert.True(decision.Advisory);
        Assert.Equal("coverage_map_untrusted", decision.Reason);
    }

    [Fact]
    public void Every_coverage_row_must_be_trusted_when_duplicate_evidence_is_supplied()
    {
        var staticSelection = Selection(["tc:heuristic"], ["tc:heuristic"], Evidence("tc:heuristic", "path_stem"));
        var trusted = TrustedEvidence("tc:heuristic");
        var untrusted = trusted with { IsTrustedAtRevision = false };

        var result = ContinuousTestCoverageNarrower.Narrow(
            staticSelection,
            WorkspaceId,
            ProjectPath,
            Selected,
            [trusted, untrusted]);

        Assert.Equal(["tc:heuristic"], result.FinalSelectedTestCaseIds);
        Assert.Equal(["tc:heuristic"], result.AdvisoryTestCaseIds);
    }

    [Fact]
    public void Changed_index_identity_does_not_trust_a_map()
    {
        var staticSelection = Selection(["tc:heuristic"], ["tc:heuristic"], Evidence("tc:heuristic", "path_stem"));
        var map = TrustedMap("tc:heuristic") with { IndexIdentity = "gen-2" };

        var result = ContinuousTestCoverageNarrower.Narrow(
            staticSelection,
            WorkspaceId,
            ProjectPath,
            Selected,
            [new CtCoverageNarrowingEvidence("tc:heuristic", map, IsTrustedAtRevision: true)]);

        Assert.Equal(["tc:heuristic"], result.AdvisoryTestCaseIds);
        Assert.Equal("coverage_map_untrusted", Assert.Single(result.Decisions).Reason);
    }

    [Fact]
    public void SpansCovering_reads_hit_spans_by_symbol_name_and_file_path()
    {
        using var store = new ContinuousTestStore(_dbPath);
        store.PutCoverageFile(new CoverageFile(
            Id: "cov:file",
            WorkspaceId: WorkspaceId,
            IndexIdentity: Selected.IndexIdentity,
            Revision: Selected.Revision,
            Format: "lcov",
            Path: "src/App.cs",
            Parser: "lcov",
            SourceHash: "blake3:app"));
        store.PutCoverageSpan(new CoverageSpan(
            Id: "span:hit",
            WorkspaceId: WorkspaceId,
            IndexIdentity: Selected.IndexIdentity,
            Revision: Selected.Revision,
            CoverageFileId: "cov:file",
            StartLine: 4,
            EndLine: 4,
            Hits: 2,
            FilePath: "src/App.cs",
            SymbolName: "sym:run",
            Metadata: new Dictionary<string, object?> { ["test_case_id"] = "tc:1" }));
        store.PutCoverageSpan(new CoverageSpan(
            Id: "span:miss",
            WorkspaceId: WorkspaceId,
            IndexIdentity: Selected.IndexIdentity,
            Revision: Selected.Revision,
            CoverageFileId: "cov:file",
            StartLine: 12,
            EndLine: 12,
            Hits: 0,
            FilePath: "src/App.cs",
            SymbolName: "sym:run"));

        ICtCoverageFactSource source = new ContinuousTestCoverageNarrower(store);
        var hits = source.SpansCovering(WorkspaceId, ["sym:run"], ["src/App.cs"]);

        var span = Assert.Single(hits);
        Assert.Equal("span:hit", span.SpanId);
        Assert.Equal("tc:1", span.TestCaseId);
        Assert.Equal("sym:run", span.SymbolId);
        Assert.Equal("src/App.cs", span.Path);
        Assert.Equal(4, span.StartLine);
    }

    public static TheoryData<CtCoverageNarrowingEvidence> UntrustedEvidence => new()
    {
        TrustedEvidence("tc:heuristic") with { IsTrustedAtRevision = false },
        TrustedEvidence("tc:heuristic") with { Map = TrustedMap("tc:heuristic") with { Complete = false } },
        TrustedEvidence("tc:heuristic") with { Map = TrustedMap("tc:heuristic") with { RevisionAtEnd = "41" } },
        TrustedEvidence("tc:heuristic") with { Map = TrustedMap("tc:heuristic") with { InvalidatedAtRevision = "42" } },
        TrustedEvidence("tc:heuristic") with { Map = TrustedMap("tc:heuristic") with { ProjectPath = Path.GetFullPath("/repo/tests/Other/Other.csproj") } },
        TrustedEvidence("tc:heuristic") with { Map = TrustedMap("tc:heuristic") with { ValidThroughRevision = "41" } },
        TrustedEvidence("tc:heuristic") with { Map = TrustedMap("tc:heuristic") with { WorkspaceId = "ws:other" } },
    };

    private static ContinuousTestSelectionResult Selection(
        IReadOnlyList<string> selected,
        IReadOnlyList<string> stale,
        params ContinuousTestSelectionEvidence[] evidence) =>
        new(selected, stale, evidence);

    private static ContinuousTestSelectionEvidence Evidence(
        string testCaseId,
        string tier,
        string? evidenceStatus = null,
        string? evidenceReason = null) =>
        new(
            testCaseId,
            "Tests.Case",
            tier,
            0.5,
            "static reason",
            ["fact:1"],
            evidenceStatus,
            evidenceReason);

    private static CtCoverageNarrowingEvidence TrustedEvidence(string testCaseId) =>
        new(testCaseId, TrustedMap(testCaseId), IsTrustedAtRevision: true);

    private static CtCoverageMapRecord TrustedMap(string testCaseId) =>
        new(
            MapId: ContinuousTestStore.CtCoverageMapId(WorkspaceId, testCaseId),
            WorkspaceId,
            TestCaseId: testCaseId,
            ProjectPath,
            RunId: "run:1",
            GenerationId: "generation:1",
            IndexIdentity: Selected.IndexIdentity,
            Revision: Selected.Revision,
            RevisionAtStart: "40",
            StartConverged: true,
            RevisionAtEnd: "40",
            EndConverged: true,
            Complete: true,
            FailureReason: null,
            Granularity: "test",
            ValidThroughRevision: "42",
            InvalidatedAtRevision: null,
            RecordedAt: new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            Source: "dotnet-coverage");
}
