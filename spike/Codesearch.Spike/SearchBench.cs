using System.Collections.Frozen;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Codesearch.Spike;

/// <summary>
/// The OTHER abandonment question: the user's earlier "sqlite + sqlite-vec + FTS with pretokenization"
/// attempt produced a HUGE index with POOR query latency vs tantivy/lancedb. This confronts that with
/// real data on the same 565k-symbol corpus, measuring the three things that actually killed it:
///   INDEX SIZE, BUILD TIME, QUERY LATENCY.
///
/// Three realistic pure-stack options, no Rust:
///   1) FTS5 + pretokenized component tokens (span tokenizer) -> idiom-aware ("http" hits getHTTPResponseCode)
///   2) FTS5 + trigram tokenizer over raw names               -> substring match, historically the big index
///   3) In-memory C# inverted index (Dictionary / FrozenDictionary postings) -> zero disk, RAM-resident
/// </summary>
public static class SearchBench
{
    // Representative single-term queries: component words that live INSIDE camelCase/snake identifiers,
    // which is precisely where naive FTS over raw text fails and pretokenization / trigram earn their keep.
    private static readonly string[] Queries =
        ["user", "http", "service", "request", "handle", "create", "async", "token", "parse", "config"];

    public static void Run(string dbPath)
    {
        if (!File.Exists(dbPath)) { Console.WriteLine($"db missing: {dbPath}"); return; }

        var (names, texts) = LoadSymbols(dbPath);
        long sourceBytes = new FileInfo(dbPath).Length;
        Console.WriteLine($"== search index bench: {names.Length:N0} symbols (source extract db {Mb(sourceBytes)}) ==\n");

        Fts5(names, texts, trigram: false);
        Fts5(names, texts, trigram: true);
        InMemory(names, texts);
        Console.WriteLine();
    }

    // ---- 1 & 2: FTS5 on disk (pretokenized component tokens, or trigram over raw names) ----

    private static void Fts5(string[] names, string[] texts, bool trigram)
    {
        string label = trigram ? "FTS5 trigram (raw names)" : "FTS5 pretokenized (component tokens)";
        string path = trigram ? "/tmp/cs-fts-trigram.sqlite" : "/tmp/cs-fts-pretok.sqlite";
        foreach (var ext in new[] { "", "-wal", "-shm", "-journal" })
            File.Delete(path + ext);

        var sw = Stopwatch.StartNew();
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            Exec(conn, "PRAGMA journal_mode=MEMORY; PRAGMA synchronous=OFF;");
            Exec(conn, trigram
                ? "CREATE VIRTUAL TABLE fts USING fts5(body, tokenize='trigram');"
                : "CREATE VIRTUAL TABLE fts USING fts5(body);");

            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO fts(rowid, body) VALUES($id, $b)";
            var pId = cmd.CreateParameter(); pId.ParameterName = "$id"; cmd.Parameters.Add(pId);
            var pB = cmd.CreateParameter(); pB.ParameterName = "$b"; cmd.Parameters.Add(pB);

            var toks = new List<string>(16);
            for (int i = 0; i < names.Length; i++)
            {
                // trigram indexes raw name (substring search); pretok indexes the joined component tokens.
                string body;
                if (trigram)
                {
                    body = names[i];
                }
                else
                {
                    toks.Clear();
                    CodeTokenizer.Tokenize(texts[i], toks);
                    body = string.Join(' ', toks);
                }
                pId.Value = i;
                pB.Value = body;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();

            // Merge segments to the compact, queryable form a real index would ship.
            Exec(conn, "INSERT INTO fts(fts) VALUES('optimize');");
            Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        sw.Stop();
        long size = new FileInfo(path).Length;

        // Query latency (realistic: rank + top-50, the cost a search tool actually pays).
        using var qconn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        qconn.Open();
        var (medMs, totalHits) = MeasureQueries(q =>
        {
            using var c = qconn.CreateCommand();
            c.CommandText = "SELECT rowid FROM fts WHERE fts MATCH $q ORDER BY rank LIMIT 50";
            var p = c.CreateParameter(); p.ParameterName = "$q"; p.Value = q; c.Parameters.Add(p);
            int hits = 0;
            using var r = c.ExecuteReader();
            while (r.Read()) hits++;
            return hits;
        });

        Console.WriteLine($"  {label}");
        Console.WriteLine($"    build {sw.Elapsed.TotalSeconds,6:F2}s | index {Mb(size),9} | {size / (double)(names.Length),5:F0} B/sym | query median {medMs,6:F3} ms | {totalHits} top50-hits/10q\n");
    }

    // ---- 3: in-memory inverted index ----

    private static void InMemory(string[] names, string[] texts)
    {
        // Build token -> postings. Force a clean baseline so the retained-memory delta is the index itself.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long memBefore = GC.GetTotalMemory(forceFullCollection: true);

        var sw = Stopwatch.StartNew();
        var build = new Dictionary<string, List<int>>(1 << 18);
        var docLen = new int[names.Length];                 // BM25 length normalization
        var toks = new List<string>(16);
        for (int i = 0; i < names.Length; i++)
        {
            toks.Clear();
            CodeTokenizer.Tokenize(texts[i], toks);
            docLen[i] = toks.Count;
            // de-dup tokens within a symbol so postings carry each symbol once per term
            for (int k = 0; k < toks.Count; k++)
            {
                bool dup = false;
                for (int j = 0; j < k; j++) if (toks[j] == toks[k]) { dup = true; break; }
                if (dup) continue;
                if (!build.TryGetValue(toks[k], out var list)) { list = new List<int>(2); build[toks[k]] = list; }
                list.Add(i);
            }
        }
        // Freeze to int[] postings + FrozenDictionary: drops List overhead, gives .NET 10's fastest lookup.
        var frozen = build.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        sw.Stop();

        long postings = 0;
        foreach (var v in frozen.Values) postings += v.Length;
        double avgdl = 0;
        for (int i = 0; i < docLen.Length; i++) avgdl += docLen[i];
        avgdl /= docLen.Length;

        // Drop the transient builder, then measure what the queryable index actually retains.
        build.Clear();
        build = null!;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long memAfter = GC.GetTotalMemory(forceFullCollection: true);
        long retained = Math.Max(0, memAfter - memBefore);

        // Honest comparison vs FTS5's "ORDER BY rank LIMIT 50": full BM25 scoring + bounded top-50.
        const double k1 = 1.2, b = 0.75;
        int n = names.Length;
        var topScore = new double[50];
        var topId = new int[50];
        var (medMs, totalHits) = MeasureQueries(q =>
        {
            if (!frozen.TryGetValue(q, out var p)) return 0;
            double idf = Math.Log(1.0 + (n - p.Length + 0.5) / (p.Length + 0.5));
            int held = 0;            // bounded min-tracking top-50
            double worst = double.NegativeInfinity;
            int worstAt = 0;
            for (int pi = 0; pi < p.Length; pi++)
            {
                int id = p[pi];
                double score = idf * (k1 + 1) / (1 + k1 * (1 - b + b * docLen[id] / avgdl)); // tf=1
                if (held < 50)
                {
                    topScore[held] = score; topId[held] = id;
                    if (held == 0 || score < worst) { worst = score; worstAt = held; }
                    held++;
                    if (held == 50) { worst = topScore[0]; worstAt = 0; for (int t = 1; t < 50; t++) if (topScore[t] < worst) { worst = topScore[t]; worstAt = t; } }
                }
                else if (score > worst)
                {
                    topScore[worstAt] = score; topId[worstAt] = id;
                    worst = topScore[0]; worstAt = 0;
                    for (int t = 1; t < 50; t++) if (topScore[t] < worst) { worst = topScore[t]; worstAt = t; }
                }
            }
            return held;
        });

        Console.WriteLine("  in-memory inverted index (FrozenDictionary<string,int[]>)");
        Console.WriteLine($"    build {sw.Elapsed.TotalSeconds,6:F2}s | retained ~{Mb(retained),9} | {frozen.Count:N0} terms / {postings:N0} postings | query median {medMs * 1000,6:F2} us | {totalHits} top50-hits/10q\n");

        GC.KeepAlive(frozen);
    }

    // ---- helpers ----

    private static (double medianMs, int totalHits) MeasureQueries(Func<string, int> runOne)
    {
        foreach (var q in Queries) runOne(q);     // warmup
        var times = new List<double>();
        int totalHits = 0;
        var sw = new Stopwatch();
        for (int rep = 0; rep < 20; rep++)
            foreach (var q in Queries)
            {
                sw.Restart();
                int hits = runOne(q);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
                if (rep == 0) totalHits += hits;
            }
        times.Sort();
        return (times[times.Count / 2], totalHits);
    }

    private static (string[] names, string[] texts) LoadSymbols(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name, COALESCE(signature, '') FROM symbols WHERE name IS NOT NULL";
        using var r = cmd.ExecuteReader();
        var names = new List<string>(1 << 19);
        var texts = new List<string>(1 << 19);
        while (r.Read())
        {
            string name = r.GetString(0);
            string sig = r.GetString(1);
            names.Add(name);
            // index both the identifier and its signature (param/type tokens matter for cross-language tracing)
            texts.Add(sig.Length == 0 ? name : name + ' ' + sig);
        }
        return (names.ToArray(), texts.ToArray());
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0,7:F1} MB";
}
