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
/// <see cref="IndexRebuilder.Rebuild"/> (read + build — what <c>FreshnessService</c> actually runs each swap),
/// asserts a generous budget so it fails loudly only on a true regression, and emits the ms via the test
/// output. <c>[Trait("Category","Scale")]</c>, EXCLUDED by default (the large fixture build exceeds the &lt;10s
/// default budget).
/// </summary>
[Trait("Category", "Scale")]
public sealed class RebuildLatencyTests
{
    private readonly ITestOutputHelper _output;

    public RebuildLatencyTests(ITestOutputHelper output) => _output = output;

    // A realistic mid/large repo symbol count. julie reports ~565k for very large monorepos; 50k is a solid
    // single-product repo and keeps the synthesized-DB build time bounded for a Scale test.
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

        // Warm up the JIT + allocator so the measured number reflects steady-state, not first-call cost.
        _ = MillerRepositoryIndex.Build(symbols);

        var sw = Stopwatch.StartNew();
        var index = MillerRepositoryIndex.Build(symbols);
        sw.Stop();
        long buildMs = sw.ElapsedMilliseconds;

        Assert.Equal(SymbolCount, index.DocumentCount);

        _output.WriteLine(
            $"[RebuildLatency] MillerRepositoryIndex.Build({SymbolCount:N0} symbols) = {buildMs} ms " +
            $"({(buildMs == 0 ? double.PositiveInfinity : SymbolCount / (double)buildMs):N0} symbols/ms)");

        // A generous budget: a full in-memory rebuild of 50k symbols should be well under a second on any dev
        // machine. This fails ONLY on a real regression, leaving the printed number as the actual decision input.
        Assert.True(buildMs < 5_000,
            $"Build of {SymbolCount} symbols took {buildMs} ms (budget 5000 ms) — investigate before shipping.");
    }

    [Fact]
    public void Measure_FullRebuild_ReadPlusBuild_FromADb_PrintsTheLatency()
    {
        // The production swap path: SqliteSymbolReader.Read + Build. Use the synthesized large set written to a
        // real DB so the read cost (the part the in-memory-only build measurement omits) is included.
        var symbols = SynthesizeSymbols(SymbolCount);
        string dir = Path.Combine(Path.GetTempPath(), "miller-rebuildlat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "symbols.db");

        try
        {
            LargeDbWriter.Write(db, symbols);
            var rebuilder = new IndexRebuilder(db);

            _ = rebuilder.Rebuild(); // warm up

            var sw = Stopwatch.StartNew();
            var index = rebuilder.Rebuild();
            sw.Stop();
            long rebuildMs = sw.ElapsedMilliseconds;

            Assert.Equal(SymbolCount, index.DocumentCount);

            _output.WriteLine(
                $"[RebuildLatency] IndexRebuilder.Rebuild (read+build, {SymbolCount:N0} symbols) = {rebuildMs} ms");

            // Generous budget covering disk read + build for 50k symbols.
            Assert.True(rebuildMs < 10_000,
                $"Full rebuild of {SymbolCount} symbols took {rebuildMs} ms (budget 10000 ms) — investigate.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
