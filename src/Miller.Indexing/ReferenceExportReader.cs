using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;

namespace Miller.Indexing;

/// <summary>
/// Emits one deterministic canonical assertion for each
/// <c>(reference_site_id, target, canonical_kind)</c> tuple represented by identifiers, relationships, and
/// resolution overlays. Producer-owned reference-site identity and provenance are preserved without
/// consumer span matching. Rows are ordered by path, producer start byte, reference-site ID, canonical kind,
/// and target identity so re-exporting an unchanged artifact is byte-identical.
///
/// <para>Every row carries the artifact's <c>index_level</c>. Identifier-derived arms are EMPTY at symbols
/// level while the <c>relationships</c> arm is populated,
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
        WriteJsonLines(connection, writer, reader: null);
    }

    public static void WriteJsonLines(IWorkspaceReadSession session, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(writer);
        session.Read(connection =>
        {
            WriteJsonLines(connection, writer, ReferenceEvidenceReader.ReaderFor(session, connection));
            return true;
        });
    }

    private static void WriteJsonLines(
        SqliteConnection connection,
        TextWriter writer,
        QueryTimeResolutionReader? reader)
    {
        if (reader is null)
            JulieSchemaGate.Verify(connection);

        string? artifactId = ReadArtifactId(connection);
        long? workspaceRevision = ReadWorkspaceRevision(connection);
        string indexLevel = ExtractIndexLevelReader.Read(connection);
        reader ??= new QueryTimeResolutionReader(RevisionFactCache.LoadFromArtifact(connection), visibility: null);
        IReadOnlyList<ReferenceAssertionRow> rows = ReadAssertions(reader, connection);
        foreach (ReferenceAssertionRow row in rows)
        {
            writer.Write(RenderRow(row, artifactId, workspaceRevision, indexLevel));
            writer.Write('\n');
        }
    }

    private static IReadOnlyList<ReferenceAssertionRow> ReadAssertions(
        QueryTimeResolutionReader resolution,
        SqliteConnection connection)
    {
        var evidence = new List<ReferenceEvidenceExportRow>();
        foreach (QueryTimeExportEvidence row in resolution.ReadExportEvidence(connection))
        {
            evidence.Add(new ReferenceEvidenceExportRow(
                row.ReferenceSiteId,
                row.IsExact,
                row.SiteProvenance,
                row.Path,
                row.Language,
                row.ContainingSymbolId,
                row.StartLine,
                row.StartColumn,
                row.EndLine,
                row.EndColumn,
                row.StartByte,
                row.EndByte,
                row.CanonicalKind,
                row.TargetSymbolId,
                row.TargetName,
                row.TargetKind,
                row.TargetIsTest,
                row.ResolutionTier,
                row.Confidence,
                row.EvidenceSource,
                row.SourceName,
                row.SourceKind,
                row.SourceIsTest));
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

    private static int EvidencePrecedence(string source) => source switch
    {
        "identifier_resolution" => 0,
        "relationship" => 1,
        "pending_resolution" => 2,
        "name_fallback" => 3,
        _ => 4,
    };

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
