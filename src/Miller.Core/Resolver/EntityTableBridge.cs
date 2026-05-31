using Miller.Core.Contracts;

namespace Miller.Core.Resolver;

/// <summary>
/// A Dapper inline-SQL literal paired with the entity type it reads (design §4 Leg 3 secondary; findings 28-2). julie's
/// <c>literals</c> carry no <c>identifier_id</c> and cannot join to <c>type_arguments</c> by a shared key, so the entity
/// (<c>T</c>) is paired to the SQL literal by span-proximity within the same <c>containing_symbol_id</c> — that pairing
/// is the graph builder's job (plan Task 8, out of this leg's scope). This record is the already-paired input the pure
/// leg consumes: the leg parses the <c>FROM</c> table from <see cref="Literal"/> and links it to
/// <see cref="EntityTypeName"/>. The leg NEVER fabricates a FROM — a literal with no <c>FROM</c> clause yields no edge.
///
/// <para>The evidence <see cref="FilePath"/>/<see cref="Line"/> are carried HERE rather than read off
/// <see cref="LiteralRecord"/> because Miller's <see cref="LiteralRecord"/> surfaces only the byte <c>span</c> + the
/// <c>containing_symbol_id</c> — it does not re-expose the <c>literals</c> row's own <c>file_path</c>/line columns —
/// so the builder that pairs the literal to its entity resolves the use-site file:line into this evidence.</para>
/// </summary>
/// <param name="Literal">The <c>kind=sql</c> literal whose text may contain a <c>FROM &lt;table&gt;</c> clause.</param>
/// <param name="EntityTypeName">The entity type (<c>T</c>) paired to this literal by span-proximity; resolved by name.</param>
/// <param name="FilePath">The literal's use-site file (workspace-relative), for the edge evidence.</param>
/// <param name="Line">The 1-based use-site line, for the edge evidence (file:line).</param>
public sealed record DapperFromCandidate(LiteralRecord Literal, string EntityTypeName, string FilePath, int Line);

/// <summary>
/// The in-memory contract collections <see cref="EntityTableBridge"/> consumes (design §4 Leg 3; plan Task 5). Pure
/// value input — no DB, no I/O. The DB loader (plan Task 9) builds these from julie rows; the leg never reads SQLite.
/// </summary>
/// <param name="DbSetProperties">
/// The DbContext <c>DbSet&lt;T&gt;</c> properties (Leg 3 PRIMARY breadcrumb): table = property name, entity = generic arg.
/// </param>
/// <param name="DapperFromCandidates">
/// Dapper inline-SQL literals already paired to their entity (Leg 3 SECONDARY, opportunistic): a real <c>FROM</c> clause
/// in the literal anchors a table edge; no <c>FROM</c> ⇒ no edge.
/// </param>
public sealed record EntityTableInput(
    IReadOnlyList<DbSetProperty> DbSetProperties,
    IReadOnlyList<DapperFromCandidate> DapperFromCandidates);

/// <summary>
/// Leg 3 of the cross-language resolver (design §4): builds candidate <see cref="BridgeKind.StoredIn"/> edges linking a
/// C# entity to its database table. PURE Miller.Core — it operates over the in-memory <see cref="EntityTableInput"/>,
/// resolves the entity type name via <see cref="SymbolResolver"/>, and emits typed <see cref="CandidateEdge"/>s. It
/// NEVER scores, bands, or re-implements confidence logic; every signal it emits is decidable by
/// <see cref="BridgeScorer"/> from the candidate payload alone (the trust contract, design §5).
///
/// <para><b>PRIMARY — DbSet&lt;T&gt; property (High-eligible).</b> Table = the DbSet property name (EF convention,
/// <see cref="DbSetProperty.TableName"/>); entity = the <c>DbSet&lt;T&gt;</c> generic arg
/// (<see cref="DbSetProperty.EntityTypeName"/>). The verified trap (design §8 / findings 28-2): the DbSet use-site
/// container points at the DbContext CLASS, never the entity — so the entity is ALWAYS taken from the property's generic
/// arg, never from a use-site container. Emits <see cref="SignalRule.DbSetProperty"/> plus, when the entity stem and
/// table stem fold together, a corroborating <see cref="NameSignal"/>.</para>
///
/// <para><b>SECONDARY — Dapper FROM literal (High-eligible ONLY with a real FROM).</b> Parses the table token after
/// <c>FROM</c> in a <c>kind=sql</c> literal and emits <see cref="SignalRule.DapperFrom"/>. A literal with no <c>FROM</c>
/// clause yields NO edge — the leg never guesses a table (design §8; most stored-proc literals are splitOn column lists,
/// not <c>SELECT…FROM</c>).</para>
///
/// <para>The table side is a node named by EF convention / SQL text, not a code symbol, so its <see cref="EdgeRef"/> is
/// trivially <see cref="ResolutionStatus.Resolved"/> with no symbol id, and the leg never fabricates a field-set for the
/// table (a table has no symbol-derived field shape).</para>
/// </summary>
public static class EntityTableBridge
{
    /// <summary>
    /// Build the entity↔table candidate edges from <paramref name="input"/>, resolving each entity type name through
    /// <paramref name="resolver"/>. Returns one candidate per DbSet property and per Dapper literal that has a real
    /// <c>FROM</c> clause; an unresolved/ambiguous entity is reflected in the candidate's <see cref="EdgeRef.Resolution"/>
    /// + a <see cref="NameResolutionSignal"/> so the scorer (not the leg) applies the §5 drop/cap rules. The leg does NOT
    /// score; it never returns a band.
    /// </summary>
    /// <param name="input">The in-memory DbSet + Dapper breadcrumbs.</param>
    /// <param name="resolver">The name resolver over the workspace's symbols.</param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="resolver"/> is null.</exception>
    public static IReadOnlyList<CandidateEdge> Resolve(EntityTableInput input, SymbolResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(resolver);

        var edges = new List<CandidateEdge>();

        foreach (var dbSet in input.DbSetProperties)
            edges.Add(BuildDbSetEdge(dbSet, resolver));

        foreach (var dapper in input.DapperFromCandidates)
        {
            var edge = TryBuildDapperEdge(dapper, resolver);
            if (edge is not null)
                edges.Add(edge);
        }

        return edges;
    }

    /// <summary>
    /// Build the PRIMARY edge for one <c>DbSet&lt;T&gt;</c> property: entity (resolved by name) —stored_in→ table
    /// (= property name). Emits the DbSetProperty breadcrumb, the per-side NameResolution metadata, and a corroborating
    /// NameSignal when the entity stem folds to the table stem.
    /// </summary>
    private static CandidateEdge BuildDbSetEdge(DbSetProperty dbSet, SymbolResolver resolver)
    {
        var evidence = new Evidence(dbSet.FilePath, dbSet.StartLine);

        // Entity = the DbSet<T> generic arg (NEVER the use-site container). Resolve it by name.
        var entityResolution = resolver.Resolve(dbSet.EntityTypeName, preferFile: dbSet.FilePath);
        var entityRef = new EdgeRef(
            Display: LeafName(dbSet.EntityTypeName),
            SymbolId: entityResolution.SymbolId,
            FilePath: dbSet.FilePath,
            Resolution: entityResolution);

        // Table = the DbSet property name (EF convention). A table is not a code symbol — trivially Resolved, no id.
        var tableRef = TableRef(dbSet.TableName, dbSet.FilePath);

        var signals = new List<Signal>
        {
            new StructuralSignal(SignalRule.DbSetProperty, Present: true, evidence),
            new NameResolutionSignal(EndpointSide.Source, entityResolution.Status, entityResolution.MatchCount, evidence),
        };

        AddNameCorroboratorIfStemsMatch(signals, dbSet.EntityTypeName, dbSet.TableName, evidence);

        return new CandidateEdge(
            BridgeKind.StoredIn,
            entityRef,
            tableRef,
            [evidence],
            signals);
    }

    /// <summary>
    /// Build the SECONDARY edge for a Dapper literal, or return null when the literal has no real <c>FROM</c> clause
    /// (never guess a table). Emits the DapperFrom breadcrumb, the per-side NameResolution metadata, and a corroborating
    /// NameSignal when the entity stem folds to the parsed table stem.
    /// </summary>
    private static CandidateEdge? TryBuildDapperEdge(DapperFromCandidate dapper, SymbolResolver resolver)
    {
        var table = ParseFromTable(dapper.Literal.LiteralText);
        if (table is null)
            return null;

        var evidence = new Evidence(dapper.FilePath, dapper.Line);

        var entityResolution = resolver.Resolve(dapper.EntityTypeName, preferFile: dapper.FilePath);
        var entityRef = new EdgeRef(
            Display: LeafName(dapper.EntityTypeName),
            SymbolId: entityResolution.SymbolId,
            FilePath: dapper.FilePath,
            Resolution: entityResolution);

        var tableRef = TableRef(table, dapper.FilePath);

        var signals = new List<Signal>
        {
            new StructuralSignal(SignalRule.DapperFrom, Present: true, evidence),
            new NameResolutionSignal(EndpointSide.Source, entityResolution.Status, entityResolution.MatchCount, evidence),
        };

        AddNameCorroboratorIfStemsMatch(signals, dapper.EntityTypeName, table, evidence);

        return new CandidateEdge(
            BridgeKind.StoredIn,
            entityRef,
            tableRef,
            [evidence],
            signals);
    }

    /// <summary>
    /// A non-symbol table endpoint: named by EF convention or SQL text, it has no code symbol, so the ref is trivially
    /// <see cref="ResolutionStatus.Resolved"/> with no symbol id (so the scorer's unresolved/ambiguous gates never fire
    /// on the table side — the design §5 gates are about the entity name).
    /// </summary>
    private static EdgeRef TableRef(string tableName, string filePath) =>
        new(tableName, SymbolId: null, FilePath: filePath, new NameResolution(ResolutionStatus.Resolved, null, 1));

    /// <summary>
    /// Add a corroborating <see cref="NameSignal"/> when the entity name and table name fold to the same canonical stem
    /// (e.g. <c>AppSetting</c> ⇄ <c>AppSettings</c>). Exact when the raw leaf names are already identical (case-folded);
    /// <see cref="NameTier.Affix"/> when they only matched after singular/plural + affix folding. This only RAISES an
    /// already-anchored edge (the structural breadcrumb is the anchor); it is never the sole signal.
    /// </summary>
    private static void AddNameCorroboratorIfStemsMatch(List<Signal> signals, string entityName, string tableName, Evidence evidence)
    {
        var entityLeaf = LeafName(entityName);
        var entityStem = NameNormalizer.Stem(entityLeaf);
        var tableStem = NameNormalizer.Stem(tableName);
        if (entityStem.Length == 0 || tableStem.Length == 0 || !string.Equals(entityStem, tableStem, StringComparison.Ordinal))
            return;

        var tier = string.Equals(entityLeaf, tableName, StringComparison.OrdinalIgnoreCase)
            ? NameTier.Exact
            : NameTier.Affix;
        signals.Add(new NameSignal(tier, evidence));
    }

    /// <summary>
    /// Parse the table token immediately after a top-level <c>FROM</c> keyword, with any schema qualifier stripped
    /// (<c>dbo.AppSettings</c> → <c>AppSettings</c>), or null when there is no <c>FROM</c> clause. Matches <c>FROM</c>
    /// only as a whole word so a column/identifier containing the substring never triggers it. JOINs are not parsed —
    /// the design takes the FROM table only, never JOINs/multi-map.
    /// </summary>
    private static string? ParseFromTable(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return null;

        // Tokenize on whitespace; find a standalone "from" (case-insensitive) and take the next token as the table.
        var tokens = sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            if (!string.Equals(tokens[i], "FROM", StringComparison.OrdinalIgnoreCase))
                continue;

            var table = StripSchemaAndDelimiters(tokens[i + 1]);
            return table.Length == 0 ? null : table;
        }
        return null;
    }

    /// <summary>
    /// Reduce a raw FROM token to a bare table name: drop a trailing statement terminator / list separator, take the
    /// segment after the last <c>.</c> (drop the schema/database qualifier), and strip surrounding SQL identifier
    /// delimiters (<c>[]</c>, <c>"</c>, backticks).
    /// </summary>
    private static string StripSchemaAndDelimiters(string token)
    {
        var t = token.Trim().TrimEnd(';', ',', ')');

        int dot = t.LastIndexOf('.');
        if (dot >= 0 && dot < t.Length - 1)
            t = t[(dot + 1)..];

        return t.Trim('[', ']', '"', '`');
    }

    /// <summary>The leaf (simple) name of a possibly-qualified type name (<c>Core.Data.Account</c> → <c>Account</c>).</summary>
    private static string LeafName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return typeName;
        int dot = typeName.LastIndexOf('.');
        return (dot >= 0 && dot < typeName.Length - 1) ? typeName[(dot + 1)..] : typeName;
    }
}
