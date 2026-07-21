namespace RetrievalEval;

sealed record ScoredQuery(EvalQuery Query, double Recall, double Ndcg, bool Hit);

/// <summary>
/// One evaluation unit under the cluster unit policy: an intent cluster scored as the mean over its member
/// paraphrases, or a single standalone query. <see cref="MaxRecall"/>/<see cref="MaxNdcg"/> carry the
/// best-member view used by the secondary cluster-max metrics.
/// </summary>
sealed record EvalUnit(
    string UnitId,
    string Language,
    double Recall,
    double Ndcg,
    double MaxRecall,
    double MaxNdcg,
    int QueryCount);

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

        var units = BuildUnits(positives);

        var perLanguage = units
            .GroupBy(u => u.Language, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, AggregateUnits, StringComparer.Ordinal);

        var perLanguagePerQuery = positives
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
                UnitCount = kv.Value.UnitCount,
                QueryCount = kv.Value.QueryCount,
            })
            .FirstOrDefault();

        return new EvalReport
        {
            K = k,
            UnitPolicy = UnitPolicies.Cluster,
            QueryCount = queries.Count,
            PositiveQueryCount = positives.Count,
            NegativeQueryCount = negativeCount,
            EvaluationUnitCount = units.Count,
            Overall = AggregateUnits(units),
            OverallPerQuery = Aggregate(positives),
            OverallClusterMax = AggregateUnitsMax(units),
            PerLanguage = perLanguage,
            PerLanguagePerQuery = perLanguagePerQuery,
            LanguageMacroAverage = new MacroAverage
            {
                RecallAtK = Mean(perLanguage.Values.Select(v => v.RecallAtK)),
                NdcgAtK = Mean(perLanguage.Values.Select(v => v.NdcgAtK)),
                LanguageCount = perLanguage.Count,
            },
            WorstLanguage = worst,
            PerQueryClass = perQueryClass,
            PerIntentCluster = clusters,
            Units = units
                .OrderBy(u => u.UnitId, StringComparer.Ordinal)
                .Select(u => new UnitRow
                {
                    UnitId = u.UnitId,
                    Language = u.Language,
                    RecallAtK = u.Recall,
                    NdcgAtK = u.Ndcg,
                    QueryCount = u.QueryCount,
                })
                .ToList(),
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

    /// <summary>
    /// Design §8: paraphrase intent clusters are scored as clusters, not independent samples. A cluster becomes one
    /// unit whose score is the mean over its members — the expected quality over a random phrasing of that intent —
    /// so adding a paraphrase sharpens a cluster's estimate without buying it extra weight in the headline metric.
    /// A query with no cluster is its own unit.
    /// </summary>
    static IReadOnlyList<EvalUnit> BuildUnits(IReadOnlyList<ScoredQuery> positives)
    {
        var units = new List<EvalUnit>();

        foreach (var solo in positives.Where(s => string.IsNullOrWhiteSpace(s.Query.IntentCluster)))
            units.Add(new EvalUnit($"query:{solo.Query.QueryId}", solo.Query.Language, solo.Recall, solo.Ndcg, solo.Recall, solo.Ndcg, 1));

        var clustered = positives
            .Where(s => !string.IsNullOrWhiteSpace(s.Query.IntentCluster))
            .GroupBy(s => s.Query.IntentCluster!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var cluster in clustered)
        {
            units.Add(new EvalUnit(
                $"cluster:{cluster.Key}",
                DominantLanguage(cluster),
                cluster.Average(s => s.Recall),
                cluster.Average(s => s.Ndcg),
                cluster.Max(s => s.Recall),
                cluster.Max(s => s.Ndcg),
                cluster.Count()));
        }

        return units;
    }

    /// <summary>A cluster's language for parity reporting: the most common member language, ties broken by name.</summary>
    static string DominantLanguage(IEnumerable<ScoredQuery> members) =>
        members
            .GroupBy(s => s.Query.Language, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First().Key;

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
            UnitCount = items.Count,
            QueryCount = items.Count,
        };
    }

    static MetricBlock AggregateUnits(IEnumerable<EvalUnit> units)
    {
        var items = units as IReadOnlyCollection<EvalUnit> ?? units.ToList();
        return new MetricBlock
        {
            RecallAtK = Mean(items.Select(u => u.Recall)),
            NdcgAtK = Mean(items.Select(u => u.Ndcg)),
            UnitCount = items.Count,
            QueryCount = items.Sum(u => u.QueryCount),
        };
    }

    static MetricBlock AggregateUnitsMax(IEnumerable<EvalUnit> units)
    {
        var items = units as IReadOnlyCollection<EvalUnit> ?? units.ToList();
        return new MetricBlock
        {
            RecallAtK = Mean(items.Select(u => u.MaxRecall)),
            NdcgAtK = Mean(items.Select(u => u.MaxNdcg)),
            UnitCount = items.Count,
            QueryCount = items.Sum(u => u.QueryCount),
        };
    }

    static double Mean(IEnumerable<double> values)
    {
        var items = values as IReadOnlyCollection<double> ?? values.ToList();
        return items.Count == 0 ? 0.0 : items.Sum() / items.Count;
    }
}
