namespace RetrievalEval;

/// <summary>Scores stabilized Miller and Julie agent runs without retaining task-level data.</summary>
public static class AgentEfficiencyScorer
{
    const int SubgroupMinimumTasks = 5;

    public static AgentEfficiencyReport Score(
        IReadOnlyList<AgentTaskManifestRow> tasks,
        IReadOnlyList<AgentRunResult> millerRuns,
        IReadOnlyList<AgentRunResult> julieRuns)
    {
        var taskById = ValidateTasks(tasks);
        var millerByTask = ValidateRuns(millerRuns, taskById, "Miller");
        var julieByTask = ValidateRuns(julieRuns, taskById, "Julie");
        ValidateRerunShapes(tasks, millerByTask, julieByTask);

        var pairs = tasks.Select(task => new StabilizedPair(
            task,
            millerByTask[task.TaskId].Values.OrderBy(run => run.Repetition).ToList(),
            julieByTask[task.TaskId].Values.OrderBy(run => run.Repetition).ToList()))
            .ToList();
        var completion = Completion(pairs);
        var criticalLosses = pairs.Count(pair => pair.Task.EvidenceCritical && pair.JulieCompleted && !pair.MillerCompleted);
        var correctness = new AgentCorrectnessGate
        {
            Verdict = completion.MillerOnly >= completion.JulieOnly && criticalLosses == 0
                ? AgentEfficiencyVerdicts.Pass
                : AgentEfficiencyVerdicts.Fail,
            MillerCompletedCount = completion.BothCompleted + completion.MillerOnly,
            JulieCompletedCount = completion.BothCompleted + completion.JulieOnly,
            CriticalLossCount = criticalLosses,
        };
        var bothPass = pairs.Where(pair => pair.MillerCompleted && pair.JulieCompleted).ToList();
        var millerMetrics = Metrics(bothPass.Select(pair => pair.MillerRuns));
        var julieMetrics = Metrics(bothPass.Select(pair => pair.JulieRuns));
        var efficiency = Efficiency(bothPass.Count, millerMetrics, julieMetrics);

        return new AgentEfficiencyReport
        {
            Verdict = correctness.Verdict == AgentEfficiencyVerdicts.Pass && efficiency.Verdict == AgentEfficiencyVerdicts.Pass
                ? AgentEfficiencyVerdicts.Pass
                : AgentEfficiencyVerdicts.Fail,
            TaskCount = tasks.Count,
            Completion = completion,
            Correctness = correctness,
            Efficiency = efficiency,
            Miller = millerMetrics,
            Julie = julieMetrics,
            FailureCounts = new AgentFailureCounts
            {
                Miller = FailureCounts(millerRuns),
                Julie = FailureCounts(julieRuns),
            },
            ByWorkflow = Group(pairs, pair => pair.Task.WorkflowClass),
            ByRepo = Group(pairs, pair => pair.Task.Repo),
            ByLanguage = Group(pairs, pair => pair.Task.Language),
        };
    }

    static Dictionary<string, AgentTaskManifestRow> ValidateTasks(IReadOnlyList<AgentTaskManifestRow> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0) throw new InvalidOperationException("Task manifest must not be empty.");

        var byId = new Dictionary<string, AgentTaskManifestRow>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.TaskId))
                throw new InvalidOperationException("Task manifest task_id must be nonblank.");
            if (string.IsNullOrWhiteSpace(task.Repo))
                throw new InvalidOperationException("Task manifest repo must be nonblank.");
            if (string.IsNullOrWhiteSpace(task.Language))
                throw new InvalidOperationException("Task manifest language must be nonblank.");
            if (IsPathLike(task.Repo))
                throw new InvalidOperationException("Task manifest repo must be a non-path label.");
            if (IsPathLike(task.Language))
                throw new InvalidOperationException("Task manifest language must be a non-path label.");
            if (!AgentWorkflowClasses.All.Contains(task.WorkflowClass))
                throw new InvalidOperationException("Task manifest workflow_class is unsupported.");
            var expectedCritical = AgentWorkflowClasses.EvidenceCritical.Contains(task.WorkflowClass);
            if (task.EvidenceCritical != expectedCritical)
                throw new InvalidOperationException("Task manifest evidence_critical does not match workflow_class.");
            if (!byId.TryAdd(task.TaskId, task))
                throw new InvalidOperationException("Duplicate task_id in task manifest.");
        }
        return byId;
    }

    static Dictionary<string, SortedDictionary<int, AgentRunResult>> ValidateRuns(
        IReadOnlyList<AgentRunResult> runs,
        IReadOnlyDictionary<string, AgentTaskManifestRow> tasks,
        string arm)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var byTask = new Dictionary<string, SortedDictionary<int, AgentRunResult>>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            ValidateRun(run, arm);
            if (!byTask.TryGetValue(run.TaskId, out var repetitions))
            {
                repetitions = new SortedDictionary<int, AgentRunResult>();
                byTask.Add(run.TaskId, repetitions);
            }
            if (!repetitions.TryAdd(run.Repetition, run))
                throw new InvalidOperationException($"Duplicate task_id/repetition in {arm} results.");
        }

        if (tasks.Count != byTask.Count || tasks.Keys.Any(taskId => !byTask.ContainsKey(taskId)))
            throw new InvalidOperationException($"{arm} task-id set does not match task manifest.");
        if (byTask.Keys.Any(taskId => !tasks.ContainsKey(taskId)))
            throw new InvalidOperationException($"{arm} task-id set does not match task manifest.");
        foreach (var repetitions in byTask.Values)
            if (!repetitions.ContainsKey(1))
                throw new InvalidOperationException($"Every {arm} task must have repetition 1.");
        return byTask;
    }

    static void ValidateRun(AgentRunResult run, string arm)
    {
        if (string.IsNullOrWhiteSpace(run.TaskId))
            throw new InvalidOperationException($"{arm} result task_id must be nonblank.");
        if (run.Repetition is < 1 or > 3)
            throw new InvalidOperationException($"{arm} result repetition must be between 1 and 3.");
        if (run.Completed && run.FailureReason is not null)
            throw new InvalidOperationException($"{arm} completed result failure_reason must be null.");
        if (!run.Completed && string.IsNullOrWhiteSpace(run.FailureReason))
            throw new InvalidOperationException($"{arm} incomplete result failure_reason is required.");
        if (!run.Completed && !AgentFailureReasons.All.Contains(run.FailureReason!))
            throw new InvalidOperationException($"{arm} result failure_reason is unsupported.");
        if (run.DurationMs < 0 || run.ToolCalls < 0 || run.ToolOutputBytes < 0 || run.ToolOutputTokens < 0 ||
            run.ModelInputTokens < 0 || run.ModelOutputTokens < 0 || run.ProductErrors < 0 ||
            run.DuplicateCalls < 0 || run.UncitedToolOutputTokens < 0)
            throw new InvalidOperationException($"{arm} result counts and duration must be nonnegative.");
        if (run.UncitedToolOutputTokens > run.ToolOutputTokens)
            throw new InvalidOperationException($"{arm} result uncited_tool_output_tokens must not exceed tool_output_tokens.");
    }

    static void ValidateRerunShapes(
        IReadOnlyList<AgentTaskManifestRow> tasks,
        IReadOnlyDictionary<string, SortedDictionary<int, AgentRunResult>> miller,
        IReadOnlyDictionary<string, SortedDictionary<int, AgentRunResult>> julie)
    {
        foreach (var task in tasks)
        {
            var millerRuns = miller[task.TaskId];
            var julieRuns = julie[task.TaskId];
            if (millerRuns[1].Completed == julieRuns[1].Completed)
            {
                if (millerRuns.Count != 1 || julieRuns.Count != 1)
                    throw new InvalidOperationException("Initial agreement permits only repetition 1 for both arms.");
                continue;
            }

            if (!HasThreeRepetitions(millerRuns) || !HasThreeRepetitions(julieRuns))
                throw new InvalidOperationException("Initial disagreement requires exactly repetitions 1, 2, and 3 for both arms.");
        }
    }

    static bool HasThreeRepetitions(IReadOnlyDictionary<int, AgentRunResult> runs) =>
        runs.Count == 3 && runs.ContainsKey(1) && runs.ContainsKey(2) && runs.ContainsKey(3);

    static AgentCompletionCells Completion(IReadOnlyCollection<StabilizedPair> pairs) => new()
    {
        BothCompleted = pairs.Count(pair => pair.MillerCompleted && pair.JulieCompleted),
        MillerOnly = pairs.Count(pair => pair.MillerCompleted && !pair.JulieCompleted),
        JulieOnly = pairs.Count(pair => !pair.MillerCompleted && pair.JulieCompleted),
        NeitherCompleted = pairs.Count(pair => !pair.MillerCompleted && !pair.JulieCompleted),
    };

    static AgentArmMetrics Metrics(IEnumerable<IReadOnlyList<AgentRunResult>> taskRuns)
    {
        var perTask = taskRuns.Select(runs =>
        {
            var passing = runs.Where(run => run.Completed).ToList();
            return new PerTaskMetrics(
                Median(passing.Select(run => run.ToolOutputTokens)),
                Median(passing.Select(run => run.ToolCalls)),
                Median(passing.Select(run => run.DurationMs)));
        }).ToList();
        if (perTask.Count == 0) return new AgentArmMetrics();

        return new AgentArmMetrics
        {
            MedianToolOutputTokens = Median(perTask.Select(metric => metric.ToolOutputTokens)),
            MedianToolCalls = Median(perTask.Select(metric => metric.ToolCalls)),
            P75DurationMs = NearestRank(perTask.Select(metric => metric.DurationMs), 0.75),
        };
    }

    static AgentEfficiencyGate Efficiency(int bothPassCount, AgentArmMetrics miller, AgentArmMetrics julie)
    {
        if (bothPassCount == 0)
            return new AgentEfficiencyGate { Verdict = AgentEfficiencyVerdicts.Fail, BothPassTaskCount = 0 };

        var tokenRoute = julie.MedianToolOutputTokens > 0 &&
            miller.MedianToolOutputTokens <= julie.MedianToolOutputTokens * 0.8;
        var callRoute = miller.MedianToolCalls <= julie.MedianToolCalls - 1.0 &&
            miller.MedianToolOutputTokens <= julie.MedianToolOutputTokens;
        var wallGuard = miller.P75DurationMs <= julie.P75DurationMs * 1.2;
        var passed = wallGuard && (tokenRoute || callRoute);

        return new AgentEfficiencyGate
        {
            Verdict = passed ? AgentEfficiencyVerdicts.Pass : AgentEfficiencyVerdicts.Fail,
            Measurable = true,
            BothPassTaskCount = bothPassCount,
            TokenRoutePassed = tokenRoute,
            CallRoutePassed = callRoute,
            WallGuardPassed = wallGuard,
        };
    }

    static IReadOnlyDictionary<string, int> FailureCounts(IEnumerable<AgentRunResult> runs)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in runs.Where(run => !run.Completed).GroupBy(run => run.FailureReason!, StringComparer.Ordinal))
            counts.Add(group.Key, group.Count());
        return counts;
    }

    static IReadOnlyDictionary<string, AgentSubgroupReport> Group(
        IReadOnlyCollection<StabilizedPair> pairs,
        Func<StabilizedPair, string> selector)
    {
        var reports = new SortedDictionary<string, AgentSubgroupReport>(StringComparer.Ordinal);
        foreach (var group in pairs.GroupBy(selector, StringComparer.Ordinal)
            .Where(group => group.Count() >= SubgroupMinimumTasks)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var members = group.ToList();
            reports.Add(group.Key, new AgentSubgroupReport
            {
                TaskCount = members.Count,
                Completion = Completion(members),
            });
        }
        return reports;
    }

    static double Median(IEnumerable<long> values) => Median(values.Select(value => (double)value));

    static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2.0;
    }

    static double NearestRank(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        var rank = Math.Max(1, (int)Math.Ceiling(percentile * ordered.Length));
        return ordered[rank - 1];
    }

    static bool IsPathLike(string value) => value.Contains('/') || value.Contains('\\');

    sealed record PerTaskMetrics(double ToolOutputTokens, double ToolCalls, double DurationMs);

    sealed record StabilizedPair(
        AgentTaskManifestRow Task,
        IReadOnlyList<AgentRunResult> MillerRuns,
        IReadOnlyList<AgentRunResult> JulieRuns)
    {
        public bool MillerCompleted => MillerRuns.Count(run => run.Completed) > MillerRuns.Count / 2;
        public bool JulieCompleted => JulieRuns.Count(run => run.Completed) > JulieRuns.Count / 2;
    }
}
