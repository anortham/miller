using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins <see cref="SqliteBridgeReader"/> (plan Task 9) against a hand-written v1 bridge DB. These are
/// read-CONTRACT tests: they assert the exact <see cref="Miller.Core.Contracts.TypeArgument"/> /
/// <see cref="Miller.Core.Contracts.LiteralRecord"/> / <see cref="Miller.Core.Contracts.SymbolAnnotation"/> /
/// <see cref="Miller.Core.Contracts.DbSetProperty"/> mapping (ordering, NULL discipline, the DbSet&lt;T&gt;
/// signature parse, and the literal→file:line seam the <see cref="BridgeGraphBuilder"/> requires). The reader
/// performs NO leg transformation — that is Task 8's job — so these assert raw rows only. Fast suite: a temp
/// SQLite DB, no julie-extract.
/// </summary>
public sealed class SqliteBridgeReaderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public SqliteBridgeReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-bridge-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "symbols.db");
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- DB builder ------------------------------------------------------------------------------------------

    private SqliteConnection OpenWrite()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        return connection;
    }

    // Create the v1 bridge-relevant schema (the tables the reader reads + the artifact_metadata gate table) and
    // seed the gate. v1 splits type_arguments/type_argument_usages, drops symbol_annotations.ordinal, and renames
    // file_path→path / id→{symbol_id,literal_id}.
    private void CreateSchemaAndGate(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE symbols (
                symbol_id TEXT PRIMARY KEY, name TEXT, signature TEXT, kind TEXT, language TEXT,
                path TEXT, start_line INTEGER, end_line INTEGER, parent_symbol_id TEXT, metadata_json TEXT
            );
            CREATE TABLE type_argument_usages (
                usage_id TEXT PRIMARY KEY, identifier_id TEXT, file_id TEXT, path TEXT, language TEXT, metadata_json TEXT
            );
            CREATE TABLE type_arguments (
                type_argument_id TEXT PRIMARY KEY, usage_id TEXT, parent_type_argument_id TEXT, ordinal INTEGER,
                type_name TEXT
            );
            CREATE TABLE literals (
                literal_id TEXT PRIMARY KEY, literal_text TEXT, kind TEXT, carrier TEXT, arg_position INTEGER,
                language TEXT, path TEXT, start_line INTEGER, end_line INTEGER, start_byte INTEGER,
                end_byte INTEGER, containing_symbol_id TEXT, confidence REAL
            );
            CREATE TABLE symbol_annotations (
                annotation_id TEXT PRIMARY KEY, symbol_id TEXT, annotation TEXT, annotation_key TEXT,
                raw_text TEXT, carrier TEXT
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

        using var seed = connection.CreateCommand();
        // Seed the v1 gate keys from the pinned constants (not literals) so a julie re-pin needs no edit here.
        seed.CommandText = $"""
            INSERT INTO artifact_metadata(key, value) VALUES ('sqlite_schema_version', '{MillerExtractContract.ExpectedSqliteSchemaVersion}');
            INSERT INTO artifact_metadata(key, value) VALUES ('schema_version', '{MillerExtractContract.ExpectedSchemaVersion}');
            INSERT INTO artifact_metadata(key, value) VALUES ('extract_contract_version', '{MillerExtractContract.ExpectedExtractContractVersion}');
            INSERT INTO artifact_metadata(key, value) VALUES ('hash_algorithm', '{MillerExtractContract.ExpectedHashAlgorithm}');
            """;
        seed.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // ---- type_arguments --------------------------------------------------------------------------------------

    [Fact]
    public void Read_TypeArguments_OrderedByIdentifierThenOrdinal_WithNullDiscipline()
    {
        using (var c = OpenWrite())
        {
            CreateSchemaAndGate(c);
            // v1 split: the usage rows carry identifier_id/path; the arg rows carry usage_id/ordinal/parent/type.
            // Insert OUT of order to prove the reader sorts by (identifier_id, ordinal).
            Exec(c, """
                INSERT INTO type_argument_usages(usage_id, identifier_id, file_id, path, language) VALUES
                  ('uA','idA','fA','src/Map.cs','csharp'),
                  ('uB','idB','fB','src/Profile.cs','csharp');
                INSERT INTO type_arguments(type_argument_id, usage_id, parent_type_argument_id, ordinal, type_name)
                VALUES
                  ('t4', 'uB', NULL, 1, 'ApplicationUser'),
                  ('t2', 'uA', NULL, 1, 'UserDto'),
                  ('t1', 'uA', NULL, 0, 'ApplicationUser'),
                  ('t3', 'uB', NULL, 0, 'List'),
                  ('t5', 'uB', 't3', 0, 'Inner');
                """);
        }

        var data = SqliteBridgeReader.Read(_dbPath);

        // Ordered by identifier_id then ordinal: idA(0,1) then idB(0,1) then nested idB(t5, ordinal 0).
        Assert.Equal(
            new[] { ("idA", 0, "ApplicationUser"), ("idA", 1, "UserDto"),
                    ("idB", 0, "List"), ("idB", 0, "Inner"), ("idB", 1, "ApplicationUser") },
            data.TypeArguments.Select(t => (t.IdentifierId, t.Ordinal, t.TypeName)).ToArray());

        // parent_arg_id NULL → null; populated → verbatim. The nested arg is the one with ParentArgId="t3".
        var nested = data.TypeArguments.Single(t => t.TypeName == "Inner");
        Assert.Equal("t3", nested.ParentArgId);
        Assert.Null(data.TypeArguments.Single(t => t.TypeName == "UserDto").ParentArgId);
        Assert.Equal("src/Map.cs", data.TypeArguments.Single(t => t.TypeName == "UserDto").FilePath);
    }

    // ---- literals + the literal→file:line seam ---------------------------------------------------------------

    [Fact]
    public void Read_Literals_MapsUrlAndSql_AndExposesPerLiteralFileLineSites()
    {
        using (var c = OpenWrite())
        {
            CreateSchemaAndGate(c);
            // A url literal (TS client call) and a sql literal — both must come through; ordered by path,start_byte.
            Exec(c, """
                INSERT INTO literals(literal_id, literal_text, kind, carrier, arg_position, language, path, start_line, end_line, start_byte, end_byte, containing_symbol_id, confidence)
                VALUES
                  ('l2', '/api/users/{}', 'url', 'axios.get',  0, 'typescript', 'web/api.ts', 42, 42, 120, 135, 'sym-ts',  0.9),
                  ('l1', 'SELECT 1 FROM dbo.AppSettings', 'sql', 'QueryAsync', 1, 'csharp', 'data/Repo.cs', 7, 7, 30, 60, 'sym-cs', 0.8);
                """);
        }

        var data = SqliteBridgeReader.Read(_dbPath);

        // Ordered by path then start_byte: data/Repo.cs(sql) before web/api.ts(url).
        Assert.Equal(new[] { "sql", "url" }, data.Literals.Select(l => l.Kind).ToArray());

        var url = data.Literals.Single(l => l.Kind == "url");
        Assert.Equal("/api/users/{}", url.LiteralText);
        Assert.Equal("axios.get", url.Carrier);
        Assert.Equal(0, url.ArgPosition);
        Assert.Equal("typescript", url.Language);
        Assert.Equal("sym-ts", url.ContainingSymbolId);
        Assert.Equal(120, url.Span.StartByte);
        Assert.Equal(135, url.Span.EndByte);

        // The literal→(file,line) seam: the lookup carries the literals row's OWN file_path/start_line, which the
        // lean LiteralRecord does not re-expose. This is what BridgeGraphBuilder consumes for literal evidence.
        Assert.True(data.LiteralSites.TryGetValue(url, out var urlSite));
        Assert.Equal("web/api.ts", urlSite.FilePath);
        Assert.Equal(42, urlSite.Line);

        var sql = data.Literals.Single(l => l.Kind == "sql");
        Assert.True(data.LiteralSites.TryGetValue(sql, out var sqlSite));
        Assert.Equal("data/Repo.cs", sqlSite.FilePath);
        Assert.Equal(7, sqlSite.Line);
    }

    // ---- symbol_annotations ----------------------------------------------------------------------------------

    [Fact]
    public void Read_Annotations_OrderedBySymbolThenAnnotationId_ArgsLiveInRawText()
    {
        using (var c = OpenWrite())
        {
            CreateSchemaAndGate(c);
            // A class [Route] and a method [HttpGet] — inserted out of order to prove (symbol_id, annotation_id)
            // sort. v1 has no ordinal column; ordering re-keys to (symbol_id, annotation_id).
            Exec(c, """
                INSERT INTO symbol_annotations(annotation_id, symbol_id, annotation, annotation_key, raw_text, carrier)
                VALUES
                  ('a2', 'sym-method', 'HttpGet', 'httpget', 'HttpGet("{id}")', 'attribute'),
                  ('a1', 'sym-class',  'Route',   'route',   'Route("api/[controller]")', 'attribute');
                """);
        }

        var data = SqliteBridgeReader.Read(_dbPath);

        // Ordered by symbol_id then annotation_id: sym-class before sym-method.
        Assert.Equal(new[] { "sym-class", "sym-method" }, data.Annotations.Select(a => a.SymbolId).ToArray());

        var route = data.Annotations.Single(a => a.AnnotationKey == "route");
        Assert.Equal("Route", route.Annotation);
        Assert.Equal("Route(\"api/[controller]\")", route.RawText); // args live ONLY in raw_text (findings 28-2)
        Assert.Equal("attribute", route.Carrier);

        var verb = data.Annotations.Single(a => a.AnnotationKey == "httpget");
        Assert.Equal("HttpGet(\"{id}\")", verb.RawText);
    }

    // ---- DbSet<T> properties ---------------------------------------------------------------------------------

    [Fact]
    public void Read_DbSetProperties_TableIsPropertyName_EntityIsGenericArg_LeafResolved()
    {
        using (var c = OpenWrite())
        {
            CreateSchemaAndGate(c);
            // Three DbContext properties + one non-DbSet property (must be excluded) + a namespaced generic arg.
            Exec(c, """
                INSERT INTO symbols(symbol_id, name, signature, kind, language, path, start_line, end_line, parent_symbol_id, metadata_json)
                VALUES
                  ('p1', 'ApplicationUsers', 'public DbSet<ApplicationUser> ApplicationUsers { get; set; }', 'property', 'csharp', 'data/Ctx.cs', 10, 10, 'ctx', NULL),
                  ('p2', 'AppSettings',      'public DbSet<Core.Data.AppSetting> AppSettings { get; set; }',  'property', 'csharp', 'data/Ctx.cs', 11, 11, 'ctx', NULL),
                  ('p3', 'Preferences',      'public DbSet<Preferences> Preferences { get; set; }',          'property', 'csharp', 'data/Ctx.cs', 12, 12, 'ctx', NULL),
                  ('p4', 'Name',             'public string Name { get; set; }',                              'property', 'csharp', 'model/User.cs', 5, 5, 'u', NULL),
                  ('m1', 'SaveChanges',      'public int SaveChanges()',                                      'method',   'csharp', 'data/Ctx.cs', 20, 22, 'ctx', NULL);
                """);
        }

        var data = SqliteBridgeReader.Read(_dbPath);

        // Only the three DbSet<T> properties are anchors; the plain string property + the method are excluded.
        Assert.Equal(
            new[] { ("ApplicationUsers", "ApplicationUser"), ("AppSettings", "AppSetting"), ("Preferences", "Preferences") },
            data.DbSetProperties
                .OrderBy(d => d.StartLine)
                .Select(d => (d.TableName, d.EntityTypeName))
                .ToArray());

        // Table = property NAME (EF convention), NOT a pluralized entity name; entity = the DbSet<T> generic LEAF.
        var appUsers = data.DbSetProperties.Single(d => d.TableName == "ApplicationUsers");
        Assert.Equal("ApplicationUser", appUsers.EntityTypeName); // not "ApplicationUsers"
        Assert.Equal("data/Ctx.cs", appUsers.FilePath);
        Assert.Equal(10, appUsers.StartLine);
        // A namespaced generic arg resolves to its leaf type.
        Assert.Equal("AppSetting", data.DbSetProperties.Single(d => d.TableName == "AppSettings").EntityTypeName);
    }

    [Fact]
    public void Read_EmptyBridgeTables_YieldEmptyCollections_NotNull()
    {
        using (var c = OpenWrite())
            CreateSchemaAndGate(c); // schema + gate, no bridge rows

        var data = SqliteBridgeReader.Read(_dbPath);

        Assert.Empty(data.TypeArguments);
        Assert.Empty(data.Literals);
        Assert.Empty(data.Annotations);
        Assert.Empty(data.DbSetProperties);
        Assert.Empty(data.StructuralFacts);
        Assert.Empty(data.LiteralSites);
    }

    [Fact]
    public void Read_StructuralFacts_IncludesFrameworkRoutePatternIdsNeededForBridge()
    {
        using (var c = OpenWrite())
        {
            CreateSchemaAndGate(c);
            Exec(c, """
                INSERT INTO structural_facts
                    (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                     containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                     confidence, metadata_json)
                VALUES
                  ('sf-aspnet-route', 'f-api', 'Api/Routes.cs', 'csharp', 'aspnet.minimal_api.route.v1',
                   'route_call', 'invocation_expression', 's-api', 10, 0, 10, 30, 100, 130, 1.0,
                   '{"route_template":"/calendar","effective_route_template":"/api/calendar","verb":"GET"}'),
                  ('sf-aspnet-group', 'f-api', 'Api/Routes.cs', 'csharp', 'aspnet.minimal_api.route_group.v1',
                   'route_group', 'invocation_expression', 's-api', 9, 0, 9, 30, 90, 99, 1.0,
                   '{"route_prefix":"/api","group_variable":"api"}'),
                  ('sf-htmx', 'f-view', 'Components/Form.razor', 'razor', 'htmx.attribute.v1',
                   'attribute', 'element', 's-form', 20, 0, 20, 30, 200, 230, 1.0,
                   '{"target_path":"/api/calendar","verb":"POST"}'),
                  ('sf-vue-ref', 'f-vue', 'web/App.vue', 'vue', 'vue.route_reference.v1',
                   'route_reference', 'template_attribute', 's-vue', 30, 0, 30, 30, 300, 330, 1.0,
                   '{"target_path":"/calendar"}'),
                  ('sf-vue-def', 'f-vue', 'web/router.ts', 'vue', 'vue.route_definition.v1',
                   'route_definition', 'object', 's-router', 31, 0, 31, 30, 331, 360, 1.0,
                   '{"target_path":"/calendar","component_path":"./Calendar.vue"}'),
                  ('sf-react-ref', 'f-react', 'web/Nav.tsx', 'tsx', 'react.route_reference.v1',
                   'route_reference', 'jsx_attribute', 's-react', 40, 0, 40, 30, 400, 430, 1.0,
                   '{"target_path":"/calendar","library":"react_router"}'),
                  ('sf-react-def', 'f-react', 'web/routes.tsx', 'tsx', 'react.route_definition.v1',
                   'route_definition', 'jsx_element', 's-routes', 41, 0, 41, 30, 431, 460, 1.0,
                   '{"route_path":"/calendar","library":"react_router"}'),
                  ('sf-next-ref', 'f-next', 'web/Nav.tsx', 'tsx', 'nextjs.route_reference.v1',
                   'route_reference', 'jsx_attribute', 's-next-link', 50, 0, 50, 30, 500, 530, 1.0,
                   '{"target_path":"/calendar"}'),
                  ('sf-next-file', 'f-next', 'web/app/calendar/page.tsx', 'tsx', 'nextjs.file_route.v1',
                   'file_route', 'file', 's-next-page', 1, 0, 100, 0, 531, 800, 1.0,
                   '{"route_path":"/calendar","normalized_route_template":"/calendar"}'),
                  ('sf-nuxt-ref', 'f-nuxt', 'web/zz/NuxtNav.vue', 'vue', 'nuxt.route_reference.v1',
                   'route_reference', 'template_attribute', 's-nuxt-link', 70, 0, 70, 30, 1000, 1030, 1.0,
                   '{"target_path":"/calendar"}'),
                  ('sf-nuxt-file', 'f-nuxt', 'web/zz/pages/calendar.vue', 'vue', 'nuxt.file_route.v1',
                   'file_route', 'file', 's-nuxt-page', 1, 0, 100, 0, 1031, 1200, 1.0,
                   '{"route_path":"/calendar","normalized_route_template":"/calendar"}'),
                  ('sf-ignored', 'f-css', 'web/site.css', 'css', 'css.selector_rule.v1',
                   'selector', 'rule_set', NULL, 60, 0, 60, 10, 900, 910, 1.0,
                   '{"selector":".calendar"}');
                """);
        }

        var data = SqliteBridgeReader.Read(_dbPath);

        Assert.Equal(
            new[]
            {
                "aspnet.minimal_api.route.v1",
                "htmx.attribute.v1",
                "vue.route_reference.v1",
                "react.route_reference.v1",
                "nextjs.route_reference.v1",
                "nextjs.file_route.v1",
                "vue.route_definition.v1",
                "react.route_definition.v1",
                "nuxt.route_reference.v1",
                "nuxt.file_route.v1",
            },
            data.StructuralFacts.Select(f => f.PatternId).ToArray());
        Assert.DoesNotContain(data.StructuralFacts, f => f.PatternId == "aspnet.minimal_api.route_group.v1");
        Assert.DoesNotContain(data.StructuralFacts, f => f.PatternId == "css.selector_rule.v1");
        Assert.Equal("/api/calendar", data.StructuralFacts.Single(f => f.FactId == "sf-aspnet-route").Metadata["effective_route_template"]);
    }

    [Fact]
    public void Read_StructuralFacts_IncludesRazorAndBlazorBridgePatternIds()
    {
        using (var c = OpenWrite())
        {
            CreateSchemaAndGate(c);
            Exec(c, """
                INSERT INTO structural_facts
                    (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                     containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                     confidence, metadata_json)
                VALUES
                  ('sf-razor-route-ref', 'f-razor', 'Components/Nav.razor', 'razor', 'razor.route_reference.v1',
                   'route_reference', 'attribute', 's-razor-nav', 10, 0, 10, 30, 100, 130, 1.0,
                   '{"target_path":"/calendar","source_kind":"navlink"}'),
                  ('sf-razor-page', 'f-razor', 'Components/Page.razor', 'razor', 'razor.page_directive.v1',
                   'page_directive', 'directive', 's-razor-page', 1, 0, 1, 30, 131, 160, 1.0,
                   '{"route_template":"/calendar"}'),
                  ('sf-blazor-component', 'f-razor', 'Components/Widget.razor', 'razor', 'blazor.component_reference.v1',
                   'component_reference', 'element', 's-razor-widget', 20, 0, 20, 30, 161, 190, 1.0,
                   '{"tag":"CalendarWidget","containing_component":"Widget","namespace_context":["Example.Components"],"generic_arguments":[]}'),
                  ('sf-ignored', 'f-css', 'web/site.css', 'css', 'css.selector_rule.v1',
                   'selector', 'rule_set', NULL, 60, 0, 60, 10, 200, 210, 1.0,
                   '{"selector":".calendar"}');
                """);
        }

        var data = SqliteBridgeReader.Read(_dbPath);

        Assert.Equal(
            new[]
            {
                BridgeStructuralPatterns.RazorRouteReference,
                BridgeStructuralPatterns.RazorPageDirective,
                BridgeStructuralPatterns.BlazorComponentReference,
            },
            data.StructuralFacts.Select(f => f.PatternId).ToArray());
        Assert.DoesNotContain(data.StructuralFacts, f => f.PatternId == "css.selector_rule.v1");
    }

    // ---- gate + error paths ----------------------------------------------------------------------------------

    [Fact]
    public void Read_IncompatibleSchema_Throws()
    {
        using (var c = OpenWrite())
        {
            // Build the schema but seed a WRONG (newer-than-pinned) sqlite_schema_version so the gate rejects before any bridge read.
            using var command = c.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE type_argument_usages (usage_id TEXT PRIMARY KEY, identifier_id TEXT, file_id TEXT, path TEXT, language TEXT, metadata_json TEXT);
                CREATE TABLE type_arguments (type_argument_id TEXT PRIMARY KEY, usage_id TEXT, parent_type_argument_id TEXT, ordinal INTEGER, type_name TEXT);
                CREATE TABLE literals (literal_id TEXT PRIMARY KEY, literal_text TEXT, kind TEXT, carrier TEXT, arg_position INTEGER, language TEXT, path TEXT, start_line INTEGER, end_line INTEGER, start_byte INTEGER, end_byte INTEGER, containing_symbol_id TEXT, confidence REAL);
                CREATE TABLE symbol_annotations (annotation_id TEXT PRIMARY KEY, symbol_id TEXT, annotation TEXT, annotation_key TEXT, raw_text TEXT, carrier TEXT);
                CREATE TABLE symbols (symbol_id TEXT PRIMARY KEY, name TEXT, signature TEXT, kind TEXT, language TEXT, path TEXT, start_line INTEGER, end_line INTEGER, parent_symbol_id TEXT, metadata_json TEXT);
                CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO artifact_metadata(key, value) VALUES ('sqlite_schema_version', '{MillerExtractContract.ExpectedSqliteSchemaVersion + 1}');
                INSERT INTO artifact_metadata(key, value) VALUES ('extract_contract_version', '{MillerExtractContract.ExpectedExtractContractVersion}');
                INSERT INTO artifact_metadata(key, value) VALUES ('hash_algorithm', '{MillerExtractContract.ExpectedHashAlgorithm}');
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<IncompatibleExtractException>(() => SqliteBridgeReader.Read(_dbPath));
    }

    [Fact]
    public void Read_MissingDbFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(Path.GetTempPath(), "miller-nope-" + Guid.NewGuid().ToString("N"), "symbols.db");

        var ex = Assert.Throws<FileNotFoundException>(() => SqliteBridgeReader.Read(missing));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Read_NullPath_Throws()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException (a subclass) for null — matching
        // RepositoryIndexLoader/SqliteSymbolReader. Assert the exact derived type, and that whitespace still throws.
        Assert.Throws<ArgumentNullException>(() => SqliteBridgeReader.Read(null!));
        Assert.Throws<ArgumentException>(() => SqliteBridgeReader.Read("   "));
    }
}
