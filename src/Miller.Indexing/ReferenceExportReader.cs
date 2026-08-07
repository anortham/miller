using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Emits one deterministic canonical assertion for each
/// <c>(reference_site_id, target, canonical_kind)</c> tuple represented by identifiers, relationships, and
/// resolution overlays. Producer-owned reference-site identity and provenance are preserved without
/// consumer span matching. Rows are ordered by path, producer start byte, reference-site ID, canonical kind,
/// and target identity so re-exporting an unchanged artifact is byte-identical.
///
/// <para>Every row carries the artifact's <c>index_level</c>. The union's <c>identifiers</c> and
/// <c>identifier_resolutions</c> arms are EMPTY at symbols level while the <c>relationships</c> arm is populated,
/// so this feed degrades to a partial — not an empty — stream, and a consumer reading stdout alone has no other
/// way to tell a symbols-level export from a complete one.</para>
/// </summary>
public static class ReferenceExportReader
{
    public const int SchemaVersion = 2;

    public static string ExportJsonLines(string symbolsDbPath)
    {
        using var writer = new StringWriter();
        WriteJsonLines(symbolsDbPath, writer);
        return writer.ToString();
    }

    public static void WriteJsonLines(string symbolsDbPath, TextWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        ArgumentNullException.ThrowIfNull(writer);

        using SqliteConnection connection = SqliteReadOnlyAccess.Open(symbolsDbPath);
        JulieSchemaGate.Verify(connection);

        string? artifactId = ReadArtifactId(connection);
        long? workspaceRevision = ReadWorkspaceRevision(connection);
        string indexLevel = ExtractIndexLevelReader.Read(connection);
        IReadOnlyList<ReferenceAssertionRow> rows = ReadAssertions(connection);
        foreach (ReferenceAssertionRow row in rows)
        {
            writer.Write(RenderRow(row, artifactId, workspaceRevision, indexLevel));
            writer.Write('\n');
        }
    }

    private static IReadOnlyList<ReferenceAssertionRow> ReadAssertions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.reference_site_id, s.is_exact, s.provenance, s.path, s.language,
                   s.containing_symbol_id, s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, i.kind, NULL AS target_symbol_id, i.name,
                   NULL AS target_kind, NULL AS target_is_test, NULL AS resolution_tier, i.confidence,
                   'name_fallback' AS evidence_source, source.name, source.kind, source.is_test
            FROM identifiers i
            JOIN reference_sites s ON s.reference_site_id = i.reference_site_id
            LEFT JOIN identifier_resolutions ir ON ir.identifier_id = i.identifier_id
            LEFT JOIN symbols source ON source.symbol_id = s.containing_symbol_id
            WHERE ir.target_symbol_id IS NULL
            UNION ALL
            SELECT s.reference_site_id, s.is_exact, s.provenance, s.path, s.language,
                   s.containing_symbol_id, s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, i.kind, ir.target_symbol_id, target.name,
                   target.kind, target.is_test, ir.tier, COALESCE(ir.confidence, i.confidence),
                   'identifier_resolution', source.name, source.kind, source.is_test
            FROM identifier_resolutions ir
            JOIN identifiers i ON i.identifier_id = ir.identifier_id
            JOIN reference_sites s ON s.reference_site_id = i.reference_site_id
            LEFT JOIN symbols source ON source.symbol_id = s.containing_symbol_id
            JOIN symbols target ON target.symbol_id = ir.target_symbol_id
            UNION ALL
            SELECT s.reference_site_id, s.is_exact, s.provenance, s.path, s.language,
                   s.containing_symbol_id, s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, r.kind, r.to_symbol_id, target.name,
                   target.kind, target.is_test, NULL, r.confidence,
                   'relationship', source.name, source.kind, source.is_test
            FROM relationships r
            JOIN reference_sites s ON s.reference_site_id = r.reference_site_id
            LEFT JOIN symbols source ON source.symbol_id = s.containing_symbol_id
            JOIN symbols target ON target.symbol_id = r.to_symbol_id
            UNION ALL
            SELECT s.reference_site_id, s.is_exact, s.provenance, s.path, s.language,
                   s.containing_symbol_id, s.start_line, s.start_column, s.end_line, s.end_column,
                   s.start_byte, s.end_byte, p.kind, pr.target_symbol_id,
                   COALESCE(target.name, p.target_display_name), target.kind, target.is_test,
                   pr.tier, CASE WHEN pr.confidence IS NULL THEN p.confidence ELSE MIN(p.confidence, pr.confidence) END,
                   CASE WHEN pr.target_symbol_id IS NULL THEN 'name_fallback' ELSE 'pending_resolution' END,
                   source.name, source.kind, source.is_test
            FROM pending_relationships p
            JOIN reference_sites s ON s.reference_site_id = p.reference_site_id
            LEFT JOIN pending_resolutions pr ON pr.pending_relationship_id = p.pending_relationship_id
            LEFT JOIN symbols source ON source.symbol_id = s.containing_symbol_id
            LEFT JOIN symbols target ON target.symbol_id = pr.target_symbol_id;
            """;

        var evidence = new List<ReferenceEvidenceExportRow>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            evidence.Add(new ReferenceEvidenceExportRow(
                ReferenceSiteId: reader.GetString(0),
                IsExact: reader.GetInt64(1) == 1,
                SiteProvenance: reader.GetString(2),
                Path: reader.GetString(3),
                Language: reader.GetString(4),
                ContainingSymbolId: ReadString(reader, 5),
                StartLine: ReadInt64(reader, 6),
                StartColumn: ReadInt64(reader, 7),
                EndLine: ReadInt64(reader, 8),
                EndColumn: ReadInt64(reader, 9),
                StartByte: ReadInt64(reader, 10),
                EndByte: ReadInt64(reader, 11),
                CanonicalKind: CanonicalKind(reader.GetString(12)),
                TargetSymbolId: ReadString(reader, 13),
                TargetName: reader.GetString(14),
                TargetKind: ReadString(reader, 15),
                TargetIsTest: ReadBool(reader, 16),
                ResolutionTier: ReadInt64(reader, 17),
                Confidence: reader.GetDouble(18),
                EvidenceSource: reader.GetString(19),
                SourceName: ReadString(reader, 20),
                SourceKind: ReadString(reader, 21),
                SourceIsTest: ReadBool(reader, 22)));
        }

        return evidence
            .GroupBy(static row => new ReferenceAssertionKey(
                row.ReferenceSiteId,
                row.TargetSymbolId,
                row.TargetSymbolId is null ? row.TargetName : null,
                row.CanonicalKind))
            .Select(static group =>
            {
                ReferenceEvidenceExportRow primary = group
                    .OrderBy(static row => EvidencePrecedence(row.EvidenceSource))
                    .ThenByDescending(static row => row.Confidence)
                    .First();
                return new ReferenceAssertionRow(
                    primary,
                    group.Select(static row => row.EvidenceSource)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(EvidencePrecedence)
                        .ThenBy(static source => source, StringComparer.Ordinal)
                        .ToArray(),
                    group.Where(static row => row.ResolutionTier is not null)
                        .Select(static row => row.ResolutionTier)
                        .Min(),
                    group.Max(static row => row.Confidence));
            })
            .OrderBy(static row => row.Primary.Path, StringComparer.Ordinal)
            .ThenBy(static row => row.Primary.StartByte ?? long.MaxValue)
            .ThenBy(static row => row.Primary.ReferenceSiteId, StringComparer.Ordinal)
            .ThenBy(static row => row.Primary.CanonicalKind, StringComparer.Ordinal)
            .ThenBy(static row => row.Primary.TargetSymbolId, StringComparer.Ordinal)
            .ThenBy(static row => row.Primary.TargetName, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ReadArtifactId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM artifact_metadata WHERE key = 'artifact_id' LIMIT 1;";
        object? value = command.ExecuteScalar();
        return value is string text ? text : null;
    }

    private static long? ReadWorkspaceRevision(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(revision_id) FROM extraction_revisions;";
        object? value = command.ExecuteScalar();
        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string RenderRow(
        ReferenceAssertionRow assertion,
        string? artifactId,
        long? workspaceRevision,
        string indexLevel)
    {
        ReferenceEvidenceExportRow row = assertion.Primary;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            writer.WriteString("reference_site_id", row.ReferenceSiteId);
            writer.WriteString("canonical_kind", row.CanonicalKind);
            writer.WriteString("language", row.Language);
            writer.WriteString("path", row.Path);
            WriteNullableString(writer, "source_symbol_id", row.ContainingSymbolId);
            WriteNullableString(writer, "source_symbol_name", row.SourceName);
            WriteNullableString(writer, "source_symbol_kind", row.SourceKind);
            WriteNullableBool(writer, "source_symbol_is_test", row.SourceIsTest);
            if (row.StartLine is null)
            {
                writer.WriteNull("span");
            }
            else
            {
                writer.WriteStartObject("span");
                writer.WriteNumber("start_line", row.StartLine.Value);
                writer.WriteNumber("start_column", row.StartColumn!.Value);
                writer.WriteNumber("end_line", row.EndLine!.Value);
                writer.WriteNumber("end_column", row.EndColumn!.Value);
                writer.WriteNumber("start_byte", row.StartByte!.Value);
                writer.WriteNumber("end_byte", row.EndByte!.Value);
                writer.WriteEndObject();
            }
            writer.WriteBoolean("is_exact", row.IsExact);
            writer.WriteString("site_provenance", row.SiteProvenance);
            WriteNullableString(writer, "target_symbol_id", row.TargetSymbolId);
            writer.WriteString("target_name", row.TargetName);
            WriteNullableString(writer, "target_symbol_kind", row.TargetKind);
            WriteNullableBool(writer, "target_symbol_is_test", row.TargetIsTest);
            writer.WriteString("resolution_status", row.TargetSymbolId is null ? "unresolved" : "resolved");
            WriteNullableLong(writer, "resolution_tier", assertion.ResolutionTier);
            writer.WriteNumber("confidence", assertion.Confidence);
            writer.WriteStartArray("provenance");
            foreach (string source in assertion.Provenance)
                writer.WriteStringValue(source);
            writer.WriteEndArray();
            WriteNullableString(writer, "artifact_id", artifactId);
            WriteNullableLong(writer, "workspace_revision", workspaceRevision);
            writer.WriteString("index_level", indexLevel);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string CanonicalKind(string kind) => kind switch
    {
        "calls" => "call",
        "imports" => "import",
        "references" => "reference",
        "uses" => "usage",
        _ => kind,
    };

    private static int EvidencePrecedence(string source) => source switch
    {
        "identifier_direct" => 0,
        "identifier_resolution" => 1,
        "relationship" => 2,
        "pending_resolution" => 3,
        "name_fallback" => 4,
        _ => 5,
    };

    private static string? ReadString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? ReadInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static bool? ReadBool(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static void WriteNullableLong(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteNumber(name, value.Value);
    }

    private static void WriteNullableBool(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteBoolean(name, value.Value);
    }

    private sealed record ReferenceEvidenceExportRow(
        string ReferenceSiteId,
        bool IsExact,
        string SiteProvenance,
        string Path,
        string Language,
        string? ContainingSymbolId,
        long? StartLine,
        long? StartColumn,
        long? EndLine,
        long? EndColumn,
        long? StartByte,
        long? EndByte,
        string CanonicalKind,
        string? TargetSymbolId,
        string TargetName,
        string? TargetKind,
        bool? TargetIsTest,
        long? ResolutionTier,
        double Confidence,
        string EvidenceSource,
        string? SourceName,
        string? SourceKind,
        bool? SourceIsTest);

    private readonly record struct ReferenceAssertionKey(
        string ReferenceSiteId,
        string? TargetSymbolId,
        string? TargetName,
        string CanonicalKind);

    private sealed record ReferenceAssertionRow(
        ReferenceEvidenceExportRow Primary,
        IReadOnlyList<string> Provenance,
        long? ResolutionTier,
        double Confidence);
}
