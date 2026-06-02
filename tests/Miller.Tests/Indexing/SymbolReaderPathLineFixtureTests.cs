using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// B3 v1 lock: pins that <see cref="SqliteSymbolReader"/> reads the v1 <c>symbols.path</c> and
/// <c>symbols.start_line</c> columns into <see cref="IndexedSymbol.FilePath"/> and
/// <see cref="IndexedSymbol.StartLine"/> EXACTLY — the "path:line" coordinate every search/inspect hit renders.
/// The pre-v1 column was <c>file_path</c>; a reader that still selected it (or crossed path with another string
/// column under positional reads) would render the wrong file or a 0 line. This asserts the precise
/// <c>path:line</c> pair per known row, so a column rename/reorder cannot silently corrupt the coordinate.
/// Fast suite (no julie-extract binary).
/// </summary>
public sealed class SymbolReaderPathLineFixtureTests
{
    [Fact]
    public void Read_ProjectsExactPathAndLine_PerKnownRow()
    {
        // Distinct (path, line) coordinates across languages, including a row with a NULL start_line (→ 0).
        var rows = new[]
        {
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000001", "Alpha", "class", "csharp",
                "src/app/Alpha.cs", "public class Alpha", 12, null),
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000002", "beta", "function", "typescript",
                "web/components/beta.ts", "function beta()", 3, null),
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000003", "Gamma", "struct", "rust",
                "core/gamma.rs", "pub struct Gamma", 99, null),
            // NULL start_line → the reader's nullable-INTEGER discipline maps it to 0.
            new JulieDbFixture.SymbolRow("a0000000000000000000000000000004", "DELTA", "constant", "typescript",
                "web/components/beta.ts", "const DELTA = 1", null, null),
        };
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows);

        var symbols = SqliteSymbolReader.Read(fx.DbPath);

        // The exact path:line coordinate per row — the load-bearing rendering input.
        AssertCoordinate(symbols, "Alpha", "src/app/Alpha.cs", 12);
        AssertCoordinate(symbols, "beta", "web/components/beta.ts", 3);
        AssertCoordinate(symbols, "Gamma", "core/gamma.rs", 99);
        AssertCoordinate(symbols, "DELTA", "web/components/beta.ts", 0); // NULL start_line → 0
    }

    private static void AssertCoordinate(
        IReadOnlyList<IndexedSymbol> symbols, string name, string expectedPath, int expectedLine)
    {
        var sym = symbols.Single(s => s.Name == name);
        Assert.Equal(expectedPath, sym.FilePath);
        Assert.Equal(expectedLine, sym.StartLine);
    }
}
