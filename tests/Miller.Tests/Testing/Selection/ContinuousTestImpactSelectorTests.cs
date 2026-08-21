using Microsoft.Data.Sqlite;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Tests.Indexing.Resolution;
using Xunit;

namespace Miller.Tests.Testing.Selection;

public sealed class ContinuousTestImpactSelectorTests : IDisposable
{
    private const string Workspace = "ws:1";

    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-impact-selector-tests-").FullName;

    private string DbPath => Path.Combine(_dir, CtSchema.DbFileName);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Confidence_table_matches_eros_weights()
    {
        Assert.Equal(0.78, ContinuousTestImpactSelector.TierConfidence["test_result"]);
        Assert.Equal(0.65, ContinuousTestImpactSelector.TierConfidence["explicit_linkage"]);
        Assert.Equal(0.58, ContinuousTestImpactSelector.TierConfidence["graph_reference"]);
        Assert.Equal(0.52, ContinuousTestImpactSelector.TierConfidence["identifier_reference"]);
        Assert.Equal(0.35, ContinuousTestImpactSelector.TierConfidence["path_stem"]);
        Assert.Equal(0.85, ContinuousTestImpactSelector.TierConfidence["project_scope"]);
        Assert.Equal(0.88, ContinuousTestImpactSelector.TierConfidence["impacted_test"]);
        Assert.Equal(7, ContinuousTestImpactSelector.TierConfidence.Count);
    }

    [Fact]
    public void Select_narrows_to_miller_impacted_tests_without_canonical_linkage()
    {
        using ContinuousTestStore store = OpenStore();
        SeedProviderCase(
            store,
            "tc:paths",
            selector: "Eros.Core.Tests.ErosPathsTests.Workspace_canonical_db_path_uses_safe_segment",
            qualifiedName: "Eros.Core.Tests.ErosPathsTests.Workspace_canonical_db_path_uses_safe_segment",
            name: "Workspace_canonical_db_path_uses_safe_segment",
            sourcePath: "tests/Eros.Core.Tests/ErosPathsTests.cs");
        SeedProviderCase(
            store,
            "tc:inventory",
            selector: "Eros.ContinuousTesting.Tests.ContinuousTestProjectInventoryTests.Materialize_dotnet_workspaces_derives_ct_build_roots_outside_workspace",
            qualifiedName: "Eros.ContinuousTesting.Tests.ContinuousTestProjectInventoryTests.Materialize_dotnet_workspaces_derives_ct_build_roots_outside_workspace",
            name: "Materialize_dotnet_workspaces_derives_ct_build_roots_outside_workspace",
            sourcePath: "tests/Eros.ContinuousTesting.Tests/ContinuousTestProjectInventoryTests.cs");
        SeedProviderCase(
            store,
            "tc:unrelated",
            selector: "Eros.Store.Tests.WorkspaceRemovalTests.Remove_workspace_deletes_canonical_db_dir",
            qualifiedName: "Eros.Store.Tests.WorkspaceRemovalTests.Remove_workspace_deletes_canonical_db_dir",
            name: "Remove_workspace_deletes_canonical_db_dir",
            sourcePath: "tests/Eros.Store.Tests/WorkspaceRemovalTests.cs");
        SeedProviderCase(
            store,
            "tc:selector-only",
            selector: "tests/web/app.test.ts::renders_homepage",
            qualifiedName: "web.suite.not_the_reported_name",
            name: "renders homepage",
            sourcePath: "tests/web/app.test.ts");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Eros.Core/SafePath.cs"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:miller-paths",
                    Path: "tests/Eros.Core.Tests/ErosPathsTests.cs",
                    Name: "Workspace_canonical_db_path_uses_safe_segment",
                    Line: 36,
                    Hop: 1),
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:miller-inventory",
                    Path: "tests/Eros.ContinuousTesting.Tests/ContinuousTestProjectInventoryTests.cs",
                    Name: "Materialize_dotnet_workspaces_derives_ct_build_roots_outside_workspace",
                    Line: 17,
                    Hop: 1),
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:miller-selector",
                    Path: "tests/web/app.test.ts",
                    Name: "renders_homepage",
                    Line: 8,
                    Hop: 1),
            ]));

        Assert.Equal(["tc:inventory", "tc:paths", "tc:selector-only"], result.SelectedTestCaseIds);
        Assert.Equal(["tc:inventory", "tc:paths", "tc:selector-only", "tc:unrelated"], result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        Assert.All(result.Evidence, row => Assert.Equal("impacted_test", row.Tier));
        Assert.All(result.Evidence, row => Assert.Equal(0.88, row.Confidence));
        Assert.DoesNotContain("tc:unrelated", result.SelectedTestCaseIds);
    }

    [Theory]
    [InlineData("unknown", "parse_diagnostics")]
    [InlineData("current", null)]
    [InlineData("unknown", "future_reason")]
    public void Select_preserves_miller_currency_without_changing_impacted_test_scheduling(
        string evidenceStatus,
        string? evidenceReason)
    {
        using ContinuousTestStore store = OpenStore();
        SeedProviderCase(
            store,
            "tc:case",
            selector: "AppTests.does_the_thing",
            qualifiedName: "AppTests.does_the_thing",
            name: "does_the_thing",
            sourcePath: "tests/AppTests.cs");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/App.cs"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:case",
                    Path: "tests/AppTests.cs",
                    Name: "does_the_thing",
                    TestCase: true,
                    EvidenceStatus: evidenceStatus,
                    EvidenceReason: evidenceReason),
            ]));

        Assert.Equal(["tc:case"], result.SelectedTestCaseIds);
        ContinuousTestSelectionEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("impacted_test", evidence.Tier);
        Assert.Equal(evidenceStatus, evidence.EvidenceStatus);
        Assert.Equal(evidenceReason, evidence.EvidenceReason);
    }

    [Fact]
    public void Select_narrows_rust_impacted_tests_when_provider_source_path_is_crate_root()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/Cargo.toml";
        SeedProviderCase(
            store,
            "tc:rust-hit",
            selector: "rust-test:julie-extractors::lib/julie_extractors::tests::base::test_create_identifier_basic_call",
            qualifiedName: "tests::base::test_create_identifier_basic_call",
            name: "tests::base::test_create_identifier_basic_call",
            sourcePath: "/repo/crates/julie-extractors",
            projectPath: projectPath);
        SeedProviderCase(
            store,
            "tc:rust-miss",
            selector: "rust-test:julie-extractors::lib/julie_extractors::tests::other::test_other",
            qualifiedName: "tests::other::test_other",
            name: "tests::other::test_other",
            sourcePath: "/repo/crates/julie-extractors",
            projectPath: projectPath);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["crates/julie-extractors/src/base/framework_structural_facts/axum.rs"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:rust-test",
                    Path: "crates/julie-extractors/src/tests/base.rs",
                    Name: "test_create_identifier_basic_call",
                    Line: 1002,
                    Hop: 1),
            ]));

        Assert.Equal(["tc:rust-hit"], result.SelectedTestCaseIds);
        Assert.All(result.Evidence, row => Assert.Equal("impacted_test", row.Tier));
        Assert.DoesNotContain("tc:rust-miss", result.SelectedTestCaseIds);
    }

    [Fact]
    public void Select_reconciles_unique_fileless_nunit_case_by_impacted_csharp_test_name()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/Client.Tests.csproj";
        SeedProviderCase(
            store,
            "tc:hit",
            selector: "Upload_RendersSelectedFiles",
            qualifiedName: "Upload_RendersSelectedFiles",
            name: "Upload_RendersSelectedFiles",
            sourcePath: null,
            projectPath: projectPath,
            framework: "nunit");
        SeedProviderCase(
            store,
            "tc:miss",
            selector: "Unrelated_Test",
            qualifiedName: "Unrelated_Test",
            name: "Unrelated_Test",
            sourcePath: null,
            projectPath: projectPath,
            framework: "nunit");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["src/Client/Features/Edr/EdrFileUpload.razor"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:miller-hit",
                    Path: "src/Client.Tests/Features/Edr/EdrFileUploadTests.cs",
                    Name: "Upload_RendersSelectedFiles",
                    TestCase: true,
                    EvidenceStatus: "current"),
            ]));

        Assert.Equal(["tc:hit"], result.SelectedTestCaseIds);
        ContinuousTestSelectionEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("impacted_test", evidence.Tier);
        Assert.Equal("current", evidence.EvidenceStatus);
    }

    [Fact]
    public void Select_reconciles_unique_fileless_fqn_nunit_case_by_impacted_csharp_test_name()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/Client.Tests.csproj";
        SeedProviderCase(
            store,
            "tc:hit",
            selector: "Client.Tests.Features.Edr.EdrFormPageTests.Form_Renders",
            qualifiedName: "Client.Tests.Features.Edr.EdrFormPageTests.Form_Renders",
            name: "Form_Renders",
            sourcePath: null,
            projectPath: projectPath,
            className: "Client.Tests.Features.Edr.EdrFormPageTests",
            framework: "nunit");
        SeedProviderCase(
            store,
            "tc:miss",
            selector: "Client.Tests.UnrelatedTests.Other",
            qualifiedName: "Client.Tests.UnrelatedTests.Other",
            name: "Other",
            sourcePath: null,
            projectPath: projectPath,
            className: "Client.Tests.UnrelatedTests",
            framework: "nunit");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["src/Client/Features/Edr/EdrFileUpload.razor"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:form-renders",
                    Path: "src/Client.Tests/Features/Edr/EdrFormPageTests.cs",
                    Name: "Form_Renders",
                    TestCase: true,
                    EvidenceStatus: "current"),
            ]));

        Assert.Equal(["tc:hit"], result.SelectedTestCaseIds);
        Assert.Equal("impacted_test", Assert.Single(result.Evidence).Tier);
    }

    /// <summary>
    /// Defect D1 (2026-08-21 live validation): real xunit.v3 discovery stores the case NAME as the
    /// fully qualified "Namespace.Class.Method" with NULL file_path/symbol_name and a "class"
    /// metadata key. The impacted-test hint carries the SHORT method name plus the test file path.
    /// The fallback must map them via class metadata + file stem, or every source edit fails
    /// closed to Unknown and the impacted auto-run never fires.
    /// </summary>
    [Fact]
    public void Select_maps_fqn_named_fileless_xunit_v3_case_via_class_metadata()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/Fixture.Tests.csproj";
        SeedProviderCase(
            store,
            "tc:adds",
            selector: "Fixture.Tests.MathOpsTests.Adds",
            qualifiedName: "Fixture.Tests.MathOpsTests.Adds",
            name: "Fixture.Tests.MathOpsTests.Adds",
            sourcePath: null,
            projectPath: projectPath,
            className: "Fixture.Tests.MathOpsTests");
        SeedProviderCase(
            store,
            "tc:greets",
            selector: "Fixture.Tests.GreeterTests.Greets",
            qualifiedName: "Fixture.Tests.GreeterTests.Greets",
            name: "Fixture.Tests.GreeterTests.Greets",
            sourcePath: null,
            projectPath: projectPath,
            className: "Fixture.Tests.GreeterTests");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["Fixture/MathOps.cs"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:adds",
                    Path: "Fixture.Tests/MathOpsTests.cs",
                    Name: "Adds",
                    TestCase: true,
                    EvidenceStatus: "current"),
            ]));

        Assert.Equal(["tc:adds"], result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        ContinuousTestSelectionEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("impacted_test", evidence.Tier);
        Assert.DoesNotContain("tc:greets", result.SelectedTestCaseIds);
    }

    /// <summary>
    /// Two stored FQN cases share the SHORT method name in different classes. The impacted test's
    /// file stem names one class; class metadata must pick that one deterministically instead of
    /// reading the pair as ambiguous.
    /// </summary>
    [Fact]
    public void Select_disambiguates_shared_method_name_by_class_metadata_and_file_stem()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/Fixture.Tests.csproj";
        SeedProviderCase(
            store,
            "tc:mathops-adds",
            selector: "Fixture.Tests.MathOpsTests.Adds",
            qualifiedName: "Fixture.Tests.MathOpsTests.Adds",
            name: "Fixture.Tests.MathOpsTests.Adds",
            sourcePath: null,
            projectPath: projectPath,
            className: "Fixture.Tests.MathOpsTests");
        SeedProviderCase(
            store,
            "tc:calc-adds",
            selector: "Fixture.Tests.CalculatorTests.Adds",
            qualifiedName: "Fixture.Tests.CalculatorTests.Adds",
            name: "Fixture.Tests.CalculatorTests.Adds",
            sourcePath: null,
            projectPath: projectPath,
            className: "Fixture.Tests.CalculatorTests");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["Fixture/MathOps.cs"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:adds",
                    Path: "Fixture.Tests/MathOpsTests.cs",
                    Name: "Adds",
                    TestCase: true),
            ]));

        Assert.Equal(["tc:mathops-adds"], result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        Assert.DoesNotContain("tc:calc-adds", result.SelectedTestCaseIds);
    }

    /// <summary>
    /// A stored FQN case with NO class metadata still maps when its trailing name segment matches
    /// the impacted short name UNIQUELY across the fileless cases in scope.
    /// </summary>
    [Fact]
    public void Select_maps_unique_fqn_named_fileless_case_without_class_metadata()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/Fixture.Tests.csproj";
        SeedProviderCase(
            store,
            "tc:adds",
            selector: "Fixture.Tests.MathOpsTests.Adds",
            qualifiedName: "Fixture.Tests.MathOpsTests.Adds",
            name: "Fixture.Tests.MathOpsTests.Adds",
            sourcePath: null,
            projectPath: projectPath);
        SeedProviderCase(
            store,
            "tc:greets",
            selector: "Fixture.Tests.GreeterTests.Greets",
            qualifiedName: "Fixture.Tests.GreeterTests.Greets",
            name: "Fixture.Tests.GreeterTests.Greets",
            sourcePath: null,
            projectPath: projectPath);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["Fixture/MathOps.cs"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:adds",
                    Path: "Fixture.Tests/MathOpsTests.cs",
                    Name: "Adds",
                    TestCase: true),
            ]));

        Assert.Equal(["tc:adds"], result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
    }

    /// <summary>
    /// Contract clause (a): the stale set for a path-scoped change is the impacted set plus the
    /// already-owed backlog — NOTHING else. A case committed fresh must survive an edit that
    /// cannot reach it; that survival is the whole point of the watermark design.
    /// </summary>
    [Fact]
    public void Select_path_scoped_stale_set_is_impacted_union_already_stale()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:hit", "sym:test-hit", "tests/payments/HitTests.cs", "test_hit");
        SeedLinkedCase(store, "tc:owed", "sym:test-owed", "tests/other/OwedTests.cs", "test_owed");
        SeedLinkedCase(store, "tc:fresh", "sym:test-fresh", "tests/other/FreshTests.cs", "test_fresh");
        SeedCommittedResult(store, "tc:fresh");
        store.MarkContinuousTestsStale(Workspace, ["tc:owed"], new CtFreshnessKey("gen-1", 1));
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:charge", "charge", "src/payments/service.cs"));
        facts.Tests.Add(FakeMillerFactSource.Hit(
            "sym:test-hit",
            "test_hit",
            "tests/payments/HitTests.cs",
            isTest: true,
            edgeKind: "calls",
            edgeSource: "relationship"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/payments/service.cs"]));

        Assert.Equal(["tc:hit"], result.SelectedTestCaseIds);
        Assert.Equal(["tc:hit", "tc:owed"], result.StaleTestCaseIds);
        Assert.DoesNotContain("tc:fresh", result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        Assert.True(result.MayExecute);
    }

    /// <summary>
    /// Contract clause (b): a change whose files the index fully accounts for, and whose complete
    /// impact read reaches no test, is a KNOWN-EMPTY selection — an empty stale delta and no run.
    /// A committed-fresh case stays untouched.
    /// </summary>
    [Fact]
    public void Select_resolved_change_with_no_reachable_tests_is_known_empty()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:fresh", "sym:test-fresh", "tests/other/FreshTests.cs", "test_fresh");
        SeedCommittedResult(store, "tc:fresh");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:persistence", "Persist", "src/Persistence.cs"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Persistence.cs"]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Empty(result.StaleTestCaseIds);
        Assert.Empty(result.Evidence);
        Assert.Equal(ContinuousTestSelectionOutcome.KnownEmpty, result.Outcome);
        Assert.False(result.MayExecute);
    }

    /// <summary>A docs-only change (a markdown file with no symbols) cannot reach a test, so it is
    /// known-empty rather than fail-closed — the design's "editing an unrelated markdown file
    /// leaves the verdict green" acceptance.</summary>
    [Fact]
    public void Select_markdown_change_is_known_empty()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:fresh", "sym:test-fresh", "tests/other/FreshTests.cs", "test_fresh");
        SeedCommittedResult(store, "tc:fresh");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["docs/README.md"]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Empty(result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.KnownEmpty, result.Outcome);
    }

    /// <summary>
    /// Contract clause (c): a truncated impact read means the blast radius is incomplete, so the
    /// selection fails closed — everything previously fresh goes stale and nothing may execute.
    /// The truncation flags used to be dropped on the floor.
    /// </summary>
    [Fact]
    public void Select_truncated_impact_read_fails_closed_to_unknown()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:hit", "sym:test-hit", "tests/payments/HitTests.cs", "test_hit");
        SeedLinkedCase(store, "tc:fresh", "sym:test-fresh", "tests/other/FreshTests.cs", "test_fresh");
        SeedCommittedResult(store, "tc:fresh");
        var facts = new FakeMillerFactSource { ImpactTruncatedByLimit = true };
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:charge", "charge", "src/payments/service.cs"));
        facts.Tests.Add(FakeMillerFactSource.Hit(
            "sym:test-hit",
            "test_hit",
            "tests/payments/HitTests.cs",
            isTest: true,
            edgeKind: "calls",
            edgeSource: "relationship"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/payments/service.cs"]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:fresh", "tc:hit"], result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.False(result.MayExecute);
    }

    /// <summary>
    /// An impacted-test hint that names a case this project knows but cannot uniquely map is
    /// UNKNOWN reachability: everything goes stale and nothing runs. The old behaviour ran the
    /// whole workspace instead — a degraded edge must stale more, never run more. Here TWO classes
    /// in different namespaces share the trailing class segment the impacted file stem names, so
    /// class metadata cannot pick one: that is genuine ambiguity.
    /// </summary>
    [Fact]
    public void Select_ambiguous_fileless_nunit_name_fails_closed_without_a_run()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/Client.Tests.csproj";
        foreach ((string Id, string Selector, string ClassName) item in new[]
        {
            (Id: "tc:first", Selector: "Client.Tests.A.FirstTests.SavesAsync", ClassName: "Client.Tests.A.FirstTests"),
            (Id: "tc:second", Selector: "Client.Tests.B.FirstTests.SavesAsync", ClassName: "Client.Tests.B.FirstTests"),
        })
        {
            SeedProviderCase(
                store,
                item.Id,
                selector: item.Selector,
                qualifiedName: item.Selector,
                name: item.Selector,
                sourcePath: null,
                projectPath: projectPath,
                className: item.ClassName,
                framework: "nunit");
        }

        SeedProviderCase(
            store,
            "tc:unrelated",
            selector: "Unrelated_Test",
            qualifiedName: "Unrelated_Test",
            name: "Unrelated_Test",
            sourcePath: null,
            projectPath: projectPath,
            framework: "nunit");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["src/Client/Feature.razor"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:ambiguous",
                    Path: "src/Client.Tests/FirstTests.cs",
                    Name: "SavesAsync"),
            ]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:first", "tc:second", "tc:unrelated"], result.StaleTestCaseIds);
        Assert.Empty(result.Evidence);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.False(result.MayExecute);
    }

    [Theory]
    [InlineData("src/Client/Features/Edr/EdrFileUpload.razor.css")]
    [InlineData("src/Client/Features/Edr/EdrFileUpload.razor.js")]
    public void Select_leaves_unmatched_scoped_asset_project_stale_without_running_workspace_scope(
        string changedPath)
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/Api.Tests.csproj";
        SeedProviderCase(
            store,
            "tc:api",
            selector: "Api.Tests.Controllers.HealthControllerTests.Passes",
            qualifiedName: "Api.Tests.Controllers.HealthControllerTests.Passes",
            name: "Passes",
            sourcePath: null,
            projectPath: projectPath,
            className: "Api.Tests.Controllers.HealthControllerTests",
            framework: "nunit");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: [changedPath]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:api"], result.StaleTestCaseIds);
        Assert.Empty(result.Evidence);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void Select_prefers_coverage_over_explicit_link_and_graph_evidence()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:coverage", "sym:test-coverage", "tests/payments/CoverageTests.cs", "test_coverage");
        SeedLinkedCase(store, "tc:explicit", "sym:test-explicit", "tests/payments/ExplicitTests.cs", "test_explicit");
        SeedLinkedCase(store, "tc:graph", "sym:test-graph", "tests/payments/GraphTests.cs", "test_graph");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:charge", "charge", "src/payments/service.cs"));
        facts.Tests.Add(FakeMillerFactSource.Hit(
            "sym:test-explicit",
            "test_explicit",
            "tests/payments/ExplicitTests.cs",
            isTest: true,
            edgeKind: "test_linkage",
            edgeSource: "test_linkage"));
        facts.Tests.Add(FakeMillerFactSource.Hit(
            "sym:test-graph",
            "test_graph",
            "tests/payments/GraphTests.cs",
            isTest: true,
            edgeKind: "calls",
            edgeSource: "relationship"));
        var coverage = new FakeCoverageFactSource();
        coverage.Spans.Add(new CtCoverageSpanFact(
            SpanId: "cov:span",
            TestCaseId: "tc:coverage",
            SymbolId: "sym:charge",
            Path: "src/payments/service.cs",
            StartLine: 1));
        var selector = new ContinuousTestImpactSelector(store, facts, coverage);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/payments/service.cs"],
            ImpactedSymbols:
            [
                new ContinuousTestImpactedSymbol(
                    SymbolId: "sym:charge",
                    Path: "src/payments/service.cs",
                    Name: "charge"),
            ]));

        Assert.Equal(["tc:coverage", "tc:explicit", "tc:graph"], result.SelectedTestCaseIds);
        Assert.Equal(["coverage", "explicit_linkage", "graph_reference"], result.Evidence.Select(row => row.Tier));
        Assert.Equal([0.90, 0.65, 0.58], result.Evidence.Select(row => row.Confidence));
        Assert.Equal(["cov:span"], result.Evidence[0].SourceFactIds);
        Assert.Contains("coverage artifact covers src/payments/service.cs:1", result.Evidence[0].Explanation);
    }

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("Directory.Packages.props")]
    [InlineData("src/App/App.csproj")]
    [InlineData("global.json")]
    [InlineData("nuget.config")]
    public void Select_escalates_config_or_project_file_change_to_project_scope(string changedPath)
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.GetFullPath("/repo/tests/App.Tests/App.Tests.csproj");
        SeedLinkedCase(store, "tc:app-a", "sym:test-a", "tests/app_a.cs", "test_a", projectPath);
        SeedLinkedCase(store, "tc:app-b", "sym:test-b", "tests/app_b.cs", "test_b", projectPath);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [changedPath],
            WorkspaceScope: false,
            ProjectPath: projectPath));

        Assert.Equal(["tc:app-a", "tc:app-b"], result.SelectedTestCaseIds);
        Assert.All(result.Evidence, evidence => Assert.Equal("project_scope", evidence.Tier));
        Assert.All(result.Evidence, evidence => Assert.Equal(0.85, evidence.Confidence));
    }

    [Fact]
    public void Select_keeps_targeted_changed_test_file_over_project_scope_escalation()
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.GetFullPath("/repo/tests/App.Tests/App.Tests.csproj");
        SeedLinkedCase(store, "tc:app-a", "sym:test-a", "tests/app_a.cs", "test_a", projectPath);
        SeedLinkedCase(store, "tc:app-b", "sym:test-b", "tests/app_b.cs", "test_b", projectPath);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["Directory.Build.props", "tests/app_a.cs"],
            WorkspaceScope: false,
            ProjectPath: projectPath));

        Assert.Equal(["tc:app-a", "tc:app-b"], result.SelectedTestCaseIds);
        ContinuousTestSelectionEvidence appA = Assert.Single(result.Evidence, row => row.TestCaseId == "tc:app-a");
        Assert.Equal("changed_test_file", appA.Tier);
        Assert.Equal(1.0, appA.Confidence);
        ContinuousTestSelectionEvidence appB = Assert.Single(result.Evidence, row => row.TestCaseId == "tc:app-b");
        Assert.Equal("project_scope", appB.Tier);
    }

    [Fact]
    public void Select_does_not_use_coverage_or_links_for_cases_without_test_backing_files()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(
            store,
            "tc:false-source",
            "sym:false-source",
            "src/test_result_histories.cs",
            "test_result_histories",
            fileRole: "source");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:charge", "charge", "src/payments/service.cs"));
        facts.Tests.Add(FakeMillerFactSource.Hit(
            "sym:false-source",
            "test_result_histories",
            "src/test_result_histories.cs",
            isTest: true,
            edgeKind: "test_linkage",
            edgeSource: "test_linkage"));
        var coverage = new FakeCoverageFactSource();
        coverage.Spans.Add(new CtCoverageSpanFact(
            SpanId: "cov:false-source",
            TestCaseId: "tc:false-source",
            SymbolId: "sym:charge",
            Path: "src/payments/service.cs",
            StartLine: 1));
        var selector = new ContinuousTestImpactSelector(store, facts, coverage);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ImpactedSymbols:
            [
                new ContinuousTestImpactedSymbol(
                    SymbolId: "sym:charge",
                    Path: "src/payments/service.cs",
                    Name: "charge"),
            ]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:false-source"], result.StaleTestCaseIds);
        Assert.Empty(result.Evidence);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void Select_treats_aggregate_coverage_without_test_case_id_as_unknown()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:one", "sym:test-one", "tests/payments/OneTests.cs", "test_one");
        SeedLinkedCase(store, "tc:two", "sym:test-two", "tests/payments/TwoTests.cs", "test_two");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:charge", "charge", "src/payments/service.cs"));
        var coverage = new FakeCoverageFactSource();
        coverage.Spans.Add(new CtCoverageSpanFact(
            SpanId: "cov:aggregate",
            TestCaseId: null,
            SymbolId: "sym:charge",
            Path: "src/payments/service.cs",
            StartLine: 1));
        var selector = new ContinuousTestImpactSelector(store, facts, coverage);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ImpactedSymbols:
            [
                new ContinuousTestImpactedSymbol(
                    SymbolId: "sym:charge",
                    Path: "src/payments/service.cs",
                    Name: "charge"),
            ]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:one", "tc:two"], result.StaleTestCaseIds);
        Assert.Empty(result.Evidence);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void Select_ranks_graph_identifier_and_path_stem_evidence_for_impacted_symbol()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:graph", "sym:test-graph", "tests/payments/test_service.cs", "test_graph");
        SeedLinkedCase(store, "tc:identifier", "sym:test-identifier", "tests/payments/test_service.cs", "test_identifier");
        SeedLinkedCase(store, "tc:path", "sym:test-path", "tests/payments/test_service.cs", "test_path");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:charge", "charge", "src/payments/service.cs"));
        facts.Tests.Add(FakeMillerFactSource.Hit(
            "sym:test-graph",
            "test_graph",
            "tests/payments/test_service.cs",
            isTest: true,
            edgeKind: "calls",
            edgeSource: "relationship"));
        facts.Identifiers.Add(FakeMillerFactSource.Identifier(
            "sym:test-identifier",
            "sym:charge",
            "tests/payments/test_service.cs"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/payments/service.cs"],
            ImpactedSymbols:
            [
                new ContinuousTestImpactedSymbol(
                    SymbolId: "sym:charge",
                    Path: "src/payments/service.cs",
                    Name: "charge"),
            ]));

        Assert.Equal(["tc:graph", "tc:identifier", "tc:path"], result.SelectedTestCaseIds);
        Assert.Equal(
            ["graph_reference", "identifier_reference", "path_stem"],
            result.Evidence.Select(row => row.Tier));
        Assert.Equal([0.58, 0.52, 0.35], result.Evidence.Select(row => row.Confidence));
        Assert.Contains("changed symbol charge", result.Evidence[0].Explanation);
    }

    [Fact]
    public void Select_prioritizes_test_cases_in_changed_test_files()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:changed", "sym:test-changed", "tests/payments/test_service.cs", "test_changed");
        SeedLinkedCase(store, "tc:other", "sym:test-other", "tests/payments/test_other.cs", "test_other");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["tests/payments/test_service.cs"]));

        Assert.Equal(["tc:changed"], result.SelectedTestCaseIds);
        Assert.Equal(["tc:changed", "tc:other"], result.StaleTestCaseIds);
        ContinuousTestSelectionEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("changed_test_file", evidence.Tier);
        Assert.Equal("tc:changed", evidence.TestCaseId);
    }

    [Fact]
    public void Select_includes_impacted_test_symbol()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:changed", "sym:test-changed", "tests/payments/test_service.cs", "test_changed");
        SeedLinkedCase(store, "tc:other", "sym:test-other", "tests/payments/test_other.cs", "test_other");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:test-changed",
            "test_changed",
            "tests/payments/test_service.cs",
            isTest: true,
            kind: "method"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ImpactedSymbols: [new ContinuousTestImpactedSymbol(SymbolId: "sym:test-changed")]));

        Assert.Equal(["tc:changed"], result.SelectedTestCaseIds);
        Assert.Equal(["tc:changed", "tc:other"], result.StaleTestCaseIds);
        ContinuousTestSelectionEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("impacted_test_symbol", evidence.Tier);
        Assert.Equal("changed test symbol test_changed", evidence.Explanation);
        Assert.Equal(0.86, evidence.Confidence);
    }

    [Fact]
    public void Select_path_stem_matches_same_language_tests_and_excludes_false_positives()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:csharp", "sym:test-csharp", "tests/payments/test_service.cs", "test_service", language: "csharp");
        SeedLinkedCase(store, "tc:python", "sym:test-python", "tests/payments/test_service.py", "test_service", language: "python");
        SeedLinkedCase(
            store,
            "tc:fixture",
            "sym:test-fixture",
            "fixtures/payments/test_service.cs",
            "test_service_fixture",
            language: "csharp",
            fileRole: "fixture");
        SeedLinkedCase(store, "tc:generic", "sym:test-lib", "tests/test_lib.cs", "test_lib", language: "csharp");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:service", "charge", "src/payments/service.cs", language: "csharp"));
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:lib", "add", "src/lib.cs", language: "csharp"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/payments/service.cs", "src/lib.cs"]));

        Assert.Equal(["tc:csharp"], result.SelectedTestCaseIds);
        ContinuousTestSelectionEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("path_stem", evidence.Tier);
        Assert.Equal("test path stem matches changed path stem service", evidence.Explanation);
    }

    [Theory]
    [InlineData("src/EdrFileUpload.cs", "csharp", "tests/EdrFileUploadTests.cs", "csharp", "EdrFileUpload")]
    [InlineData("src/Foo.razor.css", "css", "tests/FooTests.cs", "csharp", "Foo")]
    [InlineData("src/Foo.razor.js", "javascript", "tests/FooTests.cs", "csharp", "Foo")]
    [InlineData("src/Tests.cs", "csharp", "tests/Tests.cs", "csharp", "Tests")]
    [InlineData("src/utils.py", "python", "tests/test_utils.py", "python", "utils")]
    [InlineData("src/foo.go", "go", "tests/foo_test.go", "go", "foo")]
    public void Select_normalizes_provider_aware_test_path_stems(
        string changedPath,
        string changedLanguage,
        string testPath,
        string testLanguage,
        string expectedStem)
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:match", "sym:test", testPath, "case", language: testLanguage);
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:source", "source", changedPath, language: changedLanguage));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [changedPath]));

        Assert.Equal(["tc:match"], result.SelectedTestCaseIds);
        ContinuousTestSelectionEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("path_stem", evidence.Tier);
        Assert.Equal($"test path stem matches changed path stem {expectedStem}", evidence.Explanation);
    }

    [Theory]
    [InlineData("src/Features/Edr/EdrFileUpload.razor", "razor")]
    [InlineData("src/Features/Edr/EdrFileUpload.razor.css", "css")]
    public void Select_treats_indexed_razor_paths_as_csharp_affine(string changedPath, string indexedLanguage)
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:csharp", "sym:test-csharp", "tests/EdrFileUploadTests.cs", "Case", language: "csharp");
        SeedLinkedCase(store, "tc:python", "sym:test-python", "tests/test_EdrFileUpload.py", "Case", language: "python");
        SeedProviderCase(
            store,
            "tc:nunit",
            selector: "Terraform.Client.Tests.Features.Edr.EdrFileUploadTests.Case",
            qualifiedName: "Terraform.Client.Tests.Features.Edr.EdrFileUploadTests.Case",
            name: "Case",
            sourcePath: null,
            className: "Terraform.Client.Tests.Features.Edr.EdrFileUploadTests",
            framework: "nunit");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:component", "EdrFileUpload", changedPath, language: indexedLanguage));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [changedPath]));

        Assert.Equal(["tc:csharp", "tc:nunit"], result.SelectedTestCaseIds.Order(StringComparer.Ordinal));
        Assert.All(result.Evidence, row => Assert.Equal("path_stem", row.Tier));
    }

    [Theory]
    [InlineData("SRC\\Features\\EDR\\EdrFileUpload.RAZOR")]
    [InlineData("src/Features/Edr/EdrFileUpload.razor.js")]
    public void Select_treats_unindexed_razor_path_variants_as_csharp_affine(string changedPath)
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:csharp", "sym:test-csharp", "tests/edrfileuploadTests.cs", "Case", language: "csharp");
        SeedLinkedCase(store, "tc:go", "sym:test-go", "tests/edrfileupload_test.go", "Case", language: "go");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [changedPath]));

        Assert.Equal(["tc:csharp"], result.SelectedTestCaseIds);
        Assert.Equal("path_stem", Assert.Single(result.Evidence).Tier);
    }

    [Fact]
    public void Select_does_not_treat_fileless_dotnet_language_as_a_wildcard()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:python", "sym:test-python", "tests/test_EdrFileUpload.py", "Case", language: "python");
        SeedProviderCase(
            store,
            "tc:nunit",
            selector: "Terraform.Client.Tests.Features.Edr.EdrFileUploadTests.Case",
            qualifiedName: "Terraform.Client.Tests.Features.Edr.EdrFileUploadTests.Case",
            name: "Case",
            sourcePath: null,
            className: "Terraform.Client.Tests.Features.Edr.EdrFileUploadTests",
            framework: "nunit");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:python", "EdrFileUpload", "src/EdrFileUpload.py", language: "python"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/EdrFileUpload.py"]));

        Assert.Equal(["tc:python"], result.SelectedTestCaseIds);
        Assert.Equal("path_stem", Assert.Single(result.Evidence).Tier);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Select_uses_dotnet_test_class_stem_when_provider_case_has_no_source_path(bool hasClassMetadata)
    {
        using ContinuousTestStore store = OpenStore();
        SeedProviderCase(
            store,
            "tc:nunit",
            selector: "Terraform.Client.Tests.Features.Edr.EdrFileUploadTests.Case",
            qualifiedName: "Terraform.Client.Tests.Features.Edr.EdrFileUploadTests.Case",
            name: "Case",
            sourcePath: null,
            className: hasClassMetadata ? "Terraform.Client.Tests.Features.Edr.EdrFileUploadTests" : null,
            framework: "nunit");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:component",
            "EdrFileUpload",
            "src/Features/Edr/EdrFileUpload.razor"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Features/Edr/EdrFileUpload.razor"]));

        Assert.Equal(["tc:nunit"], result.SelectedTestCaseIds);
        ContinuousTestSelectionEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("path_stem", evidence.Tier);
        Assert.Equal("test path stem matches changed path stem EdrFileUpload", evidence.Explanation);
    }

    [Fact]
    public void Select_uses_impact_test_linkage_as_explicit_linkage()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:file-link", "sym:test-file-link", "tests/payments/test_service.cs", "test_service");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:service", "charge", "src/payments/service.cs"));
        facts.Tests.Add(FakeMillerFactSource.Hit(
            "sym:test-file-link",
            "test_service",
            "tests/payments/test_service.cs",
            isTest: true,
            edgeKind: "test_linkage",
            edgeSource: "test_linkage"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/payments/service.cs"]));

        ContinuousTestSelectionEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("tc:file-link", evidence.TestCaseId);
        Assert.Equal("explicit_linkage", evidence.Tier);
        Assert.Equal(0.65, evidence.Confidence);
    }

    [Fact]
    public void Select_config_change_escalates_to_project_scope_without_false_green()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:one", "sym:test-one", "tests/OneTests.cs", "test_one");
        SeedLinkedCase(store, "tc:two", "sym:test-two", "tests/TwoTests.cs", "test_two");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["Directory.Build.props"]));

        Assert.Equal(["tc:one", "tc:two"], result.StaleTestCaseIds);
        Assert.Equal(["tc:one", "tc:two"], result.SelectedTestCaseIds);
        Assert.All(result.Evidence, row => Assert.Equal("project_scope", row.Tier));
    }

    /// <summary>A changed source file the index cannot resolve has UNKNOWN reachability: everything
    /// goes stale and nothing runs. The old behaviour ran the whole workspace instead.</summary>
    [Fact]
    public void Select_unmapped_source_change_fails_closed_without_false_green()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:one", "sym:test-one", "tests/OneTests.cs", "test_one");
        SeedLinkedCase(store, "tc:two", "sym:test-two", "tests/TwoTests.cs", "test_two");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/unmapped/anything.cs"]));

        Assert.Equal(["tc:one", "tc:two"], result.StaleTestCaseIds);
        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Empty(result.Evidence);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.False(result.MayExecute);
    }

    [Fact]
    public void Select_change_with_no_mappable_evidence_fails_closed_without_a_run()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:one", "sym:test-one", "tests/OneTests.cs", "test_one");
        SeedLinkedCase(store, "tc:two", "sym:test-two", "tests/TwoTests.cs", "test_two");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/App.cs"],
            ImpactedTests: []));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:one", "tc:two"], result.StaleTestCaseIds);
        Assert.Empty(result.Evidence);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void Select_workspace_scope_selects_all_provider_cases_without_changed_paths()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:one", "sym:test-one", "tests/OneTests.cs", "test_one");
        SeedLinkedCase(store, "tc:two", "sym:test-two", "tests/TwoTests.cs", "test_two");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            WorkspaceScope: true));

        Assert.Equal(["tc:one", "tc:two"], result.StaleTestCaseIds);
        Assert.Equal(["tc:one", "tc:two"], result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.WorkspaceScope, result.Outcome);
        Assert.True(result.MayExecute);
        Assert.All(result.Evidence, row =>
        {
            Assert.Equal("workspace_scope", row.Tier);
            Assert.Contains("workspace scope", row.Explanation, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Select_ignores_imported_non_provider_test_cases()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:provider", "sym:test-provider", "tests/ProviderTests.cs", "test_provider");
        store.PutTestCase(new ContinuousTestCase(
            Id: "tc:imported",
            WorkspaceId: Workspace,
            Name: "imported",
            QualifiedName: "imported",
            Selector: "legacy/tests/test_imported.py::test_imported",
            Framework: "pytest",
            Role: ContinuousTestRole.TestCase,
            Source: "extractor_metadata",
            Confidence: 0.75));
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            WorkspaceScope: true));

        Assert.Equal(["tc:provider"], result.SelectedTestCaseIds);
        Assert.Equal(["tc:provider"], result.StaleTestCaseIds);
    }

    [Fact]
    public void Select_wraps_indexing_fact_source_with_miller_fact_source()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:one", "sym:test-one", "tests/OneTests.cs", "test_one");
        var inner = new FakeCtFactSource();
        IMillerFactSource facts = new MillerFactSource(inner);
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            WorkspaceScope: true));

        Assert.Equal(["tc:one"], result.SelectedTestCaseIds);
        Assert.Equal(new CtFreshnessKey("gen-1", 1), facts.Freshness);
    }

    [Fact]
    public void Select_wraps_real_ct_fact_adapter_and_uses_impact_test_partition()
    {
        using ResolutionArtifactFixture fixture = CreateAdapterFixture();
        using var adapter = CtFactAdapter.OpenArtifact(fixture.DbPath);
        IMillerFactSource facts = new MillerFactSource(adapter);
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:process-works", "fn-test", "tests/ServiceTests.cs", "ProcessWorks");
        SeedLinkedCase(store, "tc:other", "fn-other", "tests/OtherTests.cs", "Other");
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Service.cs"],
            ImpactedSymbols: [new ContinuousTestImpactedSymbol(SymbolId: "fn-validate")]));

        Assert.Contains("tc:process-works", result.SelectedTestCaseIds);
        Assert.DoesNotContain("tc:other", result.SelectedTestCaseIds);
        Assert.Contains(result.Evidence, row =>
            row.TestCaseId == "tc:process-works"
            && row.Tier is "graph_reference" or "explicit_linkage" or "identifier_reference");
        Assert.Equal(new CtFreshnessKey(adapter.Current.IndexIdentity, adapter.Current.Revision), facts.Freshness);
    }

    [Fact]
    public void Select_without_input_returns_empty_and_does_not_mark_stale()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:one", "sym:test-one", "tests/OneTests.cs", "test_one");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Empty(result.StaleTestCaseIds);
        Assert.Empty(result.Evidence);
    }

    private ContinuousTestStore OpenStore() => new(DbPath);

    /// <summary>Commits one green result through the real run-completion path, so the case is
    /// committed-fresh at <c>(identity, revision)</c> the way production rows are.</summary>
    private static void SeedCommittedResult(
        ContinuousTestStore store,
        string testCaseId,
        string identity = "gen-1",
        long revision = 1)
    {
        string revisionText = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string runId = "run:" + testCaseId;
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: Workspace,
                Status: "running",
                SelectedRevision: revisionText,
                IndexIdentity: identity,
                Revision: revision),
            [testCaseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: Workspace,
            TestRunId: runId,
            SelectedRevision: revisionText,
            CurrentRevision: revisionText,
            IndexIdentity: identity,
            Revision: revision,
            Status: "passed",
            Results:
            [
                new ContinuousTestResult(
                    Id: runId + ":result",
                    WorkspaceId: Workspace,
                    TestCaseId: testCaseId,
                    TestRunId: runId,
                    Status: "passed",
                    ResultRevision: revisionText,
                    IndexIdentity: identity,
                    Revision: revision),
            ]));
    }

    private static void SeedProviderCase(
        ContinuousTestStore store,
        string testCaseId,
        string selector,
        string qualifiedName,
        string name,
        string? sourcePath,
        string? projectPath = null,
        string? className = null,
        string framework = "xunit")
    {
        var metadata = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(sourcePath))
            metadata["source_path"] = sourcePath;
        if (!string.IsNullOrWhiteSpace(projectPath))
            metadata["ct_project_path"] = projectPath;
        if (!string.IsNullOrWhiteSpace(className))
            metadata["class"] = className;

        store.PutTestCase(new ContinuousTestCase(
            Id: testCaseId,
            WorkspaceId: Workspace,
            Name: name,
            QualifiedName: qualifiedName,
            Selector: selector,
            Framework: framework,
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:dotnet",
            Confidence: 1.0,
            Metadata: metadata));
    }

    private static void SeedLinkedCase(
        ContinuousTestStore store,
        string testCaseId,
        string symbolId,
        string path,
        string name,
        string? projectPath = null,
        string language = "csharp",
        string? fileRole = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["file_language"] = language,
        };
        if (!string.IsNullOrWhiteSpace(projectPath))
            metadata["ct_project_path"] = projectPath;
        if (!string.IsNullOrWhiteSpace(fileRole))
            metadata["file_role"] = fileRole;

        store.PutTestCase(new ContinuousTestCase(
            Id: testCaseId,
            WorkspaceId: Workspace,
            Name: name,
            QualifiedName: name,
            Selector: $"{path}::{name}",
            FilePath: path,
            ContentHash: "blake3:" + symbolId,
            SymbolName: symbolId,
            SymbolPath: path,
            Framework: "xunit",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:dotnet",
            Confidence: 1.0,
            Metadata: metadata));
    }

    private static ResolutionArtifactFixture CreateAdapterFixture()
    {
        ResolutionArtifactFixture fixture = ResolutionArtifactFixture.Create();
        fixture.AddFile("file-service", "src/Service.cs");
        fixture.AddFile("file-tests", "tests/ServiceTests.cs");
        fixture.AddSymbol("file-service", "cls-service", "Service", "class", "src/Service.cs");
        fixture.AddSymbol("file-service", "fn-validate", "Validate", "method", "src/Service.cs", parentId: "cls-service");
        fixture.AddSymbol("file-service", "fn-process", "Process", "function", "src/Service.cs", parentId: "cls-service");
        fixture.AddSymbol("file-tests", "cls-tests", "ServiceTests", "class", "tests/ServiceTests.cs");
        fixture.AddSymbol("file-tests", "fn-test", "ProcessWorks", "method", "tests/ServiceTests.cs", parentId: "cls-tests");
        fixture.AddRelationship("file-service", "rel-validate", "fn-process", "fn-validate", "src/Service.cs", kind: "calls");
        fixture.AddRelationship("file-tests", "rel-process", "fn-test", "fn-process", "tests/ServiceTests.cs", kind: "calls");
        fixture.AddIdentifier(
            "file-tests",
            "id-process",
            "Process",
            "tests/ServiceTests.cs",
            kind: "call",
            containingSymbolId: "fn-test",
            startByte: 40,
            endByte: 47);
        MarkTest(fixture, "cls-tests", container: true);
        MarkTest(fixture, "fn-test", container: false);
        return fixture;
    }

    private static void MarkTest(ResolutionArtifactFixture fixture, string symbolId, bool container)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE symbols
            SET is_test = 1, test_container = $container, test_lifecycle = 1
            WHERE symbol_id = $id;
            """;
        command.Parameters.AddWithValue("$container", container ? 1 : 0);
        command.Parameters.AddWithValue("$id", symbolId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
