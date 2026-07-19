using System.Text.Json;

namespace RetrievalEval;

public static class Program
{
    const string Usage = """
        retrieval-eval — pure scoring for Miller semantic retrieval evaluation (no embedding calls).

          score    --queries <queries.jsonl> --results <results.jsonl> --out <report.json>
                   [--corpus <dir> | --corpus <repo>=<dir> ...] [--k 10]
          validate --queries <queries.jsonl> [--corpus <dir> | --corpus <repo>=<dir> ...]

        --corpus with a bare directory applies to every repo; repeat `<repo>=<dir>` for a multi-repo set.
        Exit codes: 0 ok, 1 usage/IO error, 2 validation failed.
        """;

    public static int Main(string[] args)
    {
        try
        {
            return args.Length == 0 ? Fail(Usage) : args[0] switch
            {
                "score" => RunScore(ParseOptions(args.Skip(1))),
                "validate" => RunValidate(ParseOptions(args.Skip(1))),
                "--help" or "-h" or "help" => Ok(Usage),
                _ => Fail($"unknown verb '{args[0]}'\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return Fail(ex.Message);
        }
    }

    static int RunScore(Options options)
    {
        var queriesPath = options.Require("queries");
        var resultsPath = options.Require("results");
        var outPath = options.Require("out");
        var k = options.Int("k", 10);

        var queries = Jsonl.ReadAll<EvalQuery>(queriesPath);
        var results = Jsonl.ReadAll<EvalResult>(resultsPath);
        var corpusRoots = options.CorpusRoots();

        var report = Scorer.Score(
            queries,
            results,
            k,
            new ReportInputs { Queries = Path.GetFullPath(queriesPath), Results = Path.GetFullPath(resultsPath), Corpus = corpusRoots },
            corpusRoots.Count == 0 ? null : CorpusChecker.Check(queries, corpusRoots));

        var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, Jsonl.Options));

        Console.WriteLine($"k={report.K}  queries={report.QueryCount} (positive {report.PositiveQueryCount}, negative {report.NegativeQueryCount})");
        Console.WriteLine($"recall@{k}={report.Overall.RecallAtK:F4}  ndcg@{k}={report.Overall.NdcgAtK:F4}");
        Console.WriteLine($"language macro-average: recall={report.LanguageMacroAverage.RecallAtK:F4} ndcg={report.LanguageMacroAverage.NdcgAtK:F4} over {report.LanguageMacroAverage.LanguageCount} languages");
        if (report.WorstLanguage is { } worst)
            Console.WriteLine($"worst language: {worst.Language} recall={worst.RecallAtK:F4} ndcg={worst.NdcgAtK:F4} (n={worst.QueryCount})");
        Console.WriteLine($"intent clusters: {report.IntentClusterSummary.ClusterHitCount}/{report.IntentClusterSummary.ClusterCount} hit");
        Console.WriteLine($"negatives: false-positive rate {report.Negatives.FalsePositiveRate:F4} ({report.Negatives.FalsePositiveCount}/{report.Negatives.Count})");
        if (report.PerQueryClass.TryGetValue("identifier", out var identifier))
            Console.WriteLine($"identifier (non-inferiority set): recall={identifier.RecallAtK:F4} ndcg={identifier.NdcgAtK:F4} (n={identifier.QueryCount})");
        if (report.MissingResults.Count > 0)
            Console.WriteLine($"WARNING: {report.MissingResults.Count} queries have no results row (scored as zero)");
        if (report.UnknownResults.Count > 0)
            Console.WriteLine($"WARNING: {report.UnknownResults.Count} results rows reference unknown query ids");
        Console.WriteLine($"report written to {Path.GetFullPath(outPath)}");

        return 0;
    }

    static int RunValidate(Options options)
    {
        var queries = Jsonl.ReadAll<EvalQuery>(options.Require("queries"));
        var problems = QuerySetValidator.Validate(queries, CompositionMinimums.Dev).ToList();

        var corpusRoots = options.CorpusRoots();
        if (corpusRoots.Count > 0)
        {
            var corpus = CorpusChecker.Check(queries, corpusRoots);
            problems.AddRange(corpus.MissingDocs.Select(d => $"corpus: '{d}' does not exist"));
            Console.WriteLine($"corpus: {corpus.CheckedDocCount} distinct doc references checked, {corpus.MissingDocCount} missing");
        }

        Console.WriteLine($"queries: {queries.Count}");
        foreach (var problem in problems) Console.WriteLine($"FAIL {problem}");

        if (problems.Count > 0) return 2;
        Console.WriteLine("OK: schema valid and composition minimums met");
        return 0;
    }

    static int Ok(string message) { Console.WriteLine(message); return 0; }

    static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

    sealed class Options
    {
        readonly Dictionary<string, string> _single = new(StringComparer.Ordinal);
        readonly List<string> _corpus = [];

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            string? pending = null;
            foreach (var arg in args)
            {
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    if (pending is not null) throw new ArgumentException($"--{pending} requires a value");
                    pending = arg[2..];
                    continue;
                }

                if (pending is null) throw new ArgumentException($"unexpected argument '{arg}'");
                if (pending == "corpus") options._corpus.Add(arg);
                else options._single[pending] = arg;
                pending = null;
            }

            if (pending is not null) throw new ArgumentException($"--{pending} requires a value");
            return options;
        }

        public string Require(string name) =>
            _single.TryGetValue(name, out var value)
                ? value
                : throw new ArgumentException($"--{name} is required\n\n{Usage}");

        public int Int(string name, int fallback) =>
            _single.TryGetValue(name, out var value)
                ? int.Parse(value)
                : fallback;

        public IReadOnlyDictionary<string, string> CorpusRoots()
        {
            var roots = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in _corpus)
            {
                var parts = entry.Split('=', 2);
                var (repo, dir) = parts.Length == 2 ? (parts[0], parts[1]) : ("*", entry);
                if (!Directory.Exists(dir)) throw new ArgumentException($"corpus directory does not exist: {dir}");
                roots[repo] = Path.GetFullPath(dir);
            }

            return roots;
        }
    }

    static Options ParseOptions(IEnumerable<string> args) => Options.Parse(args);
}
