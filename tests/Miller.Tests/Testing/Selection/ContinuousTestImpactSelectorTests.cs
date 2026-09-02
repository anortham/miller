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

    [Theory]
    [InlineData("csharp", "dotnet", ".cs", "csharp")]
    [InlineData("razor", "dotnet", ".razor", "razor")]
    [InlineData("vbnet", "dotnet", ".vb", "vbnet")]
    [InlineData("javascript", "node", ".js", "javascript")]
    [InlineData("jsx", "node", ".jsx", "jsx")]
    [InlineData("javascript", "node", ".mjs", "javascript")]
    [InlineData("javascript", "node", ".cjs", "javascript")]
    [InlineData("typescript", "node", ".ts", "typescript")]
    [InlineData("tsx", "node", ".tsx", "tsx")]
    [InlineData("typescript", "node", ".mts", "typescript")]
    [InlineData("typescript", "node", ".cts", "typescript")]
    [InlineData("qml", "qml", ".qml", "qml")]
    [InlineData("go", "go", ".go", "go")]
    public void Language_family_maps_exact_labels_and_paths(
        string label,
        string expected,
        string extension,
        string expectedPathLabel)
    {
        Assert.Equal(expected, ContinuousTestLanguageFamily.FromLabel(label));
        Assert.Equal(expected, ContinuousTestLanguageFamily.FromPath("src/Thing" + extension));
        Assert.Equal(expectedPathLabel, ContinuousTestLanguageFamily.LabelFromPath("src/Thing" + extension));
    }

    [Fact]
    public void Language_family_keeps_unknown_labels_incompatible()
    {
        Assert.Null(ContinuousTestLanguageFamily.FromLabel("unknown-language"));
        Assert.Null(ContinuousTestLanguageFamily.FromPath("src/Thing.xyz"));
        Assert.False(ContinuousTestLanguageFamily.AreCompatible("vbnet", "javascript"));
        Assert.False(ContinuousTestLanguageFamily.AreCompatible("unknown-language", "csharp"));
    }

    [Fact]
    public void Select_uses_vbnet_path_identity_without_borrowing_a_same_stem_csharp_case()
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.GetFullPath("/repo/tests/App.Tests/App.Tests.vbproj");
        SeedProviderCase(
            store,
            "tc:vb",
            "tests/Calculator.vb::Adds",
            "CalculatorTests.Adds",
            "Adds",
            "tests/Calculator.vb",
            projectPath: projectPath,
            framework: "mstest");
        SeedProviderCase(
            store,
            "tc:cs",
            "tests/Calculator.cs::Adds",
            "CalculatorTests.Adds",
            "Adds",
            "tests/Calculator.cs",
            projectPath: projectPath,
            framework: "mstest");
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, "src/Calculator.vb");
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Calculator.vb"],
            ProjectPath: projectPath));

        Assert.True(
            result.SelectedTestCaseIds.SequenceEqual(["tc:vb"]),
            $"selected={string.Join(',', result.SelectedTestCaseIds)} evidence="
            + string.Join(";", result.Evidence.Select(row => $"{row.TestCaseId}:{row.Explanation}")));
        Assert.DoesNotContain("tc:cs", result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
    }

    [Fact]
    public void Select_does_not_guess_fileless_dotnet_case_for_a_vbnet_change()
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.GetFullPath("/repo/tests/App.Tests/App.Tests.vbproj");
        SeedProviderCase(
            store,
            "tc:fileless",
            "CalculatorTests.Adds",
            "CalculatorTests.Adds",
            "Adds",
            sourcePath: null,
            projectPath: projectPath,
            className: "CalculatorTests",
            framework: "mstest");
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, "src/Calculator.vb");
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Calculator.vb"],
            ProjectPath: projectPath));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void Select_does_not_guess_csharp_for_a_fileless_dotnet_case()
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.GetFullPath("/repo/tests/App.Tests/App.Tests.csproj");
        SeedProviderCase(
            store,
            "tc:fileless",
            "CalculatorTests.Adds",
            "CalculatorTests.Adds",
            "Adds",
            sourcePath: null,
            projectPath: projectPath,
            className: "CalculatorTests",
            framework: "mstest");
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, "src/Calculator.cs");
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Calculator.cs"],
            ProjectPath: projectPath));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void Select_does_not_reconcile_a_fileless_case_across_vbnet_and_csharp_paths()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/tests/App.Tests/App.Tests.vbproj";
        SeedProviderCase(
            store,
            "tc:fileless",
            "CalculatorTests.Adds",
            "CalculatorTests.Adds",
            "Adds",
            sourcePath: null,
            projectPath: projectPath,
            className: "CalculatorTests",
            framework: "mstest");
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, "src/Calculator.vb");
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Calculator.vb"],
            ProjectPath: projectPath,
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:adds",
                    Path: "tests/App.Tests/CalculatorTests.cs",
                    Name: "Adds",
                    TestCase: true,
                    EvidenceStatus: "current"),
            ]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void Select_requires_current_file_evidence_before_known_empty()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:fresh", "legacy:symbol", "tests/FreshTests.cs", "fresh");
        SeedCommittedResult(store, "tc:fresh");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:changed", "Changed", "src/Changed.cs"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Changed.cs"]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:fresh"], result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.False(result.MayExecute);
    }

    [Fact]
    public void Select_fails_closed_on_missing_changed_file_evidence_even_with_exact_impact()
    {
        using ContinuousTestStore store = OpenStore();
        SeedProviderCase(
            store,
            "tc:case",
            selector: "tests/Suite.cs::Case",
            qualifiedName: "Suite.Case",
            name: "Case",
            sourcePath: "tests/Suite.cs");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:changed", "Changed", "src/Changed.cs"));
        facts.Tests.Add(FakeMillerFactSource.Hit(
            "sym:case",
            "Case",
            "tests/Suite.cs",
            isTest: true,
            edgeKind: "calls",
            edgeSource: "relationship"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Changed.cs"]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:case"], result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void Select_does_not_schedule_detailed_container_or_lifecycle_symbols()
    {
        using ContinuousTestStore store = OpenStore();
        SeedProviderCase(
            store,
            "tc:container",
            selector: "Suite.Case",
            qualifiedName: "Suite.Case",
            name: "Case",
            sourcePath: "tests/Suite.cs");
        SeedCommittedResult(store, "tc:container");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:changed", "Changed", "src/Changed.cs"));
        facts.FileFacts.Add(new CtFileFact("src/Changed.cs", "csharp", "blake3:changed", "indexed", false, true));
        facts.Tests.Add(new CtImpactedSymbol(
            "sym:container",
            "Suite",
            "class",
            "tests/Suite.cs",
            IsTest: true,
            Hop: 1,
            EdgeKind: "calls",
            EdgeSource: "relationship",
            TestCase: false,
            TestContainer: true,
            TestLifecycle: false,
            TestEvidenceStatus: "current"));
        facts.Tests.Add(new CtImpactedSymbol(
            "sym:lifecycle",
            "Setup",
            "method",
            "tests/Suite.cs",
            IsTest: true,
            Hop: 1,
            EdgeKind: "calls",
            EdgeSource: "relationship",
            TestCase: false,
            TestContainer: false,
            TestLifecycle: true,
            TestEvidenceStatus: "current"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Changed.cs"]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Empty(result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.KnownEmpty, result.Outcome);
    }

    [Fact]
    public void Select_resolves_provider_identity_by_current_name_and_path()
    {
        using ContinuousTestStore store = OpenStore();
        store.PutTestCase(new ContinuousTestCase(
            Id: "tc:case",
            WorkspaceId: Workspace,
            Name: "Case",
            QualifiedName: "Suite.Case",
            Selector: "Suite.Case",
            FilePath: "tests/Suite.cs",
            SymbolName: "Case",
            SymbolPath: "tests/Suite.cs",
            Framework: "xunit",
            Source: "ct-provider:dotnet"));
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:changed", "Changed", "src/Changed.cs"));
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:case", "Case", "tests/Suite.cs", isTest: true));
        facts.FileFacts.Add(new CtFileFact("src/Changed.cs", "csharp", "blake3:changed", "indexed", false, true));
        facts.FileFacts.Add(new CtFileFact("tests/Suite.cs", "csharp", "blake3:suite", "indexed", false, true));
        facts.Tests.Add(new CtImpactedSymbol(
            "sym:case",
            "Case",
            "method",
            "tests/Suite.cs",
            IsTest: true,
            Hop: 1,
            EdgeKind: "calls",
            EdgeSource: "relationship",
            TestCase: true,
            TestContainer: false,
            TestLifecycle: false,
            TestEvidenceStatus: "current"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Changed.cs"]));

        Assert.Equal(["tc:case"], result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("stale")]
    public void Select_reads_unknown_when_a_graph_impacted_test_has_unknown_evidence_currency(
        string evidenceStatus)
    {
        using ContinuousTestStore store = OpenStore();
        store.PutTestCase(new ContinuousTestCase(
            Id: "tc:case",
            WorkspaceId: Workspace,
            Name: "Case",
            QualifiedName: "Suite.Case",
            Selector: "Suite.Case",
            FilePath: "tests/Suite.cs",
            SymbolName: "Case",
            SymbolPath: "tests/Suite.cs",
            Framework: "xunit",
            Source: "ct-provider:dotnet"));
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:changed", "Changed", "src/Changed.cs"));
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:case", "Case", "tests/Suite.cs", isTest: true));
        facts.FileFacts.Add(new CtFileFact("src/Changed.cs", "csharp", "blake3:changed", "indexed", false, true));
        facts.FileFacts.Add(new CtFileFact("tests/Suite.cs", "csharp", "blake3:suite", "indexed", false, true));
        facts.Tests.Add(new CtImpactedSymbol(
            "sym:case",
            "Case",
            "method",
            "tests/Suite.cs",
            IsTest: true,
            Hop: 1,
            EdgeKind: "calls",
            EdgeSource: "relationship",
            TestCase: true,
            TestContainer: false,
            TestLifecycle: false,
            TestEvidenceStatus: evidenceStatus));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Changed.cs"]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.Equal(["tc:case"], result.StaleTestCaseIds);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Select_fails_closed_when_provider_identity_file_evidence_is_not_current(
        bool evidenceAvailable,
        bool hasParseDiagnostics)
    {
        using ContinuousTestStore store = OpenStore();
        store.PutTestCase(new ContinuousTestCase(
            Id: "tc:case",
            WorkspaceId: Workspace,
            Name: "Case",
            QualifiedName: "Suite.Case",
            Selector: "Suite.Case",
            FilePath: "tests/Suite.cs",
            SymbolName: "Case",
            SymbolPath: "tests/Suite.cs",
            Framework: "xunit",
            Source: "ct-provider:dotnet"));
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:changed", "Changed", "src/Changed.cs"));
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:case", "Case", "tests/Suite.cs", isTest: true));
        facts.FileFacts.Add(new CtFileFact("src/Changed.cs", "csharp", "blake3:changed", "indexed", false, true));
        facts.FileFacts.Add(new CtFileFact(
            "tests/Suite.cs",
            "csharp",
            evidenceAvailable ? "blake3:suite" : null,
            evidenceAvailable ? "indexed" : null,
            hasParseDiagnostics,
            evidenceAvailable));
        facts.Tests.Add(new CtImpactedSymbol(
            "sym:case",
            "Case",
            "method",
            "tests/Suite.cs",
            IsTest: true,
            Hop: 1,
            EdgeKind: "calls",
            EdgeSource: "relationship",
            TestCase: true,
            TestContainer: false,
            TestLifecycle: false,
            TestEvidenceStatus: "current"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Changed.cs"]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:case"], result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void Select_fails_closed_when_provider_identity_is_unresolved()
    {
        using ContinuousTestStore store = OpenStore();
        store.PutTestCase(new ContinuousTestCase(
            Id: "tc:case",
            WorkspaceId: Workspace,
            Name: "Case",
            QualifiedName: "Suite.Case",
            Selector: "Suite.Case",
            FilePath: "tests/Suite.cs",
            SymbolName: "Case",
            SymbolPath: "tests/Suite.cs",
            Framework: "xunit",
            Source: "ct-provider:dotnet"));
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:changed", "Changed", "src/Changed.cs"));
        facts.Tests.Add(new CtImpactedSymbol(
            "sym:missing",
            "Case",
            "method",
            "tests/Suite.cs",
            IsTest: true,
            Hop: 1,
            EdgeKind: "calls",
            EdgeSource: "relationship",
            TestCase: true,
            TestContainer: false,
            TestLifecycle: false,
            TestEvidenceStatus: "current"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Changed.cs"]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:case"], result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void Select_fails_closed_when_provider_identity_is_ambiguous()
    {
        using ContinuousTestStore store = OpenStore();
        store.PutTestCase(new ContinuousTestCase(
            Id: "tc:case",
            WorkspaceId: Workspace,
            Name: "Case",
            QualifiedName: "Suite.Case",
            Selector: "Suite.Case",
            FilePath: "tests/Suite.cs",
            SymbolName: "Case",
            SymbolPath: "tests/Suite.cs",
            Framework: "xunit",
            Source: "ct-provider:dotnet"));
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:changed", "Changed", "src/Changed.cs"));
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:case-a", "Case", "tests/Suite.cs", isTest: true));
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:case-b", "Case", "tests/Suite.cs", isTest: true));
        facts.Tests.Add(new CtImpactedSymbol(
            "sym:case-a",
            "Case",
            "method",
            "tests/Suite.cs",
            IsTest: true,
            Hop: 1,
            EdgeKind: "calls",
            EdgeSource: "relationship",
            TestCase: true,
            TestContainer: false,
            TestLifecycle: false,
            TestEvidenceStatus: "current"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["src/Changed.cs"]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:case"], result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
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
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:safepath", "SafePath", "src/Eros.Core/SafePath.cs"));
        AddCurrentFileFacts(facts, "src/Eros.Core/SafePath.cs");
        var selector = new ContinuousTestImpactSelector(store, facts);

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

    [Fact]
    public void Select_reads_only_the_requested_project_and_keeps_unmapped_cases_unknown()
    {
        using ContinuousTestStore store = OpenStore();
        string project = Path.Combine(_dir, "A.csproj");
        string otherProject = Path.Combine(_dir, "B.csproj");
        SeedProviderCase(store, "tc:a", "ATests.test", "ATests.test", "test", "tests/A.cs", projectPath: project);
        SeedProviderCase(store, "tc:b", "BTests.test", "BTests.test", "test", "tests/B.cs", projectPath: otherProject);
        SeedProviderCase(store, "tc:unknown", "UnknownTests.test", "UnknownTests.test", "test", "tests/U.cs");
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, "src/Sample/Calculator.cs");
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.SelectAtRevision(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            WorkspaceScope: true,
            ProjectPath: project),
            new CtFreshnessKey("gen-1", 1));
        ContinuousTestSelectionResult otherResult = selector.SelectAtRevision(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            WorkspaceScope: true,
            ProjectPath: otherProject),
            new CtFreshnessKey("gen-1", 1));

        Assert.Equal(["tc:a"], result.SelectedTestCaseIds);
        Assert.Equal(["tc:a"], result.StaleTestCaseIds);
        Assert.DoesNotContain("tc:b", result.SelectedTestCaseIds);
        Assert.DoesNotContain("tc:unknown", result.SelectedTestCaseIds);
        Assert.Equal(["tc:b"], otherResult.SelectedTestCaseIds);
    }

    [Fact]
    public void Selection_snapshot_reuses_cases_until_discovery_invalidates_it()
    {
        using ContinuousTestStore store = OpenStore();
        string project = Path.Combine(_dir, "A.csproj");
        SeedProviderCase(store, "tc:a", "ATests.test", "ATests.test", "test", "tests/A.cs", projectPath: project);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());
        var request = new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            WorkspaceScope: true,
            ProjectPath: project);
        var key = new CtFreshnessKey("gen-1", 1);

        Assert.Equal(["tc:a"], selector.SelectAtRevision(request, key).SelectedTestCaseIds);
        SeedProviderCase(store, "tc:new", "BTests.test", "BTests.test", "test", "tests/B.cs", projectPath: project);
        Assert.Equal(["tc:a"], selector.SelectAtRevision(request, key).SelectedTestCaseIds);

        selector.InvalidateSelectionSnapshot(Workspace);
        Assert.Equal(["tc:a", "tc:new"], selector.SelectAtRevision(request, key).SelectedTestCaseIds);
    }

    [Fact]
    public void Selection_reads_live_status_after_a_same_revision_case_mutation()
    {
        using ContinuousTestStore store = OpenStore();
        string project = Path.Combine(_dir, "A.csproj");
        SeedProviderCase(store, "tc:a", "ATests.test", "ATests.test", "test", "tests/A.cs", projectPath: project);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());
        var request = new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["README.md"],
            ProjectPath: project);
        var key = new CtFreshnessKey("gen-1", 1);

        Assert.Equal(["tc:a"], selector.SelectAtRevision(request, key).StaleTestCaseIds);
        SeedCommittedResult(store, "tc:a", identity: "gen-1", revision: 1);
        Assert.Empty(selector.SelectAtRevision(request, key).StaleTestCaseIds);
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
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:app", "App", "src/App.cs"));
        AddCurrentFileFacts(facts, "src/App.cs");
        var selector = new ContinuousTestImpactSelector(store, facts);

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
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:axum",
            "extract_axum_route",
            "crates/julie-extractors/src/base/framework_structural_facts/axum.rs",
            language: "rust"));
        AddCurrentFileFacts(facts, "crates/julie-extractors/src/base/framework_structural_facts/axum.rs");
        var selector = new ContinuousTestImpactSelector(store, facts);

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
    public void Select_maps_unique_go_impacted_test_by_package_directory_and_name()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/go.mod";
        SeedGoProviderCase(store, "tc:go", "TestAdd", "pkg", projectPath);
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:changed", "Changed", "src/compute.go", language: "go"));
        AddCurrentFileFacts(facts, "src/compute.go");
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["src/compute.go"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:go-test",
                    Path: "pkg/math_test.go",
                    Name: "TestAdd",
                    TestCase: true,
                    EvidenceStatus: "current"),
            ]));

        Assert.Equal(["tc:go"], result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        Assert.Equal("impacted_test", Assert.Single(result.Evidence).Tier);
    }

    [Fact]
    public void Select_maps_nested_go_impacted_test_from_a_workspace_relative_path()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/services/foo/go.mod";
        SeedGoProviderCase(store, "tc:go-nested", "TestAdd", "services/foo/pkg", projectPath);
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:changed", "Changed", "services/foo/src/compute.go", language: "go"));
        AddCurrentFileFacts(facts, "services/foo/src/compute.go");
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["services/foo/src/compute.go"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:go-nested-test",
                    Path: "services/foo/pkg/math_test.go",
                    Name: "TestAdd",
                    TestCase: true,
                    EvidenceStatus: "current"),
            ]));

        Assert.Equal(["tc:go-nested"], result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
    }

    [Fact]
    public void Select_fails_closed_when_go_impacted_test_mapping_is_ambiguous()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/go.mod";
        SeedGoProviderCase(store, "tc:go-a", "TestAdd", "pkg", projectPath);
        SeedGoProviderCase(store, "tc:go-b", "TestAdd", "pkg", projectPath);
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:changed", "Changed", "src/compute.go", language: "go"));
        AddCurrentFileFacts(facts, "src/compute.go");
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["src/compute.go"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:go-test",
                    Path: "pkg/math_test.go",
                    Name: "TestAdd",
                    TestCase: true,
                    EvidenceStatus: "current"),
            ]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:go-a", "tc:go-b"], result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.False(result.MayExecute);
    }

    [Fact]
    public void Select_fails_closed_when_go_impacted_test_lacks_package_directory_evidence()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/go.mod";
        store.PutTestCase(new ContinuousTestCase(
            Id: "tc:go",
            WorkspaceId: Workspace,
            Name: "TestAdd",
            QualifiedName: "example.com/math/TestAdd",
            Selector: "TestAdd",
            FilePath: null,
            Framework: "go",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:go",
            Confidence: 1.0,
            Metadata: new Dictionary<string, object?>
            {
                ["ct_project_path"] = projectPath,
            }));
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:changed", "Changed", "src/compute.go", language: "go"));
        AddCurrentFileFacts(facts, "src/compute.go");
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["src/compute.go"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:go-test",
                    Path: "pkg/math_test.go",
                    Name: "TestAdd",
                    TestCase: true,
                    EvidenceStatus: "current"),
            ]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.False(result.MayExecute);
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
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:upload", "EdrFileUpload", "src/Client/Features/Edr/EdrFileUpload.razor"));
        AddCurrentFileFacts(facts, "src/Client/Features/Edr/EdrFileUpload.razor");
        var selector = new ContinuousTestImpactSelector(store, facts);

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
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:upload", "EdrFileUpload", "src/Client/Features/Edr/EdrFileUpload.razor"));
        AddCurrentFileFacts(facts, "src/Client/Features/Edr/EdrFileUpload.razor");
        var selector = new ContinuousTestImpactSelector(store, facts);

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
        var facts = new FakeMillerFactSource();
        facts.FileFacts.Add(new CtFileFact("Fixture/MathOps.cs", "csharp", "blake3:mathops", "indexed", false, true));
        var selector = new ContinuousTestImpactSelector(store, facts);

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
        var facts = new FakeMillerFactSource();
        facts.FileFacts.Add(new CtFileFact("Fixture/MathOps.cs", "csharp", "blake3:mathops", "indexed", false, true));
        var selector = new ContinuousTestImpactSelector(store, facts);

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
        var facts = new FakeMillerFactSource();
        facts.FileFacts.Add(new CtFileFact("Fixture/MathOps.cs", "csharp", "blake3:mathops", "indexed", false, true));
        var selector = new ContinuousTestImpactSelector(store, facts);

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
    /// Defect D5 (branch-gate scale suite): a stored case can carry its residence in the row's
    /// file_path column (real xunit.v3 FQN display name plus a discovered file path) WITHOUT a
    /// source_path metadata key. The by-path hint bucket must index file_path too; before the fix
    /// it was keyed on source_path metadata only, so the hint tier read the case as unmappable
    /// and the whole selection failed closed to Unknown — while the changed-test-file tier ranked
    /// the very same case by the very same path.
    /// </summary>
    [Fact]
    public void Select_maps_impacted_hint_to_case_stored_with_file_path_but_no_source_path_metadata()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/Sample.Tests.csproj";
        SeedProviderCase(
            store,
            "tc:adds",
            selector: "Sample.Tests.CalculatorTests.Adds",
            qualifiedName: "Sample.Tests.CalculatorTests.Adds",
            name: "Sample.Tests.CalculatorTests.Adds",
            sourcePath: null,
            projectPath: projectPath,
            className: "Sample.Tests.CalculatorTests",
            filePath: "tests/Sample.Tests/CalculatorTests.cs");
        SeedProviderCase(
            store,
            "tc:other",
            selector: "Sample.Tests.OtherTests.Works",
            qualifiedName: "Sample.Tests.OtherTests.Works",
            name: "Sample.Tests.OtherTests.Works",
            sourcePath: null,
            projectPath: projectPath,
            className: "Sample.Tests.OtherTests",
            filePath: "tests/Sample.Tests/OtherTests.cs");
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, "src/Sample/Calculator.cs");
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["src/Sample/Calculator.cs"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:adds",
                    Path: "tests/Sample.Tests/CalculatorTests.cs",
                    Name: "Adds",
                    TestCase: true,
                    EvidenceStatus: "current"),
            ]));

        Assert.Equal(["tc:adds"], result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        ContinuousTestSelectionEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("impacted_test", evidence.Tier);
        Assert.DoesNotContain("tc:other", result.SelectedTestCaseIds);
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
        AddCurrentFileFacts(facts, "src/payments/service.cs");
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

    [Fact]
    public void Select_owed_backlog_includes_a_stamped_red_and_excludes_a_standing_red()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:hit", "sym:test-hit", "tests/payments/HitTests.cs", "test_hit");
        SeedLinkedCase(store, "tc:red-owed", "sym:test-owed", "tests/other/OwedTests.cs", "test_owed");
        SeedLinkedCase(store, "tc:red-standing", "sym:test-standing", "tests/other/StandingTests.cs", "test_standing");
        SeedCommittedResult(store, "tc:red-owed", status: "failed");
        SeedCommittedResult(store, "tc:red-standing", status: "failed");
        store.MarkContinuousTestsStale(Workspace, ["tc:red-owed"], new CtFreshnessKey("gen-1", 2));
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:charge", "charge", "src/payments/service.cs"));
        AddCurrentFileFacts(facts, "src/payments/service.cs");
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
        Assert.Equal(["tc:hit", "tc:red-owed"], result.StaleTestCaseIds);
        Assert.DoesNotContain("tc:red-standing", result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
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
        facts.FileFacts.Add(new CtFileFact("src/Persistence.cs", "csharp", "blake3:persistence", "indexed", false, true));
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
    /// Review finding F2: one mapped impact hint must not vouch for the whole delta. The hint
    /// read proves what the RESOLVED symbols reach; it never proves that every changed path
    /// resolved to symbols. A mixed save (a mapped .cs plus a fixture asset the index cannot
    /// account for) previously skipped per-path accounting and produced a false Impacted while
    /// the fixture's dependent tests kept their watermarks.
    /// </summary>
    [Fact]
    public void Select_mixed_delta_with_unaccounted_path_fails_closed_despite_mapped_hint()
    {
        using ContinuousTestStore store = OpenStore();
        const string projectPath = "/repo/Sample.Tests.csproj";
        SeedProviderCase(
            store,
            "tc:adds",
            selector: "Sample.Tests.CalculatorTests.Adds",
            qualifiedName: "Sample.Tests.CalculatorTests.Adds",
            name: "Sample.Tests.CalculatorTests.Adds",
            sourcePath: null,
            projectPath: projectPath,
            className: "Sample.Tests.CalculatorTests",
            filePath: "tests/Sample.Tests/CalculatorTests.cs");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:calc", "Calculator", "src/Sample/Calculator.cs"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ProjectPath: projectPath,
            ChangedPaths: ["src/Sample/Calculator.cs", "tests/Sample.Tests/fixtures/Payload.dat"],
            ImpactedTests:
            [
                new ContinuousTestImpactedTest(
                    SymbolId: "sym:adds",
                    Path: "tests/Sample.Tests/CalculatorTests.cs",
                    Name: "Adds",
                    TestCase: true),
            ]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:adds"], result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.False(result.MayExecute);
    }

    /// <summary>
    /// Review finding F3 (general + security pass): images, fonts, .txt, .svg and other assets
    /// can be embedded resources, snapshot fixtures, or runtime config — they are NOT harmless.
    /// With nothing else accounting for the path, the honest outcome under the path-accounting
    /// rules is Unknown (fail closed). Only prose documentation stays KnownEmpty.
    /// </summary>
    [Fact]
    public void Select_image_only_change_fails_closed_when_nothing_accounts_for_it()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:fresh", "sym:test-fresh", "tests/other/FreshTests.cs", "test_fresh");
        SeedCommittedResult(store, "tc:fresh");
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["assets/Logo.png"]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:fresh"], result.StaleTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.False(result.MayExecute);
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
        AddCurrentFileFacts(facts, "src/payments/service.cs");
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
    public void Select_qml_change_uses_qt_quick_test_project_scope_without_function_precision()
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.Combine(_dir, "qml", "CMakeLists.txt");
        string evidenceRoot = Path.Combine(_dir, "qml", "tests");
        SeedQmlCase(store, "tc:qml-a", "A/basic", projectPath, evidenceRoot);
        SeedQmlCase(store, "tc:qml-b", "B/slow", projectPath, evidenceRoot);
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, Path.Combine(_dir, "qml", "ui", "Card.qml"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [Path.Combine(_dir, "qml", "ui", "Card.qml")],
            ProjectPath: projectPath));

        Assert.Equal(["tc:qml-a", "tc:qml-b"], result.SelectedTestCaseIds);
        Assert.All(result.Evidence, evidence => Assert.Equal("project_scope", evidence.Tier));
        Assert.All(result.Evidence, evidence =>
            Assert.Contains("CTest does not expose QML function ownership", evidence.Explanation));
    }

    [Theory]
    [InlineData("CMakeLists.txt")]
    [InlineData("cmake/QtQuickTest.cmake")]
    [InlineData("tests/runner.cpp")]
    public void Select_qt_quick_test_project_changes_do_not_select_unrelated_provider_cases(string changedPath)
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.Combine(_dir, "qml", "CMakeLists.txt");
        string evidenceRoot = Path.Combine(_dir, "qml", "tests");
        SeedQmlCase(store, "tc:qml", "Smoke/smoke", projectPath, evidenceRoot);
        SeedProviderCase(
            store,
            "tc:dotnet",
            "AppTests.test",
            "AppTests.test",
            "test",
            Path.Combine(_dir, "qml", "tests", "AppTests.cs"),
            projectPath: projectPath,
            framework: "xunit");
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, Path.Combine(_dir, "qml", changedPath));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [Path.Combine(_dir, "qml", changedPath)],
            ProjectPath: projectPath));

        Assert.Equal(["tc:qml"], result.SelectedTestCaseIds);
        Assert.DoesNotContain("tc:dotnet", result.SelectedTestCaseIds);
    }

    [Fact]
    public void Select_unrelated_qml_application_change_does_not_enter_qt_quick_test_project()
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.Combine(_dir, "qml", "CMakeLists.txt");
        string evidenceRoot = Path.Combine(_dir, "qml", "tests");
        SeedQmlCase(store, "tc:qml", "Smoke/smoke", projectPath, evidenceRoot);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [Path.Combine(_dir, "app", "Main.qml")]));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.Equal(["tc:qml"], result.StaleTestCaseIds);
    }

    [Theory]
    [InlineData("app.pro")]
    [InlineData("common.pri")]
    public void Select_qmake_project_change_selects_the_qt_quick_test_project_without_file_evidence(
        string changedPath)
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.Combine(_dir, "qml", "app.pro");
        string evidenceRoot = Path.Combine(_dir, "qml", "tests");
        SeedQmlCase(store, "tc:qml", "Smoke/smoke", projectPath, evidenceRoot);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [Path.Combine(_dir, "qml", changedPath)],
            ProjectPath: projectPath));

        Assert.Equal(["tc:qml"], result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        ContinuousTestSelectionEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("project_scope", evidence.Tier);
    }

    [Theory]
    [InlineData("go.mod")]
    [InlineData("go.sum")]
    public void Select_go_module_manifest_change_selects_the_module_tests_without_file_evidence(
        string changedManifest)
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.Combine(_dir, "go", "go.mod");
        SeedGoProviderCase(store, "tc:go", "TestAdd", Path.Combine(_dir, "go", "pkg"), projectPath);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [Path.Combine(_dir, "go", changedManifest)],
            ProjectPath: projectPath));

        Assert.Equal(["tc:go"], result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        ContinuousTestSelectionEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("project_scope", evidence.Tier);
    }

    [Theory]
    [InlineData("go.work")]
    [InlineData("go.work.sum")]
    public void Select_go_workspace_manifest_change_selects_member_module_tests(string changedManifest)
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.Combine(_dir, "go", "go.mod");
        string workspacePath = Path.Combine(_dir, "go.work");
        SeedGoProviderCase(store, "tc:go", "TestAdd", Path.Combine(_dir, "go", "pkg"), projectPath, workspacePath);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [Path.Combine(_dir, changedManifest)],
            ProjectPath: projectPath));

        Assert.Equal(["tc:go"], result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
    }

    [Theory]
    [InlineData("go.mod")]
    [InlineData("go.sum")]
    public void Select_go_parent_manifest_change_does_not_select_nested_module_tests(string changedManifest)
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.Combine(_dir, "nested", "go.mod");
        SeedGoProviderCase(store, "tc:go", "TestAdd", Path.Combine(_dir, "nested", "pkg"), projectPath);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [Path.Combine(_dir, changedManifest)],
            ProjectPath: projectPath));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.Equal(["tc:go"], result.StaleTestCaseIds);
    }

    [Fact]
    public void Select_go_workspace_manifest_change_does_not_select_non_member_module_tests()
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.Combine(_dir, "go", "go.mod");
        string workspacePath = Path.Combine(_dir, "other", "go.work");
        SeedGoProviderCase(store, "tc:go", "TestAdd", Path.Combine(_dir, "go", "pkg"), projectPath, workspacePath);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [Path.Combine(_dir, "go.work")],
            ProjectPath: projectPath));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.Equal(["tc:go"], result.StaleTestCaseIds);
    }

    [Fact]
    public void Select_go_manifest_change_outside_the_module_reads_unknown()
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.Combine(_dir, "go", "go.mod");
        SeedGoProviderCase(store, "tc:go", "TestAdd", Path.Combine(_dir, "go", "pkg"), projectPath);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [Path.Combine(_dir, "other", "go.mod")],
            ProjectPath: projectPath));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.Equal(["tc:go"], result.StaleTestCaseIds);
    }

    [Theory]
    [InlineData("CMakeLists.txt")]
    [InlineData("tests/runner.cpp")]
    [InlineData("tests/tst_smoke.cpp")]
    [InlineData("tests/tst_smoke.hpp")]
    public void Select_project_scoped_qml_changes_require_the_changed_path_to_be_in_the_project(
        string changedPath)
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.Combine(_dir, "qml", "CMakeLists.txt");
        string evidenceRoot = Path.Combine(_dir, "qml", "tests");
        SeedQmlCase(store, "tc:qml", "Smoke/smoke", projectPath, evidenceRoot);
        var selector = new ContinuousTestImpactSelector(store, new FakeMillerFactSource());

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [Path.Combine(_dir, "other", changedPath)],
            ProjectPath: projectPath));

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.Equal(["tc:qml"], result.StaleTestCaseIds);
    }

    [Theory]
    [InlineData("tests/tst_smoke.cpp")]
    [InlineData("tests/tst_smoke.hpp")]
    public void Select_quick_test_harness_source_and_header_changes_select_the_qml_project(
        string changedPath)
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.Combine(_dir, "qml", "CMakeLists.txt");
        string evidenceRoot = Path.Combine(_dir, "qml", "tests");
        SeedQmlCase(store, "tc:qml", "Smoke/smoke", projectPath, evidenceRoot);
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, Path.Combine(_dir, "qml", changedPath));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [Path.Combine(_dir, "qml", changedPath)],
            ProjectPath: projectPath));

        Assert.Equal(["tc:qml"], result.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        Assert.All(result.Evidence, evidence => Assert.Equal("project_scope", evidence.Tier));
    }

    [Fact]
    public void Select_unscoped_qml_change_chooses_only_the_matching_quick_test_project()
    {
        using ContinuousTestStore store = OpenStore();
        string projectA = Path.Combine(_dir, "qml-a", "CMakeLists.txt");
        string projectB = Path.Combine(_dir, "qml-b", "CMakeLists.txt");
        SeedQmlCase(store, "tc:qml-a", "A/basic", projectA, Path.Combine(_dir, "qml-a", "tests"));
        SeedQmlCase(store, "tc:qml-b", "B/slow", projectB, Path.Combine(_dir, "qml-b", "tests"));
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, Path.Combine(_dir, "qml-a", "ui", "Card.qml"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [Path.Combine(_dir, "qml-a", "ui", "Card.qml")]));

        Assert.Equal(["tc:qml-a"], result.SelectedTestCaseIds);
        Assert.DoesNotContain("tc:qml-b", result.SelectedTestCaseIds);
        Assert.All(result.Evidence, evidence => Assert.Equal("project_scope", evidence.Tier));
    }

    [Fact]
    public void Select_keeps_targeted_changed_test_file_over_project_scope_escalation()
    {
        using ContinuousTestStore store = OpenStore();
        string projectPath = Path.GetFullPath("/repo/tests/App.Tests/App.Tests.csproj");
        SeedLinkedCase(store, "tc:app-a", "sym:test-a", "tests/app_a.cs", "test_a", projectPath);
        SeedLinkedCase(store, "tc:app-b", "sym:test-b", "tests/app_b.cs", "test_b", projectPath);
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, "tests/app_a.cs");
        var selector = new ContinuousTestImpactSelector(store, facts);

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
        AddCurrentFileFacts(facts, "src/payments/service.cs");
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
        SeedLinkedCase(
            store,
            "tc:identifier",
            "sym:test-identifier",
            "tests/payments/test_service.cs",
            "test_identifier",
            typedIdentity: true);
        SeedLinkedCase(store, "tc:path", "sym:test-path", "tests/payments/test_service.cs", "test_path");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:charge", "charge", "src/payments/service.cs"));
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:test-identifier",
            "test_identifier",
            "tests/payments/test_service.cs",
            isTest: true));
        AddCurrentFileFacts(facts, "src/payments/service.cs", "tests/payments/test_service.cs");
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
    public void Select_ignores_identifier_references_from_an_unstored_sibling_test_symbol()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(
            store,
            "tc:adds",
            "sym:test-adds",
            "tests/payments/test_service.cs",
            "test_adds",
            typedIdentity: true);
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:test-adds", "test_adds", "tests/payments/test_service.cs", isTest: true));
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:test-positive", "test_positive", "tests/payments/test_service.cs", isTest: true));
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:value", "value", "tests/payments/test_service.cs", kind: "variable"));
        AddCurrentFileFacts(facts, "tests/payments/test_service.cs");
        facts.Identifiers.Add(FakeMillerFactSource.Identifier(
            "sym:test-positive",
            "sym:value",
            "tests/payments/test_service.cs"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["tests/payments/test_service.cs"]));

        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        Assert.Equal(["tc:adds"], result.SelectedTestCaseIds);
        Assert.DoesNotContain(result.Evidence, row => row.Tier == "identifier_reference");
    }

    [Fact]
    public void Select_trusts_a_resolved_test_symbol_as_the_backing_file_when_the_path_looks_plain()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:adds", "sym:adds", "UnitTests.vb", "Adds", language: "vbnet", typedIdentity: true);
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:adds", "Adds", "UnitTests.vb", isTest: true, language: "vbnet", kind: "method"));
        facts.Symbols.Add(FakeMillerFactSource.Symbol(
            "sym:first", "first", "UnitTests.vb", language: "vbnet", kind: "variable"));
        AddCurrentFileFacts(facts, "UnitTests.vb");
        facts.Identifiers.Add(FakeMillerFactSource.Identifier("sym:adds", "sym:first", "UnitTests.vb"));
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: ["UnitTests.vb"]));

        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        Assert.Equal(["tc:adds"], result.SelectedTestCaseIds);
    }

    [Fact]
    public void Select_prioritizes_test_cases_in_changed_test_files()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:changed", "sym:test-changed", "tests/payments/test_service.cs", "test_changed");
        SeedLinkedCase(store, "tc:other", "sym:test-other", "tests/payments/test_other.cs", "test_other");
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, "tests/payments/test_service.cs");
        var selector = new ContinuousTestImpactSelector(store, facts);

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
            ImpactedSymbols:
            [
                new ContinuousTestImpactedSymbol(
                    SymbolId: "sym:test-changed",
                    Path: "tests/payments/test_service.cs",
                    Name: "test_changed"),
            ]));

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
        AddCurrentFileFacts(facts, "src/payments/service.cs", "src/lib.cs");
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
        AddCurrentFileFacts(facts, changedPath);
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
        AddCurrentFileFacts(facts, changedPath);
        var selector = new ContinuousTestImpactSelector(store, facts);

        ContinuousTestSelectionResult result = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: Workspace,
            ChangedPaths: [changedPath]));

        Assert.Equal(["tc:csharp"], result.SelectedTestCaseIds);
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
        var facts = new FakeMillerFactSource();
        AddCurrentFileFacts(facts, changedPath);
        var selector = new ContinuousTestImpactSelector(store, facts);

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
        AddCurrentFileFacts(facts, "src/EdrFileUpload.py");
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
    public void Select_does_not_use_dotnet_test_class_stem_when_provider_case_has_no_source_path(bool hasClassMetadata)
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

        Assert.Empty(result.SelectedTestCaseIds);
        Assert.Equal(["tc:nunit"], result.StaleTestCaseIds);
        Assert.Empty(result.Evidence);
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void Select_uses_impact_test_linkage_as_explicit_linkage()
    {
        using ContinuousTestStore store = OpenStore();
        SeedLinkedCase(store, "tc:file-link", "sym:test-file-link", "tests/payments/test_service.cs", "test_service");
        var facts = new FakeMillerFactSource();
        facts.Symbols.Add(FakeMillerFactSource.Symbol("sym:service", "charge", "src/payments/service.cs"));
        AddCurrentFileFacts(facts, "src/payments/service.cs");
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

    private static void AddCurrentFileFacts(FakeMillerFactSource facts, params string[] paths)
    {
        foreach (string path in paths.Distinct(StringComparer.Ordinal))
        {
            facts.FileFacts.Add(new CtFileFact(
                path,
                ContinuousTestLanguageFamily.LabelFromPath(path),
                "blake3:test",
                "indexed",
                false,
                true));
        }
    }

    /// <summary>Commits one green result through the real run-completion path, so the case is
    /// committed-fresh at <c>(identity, revision)</c> the way production rows are.</summary>
    private static void SeedCommittedResult(
        ContinuousTestStore store,
        string testCaseId,
        string identity = "gen-1",
        long revision = 1,
        string status = "passed")
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
            Status: status,
            Results:
            [
                new ContinuousTestResult(
                    Id: runId + ":result",
                    WorkspaceId: Workspace,
                    TestCaseId: testCaseId,
                    TestRunId: runId,
                    Status: status,
                    ResultRevision: revisionText,
                    IndexIdentity: identity,
                    Revision: revision),
            ]));
    }

    private static void SeedQmlCase(
        ContinuousTestStore store,
        string testCaseId,
        string selector,
        string projectPath,
        string sourcePath)
    {
        store.PutTestCase(new ContinuousTestCase(
            Id: testCaseId,
            WorkspaceId: Workspace,
            Name: selector,
            QualifiedName: selector,
            Selector: selector,
            FilePath: sourcePath,
            Framework: "qt-quick-test",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:qml",
            Confidence: 1.0,
            Metadata: new Dictionary<string, object?>
            {
                ["ct_project_path"] = projectPath,
                ["source_path"] = sourcePath,
            }));
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
        string framework = "xunit",
        string? filePath = null)
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
            FilePath: filePath,
            Framework: framework,
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:dotnet",
            Confidence: 1.0,
            Metadata: metadata));
    }

    private static void SeedGoProviderCase(
        ContinuousTestStore store,
        string testCaseId,
        string name,
        string packageDirectory,
        string projectPath,
        string? workspacePath = null)
    {
        store.PutTestCase(new ContinuousTestCase(
            Id: testCaseId,
            WorkspaceId: Workspace,
            Name: name,
            QualifiedName: "example.com/math/" + name,
            Selector: name,
            FilePath: packageDirectory,
            Framework: "go",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:go",
            Confidence: 1.0,
            Metadata: new Dictionary<string, object?>
            {
                ["ct_project_path"] = projectPath,
                ["source_path"] = packageDirectory,
                ["package_dir"] = packageDirectory,
                ["gowork"] = workspacePath,
            }));
    }

    private static void SeedLinkedCase(
        ContinuousTestStore store,
        string testCaseId,
        string symbolId,
        string path,
        string name,
        string? projectPath = null,
        string language = "csharp",
        string? fileRole = null,
        bool typedIdentity = false)
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
            SymbolName: typedIdentity ? name : null,
            SymbolPath: typedIdentity ? path : null,
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
            SET is_test = $isTest, test_container = $container, test_lifecycle = 0
            WHERE symbol_id = $id;
            """;
        command.Parameters.AddWithValue("$container", container ? 1 : 0);
        command.Parameters.AddWithValue("$isTest", container ? 0 : 1);
        command.Parameters.AddWithValue("$id", symbolId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
