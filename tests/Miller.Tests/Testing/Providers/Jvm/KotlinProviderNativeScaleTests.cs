using Miller.Indexing;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Testing.Providers.Jvm;
using Xunit;

namespace Miller.Tests.Testing.Providers.Jvm;

[Trait("Category", "Scale")]
public sealed class KotlinProviderNativeScaleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-kotlin-native-").FullName;

    [Fact]
    public async Task Java_native_case_uses_real_extracted_identity_and_excludes_unrelated_failure()
    {
        CtProviderTestSupport.RequireJava();
        CtProviderTestSupport.RequireGradle();
        string julie = ScaleTestSupport.RequireJulieServer();
        string root = Path.Combine(_root, "java-project");
        Directory.CreateDirectory(root);
        string project = Path.Combine(root, "build.gradle");
        File.WriteAllText(project, "plugins { id 'java' }\nrepositories { mavenCentral() }\ndependencies { testImplementation 'junit:junit:4.13.2' }\ntest { useJUnit() }\n");
        File.WriteAllText(Path.Combine(root, "settings.gradle"), "rootProject.name = 'java-native'\n");
        string source = Path.Combine(root, "src", "test", "java", "sample");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "SelectedTest.java"), """
            package sample;
            public class SelectedTest {
                @org.junit.Test public void selected() {}
            }
            """);
        File.WriteAllText(Path.Combine(source, "UnrelatedTest.java"), """
            package sample;
            public class UnrelatedTest {
                @org.junit.Test public void fails() { throw new AssertionError("must not run"); }
            }
            """);
        string artifact = Path.Combine(_root, "java-symbols.db");
        var extract = new JulieExtractRunner(julie).Scan(root, artifact, force: true, jobs: 1);
        Assert.NotEqual("failed", extract.Status);
        IndexedSymbol symbol = Assert.Single(SqliteSymbolReader.Read(artifact), item => item.Name == "selected" && item.Language == "java");
        Assert.True(symbol.IsTest);
        var workspace = new ContinuousTestWorkspace("java-native", root, project,
            Path.Combine(root, ".miller", "ct-java-native"), Framework: "gradle");
        var provider = new JvmTestProvider(new TestProcessRunner());
        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        var selected = Assert.Single(cases, item => item.SymbolName == "selected");
        Assert.Equal(2, cases.Count);
        using var store = new ContinuousTestStore(Path.Combine(_root, "java-ct.db"));
        foreach (var testCase in cases)
            store.PutTestCase(new ContinuousTestCase(testCase.Id, workspace.WorkspaceId, testCase.SymbolName!,
                testCase.FullyQualifiedName, testCase.Selector, SymbolName: testCase.SymbolName,
                Source: "ct-provider:jvm", Framework: "gradle", Metadata: new Dictionary<string, object?>
                { ["ct_project_path"] = project }));
        using var facts = CtFactAdapter.OpenArtifact(artifact);
        CtNativeSymbolCandidates candidates = facts.NativeClassCandidates(["SelectedTest"]);
        Assert.False(candidates.Truncated);
        foreach (CtSymbolFact candidate in candidates.Symbols)
            Console.WriteLine($"Java candidate: id={candidate.SymbolId} name={candidate.Name} kind={candidate.Kind} parent={candidate.ParentId} path={candidate.FilePath} test={candidate.IsTest}");
        Assert.True(JvmTestTooling.TryDecodeCaseId(selected.Id, out JvmTestCaseIdentity identity));
        JvmNativeDeclarationResolver.Binding? binding = new JvmNativeDeclarationResolver(candidates.Symbols, candidates.Files!).Resolve(identity);
        Assert.True(binding is not null, string.Join(" | ", candidates.Symbols.Select(candidate =>
            $"{candidate.Name}:{candidate.Kind}:parent={candidate.ParentId}:path={candidate.FilePath}:test={candidate.IsTest}")));
        var selection = new ContinuousTestImpactSelector(store, new MillerFactSource(facts)).Select(
            new ContinuousTestImpactSelectionRequest(workspace.WorkspaceId, ProjectPath: project,
                ImpactedSymbols: [new ContinuousTestImpactedSymbol(SymbolId: symbol.SymbolId, Path: symbol.FilePath, Name: symbol.Name)]));
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, selection.Outcome);
        Assert.Equal([selected.Id], selection.SelectedTestCaseIds);
        var run = await provider.RunAsync(new ContinuousTestProviderRunRequest(workspace, "revision",
            IndexIdentity: "java-native", TestCaseIds: selection.SelectedTestCaseIds), TestContext.Current.CancellationToken);
        Assert.Equal("passed", run.Status);
        Assert.Equal(selected.Id, Assert.Single(run.CaseResults).TestCaseId);
        Console.WriteLine($"Java source evidence: symbol={symbol.SymbolId} language={symbol.Language} kind={symbol.Kind} is_test={symbol.IsTest} selected_method={selected.SymbolName}");
    }

    [Fact]
    public async Task Kotlin_native_case_uses_real_extracted_identity_and_excludes_unrelated_failure()
    {
        CtProviderTestSupport.RequireJava();
        CtProviderTestSupport.RequireGradle();
        string julie = ScaleTestSupport.RequireJulieServer();
        string root = Path.Combine(_root, "project");
        Directory.CreateDirectory(root);
        string project = Path.Combine(root, "build.gradle");
        File.WriteAllText(project, """
            plugins { id 'org.jetbrains.kotlin.jvm' version '2.3.20' }
            repositories { mavenCentral() }
            dependencies { testImplementation 'junit:junit:4.13.2' }
            java { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
            kotlin { compilerOptions { jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17 } }
            test { useJUnit() }
            """);
        File.WriteAllText(Path.Combine(root, "settings.gradle"), "rootProject.name = 'kotlin-native'\n");
        File.WriteAllText(Path.Combine(root, "gradle.properties"), "kotlin.compiler.execution.strategy=in-process\norg.gradle.workers.max=2\n");
        string source = Path.Combine(root, "src", "test", "kotlin", "sample");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "SelectedTest.kt"), """
            package sample
            import org.junit.Test
            class SelectedTest {
                @Test fun selected() { org.junit.Assert.assertTrue(true) }
            }
            """);
        File.WriteAllText(Path.Combine(source, "UnrelatedTest.kt"), """
            package sample
            import org.junit.Test
            class UnrelatedTest {
                @Test fun fails() { throw AssertionError("must not run") }
            }
            """);
        string artifact = Path.Combine(_root, "symbols.db");
        var extract = new JulieExtractRunner(julie).Scan(root, artifact, force: true, jobs: 1);
        Assert.NotEqual("failed", extract.Status);
        IndexedSymbol symbol = Assert.Single(SqliteSymbolReader.Read(artifact), item => item.Name == "selected" && item.Language == "kotlin");
        Assert.True(symbol.IsTest);
        Console.WriteLine($"Kotlin source evidence: symbol={symbol.SymbolId} language={symbol.Language} kind={symbol.Kind} is_test={symbol.IsTest} status={symbol.TestEvidenceStatus}");
        var workspace = new ContinuousTestWorkspace("kotlin-native", root, project,
            Path.Combine(root, ".miller", "ct-kotlin-native"), Framework: "gradle");
        var provider = new JvmTestProvider(new TestProcessRunner());
        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        var selected = Assert.Single(cases, item => item.SymbolName == "selected");
        Assert.Equal(2, cases.Count);
        using var store = new ContinuousTestStore(Path.Combine(_root, "ct.db"));
        foreach (var testCase in cases)
            store.PutTestCase(new ContinuousTestCase(testCase.Id, workspace.WorkspaceId, testCase.SymbolName!,
                testCase.FullyQualifiedName, testCase.Selector, FilePath: testCase.SymbolPath,
                SymbolName: testCase.SymbolName, SymbolPath: testCase.SymbolPath, Framework: testCase.Framework,
                Source: "ct-provider:jvm", Metadata: new Dictionary<string, object?>
                {
                    ["source_path"] = testCase.SourcePath,
                    ["file_language"] = "kotlin",
                    ["ct_project_path"] = project,
                }));
        using var facts = CtFactAdapter.OpenArtifact(artifact);
        var selector = new ContinuousTestImpactSelector(store, new MillerFactSource(facts));
        var selection = selector.Select(new ContinuousTestImpactSelectionRequest(workspace.WorkspaceId,
            ProjectPath: project, ImpactedSymbols: [new ContinuousTestImpactedSymbol(SymbolId: symbol.SymbolId, Path: symbol.FilePath, Name: symbol.Name)]));
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, selection.Outcome);
        Assert.Equal([selected.Id], selection.SelectedTestCaseIds);
        var run = await provider.RunAsync(new ContinuousTestProviderRunRequest(workspace,
            "revision", IndexIdentity: "kotlin-native", TestCaseIds: selection.SelectedTestCaseIds), TestContext.Current.CancellationToken);
        Assert.Equal("passed", run.Status);
        Assert.Equal(selected.Id, Assert.Single(run.CaseResults).TestCaseId);
    }

    [Fact]
    public async Task Scala_test_body_uses_real_extracted_identity_to_select_only_its_native_suite()
    {
        CtProviderTestSupport.RequireJava();
        CtProviderTestSupport.RequireSbt();
        string julie = ScaleTestSupport.RequireJulieServer();
        string root = Path.Combine(_root, "scala-project");
        Directory.CreateDirectory(root);
        string project = Path.Combine(root, "build.sbt");
        File.WriteAllText(project, "scalaVersion := \"2.13.14\"\nlibraryDependencies += \"org.scalatest\" %% \"scalatest\" % \"3.2.18\" % Test\n");
        string source = Path.Combine(root, "src", "test", "scala");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Arbitrary.scala"), """
            package sample
            import org.scalatest.funsuite.AnyFunSuite
            class SelectedSuite extends AnyFunSuite { test("selected") { assert(1 + 1 == 2) } }
            class UnrelatedSuite extends AnyFunSuite { test("fails") { assert(false) } }
            """);
        string artifact = Path.Combine(_root, "scala-symbols.db");
        var extract = new JulieExtractRunner(julie).Scan(root, artifact, force: true, jobs: 1);
        Assert.NotEqual("failed", extract.Status);
        IndexedSymbol symbol = Assert.Single(SqliteSymbolReader.Read(artifact), item => item.Name == "selected" && item.Language == "scala");
        Assert.True(symbol.IsTest);
        var workspace = new ContinuousTestWorkspace("scala-native", root, project,
            Path.Combine(root, ".miller", "ct-scala-native"), Framework: "sbt");
        var provider = new JvmTestProvider(new SbtTestBackend(new TestProcessRunner()));
        var cases = await provider.DiscoverAsync(workspace, TestContext.Current.CancellationToken);
        var selected = Assert.Single(cases, item => item.Selector == "sample.SelectedSuite");
        Assert.Equal(2, cases.Count);
        using var store = new ContinuousTestStore(Path.Combine(_root, "scala-ct.db"));
        foreach (var testCase in cases)
            store.PutTestCase(new ContinuousTestCase(testCase.Id, workspace.WorkspaceId, testCase.SymbolName!,
                testCase.FullyQualifiedName, testCase.Selector, SymbolName: testCase.SymbolName,
                Source: "ct-provider:jvm", Framework: "sbt", Metadata: new Dictionary<string, object?>
                { ["ct_project_path"] = project }));
        using var facts = CtFactAdapter.OpenArtifact(artifact);
        var selection = new ContinuousTestImpactSelector(store, new MillerFactSource(facts)).Select(
            new ContinuousTestImpactSelectionRequest(workspace.WorkspaceId, ProjectPath: project,
                ImpactedSymbols: [new ContinuousTestImpactedSymbol(SymbolId: symbol.SymbolId, Path: symbol.FilePath, Name: symbol.Name)]));
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, selection.Outcome);
        Assert.Equal([selected.Id], selection.SelectedTestCaseIds);
        var run = await provider.RunAsync(new ContinuousTestProviderRunRequest(workspace, "revision",
            IndexIdentity: "scala-native", TestCaseIds: selection.SelectedTestCaseIds), TestContext.Current.CancellationToken);
        Assert.Equal("passed", run.Status);
        Assert.Equal(selected.Id, Assert.Single(run.CaseResults).TestCaseId);
        Console.WriteLine($"Scala source evidence: symbol={symbol.SymbolId} language={symbol.Language} kind={symbol.Kind} is_test={symbol.IsTest} selected_suite={selected.Selector}");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
