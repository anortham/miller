using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the workspace health aggregate reader against the julie-extract SQLite artifact.
/// Fast suite only: temp SQLite fixture, no julie-extract subprocess.
/// </summary>
public sealed class WorkspaceHealthReaderTests
{
    [Fact]
    public void Read_GroupsParseDiagnosticsCapabilityGapsCapabilitiesAndFiles()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow("sym-a", "A", "class", "csharp",
                    "src/A.cs", "public class A", 1, null),
                new JulieDbFixture.SymbolRow("sym-b", "B", "function", "typescript",
                    "src/B.ts", "export function B()", 1, null),
            },
            fileContent: new Dictionary<string, string>
            {
                ["src/A.cs"] = "public class A {}\n",
                ["src/B.ts"] = "export function B() {}\n",
            });
        SeedHealthRows(fx.DbPath);

        WorkspaceExtractionHealthFacts facts = WorkspaceHealthReader.Read(fx.DbPath);

        Assert.True(facts.ParseDiagnostics.Available);
        ParseDiagnosticGroup parse = Assert.Single(facts.ParseDiagnostics.Rows);
        Assert.Equal("csharp", parse.Language);
        Assert.Equal("parse_error", parse.Kind);
        Assert.Equal(2, parse.Count);

        Assert.True(facts.CapabilityGaps.Available);
        CapabilityGapGroup gap = Assert.Single(facts.CapabilityGaps.Rows);
        Assert.Equal("typescript", gap.Language);
        Assert.Equal("relationships", gap.Capability);
        Assert.Equal("open", gap.Status);
        Assert.Equal(1, gap.Count);

        Assert.True(facts.LanguageCapabilities.Available);
        LanguageCapabilitySummary capability = Assert.Single(facts.LanguageCapabilities.Rows);
        Assert.Equal("csharp", capability.Language);
        Assert.Equal(8, capability.TargetSymbols);
        Assert.Equal(7, capability.ActualSymbols);
        Assert.Equal(3, capability.TargetRelationships);
        Assert.Equal(2, capability.ActualRelationships);
        Assert.Equal(1, capability.TargetPendingRelationships);
        Assert.Equal(1, capability.ActualPendingRelationships);
        Assert.Equal(6, capability.TargetIdentifiers);
        Assert.Equal(5, capability.ActualIdentifiers);
        Assert.Equal(2, capability.TargetTypes);
        Assert.Equal(1, capability.ActualTypes);

        Assert.True(facts.Files.Available);
        Assert.Contains(facts.Files.Rows, row =>
            row.Language == "csharp" && row.Status == "indexed" && row.Count == 1);
        Assert.Contains(facts.Files.Rows, row =>
            row.Language == "typescript" && row.Status == "indexed" && row.Count == 1);
    }

    [Fact]
    public void Read_MissingOptionalTables_ReportUnavailableSections()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>());
        Exec(fx.DbPath, "DROP TABLE language_capability_gaps;");

        WorkspaceExtractionHealthFacts facts = WorkspaceHealthReader.Read(fx.DbPath);

        Assert.False(facts.CapabilityGaps.Available);
        Assert.Empty(facts.CapabilityGaps.Rows);
        Assert.Contains("language_capability_gaps", facts.CapabilityGaps.Error, StringComparison.Ordinal);
        Assert.True(facts.ParseDiagnostics.Available);
    }

    private static void Exec(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        Exec(connection, sql);
    }

    private static void SeedHealthRows(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();

        Exec(connection, "UPDATE files SET language = 'typescript' WHERE path = 'src/B.ts';");
        Exec(connection, """
            INSERT INTO parse_diagnostics
                (diagnostic_id, file_id, path, language, kind, message, start_line, start_column,
                 end_line, end_column, start_byte, end_byte, metadata_json)
            VALUES
                ('diag-1', 'file:src/A.cs', 'src/A.cs', 'csharp', 'parse_error', 'first', 1, 1, 1, 1, 0, 1, NULL),
                ('diag-2', 'file:src/A.cs', 'src/A.cs', 'csharp', 'parse_error', 'second', 2, 1, 2, 1, 2, 3, NULL);
            """);
        Exec(connection, """
            INSERT INTO language_capability_gaps
                (gap_id, language, capability, status, reason, required_closure, evidence_json)
            VALUES
                ('gap-1', 'typescript', 'relationships', 'open', 'fixture missing', 'fixture', '{}');
            """);
        Exec(connection, """
            INSERT INTO language_capabilities
                (language, parser_package, extensions_json, dependency_status,
                 target_symbols, target_relationships, target_pending_relationships, target_identifiers, target_types,
                 actual_symbols, actual_relationships, actual_pending_relationships, actual_identifiers, actual_types,
                 kind_coverage_json)
            VALUES
                ('csharp', 'tree-sitter-c-sharp', '[".cs"]', 'ready',
                 8, 3, 1, 6, 2,
                 7, 2, 1, 5, 1,
                 '{}');
            """);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
