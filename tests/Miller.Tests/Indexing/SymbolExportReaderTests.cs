using System.Text.Json;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SymbolExportReaderTests
{
    [Fact]
    public void ExportJsonLines_RemainsSchemaOneAndAddsDeterministicRoleAndCurrencyEvidence()
    {
        using var fx = JulieDbFixture.CreateTestRoleEvidenceScenario("export");

        string first = SymbolExportReader.ExportJsonLines(fx.DbPath);
        string second = SymbolExportReader.ExportJsonLines(fx.DbPath);

        Assert.Equal(first, second);
        JsonElement[] rows = ParseLines(first);
        Assert.Equal(["Current", "FileStatus", "Diagnostic", "Combined", "Unavailable"],
            rows.Select(static row => row.GetProperty("name").GetString()!).ToArray());
        Assert.All(rows, row => Assert.Equal(SymbolExportReader.SchemaVersion,
            row.GetProperty("schema_version").GetInt32()));
        Assert.Equal(1, SymbolExportReader.SchemaVersion);

        string[] expectedFields =
        [
            "schema_version", "symbol_id", "name", "kind", "language", "path",
            "start_line", "end_line", "start_byte", "end_byte", "visibility",
            "parent_symbol_id", "signature", "has_doc", "body_hash", "is_test",
            "test_case", "test_container", "test_lifecycle", "test_evidence_status",
            "test_evidence_reason",
        ];
        Assert.All(rows, row => Assert.Equal(expectedFields,
            row.EnumerateObject().Select(static property => property.Name).ToArray()));

        AssertEvidence(rows, "Current", isTest: true, isCase: true, isContainer: true, isLifecycle: false,
            status: "current", reason: null);
        AssertEvidence(rows, "FileStatus", isTest: true, isCase: false, isContainer: false, isLifecycle: true,
            status: "unknown", reason: "file_status");
        AssertEvidence(rows, "Diagnostic", isTest: false, isCase: false, isContainer: true, isLifecycle: false,
            status: "unknown", reason: "parse_diagnostics");
        AssertEvidence(rows, "Combined", isTest: true, isCase: false, isContainer: true, isLifecycle: true,
            status: "unknown", reason: "file_status_and_parse_diagnostics");
        AssertEvidence(rows, "Unavailable", isTest: true, isCase: true, isContainer: false, isLifecycle: false,
            status: "unknown", reason: "file_evidence_unavailable");
    }

    [Fact]
    public void ExportJsonLines_MissingOptionalEvidenceSources_DefaultsRoleEvidenceToUnknown()
    {
        // The schema gate checks artifact_metadata only, never table/column presence, so a gate-passing
        // 2.9–2.11 artifact can lack the role columns and the files/parse_diagnostics tables. The export
        // must degrade to unknown evidence exactly like SqliteSymbolReader, not throw SqliteException.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("minimal-export", "MinimalExport", "method", "csharp",
                "Minimal.cs", "void MinimalExport()", 1, null) { IsTest = true },
        });
        fx.ExecuteWrite("""
            DROP TABLE parse_diagnostics;
            DROP TABLE files;
            ALTER TABLE symbols DROP COLUMN test_container;
            ALTER TABLE symbols DROP COLUMN test_lifecycle;
            """);

        JsonElement row = Assert.Single(ParseLines(SymbolExportReader.ExportJsonLines(fx.DbPath)));

        Assert.Equal("MinimalExport", row.GetProperty("name").GetString());
        AssertEvidence([row], "MinimalExport", isTest: true, isCase: true, isContainer: false,
            isLifecycle: false, status: "unknown", reason: "file_evidence_unavailable");
    }

    private static void AssertEvidence(
        IReadOnlyList<JsonElement> rows,
        string name,
        bool isTest,
        bool isCase,
        bool isContainer,
        bool isLifecycle,
        string status,
        string? reason)
    {
        JsonElement row = rows.Single(candidate => candidate.GetProperty("name").GetString() == name);
        Assert.Equal(isTest, row.GetProperty("is_test").GetBoolean());
        Assert.Equal(isCase, row.GetProperty("test_case").GetBoolean());
        Assert.Equal(isContainer, row.GetProperty("test_container").GetBoolean());
        Assert.Equal(isLifecycle, row.GetProperty("test_lifecycle").GetBoolean());
        Assert.Equal(status, row.GetProperty("test_evidence_status").GetString());
        if (reason is null)
            Assert.Equal(JsonValueKind.Null, row.GetProperty("test_evidence_reason").ValueKind);
        else
            Assert.Equal(reason, row.GetProperty("test_evidence_reason").GetString());
    }

    private static JsonElement[] ParseLines(string jsonl) => jsonl
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(static line => JsonDocument.Parse(line).RootElement.Clone())
        .ToArray();
}
