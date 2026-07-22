namespace RetrievalEval;

/// <summary>Validates and scores paired task-completion measurements without retaining task-level data.</summary>
public static class TaskCompletionScorer
{
    const int PrimaryMinimumPairs = 30;
    const int SafetyMinimumPairs = 5;
    const int SubgroupMinimumPairs = 5;
    const double WilsonZ = 1.959963984540054;

    public static TaskCompletionReport Score(
        IReadOnlyList<TaskManifestRow> tasks,
        IReadOnlyList<TaskArmResult> baseline,
        IReadOnlyList<TaskArmResult> candidate)
    {
        var taskById = ValidateTasks(tasks);
        var baselineById = ValidateResults(baseline, "Baseline");
        var candidateById = ValidateResults(candidate, "Candidate");
        ValidateExactTaskSet(taskById, baselineById, "Baseline");
        ValidateExactTaskSet(taskById, candidateById, "Candidate");

        var pairs = tasks
            .Select(task => new TaskPair(task, baselineById[task.TaskId], candidateById[task.TaskId]))
            .ToList();
        var completion = Completion(pairs);
        var safetyPairs = pairs
            .Where(pair => pair.Task.QueryProfile is "identifier" or "path")
            .ToList();

        return new TaskCompletionReport
        {
            PairCount = pairs.Count,
            Completion = completion,
            PrimaryGate = PrimaryGate(pairs.Count, completion),
            IdentifierPathSafety = SafetyGate(safetyPairs),
            Diagnostics = Diagnostics(pairs),
            ByRepo = Group(pairs, pair => pair.Task.Repo),
            ByLanguage = Group(pairs, pair => pair.Task.Language),
            ByQueryProfile = Group(pairs, pair => pair.Task.QueryProfile),
        };
    }

    static Dictionary<string, TaskManifestRow> ValidateTasks(IReadOnlyList<TaskManifestRow> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var byId = new Dictionary<string, TaskManifestRow>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.TaskId))
                throw new InvalidOperationException("Task manifest task_id must be nonblank.");
            if (string.IsNullOrWhiteSpace(task.Repo))
                throw new InvalidOperationException("Task manifest repo must be nonblank.");
            if (string.IsNullOrWhiteSpace(task.Language))
                throw new InvalidOperationException("Task manifest language must be nonblank.");
            if (!TaskQueryProfiles.All.Contains(task.QueryProfile))
                throw new InvalidOperationException("Task manifest query_profile is invalid.");
            if (!byId.TryAdd(task.TaskId, task))
                throw new InvalidOperationException("Duplicate task_id in task manifest.");
        }
        return byId;
    }

    static Dictionary<string, TaskArmResult> ValidateResults(IReadOnlyList<TaskArmResult> results, string label)
    {
        ArgumentNullException.ThrowIfNull(results);
        var byId = new Dictionary<string, TaskArmResult>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (string.IsNullOrWhiteSpace(result.TaskId))
                throw new InvalidOperationException($"{label} result task_id must be nonblank.");
            if (result.DurationMs < 0)
                throw new InvalidOperationException($"{label} result duration_ms must be nonnegative.");
            if (result.ToolCalls < 0)
                throw new InvalidOperationException($"{label} result tool_calls must be nonnegative.");
            if (result.SearchCalls < 0)
                throw new InvalidOperationException($"{label} result search_calls must be nonnegative.");
            if (result.ZeroResultSearchCalls < 0)
                throw new InvalidOperationException($"{label} result zero_result_search_calls must be nonnegative.");
            if (result.SearchCalls > result.ToolCalls)
                throw new InvalidOperationException($"{label} result search_calls must not exceed tool_calls.");
            if (result.ZeroResultSearchCalls > result.SearchCalls)
                throw new InvalidOperationException($"{label} result zero_result_search_calls must not exceed search_calls.");
            if (!byId.TryAdd(result.TaskId, result))
                throw new InvalidOperationException($"Duplicate task_id in {label.ToLowerInvariant()} results.");
        }
        return byId;
    }

    static void ValidateExactTaskSet(
        IReadOnlyDictionary<string, TaskManifestRow> tasks,
        IReadOnlyDictionary<string, TaskArmResult> results,
        string label)
    {
        if (tasks.Count != results.Count || tasks.Keys.Any(taskId => !results.ContainsKey(taskId)))
            throw new InvalidOperationException($"{label} task-id set does not match task manifest.");
    }

    static TaskCompletionCells Completion(IReadOnlyCollection<TaskPair> pairs) => new()
    {
        BothCompleted = pairs.Count(pair => pair.Baseline.Completed && pair.Candidate.Completed),
        CandidateOnly = pairs.Count(pair => !pair.Baseline.Completed && pair.Candidate.Completed),
        BaselineOnly = pairs.Count(pair => pair.Baseline.Completed && !pair.Candidate.Completed),
        NeitherCompleted = pairs.Count(pair => !pair.Baseline.Completed && !pair.Candidate.Completed),
    };

    static TaskCompletionGate PrimaryGate(int pairCount, TaskCompletionCells completion)
    {
        var discordant = completion.CandidateOnly + completion.BaselineOnly;
        var interval = Wilson(completion.CandidateOnly, discordant);
        var verdict = pairCount < PrimaryMinimumPairs
            ? TaskVerdicts.Underpowered
            : discordant == 0 || interval.Lower <= 0.5
                ? TaskVerdicts.Fail
                : TaskVerdicts.Pass;

        return new TaskCompletionGate
        {
            Verdict = verdict,
            PairCount = pairCount,
            DiscordantPairCount = discordant,
            CandidateWinShare = discordant == 0 ? null : (double)completion.CandidateOnly / discordant,
            WilsonLowerBound = discordant == 0 ? null : interval.Lower,
            WilsonUpperBound = discordant == 0 ? null : interval.Upper,
        };
    }

    static TaskSafetyGate SafetyGate(IReadOnlyCollection<TaskPair> pairs)
    {
        var completion = Completion(pairs);
        return new TaskSafetyGate
        {
            PairCount = pairs.Count,
            Completion = completion,
            Verdict = pairs.Count < SafetyMinimumPairs
                ? TaskVerdicts.Underpowered
                : completion.BaselineOnly > completion.CandidateOnly
                    ? TaskVerdicts.Fail
                    : TaskVerdicts.Pass,
        };
    }

    static TaskArmDiagnostics Diagnostics(IReadOnlyCollection<TaskPair> pairs) => new()
    {
        Baseline = Aggregate(pairs.Select(pair => pair.Baseline)),
        Candidate = Aggregate(pairs.Select(pair => pair.Candidate)),
    };

    static TaskArmAggregate Aggregate(IEnumerable<TaskArmResult> source)
    {
        var results = source as IReadOnlyCollection<TaskArmResult> ?? source.ToList();
        var count = results.Count;
        var totalDuration = results.Sum(result => result.DurationMs);
        var totalTools = results.Sum(result => (long)result.ToolCalls);
        var totalSearch = results.Sum(result => (long)result.SearchCalls);
        var totalZero = results.Sum(result => (long)result.ZeroResultSearchCalls);
        var completed = results.Count(result => result.Completed);

        return new TaskArmAggregate
        {
            PairCount = count,
            CompletedCount = completed,
            CompletionRate = Divide(completed, count),
            TotalDurationMs = totalDuration,
            MeanDurationMs = Divide(totalDuration, count),
            TotalToolCalls = totalTools,
            MeanToolCalls = Divide(totalTools, count),
            TotalSearchCalls = totalSearch,
            MeanSearchCalls = Divide(totalSearch, count),
            TotalZeroResultSearchCalls = totalZero,
            MeanZeroResultSearchCalls = Divide(totalZero, count),
            ZeroResultSearchRate = Divide(totalZero, totalSearch),
        };
    }

    static IReadOnlyDictionary<string, TaskSubgroupReport> Group(
        IReadOnlyCollection<TaskPair> pairs,
        Func<TaskPair, string> selector)
    {
        var groups = new SortedDictionary<string, TaskSubgroupReport>(StringComparer.Ordinal);
        foreach (var group in pairs
            .GroupBy(selector, StringComparer.Ordinal)
            .Where(group => group.Count() >= SubgroupMinimumPairs)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var members = group.ToList();
            groups.Add(group.Key, new TaskSubgroupReport
            {
                PairCount = members.Count,
                Completion = Completion(members),
                Diagnostics = Diagnostics(members),
            });
        }
        return groups;
    }

    static (double Lower, double Upper) Wilson(int wins, int total)
    {
        if (total == 0) return (0.0, 0.0);
        var proportion = (double)wins / total;
        var zSquared = WilsonZ * WilsonZ;
        var denominator = 1.0 + zSquared / total;
        var center = (proportion + zSquared / (2.0 * total)) / denominator;
        var halfWidth = WilsonZ * Math.Sqrt((proportion * (1.0 - proportion) + zSquared / (4.0 * total)) / total) / denominator;
        return (Math.Max(0.0, center - halfWidth), Math.Min(1.0, center + halfWidth));
    }

    static double Divide(long numerator, long denominator) =>
        denominator == 0 ? 0.0 : (double)numerator / denominator;

    sealed record TaskPair(TaskManifestRow Task, TaskArmResult Baseline, TaskArmResult Candidate);
}
