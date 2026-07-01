using Microsoft.Data.Sqlite;
using Miller.Core.Contracts;
using Miller.Core.Graph;
using System.Text.Json;

namespace Miller.Indexing;

/// <summary>
/// The cross-language bridge read layer (plan Task 9; design SQLite-reader section). Opens a julie extract DB
/// under the SAME D4 read-only discipline as <see cref="SqliteSymbolReader"/> (<see cref="SqliteReadOnlyAccess.Open"/>
/// + <see cref="JulieSchemaGate"/>) and projects the four bridge-relevant tables into the RAW Miller.Core contract
/// rows the <see cref="BridgeGraphBuilder"/> consumes. It performs NO leg transformation — that is Task 8's job; this
/// reader only maps julie columns to records.
///
/// <para>The bridge sources (v1 schema.rs column shapes):
/// <list type="bullet">
/// <item><c>type_arguments</c> JOIN <c>type_argument_usages</c> → <see cref="TypeArgument"/> (CreateMap grouping
/// input): v1 moved <c>identifier_id</c>/<c>path</c> onto the usage row, so the reader JOINs by <c>usage_id</c>.
/// Ordered by <c>identifier_id</c> then <c>ordinal</c> for deterministic grouping downstream.</item>
/// <item><c>literals</c> → <see cref="LiteralRecord"/> (url client calls + sql), ordered by <c>path</c> then start_byte.
/// The <c>literals</c> table HAS its own <c>path</c>/<c>start_line</c> columns (unlike the lean
/// <see cref="LiteralRecord"/> which surfaces only span + containing-symbol id), so the reader ALSO returns the
/// literal→(file,line) lookup the builder's literal-evidence seam needs (Task 8 deviation).</item>
/// <item><c>symbol_annotations</c> → <see cref="SymbolAnnotation"/> (http-verb endpoints + class <c>[Route]</c>),
/// ordered by <c>symbol_id</c> then <c>annotation_id</c> (v1 dropped <c>ordinal</c>).</item>
/// <item>DbContext <c>DbSet&lt;T&gt;</c> properties → <see cref="DbSetProperty"/> (Leg 3 PRIMARY), parsed from
/// <c>symbols</c> rows with <c>kind='property'</c> whose <c>signature</c> contains <c>DbSet&lt;…&gt;</c>: the property
/// name IS the table name (EF convention), the generic arg IS the entity type.</item>
/// <item><c>structural_facts</c> → <see cref="StructuralFactRecord"/> for parser-backed web/framework route facts
/// emitted by julie-extractors, including ASP.NET Minimal API, htmx, Vue, React Router, Next.js, and Nuxt routes.</item>
/// </list></para>
///
/// <para>Sync by design: this is part of the single startup/rebuild pass (Microsoft.Data.Sqlite's async is
/// synchronous internally), and it runs after <see cref="SqliteSymbolReader"/> on the same DB.</para>
/// </summary>
public static class SqliteBridgeReader
{
    /// <summary>
    /// Read bridge tables from the julie extract at <paramref name="dbPath"/> into the raw Core contract
    /// collections (plus the literal→file:line lookup for the builder's literal-evidence seam).
    /// </summary>
    /// <param name="dbPath">Path to the julie extract DB (opened <c>Mode=ReadOnly</c> by the shared D4 discipline).</param>
    /// <returns>The raw bridge rows + literal-site lookup, ready for <see cref="BridgeGraphBuilder.Build"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="dbPath"/> is null/empty/whitespace.</exception>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The DB's directory is not writable (WAL sidecar trap).</exception>
    /// <exception cref="IncompatibleExtractException">The DB is not a compatible julie-extract v1 artifact.</exception>
    public static BridgeData Read(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);

        var typeArguments = ReadTypeArguments(connection);
        var (literals, literalSites) = ReadLiterals(connection);
        var annotations = ReadAnnotations(connection);
        var dbSetProperties = ReadDbSetProperties(connection);
        var structuralFacts = ReadStructuralFacts(connection);

        return new BridgeData(typeArguments, literals, annotations, dbSetProperties, structuralFacts, literalSites);
    }

    // ---- type_arguments ---------------------------------------------------------------------------------------

    private static IReadOnlyList<TypeArgument> ReadTypeArguments(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // v1 splits the use-site identity onto type_argument_usages: identifier_id/path live there, the args
        // (ordinal/parent/type_name) live on type_arguments and JOIN by usage_id. identifier_id keys the CreateMap
        // grouping; ordinal slots the args. Order by (identifier_id, ordinal, type_argument_id) so the builder's
        // grouping (and any test asserting row order) is deterministic. By-name reads (D6).
        command.CommandText = """
            SELECT u.identifier_id, t.ordinal, t.parent_type_argument_id, t.type_name, u.path
            FROM type_arguments t
            JOIN type_argument_usages u ON u.usage_id = t.usage_id
            WHERE u.identifier_id IS NOT NULL AND t.type_name IS NOT NULL
            ORDER BY u.identifier_id, t.ordinal, t.type_argument_id;
            """;

        var results = new List<TypeArgument>();
        using var reader = command.ExecuteReader();
        int oIdentifierId = reader.GetOrdinal("identifier_id");
        int oOrdinal = reader.GetOrdinal("ordinal");
        int oParent = reader.GetOrdinal("parent_type_argument_id");
        int oTypeName = reader.GetOrdinal("type_name");
        int oPath = reader.GetOrdinal("path");
        while (reader.Read())
        {
            string identifierId = reader.GetString(oIdentifierId);                          // identifier_id  NOT NULL (filtered)
            int ordinal = reader.IsDBNull(oOrdinal) ? 0 : reader.GetInt32(oOrdinal);        // ordinal        v1 NOT NULL; guard
            string? parentArgId = reader.IsDBNull(oParent) ? null : reader.GetString(oParent); // parent_type_argument_id nullable
            string typeName = reader.GetString(oTypeName);                                  // type_name      NOT NULL (filtered)
            string filePath = reader.IsDBNull(oPath) ? string.Empty : reader.GetString(oPath); // u.path      -> FilePath

            results.Add(new TypeArgument(identifierId, ordinal, parentArgId, typeName, filePath));
        }
        return results;
    }

    // ---- literals ---------------------------------------------------------------------------------------------

    private static (List<LiteralRecord> Literals, Dictionary<LiteralRecord, LiteralSite> Sites)
        ReadLiterals(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // v1 literals carries its OWN path + start_line (the lean LiteralRecord does not re-expose them), so we
        // build the literal->(file,line) lookup the BridgeGraphBuilder's literal-evidence seam needs here.
        // By-name reads (D6); order by (path, start_byte, literal_id).
        command.CommandText = """
            SELECT literal_text, kind, carrier, arg_position, language, containing_symbol_id,
                   start_byte, end_byte, path, start_line
            FROM literals
            WHERE literal_text IS NOT NULL
            ORDER BY path, start_byte, literal_id;
            """;

        var literals = new List<LiteralRecord>();
        // Reference-identity keyed: each LiteralRecord instance is unique per row, so a per-instance lookup is exact
        // (two literals with identical field values still map to their own site).
        var sites = new Dictionary<LiteralRecord, LiteralSite>(ReferenceEqualityComparer.Instance);

        using var reader = command.ExecuteReader();
        int oLiteralText = reader.GetOrdinal("literal_text");
        int oKind = reader.GetOrdinal("kind");
        int oCarrier = reader.GetOrdinal("carrier");
        int oArgPosition = reader.GetOrdinal("arg_position");
        int oLanguage = reader.GetOrdinal("language");
        int oContaining = reader.GetOrdinal("containing_symbol_id");
        int oStartByte = reader.GetOrdinal("start_byte");
        int oEndByte = reader.GetOrdinal("end_byte");
        int oPath = reader.GetOrdinal("path");
        int oStartLine = reader.GetOrdinal("start_line");
        while (reader.Read())
        {
            string literalText = reader.GetString(oLiteralText);                                          // literal_text  NOT NULL (filtered)
            string kind = reader.IsDBNull(oKind) ? string.Empty : reader.GetString(oKind);               // kind          nullable -> ""
            string carrier = reader.IsDBNull(oCarrier) ? string.Empty : reader.GetString(oCarrier);      // carrier       nullable -> ""
            int argPosition = reader.IsDBNull(oArgPosition) ? 0 : reader.GetInt32(oArgPosition);          // arg_position  nullable -> 0
            string language = reader.IsDBNull(oLanguage) ? string.Empty : reader.GetString(oLanguage);   // language      nullable -> ""
            string containingSymbolId = reader.IsDBNull(oContaining) ? string.Empty : reader.GetString(oContaining); // containing_symbol_id nullable -> ""
            int startByte = reader.IsDBNull(oStartByte) ? 0 : reader.GetInt32(oStartByte);                // start_byte    nullable -> 0
            int endByte = reader.IsDBNull(oEndByte) ? 0 : reader.GetInt32(oEndByte);                      // end_byte      nullable -> 0
            string filePath = reader.IsDBNull(oPath) ? string.Empty : reader.GetString(oPath);            // path          -> FilePath
            int startLine = reader.IsDBNull(oStartLine) ? 0 : reader.GetInt32(oStartLine);                // start_line    nullable -> 0

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
        // v1 drops symbol_annotations.ordinal; the deterministic order re-keys to (symbol_id, annotation_id)
        // (design §4.3). The SymbolAnnotation Core record keeps its Ordinal field (minimizing blast radius into
        // BridgeGraphBuilder) but it is no longer meaningful — passed as 0. By-name reads (D6).
        command.CommandText = """
            SELECT symbol_id, annotation, annotation_key, raw_text, carrier
            FROM symbol_annotations
            WHERE symbol_id IS NOT NULL
            ORDER BY symbol_id, annotation_id;
            """;

        var results = new List<SymbolAnnotation>();
        using var reader = command.ExecuteReader();
        int oSymbolId = reader.GetOrdinal("symbol_id");
        int oAnnotation = reader.GetOrdinal("annotation");
        int oAnnotationKey = reader.GetOrdinal("annotation_key");
        int oRawText = reader.GetOrdinal("raw_text");
        int oCarrier = reader.GetOrdinal("carrier");
        while (reader.Read())
        {
            string symbolId = reader.GetString(oSymbolId);                                          // symbol_id       NOT NULL (filtered)
            string annotation = reader.IsDBNull(oAnnotation) ? string.Empty : reader.GetString(oAnnotation);       // annotation      v1 NOT NULL; guard
            string annotationKey = reader.IsDBNull(oAnnotationKey) ? string.Empty : reader.GetString(oAnnotationKey); // annotation_key v1 NOT NULL; guard
            string rawText = reader.IsDBNull(oRawText) ? string.Empty : reader.GetString(oRawText);  // raw_text        nullable -> ""
            string carrier = reader.IsDBNull(oCarrier) ? string.Empty : reader.GetString(oCarrier);  // carrier         nullable -> ""

            // ordinal is gone in v1; pass 0 (annotation order is now opaque-id order, not insertion ordinal).
            results.Add(new SymbolAnnotation(symbolId, 0, annotation, annotationKey, rawText, carrier));
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
        // v1 columns (symbol_id/path); deterministic by (path, start_line, symbol_id). By-name reads (D6).
        command.CommandText = """
            SELECT symbol_id, name, signature, path, start_line
            FROM symbols
            WHERE kind = 'property' AND name IS NOT NULL AND signature LIKE '%DbSet<%'
            ORDER BY path, start_line, symbol_id;
            """;

        var results = new List<DbSetProperty>();
        using var reader = command.ExecuteReader();
        int oSymbolId = reader.GetOrdinal("symbol_id");
        int oName = reader.GetOrdinal("name");
        int oSignature = reader.GetOrdinal("signature");
        int oPath = reader.GetOrdinal("path");
        int oStartLine = reader.GetOrdinal("start_line");
        while (reader.Read())
        {
            string propertyId = reader.GetString(oSymbolId);                               // symbol_id   NOT NULL
            string tableName = reader.GetString(oName);                                    // name        NOT NULL (filtered) = table
            string signature = reader.IsDBNull(oSignature) ? string.Empty : reader.GetString(oSignature); // signature (filtered to contain DbSet<)
            string filePath = reader.IsDBNull(oPath) ? string.Empty : reader.GetString(oPath); // path     -> FilePath
            int startLine = reader.IsDBNull(oStartLine) ? 0 : reader.GetInt32(oStartLine);  // start_line  nullable -> 0

            string? entityType = ParseDbSetEntity(signature);
            if (entityType is null)
                continue; // a DbSet<…> with no parseable generic arg is not a usable entity↔table anchor

            results.Add(new DbSetProperty(propertyId, tableName, entityType, filePath, startLine));
        }
        return results;
    }

    // ---- structural_facts -------------------------------------------------------------------------------------

    private static IReadOnlyList<StructuralFactRecord> ReadStructuralFacts(SqliteConnection connection)
    {
        if (!TableExists(connection, "structural_facts"))
            return [];

        var patternIds = BridgeStructuralPatterns.BridgeFactPatternIds;
        var placeholders = string.Join(", ", patternIds.Select((_, index) => "$pattern" + index));

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT structural_fact_id, pattern_id, language, path, capture_name, node_kind,
                   containing_symbol_id, start_line, start_column, end_line, end_column,
                   start_byte, end_byte, confidence, metadata_json
            FROM structural_facts
            WHERE pattern_id IN ({placeholders})
            ORDER BY path, start_byte, structural_fact_id;
            """;
        for (int i = 0; i < patternIds.Count; i++)
            command.Parameters.AddWithValue("$pattern" + i, patternIds[i]);

        var results = new List<StructuralFactRecord>();
        using var reader = command.ExecuteReader();
        int oFactId = reader.GetOrdinal("structural_fact_id");
        int oPatternId = reader.GetOrdinal("pattern_id");
        int oLanguage = reader.GetOrdinal("language");
        int oPath = reader.GetOrdinal("path");
        int oCaptureName = reader.GetOrdinal("capture_name");
        int oNodeKind = reader.GetOrdinal("node_kind");
        int oContaining = reader.GetOrdinal("containing_symbol_id");
        int oStartLine = reader.GetOrdinal("start_line");
        int oStartColumn = reader.GetOrdinal("start_column");
        int oEndLine = reader.GetOrdinal("end_line");
        int oEndColumn = reader.GetOrdinal("end_column");
        int oStartByte = reader.GetOrdinal("start_byte");
        int oEndByte = reader.GetOrdinal("end_byte");
        int oConfidence = reader.GetOrdinal("confidence");
        int oMetadata = reader.GetOrdinal("metadata_json");
        while (reader.Read())
        {
            string? metadataJson = reader.IsDBNull(oMetadata) ? null : reader.GetString(oMetadata);
            results.Add(new StructuralFactRecord(
                FactId: reader.GetString(oFactId),
                PatternId: reader.GetString(oPatternId),
                Language: reader.GetString(oLanguage),
                Path: reader.GetString(oPath),
                CaptureName: reader.GetString(oCaptureName),
                NodeKind: reader.GetString(oNodeKind),
                ContainingSymbolId: reader.IsDBNull(oContaining) ? null : reader.GetString(oContaining),
                Span: new StructuralFactSpan(
                    reader.GetInt32(oStartLine),
                    reader.GetInt32(oStartColumn),
                    reader.GetInt32(oEndLine),
                    reader.GetInt32(oEndColumn),
                    reader.GetInt32(oStartByte),
                    reader.GetInt32(oEndByte)),
                Confidence: reader.GetDouble(oConfidence),
                Metadata: ParseMetadata(metadataJson)));
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

    private static IReadOnlyDictionary<string, string> ParseMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                metadata[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
            }
            return metadata;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        using var reader = command.ExecuteReader();
        return reader.Read();
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
/// <param name="StructuralFacts">The parser-backed route facts used by bridge providers.</param>
/// <param name="LiteralSites">The per-literal-instance <c>literal → (file, line)</c> lookup (the literal-evidence seam).</param>
public sealed record BridgeData(
    IReadOnlyList<TypeArgument> TypeArguments,
    IReadOnlyList<LiteralRecord> Literals,
    IReadOnlyList<SymbolAnnotation> Annotations,
    IReadOnlyList<DbSetProperty> DbSetProperties,
    IReadOnlyList<StructuralFactRecord> StructuralFacts,
    IReadOnlyDictionary<LiteralRecord, LiteralSite> LiteralSites);
