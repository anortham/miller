using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

[Trait("Category", "Scale")]
public sealed class ContinuousTestDaemonEngineScaleTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-engine-scale-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Change_selection_execution_reaches_green_on_a_real_dotnet_provider()
    {
        CtProviderTestSupport.RequireDotnet();

        CancellationToken ct = TestContext.Current.CancellationToken;
        string workspaceRoot = Path.Combine(_dir, "repo");
        string projectDir = Path.Combine(workspaceRoot, "tests", "Sample.Tests");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "Sample.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="3.2.2" />
              </ItemGroup>
            </Project>
            """,
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "CalculatorTests.cs"),
            """
            using Xunit;

            namespace Sample.Tests;

            public sealed class CalculatorTests
            {
                [Fact]
                public void Adds() => Assert.Equal(2, 1 + 1);
            }
            """,
            ct);

        string projectPath = Path.Combine(projectDir, "Sample.Tests.csproj");
        var workspace = new ContinuousTestWorkspace(
            "ws:scale",
            workspaceRoot,
            projectPath,
            Path.Combine(workspaceRoot, ".miller", "ct", "build", "proj"));
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(workspaceRoot));
        var provider = new DotnetTestProvider(new TestProcessRunner());
        IReadOnlyList<ProviderTestCase> cases = await provider.DiscoverAsync(workspace, ct);
        ProviderTestCase testCase = Assert.Single(cases);
        store.PutTestCase(new ContinuousTestCase(
            testCase.Id,
            workspace.WorkspaceId,
            testCase.DisplayName,
            testCase.FullyQualifiedName,
            testCase.Selector,
            FilePath: "tests/Sample.Tests/CalculatorTests.cs",
            Framework: testCase.Framework,
            Source: "ct-provider:dotnet",
            Metadata: new Dictionary<string, object?>
            {
                ["ct_project_path"] = workspace.ProjectPath,
            }));

        var facts = new Miller.Tests.Testing.Selection.FakeMillerFactSource
        {
            Current = new Miller.Indexing.Testing.CtIndexCursor("gen-scale", 3),
        };
        facts.FileFacts.Add(new Miller.Indexing.Testing.CtFileFact(
            "tests/Sample.Tests/CalculatorTests.cs",
            "csharp",
            "blake3:calculator",
            "indexed",
            false,
            true));
        facts.Symbols.Add(Miller.Tests.Testing.Selection.FakeMillerFactSource.Symbol(
            "sym:calc", "Adds", "tests/Sample.Tests/CalculatorTests.cs", isTest: true));
        facts.Tests.Add(Miller.Tests.Testing.Selection.FakeMillerFactSource.Hit(
            testCase.Id, "Adds", "tests/Sample.Tests/CalculatorTests.cs", isTest: true));
        var selector = new ContinuousTestImpactSelector(store, facts);
        var coordinator = new ContinuousTestCoordinator(provider, store);
        var queue = new ContinuousTestDaemonQueue(store, selector, coordinator);
        queue.Enqueue(new ContinuousTestDaemonChange(
            workspace,
            "3",
            "gen-scale",
            ChangedPaths: ["tests/Sample.Tests/CalculatorTests.cs"],
            ImpactedTests: [new ContinuousTestImpactedTest(Name: "Adds", Path: "tests/Sample.Tests/CalculatorTests.cs")],
            DeltaCompleteness: ContinuousTestDeltaCompleteness.Complete,
            DeltaFromRevision: 2,
            DeltaToRevision: 3));

        IReadOnlyList<ContinuousTestDaemonDrainResult> drained = await queue.DrainReadyAsync(
            DateTimeOffset.UtcNow,
            ct);
        Assert.Single(drained);
        var statuses = store.ListContinuousTestStatuses(workspace.WorkspaceId);
        var selected = new CtFreshnessKey("gen-scale", 3);
        Assert.Equal(
            ContinuousTestVerdict.Green,
            ContinuousTestFreshness.Evaluate(statuses, selected, watchHealthy: true));
        Assert.All(statuses, row => Assert.Equal(ContinuousTestState.Green, row.State));
    }
}

