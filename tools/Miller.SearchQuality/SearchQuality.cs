using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Miller.SearchQuality;

public sealed record SearchQualitySuite
{
    public List<RepositorySpec> Repositories { get; init; } = [];
    public List<SearchCaseSpec> Cases { get; init; } = [];
}

public sealed record RepositorySpec
{
    public required string Name { get; init; }
    public required string Root { get; init; }
    public string? Language { get; init; }
    public string? ErosWorkspaceId { get; init; }
}

public sealed record SearchCaseSpec
{
    public required string Id { get; init; }
    public required string Repository { get; init; }
    public required string Query { get; init; }
    public string Mode { get; init; } = "auto";
    public string? Regions { get; init; }
    public string? FilePattern { get; init; }
    public string? Language { get; init; }
    public bool? ExcludeTests { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<SearchExpectation> Expected { get; init; } = [];
}

public sealed record SearchExpectation
{
    public string? Path { get; init; }
    public string? Symbol { get; init; }
    public string? Kind { get; init; }
    public int? Line { get; init; }
}

public sealed record SearchQualityHit
{
    public required string Provider { get; init; }
    public string? Title { get; init; }
    public string? Name { get; init; }
    public string? Kind { get; init; }
    public string? Path { get; init; }
    public int? Line { get; init; }
    public double? Score { get; init; }
}

public sealed record SearchProviderResult
{
    public required string Provider { get; init; }
    public required string Repository { get; init; }
    public required string CaseId { get; init; }
    public required string Query { get; init; }
    public int ExitCode { get; init; }
    public long DurationMs { get; init; }
    public string? Error { get; init; }
    public List<SearchQualityHit> Hits { get; init; } = [];
    public required SearchCaseScore Score { get; init; }
}

public sealed record SearchCaseScore
{
    public required string Provider { get; init; }
    public required string Repository { get; init; }
    public required string CaseId { get; init; }
    public int HitCount { get; init; }
    public int? MatchedRank { get; init; }
    public bool Top1 { get; init; }
    public bool Top3 { get; init; }
    public bool Top5 { get; init; }
    public double ReciprocalRank { get; init; }
}

public sealed record ProviderSummary
{
    public required string Provider { get; init; }
    public int Total { get; init; }
    public int Top1 { get; init; }
    public int Top3 { get; init; }
    public int Top5 { get; init; }
    public int Misses { get; init; }
    public double Mrr { get; init; }
}

public sealed record SearchRunArtifact
{
    public required DateTimeOffset StartedAt { get; init; }
    public required string CasesPath { get; init; }
    public required List<string> Providers { get; init; }
    public required List<SearchProviderResult> Results { get; init; }
    public required List<ProviderSummary> Summaries { get; init; }
}

public static class SearchQualityScorer
{
    public static SearchCaseScore Score(string provider, SearchCaseSpec searchCase, IReadOnlyList<SearchQualityHit> hits)
    {
        int? rank = null;
        if (searchCase.Expected.Count > 0)
        {
            for (int i = 0; i < hits.Count; i++)
            {
                if (searchCase.Expected.Any(expectation => Matches(expectation, hits[i])))
                {
                    rank = i + 1;
                    break;
                }
            }
        }

        return new SearchCaseScore
        {
            Provider = provider,
            Repository = searchCase.Repository,
            CaseId = searchCase.Id,
            HitCount = hits.Count,
            MatchedRank = rank,
            Top1 = rank == 1,
            Top3 = rank is >= 1 and <= 3,
            Top5 = rank is >= 1 and <= 5,
            ReciprocalRank = rank is null ? 0.0 : 1.0 / rank.Value,
        };
    }

    public static ProviderSummary Summarize(string provider, IEnumerable<SearchCaseScore> scores)
    {
        var list = scores.ToList();
        return new ProviderSummary
        {
            Provider = provider,
            Total = list.Count,
            Top1 = list.Count(s => s.Top1),
            Top3 = list.Count(s => s.Top3),
            Top5 = list.Count(s => s.Top5),
            Misses = list.Count(s => s.MatchedRank is null),
            Mrr = list.Count == 0 ? 0.0 : list.Sum(s => s.ReciprocalRank) / list.Count,
        };
    }

    private static bool Matches(SearchExpectation expectation, SearchQualityHit hit)
    {
        if (expectation.Path is not null && !PathMatches(expectation.Path, hit.Path))
            return false;
        if (expectation.Symbol is not null && !SymbolMatches(expectation.Symbol, hit))
            return false;
        if (expectation.Kind is not null
            && !string.Equals(expectation.Kind, hit.Kind, StringComparison.OrdinalIgnoreCase))
            return false;
        if (expectation.Line is not null && expectation.Line != hit.Line)
            return false;
        return expectation.Path is not null
            || expectation.Symbol is not null
            || expectation.Kind is not null
            || expectation.Line is not null;
    }

    private static bool PathMatches(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual))
            return false;
        string expectedNorm = NormalizePath(expected);
        string actualNorm = NormalizePath(actual);
        return string.Equals(actualNorm, expectedNorm, StringComparison.OrdinalIgnoreCase)
            || actualNorm.EndsWith("/" + expectedNorm, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('.', '/');

    private static bool SymbolMatches(string expected, SearchQualityHit hit) =>
        SymbolTextMatches(expected, hit.Name)
        || SymbolTextMatches(expected, hit.Title);

    private static bool SymbolTextMatches(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual))
            return false;
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            return true;

        int index = actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            int before = index - 1;
            int after = index + expected.Length;
            bool leftBoundary = before < 0 || !IsIdentifierChar(actual[before]);
            bool rightBoundary = after >= actual.Length || !IsIdentifierChar(actual[after]);
            if (leftBoundary && rightBoundary)
                return true;
            index = actual.IndexOf(expected, after, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}

public static class SearchQualityParsers
{
    public static IReadOnlyList<SearchQualityHit> ParseMillerJson(string provider, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var hits = new List<SearchQualityHit>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string? name = GetString(item, "name");
            string? file = GetString(item, "file") ?? GetString(item, "display_path") ?? GetString(item, "path");
            string? kind = GetString(item, "kind") ?? GetString(item, "content_kind") ?? (name is null ? "content" : null);
            hits.Add(new SearchQualityHit
            {
                Provider = provider,
                Title = name ?? file,
                Name = name,
                Kind = kind,
                Path = file,
                Line = GetInt(item, "line"),
                Score = GetDouble(item, "score"),
            });
        }
        return hits;
    }

    public static IReadOnlyList<SearchQualityHit> ParseErosJson(string provider, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out JsonElement results)
            || results.ValueKind != JsonValueKind.Array)
            return [];

        var hits = new List<SearchQualityHit>();
        foreach (JsonElement item in results.EnumerateArray())
        {
            hits.Add(new SearchQualityHit
            {
                Provider = provider,
                Title = GetString(item, "title"),
                Name = GetString(item, "title"),
                Kind = GetString(item, "kind"),
                Path = GetString(item, "path"),
                Line = GetInt(item, "line"),
                Score = GetDouble(item, "score"),
            });
        }
        return hits;
    }

    public static IReadOnlyList<SearchQualityHit> ParseJulieStandaloneJson(string provider, string output)
    {
        string json = ExtractJsonObject(output);
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
            return [];

        var text = new StringBuilder();
        foreach (JsonElement item in content.EnumerateArray())
        {
            string? value = GetString(item, "text");
            if (!string.IsNullOrEmpty(value))
                text.AppendLine(value);
        }

        return ParseJulieText(provider, text.ToString());
    }

    private static IReadOnlyList<SearchQualityHit> ParseJulieText(string provider, string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var hits = new List<SearchQualityHit>();
        string? pendingDefinition = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            string line = raw.Trim();
            if (line.Length == 0 || string.Equals(line, "Other matches:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.StartsWith("Definition found:", StringComparison.OrdinalIgnoreCase))
            {
                pendingDefinition = line["Definition found:".Length..].Trim();
                continue;
            }

            if (pendingDefinition is not null && TryParseJulieFileRow(line, out string? filePath, out int? definitionLine, out string? kind))
            {
                hits.Add(new SearchQualityHit
                {
                    Provider = provider,
                    Title = pendingDefinition,
                    Name = pendingDefinition,
                    Kind = kind,
                    Path = filePath,
                    Line = definitionLine,
                });
                pendingDefinition = null;
                continue;
            }

            if (TryParsePathLine(line, out string? path, out int lineNumber))
            {
                string? title = null;
                if (i + 1 < lines.Length && char.IsWhiteSpace(lines[i + 1], 0))
                {
                    title = lines[i + 1].Trim();
                    i++;
                }

                hits.Add(new SearchQualityHit
                {
                    Provider = provider,
                    Title = string.IsNullOrWhiteSpace(title) ? path : title,
                    Name = ExtractLikelySymbolName(title),
                    Kind = ExtractLikelyKind(title),
                    Path = path,
                    Line = lineNumber,
                });
                continue;
            }

            if (TryParseJulieFileRow(line, out filePath, out int? fileLine, out kind))
            {
                hits.Add(new SearchQualityHit
                {
                    Provider = provider,
                    Title = filePath,
                    Kind = kind,
                    Path = filePath,
                    Line = fileLine,
                });
            }
        }

        return hits;
    }

    private static string ExtractJsonObject(string output)
    {
        for (int i = 0; i < output.Length; i++)
        {
            if (output[i] != '{')
                continue;
            string candidate = output[i..].Trim();
            try
            {
                using JsonDocument _ = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch (JsonException)
            {
                // Keep scanning; Julie standalone prepends human-readable lines before the JSON payload.
            }
        }
        throw new JsonException("no JSON object found in Julie output");
    }

    private static bool TryParseJulieFileRow(string line, out string path, out int? lineNumber, out string? kind)
    {
        path = "";
        lineNumber = null;
        kind = null;
        int suffixStart = line.LastIndexOf(" (", StringComparison.Ordinal);
        if (suffixStart <= 0 || !line.EndsWith(')'))
            return false;

        string candidate = line[..suffixStart].Trim();
        string suffix = line[(suffixStart + 2)..^1];
        string[] parts = suffix.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !LooksLikePath(candidate))
            return false;

        if (TryParsePathLine(candidate, out string splitPath, out int splitLine))
        {
            candidate = splitPath;
            lineNumber = splitLine;
        }

        path = candidate;
        kind = parts[0].Length == 0 ? null : parts[0];
        return true;
    }

    private static bool TryParsePathLine(string line, out string path, out int lineNumber)
    {
        path = "";
        lineNumber = 0;
        int colon = line.LastIndexOf(':');
        if (colon <= 0 || colon == line.Length - 1)
            return false;
        if (!int.TryParse(line[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out lineNumber))
            return false;
        string candidate = line[..colon].Trim();
        if (!LooksLikePath(candidate))
            return false;
        path = candidate;
        return true;
    }

    private static bool LooksLikePath(string value) =>
        value.Contains('/', StringComparison.Ordinal)
        || value.Contains('\\', StringComparison.Ordinal)
        || Path.HasExtension(value);

    private static string? ExtractLikelySymbolName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        string[] parts = title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2 && IsKindWord(parts[0]))
            return parts[1];
        return parts[0];
    }

    private static string? ExtractLikelyKind(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        string first = title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        return IsKindWord(first) ? first : null;
    }

    private static bool IsKindWord(string value) => value is
        "class" or "struct" or "interface" or "enum" or "trait" or "function" or "method" or "def" or "fn";

    private static string? GetString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetInt(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value))
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;
    }

    private static double? GetDouble(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value))
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result) ? result : null;
    }
}

public static class SearchQualityCli
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            WriteUsage(stdout);
            return 0;
        }

        string command = args[0];
        var options = OptionBag.Parse(args.Skip(1));
        return command switch
        {
            "init" => RunInit(options, stdout, stderr),
            "run" => RunSuite(options, stdout, stderr),
            _ => UnknownCommand(command, stderr),
        };
    }

    private static int RunInit(OptionBag options, TextWriter stdout, TextWriter stderr)
    {
        string casesPath = options.Value("cases") ?? DefaultCasesPath();
        bool force = options.Has("force");
        if (File.Exists(casesPath) && !force)
        {
            stderr.WriteLine($"{casesPath} already exists; pass --force to overwrite.");
            return 1;
        }

        string? directory = Path.GetDirectoryName(casesPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(casesPath, JsonSerializer.Serialize(StarterSuite(), JsonOptions) + Environment.NewLine);
        stdout.WriteLine($"wrote starter search-quality cases: {casesPath}");
        return 0;
    }

    private static int RunSuite(OptionBag options, TextWriter stdout, TextWriter stderr)
    {
        string casesPath = options.Value("cases") ?? DefaultCasesPath();
        if (!File.Exists(casesPath))
        {
            stderr.WriteLine($"case file not found: {casesPath}");
            stderr.WriteLine("run `dotnet run --project tools/Miller.SearchQuality -- init` first, or pass --cases.");
            return 1;
        }

        var suite = JsonSerializer.Deserialize<SearchQualitySuite>(File.ReadAllText(casesPath), JsonOptions);
        if (suite is null)
        {
            stderr.WriteLine($"case file could not be parsed: {casesPath}");
            return 1;
        }

        int limit = options.Int("limit", 5);
        int timeoutSeconds = options.Int("timeout-seconds", 90);
        var providers = SplitCsv(options.Value("providers") ?? "miller,julie,eros").ToList();
        string outDir = options.Value("out") ?? Path.Combine(".miller", "eval", "search-quality", "runs");
        Directory.CreateDirectory(outDir);

        var repositories = suite.Repositories.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var repoFilters = SplitCsv(options.Value("repo") ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var caseFilters = SplitCsv(options.Value("case") ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tagFilters = SplitCsv(options.Value("tag") ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cases = suite.Cases
            .Where(c => repoFilters.Count == 0 || repoFilters.Contains(c.Repository))
            .Where(c => caseFilters.Count == 0 || caseFilters.Contains(c.Id))
            .Where(c => tagFilters.Count == 0 || c.Tags.Any(tagFilters.Contains))
            .ToList();
        var results = new List<SearchProviderResult>();
        DateTimeOffset started = DateTimeOffset.UtcNow;

        foreach (SearchCaseSpec searchCase in cases)
        {
            if (!repositories.TryGetValue(searchCase.Repository, out RepositorySpec? repo))
            {
                stderr.WriteLine($"skipping {searchCase.Id}: unknown repository `{searchCase.Repository}`");
                continue;
            }

            foreach (string provider in providers)
            {
                SearchProviderResult result = RunProvider(provider, repo, searchCase, limit, timeoutSeconds, options);
                results.Add(result);
                WriteResultRow(stdout, result);
            }
        }

        var summaries = results
            .GroupBy(r => r.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(g => SearchQualityScorer.Summarize(g.Key, g.Select(r => r.Score)))
            .OrderBy(s => s.Provider, StringComparer.Ordinal)
            .ToList();

        stdout.WriteLine();
        stdout.WriteLine("provider,total,top1,top3,top5,misses,mrr");
        foreach (ProviderSummary summary in summaries)
        {
            stdout.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{summary.Provider},{summary.Total},{summary.Top1},{summary.Top3},{summary.Top5},{summary.Misses},{summary.Mrr:0.0000}"));
        }

        string artifactPath = Path.Combine(outDir, started.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + ".json");
        var artifact = new SearchRunArtifact
        {
            StartedAt = started,
            CasesPath = casesPath,
            Providers = providers,
            Results = results,
            Summaries = summaries,
        };
        File.WriteAllText(artifactPath, JsonSerializer.Serialize(artifact, JsonOptions) + Environment.NewLine);
        stdout.WriteLine();
        stdout.WriteLine($"artifact={artifactPath}");
        return results.Any(r => r.Error is not null) ? 2 : 0;
    }

    private static SearchProviderResult RunProvider(
        string provider,
        RepositorySpec repo,
        SearchCaseSpec searchCase,
        int limit,
        int timeoutSeconds,
        OptionBag options)
    {
        string providerId = provider.Trim();
        string providerKind = providerId.Split(':', 2)[0].ToLowerInvariant();
        var stopwatch = Stopwatch.StartNew();
        ProcessResult process;
        IReadOnlyList<SearchQualityHit> hits = [];
        string? error = null;

        try
        {
            process = providerKind switch
            {
                "miller" => RunProcess(MillerCommand(options), MillerArgs(repo, searchCase, limit), timeoutSeconds),
                "julie" => RunProcess(JulieCommand(options), BuildJulieArgs(repo, searchCase, limit), timeoutSeconds),
                "eros" => RunProcess(ErosCommand(options), ErosArgs(providerId, repo, searchCase, limit), timeoutSeconds),
                _ => new ProcessResult(127, "", $"unknown provider `{provider}`"),
            };

            if (process.ExitCode == 0)
            {
                hits = providerKind switch
                {
                    "miller" => SearchQualityParsers.ParseMillerJson(providerId, process.Stdout),
                    "julie" => SearchQualityParsers.ParseJulieStandaloneJson(providerId, process.Stdout),
                    "eros" => SearchQualityParsers.ParseErosJson(providerId, process.Stdout),
                    _ => [],
                };
            }
            else
            {
                error = Truncate(string.IsNullOrWhiteSpace(process.Stderr) ? process.Stdout : process.Stderr, 500);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            process = new ProcessResult(1, "", ex.Message);
            error = ex.Message;
        }
        stopwatch.Stop();

        SearchCaseScore score = SearchQualityScorer.Score(providerId, searchCase, hits);
        return new SearchProviderResult
        {
            Provider = providerId,
            Repository = repo.Name,
            CaseId = searchCase.Id,
            Query = searchCase.Query,
            ExitCode = process.ExitCode,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Error = error,
            Hits = hits.ToList(),
            Score = score,
        };
    }

    private static IReadOnlyList<string> MillerArgs(RepositorySpec repo, SearchCaseSpec searchCase, int limit)
    {
        var args = new List<string>
        {
            "search", searchCase.Query,
            "--workspace", ExpandHome(repo.Root),
            "--mode", searchCase.Mode,
            "--limit", limit.ToString(CultureInfo.InvariantCulture),
            "--json",
        };
        AddCommonSearchOptions(args, searchCase);
        return args;
    }

    public static IReadOnlyList<string> BuildJulieArgs(RepositorySpec repo, SearchCaseSpec searchCase, int limit)
    {
        var args = new List<string>
        {
            "search", searchCase.Query,
            "--workspace", ExpandHome(repo.Root),
            "--standalone",
            "--json",
            "--limit", limit.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(searchCase.FilePattern))
        {
            args.Add("--file-pattern");
            args.Add(searchCase.FilePattern);
        }
        if (!string.IsNullOrWhiteSpace(searchCase.Language))
        {
            args.Add("--language");
            args.Add(searchCase.Language);
        }
        if (searchCase.ExcludeTests == true)
            args.Add("--exclude-tests");
        return args;
    }

    private static IReadOnlyList<string> ErosArgs(string providerId, RepositorySpec repo, SearchCaseSpec searchCase, int limit)
    {
        var args = new List<string>
        {
            "search", searchCase.Query,
            "--workspace-id", repo.ErosWorkspaceId ?? repo.Name,
            "--limit", limit.ToString(CultureInfo.InvariantCulture),
            "--json",
        };
        string[] parts = providerId.Split(':', 2);
        if (parts.Length == 2 && parts[1].Length > 0)
        {
            args.Add("--backend");
            args.Add(parts[1]);
        }
        if (searchCase.ExcludeTests is not null)
            args.Add(searchCase.ExcludeTests.Value ? "--exclude-tests" : "--include-tests");
        return args;
    }

    private static void AddCommonSearchOptions(List<string> args, SearchCaseSpec searchCase)
    {
        if (!string.IsNullOrWhiteSpace(searchCase.Regions))
        {
            args.Add("--regions");
            args.Add(searchCase.Regions);
        }
        if (!string.IsNullOrWhiteSpace(searchCase.FilePattern))
        {
            args.Add("--file-pattern");
            args.Add(searchCase.FilePattern);
        }
        if (!string.IsNullOrWhiteSpace(searchCase.Language))
        {
            args.Add("--language");
            args.Add(searchCase.Language);
        }
        if (searchCase.ExcludeTests is not null)
            args.Add(searchCase.ExcludeTests.Value ? "--exclude-tests" : "--include-tests");
    }

    private static CommandSpec MillerCommand(OptionBag options) =>
        CommandSpec.Parse(options.Value("miller-command")
            ?? Environment.GetEnvironmentVariable("MILLER_SEARCH_QUALITY_MILLER_COMMAND")
            ?? (File.Exists(Path.Combine("src", "Miller.Server", "Miller.Server.csproj"))
                ? "dotnet run -c Release --project src/Miller.Server/Miller.Server.csproj --"
                : "miller"));

    public static string ResolveDefaultJulieCommand(string julieRoot = "/Users/murphy/source/julie")
    {
        string release = Path.Combine(julieRoot, "target", "release", "julie-server");
        if (File.Exists(release))
            return release;

        string debug = Path.Combine(julieRoot, "target", "debug", "julie-server");
        return File.Exists(debug) ? debug : "julie-server";
    }

    private static CommandSpec JulieCommand(OptionBag options) =>
        CommandSpec.Parse(options.Value("julie-command")
            ?? Environment.GetEnvironmentVariable("MILLER_SEARCH_QUALITY_JULIE_COMMAND")
            ?? ResolveDefaultJulieCommand());

    private static CommandSpec ErosCommand(OptionBag options) =>
        CommandSpec.Parse(options.Value("eros-command")
            ?? Environment.GetEnvironmentVariable("MILLER_SEARCH_QUALITY_EROS_COMMAND")
            ?? (File.Exists("/Users/murphy/source/eros/.venv/bin/eros")
                ? "/Users/murphy/source/eros/.venv/bin/eros"
                : "eros"));

    private static ProcessResult RunProcess(CommandSpec command, IReadOnlyList<string> args, int timeoutSeconds)
    {
        using var process = new Process();
        process.StartInfo.FileName = command.FileName;
        foreach (string arg in command.Arguments)
            process.StartInfo.ArgumentList.Add(arg);
        foreach (string arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;

        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new ProcessResult(124, "", $"timed out after {timeoutSeconds}s");
        }
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static void WriteResultRow(TextWriter stdout, SearchProviderResult result)
    {
        string rank = result.Score.MatchedRank?.ToString(CultureInfo.InvariantCulture) ?? "miss";
        string status = result.Error is null ? "ok" : "error";
        stdout.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{result.Provider},{result.Repository},{result.CaseId},{rank},{result.Score.HitCount},{result.DurationMs},{status}"));
    }

    private static IEnumerable<string> SplitCsv(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string DefaultCasesPath() =>
        Path.Combine(".miller", "eval", "search-quality", "cases.json");

    private static SearchQualitySuite StarterSuite() => new()
    {
        Repositories =
        [
            new RepositorySpec { Name = "hermes-agent", Root = "~/source/hermes-agent", Language = "python" },
            new RepositorySpec { Name = "openclaw", Root = "~/source/openclaw", Language = "typescript" },
            new RepositorySpec { Name = "MyraNext", Root = "~/source/MyraNext", Language = "csharp" },
            new RepositorySpec { Name = "miller", Root = ".", Language = "csharp" },
        ],
        Cases =
        [
            new SearchCaseSpec
            {
                Id = "hermes-python-class-trajectory-compressor",
                Repository = "hermes-agent",
                Query = "TrajectoryCompressor",
                Language = "python",
                Tags = ["python", "symbol"],
                Expected = [new SearchExpectation { Path = "trajectory_compressor.py", Symbol = "TrajectoryCompressor" }],
            },
            new SearchCaseSpec
            {
                Id = "hermes-python-function-atomic-json-write",
                Repository = "hermes-agent",
                Query = "atomic_json_write",
                Language = "python",
                Tags = ["python", "symbol"],
                Expected = [new SearchExpectation { Path = "utils.py", Symbol = "atomic_json_write" }],
            },
            new SearchCaseSpec
            {
                Id = "openclaw-typescript-media-server",
                Repository = "openclaw",
                Query = "media/server",
                Mode = "file",
                FilePattern = "src/media/**",
                Language = "typescript",
                Tags = ["typescript", "file"],
                Expected = [new SearchExpectation { Path = "src/media/server.ts" }],
            },
            new SearchCaseSpec
            {
                Id = "openclaw-typescript-image-ops",
                Repository = "openclaw",
                Query = "image-ops",
                Mode = "file",
                FilePattern = "src/media/**",
                Language = "typescript",
                Tags = ["typescript", "file"],
                Expected = [new SearchExpectation { Path = "src/media/image-ops.ts" }],
            },
            new SearchCaseSpec
            {
                Id = "myranext-csharp-cache-orchestrator",
                Repository = "MyraNext",
                Query = "CacheOrchestrator",
                Language = "csharp",
                Tags = ["csharp", "symbol"],
                Expected = [new SearchExpectation { Path = "MyraNext.Core/Caching/CacheOrchestrator.cs", Symbol = "CacheOrchestrator" }],
            },
            new SearchCaseSpec
            {
                Id = "myranext-vue-report-menu-editor",
                Repository = "MyraNext",
                Query = "ReportMenuEditor",
                Language = "vue",
                Tags = ["vue", "symbol"],
                Expected = [new SearchExpectation { Path = "MyraNext.Web/ClientApp/src/pages/ReportMenuEditor.vue", Symbol = "ReportMenuEditor" }],
            },
            new SearchCaseSpec
            {
                Id = "myranext-sql-report-menu-items",
                Repository = "MyraNext",
                Query = "ReportMenuItems",
                Mode = "file",
                Tags = ["sql", "file"],
                Expected = [new SearchExpectation { Path = "MyraNext.SqlDB/dbo/Tables/ReportMenuItems.sql" }],
            },
            new SearchCaseSpec
            {
                Id = "miller-source-error-string-content-corpus",
                Repository = "miller",
                Query = "content.db schema_version",
                Mode = "source",
                Tags = ["csharp", "source", "error-string"],
                Expected = [new SearchExpectation { Path = "ContentCorpusExportReader.cs" }],
            },
            new SearchCaseSpec
            {
                Id = "miller-source-assertion-text",
                Repository = "miller",
                Query = "Assert.Contains",
                Mode = "source",
                Tags = ["csharp", "source", "assertion"],
                Expected = [new SearchExpectation { Path = "tests/Miller.Tests" }],
            },
            new SearchCaseSpec
            {
                Id = "miller-content-config-key",
                Repository = "miller",
                Query = "MILLER_REGION_INDEX",
                Mode = "content",
                Tags = ["config-key", "content"],
                Expected = [new SearchExpectation { Path = "README.md" }],
            },
            new SearchCaseSpec
            {
                Id = "miller-content-docs-phrase",
                Repository = "miller",
                Query = "content corpus",
                Mode = "content",
                Tags = ["docs", "content"],
                Expected = [new SearchExpectation { Path = "docs/contracts/content-corpus-v1.md" }],
            },
            new SearchCaseSpec
            {
                Id = "miller-external-imported-log",
                Repository = "miller",
                Query = "ExternalToolExportMarker",
                Mode = "external",
                Tags = ["external", "content"],
                Expected = [new SearchExpectation { Kind = "external_file" }],
            },
            new SearchCaseSpec
            {
                Id = "miller-web-imported-page",
                Repository = "miller",
                Query = "WebToolExportMarker",
                Mode = "web",
                Tags = ["web", "content"],
                Expected = [new SearchExpectation { Kind = "web" }],
            },
        ],
    };

    private static string ExpandHome(string path)
    {
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static int UnknownCommand(string command, TextWriter stderr)
    {
        stderr.WriteLine($"unknown command: {command}");
        return 2;
    }

    private static void WriteUsage(TextWriter stdout)
    {
        stdout.WriteLine("""
            Usage:
              dotnet run --project tools/Miller.SearchQuality -- init [--cases PATH] [--force]
              dotnet run --project tools/Miller.SearchQuality -- run [--cases PATH] [--providers miller,julie,eros] [--limit N]
                [--repo NAME] [--case ID] [--tag TAG]

            Generated cases and run artifacts default to .miller/eval/search-quality/.
            Override tool commands with --miller-command, --julie-command, --eros-command or the matching
            MILLER_SEARCH_QUALITY_*_COMMAND environment variables.
            """);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed record CommandSpec(string FileName, IReadOnlyList<string> Arguments)
    {
        public static CommandSpec Parse(string command)
        {
            var parts = SplitCommand(command).ToList();
            if (parts.Count == 0)
                throw new InvalidOperationException("empty command");
            return new CommandSpec(parts[0], parts.Skip(1).ToList());
        }

        private static IEnumerable<string> SplitCommand(string command)
        {
            var current = new StringBuilder();
            bool inSingle = false;
            bool inDouble = false;
            foreach (char c in command)
            {
                if (c == '\'' && !inDouble)
                {
                    inSingle = !inSingle;
                    continue;
                }
                if (c == '"' && !inSingle)
                {
                    inDouble = !inDouble;
                    continue;
                }
                if (char.IsWhiteSpace(c) && !inSingle && !inDouble)
                {
                    if (current.Length > 0)
                    {
                        yield return current.ToString();
                        current.Clear();
                    }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0)
                yield return current.ToString();
        }
    }

    private sealed class OptionBag
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public static OptionBag Parse(IEnumerable<string> args)
        {
            var bag = new OptionBag();
            string[] list = args.ToArray();
            for (int i = 0; i < list.Length; i++)
            {
                string arg = list[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                    continue;
                string key = arg[2..];
                string? value = null;
                int equals = key.IndexOf('=', StringComparison.Ordinal);
                if (equals >= 0)
                {
                    value = key[(equals + 1)..];
                    key = key[..equals];
                }
                else if (i + 1 < list.Length && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = list[++i];
                }
                _ = bag._values.TryAdd(key, value ?? "true");
            }
            return bag;
        }

        public bool Has(string name) => _values.ContainsKey(name);

        public string? Value(string name) =>
            _values.TryGetValue(name, out string? value) && value != "true" ? value : null;

        public int Int(string name, int defaultValue)
        {
            string? value = Value(name);
            return value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : defaultValue;
        }
    }
}
