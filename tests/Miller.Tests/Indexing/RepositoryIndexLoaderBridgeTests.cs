using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Core.Resolver; // BridgeKind, ConfidenceBand, ScoredEdge (the Walk result element type)
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the M4 bridge wiring through the SINGLE production build path (plan Task 9): after
/// <see cref="RepositoryIndexLoader.Load"/> runs <see cref="SqliteBridgeReader"/> + <see cref="BridgeGraphBuilder"/>,
/// the resulting <see cref="MillerRepositoryIndex.BridgeGraph"/> is populated and the existing index features still
/// work. Builds a temp 28/2 DB carrying the <c>UserDto → ApplicationUser → ApplicationUsers</c> chain (a CreateMap
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
        BuildChainDb();
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // Build a DB whose bridge breadcrumbs resolve into a real cross-language chain:
    //   CreateMap<ApplicationUser, UserDto>  (DTO↔entity, MapsTo)   +   DbSet<ApplicationUser> ApplicationUsers (entity↔table, StoredIn)
    // The entity (ApplicationUser) and DTO (UserDto) are real class symbols so the SymbolResolver resolves both
    // sides and the scorer can band the StoredIn/MapsTo edges High.
    private void BuildChainDb()
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE symbols (
                    id TEXT PRIMARY KEY, name TEXT, signature TEXT, kind TEXT, language TEXT,
                    file_path TEXT, start_line INTEGER, end_line INTEGER, parent_id TEXT, metadata TEXT
                );
                CREATE TABLE identifiers (
                    id TEXT PRIMARY KEY, name TEXT, kind TEXT, file_path TEXT, start_line INTEGER, containing_symbol_id TEXT
                );
                CREATE TABLE relationships (id TEXT PRIMARY KEY, from_symbol_id TEXT, to_symbol_id TEXT, kind TEXT);
                CREATE TABLE type_arguments (
                    id TEXT PRIMARY KEY, identifier_id TEXT, parent_arg_id TEXT, ordinal INTEGER,
                    type_name TEXT, target_symbol_id TEXT, file_path TEXT, language TEXT, last_indexed TEXT
                );
                CREATE TABLE literals (
                    id TEXT PRIMARY KEY, literal_text TEXT, kind TEXT, carrier TEXT, arg_position INTEGER,
                    language TEXT, file_path TEXT, start_line INTEGER, end_line INTEGER, start_byte INTEGER,
                    end_byte INTEGER, containing_symbol_id TEXT, confidence REAL
                );
                CREATE TABLE symbol_annotations (
                    id TEXT PRIMARY KEY, symbol_id TEXT, ordinal INTEGER, annotation TEXT, annotation_key TEXT, raw_text TEXT, carrier TEXT
                );
                CREATE TABLE schema_version (version INTEGER);
                CREATE TABLE external_extract_metadata (key TEXT, value TEXT);

                INSERT INTO schema_version(version) VALUES (28);
                INSERT INTO external_extract_metadata(key, value) VALUES ('extract_contract_version', '2');

                -- The entity, the DTO, and a DbContext exposing DbSet<ApplicationUser> ApplicationUsers.
                INSERT INTO symbols(id, name, signature, kind, language, file_path, start_line, end_line, parent_id, metadata)
                VALUES
                  ('s-entity', 'ApplicationUser', 'public class ApplicationUser', 'class', 'csharp', 'model/ApplicationUser.cs', 1, 20, NULL, NULL),
                  ('s-dto',    'UserDto',         'public class UserDto',         'class', 'csharp', 'dto/UserDto.cs',           1, 10, NULL, NULL),
                  ('s-ctx',    'AppDbContext',    'public class AppDbContext',    'class', 'csharp', 'data/AppDbContext.cs',     1, 30, NULL, NULL),
                  ('s-prop',   'ApplicationUsers','public DbSet<ApplicationUser> ApplicationUsers { get; set; }', 'property', 'csharp', 'data/AppDbContext.cs', 5, 5, 's-ctx', NULL),
                  ('s-profile','MapProfile',      'public class MapProfile',      'class', 'csharp', 'map/MapProfile.cs',        1, 8, NULL, NULL);

                -- CreateMap<ApplicationUser, UserDto> use-site: two top-level type args sharing identifier_id 'id-map'.
                INSERT INTO type_arguments(id, identifier_id, parent_arg_id, ordinal, type_name, target_symbol_id, file_path, language, last_indexed)
                VALUES
                  ('ta-0', 'id-map', NULL, 0, 'ApplicationUser', NULL, 'map/MapProfile.cs', 'csharp', NULL),
                  ('ta-1', 'id-map', NULL, 1, 'UserDto',         NULL, 'map/MapProfile.cs', 'csharp', NULL);
                """;
            command.ExecuteNonQuery();
        }
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
                command.CommandText = """
                    CREATE TABLE symbols (id TEXT PRIMARY KEY, name TEXT, signature TEXT, kind TEXT, language TEXT, file_path TEXT, start_line INTEGER, end_line INTEGER, parent_id TEXT, metadata TEXT);
                    CREATE TABLE identifiers (id TEXT PRIMARY KEY, name TEXT, kind TEXT, file_path TEXT, start_line INTEGER, containing_symbol_id TEXT);
                    CREATE TABLE relationships (id TEXT PRIMARY KEY, from_symbol_id TEXT, to_symbol_id TEXT, kind TEXT);
                    CREATE TABLE type_arguments (id TEXT PRIMARY KEY, identifier_id TEXT, parent_arg_id TEXT, ordinal INTEGER, type_name TEXT, target_symbol_id TEXT, file_path TEXT, language TEXT, last_indexed TEXT);
                    CREATE TABLE literals (id TEXT PRIMARY KEY, literal_text TEXT, kind TEXT, carrier TEXT, arg_position INTEGER, language TEXT, file_path TEXT, start_line INTEGER, end_line INTEGER, start_byte INTEGER, end_byte INTEGER, containing_symbol_id TEXT, confidence REAL);
                    CREATE TABLE symbol_annotations (id TEXT PRIMARY KEY, symbol_id TEXT, ordinal INTEGER, annotation TEXT, annotation_key TEXT, raw_text TEXT, carrier TEXT);
                    CREATE TABLE schema_version (version INTEGER);
                    CREATE TABLE external_extract_metadata (key TEXT, value TEXT);
                    INSERT INTO schema_version(version) VALUES (28);
                    INSERT INTO external_extract_metadata(key, value) VALUES ('extract_contract_version', '2');
                    INSERT INTO symbols(id, name, signature, kind, language, file_path, start_line, end_line, parent_id, metadata)
                    VALUES ('s1', 'Foo', 'public class Foo', 'class', 'csharp', 'Foo.cs', 1, 3, NULL, NULL);
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
