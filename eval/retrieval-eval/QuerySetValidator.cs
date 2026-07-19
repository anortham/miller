namespace RetrievalEval;

/// <summary>The dev-set composition floor from the semantic program's evaluation protocol (design §8).</summary>
public sealed record CompositionMinimums
{
    public int TotalQueries { get; init; } = 60;
    public IReadOnlyList<string> Repos { get; init; } = ["miller", "julie"];
    public int ClustersPerRepo { get; init; } = 6;
    public int ParaphrasesPerCluster { get; init; } = 3;
    public int IdentifierQueries { get; init; } = 15;
    public int ShortTokenQueries { get; init; } = 5;
    public int NegationOrAmbiguousQueries { get; init; } = 5;
    public int Negatives { get; init; } = 5;

    public static CompositionMinimums Dev { get; } = new();
}

public static class QuerySetValidator
{
    public const string NegationTag = "negation";
    public const string AmbiguousTag = "ambiguous";

    /// <summary>Returns one message per violated schema rule or composition minimum; empty means valid.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<EvalQuery> queries, CompositionMinimums minimums)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var query in queries)
        {
            var id = string.IsNullOrWhiteSpace(query.QueryId) ? "<blank>" : query.QueryId;
            if (string.IsNullOrWhiteSpace(query.QueryId)) problems.Add("a row has a blank query_id");
            else if (!seen.Add(query.QueryId)) problems.Add($"{id}: duplicate query_id");

            if (string.IsNullOrWhiteSpace(query.Query)) problems.Add($"{id}: blank query text");
            if (string.IsNullOrWhiteSpace(query.Repo)) problems.Add($"{id}: blank repo");
            if (string.IsNullOrWhiteSpace(query.Language)) problems.Add($"{id}: blank language");
            if (!QueryClasses.All.Contains(query.QueryClass))
                problems.Add($"{id}: query_class '{query.QueryClass}' is not in the enum ({string.Join('|', QueryClasses.All)})");

            if (query.Negative)
            {
                if (query.Relevant.Count > 0) problems.Add($"{id}: a negative query must have no relevant docs");
                if (!string.IsNullOrWhiteSpace(query.IntentCluster))
                    problems.Add($"{id}: a negative query must not belong to an intent cluster");
            }
            else
            {
                if (query.Relevant.Count == 0) problems.Add($"{id}: positive query has no relevant docs");
                foreach (var doc in query.Relevant)
                {
                    if (string.IsNullOrWhiteSpace(doc.DocId)) problems.Add($"{id}: relevant doc with blank doc_id");
                    if (doc.Grade is < 1 or > 3) problems.Add($"{id}: grade {doc.Grade} for '{doc.DocId}' is outside 1..3");
                }

                if (query.Relevant.Select(d => d.DocId).Distinct(StringComparer.Ordinal).Count() != query.Relevant.Count)
                    problems.Add($"{id}: duplicate doc_id in relevant");
            }
        }

        if (queries.Count < minimums.TotalQueries)
            problems.Add($"composition: {queries.Count} queries, minimum {minimums.TotalQueries}");

        foreach (var repo in minimums.Repos)
        {
            var repoQueries = queries.Where(q => q.Repo == repo).ToList();
            if (repoQueries.Count == 0) { problems.Add($"composition: no queries for repo '{repo}'"); continue; }

            var clusters = repoQueries
                .Where(q => !string.IsNullOrWhiteSpace(q.IntentCluster))
                .GroupBy(q => q.IntentCluster!, StringComparer.Ordinal)
                .ToList();

            var qualifying = clusters.Count(g => g.Count() >= minimums.ParaphrasesPerCluster);
            if (qualifying < minimums.ClustersPerRepo)
                problems.Add($"composition: repo '{repo}' has {qualifying} clusters with >= {minimums.ParaphrasesPerCluster} paraphrases, minimum {minimums.ClustersPerRepo}");

            foreach (var undersized in clusters.Where(g => g.Count() < minimums.ParaphrasesPerCluster))
                problems.Add($"composition: cluster '{undersized.Key}' has {undersized.Count()} paraphrases, minimum {minimums.ParaphrasesPerCluster}");
        }

        Require(problems, queries.Count(q => q.QueryClass == "identifier" && !q.Negative), minimums.IdentifierQueries, "identifier queries");
        Require(problems, queries.Count(q => q.QueryClass == "short_token" && !q.Negative), minimums.ShortTokenQueries, "short_token queries");
        Require(
            problems,
            queries.Count(q => q.Tags.Contains(NegationTag, StringComparer.Ordinal) || q.Tags.Contains(AmbiguousTag, StringComparer.Ordinal)),
            minimums.NegationOrAmbiguousQueries,
            $"queries tagged '{NegationTag}' or '{AmbiguousTag}'");
        Require(problems, queries.Count(q => q.Negative), minimums.Negatives, "irrelevant negatives");

        return problems;
    }

    static void Require(List<string> problems, int actual, int minimum, string label)
    {
        if (actual < minimum) problems.Add($"composition: {actual} {label}, minimum {minimum}");
    }
}
