using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Core.Resolver; // BridgeKind, ConfidenceBand, ScoredEdge (the Walk result element type)
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Tools;
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
                    path TEXT, start_line INTEGER, end_line INTEGER, parent_symbol_id TEXT, visibility TEXT,
                    is_test INTEGER NOT NULL DEFAULT 0, metadata_json TEXT
                );
                CREATE TABLE identifiers (
                    identifier_id TEXT PRIMARY KEY, name TEXT, kind TEXT, path TEXT, start_line INTEGER,
                    containing_symbol_id TEXT, target_symbol_id TEXT, confidence REAL NOT NULL DEFAULT 1.0
                );
                CREATE TABLE identifier_resolutions (
                    identifier_id TEXT PRIMARY KEY, target_symbol_id TEXT, tier INTEGER, confidence REAL,
                    method TEXT, outcome TEXT NOT NULL, candidates INTEGER, resolved_at_revision INTEGER NOT NULL
                );
                CREATE TABLE relationships (
                    relationship_id TEXT PRIMARY KEY, from_symbol_id TEXT, to_symbol_id TEXT, path TEXT, kind TEXT,
                    confidence REAL NOT NULL DEFAULT 1.0
                );
                CREATE TABLE pending_relationships (
                    pending_relationship_id TEXT PRIMARY KEY,
                    from_symbol_id TEXT NOT NULL,
                    caller_scope_symbol_id TEXT,
                    file_id TEXT NOT NULL,
                    path TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    target_display_name TEXT NOT NULL,
                    target_terminal_name TEXT NOT NULL,
                    target_receiver TEXT,
                    target_namespace_json TEXT NOT NULL,
                    target_import_context TEXT,
                    start_line INTEGER NOT NULL,
                    start_column INTEGER,
                    end_line INTEGER,
                    end_column INTEGER,
                    start_byte INTEGER,
                    end_byte INTEGER,
                    confidence REAL NOT NULL,
                    metadata_json TEXT,
                    FOREIGN KEY (from_symbol_id) REFERENCES symbols(symbol_id) ON DELETE CASCADE,
                    FOREIGN KEY (caller_scope_symbol_id) REFERENCES symbols(symbol_id) ON DELETE SET NULL,
                    FOREIGN KEY (file_id) REFERENCES files(file_id) ON DELETE CASCADE
                );
                CREATE TABLE pending_resolutions (
                    pending_relationship_id TEXT PRIMARY KEY
                        REFERENCES pending_relationships(pending_relationship_id) ON DELETE CASCADE,
                    target_symbol_id TEXT NOT NULL REFERENCES symbols(symbol_id) ON DELETE CASCADE,
                    tier INTEGER NOT NULL,
                    confidence REAL NOT NULL,
                    method TEXT NOT NULL,
                    resolved_at_revision INTEGER NOT NULL
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
                    structural_fact_id TEXT PRIMARY KEY, file_id TEXT NOT NULL, path TEXT NOT NULL, language TEXT NOT NULL,
                    pattern_id TEXT NOT NULL, capture_name TEXT NOT NULL, node_kind TEXT NOT NULL,
                    containing_symbol_id TEXT, start_line INTEGER NOT NULL, start_column INTEGER NOT NULL,
                    end_line INTEGER NOT NULL, end_column INTEGER NOT NULL, start_byte INTEGER NOT NULL,
                    end_byte INTEGER NOT NULL, confidence REAL NOT NULL DEFAULT 1.0, metadata_json TEXT
                );
                CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);

                -- The entity, the DTO, and a DbContext exposing DbSet<ApplicationUser> ApplicationUsers.
                INSERT INTO symbols(symbol_id, name, signature, kind, language, path, start_line, end_line, parent_symbol_id)
                VALUES
                  ('s-entity', 'ApplicationUser', 'public class ApplicationUser', 'class', 'csharp', 'model/ApplicationUser.cs', 1, 20, NULL),
                  ('s-dto',    'UserDto',         'public class UserDto',         'class', 'csharp', 'dto/UserDto.cs',           1, 10, NULL),
                  ('s-ctx',    'AppDbContext',    'public class AppDbContext',    'class', 'csharp', 'data/AppDbContext.cs',     1, 30, NULL),
                  ('s-prop',   'ApplicationUsers','public DbSet<ApplicationUser> ApplicationUsers { get; set; }', 'property', 'csharp', 'data/AppDbContext.cs', 5, 5, 's-ctx'),
                  ('s-profile','MapProfile',      'public class MapProfile',      'class', 'csharp', 'map/MapProfile.cs',        1, 8, NULL),
                  ('s-map-connectors', 'MapAdminConnectorsEndpoints', 'public static IEndpointRouteBuilder MapAdminConnectorsEndpoints(this IEndpointRouteBuilder app)', 'method', 'csharp', 'endpoints/AdminConnectorsEndpoints.cs', 16, 49, NULL),
                  ('s-save', 'SaveAsync', 'private static Task<IResult> SaveAsync(ConnectorSaveRequest request)', 'method', 'csharp', 'endpoints/AdminConnectorsEndpoints.cs', 51, 72, 's-map-connectors'),
                  ('s-connector-form', 'ConnectorForm', 'component ConnectorForm', 'component', 'razor', 'components/ConnectorForm.razor', 1, 120, NULL);

                -- CreateMap<ApplicationUser, UserDto> use-site: ONE usage row carries identifier_id 'id-map' + path;
                -- the two top-level type args JOIN it by usage_id (the v1 split the reader re-assembles).
                INSERT INTO type_argument_usages(usage_id, identifier_id, path, language)
                VALUES ('u-map', 'id-map', 'map/MapProfile.cs', 'csharp');

                INSERT INTO type_arguments(type_argument_id, usage_id, parent_type_argument_id, ordinal, type_name)
                VALUES
                  ('ta-0', 'u-map', NULL, 0, 'ApplicationUser'),
                  ('ta-1', 'u-map', NULL, 1, 'UserDto');

                INSERT INTO structural_facts
                    (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                     containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                     confidence, metadata_json)
                VALUES
                  ('sf-htmx-save', 'f-form', 'components/ConnectorForm.razor', 'razor', 'htmx.attribute.v1',
                   'attribute', 'element', 's-connector-form', 61, 8, 61, 40, 2000, 2032, 1.0,
                   '{"framework":"htmx","attribute_name":"hx-post","attribute_value":"/admin/connectors/save","target_path":"/admin/connectors/save","verb":"POST"}'),
                  ('sf-route-save', 'f-endpoints', 'endpoints/AdminConnectorsEndpoints.cs', 'csharp', 'aspnet.minimal_api.route.v1',
                   'route_call', 'invocation_expression', 's-map-connectors', 43, 14, 43, 41, 1790, 1817, 1.0,
                   '{"framework":"aspnet","api_style":"minimal_api","route_template":"/save","effective_route_template":"/admin/connectors/save","verb":"POST","handler_kind":"method_group","handler_name":"SaveAsync"}');
                """;
            command.ExecuteNonQuery();
            JulieDbFixture.EnsureRequiredSchemaFiveTables(connection);

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

    private static (string Root, string DbPath) CreateNextOnlyWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-next-only-" + Guid.NewGuid().ToString("N"));
        string millerDir = Path.Combine(root, ".miller");
        System.IO.Directory.CreateDirectory(millerDir);
        string dbPath = Path.Combine(millerDir, "symbols.db");
        BuildNextOnlyDb(dbPath);
        return (root, dbPath);
    }

    private static void BuildNextOnlyDb(string dbPath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE symbols (
                symbol_id TEXT PRIMARY KEY, name TEXT, signature TEXT, kind TEXT, language TEXT,
                path TEXT, start_line INTEGER, end_line INTEGER, parent_symbol_id TEXT, visibility TEXT,
                is_test INTEGER NOT NULL DEFAULT 0, metadata_json TEXT
            );
            CREATE TABLE identifiers (
                identifier_id TEXT PRIMARY KEY, name TEXT, kind TEXT, path TEXT, start_line INTEGER,
                containing_symbol_id TEXT, target_symbol_id TEXT, confidence REAL NOT NULL DEFAULT 1.0
            );
            CREATE TABLE identifier_resolutions (
                identifier_id TEXT PRIMARY KEY, target_symbol_id TEXT, tier INTEGER, confidence REAL,
                method TEXT, outcome TEXT NOT NULL, candidates INTEGER, resolved_at_revision INTEGER NOT NULL
            );
            CREATE TABLE relationships (
                relationship_id TEXT PRIMARY KEY, from_symbol_id TEXT, to_symbol_id TEXT, path TEXT, kind TEXT,
                confidence REAL NOT NULL DEFAULT 1.0
            );
            CREATE TABLE pending_relationships (
                pending_relationship_id TEXT PRIMARY KEY,
                from_symbol_id TEXT NOT NULL,
                caller_scope_symbol_id TEXT,
                file_id TEXT NOT NULL,
                path TEXT NOT NULL,
                kind TEXT NOT NULL,
                target_display_name TEXT NOT NULL,
                target_terminal_name TEXT NOT NULL,
                target_receiver TEXT,
                target_namespace_json TEXT NOT NULL,
                target_import_context TEXT,
                start_line INTEGER NOT NULL,
                start_column INTEGER,
                end_line INTEGER,
                end_column INTEGER,
                start_byte INTEGER,
                end_byte INTEGER,
                confidence REAL NOT NULL,
                metadata_json TEXT,
                FOREIGN KEY (from_symbol_id) REFERENCES symbols(symbol_id) ON DELETE CASCADE,
                FOREIGN KEY (caller_scope_symbol_id) REFERENCES symbols(symbol_id) ON DELETE SET NULL,
                FOREIGN KEY (file_id) REFERENCES files(file_id) ON DELETE CASCADE
            );
            CREATE TABLE pending_resolutions (
                pending_relationship_id TEXT PRIMARY KEY
                    REFERENCES pending_relationships(pending_relationship_id) ON DELETE CASCADE,
                target_symbol_id TEXT NOT NULL REFERENCES symbols(symbol_id) ON DELETE CASCADE,
                tier INTEGER NOT NULL,
                confidence REAL NOT NULL,
                method TEXT NOT NULL,
                resolved_at_revision INTEGER NOT NULL
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
                structural_fact_id TEXT PRIMARY KEY, file_id TEXT NOT NULL, path TEXT NOT NULL, language TEXT NOT NULL,
                pattern_id TEXT NOT NULL, capture_name TEXT NOT NULL, node_kind TEXT NOT NULL,
                containing_symbol_id TEXT, start_line INTEGER NOT NULL, start_column INTEGER NOT NULL,
                end_line INTEGER NOT NULL, end_column INTEGER NOT NULL, start_byte INTEGER NOT NULL,
                end_byte INTEGER NOT NULL, confidence REAL NOT NULL DEFAULT 1.0, metadata_json TEXT
            );
            CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        JulieDbFixture.EnsureRequiredSchemaFiveTables(connection);
        SeedGate(command);
        InsertNextRouteFacts(command);
    }

    private static void AddNextRouteFacts(string dbPath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        InsertNextRouteFacts(command);
    }

    private static void InsertNextRouteFacts(SqliteCommand command)
    {
        command.CommandText = """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
              ('sf-next-settings-link', 'f-next-nav', 'web/Nav.tsx', 'tsx', 'nextjs.route_reference.v1',
               'jsx_attribute', 'jsx_opening_element', NULL, 12, 18, 12, 35, 3000, 3017, 1.0,
               '{"framework":"nextjs","target_path":"/settings"}'),
              ('sf-next-settings-page', 'f-next-page', 'web/app/settings/page.tsx', 'tsx', 'nextjs.file_route.v1',
               'file_route', 'source_file', NULL, 1, 1, 40, 1, 4000, 5000, 1.0,
               '{"framework":"nextjs","route_path":"/settings"}');
            """;
        command.ExecuteNonQuery();
    }

    private static void AddNuxtRouteFacts(string dbPath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
              ('sf-nuxt-about-link', 'f-nuxt-nav', 'app/components/Nav.vue', 'vue', 'nuxt.route_reference.v1',
               'route_reference', 'template_attribute', NULL, 8, 18, 8, 35, 3000, 3017, 1.0,
               '{"framework":"nuxt","target_path":"/about"}'),
              ('sf-nuxt-about-page', 'f-nuxt-page', 'app/pages/about.vue', 'vue', 'nuxt.file_route.v1',
               'file_route', 'file', NULL, 1, 1, 20, 1, 4000, 5000, 1.0,
               '{"framework":"nuxt","route_path":"/about"}');
            """;
        command.ExecuteNonQuery();
    }

    // The four 2.6.0 HTTP-boundary fact families (plan Task 2): a fetch client request, an ASP.NET attribute
    // route, a Next.js route handler, and a Nuxt server route — plus the two symbols the dotnet-web bridge binds.
    private static void AddHttpBoundaryFacts(string dbPath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO symbols(symbol_id, name, signature, kind, language, path, start_line, end_line, parent_symbol_id)
            VALUES
              ('s-users-get', 'GetUsers', 'IResult GetUsers()', 'method', 'csharp', 'api/UsersController.cs', 12, 20, NULL),
              ('s-fetch-users', 'fetchUsers', 'function fetchUsers()', 'function', 'typescript', 'web/api.ts', 4, 9, NULL);

            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
              ('sf-fetch-users', 'f-web-api', 'web/api.ts', 'typescript', 'http.client_request.v1',
               'client_request', 'call_expression', 's-fetch-users', 5, 3, 5, 40, 100, 140, 1.0,
               '{"client":"fetch","framework":"fetch","target_path":"/api/users","url_kind":"path","verb":"GET","verb_source":"default"}'),
              ('sf-users-get', 'f-users-controller', 'api/UsersController.cs', 'csharp', 'aspnet.attribute_route.v1',
               'attribute', 'attribute', 's-users-get', 12, 5, 12, 15, 300, 320, 1.0,
               '{"attribute_kind":"http_method","verb":"GET","controller_route_template":"api/[controller]","effective_route_template":"/api/users","route_tokens":["controller"]}'),
              ('sf-next-handler', 'f-next-route', 'web/app/api/messages/route.ts', 'typescript', 'nextjs.route_handler.v1',
               'route_handler', 'export_statement', NULL, 1, 1, 10, 1, 0, 200, 1.0,
               '{"framework":"nextjs","router":"app","route_path":"/api/messages","verb":"GET","verb_source":"attested"}'),
              ('sf-nuxt-handler', 'f-nuxt-route', 'server/api/notes.ts', 'typescript', 'nuxt.server_route.v1',
               'server_route', 'source_file', NULL, 1, 1, 8, 1, 0, 150, 1.0,
               '{"framework":"nuxt","route_path":"/api/notes"}');
            """;
        command.ExecuteNonQuery();
    }

    // A matching Next.js client-request/route-handler pair (plan Task 4): the exported GET handler symbol,
    // the containing client function, and the two 2.6.0 facts that bridge them.
    private static void AddNextApiBoundaryFacts(string dbPath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO symbols(symbol_id, name, signature, kind, language, path, start_line, end_line, parent_symbol_id)
            VALUES
              ('s-next-get', 'GET', 'export async function GET(request: Request)', 'function', 'typescript', 'web/app/api/messages/route.ts', 3, 9, NULL),
              ('s-load-messages', 'loadMessages', 'function loadMessages()', 'function', 'typescript', 'web/lib/api.ts', 4, 9, NULL);

            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
              ('sf-next-msg-handler', 'f-next-msg-route', 'web/app/api/messages/route.ts', 'typescript', 'nextjs.route_handler.v1',
               'route_handler', 'export_statement', 's-next-get', 3, 1, 9, 1, 40, 260, 1.0,
               '{"framework":"nextjs","router":"app","route_path":"/api/messages","verb":"GET","verb_source":"attested"}'),
              ('sf-fetch-messages', 'f-web-lib-api', 'web/lib/api.ts', 'typescript', 'http.client_request.v1',
               'client_request', 'call_expression', 's-load-messages', 5, 3, 5, 40, 100, 140, 1.0,
               '{"client":"fetch","framework":"fetch","target_path":"/api/messages","url_kind":"path","verb":"GET","verb_source":"default"}');
            """;
        command.ExecuteNonQuery();
    }

    // A matching Express client-request/route-template pair (plan Task 2): the handler symbol carrying the
    // express.route.v1 fact, the containing client function, and the two facts that bridge them through the
    // SqliteBridgeReader whitelist (express.route.v1 was added to the whitelist in Task 1).
    private static void AddBackendHttpBoundaryFacts(string dbPath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO symbols(symbol_id, name, signature, kind, language, path, start_line, end_line, parent_symbol_id)
            VALUES
              ('s-express-create', 'createUser', 'createUser(req, res)', 'function', 'javascript', 'api/users.js', 12, 20, NULL),
              ('s-create-user', 'createUser', 'function createUser()', 'function', 'typescript', 'web/lib/api.ts', 4, 9, NULL);

            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
              ('sf-express-route', 'f-users-js', 'api/users.js', 'javascript', 'express.route.v1',
               'route_call', 'call_expression', 's-express-create', 12, 1, 12, 40, 40, 260, 1.0,
               '{"framework":"express","normalized_route_template":"/api/users","verb":"POST"}'),
              ('sf-express-client', 'f-web-lib-api', 'web/lib/api.ts', 'typescript', 'http.client_request.v1',
               'client_request', 'call_expression', 's-create-user', 5, 3, 5, 40, 100, 140, 1.0,
               '{"client":"fetch","framework":"fetch","target_path":"/api/users","url_kind":"path","verb":"POST","verb_source":"attested"}');
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Load_RootMillerJsonBackendHttpProvider_BridgesClientRequestToBackendRoute()
    {
        var (root, dbPath) = CreateConfiguredBridgeWorkspace("""
            {
              "bridge": {
                "providers": ["backend-http"]
              }
            }
            """);
        try
        {
            AddBackendHttpBoundaryFacts(dbPath);

            var index = RepositoryIndexLoader.Load(dbPath);

            Assert.False(index.BridgeGraph.Contains("s-entity"));
            Assert.Contains("backend-http", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.DoesNotContain("dotnet-web", index.BridgeGraph.CapabilityReport.ActiveProviders);

            var edge = Assert.Single(index.BridgeGraph.Edges, item => item.Edge.Kind == BridgeKind.Hits);
            Assert.Equal(ConfidenceBand.High, edge.Band);
            Assert.Equal("s-create-user", edge.Edge.SourceRef.SymbolId);
            Assert.Equal("s-express-create", edge.Edge.TargetRef.SymbolId);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["backend-http.clientRequests"]);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["backend-http.routeFacts"]);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["backend-http.candidates"]);
            Assert.Equal(0, index.BridgeGraph.CapabilityReport.EvidenceCounts["backend-http.ambiguousMatches"]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Load_RootMillerJsonNextJsApiProvider_BridgesClientRequestToRouteHandler()
    {
        var (root, dbPath) = CreateConfiguredBridgeWorkspace("""
            {
              "bridge": {
                "providers": ["nextjs-api"]
              }
            }
            """);
        try
        {
            AddNextApiBoundaryFacts(dbPath);

            var index = RepositoryIndexLoader.Load(dbPath);

            Assert.False(index.BridgeGraph.Contains("s-entity"));
            Assert.Contains("nextjs-api", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.DoesNotContain("dotnet-web", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.DoesNotContain("nextjs", index.BridgeGraph.CapabilityReport.ActiveProviders);

            var edge = Assert.Single(index.BridgeGraph.Edges, item => item.Edge.Kind == BridgeKind.Hits);
            Assert.Equal(ConfidenceBand.High, edge.Band);
            Assert.Equal("s-load-messages", edge.Edge.SourceRef.SymbolId);
            Assert.Equal("s-next-get", edge.Edge.TargetRef.SymbolId);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nextjs-api.clientRequests"]);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nextjs-api.routeHandlers"]);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nextjs-api.candidates"]);
            Assert.Equal(0, index.BridgeGraph.CapabilityReport.EvidenceCounts["nextjs-api.ambiguousMatches"]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Load_RootMillerJsonNuxtApiProvider_IsSelectableAndReportsEvidence()
    {
        var (root, dbPath) = CreateConfiguredBridgeWorkspace("""
            {
              "bridge": {
                "providers": ["nuxt-api"]
              }
            }
            """);
        try
        {
            // The fetch /api/users request and the suffix-less /api/notes server route do not match:
            // the provider is selected and active with evidence but emits no edge.
            AddHttpBoundaryFacts(dbPath);

            var index = RepositoryIndexLoader.Load(dbPath);

            Assert.False(index.BridgeGraph.Contains("s-entity"));
            Assert.Contains("nuxt-api", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.DoesNotContain("dotnet-web", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.Empty(index.BridgeGraph.Edges);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nuxt-api.clientRequests"]);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nuxt-api.serverRoutes"]);
            Assert.Equal(0, index.BridgeGraph.CapabilityReport.EvidenceCounts["nuxt-api.candidates"]);
            Assert.Equal(0, index.BridgeGraph.CapabilityReport.EvidenceCounts["nuxt-api.ambiguousMatches"]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Load_HttpBoundaryFactFamilies_LoadThroughTheWhitelistAndBridge()
    {
        AddHttpBoundaryFacts(_dbPath);

        var index = RepositoryIndexLoader.Load(_dbPath);

        // All four 2.6.0 families pass the SqliteBridgeReader whitelist: the base fixture carries 2 facts, +4 here.
        Assert.Equal(6, index.BridgeGraph.CapabilityReport.EvidenceCounts["dotnet-web.structuralFacts"]);
        Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["dotnet-web.clientRequests"]);
        Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["dotnet-web.attributeRoutes"]);

        // And the client request bridges to the attribute-routed action through the production Load path.
        var fromClient = index.BridgeGraph.Walk("s-fetch-users", maxDepth: 2);
        var hits = Assert.Single(fromClient, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hits.Band);
        Assert.Equal("s-users-get", hits.Edge.TargetRef.SymbolId);
    }

    private static void TryDelete(string path)
    {
        try { System.IO.Directory.Delete(path, recursive: true); } catch { /* best effort */ }
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
        Assert.Equal(8, index.DocumentCount);
        Assert.Equal("s-entity", index.FindByName("ApplicationUser").Single().SymbolId);
        Assert.True(index.Graph.Contains("s-entity"));
    }

    [Fact]
    public void Load_MalformedBlazorMetadata_PreservesRepositoryAndUnrelatedGraphEvidence()
    {
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWrite }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE symbols
                SET metadata_json = '{"type":7,"qualifiedName":"Broken.ApplicationUser"}'
                WHERE symbol_id = 's-entity';

                INSERT INTO structural_facts
                    (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                     containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                     confidence, metadata_json)
                VALUES
                    ('sf-blazor-malformed', 'f-form', 'components/ConnectorForm.razor', 'razor',
                     'blazor.component_reference.v1', 'component_reference', 'markup_element', 's-connector-form',
                     70, 8, 70, 28, 2200, 2220, 1.0,
                     '{"tag":"Broken","containing_component":"ConnectorForm","namespace_context":[]}');
                """;
            command.ExecuteNonQuery();
        }

        var index = RepositoryIndexLoader.Load(_dbPath);

        Assert.Equal(8, index.DocumentCount);
        Assert.Equal("s-entity", index.FindByName("ApplicationUser").Single().SymbolId);
        Assert.True(index.Graph.Contains("s-entity"));
        Assert.Contains(index.BridgeGraph.Walk("s-entity", maxDepth: 3), edge => edge.Edge.Kind == BridgeKind.StoredIn);
    }

    [Fact]
    public void Load_UsesStructuralFacts_ToBridgeHtmxRouteToMinimalApiHandler()
    {
        var index = RepositoryIndexLoader.Load(_dbPath);

        var fromComponent = index.BridgeGraph.Walk("s-connector-form", maxDepth: 2);
        var hits = Assert.Single(fromComponent, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hits.Band);
        Assert.Equal("s-connector-form", hits.Edge.SourceRef.SymbolId);
        Assert.Equal("s-save", hits.Edge.TargetRef.SymbolId);
        Assert.Equal("admin/connectors/save", hits.Edge.SourceRef.Display);
        Assert.DoesNotContain(index.BridgeGraph.Incident("s-save"), e => e.Edge.Kind == BridgeKind.Consumes);

        Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["dotnet-web.structuralClientCalls"]);
        Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["dotnet-web.structuralEndpoints"]);
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
    public void ProvidersForDatabase_NoConfig_ReturnsAllDefaultBridgeProviders()
    {
        var providers = BridgeProviderSelection.ProvidersForDatabase(_dbPath);

        Assert.Equal(
            ["dotnet-web", "nextjs", "nextjs-api", "nuxt", "nuxt-api", "vue", "react", "blazor", "backend-http"],
            providers.Select(provider => provider.Id).ToArray());
    }

    [Fact]
    public void Load_NoConfigPureNextFacts_BuildsTraceableNavigationBridge()
    {
        var (root, dbPath) = CreateNextOnlyWorkspace();
        try
        {
            var index = RepositoryIndexLoader.Load(dbPath);

            Assert.Equal(0, index.DocumentCount);
            Assert.Contains("nextjs", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nextjs.routeReferences"]);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nextjs.fileRoutes"]);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nextjs.candidates"]);

            var edge = Assert.Single(index.BridgeGraph.Edges, item => item.Edge.Kind == BridgeKind.NavigatesTo);
            Assert.Equal(ConfidenceBand.High, edge.Band);
            Assert.Equal("/settings", edge.Edge.SourceRef.Display);
            Assert.Equal("/settings", edge.Edge.TargetRef.Display);

            string rendered = TraceTool.Run(index, new SmartTargetResolver(index),
                target: "/settings", mode: "bridge", to: null, depth: 2, limit: 20, fullFormat: false,
                out int emitted, out _);

            Assert.Equal(1, emitted);
            Assert.Contains("# trace bridge /settings", rendered);
            Assert.Contains("/settings  --navigates_to-->  /settings", rendered);
            Assert.DoesNotContain("--route-->", rendered);
        }
        finally
        {
            TryDelete(root);
        }
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
            Assert.DoesNotContain("nextjs", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.DoesNotContain("nuxt", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.DoesNotContain(
                index.BridgeGraph.CapabilityReport.SkippedProviders,
                skipped => skipped.ProviderId == "nextjs");
            Assert.DoesNotContain(
                index.BridgeGraph.CapabilityReport.SkippedProviders,
                skipped => skipped.ProviderId == "nuxt");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Load_RootMillerJsonNextJsProvider_PopulatesOnlyNextBridgeGraph()
    {
        var (root, dbPath) = CreateConfiguredBridgeWorkspace("""
            {
              "bridge": {
                "providers": ["nextjs"]
              }
            }
            """);
        try
        {
            AddNextRouteFacts(dbPath);

            var index = RepositoryIndexLoader.Load(dbPath);

            Assert.False(index.BridgeGraph.Contains("s-entity"));
            var edge = Assert.Single(index.BridgeGraph.Edges, item => item.Edge.Kind == BridgeKind.NavigatesTo);
            Assert.Equal("/settings", edge.Edge.TargetRef.Display);
            Assert.Contains("nextjs", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.DoesNotContain("dotnet-web", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.DoesNotContain(
                index.BridgeGraph.CapabilityReport.SkippedProviders,
                skipped => skipped.ProviderId == "dotnet-web");
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nextjs.routeReferences"]);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nextjs.fileRoutes"]);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nextjs.candidates"]);
            Assert.Equal(0, index.BridgeGraph.CapabilityReport.EvidenceCounts["nextjs.ambiguousMatches"]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Load_RootMillerJsonNuxtProvider_PopulatesOnlyNuxtBridgeGraph()
    {
        var (root, dbPath) = CreateConfiguredBridgeWorkspace("""
            {
              "bridge": {
                "providers": ["nuxt"]
              }
            }
            """);
        try
        {
            AddNuxtRouteFacts(dbPath);

            var index = RepositoryIndexLoader.Load(dbPath);

            Assert.False(index.BridgeGraph.Contains("s-entity"));
            var edge = Assert.Single(index.BridgeGraph.Edges, item => item.Edge.Kind == BridgeKind.NavigatesTo);
            Assert.Equal("/about", edge.Edge.TargetRef.Display);
            Assert.Contains("nuxt", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.DoesNotContain("dotnet-web", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.DoesNotContain("nextjs", index.BridgeGraph.CapabilityReport.ActiveProviders);
            Assert.DoesNotContain(
                index.BridgeGraph.CapabilityReport.SkippedProviders,
                skipped => skipped.ProviderId == "dotnet-web");
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nuxt.routeReferences"]);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nuxt.fileRoutes"]);
            Assert.Equal(1, index.BridgeGraph.CapabilityReport.EvidenceCounts["nuxt.candidates"]);
            Assert.Equal(0, index.BridgeGraph.CapabilityReport.EvidenceCounts["nuxt.ambiguousMatches"]);
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
            Assert.DoesNotContain(
                index.BridgeGraph.CapabilityReport.SkippedProviders,
                item => item.ProviderId == "nextjs");
            Assert.DoesNotContain(
                index.BridgeGraph.CapabilityReport.SkippedProviders,
                item => item.ProviderId == "nuxt");
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
                    CREATE TABLE symbols (symbol_id TEXT PRIMARY KEY, name TEXT, signature TEXT, kind TEXT, language TEXT, path TEXT, start_line INTEGER, end_line INTEGER, parent_symbol_id TEXT, visibility TEXT, is_test INTEGER NOT NULL DEFAULT 0, metadata_json TEXT);
                    CREATE TABLE identifiers (identifier_id TEXT PRIMARY KEY, name TEXT, kind TEXT, path TEXT, start_line INTEGER, containing_symbol_id TEXT, target_symbol_id TEXT, confidence REAL NOT NULL DEFAULT 1.0);
                    CREATE TABLE identifier_resolutions (identifier_id TEXT PRIMARY KEY, target_symbol_id TEXT, tier INTEGER, confidence REAL, method TEXT, outcome TEXT NOT NULL, candidates INTEGER, resolved_at_revision INTEGER NOT NULL);
                    CREATE TABLE relationships (relationship_id TEXT PRIMARY KEY, from_symbol_id TEXT, to_symbol_id TEXT, path TEXT, kind TEXT, confidence REAL NOT NULL DEFAULT 1.0);
                    CREATE TABLE pending_relationships (
                        pending_relationship_id TEXT PRIMARY KEY,
                        from_symbol_id TEXT NOT NULL,
                        caller_scope_symbol_id TEXT,
                        file_id TEXT NOT NULL,
                        path TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        target_display_name TEXT NOT NULL,
                        target_terminal_name TEXT NOT NULL,
                        target_receiver TEXT,
                        target_namespace_json TEXT NOT NULL,
                        target_import_context TEXT,
                        start_line INTEGER NOT NULL,
                        start_column INTEGER,
                        end_line INTEGER,
                        end_column INTEGER,
                        start_byte INTEGER,
                        end_byte INTEGER,
                        confidence REAL NOT NULL,
                        metadata_json TEXT,
                        FOREIGN KEY (from_symbol_id) REFERENCES symbols(symbol_id) ON DELETE CASCADE,
                        FOREIGN KEY (caller_scope_symbol_id) REFERENCES symbols(symbol_id) ON DELETE SET NULL,
                        FOREIGN KEY (file_id) REFERENCES files(file_id) ON DELETE CASCADE
                    );
                    CREATE TABLE pending_resolutions (
                        pending_relationship_id TEXT PRIMARY KEY
                            REFERENCES pending_relationships(pending_relationship_id) ON DELETE CASCADE,
                        target_symbol_id TEXT NOT NULL REFERENCES symbols(symbol_id) ON DELETE CASCADE,
                        tier INTEGER NOT NULL,
                        confidence REAL NOT NULL,
                        method TEXT NOT NULL,
                        resolved_at_revision INTEGER NOT NULL
                    );
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
                JulieDbFixture.EnsureRequiredSchemaFiveTables(connection);
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
