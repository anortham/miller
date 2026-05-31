using Microsoft.Data.Sqlite;
using Miller.Core.Contracts;
using Miller.Core.Graph;

namespace Miller.Indexing;

/// <summary>
/// The cross-language bridge read layer (plan Task 9; design SQLite-reader section). Opens a julie extract DB
/// under the SAME D4 read-only discipline as <see cref="SqliteSymbolReader"/> (<see cref="SqliteReadOnlyAccess.Open"/>
/// + <see cref="JulieSchemaGate"/>) and projects the four bridge-relevant tables into the RAW Miller.Core contract
/// rows the <see cref="BridgeGraphBuilder"/> consumes. It performs NO leg transformation — that is Task 8's job; this
/// reader only maps julie columns to records.
///
/// <para>The four sources (verified 28/2 column shapes — findings 28-2):
/// <list type="bullet">
/// <item><c>type_arguments</c> → <see cref="TypeArgument"/> (CreateMap grouping input), ordered by
/// <c>identifier_id</c> then <c>ordinal</c> for deterministic grouping downstream.</item>
/// <item><c>literals</c> → <see cref="LiteralRecord"/> (url client calls + sql), ordered by file then start_byte.
/// The <c>literals</c> table HAS its own <c>file_path</c>/<c>start_line</c> columns (unlike the lean
/// <see cref="LiteralRecord"/> which surfaces only span + containing-symbol id), so the reader ALSO returns the
/// literal→(file,line) lookup the builder's literal-evidence seam needs (Task 8 deviation).</item>
/// <item><c>symbol_annotations</c> → <see cref="SymbolAnnotation"/> (http-verb endpoints + class <c>[Route]</c>),
/// ordered by <c>symbol_id</c> then <c>ordinal</c>.</item>
/// <item>DbContext <c>DbSet&lt;T&gt;</c> properties → <see cref="DbSetProperty"/> (Leg 3 PRIMARY), parsed from
/// <c>symbols</c> rows with <c>kind='property'</c> whose <c>signature</c> contains <c>DbSet&lt;…&gt;</c>: the property
/// name IS the table name (EF convention), the generic arg IS the entity type.</item>
/// </list></para>
///
/// <para>Sync by design: this is part of the single startup/rebuild pass (Microsoft.Data.Sqlite's async is
/// synchronous internally), and it runs after <see cref="SqliteSymbolReader"/> on the same DB.</para>
/// </summary>
public static class SqliteBridgeReader
{
    /// <summary>
    /// Read the four bridge tables from the julie extract at <paramref name="dbPath"/> into the raw Core contract
    /// collections (plus the literal→file:line lookup for the builder's literal-evidence seam).
    /// </summary>
    /// <param name="dbPath">Path to the julie extract DB (opened <c>Mode=ReadOnly</c> by the shared D4 discipline).</param>
    /// <returns>The raw bridge rows + literal-site lookup, ready for <see cref="BridgeGraphBuilder.Build"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="dbPath"/> is null/empty/whitespace.</exception>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The DB's directory is not writable (WAL sidecar trap).</exception>
    /// <exception cref="IncompatibleExtractException">The DB is not a compatible v7.13.0 julie extract.</exception>
    public static BridgeData Read(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);

        var typeArguments = ReadTypeArguments(connection);
        var (literals, literalSites) = ReadLiterals(connection);
        var annotations = ReadAnnotations(connection);
        var dbSetProperties = ReadDbSetProperties(connection);

        return new BridgeData(typeArguments, literals, annotations, dbSetProperties, literalSites);
    }

    // ---- type_arguments ---------------------------------------------------------------------------------------

    private static IReadOnlyList<TypeArgument> ReadTypeArguments(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // identifier_id keys the CreateMap grouping; ordinal slots the args. Order by both so the builder's grouping
        // (and any test asserting row order) is deterministic.
        command.CommandText = """
            SELECT identifier_id, ordinal, parent_arg_id, type_name, file_path
            FROM type_arguments
            WHERE identifier_id IS NOT NULL AND type_name IS NOT NULL
            ORDER BY identifier_id, ordinal, id;
            """;

        var results = new List<TypeArgument>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string identifierId = reader.GetString(0);                                  // identifier_id  NOT NULL (filtered)
            int ordinal = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);                  // ordinal        nullable -> 0
            string? parentArgId = reader.IsDBNull(2) ? null : reader.GetString(2);     // parent_arg_id  nullable
            string typeName = reader.GetString(3);                                      // type_name      NOT NULL (filtered)
            string filePath = reader.IsDBNull(4) ? string.Empty : reader.GetString(4); // file_path      nullable -> ""

            results.Add(new TypeArgument(identifierId, ordinal, parentArgId, typeName, filePath));
        }
        return results;
    }

    // ---- literals ---------------------------------------------------------------------------------------------

    private static (IReadOnlyList<LiteralRecord> Literals, IReadOnlyDictionary<LiteralRecord, LiteralSite> Sites)
        ReadLiterals(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // literals carries its OWN file_path + start_line (the lean LiteralRecord does not re-expose them), so we
        // build the literal->(file,line) lookup the BridgeGraphBuilder's literal-evidence seam needs here.
        command.CommandText = """
            SELECT literal_text, kind, carrier, arg_position, language, containing_symbol_id,
                   start_byte, end_byte, file_path, start_line
            FROM literals
            WHERE literal_text IS NOT NULL
            ORDER BY file_path, start_byte, id;
            """;

        var literals = new List<LiteralRecord>();
        // Reference-identity keyed: each LiteralRecord instance is unique per row, so a per-instance lookup is exact
        // (two literals with identical field values still map to their own site).
        var sites = new Dictionary<LiteralRecord, LiteralSite>(ReferenceEqualityComparer.Instance);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string literalText = reader.GetString(0);                                          // literal_text  NOT NULL (filtered)
            string kind = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);            // kind          nullable -> ""
            string carrier = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);         // carrier       nullable -> ""
            int argPosition = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);                     // arg_position  nullable -> 0
            string language = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);         // language      nullable -> ""
            string containingSymbolId = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);// containing_symbol_id nullable -> ""
            int startByte = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);                       // start_byte    nullable -> 0
            int endByte = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);                         // end_byte      nullable -> 0
            string filePath = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);         // file_path     nullable -> ""
            int startLine = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);                        // start_line    nullable -> 0

            var record = new LiteralRecord(
                literalText, kind, carrier, argPosition, language, containingSymbolId,
                new SourceSpan(startByte, endByte));
            literals.Add(record);
            sites[record] = new LiteralSite(filePath, startLine);
        }
        return (literals, sites);
    }

    // ---- symbol_annotations -----------------------------------------------------------------------------------

    private static IReadOnlyList<SymbolAnnotation> ReadAnnotations(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // Deterministic by (symbol_id, ordinal) — the UNIQUE(symbol_id, ordinal) pair (findings 28-2).
        command.CommandText = """
            SELECT symbol_id, ordinal, annotation, annotation_key, raw_text, carrier
            FROM symbol_annotations
            WHERE symbol_id IS NOT NULL
            ORDER BY symbol_id, ordinal, id;
            """;

        var results = new List<SymbolAnnotation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string symbolId = reader.GetString(0);                                          // symbol_id       NOT NULL (filtered)
            int ordinal = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);                       // ordinal         nullable -> 0
            string annotation = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);    // annotation      nullable -> ""
            string annotationKey = reader.IsDBNull(3) ? string.Empty : reader.GetString(3); // annotation_key  nullable -> ""
            string rawText = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);       // raw_text        nullable -> ""
            string carrier = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);       // carrier         nullable -> ""

            results.Add(new SymbolAnnotation(symbolId, ordinal, annotation, annotationKey, rawText, carrier));
        }
        return results;
    }

    // ---- DbSet<T> properties ----------------------------------------------------------------------------------

    private static IReadOnlyList<DbSetProperty> ReadDbSetProperties(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // EF convention (findings 28-2): a DbContext exposes `public DbSet<Entity> TableName { get; set; }`. The
        // property NAME is the table; the generic arg of DbSet<…> in the signature is the entity. There is no
        // [Table] attribute and no Dapper FROM on the stored-proc repos, so this property is Leg 3's sole anchor.
        // Deterministic by (file_path, start_line, id).
        command.CommandText = """
            SELECT id, name, signature, file_path, start_line
            FROM symbols
            WHERE kind = 'property' AND name IS NOT NULL AND signature LIKE '%DbSet<%'
            ORDER BY file_path, start_line, id;
            """;

        var results = new List<DbSetProperty>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string propertyId = reader.GetString(0);                                       // id          NOT NULL
            string tableName = reader.GetString(1);                                        // name        NOT NULL (filtered) = table
            string signature = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);    // signature   (filtered to contain DbSet<)
            string filePath = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);     // file_path   nullable -> ""
            int startLine = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);                    // start_line  nullable -> 0

            string? entityType = ParseDbSetEntity(signature);
            if (entityType is null)
                continue; // a DbSet<…> with no parseable generic arg is not a usable entity↔table anchor

            results.Add(new DbSetProperty(propertyId, tableName, entityType, filePath, startLine));
        }
        return results;
    }

    /// <summary>
    /// Parse the entity type out of the FIRST <c>DbSet&lt;T&gt;</c> in a property signature. Returns the leaf type
    /// name (no namespace) of the generic arg, or null when no balanced <c>DbSet&lt;…&gt;</c> is present. Handles a
    /// namespaced arg (<c>DbSet&lt;Core.Data.ApplicationUser&gt;</c> → <c>ApplicationUser</c>) and tolerates trailing
    /// generic depth (the inner arg's own brackets) by matching the balanced close.
    /// </summary>
    private static string? ParseDbSetEntity(string signature)
    {
        const string marker = "DbSet<";
        int markerStart = signature.IndexOf(marker, StringComparison.Ordinal);
        if (markerStart < 0)
            return null;

        int open = markerStart + marker.Length - 1; // index of the '<'
        int depth = 0;
        int argStart = open + 1;
        for (int i = open; i < signature.Length; i++)
        {
            char ch = signature[i];
            if (ch == '<')
                depth++;
            else if (ch == '>')
            {
                depth--;
                if (depth == 0)
                {
                    var arg = signature[argStart..i].Trim();
                    return LeafTypeName(arg);
                }
            }
        }
        return null; // unbalanced <…> — not a usable signature
    }

    /// <summary>The leaf type name of a possibly-namespaced type arg (the run after the last top-level <c>.</c>).</summary>
    private static string? LeafTypeName(string typeArg)
    {
        if (typeArg.Length == 0)
            return null;
        int lastDot = typeArg.LastIndexOf('.');
        var leaf = lastDot >= 0 ? typeArg[(lastDot + 1)..] : typeArg;
        leaf = leaf.Trim();
        return leaf.Length == 0 ? null : leaf;
    }
}

/// <summary>
/// The RAW bridge rows + literal-site lookup read from a julie extract (plan Task 9). NOT leg inputs — the
/// per-leg transformation is <see cref="BridgeGraphBuilder"/>'s job. <see cref="LiteralSites"/> is the
/// literal-evidence seam the builder requires (Task 8 deviation): the <c>literals</c> table carries its own
/// file/line, but the lean <see cref="LiteralRecord"/> does not re-expose them, so the reader returns the
/// per-literal-instance lookup here.
/// </summary>
/// <param name="TypeArguments">The <c>type_arguments</c> rows (CreateMap grouping input).</param>
/// <param name="Literals">The <c>literals</c> rows (url client calls + sql).</param>
/// <param name="Annotations">The <c>symbol_annotations</c> rows (http-verb endpoints + class <c>[Route]</c>).</param>
/// <param name="DbSetProperties">The DbContext <c>DbSet&lt;T&gt;</c> property breadcrumbs (Leg 3 PRIMARY).</param>
/// <param name="LiteralSites">The per-literal-instance <c>literal → (file, line)</c> lookup (the literal-evidence seam).</param>
public sealed record BridgeData(
    IReadOnlyList<TypeArgument> TypeArguments,
    IReadOnlyList<LiteralRecord> Literals,
    IReadOnlyList<SymbolAnnotation> Annotations,
    IReadOnlyList<DbSetProperty> DbSetProperties,
    IReadOnlyDictionary<LiteralRecord, LiteralSite> LiteralSites);
