using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Miller.Indexing;
using Miller.Indexing.Testing;
using Miller.Testing;
using Miller.Testing.Parsing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Miller.Tests.Testing.Providers.Dotnet;

[Trait("Category", "Scale")]
public sealed class DotnetProviderScaleTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-provider-scale-").FullName;

    private readonly HashSet<string> _ctTemps = new(StringComparer.Ordinal);
    private static readonly TimeSpan ProcessReadinessTimeout = TimeSpan.FromSeconds(30);

    public void Dispose()
    {
        BestEffortDelete(_dir);
        foreach (var temp in _ctTemps)
            BestEffortDelete(temp);
    }

    /// <summary>
    /// The real-toolchain proof for the xUnit v2 refusal: a genuine v2 project, built by a genuine
    /// <c>dotnet build</c>, produces the dll-without-executable shape the provider now names.
    ///
    /// <para>A fake runner can only assert the message; it cannot prove that a v2 build really leaves no
    /// self-executing assembly beside the dll. That claim is the whole basis of the classification, and it is
    /// what the field report met as a raw OS process error naming a missing file (2026-08-25).</para>
    ///
    /// <para>The project's csproj carries no <c>OutputType</c>, exactly as <c>dotnet new xunit</c> scaffolds
    /// it.</para>
    /// </summary>
    [Fact]
    public async Task A_real_xunit_v2_project_is_refused_by_name_rather_than_by_a_raw_process_error()
    {
        CtProviderTestSupport.RequireDotnet();

        CancellationToken ct = TestContext.Current.CancellationToken;
        string workspaceRoot = Path.Combine(_dir, "v2-repo");
        string projectDir = Path.Combine(workspaceRoot, "tests", "Sample.Tests");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "Sample.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
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
        Assert.Equal(
            ContinuousTestFrameworkSupport.XunitV2,
            ContinuousTestProjectInventory.Identify(workspaceRoot, "ws:v2", projectPath)?.Framework);

        var workspace = new ContinuousTestWorkspace(
            "ws:v2",
            workspaceRoot,
            projectPath,
            Path.Combine(_dir, "v2-ct-build"),
            Framework: "xunit");
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        var provider = new DotnetTestProvider(new TestProcessRunner());

        ContinuousTestProviderException exception =
            await Assert.ThrowsAsync<ContinuousTestProviderException>(() =>
                provider.DiscoverAsync(workspace, ct));

        Assert.Contains(ContinuousTestFrameworkSupport.XunitV2Reason, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Sample.Tests.dll", exception.Message, StringComparison.Ordinal);
        Assert.Contains("xunit.v3", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("An error occurred trying to start process", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The real-runner proof for <c>-preEnumerateTheories</c>, which a fake runner cannot give: without the
    /// flag xunit v3 reports one entry per test METHOD at discovery and folds every row of a theory onto one
    /// <c>TestCaseUniqueID</c> at run time. Measured on Miller's own suite, that was 6,233 discovered against
    /// 7,723 run.
    ///
    /// <para>This test builds a project whose test COUNT and METHOD count differ, so the two numbers cannot
    /// be confused. It fails if either command loses the flag: without it at discovery there are two cases
    /// instead of four; without it at run time the run reports ids the discovery never recorded.</para>
    /// </summary>
    [Fact]
    public async Task A_real_theory_is_discovered_and_run_as_one_case_per_row()
    {
        CtProviderTestSupport.RequireDotnet();

        CancellationToken ct = TestContext.Current.CancellationToken;
        string workspaceRoot = Path.Combine(_dir, "theory-repo");
        string projectDir = Path.Combine(workspaceRoot, "tests", "Sample.Tests");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "Sample.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
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

                [Theory]
                [InlineData(1)]
                [InlineData(2)]
                [InlineData(3)]
                public void Positive(int value) => Assert.True(value > 0);
            }
            """,
            ct);

        var workspace = new ContinuousTestWorkspace(
            "ws:theory",
            workspaceRoot,
            Path.Combine(projectDir, "Sample.Tests.csproj"),
            Path.Combine(_dir, "theory-ct-build"));
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        var provider = new DotnetTestProvider(new TestProcessRunner());

        IReadOnlyList<ProviderTestCase> discovered = await provider.DiscoverAsync(workspace, ct);
        string[] discoveredIds = discovered
            .Select(row => row.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        // Two methods, four tests. The theory's three rows are three cases, each carrying its argument.
        Assert.Equal(
            [
                "xunit:Sample.Tests.CalculatorTests.Adds",
                "xunit:Sample.Tests.CalculatorTests.Positive(value: 1)",
                "xunit:Sample.Tests.CalculatorTests.Positive(value: 2)",
                "xunit:Sample.Tests.CalculatorTests.Positive(value: 3)",
            ],
            discoveredIds);

        ProviderRunResult run = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: "store:theory",
                RunId: "run:theory",
                TestCaseIds: discoveredIds),
            ct);

        Assert.Equal(
            discoveredIds,
            run.CaseResults.Select(row => row.TestCaseId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.All(run.CaseResults, row => Assert.Equal("passed", row.Status));
    }

    [Fact]
    public async Task Runner_cancel_kills_the_entire_process_tree()
    {
        var rootPidPath = Path.Combine(_dir, "root.pid");
        var childPidPath = Path.Combine(_dir, "child.pid");
        var command = BuildBlockingTreeCommand(rootPidPath, childPidPath);
        var runner = new TestProcessRunner();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var runTask = runner.RunAsync(command, cancellation.Token);
        int? rootPid = null;
        int? childPid = null;

        try
        {
            rootPid = await WaitForPidFileAsync(rootPidPath, TestContext.Current.CancellationToken);
            childPid = await WaitForPidFileAsync(childPidPath, TestContext.Current.CancellationToken);
            Assert.False(runTask.IsCompleted);
            Assert.True(IsProcessAlive(rootPid.Value));
            Assert.True(IsProcessAlive(childPid.Value));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
            Assert.False(IsProcessAlive(rootPid.Value));
            Assert.False(IsProcessAlive(childPid.Value));
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await runTask;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                KillIfAlive(childPid);
                KillIfAlive(rootPid);
            }
        }
    }

    [Fact]
    public async Task Owned_background_process_termination_kills_the_root_and_child_tree()
    {
        var rootPidPath = Path.Combine(_dir, "owned-root.pid");
        var childPidPath = Path.Combine(_dir, "owned-child.pid");
        var runner = new TestProcessRunner();
        await using var process = runner.Start(BuildBlockingTreeCommand(rootPidPath, childPidPath));
        int? rootPid = null;
        int? childPid = null;

        try
        {
            rootPid = await WaitForPidFileAsync(rootPidPath, TestContext.Current.CancellationToken);
            childPid = await WaitForPidFileAsync(childPidPath, TestContext.Current.CancellationToken);

            process.TerminateProcessTree();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.False(IsProcessAlive(rootPid.Value));
            Assert.False(IsProcessAlive(childPid.Value));
        }
        finally
        {
            process.TerminateProcessTree();
            KillIfAlive(childPid);
            KillIfAlive(rootPid);
        }
    }

    [Fact]
    public async Task Dotnet_smoke_executes_a_tiny_fixture_and_parses_results()
    {
        CtProviderTestSupport.RequireDotnet();
        var ct = TestContext.Current.CancellationToken;
        var workspaceRoot = Path.Combine(_dir, "repo");
        var projectDir = Path.Combine(workspaceRoot, "tests", "Sample.Tests");
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

        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:scale",
            WorkspaceRoot: workspaceRoot,
            ProjectPath: Path.Combine(projectDir, "Sample.Tests.csproj"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build"));
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));

        var runner = new TestProcessRunner();
        var provider = new DotnetTestProvider(runner);

        var cases = await provider.DiscoverAsync(workspace, ct);
        var testCase = Assert.Single(cases);
        Assert.Equal("xunit:Sample.Tests.CalculatorTests.Adds", testCase.Id);
        Assert.Equal("xunit", testCase.Framework);

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "12",
                IndexIdentity: "store:scale-identity",
                RunId: "run:scale-smoke",
                TestCaseIds: [testCase.Id]),
            ct);

        Assert.Equal("passed", result.Status);
        Assert.Equal("run:scale-smoke", result.RunId);
        Assert.True(CtGenerationPaths.IsGenerationId(result.GenerationId));
        var caseResult = Assert.Single(result.CaseResults);
        Assert.Equal(testCase.Id, caseResult.TestCaseId);
        Assert.Equal("passed", caseResult.Status);
        Assert.Equal("12", caseResult.ResultRevision);
        Assert.Equal("store:scale-identity", caseResult.IndexIdentity);

        Assert.Empty(WorkspaceGeneratedEntries(workspaceRoot));
        Assert.False(Directory.Exists(Path.Combine(workspace.BuildOutputRoot, "bin")));
        Assert.False(Directory.Exists(Path.Combine(workspace.BuildOutputRoot, "obj")));
        Assert.True(IsContained(workspace.BuildOutputRoot, result.GenerationId is null
            ? workspace.BuildOutputRoot
            : CtGenerationPaths.For(workspace, result.GenerationId).GenerationRoot));
        if (OperatingSystem.IsWindows())
            Assert.True(result.GenerationId!.Length <= 16);
    }

    [Fact]
    public async Task Real_vb_fixture_discovers_runs_and_selects_with_julie_identity()
    {
        string dotnet = CtProviderTestSupport.RequireDotnet();
        string julie = ScaleTestSupport.RequireJulieServer();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repositoryFixture = Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "tests",
            "Miller.Tests",
            "Fixtures",
            "VbDotnetScale");
        string workspaceRoot = Path.Combine(_dir, "vb-repo");
        CopyFixture(repositoryFixture, workspaceRoot);
        string projectPath = Path.Combine(workspaceRoot, "VbDotnetScale.vbproj");
        string symbolsPath = Path.Combine(_dir, "vb-symbols.db");
        string ctDbPath = Path.Combine(_dir, "vb-ct.db");
        ExtractReport report = new JulieExtractRunner(julie).Scan(
            workspaceRoot,
            symbolsPath,
            force: true,
            jobs: 1);
        Assert.NotEqual("failed", report.Status);

        IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.Read(symbolsPath);
        IndexedSymbol julieCase = Assert.Single(
            symbols,
            symbol => symbol.FilePath == "UnitTests.vb" && symbol.Name == "Adds");
        Assert.Equal("vbnet", julieCase.Language);
        Assert.True(julieCase.TestEvidence.IsCase);

        ContinuousTestProject project = Assert.IsType<ContinuousTestProject>(
            ContinuousTestProjectInventory.Identify(workspaceRoot, "ws:vb", projectPath));
        Assert.Equal("mstest", project.Framework);
        var workspace = new ContinuousTestWorkspace(
            "ws:vb",
            workspaceRoot,
            projectPath,
            Path.Combine(_dir, "vb-state", "ct-build"),
            Framework: project.Framework,
            Metadata: project.Metadata);
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        var provider = new DotnetTestProvider(new TestProcessRunner(), dotnet);

        IReadOnlyList<ProviderTestCase> discovered = await provider.DiscoverAsync(workspace, ct);
        ProviderTestCase adds = Assert.Single(
            discovered,
            testCase => testCase.FullyQualifiedName == "VbDotnetScale.UnitTests.Adds");
        ProviderTestCase[] positiveCases = discovered
            .Where(testCase => testCase.FullyQualifiedName == "VbDotnetScale.UnitTests.Positive")
            .OrderBy(testCase => testCase.DisplayName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["Positive (1)", "Positive (2)"], positiveCases.Select(testCase => testCase.DisplayName));
        Assert.Equal(positiveCases.Length, positiveCases.Select(testCase => testCase.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("UnitTests.vb", adds.SymbolPath);
        Assert.Equal("Adds", adds.SymbolName);
        Assert.Equal("UnitTests.vb", adds.SourcePath);

        ProviderRunResult run = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                workspace,
                SelectedRevision: "vb-revision",
                IndexIdentity: "vb-index",
                RunId: "vb-run",
                TestCaseIds: [adds.Id, .. positiveCases.Select(testCase => testCase.Id)]),
            ct);
        Assert.Equal("passed", run.Status);
        Assert.Equal(
            [adds.Id, .. positiveCases.Select(testCase => testCase.Id)],
            run.CaseResults.Select(result => result.TestCaseId).Order(StringComparer.Ordinal));
        Assert.All(run.CaseResults, result => Assert.Equal("passed", result.Status));

        using var store = new ContinuousTestStore(ctDbPath);
        store.PutTestCase(new ContinuousTestCase(
            Id: adds.Id,
            WorkspaceId: "ws:vb",
            Name: adds.SymbolName!,
            QualifiedName: adds.FullyQualifiedName,
            Selector: adds.Selector,
            FilePath: adds.SymbolPath,
            SymbolName: adds.SymbolName,
            SymbolPath: adds.SymbolPath,
            Framework: adds.Framework,
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:dotnet",
            Metadata: new Dictionary<string, object?>
            {
                ["source_path"] = adds.SourcePath,
                ["file_language"] = "vbnet",
                ["ct_project_path"] = projectPath,
            }));
        using var facts = CtFactAdapter.OpenArtifact(symbolsPath);
        var selector = new ContinuousTestImpactSelector(store, new MillerFactSource(facts));
        ContinuousTestSelectionResult selection = selector.Select(new ContinuousTestImpactSelectionRequest(
            WorkspaceId: "ws:vb",
            ProjectPath: projectPath,
            ChangedPaths: ["UnitTests.vb"]));
        Assert.Equal([adds.Id], selection.SelectedTestCaseIds);
        Assert.Equal(ContinuousTestSelectionOutcome.Impacted, selection.Outcome);

        Assert.Equal(
            Snapshot(repositoryFixture),
            Snapshot(workspaceRoot));
    }

    private static void CopyFixture(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static IReadOnlyDictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "obj" or "bin" or ".miller"))
            .ToDictionary(
                file => Path.GetRelativePath(root, file),
                file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
                StringComparer.Ordinal);

    [Fact]
    public async Task Diagnostic_real_provider_inventory_records_generation_layout_and_results()
    {
        CtProviderTestSupport.RequireDotnet();
        var ct = TestContext.Current.CancellationToken;
        var workspaceRoot = Path.Combine(_dir, "layout-repo");
        var projectDir = Path.Combine(workspaceRoot, "tests", "Sample.Tests");
        var helperRoot = Path.Combine(workspaceRoot, "helpers");
        var runtimeRoot = Path.Combine(workspaceRoot, "runtime");
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(runtimeRoot);
        foreach (var helperName in new[] { "Helper.A", "Helper.B" })
        {
            var helperDirectory = Path.Combine(helperRoot, helperName);
            Directory.CreateDirectory(helperDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(helperDirectory, helperName + ".csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <Content Include="../../runtime/payload.bin" Link="runtimes/shared/payload.bin">
                      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
                    </Content>
                  </ItemGroup>
                </Project>
                """,
                ct);
        }
        await File.WriteAllBytesAsync(
            Path.Combine(runtimeRoot, "payload.bin"),
            new byte[16 * 1024 * 1024],
            ct);
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
                <ProjectReference Include="../../helpers/Helper.A/Helper.A.csproj" ReferenceOutputAssembly="false" />
                <ProjectReference Include="../../helpers/Helper.B/Helper.B.csproj" ReferenceOutputAssembly="false" />
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

        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:layout",
            WorkspaceRoot: workspaceRoot,
            ProjectPath: Path.Combine(projectDir, "Sample.Tests.csproj"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build"));
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        var provider = new DotnetTestProvider(new TestProcessRunner());

        var discovered = await provider.DiscoverAsync(workspace, ct);
        var testCase = Assert.Single(discovered);
        var discoveryGeneration = CtGenerationPaths.ResolveLatestOrFirst(workspace);
        var discoveryLaunchPath = Path.Combine(
            discoveryGeneration.OutDir,
            "Sample.Tests",
            "Sample.Tests" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        Assert.True(File.Exists(discoveryLaunchPath));
        var discoveryLaunchIdentity = HashFile(discoveryLaunchPath);
        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "layout-revision",
                IndexIdentity: "store:layout",
                RunId: "run:layout",
                TestCaseIds: [testCase.Id]),
            ct);

        Assert.Equal("passed", result.Status);
        var caseResult = Assert.Single(result.CaseResults);
        Assert.Equal(testCase.Id, caseResult.TestCaseId);
        Assert.Equal("passed", caseResult.Status);
        Assert.NotNull(result.GenerationId);

        var generation = CtGenerationPaths.For(workspace, result.GenerationId!);
        var launchPath = Path.Combine(
            generation.OutDir,
            "Sample.Tests",
            "Sample.Tests" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        Assert.True(File.Exists(launchPath));
        Assert.Equal(discoveryLaunchIdentity, HashFile(launchPath));

        var inventory = LayoutInventory.Create(workspace.BuildOutputRoot);
        var payloadFiles = inventory.Files
            .Where(file => file.RelativePath.EndsWith(
                Path.Combine("runtimes", "shared", "payload.bin"),
                StringComparison.Ordinal))
            .ToArray();
        Assert.True(payloadFiles.Length >= 2);
        Assert.Single(payloadFiles.Select(file => file.Identity).Distinct(StringComparer.Ordinal));
        if (payloadFiles.All(file => file.PhysicalIdentity is not null))
            Assert.Single(payloadFiles.Select(file => file.PhysicalIdentity).Distinct(StringComparer.Ordinal));
        Assert.True(inventory.UniqueBytes < inventory.Bytes * 3 / 4);
        if (DiskBytes(workspace.BuildOutputRoot) is { } diskBytes)
            Assert.True(diskBytes < inventory.Bytes * 3 / 4);
        Assert.False(Directory.Exists(Path.Combine(workspace.BuildOutputRoot, "bin")));
        Assert.False(Directory.Exists(Path.Combine(workspace.BuildOutputRoot, "obj")));
    }

    [Fact]
    public async Task Whole_suite_transport_accepts_real_xunit_verbose_junit()
    {
        CtProviderTestSupport.RequireDotnet();
        var ct = TestContext.Current.CancellationToken;
        var workspaceRoot = Path.Combine(_dir, "whole-suite-repo");
        var projectDir = Path.Combine(workspaceRoot, "tests", "Sample.Tests");
        Directory.CreateDirectory(projectDir);

        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "Sample.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
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

        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:whole-suite",
            WorkspaceRoot: workspaceRoot,
            ProjectPath: Path.Combine(projectDir, "Sample.Tests.csproj"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build"));
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        var provider = new DotnetTestProvider(new TestProcessRunner());

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-1",
                IndexIdentity: "store:whole-suite",
                RunId: "run:whole-suite",
                TestCaseIds: ["xunit:Sample.Tests.CalculatorTests.Adds"],
                WholeSuite: true),
            ct);

        Assert.Equal("passed", result.Status);
        Assert.Empty(result.CaseResults);
        Assert.NotNull(result.ResultArtifactPath);
        Assert.NotEmpty(JunitTestResultParser.Parse(result.ResultArtifactPath!).Cases);
    }

    [Fact]
    public async Task Generic_build_places_reference_output_assembly_false_helper_in_a_launchable_project_folder()
    {
        CtProviderTestSupport.RequireDotnet();
        var ct = TestContext.Current.CancellationToken;
        var workspaceRoot = Path.Combine(_dir, "reference-output-repo");
        var testProjectDir = Path.Combine(workspaceRoot, "tests", "Sample.Tests");
        var helperProjectDir = Path.Combine(workspaceRoot, "tools", "Helper");
        Directory.CreateDirectory(testProjectDir);
        Directory.CreateDirectory(helperProjectDir);

        await File.WriteAllTextAsync(
            Path.Combine(testProjectDir, "Sample.Tests.csproj"),
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
                <ProjectReference Include="../../tools/Helper/Helper.csproj" ReferenceOutputAssembly="false" />
              </ItemGroup>
            </Project>
            """,
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(testProjectDir, "CalculatorTests.cs"),
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
        await File.WriteAllTextAsync(
            Path.Combine(helperProjectDir, "Helper.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """,
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(helperProjectDir, "Program.cs"),
            "System.Console.WriteLine(\"helper-ran\");",
            ct);

        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:reference-output",
            WorkspaceRoot: workspaceRoot,
            ProjectPath: Path.Combine(testProjectDir, "Sample.Tests.csproj"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build"));
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));
        var provider = new DotnetTestProvider(new TestProcessRunner());

        var testCase = Assert.Single(await provider.DiscoverAsync(workspace, ct));
        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "1",
                IndexIdentity: "store:reference-output",
                RunId: "run:reference-output",
                TestCaseIds: [testCase.Id]),
            ct);

        Assert.Equal("passed", result.Status);
        Assert.NotNull(result.GenerationId);
        var generation = CtGenerationPaths.For(workspace, result.GenerationId!);
        var helperDirectory = Path.Combine(generation.OutDir, "Helper");
        var helperExecutable = Path.Combine(
            helperDirectory,
            "Helper" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        var helperAssembly = Path.Combine(helperDirectory, "Helper.dll");
        Assert.True(File.Exists(helperExecutable));
        Assert.True(File.Exists(helperAssembly));

        var startInfo = new ProcessStartInfo(helperExecutable)
        {
            WorkingDirectory = helperDirectory,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        string output = await process!.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("helper-ran", output.Trim());
    }

    private TestProcessCommand BuildBlockingTreeCommand(string rootPidPath, string childPidPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var powershell = CtProviderTestSupport.RequirePowerShell();
            return new TestProcessCommand(
                powershell,
                [
                    "-NoLogo",
                    "-NoProfile",
                    "-Command",
                    """
                    Set-Content -LiteralPath $env:MILLER_CT_ROOT_PID_PATH -Value $PID
                    $child = Start-Process -FilePath $env:MILLER_CT_POWERSHELL_PATH -ArgumentList '-NoLogo','-NoProfile','-Command','while ($true) { Start-Sleep -Seconds 60 }' -NoNewWindow -PassThru
                    Set-Content -LiteralPath $env:MILLER_CT_CHILD_PID_PATH -Value $child.Id
                    [Console]::Out.WriteLine('stdout-ready')
                    [Console]::Error.WriteLine('stderr-ready')
                    Wait-Process -Id $child.Id
                    """,
                ],
                _dir,
                new Dictionary<string, string?>
                {
                    ["MILLER_CT_ROOT_PID_PATH"] = rootPidPath,
                    ["MILLER_CT_CHILD_PID_PATH"] = childPidPath,
                    ["MILLER_CT_POWERSHELL_PATH"] = powershell,
                });
        }

        return new TestProcessCommand(
            "/bin/sh",
            [
                "-c",
                """
                printf '%s\n' "$$" > "$MILLER_CT_ROOT_PID_PATH"
                ( while :; do sleep 60; done ) &
                printf '%s\n' "$!" > "$MILLER_CT_CHILD_PID_PATH"
                printf 'stdout-ready\n'
                printf 'stderr-ready\n' >&2
                wait
                """,
            ],
            _dir,
            new Dictionary<string, string?>
            {
                ["MILLER_CT_ROOT_PID_PATH"] = rootPidPath,
                ["MILLER_CT_CHILD_PID_PATH"] = childPidPath,
            });
    }

    private static async Task<int> WaitForPidFileAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(ProcessReadinessTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(path, cancellationToken);
                    if (int.TryParse(text.Trim(), out var pid))
                        return pid;
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException($"PID file was not written: {path}");
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void KillIfAlive(int? pid)
    {
        if (pid is null)
            return;

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static readonly HashSet<string> BuildOutputNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        "TestResults",
    };

    private static string[] WorkspaceGeneratedEntries(string workspaceRoot)
    {
        if (!Directory.Exists(workspaceRoot))
            return [];

        return Directory
            .EnumerateFileSystemEntries(workspaceRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetRelativePath(workspaceRoot, path)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(BuildOutputNames.Contains))
            .ToArray();
    }

    private static bool IsContained(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.Ordinal)
               || string.Equals(fullPath, Path.GetFullPath(root), StringComparison.Ordinal);
    }

    private static void BestEffortDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record LayoutFile(string RelativePath, long Bytes, string Identity, string? PhysicalIdentity);

    private sealed class LayoutInventory
    {
        private LayoutInventory(IReadOnlyList<LayoutFile> files)
        {
            Files = files;
            Bytes = files.Sum(file => file.Bytes);
            var identities = files.All(file => file.PhysicalIdentity is not null)
                ? files.Select(file => file.PhysicalIdentity!).ToArray()
                : files.Select(file => file.Identity).ToArray();
            UniqueBytes = files
                .Select((file, index) => (file, identity: identities[index]))
                .GroupBy(pair => pair.identity, StringComparer.Ordinal)
                .Sum(group => group.First().file.Bytes);
        }

        public IReadOnlyList<LayoutFile> Files { get; }

        public long Bytes { get; }

        public long UniqueBytes { get; }

        public static LayoutInventory Create(string root)
        {
            var files = Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => new LayoutFile(
                    Path.GetRelativePath(root, path),
                    new FileInfo(path).Length,
                    HashFile(path),
                    PhysicalIdentity(path)))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray();
            return new LayoutInventory(files);
        }
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string? PhysicalIdentity(string path)
    {
        if (OperatingSystem.IsWindows())
            return null;

        var startInfo = new ProcessStartInfo
        {
            FileName = "stat",
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("%d:%i");
        startInfo.ArgumentList.Add(path);
        using var process = Process.Start(startInfo);
        if (process is null)
            return null;

        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && output.Length > 0 ? output : null;
    }

    private static long? DiskBytes(string root)
    {
        if (OperatingSystem.IsWindows())
            return null;

        var startInfo = new ProcessStartInfo
        {
            FileName = "du",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-sb");
        startInfo.ArgumentList.Add(root);
        using var process = Process.Start(startInfo);
        if (process is null)
            return null;

        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        string first = output
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        return process.ExitCode == 0
               && long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes)
            ? bytes
            : null;
    }
}
