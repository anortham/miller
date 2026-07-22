using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetrievalEval;

public static class Program
{
    const string Usage = """
        retrieval-eval — pure scoring for Miller semantic retrieval evaluation (no embedding calls).

          score    --queries <queries.jsonl> --results <results.jsonl> --out <report.json>
                   [--corpus <dir> | --corpus <repo>=<dir> ...] [--k 10]
          validate --queries <queries.jsonl> [--corpus <dir> | --corpus <repo>=<dir> ...]
          task-score --tasks <manifest.jsonl> --baseline <results.jsonl>
                     --candidate <results.jsonl> --out <aggregate.json>

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
                "task-score" => RunTaskScore(ParseOptions(args.Skip(1))),
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

    static int RunTaskScore(Options options)
    {
        var tasksPath = options.Require("tasks");
        var baselinePath = options.Require("baseline");
        var candidatePath = options.Require("candidate");
        var outPath = options.Require("out");

        try
        {
            var tasks = ReadTaskRows<TaskManifestRow>(tasksPath, "task manifest", TaskManifestFields);
            var baseline = ReadTaskRows<TaskArmResult>(baselinePath, "baseline results", TaskResultFields);
            var candidate = ReadTaskRows<TaskArmResult>(candidatePath, "candidate results", TaskResultFields);
            if (tasks.Any(task => IsPathLikeGroupLabel(task.Repo)))
                throw new InvalidOperationException("Task manifest repo must be a non-path label.");
            if (tasks.Any(task => IsPathLikeGroupLabel(task.Language)))
                throw new InvalidOperationException("Task manifest language must be a non-path label.");
            var report = TaskCompletionScorer.Score(tasks, baseline, candidate);
            var aggregate = TaskScoreAggregate.From(
                report,
                new TaskScoreInputs
                {
                    TasksSha256 = Sha256(tasksPath),
                    BaselineSha256 = Sha256(baselinePath),
                    CandidateSha256 = Sha256(candidatePath),
                });

            var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(outPath, JsonSerializer.Serialize(aggregate, Jsonl.Options));

            Console.WriteLine($"pairs={report.PairCount}  primary={report.PrimaryGate.Verdict}  identifier/path={report.IdentifierPathSafety.Verdict}");
            Console.WriteLine("aggregate written");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return ValidationFail(ex.Message);
        }
    }

    static List<T> ReadTaskRows<T>(string path, string label, IReadOnlySet<string> allowedFields)
    {
        var lineNumber = 0;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException($"{label} row {lineNumber} must be a JSON object.");

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                        throw new InvalidOperationException($"{label} row {lineNumber} contains duplicate field '{property.Name}'.");
                    if (!allowedFields.Contains(property.Name))
                        throw new InvalidOperationException($"{label} row {lineNumber} contains unsupported field '{property.Name}'.");
                }
            }

            return Jsonl.ReadAll<T>(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"{label} input does not match its schema.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{label} row {lineNumber} is not valid JSON.", ex);
        }
    }

    static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    static bool IsPathLikeGroupLabel(string value) => value.Contains('/') || value.Contains('\\');

    static int Ok(string message) { Console.WriteLine(message); return 0; }

    static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

    static int ValidationFail(string message) { Console.Error.WriteLine($"validation failed: {message}"); return 2; }

    static readonly IReadOnlySet<string> TaskManifestFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "task_id", "repo", "language", "query_profile",
    };

    static readonly IReadOnlySet<string> TaskResultFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "task_id", "completed", "duration_ms", "tool_calls", "search_calls", "zero_result_search_calls",
    };

    sealed record TaskScoreInputs
    {
        [JsonPropertyName("tasks_sha256")] public string TasksSha256 { get; init; } = "";
        [JsonPropertyName("baseline_sha256")] public string BaselineSha256 { get; init; } = "";
        [JsonPropertyName("candidate_sha256")] public string CandidateSha256 { get; init; } = "";
    }

    sealed record TaskScoreAggregate
    {
        [JsonPropertyName("schema")] public int Schema { get; init; }
        [JsonPropertyName("inputs")] public TaskScoreInputs Inputs { get; init; } = new();
        [JsonPropertyName("pair_count")] public int PairCount { get; init; }
        [JsonPropertyName("completion")] public TaskCompletionCells Completion { get; init; } = new();
        [JsonPropertyName("primary_gate")] public TaskCompletionGate PrimaryGate { get; init; } = new();
        [JsonPropertyName("identifier_path_safety")] public TaskSafetyGate IdentifierPathSafety { get; init; } = new();
        [JsonPropertyName("diagnostics")] public TaskArmDiagnostics Diagnostics { get; init; } = new();
        [JsonPropertyName("by_repo")] public IReadOnlyDictionary<string, TaskSubgroupReport> ByRepo { get; init; } = new Dictionary<string, TaskSubgroupReport>();
        [JsonPropertyName("by_language")] public IReadOnlyDictionary<string, TaskSubgroupReport> ByLanguage { get; init; } = new Dictionary<string, TaskSubgroupReport>();
        [JsonPropertyName("by_query_profile")] public IReadOnlyDictionary<string, TaskSubgroupReport> ByQueryProfile { get; init; } = new Dictionary<string, TaskSubgroupReport>();

        public static TaskScoreAggregate From(TaskCompletionReport report, TaskScoreInputs inputs) => new()
        {
            Schema = report.Schema,
            Inputs = inputs,
            PairCount = report.PairCount,
            Completion = report.Completion,
            PrimaryGate = report.PrimaryGate,
            IdentifierPathSafety = report.IdentifierPathSafety,
            Diagnostics = report.Diagnostics,
            ByRepo = report.ByRepo,
            ByLanguage = report.ByLanguage,
            ByQueryProfile = report.ByQueryProfile,
        };
    }

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
