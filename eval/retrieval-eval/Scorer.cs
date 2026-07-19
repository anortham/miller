namespace RetrievalEval;

sealed record ScoredQuery(EvalQuery Query, double Recall, double Ndcg, bool Hit);

/// <summary>Rolls per-query metrics up into the report shape the semantic program's gates read.</summary>
public static class Scorer
{
    public static EvalReport Score(
        IReadOnlyList<EvalQuery> queries,
        IReadOnlyList<EvalResult> results,
        int k,
        ReportInputs? inputs = null,
        CorpusValidation? corpusValidation = null)
    {
        var byId = new Dictionary<string, EvalQuery>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            if (!byId.TryAdd(query.QueryId, query))
                throw new InvalidOperationException($"Duplicate query_id in the query set: {query.QueryId}");
        }

        var ranked = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (var result in results)
        {
            if (!byId.ContainsKey(result.QueryId)) { unknown.Add(result.QueryId); continue; }
            if (!ranked.TryAdd(result.QueryId, result.Ranked))
                throw new InvalidOperationException($"Duplicate query_id in the results file: {result.QueryId}");
        }

        var missing = queries.Where(q => !ranked.ContainsKey(q.QueryId)).Select(q => q.QueryId).ToList();

        var positives = new List<ScoredQuery>();
        var falsePositives = 0;
        var negativeCount = 0;

        foreach (var query in queries)
        {
            var docs = ranked.TryGetValue(query.QueryId, out var r) ? r : [];

            if (query.Negative)
            {
                negativeCount++;
                if (docs.Take(Math.Max(0, k)).Any()) falsePositives++;
                continue;
            }

            var relevant = ToGradeMap(query);
            var recall = Metrics.RecallAtK(docs, relevant, k);
            var ndcg = Metrics.NdcgAtK(docs, relevant, k);
            positives.Add(new ScoredQuery(query, recall, ndcg, recall > 0.0));
        }

        var perLanguage = positives
            .GroupBy(s => s.Query.Language, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, Aggregate, StringComparer.Ordinal);

        var perQueryClass = positives
            .GroupBy(s => s.Query.QueryClass, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, Aggregate, StringComparer.Ordinal);

        var clusters = positives
            .Where(s => !string.IsNullOrWhiteSpace(s.Query.IntentCluster))
            .GroupBy(s => s.Query.IntentCluster!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ClusterRollup
            {
                IntentCluster = g.Key,
                Repo = g.First().Query.Repo,
                Language = g.First().Query.Language,
                MemberCount = g.Count(),
                ClusterHit = g.Any(s => s.Hit),
                MemberHitRate = (double)g.Count(s => s.Hit) / g.Count(),
                RecallAtK = g.Average(s => s.Recall),
                NdcgAtK = g.Average(s => s.Ndcg),
            })
            .ToList();

        var worst = perLanguage
            .OrderBy(kv => kv.Value.NdcgAtK)
            .ThenBy(kv => kv.Value.RecallAtK)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new WorstLanguage
            {
                Language = kv.Key,
                RecallAtK = kv.Value.RecallAtK,
                NdcgAtK = kv.Value.NdcgAtK,
                QueryCount = kv.Value.QueryCount,
            })
            .FirstOrDefault();

        return new EvalReport
        {
            K = k,
            QueryCount = queries.Count,
            PositiveQueryCount = positives.Count,
            NegativeQueryCount = negativeCount,
            Overall = Aggregate(positives),
            PerLanguage = perLanguage,
            LanguageMacroAverage = new MacroAverage
            {
                RecallAtK = Mean(perLanguage.Values.Select(v => v.RecallAtK)),
                NdcgAtK = Mean(perLanguage.Values.Select(v => v.NdcgAtK)),
                LanguageCount = perLanguage.Count,
            },
            WorstLanguage = worst,
            PerQueryClass = perQueryClass,
            PerIntentCluster = clusters,
            IntentClusterSummary = new ClusterSummary
            {
                ClusterCount = clusters.Count,
                ClusterHitCount = clusters.Count(c => c.ClusterHit),
                ClusterHitRate = clusters.Count == 0 ? 0.0 : (double)clusters.Count(c => c.ClusterHit) / clusters.Count,
            },
            Negatives = new NegativeBlock
            {
                Count = negativeCount,
                FalsePositiveCount = falsePositives,
                FalsePositiveRate = negativeCount == 0 ? 0.0 : (double)falsePositives / negativeCount,
                PassRate = negativeCount == 0 ? 0.0 : (double)(negativeCount - falsePositives) / negativeCount,
            },
            MissingResults = missing,
            UnknownResults = unknown,
            Inputs = inputs,
            CorpusValidation = corpusValidation,
        };
    }

    static Dictionary<string, int> ToGradeMap(EvalQuery query)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var doc in query.Relevant) map[doc.DocId] = doc.Grade;
        return map;
    }

    static MetricBlock Aggregate(IEnumerable<ScoredQuery> scored)
    {
        var items = scored as IReadOnlyCollection<ScoredQuery> ?? scored.ToList();
        return new MetricBlock
        {
            RecallAtK = Mean(items.Select(s => s.Recall)),
            NdcgAtK = Mean(items.Select(s => s.Ndcg)),
            QueryCount = items.Count,
        };
    }

    static double Mean(IEnumerable<double> values)
    {
        var items = values as IReadOnlyCollection<double> ?? values.ToList();
        return items.Count == 0 ? 0.0 : items.Sum() / items.Count;
    }
}
