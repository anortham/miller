using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SymbolExportReaderTests
{
    [Fact]
    public void ExportJsonLines_RemainsSchemaOneAndAddsDeterministicRoleAndCurrencyEvidence()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("export-current", "Current", "method", "csharp",
                "a-current.cs", "void Current()", 1, null) { IsTest = true, TestContainer = true },
            new JulieDbFixture.SymbolRow("export-file-status", "FileStatus", "method", "csharp",
                "b-file-status.cs", "void FileStatus()", 1, null) { IsTest = true, TestLifecycle = true },
            new JulieDbFixture.SymbolRow("export-diagnostic", "Diagnostic", "method", "csharp",
                "c-diagnostic.cs", "void Diagnostic()", 1, null) { TestContainer = true },
            new JulieDbFixture.SymbolRow("export-combined", "Combined", "method", "csharp",
                "d-combined.cs", "void Combined()", 1, null)
                { IsTest = true, TestContainer = true, TestLifecycle = true },
            new JulieDbFixture.SymbolRow("export-unavailable", "Unavailable", "method", "csharp",
                "e-unavailable.cs", "void Unavailable()", 1, null) { IsTest = true },
        });
        ExecuteWrite(fx.DbPath, """
            UPDATE files
            SET status = 'failed_preserved'
            WHERE path IN ('b-file-status.cs', 'd-combined.cs');

            INSERT INTO parse_diagnostics
                (diagnostic_id, file_id, path, language, kind, message, start_line, start_column,
                 end_line, end_column, start_byte, end_byte, metadata_json)
            VALUES
                ('diag-export-1', 'file:c-diagnostic.cs', 'c-diagnostic.cs', 'csharp', 'parse_error',
                 'diagnostic-only', 1, 1, 1, 1, 0, 1, NULL),
                ('diag-export-2', 'file:d-combined.cs', 'd-combined.cs', 'csharp', 'parse_error',
                 'combined', 1, 1, 1, 1, 0, 1, NULL);

            DELETE FROM files WHERE path = 'e-unavailable.cs';
            """);

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

    private static void ExecuteWrite(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
