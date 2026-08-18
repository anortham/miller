using Miller.Dashboard;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Semantic;
using Miller.Indexing.Store;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Hosting;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Miller.Tests.Indexing.Resolution;
using System.Text.Json;
using Xunit;

namespace Miller.Tests.Server;

[Trait("Category", "Scale")]
[Collection(Miller.Tests.Indexing.SqliteVecEnvironment.Name)]
public sealed class StoreWorkspaceIndexProviderScaleTests
{
    [Fact]
    public void ExplicitDowngradeOverrideRecoversAnUnreadableUnpublishedStore()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string directory = Path.Combine(Path.GetTempPath(), "miller-store-override-recovery-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "root");
        string home = Path.Combine(directory, "home");
        string artifact = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(
            Path.Combine(root, "OverrideRecovery.cs"),
            "namespace Example; public static class OverrideRecovery { public static int Value => 42; }");
        ScaleTestSupport.RunJulie(
            binary,
            "scan", "--root", root, "--db", artifact, "--level", "full", "--jobs", "1", "--json");

        string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(root);
        string workspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);
        string registryPath = Path.Combine(home, "workspaces.db");
        using var registry = WorkspaceRegistry.Open(registryPath);
        registry.UpsertSeen(workspaceId, "override-recovery", canonicalRoot, artifact);
        StoreFamilyBinding binding = new StoreFamilyResolver(
            registry,
            Path.Combine(home, "stores")).ResolveOrCreate(new WorkspaceRootFacts(
                workspaceId,
                canonicalRoot,
                CanonicalGitCommonDir: null,
                GitCommonDirCreatedAtUtc: null,
                WorkspaceRootIdentity.Capture(canonicalRoot)));
        string otherRoot = Path.Combine(directory, "other-root");
        Directory.CreateDirectory(otherRoot);
        File.WriteAllText(
            Path.Combine(otherRoot, "Published.cs"),
            "namespace Example; public static class Published { public static int Value => 7; }");
        ScaleTestSupport.RunJulie(
            binary,
            "store", "import", "--store", binding.StoreRoot,
            "--family", binding.FamilyId.ToString("D"),
            "--root", otherRoot, "--view", "published-view",
            "--level", "full", "--jobs", "1", "--json");
        Assert.True(File.Exists(Path.Combine(binding.StoreRoot, "coord.db")));
        Assert.Contains(
            Directory.EnumerateFiles(binding.StoreRoot, "store.db", SearchOption.AllDirectories),
            File.Exists);
        Assert.False(StoreArtifactVersionReader.RequiresRootRebind(artifact));
        StoreArtifactVersionReadException unreadable = Assert.Throws<StoreArtifactVersionReadException>(() =>
            StoreArtifactVersionReader.ReadForLeadership(artifact, ExtractBinaryVersionReader.TryRead));
        Assert.Contains("no view", unreadable.InnerException?.Message, StringComparison.OrdinalIgnoreCase);

        string? priorStoreMode = Environment.GetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable);
        string? priorOverride = Environment.GetEnvironmentVariable("MILLER_ALLOW_EXTRACTOR_DOWNGRADE");
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, "on");
        Environment.SetEnvironmentVariable("MILLER_ALLOW_EXTRACTOR_DOWNGRADE", null);
        try
        {
            var refresh = new CrossWorkspaceRefreshService(
                registry,
                new JulieExtractRunner(binary),
                SymbolSearchSidecar.Disabled,
                ScanGovernor.Disabled(),
                storeEnabled: static () => true);

            WorkspaceRefreshResult refused = refresh.Refresh(workspaceId, bypassBackoff: true);

            Assert.Equal(WorkspaceRefreshStatus.IneligibleExtractor, refused.Status);
            Assert.Throws<StoreArtifactVersionReadException>(() =>
                StoreArtifactVersionReader.ReadForLeadership(artifact, ExtractBinaryVersionReader.TryRead));

            Environment.SetEnvironmentVariable("MILLER_ALLOW_EXTRACTOR_DOWNGRADE", "1");
            WorkspaceRefreshResult recovered = refresh.Refresh(workspaceId, bypassBackoff: true);

            Assert.True(WorkspaceRefreshStatus.Refreshed == recovered.Status, recovered.Error);
            using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
                artifact,
                canonicalRoot,
                workspaceId,
                storeEnabled: true);
            Assert.Equal("unbound", session.Snapshot.ResolutionState);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, priorStoreMode);
            Environment.SetEnvironmentVariable("MILLER_ALLOW_EXTRACTOR_DOWNGRADE", priorOverride);
            // `using var registry` above is scoped to the whole METHOD, so on Windows it still holds
            // workspaces.db open when the delete below runs and the delete throws IOException. POSIX allows
            // unlink-while-open, which is why only the Windows runs were red. Dispose is idempotent, so
            // releasing it here leaves the using declaration harmless.
            registry.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFamilyStoreRootRebindsFromLegacyArtifact()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string directory = Path.Combine(Path.GetTempPath(), "miller-store-partial-seed-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "root");
        string home = Path.Combine(directory, "home");
        string artifact = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(
            Path.Combine(root, "PartialSeed.cs"),
            "namespace Example; public static class PartialSeed { public static int Value => 42; }");

        string? priorStoreMode = Environment.GetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable);
        string? priorSearchSidecar = Environment.GetEnvironmentVariable(SymbolSearchSidecar.EnvVar);
        string? priorSemantic = Environment.GetEnvironmentVariable("MILLER_SEMANTIC");
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, "on");
        Environment.SetEnvironmentVariable(SymbolSearchSidecar.EnvVar, "off");
        Environment.SetEnvironmentVariable("MILLER_SEMANTIC", "off");
        try
        {
            ScaleTestSupport.RunJulie(
                binary,
                "scan", "--root", root, "--db", artifact, "--level", "full", "--jobs", "1", "--json");

            bool storeEnabled = false;
            using var bootstrap = new IndexBootstrapService(
                NullLogger<IndexBootstrapService>.Instance,
                storeEnabled: () => storeEnabled);
            bootstrap.TestHomeDirectoryOverride = home;
            Assert.Equal(
                BindOutcome.Started,
                bootstrap.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Roots));
            int initialGeneration = bootstrap.Snapshot.RunGeneration;
            await bootstrap.WaitForRunAsync(initialGeneration, TestContext.Current.CancellationToken);

            Assert.True(bootstrap.IsBound, bootstrap.Snapshot.FailureMessage);
            Assert.Null(StoreWorkspacePointer.Read(root));
            storeEnabled = true;
            int replacementGeneration = bootstrap.RebootstrapForReplacedRoot(
                PathCanonicalizer.CanonicalizeRoot(root));
            Assert.True(replacementGeneration > initialGeneration);
            await bootstrap.WaitForRunAsync(replacementGeneration, TestContext.Current.CancellationToken);

            Assert.True(bootstrap.IsBound, bootstrap.Snapshot.FailureMessage);
            Assert.NotNull(StoreWorkspacePointer.Read(root));
            // Scoped, not a `using` declaration: this session ATTACHes the generation's base-*.db onto its
            // connection, so leaving it open until the end of the try block held that file when the
            // Directory.Delete below ran — an IOException on Windows only, since POSIX permits
            // unlink-while-open.
            using (WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
                artifact,
                root,
                bootstrap.Workspace.WorkspaceId))
            {
                Assert.Equal("unbound", session.Snapshot.ResolutionState);
            }

            StoreWorkspacePointerDocument pointer = Assert.IsType<StoreWorkspacePointerDocument>(
                StoreWorkspacePointer.Read(root));
            Directory.Delete(pointer.StoreRoot, recursive: true);
            using var registry = WorkspaceRegistry.Open(bootstrap.Workspace.RegistryDbPath);
            var refresh = new CrossWorkspaceRefreshService(
                registry,
                new JulieExtractRunner(binary),
                SymbolSearchSidecar.Disabled,
                ScanGovernor.Disabled(),
                storeEnabled: static () => true);

            WorkspaceRefreshResult recovered = refresh.Refresh(
                bootstrap.Workspace.WorkspaceId!,
                bypassBackoff: true);

            Assert.Equal(WorkspaceRefreshStatus.Refreshed, recovered.Status);
            using WorkspaceReadHandle recoveredSession = WorkspaceReadSessionFactory.Open(
                artifact,
                root,
                bootstrap.Workspace.WorkspaceId);
            Assert.Equal("unbound", recoveredSession.Snapshot.ResolutionState);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, priorStoreMode);
            Environment.SetEnvironmentVariable(SymbolSearchSidecar.EnvVar, priorSearchSidecar);
            Environment.SetEnvironmentVariable("MILLER_SEMANTIC", priorSemantic);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CliPrimaryReadsUseFamilyStoreWhenLegacyArtifactIsAbsent()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string directory = Path.Combine(Path.GetTempPath(), "miller-store-cli-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "root");
        string home = Path.Combine(directory, "home");
        string artifact = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(
            Path.Combine(root, "StoreCliCalculator.cs"),
            "namespace Example; public static class StoreCliCalculator { // TODO: keep this store-visible\n public static int Add(int a, int b) => a + b; }");
        File.WriteAllText(
            Path.Combine(root, "StoreCliNotes.md"),
            "StoreCliText is served from the family-store content sidecar.");

        string? priorStoreMode = Environment.GetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable);
        string? priorSearchSidecar = Environment.GetEnvironmentVariable(SymbolSearchSidecar.EnvVar);
        string? priorSemantic = Environment.GetEnvironmentVariable("MILLER_SEMANTIC");
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, "on");
        Environment.SetEnvironmentVariable(SymbolSearchSidecar.EnvVar, "off");
        Environment.SetEnvironmentVariable("MILLER_SEMANTIC", "off");
        try
        {
            ScaleTestSupport.RunJulie(
                binary,
                "scan", "--root", root, "--db", artifact, "--level", "full", "--jobs", "1", "--json");

            using var bootstrap = new IndexBootstrapService(
                NullLogger<IndexBootstrapService>.Instance,
                storeEnabled: static () => true);
            bootstrap.TestHomeDirectoryOverride = home;
            Assert.Equal(
                BindOutcome.Started,
                bootstrap.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Roots));
            int generation = bootstrap.Snapshot.RunGeneration;
            await bootstrap.WaitForRunAsync(generation, TestContext.Current.CancellationToken);
            Assert.True(bootstrap.IsBound, bootstrap.Snapshot.FailureMessage);
            Assert.NotNull(StoreWorkspacePointer.Read(root));

            using (WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
                       artifact,
                       root,
                       bootstrap.Workspace.WorkspaceId))
            {
                Assert.True(new ContentCorpusSidecar().EnsureStoreCurrent(session.FamilyStoreRoot!, session));
            }

            File.Delete(artifact);
            WorkspaceContext context = bootstrap.Workspace;
            DashboardSnapshot dashboard = DashboardData.ReadSnapshot(
                context.RegistryDbPath,
                context.TelemetryDbPath,
                context.WorkspaceId);
            Assert.NotNull(dashboard.Health);
            Assert.NotEqual("unavailable", dashboard.Health!.State);
            Assert.NotNull(dashboard.PatternInventory);
            Assert.NotEqual("unavailable", dashboard.PatternInventory!.State);
            Assert.NotNull(dashboard.LocalMetrics);
            Assert.NotEqual("unavailable", dashboard.LocalMetrics!.State);

            foreach (string[] args in new[]
            {
                new[] { "search", "StoreCliCalculator" },
                new[] { "inspect", "StoreCliCalculator" },
                new[] { "impact", "StoreCliCalculator" },
                new[] { "trace", "StoreCliCalculator", "--mode", "path", "--to", "StoreCliCalculator" },
                new[] { "patterns", "list" },
                new[] { "todos" },
                new[] { "search", "TODO", "--mode", "markers" },
                new[] { "search", "StoreCliText", "--mode", "content" },
                new[] { "workspace", "status" },
                new[] { "workspace", "health" },
                new[] { "workspace", "onboarding" },
                new[] { "workspace", "levels" },
                new[] { "metrics", "complexity" },
                new[] { "metrics", "clones" },
                new[] { "complexity", "export" },
                new[] { "references", "export" },
                new[] { "report" },
            })
            {
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();
                int code = CliDispatch.Run(args, context, stdout, stderr);

                Assert.True(
                    code == 0,
                    $"{string.Join(' ', args)} failed with code {code}: {stderr}\n{stdout}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WorkspaceReadSessionFactory.StoreEnvironmentVariable,
                priorStoreMode);
            Environment.SetEnvironmentVariable(SymbolSearchSidecar.EnvVar, priorSearchSidecar);
            Environment.SetEnvironmentVariable("MILLER_SEMANTIC", priorSemantic);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StoreOffBootstrapReconcilesAnInterruptedRollbackMarkerFromSource()
    {
        ScaleTestSupport.RequireJulieServer();
        string directory = Path.Combine(Path.GetTempPath(), "miller-store-rollback-bootstrap-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "root");
        string home = Path.Combine(directory, "home");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "RollbackBootstrap.cs"),
            "namespace Example; public static class RollbackBootstrap { public static int Add(int a, int b) => a + b; }");
        try
        {
            using (var store = new IndexBootstrapService(
                       NullLogger<IndexBootstrapService>.Instance,
                       storeEnabled: static () => true))
            {
                store.TestHomeDirectoryOverride = home;
                Assert.Equal(
                    BindOutcome.Started,
                    store.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Roots));
                int generation = store.Snapshot.RunGeneration;
                await store.WaitForRunAsync(generation, TestContext.Current.CancellationToken);
                Assert.True(store.IsBound, store.Snapshot.FailureMessage);
            }

            StoreWorkspacePointerDocument pointer = Assert.IsType<StoreWorkspacePointerDocument>(
                StoreWorkspacePointer.Read(root));
            string canonicalRoot = PathCanonicalizer.CanonicalizeRoot(root);
            string canonicalArtifact = Path.Combine(canonicalRoot, ".miller", "symbols.db");
            var binding = new StoreFamilyBinding(
                pointer.FamilyId,
                pointer.StoreRoot,
                pointer.ViewId,
                canonicalRoot,
                StoreBindingState.Ready);
            using (FamilyStoreReadSession session = FamilyStoreReadSession.Open(binding))
            {
                File.WriteAllLines(
                    Path.Combine(root, ".miller", "store-rollback.pending"),
                    [
                        "3",
                        "started",
                        pointer.FamilyId.ToString("D"),
                        Encode(pointer.StoreRoot),
                        Encode(pointer.ViewId),
                        Encode(canonicalRoot),
                        Encode(canonicalArtifact),
                        Encode(FullRebuildPromotion.RebuildDbPathFor(canonicalArtifact)),
                        "",
                        session.Visibility.ManifestGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        session.Visibility.ManifestHash,
                        session.Visibility.StoreLogSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ]);
            }

            using var legacy = new IndexBootstrapService(
                NullLogger<IndexBootstrapService>.Instance,
                storeEnabled: static () => false);
            legacy.TestHomeDirectoryOverride = home;
            Assert.Equal(
                BindOutcome.Started,
                legacy.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Roots));
            int legacyGeneration = legacy.Snapshot.RunGeneration;
            await legacy.WaitForRunAsync(legacyGeneration, TestContext.Current.CancellationToken);

            Assert.True(legacy.IsBound, legacy.Snapshot.FailureMessage);
            Assert.Null(StoreWorkspacePointer.Read(root));
            Assert.False(File.Exists(Path.Combine(root, ".miller", "store-rollback.pending")));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BootstrapMigratesLegacyIntoTheFamilyStoreAndTheNextProcessReusesIt()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string directory = Path.Combine(Path.GetTempPath(), "miller-store-bootstrap-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "root");
        string home = Path.Combine(directory, "home");
        string artifact = Path.Combine(root, ".miller", "symbols.db");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(
            Path.Combine(root, "BootstrapCalculator.cs"),
            "namespace Example; public static class BootstrapCalculator { public static int Add(int a, int b) => a + b; }");
        try
        {
            ScaleTestSupport.RunJulie(
                binary,
                "scan", "--root", root, "--db", artifact, "--level", "full", "--jobs", "1", "--json");

            StoreWorkspacePointerDocument? pointer = null;
            using (var first = new IndexBootstrapService(
                       NullLogger<IndexBootstrapService>.Instance,
                       storeEnabled: static () => true))
            {
                first.TestHomeDirectoryOverride = home;
                Assert.Equal(
                    BindOutcome.Started,
                    first.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Roots));
                int generation = first.Snapshot.RunGeneration;
                await first.WaitForRunAsync(generation, TestContext.Current.CancellationToken);
                Assert.True(first.IsBound, BootstrapFailureDetails(first.Snapshot));
                Assert.Contains(
                    first.Index.Search("BootstrapCalculator", 10),
                    symbol => symbol.Document.Name == "BootstrapCalculator");
                Assert.True(File.Exists(artifact));
                pointer = Assert.IsType<StoreWorkspacePointerDocument>(
                    StoreWorkspacePointer.Read(root));
                Assert.True(File.Exists(Path.Combine(pointer.StoreRoot, "CURRENT")));

                File.WriteAllText(
                    Path.Combine(root, "BootstrapCalculator.cs"),
                    "namespace Example; public static class BootstrapCalculator { public static int Multiply(int a, int b) => a * b; }");
                StoreWorkspaceCoordinator.Create(
                    first.Workspace,
                    first.Workspace.CanonicalRoot!,
                    static () => IndexLevelPolicy.Full).Update(Path.Combine(root, "BootstrapCalculator.cs"));
                StoreFamilyBinding updatedBinding = StoreWorkspaceCoordinator.ResolveBinding(
                    first.Workspace,
                    first.Workspace.CanonicalRoot!);
                using (FamilyStoreReadSession updated = FamilyStoreReadSession.Open(updatedBinding))
                {
                    Assert.Contains(
                        RepositoryIndexLoader.LoadSession(updated).Search("Multiply", 10),
                        symbol => symbol.Document.Name == "Multiply");
                }
                var freshness = new FreshnessService(
                    first,
                    NullLogger<FreshnessService>.Instance,
                    storeEnabled: static () => true);
                PollResult poll = freshness.PollNow();
                Assert.True(poll.Swapped);
                Assert.Contains(
                    first.Index.Search("Multiply", 10),
                    symbol => symbol.Document.Name == "Multiply");

                File.WriteAllText(
                    Path.Combine(root, "BootstrapCalculator.cs"),
                    "namespace Example; public static class BootstrapCalculator { public static int Divide(int a, int b) => a / b; }");
                using (var registry = WorkspaceRegistry.Open(first.Workspace.RegistryDbPath))
                {
                    var crossRefresh = new CrossWorkspaceRefreshService(
                        registry,
                        new JulieExtractRunner(binary),
                        SymbolSearchSidecar.Disabled,
                        ScanGovernor.Disabled(),
                        storeEnabled: static () => true);
                    WorkspaceRefreshResult result = crossRefresh.Refresh(first.Workspace.WorkspaceId!);
                    Assert.Equal(WorkspaceRefreshStatus.Refreshed, result.Status);
                    Assert.Equal(pointer.FamilyId.ToString("D"), result.ArtifactId);

                    WorkspaceRegistryRow row = Assert.IsType<WorkspaceRegistryRow>(
                        registry.Get(first.Workspace.WorkspaceId!));
                    WorkspaceFacts facts = WorkspaceFactsAssembler.FromRegisteredRow(
                        registry,
                        row,
                        WorkspaceRegisteredFactsProfile.CliStatus,
                        SymbolSearchSidecar.Disabled,
                        new ContentCorpusSidecar(),
                        new VectorSidecar(SemanticMode.Off),
                        storeEnabled: true);
                    Assert.Equal("ready", facts.Store?.State);
                    Assert.Equal(pointer.FamilyId.ToString("D"), facts.Store?.FamilyId);
                    Assert.Equal(pointer.ViewId, facts.Store?.ViewId);
                    Assert.Equal("legacy_preserved", facts.Store?.MigrationState);
                }
                Assert.True(freshness.PollNow().Swapped);
                Assert.Contains(
                    first.Index.Search("Divide", 10),
                    symbol => symbol.Document.Name == "Divide");
            }

            int scans = 0;
            using (var second = new IndexBootstrapService(
                       NullLogger<IndexBootstrapService>.Instance,
                       storeEnabled: static () => true))
            {
                second.TestHomeDirectoryOverride = home;
                second.TestScanObserver = () => scans++;
                Assert.Equal(
                    BindOutcome.Started,
                    second.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Roots));
                int generation = second.Snapshot.RunGeneration;
                await second.WaitForRunAsync(generation, TestContext.Current.CancellationToken);
                Assert.True(second.IsBound, second.Snapshot.FailureMessage);
                Assert.Equal(0, scans);
                Assert.Contains(
                    second.Index.Search("BootstrapCalculator", 10),
                    symbol => symbol.Document.Name == "BootstrapCalculator");
            }

            Assert.NotNull(pointer);
            using (var legacy = new IndexBootstrapService(
                       NullLogger<IndexBootstrapService>.Instance,
                       storeEnabled: static () => false))
            {
                legacy.TestHomeDirectoryOverride = home;
                Assert.Equal(
                    BindOutcome.Started,
                    legacy.BootstrapForRoot(root, WorkspaceBindingResolver.WorkspaceSource.Roots));
                int generation = legacy.Snapshot.RunGeneration;
                await legacy.WaitForRunAsync(generation, TestContext.Current.CancellationToken);
                Assert.True(legacy.IsBound, legacy.Snapshot.FailureMessage);
                Assert.Contains(
                    legacy.Index.Search("Divide", 10),
                    symbol => symbol.Document.Name == "Divide");
                Assert.DoesNotContain(
                    legacy.Index.Search("Add", 10),
                    symbol => symbol.Document.Name == "Add");
            }
            Assert.Null(StoreWorkspacePointer.Read(root));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReleasedStoreAndLegacyArtifactProduceEqualPrimaryReadRows()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string directory = Path.Combine(
            Path.GetTempPath(),
            "miller-store-read-scale-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "root");
        string store = Path.Combine(directory, "store");
        string artifact = Path.Combine(directory, "symbols.db");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "Calculator.cs"),
            "namespace Example; public static class Calculator { public static int Add(int a, int b) => a + b; }");
        try
        {
            ScaleTestSupport.RunJulie(
                binary,
                "scan", "--root", root, "--db", artifact, "--level", "full", "--jobs", "1", "--json");
            ScaleTestSupport.RunJulie(
                binary,
                "store", "import", "--store", store,
                "--family", "11111111-1111-4111-8111-111111111111",
                "--root", root, "--view", "view-a", "--level", "full", "--jobs", "1", "--json");

            var binding = new StoreFamilyBinding(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                store,
                "view-a",
                PathCanonicalizer.CanonicalizeRoot(root),
                StoreBindingState.Ready);
            using LegacyArtifactReadSession legacy = LegacyArtifactReadSession.Open(artifact);
            using FamilyStoreReadSession family = FamilyStoreReadSession.Open(binding);

            Assert.Equal(
                SqliteSymbolReader.ReadSession(legacy),
                SqliteSymbolReader.ReadSession(family));
            BridgeData legacyBridge = SqliteBridgeReader.ReadSession(legacy);
            BridgeData familyBridge = SqliteBridgeReader.ReadSession(family);
            Assert.Equal(legacyBridge.TypeArguments, familyBridge.TypeArguments);
            Assert.Equal(legacyBridge.Literals, familyBridge.Literals);
            Assert.Equal(legacyBridge.Annotations, familyBridge.Annotations);
            Assert.Equal(legacyBridge.DbSetProperties, familyBridge.DbSetProperties);
            Assert.Equal(legacyBridge.StructuralFacts, familyBridge.StructuralFacts);
            Assert.Equal(ReadResolutionRows(legacy), ReadResolutionRows(family));

            var searchSidecar = new SymbolSearchSidecar(true, RegionIndexOptions.Disabled);
            Assert.True(searchSidecar.EnsureStoreCurrent(store, family));
            Assert.False(searchSidecar.EnsureStoreCurrent(store, family));
            Assert.True(searchSidecar.EnsureBuilt(artifact, legacy.Snapshot.Freshness.Revision));
            ISymbolLookupIndex legacyDiskSearch = searchSidecar.OpenRequired(
                artifact,
                legacy.Snapshot.Freshness.Revision);
            ISymbolLookupIndex storeDiskSearch = searchSidecar.OpenStoreRequired(store, family.Snapshot);
            Assert.Equal(
                legacyDiskSearch.Search("Calculator Add", 20),
                storeDiskSearch.Search("Calculator Add", 20));

            var contentSidecar = new ContentCorpusSidecar();
            Assert.True(contentSidecar.EnsureStoreCurrent(store, family));
            Assert.False(contentSidecar.EnsureStoreCurrent(store, family));
            Assert.True(contentSidecar.EnsureBuilt(
                artifact,
                root,
                legacy.Snapshot.WorkspaceId,
                legacy.Snapshot.Freshness.Revision));
            ITextContentSearchIndex legacyContent = ContentCorpusSidecar.OpenGenerationChecked(
                ContentCorpusSidecar.ContentDbPathFor(artifact),
                artifact,
                legacy.Snapshot.Freshness.Revision);
            ITextContentSearchIndex storeContent = ContentCorpusSidecar.OpenStoreGenerationChecked(
                store,
                family.Snapshot);
            Assert.Equal(
                legacyContent.Search("Calculator", TextContentKind.WorkspaceSource, 20),
                storeContent.Search("Calculator", TextContentKind.WorkspaceSource, 20));

            StoreWorkspacePointer.Write(root, binding);
            Assert.NotNull(new IndexedSourceTextReader().FindLiteralForWorkspace(
                artifact,
                root,
                "Calculator.cs",
                "Calculator",
                storeEnabled: true));
            IndexedEditCandidateResult editCandidates = new IndexedEditCandidateReader().FindCandidatesForWorkspace(
                artifact,
                root,
                "Calculator.cs",
                family.Snapshot.Freshness.StoreLogSequence!.Value,
                oldText: null,
                query: "Calculator",
                anchor: null,
                line: null,
                storeEnabled: true);
            Assert.Equal(IndexedEditCandidateState.Current, editCandidates.State);
            string importedPath = Path.Combine(directory, "review.log");
            File.WriteAllText(importedPath, "StoreContentNeedle is visible through the public content tool.");
            var toolWorkspace = new WorkspaceContext(
                root,
                artifact,
                Path.Combine(directory, "telemetry.db"),
                Path.Combine(directory, "workspaces.db"),
                directory,
                "workspace-a",
                PathCanonicalizer.CanonicalizeRoot(root),
                artifact);
            var contentTool = new ContentTool(
                toolWorkspace,
                new ContentCorpusExternalStore(),
                storeEnabled: static () => true);

            string imported = contentTool.Content(
                operation: "import",
                path: importedPath,
                display_path: "review.log",
                format: "json");
            string found = contentTool.Content(
                operation: "search",
                query: "StoreContentNeedle",
                content_kind: TextContentKind.ExternalFile,
                format: "json");

            Assert.Contains("review.log", imported, StringComparison.Ordinal);
            Assert.Contains("StoreContentNeedle", found, StringComparison.Ordinal);

            StoreWorkspacePointer.Write(root, binding);
            string extension = SqliteVecTestSupport.RequireExtension();
            string? priorExtension = Environment.GetEnvironmentVariable(VectorStore.ExtensionPathEnvVar);
            string? priorStoreMode = Environment.GetEnvironmentVariable(
                WorkspaceReadSessionFactory.StoreEnvironmentVariable);
            Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, extension);
            Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, "on");
            try
            {
                WorkspaceContext workspace = WorkspaceContext.Create(root, AppContext.BaseDirectory, directory) with
                {
                    WorkspaceId = "workspace-a",
                    CanonicalRoot = PathCanonicalizer.CanonicalizeRoot(root),
                    CanonicalExtractDbPath = artifact,
                };
                string vectorPath;
                VectorGenerationManager generations;
                {
                    using IVectorConvergePort port = Assert.IsAssignableFrom<IVectorConvergePort>(
                        SqliteVectorConvergePort.TryOpenStore(workspace));
                    using IVectorConvergePort legacyPort = Assert.IsAssignableFrom<IVectorConvergePort>(
                        SqliteVectorConvergePort.TryOpenAt(
                            workspace,
                            Path.Combine(directory, "legacy-vectors.db")));
                    VectorConvergeSnapshot snapshot = port.Snapshot(0);
                    Assert.True(snapshot.FullPass);
                    Assert.Equal(family.Snapshot.Freshness.StoreLogSequence, snapshot.TargetRevision);
                    long manifestRevision = family.Read(connection =>
                    {
                        using SqliteCommand command = connection.CreateCommand();
                        command.CommandText =
                            "SELECT MIN(sequence) FROM store_log WHERE event_kind='manifest_flipped';";
                        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                    });
                    VectorConvergeSnapshot resolutionOnly = port.Snapshot(manifestRevision);
                    Assert.True(resolutionOnly.DeltaHistoryComplete);
                    Assert.False(resolutionOnly.FullPass);
                    Assert.Empty(resolutionOnly.ChangedPaths);
                    Assert.Equal(
                        legacyPort.Units(VectorUnitKind.Symbol, paths: null),
                        port.Units(VectorUnitKind.Symbol, paths: null));
                    Assert.NotEmpty(port.Units(VectorUnitKind.Symbol, paths: null));

                    string cursor = snapshot.TargetRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    port.SetMeta(VectorConvergeService.SymbolCompletedKey, cursor);
                    port.SetMeta(VectorConvergeService.SymbolTargetKey, cursor);
                    port.SetMeta(VectorConvergeService.ChunkCompletedKey, cursor);
                    port.SetMeta(VectorConvergeService.ChunkTargetKey, cursor);
                    port.SetMeta("build_state", "ready");
                    port.PublishCompleteness();

                    StoreSidecarStamp expected = StoreSidecarStamp.FromSnapshot(
                        StoreSidecarKind.Vector,
                        family.Snapshot);
                    vectorPath = VectorSidecar.PathForStore(store, family.Snapshot.ViewId);
                    generations = VectorConvergeService.VectorGenerationManagerFor(workspace);
                    Assert.Equal(vectorPath, generations.ActivePath);
                    Assert.Equal(vectorPath + ".rebuild", generations.ShadowPath);
                    Assert.True(StoreSidecarCatalog.IsCurrent(vectorPath, expected));
                    Assert.Equal("ready", new VectorSidecar(SemanticMode.On).InspectStore(store, family.Snapshot).State);
                }

                File.Move(vectorPath, generations.ShadowPath);
                using IVectorConvergePort recovered = Assert.IsAssignableFrom<IVectorConvergePort>(
                    SqliteVectorConvergePort.TryOpenStore(workspace));
                Assert.True(File.Exists(vectorPath));
                Assert.False(File.Exists(generations.ShadowPath));
                Assert.NotEmpty(recovered.Units(VectorUnitKind.Symbol, paths: null));
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    WorkspaceReadSessionFactory.StoreEnvironmentVariable,
                    priorStoreMode);
                Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, priorExtension);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static IReadOnlyList<string> ReadResolutionRows(IWorkspaceReadSession session) =>
        session.Read(connection =>
        {
            QueryTimeResolutionReader reader = ReferenceEvidenceReader.ReaderFor(session, connection);
            return QueryTimeResolutionParity.SerializeExport(reader, connection);
        });

    private static string Encode(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

    private static string BootstrapFailureDetails(BootstrapSnapshot snapshot)
    {
        string message = snapshot.FailureMessage ?? snapshot.LastFailureMessage ?? "<none>";
        int separator = message.IndexOf(':', StringComparison.Ordinal);
        string failureClass = separator > 0 ? message[..separator] : "unknown";
        return $"failure_class={failureClass} failure_message={message} " +
               $"snapshot_json={JsonSerializer.Serialize(snapshot)}";
    }
}
