using Miller.Indexing;
using Miller.Indexing.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Miller.Tests.Testing.FactAdapter;

[Trait("Category", "Scale")]
public sealed class CtFactAdapterScaleTests
{
    [Fact]
    public void RealExtract_RoundTripsEveryObservedRoleAcrossLanguages()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        string work = Path.Combine(Path.GetTempPath(), "miller-ct-fact-scale-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string db = Path.Combine(work, ".miller", "symbols.db");
        Directory.CreateDirectory(Path.Combine(repo, "tests"));
        try
        {
            File.WriteAllText(Path.Combine(repo, "tests", "Roles.razor"), """
                @code {
                    public sealed class TestRoles {
                        [Fact] public void Case() {}
                        public void Control() {}
                    }
                }
                """);
            File.WriteAllText(Path.Combine(repo, "tests", "Roles.kt"), """
                import org.junit.jupiter.api.AfterEach
                import org.junit.jupiter.api.BeforeEach
                import org.junit.jupiter.api.Nested
                import org.junit.jupiter.api.Test

                class RolesTest {
                    @BeforeEach
                    fun setUp() {
                    }

                    @AfterEach
                    fun tearDown() {
                    }

                    @Test
                    fun caseMethod() {
                    }

                    @Nested
                    class NestedRoles {
                        @Test
                        fun nestedCase() {
                        }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(repo, "tests", "Roles.vue"), """
                <script>
                describe("roles", () => {
                  beforeEach(() => {});
                  it("case", () => {});
                });
                </script>
                """);

            ExtractReport report = new JulieExtractRunner(binary).Scan(repo, db, force: true);
            Assert.NotEqual("failed", report.Status);

            IReadOnlyList<IndexedSymbol> indexed = SqliteSymbolReader.Read(db);
            Assert.True(indexed.Select(symbol => symbol.Language).Distinct(StringComparer.Ordinal).Count() >= 3);
            Assert.Contains(indexed, static symbol => symbol.TestEvidence.IsCase);
            Assert.Contains(indexed, static symbol => symbol.TestEvidence.IsContainer);
            Assert.Contains(indexed, static symbol => symbol.TestEvidence.IsLifecycle);

            using var adapter = CtFactAdapter.OpenArtifact(db);
            string[] paths = indexed.Select(static symbol => symbol.FilePath).Distinct(StringComparer.Ordinal).ToArray();
            IReadOnlyList<CtSymbolFact> facts = adapter.SymbolsForChangedFiles(paths);
            Assert.Equal(indexed.Count, facts.Count);

            foreach (IGrouping<string, IndexedSymbol> language in indexed.GroupBy(
                         static symbol => symbol.Language,
                         StringComparer.Ordinal))
            {
                foreach (IndexedSymbol symbol in language)
                {
                    CtSymbolFact fact = Assert.Single(facts, row => row.SymbolId == symbol.SymbolId);
                    Assert.Equal(symbol.IsTest, fact.IsTest);
                    Assert.Equal(symbol.TestEvidence.IsCase, fact.TestCase);
                    Assert.Equal(symbol.TestEvidence.IsContainer, fact.TestContainer);
                    Assert.Equal(symbol.TestEvidence.IsLifecycle, fact.TestLifecycle);
                    Assert.Equal(symbol.TestEvidence.Status, fact.TestEvidenceStatus);
                    Assert.Equal(symbol.TestEvidence.Reason, fact.TestEvidenceReason);
                }
            }

            IReadOnlyList<CtFileFact> fileFacts = adapter.FileFactsForPaths(paths);
            Assert.Equal(paths.Length, fileFacts.Count);
            foreach (CtFileFact file in fileFacts)
            {
                IndexedSymbol symbol = indexed.First(row => row.FilePath == file.Path);
                CtSymbolFact fact = facts.First(row => row.FilePath == file.Path);
                Assert.True(file.EvidenceAvailable);
                Assert.Equal(symbol.Language, file.Language);
                Assert.Equal(fact.ContentHash, file.ContentHash);
                Assert.Equal("indexed", file.Status);
                Assert.False(file.HasParseDiagnostics);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
    }
}
