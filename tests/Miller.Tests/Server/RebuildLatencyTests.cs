using System.Diagnostics;
using Miller.Indexing;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The decision-5 data point (m3-design §Test strategy, Scale): measure how long a FULL index rebuild takes on
/// a large symbol set, and PRINT the number, so the "incremental rebuild?" decision is driven by data, not a
/// guess. M3 ships full-rebuild-on-swap; if this latency is comfortable at a realistic repo size, incremental
/// (which would break the frozen/dense-DocId invariant) is NOT taken speculatively. The test measures both the
/// pure <see cref="MillerRepositoryIndex.Build"/> (the in-memory work the swap does) and the full
/// <see cref="IndexRebuilder.Rebuild"/> (read + build — what <c>FreshnessService</c> actually runs each swap)
/// and emits the measured milliseconds via the test output. <c>[Trait("Category","Scale")]</c>, EXCLUDED by
/// default because it builds a large fixture.
/// </summary>
[Trait("Category", "Scale")]
public sealed class RebuildLatencyTests
{
    private readonly ITestOutputHelper _output;

    public RebuildLatencyTests(ITestOutputHelper output) => _output = output;

    private const int SymbolCount = 50_000;

    private static IReadOnlyList<IndexedSymbol> SynthesizeSymbols(int count)
    {
        var symbols = new IndexedSymbol[count];
        string[] kinds = { "class", "method", "function", "struct", "interface", "field" };
        string[] langs = { "csharp", "rust", "typescript", "go", "python" };
        for (int i = 0; i < count; i++)
        {
            symbols[i] = new IndexedSymbol(
                DocId: i,
                SymbolId: i.ToString("x32").PadLeft(32, '0')[..32],
                Name: $"Symbol_{i}_DoWorkHandler",
                Signature: $"public void Symbol_{i}_DoWorkHandler(int arg{i}, string name)",
                Kind: kinds[i % kinds.Length],
                Language: langs[i % langs.Length],
                FilePath: $"src/module{i % 200}/File{i % 1000}.cs",
                StartLine: (i % 500) + 1,
                EndLine: (i % 500) + 5,
                ParentId: i % 7 == 0 ? null : (i - 1).ToString("x32").PadLeft(32, '0')[..32],
                IsTest: false);
        }
        return symbols;
    }

    [Fact]
    public void Measure_FullIndexBuild_OnALargeSymbolSet_PrintsTheLatency()
    {
        var symbols = SynthesizeSymbols(SymbolCount);

        _ = MillerRepositoryIndex.Build(symbols);

        var sw = Stopwatch.StartNew();
        var index = MillerRepositoryIndex.Build(symbols);
        sw.Stop();
        long buildMs = sw.ElapsedMilliseconds;

        Assert.Equal(SymbolCount, index.DocumentCount);

        _output.WriteLine(
            $"[RebuildLatency] MillerRepositoryIndex.Build({SymbolCount:N0} symbols) = {buildMs} ms " +
            $"({(buildMs == 0 ? double.PositiveInfinity : SymbolCount / (double)buildMs):N0} symbols/ms)");

    }

    [Fact]
    public void Measure_FullRebuild_ReadPlusBuild_FromADb_PrintsTheLatency()
    {
        var symbols = SynthesizeSymbols(SymbolCount);
        string dir = Path.Combine(Path.GetTempPath(), "miller-rebuildlat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "symbols.db");

        try
        {
            LargeDbWriter.Write(db, symbols);
            var rebuilder = new IndexRebuilder(db);

            _ = rebuilder.Rebuild();

            var sw = Stopwatch.StartNew();
            var index = rebuilder.Rebuild();
            sw.Stop();
            long rebuildMs = sw.ElapsedMilliseconds;

            Assert.Equal(SymbolCount, index.DocumentCount);

            _output.WriteLine(
                $"[RebuildLatency] IndexRebuilder.Rebuild (read+build, {SymbolCount:N0} symbols) = {rebuildMs} ms");

        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
