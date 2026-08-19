namespace Miller.Testing;

public static class ContinuousTestPreEditConfidence
{
    private static readonly IReadOnlyDictionary<string, int> StatePriority =
        new Dictionary<string, int>
        {
            ["failed"] = 0,
            ["weak"] = 1,
            ["stale"] = 2,
            ["unknown"] = 3,
            ["untested"] = 4,
            ["likely"] = 5,
            ["covered"] = 6,
            ["verified"] = 7,
        };

    public static IReadOnlyDictionary<string, object?> BuildPreEditConfidencePack(
        string workspaceId,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> likelyTests,
        IReadOnlyDictionary<string, object?> confidenceSummary,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? confidenceSummaries = null,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? qualityWarnings = null,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? artifactActions = null,
        int likelyTestLimit = 5)
    {
        confidenceSummaries ??= [];
        qualityWarnings ??= [];
        artifactActions ??= [];

        Dictionary<string, object?>[] tests = likelyTests.Select(RowDict).ToArray();
        Dictionary<string, object?>[] compactTests = tests.Take(likelyTestLimit).ToArray();
        (string state, double score) = AggregateConfidence(confidenceSummaries, tests.Length > 0);
        Dictionary<string, object?>[] missingArtifactActions = artifactActions
            .Where(action => Convert.ToString(action.GetValueOrDefault("code"))?.StartsWith("import_", StringComparison.Ordinal) == true)
            .Select(RowDict)
            .ToArray();

        return new Dictionary<string, object?>
        {
            ["workspace_id"] = workspaceId,
            ["state"] = state,
            ["score"] = score,
            ["summary"] = Summary(confidenceSummary, tests, compactTests, qualityWarnings),
            ["likely_tests"] = compactTests,
            ["verification"] = Verification(compactTests),
            ["artifact_actions"] = missingArtifactActions,
            ["quality_warnings"] = qualityWarnings.Select(RowDict).ToArray(),
            ["omitted"] = new Dictionary<string, object?>
            {
                ["likely_tests"] = Math.Max(0, tests.Length - compactTests.Length),
            },
            ["limitations"] = Limitations(state, missingArtifactActions, tests),
        };
    }

    private static Dictionary<string, object?> RowDict(IReadOnlyDictionary<string, object?> row) =>
        row.Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static (string State, double Score) AggregateConfidence(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> confidenceSummaries,
        bool hasLikelyTests)
    {
        if (confidenceSummaries.Count == 0)
            return hasLikelyTests ? ("likely", 0.55) : ("unknown", 0.0);

        string[] states = confidenceSummaries
            .Select(row => Convert.ToString(row.GetValueOrDefault("state")) ?? "unknown")
            .ToArray();
        string state = states.MinBy(value => StatePriority.GetValueOrDefault(value, StatePriority["unknown"])) ?? "unknown";
        double[] scores = confidenceSummaries
            .Select(row => row.GetValueOrDefault("score"))
            .OfType<IConvertible>()
            .Select(value => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        return scores.Length > 0 ? (state, scores.Min()) : (state, ScoreForState(state));
    }

    private static Dictionary<string, int> Summary(
        IReadOnlyDictionary<string, object?> confidenceSummary,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> tests,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> compactTests,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> qualityWarnings) =>
        new()
        {
            ["changed_symbol_count"] = IntValue(confidenceSummary, "changed_symbol_count"),
            ["affected_symbol_count"] = IntValue(confidenceSummary, "affected_symbol_count"),
            ["likely_test_count"] = tests.Count,
            ["returned_likely_test_count"] = compactTests.Count,
            ["quality_warning_count"] = qualityWarnings.Count,
            ["dependency_impact_count"] = IntValue(confidenceSummary, "dependency_impact_count"),
            ["deleted_file_count"] = IntValue(confidenceSummary, "deleted_file_count"),
        };

    private static Dictionary<string, object?> Verification(IReadOnlyList<IReadOnlyDictionary<string, object?>> tests)
    {
        string[] selectors = tests
            .Select(row => Convert.ToString(row.GetValueOrDefault("selector")) ?? "")
            .Where(selector => selector.Length > 0)
            .Where(IsPytestCompatible)
            .ToArray();

        if (selectors.Length == 0)
        {
            return new Dictionary<string, object?>
            {
                ["command"] = null,
                ["selectors"] = Array.Empty<string>(),
                ["reason"] = tests.Count == 0
                    ? "no likely tests were identified"
                    : "no pytest-compatible selectors were identified",
            };
        }

        return new Dictionary<string, object?>
        {
            ["command"] = "pytest " + string.Join(" ", selectors) + " -q",
            ["selectors"] = selectors,
        };
    }

    private static bool IsPytestCompatible(string selector)
    {
        string selectorPath = selector.Split("::", count: 2, StringSplitOptions.None)[0];
        return selectorPath.EndsWith(".py", StringComparison.Ordinal) &&
            IsProbablePythonTestPath(selectorPath);
    }

    private static bool IsProbablePythonTestPath(string path)
    {
        int slash = path.LastIndexOf('/');
        string basename = slash >= 0 ? path[(slash + 1)..] : path;
        return path.StartsWith("tests/", StringComparison.Ordinal) ||
            path.Contains("/tests/", StringComparison.Ordinal) ||
            basename.StartsWith("test_", StringComparison.Ordinal) ||
            basename.EndsWith("_test.py", StringComparison.Ordinal);
    }

    private static string[] Limitations(
        string state,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> artifactActions,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> tests)
    {
        var limitations = new List<string>();
        if (artifactActions.Count > 0)
            limitations.Add("test or coverage artifacts are missing");

        if (state is "unknown" or "stale" or "untested")
            limitations.Add($"confidence state is {state}");

        if (tests.Count == 0)
            limitations.Add("no likely tests identified");

        return limitations.ToArray();
    }

    private static int IntValue(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? value) || value is null)
            return 0;

        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double ScoreForState(string state) =>
        state switch
        {
            "verified" => 0.9,
            "covered" => 0.72,
            "likely" => 0.55,
            "weak" => 0.4,
            "failed" => 0.2,
            "untested" => 0.1,
            "unknown" => 0.0,
            "stale" => 0.0,
            _ => 0.0,
        };
}
