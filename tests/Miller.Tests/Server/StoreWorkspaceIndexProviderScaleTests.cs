using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Semantic;
using Miller.Indexing.Store;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Server;
using Miller.Server.Hosting;
using Miller.Server.Workspaces;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

[Trait("Category", "Scale")]
[Collection(Miller.Tests.Indexing.SqliteVecEnvironment.Name)]
public sealed class StoreWorkspaceIndexProviderScaleTests
{
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
            string extension = SqliteVecTestSupport.RequireExtension();
            string? priorExtension = Environment.GetEnvironmentVariable(VectorStore.ExtensionPathEnvVar);
            Environment.SetEnvironmentVariable(VectorStore.ExtensionPathEnvVar, extension);
            try
            {
                WorkspaceContext workspace = WorkspaceContext.Create(root, AppContext.BaseDirectory, directory) with
                {
                    WorkspaceId = "workspace-a",
                    CanonicalRoot = PathCanonicalizer.CanonicalizeRoot(root),
                    CanonicalExtractDbPath = artifact,
                };
                using IVectorConvergePort port = Assert.IsAssignableFrom<IVectorConvergePort>(
                    SqliteVectorConvergePort.TryOpenStore(workspace));
                using IVectorConvergePort legacyPort = Assert.IsAssignableFrom<IVectorConvergePort>(
                    SqliteVectorConvergePort.TryOpenAt(
                        workspace,
                        Path.Combine(directory, "legacy-vectors.db")));
                VectorConvergeSnapshot snapshot = port.Snapshot(0);
                Assert.True(snapshot.FullPass);
                Assert.Equal(family.Snapshot.Freshness.StoreLogSequence, snapshot.TargetRevision);
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
                string vectorPath = VectorSidecar.PathForStore(store, family.Snapshot.ViewId);
                Assert.True(StoreSidecarCatalog.IsCurrent(vectorPath, expected));
                Assert.Equal("ready", new VectorSidecar(SemanticMode.On).InspectStore(store, family.Snapshot).State);
            }
            finally
            {
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
