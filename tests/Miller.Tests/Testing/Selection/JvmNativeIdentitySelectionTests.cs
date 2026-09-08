using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Testing.Providers.Jvm;
using Xunit;

namespace Miller.Tests.Testing.Selection;

public sealed class JvmNativeIdentitySelectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-jvm-identity-").FullName;

    [Theory]
    [InlineData("java", "Unrelated.java", false)]
    [InlineData("kotlin", "Arbitrary.kt", false)]
    [InlineData("scala", "Mixed.scala", false)]
    [InlineData("java", "Unrelated.java", true)]
    [InlineData("kotlin", "Arbitrary.kt", true)]
    [InlineData("scala", "Mixed.scala", true)]
    public void Pathless_native_class_and_method_bind_exact_extracted_scope(string language, string path, bool nested)
    {
        using var store = new ContinuousTestStore(Path.Combine(_root, "ct.db"));
        string project = Path.Combine(_root, "build.gradle");
        string className = nested ? "sample.Outer$Suite" : "sample.Suite";
        string selected = Seed(store, project, className, "selected");
        Seed(store, project, "sample.Other", "selected");
        var facts = Facts(language, path, nested);
        var result = new ContinuousTestImpactSelector(store, facts).Select(new ContinuousTestImpactSelectionRequest("workspace",
            ProjectPath: project, ImpactedSymbols: [new(SymbolId: "method", Path: path, Name: "selected")]));
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        Assert.Equal([selected], result.SelectedTestCaseIds);
    }

    [Theory]
    [InlineData("duplicate_class")]
    [InlineData("unmapped_method")]
    [InlineData("missing_package")]
    [InlineData("truncated")]
    [InlineData("parse_errors")]
    [InlineData("stale_file")]
    public void Unmappable_native_source_evidence_never_returns_known_empty(string defect)
    {
        using var store = new ContinuousTestStore(Path.Combine(_root, "ct.db"));
        string project = Path.Combine(_root, "build.gradle");
        Seed(store, project, "sample.Suite", defect == "unmapped_method" ? "selected [dynamic]" : "selected");
        var facts = Facts("kotlin", "Arbitrary.kt", false);
        if (defect == "duplicate_class")
        {
            facts.FileFacts.Add(new("Other.kt", "kotlin", "hash", "indexed", false, true));
            facts.Symbols.AddRange(facts.Symbols.Where(x => x.Name != "Other").ToArray().Select(x => x with
            { SymbolId = "duplicate-" + x.SymbolId, FilePath = "Other.kt", ParentId = x.ParentId is null ? null : "duplicate-" + x.ParentId }));
        }
        if (defect == "truncated") facts.NativeCandidatesTruncated = true;
        if (defect == "parse_errors") facts.FileFacts[0] = facts.FileFacts[0] with { HasParseDiagnostics = true };
        if (defect == "stale_file") facts.FileFacts[0] = facts.FileFacts[0] with { Status = "error" };
        if (defect == "missing_package") facts.Symbols.RemoveAll(x => x.Kind == "namespace");
        var result = new ContinuousTestImpactSelector(store, facts).Select(new ContinuousTestImpactSelectionRequest("workspace",
            ProjectPath: project, ImpactedSymbols: [new(SymbolId: "method", Path: "Arbitrary.kt", Name: "selected")]));
        Assert.Equal(ContinuousTestSelectionOutcome.Unknown, result.Outcome);
        Assert.Empty(result.SelectedTestCaseIds);
    }

    [Fact]
    public void Scala_class_case_covers_its_extracted_test_body_without_selecting_another_suite()
    {
        using var store = new ContinuousTestStore(Path.Combine(_root, "ct.db"));
        string project = Path.Combine(_root, "build.sbt");
        string selected = Seed(store, project, "sample.Suite", JvmTestBackendIds.ClassCaseSentinel, "sbt");
        Seed(store, project, "sample.Other", JvmTestBackendIds.ClassCaseSentinel, "sbt");
        var facts = Facts("scala", "Arbitrary.scala", false);
        var result = new ContinuousTestImpactSelector(store, facts).Select(new ContinuousTestImpactSelectionRequest("workspace",
            ProjectPath: project, ImpactedSymbols: [new(SymbolId: "method", Path: "Arbitrary.scala", Name: "selected")]));
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        Assert.Equal([selected], result.SelectedTestCaseIds);
    }

    [Fact]
    public void Cross_file_parent_chain_cannot_bind_a_native_identity()
    {
        CtSymbolFact suite = FakeMillerFactSource.Symbol("suite", "Suite", "Suite.scala", false, "scala") with
        {
            Kind = "class",
            ParentId = "package",
        };
        CtSymbolFact package = FakeMillerFactSource.Symbol("package", "sample", "Package.scala", false, "scala") with
        {
            Kind = "namespace",
        };
        CtSymbolFact method = FakeMillerFactSource.Symbol("method", "selected", "Suite.scala", true, "scala") with
        {
            ParentId = "suite",
        };
        var resolver = new JvmNativeDeclarationResolver(
            [suite, package, method],
            [new("Suite.scala", "scala", "hash", "indexed", false, true)]);

        JvmNativeDeclarationResolver.Binding? binding = resolver.Resolve(
            new JvmTestCaseIdentity("workspace", "build.sbt", "sbt", "sample.Suite", "selected"));

        Assert.Null(binding);
    }

    [Fact]
    public void Shared_source_suite_body_selects_native_suite_cases_in_each_project()
    {
        using var store = new ContinuousTestStore(Path.Combine(_root, "ct.db"));
        string first = Seed(store, Path.Combine(_root, "one", "build.sbt"), "sample.Suite", JvmTestBackendIds.ClassCaseSentinel, "sbt");
        string second = Seed(store, Path.Combine(_root, "two", "build.sbt"), "sample.Suite", JvmTestBackendIds.ClassCaseSentinel, "sbt");
        var facts = Facts("scala", "Shared.scala", false);
        var result = new ContinuousTestImpactSelector(store, facts).Select(new ContinuousTestImpactSelectionRequest("workspace",
            ImpactedSymbols: [new(SymbolId: "method", Path: "Shared.scala", Name: "selected")]));
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, result.Outcome);
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), result.SelectedTestCaseIds.Order(StringComparer.Ordinal));
    }

    private static string Seed(ContinuousTestStore store, string project, string className, string method, string backend = "gradle")
    {
        string id = JvmTestTooling.EncodeCaseId("workspace", project, backend, className, method);
        store.PutTestCase(new(id, "workspace", method, className + "." + method, className + "." + method,
            SymbolName: method, Source: "ct-provider:jvm", Framework: backend,
            Metadata: new Dictionary<string, object?> { ["ct_project_path"] = project }));
        return id;
    }

    private static FakeMillerFactSource Facts(string language, string path, bool nested)
    {
        var facts = new FakeMillerFactSource();
        facts.FileFacts.Add(new(path, language, "hash", "indexed", false, true));
        facts.Symbols.Add(FakeMillerFactSource.Symbol("package", "sample", path, false, language) with { Kind = "namespace" });
        if (nested) facts.Symbols.Add(FakeMillerFactSource.Symbol("outer", "Outer", path, false, language) with { Kind = "class" });
        facts.Symbols.Add(FakeMillerFactSource.Symbol("suite", "Suite", path, false, language) with { Kind = "class", ParentId = nested ? "outer" : null });
        facts.Symbols.Add(FakeMillerFactSource.Symbol("other", "Other", path, false, language) with { Kind = "class" });
        facts.Symbols.Add(FakeMillerFactSource.Symbol("method", "selected", path, true, language) with { ParentId = "suite" });
        facts.Symbols.Add(FakeMillerFactSource.Symbol("other-method", "selected", path, true, language) with { ParentId = "other" });
        return facts;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
