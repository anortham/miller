using Miller.Core.Contracts;
using Miller.Core.Resolver;
using Xunit;

namespace Miller.Tests.Resolver;

/// <summary>
/// Pins design §4 Leg 3 (<see cref="EntityTableBridge"/>): C# entity ⇄ DB table edges (<see cref="BridgeKind.StoredIn"/>).
/// Every fixture is hand-built in-memory (NO julie, NO I/O), so these are fast-suite tests. The leg only builds
/// candidates and delegates ALL confidence to <see cref="BridgeScorer"/>; the tests assert the resulting band/score and
/// the load-bearing traps:
/// <list type="bullet">
/// <item>the entity comes from the <c>DbSet&lt;T&gt;</c> generic arg, NEVER the DbContext class container (findings 28-2);</item>
/// <item>the table name is the DbSet property name (EF convention), not a pluralized entity;</item>
/// <item>a Dapper FROM literal anchors only when a real <c>FROM</c> clause is present;</item>
/// <item>an ambiguous entity name is never High; an unresolved entity yields no edge.</item>
/// </list>
/// </summary>
public sealed class EntityTableBridgeTests
{
    // SymbolDetail ctor order: (Id, Name, Kind, FilePath, Signature, Namespace, IsTest, ParentClassName).
    private static SymbolDetail Entity(string id, string name, string? ns, string file = "Domain/Entities.cs") =>
        new(id, name, "class", file, $"public class {name}", ns, false, null);

    // DbSetProperty ctor order: (PropertySymbolId, TableName, EntityTypeName, FilePath, StartLine).
    private static DbSetProperty DbSet(string entity, string table,
        string file = "Data/MyraNextContext.cs", int line = 18) =>
        new("prop:" + table, table, entity, file, line);

    // LiteralRecord ctor order: (LiteralText, Kind, Carrier, ArgPosition, Language, ContainingSymbolId, Span).
    private static DapperFromCandidate Dapper(string sql, string entity,
        string file = "Data/Repo.cs", int line = 42) =>
        new(
            new LiteralRecord(sql, "sql", "QueryAsync", 0, "csharp", "m1", new SourceSpan(0, sql.Length)),
            entity,
            file,
            line);

    // ---- PRIMARY: DbSet<T> property ----------------------------------------------------------------------------

    [Fact]
    public void Resolve_DbSetProperty_EmitsEntityToTableStoredInEdge_High()
    {
        // ApplicationUser entity is uniquely resolvable; the DbSet property "ApplicationUsers" names the table.
        var resolver = new SymbolResolver([Entity("e1", "ApplicationUser", "Domain")]);
        var input = new EntityTableInput([DbSet("ApplicationUser", "ApplicationUsers")], []);

        var edges = EntityTableBridge.Resolve(input, resolver);

        var edge = Assert.Single(edges);
        Assert.Equal(BridgeKind.StoredIn, edge.Kind);
        Assert.Equal("ApplicationUser", edge.SourceRef.Display);
        Assert.Equal("ApplicationUsers", edge.TargetRef.Display);

        Assert.Contains(edge.Signals, s => s is StructuralSignal { Rule: SignalRule.DbSetProperty, Present: true });

        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.True(scored.Score >= 0.90);
        Assert.False(scored.HasAmbiguousName);
        // ApplicationUser/ApplicationUsers stems fold, so this High edge is multi-signal (DbSet breadcrumb + Affix
        // NameSignal). The single-breadcrumb High path is pinned by Resolve_DbSetProperty_UnrelatedTableName_NoNameSignal.
        Assert.True(scored.IsMultiSignal);
    }

    [Fact]
    public void Resolve_DbSetProperty_EntityIsGenericArg_NotDbContextContainer()
    {
        // The TRAP (findings 28-2): the DbSet use-site container is the DbContext class (MyraNextContext). The edge
        // must point at the entity (ApplicationUser), and the resolved source symbol must be the entity symbol — never
        // the context. We seed both names so a leg that picked the container would resolve to "ctx".
        var resolver = new SymbolResolver(
        [
            Entity("e1", "ApplicationUser", "Domain"),
            new SymbolDetail("ctx", "MyraNextContext", "class", "Data/MyraNextContext.cs",
                "public class MyraNextContext : DbContext", "Data", false, null),
        ]);
        var input = new EntityTableInput([DbSet("ApplicationUser", "ApplicationUsers")], []);

        var edge = Assert.Single(EntityTableBridge.Resolve(input, resolver));

        // The source is the ENTITY, resolved to the entity symbol — not the DbContext class.
        Assert.Equal("ApplicationUser", edge.SourceRef.Display);
        Assert.Equal("e1", edge.SourceRef.SymbolId);
        Assert.NotEqual("MyraNextContext", edge.SourceRef.Display);
        Assert.NotEqual("ctx", edge.SourceRef.SymbolId);

        // The table side is a non-symbol node (named by EF convention), trivially resolved with no symbol id.
        Assert.Null(edge.TargetRef.SymbolId);
        Assert.Equal(ResolutionStatus.Resolved, edge.TargetRef.Resolution.Status);
    }

    [Fact]
    public void Resolve_DbSetProperty_TableIsPropertyName_NotPluralizedEntity()
    {
        // Plan Task 5: Preferences->Preferences (entity "Preferences" already plural; table = property name verbatim).
        // A pluralizer-on-the-entity would mis-map; the leg must take the property name as the table.
        var resolver = new SymbolResolver([Entity("e1", "Preferences", "Domain")]);
        var input = new EntityTableInput([DbSet("Preferences", "Preferences")], []);

        var edge = Assert.Single(EntityTableBridge.Resolve(input, resolver));

        Assert.Equal("Preferences", edge.TargetRef.Display);
    }

    [Fact]
    public void Resolve_DbSetProperty_EntityTableNameStem_CorroboratesWithNameSignal()
    {
        // AppSetting entity vs AppSettings table: the stems fold to the same canonical stem, so a NameSignal
        // corroborator fires alongside the DbSet breadcrumb — a multi-signal High edge.
        var resolver = new SymbolResolver([Entity("e1", "AppSetting", "Domain")]);
        var input = new EntityTableInput([DbSet("AppSetting", "AppSettings")], []);

        var edge = Assert.Single(EntityTableBridge.Resolve(input, resolver));

        Assert.Contains(edge.Signals, s => s is StructuralSignal { Rule: SignalRule.DbSetProperty, Present: true });
        Assert.Contains(edge.Signals, s => s is NameSignal { Tier: NameTier.Affix });

        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.True(scored.IsMultiSignal);
    }

    [Fact]
    public void Resolve_DbSetProperty_EntityNameEqualsTable_NameSignalIsExactTier()
    {
        // When the entity leaf name and the table name are identical (case-folded), the corroborating NameSignal is the
        // Exact tier — distinct from the singular/plural Affix tier above.
        var resolver = new SymbolResolver([Entity("e1", "AuditLog", "Domain")]);
        var input = new EntityTableInput([DbSet("AuditLog", "AuditLog")], []);

        var edge = Assert.Single(EntityTableBridge.Resolve(input, resolver));

        Assert.Contains(edge.Signals, s => s is NameSignal { Tier: NameTier.Exact });
    }

    [Fact]
    public void Resolve_DbSetProperty_UnrelatedTableName_NoNameSignal()
    {
        // A DbSet whose property name does not fold to the entity stem (a deliberate mapping override) gets the DbSet
        // breadcrumb but NO name corroborator — the leg never fabricates a name match.
        var resolver = new SymbolResolver([Entity("e1", "Preference", "Domain")]);
        var input = new EntityTableInput([DbSet("Preference", "AppSettings")], []);

        var edge = Assert.Single(EntityTableBridge.Resolve(input, resolver));

        Assert.DoesNotContain(edge.Signals, s => s is NameSignal);
        // Still a valid High edge on the structural breadcrumb alone.
        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.False(scored.IsMultiSignal);
    }

    [Fact]
    public void Resolve_DbSetProperty_AmbiguousEntity_NeverHigh()
    {
        // Two same-named entities, no tie-break hint => ambiguous => the edge is capped at Medium and flagged, even
        // though the DbSet structural breadcrumb is present.
        var resolver = new SymbolResolver(
        [
            Entity("e1", "Account", "Core.Reporting.Data", "ServiceA/Account.cs"),
            Entity("e2", "Account", "ResponseObjects", "ServiceB/Account.cs"),
        ]);
        var input = new EntityTableInput([DbSet("Account", "Accounts")], []);

        var edge = Assert.Single(EntityTableBridge.Resolve(input, resolver));
        Assert.Equal(ResolutionStatus.Ambiguous, edge.SourceRef.Resolution.Status);
        Assert.Contains(edge.Signals,
            s => s is NameResolutionSignal { Endpoint: EndpointSide.Source, Status: ResolutionStatus.Ambiguous });

        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.Medium, scored!.Band);
        Assert.True(scored.HasAmbiguousName);
    }

    [Fact]
    public void Resolve_DbSetProperty_UnresolvedEntity_NoEdge()
    {
        // The DbSet<T> arg names an entity that does not exist as a symbol => unresolved => the leg still emits a
        // candidate carrying the Unresolved status, and the scorer drops it (no symbol to point at).
        var resolver = new SymbolResolver([Entity("e1", "SomeOtherType", "Domain")]);
        var input = new EntityTableInput([DbSet("GhostEntity", "GhostEntities")], []);

        var edge = Assert.Single(EntityTableBridge.Resolve(input, resolver));
        Assert.Equal(ResolutionStatus.Unresolved, edge.SourceRef.Resolution.Status);

        var scored = BridgeScorer.Score(edge);
        Assert.Null(scored);
    }

    [Fact]
    public void Resolve_MultipleDbSets_OneEdgePerProperty()
    {
        // The 11/11 MyraNext shape in miniature: each DbSet property yields its own entity↔table edge.
        var resolver = new SymbolResolver(
        [
            Entity("e1", "ApplicationUser", "Domain"),
            Entity("e2", "Preferences", "Domain"),
            Entity("e3", "AppSetting", "Domain"),
        ]);
        var input = new EntityTableInput(
        [
            DbSet("ApplicationUser", "ApplicationUsers"),
            DbSet("Preferences", "Preferences"),
            DbSet("AppSetting", "AppSettings"),
        ], []);

        var edges = EntityTableBridge.Resolve(input, resolver);

        Assert.Equal(3, edges.Count);
        Assert.All(edges, e => Assert.Equal(BridgeKind.StoredIn, e.Kind));
        Assert.Contains(edges, e => e.TargetRef.Display == "ApplicationUsers");
        Assert.Contains(edges, e => e.TargetRef.Display == "Preferences");
        Assert.Contains(edges, e => e.TargetRef.Display == "AppSettings");
    }

    // ---- SECONDARY: Dapper FROM literal ------------------------------------------------------------------------

    [Fact]
    public void Resolve_DapperLiteral_WithFrom_EmitsDapperFromEdge_High()
    {
        // A real inline SQL literal "SELECT TOP 1 Id FROM dbo.AppSettings" -> table token after FROM (schema-stripped).
        var resolver = new SymbolResolver([Entity("e1", "AppSetting", "Domain")]);
        var input = new EntityTableInput([], [Dapper("SELECT TOP 1 Id FROM dbo.AppSettings", "AppSetting")]);

        var edge = Assert.Single(EntityTableBridge.Resolve(input, resolver));
        Assert.Equal(BridgeKind.StoredIn, edge.Kind);
        Assert.Equal("AppSetting", edge.SourceRef.Display);
        // The table token comes from the FROM clause, with the schema qualifier stripped.
        Assert.Equal("AppSettings", edge.TargetRef.Display);
        Assert.Contains(edge.Signals, s => s is StructuralSignal { Rule: SignalRule.DapperFrom, Present: true });

        var scored = BridgeScorer.Score(edge);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
    }

    [Fact]
    public void Resolve_DapperLiteral_NoFrom_NoDapperFromEdge()
    {
        // A stored-proc artifact literal (a splitOn column list, no FROM) must NOT emit a DapperFrom edge — never guess
        // a table when there is no FROM clause (design §8 trap; MyraNext's 13/15 sql literals are exactly this shape).
        var resolver = new SymbolResolver([Entity("e1", "ProjectRole", "Domain")]);
        var input = new EntityTableInput([], [Dapper("AccountNumber,ProjectRole", "ProjectRole")]);

        var edges = EntityTableBridge.Resolve(input, resolver);

        Assert.Empty(edges);
    }

    [Fact]
    public void Resolve_DapperLiteral_FromWithoutSchema_StillParsesTable()
    {
        // FROM with no schema qualifier: "SELECT * FROM Orders WHERE Id = {}" -> table "Orders".
        var resolver = new SymbolResolver([Entity("e1", "Order", "Domain")]);
        var input = new EntityTableInput([], [Dapper("SELECT * FROM Orders WHERE Id = {}", "Order")]);

        var edge = Assert.Single(EntityTableBridge.Resolve(input, resolver));
        Assert.Equal("Orders", edge.TargetRef.Display);
        Assert.Contains(edge.Signals, s => s is StructuralSignal { Rule: SignalRule.DapperFrom, Present: true });
    }

    [Fact]
    public void Resolve_DapperLiteral_SubstringFromInColumn_DoesNotFalseTrigger()
    {
        // "FROM" must match only as a whole token. A column named "FromDate" (no real FROM clause) must NOT anchor.
        var resolver = new SymbolResolver([Entity("e1", "Audit", "Domain")]);
        var input = new EntityTableInput([], [Dapper("SELECT FromDate, ToDate", "Audit")]);

        var edges = EntityTableBridge.Resolve(input, resolver);

        Assert.Empty(edges);
    }

    [Fact]
    public void Resolve_DapperLiteral_UnresolvedEntity_NoEdge()
    {
        // A real FROM but the entity does not resolve => the scorer drops it (no symbol to point at).
        var resolver = new SymbolResolver([Entity("e1", "SomethingElse", "Domain")]);
        var input = new EntityTableInput([], [Dapper("SELECT * FROM Orders", "GhostEntity")]);

        var edge = Assert.Single(EntityTableBridge.Resolve(input, resolver));
        Assert.Equal(ResolutionStatus.Unresolved, edge.SourceRef.Resolution.Status);

        Assert.Null(BridgeScorer.Score(edge));
    }

    // ---- guards ------------------------------------------------------------------------------------------------

    [Fact]
    public void Resolve_NullInput_Throws()
    {
        var resolver = new SymbolResolver([]);
        Assert.Throws<ArgumentNullException>(() => EntityTableBridge.Resolve(null!, resolver));
    }

    [Fact]
    public void Resolve_NullResolver_Throws()
    {
        var input = new EntityTableInput([], []);
        Assert.Throws<ArgumentNullException>(() => EntityTableBridge.Resolve(input, null!));
    }
}
