namespace RetrievalEval;

/// <summary>
/// Verifies that every graded doc_id still resolves to a real file in the pinned corpus. A doc_id is either a
/// repo-relative file path or `path#SymbolName`; only the path part is resolved on disk.
/// </summary>
public static class CorpusChecker
{
    public static CorpusValidation Check(IReadOnlyList<EvalQuery> queries, IReadOnlyDictionary<string, string> corpusRoots)
    {
        var missing = new List<string>();
        var checkedDocs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var query in queries)
        {
            foreach (var doc in query.Relevant)
            {
                var key = $"{query.Repo}:{doc.DocId}";
                if (!checkedDocs.Add(key)) continue;

                if (!corpusRoots.TryGetValue(query.Repo, out var root) &&
                    !corpusRoots.TryGetValue("*", out root))
                {
                    missing.Add($"{key} (no corpus root configured for repo '{query.Repo}')");
                    continue;
                }

                var relative = doc.DocId.Split('#', 2)[0];
                if (!File.Exists(Path.Combine(root, relative))) missing.Add(key);
            }
        }

        return new CorpusValidation
        {
            CheckedDocCount = checkedDocs.Count,
            MissingDocCount = missing.Count,
            MissingDocs = missing,
        };
    }
}
