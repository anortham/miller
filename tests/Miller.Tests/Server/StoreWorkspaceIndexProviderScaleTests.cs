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
using Xunit;

namespace Miller.Tests.Server;

[Trait("Category", "Scale")]
[Collection(Miller.Tests.Indexing.SqliteVecEnvironment.Name)]
public sealed class StoreWorkspaceIndexProviderScaleTests
{
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
            "namespace Example; public static class StoreCliCalculator { public static int Add(int a, int b) => a + b; }");

        string? priorStoreMode = Environment.GetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable);
        string? priorSearchSidecar = Environment.GetEnvironmentVariable(SymbolSearchSidecar.EnvVar);
        Environment.SetEnvironmentVariable(WorkspaceReadSessionFactory.StoreEnvironmentVariable, "on");
        Environment.SetEnvironmentVariable(SymbolSearchSidecar.EnvVar, "off");
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

            File.Delete(artifact);
            WorkspaceContext context = bootstrap.Workspace;
            foreach (string[] args in new[]
            {
                new[] { "search", "StoreCliCalculator" },
                new[] { "inspect", "StoreCliCalculator" },
                new[] { "impact", "StoreCliCalculator" },
                new[] { "trace", "StoreCliCalculator", "--mode", "path", "--to", "StoreCliCalculator" },
                new[] { "patterns", "list" },
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
                Assert.True(first.IsBound, first.Snapshot.FailureMessage);
                Assert.Contains(
                    first.Index.Search("BootstrapCalculator", 10),
                    symbol => symbol.Document.Name == "BootstrapCalculator");
                Assert.True(File.Exists(artifact));
                StoreWorkspacePointerDocument pointer = Assert.IsType<StoreWorkspacePointerDocument>(
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
            ScaleTestSupport.RunJulie(
                binary,
                "store", "resolve", "--store", store, "--view", "view-a", "--json");

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
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT identifier_id||'|'||COALESCE(target_symbol_id,'')||'|'||
                       COALESCE(CAST(tier AS TEXT),'')||'|'||COALESCE(method,'')||'|'||outcome||'|'||
                       COALESCE(CAST(candidates AS TEXT),'')
                FROM identifier_resolutions
                ORDER BY identifier_id
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
                rows.Add(reader.GetString(0));
            return rows;
        });

}
