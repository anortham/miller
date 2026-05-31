using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins <see cref="SqliteBridgeReader"/> (plan Task 9) against a hand-written 28/2 bridge DB. These are
/// read-CONTRACT tests: they assert the exact <see cref="Miller.Core.Contracts.TypeArgument"/> /
/// <see cref="Miller.Core.Contracts.LiteralRecord"/> / <see cref="Miller.Core.Contracts.SymbolAnnotation"/> /
/// <see cref="Miller.Core.Contracts.DbSetProperty"/> mapping (ordering, NULL discipline, the DbSet&lt;T&gt;
/// signature parse, and the literal→file:line seam the <see cref="BridgeGraphBuilder"/> requires). The reader
/// performs NO leg transformation — that is Task 8's job — so these assert raw rows only. Fast suite: a temp
/// SQLite DB, no julie-server.
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

    // Create the 28/2 bridge-relevant schema (the four tables the reader reads + the gate tables) and seed the gate.
    private void CreateSchemaAndGate(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE symbols (
                id TEXT PRIMARY KEY, name TEXT, signature TEXT, kind TEXT, language TEXT,
                file_path TEXT, start_line INTEGER, end_line INTEGER, parent_id TEXT, metadata TEXT
            );
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
                id TEXT PRIMARY KEY, symbol_id TEXT, ordinal INTEGER, annotation TEXT, annotation_key TEXT,
                raw_text TEXT, carrier TEXT
            );
            CREATE TABLE schema_version (version INTEGER);
            CREATE TABLE external_extract_metadata (key TEXT, value TEXT);
            """;
        command.ExecuteNonQuery();

        using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO schema_version(version) VALUES (28);
            INSERT INTO external_extract_metadata(key, value) VALUES ('extract_contract_version', '2');
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
            // Two use-sites. Insert OUT of order to prove the reader sorts by (identifier_id, ordinal).
            Exec(c, """
                INSERT INTO type_arguments(id, identifier_id, parent_arg_id, ordinal, type_name, target_symbol_id, file_path, language, last_indexed)
                VALUES
                  ('t4', 'idB', NULL, 1, 'ApplicationUser', NULL, 'src/Profile.cs', 'csharp', NULL),
                  ('t2', 'idA', NULL, 1, 'UserDto',         NULL, 'src/Map.cs',     'csharp', NULL),
                  ('t1', 'idA', NULL, 0, 'ApplicationUser', NULL, 'src/Map.cs',     'csharp', NULL),
                  ('t3', 'idB', NULL, 0, 'List',            NULL, 'src/Profile.cs', 'csharp', NULL),
                  ('t5', 'idB', 't3', 0, 'Inner',           NULL, 'src/Profile.cs', 'csharp', NULL);
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
            // A url literal (TS client call) and a sql literal — both must come through; ordered by file,start_byte.
            Exec(c, """
                INSERT INTO literals(id, literal_text, kind, carrier, arg_position, language, file_path, start_line, end_line, start_byte, end_byte, containing_symbol_id, confidence)
                VALUES
                  ('l2', '/api/users/{}', 'url', 'axios.get',  0, 'typescript', 'web/api.ts', 42, 42, 120, 135, 'sym-ts',  0.9),
                  ('l1', 'SELECT 1 FROM dbo.AppSettings', 'sql', 'QueryAsync', 1, 'csharp', 'data/Repo.cs', 7, 7, 30, 60, 'sym-cs', 0.8);
                """);
        }

        var data = SqliteBridgeReader.Read(_dbPath);

        // Ordered by file_path then start_byte: data/Repo.cs(sql) before web/api.ts(url).
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
    public void Read_Annotations_OrderedBySymbolThenOrdinal_ArgsLiveInRawText()
    {
        using (var c = OpenWrite())
        {
            CreateSchemaAndGate(c);
            // A class [Route] and a method [HttpGet] — inserted out of order to prove (symbol_id, ordinal) sort.
            Exec(c, """
                INSERT INTO symbol_annotations(id, symbol_id, ordinal, annotation, annotation_key, raw_text, carrier)
                VALUES
                  ('a2', 'sym-method', 0, 'HttpGet', 'httpget', 'HttpGet("{id}")', 'attribute'),
                  ('a1', 'sym-class',  0, 'Route',   'route',   'Route("api/[controller]")', 'attribute');
                """);
        }

        var data = SqliteBridgeReader.Read(_dbPath);

        // Ordered by symbol_id then ordinal: sym-class before sym-method (ordinal both 0).
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
                INSERT INTO symbols(id, name, signature, kind, language, file_path, start_line, end_line, parent_id, metadata)
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
        Assert.Empty(data.LiteralSites);
    }

    // ---- gate + error paths ----------------------------------------------------------------------------------

    [Fact]
    public void Read_IncompatibleSchema_Throws()
    {
        using (var c = OpenWrite())
        {
            // Build the schema but seed a WRONG schema_version so the gate rejects it before any bridge read.
            using var command = c.CreateCommand();
            command.CommandText = """
                CREATE TABLE type_arguments (id TEXT PRIMARY KEY, identifier_id TEXT, parent_arg_id TEXT, ordinal INTEGER, type_name TEXT, target_symbol_id TEXT, file_path TEXT, language TEXT, last_indexed TEXT);
                CREATE TABLE literals (id TEXT PRIMARY KEY, literal_text TEXT, kind TEXT, carrier TEXT, arg_position INTEGER, language TEXT, file_path TEXT, start_line INTEGER, end_line INTEGER, start_byte INTEGER, end_byte INTEGER, containing_symbol_id TEXT, confidence REAL);
                CREATE TABLE symbol_annotations (id TEXT PRIMARY KEY, symbol_id TEXT, ordinal INTEGER, annotation TEXT, annotation_key TEXT, raw_text TEXT, carrier TEXT);
                CREATE TABLE symbols (id TEXT PRIMARY KEY, name TEXT, signature TEXT, kind TEXT, language TEXT, file_path TEXT, start_line INTEGER, end_line INTEGER, parent_id TEXT, metadata TEXT);
                CREATE TABLE schema_version (version INTEGER);
                CREATE TABLE external_extract_metadata (key TEXT, value TEXT);
                INSERT INTO schema_version(version) VALUES (27);
                INSERT INTO external_extract_metadata(key, value) VALUES ('extract_contract_version', '2');
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
