using System.Diagnostics;
using System.Numerics.Tensors;

namespace Codesearch.Spike;

/// <summary>
/// Stopwatch micro-benchmarks (warmup + repetitions + per-iteration allocation tracking, with an
/// anti-dead-code-elimination sink). Not BenchmarkDotNet-rigorous, but enough to SEE the shape:
///   1) TensorPrimitives.CosineSimilarity (SIMD) vs a scalar loop, over repo-scale vector counts.
///   2) The span tokenizer vs a Regex/Split baseline, on REAL identifiers from the extracted DB.
/// </summary>
public static class Bench
{
    private static double _doubleSink;
    private static long _longSink;

    public static void Run(string dbPath)
    {
        Console.WriteLine("== benchmarks ==\n");

        DemoTokenizer();
        CosineBench();
        TokenizeBench(dbPath);

        // Defeat dead-code elimination.
        Console.WriteLine($"\n(sinks: {_doubleSink:F3}, {_longSink})");
    }

    private static void DemoTokenizer()
    {
        Console.WriteLine("  tokenizer sanity (input -> tokens):");
        foreach (var s in new[] { "getHTTPResponseCode", "IUserService", "parse_http_header", "Vector512", "EmbedBatchAsync" })
        {
            var toks = new List<string>();
            CodeTokenizer.Tokenize(s, toks);
            Console.WriteLine($"    {s,-22} -> [{string.Join(", ", toks)}]");
        }
        Console.WriteLine();
    }

    private static void CosineBench()
    {
        const int dim = 768; // nomic-embed-text-v1.5
        foreach (int n in new[] { 10_000, 100_000 })
        {
            var rng = new Random(42);
            var query = RandomUnit(rng, dim);
            var vecs = new float[n][];
            for (int i = 0; i < n; i++) vecs[i] = RandomUnit(rng, dim);

            var simd = Measure(warmup: 3, iters: 10, _ =>
            {
                double acc = 0;
                for (int i = 0; i < n; i++) acc += TensorPrimitives.CosineSimilarity(query, vecs[i]);
                _doubleSink += acc;
            });

            var scalar = Measure(warmup: 2, iters: 5, _ =>
            {
                double acc = 0;
                for (int i = 0; i < n; i++) acc += ScalarCosine(query, vecs[i]);
                _doubleSink += acc;
            });

            Console.WriteLine($"  cosine scan, {n,7:N0} vectors x {dim}d:");
            Console.WriteLine($"    TensorPrimitives (SIMD)  {simd.MedianMs,8:F3} ms   ({n / (simd.MedianMs / 1000.0),12:N0} vec/s)");
            Console.WriteLine($"    scalar loop              {scalar.MedianMs,8:F3} ms   ({n / (scalar.MedianMs / 1000.0),12:N0} vec/s)");
            Console.WriteLine($"    speedup                  {scalar.MedianMs / simd.MedianMs,8:F1}x\n");
        }
    }

    private static void TokenizeBench(string dbPath)
    {
        if (!File.Exists(dbPath)) { Console.WriteLine("  (skipping tokenize bench: no db)\n"); return; }
        string[] names = ContractCheck.LoadIdentifierNames(dbPath);
        Console.WriteLine($"  tokenizing {names.Length:N0} real identifiers from the extracted DB:");

        var buf = new List<string>(8);
        var span = Measure(warmup: 3, iters: 20, _ =>
        {
            long t = 0;
            foreach (var name in names) { buf.Clear(); CodeTokenizer.Tokenize(name, buf); t += buf.Count; }
            _longSink += t;
        });

        var naive = Measure(warmup: 2, iters: 10, _ =>
        {
            long t = 0;
            foreach (var name in names) t += CodeTokenizer.TokenizeNaive(name).Count;
            _longSink += t;
        });

        Console.WriteLine($"    span tokenizer   {span.MedianMs,8:F3} ms   {span.AllocKB,10:N0} KB/run   ({names.Length / (span.MedianMs / 1000.0),12:N0} ident/s)");
        Console.WriteLine($"    regex/split      {naive.MedianMs,8:F3} ms   {naive.AllocKB,10:N0} KB/run   ({names.Length / (naive.MedianMs / 1000.0),12:N0} ident/s)");
        Console.WriteLine($"    speedup          {naive.MedianMs / span.MedianMs,8:F1}x   alloc reduction {(naive.AllocKB <= 0 ? 0 : (double)naive.AllocKB / Math.Max(1, span.AllocKB)),6:F1}x\n");
    }

    // ---- helpers ----

    private readonly record struct Result(double MedianMs, double MinMs, long AllocKB);

    private static Result Measure(int warmup, int iters, Action<int> body)
    {
        for (int i = 0; i < warmup; i++) body(i);
        var times = new double[iters];
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = new Stopwatch();
        for (int i = 0; i < iters; i++)
        {
            sw.Restart();
            body(i);
            sw.Stop();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        Array.Sort(times);
        return new Result(times[iters / 2], times[0], (allocAfter - allocBefore) / iters / 1024);
    }

    private static float[] RandomUnit(Random rng, int dim)
    {
        var v = new float[dim];
        double norm = 0;
        for (int i = 0; i < dim; i++) { v[i] = (float)(rng.NextDouble() * 2 - 1); norm += v[i] * v[i]; }
        float inv = (float)(1.0 / Math.Sqrt(norm));
        for (int i = 0; i < dim; i++) v[i] *= inv;
        return v;
    }

    private static float ScalarCosine(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }
}
