using System.Diagnostics;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Data.Sqlite;

namespace Codesearch.Spike;

/// <summary>
/// The original abandonment question: is local embedding generation fast enough in pure .NET?
/// Embeds real symbol texts via LLamaSharp (llama.cpp) with nomic-embed-text-v1.5 GGUF, comparing
/// GpuLayerCount=99 (Metal on Apple Silicon) vs 0 (CPU). The Jan 51-182s figures were the old ONNX
/// path; this measures the llama.cpp/Metal path that replaced it.
/// </summary>
public static class EmbedBench
{
    public static void Run(string dbPath, int sample)
    {
        string modelPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codesearch", "models", "nomic-embed-text-v1.5", "nomic-embed-text-v1.5.Q8_0.gguf");
        if (!File.Exists(modelPath)) { Console.WriteLine($"model missing: {modelPath}"); return; }
        if (!File.Exists(dbPath)) { Console.WriteLine($"db missing: {dbPath}"); return; }

        string[] texts = LoadSymbolTexts(dbPath, sample);
        Console.WriteLine($"== embedding throughput: {texts.Length:N0} real symbols (nomic-embed-text-v1.5 Q8_0, 768d) ==\n");

        foreach (int gpu in new[] { 99, 0 })
            RunOne(modelPath, texts, gpu);
        Console.WriteLine();
    }

    private static void RunOne(string modelPath, string[] texts, int gpuLayers)
    {
        var mp = new ModelParams(modelPath)
        {
            ContextSize = 2048,
            PoolingType = LLamaPoolingType.Mean,
            GpuLayerCount = gpuLayers,
        };

        var swLoad = Stopwatch.StartNew();
        using var weights = LLamaWeights.LoadFromFile(mp);
        using var embedder = new LLamaEmbedder(weights, mp);
        swLoad.Stop();

        // warmup (also triggers first-run Metal shader compile)
        _ = embedder.GetEmbeddings("search_document: warmup").GetAwaiter().GetResult();

        int dim = 0;
        double sink = 0;
        var sw = Stopwatch.StartNew();
        foreach (var t in texts)
        {
            var e = embedder.GetEmbeddings("search_document: " + t).GetAwaiter().GetResult();
            if (e.Count > 0) { dim = e[0].Length; sink += e[0][0]; }
        }
        sw.Stop();

        double secs = sw.Elapsed.TotalSeconds;
        string mode = gpuLayers > 0 ? "Metal(GPU)" : "CPU";
        Console.WriteLine(
            $"  {mode,-11} load {swLoad.Elapsed.TotalSeconds,5:F1}s | " +
            $"embed {texts.Length:N0} in {secs,7:F2}s = {texts.Length / secs,8:N1} sym/s | " +
            $"{secs * 1000 / texts.Length,6:F2} ms/sym | dim {dim} | sink {sink:F2}");
    }

    private static string[] LoadSymbolTexts(string dbPath, int limit)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT kind || ' ' || name || ' ' || COALESCE(signature,'') FROM symbols WHERE name IS NOT NULL LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<string>(limit);
        while (r.Read())
        {
            var s = r.GetString(0);
            if (s.Length > 512) s = s[..512];
            list.Add(s);
        }
        return list.ToArray();
    }
}
