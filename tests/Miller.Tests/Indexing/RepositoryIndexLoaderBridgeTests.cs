using Microsoft.Data.Sqlite;
using Miller.Core.Contracts;
using Miller.Core.Graph;
using Miller.Core.Resolver; // BridgeKind, ConfidenceBand, ScoredEdge (the Walk result element type)
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the M4 bridge wiring through the SINGLE production build path (plan Task 9): after
/// <see cref="RepositoryIndexLoader.Load"/> runs <see cref="SqliteBridgeReader"/> + <see cref="BridgeGraphBuilder"/>,
/// the resulting <see cref="MillerRepositoryIndex.BridgeGraph"/> is populated and the existing index features still
/// work. Builds a temp 28/3 DB carrying the <c>UserDto → ApplicationUser → ApplicationUsers</c> chain (a CreateMap
/// type-argument pair + a DbSet&lt;T&gt; property) plus the entity/DTO symbols the resolver needs. Fast suite.
/// </summary>
public sealed class RepositoryIndexLoaderBridgeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public RepositoryIndexLoaderBridgeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-loaderbridge-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "symbols.db");
        BuildChainDb(_dbPath);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // Build a DB whose bridge breadcrumbs resolve into a real cross-language chain:
    //   CreateMap<ApplicationUser, UserDto>  (DTO↔entity, MapsTo)   +   DbSet<ApplicationUser> ApplicationUsers (entity↔table, StoredIn)
    // The entity (ApplicationUser) and DTO (UserDto) are real class symbols so the SymbolResolver resolves both
    // sides and the scorer can band the StoredIn/MapsTo edges High.
    private static void BuildChainDb(string dbPath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            // v1 schema.rs column shapes: symbols(symbol_id/path/parent_symbol_id + typed test cols),
            // identifiers(identifier_id/path/containing_symbol_id), relationships(relationship_id + path),
            // type_argument_usages + type_arguments (the v1 split — identifier_id/path live on the usage row),
            // literals(literal_id/path), symbol_annotations(annotation_id, NO ordinal). The readers do by-name
            // GetOrdinal reads, so only the columns they SELECT must exist; the rest honor the v1 NOT NULLs.
            command.CommandText = """
                CREATE TABLE symbols (
                    symbol_id TEXT PRIMARY KEY, name TEXT, signature TEXT, kind TEXT, language TEXT,
                    path TEXT, start_line INTEGER, end_line INTEGER, parent_symbol_id TEXT,
                    is_test INTEGER NOT NULL DEFAULT 0, metadata_json TEXT
                );
                CREATE TABLE identifiers (
                    identifier_id TEXT PRIMARY KEY, name TEXT, kind TEXT, path TEXT, start_line INTEGER,
                    containing_symbol_id TEXT, target_symbol_id TEXT
                );
                CREATE TABLE relationships (
                    relationship_id TEXT PRIMARY KEY, from_symbol_id TEXT, to_symbol_id TEXT, path TEXT, kind TEXT
                );
                CREATE TABLE type_argument_usages (
                    usage_id TEXT PRIMARY KEY, identifier_id TEXT, path TEXT, language TEXT
                );
                CREATE TABLE type_arguments (
                    type_argument_id TEXT PRIMARY KEY, usage_id TEXT, parent_type_argument_id TEXT,
                    ordinal INTEGER, type_name TEXT
                );
                CREATE TABLE literals (
                    literal_id TEXT PRIMARY KEY, literal_text TEXT, kind TEXT, carrier TEXT, arg_position INTEGER,
                    language TEXT, path TEXT, start_line INTEGER, end_line INTEGER, start_byte INTEGER,
                    end_byte INTEGER, containing_symbol_id TEXT, confidence REAL
                );
                CREATE TABLE symbol_annotations (
                    annotation_id TEXT PRIMARY KEY, symbol_id TEXT, annotation TEXT, annotation_key TEXT, raw_text TEXT, carrier TEXT
                );
                CREATE TABLE structural_facts (
                    structural_fact_id TEXT PRIMARY KEY, file_id TEXT, path TEXT, language TEXT, pattern_id TEXT,
                    capture_name TEXT, node_kind TEXT, containing_symbol_id TEXT, start_line INTEGER, start_column INTEGER,
                    end_line INTEGER, end_column INTEGER, start_byte INTEGER, end_byte INTEGER, confidence REAL,
                    metadata_json TEXT
                );
                CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);

                -- The entity, the DTO, and a DbContext exposing DbSet<ApplicationUser> ApplicationUsers.
                INSERT INTO symbols(symbol_id, name, signature, kind, language, path, start_line, end_line, parent_symbol_id)
                VALUES
                  ('s-entity', 'ApplicationUser', 'public class ApplicationUser', 'class', 'csharp', 'model/ApplicationUser.cs', 1, 20, NULL),
                  ('s-dto',    'UserDto',         'public class UserDto',         'class', 'csharp', 'dto/UserDto.cs',           1, 10, NULL),
                  ('s-ctx',    'AppDbContext',    'public class AppDbContext',    'class', 'csharp', 'data/AppDbContext.cs',     1, 30, NULL),
                  ('s-prop',   'ApplicationUsers','public DbSet<ApplicationUser> ApplicationUsers { get; set; }', 'property', 'csharp', 'data/AppDbContext.cs', 5, 5, 's-ctx'),
                  ('s-profile','MapProfile',      'public class MapProfile',      'class', 'csharp', 'map/MapProfile.cs',        1, 8, NULL);

                -- CreateMap<ApplicationUser, UserDto> use-site: ONE usage row carries identifier_id 'id-map' + path;
                -- the two top-level type args JOIN it by usage_id (the v1 split the reader re-assembles).
                INSERT INTO type_argument_usages(usage_id, identifier_id, path, language)
                VALUES ('u-map', 'id-map', 'map/MapProfile.cs', 'csharp');

                INSERT INTO type_arguments(type_argument_id, usage_id, parent_type_argument_id, ordinal, type_name)
                VALUES
                  ('ta-0', 'u-map', NULL, 0, 'ApplicationUser'),
                  ('ta-1', 'u-map', NULL, 1, 'UserDto');

                -- Structural facts are raw bridge inputs only in this task. The selected route-relevant facts should
                -- be passed into BridgeGraphBuilder; the generic JSON fact should be ignored by the bridge reader.
                INSERT INTO structural_facts
                    (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                     containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                     confidence, metadata_json)
                VALUES
                  ('sf-minimal', 'file:Api/Program.cs', 'Api/Program.cs', 'csharp',
                   'aspnet.minimal_api.route.v1', 'route', 'invocation', 's-profile',
                   20, 9, 20, 42, 300, 333, 1.0, '{"route":"/api/users"}'),
                  ('sf-hx', 'file:Views/Users.cshtml', 'Views/Users.cshtml', 'razor',
                   'htmx.attribute.v1', 'attribute', 'attribute', NULL,
                   5, 12, 5, 34, 80, 102, 0.95, '{"name":"hx-get","value":"/api/users"}'),
                  ('sf-json', 'file:config/routes.json', 'config/routes.json', 'json',
                   'json.property.v1', 'property', 'pair', NULL,
                   1, 1, 1, 9, 0, 8, 1.0, '{"key":"route"}');
                """;
            command.ExecuteNonQuery();

            // Seed the v1 artifact_metadata gate keys from the pinned constants (not literals) so a julie re-pin
            // needs no edit here. Kept as a separate interpolated command to leave the brace-bearing DDL above a
            // plain raw string.
            SeedGate(command);
        }
    }

    // Seed the v1 artifact_metadata gate keys (sqlite_schema_version / schema_version / extract_contract_version /
    // hash_algorithm) from the pinned contract so JulieSchemaGate.Verify accepts the fixture (Phase 3).
    private static void SeedGate(SqliteCommand command)
    {
        command.CommandText = $"""
            INSERT INTO artifact_metadata(key, value) VALUES ('sqlite_schema_version', '{MillerExtractContract.ExpectedSchemaVersion}');
            INSERT INTO artifact_metadata(key, value) VALUES ('schema_version', '{MillerExtractContract.ExpectedSchemaVersion}');
            INSERT INTO artifact_metadata(key, value) VALUES ('extract_contract_version', '{MillerExtractContract.ExpectedExtractContractVersion}');
            INSERT INTO artifact_metadata(key, value) VALUES ('hash_algorithm', '{MillerExtractContract.ExpectedHashAlgorithm}');
            """;
        command.ExecuteNonQuery();
    }

    private static (string Root, string DbPath) CreateConfiguredBridgeWorkspace(string configJson)
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-bridge-config-" + Guid.NewGuid().ToString("N"));
        string millerDir = Path.Combine(root, ".miller");
        System.IO.Directory.CreateDirectory(millerDir);
        string dbPath = Path.Combine(millerDir, "symbols.db");
        BuildChainDb(dbPath);
        File.WriteAllText(Path.Combine(root, "miller.json"), configJson);
        return (root, dbPath);
    }

    private static void TryDelete(string path)
    {
        try { System.IO.Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    private sealed class CapturingStructuralFactsProvider : IBridgeProvider
    {
        public string Id => "capture-structural-facts";

        public IReadOnlyList<StructuralFactRecord>? Observed { get; private set; }

        public BridgeProviderResult BuildCandidates(BridgeProviderContext context)
        {
            Observed = context.StructuralFacts;
            return BridgeProviderResult.ActiveResult(
                candidates: [],
                evidenceCounts: new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["capture.structuralFacts"] = context.StructuralFacts.Count,
                });
        }
    }

    [Fact]
    public void BridgeGraphBuilder_PassesStructuralFactsToProviderContext()
    {
        IReadOnlyList<StructuralFactRecord> facts =
        [
            new(
                FactId: "sf-vue",
                PatternId: "vue.route_reference.v1",
                Language: "vue",
                Path: "web/App.vue",
                CaptureName: "route_reference",
                NodeKind: "call",
                ContainingSymbolId: null,
                StartLine: 3,
                StartColumn: 12,
                EndLine: 3,
                EndColumn: 45,
                Span: new SourceSpan(90, 123),
                Confidence: 0.92,
                MetadataJson: """{"route":"/users/:id"}"""),
        ];
        var provider = new CapturingStructuralFactsProvider();

        var graph = BridgeGraphBuilder.Build(
            symbols: [],
            typeArguments: [],
            literals: [],
            annotations: [],
            dbSetProperties: [],
            providers: [provider],
            structuralFacts: facts);

        Assert.Same(facts, provider.Observed);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["capture.structuralFacts"]);
    }

    [Fact]
    public void Load_PopulatesBridgeGraph_WithTheEntityTableChain()
    {
        var index = RepositoryIndexLoader.Load(_dbPath);

        // The entity↔table StoredIn edge: the entity node is ApplicationUser's symbol id, and it bridges to the
        // synthesized table node (CsEntity → DbTable). The table node id is namespaced by kind + lowercased display.
        Assert.True(index.BridgeGraph.Contains("s-entity"));

        string tableNodeId = BridgeGraph.SynthesizeId(BridgeNodeKind.DbTable, "ApplicationUsers");
        Assert.True(index.BridgeGraph.Contains(tableNodeId));

        // Walking from the entity reaches the StoredIn edge to the table at depth 1, banded High (a DbSet anchor with
        // a resolved entity is an explicit breadcrumb).
        var fromEntity = index.BridgeGraph.Walk("s-entity", maxDepth: 3);
        var storedIn = Assert.Single(fromEntity, e => e.Edge.Kind == BridgeKind.StoredIn);
        Assert.Equal(ConfidenceBand.High, storedIn.Band);
        Assert.Equal("ApplicationUser", storedIn.Edge.SourceRef.Display);
        Assert.Equal("ApplicationUsers", storedIn.Edge.TargetRef.Display);
        Assert.Contains("dotnet-web", index.BridgeGraph.CapabilityReport.ActiveProviders);
    }

    [Fact]
    public void Load_PopulatesBridgeGraph_WithTheCreateMapDtoEntityEdge()
    {
        var index = RepositoryIndexLoader.Load(_dbPath);

        // The DTO↔entity MapsTo edge from CreateMap<ApplicationUser, UserDto>: both sides resolve to real class
        // symbols, so the node ids are the symbol ids and the edge connects them.
        Assert.True(index.BridgeGraph.Contains("s-dto"));

        var fromDto = index.BridgeGraph.Walk("s-dto", maxDepth: 3);
        Assert.Contains(fromDto, e => e.Edge.Kind == BridgeKind.MapsTo);

        // From the entity, the walk reaches BOTH the table (StoredIn) and the DTO (MapsTo) — the chain
        // UserDto ↔ ApplicationUser ↔ ApplicationUsers is connected through the single entity node.
        var fromEntity = index.BridgeGraph.Walk("s-entity", maxDepth: 3);
        Assert.Contains(fromEntity, e => e.Edge.Kind == BridgeKind.StoredIn);
        Assert.Contains(fromEntity, e => e.Edge.Kind == BridgeKind.MapsTo);
    }

    [Fact]
    public void Load_StillBuildsTheSymbolIndexAndGraph_BridgeIsAdditive()
    {
        var index = RepositoryIndexLoader.Load(_dbPath);

        // The existing index features are intact: every symbol is indexed, search resolves, and the dependency
        // graph still carries every symbol as a node (the bridge graph is additive, not a replacement).
        Assert.Equal(5, index.DocumentCount);
        Assert.Equal("s-entity", index.FindByName("ApplicationUser").Single().SymbolId);
        Assert.True(index.Graph.Contains("s-entity"));
    }

    [Fact]
    public void Load_InvokesBridgeBuildMeasurementCallback()
    {
        // Plan Task 9: the loader MEASURES the bridge-graph build cost and hands the elapsed time to the caller
        // (the services log it). The callback must fire exactly once with a non-negative duration.
        int calls = 0;
        TimeSpan observed = TimeSpan.FromSeconds(-1);
        var index = RepositoryIndexLoader.Load(_dbPath, onBridgeGraphBuilt: elapsed =>
        {
            calls++;
            observed = elapsed;
        });

        Assert.Equal(1, calls);
        Assert.True(observed >= TimeSpan.Zero);
        Assert.True(index.BridgeGraph.Contains("s-entity")); // and the graph is still populated
    }

    [Fact]
    public void Load_PassesSelectedStructuralFactsIntoBridgeBuilder_WithoutGraphingUnrelatedPatternIds()
    {
        var index = RepositoryIndexLoader.Load(_dbPath);

        Assert.True(index.BridgeGraph.CapabilityReport.EvidenceCounts.TryGetValue("bridge.structuralFacts", out int count));
        Assert.Equal(2, count);
        Assert.DoesNotContain(
            index.BridgeGraph.CapabilityReport.EvidenceCounts.Keys,
            key => key.Contains("json.property", StringComparison.Ordinal));
        Assert.False(index.BridgeGraph.Contains(BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "json.property.v1")));
    }

    [Fact]
    public void Load_RootMillerJsonDotnetWebProvider_PopulatesBridgeGraph()
    {
        var (root, dbPath) = CreateConfiguredBridgeWorkspace("""
            {
              "bridge": {
                "providers": ["dotnet-web"]
              }
            }
            """);
        try
        {
            var index = RepositoryIndexLoader.Load(dbPath);

            Assert.True(index.BridgeGraph.Contains("s-entity"));
            Assert.Contains("dotnet-web", index.BridgeGraph.CapabilityReport.ActiveProviders);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Load_RootMillerJsonEmptyProviders_DisablesBridgeGraph()
    {
        var (root, dbPath) = CreateConfiguredBridgeWorkspace("""
            {
              "bridge": {
                "providers": []
              }
            }
            """);
        try
        {
            var index = RepositoryIndexLoader.Load(dbPath);

            Assert.False(index.BridgeGraph.Contains("s-entity"));
            Assert.Empty(index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.Contains(
                index.BridgeGraph.CapabilityReport.Notes,
                note => note.Contains("no bridge providers", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Load_RootMillerJsonUnknownProvider_DoesNotRunDefaultProvider()
    {
        var (root, dbPath) = CreateConfiguredBridgeWorkspace("""
            {
              "bridge": {
                "providers": ["rails"]
              }
            }
            """);
        try
        {
            var index = RepositoryIndexLoader.Load(dbPath);

            Assert.False(index.BridgeGraph.Contains("s-entity"));
            var skipped = Assert.Single(index.BridgeGraph.CapabilityReport.SkippedProviders);
            Assert.Equal("rails", skipped.ProviderId);
            Assert.Contains("unknown bridge provider", skipped.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(index.BridgeGraph.CapabilityReport.ActiveProviders);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Load_EmptyBridgeBreadcrumbs_YieldEmptyBridgeGraph_IndexStillBuilds()
    {
        // A DB with symbols but NO bridge breadcrumbs (no type_arguments / DbSet props) builds an index whose
        // bridge graph has no edges — but the index itself is fully functional.
        string dir = Path.Combine(Path.GetTempPath(), "miller-nobridge-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "symbols.db");
        try
        {
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    CREATE TABLE symbols (symbol_id TEXT PRIMARY KEY, name TEXT, signature TEXT, kind TEXT, language TEXT, path TEXT, start_line INTEGER, end_line INTEGER, parent_symbol_id TEXT, is_test INTEGER NOT NULL DEFAULT 0, metadata_json TEXT);
                    CREATE TABLE identifiers (identifier_id TEXT PRIMARY KEY, name TEXT, kind TEXT, path TEXT, start_line INTEGER, containing_symbol_id TEXT, target_symbol_id TEXT);
                    CREATE TABLE relationships (relationship_id TEXT PRIMARY KEY, from_symbol_id TEXT, to_symbol_id TEXT, path TEXT, kind TEXT);
                    CREATE TABLE type_argument_usages (usage_id TEXT PRIMARY KEY, identifier_id TEXT, path TEXT, language TEXT);
                    CREATE TABLE type_arguments (type_argument_id TEXT PRIMARY KEY, usage_id TEXT, parent_type_argument_id TEXT, ordinal INTEGER, type_name TEXT);
                    CREATE TABLE literals (literal_id TEXT PRIMARY KEY, literal_text TEXT, kind TEXT, carrier TEXT, arg_position INTEGER, language TEXT, path TEXT, start_line INTEGER, end_line INTEGER, start_byte INTEGER, end_byte INTEGER, containing_symbol_id TEXT, confidence REAL);
                    CREATE TABLE symbol_annotations (annotation_id TEXT PRIMARY KEY, symbol_id TEXT, annotation TEXT, annotation_key TEXT, raw_text TEXT, carrier TEXT);
                    CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO artifact_metadata(key, value) VALUES ('sqlite_schema_version', '{MillerExtractContract.ExpectedSchemaVersion}');
                    INSERT INTO artifact_metadata(key, value) VALUES ('schema_version', '{MillerExtractContract.ExpectedSchemaVersion}');
                    INSERT INTO artifact_metadata(key, value) VALUES ('extract_contract_version', '{MillerExtractContract.ExpectedExtractContractVersion}');
                    INSERT INTO artifact_metadata(key, value) VALUES ('hash_algorithm', '{MillerExtractContract.ExpectedHashAlgorithm}');
                    INSERT INTO symbols(symbol_id, name, signature, kind, language, path, start_line, end_line, parent_symbol_id)
                    VALUES ('s1', 'Foo', 'public class Foo', 'class', 'csharp', 'Foo.cs', 1, 3, NULL);
                    """;
                command.ExecuteNonQuery();
            }

            var index = RepositoryIndexLoader.Load(dbPath);

            Assert.Equal(1, index.DocumentCount);
            // No breadcrumbs → the symbol is not a bridge node, and walking it yields nothing.
            Assert.False(index.BridgeGraph.Contains("s1"));
            Assert.Empty(index.BridgeGraph.Walk("s1", maxDepth: 3));
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
