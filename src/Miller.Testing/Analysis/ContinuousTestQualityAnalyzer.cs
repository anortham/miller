using System.Text.RegularExpressions;

namespace Miller.Testing;

public sealed record ContinuousTestQualityFinding(
    string Id,
    string WorkspaceId,
    string TestCaseId,
    string FindingType,
    string Severity,
    double Confidence,
    string Explanation,
    IReadOnlyDictionary<string, object?> Evidence,
    string? FilePath = null,
    string? ContentHash = null,
    string? SymbolName = null,
    string? SymbolPath = null);

public sealed record ContinuousImplementationQualityFinding(
    string Id,
    string WorkspaceId,
    string FindingType,
    string Severity,
    double Confidence,
    string Explanation,
    IReadOnlyDictionary<string, object?> Evidence,
    string? FilePath = null,
    string? ContentHash = null,
    string? SymbolName = null,
    string? SymbolPath = null);

public sealed record ContinuousTestQualitySymbol(
    string Id,
    string WorkspaceId,
    string Name,
    string FilePath,
    int StartLine,
    int EndLine);

public sealed class ContinuousTestQualityAnalyzer
{
    private static readonly Regex FunctionStart =
        new(@"^(?<indent>\s*)def\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

    private static readonly Regex CallName =
        new(@"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

    private readonly string[] _lines;
    private IReadOnlyDictionary<string, PythonFunction>? _functions;

    public ContinuousTestQualityAnalyzer(string content)
    {
        _lines = (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }

    public int ParseCount { get; private set; }

    public static IReadOnlyList<ContinuousTestQualityFinding> AnalyzeTestQuality(
        string content,
        ContinuousTestCase testCase,
        ContinuousTestQualitySymbol symbol) =>
        new ContinuousTestQualityAnalyzer(content).AnalyzeTest(testCase, symbol);

    public static IReadOnlyList<ContinuousImplementationQualityFinding> AnalyzeImplementationQuality(
        string content,
        ContinuousTestQualitySymbol symbol) =>
        new ContinuousTestQualityAnalyzer(content).AnalyzeImplementation(symbol);

    public IReadOnlyList<ContinuousTestQualityFinding> AnalyzeTest(
        ContinuousTestCase testCase,
        ContinuousTestQualitySymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(symbol);

        if (testCase.Role is not (ContinuousTestRole.TestCase or ContinuousTestRole.ParameterizedTest))
            return [];

        PythonFunction? function = Function(symbol.Name);
        string bodyText = SymbolText(symbol);
        var findings = new List<ContinuousTestQualityFinding>();

        if (IsPlaceholderBody(function, bodyText))
        {
            findings.Add(TestFinding(
                testCase,
                symbol,
                "placeholder_test",
                "test body is placeholder",
                new Dictionary<string, object?> { ["body"] = CleanBody(bodyText) }));
            return findings;
        }

        if (SkipWithoutReason(function))
        {
            findings.Add(TestFinding(
                testCase,
                symbol,
                "skip_without_reason",
                "test is skipped without a reason",
                new Dictionary<string, object?>()));
            return findings;
        }

        if (HasTautologicalAssertion(function, bodyText))
        {
            findings.Add(TestFinding(
                testCase,
                symbol,
                "tautological_assertion",
                "test asserts a tautology",
                new Dictionary<string, object?>()));
            return findings;
        }

        string? duplicate = CopyPasteDuplicate(symbol.Name);
        if (duplicate is not null)
        {
            findings.Add(TestFinding(
                testCase,
                symbol,
                "copy_paste_test",
                "test body duplicates another test with only literal changes",
                new Dictionary<string, object?> { ["duplicate"] = duplicate }));
            return findings;
        }

        if (!HasAssertion(function, bodyText))
        {
            IReadOnlyList<string> calls = CallNames(function, bodyText);
            if (calls.Count == 0)
                return findings;

            if (LooksSmokeOnly(function, bodyText))
            {
                findings.Add(TestFinding(
                    testCase,
                    symbol,
                    "smoke_only",
                    "test calls code but has no assertion",
                    new Dictionary<string, object?> { ["calls"] = calls }));
            }
            else
            {
                findings.Add(TestFinding(
                    testCase,
                    symbol,
                    "no_assertion",
                    "test has identifier evidence but no assertion",
                    new Dictionary<string, object?> { ["identifier_evidence"] = calls }));
            }
        }

        return findings;
    }

    public IReadOnlyList<ContinuousImplementationQualityFinding> AnalyzeImplementation(ContinuousTestQualitySymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        PythonFunction? function = Function(symbol.Name);
        string bodyText = SymbolText(symbol);
        if (IsPlaceholderBody(function, bodyText))
        {
            return
            [
                ImplementationFinding(
                    symbol,
                    "stub_implementation",
                    "implementation body is a placeholder",
                    new Dictionary<string, object?> { ["body"] = CleanBody(bodyText) }),
            ];
        }

        if (CannedReturn(function, bodyText))
        {
            return
            [
                ImplementationFinding(
                    symbol,
                    "canned_return",
                    "implementation returns a canned literal",
                    new Dictionary<string, object?> { ["body"] = CleanBody(bodyText) }),
            ];
        }

        return [];
    }

    private PythonFunction? Function(string name) =>
        Functions().TryGetValue(name, out PythonFunction? function) ? function : null;

    private IReadOnlyDictionary<string, PythonFunction> Functions()
    {
        if (_functions is not null)
            return _functions;

        ParseCount++;
        var functions = new Dictionary<string, PythonFunction>(StringComparer.Ordinal);
        for (int index = 0; index < _lines.Length; index++)
        {
            Match match = FunctionStart.Match(_lines[index]);
            if (!match.Success)
                continue;

            string name = match.Groups["name"].Value;
            var decorators = new List<string>();
            for (int before = index - 1; before >= 0; before--)
            {
                string line = _lines[before].Trim();
                if (line.StartsWith("@", StringComparison.Ordinal))
                {
                    decorators.Insert(0, line);
                    continue;
                }

                if (line.Length == 0)
                    continue;
                break;
            }

            int end = _lines.Length;
            for (int next = index + 1; next < _lines.Length; next++)
            {
                if (FunctionStart.IsMatch(_lines[next]))
                {
                    end = next;
                    break;
                }
            }

            functions[name] = new PythonFunction(
                Name: name,
                StartLine: index + 1,
                EndLine: end,
                Lines: _lines.Skip(index).Take(end - index).ToArray(),
                Decorators: decorators);
        }

        _functions = functions;
        return _functions;
    }

    private string SymbolText(ContinuousTestQualitySymbol symbol)
    {
        int start = Math.Max(0, symbol.StartLine - 1);
        int count = Math.Max(0, Math.Min(_lines.Length, symbol.EndLine) - start);
        return string.Join('\n', _lines.Skip(start).Take(count));
    }

    private static bool IsPlaceholderBody(PythonFunction? function, string bodyText)
    {
        string body = CleanBody(function is null ? bodyText : string.Join('\n', BodyLines(function)));
        return body is "pass" or "..." or "raise NotImplementedError" or "raise NotImplementedError()";
    }

    private static bool CannedReturn(PythonFunction? function, string bodyText)
    {
        string body = CleanBody(function is null ? bodyText : string.Join('\n', BodyLines(function)));
        return body is "return True" or "return False" or "return None" or "return 0" or "return \"\"" or "return ''";
    }

    private static bool SkipWithoutReason(PythonFunction? function)
    {
        if (function is null)
            return false;

        return function.Decorators.Any(line =>
            line.Contains("pytest.mark.skip", StringComparison.Ordinal)
            && !line.Contains("reason", StringComparison.OrdinalIgnoreCase)
            && !line.Contains('"', StringComparison.Ordinal)
            && !line.Contains('\'', StringComparison.Ordinal));
    }

    private static bool HasTautologicalAssertion(PythonFunction? function, string bodyText) =>
        LinesFor(function, bodyText).Any(line =>
            line.Trim() is "assert True" or "assert 1 == 1" or "assert 1");

    private static bool HasAssertion(PythonFunction? function, string bodyText) =>
        LinesFor(function, bodyText).Any(line => line.TrimStart().StartsWith("assert ", StringComparison.Ordinal));

    private static bool LooksSmokeOnly(PythonFunction? function, string bodyText) =>
        LinesFor(function, bodyText).Any(line =>
            line.Contains('=', StringComparison.Ordinal)
            && CallName.IsMatch(line)
            && !line.TrimStart().StartsWith("assert ", StringComparison.Ordinal));

    private IReadOnlyList<string> CallNames(PythonFunction? function, string bodyText) =>
        LinesFor(function, bodyText)
            .SelectMany(line => CallName.Matches(line).Select(match => match.Groups["name"].Value))
            .Where(name => name is not "assert" and not "print")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private string? CopyPasteDuplicate(string currentName)
    {
        string current = NormalizedBody(Function(currentName));
        if (string.IsNullOrEmpty(current))
            return null;

        foreach (PythonFunction function in Functions().Values.OrderBy(row => row.StartLine))
        {
            if (function.Name == currentName)
                continue;
            if (NormalizedBody(function) == current)
                return function.Name;
        }

        return null;
    }

    private static string NormalizedBody(PythonFunction? function)
    {
        if (function is null)
            return string.Empty;

        IEnumerable<string> lines = BodyLines(function)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line => Regex.Replace(line, @"\b\d+\b", "<number>"))
            .Select(line => Regex.Replace(line, @"""[^""]*""|'[^']*'", "<string>"));
        return string.Join('\n', lines);
    }

    private static IEnumerable<string> LinesFor(PythonFunction? function, string bodyText) =>
        function is null ? bodyText.Split('\n') : BodyLines(function);

    private static IEnumerable<string> BodyLines(PythonFunction function) =>
        function.Lines.Skip(1);

    private static string CleanBody(string bodyText) =>
        string.Join(
            '\n',
            bodyText.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("def ", StringComparison.Ordinal)));

    private static ContinuousTestQualityFinding TestFinding(
        ContinuousTestCase testCase,
        ContinuousTestQualitySymbol symbol,
        string findingType,
        string explanation,
        IReadOnlyDictionary<string, object?> evidence) =>
        new(
            Id: CtStableIds.StableId("test_quality_finding", testCase.WorkspaceId, testCase.Id, findingType),
            WorkspaceId: testCase.WorkspaceId,
            TestCaseId: testCase.Id,
            FindingType: findingType,
            Severity: "warning",
            Confidence: 0.82,
            Explanation: explanation,
            Evidence: evidence,
            FilePath: testCase.FilePath,
            ContentHash: testCase.ContentHash,
            SymbolName: symbol.Id,
            SymbolPath: symbol.FilePath);

    private static ContinuousImplementationQualityFinding ImplementationFinding(
        ContinuousTestQualitySymbol symbol,
        string findingType,
        string explanation,
        IReadOnlyDictionary<string, object?> evidence) =>
        new(
            Id: CtStableIds.StableId("implementation_quality_finding", symbol.WorkspaceId, symbol.Id, findingType),
            WorkspaceId: symbol.WorkspaceId,
            FindingType: findingType,
            Severity: "warning",
            Confidence: 0.82,
            Explanation: explanation,
            Evidence: evidence,
            FilePath: symbol.FilePath,
            SymbolName: symbol.Id,
            SymbolPath: symbol.FilePath);

    private sealed record PythonFunction(
        string Name,
        int StartLine,
        int EndLine,
        IReadOnlyList<string> Lines,
        IReadOnlyList<string> Decorators);
}

public sealed record ContinuousRealtimeQualityFinding(
    string Code,
    string Severity,
    string Message,
    string Path,
    int? Line = null)
{
    public IReadOnlyDictionary<string, object?> AsWarning() =>
        new Dictionary<string, object?>
        {
            ["code"] = Code,
            ["severity"] = Severity,
            ["message"] = Message,
            ["path"] = Path,
            ["line"] = Line,
        };
}

public static class ContinuousRealtimeQuality
{
    private static readonly Regex FunctionStart =
        new(@"^\s*def\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

    private static readonly Regex DiffHunk =
        new(@"@@\s+-\d+(?:,\d+)?\s+\+(?<line>\d+)(?:,\d+)?\s+@@", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> TestCodes = new Dictionary<string, string>
    {
        ["placeholder_test"] = "test_placeholder",
        ["tautological_assertion"] = "test_tautological_assertion",
        ["no_assertion"] = "test_no_assertion",
        ["smoke_only"] = "test_smoke_only",
        ["skip_without_reason"] = "test_skip_without_reason",
        ["copy_paste_test"] = "test_copy_paste",
    };

    private static readonly IReadOnlyDictionary<string, string> ImplementationCodes = new Dictionary<string, string>
    {
        ["stub_implementation"] = "implementation_stub",
    };

    public static IReadOnlyList<ContinuousRealtimeQualityFinding> AnalyzeNewText(
        string path,
        string? language,
        string newText)
    {
        if (!string.Equals(LanguageForPath(path, language), "python", StringComparison.OrdinalIgnoreCase))
            return [];

        string[] functionNames = FunctionStart.Matches(newText)
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        if (functionNames.Length == 0)
            return FragmentFindings(path, newText);

        var findings = new List<ContinuousRealtimeQualityFinding>();
        foreach (string name in functionNames)
        {
            ContinuousTestQualitySymbol symbol = SymbolForFunction(path, name, newText);
            if (IsTestFunction(path, name))
            {
                ContinuousTestCase testCase = TestCaseForFunction(path, name, symbol);
                findings.AddRange(ContinuousTestQualityAnalyzer.AnalyzeTestQuality(newText, testCase, symbol)
                    .Select(row => TestFinding(path, row.FindingType, row.Explanation, symbol.StartLine)));
            }
            else
            {
                findings.AddRange(ContinuousTestQualityAnalyzer.AnalyzeImplementationQuality(newText, symbol)
                    .Select(row => ImplementationFinding(path, row.FindingType, row.Explanation, symbol.StartLine)));
            }
        }

        return findings;
    }

    public static IReadOnlyList<ContinuousRealtimeQualityFinding> AnalyzeDiff(string diff) =>
        NewTextByPathFromDiff(diff)
            .SelectMany(entry => AnalyzeNewText(entry.Key, language: null, entry.Value))
            .ToArray();

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> WarningsForNewText(
        string path,
        string? language,
        string newText) =>
        AnalyzeNewText(path, language, newText).Select(row => row.AsWarning()).ToArray();

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> WarningsForDiff(string? diff) =>
        string.IsNullOrEmpty(diff)
            ? []
            : AnalyzeDiff(diff).Select(row => row.AsWarning()).ToArray();

    private static ContinuousRealtimeQualityFinding TestFinding(
        string path,
        string findingType,
        string message,
        int? line) =>
        TestCodes.TryGetValue(findingType, out string? code)
            ? new ContinuousRealtimeQualityFinding(code, "warning", message, path, line)
            : new ContinuousRealtimeQualityFinding(findingType, "warning", message, path, line);

    private static ContinuousRealtimeQualityFinding ImplementationFinding(
        string path,
        string findingType,
        string message,
        int? line) =>
        ImplementationCodes.TryGetValue(findingType, out string? code)
            ? new ContinuousRealtimeQualityFinding(code, "warning", message, path, line)
            : new ContinuousRealtimeQualityFinding(findingType, "warning", message, path, line);

    private static IReadOnlyList<ContinuousRealtimeQualityFinding> FragmentFindings(string path, string newText)
    {
        string[] trimmedLines = newText.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        if (trimmedLines.Length == 0)
            return [];

        string text = string.Join('\n', trimmedLines);
        if (IsTestPath(path)
            && !trimmedLines.Any(line => line.StartsWith("assert ", StringComparison.Ordinal))
            && trimmedLines.Any(line => Regex.IsMatch(line, @"\b[A-Za-z_][A-Za-z0-9_]*\s*\(")))
        {
            return [new ContinuousRealtimeQualityFinding("test_no_assertion", "warning", "test has no assertion", path)];
        }

        if (!IsTestPath(path)
            && (text is "pass" or "..." or "raise NotImplementedError" or "raise NotImplementedError()"))
        {
            return [new ContinuousRealtimeQualityFinding("implementation_stub", "warning", "implementation body is a placeholder", path)];
        }

        return [];
    }

    private static ContinuousTestQualitySymbol SymbolForFunction(string path, string name, string text)
    {
        string[] lines = text.Split('\n');
        int lineNumber = 1;
        for (int index = 0; index < lines.Length; index++)
        {
            if (FunctionStart.Match(lines[index]) is { Success: true } match && match.Groups["name"].Value == name)
            {
                lineNumber = index + 1;
                break;
            }
        }

        return new ContinuousTestQualitySymbol(
            Id: CtStableIds.StableId("realtime_symbol", path, name, lineNumber),
            WorkspaceId: "realtime",
            Name: name,
            FilePath: path,
            StartLine: lineNumber,
            EndLine: lines.Length);
    }

    private static ContinuousTestCase TestCaseForFunction(string path, string name, ContinuousTestQualitySymbol symbol) =>
        new(
            Id: CtStableIds.StableId("realtime_test_case", path, name),
            WorkspaceId: "realtime",
            Name: name,
            QualifiedName: name,
            Selector: $"{path}::{name}",
            FilePath: path,
            SymbolName: symbol.Id,
            SymbolPath: path,
            Framework: "pytest",
            Role: ContinuousTestRole.TestCase,
            Source: "realtime",
            Confidence: 1.0);

    private static bool IsTestFunction(string path, string name) =>
        IsTestPath(path) || name.StartsWith("test_", StringComparison.Ordinal);

    private static bool IsTestPath(string path)
    {
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        string name = normalized.Split('/').Last();
        return normalized.Contains("/test/", StringComparison.Ordinal)
            || normalized.Contains("/tests/", StringComparison.Ordinal)
            || normalized.StartsWith("test/", StringComparison.Ordinal)
            || normalized.StartsWith("tests/", StringComparison.Ordinal)
            || name.StartsWith("test_", StringComparison.Ordinal);
    }

    private static string? LanguageForPath(string path, string? language)
    {
        if (!string.IsNullOrWhiteSpace(language))
            return language;

        return Path.GetExtension(path).Equals(".py", StringComparison.OrdinalIgnoreCase) ? "python" : null;
    }

    private static Dictionary<string, string> NewTextByPathFromDiff(string diff)
    {
        string currentPath = string.Empty;
        int currentLine = 1;
        var textByPath = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string line in diff.Split('\n'))
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                currentPath = NormalizeDiffPath(line[4..].Trim());
                textByPath.TryAdd(currentPath, []);
                continue;
            }

            Match hunk = DiffHunk.Match(line);
            if (hunk.Success)
            {
                currentLine = int.Parse(hunk.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(currentPath))
                    continue;

                List<string> target = textByPath[currentPath];
                while (target.Count < currentLine - 1)
                    target.Add(string.Empty);
                target.Add(line[1..]);
                currentLine++;
            }
        }

        return textByPath.ToDictionary(
            entry => entry.Key,
            entry => string.Join('\n', entry.Value),
            StringComparer.Ordinal);
    }

    private static string NormalizeDiffPath(string rawPath)
    {
        string path = rawPath.Split('\t', 2)[0];
        if (path == "/dev/null")
            return path;
        if (path.StartsWith("b/", StringComparison.Ordinal) || path.StartsWith("a/", StringComparison.Ordinal))
            return path[2..];
        return path;
    }
}
