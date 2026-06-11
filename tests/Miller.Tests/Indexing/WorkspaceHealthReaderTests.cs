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
    public void CreateDefault_UsesPinnedV3ExtractionFactTables()
    {
        using var fx = JulieDbFixture.CreateDefault();

        Assert.True(TableExists(fx.DbPath, "structural_facts"));
        Assert.True(TableExists(fx.DbPath, "complexity_metrics"));
    }

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

        Assert.Equal(2, capability.KindCoverage.Count);
        KindCoverageDomain docComments = capability.KindCoverage[0];
        Assert.Equal("doc_comments", docComments.Domain);
        Assert.Equal(["method"], docComments.Supported);
        Assert.Equal(["property"], docComments.OpenGaps);
        Assert.Empty(docComments.NotApplicable);
        KindCoverageDomain symbols = capability.KindCoverage[1];
        Assert.Equal("symbols", symbols.Domain);
        Assert.Equal(["class", "method"], symbols.Supported);
        Assert.Empty(symbols.OpenGaps);
        Assert.Equal(["import"], symbols.NotApplicable);

        Assert.True(facts.Files.Available);
        Assert.Contains(facts.Files.Rows, row =>
            row.Language == "csharp" && row.Status == "indexed" && row.Count == 1);
        Assert.Contains(facts.Files.Rows, row =>
            row.Language == "typescript" && row.Status == "indexed" && row.Count == 1);

        object structuralSection = RequiredSection(facts, "StructuralFacts");
        Assert.True(SectionAvailable(structuralSection));
        object structural = SingleSectionRow(structuralSection);
        Assert.Equal("typescript", RowProperty<string>(structural, "Language"));
        Assert.Equal("typescript.await_expression.v1", RowProperty<string>(structural, "PatternId"));
        Assert.Equal("await_expression", RowProperty<string>(structural, "CaptureName"));
        Assert.Equal(2, RowProperty<long>(structural, "Count"));

        object complexitySection = RequiredSection(facts, "ComplexityMetrics");
        Assert.True(SectionAvailable(complexitySection));
        object complexity = SingleSectionRow(complexitySection);
        Assert.Equal("typescript", RowProperty<string>(complexity, "Language"));
        Assert.Equal("symbol", RowProperty<string>(complexity, "Scope"));
        Assert.Equal("julie-ast-complexity-v1", RowProperty<string>(complexity, "AlgorithmId"));
        Assert.Equal(1, RowProperty<long>(complexity, "Count"));
        Assert.Equal(3, RowProperty<long>(complexity, "MaxDecisionCount"));
        Assert.Equal(1, RowProperty<long>(complexity, "MaxLoopCount"));
        Assert.Equal(2, RowProperty<long>(complexity, "MaxNestingDepth"));
        Assert.Equal(4, RowProperty<long?>(complexity, "MaxParameterCount"));
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

    private static bool TableExists(string dbPath, string tableName)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        object? result = command.ExecuteScalar();
        return result is not null and not DBNull;
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
                 '{"symbols":{"supported":["class","method"],"open_gaps":[],"not_applicable":["import"]},"doc_comments":{"supported":["method"],"open_gaps":["property"],"not_applicable":[]}}');
            """);
        Exec(connection, """
            INSERT INTO structural_facts
                (structural_fact_id, file_id, path, language, pattern_id, capture_name, node_kind,
                 containing_symbol_id, start_line, start_column, end_line, end_column, start_byte, end_byte,
                 confidence, metadata_json)
            VALUES
                ('struct-1', 'file:src/B.ts', 'src/B.ts', 'typescript', 'typescript.await_expression.v1',
                 'await_expression', 'await_expression', 'sym-b', 1, 1, 1, 6, 0, 5, 1.0,
                 '{"pattern_version":1,"query_family":"async"}'),
                ('struct-2', 'file:src/B.ts', 'src/B.ts', 'typescript', 'typescript.await_expression.v1',
                 'await_expression', 'await_expression', 'sym-b', 2, 1, 2, 6, 6, 11, 1.0,
                 '{"pattern_version":1,"query_family":"async"}');
            """);
        Exec(connection, """
            INSERT INTO complexity_metrics
                (complexity_metric_id, file_id, path, language, scope, symbol_id, algorithm_id, covered_lines,
                 covered_bytes, decision_count, loop_count, max_nesting_depth, parameter_count, start_line,
                 start_column, end_line, end_column, start_byte, end_byte, metadata_json)
            VALUES
                ('complexity-1', 'file:src/B.ts', 'src/B.ts', 'typescript', 'symbol', 'sym-b',
                 'julie-ast-complexity-v1', 5, 120, 3, 1, 2, 4, 1, 0, 5, 1, 0, 120, NULL);
            """);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object RequiredSection(WorkspaceExtractionHealthFacts facts, string propertyName)
    {
        var property = typeof(WorkspaceExtractionHealthFacts).GetProperty(propertyName);
        Assert.NotNull(property);
        object? section = property!.GetValue(facts);
        Assert.NotNull(section);
        return section!;
    }

    private static bool SectionAvailable(object section)
    {
        object? available = section.GetType().GetProperty("Available")?.GetValue(section);
        return Assert.IsType<bool>(available);
    }

    private static object SingleSectionRow(object section)
    {
        object? rowsValue = section.GetType().GetProperty("Rows")?.GetValue(section);
        var rows = Assert.IsAssignableFrom<System.Collections.IEnumerable>(rowsValue);
        return Assert.Single(rows.Cast<object>());
    }

    private static T RowProperty<T>(object row, string propertyName)
    {
        object? value = row.GetType().GetProperty(propertyName)?.GetValue(row);
        return Assert.IsAssignableFrom<T>(value);
    }
}
