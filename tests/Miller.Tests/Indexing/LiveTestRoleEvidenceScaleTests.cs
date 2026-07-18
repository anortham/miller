using System.Text.Json;
using Miller.Core.Graph;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Live v2.15 test-role proof over the Razor, Vue, and Kotlin regression shapes published by julie-extractors.
/// This is positive candidate evidence only; zero rows or false flags are never treated as completeness proof.
/// </summary>
[Trait("Category", "Scale")]
public sealed class LiveTestRoleEvidenceScaleTests
{
    private readonly ITestOutputHelper _output;

    public LiveTestRoleEvidenceScaleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void LiveExtract_RazorVueAndKotlinRoles_RoundTripThroughReaderExportAndImpact()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        Assert.Equal(
            $"julie-extract {MillerExtractContract.PinnedJulieExtractVersion}",
            ScaleTestSupport.RunJulie(binary, "--version").Trim());

        string work = Path.Combine(Path.GetTempPath(), "miller-live-test-roles-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string db = Path.Combine(work, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.Combine(repo, "tests"));
        try
        {
            File.WriteAllText(Path.Combine(repo, "tests", "RazorRoles.razor"), """
                @code {
                    public sealed class RazorTestRoles {
                        [Fact] public void RazorCase() {}
                        public void Fact() {}
                    }
                }
                """);
            File.WriteAllText(Path.Combine(repo, "tests", "KotlinRoles.kt"), """
                import org.junit.jupiter.api.AfterAll
                import org.junit.jupiter.api.AfterEach
                import org.junit.jupiter.api.BeforeAll
                import org.junit.jupiter.api.BeforeEach
                import org.junit.jupiter.api.Nested
                import org.junit.jupiter.api.Test

                class CalculatorTest {
                    @BeforeAll
                    fun setUpAll() {
                    }

                    @BeforeEach
                    fun setUp() {
                    }

                    @AfterEach
                    fun tearDown() {
                    }

                    @AfterAll
                    fun tearDownAll() {
                    }

                    @Test
                    fun addsTwoNumbers() {
                    }

                    @Nested
                    class WhenNegative {
                        @Test
                        fun rejectsNegativeInput() {
                        }
                    }

                    fun helper() {
                    }
                }

                class OrdinaryHelper {
                    fun helper() {
                    }
                }
                """);
            File.WriteAllText(Path.Combine(repo, "tests", "Options.vue"), """
                <script>
                suite("vue options suite", () => {
                  afterAll(() => {});
                  it("renders options", () => {});
                });
                const ordinary = { it(name, fn) { fn(); } };
                ordinary.it("ordinary member call", () => {});
                </script>
                """);
            File.WriteAllText(Path.Combine(repo, "tests", "Setup.vue"), """
                <script setup lang="ts">
                describe("vue embedded roles", () => {
                  beforeEach(() => {});
                  test("renders a Vue test case", () => {});
                });
                function testNamedButOrdinary(): void {}
                const ordinary = { test(name: string, fn: () => void) { fn(); } };
                ordinary.test("ordinary member call", () => {});
                </script>
                """);

            var runner = new JulieExtractRunner(binary);
            ExtractReport report = runner.Scan(repo, db, force: true);
            Assert.NotEqual("failed", report.Status);

            IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.Read(db);
            IndexedSymbol razorCase = Assert.Single(symbols,
                symbol => symbol.FilePath == "tests/RazorRoles.razor" && symbol.Name == "RazorCase");
            IndexedSymbol razorControl = Assert.Single(symbols,
                symbol => symbol.FilePath == "tests/RazorRoles.razor" && symbol.Name == "Fact");
            Assert.True(razorCase.TestEvidence.IsCase);
            Assert.False(razorControl.TestEvidence.IsTest);

            AssertVueRoles(symbols, "tests/Options.vue");
            AssertVueRoles(symbols, "tests/Setup.vue");
            IndexedSymbol namedControl = Assert.Single(symbols,
                symbol => symbol.FilePath == "tests/Setup.vue" && symbol.Name == "testNamedButOrdinary");
            Assert.False(namedControl.TestEvidence.IsTest);
            (IndexedSymbol kotlinCase, IndexedSymbol kotlinControl) = AssertKotlinRoles(symbols);

            Dictionary<string, (long Cases, long Containers, long Lifecycles)> counts = ReadRoleCounts(db);
            Assert.Contains("kotlin", counts.Keys);
            Assert.Contains("razor", counts.Keys);
            Assert.Contains("vue", counts.Keys);
            Assert.Equal(2, counts["kotlin"].Cases);
            Assert.Equal(2, counts["kotlin"].Containers);
            Assert.Equal(4, counts["kotlin"].Lifecycles);
            Assert.True(counts["razor"].Cases >= 1);
            Assert.True(counts["vue"].Cases >= 2);
            Assert.True(counts["vue"].Containers >= 2);
            Assert.True(counts["vue"].Lifecycles >= 2);
            foreach ((string language, var roleCounts) in counts)
                _output.WriteLine(
                    "language={0} test_cases={1} test_containers={2} test_lifecycles={3}",
                    language, roleCounts.Cases, roleCounts.Containers, roleCounts.Lifecycles);
            _output.WriteLine(
                "controls: RazorRoles.razor::Fact is_test={0}; Setup.vue::testNamedButOrdinary is_test={1}; "
                + "KotlinRoles.kt::OrdinaryHelper.helper is_test={2}",
                razorControl.IsTest, namedControl.IsTest, kotlinControl.IsTest);

            Dictionary<string, JsonElement> exported = SymbolExportReader.ExportJsonLines(db)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => JsonDocument.Parse(line).RootElement.Clone())
                .ToDictionary(static row => row.GetProperty("symbol_id").GetString()!, StringComparer.Ordinal);
            foreach (IndexedSymbol symbol in symbols)
                AssertExportMatches(symbol, exported[symbol.SymbolId]);

            var impactSymbols = new[] { kotlinControl with { DocId = 0 }, kotlinCase with { DocId = 1 } };
            var impactIndex = MillerRepositoryIndex.Build(
                impactSymbols,
                [new GraphEdge(kotlinCase.SymbolId, kotlinControl.SymbolId, "calls")]);
            string impactJson = ImpactTool.Run(
                impactIndex,
                new SmartTargetResolver(impactIndex),
                target: "helper",
                changedPaths: null,
                diff: null,
                maxDepth: 1,
                limit: 10,
                json: true,
                out _,
                out _);
            using JsonDocument impact = JsonDocument.Parse(impactJson);
            JsonElement reached = Assert.Single(impact.RootElement.GetProperty("tests").EnumerateArray());
            Assert.Equal(kotlinCase.SymbolId, reached.GetProperty("symbol_id").GetString());
            Assert.Equal("current", reached.GetProperty("test_evidence").GetProperty("status").GetString());
            Assert.True(reached.GetProperty("test_evidence").GetProperty("test_case").GetBoolean());
            Assert.Equal("candidate_only",
                impact.RootElement.GetProperty("test_evidence_scope").GetProperty("status").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static (IndexedSymbol Case, IndexedSymbol Control) AssertKotlinRoles(
        IReadOnlyList<IndexedSymbol> symbols)
    {
        const string path = "tests/KotlinRoles.kt";
        IndexedSymbol calculator = Assert.Single(symbols,
            symbol => symbol.FilePath == path && symbol.Name == "CalculatorTest");
        IndexedSymbol nested = Assert.Single(symbols,
            symbol => symbol.FilePath == path && symbol.Name == "WhenNegative");
        Assert.True(calculator.TestEvidence.IsContainer);
        Assert.True(nested.TestEvidence.IsContainer);

        IndexedSymbol testCase = Assert.Single(symbols,
            symbol => symbol.FilePath == path && symbol.Name == "addsTwoNumbers");
        IndexedSymbol nestedCase = Assert.Single(symbols,
            symbol => symbol.FilePath == path && symbol.Name == "rejectsNegativeInput");
        Assert.True(testCase.TestEvidence.IsCase);
        Assert.True(nestedCase.TestEvidence.IsCase);

        foreach (string name in new[] { "setUpAll", "setUp", "tearDown", "tearDownAll" })
        {
            IndexedSymbol lifecycle = Assert.Single(symbols,
                symbol => symbol.FilePath == path && symbol.Name == name);
            Assert.True(lifecycle.TestEvidence.IsTest);
            Assert.True(lifecycle.TestEvidence.IsLifecycle);
            Assert.False(lifecycle.TestEvidence.IsCase);
        }

        IndexedSymbol ordinaryClass = Assert.Single(symbols,
            symbol => symbol.FilePath == path && symbol.Name == "OrdinaryHelper");
        IndexedSymbol control = Assert.Single(symbols,
            symbol => symbol.FilePath == path
                && symbol.Name == "helper"
                && symbol.ParentId == ordinaryClass.SymbolId);
        Assert.False(ordinaryClass.TestEvidence.IsTest);
        Assert.False(ordinaryClass.TestEvidence.IsContainer);
        Assert.False(control.TestEvidence.IsTest);

        return (testCase, control);
    }

    private static void AssertVueRoles(IReadOnlyList<IndexedSymbol> symbols, string path)
    {
        IndexedSymbol[] rows = symbols.Where(symbol => symbol.FilePath == path).ToArray();
        Assert.Contains(rows, static symbol => symbol.TestEvidence.IsCase);
        Assert.Contains(rows, static symbol => symbol.TestEvidence.IsContainer);
        Assert.Contains(rows, static symbol => symbol.TestEvidence.IsLifecycle);
        Assert.DoesNotContain(rows,
            static symbol => symbol.Name == "ordinary member call" && symbol.TestEvidence.IsTest);
    }

    private static Dictionary<string, (long Cases, long Containers, long Lifecycles)> ReadRoleCounts(string db)
    {
        var counts = new Dictionary<string, (long, long, long)>(StringComparer.Ordinal);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT language,
                   SUM(CASE WHEN is_test = 1 AND test_lifecycle = 0 THEN 1 ELSE 0 END),
                   SUM(test_container),
                   SUM(test_lifecycle)
            FROM symbols
            GROUP BY language
            ORDER BY language;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            counts.Add(reader.GetString(0), (reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        return counts;
    }

    private static void AssertExportMatches(IndexedSymbol symbol, JsonElement row)
    {
        Assert.Equal(symbol.TestEvidence.IsTest, row.GetProperty("is_test").GetBoolean());
        Assert.Equal(symbol.TestEvidence.IsCase, row.GetProperty("test_case").GetBoolean());
        Assert.Equal(symbol.TestEvidence.IsContainer, row.GetProperty("test_container").GetBoolean());
        Assert.Equal(symbol.TestEvidence.IsLifecycle, row.GetProperty("test_lifecycle").GetBoolean());
        Assert.Equal(symbol.TestEvidence.Status, row.GetProperty("test_evidence_status").GetString());
        if (symbol.TestEvidence.Reason is null)
            Assert.Equal(JsonValueKind.Null, row.GetProperty("test_evidence_reason").ValueKind);
        else
            Assert.Equal(symbol.TestEvidence.Reason, row.GetProperty("test_evidence_reason").GetString());
    }

}
